using Nemus.Domain.Monetary;

namespace Nemus.Domain.Ledger;

/// <summary>
/// Uma perna. Negativa saiu da conta, positiva entrou.
///
/// Nao tem construtor publico e nao tem setter: so nasce por dentro de
/// <see cref="Transaction"/>, que e quem garante que o conjunto fecha em
/// zero. Perna solta nao existe neste modelo.
/// </summary>
public sealed class Entry
{
    internal Entry(
        Guid id,
        Guid transactionId,
        Guid accountId,
        Money amount,
        Guid? categoryId,
        string? memo,
        short sortOrder)
    {
        Id = id;
        TransactionId = transactionId;
        AccountId = accountId;
        Amount = amount;
        CategoryId = categoryId;
        Memo = memo;
        SortOrder = sortOrder;
    }

    public Guid Id { get; }
    public Guid TransactionId { get; }
    public Guid AccountId { get; }
    public Money Amount { get; }

    /// <summary>
    /// A categoria vive na perna, nao na transacao: e o que permite compra
    /// dividida (mercado + limpeza numa unica linha de extrato).
    /// </summary>
    public Guid? CategoryId { get; private set; }

    public string? Memo { get; private set; }
    public short SortOrder { get; }

    public bool IsInflow => Amount.IsPositive;
    public bool IsOutflow => Amount.IsNegative;

    /// <summary>
    /// Recategorizar nao mexe em valor, entao nao pode desbalancear nada.
    /// E por isso que e a unica mutacao permitida direto na perna.
    /// </summary>
    public void Recategorize(Guid? categoryId) => CategoryId = categoryId;

    public void SetMemo(string? memo) =>
        Memo = string.IsNullOrWhiteSpace(memo) ? null : memo.Trim();

    public override string ToString() => $"{Amount} em {AccountId:D}";
}

/// <summary>Descricao de uma perna antes de a transacao existir.</summary>
public sealed record EntryDraft(Guid AccountId, Money Amount)
{
    public Guid? CategoryId { get; init; }
    public string? Memo { get; init; }
    public Guid? Id { get; init; }
}
