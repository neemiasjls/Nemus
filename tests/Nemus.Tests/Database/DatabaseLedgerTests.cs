using Nemus.Domain.Accounts;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Migrations;
using Nemus.Infrastructure.Persistence;
using Nemus.Tests.Domain;
using Npgsql;
using Xunit;

namespace Nemus.Tests.Database;

/// <summary>
/// TESTE (a) e (b) contra Postgres de verdade.
///
/// O ponto de rodar tambem no banco: provar que nao da para furar a
/// invariante por fora do dominio. Quem abrir um psql e escrever INSERT na
/// mao continua esbarrando no gatilho diferido.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DatabaseLedgerTests : IAsyncLifetime
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly DateOnly Hoje = new(2026, 3, 15);

    private readonly PostgresFixture _fixture;

    public DatabaseLedgerTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (PostgresFixture.IsAvailable)
        {
            await _fixture.ResetDataAsync().ConfigureAwait(false);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // -----------------------------------------------------------------------

    [RequiresPostgresFact]
    public async Task Migrations_sao_idempotentes()
    {
        var runner = new MigrationRunner(_fixture.DataSource);

        IReadOnlyList<string> segunda = await runner.ApplyAsync();

        // O schema ja foi aplicado no InitializeAsync da fixture.
        Assert.Empty(segunda);
        Assert.Equal(runner.DiscoverMigrations().Count, (await runner.GetAppliedAsync()).Count);
    }

    [RequiresPostgresFact]
    public async Task Contas_de_sistema_batem_com_as_constantes_do_dominio()
    {
        var repository = new AccountRepository(_fixture.DataSource);

        IReadOnlyList<Guid> noBanco = await repository.GetSystemAccountIdsAsync();

        Assert.Equal(SystemAccounts.All.OrderBy(id => id), noBanco.OrderBy(id => id));
    }

    [RequiresPostgresFact]
    public async Task Insercao_direta_desbalanceada_e_recusada_no_commit()
    {
        Guid conta = await CriarContaAsync("Corrente", AccountType.Asset);
        Guid transacao = UuidV7.NewGuid();

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using NpgsqlConnection connection = await _fixture.DataSource.OpenConnectionAsync();
            await using NpgsqlTransaction dbTransaction = await connection.BeginTransactionAsync();

            await InsertTransactionRowAsync(connection, dbTransaction, transacao, "Furo pelo SQL");
            await InsertEntryRowAsync(connection, dbTransaction, transacao, conta, -10000, 0);
            await InsertEntryRowAsync(
                connection, dbTransaction, transacao, SystemAccounts.ExternalExpenses, 9999, 1);

            // O gatilho e diferido: so aqui a soma e avaliada.
            await dbTransaction.CommitAsync();
        });

        Assert.Equal(NemusSqlStates.TransactionUnbalanced, error.SqlState);
        Assert.Contains("desbalanceada", error.MessageText, StringComparison.Ordinal);
    }

    [RequiresPostgresFact]
    public async Task Transacao_com_uma_perna_so_e_recusada()
    {
        Guid conta = await CriarContaAsync("Corrente", AccountType.Asset);
        Guid transacao = UuidV7.NewGuid();

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using NpgsqlConnection connection = await _fixture.DataSource.OpenConnectionAsync();
            await using NpgsqlTransaction dbTransaction = await connection.BeginTransactionAsync();

            await InsertTransactionRowAsync(connection, dbTransaction, transacao, "Perna solta");
            await InsertEntryRowAsync(connection, dbTransaction, transacao, conta, 10000, 0);

            await dbTransaction.CommitAsync();
        });

        Assert.Equal(NemusSqlStates.TransactionTooFewEntries, error.SqlState);
    }

    [RequiresPostgresFact]
    public async Task Transacao_sem_nenhuma_perna_e_recusada()
    {
        // Sem o gatilho em transactions, este caso passaria: nenhuma linha
        // de entries chega a existir para disparar o outro gatilho.
        Guid transacao = UuidV7.NewGuid();

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using NpgsqlConnection connection = await _fixture.DataSource.OpenConnectionAsync();
            await using NpgsqlTransaction dbTransaction = await connection.BeginTransactionAsync();

            await InsertTransactionRowAsync(connection, dbTransaction, transacao, "Cabeca sem corpo");

            await dbTransaction.CommitAsync();
        });

        Assert.Equal(NemusSqlStates.TransactionTooFewEntries, error.SqlState);
    }

    [RequiresPostgresFact]
    public async Task Perna_em_moeda_diferente_da_conta_e_impossivel_por_fk()
    {
        Guid conta = await CriarContaAsync("Corrente", AccountType.Asset);
        Guid transacao = UuidV7.NewGuid();

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using NpgsqlConnection connection = await _fixture.DataSource.OpenConnectionAsync();
            await using NpgsqlTransaction dbTransaction = await connection.BeginTransactionAsync();

            await InsertTransactionRowAsync(connection, dbTransaction, transacao, "Moeda trocada");

            await using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = dbTransaction;
            command.CommandText = """
                INSERT INTO entries (id, transaction_id, account_id, currency_code, amount, sort_order)
                VALUES (@id, @tx, @account, 'USD', 10000, 0)
                """;
            command.Parameters.AddWithValue("id", UuidV7.NewGuid());
            command.Parameters.AddWithValue("tx", transacao);
            command.Parameters.AddWithValue("account", conta);
            await command.ExecuteNonQueryAsync();

            await dbTransaction.CommitAsync();
        });

        // Nao e trigger: e a FK composta (account_id, currency_code).
        Assert.Equal("23503", error.SqlState);
    }

    [RequiresPostgresFact]
    public async Task Conta_de_sistema_nao_pode_ser_removida()
    {
        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(
            "DELETE FROM accounts WHERE id = @id");
        command.Parameters.AddWithValue("id", SystemAccounts.OpeningBalances);

        PostgresException error =
            await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(NemusSqlStates.SystemAccountProtected, error.SqlState);
    }

    [RequiresPostgresFact]
    public async Task Categoria_alem_de_dois_niveis_e_recusada()
    {
        Guid raiz = await CriarCategoriaAsync("Casa", null);
        Guid filha = await CriarCategoriaAsync("Mercado", raiz);

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => CriarCategoriaAsync("Horti", filha));

        Assert.Equal(NemusSqlStates.CategoryDepthExceeded, error.SqlState);
    }

    // -----------------------------------------------------------------------
    // TESTE (a) no banco

    [RequiresPostgresTheory]
    [InlineData(11)]
    [InlineData(22)]
    [InlineData(33)]
    public async Task Razao_permanece_integro_apos_operacoes_aleatorias(int seed)
    {
        var accounts = new List<Guid>
        {
            await CriarContaAsync("Corrente", AccountType.Asset),
            await CriarContaAsync("Poupanca", AccountType.Asset),
            await CriarContaAsync("Cartao", AccountType.Liability),
            SystemAccounts.ExternalExpenses,
            SystemAccounts.ExternalRevenue,
            SystemAccounts.OpeningBalances,
        };

        var repository = new TransactionRepository(_fixture.DataSource);
        var integrity = new LedgerIntegrityReader(_fixture.DataSource);
        var rng = new Random(seed);

        var lote = new List<Transaction>();
        for (int i = 0; i < 120; i++)
        {
            Result<Transaction> result = Transaction.Create(
                LedgerGenerator.Balanced(rng, accounts, Brl, Hoje));

            Assert.True(result.IsSuccess, result.Error.ToString());
            lote.Add(result.Value);
        }

        await repository.ImportAsync(lote);

        LedgerIntegrity estado = await integrity.ReadAsync();
        Assert.True(estado.IsIntact, estado.Describe());

        // Prova de que o teste nao passou por vacuidade.
        Assert.Equal(120, await repository.CountAsync());
        Assert.True(await repository.CountEntriesAsync() >= 240);

        IReadOnlyList<AccountBalance> saldos =
            await new AccountRepository(_fixture.DataSource).GetBalancesAsync();

        Assert.Equal(0, saldos.Sum(s => s.Balance.MinorUnits));
        Assert.Equal(
            saldos.Where(s => s.IsInternal).Sum(s => s.Balance.MinorUnits),
            -saldos.Where(s => !s.IsInternal).Sum(s => s.Balance.MinorUnits));
    }

    [RequiresPostgresFact]
    public async Task Soft_delete_preserva_a_soma_zero()
    {
        Guid conta = await CriarContaAsync("Corrente", AccountType.Asset);
        var repository = new TransactionRepository(_fixture.DataSource);
        var integrity = new LedgerIntegrityReader(_fixture.DataSource);

        Transaction transacao = Transaction.Spend(
            Hoje, "Mercado", conta, SystemAccounts.ExternalExpenses, Money.FromUnits(150, Brl)).Value;

        await repository.AddAsync(transacao);
        Assert.True((await integrity.ReadAsync()).IsIntact);

        await using (NpgsqlCommand command = _fixture.DataSource.CreateCommand(
            "UPDATE transactions SET deleted_at = now() WHERE id = @id"))
        {
            command.Parameters.AddWithValue("id", transacao.Id);
            await command.ExecuteNonQueryAsync();
        }

        // As duas pernas saem juntas do calculo: a soma continua zero.
        LedgerIntegrity depois = await integrity.ReadAsync();
        Assert.True(depois.IsIntact, depois.Describe());
        Assert.Equal(0, depois.TotalAmount);
        Assert.Equal(0, await repository.CountAsync());
        Assert.Equal(1, await repository.CountAsync(includeDeleted: true));
    }

    [RequiresPostgresFact]
    public async Task Transacao_gravada_volta_identica_do_banco()
    {
        Guid conta = await CriarContaAsync("Corrente", AccountType.Asset);
        var repository = new TransactionRepository(_fixture.DataSource);

        Transaction original = Transaction.Create(new TransactionDraft
        {
            OccurredOn = Hoje,
            Description = "Supermercado dividido",
            Currency = Brl,
            Entries =
            [
                new EntryDraft(conta, Money.FromUnits(-300, Brl)),
                new EntryDraft(SystemAccounts.ExternalExpenses, Money.FromUnits(220, Brl)),
                new EntryDraft(SystemAccounts.ExternalExpenses, Money.FromUnits(80, Brl)),
            ],
        }).Value;

        await repository.AddAsync(original);

        Transaction? lida = await repository.FindAsync(original.Id);

        Assert.NotNull(lida);
        Assert.Equal(original.Description, lida.Description);
        Assert.Equal(original.OccurredOn, lida.OccurredOn);
        Assert.True(lida.Balance.IsZero);
        Assert.Equal(
            original.Entries.Select(e => e.Amount.MinorUnits),
            lida.Entries.Select(e => e.Amount.MinorUnits));
    }

    // -----------------------------------------------------------------------

    private async Task<Guid> CriarContaAsync(string nome, AccountType tipo)
    {
        Account conta = Account.Create(nome, tipo, Brl).Value;
        await new AccountRepository(_fixture.DataSource).AddAsync(conta);
        return conta.Id;
    }

    private async Task<Guid> CriarCategoriaAsync(string nome, Guid? pai)
    {
        Guid id = UuidV7.NewGuid();

        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand("""
            INSERT INTO categories (id, parent_id, name, kind) VALUES (@id, @parent, @name, 'EXPENSE')
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("parent", pai.HasValue ? pai.Value : DBNull.Value);
        command.Parameters.AddWithValue("name", nome);

        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task InsertTransactionRowAsync(
        NpgsqlConnection connection, NpgsqlTransaction dbTransaction, Guid id, string descricao)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = """
            INSERT INTO transactions (id, occurred_on, description, currency_code, kind, source)
            VALUES (@id, DATE '2026-03-15', @description, 'BRL', 'STANDARD', 'MANUAL')
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("description", descricao);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertEntryRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction dbTransaction,
        Guid transactionId,
        Guid accountId,
        long amount,
        short sortOrder)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = """
            INSERT INTO entries (id, transaction_id, account_id, currency_code, amount, sort_order)
            VALUES (@id, @tx, @account, 'BRL', @amount, @sort)
            """;
        command.Parameters.AddWithValue("id", UuidV7.NewGuid());
        command.Parameters.AddWithValue("tx", transactionId);
        command.Parameters.AddWithValue("account", accountId);
        command.Parameters.AddWithValue("amount", amount);
        command.Parameters.AddWithValue("sort", sortOrder);
        await command.ExecuteNonQueryAsync();
    }
}
