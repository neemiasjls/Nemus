using Nemus.Domain.Accounts;
using Nemus.Domain.Budgeting;
using Nemus.Domain.Categories;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Persistence;
using Xunit;

namespace Nemus.Tests.Database;

/// <summary>
/// Categorizar um lancamento que ja existe.
///
/// E o que liga a importacao ao orcamento: o OFX do banco nao traz categoria,
/// e sem este caminho todo dinheiro importado ficaria fora dos envelopes para
/// sempre. O ultimo teste e o ciclo inteiro - importado sem categoria, depois
/// categorizado, e o envelope mexendo por causa disso.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CategorizeTests : IAsyncLifetime
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly BudgetMonth January = BudgetMonth.Parse("2026-01").Value;
    private static readonly DateOnly Day = new(2026, 1, 12);

    private readonly PostgresFixture _fixture;

    public CategorizeTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (PostgresFixture.IsAvailable)
        {
            await _fixture.ResetDataAsync().ConfigureAwait(false);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private LedgerQueries Queries => new(_fixture.DataSource);

    [RequiresPostgresFact]
    public async Task Categoria_entra_na_perna_da_conta_externa()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid groceries = await CreateCategoryAsync("Mercado");

        Transaction spent = await SpendAsync(checking, category: null, 12_345);

        Assert.Equal(CategorizeOutcome.Ok, await Queries.CategorizeAsync(spent.Id, groceries));

        TransactionView view = await ReadAsync(spent.Id);
        EntryView external = Assert.Single(view.Entries, e => !e.AccountIsInternal);
        EntryView internalLeg = Assert.Single(view.Entries, e => e.AccountIsInternal);

        Assert.Equal(groceries, external.CategoryId);
        Assert.Equal("Mercado", external.CategoryName);

        // A perna da conta propria continua sem categoria: o orcamento le a externa.
        Assert.Null(internalLeg.CategoryId);
    }

    [RequiresPostgresFact]
    public async Task Categorizar_de_novo_substitui_e_nulo_limpa()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid groceries = await CreateCategoryAsync("Mercado");
        Guid dining = await CreateCategoryAsync("Restaurantes");

        Transaction spent = await SpendAsync(checking, groceries, 5_000);

        await Queries.CategorizeAsync(spent.Id, dining);
        Assert.Equal(dining, (await ExternalLegAsync(spent.Id)).CategoryId);

        await Queries.CategorizeAsync(spent.Id, null);
        Assert.Null((await ExternalLegAsync(spent.Id)).CategoryId);
    }

    /// <summary>
    /// Transferencia entre contas suas nao tem contraparte externa - o dinheiro
    /// nao saiu do seu patrimonio, entao nao ha o que categorizar.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Transferencia_nao_aceita_categoria()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid savings = await CreateAccountAsync("Poupanca", AccountType.Asset);
        Guid groceries = await CreateCategoryAsync("Mercado");

        Transaction transfer = await AddAsync(Transaction.Transfer(
            Day, "Poupar", checking, savings, Money.FromMinorUnits(50_000, Brl)));

        Assert.Equal(
            CategorizeOutcome.NoExternalLeg,
            await Queries.CategorizeAsync(transfer.Id, groceries));
    }

    /// <summary>
    /// Compra dividida em duas categorias: escolher uma so apagaria a divisao
    /// que alguem fez de proposito.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Lancamento_dividido_e_recusado()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid groceries = await CreateCategoryAsync("Mercado");
        Guid home = await CreateCategoryAsync("Casa");

        Transaction split = await AddAsync(Transaction.Create(new TransactionDraft
        {
            OccurredOn = Day,
            Description = "Compra dividida",
            Currency = Brl,
            Entries =
            [
                new EntryDraft(checking, Money.FromMinorUnits(-30_000, Brl)),
                new EntryDraft(SystemAccounts.ExternalExpenses, Money.FromMinorUnits(20_000, Brl))
                {
                    CategoryId = groceries,
                },
                new EntryDraft(SystemAccounts.ExternalExpenses, Money.FromMinorUnits(10_000, Brl))
                {
                    CategoryId = home,
                },
            ],
        }));

        Assert.Equal(CategorizeOutcome.Split, await Queries.CategorizeAsync(split.Id, groceries));

        // E nada mudou.
        TransactionView view = await ReadAsync(split.Id);
        Assert.Equal(2, view.Entries.Count(e => e.CategoryId is not null));
    }

    [RequiresPostgresFact]
    public async Task Lancamento_inexistente_ou_apagado_nao_e_encontrado()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid groceries = await CreateCategoryAsync("Mercado");
        Transaction spent = await SpendAsync(checking, null, 4_000);

        await Queries.SoftDeleteAsync(spent.Id);

        Assert.Equal(CategorizeOutcome.NotFound, await Queries.CategorizeAsync(spent.Id, groceries));
        Assert.Equal(CategorizeOutcome.NotFound, await Queries.CategorizeAsync(UuidV7.NewGuid(), groceries));
    }

    /// <summary>
    /// O ciclo que motiva o endpoint: extrato importado chega sem categoria,
    /// entao o gasto aparece como "sem envelope" e sai do pronto para
    /// atribuir. Categorizar move o dinheiro para o envelope - e o total das
    /// contas do orcamento nao muda, porque categoria nao e dinheiro.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Categorizar_tira_do_sem_categoria_e_poe_no_envelope()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid groceries = await CreateCategoryAsync("Mercado");
        var budget = new BudgetRepository(_fixture.DataSource);

        await AddAsync(Transaction.OpeningBalance(
            new DateOnly(2026, 1, 1), checking, SystemAccounts.OpeningBalances,
            Money.FromMinorUnits(100_000, Brl)));

        await budget.SetAsync(BudgetAssignment.Create(
            groceries, January, Money.FromMinorUnits(50_000, Brl)).Value);

        Transaction imported = await SpendAsync(checking, category: null, 30_000);

        BudgetMonthView before = await budget.ReadMonthAsync(January, Brl);
        Assert.Equal(1, before.UncategorizedTransactions);
        Assert.Equal(30_000, before.UncategorizedAmount);
        Assert.Equal(0, before.Categories.Single(c => c.CategoryId == groceries).Activity);
        Assert.Equal(100_000 - 50_000 - 30_000, before.ReadyToAssign);

        Assert.Equal(CategorizeOutcome.Ok, await Queries.CategorizeAsync(imported.Id, groceries));

        BudgetMonthView after = await budget.ReadMonthAsync(January, Brl);
        Assert.Equal(0, after.UncategorizedTransactions);
        Assert.Equal(-30_000, after.Categories.Single(c => c.CategoryId == groceries).Activity);
        Assert.Equal(20_000, after.Categories.Single(c => c.CategoryId == groceries).Available);

        // O dinheiro so mudou de lugar: pronto para atribuir volta ao que seria
        // se o gasto tivesse nascido categorizado, e o saldo nao se mexe.
        Assert.Equal(100_000 - 50_000, after.ReadyToAssign);
        Assert.Equal(before.OnBudgetBalance, after.OnBudgetBalance);
        Assert.True(after.IsBalanced);
    }

    // -----------------------------------------------------------------------

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

    private Task<Transaction> SpendAsync(Guid account, Guid? category, long minorUnits) =>
        AddAsync(Transaction.Spend(
            Day, "PAG*MERCADO", account, SystemAccounts.ExternalExpenses,
            Money.FromMinorUnits(minorUnits, Brl), category));

    private async Task<TransactionView> ReadAsync(Guid id)
    {
        TransactionPage page = await Queries.ListAsync(limit: 50);
        return Assert.Single(page.Items, t => t.Id == id);
    }

    private async Task<EntryView> ExternalLegAsync(Guid id)
    {
        TransactionView view = await ReadAsync(id);
        return Assert.Single(view.Entries, e => !e.AccountIsInternal);
    }
}
