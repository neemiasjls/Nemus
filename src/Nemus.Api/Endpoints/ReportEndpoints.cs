using Nemus.Api.Contracts;
using Nemus.Domain.Budgeting;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Persistence;

namespace Nemus.Api.Endpoints;

/// <summary>
/// Relatorios: as somas que antes o navegador fazia sobre a pagina que tinha
/// em maos e agora o banco faz sobre o razao inteiro.
///
/// A janela e sempre de meses fechados e vem nomeada nos dois extremos -
/// quantos meses e ate qual. Intervalo por data solta convidaria a pedir "de
/// 12 de marco a 4 de agosto", que nao e pergunta que orcamento mensal saiba
/// responder.
/// </summary>
internal static class ReportEndpoints
{
    /// <summary>Seis meses: meio ano cabe num grafico sem virar tapete.</summary>
    private const int DefaultMonths = 6;

    public static void MapReports(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/api/reports").WithTags("Relatorios");

        group.MapGet("/monthly", async (
            string? until,
            int? months,
            string? currency,
            ReportQueries reports,
            CancellationToken cancellationToken) =>
        {
            Result<Window> window = Window.Parse(until, months, currency);
            if (window.IsFailure)
            {
                return Failures.Invalid(window.Error.Code, window.Error.Message);
            }

            (BudgetMonth first, int count, Currency money) = window.Value;

            IReadOnlyList<MonthFlowRow> rows = await reports
                .ReadMonthlyFlowAsync(first.FirstDay, count, money.Code, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new MonthlyFlowResponse(
                money.Code,
                rows.Select(r => new MonthFlowResponse(
                    BudgetMonth.Of(r.Month).ToString(), r.Income, r.Expense)).ToList()));
        });

        group.MapGet("/categories", async (
            string? until,
            int? months,
            string? currency,
            ReportQueries reports,
            CancellationToken cancellationToken) =>
        {
            Result<Window> window = Window.Parse(until, months, currency);
            if (window.IsFailure)
            {
                return Failures.Invalid(window.Error.Code, window.Error.Message);
            }

            (BudgetMonth first, int count, Currency money) = window.Value;

            IReadOnlyList<CategoryMonthRow> rows = await reports
                .ReadCategoryTrendAsync(first.FirstDay, count, money.Code, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(Pivot(rows, first, count, money));
        });
    }

    /// <summary>
    /// Vira as linhas esparsas do banco na tabela que a tela desenha: uma
    /// linha por categoria, um valor por mes, buraco preenchido com zero.
    ///
    /// A media divide pelo numero de meses da janela, nao pelos meses em que
    /// houve gasto. Uma conta que veio em dois dos seis meses tem media de
    /// seis meses mesmo: o que se quer saber e quanto ela pesa no mes tipico,
    /// e nos quatro meses sem ela o peso foi zero de verdade.
    /// </summary>
    private static CategoryTrendResponse Pivot(
        IReadOnlyList<CategoryMonthRow> rows, BudgetMonth first, int months, Currency currency)
    {
        var labels = new List<string>(months);
        var indexOf = new Dictionary<DateOnly, int>(months);

        BudgetMonth cursor = first;
        for (int i = 0; i < months; i++)
        {
            labels.Add(cursor.ToString());
            indexOf[cursor.FirstDay] = i;
            cursor = cursor.Next;
        }

        // Guid.Empty representa o balde "Sem categoria", que nao tem id.
        var byCategory = new Dictionary<Guid, (Guid? Id, string Name, long[] Amounts)>();

        foreach (CategoryMonthRow row in rows)
        {
            if (!indexOf.TryGetValue(row.Month, out int column))
            {
                continue;
            }

            Guid key = row.CategoryId ?? Guid.Empty;

            if (!byCategory.TryGetValue(key, out (Guid? Id, string Name, long[] Amounts) bucket))
            {
                bucket = (row.CategoryId, row.CategoryName, new long[months]);
                byCategory[key] = bucket;
            }

            bucket.Amounts[column] += row.Amount;
        }

        List<CategoryTrendRowResponse> categories = byCategory.Values
            .Select(b =>
            {
                long total = b.Amounts.Sum();
                return new CategoryTrendRowResponse(b.Id, b.Name, b.Amounts, total, total / months);
            })
            .OrderByDescending(c => c.TotalMinorUnits)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new CategoryTrendResponse(currency.Code, labels, categories);
    }

    /// <summary>Janela pedida, ja validada: primeiro mes, quantos meses, moeda.</summary>
    private readonly record struct Window(BudgetMonth First, int Months, Currency Selected)
    {
        public static Result<Window> Parse(string? until, int? months, string? currency)
        {
            Result<BudgetMonth> last = until is null
                ? Result<BudgetMonth>.Success(BudgetMonth.Of(DateOnly.FromDateTime(DateTime.UtcNow)))
                : BudgetMonth.Parse(until);

            if (last.IsFailure)
            {
                return Result<Window>.Failure(last.Error);
            }

            int count = months ?? DefaultMonths;
            if (count < 1 || count > ReportQueries.MaxMonths)
            {
                return Result<Window>.Failure(new Error(
                    "report.months_out_of_range",
                    $"A janela do relatorio vai de 1 a {ReportQueries.MaxMonths} meses."));
            }

            Result<Currency> money = Currency.TryFrom(currency ?? "BRL");
            if (money.IsFailure)
            {
                return Result<Window>.Failure(money.Error);
            }

            // Janela de N meses TERMINANDO em "until": o mes pedido e o
            // ultimo, nao o primeiro. Quem olha relatorio quer os seis meses
            // ate agora, nao os seis a partir de agora - metade deles no
            // futuro, todos vazios.
            BudgetMonth first = last.Value;
            for (int i = 1; i < count; i++)
            {
                first = first.Previous;
            }

            return Result<Window>.Success(new Window(first, count, money.Value));
        }
    }
}
