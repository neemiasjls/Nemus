using Nemus.Domain.Ledger;
using Nemus.Domain.Primitives;
using Npgsql;

namespace Nemus.Infrastructure.Persistence;

public sealed record ImportBatch(
    Guid Id,
    ImportSource Source,
    string? FileSha256,
    Guid? AccountId,
    DateTimeOffset ImportedAt,
    int RowCount);

public sealed class ImportBatchRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public ImportBatchRepository(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>
    /// Primeira camada de idempotencia: o arquivo inteiro. E atalho barato,
    /// nao garantia - dois downloads do mesmo periodo podem diferir num byte
    /// de cabecalho. Quem garante correcao e o FITID de cada linha.
    /// </summary>
    public async Task<ImportBatch?> FindByFileHashAsync(
        ImportSource source, string fileSha256, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT id, source, file_sha256, account_id, imported_at, row_count
              FROM import_batches
             WHERE source = @source AND file_sha256 = @sha
            """);

        command.Parameters.AddWithValue("source", source.ToCode());
        command.Parameters.AddWithValue("sha", fileSha256);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ImportBatch(
            reader.GetGuid(0),
            ImportSourceExtensions.FromCode(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetInt32(5));
    }

    public async Task AddAsync(ImportBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            INSERT INTO import_batches (id, source, file_sha256, account_id, imported_at, row_count)
            VALUES (@id, @source, @sha, @account_id, @imported_at, @row_count)
            """);

        command.Parameters.AddWithValue("id", batch.Id);
        command.Parameters.AddWithValue("source", batch.Source.ToCode());
        command.Parameters.AddWithValue(
            "sha", batch.FileSha256 is null ? DBNull.Value : batch.FileSha256);
        command.Parameters.AddWithValue(
            "account_id", batch.AccountId is null ? DBNull.Value : batch.AccountId.Value);
        command.Parameters.AddWithValue("imported_at", batch.ImportedAt);
        command.Parameters.AddWithValue("row_count", batch.RowCount);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Conta do razao ligada a uma identidade de conta na origem (ACCTID do
    /// OFX). E o que permite importar sem perguntar a conta toda vez, depois
    /// da primeira ligacao.
    /// </summary>
    public async Task<Guid?> FindAccountByExternalRefAsync(
        string externalRef, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "SELECT id FROM accounts WHERE external_ref = @ref AND NOT is_archived");
        command.Parameters.AddWithValue("ref", externalRef);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is Guid id ? id : null;
    }

    /// <summary>
    /// Grava a ligacao entre a conta do razao e a identidade na origem.
    /// Recusa se a referencia ja pertencer a outra conta: apontar duas contas
    /// para o mesmo ACCTID faria a idempotencia proteger a conta errada.
    /// </summary>
    public async Task<Result> LinkAccountAsync(
        Guid accountId, string externalRef, CancellationToken cancellationToken = default)
    {
        Guid? existing =
            await FindAccountByExternalRefAsync(externalRef, cancellationToken).ConfigureAwait(false);

        if (existing is Guid owner && owner != accountId)
        {
            return new Error(
                "import.account_ref_taken",
                "Esta conta do extrato ja esta vinculada a outra conta do razao.");
        }

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            UPDATE accounts SET external_ref = @ref, updated_at = now()
             WHERE id = @id AND external_ref IS DISTINCT FROM @ref
            """);

        command.Parameters.AddWithValue("id", accountId);
        command.Parameters.AddWithValue("ref", externalRef);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
