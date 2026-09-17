using Nemus.Api.Contracts;
using Nemus.Domain.Budgeting;
using Nemus.Domain.Categories;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Persistence;
using Npgsql;

namespace Nemus.Api.Endpoints;

internal static class BudgetEndpoints
{
    public static void MapBudget(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/api/budget").WithTags("Orcamento");

        group.MapGet("/{month}", async (
            string month,
            string? currency,
            BudgetRepository budget,
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

            BudgetMonthView view = await budget
                .ReadMonthAsync(parsedMonth.Value, parsedCurrency.Value, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(Map(view));
        });

        // PUT porque e "o valor atribuido passa a ser X", nao "some X": repetir
        // a mesma requisicao deixa o mesmo estado. Um clique duplo nao dobra
        // o dinheiro do envelope.
        group.MapPut("/{month}/categories/{categoryId:guid}", async (
            string month,
            Guid categoryId,
            SetAssignmentRequest request,
            BudgetRepository budget,
            CategoryRepository categories,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Nemus.Api.Budget");

            if (request is null)
            {
                return Failures.Invalid("request.empty", "Corpo da requisicao ausente.");
            }

            Result<BudgetMonth> parsedMonth = BudgetMonth.Parse(month);
            if (parsedMonth.IsFailure)
            {
                return Failures.Invalid(parsedMonth.Error.Code, parsedMonth.Error.Message);
            }

            Result<Currency> parsedCurrency = Currency.TryFrom(request.CurrencyCode ?? "BRL");
            if (parsedCurrency.IsFailure)
            {
                return Failures.Invalid(parsedCurrency.Error.Code, parsedCurrency.Error.Message);
            }

            Category? category = await categories.FindAsync(categoryId, cancellationToken).ConfigureAwait(false);
            if (category is null)
            {
                return Failures.NotFound("category.not_found", "Categoria nao encontrada.");
            }

            // A FK composta da 010 barraria de qualquer jeito. Checar aqui
            // antes so troca a mensagem generica de "referencia invalida" por
            // uma que explica o metodo.
            if (category.Kind != CategoryKind.Expense)
            {
                return Failures.FromDomain(new Error(
                    "budget.income_category",
                    "Categoria de receita nao recebe atribuicao: receita vira dinheiro pronto para atribuir."));
            }

            Result<BudgetAssignment> assignment = BudgetAssignment.Create(
                categoryId,
                parsedMonth.Value,
                Money.FromMinorUnits(request.AmountMinorUnits, parsedCurrency.Value));

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
                return Failures.FromPostgres(exception, logger);
            }

            // Devolve o mes inteiro: mudar um envelope muda o pronto para
            // atribuir, e a tela precisa dos dois numeros juntos.
            BudgetMonthView view = await budget
                .ReadMonthAsync(parsedMonth.Value, parsedCurrency.Value, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(Map(view));
        });
    }

    private static BudgetMonthResponse Map(BudgetMonthView view) => new(
        view.Month.ToString(),
        view.CurrencyCode,
        view.ReadyToAssign,
        view.NetInflow,
        view.Assigned,
        view.Activity,
        view.Available,
        view.OnBudgetBalance,
        view.IsBalanced,
        view.UncategorizedTransactions,
        view.UncategorizedAmount,
        view.Categories
            .Select(c => new BudgetCategoryResponse(
                c.CategoryId, c.ParentId, c.Name, c.IsArchived, c.Assigned, c.Activity, c.Available))
            .ToList());
}
