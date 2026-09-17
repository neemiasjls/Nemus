using Nemus.Domain.Installments;
using Nemus.Domain.Monetary;
using Npgsql;
using NpgsqlTypes;

namespace Nemus.Infrastructure.Persistence;

/// <summary>Um plano como a tela le: com o andamento, nao so o cadastro.</summary>
public sealed record InstallmentPlanRow(
    Guid Id,
    Guid CardAccountId,
    string CardName,
    string Description,
    string CurrencyCode,
    long TotalAmount,
    long FinancedAmount,
    int InstallmentCount,
    DateOnly PurchaseDate,
    DateOnly FirstStatementMonth,
    DateOnly LastStatementMonth,
    long InstallmentAmount,
    int PaidCount,
    long RemainingAmount)
{
    public long InterestAmount => FinancedAmount - TotalAmount;
    public bool IsFinished => RemainingAmount == 0;
}

/// <summary>O que ja esta comprometido numa competencia futura.</summary>
public sealed record UpcomingCommitment(
    DateOnly StatementMonth,
    string CurrencyCode,
    long Amount,
    int PlanCount);

/// <summary>
/// Compras parceladas.
///
/// O razao ja registrou a compra inteira no ato - o passivo e imediato. O que
/// esta tabela guarda e o CRONOGRAMA: quanto de cada fatura futura ja esta
/// comprometido. Nao sao lancamentos, e por isso nao entram em saldo nenhum.
/// </summary>
public sealed class InstallmentRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public InstallmentRepository(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>
    /// Grava a COMPRA, o plano e as parcelas na mesma transacao de banco.
    ///
    /// Tudo junto ou nada: o razao registra o passivo inteiro no ato, e o
    /// cronograma diz como ele se distribui. Um sem o outro seria meia
    /// verdade gravada. O gatilho diferido da migration 006 ainda confere, no
    /// commit, que a soma das parcelas e exatamente o valor financiado.
    /// </summary>
    public async Task AddAsync(
        InstallmentPlan plan,
        Nemus.Domain.Ledger.Transaction purchase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(purchase);

        await using NpgsqlConnection connection =
            await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await TransactionRepository
            .AddWithinAsync(connection, transaction, purchase, cancellationToken)
            .ConfigureAwait(false);

        await using (NpgsqlCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO installment_plans
                       (id, card_account_id, purchase_tx_id, payee_id, description, currency_code,
                        total_amount, financed_amount, installment_count, purchase_date,
                        first_statement_month, source, created_at)
                VALUES (@id, @card, @purchase_tx, @payee, @description, @currency,
                        @total, @financed, @count, @purchase_date,
                        @first_month, 'MANUAL', @created_at)
                """;

            command.Parameters.AddWithValue("id", plan.Id);
            command.Parameters.AddWithValue("card", plan.CardAccountId);
            command.Parameters.AddWithValue("purchase_tx", plan.PurchaseTransactionId);
            command.Parameters.Add(new NpgsqlParameter("payee", NpgsqlDbType.Uuid)
            {
                Value = plan.PayeeId is null ? DBNull.Value : plan.PayeeId.Value,
            });
            command.Parameters.AddWithValue("description", plan.Description);
            command.Parameters.AddWithValue("currency", plan.FinancedAmount.Currency.Code);
            command.Parameters.AddWithValue("total", plan.TotalAmount.MinorUnits);
            command.Parameters.AddWithValue("financed", plan.FinancedAmount.MinorUnits);
            command.Parameters.AddWithValue("count", (short)plan.InstallmentCount);
            command.Parameters.Add(new NpgsqlParameter("purchase_date", NpgsqlDbType.Date) { Value = plan.PurchaseDate });
            command.Parameters.Add(new NpgsqlParameter("first_month", NpgsqlDbType.Date) { Value = plan.FirstStatementMonth });
            command.Parameters.AddWithValue("created_at", plan.CreatedAt);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (Installment installment in plan.Installments)
        {
            await using NpgsqlCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO installments
                       (id, plan_id, sequence, amount, statement_month, due_date, source)
                VALUES (@id, @plan, @sequence, @amount, @statement_month, @due_date, 'MANUAL')
                """;

            command.Parameters.AddWithValue("id", installment.Id);
            command.Parameters.AddWithValue("plan", plan.Id);
            command.Parameters.AddWithValue("sequence", installment.Sequence);
            command.Parameters.AddWithValue("amount", installment.Amount.MinorUnits);
            command.Parameters.Add(new NpgsqlParameter("statement_month", NpgsqlDbType.Date) { Value = installment.StatementMonth });
            command.Parameters.Add(new NpgsqlParameter("due_date", NpgsqlDbType.Date) { Value = installment.DueDate });

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Planos com andamento. "Pago" aqui e parcela cuja competencia ja passou
    /// - nao e liquidacao reconciliada, que chega com a importacao de fatura.
    /// </summary>
    public async Task<IReadOnlyList<InstallmentPlanRow>> ListAsync(
        DateOnly today, bool onlyOpen = false, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand($"""
            WITH progress AS (
                SELECT i.plan_id,
                       MIN(i.amount)::BIGINT                                        AS smallest,
                       MAX(i.statement_month)                                       AS last_month,
                       COUNT(*) FILTER (WHERE i.statement_month <= @month)::INT      AS paid_count,
                       COALESCE(SUM(i.amount) FILTER (WHERE i.statement_month > @month), 0)::BIGINT
                                                                                    AS remaining
                  FROM installments i
                 GROUP BY i.plan_id
            )
            SELECT p.id, p.card_account_id, a.name, p.description, p.currency_code,
                   p.total_amount, p.financed_amount, p.installment_count,
                   p.purchase_date, p.first_statement_month,
                   g.last_month, g.smallest, g.paid_count, g.remaining
              FROM installment_plans p
              JOIN accounts a  ON a.id = p.card_account_id
              JOIN progress g  ON g.plan_id = p.id
             {(onlyOpen ? "WHERE g.remaining > 0" : string.Empty)}
             ORDER BY p.purchase_date DESC, p.created_at DESC
            """);

        command.Parameters.Add(new NpgsqlParameter("month", NpgsqlDbType.Date)
        {
            Value = new DateOnly(today.Year, today.Month, 1),
        });

        var rows = new List<InstallmentPlanRow>();

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new InstallmentPlanRow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4).Trim(),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt16(7),
                DateOnly.FromDateTime(reader.GetDateTime(8)),
                DateOnly.FromDateTime(reader.GetDateTime(9)),
                DateOnly.FromDateTime(reader.GetDateTime(10)),
                reader.GetInt64(11),
                reader.GetInt32(12),
                reader.GetInt64(13)));
        }

        return rows;
    }

    /// <summary>
    /// Quanto de cada fatura futura ja esta comprometido, mes a mes. E a
    /// resposta para "quanto do meu proximo mes ja esta gasto antes de
    /// comecar" - a pergunta que parcelamento cria.
    /// </summary>
    public async Task<IReadOnlyList<UpcomingCommitment>> UpcomingAsync(
        DateOnly from, int months = 6, CancellationToken cancellationToken = default)
    {
        months = Math.Clamp(months, 1, 60);
        DateOnly first = new(from.Year, from.Month, 1);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT i.statement_month, p.currency_code,
                   SUM(i.amount)::BIGINT          AS amount,
                   COUNT(DISTINCT i.plan_id)::INT AS plans
              FROM installments i
              JOIN installment_plans p ON p.id = i.plan_id
             WHERE i.statement_month >= @from
               AND i.statement_month < @until
             GROUP BY i.statement_month, p.currency_code
             ORDER BY i.statement_month
            """);

        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = first });
        command.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.Date) { Value = first.AddMonths(months) });

        var rows = new List<UpcomingCommitment>();

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new UpcomingCommitment(
                DateOnly.FromDateTime(reader.GetDateTime(0)),
                reader.GetString(1).Trim(),
                reader.GetInt64(2),
                reader.GetInt32(3)));
        }

        return rows;
    }
}
