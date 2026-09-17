using Nemus.Domain.Accounts;
using Nemus.Domain.Monetary;
using Npgsql;

namespace Nemus.Infrastructure.Persistence;

public sealed record AccountBalance(
    Guid AccountId,
    string Name,
    AccountType Type,
    bool IsInternal,
    Money Balance,
    bool IsOnBudget,
    bool IsSystem,
    bool IsArchived);

/// <summary>O cadastro de uma conta, sem saldo. Para quem precisa so saber o que ela e.</summary>
public sealed record AccountSummary(
    Guid Id,
    string Name,
    AccountType Type,
    Currency Currency,
    bool IsOnBudget,
    bool IsSystem);

public sealed class AccountRepository
{
    public async Task<AccountSummary?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "SELECT id, name, type_code, currency_code, is_on_budget, is_system FROM accounts WHERE id = @id");
        command.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new AccountSummary(
            reader.GetGuid(0),
            reader.GetString(1),
            AccountTypeExtensions.FromCode(reader.GetString(2)),
            Currency.From(reader.GetString(3).Trim()),
            reader.GetBoolean(4),
            reader.GetBoolean(5));
    }

    private readonly NpgsqlDataSource _dataSource;

    public AccountRepository(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task AddAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            INSERT INTO accounts (
                id, name, type_code, currency_code, institution,
                is_on_budget, is_system, external_ref, is_archived, created_at, updated_at)
            VALUES (
                @id, @name, @type_code, @currency_code, @institution,
                @is_on_budget, FALSE, @external_ref, @is_archived, @created_at, @updated_at)
            """);

        command.Parameters.AddWithValue("id", account.Id);
        command.Parameters.AddWithValue("name", account.Name);
        command.Parameters.AddWithValue("type_code", account.Type.ToCode());
        command.Parameters.AddWithValue("currency_code", account.Currency.Code);
        command.Parameters.AddWithValue(
            "institution", account.Institution is null ? DBNull.Value : account.Institution);
        command.Parameters.AddWithValue("is_on_budget", account.IsOnBudget);
        command.Parameters.AddWithValue(
            "external_ref", account.ExternalRef is null ? DBNull.Value : account.ExternalRef);
        command.Parameters.AddWithValue("is_archived", account.IsArchived);
        command.Parameters.AddWithValue("created_at", account.CreatedAt);
        command.Parameters.AddWithValue("updated_at", account.UpdatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Saldos calculados a partir das pernas, ignorando transacao com soft
    /// delete. Num razao integro a soma desta lista e exatamente zero.
    /// </summary>
    public async Task<IReadOnlyList<AccountBalance>> GetBalancesAsync(
        CancellationToken cancellationToken = default)
    {
        var balances = new List<AccountBalance>();

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT v.account_id, v.name, v.type_code, v.is_internal, v.currency_code,
                   v.balance, a.is_on_budget, a.is_system, v.is_archived
              FROM v_account_balances v
              JOIN accounts a ON a.id = v.account_id
             ORDER BY v.type_code, v.name
            """);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Currency currency = Currency.From(reader.GetString(4));
            balances.Add(new AccountBalance(
                reader.GetGuid(0),
                reader.GetString(1),
                AccountTypeExtensions.FromCode(reader.GetString(2)),
                reader.GetBoolean(3),
                Money.FromMinorUnits(reader.GetInt64(5), currency),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8)));
        }

        return balances;
    }

    public async Task<Money> GetBalanceAsync(
        Guid accountId, Currency currency, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "SELECT balance FROM v_account_balances WHERE account_id = @id");
        command.Parameters.AddWithValue("id", accountId);

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long minorUnits
            ? Money.FromMinorUnits(minorUnits, currency)
            : Money.Zero(currency);
    }

    public async Task<IReadOnlyList<Guid>> GetSystemAccountIdsAsync(
        CancellationToken cancellationToken = default)
    {
        var ids = new List<Guid>();

        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "SELECT id FROM accounts WHERE is_system ORDER BY id");
        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }
}
