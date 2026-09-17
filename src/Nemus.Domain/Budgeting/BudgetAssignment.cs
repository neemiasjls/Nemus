using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;

namespace Nemus.Domain.Budgeting;

/// <summary>
/// Quanto foi colocado num envelope num mes. E a unica coisa do orcamento que
/// e guardada; atividade, disponivel e pronto para atribuir sao calculados
/// pelas visoes da migration 010, a partir do razao.
///
/// O valor pode ser negativo: tirar dinheiro de um envelope para cobrir o
/// estouro de outro e operacao normal do metodo, nao erro. Zero significa
/// "sem atribuicao" e o repositorio apaga a linha.
/// </summary>
public sealed class BudgetAssignment
{
    /// <summary>
    /// Teto de sanidade por envelope e mes: R$ 1 bilhao. Nao e regra de
    /// negocio, e barreira contra valor digitado com zeros a mais e contra a
    /// soma das visoes estourar BIGINT.
    /// </summary>
    public const long MaxMagnitudeMinorUnits = 100_000_000_000;

    private BudgetAssignment(Guid id, Guid categoryId, BudgetMonth month, Money amount, DateTimeOffset createdAt)
    {
        Id = id;
        CategoryId = categoryId;
        Month = month;
        Amount = amount;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }
    public Guid CategoryId { get; }
    public BudgetMonth Month { get; }
    public Money Amount { get; }
    public DateTimeOffset CreatedAt { get; }

    public bool ClearsAssignment => Amount.IsZero;

    public static Result<BudgetAssignment> Create(
        Guid categoryId,
        BudgetMonth month,
        Money amount,
        Guid? id = null,
        DateTimeOffset? createdAt = null)
    {
        if (categoryId == Guid.Empty)
        {
            return new Error("budget.category_required", "Informe a categoria que recebe a atribuicao.");
        }

        if (!amount.Currency.IsDefined)
        {
            return new Error("budget.currency_required", "Atribuicao precisa de moeda definida.");
        }

        if (amount.MinorUnits is > MaxMagnitudeMinorUnits or < -MaxMagnitudeMinorUnits)
        {
            return new Error(
                "budget.amount_out_of_range",
                "Valor fora do limite de uma atribuicao. Confira se nao sobrou zero no fim.");
        }

        return new BudgetAssignment(
            id ?? UuidV7.NewGuid(), categoryId, month, amount, createdAt ?? DateTimeOffset.UtcNow);
    }
}
