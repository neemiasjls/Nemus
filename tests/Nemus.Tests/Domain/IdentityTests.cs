using Nemus.Domain.Identity;
using Nemus.Domain.Primitives;
using Xunit;

namespace Nemus.Tests.Domain;

public sealed class UserTests
{
    [Theory]
    [InlineData("neemias", "neemias")]
    [InlineData("  Neemias  ", "neemias")]
    [InlineData("NEEMIAS", "neemias")]
    [InlineData("maria.silva", "maria.silva")]
    [InlineData("joao_2026", "joao_2026")]
    public void Nome_de_usuario_vira_minusculo_e_sem_espaco(string typed, string expected)
    {
        Assert.Equal(expected, User.NormalizeUsername(typed).Value);
    }

    /// <summary>
    /// Sem esta regra "Neemias" e "neemias" seriam duas contas - e a que voce
    /// nao criou e a que outra pessoa registra.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("nome com espaco")]
    [InlineData("acentuação")]
    [InlineData("email@dominio.com")]
    [InlineData("barra/invertida")]
    public void Nome_de_usuario_invalido_e_recusado(string typed)
    {
        Result<string> result = User.NormalizeUsername(typed);

        Assert.True(result.IsFailure, $"\"{typed}\" deveria ser recusado.");
        Assert.Equal("auth.username_invalid", result.Error.Code);
    }

    [Fact]
    public void Nome_de_usuario_de_quarenta_e_um_caracteres_e_recusado()
    {
        Assert.True(User.NormalizeUsername(new string('a', 41)).IsFailure);
        Assert.True(User.NormalizeUsername(new string('a', 40)).IsSuccess);
    }

    [Theory]
    [InlineData("uma frase que eu lembro")]
    [InlineData("cafe com leite e pao")]
    [InlineData("chuva de maio 88")]
    public void Senha_boa_passa(string password)
    {
        Assert.True(User.ValidatePassword(password, "neemias").IsSuccess);
    }

    [Theory]
    [InlineData(null, "auth.password_required")]
    [InlineData("", "auth.password_required")]
    [InlineData("curta123", "auth.password_too_short")]
    [InlineData("aaaaaaaaaaaa", "auth.password_too_common")]
    [InlineData("1234567890", "auth.password_too_common")]
    [InlineData("senha123", "auth.password_too_short")]
    [InlineData("neemias12345", "auth.password_contains_username")]
    // Começa com sequência conhecida: o "a" no fim não salva.
    [InlineData("0123456789a", "auth.password_too_common")]
    public void Senha_ruim_e_recusada_com_motivo(string? password, string expectedCode)
    {
        Result result = User.ValidatePassword(password, "neemias");

        Assert.True(result.IsFailure, $"\"{password}\" deveria ser recusada.");
        Assert.Equal(expectedCode, result.Error.Code);
    }

    /// <summary>
    /// Teto por seguranca do servidor: PBKDF2 processa a entrada inteira, e
    /// senha gigante seria trabalho gratuito para derrubar a API.
    /// </summary>
    [Fact]
    public void Senha_gigante_e_recusada()
    {
        Result result = User.ValidatePassword(new string('x', User.MaxPasswordLength + 1));

        Assert.Equal("auth.password_too_long", result.Error.Code);
    }

    [Fact]
    public void Usuario_nasce_ativo_e_sem_login_registrado()
    {
        User user = User.Create("neemias", "pbkdf2-sha256$1000$c2FsdA==$aGFzaA==").Value;

        Assert.True(user.IsActive);
        Assert.Null(user.LastLoginAt);
        Assert.Equal("neemias", user.Username);
    }

    [Fact]
    public void Trocar_a_senha_atualiza_o_hash_e_a_data()
    {
        DateTimeOffset creation = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        User user = User.Create("neemias", "pbkdf2-sha256$1000$c2FsdA==$aGFzaA==", createdAt: creation).Value;

        DateTimeOffset later = creation.AddDays(30);
        user.ChangePassword("pbkdf2-sha256$2000$b3V0cm8=$b3V0cm8=", later);

        Assert.Equal("pbkdf2-sha256$2000$b3V0cm8=$b3V0cm8=", user.PasswordHash);
        Assert.Equal(later, user.UpdatedAt);
    }
}

public sealed class SessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private const string Fingerprint = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";

    private static Session Start() =>
        Session.Start(Guid.NewGuid(), Fingerprint, Now).Value;

    [Fact]
    public void Sessao_nova_vale_agora_e_vence_no_prazo()
    {
        Session session = Start();

        Assert.True(session.IsUsableAt(Now));
        Assert.True(session.IsUsableAt(Now + Session.DefaultLifetime - TimeSpan.FromMinutes(1)));
        Assert.False(session.IsUsableAt(Now + Session.DefaultLifetime));
    }

    [Fact]
    public void Revogar_derruba_na_hora()
    {
        Session session = Start();
        session.Revoke(Now.AddMinutes(1));

        Assert.False(session.IsUsableAt(Now.AddMinutes(2)));
    }

    /// <summary>Revogar duas vezes nao reescreve quando foi: o primeiro momento e o que vale.</summary>
    [Fact]
    public void Revogar_de_novo_nao_muda_a_data()
    {
        Session session = Start();
        session.Revoke(Now.AddMinutes(1));
        session.Revoke(Now.AddHours(5));

        Assert.Equal(Now.AddMinutes(1), session.RevokedAt);
    }

    [Fact]
    public void O_toque_nao_anda_para_tras()
    {
        Session session = Start();

        session.Touch(Now.AddHours(3));
        session.Touch(Now.AddHours(1));

        Assert.Equal(Now.AddHours(3), session.LastSeenAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Sessao_sem_token_e_recusada(string tokenHash)
    {
        Assert.Equal("auth.session_token_required", Session.Start(Guid.NewGuid(), tokenHash, Now).Error.Code);
    }

    [Fact]
    public void Sessao_sem_usuario_e_recusada()
    {
        Assert.Equal("auth.session_user_required", Session.Start(Guid.Empty, Fingerprint, Now).Error.Code);
    }

    [Fact]
    public void Duracao_nao_positiva_e_recusada()
    {
        Result<Session> result = Session.Start(Guid.NewGuid(), Fingerprint, Now, TimeSpan.Zero);

        Assert.Equal("auth.session_lifetime_invalid", result.Error.Code);
    }
}
