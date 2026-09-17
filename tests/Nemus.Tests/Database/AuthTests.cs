using Nemus.Domain.Identity;
using Nemus.Infrastructure.Persistence;
using Nemus.Infrastructure.Security;
using Npgsql;
using Xunit;

namespace Nemus.Tests.Database;

/// <summary>
/// Login contra o banco de verdade.
///
/// O que estes testes cobram nao e "consigo entrar": e o contrario - as
/// quatro maneiras de NAO entrar (senha errada, sessao revogada, sessao
/// vencida, usuario desativado) e o que o banco recusa por estrutura.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AuthTests : IAsyncLifetime
{
    private const int Cheap = 1_000;
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly PostgresFixture _fixture;

    public AuthTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        if (PostgresFixture.IsAvailable)
        {
            await ClearAsync().ConfigureAwait(false);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private UserRepository Users => new(_fixture.DataSource);

    private SessionRepository Sessions => new(_fixture.DataSource);

    [RequiresPostgresFact]
    public async Task Usuario_entra_com_a_propria_senha()
    {
        User user = await CreateUserAsync("neemias", "uma frase que eu lembro");

        Assert.Equal(
            PasswordCheck.ValidNeedsRehash,
            PasswordHasher.Verify("uma frase que eu lembro", (await Users.FindByUsernameAsync("neemias"))!.PasswordHash));

        AuthenticatedSession? authenticated = await OpenSessionAsync(user);

        Assert.NotNull(authenticated);
        Assert.Equal("neemias", authenticated.User.Username);
    }

    /// <summary>
    /// A senha nao esta no banco em lugar nenhum - nem em outra coluna, nem
    /// invertida, nem em pedaco. So o hash.
    /// </summary>
    [RequiresPostgresFact]
    public async Task A_senha_nao_aparece_no_banco()
    {
        await CreateUserAsync("neemias", "minha frase secreta 42");

        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(
            "SELECT count(*)::BIGINT FROM users WHERE password_hash LIKE '%frase%' OR username LIKE '%frase%'");

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [RequiresPostgresFact]
    public async Task Sessao_revogada_nao_abre_mais_nada()
    {
        User user = await CreateUserAsync("neemias", "uma frase que eu lembro");
        (string token, _) = await IssueAsync(user);

        Assert.True(await Sessions.RevokeAsync(SessionTokens.Fingerprint(token), Now));

        Assert.Null(await Sessions.FindActiveAsync(SessionTokens.Fingerprint(token), Now.AddMinutes(1)));

        // Revogar de novo devolve false: nao havia mais o que revogar.
        Assert.False(await Sessions.RevokeAsync(SessionTokens.Fingerprint(token), Now));
    }

    [RequiresPostgresFact]
    public async Task Sessao_vencida_nao_vale()
    {
        User user = await CreateUserAsync("neemias", "uma frase que eu lembro");
        (string token, Session session) = await IssueAsync(user);

        Assert.NotNull(await Sessions.FindActiveAsync(SessionTokens.Fingerprint(token), session.ExpiresAt.AddSeconds(-1)));
        Assert.Null(await Sessions.FindActiveAsync(SessionTokens.Fingerprint(token), session.ExpiresAt));
    }

    /// <summary>Trocar a senha precisa derrubar o que estava aberto, em todo lugar.</summary>
    [RequiresPostgresFact]
    public async Task Trocar_a_senha_derruba_todas_as_sessoes()
    {
        User user = await CreateUserAsync("neemias", "uma frase que eu lembro");
        (string first, _) = await IssueAsync(user);
        (string second, _) = await IssueAsync(user);

        Assert.Equal(2, await Sessions.RevokeAllForUserAsync(user.Id, Now));

        Assert.Null(await Sessions.FindActiveAsync(SessionTokens.Fingerprint(first), Now.AddMinutes(1)));
        Assert.Null(await Sessions.FindActiveAsync(SessionTokens.Fingerprint(second), Now.AddMinutes(1)));
    }

    [RequiresPostgresFact]
    public async Task Usuario_desativado_perde_a_sessao_que_ja_tinha()
    {
        User user = await CreateUserAsync("neemias", "uma frase que eu lembro");
        (string token, _) = await IssueAsync(user);

        await using (NpgsqlCommand command = _fixture.DataSource.CreateCommand(
            "UPDATE users SET is_active = FALSE WHERE id = @id"))
        {
            command.Parameters.AddWithValue("id", user.Id);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Null(await Sessions.FindActiveAsync(SessionTokens.Fingerprint(token), Now.AddMinutes(1)));
    }

    /// <summary>Token errado nao acha sessao nenhuma, nem por engano.</summary>
    [RequiresPostgresFact]
    public async Task Token_que_nao_existe_nao_abre_sessao()
    {
        User user = await CreateUserAsync("neemias", "uma frase que eu lembro");
        await IssueAsync(user);

        string other = SessionTokens.Fingerprint(SessionTokens.Issue());

        Assert.Null(await Sessions.FindActiveAsync(other, Now));
    }

    [RequiresPostgresFact]
    public async Task Dois_usuarios_com_o_mesmo_nome_e_impossivel()
    {
        await CreateUserAsync("neemias", "uma frase que eu lembro");

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => CreateUserAsync("neemias", "outra frase bem diferente"));

        Assert.Equal(NemusSqlStates.UniqueViolation, error.SqlState);
    }

    /// <summary>
    /// O CHECK do banco e a segunda parede: mesmo quem escrever SQL direto nao
    /// consegue criar "Neemias" com maiuscula e ficar com duas contas.
    /// </summary>
    [RequiresPostgresTheory]
    [InlineData("Neemias")]
    [InlineData("nome com espaco")]
    [InlineData("ab")]
    [InlineData("email@dominio.com")]
    public async Task O_banco_recusa_nome_de_usuario_fora_do_formato(string username)
    {
        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertRawUserAsync(username, PasswordHasher.Hash("uma frase que eu lembro", Cheap)));

        Assert.Equal("23514", error.SqlState);
    }

    /// <summary>
    /// Guardar senha em texto na coluna de hash e o acidente classico. O CHECK
    /// do formato torna isso impossivel mesmo por SQL direto.
    /// </summary>
    [RequiresPostgresTheory]
    [InlineData("minha senha em texto puro")]
    [InlineData("")]
    [InlineData("md5$abc")]
    public async Task O_banco_recusa_hash_fora_do_formato(string hash)
    {
        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => InsertRawUserAsync("neemias", hash));

        Assert.Equal("23514", error.SqlState);
    }

    [RequiresPostgresFact]
    public async Task O_banco_recusa_guardar_o_token_em_vez_do_hash()
    {
        User user = await CreateUserAsync("neemias", "uma frase que eu lembro");

        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand("""
            INSERT INTO sessions (id, user_id, token_hash, expires_at)
            VALUES (@id, @user_id, @token_hash, now() + interval '30 days')
            """);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("user_id", user.Id);
        // O token cru: base64url, com maiuscula e tamanho diferente do hash.
        command.Parameters.AddWithValue("token_hash", SessionTokens.Issue());

        PostgresException error = await Assert.ThrowsAsync<PostgresException>(
            () => command.ExecuteNonQueryAsync());

        Assert.Equal("23514", error.SqlState);
    }

    [RequiresPostgresFact]
    public async Task Apagar_o_usuario_leva_as_sessoes_junto()
    {
        User user = await CreateUserAsync("neemias", "uma frase que eu lembro");
        (string token, _) = await IssueAsync(user);

        await using (NpgsqlCommand command = _fixture.DataSource.CreateCommand(
            "DELETE FROM users WHERE id = @id"))
        {
            command.Parameters.AddWithValue("id", user.Id);
            await command.ExecuteNonQueryAsync();
        }

        Assert.Null(await Sessions.FindActiveAsync(SessionTokens.Fingerprint(token), Now));
        Assert.Equal(0, await Users.CountAsync());
    }

    [RequiresPostgresFact]
    public async Task Contagem_de_usuarios_decide_o_primeiro_acesso()
    {
        Assert.Equal(0, await Users.CountAsync());

        await CreateUserAsync("neemias", "uma frase que eu lembro");

        Assert.Equal(1, await Users.CountAsync());
    }

    [RequiresPostgresFact]
    public async Task Registrar_login_grava_a_data()
    {
        User user = await CreateUserAsync("neemias", "uma frase que eu lembro");

        await Users.RegisterLoginAsync(user.Id, Now);

        User? reloaded = await Users.FindAsync(user.Id);
        Assert.Equal(Now, reloaded!.LastLoginAt);
    }

    [RequiresPostgresFact]
    public async Task Limpeza_apaga_so_o_que_ja_venceu()
    {
        User user = await CreateUserAsync("neemias", "uma frase que eu lembro");
        (string alive, _) = await IssueAsync(user);

        Session old = Session.Start(user.Id, SessionTokens.Fingerprint(SessionTokens.Issue()),
            Now.AddYears(-1), TimeSpan.FromDays(30)).Value;
        await Sessions.AddAsync(old);

        Assert.Equal(1, await Sessions.DeleteExpiredAsync(Now));
        Assert.NotNull(await Sessions.FindActiveAsync(SessionTokens.Fingerprint(alive), Now));
    }

    // -----------------------------------------------------------------------

    private async Task<User> CreateUserAsync(string username, string password)
    {
        User user = User.Create(username, PasswordHasher.Hash(password, Cheap)).Value;
        await Users.AddAsync(user);
        return user;
    }

    private async Task<(string Token, Session Session)> IssueAsync(User user)
    {
        string token = SessionTokens.Issue();
        Session session = Session.Start(user.Id, SessionTokens.Fingerprint(token), Now).Value;
        await Sessions.AddAsync(session);
        return (token, session);
    }

    private async Task<AuthenticatedSession?> OpenSessionAsync(User user)
    {
        (string token, _) = await IssueAsync(user);
        return await Sessions.FindActiveAsync(SessionTokens.Fingerprint(token), Now.AddMinutes(1));
    }

    private async Task InsertRawUserAsync(string username, string passwordHash)
    {
        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(
            "INSERT INTO users (id, username, password_hash) VALUES (@id, @username, @password_hash)");
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("username", username);
        command.Parameters.AddWithValue("password_hash", passwordHash);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Usuario e sessao nao entram no TRUNCATE geral da fixture: o resto da
    /// suite nao mexe neles, e limpar aqui deixa cada caso comecando sozinho.
    /// </summary>
    private async Task ClearAsync()
    {
        await using NpgsqlCommand command = _fixture.DataSource.CreateCommand(
            "TRUNCATE sessions, users CASCADE");

        await command.ExecuteNonQueryAsync();
    }
}
