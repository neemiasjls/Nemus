using Nemus.Api.Contracts;
using Nemus.Domain.Accounts;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Persistence;
using Npgsql;

namespace Nemus.Api.Endpoints;

internal static class TransactionEndpoints
{
    /// <summary>
    /// Teto de pernas aceitas num lancamento manual. O dominio aguenta 256;
    /// a borda HTTP e mais restrita de proposito, porque aqui o numero vem
    /// de fora e uma requisicao gigante e trabalho gratuito para o servidor.
    /// </summary>
    private const int MaxEntriesPerRequest = 64;

    public static void MapTransactions(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/api/transactions").WithTags("Lancamentos");

        group.MapGet("/", async (
            LedgerQueries queries,
            DateOnly? from,
            DateOnly? to,
            Guid? accountId,
            int? limit,
            int? offset,
            CancellationToken cancellationToken) =>
        {
            TransactionPage page = await queries.ListAsync(
                from, to, accountId, limit ?? 50, offset ?? 0, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<TransactionResponse> items = page.Items.Select(Map).ToList();

            return Results.Ok(new TransactionPageResponse(
                items,
                page.Total,
                Math.Clamp(limit ?? 50, 1, LedgerQueries.MaxPageSize),
                Math.Max(offset ?? 0, 0)));
        });

        // Lancamento geral: N pernas, o cliente monta.
        group.MapPost("/", async (
            CreateTransactionRequest request,
            TransactionRepository transactions,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Nemus.Api.Transactions");

            if (request is null)
            {
                return Failures.Invalid("request.empty", "Corpo da requisicao ausente.");
            }

            if (request.Entries is null || request.Entries.Count == 0)
            {
                return Failures.Invalid(
                    "transaction.entries_required",
                    "Um lancamento precisa de ao menos duas pernas somando zero.");
            }

            if (request.Entries.Count > MaxEntriesPerRequest)
            {
                return Failures.Invalid(
                    "transaction.too_many_entries",
                    $"Maximo de {MaxEntriesPerRequest} pernas por lancamento.");
            }

            Result<Currency> currency = Currency.TryFrom(request.CurrencyCode ?? "BRL");
            if (currency.IsFailure)
            {
                return Failures.FromDomain(currency.Error);
            }

            TransactionKind kind = ParseKind(request.Kind);

            var legs = new List<EntryDraft>(request.Entries.Count);
            foreach (CreateEntryRequest leg in request.Entries)
            {
                legs.Add(new EntryDraft(
                    leg.AccountId,
                    Money.FromMinorUnits(leg.AmountMinorUnits, currency.Value))
                {
                    CategoryId = leg.CategoryId,
                    Memo = leg.Memo,
                });
            }

            // Repare no que NAO e copiado do pedido: Id, CreatedAt,
            // ImportBatchId e External. Sao decisao do servidor, e a origem
            // fica Manual por construcao - um lancamento vindo por HTTP nao
            // pode se declarar importado de banco.
            Result<Transaction> created = Transaction.Create(new TransactionDraft
            {
                OccurredOn = request.OccurredOn,
                Description = request.Description ?? string.Empty,
                Currency = currency.Value,
                Kind = kind,
                Notes = request.Notes,
                Entries = legs,
            });

            return await PersistAsync(created, transactions, logger, cancellationToken)
                .ConfigureAwait(false);
        });

        // Atalho para o caso comum: uma despesa, duas pernas montadas aqui.
        group.MapPost("/expense", async (
            CreateExpenseRequest request,
            TransactionRepository transactions,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Nemus.Api.Transactions");

            if (request is null)
            {
                return Failures.Invalid("request.empty", "Corpo da requisicao ausente.");
            }

            if (request.AmountMinorUnits <= 0)
            {
                return Failures.Invalid(
                    "expense.amount_positive",
                    "Informe o valor gasto como positivo; o sinal das pernas e responsabilidade do razao.");
            }

            Result<Transaction> created = Transaction.Spend(
                request.OccurredOn,
                request.Description ?? string.Empty,
                request.AccountId,
                SystemAccounts.ExternalExpenses,
                Money.FromMinorUnits(request.AmountMinorUnits, Currency.Brl),
                request.CategoryId);

            return await PersistAsync(created, transactions, logger, cancellationToken)
                .ConfigureAwait(false);
        });

        // Categorizar depois. E o que fecha o ciclo do extrato importado: o
        // OFX do banco nao traz categoria, e sem este caminho o dinheiro
        // importado nunca entraria em envelope nenhum.
        group.MapPut("/{id:guid}/category", async (
            Guid id,
            CategorizeRequest request,
            LedgerQueries queries,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Nemus.Api.Transactions");

            if (request is null)
            {
                return Failures.Invalid("request.empty", "Corpo da requisicao ausente.");
            }

            try
            {
                CategorizeOutcome outcome = await queries
                    .CategorizeAsync(id, request.CategoryId, cancellationToken)
                    .ConfigureAwait(false);

                return outcome switch
                {
                    CategorizeOutcome.Ok => Results.NoContent(),

                    CategorizeOutcome.NotFound =>
                        Failures.NotFound("transaction.not_found", "Lancamento nao encontrado."),

                    CategorizeOutcome.NoExternalLeg => Failures.FromDomain(new Error(
                        "transaction.transfer_has_no_category",
                        "Transferencia entre contas suas nao tem categoria: o dinheiro nao saiu do seu patrimonio.")),

                    _ => Failures.FromDomain(new Error(
                        "transaction.split_has_many_categories",
                        "Lancamento dividido: a categoria vive em cada perna, e mudar tudo de uma vez apagaria a divisao.")),
                };
            }
            catch (PostgresException exception)
            {
                return Failures.FromPostgres(exception, logger);
            }
        });

        group.MapDelete("/{id:guid}", async (
            Guid id,
            LedgerQueries queries,
            CancellationToken cancellationToken) =>
        {
            bool removed = await queries.SoftDeleteAsync(id, cancellationToken).ConfigureAwait(false);

            return removed
                ? Results.NoContent()
                : Failures.NotFound("transaction.not_found", "Lancamento nao encontrado.");
        });
    }

    private static async Task<IResult> PersistAsync(
        Result<Transaction> created,
        TransactionRepository transactions,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (created.IsFailure)
        {
            return Failures.FromDomain(created.Error);
        }

        Transaction transaction = created.Value;

        try
        {
            await transactions.AddAsync(transaction, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            return Failures.FromPostgres(exception, logger);
        }

        return Results.Created(
            $"/api/transactions/{transaction.Id}",
            new TransactionCreatedResponse(
                transaction.Id,
                transaction.OccurredOn,
                transaction.Description,
                transaction.Currency.Code,
                transaction.Kind.ToCode(),
                transaction.Magnitude.MinorUnits,
                transaction.Entries.Count));
    }

    private static TransactionResponse Map(TransactionView view) => new(
        view.Id,
        view.OccurredOn,
        view.Description,
        view.CurrencyCode,
        view.Kind,
        view.Source,
        view.Notes,
        view.Entries.Select(e => new EntryResponse(
            e.Id,
            e.AccountId,
            e.AccountName,
            e.AccountTypeCode,
            e.AccountIsInternal,
            e.Amount,
            e.CategoryId,
            e.CategoryName,
            e.Memo)).ToList());

    /// <summary>
    /// So STANDARD e TRANSFER nascem de lancamento manual. OPENING_BALANCE
    /// vem da criacao de conta, e os de cartao tem fluxo proprio na fase 6 -
    /// aceitar esses codigos aqui deixaria o cliente rotular errado um
    /// lancamento e sujar relatorio depois.
    /// </summary>
    private static TransactionKind ParseKind(string? code) =>
        code?.Trim().ToUpperInvariant() == "TRANSFER"
            ? TransactionKind.Transfer
            : TransactionKind.Standard;
}
