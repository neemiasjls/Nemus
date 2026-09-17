using Nemus.Api.Contracts;
using Nemus.Domain.Ledger;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Import;
using Nemus.Infrastructure.Persistence;
using Npgsql;

namespace Nemus.Api.Endpoints;

internal static class ImportEndpoints
{
    /// <summary>
    /// ITEM 16 DA AUDITORIA - RESTRINGIR UPLOAD.
    ///
    /// Extrato de banco e arquivo pequeno: um ano inteiro de conta corrente
    /// raramente passa de algumas centenas de kilobytes. 4 MB e folgado e
    /// ainda assim impede que alguem ocupe memoria do servidor de graca.
    ///
    /// O limite e verificado ANTES de ler o corpo, pelo Content-Length, e
    /// tambem durante a leitura - porque Content-Length e informado pelo
    /// cliente e nao se confia nele.
    /// </summary>
    private const long MaxUploadBytes = 4 * 1024 * 1024;

    public static void MapImports(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/api/import").WithTags("Importacao");

        group.MapPost("/ofx", async (
            HttpRequest request,
            Guid accountId,
            bool? dryRun,
            TransactionRepository transactions,
            ImportBatchRepository batches,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Nemus.Api.Import");

            if (accountId == Guid.Empty)
            {
                return Failures.Invalid(
                    "import.account_required", "Informe a conta do razao que recebe o extrato.");
            }

            if (request.ContentLength > MaxUploadBytes)
            {
                return Failures.Invalid(
                    "import.file_too_large",
                    $"O arquivo passa do limite de {MaxUploadBytes / 1024 / 1024} MB.");
            }

            Result<byte[]> content =
                await ReadBodyAsync(request, cancellationToken).ConfigureAwait(false);

            if (content.IsFailure)
            {
                return Failures.Invalid(content.Error.Code, content.Error.Message);
            }

            // Quem decide se e OFX e o proprio leitor, nao a extensao nem o
            // Content-Type: os dois sao informados por quem envia.
            Result<OfxNode> tree = OfxParser.Parse(content.Value);
            if (tree.IsFailure)
            {
                return Failures.FromDomain(tree.Error);
            }

            Result<IReadOnlyList<OfxStatement>> statements = OfxStatementReader.Read(tree.Value);
            if (statements.IsFailure)
            {
                return Failures.FromDomain(statements.Error);
            }

            if (statements.Value.Count > 1)
            {
                return Failures.Invalid(
                    "import.multiple_statements",
                    "O arquivo traz mais de um extrato. Envie um por vez, indicando a conta de cada um.");
            }

            OfxStatement statement = statements.Value[0];
            string hash = OfxImporter.ComputeFileHash(content.Value);

            try
            {
                ImportBatch? existingBatch = await batches
                    .FindByFileHashAsync(ImportSource.Ofx, hash, cancellationToken)
                    .ConfigureAwait(false);

                var batch = new ImportBatch(
                    Guid.NewGuid(), ImportSource.Ofx, hash, accountId, DateTimeOffset.UtcNow, 0);

                Result<OfxMapping> mapping = OfxImporter.Map(
                    statement, accountId, existingBatch is null ? batch.Id : existingBatch.Id);

                if (mapping.IsFailure)
                {
                    return Failures.FromDomain(mapping.Error);
                }

                // Simulacao: le, valida e conta, sem gravar nada. Existe
                // porque conferir antes de mexer no razao e barato, e
                // desfazer uma importacao errada nao e.
                if (dryRun == true)
                {
                    return Results.Ok(new ImportPreviewResponse(
                        statement.AccountId,
                        statement.StartsOn,
                        statement.EndsOn,
                        mapping.Value.Transactions.Count,
                        mapping.Value.DuplicatesWithinFile,
                        existingBatch is not null,
                        mapping.Value.Transactions
                            .Take(20)
                            .Select(t => new ImportPreviewLine(
                                t.OccurredOn,
                                t.Description,
                                t.Entries.First().Amount.MinorUnits))
                            .ToList()));
                }

                if (existingBatch is null)
                {
                    batch = batch with { RowCount = mapping.Value.Transactions.Count };
                    await batches.AddAsync(batch, cancellationToken).ConfigureAwait(false);
                }

                ImportSummary summary = await transactions
                    .ImportAsync(mapping.Value.Transactions, cancellationToken)
                    .ConfigureAwait(false);

                // Liga a conta do razao ao ACCTID do extrato, para a proxima
                // importacao saber sozinha de quem e o arquivo.
                if (!string.IsNullOrWhiteSpace(statement.AccountId))
                {
                    await batches
                        .LinkAccountAsync(accountId, statement.AccountId, cancellationToken)
                        .ConfigureAwait(false);
                }

                return Results.Ok(new ImportResultResponse(
                    summary.Inserted,
                    summary.SkippedAsDuplicate,
                    mapping.Value.DuplicatesWithinFile,
                    summary.Total));
            }
            catch (PostgresException exception)
            {
                return Failures.FromPostgres(exception, logger);
            }
        });
    }

    /// <summary>
    /// Le o corpo com teto proprio. Nao confia no Content-Length: ele e
    /// informado pelo cliente, e um cliente mal-intencionado informa 10 e
    /// manda 10 gigabytes.
    /// </summary>
    private static async Task<Result<byte[]>> ReadBodyAsync(
        HttpRequest request, CancellationToken cancellationToken)
    {
        using var destination = new MemoryStream();
        byte[] buffer = new byte[81920];
        long total = 0;

        while (true)
        {
            int read = await request.Body
                .ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > MaxUploadBytes)
            {
                return new Error(
                    "import.file_too_large",
                    $"O arquivo passa do limite de {MaxUploadBytes / 1024 / 1024} MB.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (total == 0)
        {
            return new Error("import.empty_body", "Nenhum arquivo foi enviado.");
        }

        return destination.ToArray();
    }
}
