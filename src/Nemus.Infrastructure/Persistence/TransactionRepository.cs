using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Npgsql;
using NpgsqlTypes;

namespace Nemus.Infrastructure.Persistence;

/// <summary>Quantas transacoes entraram e quantas ja existiam.</summary>
public sealed record ImportSummary(int Inserted, int SkippedAsDuplicate)
{
    public int Total => Inserted + SkippedAsDuplicate;
}

public sealed class TransactionRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public TransactionRepository(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>
    /// Grava transacao e pernas na mesma transacao de banco. O gatilho
    /// diferido so avalia no COMMIT, entao as pernas podem entrar uma a uma
    /// sem que estados intermediarios sejam vistos como desbalanceados.
    /// </summary>
    public async Task AddAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction dbTransaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await InsertTransactionAsync(connection, dbTransaction, transaction, onConflictDoNothing: false, cancellationToken)
            .ConfigureAwait(false);
        await InsertEntriesAsync(connection, dbTransaction, transaction, cancellationToken)
            .ConfigureAwait(false);

        await dbTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Grava a transacao numa conexao que JA esta aberta, dentro da transacao
    /// de banco de quem chamou.
    ///
    /// Existe para os casos em que o razao e outra coisa precisam entrar
    /// juntos ou nao entrar: a compra parcelada e o cronograma dela, por
    /// exemplo. Em duas transacoes separadas, uma falha no meio deixaria o
    /// razao dizendo que houve a compra sem plano nenhum por tras.
    /// </summary>
    internal static async Task AddWithinAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction dbTransaction,
        Transaction transaction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        await InsertTransactionAsync(connection, dbTransaction, transaction, onConflictDoNothing: false, cancellationToken)
            .ConfigureAwait(false);
        await InsertEntriesAsync(connection, dbTransaction, transaction, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// PILAR 3 - importacao idempotente.
    ///
    /// Cada transacao tenta entrar com ON CONFLICT DO NOTHING contra o
    /// indice unico (source, source_account_ref, external_id). Quando o
    /// INSERT nao devolve linha, a transacao ja existia e as pernas dela
    /// NAO sao inseridas - repare que inserir as pernas de novo e o jeito
    /// classico de duplicar valor e desbalancear tudo.
    ///
    /// Reimportar o mesmo arquivo mil vezes deixa o banco identico.
    /// </summary>
    public async Task<ImportSummary> ImportAsync(
        IReadOnlyList<Transaction> transactions, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactions);

        int inserted = 0;
        int skipped = 0;

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction dbTransaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (Transaction transaction in transactions)
        {
            bool wasInserted = await InsertTransactionAsync(
                connection, dbTransaction, transaction, onConflictDoNothing: true, cancellationToken)
                .ConfigureAwait(false);

            if (!wasInserted)
            {
                skipped++;
                continue;
            }

            await InsertEntriesAsync(connection, dbTransaction, transaction, cancellationToken)
                .ConfigureAwait(false);
            inserted++;
        }

        await dbTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ImportSummary(inserted, skipped);
    }

    private static async Task<bool> InsertTransactionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction dbTransaction,
        Transaction transaction,
        bool onConflictDoNothing,
        CancellationToken cancellationToken)
    {
        string conflictClause = onConflictDoNothing ? "ON CONFLICT DO NOTHING" : string.Empty;

        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = dbTransaction;
        command.CommandText = $"""
            INSERT INTO transactions (
                id, occurred_on, booked_at, description, payee_id, currency_code,
                kind, notes, source, source_account_ref, external_id,
                import_batch_id, created_at, updated_at, deleted_at)
            VALUES (
                @id, @occurred_on, @booked_at, @description, @payee_id, @currency_code,
                @kind, @notes, @source, @source_account_ref, @external_id,
                @import_batch_id, @created_at, @updated_at, @deleted_at)
            {conflictClause}
            RETURNING id
            """;

        command.Parameters.AddWithValue("id", transaction.Id);
        command.Parameters.Add(new NpgsqlParameter("occurred_on", NpgsqlDbType.Date)
        {
            Value = transaction.OccurredOn,
        });
        AddNullable(command, "booked_at", transaction.BookedAt);
        command.Parameters.AddWithValue("description", transaction.Description);
        AddNullable(command, "payee_id", transaction.PayeeId);
        command.Parameters.AddWithValue("currency_code", transaction.Currency.Code);
        command.Parameters.AddWithValue("kind", transaction.Kind.ToCode());
        AddNullable(command, "notes", transaction.Notes);
        command.Parameters.AddWithValue("source", transaction.Source.ToCode());
        AddNullable(command, "source_account_ref", transaction.External?.AccountRef);
        AddNullable(command, "external_id", transaction.External?.ExternalId);
        AddNullable(command, "import_batch_id", transaction.ImportBatchId);
        command.Parameters.AddWithValue("created_at", transaction.CreatedAt);
        command.Parameters.AddWithValue("updated_at", transaction.UpdatedAt);
        AddNullable(command, "deleted_at", transaction.DeletedAt);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task InsertEntriesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction dbTransaction,
        Transaction transaction,
        CancellationToken cancellationToken)
    {
        foreach (Entry entry in transaction.Entries)
        {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = dbTransaction;
            command.CommandText = """
                INSERT INTO entries (
                    id, transaction_id, account_id, currency_code, amount,
                    category_id, memo, sort_order)
                VALUES (
                    @id, @transaction_id, @account_id, @currency_code, @amount,
                    @category_id, @memo, @sort_order)
                """;

            command.Parameters.AddWithValue("id", entry.Id);
            command.Parameters.AddWithValue("transaction_id", entry.TransactionId);
            command.Parameters.AddWithValue("account_id", entry.AccountId);
            command.Parameters.AddWithValue("currency_code", entry.Amount.Currency.Code);
            command.Parameters.AddWithValue("amount", entry.Amount.MinorUnits);
            AddNullable(command, "category_id", entry.CategoryId);
            AddNullable(command, "memo", entry.Memo);
            command.Parameters.AddWithValue("sort_order", entry.SortOrder);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // -----------------------------------------------------------------------

    public async Task<bool> ExistsAsync(
        ExternalReference reference, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT 1 FROM transactions
             WHERE source = @source
               AND COALESCE(source_account_ref, '') = @account_ref
               AND external_id = @external_id
             LIMIT 1
            """);

        command.Parameters.AddWithValue("source", reference.Source.ToCode());
        command.Parameters.AddWithValue("account_ref", reference.AccountRef ?? string.Empty);
        command.Parameters.AddWithValue("external_id", reference.ExternalId);

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    public async Task<int> CountAsync(
        bool includeDeleted = false, CancellationToken cancellationToken = default)
    {
        string filter = includeDeleted ? string.Empty : "WHERE deleted_at IS NULL";
        await using NpgsqlCommand command =
            _dataSource.CreateCommand($"SELECT COUNT(*) FROM transactions {filter}");

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<int> CountEntriesAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("SELECT COUNT(*) FROM entries");
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<Transaction?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        string description;
        DateOnly occurredOn;
        DateTimeOffset? bookedAt;
        Guid? payeeId;
        Currency currency;
        TransactionKind kind;
        string? notes;
        ImportSource source;
        string? sourceAccountRef;
        string? externalId;
        Guid? importBatchId;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;
        DateTimeOffset? deletedAt;

        await using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT occurred_on, booked_at, description, payee_id, currency_code,
                       kind, notes, source, source_account_ref, external_id,
                       import_batch_id, created_at, updated_at, deleted_at
                  FROM transactions WHERE id = @id
                """;
            command.Parameters.AddWithValue("id", id);

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            occurredOn = reader.GetFieldValue<DateOnly>(0);
            bookedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
            description = reader.GetString(2);
            payeeId = reader.IsDBNull(3) ? null : reader.GetGuid(3);
            currency = Currency.From(reader.GetString(4));
            kind = TransactionKindExtensions.FromCode(reader.GetString(5));
            notes = reader.IsDBNull(6) ? null : reader.GetString(6);
            source = ImportSourceExtensions.FromCode(reader.GetString(7));
            sourceAccountRef = reader.IsDBNull(8) ? null : reader.GetString(8);
            externalId = reader.IsDBNull(9) ? null : reader.GetString(9);
            importBatchId = reader.IsDBNull(10) ? null : reader.GetGuid(10);
            createdAt = reader.GetFieldValue<DateTimeOffset>(11);
            updatedAt = reader.GetFieldValue<DateTimeOffset>(12);
            deletedAt = reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13);
        }

        var entries = new List<EntryDraft>();
        await using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, account_id, amount, category_id, memo
                  FROM entries WHERE transaction_id = @id ORDER BY sort_order
                """;
            command.Parameters.AddWithValue("id", id);

            await using NpgsqlDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new EntryDraft(
                    reader.GetGuid(1),
                    Money.FromMinorUnits(reader.GetInt64(2), currency))
                {
                    Id = reader.GetGuid(0),
                    CategoryId = reader.IsDBNull(3) ? null : reader.GetGuid(3),
                    Memo = reader.IsDBNull(4) ? null : reader.GetString(4),
                });
            }
        }

        ExternalReference? external = null;
        if (externalId is not null && source != ImportSource.Manual)
        {
            external = ExternalReference.Create(source, externalId, sourceAccountRef).Value;
        }

        // Rehydrate revalida: dado corrompido no banco falha alto aqui, em
        // vez de virar razao quebrado dentro da aplicacao.
        var result = Transaction.Rehydrate(
            id, occurredOn, bookedAt, description, payeeId, currency, kind, notes,
            external, importBatchId, createdAt, updatedAt, deletedAt, entries);

        return result.IsSuccess
            ? result.Value
            : throw new InvalidOperationException(
                $"A transacao {id} esta corrompida no banco: {result.Error}");
    }

    private static void AddNullable<T>(NpgsqlCommand command, string name, T? value)
        where T : struct =>
        command.Parameters.AddWithValue(name, value.HasValue ? value.Value : DBNull.Value);

    private static void AddNullable(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value);
}
