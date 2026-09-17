using Nemus.Api.Contracts;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Persistence;
using Npgsql;

namespace Nemus.Api.Endpoints;

/// <summary>
/// Traduz falha em resposta HTTP.
///
/// ITEM 15 DA AUDITORIA. Excecao de banco carrega nome de tabela, de
/// constraint, texto de comando e as vezes o proprio valor recusado. Nada
/// disso pode chegar ao cliente. O que sai daqui e um codigo estavel e uma
/// frase escrita para ser lida; o detalhe vai para o log do servidor, onde
/// serve para depurar sem servir de mapa para atacante.
/// </summary>
internal static class Failures
{
    /// <summary>Erro de dominio: a requisicao foi entendida e recusada por regra de negocio.</summary>
    public static IResult FromDomain(Error error) =>
        Results.UnprocessableEntity(new ErrorResponse(error.Code, error.Message));

    public static IResult Invalid(string code, string message) =>
        Results.BadRequest(new ErrorResponse(code, message));

    public static IResult NotFound(string code, string message) =>
        Results.NotFound(new ErrorResponse(code, message));

    /// <summary>
    /// Violacao de invariante do razao vinda do banco. Nao deveria acontecer:
    /// o dominio barra antes. Se chegar aqui, algo passou por fora dele - por
    /// isso e registrado como erro, nao como aviso.
    /// </summary>
    public static IResult FromPostgres(PostgresException exception, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(logger);

        if (NemusSqlStates.IsLedgerInvariant(exception.SqlState))
        {
            logger.LogError(
                exception,
                "Invariante do razao violada no banco (SQLSTATE {SqlState}). O dominio deveria ter barrado antes.",
                exception.SqlState);

            return Results.UnprocessableEntity(new ErrorResponse(
                "ledger.invariant_violated",
                "O lancamento viola uma regra do razao e foi recusado."));
        }

        if (exception.SqlState == NemusSqlStates.UniqueViolation)
        {
            logger.LogWarning(exception, "Violacao de unicidade.");

            return Results.Conflict(new ErrorResponse(
                "conflict.duplicate",
                "Ja existe um registro com estes dados."));
        }

        // FK: o cliente apontou para conta ou categoria que nao existe.
        if (exception.SqlState == "23503")
        {
            logger.LogWarning(exception, "Violacao de chave estrangeira.");

            return Results.UnprocessableEntity(new ErrorResponse(
                "reference.not_found",
                "Alguma conta ou categoria informada nao existe."));
        }

        logger.LogError(exception, "Erro inesperado do banco (SQLSTATE {SqlState}).", exception.SqlState);

        return Results.Problem(
            title: "Erro ao gravar.",
            detail: "A operacao nao foi concluida.",
            statusCode: StatusCodes.Status500InternalServerError);
    }
}
