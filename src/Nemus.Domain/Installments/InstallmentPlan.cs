using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;

namespace Nemus.Domain.Installments;

/// <summary>
/// Uma compra parcelada. ESTRUTURA DA FASE 6 - existe aqui para que o
/// modelo de dados da fase 1 ja tenha forma coerente, e nao esta ligada a
/// nenhum fluxo.
///
/// Um "12x sem juros" de R$ 1.200 nao e uma despesa de hoje nem sao 12
/// despesas soltas. Sao duas verdades ao mesmo tempo:
///
///   contabil      no ato da compra voce ja deve R$ 1.200 ao cartao.
///                 Isso e UMA transacao no razao, -120000 no cartao,
///                 apontada por PurchaseTransactionId.
///
///   orcamentaria  o compromisso consome R$ 100 do orcamento em cada um
///                 dos 12 meses seguintes. Isso e a lista de parcelas
///                 daqui - um cronograma, nao lancamentos do razao.
///
/// Invariante inegociavel: a soma das parcelas e exatamente o valor
/// financiado. R$ 100 em 3x sao 33,34 + 33,33 + 33,33. O centavo residual
/// tem que existir em algum lugar e jamais pode evaporar.
/// </summary>
public sealed class InstallmentPlan
{
    private readonly List<Installment> _installments;

    private InstallmentPlan(
        Guid id,
        Guid cardAccountId,
        Guid purchaseTransactionId,
        Guid? payeeId,
        string description,
        Money totalAmount,
        Money financedAmount,
        DateOnly purchaseDate,
        DateOnly firstStatementMonth,
        List<Installment> installments,
        DateTimeOffset createdAt)
    {
        Id = id;
        CardAccountId = cardAccountId;
        PurchaseTransactionId = purchaseTransactionId;
        PayeeId = payeeId;
        Description = description;
        TotalAmount = totalAmount;
        FinancedAmount = financedAmount;
        PurchaseDate = purchaseDate;
        FirstStatementMonth = firstStatementMonth;
        _installments = installments;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }
    public Guid CardAccountId { get; }
    public Guid PurchaseTransactionId { get; }
    public Guid? PayeeId { get; }
    public string Description { get; }

    /// <summary>Preco a vista.</summary>
    public Money TotalAmount { get; }

    /// <summary>Soma das parcelas. Igual ao total quando e sem juros.</summary>
    public Money FinancedAmount { get; }

    public Money InterestAmount => FinancedAmount - TotalAmount;
    public bool IsInterestFree => InterestAmount.IsZero;

    public DateOnly PurchaseDate { get; }
    public DateOnly FirstStatementMonth { get; }
    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyList<Installment> Installments => _installments;
    public int InstallmentCount => _installments.Count;

    /// <summary>Sempre igual a FinancedAmount. Se nao for, ha bug aqui.</summary>
    public Money ScheduledTotal =>
        Money.Sum(_installments.Select(i => i.Amount), FinancedAmount.Currency);

    /// <summary>O que ainda nao foi reconciliado com fatura fechada.</summary>
    public Money OutstandingAmount => Money.Sum(
        _installments.Where(i => !i.IsSettled).Select(i => i.Amount),
        FinancedAmount.Currency);

    // -----------------------------------------------------------------------

    public static Result<InstallmentPlan> Create(InstallmentPlanDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (string.IsNullOrWhiteSpace(draft.Description))
        {
            return new Error("installment.description_required", "Compra parcelada precisa de descricao.");
        }

        if (!draft.TotalAmount.IsPositive)
        {
            return new Error("installment.total_must_be_positive",
                "Informe o valor da compra como positivo.");
        }

        Money financed = draft.FinancedAmount ?? draft.TotalAmount;

        if (financed.Currency != draft.TotalAmount.Currency)
        {
            return new Error("installment.currency_mismatch",
                $"Valor financiado em {financed.Currency} nao combina com a compra em {draft.TotalAmount.Currency}.");
        }

        if (financed < draft.TotalAmount)
        {
            return new Error("installment.financed_below_total",
                "O valor financiado nao pode ser menor que o preco a vista; isso seria desconto, nao parcelamento.");
        }

        if (draft.InstallmentCount is < 1 or > 99)
        {
            return new Error("installment.count_out_of_range",
                $"Numero de parcelas fora de 1..99: {draft.InstallmentCount}.");
        }

        DateOnly firstStatementMonth = draft.FirstStatementMonth
            ?? InstallmentSchedule.StatementMonthFor(draft.PurchaseDate, draft.ClosingDay);

        firstStatementMonth = InstallmentSchedule.ToStatementMonth(firstStatementMonth);

        Money[] slices = financed.Allocate(draft.InstallmentCount, draft.RemainderPlacement);

        Guid planId = draft.Id ?? UuidV7.NewGuid();
        var installments = new List<Installment>(slices.Length);

        for (int index = 0; index < slices.Length; index++)
        {
            DateOnly statementMonth = firstStatementMonth.AddMonths(index);
            DateOnly dueDate = InstallmentSchedule.DueDateFor(
                statementMonth, draft.ClosingDay, draft.DueDay);

            installments.Add(new Installment(
                UuidV7.NewGuid(), planId, (short)(index + 1), slices[index], statementMonth, dueDate));
        }

        var plan = new InstallmentPlan(
            planId,
            draft.CardAccountId,
            draft.PurchaseTransactionId,
            draft.PayeeId,
            draft.Description.Trim(),
            draft.TotalAmount,
            financed,
            draft.PurchaseDate,
            firstStatementMonth,
            installments,
            draft.CreatedAt ?? DateTimeOffset.UtcNow);

        // Cinto e suspensorio. Allocate ja garante a soma exata; se um dia
        // deixar de garantir, o plano nao chega a existir.
        if (plan.ScheduledTotal != financed)
        {
            return new Error("installment.schedule_mismatch",
                $"Soma das parcelas ({plan.ScheduledTotal}) difere do financiado ({financed}).");
        }

        return plan;
    }

