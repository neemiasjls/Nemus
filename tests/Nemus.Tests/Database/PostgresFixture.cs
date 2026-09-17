using Nemus.Infrastructure.Migrations;
using Npgsql;
using Xunit;

namespace Nemus.Tests.Database;

/// <summary>
/// Banco de verdade, nao substituto em memoria.
///
/// Indice unico parcial, FK composta e CONSTRAINT TRIGGER diferido nao
/// existem em SQLite nem no provedor InMemory. Testar contra um banco que
/// nao tem as garantias das quais o sistema depende e teatro: passa verde e
/// nao prova nada.
///
/// Sem Testcontainers de proposito - ele exige daemon do Docker. A string de
/// conexao vem de NEMUS_TEST_DB e, sem ela, os testes de banco pulam com
/// mensagem explicita em vez de falhar.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string EnvironmentVariable = "NEMUS_TEST_DB";

    private NpgsqlDataSource? _dataSource;

    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(EnvironmentVariable);

    public static bool IsAvailable => !string.IsNullOrWhiteSpace(ConnectionString);

    public NpgsqlDataSource DataSource => _dataSource
        ?? throw new InvalidOperationException(
            $"Banco indisponivel. Defina {EnvironmentVariable}.");

    public async Task InitializeAsync()
    {
        if (!IsAvailable)
        {
            return;
        }

        string connectionString = ConnectionString!;
        GuardAgainstNonTestDatabase(connectionString);

        _dataSource = NpgsqlDataSource.Create(connectionString);

        // Estado limpo a cada execucao da suite.
        var runner = new MigrationRunner(_dataSource);
        await runner.ResetAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A suite comeca derrubando o schema public. Apontar NEMUS_TEST_DB para
    /// o banco errado uma vez seria um estrago dificil de desfazer, entao
    /// exige-se duas coisas independentes.
    ///
    /// A segunda barreira e host local. Com o banco da aplicacao no Supabase,
    /// a string de conexao de producao passa a estar sempre por perto -
    /// copiada num terminal, colada num .env, exportada sem querer. Um teste
    /// que comeca com DROP SCHEMA nao pode ter como alcancar um host remoto,
    /// e nome de banco sozinho nao protege: o banco do Supabase se chama
    /// "postgres", mas um projeto chamado "nemus-test" passaria no primeiro
    /// filtro sem problema nenhum.
    /// </summary>
    private static void GuardAgainstNonTestDatabase(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        string database = builder.Database ?? string.Empty;
        string host = builder.Host ?? string.Empty;

        string[] hostsPermitidos = ["localhost", "127.0.0.1", "::1", "host.docker.internal"];

        if (!hostsPermitidos.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Recusando rodar contra o host \"{host}\": a suite comeca com "
                + "DROP SCHEMA public CASCADE e so pode tocar em banco local. "
                + "Nunca aponte NEMUS_TEST_DB para Supabase ou qualquer host remoto.");
        }

        if (!database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Recusando rodar: o banco \"{database}\" nao parece de teste, e a suite "
                + "comeca com DROP SCHEMA public CASCADE. Use um banco cujo nome contenha \"test\".");
        }
    }

    /// <summary>Limpa os dados entre testes, preservando schema e contas de sistema.</summary>
    public async Task ResetDataAsync()
    {
        await using NpgsqlCommand command = DataSource.CreateCommand("""
            TRUNCATE budget_assignments, entries, transactions, installments, installment_plans,
                     import_batches, payee_aliases, payees, categories
                     RESTART IDENTITY CASCADE;
            DELETE FROM credit_card_terms;
            DELETE FROM accounts WHERE NOT is_system;
            """);

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        if (_dataSource is not null)
        {
            await _dataSource.DisposeAsync().ConfigureAwait(false);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>Fato que pula sozinho quando nao ha banco configurado.</summary>
public sealed class RequiresPostgresFactAttribute : FactAttribute
{
    public RequiresPostgresFactAttribute()
    {
        if (!PostgresFixture.IsAvailable)
        {
            Skip = $"Banco nao configurado. Defina {PostgresFixture.EnvironmentVariable} "
                 + "(ex.: \"Host=localhost;Username=postgres;Password=postgres;Database=nemus_test\").";
        }
    }
}

/// <summary>Teoria que pula sozinha quando nao ha banco configurado.</summary>
public sealed class RequiresPostgresTheoryAttribute : TheoryAttribute
{
    public RequiresPostgresTheoryAttribute()
    {
        if (!PostgresFixture.IsAvailable)
        {
            Skip = $"Banco nao configurado. Defina {PostgresFixture.EnvironmentVariable}.";
        }
    }
}
