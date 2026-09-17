using System.Globalization;
using Nemus.Api.Contracts;
using Nemus.Domain.Accounts;
using Nemus.Domain.Installments;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Persistence;
using Npgsql;

namespace Nemus.Api.Endpoints;

internal static class InstallmentEndpoints
{
    public static void MapInstallments(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/api/installments").WithTags("Parcelamento");

        group.MapGet("/", async (
            InstallmentRepository installments,
            bool? onlyOpen,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<InstallmentPlanRow> plans = await installments
                .ListAsync(Today(), onlyOpen ?? false, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(plans.Select(Map).ToList());
        });

        // Quanto de cada fatura futura ja esta comprometido. E a pergunta que
        // parcelamento cria: quanto do proximo mes ja esta gasto antes de
        // comecar.
        group.MapGet("/upcoming", async (
            InstallmentRepository installments,
            int? months,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<UpcomingCommitment> upcoming = await installments
                .UpcomingAsync(Today(), months ?? 6, cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(upcoming
                .Select(u => new UpcomingCommitmentResponse(
                    Month(u.StatementMonth), u.CurrencyCode, u.Amount, u.PlanCount))
                .ToList());
        });

        /*
         * Uma compra parcelada vira, de uma vez so:
         *
         *   no razao      UMA transacao: o cartao fica devendo o valor
         *                 financiado inteiro, hoje. E o que voce deve de
         *                 verdade no instante da compra.
         *
         *   no cronograma N parcelas com competencia e vencimento, que nao
         *                 sao lancamentos - sao compromisso previsto.
         *
         * Juros, quando ha, entram como perna separada: o produto custou o
         * preco a vista, e o resto e custo de financiamento. Misturar os dois
         * numa perna so esconderia quanto o parcelamento custou.
         */
        group.MapPost("/", async (
            CreateInstallmentPurchaseRequest request,
            AccountRepository accounts,
            CreditCardRepository cards,
            InstallmentRepository installments,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Nemus.Api.Installments");

            if (request is null)
            {
                return Failures.Invalid("request.empty", "Corpo da requisicao ausente.");
            }

            AccountSummary? card = await accounts
                .FindAsync(request.CardAccountId, cancellationToken).ConfigureAwait(false);

            if (card is null)
            {
                return Failures.NotFound("account.not_found", "Cartao nao encontrado.");
            }

            if (card.Type != AccountType.Liability)
            {
                return Failures.FromDomain(new Error(
                    "installment.not_a_card",
                    "Parcelamento e de cartao. Escolha uma conta do tipo passivo."));
            }

            CreditCardTerms? terms = await cards
                .FindAsync(request.CardAccountId, cancellationToken).ConfigureAwait(false);

            if (terms is null)
            {
                return Failures.FromDomain(new Error(
                    "card.terms_missing",
                    "Este cartao ainda nao tem fechamento e vencimento. Sem eles nao da para saber em qual fatura cada parcela cai."));
            }

            Money total = Money.FromMinorUnits(request.TotalAmountMinorUnits, card.Currency);
            Money? financed = request.FinancedAmountMinorUnits is long value
                ? Money.FromMinorUnits(value, card.Currency)
                : null;

            Guid purchaseId = UuidV7.NewGuid();

            Result<InstallmentPlan> plan = InstallmentPlan.Create(new InstallmentPlanDraft
            {
                CardAccountId = card.Id,
                PurchaseTransactionId = purchaseId,
                Description = request.Description ?? string.Empty,
                TotalAmount = total,
                FinancedAmount = financed,
                InstallmentCount = request.InstallmentCount,
                PurchaseDate = request.OccurredOn,
                ClosingDay = terms.ClosingDay,
                DueDay = terms.DueDay,
            });

            if (plan.IsFailure)
            {
                return Failures.FromDomain(plan.Error);
            }

            Result<Transaction> purchase = plan.Value.BuildPurchase(
                SystemAccounts.ExternalExpenses, request.CategoryId, request.InterestCategoryId);
            if (purchase.IsFailure)
            {
                return Failures.FromDomain(purchase.Error);
            }

            try
            {
                await installments
                    .AddAsync(plan.Value, purchase.Value, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (PostgresException exception)
            {
                return Failures.FromPostgres(exception, logger);
            }

            Installment first = plan.Value.Installments[0];

            return Results.Created($"/api/installments/{plan.Value.Id}", new InstallmentCreatedResponse(
                plan.Value.Id,
                purchaseId,
                plan.Value.InstallmentCount,
                first.Amount.MinorUnits,
                plan.Value.InterestAmount.MinorUnits,
                Month(plan.Value.FirstStatementMonth),
                first.DueDate));
        });
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.UtcNow);

    private static string Month(DateOnly date) =>
        date.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    private static InstallmentPlanResponse Map(InstallmentPlanRow row) => new(
        row.Id,
        row.CardAccountId,
        row.CardName,
        row.Description,
        row.CurrencyCode,
        row.TotalAmount,
        row.FinancedAmount,
        row.InterestAmount,
        row.InstallmentCount,
        row.PaidCount,
        row.InstallmentAmount,
        row.RemainingAmount,
        row.PurchaseDate,
        Month(row.FirstStatementMonth),
        Month(row.LastStatementMonth),
        row.IsFinished);
}
