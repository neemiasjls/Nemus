using Npgsql;
using NpgsqlTypes;

namespace Nemus.Infrastructure.Persistence;

/// <summary>Entrada e saida de um mes, ja somadas pelo banco.</summary>
public sealed record MonthFlowRow(DateOnly Month, long Income, long Expense);

/// <summary>
/// Quanto uma categoria consumiu num mes. <see cref="CategoryId"/> nulo e o
/// balde do gasto sem envelope - que aparece no relatorio de proposito, pela
/// mesma razao que aparece no orcamento: dinheiro sem categoria tem que
/// incomodar.
/// </summary>
public sealed record CategoryMonthRow(DateOnly Month, Guid? CategoryId, string CategoryName, long Amount);

/// <summary>
/// Agregacao no servidor.
///
/// POR QUE ISTO EXISTE. O painel somava no navegador, sobre a pagina de
/// lancamentos que tinha carregado - no maximo 200, que e o teto da API.
/// Quem tem mais do que isso no periodo via os meses antigos subestimados:
/// o grafico de seis meses desenhava a borda da pagina, nao o gasto. A tela
/// avisava, mas avisar que um numero esta errado nao e o mesmo que o numero
/// estar certo.
///
/// Aqui o SUM roda onde os dados estao e ve todos eles. O navegador recebe
/// um numero por mes em vez de um lancamento por linha, entao a tela fica
/// certa e mais leve ao mesmo tempo.
///
/// A REGRA DE LEITURA E A MESMA DE SEMPRE: gasto e o que entrou nas contas
/// externas de despesa, receita e o que saiu das de receita. Transferencia
/// entre contas proprias nao toca nenhuma das duas e por isso nao conta;
/// estorno entra negativo e se abate sozinho. Nao inventei semantica nova ao
/// mudar de lugar: se um numero mudar agora, mudou por estar corrigido, nao
/// por passar a medir outra coisa.
///
/// Todo SUM tem ::BIGINT explicito - no PostgreSQL SUM(bigint) devolve
/// NUMERIC, e sem o cast o driver entregaria decimal.
/// </summary>
public sealed class ReportQueries
{
    /// <summary>
    /// Teto de meses por consulta. Dez anos de historia mensal cabem num
    /// grafico e num payload; pedir mais e engano, nao necessidade.
    /// </summary>
    public const int MaxMonths = 120;

    private readonly NpgsqlDataSource _dataSource;

    public ReportQueries(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    /// <summary>
    /// Entrada e saida de cada mes da janela, do mais antigo para o mais novo.
    ///
    /// A grade vem de generate_series, nao dos dados: mes sem nenhum
    /// lancamento volta com zero em vez de sumir. Um grafico que omite o mes
    /// vazio mente sobre o intervalo - dezembro sem gasto tem que aparecer
    /// como coluna no chao, nao como novembro colado em janeiro.
    /// </summary>
    public async Task<IReadOnlyList<MonthFlowRow>> ReadMonthlyFlowAsync(
        DateOnly firstMonth,
        int months,
        string currencyCode,
        CancellationToken cancellationToken = default)
    {
        months = Math.Clamp(months, 1, MaxMonths);

        DateOnly from = FirstDayOf(firstMonth);
        DateOnly toExclusive = from.AddMonths(months);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            WITH grid AS (
                SELECT generate_series(
                           @from::timestamp,
                           @to::timestamp - INTERVAL '1 day',
                           INTERVAL '1 month')::date AS month
            ),
            flow AS (
                SELECT date_trunc('month', t.occurred_on::timestamp)::date AS month,
                       COALESCE(-SUM(e.amount) FILTER (WHERE a.type_code = 'REVENUE'), 0)::BIGINT AS income,
                       COALESCE( SUM(e.amount) FILTER (WHERE a.type_code = 'EXPENSE'), 0)::BIGINT AS expense
                  FROM entries e
                  JOIN transactions t ON t.id = e.transaction_id
                  JOIN accounts a     ON a.id = e.account_id
                 WHERE t.deleted_at IS NULL
                   AND e.currency_code = @currency
                   AND t.occurred_on >= @from
                   AND t.occurred_on <  @to
                 GROUP BY 1
            )
            SELECT g.month,
                   COALESCE(f.income, 0)::BIGINT,
                   COALESCE(f.expense, 0)::BIGINT
              FROM grid g
              LEFT JOIN flow f ON f.month = g.month
             ORDER BY g.month
            """);

        AddWindow(command, from, toExclusive, currencyCode);

        var rows = new List<MonthFlowRow>();

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new MonthFlowRow(
                DateOnly.FromDateTime(reader.GetDateTime(0)),
                reader.GetInt64(1),
                reader.GetInt64(2)));
        }

        return rows;
    }

    /// <summary>
    /// Gasto por categoria e mes na janela.
    ///
    /// Esparso de proposito: so volta par (categoria, mes) que teve
    /// movimento. A grade densa seria categorias x meses quase toda de
    /// zeros, e quem desenha a tabela sabe preencher buraco com zero melhor
    /// do que o banco sabe transmiti-lo.
    /// </summary>
    public async Task<IReadOnlyList<CategoryMonthRow>> ReadCategoryTrendAsync(
        DateOnly firstMonth,
        int months,
        string currencyCode,
        CancellationToken cancellationToken = default)
    {
        months = Math.Clamp(months, 1, MaxMonths);

        DateOnly from = FirstDayOf(firstMonth);
        DateOnly toExclusive = from.AddMonths(months);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT date_trunc('month', t.occurred_on::timestamp)::date AS month,
                   e.category_id,
                   COALESCE(c.name, 'Sem categoria') AS category_name,
                   SUM(e.amount)::BIGINT             AS amount
              FROM entries e
              JOIN transactions t ON t.id = e.transaction_id
              JOIN accounts a     ON a.id = e.account_id
              LEFT JOIN categories c ON c.id = e.category_id
             WHERE t.deleted_at IS NULL
               AND a.type_code = 'EXPENSE'
               AND e.currency_code = @currency
               AND t.occurred_on >= @from
               AND t.occurred_on <  @to
             GROUP BY 1, 2, 3
            HAVING SUM(e.amount) <> 0
             ORDER BY 1, 4 DESC
            """);

        AddWindow(command, from, toExclusive, currencyCode);

        var rows = new List<CategoryMonthRow>();

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new CategoryMonthRow(
                DateOnly.FromDateTime(reader.GetDateTime(0)),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                reader.GetInt64(3)));
        }

        return rows;
    }

    /// <summary>Mes e sempre o dia 1: a janela do relatorio e de meses inteiros.</summary>
    private static DateOnly FirstDayOf(DateOnly day) => new(day.Year, day.Month, 1);

    private static void AddWindow(
        NpgsqlCommand command, DateOnly from, DateOnly toExclusive, string currencyCode)
    {
        command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.Date) { Value = from });
        command.Parameters.Add(new NpgsqlParameter("to", NpgsqlDbType.Date) { Value = toExclusive });
        command.Parameters.Add(new NpgsqlParameter("currency", NpgsqlDbType.Char) { Value = currencyCode });
    }
}
