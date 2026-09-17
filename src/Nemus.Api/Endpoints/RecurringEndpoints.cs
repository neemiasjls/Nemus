using Nemus.Api.Contracts;
using Nemus.Domain.Budgeting;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Domain.Recurring;
using Nemus.Infrastructure.Persistence;
using Npgsql;

namespace Nemus.Api.Endpoints;

/// <summary>
/// Gastos fixos: o que se repete todo mes.
///
/// Nenhuma rota aqui escreve no razao. A unica que escreve alguma coisa fora
/// da propria tabela e a de atribuir ao orcamento - e ela mexe em envelope,
/// que e intencao, nao em lancamento, que e fato.
/// </summary>
internal static class RecurringEndpoints
{
    public static void MapRecurring(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/api/recurring").WithTags("Gastos fixos");

        // O mes faz parte da pergunta: "quais gastos fixos valem em setembro e
        // quais ja vieram". Sem ele a lista seria so cadastro, e cadastro
        // sozinho nao diz o que falta pagar.
        group.MapGet("/{month}", async (
            string month,
            string? currency,
            bool? includeArchived,
            RecurringExpenseRepository recurring,
            CancellationToken cancellationToken) =>
        {
            Result<BudgetMonth> parsedMonth = BudgetMonth.Parse(month);
            if (parsedMonth.IsFailure)
            {
                return Failures.Invalid(parsedMonth.Error.Code, parsedMonth.Error.Message);
            }

            Result<Currency> parsedCurrency = Currency.TryFrom(currency ?? "BRL");
            if (parsedCurrency.IsFailure)
            {
                return Failures.Invalid(parsedCurrency.Error.Code, parsedCurrency.Error.Message);
            }

            return Results.Ok(await ReadMonthAsync(
                recurring, parsedMonth.Value, parsedCurrency.Value,
                includeArchived ?? false, cancellationToken).ConfigureAwait(false));
        });

        group.MapPost("/", async (
            SaveRecurringExpenseRequest request,
            RecurringExpenseRepository recurring,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
            await SaveAsync(null, request, recurring, loggerFactory, cancellationToken).ConfigureAwait(false));

        group.MapPut("/{id:guid}", async (
            Guid id,
            SaveRecurringExpenseRequest request,
            RecurringExpenseRepository recurring,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
            await SaveAsync(id, request, recurring, loggerFactory, cancellationToken).ConfigureAwait(false));

        // Arquivar e o padrao; apagar de vez e para cadastro errado, que nao
        // deixou rastro nenhum no razao e portanto nao tem historico a
        // preservar.
        group.MapDelete("/{id:guid}", async (
            Guid id,
            bool? purge,
            RecurringExpenseRepository recurring,
            CancellationToken cancellationToken) =>
        {
            bool done = purge == true
                ? await recurring.DeleteAsync(id, cancellationToken).ConfigureAwait(false)
                : await recurring.ArchiveAsync(id, cancellationToken).ConfigureAwait(false);

            return done
                ? Results.NoContent()
                : Failures.NotFound("recurring.not_found", "Gasto fixo nao encontrado.");
        });

        // O atalho que justifica o resto: encher os envelopes do mes com o que
        // ja se sabe que vai acontecer, em vez de digitar treze valores que
        // sao os mesmos de todo mes.
        group.MapPost("/{month}/assign", async (
            string month,
            string? currency,
            RecurringExpenseRepository recurring,
            BudgetRepository budget,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            Result<BudgetMonth> parsedMonth = BudgetMonth.Parse(month);
            if (parsedMonth.IsFailure)
            {
                return Failures.Invalid(parsedMonth.Error.Code, parsedMonth.Error.Message);
            }

            Result<Currency> parsedCurrency = Currency.TryFrom(currency ?? "BRL");
            if (parsedCurrency.IsFailure)
            {
                return Failures.Invalid(parsedCurrency.Error.Code, parsedCurrency.Error.Message);
            }

            BudgetMonth target = parsedMonth.Value;
            Currency money = parsedCurrency.Value;

            IReadOnlyList<RecurringExpenseRow> rows =
                await recurring.ListAsync(false, cancellationToken).ConfigureAwait(false);

            // Varios gastos fixos podem dividir a mesma categoria - aluguel e
            // condominio em "Casa". O envelope e um so, entao o previsto da
            // categoria e a soma deles.
            Dictionary<Guid, long> expectedByCategory = rows
                .Select(r => r.Expense)
                .Where(e => e.IsActiveIn(target.Year, target.Month)
                         && e.Amount.Currency.Code == money.Code)
                .GroupBy(e => e.CategoryId)
                .ToDictionary(g => g.Key, g => g.Sum(e => e.Amount.MinorUnits));

            BudgetMonthView before =
                await budget.ReadMonthAsync(target, money, cancellationToken).ConfigureAwait(false);

            // So preenche envelope vazio. Sobrescrever apagaria, sem avisar, um
            // valor que alguem decidiu na mao - e o proposito do metodo e
            // justamente que a decisao seja de quem orca.
            var alreadyAssigned = before.Categories
                .Where(c => c.Assigned != 0)
                .Select(c => c.CategoryId)
                .ToHashSet();

            int filled = 0;
            int skipped = 0;

            foreach ((Guid categoryId, long amount) in expectedByCategory)
            {
                if (alreadyAssigned.Contains(categoryId))
                {
                    skipped++;
                    continue;
                }

                Result<BudgetAssignment> assignment = BudgetAssignment.Create(
                    categoryId, target, Money.FromMinorUnits(amount, money));

                if (assignment.IsFailure)
                {
                    return Failures.FromDomain(assignment.Error);
                }

                try
                {
                    await budget.SetAsync(assignment.Value, cancellationToken).ConfigureAwait(false);
                }
                catch (PostgresException exception)
                {
                    return Failures.FromPostgres(exception, loggerFactory.CreateLogger("Nemus.Api.Recurring"));
                }

                filled++;
            }

            BudgetMonthView after =
                await budget.ReadMonthAsync(target, money, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new
            {
                month = target.ToString(),
                filled,
                skipped,
                readyToAssignMinorUnits = after.ReadyToAssign,
                assignedMinorUnits = after.Assigned,
            });
        });
    }

    private static async Task<IResult> SaveAsync(
        Guid? id,
        SaveRecurringExpenseRequest request,
        RecurringExpenseRepository recurring,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Failures.Invalid("request.empty", "Corpo da requisicao ausente.");
        }

        Result<Currency> parsedCurrency = Currency.TryFrom(request.CurrencyCode ?? "BRL");
        if (parsedCurrency.IsFailure)
        {
            return Failures.Invalid(parsedCurrency.Error.Code, parsedCurrency.Error.Message);
        }

        Result<RecurringExpense> created = RecurringExpense.Create(
            name: request.Name ?? string.Empty,
            categoryId: request.CategoryId,
            amount: Money.FromMinorUnits(request.AmountMinorUnits, parsedCurrency.Value),
            dueDay: request.DueDay,
            startsOn: request.StartsOn == default ? DateOnly.FromDateTime(DateTime.UtcNow) : request.StartsOn,
            accountId: request.AccountId,
            isEstimate: request.IsEstimate,
            endsOn: request.EndsOn,
            id: id);

        if (created.IsFailure)
        {
            return Failures.FromDomain(created.Error);
        }

        try
        {
            await recurring.SaveAsync(created.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            return Failures.FromPostgres(exception, loggerFactory.CreateLogger("Nemus.Api.Recurring"));
        }

        return Results.Ok(new { id = created.Value.Id });
    }

    private static async Task<RecurringMonthResponse> ReadMonthAsync(
        RecurringExpenseRepository recurring,
        BudgetMonth month,
        Currency currency,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<RecurringExpenseRow> rows =
            await recurring.ListAsync(includeArchived, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<SpendingCandidate> candidates = await recurring
            .ReadCandidatesAsync(month.Year, month.Month, currency, cancellationToken)
            .ConfigureAwait(false);

        // So os que valem no mes entram no cruzamento. Um gasto fixo que
        // comecou depois nao pode "ja ter vindo".
        List<RecurringExpenseRow> active = rows
            .Where(r => r.Expense.IsActiveIn(month.Year, month.Month))
            .ToList();

        IReadOnlyList<RecurringMatch> matches =
            RecurringMatcher.Match(active.Select(r => r.Expense).ToList(), candidates);

        Dictionary<Guid, RecurringMatch> byExpense = matches.ToDictionary(m => m.RecurringExpenseId);

        var items = new List<RecurringExpenseResponse>(rows.Count);
        long expected = 0;
        long matched = 0;

        foreach (RecurringExpenseRow row in rows)
        {
            RecurringExpense e = row.Expense;
            byExpense.TryGetValue(e.Id, out RecurringMatch? hit);

            bool counts = e.IsActiveIn(month.Year, month.Month) && e.Amount.Currency.Code == currency.Code;
            if (counts)
            {
                expected += e.Amount.MinorUnits;

                // O que ja veio conta pelo valor REAL, nao pelo previsto: a
                // conta de luz que veio R$ 40 mais cara saiu R$ 40 mais cara.
                matched += hit?.ActualMinorUnits ?? 0;
            }

            items.Add(new RecurringExpenseResponse(
                e.Id,
                e.Name,
                e.CategoryId,
                row.CategoryName,
                e.Amount.MinorUnits,
                e.Amount.Currency.Code,
                e.DueDay,
                e.DueDateIn(month.Year, month.Month),
                e.AccountId,
                row.AccountName,
                e.IsEstimate,
                e.StartsOn,
                e.EndsOn,
                e.IsArchived,
                hit?.IsMatched ?? false,
                hit?.TransactionId,
                hit?.ActualMinorUnits,
                hit?.OccurredOn,
                hit?.DifferenceFrom(e.Amount.MinorUnits)));
        }

        // Pendente e o PREVISTO do que ainda nao veio - nao "esperado menos
        // realizado". Se o aluguel veio mais caro, o que falta pagar do resto
        // do mes continua sendo o mesmo.
        long pending = rows
            .Where(r => r.Expense.IsActiveIn(month.Year, month.Month)
                     && r.Expense.Amount.Currency.Code == currency.Code
                     && !(byExpense.TryGetValue(r.Expense.Id, out RecurringMatch? m) && m.IsMatched))
            .Sum(r => r.Expense.Amount.MinorUnits);

        return new RecurringMonthResponse(
            month.ToString(), currency.Code, expected, matched, pending, items);
    }
}
