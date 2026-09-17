using Nemus.Api.Security;
using Nemus.Infrastructure.Security;
using Xunit;

namespace Nemus.Tests.Security;

public sealed class PasswordHasherTests
{
    // Custo baixo so nos testes: o que se prova aqui e o comportamento, e
    // 210 mil iteracoes por caso deixariam a suite lenta sem provar mais nada.
    private const int Cheap = 1_000;

    [Fact]
    public void O_hash_nao_contem_a_senha()
    {
        string hash = PasswordHasher.Hash("frase longa de teste", Cheap);

        Assert.DoesNotContain("frase", hash, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("pbkdf2-sha256$1000$", hash, StringComparison.Ordinal);
        Assert.Equal(4, hash.Split('$').Length);
    }

    /// <summary>
    /// Sal por senha. Sem ele, duas pessoas com a mesma senha teriam o mesmo
    /// hash - e quebrar um hash quebraria todos de uma vez.
    /// </summary>
    [Fact]
    public void A_mesma_senha_gera_hashes_diferentes()
    {
        string first = PasswordHasher.Hash("a mesma senha de sempre", Cheap);
        string second = PasswordHasher.Hash("a mesma senha de sempre", Cheap);

        // Custo baixo, entao o resultado esperado e "vale, mas regrave" - o
        // que importa aqui e que os dois hashes conferem a mesma senha.
        Assert.NotEqual(first, second);
        Assert.Equal(PasswordCheck.ValidNeedsRehash, PasswordHasher.Verify("a mesma senha de sempre", first));
        Assert.Equal(PasswordCheck.ValidNeedsRehash, PasswordHasher.Verify("a mesma senha de sempre", second));
    }

    [Theory]
    [InlineData("senha quase certa")]
    [InlineData("Senha correta e boa")]
    [InlineData("")]
    [InlineData(null)]
    public void Senha_errada_nao_passa(string? attempt)
    {
        string hash = PasswordHasher.Hash("senha correta e boa", Cheap);

        Assert.Equal(PasswordCheck.Failed, PasswordHasher.Verify(attempt, hash));
    }

    /// <summary>
    /// Hash corrompido ou de formato desconhecido devolve Failed em vez de
    /// lancar: excecao aqui viraria 500, e 500 so num usuario existente conta
    /// ao atacante que aquele usuario existe.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("nao e hash")]
    [InlineData("pbkdf2-sha256$1000$sal-invalido$hash")]
    [InlineData("pbkdf2-sha256$0$c2FsdA==$aGFzaA==")]
    [InlineData("argon2id$1000$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$1000$c2FsdA==")]
    public void Hash_ilegivel_nao_lanca(string encoded)
    {
        Assert.Equal(PasswordCheck.Failed, PasswordHasher.Verify("qualquer senha", encoded));
    }

    /// <summary>
    /// Custo antigo continua valendo, mas pede regravacao - e assim que o
    /// hash acompanha o hardware sem obrigar ninguem a trocar de senha.
    /// </summary>
    [Fact]
    public void Custo_antigo_pede_regravacao()
    {
        string old = PasswordHasher.Hash("uma senha bem comprida", Cheap);

        Assert.Equal(PasswordCheck.ValidNeedsRehash, PasswordHasher.Verify("uma senha bem comprida", old));
    }

    [Fact]
    public void Custo_atual_nao_pede_regravacao()
    {
        string current = PasswordHasher.Hash("uma senha bem comprida");

        Assert.Equal(PasswordCheck.Valid, PasswordHasher.Verify("uma senha bem comprida", current));
    }

    /// <summary>O hash de mentira existe para gastar tempo; nenhuma senha bate nele.</summary>
    [Fact]
    public void O_hash_de_mentira_nunca_confere()
    {
        Assert.Equal(PasswordCheck.Failed, PasswordHasher.Verify("senha", PasswordHasher.DecoyHash));
        Assert.Equal(PasswordCheck.Failed, PasswordHasher.Verify("", PasswordHasher.DecoyHash));
    }
}

public sealed class SessionTokenTests
{
    [Fact]
    public void Cada_token_e_diferente()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < 500; i++)
        {
            Assert.True(seen.Add(SessionTokens.Issue()), "token repetido");
        }
    }

    /// <summary>Base64url: viaja em cabecalho HTTP sem escape.</summary>
    [Fact]
    public void O_token_nao_tem_caractere_que_precise_de_escape()
    {
        string token = SessionTokens.Issue();

        Assert.DoesNotContain('+', token);
        Assert.DoesNotContain('/', token);
        Assert.DoesNotContain('=', token);
        Assert.True(token.Length >= 42, $"token curto demais: {token.Length} caracteres");
    }

    [Fact]
    public void A_digital_e_estavel_e_so_hexadecimal()
    {
        string token = SessionTokens.Issue();
        string fingerprint = SessionTokens.Fingerprint(token);

        Assert.Equal(fingerprint, SessionTokens.Fingerprint(token));
        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, c => Assert.True(char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f')));
        Assert.NotEqual(fingerprint, SessionTokens.Fingerprint(SessionTokens.Issue()));
    }
}

