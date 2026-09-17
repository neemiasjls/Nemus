using Nemus.Domain.Accounts;
using Nemus.Domain.Monetary;
using Npgsql;
using NpgsqlTypes;

namespace Nemus.Infrastructure.Persistence;

public sealed class CreditCardRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public CreditCardRepository(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>
    /// Grava as condicoes do cartao. Upsert porque sao dados de configuracao:
    /// mudar o dia de fechamento e corrigir um cadastro, nao criar um cartao novo.
    /// </summary>
    public async Task SaveAsync(CreditCardTerms terms, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terms);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            INSERT INTO credit_card_terms
                   (account_id, closing_day, due_day, credit_limit, payment_account_id)
            VALUES (@account_id, @closing_day, @due_day, @credit_limit, @payment_account_id)
            ON CONFLICT (account_id) DO UPDATE
               SET closing_day        = EXCLUDED.closing_day,
                   due_day            = EXCLUDED.due_day,
                   credit_limit       = EXCLUDED.credit_limit,
                   payment_account_id = EXCLUDED.payment_account_id
            """);

        command.Parameters.AddWithValue("account_id", terms.AccountId);
        command.Parameters.AddWithValue("closing_day", (short)terms.ClosingDay);
        command.Parameters.AddWithValue("due_day", (short)terms.DueDay);
        command.Parameters.Add(new NpgsqlParameter("credit_limit", NpgsqlDbType.Bigint)
        {
            Value = terms.CreditLimit is null ? DBNull.Value : terms.CreditLimit.Value.MinorUnits,
        });
        command.Parameters.Add(new NpgsqlParameter("payment_account_id", NpgsqlDbType.Uuid)
        {
            Value = terms.PaymentAccountId is null ? DBNull.Value : terms.PaymentAccountId.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreditCardTerms?> FindAsync(
        Guid accountId, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT t.closing_day, t.due_day, t.credit_limit, t.payment_account_id, a.currency_code
              FROM credit_card_terms t
              JOIN accounts a ON a.id = t.account_id
             WHERE t.account_id = @account_id
            """);
        command.Parameters.AddWithValue("account_id", accountId);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        Currency currency = Currency.From(reader.GetString(4).Trim());

        return CreditCardTerms.Create(
            accountId,
            reader.GetInt16(0),
            reader.GetInt16(1),
            reader.IsDBNull(2) ? null : Money.FromMinorUnits(reader.GetInt64(2), currency),
            reader.IsDBNull(3) ? null : reader.GetGuid(3)).Value;
    }

    /// <summary>Todos os cartoes ja configurados. A tela usa para saber quais aceitam parcelamento.</summary>
    public async Task<IReadOnlyList<CreditCardTerms>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT t.account_id, t.closing_day, t.due_day, t.credit_limit,
                   t.payment_account_id, a.currency_code
              FROM credit_card_terms t
              JOIN accounts a ON a.id = t.account_id
             ORDER BY a.name
            """);

        var rows = new List<CreditCardTerms>();

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Currency currency = Currency.From(reader.GetString(5).Trim());

            rows.Add(CreditCardTerms.Create(
                reader.GetGuid(0),
                reader.GetInt16(1),
                reader.GetInt16(2),
                reader.IsDBNull(3) ? null : Money.FromMinorUnits(reader.GetInt64(3), currency),
                reader.IsDBNull(4) ? null : reader.GetGuid(4)).Value);
        }

        return rows;
    }
}
