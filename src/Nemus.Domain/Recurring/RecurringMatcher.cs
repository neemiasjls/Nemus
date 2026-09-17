namespace Nemus.Domain.Recurring;

/// <summary>Um gasto do razao que pode ser a chegada de um gasto fixo.</summary>
public sealed record SpendingCandidate(
    Guid TransactionId,
    Guid CategoryId,
    long AmountMinorUnits,
    DateOnly OccurredOn);

/// <summary>
/// O gasto fixo ja apareceu no razao neste mes?
///
/// "Matched", nao "pago": isto e um cruzamento por categoria e valor, nao uma
/// confirmacao de pagamento. A distincao importa na hora de escrever a tela -
/// dizer "pago" sobre um palpite seria mentir com confianca.
/// </summary>
public sealed record RecurringMatch(
    Guid RecurringExpenseId,
    Guid? TransactionId,
    long? ActualMinorUnits,
    DateOnly? OccurredOn)
{
    public bool IsMatched => TransactionId is not null;

    /// <summary>
    /// Quanto veio a mais (positivo) ou a menos (negativo) que o previsto.
    /// Nulo quando ainda nao veio - zero ali seria confundido com "veio e
    /// bateu exato".
    /// </summary>
    public long? DifferenceFrom(long expectedMinorUnits) =>
        ActualMinorUnits is null ? null : ActualMinorUnits.Value - expectedMinorUnits;
}

/// <summary>
/// Cruza a lista de gastos fixos com o que o razao registrou no mes.
///
/// POR QUE NAO BASTA OLHAR A CATEGORIA. Se "Casa" tem aluguel e condominio,
/// achar um gasto em Casa nao diz qual dos dois chegou. Entao o desempate e
/// o valor: dentro da categoria, cada gasto fixo fica com o lancamento de
/// valor mais proximo do previsto, e cada lancamento serve a no maximo um
/// gasto fixo. Dois pagamentos de aluguel no mesmo mes marcam um aluguel, nao
/// dois.
///
/// A TOLERANCIA E LARGA DE PROPOSITO. Ela existe para separar R$ 2.800 de
/// R$ 650, nao para auditar centavos. Apertada demais, um aluguel reajustado
/// apareceria como "ainda nao veio" - o erro mais irritante possivel, porque
/// acontece justamente no mes em que a conta mudou e voce esta olhando.
///
/// Tudo em inteiro: a proximidade e medida pela diferenca absoluta em
/// centavos, sem razao nem ponto flutuante em lugar nenhum.
/// </summary>
public static class RecurringMatcher
{
    /// <summary>Quanto o valor pode divergir do previsto, em porcentagem.</summary>
    private const long FixedTolerancePercent = 25;

    /// <summary>Estimado pode dobrar e ainda ser a mesma conta de luz.</summary>
    private const long EstimateTolerancePercent = 100;

    /// <summary>
    /// Folga absoluta, para valores pequenos: 25% de R$ 30 sao R$ 7,50, e uma
    /// assinatura que subiu R$ 8 nao deixou de ser a assinatura.
    /// </summary>
    private const long AbsoluteSlackMinorUnits = 2_000;

    public static IReadOnlyList<RecurringMatch> Match(
        IReadOnlyList<RecurringExpense> expenses,
        IReadOnlyList<SpendingCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(expenses);
        ArgumentNullException.ThrowIfNull(candidates);

        // Todos os pares plausiveis, do mais proximo ao mais distante. A data
        // desempata pares igualmente proximos para que o resultado nao dependa
        // da ordem em que as listas chegaram.
        var pairs = new List<(RecurringExpense Expense, SpendingCandidate Candidate, long Distance)>();

        foreach (RecurringExpense expense in expenses)
        {
            foreach (SpendingCandidate candidate in candidates)
            {
                if (candidate.CategoryId != expense.CategoryId)
                {
                    continue;
                }

                long distance = Math.Abs(candidate.AmountMinorUnits - expense.Amount.MinorUnits);

                if (WithinTolerance(expense, distance))
                {
                    pairs.Add((expense, candidate, distance));
                }
            }
        }

        pairs.Sort((a, b) =>
        {
            int byDistance = a.Distance.CompareTo(b.Distance);
            if (byDistance != 0) return byDistance;

            int byDate = a.Candidate.OccurredOn.CompareTo(b.Candidate.OccurredOn);
            return byDate != 0 ? byDate : a.Candidate.TransactionId.CompareTo(b.Candidate.TransactionId);
        });

        var matchedExpenses = new Dictionary<Guid, SpendingCandidate>();
        var claimedTransactions = new HashSet<Guid>();

        foreach ((RecurringExpense expense, SpendingCandidate candidate, _) in pairs)
        {
            if (matchedExpenses.ContainsKey(expense.Id) || !claimedTransactions.Add(candidate.TransactionId))
            {
                continue;
            }

            matchedExpenses[expense.Id] = candidate;
        }

        return expenses
            .Select(expense => matchedExpenses.TryGetValue(expense.Id, out SpendingCandidate? hit)
                ? new RecurringMatch(expense.Id, hit.TransactionId, hit.AmountMinorUnits, hit.OccurredOn)
                : new RecurringMatch(expense.Id, null, null, null))
            .ToList();
    }

    private static bool WithinTolerance(RecurringExpense expense, long distance)
    {
        if (distance <= AbsoluteSlackMinorUnits)
        {
            return true;
        }

        long percent = expense.IsEstimate ? EstimateTolerancePercent : FixedTolerancePercent;

        // distance / expected <= percent / 100, sem divisao: multiplicacao
        // cruzada mantem tudo em inteiro.
        return distance * 100 <= expense.Amount.MinorUnits * percent;
    }
}
