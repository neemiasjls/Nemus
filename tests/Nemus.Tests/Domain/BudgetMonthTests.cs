using Nemus.Domain.Budgeting;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Xunit;

namespace Nemus.Tests.Domain;

public sealed class BudgetMonthTests
{
    [Theory]
    [InlineData("2026-09", 2026, 9)]
    [InlineData("2026-01", 2026, 1)]
    [InlineData(" 2026-12 ", 2026, 12)]
    public void Le_mes_no_formato_ano_mes(string text, int year, int month)
    {
        BudgetMonth parsed = BudgetMonth.Parse(text).Value;

        Assert.Equal(year, parsed.Year);
        Assert.Equal(month, parsed.Month);
        Assert.Equal(new DateOnly(year, month, 1), parsed.FirstDay);
    }

    /// <summary>
    /// A rota da API recebe o mes como texto. Aceitar "2026-9" ou uma data
    /// completa abriria duas grafias para o mesmo mes, e o dia viraria um
    /// dado que ninguem usa.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("2026-9")]
    [InlineData("2026-13")]
    [InlineData("2026-00")]
    [InlineData("2026-09-15")]
    [InlineData("09-2026")]
    [InlineData("setembro")]
    public void Recusa_qualquer_outra_grafia(string text)
    {
        Result<BudgetMonth> parsed = BudgetMonth.Parse(text);

        Assert.True(parsed.IsFailure, $"\"{text}\" deveria ser recusado.");
        Assert.Equal("budget.month_invalid", parsed.Error.Code);
    }

    [Fact]
    public void Vizinhos_atravessam_a_virada_do_ano()
    {
        BudgetMonth december = BudgetMonth.Parse("2025-12").Value;
        BudgetMonth january = BudgetMonth.Parse("2026-01").Value;

        Assert.Equal(january, december.Next);
        Assert.Equal(december, january.Previous);
        Assert.Equal(new DateOnly(2026, 1, 1), december.NextFirstDay);
    }

    [Fact]
    public void Ordena_por_ano_e_depois_por_mes()
    {
        BudgetMonth lateLastYear = BudgetMonth.Parse("2025-11").Value;
        BudgetMonth earlyThisYear = BudgetMonth.Parse("2026-02").Value;

        Assert.True(lateLastYear < earlyThisYear);
        Assert.True(earlyThisYear >= lateLastYear);
    }

    [Fact]
    public void Contem_os_dias_do_proprio_mes_e_so_eles()
    {
        BudgetMonth february = BudgetMonth.Parse("2026-02").Value;

        Assert.True(february.Contains(new DateOnly(2026, 2, 1)));
        Assert.True(february.Contains(new DateOnly(2026, 2, 28)));
        Assert.False(february.Contains(new DateOnly(2026, 3, 1)));
        Assert.False(february.Contains(new DateOnly(2025, 2, 10)));
    }

    [Fact]
    public void Texto_volta_ao_formato_da_rota()
    {
        Assert.Equal("2026-03", BudgetMonth.Of(new DateOnly(2026, 3, 27)).ToString());
    }
}

public sealed class BudgetAssignmentTests
{
    private static readonly BudgetMonth September = BudgetMonth.Parse("2026-09").Value;

    /// <summary>Tirar dinheiro de um envelope e o metodo funcionando, nao erro.</summary>
    [Fact]
    public void Aceita_valor_negativo()
    {
        Result<BudgetAssignment> created = BudgetAssignment.Create(
            Guid.NewGuid(), September, Money.FromUnits(-80, Currency.Brl));

        Assert.True(created.IsSuccess);
        Assert.False(created.Value.ClearsAssignment);
    }

    [Fact]
    public void Zero_significa_limpar_a_atribuicao()
    {
        BudgetAssignment created = BudgetAssignment.Create(
            Guid.NewGuid(), September, Money.Zero(Currency.Brl)).Value;

        Assert.True(created.ClearsAssignment);
    }

    [Fact]
    public void Exige_categoria()
    {
        Result<BudgetAssignment> created = BudgetAssignment.Create(
            Guid.Empty, September, Money.FromUnits(10, Currency.Brl));

        Assert.Equal("budget.category_required", created.Error.Code);
    }

    [Theory]
    [InlineData(BudgetAssignment.MaxMagnitudeMinorUnits + 1)]
    [InlineData(-BudgetAssignment.MaxMagnitudeMinorUnits - 1)]
    public void Recusa_valor_absurdo(long minorUnits)
    {
        Result<BudgetAssignment> created = BudgetAssignment.Create(
            Guid.NewGuid(), September, Money.FromMinorUnits(minorUnits, Currency.Brl));

        Assert.Equal("budget.amount_out_of_range", created.Error.Code);
    }

    [Fact]
    public void Aceita_exatamente_o_teto()
    {
        Result<BudgetAssignment> created = BudgetAssignment.Create(
            Guid.NewGuid(), September,
            Money.FromMinorUnits(BudgetAssignment.MaxMagnitudeMinorUnits, Currency.Brl));

        Assert.True(created.IsSuccess);
    }
}
