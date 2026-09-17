using Nemus.Domain.Accounts;
using Nemus.Domain.Installments;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Nemus.Tests.Database;

/// <summary>
/// Compra parcelada contra o banco de verdade.
///
/// O que se prova aqui e que as duas metades entram juntas: o razao com o
/// passivo integral de hoje, e o cronograma com o compromisso de cada fatura.
/// Meia verdade gravada seria pior que nada.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class InstallmentTests : IAsyncLifetime
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly DateOnly Purchase = new(2026, 3, 20);

    private readonly PostgresFixture _fixture;

    public InstallmentTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (PostgresFixture.IsAvailable)
        {
            await _fixture.ResetDataAsync().ConfigureAwait(false);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private InstallmentRepository Installments => new(_fixture.DataSource);

    private CreditCardRepository Cards => new(_fixture.DataSource);

    [RequiresPostgresFact]
    public async Task Compra_parcelada_grava_razao_e_cronograma_juntos()
    {
        Guid card = await CreateCardAsync(closingDay: 25, dueDay: 5);
        InstallmentPlan plan = Plan(card, total: 120_000, count: 12);

        await Installments.AddAsync(plan, plan.BuildPurchase(SystemAccounts.ExternalExpenses).Value);

        // O razao: o cartao deve os R$ 1.200 inteiros, hoje.
        Money balance = await new AccountRepository(_fixture.DataSource).GetBalanceAsync(card, Brl);
        Assert.Equal(-120_000, balance.MinorUnits);

        // O cronograma: 12 parcelas de R$ 100.
        Assert.Equal(12L, await ScalarAsync("SELECT COUNT(*)::BIGINT FROM installments"));
        Assert.Equal(120_000L, await ScalarAsync("SELECT SUM(amount)::BIGINT FROM installments"));

        InstallmentPlanRow row = Assert.Single(await Installments.ListAsync(Purchase));
        Assert.Equal(10_000, row.InstallmentAmount);
        Assert.Equal(12, row.InstallmentCount);
        Assert.Equal("Sofa", row.Description);
    }

    /// <summary>
    /// R$ 100 em 3x sao 33,34 + 33,33 + 33,33. O centavo residual tem que
    /// existir em algum lugar, e o gatilho da 006 nao deixa a soma escapar.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Centavo_residual_nao_evapora()
    {
        Guid card = await CreateCardAsync(closingDay: 25, dueDay: 5);
        InstallmentPlan plan = Plan(card, total: 10_000, count: 3);

        await Installments.AddAsync(plan, plan.BuildPurchase(SystemAccounts.ExternalExpenses).Value);

        Assert.Equal(10_000L, await ScalarAsync("SELECT SUM(amount)::BIGINT FROM installments"));
        Assert.Equal(3_334L, await ScalarAsync("SELECT amount FROM installments WHERE sequence = 1"));
        Assert.Equal(3_333L, await ScalarAsync("SELECT amount FROM installments WHERE sequence = 3"));
    }

    /// <summary>
    /// Cronograma que nao soma o financiado nao chega a existir - nem por SQL
    /// direto. O gatilho e diferido, entao a recusa vem no commit.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Cronograma_torto_e_recusado_pelo_banco()
    {
        Guid card = await CreateCardAsync(closingDay: 25, dueDay: 5);
        InstallmentPlan plan = Plan(card, total: 30_000, count: 3);
        await Installments.AddAsync(plan, plan.BuildPurchase(SystemAccounts.ExternalExpenses).Value);

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using NpgsqlConnection connection = await _fixture.DataSource.OpenConnectionAsync();
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();

            await using (NpgsqlCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "UPDATE installments SET amount = amount - 1 WHERE sequence = 1";
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        });

        Assert.Equal(NemusSqlStates.InstallmentSumMismatch, error.SqlState);
    }

    /// <summary>
    /// Compra no dia 20. Ate o dia do fechamento ela entra na fatura do
    /// proprio mes; depois dele, na do mes seguinte - inclusive quando a
    /// compra cai exatamente no dia do fechamento, que e a fronteira. A regra
    /// e do dominio; aqui se confere que as condicoes gravadas do cartao
    /// chegam ate ela.
    /// </summary>
    [RequiresPostgresTheory]
    [InlineData(15, "2026-04-01")]
    [InlineData(20, "2026-03-01")]
    [InlineData(25, "2026-03-01")]
    public async Task A_fatura_da_primeira_parcela_sai_do_fechamento_do_cartao(
        int closingDay, string expectedFirstMonth)
    {
        Guid card = await CreateCardAsync(closingDay, dueDay: 5);
        CreditCardTerms terms = (await Cards.FindAsync(card))!;

        InstallmentPlan plan = InstallmentPlan.Create(new InstallmentPlanDraft
        {
            CardAccountId = card,
            PurchaseTransactionId = UuidV7.NewGuid(),
            Description = "Compra do dia 20",
            TotalAmount = Money.FromMinorUnits(60_000, Brl),
            InstallmentCount = 6,
            PurchaseDate = Purchase,
            ClosingDay = terms.ClosingDay,
            DueDay = terms.DueDay,
        }).Value;

        await Installments.AddAsync(plan, plan.BuildPurchase(SystemAccounts.ExternalExpenses).Value);

        InstallmentPlanRow row = Assert.Single(await Installments.ListAsync(Purchase));
        Assert.Equal(DateOnly.Parse(expectedFirstMonth), row.FirstStatementMonth);
    }

    /// <summary>
    /// A pergunta que parcelamento cria: quanto do proximo mes ja esta gasto
    /// antes de ele comecar.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Faturas_futuras_mostram_quanto_ja_esta_comprometido()
    {
        Guid card = await CreateCardAsync(closingDay: 25, dueDay: 5);

        InstallmentPlan sofa = Plan(card, total: 120_000, count: 12, description: "Sofa");
        await Installments.AddAsync(sofa, sofa.BuildPurchase(SystemAccounts.ExternalExpenses).Value);

        InstallmentPlan phone = Plan(card, total: 24_000, count: 3, description: "Celular");
        await Installments.AddAsync(phone, phone.BuildPurchase(SystemAccounts.ExternalExpenses).Value);

        IReadOnlyList<UpcomingCommitment> upcoming =
            await Installments.UpcomingAsync(new DateOnly(2026, 3, 1), months: 4);

        Assert.Equal(4, upcoming.Count);

        // Marco a maio: as duas compras juntas. Junho: so o sofa.
        Assert.Equal(10_000 + 8_000, upcoming[0].Amount);
        Assert.Equal(2, upcoming[0].PlanCount);
        Assert.Equal(10_000, upcoming[3].Amount);
        Assert.Equal(1, upcoming[3].PlanCount);
    }

    [RequiresPostgresFact]
    public async Task Andamento_conta_o_que_ja_venceu_e_o_que_falta()
    {
        Guid card = await CreateCardAsync(closingDay: 25, dueDay: 5);
        InstallmentPlan plan = Plan(card, total: 120_000, count: 12);
        await Installments.AddAsync(plan, plan.BuildPurchase(SystemAccounts.ExternalExpenses).Value);

        // Cinco meses depois da primeira fatura (marco): marco a julho pagos.
        InstallmentPlanRow row = Assert.Single(await Installments.ListAsync(new DateOnly(2026, 7, 15)));

        Assert.Equal(5, row.PaidCount);
        Assert.Equal(70_000, row.RemainingAmount);
        Assert.False(row.IsFinished);

        // Depois da ultima, nao sobra nada.
        InstallmentPlanRow finished = Assert.Single(await Installments.ListAsync(new DateOnly(2027, 3, 1)));
        Assert.True(finished.IsFinished);
        Assert.Empty(await Installments.ListAsync(new DateOnly(2027, 3, 1), onlyOpen: true));
    }

    /// <summary>
    /// Sem fechamento e vencimento nao da para dizer em qual fatura a parcela
    /// cai - e a API recusa antes de gravar qualquer coisa.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Cartao_sem_condicoes_nao_tem_fechamento_para_consultar()
    {
        Guid card = await CreateAccountAsync("Cartao sem condicoes", AccountType.Liability);

        Assert.Null(await Cards.FindAsync(card));
    }

    [RequiresPostgresFact]
    public async Task Condicoes_do_cartao_sao_substituidas_e_nao_duplicadas()
    {
        Guid card = await CreateCardAsync(closingDay: 25, dueDay: 5);

        await Cards.SaveAsync(CreditCardTerms.Create(card, 10, 20, Money.FromMinorUnits(500_000, Brl)).Value);

        CreditCardTerms terms = (await Cards.FindAsync(card))!;
        Assert.Equal((10, 20), (terms.ClosingDay, terms.DueDay));
        Assert.Equal(500_000, terms.CreditLimit!.Value.MinorUnits);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*)::BIGINT FROM credit_card_terms"));
    }

    /// <summary>
    /// A FK composta da 002: conta que nao e passivo nao tem como ganhar
    /// fechamento e vencimento, nem por SQL direto.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Conta_corrente_nao_aceita_condicoes_de_cartao()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => Cards.SaveAsync(CreditCardTerms.Create(checking, 25, 5).Value));

        Assert.Equal("23503", error.SqlState);
    }

    // -----------------------------------------------------------------------

    private InstallmentPlan Plan(Guid card, long total, int count, string description = "Sofa") =>
        InstallmentPlan.Create(new InstallmentPlanDraft
        {
            CardAccountId = card,
            PurchaseTransactionId = UuidV7.NewGuid(),
            Description = description,
            TotalAmount = Money.FromMinorUnits(total, Brl),
            InstallmentCount = count,
            PurchaseDate = Purchase,
            ClosingDay = 25,
            DueDay = 5,
        }).Value;

    private async Task<Guid> CreateAccountAsync(string name, AccountType type)
    {
        Account account = Account.Create(name, type, Brl).Value;
        await new AccountRepository(_fixture.DataSource).AddAsync(account);
        return account.Id;
    }

    private async Task<Guid> CreateCardAsync(int closingDay, int dueDay)
    {
        Guid card = await CreateAccountAsync($"Cartao {closingDay}/{dueDay}", AccountType.Liability);
        await Cards.SaveAsync(CreditCardTerms.Create(card, closingDay, dueDay).Value);
        return card;
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
