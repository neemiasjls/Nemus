using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;

namespace Nemus.Domain.Installments;

/// <summary>
/// Uma parcela do cronograma. Nao e lancamento do razao: o razao ja
/// registrou a compra inteira no ato. Isto e compromisso previsto contra
/// uma competencia futura.
///
/// Tem identidade externa propria porque o banco manda as parcelas como
/// linhas separadas do extrato do cartao (com formato que varia por
/// instituicao - "IFOOD 03/12" e uma delas). Na fase 6 o importador vai
/// reconciliar a linha recebida contra a parcela ja prevista, em vez de
/// criar transacao nova - e sem external_id por parcela isso nao fecha.
/// </summary>
public sealed class Installment
{
    internal Installment(
        Guid id,
        Guid planId,
        short sequence,
        Money amount,
        DateOnly statementMonth,
        DateOnly dueDate)
    {
        Id = id;
        PlanId = planId;
        Sequence = sequence;
        Amount = amount;
        StatementMonth = statementMonth;
        DueDate = dueDate;
    }

    public Guid Id { get; }
    public Guid PlanId { get; }

    /// <summary>1 a N. O "03" de "03/12".</summary>
    public short Sequence { get; }

    public Money Amount { get; }

    /// <summary>Competencia da fatura, sempre dia 1.</summary>
    public DateOnly StatementMonth { get; }

    public DateOnly DueDate { get; }

    /// <summary>
    /// Transacao que liquidou esta parcela, quando ela ja apareceu em
    /// fatura fechada e foi reconciliada. Nulo = compromisso futuro.
    /// </summary>
    public Guid? SettledTransactionId { get; private set; }

    public ExternalReference? External { get; private set; }

    public bool IsSettled => SettledTransactionId.HasValue;

    public void Settle(Guid transactionId) => SettledTransactionId = transactionId;

    public void LinkExternal(ExternalReference external) => External = external;

    public override string ToString() =>
        $"{Sequence:00} {Amount} venc. {DueDate:yyyy-MM-dd}";
}
