using System.Security.Cryptography;

namespace Nemus.Domain.Primitives;

/// <summary>
/// UUID versao 7 (RFC 9562): 48 bits de timestamp em milissegundos seguidos
/// de aleatorio. Ordenavel por tempo, o que da localidade de indice no
/// Postgres e ordenacao estavel sem coluna extra - e, quando a sincronizacao
/// com o Pluggy chegar na fase 5, ids gerados fora do banco nao colidem.
///
/// Guid.CreateVersion7() so existe a partir do .NET 9. Como o alvo e net8.0,
/// esta e a implementacao propria - preferivel a mais uma dependencia por
/// vinte linhas de codigo.
/// </summary>
public static class UuidV7
{
    private static readonly object Gate = new();
    private static long _lastMilliseconds;
    private static int _sequence;

    private const int MaxSequence = 0x0FFF; // 12 bits de rand_a

    public static Guid NewGuid() => NewGuid(DateTimeOffset.UtcNow);

    /// <summary>
    /// Gera um UUIDv7 monotonico. Dentro do mesmo milissegundo o contador de
    /// 12 bits em rand_a garante que ids gerados em sequencia continuem
    /// ordenados; se estourar, empurra um milissegundo para frente.
    /// </summary>
    public static Guid NewGuid(DateTimeOffset timestamp)
    {
        long milliseconds = timestamp.ToUnixTimeMilliseconds();
        if (milliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timestamp), "UUIDv7 nao representa instantes anteriores a 1970.");
        }

        int sequence;
        lock (Gate)
        {
            if (milliseconds > _lastMilliseconds)
            {
                _lastMilliseconds = milliseconds;
                _sequence = 0;
            }
            else if (_sequence >= MaxSequence)
            {
                _lastMilliseconds++;
                _sequence = 0;
            }
            else
            {
                _sequence++;
            }

            milliseconds = _lastMilliseconds;
            sequence = _sequence;
        }

        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes[8..]);

        bytes[0] = (byte)(milliseconds >> 40);
        bytes[1] = (byte)(milliseconds >> 32);
        bytes[2] = (byte)(milliseconds >> 24);
        bytes[3] = (byte)(milliseconds >> 16);
        bytes[4] = (byte)(milliseconds >> 8);
        bytes[5] = (byte)milliseconds;

        // Byte 6: nibble alto = versao 7, nibble baixo + byte 7 = contador.
        bytes[6] = (byte)(0x70 | ((sequence >> 8) & 0x0F));
        bytes[7] = (byte)(sequence & 0xFF);

        // Byte 8: dois bits altos = variante RFC 4122 (10xxxxxx).
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>Extrai o instante embutido. Util em teste e em diagnostico.</summary>
    public static DateTimeOffset GetTimestamp(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!value.TryWriteBytes(bytes, bigEndian: true, out _))
        {
            throw new ArgumentException("Guid invalido.", nameof(value));
        }

        if ((bytes[6] & 0xF0) != 0x70)
        {
            throw new ArgumentException("O Guid informado nao e versao 7.", nameof(value));
        }

        long milliseconds =
              ((long)bytes[0] << 40)
            | ((long)bytes[1] << 32)
            | ((long)bytes[2] << 24)
            | ((long)bytes[3] << 16)
            | ((long)bytes[4] << 8)
            | bytes[5];

        return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
    }

    public static bool IsVersion7(Guid value)
    {
        Span<byte> bytes = stackalloc byte[16];
        return value.TryWriteBytes(bytes, bigEndian: true, out _)
            && (bytes[6] & 0xF0) == 0x70
            && (bytes[8] & 0xC0) == 0x80;
    }
}
