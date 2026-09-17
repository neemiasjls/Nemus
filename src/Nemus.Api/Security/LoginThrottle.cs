using System.Collections.Concurrent;

namespace Nemus.Api.Security;

/// <summary>
/// Freio de tentativas de login.
///
/// O limitador geral da API (60 por minuto por IP) protege o servidor, nao a
/// senha: 60 palpites por minuto sao 86 mil por dia, o bastante para varrer
/// uma lista de senhas comuns. Aqui a conta e por USUARIO e IP, e so falha
/// conta - quem acerta a senha nunca e barrado.
///
/// Em memoria de proposito. O Nemus roda num processo so; guardar isso no
/// banco seria uma escrita por tentativa de login, que e exatamente o que um
/// ataque quer provocar. O custo e que reiniciar a API zera a contagem -
/// aceitavel, porque a barreira real e o PBKDF2 de 210 mil iteracoes.
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 8;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Attempts> _attempts = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset _lastSweep;

    public LoginThrottle(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _lastSweep = _clock();
    }

    /// <summary>Quanto falta para liberar, ou null se pode tentar agora.</summary>
    public TimeSpan? RetryAfter(string key)
    {
        DateTimeOffset now = _clock();
        Sweep(now);

        if (!_attempts.TryGetValue(key, out Attempts? entry))
        {
            return null;
        }

        lock (entry)
        {
            if (now - entry.WindowStart >= Window)
            {
                _attempts.TryRemove(key, out _);
                return null;
            }

            return entry.Failures >= MaxFailures ? Window - (now - entry.WindowStart) : null;
        }
    }

    public void RegisterFailure(string key)
    {
        DateTimeOffset now = _clock();
        Attempts entry = _attempts.GetOrAdd(key, _ => new Attempts(now));

        lock (entry)
        {
            if (now - entry.WindowStart >= Window)
            {
                entry.WindowStart = now;
                entry.Failures = 0;
            }

            entry.Failures++;
        }
    }

    /// <summary>Acertou a senha: a contagem daquele usuario zera.</summary>
    public void Clear(string key) => _attempts.TryRemove(key, out _);

    /// <summary>
    /// Limpeza preguicosa. Sem isto o dicionario cresceria com um nome de
    /// usuario inventado por tentativa - que e como se derruba um servidor
    /// por memoria sem nunca acertar uma senha.
    /// </summary>
    private void Sweep(DateTimeOffset now)
    {
        if (now - _lastSweep < Window)
        {
            return;
        }

        _lastSweep = now;

        foreach (KeyValuePair<string, Attempts> pair in _attempts)
        {
            lock (pair.Value)
            {
                if (now - pair.Value.WindowStart >= Window)
                {
                    _attempts.TryRemove(pair.Key, out _);
                }
            }
        }
    }

    private sealed class Attempts
    {
        public Attempts(DateTimeOffset windowStart) => WindowStart = windowStart;

        public DateTimeOffset WindowStart { get; set; }
        public int Failures { get; set; }
    }
}
