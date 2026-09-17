using Nemus.Domain.Accounts;
using Nemus.Domain.Categories;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Domain.Recurring;
using Nemus.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Nemus.Tests.Database;

/// <summary>
/// Gastos fixos no banco.
///
/// O que estes testes protegem, mais do que o repositorio, sao as promessas
/// que o schema faz sozinho: receita nao vira gasto fixo, valor nao e zero,
/// e cadastrar "Aluguel" duas vezes nao dobra a previsao do mes em silencio.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RecurringDatabaseTests : IAsyncLifetime
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly DateOnly Start = new(2026, 1, 1);

    private readonly PostgresFixture _fixture;

    public RecurringDatabaseTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (PostgresFixture.IsAvailable)
        {
            await _fixture.ResetDataAsync().ConfigureAwait(false);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private RecurringExpenseRepository Repository => new(_fixture.DataSource);

    [RequiresPostgresFact]
    public async Task Grava_e_le_de_volta_com_os_nomes_juntos()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid home = await CreateCategoryAsync("Casa");

        RecurringExpense rent = RecurringExpense.Create(
            "Aluguel", home, Money.FromMinorUnits(280_000, Brl), 10, Start,
            accountId: checking).Value;

        await Repository.SaveAsync(rent);

        RecurringExpenseRow row = Assert.Single(await Repository.ListAsync());

        Assert.Equal("Aluguel", row.Expense.Name);
        Assert.Equal(280_000, row.Expense.Amount.MinorUnits);
        Assert.Equal(10, row.Expense.DueDay);
        Assert.Equal("Casa", row.CategoryName);
        Assert.Equal("Corrente", row.AccountName);
        Assert.False(row.Expense.IsEstimate);
    }

    /// <summary>
    /// Salario que cai todo mes tambem se repete, mas vira dinheiro pronto
    /// para atribuir - nao envelope a encher. A FK composta barra, do mesmo
    /// jeito que barra no orcamento.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Categoria_de_receita_nao_vira_gasto_fixo()
    {
        Guid salary = await CreateCategoryAsync("Salario", CategoryKind.Income);

        RecurringExpense wrong = RecurringExpense.Create(
            "Salario", salary, Money.FromMinorUnits(500_000, Brl), 5, Start).Value;

        await Assert.ThrowsAsync<PostgresException>(() => Repository.SaveAsync(wrong));
    }

    /// <summary>
    /// Previsao dobrada e pior que previsao nenhuma: ela parece certa. O
    /// indice unico parcial recusa o segundo "Aluguel" ativo.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Nome_repetido_entre_ativos_e_recusado()
    {
        Guid home = await CreateCategoryAsync("Casa");

        await Repository.SaveAsync(RecurringExpense.Create(
            "Aluguel", home, Money.FromMinorUnits(280_000, Brl), 10, Start).Value);

        RecurringExpense duplicate = RecurringExpense.Create(
            "  aluguel  ", home, Money.FromMinorUnits(290_000, Brl), 10, Start).Value;

        await Assert.ThrowsAsync<PostgresException>(() => Repository.SaveAsync(duplicate));
    }

    /// <summary>
    /// Mas arquivado pode repetir: o antigo fica de historico e o nome volta
    /// a ficar livre.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Nome_de_arquivado_pode_ser_reusado()
    {
        Guid home = await CreateCategoryAsync("Casa");

        RecurringExpense first = RecurringExpense.Create(
            "Internet", home, Money.FromMinorUnits(10_000, Brl), 15, Start).Value;

        await Repository.SaveAsync(first);
        Assert.True(await Repository.ArchiveAsync(first.Id));

        await Repository.SaveAsync(RecurringExpense.Create(
            "Internet", home, Money.FromMinorUnits(12_000, Brl), 15, Start).Value);

        Assert.Single(await Repository.ListAsync());
        Assert.Equal(2, (await Repository.ListAsync(includeArchived: true)).Count);
    }

    /// <summary>
    /// O dominio ja recusa, mas o banco tem que recusar sozinho: e ele que
    /// sobrevive a uma migracao de dados feita por fora do app.
    ///
    /// UM erro provocado por teste, de proposito. O Postgres em WebAssembly
    /// que roda a suite local dessincroniza a conexao depois de um erro, e um
    /// teste que provoca quatro seguidos falharia por causa do servidor de
    /// teste, nao do schema. A prova de que as outras tres restricoes existem
    /// esta no teste seguinte, que le o catalogo em vez de provocar erro.
    /// </summary>
    [RequiresPostgresFact]
    public async Task O_banco_recusa_valor_zero()
    {
        Guid home = await CreateCategoryAsync("Casa");

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertRawAsync(home, amount: 0, dueDay: 10));

        Assert.Equal("ck_recurring_amount", error.ConstraintName);
    }

    /// <summary>
    /// As promessas que o schema faz sozinho, lidas do catalogo. Nao substitui
    /// provocar a violacao - so diz que a restricao esta la, com o nome certo,
    /// e nao foi perdida numa migration futura.
    /// </summary>
    [RequiresPostgresFact]
    public async Task As_restricoes_do_schema_estao_todas_la()
    {
        var found = new List<string>();

        await using (NpgsqlCommand command = _fixture.DataSource.CreateCommand("""
            SELECT conname
              FROM pg_constraint
             WHERE conrelid = 'recurring_expenses'::regclass
             ORDER BY conname
            """))
        {
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                found.Add(reader.GetString(0));
            }
        }

        Assert.Contains("ck_recurring_amount", found);
        Assert.Contains("ck_recurring_due_day", found);
        Assert.Contains("ck_recurring_period", found);
        Assert.Contains("ck_recurring_name", found);
        Assert.Contains("ck_recurring_expense", found);
        Assert.Contains("fk_recurring_category", found);
    }

    /// <summary>
    /// Candidato e perna externa de despesa, com categoria e positiva.
    /// Transferencia nao entra (nao tem perna externa), estorno nao entra
    /// (e negativo), e o que caiu fora do mes tambem nao.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Candidatos_sao_so_gastos_categorizados_do_mes()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid savings = await CreateAccountAsync("Poupanca", AccountType.Asset);
        Guid home = await CreateCategoryAsync("Casa");

        await SpendAsync(checking, new DateOnly(2026, 5, 10), 280_000, home);   // entra
        await SpendAsync(checking, new DateOnly(2026, 5, 11), 5_000, null);     // sem categoria
        await SpendAsync(checking, new DateOnly(2026, 4, 10), 280_000, home);   // outro mes
        await RefundAsync(checking, new DateOnly(2026, 5, 20), 3_000, home);    // estorno

        await AddAsync(Transaction.Transfer(
            new DateOnly(2026, 5, 15), "Poupar", checking, savings, Money.FromMinorUnits(10_000, Brl)));

        SpendingCandidate only = Assert.Single(await Repository.ReadCandidatesAsync(2026, 5, Brl));

        Assert.Equal(280_000, only.AmountMinorUnits);
        Assert.Equal(home, only.CategoryId);
    }

    /// <summary>
    /// O ciclo inteiro: cadastro a previsao, o gasto chega pelo razao, e o
    /// cruzamento diz que veio - sem nada ter sido gravado sobre isso.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Do_cadastro_ao_cruzamento_sem_gravar_nada()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid home = await CreateCategoryAsync("Casa");

        RecurringExpense rent = RecurringExpense.Create(
            "Aluguel", home, Money.FromMinorUnits(280_000, Brl), 10, Start).Value;
        RecurringExpense condo = RecurringExpense.Create(
            "Condominio", home, Money.FromMinorUnits(65_000, Brl), 12, Start).Value;

        await Repository.SaveAsync(rent);
        await Repository.SaveAsync(condo);

        // So o aluguel chegou, e um pouco mais caro.
        await SpendAsync(checking, new DateOnly(2026, 5, 9), 285_000, home);

        IReadOnlyList<SpendingCandidate> candidates = await Repository.ReadCandidatesAsync(2026, 5, Brl);
        IReadOnlyList<RecurringMatch> matches = RecurringMatcher.Match([rent, condo], candidates);

        RecurringMatch rentMatch = matches.Single(m => m.RecurringExpenseId == rent.Id);
        Assert.True(rentMatch.IsMatched);
        Assert.Equal(5_000, rentMatch.DifferenceFrom(280_000));

        Assert.False(matches.Single(m => m.RecurringExpenseId == condo.Id).IsMatched);
    }

    [RequiresPostgresFact]
    public async Task Apagar_de_vez_some_ate_do_historico()
    {
        Guid home = await CreateCategoryAsync("Casa");
        RecurringExpense typo = RecurringExpense.Create(
            "Alugeul", home, Money.FromMinorUnits(280_000, Brl), 10, Start).Value;

        await Repository.SaveAsync(typo);

        Assert.True(await Repository.DeleteAsync(typo.Id));
        Assert.Empty(await Repository.ListAsync(includeArchived: true));
        Assert.False(await Repository.DeleteAsync(typo.Id));
    }

    // -----------------------------------------------------------------------

    private async Task InsertRawAsync(Guid categoryId, long amount, int dueDay)
    {
        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand("""
            INSERT INTO recurring_expenses
                   (id, name, category_id, amount, currency_code, due_day, starts_on)
            VALUES (@id, @name, @category, @amount, 'BRL', @due_day, DATE '2026-01-01')
            """);

        command.Parameters.AddWithValue("id", UuidV7.NewGuid());
        command.Parameters.AddWithValue("name", $"Teste {amount}-{dueDay}");
        command.Parameters.AddWithValue("category", categoryId);
        command.Parameters.AddWithValue("amount", amount);
        command.Parameters.AddWithValue("due_day", (short)dueDay);

        await command.ExecuteNonQueryAsync();
    }

    private async Task<Guid> CreateAccountAsync(string name, AccountType type)
    {
        Account account = Account.Create(name, type, Brl).Value;
        await new AccountRepository(_fixture.DataSource).AddAsync(account);
        return account.Id;
    }

    private async Task<Guid> CreateCategoryAsync(string name, CategoryKind kind = CategoryKind.Expense)
    {
        Category category = Category.CreateGroup(name, kind).Value;
        await new CategoryRepository(_fixture.DataSource).AddAsync(category);
        return category.Id;
    }

    private async Task<Transaction> AddAsync(Result<Transaction> created)
    {
        Transaction transaction = created.Value;
        await new TransactionRepository(_fixture.DataSource).AddAsync(transaction);
        return transaction;
    }

    private Task<Transaction> SpendAsync(Guid account, DateOnly day, long minorUnits, Guid? category) =>
        AddAsync(Transaction.Spend(
            day, "PAG*ALUGUEL", account, SystemAccounts.ExternalExpenses,
            Money.FromMinorUnits(minorUnits, Brl), category));

    private Task<Transaction> RefundAsync(Guid account, DateOnly day, long minorUnits, Guid category) =>
        AddAsync(Transaction.Create(new TransactionDraft
        {
            OccurredOn = day,
            Description = "Estorno",
            Currency = Brl,
            Entries =
            [
                new EntryDraft(account, Money.FromMinorUnits(minorUnits, Brl)),
                new EntryDraft(SystemAccounts.ExternalExpenses, Money.FromMinorUnits(-minorUnits, Brl))
                {
                    CategoryId = category,
                },
            ],
        }));
}
