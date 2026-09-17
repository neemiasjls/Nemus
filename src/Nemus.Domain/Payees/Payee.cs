using Nemus.Domain.Ledger;
using Nemus.Domain.Primitives;

namespace Nemus.Domain.Payees;

/// <summary>
/// Favorecido como tabela propria, nao como conta externa por estabelecimento.
///
/// O Firefly III faz cada payee virar uma expense account. Aqui nao: isso
/// explode o cadastro de contas e transforma "juntar dois cadastros do mesmo
/// mercado" numa fusao de contas do razao. Como dimensao, fundir e um UPDATE.
/// A consulta "quanto gastei no mercado tal" sai de um GROUP BY payee_id.
/// </summary>
public sealed class Payee
{
    private Payee(Guid id, string name, Guid? defaultCategoryId, DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        DefaultCategoryId = defaultCategoryId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }
    public string Name { get; private set; }

    /// <summary>Semente do motor de regras da fase 7. Nao aplica nada sozinho.</summary>
    public Guid? DefaultCategoryId { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public static Result<Payee> Create(
        string name, Guid? defaultCategoryId = null, Guid? id = null, DateTimeOffset? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new Error("payee.name_required", "Favorecido precisa de nome.");
        }

        return new Payee(
            id ?? UuidV7.NewGuid(), name.Trim(), defaultCategoryId, createdAt ?? DateTimeOffset.UtcNow);
    }

    public Result Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new Error("payee.name_required", "Favorecido precisa de nome.");
        }

        Name = name.Trim();
        return Result.Success();
    }

    public void SetDefaultCategory(Guid? categoryId) => DefaultCategoryId = categoryId;

    public override string ToString() => Name;
}

/// <summary>
/// A camada de normalizacao. Cada banco escreve o mesmo estabelecimento de
/// um jeito - "PAG*IFOOD SAO PAULO BR", "IFOOD *IFOOD", "IFD IFOOD.COM".
/// Sem mapear isso, relatorio por estabelecimento nunca fecha.
/// </summary>
public sealed class PayeeAlias
{
    private PayeeAlias(Guid id, Guid payeeId, string rawText, ImportSource source, DateTimeOffset createdAt)
    {
        Id = id;
        PayeeId = payeeId;
        RawText = rawText;
        Source = source;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }
    public Guid PayeeId { get; }

    /// <summary>Texto cru como o banco mandou, sem normalizar.</summary>
    public string RawText { get; }

    public ImportSource Source { get; }
    public DateTimeOffset CreatedAt { get; }

    public static Result<PayeeAlias> Create(
        Guid payeeId, string rawText, ImportSource source,
        Guid? id = null, DateTimeOffset? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new Error("payee_alias.raw_text_required", "Texto de origem vazio.");
        }

        return new PayeeAlias(
            id ?? UuidV7.NewGuid(), payeeId, rawText.Trim(), source, createdAt ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Chave de comparacao: caixa e espaco nao distinguem alias.</summary>
    public string NormalizedKey => RawText.Trim().ToUpperInvariant();
}
