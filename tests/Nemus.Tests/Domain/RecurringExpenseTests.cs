using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Nemus.Domain.Recurring;
using Xunit;

namespace Nemus.Tests.Domain;

public sealed class RecurringExpenseTests
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly Guid Category = Guid.Parse("00000000-0000-7000-8000-00000000c001");
    private static readonly DateOnly Start = new(2026, 1, 1);

    private static RecurringExpense Make(
        long minorUnits = 280_000, int dueDay = 10, bool isEstimate = false, DateOnly? endsOn = null) =>
        RecurringExpense.Create(
            "Aluguel", Category, Money.FromMinorUnits(minorUnits, Brl), dueDay, Start,
            isEstimate: isEstimate, endsOn: endsOn).Value;

    [Fact]
    public void Nome_e_aparado_e_obrigatorio()
    {
        Assert.Equal("Aluguel", RecurringExpense.Create(
            "  Aluguel  ", Category, Money.FromMinorUnits(100, Brl), 5, Start).Value.Name);

        Result<RecurringExpense> empty = RecurringExpense.Create(
            "   ", Category, Money.FromMinorUnits(100, Brl), 5, Start);

        Assert.True(empty.IsFailure);
        Assert.Equal("recurring.name_required", empty.Error.Code);
    }

    /// <summary>
    /// Ao contrario do envelope, onde o negativo e o coracao do metodo. Um
    /// gasto fixo de valor negativo seria uma receita disfarcada.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-280_000)]
    public void Valor_tem_que_ser_positivo(long minorUnits)
    {
        Result<RecurringExpense> result = RecurringExpense.Create(
            "Aluguel", Category, Money.FromMinorUnits(minorUnits, Brl), 10, Start);

        Assert.True(result.IsFailure);
        Assert.Equal("recurring.amount_positive", result.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(-3)]
    public void Dia_de_vencimento_vai_de_1_a_31(int dueDay)
    {
        Result<RecurringExpense> result = RecurringExpense.Create(
            "Internet", Category, Money.FromMinorUnits(10_000, Brl), dueDay, Start);

        Assert.True(result.IsFailure);
        Assert.Equal("recurring.due_day_range", result.Error.Code);
    }

    /// <summary>
    /// Dia 31 e como se diz "ultimo dia do mes". Recusa-lo obrigaria quem
    /// paga no ultimo dia a escolher o 28 e errar em dez meses do ano.
    /// </summary>
    [Theory]
    [InlineData(31, 2026, 2, 28)]
    [InlineData(31, 2024, 2, 29)]  // bissexto
    [InlineData(31, 2026, 4, 30)]
    [InlineData(31, 2026, 3, 31)]
    [InlineData(10, 2026, 2, 10)]
    public void Vencimento_e_aparado_para_o_mes_real(int dueDay, int year, int month, int expectedDay)
    {
        Assert.Equal(new DateOnly(year, month, expectedDay), Make(dueDay: dueDay).DueDateIn(year, month));
    }

    [Fact]
    public void Fora_do_periodo_o_gasto_nao_vale_no_mes()
    {
        RecurringExpense cancelled = RecurringExpense.Create(
            "Streaming", Category, Money.FromMinorUnits(5_590, Brl), 12,
            startsOn: new DateOnly(2026, 3, 1),
            endsOn: new DateOnly(2026, 6, 30)).Value;

        Assert.False(cancelled.IsActiveIn(2026, 2));
        Assert.True(cancelled.IsActiveIn(2026, 3));
        Assert.True(cancelled.IsActiveIn(2026, 6));
        Assert.False(cancelled.IsActiveIn(2026, 7));
    }

    /// <summary>
    /// O mes de inicio conta inteiro, mesmo comecando no dia 20: quem assinou
    /// dia 20 pagou naquele mes.
    /// </summary>
    [Fact]
    public void Mes_de_inicio_vale_mesmo_comecando_no_meio()
    {
        RecurringExpense e = RecurringExpense.Create(
            "Academia", Category, Money.FromMinorUnits(12_000, Brl), 5,
            startsOn: new DateOnly(2026, 5, 20)).Value;

        Assert.True(e.IsActiveIn(2026, 5));
        Assert.False(e.IsActiveIn(2026, 4));
    }

    [Fact]
    public void Arquivado_nao_vale_em_mes_nenhum()
    {
        RecurringExpense archived = RecurringExpense.Create(
            "Jornal", Category, Money.FromMinorUnits(3_000, Brl), 1, Start,
            archivedAt: DateTimeOffset.UtcNow).Value;

        Assert.True(archived.IsArchived);
        Assert.False(archived.IsActiveIn(2026, 1));
    }

    [Fact]
    public void Fim_antes_do_inicio_e_recusado()
    {
        Result<RecurringExpense> result = RecurringExpense.Create(
            "Aluguel", Category, Money.FromMinorUnits(280_000, Brl), 10,
            startsOn: new DateOnly(2026, 5, 1),
            endsOn: new DateOnly(2026, 4, 30));

        Assert.True(result.IsFailure);
        Assert.Equal("recurring.period_inverted", result.Error.Code);
    }
}
