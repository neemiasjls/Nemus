using Nemus.Domain.Identity;
using Npgsql;

namespace Nemus.Infrastructure.Persistence;

/// <summary>
/// Usuarios. Nenhum metodo aqui recebe ou devolve senha em texto: o que
/// entra e sai e hash, e quem produz o hash e <see cref="Security.PasswordHasher"/>.
/// </summary>
public sealed class UserRepository
{
    private const string Columns =
        "id, username, password_hash, is_active, created_at, updated_at, last_login_at";

    private readonly NpgsqlDataSource _dataSource;

    public UserRepository(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task AddAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            INSERT INTO users (id, username, password_hash, is_active, created_at, updated_at)
            VALUES (@id, @username, @password_hash, @is_active, @created_at, @created_at)
            """);

        command.Parameters.AddWithValue("id", user.Id);
        command.Parameters.AddWithValue("username", user.Username);
        command.Parameters.AddWithValue("password_hash", user.PasswordHash);
        command.Parameters.AddWithValue("is_active", user.IsActive);
        command.Parameters.AddWithValue("created_at", user.CreatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<User?> FindByUsernameAsync(string username, CancellationToken cancellationToken = default) =>
        FindAsync("username = @key", username, cancellationToken);

    public Task<User?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        FindAsync("id = @key", id, cancellationToken);

    /// <summary>Quantos usuarios existem. Zero significa que o app ainda nao tem dono.</summary>
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("SELECT COUNT(*)::BIGINT FROM users");
        object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return scalar is long value ? (int)value : 0;
    }

    /// <summary>
    /// Regrava o hash. Nao revoga sessao: quem chama decide isso, porque
    /// trocar a senha por vontade propria e trocar por suspeita de invasao
    /// pedem coisas diferentes.
    /// </summary>
    public async Task SavePasswordAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            UPDATE users SET password_hash = @password_hash, updated_at = @updated_at
             WHERE id = @id
            """);

        command.Parameters.AddWithValue("id", user.Id);
        command.Parameters.AddWithValue("password_hash", user.PasswordHash);
        command.Parameters.AddWithValue("updated_at", user.UpdatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RegisterLoginAsync(
        Guid userId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "UPDATE users SET last_login_at = @at, updated_at = @at WHERE id = @id");

        command.Parameters.AddWithValue("id", userId);
        command.Parameters.AddWithValue("at", at);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<User?> FindAsync(
        string where, object key, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            $"SELECT {Columns} FROM users WHERE {where}");
        command.Parameters.AddWithValue("key", key);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    internal static User Read(NpgsqlDataReader reader, int offset = 0) => User.Rehydrate(
        reader.GetGuid(offset),
        reader.GetString(offset + 1),
        reader.GetString(offset + 2),
        reader.GetBoolean(offset + 3),
        reader.GetFieldValue<DateTimeOffset>(offset + 4),
        reader.GetFieldValue<DateTimeOffset>(offset + 5),
        reader.IsDBNull(offset + 6) ? null : reader.GetFieldValue<DateTimeOffset>(offset + 6));
}
