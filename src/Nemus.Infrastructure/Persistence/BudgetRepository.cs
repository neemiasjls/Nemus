using Nemus.Domain.Budgeting;
using Nemus.Domain.Monetary;
using Npgsql;
using NpgsqlTypes;

namespace Nemus.Infrastructure.Persistence;

/// <summary>Um envelope num mes.</summary>
/// <param name="Assigned">Atribuido neste mes.</param>
/// <param name="Activity">Movimento deste mes: negativo quando se gasta.</param>
/// <param name="Available">O que sobra no envelope, com tudo o que rolou dos meses anteriores.</param>
public sealed record BudgetCategoryRow(
    Guid CategoryId,
    Guid? ParentId,
    string Name,
    bool IsArchived,
    long Assigned,
    long Activity,
    long Available);

/// <summary>O orcamento de um mes, pronto para a tela.</summary>
/// <param name="NetInflow">O que entrou no orcamento neste mes sem envelope (receita, saldo inicial, transferencia de fora), ja descontado o que saiu sem categoria.</param>
/// <param name="OnBudgetBalance">Saldo das contas do orcamento no fim do mes, lido direto do razao.</param>
/// <param name="UncategorizedTransactions">Lancamentos do mes que tiraram dinheiro do orcamento sem categoria.</param>
public sealed record BudgetMonthView(
    BudgetMonth Month,
    string CurrencyCode,
    long ReadyToAssign,
    long NetInflow,
    long OnBudgetBalance,
    int UncategorizedTransactions,
    long UncategorizedAmount,
    IReadOnlyList<BudgetCategoryRow> Categories)
{
    public long Assigned => Categories.Sum(c => c.Assigned);
    public long Activity => Categories.Sum(c => c.Activity);
    public long Available => Categories.Sum(c => c.Available);

    /// <summary>
    /// A invariante do metodo, neste mes: todo real das contas do orcamento
    /// esta num envelope ou esperando atribuicao. O saldo vem do razao por um
    /// caminho que nao passa pelas visoes do orcamento.
    /// </summary>
    public bool IsBalanced => Available + ReadyToAssign == OnBudgetBalance;
}

/// <summary>Retrato de v_budget_integrity para uma moeda.</summary>
public sealed record BudgetIntegrity(
    string CurrencyCode,
    long OnBudgetBalance,
    long TotalAvailable,
    long ReadyToAssign,
    long Difference,
    long UncategorizedTransactions,
    long UncategorizedAmount)
{
    public bool IsIntact => Difference == 0;
}

