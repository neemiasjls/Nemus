using Nemus.Domain.Accounts;
using Nemus.Domain.Categories;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Persistence;
using Xunit;

namespace Nemus.Tests.Database;

/// <summary>
/// Agregacao no servidor.
///
/// O primeiro teste e o que justifica o arquivo inteiro: com mais
/// lancamentos do que cabe numa pagina da API, somar no navegador da um
/// numero menor que o real. Somar no banco da o real. Os outros protegem a
/// semantica de leitura - transferencia fora, estorno abatido, apagado
/// ignorado - para que "corrigir onde soma" nao vire "mudar o que soma".
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ReportTests : IAsyncLifetime
{
    private static readonly Currency Brl = Currency.Brl;

    private readonly PostgresFixture _fixture;

    public ReportTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (PostgresFixture.IsAvailable)
        {
            await _fixture.ResetDataAsync().ConfigureAwait(false);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ReportQueries Reports => new(_fixture.DataSource);

    /// <summary>
    /// O BUG QUE ISTO CORRIGE. A tela somava sobre os lancamentos que tinha
    /// carregado, no maximo <see cref="LedgerQueries.MaxPageSize"/>. Com mais
    /// do que isso no mes, o gasto exibido era o gasto dos mais recentes - um
    /// numero menor, e menor sem avisar quanto.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Soma_do_mes_ve_alem_do_teto_de_pagina()
    {
        const int count = LedgerQueries.MaxPageSize + 10;

        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);

        long expected = 0;
        for (int i = 0; i < count; i++)
        {
            // Valores diferentes: se o teste somasse o mesmo numero N vezes,
            // um erro de contagem passaria despercebido como erro de valor.
            long amount = 1_000 + i;
            expected += amount;
            await SpendAsync(checking, new DateOnly(2026, 3, 1 + (i % 28)), amount);
        }

        IReadOnlyList<MonthFlowRow> flow =
            await Reports.ReadMonthlyFlowAsync(new DateOnly(2026, 3, 1), 1, Brl.Code);

        Assert.Equal(expected, Assert.Single(flow).Expense);

        // E a prova de que o caminho antigo nao chegaria la: a pagina para no teto.
        TransactionPage page = await new LedgerQueries(_fixture.DataSource)
            .ListAsync(limit: LedgerQueries.MaxPageSize);

        Assert.Equal(count, page.Total);
        Assert.Equal(LedgerQueries.MaxPageSize, page.Items.Count);
    }

    /// <summary>
    /// Mes vazio e uma coluna no chao, nao uma coluna que falta. Omitir a
    /// linha faria o grafico colar o mes anterior no seguinte e mentir sobre
    /// o intervalo.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Mes_sem_lancamento_volta_zerado()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);

        await SpendAsync(checking, new DateOnly(2026, 1, 10), 10_000);
        await SpendAsync(checking, new DateOnly(2026, 3, 10), 30_000);

        IReadOnlyList<MonthFlowRow> flow =
            await Reports.ReadMonthlyFlowAsync(new DateOnly(2026, 1, 1), 3, Brl.Code);

        Assert.Equal(3, flow.Count);
        Assert.Equal([new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 1)],
            flow.Select(f => f.Month));

        Assert.Equal(10_000, flow[0].Expense);
        Assert.Equal(0, flow[1].Expense);
        Assert.Equal(0, flow[1].Income);
        Assert.Equal(30_000, flow[2].Expense);
    }

    /// <summary>
    /// Mover dinheiro entre contas suas nao e gasto nem receita - so mexe em
    /// contas internas, e o relatorio le as externas.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Transferencia_nao_e_gasto_nem_receita()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid savings = await CreateAccountAsync("Poupanca", AccountType.Asset);

        await AddAsync(Transaction.Transfer(
            new DateOnly(2026, 5, 9), "Poupar", checking, savings, Money.FromMinorUnits(80_000, Brl)));

        MonthFlowRow may = Assert.Single(
            await Reports.ReadMonthlyFlowAsync(new DateOnly(2026, 5, 1), 1, Brl.Code));

        Assert.Equal(0, may.Expense);
        Assert.Equal(0, may.Income);
    }

    /// <summary>
    /// Devolucao entra negativa na perna de despesa e se abate sozinha. Sem
    /// caso especial em lugar nenhum: e a mesma soma.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Estorno_abate_o_gasto_do_mes()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);

        await SpendAsync(checking, new DateOnly(2026, 5, 3), 20_000);
        await RefundAsync(checking, new DateOnly(2026, 5, 20), 7_500);

        MonthFlowRow may = Assert.Single(
            await Reports.ReadMonthlyFlowAsync(new DateOnly(2026, 5, 1), 1, Brl.Code));

        Assert.Equal(12_500, may.Expense);
    }

    [RequiresPostgresFact]
    public async Task Receita_entra_positiva_e_gasto_nao_a_contamina()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);

        await EarnAsync(checking, new DateOnly(2026, 6, 5), 500_000);
        await SpendAsync(checking, new DateOnly(2026, 6, 6), 120_000);

        MonthFlowRow june = Assert.Single(
            await Reports.ReadMonthlyFlowAsync(new DateOnly(2026, 6, 1), 1, Brl.Code));

        Assert.Equal(500_000, june.Income);
        Assert.Equal(120_000, june.Expense);
    }

    [RequiresPostgresFact]
    public async Task Lancamento_apagado_sai_do_relatorio()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);

        Transaction kept = await SpendAsync(checking, new DateOnly(2026, 7, 4), 4_000);
        Transaction removed = await SpendAsync(checking, new DateOnly(2026, 7, 5), 9_000);

        await new LedgerQueries(_fixture.DataSource).SoftDeleteAsync(removed.Id);

        MonthFlowRow july = Assert.Single(
            await Reports.ReadMonthlyFlowAsync(new DateOnly(2026, 7, 1), 1, Brl.Code));

        Assert.Equal(4_000, july.Expense);
        Assert.NotEqual(Guid.Empty, kept.Id);
    }

    /// <summary>A janela e fechada: o que cai fora dela nao entra na soma.</summary>
    [RequiresPostgresFact]
    public async Task Fora_da_janela_nao_entra()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);

        await SpendAsync(checking, new DateOnly(2026, 2, 28), 11_100);  // antes
        await SpendAsync(checking, new DateOnly(2026, 3, 1), 22_200);   // primeiro dia
        await SpendAsync(checking, new DateOnly(2026, 4, 30), 33_300);  // ultimo dia
        await SpendAsync(checking, new DateOnly(2026, 5, 1), 44_400);   // depois

        IReadOnlyList<MonthFlowRow> flow =
            await Reports.ReadMonthlyFlowAsync(new DateOnly(2026, 3, 1), 2, Brl.Code);

        Assert.Equal(2, flow.Count);
        Assert.Equal(22_200, flow[0].Expense);
        Assert.Equal(33_300, flow[1].Expense);
    }

    [RequiresPostgresFact]
    public async Task Tendencia_separa_por_categoria_e_por_mes()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid groceries = await CreateCategoryAsync("Mercado");
        Guid dining = await CreateCategoryAsync("Restaurantes");

        await SpendAsync(checking, new DateOnly(2026, 1, 5), 10_000, groceries);
        await SpendAsync(checking, new DateOnly(2026, 1, 25), 5_000, groceries);
        await SpendAsync(checking, new DateOnly(2026, 1, 9), 8_000, dining);
        await SpendAsync(checking, new DateOnly(2026, 2, 9), 12_000, groceries);

        IReadOnlyList<CategoryMonthRow> trend =
            await Reports.ReadCategoryTrendAsync(new DateOnly(2026, 1, 1), 2, Brl.Code);

        Assert.Equal(15_000, Amount(trend, "Mercado", new DateOnly(2026, 1, 1)));
        Assert.Equal(8_000, Amount(trend, "Restaurantes", new DateOnly(2026, 1, 1)));
        Assert.Equal(12_000, Amount(trend, "Mercado", new DateOnly(2026, 2, 1)));

        // Fevereiro sem restaurante nao vira linha de zero: a consulta e esparsa.
        Assert.DoesNotContain(trend, r => r.CategoryName == "Restaurantes" && r.Month.Month == 2);
    }

    /// <summary>
    /// Gasto sem envelope tem balde proprio no relatorio - pelo mesmo motivo
    /// que tem no orcamento. Se sumisse, o total do relatorio ficaria menor
    /// que o gasto real e ninguem veria a diferenca.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Sem_categoria_aparece_como_balde_proprio()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid groceries = await CreateCategoryAsync("Mercado");

        await SpendAsync(checking, new DateOnly(2026, 1, 5), 10_000, groceries);
        await SpendAsync(checking, new DateOnly(2026, 1, 6), 4_500, category: null);

        IReadOnlyList<CategoryMonthRow> trend =
            await Reports.ReadCategoryTrendAsync(new DateOnly(2026, 1, 1), 1, Brl.Code);

        CategoryMonthRow loose = Assert.Single(trend, r => r.CategoryId is null);
        Assert.Equal("Sem categoria", loose.CategoryName);
        Assert.Equal(4_500, loose.Amount);

        // E o total da tendencia bate com o gasto do mes: nada some no caminho.
        MonthFlowRow january = Assert.Single(
            await Reports.ReadMonthlyFlowAsync(new DateOnly(2026, 1, 1), 1, Brl.Code));

        Assert.Equal(january.Expense, trend.Sum(r => r.Amount));
    }

    // -----------------------------------------------------------------------

    private static long Amount(IReadOnlyList<CategoryMonthRow> rows, string name, DateOnly month) =>
        rows.Single(r => r.CategoryName == name && r.Month == month).Amount;

    private async Task<Guid> CreateAccountAsync(string name, AccountType type)
    {
        Account account = Account.Create(name, type, Brl).Value;
        await new AccountRepository(_fixture.DataSource).AddAsync(account);
        return account.Id;
    }

    private async Task<Guid> CreateCategoryAsync(string name)
    {
        Category category = Category.CreateGroup(name, CategoryKind.Expense).Value;
        await new CategoryRepository(_fixture.DataSource).AddAsync(category);
        return category.Id;
    }

    private async Task<Transaction> AddAsync(Result<Transaction> created)
    {
        Transaction transaction = created.Value;
        await new TransactionRepository(_fixture.DataSource).AddAsync(transaction);
        return transaction;
    }

    private Task<Transaction> SpendAsync(
        Guid account, DateOnly day, long minorUnits, Guid? category = null) =>
        AddAsync(Transaction.Spend(
            day, "PAG*MERCADO", account, SystemAccounts.ExternalExpenses,
            Money.FromMinorUnits(minorUnits, Brl), category));

    /// <summary>Entrada de dinheiro: a perna da conta de receita e negativa.</summary>
    private Task<Transaction> EarnAsync(Guid account, DateOnly day, long minorUnits) =>
        AddAsync(Transaction.Create(new TransactionDraft
        {
            OccurredOn = day,
            Description = "Salario",
            Currency = Brl,
            Entries =
            [
                new EntryDraft(account, Money.FromMinorUnits(minorUnits, Brl)),
                new EntryDraft(SystemAccounts.ExternalRevenue, Money.FromMinorUnits(-minorUnits, Brl)),
            ],
        }));

    /// <summary>Devolucao: o inverso de um gasto, na mesma conta de despesa.</summary>
    private Task<Transaction> RefundAsync(Guid account, DateOnly day, long minorUnits) =>
        AddAsync(Transaction.Create(new TransactionDraft
        {
            OccurredOn = day,
            Description = "Estorno",
            Currency = Brl,
            Entries =
            [
                new EntryDraft(account, Money.FromMinorUnits(minorUnits, Brl)),
                new EntryDraft(SystemAccounts.ExternalExpenses, Money.FromMinorUnits(-minorUnits, Brl)),
            ],
        }));
}
