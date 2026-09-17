using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Nemus.Infrastructure.Migrations;

public sealed record AppliedMigration(string Name, string Sha256, DateTimeOffset AppliedAt);

/// <summary>
/// Runner proprio, ~100 linhas, em vez de mais uma dependencia. Aplica os
/// arquivos .sql embutidos em ordem de nome, cada um dentro da sua propria
/// transacao, e registra o hash do que aplicou.
///
/// Guardar o hash importa: se alguem editar uma migration ja aplicada, a
/// proxima execucao acusa em vez de deixar dois bancos divergirem em
/// silencio.
/// </summary>
public sealed class MigrationRunner
{
    private const string HistoryTable = "__nemus_migrations";
    private const string ResourcePrefix = "Nemus.Migrations.";

    private readonly NpgsqlDataSource _dataSource;
    private readonly Assembly _assembly;

    public MigrationRunner(NpgsqlDataSource dataSource, Assembly? assembly = null)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _assembly = assembly ?? typeof(MigrationRunner).Assembly;
    }

    /// <summary>Nomes das migrations embutidas, em ordem de aplicacao.</summary>
    public IReadOnlyList<string> DiscoverMigrations() =>
        _assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                        && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .Select(name => name[ResourcePrefix.Length..])
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    public async Task<IReadOnlyList<string>> ApplyAsync(CancellationToken cancellationToken = default)
    {
        await EnsureHistoryTableAsync(cancellationToken).ConfigureAwait(false);

        Dictionary<string, AppliedMigration> applied =
            (await GetAppliedAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(migration => migration.Name, StringComparer.Ordinal);

        var newlyApplied = new List<string>();

        foreach (string name in DiscoverMigrations())
        {
            string sql = ReadResource(name);
            string hash = ComputeSha256(sql);

            if (applied.TryGetValue(name, out AppliedMigration? previous))
            {
                if (!string.Equals(previous.Sha256, hash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"A migration \"{name}\" ja foi aplicada com outro conteudo "
                        + $"(esperado {previous.Sha256[..12]}, encontrado {hash[..12]}). "
                        + "Editar migration aplicada faz bancos divergirem: crie uma nova.");
                }

                continue;
            }

            await ApplyOneAsync(name, sql, hash, cancellationToken).ConfigureAwait(false);
            newlyApplied.Add(name);
        }

        return newlyApplied;
    }

    private async Task ApplyOneAsync(
        string name, string sql, string hash, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using (NpgsqlCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (NpgsqlCommand record = connection.CreateCommand())
            {
                record.Transaction = transaction;
                record.CommandText =
                    $"INSERT INTO {HistoryTable} (name, sha256) VALUES (@name, @sha256)";
                record.Parameters.AddWithValue("name", name);
                record.Parameters.AddWithValue("sha256", hash);
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Migration \"{name}\" falhou: {ex.MessageText} (SQLSTATE {ex.SqlState}).", ex);
        }
    }

    private async Task EnsureHistoryTableAsync(CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"""
             CREATE TABLE IF NOT EXISTS {HistoryTable} (
                 name       TEXT        PRIMARY KEY,
                 sha256     TEXT        NOT NULL,
                 applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
             )
             """);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AppliedMigration>> GetAppliedAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureHistoryTableAsync(cancellationToken).ConfigureAwait(false);

        var result = new List<AppliedMigration>();

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"SELECT name, sha256, applied_at FROM {HistoryTable} ORDER BY name");
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new AppliedMigration(
                reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2)));
        }

        return result;
    }

    /// <summary>
    /// Apaga tudo e reaplica do zero. Existe para teste; nunca chamar em
    /// banco que tem dado de verdade.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await using (NpgsqlCommand drop = _dataSource.CreateCommand(
            "DROP SCHEMA public CASCADE; CREATE SCHEMA public;"))
        {
            await drop.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ApplyAsync(cancellationToken).ConfigureAwait(false);
    }

    private string ReadResource(string name)
    {
        using Stream? stream = _assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException($"Migration \"{name}\" nao encontrada no assembly.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string ComputeSha256(string content)
    {
        // Normaliza fim de linha: clonar o repositorio no Windows nao pode
        // invalidar migration aplicada no Linux.
        string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