/// <summary>
/// Leitura e escrita do orcamento. Grava so atribuicoes; o resto e lido das
/// visoes da migration 010, que calculam a partir do razao.
///
/// As visoes sao esparsas: so ha linha no mes em que a categoria teve
/// atribuicao ou movimento. Para um mes M, vale a linha mais recente com
/// month &lt;= M - dela sai o disponivel; atribuido e atividade so contam se a
/// linha for do proprio M.
/// </summary>
public sealed class BudgetRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public BudgetRepository(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>Define o valor atribuido. Zero apaga a atribuicao em vez de guardar uma linha vazia.</summary>
    public async Task SetAsync(BudgetAssignment assignment, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        await using NpgsqlCommand command = _dataSource.CreateCommand(assignment.ClearsAssignment
            ? """
              DELETE FROM budget_assignments
               WHERE category_id = @category_id AND month = @month
              """
            : """
              INSERT INTO budget_assignments
                     (id, category_id, month, amount, currency_code, created_at, updated_at)
              VALUES (@id, @category_id, @month, @amount, @currency_code, @at, @at)
              ON CONFLICT (category_id, month) DO UPDATE
                 SET amount        = EXCLUDED.amount,
                     currency_code = EXCLUDED.currency_code,
                     updated_at    = EXCLUDED.updated_at
              """);

        command.Parameters.AddWithValue("id", assignment.Id);
        command.Parameters.AddWithValue("category_id", assignment.CategoryId);
        command.Parameters.Add(new NpgsqlParameter("month", NpgsqlDbType.Date) { Value = assignment.Month.FirstDay });
        command.Parameters.AddWithValue("amount", assignment.Amount.MinorUnits);
        command.Parameters.AddWithValue("currency_code", assignment.Amount.Currency.Code);
        command.Parameters.AddWithValue("at", assignment.CreatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BudgetMonthView> ReadMonthAsync(
        BudgetMonth month, Currency currency, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BudgetCategoryRow> categories =
            await ReadCategoriesAsync(month, currency, cancellationToken).ConfigureAwait(false);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT
                COALESCE((SELECT r.ready_to_assign
                            FROM v_budget_ready_to_assign r
                           WHERE r.currency_code = @currency AND r.month <= @month
                           ORDER BY r.month DESC
                           LIMIT 1), 0)::BIGINT,

                COALESCE((SELECT r.net_inflow
                            FROM v_budget_ready_to_assign r
                           WHERE r.currency_code = @currency AND r.month = @month), 0)::BIGINT,

                -- Direto do razao, sem passar pelas visoes do orcamento: e o
                -- lado independente da invariante.
                (SELECT COALESCE(SUM(e.amount), 0)::BIGINT
                   FROM entries e
                   JOIN transactions t ON t.id = e.transaction_id
                   JOIN accounts a     ON a.id = e.account_id
                  WHERE t.deleted_at IS NULL
                    AND a.is_on_budget
                    AND a.type_code IN ('ASSET', 'LIABILITY')
                    AND e.currency_code = @currency
                    AND t.occurred_on < @next_month),

                (SELECT COUNT(DISTINCT u.transaction_id)::INT
                   FROM v_budget_entries u
                  WHERE u.is_uncategorized_spending
                    AND u.currency_code = @currency AND u.month = @month),

                (SELECT COALESCE(SUM(u.amount), 0)::BIGINT
                   FROM v_budget_entries u
                  WHERE u.is_uncategorized_spending
                    AND u.currency_code = @currency AND u.month = @month)
            """);

        AddMonthParameters(command, month, currency);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        return new BudgetMonthView(
            month,
            currency.Code,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt32(3),
            reader.GetInt64(4),
            categories);
    }

    /// <summary>
    /// Toda categoria de despesa ativa, mais as arquivadas que ainda tenham
    /// dinheiro ou movimento no mes. Esconder envelope arquivado com saldo
    /// faria o dinheiro dele sumir da soma da tela sem sumir do razao.
    /// </summary>
    private async Task<IReadOnlyList<BudgetCategoryRow>> ReadCategoriesAsync(
        BudgetMonth month, Currency currency, CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            WITH latest AS (
                SELECT DISTINCT ON (m.category_id)
                       m.category_id, m.month, m.assigned, m.activity, m.available
                  FROM v_budget_months m
                 WHERE m.currency_code = @currency AND m.month <= @month
                 ORDER BY m.category_id, m.month DESC
            ),
            shaped AS (
                SELECT c.id, c.parent_id, c.name, c.is_archived, c.sort_order,
                       p.name AS parent_name,
                       CASE WHEN l.month = @month THEN l.assigned ELSE 0 END AS assigned,
                       CASE WHEN l.month = @month THEN l.activity ELSE 0 END AS activity,
                       COALESCE(l.available, 0)                              AS available
                  FROM categories c
                  LEFT JOIN categories p ON p.id = c.parent_id
                  LEFT JOIN latest l     ON l.category_id = c.id
                 WHERE c.kind = 'EXPENSE'
            )
            SELECT id, parent_id, name, is_archived, assigned, activity, available
              FROM shaped
             WHERE NOT is_archived OR assigned <> 0 OR activity <> 0 OR available <> 0
             ORDER BY COALESCE(parent_name, name), parent_id NULLS FIRST, sort_order, name
            """);

        AddMonthParameters(command, month, currency);

        var rows = new List<BudgetCategoryRow>();

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new BudgetCategoryRow(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6)));
        }

        return rows;
    }

    /// <summary>v_budget_integrity, uma linha por moeda em uso.</summary>
    public async Task<IReadOnlyList<BudgetIntegrity>> ReadIntegrityAsync(
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT currency_code, on_budget_balance, total_available, ready_to_assign,
                   difference, uncategorized_transactions, uncategorized_amount
              FROM v_budget_integrity
             ORDER BY currency_code
            """);

        var rows = new List<BudgetIntegrity>();

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new BudgetIntegrity(
                reader.GetString(0).Trim(),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6)));
        }

        return rows;
    }

    private static void AddMonthParameters(NpgsqlCommand command, BudgetMonth month, Currency currency)
    {
        command.Parameters.Add(new NpgsqlParameter("month", NpgsqlDbType.Date) { Value = month.FirstDay });
        command.Parameters.Add(new NpgsqlParameter("next_month", NpgsqlDbType.Date) { Value = month.NextFirstDay });
        command.Parameters.Add(new NpgsqlParameter("currency", NpgsqlDbType.Char) { Value = currency.Code });
    }
}
