using System.Globalization;
using System.Security.Cryptography;

namespace Nemus.Infrastructure.Security;

/// <summary>Resultado da conferencia de senha.</summary>
public enum PasswordCheck
{
    /// <summary>Senha errada, ou hash ilegivel.</summary>
    Failed = 0,

    /// <summary>Senha certa.</summary>
    Valid = 1,

    /// <summary>
    /// Senha certa, mas guardada com custo menor do que o de hoje. Quem chama
    /// deve regravar o hash: e assim que o custo acompanha o hardware sem
    /// pedir que ninguem troque de senha.
    /// </summary>
    ValidNeedsRehash = 2,
}

/// <summary>
/// PBKDF2-HMAC-SHA256, escrito a mao em vez de mais uma dependencia.
///
/// POR QUE NAO SHA-256 DIRETO. Hash comum e rapido de proposito - e o que se
/// quer para conferir arquivo, e exatamente o que nao se quer para senha.
/// Uma GPU testa bilhoes por segundo. PBKDF2 e deliberadamente lento: 210 mil
/// iteracoes por tentativa, o que torna forca bruta cara sem que o login
/// demore para quem sabe a senha.
///
/// POR QUE NAO ARGON2. Seria melhor (resiste tambem a ataque com memoria
/// paralela), mas exige pacote de terceiro. PBKDF2 com este custo e a
/// recomendacao da OWASP para quem fica na biblioteca padrao, e o formato
/// guardado carrega o nome do algoritmo - trocar depois nao invalida senha
/// nenhuma.
///
/// FORMATO: algoritmo$iteracoes$sal$hash, tudo em base64. Auto-descritivo de
/// proposito: o hash antigo continua legivel quando o custo subir.
/// </summary>
public static class PasswordHasher
{
    /// <summary>Recomendacao da OWASP (2023) para PBKDF2-HMAC-SHA256.</summary>
    public const int DefaultIterations = 210_000;

    private const string Algorithm = "pbkdf2-sha256";
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>
    /// Hash de uma senha que nao existe, usado para gastar o mesmo tempo
    /// quando o usuario informado nao existe. Sem isto, o tempo de resposta
    /// diria quais nomes de usuario sao reais.
    /// </summary>
    public static string DecoyHash { get; } = Hash("senha que nunca sera usada por ninguem");

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] hash = Derive(password, salt, iterations);

        return string.Join('$',
            Algorithm,
            iterations.ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(hash));
    }

    /// <summary>
    /// Confere em tempo constante. Hash ilegivel devolve Failed em vez de
    /// lancar: linha corrompida no banco nao pode virar erro 500 que conta ao
    /// atacante que aquele usuario existe.
    /// </summary>
    public static PasswordCheck Verify(string? password, string? encoded)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(encoded))
        {
            return PasswordCheck.Failed;
        }

        string[] parts = encoded.Split('$');
        if (parts.Length != 4
            || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations)
            || iterations < 1)
        {
            return PasswordCheck.Failed;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return PasswordCheck.Failed;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return PasswordCheck.Failed;
        }

        byte[] candidate = Derive(password, salt, iterations, expected.Length);

        if (!CryptographicOperations.FixedTimeEquals(candidate, expected))
        {
            return PasswordCheck.Failed;
        }

        return iterations < DefaultIterations ? PasswordCheck.ValidNeedsRehash : PasswordCheck.Valid;
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int length = HashBytes) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, length);
}
