using Nemus.Domain.Monetary;
using Nemus.Domain.Recurring;
using Xunit;

namespace Nemus.Tests.Domain;

/// <summary>
/// O cruzamento entre o previsto e o que o razao registrou.
///
/// O caso que da nome ao arquivo inteiro e o do aluguel e do condominio na
/// mesma categoria: olhar so a categoria nao diz qual dos dois chegou, e por
/// isso o valor desempata.
/// </summary>
public sealed class RecurringMatcherTests
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly Guid Home = Guid.Parse("00000000-0000-7000-8000-00000000c001");
    private static readonly Guid Utilities = Guid.Parse("00000000-0000-7000-8000-00000000c002");
    private static readonly DateOnly Day = new(2026, 5, 10);

    private static RecurringExpense Expense(
        string name, Guid category, long minorUnits, bool isEstimate = false) =>
        RecurringExpense.Create(
            name, category, Money.FromMinorUnits(minorUnits, Brl), 10, new DateOnly(2026, 1, 1),
            isEstimate: isEstimate).Value;

    private static SpendingCandidate Spent(Guid category, long minorUnits, int day = 10) =>
        new(Guid.NewGuid(), category, minorUnits, new DateOnly(2026, 5, day));

    [Fact]
    public void Sem_lancamento_nada_casa()
    {
        RecurringExpense rent = Expense("Aluguel", Home, 280_000);

        RecurringMatch match = Assert.Single(RecurringMatcher.Match([rent], []));

        Assert.False(match.IsMatched);
        Assert.Null(match.ActualMinorUnits);
        Assert.Null(match.DifferenceFrom(280_000));
    }

    [Fact]
    public void Valor_exato_casa()
    {
        RecurringExpense rent = Expense("Aluguel", Home, 280_000);
        SpendingCandidate paid = Spent(Home, 280_000);

        RecurringMatch match = Assert.Single(RecurringMatcher.Match([rent], [paid]));

        Assert.True(match.IsMatched);
        Assert.Equal(paid.TransactionId, match.TransactionId);
        Assert.Equal(280_000, match.ActualMinorUnits);
        Assert.Equal(0, match.DifferenceFrom(280_000));
    }

    /// <summary>
    /// O CASO QUE MOTIVA O DESEMPATE POR VALOR. Aluguel de R$ 2.800 e
    /// condominio de R$ 650 vivem na mesma categoria "Casa". Cada um tem que
    /// achar o seu, e nao o do outro.
    /// </summary>
    [Fact]
    public void Dois_gastos_na_mesma_categoria_acham_cada_um_o_seu()
    {
        RecurringExpense rent = Expense("Aluguel", Home, 280_000);
        RecurringExpense condo = Expense("Condominio", Home, 65_000);

        SpendingCandidate paidRent = Spent(Home, 280_000, day: 10);
        SpendingCandidate paidCondo = Spent(Home, 66_500, day: 12);

        IReadOnlyList<RecurringMatch> matches =
            RecurringMatcher.Match([rent, condo], [paidCondo, paidRent]);

        Assert.Equal(paidRent.TransactionId, matches.Single(m => m.RecurringExpenseId == rent.Id).TransactionId);
        Assert.Equal(paidCondo.TransactionId, matches.Single(m => m.RecurringExpenseId == condo.Id).TransactionId);
    }

    /// <summary>
    /// Um lancamento serve a no maximo um gasto fixo. Sem isso, dois gastos
    /// de valor parecido casariam com o mesmo pagamento e o mes pareceria
    /// resolvido com metade do dinheiro.
    /// </summary>
    [Fact]
    public void Um_lancamento_nao_serve_a_dois_gastos()
    {
        RecurringExpense first = Expense("Seguro A", Home, 10_000);
        RecurringExpense second = Expense("Seguro B", Home, 10_000);

        SpendingCandidate onlyOne = Spent(Home, 10_000);

        IReadOnlyList<RecurringMatch> matches = RecurringMatcher.Match([first, second], [onlyOne]);

        Assert.Single(matches, m => m.IsMatched);
        Assert.Single(matches, m => !m.IsMatched);
    }

    [Fact]
    public void Categoria_diferente_nunca_casa()
    {
        RecurringExpense rent = Expense("Aluguel", Home, 280_000);
        SpendingCandidate elsewhere = Spent(Utilities, 280_000);

        Assert.False(Assert.Single(RecurringMatcher.Match([rent], [elsewhere])).IsMatched);
    }

    /// <summary>
    /// Reajuste de aluguel nao pode virar "ainda nao veio" - e o erro mais
    /// irritante possivel, porque acontece justamente no mes em que a conta
    /// mudou e voce esta olhando para ela.
    /// </summary>
    [Fact]
    public void Reajuste_dentro_da_tolerancia_ainda_casa()
    {
        RecurringExpense rent = Expense("Aluguel", Home, 280_000);
        SpendingCandidate raised = Spent(Home, 294_000);  // +5%

        RecurringMatch match = Assert.Single(RecurringMatcher.Match([rent], [raised]));

        Assert.True(match.IsMatched);
        Assert.Equal(14_000, match.DifferenceFrom(280_000));
    }

    [Fact]
    public void Valor_distante_demais_nao_casa()
    {
        RecurringExpense rent = Expense("Aluguel", Home, 280_000);
        SpendingCandidate somethingElse = Spent(Home, 50_000);

        Assert.False(Assert.Single(RecurringMatcher.Match([rent], [somethingElse])).IsMatched);
    }

    /// <summary>
    /// Conta de luz que dobra continua sendo a conta de luz. Por isso o
    /// estimado tem tolerancia maior que o fixo - e o mesmo valor que o fixo
    /// recusaria.
    /// </summary>
    [Fact]
    public void Estimado_aceita_variacao_que_o_fixo_recusa()
    {
        RecurringExpense power = Expense("Energia", Utilities, 20_000, isEstimate: true);
        RecurringExpense gym = Expense("Academia", Utilities, 20_000);

        SpendingCandidate doubled = Spent(Utilities, 38_000);  // +90%

        Assert.True(Assert.Single(RecurringMatcher.Match([power], [doubled])).IsMatched);
        Assert.False(Assert.Single(RecurringMatcher.Match([gym], [doubled])).IsMatched);
    }

    /// <summary>
    /// Folga absoluta para valores pequenos: 25% de R$ 30 sao R$ 7,50, e uma
    /// assinatura que subiu R$ 8 nao deixou de ser a assinatura.
    /// </summary>
    [Fact]
    public void Assinatura_barata_tem_folga_em_reais()
    {
        RecurringExpense streaming = Expense("Streaming", Utilities, 3_000);
        SpendingCandidate raised = Spent(Utilities, 4_800);  // +60%, mas so R$ 18

        Assert.True(Assert.Single(RecurringMatcher.Match([streaming], [raised])).IsMatched);
    }

    /// <summary>
    /// Dois pagamentos no mesmo mes marcam UM aluguel. O segundo fica solto e
    /// aparece como gasto normal da categoria - que e o que ele e.
    /// </summary>
    [Fact]
    public void Pagamento_duplicado_marca_um_so()
    {
        RecurringExpense rent = Expense("Aluguel", Home, 280_000);

        IReadOnlyList<RecurringMatch> matches = RecurringMatcher.Match(
            [rent], [Spent(Home, 280_000, day: 5), Spent(Home, 280_000, day: 6)]);

        RecurringMatch match = Assert.Single(matches);
        Assert.True(match.IsMatched);
        Assert.Equal(new DateOnly(2026, 5, 5), match.OccurredOn);
    }

    /// <summary>
    /// O resultado nao pode depender da ordem em que as listas chegaram do
    /// banco - senao o mesmo mes mostraria coisas diferentes a cada leitura.
    /// </summary>
    [Fact]
    public void Ordem_da_entrada_nao_muda_o_resultado()
    {
        RecurringExpense rent = Expense("Aluguel", Home, 280_000);
        RecurringExpense condo = Expense("Condominio", Home, 65_000);

        SpendingCandidate a = Spent(Home, 279_000, day: 3);
        SpendingCandidate b = Spent(Home, 65_500, day: 8);

        IReadOnlyList<RecurringMatch> forward = RecurringMatcher.Match([rent, condo], [a, b]);
        IReadOnlyList<RecurringMatch> backward = RecurringMatcher.Match([condo, rent], [b, a]);

        Assert.Equal(
            forward.OrderBy(m => m.RecurringExpenseId).Select(m => (m.RecurringExpenseId, m.TransactionId)),
            backward.OrderBy(m => m.RecurringExpenseId).Select(m => (m.RecurringExpenseId, m.TransactionId)));
    }
}
