using System.Security.Cryptography;
using System.Text;

namespace Nemus.Infrastructure.Security;

/// <summary>
/// O token que o navegador guarda depois do login.
///
/// 256 bits de aleatorio do gerador criptografico - nao e derivado do
/// usuario, da senha nem do relogio, entao nao ha o que adivinhar.
///
/// No banco fica so o SHA-256 dele, sem sal e sem custo: o token ja e
/// aleatorio de 256 bits, e nao uma senha adivinhavel. O que se quer aqui e
/// busca por indice; resistencia a dicionario nao faz sentido quando nao ha
/// dicionario possivel.
/// </summary>
public static class SessionTokens
{
    private const int TokenBytes = 32;

    /// <summary>Token novo, em base64url (sem +, / ou = para viajar em cabecalho sem escape).</summary>
    public static string Issue() => Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>O que vai para a coluna token_hash: SHA-256 em hexadecimal minusculo.</summary>
    public static string Fingerprint(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
