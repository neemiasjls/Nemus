using Nemus.Api.Contracts;
using Nemus.Domain.Categories;
using Nemus.Domain.Primitives;
using Nemus.Infrastructure.Persistence;
using Npgsql;

namespace Nemus.Api.Endpoints;

internal static class CategoryEndpoints
{
    public static void MapCategories(this IEndpointRouteBuilder routes)
    {
        RouteGroupBuilder group = routes.MapGroup("/api/categories").WithTags("Categorias");

        group.MapGet("/", async (
            CategoryRepository categories,
            CancellationToken cancellationToken) =>
        {
            IReadOnlyList<CategoryRow> rows =
                await categories.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            IReadOnlyList<CategoryResponse> payload = rows
                .Select(r => new CategoryResponse(r.Id, r.ParentId, r.Name, r.Kind.ToCode(), r.SortOrder))
                .ToList();

            return Results.Ok(payload);
        });

        group.MapPost("/", async (
            CreateCategoryRequest request,
            CategoryRepository categories,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Nemus.Api.Categories");

            if (request is null)
            {
                return Failures.Invalid("request.empty", "Corpo da requisicao ausente.");
            }

            Result<Category> created;

            if (request.ParentId is Guid parentId)
            {
                Category? parent =
                    await categories.FindAsync(parentId, cancellationToken).ConfigureAwait(false);

                if (parent is null)
                {
                    return Failures.NotFound(
                        "category.parent_not_found", "A categoria pai informada nao existe.");
                }

                // O tipo vem do pai, nao do pedido. Subcategoria de despesa
                // que fosse receita quebraria todo relatorio em silencio.
                created = Category.CreateChild(
                    parent, request.Name ?? string.Empty, request.SortOrder);
            }
            else
            {
                if (!TryParseKind(request.Kind, out CategoryKind kind))
                {
                    return Failures.Invalid(
                        "category.kind_invalid", "Tipo de categoria invalido. Use EXPENSE ou INCOME.");
                }

                created = Category.CreateGroup(
                    request.Name ?? string.Empty, kind, request.SortOrder);
            }

            if (created.IsFailure)
            {
                return Failures.FromDomain(created.Error);
            }

            Category category = created.Value;

            try
            {
                await categories.AddAsync(category, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception)
            {
                return Failures.FromPostgres(exception, logger);
            }

            var response = new CategoryResponse(
                category.Id, category.ParentId, category.Name, category.Kind.ToCode(), category.SortOrder);

            return Results.Created($"/api/categories/{category.Id}", response);
        });
    }

    private static bool TryParseKind(string? code, out CategoryKind kind)
    {
        switch (code?.Trim().ToUpperInvariant())
        {
            case "EXPENSE": kind = CategoryKind.Expense; return true;
            case "INCOME": kind = CategoryKind.Income; return true;
            default: kind = default; return false;
        }
    }
}