    /// <summary>
    /// A compra como ela entra no razao.
    ///
    /// Tres regras de uma vez:
    ///
    ///   o cartao deve o FINANCIADO, hoje. Nao o preco a vista, nao a
    ///   primeira parcela - o passivo e integral no ato da compra;
    ///
    ///   o produto entra pelo preco a vista, na categoria dele;
    ///
    ///   o juro, quando existe, entra em PERNA SEPARADA. Somar juro ao
    ///   produto esconderia quanto o parcelamento custou, que e justamente o
    ///   que alguem parcelando precisa enxergar.
    ///
    /// As pernas fecham em zero por construcao: -financiado + total + juro.
    /// </summary>
    public Result<Transaction> BuildPurchase(
        Guid expenseAccountId,
        Guid? categoryId = null,
        Guid? interestCategoryId = null)
    {
        var legs = new List<EntryDraft>
        {
            new(CardAccountId, FinancedAmount.Negated),
            new(expenseAccountId, TotalAmount) { CategoryId = categoryId },
        };

        if (!InterestAmount.IsZero)
        {
            legs.Add(new EntryDraft(expenseAccountId, InterestAmount)
            {
                CategoryId = interestCategoryId,
                Memo = "Juros do parcelamento",
            });
        }

        return Transaction.Create(new TransactionDraft
        {
            Id = PurchaseTransactionId,
            OccurredOn = PurchaseDate,
            Description = Description,
            Currency = FinancedAmount.Currency,
            Kind = TransactionKind.InstallmentPurchase,
            Notes = $"{InstallmentCount}x de {_installments[0].Amount}",
            Entries = legs,
        });
    }

    public Installment? FindBySequence(short sequence) =>
        _installments.FirstOrDefault(i => i.Sequence == sequence);

    public IReadOnlyList<Installment> ForStatementMonth(DateOnly month)
    {
        DateOnly normalized = InstallmentSchedule.ToStatementMonth(month);
        return _installments.Where(i => i.StatementMonth == normalized).ToList();
    }

    public override string ToString() =>
        $"{Description} - {InstallmentCount}x de {_installments[0].Amount} ({FinancedAmount} total)";
}

public sealed record InstallmentPlanDraft
{
    public Guid? Id { get; init; }
    public required Guid CardAccountId { get; init; }
    public required Guid PurchaseTransactionId { get; init; }
    public required string Description { get; init; }

    /// <summary>Preco a vista.</summary>
    public required Money TotalAmount { get; init; }

    public required int InstallmentCount { get; init; }
    public required DateOnly PurchaseDate { get; init; }
    public required int ClosingDay { get; init; }
    public required int DueDay { get; init; }

    /// <summary>Nulo em compra sem juros: assume o preco a vista.</summary>
    public Money? FinancedAmount { get; init; }

    public Guid? PayeeId { get; init; }

    /// <summary>Sobrescreve a competencia calculada a partir do fechamento.</summary>
    public DateOnly? FirstStatementMonth { get; init; }

    /// <summary>
    /// Onde cai o centavo residual. Varia por emissor no Brasil, entao e
    /// parametro. O que nao varia e a soma fechar exata.
    /// </summary>
    public RemainderPlacement RemainderPlacement { get; init; } = RemainderPlacement.First;

    public DateTimeOffset? CreatedAt { get; init; }
}
