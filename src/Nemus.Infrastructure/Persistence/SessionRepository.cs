using Nemus.Domain.Identity;
using Npgsql;

namespace Nemus.Infrastructure.Persistence;

/// <summary>Sessao valida e o dono dela, resolvidos numa consulta so.</summary>
public sealed record AuthenticatedSession(Session Session, User User);

public sealed class SessionRepository
{
    /// <summary>
    /// De quanto em quanto tempo o "visto por ultimo" e regravado. Sem esta
    /// folga seria um UPDATE por requisicao, o que transforma toda leitura em
    /// escrita para registrar um dado que ninguem le com precisao de segundo.
    /// </summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    private readonly NpgsqlDataSource _dataSource;

    public SessionRepository(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task AddAsync(Session session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            INSERT INTO sessions (id, user_id, token_hash, created_at, expires_at, last_seen_at)
            VALUES (@id, @user_id, @token_hash, @created_at, @expires_at, @created_at)
            """);

        command.Parameters.AddWithValue("id", session.Id);
        command.Parameters.AddWithValue("user_id", session.UserId);
        command.Parameters.AddWithValue("token_hash", session.TokenHash);
        command.Parameters.AddWithValue("created_at", session.CreatedAt);
        command.Parameters.AddWithValue("expires_at", session.ExpiresAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A consulta de toda requisicao autenticada: acha a sessao pelo hash do
    /// token e traz o dono junto. Devolve null para sessao revogada, expirada
    /// ou de usuario desativado - tres motivos diferentes, um resultado so,
    /// porque quem esta do lado de fora nao tem direito de distinguir.
    /// </summary>
    public async Task<AuthenticatedSession?> FindActiveAsync(
        string tokenHash, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT s.id, s.user_id, s.token_hash, s.created_at, s.expires_at, s.last_seen_at, s.revoked_at,
                   u.id, u.username, u.password_hash, u.is_active, u.created_at, u.updated_at, u.last_login_at
              FROM sessions s
              JOIN users u ON u.id = s.user_id
             WHERE s.token_hash = @token_hash
               AND s.revoked_at IS NULL
               AND s.expires_at > @now
               AND u.is_active
            """);

        command.Parameters.AddWithValue("token_hash", tokenHash);
        command.Parameters.AddWithValue("now", now);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        Session session = Session.Rehydrate(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6));

        return new AuthenticatedSession(session, UserRepository.Read(reader, offset: 7));
    }

    public async Task TouchAsync(
        Guid sessionId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "UPDATE sessions SET last_seen_at = @at WHERE id = @id");

        command.Parameters.AddWithValue("id", sessionId);
        command.Parameters.AddWithValue("at", at);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sair do app. Devolve false se o token ja nao valia.</summary>
    public async Task<bool> RevokeAsync(
        string tokenHash, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            UPDATE sessions SET revoked_at = @at
             WHERE token_hash = @token_hash AND revoked_at IS NULL
            """);

        command.Parameters.AddWithValue("token_hash", tokenHash);
        command.Parameters.AddWithValue("at", at);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Derruba tudo que estiver aberto daquele usuario. E o que troca de
    /// senha precisa fazer: senha nova com sessao antiga ainda valendo nao
    /// expulsa ninguem.
    /// </summary>
    public async Task<int> RevokeAllForUserAsync(
        Guid userId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            UPDATE sessions SET revoked_at = @at
             WHERE user_id = @user_id AND revoked_at IS NULL
            """);

        command.Parameters.AddWithValue("user_id", userId);
        command.Parameters.AddWithValue("at", at);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Limpeza: sessao vencida ha tempo nao serve nem de historico.</summary>
    public async Task<int> DeleteExpiredAsync(
        DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "DELETE FROM sessions WHERE expires_at < @before");
        command.Parameters.AddWithValue("before", before);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
