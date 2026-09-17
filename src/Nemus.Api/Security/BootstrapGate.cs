using System.Security.Cryptography;
using System.Text;

namespace Nemus.Api.Security;

/// <summary>
/// O token de INSTALACAO.
///
/// Ate a fase 4 este token abria a API inteira: quem o tivesse lia e escrevia
/// o razao. Com usuario e senha (migration 011) ele perdeu esse poder e ficou
/// com uma funcao so - autorizar a criacao do PRIMEIRO acesso, enquanto nao
/// existe nenhum usuario.
///
/// Por que ainda existe: sem ele, quem descobrisse a URL da API recem-subida
/// antes do dono viraria o dono. Com ele, criar o primeiro acesso exige algo
/// que so quem configurou o servidor tem.
///
/// Depois que o primeiro usuario existe, o endpoint que usa este token passa
/// a responder 409 para sempre - o token deixa de abrir qualquer coisa.
/// </summary>
public sealed class BootstrapGate
{
    public const string EnvironmentVariable = "NEMUS_BOOTSTRAP_TOKEN";

    /// <summary>
    /// Token curto demais e forca bruta viavel. 32 caracteres com entropia
    /// decente e o piso; a mensagem de erro ensina como gerar um.
    /// </summary>
    public const int MinimumLength = 32;

    private readonly byte[] _expected;

    private BootstrapGate(byte[] expected) => _expected = expected;

    /// <summary>
    /// Le o token do ambiente. Falha ruidosamente na subida em vez de deixar
    /// a API no ar sem como criar o primeiro acesso - ou, pior, com um token
    /// vazio que qualquer um adivinha.
    /// </summary>
    public static BootstrapGate FromEnvironment()
    {
        string? token = Environment.GetEnvironmentVariable(EnvironmentVariable);

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} nao esta definida. A API nao sobe sem ela: "
                + "e o que autoriza criar o primeiro usuario. "
                + "Gere uma com: openssl rand -base64 48");
        }

        if (token.Length < MinimumLength)
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} tem {token.Length} caracteres; o minimo e {MinimumLength}. "
                + "Gere uma com: openssl rand -base64 48");
        }

        return new BootstrapGate(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>
    /// Comparacao em tempo constante. Comparar string com == vaza, pelo tempo
    /// de resposta, quantos caracteres iniciais o atacante acertou, e isso
    /// transforma forca bruta exponencial em linear.
    /// </summary>
    public bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        byte[] candidate = Encoding.UTF8.GetBytes(presented);
        return CryptographicOperations.FixedTimeEquals(candidate, _expected);
    }
}
