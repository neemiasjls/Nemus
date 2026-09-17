using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;

namespace Nemus.Domain.Accounts;

/// <summary>
/// Fechamento e vencimento de um cartao.
///
/// Sem estes dois numeros nao da para dizer em qual fatura uma compra cai, e
/// sem isso parcelamento nao tem competencia - as 12 parcelas ficariam
/// penduradas em nenhum mes. Por isso a fase 6 exige que o cartao tenha
/// condicoes antes de aceitar uma compra parcelada.
///
/// Mora numa tabela separada de accounts, e nao em colunas nulaveis, porque
/// so vale para passivo: em conta corrente as duas colunas seriam lixo
/// permanente. A FK composta da migration 002 prova, de forma declarativa,
/// que a conta e mesmo um LIABILITY.
/// </summary>
public sealed class CreditCardTerms
{
    private CreditCardTerms(
        Guid accountId, int closingDay, int dueDay, Money? creditLimit, Guid? paymentAccountId)
    {
        AccountId = accountId;
        ClosingDay = closingDay;
        DueDay = dueDay;
        CreditLimit = creditLimit;
        PaymentAccountId = paymentAccountId;
    }

    public Guid AccountId { get; }

    /// <summary>Dia em que a fatura fecha. Compra depois dele cai na fatura seguinte.</summary>
    public int ClosingDay { get; }

    /// <summary>Dia do vencimento.</summary>
    public int DueDay { get; }

    /// <summary>Limite, quando informado. Nao e regra: o Nemus nao impede de estourar.</summary>
    public Money? CreditLimit { get; }

    /// <summary>Conta de onde a fatura costuma ser paga. Semente do fluxo da fase 7.</summary>
    public Guid? PaymentAccountId { get; }

    public static Result<CreditCardTerms> Create(
        Guid accountId,
        int closingDay,
        int dueDay,
        Money? creditLimit = null,
        Guid? paymentAccountId = null)
    {
        if (accountId == Guid.Empty)
        {
            return new Error("card.account_required", "Informe o cartao.");
        }

        if (closingDay is < 1 or > 31)
        {
            return new Error("card.closing_day_invalid", "O dia de fechamento vai de 1 a 31.");
        }

        if (dueDay is < 1 or > 31)
        {
            return new Error("card.due_day_invalid", "O dia de vencimento vai de 1 a 31.");
        }

        if (creditLimit is { IsNegative: true })
        {
            return new Error("card.limit_negative", "O limite nao pode ser negativo.");
        }

        if (paymentAccountId == accountId)
        {
            return new Error(
                "card.payment_account_is_the_card",
                "A fatura nao se paga com o proprio cartao.");
        }

        return new CreditCardTerms(accountId, closingDay, dueDay, creditLimit, paymentAccountId);
    }

    /// <summary>
    /// Quantos dias, no maximo, uma compra fica "de graca" neste cartao -
    /// comprar logo depois do fechamento e o que estica mais o prazo. Serve
    /// para explicar a diferenca entre fechamento e vencimento a quem usa.
    /// </summary>
    public int MaxFreeDays => DueDay > ClosingDay ? DueDay - ClosingDay + 30 : DueDay - ClosingDay + 60;
}
