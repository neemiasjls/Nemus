using Nemus.Domain.Categories;
using Nemus.Domain.Primitives;
using Npgsql;

namespace Nemus.Infrastructure.Persistence;

public sealed record CategoryRow(
    Guid Id,
    Guid? ParentId,
    string Name,
    CategoryKind Kind,
    bool IsArchived,
    int SortOrder);

public sealed class CategoryRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public CategoryRepository(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task AddAsync(Category category, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(category);

        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            INSERT INTO categories (id, parent_id, name, kind, is_archived, sort_order, created_at)
            VALUES (@id, @parent_id, @name, @kind, @is_archived, @sort_order, @created_at)
            """);

        command.Parameters.AddWithValue("id", category.Id);
        command.Parameters.AddWithValue(
            "parent_id", category.ParentId is null ? DBNull.Value : category.ParentId.Value);
        command.Parameters.AddWithValue("name", category.Name);
        command.Parameters.AddWithValue("kind", category.Kind.ToCode());
        command.Parameters.AddWithValue("is_archived", category.IsArchived);
        command.Parameters.AddWithValue("sort_order", category.SortOrder);
        command.Parameters.AddWithValue("created_at", category.CreatedAt);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ordena pai antes de filho para o cliente montar a arvore numa passada
    /// so, sem precisar reordenar.
    /// </summary>
    public async Task<IReadOnlyList<CategoryRow>> ListAsync(
        bool includeArchived = false, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand($"""
            SELECT c.id, c.parent_id, c.name, c.kind, c.is_archived, c.sort_order
              FROM categories c
              LEFT JOIN categories p ON p.id = c.parent_id
             {(includeArchived ? string.Empty : "WHERE NOT c.is_archived")}
             ORDER BY COALESCE(p.name, c.name), c.parent_id NULLS FIRST, c.sort_order, c.name
            """);

        var result = new List<CategoryRow>();

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new CategoryRow(
                reader.GetGuid(0),
                reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2),
                CategoryKindExtensions.FromCode(reader.GetString(3)),
                reader.GetBoolean(4),
                reader.GetInt32(5)));
        }

        return result;
    }

    public async Task<Category?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT id, parent_id, name, kind, is_archived, sort_order, created_at
              FROM categories WHERE id = @id
            """);
        command.Parameters.AddWithValue("id", id);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        Result<Category> rebuilt = Category.Rehydrate(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetString(2),
            CategoryKindExtensions.FromCode(reader.GetString(3)),
            reader.GetBoolean(4),
            reader.GetInt32(5),
            reader.GetFieldValue<DateTimeOffset>(6));

        return rebuilt.IsSuccess ? rebuilt.Value : null;
    }
}
