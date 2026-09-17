using Nemus.Domain.Primitives;

namespace Nemus.Domain.Identity;

/// <summary>
/// Uma entrada no app, revogavel.
///
/// POR QUE NAO JWT. Token assinado nao se revoga: sair do app e trocar a
/// senha so teriam efeito quando o token expirasse sozinho. Com a sessao numa
/// tabela, revogar e um UPDATE - e o custo e uma consulta por indice a cada
/// requisicao, que nesta escala e nada.
///
/// O token em si nao existe aqui: o que a sessao guarda e o hash dele.
/// </summary>
public sealed class Session
{
    /// <summary>
    /// Trinta dias. Curto demais vira relogin toda hora e empurra a pessoa
    /// para senha facil de digitar; longo demais deixa aba esquecida aberta
    /// por meses. O toque a cada uso registra atividade, sem esticar o prazo.
    /// </summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(30);

    private Session(
        Guid id, Guid userId, string tokenHash, DateTimeOffset createdAt, DateTimeOffset expiresAt)
    {
        Id = id;
        UserId = userId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        LastSeenAt = createdAt;
    }

    public Guid Id { get; }
    public Guid UserId { get; }
    public string TokenHash { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset ExpiresAt { get; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsUsableAt(DateTimeOffset moment) => RevokedAt is null && ExpiresAt > moment;

    public static Result<Session> Start(
        Guid userId,
        string tokenHash,
        DateTimeOffset now,
        TimeSpan? lifetime = null,
        Guid? id = null)
    {
        if (userId == Guid.Empty)
        {
            return new Error("auth.session_user_required", "Sessao sem usuario.");
        }

        if (string.IsNullOrWhiteSpace(tokenHash))
        {
            return new Error("auth.session_token_required", "Sessao sem token.");
        }

        TimeSpan span = lifetime ?? DefaultLifetime;
        if (span <= TimeSpan.Zero)
        {
            return new Error("auth.session_lifetime_invalid", "Duracao de sessao invalida.");
        }

        return new Session(id ?? UuidV7.NewGuid(), userId, tokenHash, now, now + span);
    }

    public static Session Rehydrate(
        Guid id,
        Guid userId,
        string tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        DateTimeOffset lastSeenAt,
        DateTimeOffset? revokedAt) =>
        new(id, userId, tokenHash, createdAt, expiresAt)
        {
            LastSeenAt = lastSeenAt,
            RevokedAt = revokedAt,
        };

    public void Revoke(DateTimeOffset at) => RevokedAt ??= at;

    public void Touch(DateTimeOffset at)
    {
        if (at > LastSeenAt)
        {
            LastSeenAt = at;
        }
    }
}
