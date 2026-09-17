using Nemus.Domain.Accounts;
using Nemus.Domain.Budgeting;
using Nemus.Domain.Categories;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace Nemus.Tests.Database;

/// <summary>
/// Orcamento contra o banco de verdade: o calculo mora nas visoes da 010, e
/// e ali que ele precisa ser provado.
///
/// Os seis primeiros casos sao os do desenho aprovado. Os demais cobrem as
/// regras que a implementacao teve de decidir: o que acontece com despesa
/// fora do orcamento, com saida sem categoria, com cartao e com reembolso.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BudgetTests : IAsyncLifetime
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly BudgetMonth January = BudgetMonth.Parse("2026-01").Value;
    private static readonly BudgetMonth February = January.Next;
    private static readonly BudgetMonth March = February.Next;

    private readonly PostgresFixture _fixture;

    public BudgetTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (PostgresFixture.IsAvailable)
        {
            await _fixture.ResetDataAsync().ConfigureAwait(false);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // -----------------------------------------------------------------------
    // Os casos do desenho

    [RequiresPostgresFact]
    public async Task Sobra_rola_para_o_mes_seguinte()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid groceries = await CreateCategoryAsync("Mercado");
        await OpenAsync(checking, 100_000, Day(January, 1));

        await AssignAsync(groceries, January, 50_000);
        await SpendAsync(checking, groceries, 40_000, Day(January, 10));

        BudgetCategoryRow january = await EnvelopeAsync(groceries, January);
        Assert.Equal((50_000L, -40_000L, 10_000L), (january.Assigned, january.Activity, january.Available));

        // Fevereiro e marco comecam com os R$ 100 que sobraram, sem nova atribuicao.
        BudgetCategoryRow february = await EnvelopeAsync(groceries, February);
        Assert.Equal((0L, 0L, 10_000L), (february.Assigned, february.Activity, february.Available));
        Assert.Equal(10_000, (await EnvelopeAsync(groceries, March)).Available);
    }

    [RequiresPostgresFact]
    public async Task Estouro_rola_negativo_ate_ser_coberto()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid leisure = await CreateCategoryAsync("Lazer");
        await OpenAsync(checking, 100_000, Day(January, 1));

        await AssignAsync(leisure, January, 20_000);
        await SpendAsync(checking, leisure, 25_000, Day(January, 20));

        Assert.Equal(-5_000, (await EnvelopeAsync(leisure, January)).Available);
        Assert.Equal(-5_000, (await EnvelopeAsync(leisure, February)).Available);

        // Cobrir em fevereiro zera fevereiro e nao reescreve janeiro.
        await AssignAsync(leisure, February, 5_000);

        Assert.Equal(0, (await EnvelopeAsync(leisure, February)).Available);
        Assert.Equal(-5_000, (await EnvelopeAsync(leisure, January)).Available);

        BudgetMonthView view = await Budget.ReadMonthAsync(February, Brl);
        Assert.Equal(100_000 - 20_000 - 5_000, view.ReadyToAssign);
        Assert.True(view.IsBalanced);
    }

    [RequiresPostgresFact]
    public async Task Estorno_recompoe_o_envelope()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid clothing = await CreateCategoryAsync("Roupas");
        await OpenAsync(checking, 100_000, Day(January, 1));

        await AssignAsync(clothing, January, 30_000);
        await SpendAsync(checking, clothing, 20_000, Day(January, 5));
        await RefundAsync(checking, clothing, 8_000, Day(January, 12));

        BudgetCategoryRow envelope = await EnvelopeAsync(clothing, January);
        Assert.Equal(-12_000, envelope.Activity);
        Assert.Equal(18_000, envelope.Available);
    }

    /// <summary>
    /// O teste que um orcamento materializado falharia: mudar o valor de um
    /// gasto de janeiro precisa corrigir fevereiro e marco sem ninguem
    /// recalcular nada.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Editar_transacao_antiga_corrige_os_meses_seguintes()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid groceries = await CreateCategoryAsync("Mercado");
        await OpenAsync(checking, 100_000, Day(January, 1));
        await AssignAsync(groceries, January, 50_000);

        Transaction purchase = Transaction.Spend(
            Day(January, 10), "Mercado do mes", checking, SystemAccounts.ExternalExpenses,
            Money.FromMinorUnits(40_000, Brl), groceries).Value;
        await new TransactionRepository(_fixture.DataSource).AddAsync(purchase);

        Assert.Equal(10_000, (await EnvelopeAsync(groceries, March)).Available);

        // Reescreve as duas pernas de uma vez, como faria uma edicao: o
        // gatilho diferido confere o balanco so no commit.
        await using (NpgsqlConnection connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (NpgsqlTransaction dbTransaction = await connection.BeginTransactionAsync())
        {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = dbTransaction;
            command.CommandText = """
                UPDATE entries SET amount = CASE WHEN amount < 0 THEN -45000 ELSE 45000 END
                 WHERE transaction_id = @id
                """;
            command.Parameters.AddWithValue("id", purchase.Id);
            await command.ExecuteNonQueryAsync();
            await dbTransaction.CommitAsync();
        }

        Assert.Equal(5_000, (await EnvelopeAsync(groceries, January)).Available);
        Assert.Equal(5_000, (await EnvelopeAsync(groceries, February)).Available);
        Assert.Equal(5_000, (await EnvelopeAsync(groceries, March)).Available);
        Assert.True((await Budget.ReadMonthAsync(March, Brl)).IsBalanced);
    }

    /// <summary>Barrado pela FK composta, nao por codigo: nem SQL direto passa.</summary>
    [RequiresPostgresFact]
    public async Task Categoria_de_receita_recusa_atribuicao_pela_fk()
    {
        Guid salary = await CreateCategoryAsync("Salario", CategoryKind.Income);

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => InsertAssignmentRowAsync(
            salary, new DateOnly(2026, 1, 1)));

        Assert.Equal("23503", error.SqlState);
    }

    [RequiresPostgresFact]
    public async Task Categoria_com_atribuicao_nao_vira_receita()
    {
        Guid groceries = await CreateCategoryAsync("Mercado");
        await AssignAsync(groceries, January, 10_000);

        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(
            "UPDATE categories SET kind = 'INCOME' WHERE id = @id");
        command.Parameters.AddWithValue("id", groceries);

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("23503", error.SqlState);
    }

    [RequiresPostgresFact]
    public async Task Mes_do_orcamento_e_sempre_o_dia_primeiro()
    {
        Guid groceries = await CreateCategoryAsync("Mercado");

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(() => InsertAssignmentRowAsync(
            groceries, new DateOnly(2026, 1, 15)));

        Assert.Equal("23514", error.SqlState);
    }

    // -----------------------------------------------------------------------
    // As regras que a implementacao decidiu

    [RequiresPostgresFact]
    public async Task Atribuir_de_novo_substitui_e_zero_limpa()
    {
        Guid groceries = await CreateCategoryAsync("Mercado");

        await AssignAsync(groceries, January, 50_000);
        await AssignAsync(groceries, January, 30_000);
        Assert.Equal(30_000, (await EnvelopeAsync(groceries, January)).Assigned);

        await AssignAsync(groceries, January, 0);
        Assert.Equal(0, (await EnvelopeAsync(groceries, January)).Assigned);
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM budget_assignments"));
    }

    [RequiresPostgresFact]
    public async Task Receita_vira_pronto_para_atribuir()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid salary = await CreateCategoryAsync("Salario", CategoryKind.Income);

        await EarnAsync(checking, 300_000, Day(January, 5), salary);

        BudgetMonthView view = await Budget.ReadMonthAsync(January, Brl);
        Assert.Equal(300_000, view.ReadyToAssign);
        Assert.Equal(300_000, view.NetInflow);
        Assert.True(view.IsBalanced);
    }

    /// <summary>
    /// Reembolso da empresa categorizado na propria categoria de despesa: a
    /// perna fica na conta de receita, negativa, e o envelope se recompoe em
    /// vez de o dinheiro virar pronto para atribuir.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Reembolso_categorizado_recompoe_o_envelope()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid travel = await CreateCategoryAsync("Viagem");
        await OpenAsync(checking, 100_000, Day(January, 1));
        await AssignAsync(travel, January, 50_000);
        await SpendAsync(checking, travel, 50_000, Day(January, 3));

        await EarnAsync(checking, 20_000, Day(January, 25), travel);

        BudgetMonthView view = await Budget.ReadMonthAsync(January, Brl);
        Assert.Equal(20_000, view.Categories.Single(c => c.CategoryId == travel).Available);
        Assert.Equal(50_000, view.ReadyToAssign);
        Assert.True(view.IsBalanced);
    }

    /// <summary>
    /// Despesa paga direto de uma conta fora do orcamento nao consome
    /// envelope: aquele dinheiro nunca esteve no orcamento.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Despesa_fora_do_orcamento_nao_consome_envelope()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid reserve = await CreateAccountAsync("Reserva", AccountType.Asset, isOnBudget: false);
        Guid leisure = await CreateCategoryAsync("Lazer");
        await OpenAsync(checking, 100_000, Day(January, 1));
        await OpenAsync(reserve, 500_000, Day(January, 1));

        await SpendAsync(reserve, leisure, 30_000, Day(January, 8));

        BudgetMonthView view = await Budget.ReadMonthAsync(January, Brl);
        Assert.Equal(0, view.Categories.Single(c => c.CategoryId == leisure).Activity);
        Assert.Equal(100_000, view.ReadyToAssign);
        Assert.Equal(100_000, view.OnBudgetBalance);
    }

    /// <summary>
    /// Saida sem categoria nao some: reduz o pronto para atribuir e aparece
    /// contada, para incomodar ate ser categorizada.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Saida_sem_categoria_reduz_o_pronto_para_atribuir()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        await OpenAsync(checking, 100_000, Day(January, 1));

        await AddAsync(Transaction.Spend(
            Day(January, 9), "PIX sem descricao", checking, SystemAccounts.ExternalExpenses,
            Money.FromMinorUnits(3_000, Brl)));

        BudgetMonthView view = await Budget.ReadMonthAsync(January, Brl);
        Assert.Equal(97_000, view.ReadyToAssign);
        Assert.Equal(1, view.UncategorizedTransactions);
        Assert.Equal(3_000, view.UncategorizedAmount);
        Assert.True(view.IsBalanced);

        // Fevereiro nao tem saida sem categoria propria, mas herda o efeito.
        BudgetMonthView february = await Budget.ReadMonthAsync(February, Brl);
        Assert.Equal(0, february.UncategorizedTransactions);
        Assert.Equal(97_000, february.ReadyToAssign);
    }

    /// <summary>
    /// Cartao dentro do orcamento: a compra sai do envelope na hora; pagar a
    /// fatura e so dinheiro trocando de conta dentro do orcamento.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Compra_no_cartao_sai_do_envelope_e_pagar_a_fatura_nao_muda_nada()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid card = await CreateAccountAsync("Cartao", AccountType.Liability);
        Guid leisure = await CreateCategoryAsync("Lazer");
        await OpenAsync(checking, 100_000, Day(January, 1));
        await AssignAsync(leisure, January, 20_000);

        await SpendAsync(card, leisure, 15_000, Day(January, 10));
        BudgetMonthView afterPurchase = await Budget.ReadMonthAsync(January, Brl);

        await AddAsync(Transaction.Transfer(
            Day(January, 28), "Fatura", checking, card, Money.FromMinorUnits(15_000, Brl)));
        BudgetMonthView afterPayment = await Budget.ReadMonthAsync(January, Brl);

        Assert.Equal(5_000, afterPurchase.Categories.Single(c => c.CategoryId == leisure).Available);
        Assert.Equal(80_000, afterPurchase.ReadyToAssign);
        Assert.Equal(afterPurchase.ReadyToAssign, afterPayment.ReadyToAssign);
        Assert.Equal(afterPurchase.Available, afterPayment.Available);
        Assert.True(afterPayment.IsBalanced);
    }

    [RequiresPostgresFact]
    public async Task Transacao_apagada_sai_do_orcamento()
    {
        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid groceries = await CreateCategoryAsync("Mercado");
        await OpenAsync(checking, 100_000, Day(January, 1));
        await AssignAsync(groceries, January, 50_000);

        Transaction purchase = Transaction.Spend(
            Day(January, 10), "Lancado duas vezes", checking, SystemAccounts.ExternalExpenses,
            Money.FromMinorUnits(40_000, Brl), groceries).Value;
        await new TransactionRepository(_fixture.DataSource).AddAsync(purchase);
        await new LedgerQueries(_fixture.DataSource).SoftDeleteAsync(purchase.Id);

        BudgetCategoryRow envelope = await EnvelopeAsync(groceries, January);
        Assert.Equal(0, envelope.Activity);
        Assert.Equal(50_000, envelope.Available);
    }

    /// <summary>
    /// Esconder envelope arquivado que ainda tem dinheiro faria o total da
    /// tela divergir do razao. Arquivado e vazio, esse sim, some.
    /// </summary>
    [RequiresPostgresFact]
    public async Task Envelope_arquivado_com_dinheiro_continua_na_tela()
    {
        Guid gifts = await CreateCategoryAsync("Presentes");
        Guid old = await CreateCategoryAsync("Antiga");
        await AssignAsync(gifts, January, 7_000);

        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(
            "UPDATE categories SET is_archived = TRUE WHERE id IN (@gifts, @old)");
        command.Parameters.AddWithValue("gifts", gifts);
        command.Parameters.AddWithValue("old", old);
        await command.ExecuteNonQueryAsync();

        BudgetMonthView view = await Budget.ReadMonthAsync(February, Brl);

        Assert.Contains(view.Categories, c => c.CategoryId == gifts && c.IsArchived && c.Available == 7_000);
        Assert.DoesNotContain(view.Categories, c => c.CategoryId == old);
    }

    // -----------------------------------------------------------------------
    // A invariante, com operacoes aleatorias

    /// <summary>
    /// Mesmo formato do teste do razao: seed na mensagem, qualquer falha e
    /// reproduzivel. A cada punhado de passos, todo mes e conferido contra um
    /// modelo em memoria escrito da forma mais burra possivel - somas por
    /// forca bruta sobre a lista de operacoes - e o pronto para atribuir do
    /// modelo sai das ENTRADAS, nao de "saldo menos envelopes". Se saisse, a
    /// comparacao seria verdadeira por construcao.
    /// </summary>
    [RequiresPostgresTheory]
    [InlineData(11)]
    [InlineData(22)]
    [InlineData(33)]
    public async Task Orcamento_fecha_em_todo_mes_apos_operacoes_aleatorias(int seed)
    {
        var rng = new Random(seed);
        var model = new BudgetModel();
        var ledger = new LedgerQueries(_fixture.DataSource);

        Guid checking = await CreateAccountAsync("Corrente", AccountType.Asset);
        Guid card = await CreateAccountAsync("Cartao", AccountType.Liability);
        Guid reserve = await CreateAccountAsync("Reserva", AccountType.Asset, isOnBudget: false);
        Guid salary = await CreateCategoryAsync("Salario", CategoryKind.Income);
        Guid[] envelopes =
        [
            await CreateCategoryAsync("Mercado"),
            await CreateCategoryAsync("Lazer"),
            await CreateCategoryAsync("Transporte"),
        ];

        BudgetMonth[] months = [January, February, March, March.Next];

        int assignments = 0, deletions = 0, offBudget = 0, uncategorized = 0;

        for (int step = 0; step < 60; step++)
        {
            BudgetMonth month = months[rng.Next(months.Length)];
            DateOnly on = month.FirstDay.AddDays(rng.Next(28));
            long amount = rng.Next(1, 50_000);
            Guid envelope = envelopes[rng.Next(envelopes.Length)];

            switch (rng.Next(9))
            {
                case 0:
                    model.Record(await EarnAsync(checking, amount, on, rng.Next(2) == 0 ? salary : null),
                        month, onBudget: amount);
                    break;

                case 1:
                    model.Record(await SpendAsync(checking, envelope, amount, on),
                        month, onBudget: -amount, envelope, spent: amount);
                    break;

                case 2:
                    model.Record(await SpendAsync(card, envelope, amount, on),
                        month, onBudget: -amount, envelope, spent: amount);
                    break;

                case 3:
                    model.Record(await RefundAsync(checking, envelope, amount, on),
                        month, onBudget: amount, envelope, spent: -amount);
                    break;

                case 4:
                    Guid loose = (await AddAsync(Transaction.Spend(
                        on, "Sem categoria", checking, SystemAccounts.ExternalExpenses,
                        Money.FromMinorUnits(amount, Brl)))).Id;
                    model.Record(loose, month, onBudget: -amount);
                    uncategorized++;
                    break;

                case 5:
                    // Invisivel para o orcamento: nem conta do orcamento, nem envelope.
                    model.Record(await SpendAsync(reserve, envelope, amount, on), month, onBudget: 0);
                    offBudget++;
                    break;

                case 6:
                    bool toReserve = rng.Next(2) == 0;
                    Guid transfer = (await AddAsync(Transaction.Transfer(
                        on, "Reserva", toReserve ? checking : reserve, toReserve ? reserve : checking,
                        Money.FromMinorUnits(amount, Brl)))).Id;
                    model.Record(transfer, month, onBudget: toReserve ? -amount : amount);
                    break;

                case 7:
                    // Negativo e zero de proposito: tirar do envelope e limpar sao operacoes normais.
                    long assigned = rng.Next(4) == 0 ? 0 : rng.Next(-20_000, 80_000);
                    await AssignAsync(envelope, month, assigned);
                    model.Assign(envelope, month, assigned);
                    assignments++;
                    break;

                default:
                    IReadOnlyList<Guid> live = model.LiveTransactions;
                    if (live.Count > 0)
                    {
                        Guid victim = live[rng.Next(live.Count)];
                        await ledger.SoftDeleteAsync(victim);
                        model.Delete(victim);
                        deletions++;
                    }

                    break;
            }

            if (step % 15 == 14)
            {
                await VerifyAgainstModelAsync(model, months, envelopes, $"seed {seed}, passo {step}");
            }
        }

        await VerifyAgainstModelAsync(model, months, envelopes, $"seed {seed}, fim");

        BudgetIntegrity integrity = Assert.Single(
            await Budget.ReadIntegrityAsync(), row => row.CurrencyCode == "BRL");
        Assert.True(integrity.IsIntact, $"[seed {seed}] v_budget_integrity: diferenca de {integrity.Difference}.");

        // Sanidade do proprio teste: sem isto um gerador preguicoso passaria verde.
        Assert.True(assignments > 0, $"[seed {seed}] nenhuma atribuicao exercitada.");
        Assert.True(uncategorized > 0, $"[seed {seed}] nenhuma saida sem categoria exercitada.");
        Assert.True(deletions > 0, $"[seed {seed}] nenhuma exclusao exercitada.");
        Assert.True(offBudget > 0, $"[seed {seed}] nenhuma despesa fora do orcamento exercitada.");
    }

    private async Task VerifyAgainstModelAsync(
        BudgetModel model, BudgetMonth[] months, Guid[] envelopes, string where)
    {
        // Um mes alem dos dados: o disponivel precisa rolar mesmo sem linha nenhuma.
        foreach (BudgetMonth month in months.Append(months[^1].Next))
        {
            BudgetMonthView view = await Budget.ReadMonthAsync(month, Brl);
            string at = $"[{where}, {month}]";

            Assert.True(view.IsBalanced,
                $"{at} envelopes {view.Available} + pronto {view.ReadyToAssign} != saldo {view.OnBudgetBalance}.");
            Assert.True(model.Balance(month) == view.OnBudgetBalance,
                $"{at} saldo: modelo {model.Balance(month)}, banco {view.OnBudgetBalance}.");
            Assert.True(model.ReadyToAssign(month) == view.ReadyToAssign,
                $"{at} pronto: modelo {model.ReadyToAssign(month)}, banco {view.ReadyToAssign}.");

            foreach (Guid envelope in envelopes)
            {
                BudgetCategoryRow row = view.Categories.Single(c => c.CategoryId == envelope);
                var expected = (model.Assigned(envelope, month), model.Activity(envelope, month), model.Available(envelope, month));

                Assert.True(expected == (row.Assigned, row.Activity, row.Available),
                    $"{at} envelope {row.Name}: modelo {expected}, banco {(row.Assigned, row.Activity, row.Available)}.");
            }
        }
    }

    /// <summary>
    /// O oraculo. Nada de janela, nada de acumulado incremental: cada numero
    /// e uma soma sobre a lista inteira, para ser obviamente certo.
    /// </summary>
    private sealed class BudgetModel
    {
        private readonly List<Operation> _operations = [];
        private readonly Dictionary<(Guid Category, BudgetMonth Month), long> _assignments = [];

        public IReadOnlyList<Guid> LiveTransactions =>
            _operations.Where(o => !o.IsDeleted).Select(o => o.TransactionId).ToList();

        public void Record(
            Guid transactionId, BudgetMonth month, long onBudget, Guid? envelope = null, long spent = 0) =>
            _operations.Add(new Operation(transactionId, month, onBudget, envelope, spent));

        public void Assign(Guid category, BudgetMonth month, long amount)
        {
            if (amount == 0)
            {
                _assignments.Remove((category, month));
            }
            else
            {
                _assignments[(category, month)] = amount;
            }
        }

        public void Delete(Guid transactionId) =>
            _operations.Single(o => o.TransactionId == transactionId).IsDeleted = true;

        public long Balance(BudgetMonth month) =>
            Live(month).Sum(o => o.OnBudget);

        /// <summary>Pelas entradas: o que chegou ao orcamento sem envelope, menos o atribuido.</summary>
        public long ReadyToAssign(BudgetMonth month) =>
            Live(month).Sum(o => o.OnBudget + o.Spent)
            - _assignments.Where(a => a.Key.Month <= month).Sum(a => a.Value);

        public long Assigned(Guid category, BudgetMonth month) =>
            _assignments.GetValueOrDefault((category, month));

        public long Activity(Guid category, BudgetMonth month) =>
            -Live(month).Where(o => o.Envelope == category && o.Month == month).Sum(o => o.Spent);

        public long Available(Guid category, BudgetMonth month) =>
            _assignments.Where(a => a.Key.Category == category && a.Key.Month <= month).Sum(a => a.Value)
            - Live(month).Where(o => o.Envelope == category).Sum(o => o.Spent);

        private IEnumerable<Operation> Live(BudgetMonth upTo) =>
            _operations.Where(o => !o.IsDeleted && o.Month <= upTo);

        private sealed record Operation(Guid TransactionId, BudgetMonth Month, long OnBudget, Guid? Envelope, long Spent)
        {
            public bool IsDeleted { get; set; }
        }
    }

    // -----------------------------------------------------------------------

    private BudgetRepository Budget => new(_fixture.DataSource);

    private static DateOnly Day(BudgetMonth month, int day) => month.FirstDay.AddDays(day - 1);

    private async Task<Guid> CreateAccountAsync(string name, AccountType type, bool isOnBudget = true)
    {
        Account account = Account.Create(name, type, Brl, isOnBudget: isOnBudget).Value;
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

    private async Task OpenAsync(Guid account, long minorUnits, DateOnly on) =>
        await AddAsync(Transaction.OpeningBalance(
            on, account, SystemAccounts.OpeningBalances, Money.FromMinorUnits(minorUnits, Brl)));

    private async Task<Guid> SpendAsync(Guid account, Guid category, long minorUnits, DateOnly on) =>
        (await AddAsync(Transaction.Spend(
            on, "Gasto", account, SystemAccounts.ExternalExpenses,
            Money.FromMinorUnits(minorUnits, Brl), category))).Id;

    private async Task<Guid> RefundAsync(Guid account, Guid category, long minorUnits, DateOnly on) =>
        (await AddAsync(Transaction.Create(new TransactionDraft
        {
            OccurredOn = on,
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
        }))).Id;

    private async Task<Guid> EarnAsync(Guid account, long minorUnits, DateOnly on, Guid? category) =>
        (await AddAsync(Transaction.Create(new TransactionDraft
        {
            OccurredOn = on,
            Description = "Entrada",
            Currency = Brl,
            Entries =
            [
                new EntryDraft(account, Money.FromMinorUnits(minorUnits, Brl)),
                new EntryDraft(SystemAccounts.ExternalRevenue, Money.FromMinorUnits(-minorUnits, Brl))
                {
                    CategoryId = category,
                },
            ],
        }))).Id;

    private Task AssignAsync(Guid category, BudgetMonth month, long minorUnits) =>
        Budget.SetAsync(BudgetAssignment.Create(category, month, Money.FromMinorUnits(minorUnits, Brl)).Value);

    private async Task<BudgetCategoryRow> EnvelopeAsync(Guid category, BudgetMonth month)
    {
        BudgetMonthView view = await Budget.ReadMonthAsync(month, Brl);
        return Assert.Single(view.Categories, c => c.CategoryId == category);
    }

    private async Task InsertAssignmentRowAsync(Guid category, DateOnly month)
    {
        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand("""
            INSERT INTO budget_assignments (id, category_id, month, amount, currency_code)
            VALUES (@id, @category, @month, 1000, 'BRL')
            """);
        command.Parameters.AddWithValue("id", UuidV7.NewGuid());
        command.Parameters.AddWithValue("category", category);
        command.Parameters.AddWithValue("month", month);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
