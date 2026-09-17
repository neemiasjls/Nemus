using Nemus.Domain.Monetary;
using Nemus.Domain.Recurring;
using Npgsql;
using NpgsqlTypes;

namespace Nemus.Infrastructure.Persistence;

/// <summary>Um gasto fixo como a tela precisa dele: com o nome da categoria junto.</summary>
public sealed record RecurringExpenseRow(
    RecurringExpense Expense,
    string CategoryName,
    string? AccountName);

/// <summary>
/// Leitura e escrita dos gastos fixos.
///
/// Repare no que NAO tem aqui: nada que escreva no razao. Gasto fixo e
/// previsao, e previsao nao vira lancamento - ver o cabecalho da migration
/// 012 para o porque.
/// </summary>
public sealed class RecurringExpenseRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public RecurringExpenseRepository(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>Cria ou substitui. Repetir o mesmo pedido deixa o mesmo estado.</summary>
    public async Task SaveAsync(RecurringExpense expense, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expense);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            INSERT INTO recurring_expenses
                   (id, name, category_id, amount, currency_code, due_day, account_id,
                    is_estimate, starts_on, ends_on, archived_at, created_at, updated_at)
            VALUES (@id, @name, @category_id, @amount, @currency, @due_day, @account_id,
                    @is_estimate, @starts_on, @ends_on, @archived_at, now(), now())
            ON CONFLICT (id) DO UPDATE
               SET name          = EXCLUDED.name,
                   category_id   = EXCLUDED.category_id,
                   amount        = EXCLUDED.amount,
                   currency_code = EXCLUDED.currency_code,
                   due_day       = EXCLUDED.due_day,
                   account_id    = EXCLUDED.account_id,
                   is_estimate   = EXCLUDED.is_estimate,
                   starts_on     = EXCLUDED.starts_on,
                   ends_on       = EXCLUDED.ends_on,
                   archived_at   = EXCLUDED.archived_at,
                   updated_at    = now()
            """);

        command.Parameters.AddWithValue("id", expense.Id);
        command.Parameters.AddWithValue("name", expense.Name);
        command.Parameters.AddWithValue("category_id", expense.CategoryId);
        command.Parameters.AddWithValue("amount", expense.Amount.MinorUnits);
        command.Parameters.Add(new NpgsqlParameter("currency", NpgsqlDbType.Char)
        {
            Value = expense.Amount.Currency.Code,
        });
        command.Parameters.AddWithValue("due_day", (short)expense.DueDay);
        command.Parameters.Add(new NpgsqlParameter("account_id", NpgsqlDbType.Uuid)
        {
            Value = expense.AccountId is null ? DBNull.Value : expense.AccountId.Value,
        });
        command.Parameters.AddWithValue("is_estimate", expense.IsEstimate);
        command.Parameters.Add(new NpgsqlParameter("starts_on", NpgsqlDbType.Date) { Value = expense.StartsOn });
        command.Parameters.Add(new NpgsqlParameter("ends_on", NpgsqlDbType.Date)
        {
            Value = expense.EndsOn is null ? DBNull.Value : expense.EndsOn.Value,
        });
        command.Parameters.Add(new NpgsqlParameter("archived_at", NpgsqlDbType.TimestampTz)
        {
            Value = expense.ArchivedAt is null ? DBNull.Value : expense.ArchivedAt.Value,
        });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Todos os gastos fixos, do vencimento mais cedo ao mais tarde - que e a
    /// ordem em que o mes acontece, e por isso a ordem em que a tela lista.
    /// </summary>
    public async Task<IReadOnlyList<RecurringExpenseRow>> ListAsync(
        bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT r.id, r.name, r.category_id, r.amount, r.currency_code, r.due_day,
                   r.account_id, r.is_estimate, r.starts_on, r.ends_on, r.archived_at,
                   c.name, a.name
              FROM recurring_expenses r
              JOIN categories c      ON c.id = r.category_id
              LEFT JOIN accounts a   ON a.id = r.account_id
             WHERE (@include_archived OR r.archived_at IS NULL)
             ORDER BY r.due_day, r.name
            """);

        command.Parameters.AddWithValue("include_archived", includeArchived);

        var rows = new List<RecurringExpenseRow>();

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Currency currency = Currency.TryFrom(reader.GetString(4).Trim()).Value;

            RecurringExpense expense = RecurringExpense.Create(
                name: reader.GetString(1),
                categoryId: reader.GetGuid(2),
                amount: Money.FromMinorUnits(reader.GetInt64(3), currency),
                dueDay: reader.GetInt16(5),
                startsOn: DateOnly.FromDateTime(reader.GetDateTime(8)),
                accountId: reader.IsDBNull(6) ? null : reader.GetGuid(6),
                isEstimate: reader.GetBoolean(7),
                endsOn: reader.IsDBNull(9) ? null : DateOnly.FromDateTime(reader.GetDateTime(9)),
                id: reader.GetGuid(0),
                archivedAt: reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10)).Value;

            rows.Add(new RecurringExpenseRow(
                expense,
                reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return rows;
    }

    /// <summary>
    /// O que o razao registrou no mes e pode ser a chegada de um gasto fixo.
    ///
    /// So perna em conta externa de despesa, so com categoria, so positiva:
    /// estorno entra negativo e nao e a chegada de conta nenhuma.
    /// </summary>
    public async Task<IReadOnlyList<SpendingCandidate>> ReadCandidatesAsync(
        int year, int month, Currency currency, CancellationToken cancellationToken = default)
    {
        var from = new DateOnly(year, month, 1);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT t.id, e.category_id, e.amount, t.occurred_on
              FROM entries e
              JOIN transactions t ON t.id = e.transaction_id
              JOIN accounts a     ON a.id = e.account_id
             WHERE t.deleted_at IS NULL
               AND a.type_code = 'EXPENSE'
               AND e.category_id IS NOT NULL
               AND e.amount > 0
               AND e.currency_code = @currency
               AND t.occurred_on >= @from
               AND t.occurred_on <  @to
             ORDER BY t.occurred_on, t.id
            """);

        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = from });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = from.AddMonths(1) });
        command.Parameters.Add(new NpgsqlParameter("currency", NpgsqlDbType.Char) { Value = currency.Code });

        var rows = new List<SpendingCandidate>();

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new SpendingCandidate(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetInt64(2),
                DateOnly.FromDateTime(reader.GetDateTime(3))));
        }

        return rows;
    }

    /// <summary>
    /// Arquiva em vez de apagar: os meses em que o gasto existiu continuam
    /// explicaveis, e o nome fica livre para um cadastro novo.
    /// </summary>
    public async Task<bool> ArchiveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            UPDATE recurring_expenses
               SET archived_at = now(), updated_at = now()
             WHERE id = @id AND archived_at IS NULL
            """);
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>
    /// Apaga de vez. Existe porque gasto fixo nao e lancamento: cadastro
    /// errado nao deixa rastro contabil nenhum, entao nao ha o que preservar.
    /// </summary>
    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand(
            "DELETE FROM recurring_expenses WHERE id = @id");
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }
}
