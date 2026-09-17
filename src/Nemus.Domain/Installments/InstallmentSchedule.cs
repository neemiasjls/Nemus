namespace Nemus.Domain.Installments;

/// <summary>
/// Em qual fatura uma compra cai, e quando essa fatura vence.
///
/// Regra brasileira comum: compra ate o dia do fechamento entra na fatura
/// do proprio mes; depois do fechamento, na do mes seguinte. O vencimento
/// e o dia de vencimento do mesmo mes quando ele vem depois do fechamento,
/// senao do mes seguinte.
///
/// Emissor tem variacao nisso (alguns usam data de processamento, nao data
/// da compra). Quando a fase 5 trouxer dado real do Pluggy, esta e a peca
/// que se ajusta - por isso ela e uma funcao pura e isolada.
/// </summary>
public static class InstallmentSchedule
{
    /// <summary>Competencia (dia 1) da fatura em que a compra cai.</summary>
    public static DateOnly StatementMonthFor(DateOnly purchaseDate, int closingDay)
    {
        EnsureDayOfMonth(closingDay, nameof(closingDay));

        int daysInMonth = DateTime.DaysInMonth(purchaseDate.Year, purchaseDate.Month);
        int effectiveClosing = Math.Min(closingDay, daysInMonth);

        var firstOfMonth = new DateOnly(purchaseDate.Year, purchaseDate.Month, 1);
        return purchaseDate.Day <= effectiveClosing ? firstOfMonth : firstOfMonth.AddMonths(1);
    }

    /// <summary>Vencimento da fatura de uma dada competencia.</summary>
    public static DateOnly DueDateFor(DateOnly statementMonth, int closingDay, int dueDay)
    {
        EnsureDayOfMonth(closingDay, nameof(closingDay));
        EnsureDayOfMonth(dueDay, nameof(dueDay));

        DateOnly target = dueDay > closingDay ? statementMonth : statementMonth.AddMonths(1);
        int daysInMonth = DateTime.DaysInMonth(target.Year, target.Month);

        // Fevereiro nao tem dia 30: o vencimento cai no ultimo dia do mes.
        return new DateOnly(target.Year, target.Month, Math.Min(dueDay, daysInMonth));
    }

    /// <summary>Normaliza qualquer data para o dia 1 do seu mes.</summary>
    public static DateOnly ToStatementMonth(DateOnly date) => new(date.Year, date.Month, 1);

    private static void EnsureDayOfMonth(int day, string parameterName)
    {
        if (day is < 1 or > 31)
        {
            throw new ArgumentOutOfRangeException(parameterName, day, "Dia do mes fora de 1..31.");
        }
    }
}
