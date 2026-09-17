using Nemus.Api.Contracts;
using Nemus.Domain.Accounts;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Persistence;
using Npgsql;

namespace Nemus.Api.Endpoints;

internal static class AccountEndpoints
{
    public static void MapAccounts(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/api/accounts").WithTags("Contas");

        group.MapGet("/", async (
            AccountRepository accounts,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<AccountBalance> balances =
                await accounts.GetBalancesAsync(cancellationToken).ConfigureAwait(false);

            IReadOnlyList<AccountResponse> payload = balances
                .Select(b => new AccountResponse(
                    b.AccountId,
                    b.Name,
                    b.Type.ToCode(),
                    b.IsInternal,
                    b.Balance.Currency.Code,
                    b.Balance.MinorUnits,
                    b.IsOnBudget,
                    b.IsSystem))
                .ToList();

            return Results.Ok(payload);
        });

        MapCardTerms(group);

        group.MapPost("/", async (
            CreateAccountRequest request,
            AccountRepository accounts,
            TransactionRepository transactions,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Nemus.Api.Accounts");

            if (request is null)
            {
                return Failures.Invalid("request.empty", "Corpo da requisicao ausente.");
            }

            if (!TryParseAccountType(request.Type, out AccountType type))
            {
                return Failures.Invalid(
                    "account.type_invalid",
                    "Tipo de conta invalido. Use ASSET, LIABILITY, EQUITY, REVENUE ou EXPENSE.");
            }

            Result<Currency> currency = Currency.TryFrom(request.CurrencyCode ?? "BRL");
            if (currency.IsFailure)
            {
                return Failures.FromDomain(currency.Error);
            }

            Result<Account> created = Account.Create(
                request.Name ?? string.Empty,
                type,
                currency.Value,
                request.Institution,
                request.IsOnBudget);

            if (created.IsFailure)
            {
                return Failures.FromDomain(created.Error);
            }

            Account account = created.Value;

            // Saldo inicial vira transacao de abertura contra o patrimonio.
            // Sem isso o dinheiro apareceria do nada e a soma global de todos
            // os saldos deixaria de fechar em zero.
            Transaction? opening = null;
            if (request.OpeningBalanceMinorUnits != 0)
            {
                if (!account.IsInternal)
                {
                    return Failures.Invalid(
                        "account.opening_balance_external",
                        "Conta externa nao tem saldo inicial: ela representa o mundo, nao um lugar onde voce guarda dinheiro.");
                }

                Result<Transaction> openingResult = Transaction.OpeningBalance(
                    request.OpeningBalanceDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                    account.Id,
                    SystemAccounts.OpeningBalances,
                    Money.FromMinorUnits(request.OpeningBalanceMinorUnits, currency.Value));

                if (openingResult.IsFailure)
                {
                    return Failures.FromDomain(openingResult.Error);
                }

                opening = openingResult.Value;
            }

            try
            {
                await accounts.AddAsync(account, cancellationToken).ConfigureAwait(false);

                if (opening is not null)
                {
                    await transactions.AddAsync(opening, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (PostgresException exception)
            {
                return Failures.FromPostgres(exception, logger);
            }

            var response = new AccountResponse(
                account.Id,
                account.Name,
                account.Type.ToCode(),
                account.IsInternal,
                account.Currency.Code,
                request.OpeningBalanceMinorUnits,
                account.IsOnBudget,
                IsSystem: false);

            return Results.Created($"/api/accounts/{account.Id}", response);
        });
    }

    /// <summary>
    /// Fechamento e vencimento do cartao. Ficam aqui, e nao no cadastro da
    /// conta, porque so valem para passivo - e porque sao o que o
    /// parcelamento precisa para saber em qual fatura cada parcela cai.
    /// </summary>
    private static void MapCardTerms(RouteGroupBuilder group)
    {
        group.MapGet("/cards", async (
            CreditCardRepository cards, CancellationToken cancellationToken) =>
        {
            IReadOnlyList<CreditCardTerms> terms =
                await cards.ListAsync(cancellationToken).ConfigureAwait(false);

            return Results.Ok(terms.Select(Map).ToList());
        });

        group.MapPut("/{id:guid}/card-terms", async (
            Guid id,
            CardTermsRequest request,
            AccountRepository accounts,
            CreditCardRepository cards,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Nemus.Api.Accounts");

            if (request is null)
            {
                return Failures.Invalid("request.empty", "Corpo da requisicao ausente.");
            }

            AccountSummary? account =
                await accounts.FindAsync(id, cancellationToken).ConfigureAwait(false);

            if (account is null)
            {
                return Failures.NotFound("account.not_found", "Conta nao encontrada.");
            }

            if (account.Type != AccountType.Liability)
            {
                return Failures.FromDomain(new Error(
                    "card.not_a_liability",
                    "Fechamento e vencimento so existem em cartao, que no razao e conta do tipo passivo."));
            }

            Result<CreditCardTerms> terms = CreditCardTerms.Create(
                id,
                request.ClosingDay,
                request.DueDay,
                request.CreditLimitMinorUnits is long limit
                    ? Money.FromMinorUnits(limit, account.Currency)
                    : null,
                request.PaymentAccountId);

            if (terms.IsFailure)
            {
                return Failures.FromDomain(terms.Error);
            }

            try
            {
                await cards.SaveAsync(terms.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception)
            {
                return Failures.FromPostgres(exception, logger);
            }

            return Results.Ok(Map(terms.Value));
        });
    }

    private static CardTermsResponse Map(CreditCardTerms terms) => new(
        terms.AccountId,
        terms.ClosingDay,
        terms.DueDay,
        terms.CreditLimit?.MinorUnits,
        terms.PaymentAccountId);

    private static bool TryParseAccountType(string? code, out AccountType type)
    {
        switch (code?.Trim().ToUpperInvariant())
        {
            case "ASSET": type = AccountType.Asset; return true;
            case "LIABILITY": type = AccountType.Liability; return true;
            case "EQUITY": type = AccountType.Equity; return true;
            case "REVENUE": type = AccountType.Revenue; return true;
            case "EXPENSE": type = AccountType.Expense; return true;
            default: type = default; return false;
        }
    }
}