public sealed class LoginThrottleTests
{
    [Fact]
    public void Deixa_tentar_ate_o_limite_e_entao_barra()
    {
        var throttle = new LoginThrottle();

        for (int attempt = 0; attempt < LoginThrottle.MaxFailures; attempt++)
        {
            Assert.Null(throttle.RetryAfter("neemias|127.0.0.1"));
            throttle.RegisterFailure("neemias|127.0.0.1");
        }

        Assert.NotNull(throttle.RetryAfter("neemias|127.0.0.1"));
    }

    /// <summary>O freio e por usuario e IP: um bloqueado nao bloqueia o outro.</summary>
    [Fact]
    public void Barrar_um_nao_barra_os_outros()
    {
        var throttle = new LoginThrottle();

        for (int attempt = 0; attempt <= LoginThrottle.MaxFailures; attempt++)
        {
            throttle.RegisterFailure("neemias|127.0.0.1");
        }

        Assert.NotNull(throttle.RetryAfter("neemias|127.0.0.1"));
        Assert.Null(throttle.RetryAfter("neemias|10.0.0.9"));
        Assert.Null(throttle.RetryAfter("outro|127.0.0.1"));
    }

    [Fact]
    public void Acertar_a_senha_zera_a_contagem()
    {
        var throttle = new LoginThrottle();

        for (int attempt = 0; attempt <= LoginThrottle.MaxFailures; attempt++)
        {
            throttle.RegisterFailure("neemias|127.0.0.1");
        }

        throttle.Clear("neemias|127.0.0.1");

        Assert.Null(throttle.RetryAfter("neemias|127.0.0.1"));
    }

    [Fact]
    public void A_janela_expira_sozinha()
    {
        DateTimeOffset now = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);
        var throttle = new LoginThrottle(() => now);

        for (int attempt = 0; attempt <= LoginThrottle.MaxFailures; attempt++)
        {
            throttle.RegisterFailure("neemias|127.0.0.1");
        }

        Assert.NotNull(throttle.RetryAfter("neemias|127.0.0.1"));

        now += LoginThrottle.Window + TimeSpan.FromSeconds(1);

        Assert.Null(throttle.RetryAfter("neemias|127.0.0.1"));
    }

    /// <summary>Quanto falta e uma resposta util: a tela diz quanto esperar.</summary>
    [Fact]
    public void Diz_quanto_falta_para_liberar()
    {
        DateTimeOffset now = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);
        var throttle = new LoginThrottle(() => now);

        for (int attempt = 0; attempt <= LoginThrottle.MaxFailures; attempt++)
        {
            throttle.RegisterFailure("neemias|127.0.0.1");
        }

        now += TimeSpan.FromMinutes(5);
        TimeSpan? left = throttle.RetryAfter("neemias|127.0.0.1");

        Assert.NotNull(left);
        Assert.InRange(left.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));
    }
}
