using Nemus.Domain.Identity;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Migrations;
using Nemus.Infrastructure.Persistence;
using Nemus.Infrastructure.Security;
using Npgsql;

namespace Nemus.MigrationTool;

/// <summary>
/// Aplica as migrations em qualquer Postgres - Supabase, um servidor local,
/// o que for. Le a string de conexao de NEMUS_DB.
///
/// No Supabase use a conexao DIRETA (porta 5432) ou o pooler em modo sessao.
/// O pooler em modo transacao (6543) nao aguenta o DDL destas migrations.
/// </summary>
internal static class Program
{
    private const string ConnectionVariable = "NEMUS_DB";

    private static async Task<int> Main(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "apply";

        string? connectionString = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Console.Error.WriteLine($"Defina {ConnectionVariable} com a string de conexao.");
            Console.Error.WriteLine(
                "Local:  Host=localhost;Port=5433;Database=nemus;Username=postgres;Password=...");
            Console.Error.WriteLine(
                "Remoto: Host=...;Port=5432;Database=nemus;Username=...;Password=...;SSL Mode=VerifyFull");
            Console.Error.WriteLine();
            Console.Error.WriteLine(
                "Em host remoto use SSL Mode=VerifyFull. Nunca "
                + "Trust Server Certificate=true: isso cifra o trafego mas para de validar "
                + "o certificado, o que deixa a conexao aberta a man-in-the-middle - "
                + "e a senha do banco vai nela.");
            return 2;
        }

        await using NpgsqlDataSource dataSource = NpgsqlDataSource.Create(connectionString);
        var runner = new MigrationRunner(dataSource);

        try
        {
            switch (command)
            {
                case "apply":
                    return await ApplyAsync(runner, connectionString);

                case "status":
                    return await StatusAsync(runner);

                // Caminho de recuperacao: nao ha e-mail neste app, entao a
                // senha perdida se resolve aqui, com quem tem acesso ao banco.
                case "user":
                    return await UserAsync(dataSource, args);

                default:
                    Console.Error.WriteLine(
                        $"Comando desconhecido: {command}. Use apply, status ou user.");
                    return 2;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or PostgresException or NpgsqlException)
        {
            Console.Error.WriteLine($"Falhou: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> ApplyAsync(MigrationRunner runner, string connectionString)
    {
        Console.WriteLine($"Alvo: {Describe(connectionString)}");

        IReadOnlyList<string> applied = await runner.ApplyAsync();

        if (applied.Count == 0)
        {
            Console.WriteLine("Nada a aplicar; o banco ja esta em dia.");
            return 0;
        }

        foreach (string name in applied)
        {
            Console.WriteLine($"  aplicada  {name}");
        }

        Console.WriteLine($"{applied.Count} migration(s) aplicada(s).");
        return 0;
    }

    private static async Task<int> StatusAsync(MigrationRunner runner)
    {
        IReadOnlyList<string> available = runner.DiscoverMigrations();
        IReadOnlyList<AppliedMigration> applied = await runner.GetAppliedAsync();
        var appliedByName = applied.ToDictionary(m => m.Name, StringComparer.Ordinal);

        foreach (string name in available)
        {
            string status = appliedByName.ContainsKey(name) ? "ok      " : "PENDENTE";
            Console.WriteLine($"  {status}  {name}");
        }

        int pending = available.Count(n => !appliedByName.ContainsKey(n));
        Console.WriteLine($"{applied.Count} aplicada(s), {pending} pendente(s).");
        return pending == 0 ? 0 : 1;
    }

    /// <summary>
    /// user list | user add NOME | user password NOME
    ///
    /// A senha e digitada na hora e nunca aparece em argumento de linha de
    /// comando: argumento fica no historico do shell e na lista de processos,
    /// onde qualquer outro usuario da maquina le.
    /// </summary>
    private static async Task<int> UserAsync(NpgsqlDataSource dataSource, string[] args)
    {
        string action = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
        var users = new UserRepository(dataSource);

        if (action == "list")
        {
            int count = await users.CountAsync();
            Console.WriteLine(count == 0
                ? "Nenhum usuario. Crie o primeiro na tela de entrada do app, ou com: user add NOME"
                : $"{count} usuario(s) cadastrado(s).");
            return 0;
        }

        if (args.Length < 3)
        {
            Console.Error.WriteLine("Informe o nome: user add NOME | user password NOME");
            return 2;
        }

        Result<string> username = User.NormalizeUsername(args[2]);
        if (username.IsFailure)
        {
            Console.Error.WriteLine(username.Error.Message);
            return 2;
        }

        User? existing = await users.FindByUsernameAsync(username.Value);

        switch (action)
        {
            case "add" when existing is not null:
                Console.Error.WriteLine($"O usuario \"{username.Value}\" ja existe.");
                return 1;

            case "password" when existing is null:
                Console.Error.WriteLine($"Nao existe usuario \"{username.Value}\".");
                return 1;

            case "add":
            case "password":
                break;

            default:
                Console.Error.WriteLine($"Comando desconhecido: user {action}.");
                return 2;
        }

        string? password = ReadPassword($"Senha de {username.Value}: ");
        if (password is null)
        {
            return 2;
        }

        Result policy = User.ValidatePassword(password, username.Value);
        if (policy.IsFailure)
        {
            Console.Error.WriteLine(policy.Error.Message);
            return 2;
        }

        if (ReadPassword("Repita: ") != password)
        {
            Console.Error.WriteLine("As duas senhas nao sao iguais.");
            return 2;
        }

        string hash = PasswordHasher.Hash(password);

        if (existing is null)
        {
            Result<User> created = User.Create(username.Value, hash);
            if (created.IsFailure)
            {
                Console.Error.WriteLine(created.Error.Message);
                return 2;
            }

            await users.AddAsync(created.Value);
            Console.WriteLine($"Usuario \"{username.Value}\" criado.");
            return 0;
        }

        existing.ChangePassword(hash);
        await users.SavePasswordAsync(existing);

        // Trocar a senha sem derrubar o que estava aberto nao expulsa ninguem.
        int revoked = await new SessionRepository(dataSource)
            .RevokeAllForUserAsync(existing.Id, DateTimeOffset.UtcNow);

        Console.WriteLine(
            $"Senha de \"{username.Value}\" trocada. {revoked} sessao(oes) encerrada(s).");
        return 0;
    }

    /// <summary>Le sem ecoar: a senha nao aparece na tela nem fica no scrollback.</summary>
    private static string? ReadPassword(string prompt)
    {
        Console.Write(prompt);

        if (Console.IsInputRedirected)
        {
            Console.WriteLine();
            Console.Error.WriteLine(
                "Entrada redirecionada: a senha seria lida de um arquivo ou cano, "
                + "e e justamente onde ela nao pode passar. Rode no terminal.");
            return null;
        }

        var typed = new System.Text.StringBuilder();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
    }

    /// <summary>Host e banco, sem a senha.</summary>
    private static string Describe(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return $"{builder.Host}:{builder.Port}/{builder.Database}";
    }
}
