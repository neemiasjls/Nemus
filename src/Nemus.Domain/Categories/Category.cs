using Nemus.Domain.Primitives;

namespace Nemus.Domain.Categories;

public enum CategoryKind
{
    Expense = 0,
    Income = 1,
}

public static class CategoryKindExtensions
{
    public static string ToCode(this CategoryKind kind) =>
        kind == CategoryKind.Income ? "INCOME" : "EXPENSE";

    public static CategoryKind FromCode(string code) => code switch
    {
        "EXPENSE" => CategoryKind.Expense,
        "INCOME" => CategoryKind.Income,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Tipo de categoria desconhecido."),
    };
}

/// <summary>
/// Categoria e dimensao do lancamento, nao conta do razao.
///
/// A alternativa classica seria categoria como conta de despesa, o que e
/// contabilidade mais pura. Ficou de fora porque o envelope da fase 4 tem
/// saldo proprio (atribuido - gasto + rollover) e nao e a mesma coisa que
/// conta de resultado: seriam dois conjuntos de contas espelhados, cada um
/// com seu jeito de quebrar.
///
/// Hierarquia de no maximo dois niveis, igual ao gatilho da migration 003.
/// </summary>
public sealed class Category
{
    private Category(
        Guid id, Guid? parentId, string name, CategoryKind kind, int sortOrder, DateTimeOffset createdAt)
    {
        Id = id;
        ParentId = parentId;
        Name = name;
        Kind = kind;
        SortOrder = sortOrder;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }
    public Guid? ParentId { get; }
    public string Name { get; private set; }
    public CategoryKind Kind { get; }
    public bool IsArchived { get; private set; }
    public int SortOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; }

    public bool IsGroup => ParentId is null;

    public static Result<Category> CreateGroup(
        string name, CategoryKind kind, int sortOrder = 0, Guid? id = null, DateTimeOffset? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new Error("category.name_required", "Categoria precisa de nome.");
        }

        return new Category(
            id ?? UuidV7.NewGuid(), null, name.Trim(), kind, sortOrder, createdAt ?? DateTimeOffset.UtcNow);
    }

    public static Result<Category> CreateChild(
        Category parent, string name, int sortOrder = 0, Guid? id = null, DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(parent);

        if (string.IsNullOrWhiteSpace(name))
        {
            return new Error("category.name_required", "Categoria precisa de nome.");
        }

        if (!parent.IsGroup)
        {
            return new Error("category.depth_exceeded",
                $"\"{parent.Name}\" ja e subcategoria; a hierarquia tem no maximo 2 niveis.");
        }

        // O tipo e herdado, nao informado: subcategoria de despesa que fosse
        // receita quebraria todo relatorio silenciosamente.
        return new Category(
            id ?? UuidV7.NewGuid(), parent.Id, name.Trim(), parent.Kind, sortOrder,
            createdAt ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Reconstroi a partir do banco preservando parent_id. Isto importa mais
    /// do que parece: reconstruir uma subcategoria como se fosse grupo faria
    /// IsGroup mentir, e CreateChild passaria a aceitar um terceiro nivel que
    /// so o gatilho NM010 barraria - erro no banco em vez de erro no dominio.
    /// </summary>
    public static Result<Category> Rehydrate(
        Guid id,
        Guid? parentId,
        string name,
        CategoryKind kind,
        bool isArchived,
        int sortOrder,
        DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new Error("category.name_required", "Categoria precisa de nome.");
        }

        if (parentId == id)
        {
            return new Error("category.self_parent", "Categoria nao pode ser pai de si mesma.");
        }

        var category = new Category(id, parentId, name.Trim(), kind, sortOrder, createdAt);

        if (isArchived)
        {
            category.Archive();
        }

        return category;
    }

    public Result Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new Error("category.name_required", "Categoria precisa de nome.");
        }

        Name = name.Trim();
        return Result.Success();
    }

    public void Archive() => IsArchived = true;

    public void Reorder(int sortOrder) => SortOrder = sortOrder;

    public override string ToString() => $"{Name} ({Kind.ToCode()})";
}
