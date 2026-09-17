namespace Nemus.Domain.Ledger;

/// <summary>
/// Natureza da transacao. Nao muda a mecanica do razao - toda transacao
/// fecha em zero do mesmo jeito - mas define regra de forma e da as fases
/// seguintes um gancho para tratar cada caso.
/// </summary>
public enum TransactionKind
{
    /// <summary>Despesa ou receita contra o mundo externo.</summary>
    Standard = 0,

    /// <summary>Movimento entre duas contas internas. Nao e despesa nem receita.</summary>
    Transfer = 1,

    /// <summary>Saldo inicial da conta, contra a conta de patrimonio.</summary>
    OpeningBalance = 2,

    /// <summary>
    /// Fase 6. A compra parcelada inteira, lancada de uma vez contra o
    /// cartao: no dia da compra voce ja deve o valor cheio.
    /// </summary>
    InstallmentPurchase = 3,

    /// <summary>Fase 6. Pagamento da fatura: conta corrente para cartao.</summary>
    CardPayment = 4,
}

public static class TransactionKindExtensions
{
    public static string ToCode(this TransactionKind kind) => kind switch
    {
        TransactionKind.Standard => "STANDARD",
        TransactionKind.Transfer => "TRANSFER",
        TransactionKind.OpeningBalance => "OPENING_BALANCE",
        TransactionKind.InstallmentPurchase => "INSTALLMENT_PURCHASE",
        TransactionKind.CardPayment => "CARD_PAYMENT",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Natureza desconhecida."),
    };

    public static TransactionKind FromCode(string code) => code switch
    {
        "STANDARD" => TransactionKind.Standard,
        "TRANSFER" => TransactionKind.Transfer,
        "OPENING_BALANCE" => TransactionKind.OpeningBalance,
        "INSTALLMENT_PURCHASE" => TransactionKind.InstallmentPurchase,
        "CARD_PAYMENT" => TransactionKind.CardPayment,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Natureza desconhecida."),
    };
}
