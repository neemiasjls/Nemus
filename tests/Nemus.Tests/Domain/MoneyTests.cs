using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Xunit;

namespace Nemus.Tests.Domain;

public sealed class MoneyTests
{
    private static readonly Currency Brl = Currency.Brl;

    [Fact]
    public void FromUnits_converte_para_menor_unidade()
    {
        Money fifty = Money.FromUnits(50, Brl);
        Assert.Equal(5000, fifty.MinorUnits);
    }

    [Fact]
    public void Aritmetica_preserva_inteiro_exato()
    {
        Money a = Money.FromMinorUnits(1999, Brl);
        Money b = Money.FromMinorUnits(1, Brl);

        Assert.Equal(2000, (a + b).MinorUnits);
        Assert.Equal(1998, (a - b).MinorUnits);
        Assert.Equal(5997, (a * 3).MinorUnits);
        Assert.Equal(-1999, (-a).MinorUnits);
    }

    [Fact]
    public void Somar_moedas_diferentes_lanca()
    {
        Money real = Money.FromUnits(10, Currency.Brl);
        Money dolar = Money.FromUnits(10, Currency.Usd);

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => real + dolar);
        Assert.Contains("moedas diferentes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Estouro_lanca_em_vez_de_dar_a_volta()
    {
        Money maximo = Money.FromMinorUnits(long.MaxValue, Brl);
        Money umCentavo = Money.FromMinorUnits(1, Brl);

        // Num razao, saldo errado em silencio e pior que excecao.
        Assert.Throws<OverflowException>(() => maximo + umCentavo);
    }

    [Fact]
    public void Money_default_nao_participa_de_aritmetica()
    {
        Money indefinido = default;
        Money valido = Money.FromUnits(1, Brl);

        Assert.Throws<ArgumentException>(() => valido + indefinido);
    }

    [Theory]
    [InlineData("1234.56", 123456)]
    [InlineData("-1234.56", -123456)]
    [InlineData("0.01", 1)]
    [InlineData("0.10", 10)]
    [InlineData("10", 1000)]
    [InlineData("10.5", 1050)]
    [InlineData("1,234,567.89", 123456789)]
    [InlineData("+42.00", 4200)]
    public void ParseInvariant_le_formato_de_maquina(string text, long expected)
    {
        Result<Money> result = Money.ParseInvariant(text, Brl);

        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.Equal(expected, result.Value.MinorUnits);
    }

    [Theory]
    [InlineData("1.234,56", 123456)]
    [InlineData("-99,90", -9990)]
    [InlineData("0,05", 5)]
    public void ParsePtBr_le_formato_brasileiro(string text, long expected)
    {
        Result<Money> result = Money.ParsePtBr(text, Brl);

        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.Equal(expected, result.Value.MinorUnits);
    }

    [Fact]
    public void Parse_recusa_precisao_alem_da_moeda_em_vez_de_arredondar()
    {
        // Arredondar aqui e como a maioria dos sistemas perde centavo.
        Result<Money> result = Money.ParseInvariant("10.999", Brl);

        Assert.True(result.IsFailure);
        Assert.Equal("money.parse_precision", result.Error.Code);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("10.5.5")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("R$ 10,00")]
    public void Parse_recusa_lixo(string text)
    {
        Assert.True(Money.ParseInvariant(text, Brl).IsFailure);
    }

    [Fact]
    public void Formatacao_nao_passa_por_ponto_flutuante()
    {
        Assert.Equal("1234.56", Money.FromMinorUnits(123456, Brl).ToString());
        Assert.Equal("-1234.56", Money.FromMinorUnits(-123456, Brl).ToString());
        Assert.Equal("1.234,56", Money.FromMinorUnits(123456, Brl).ToPtBrString());
        Assert.Equal("0.00", Money.Zero(Brl).ToString());
    }

    [Fact]
    public void Formatacao_aguenta_o_extremo_de_bigint()
    {
        // Math.Abs(long.MinValue) estouraria; a formatacao usa magnitude
        // em ulong justamente por isso.
        string text = Money.FromMinorUnits(long.MinValue, Brl).ToString();
        Assert.Equal("-92233720368547758.08", text);
    }

    [Fact]
    public void Comparacao_ordena_por_valor()
    {
        Money dez = Money.FromUnits(10, Brl);
        Money vinte = Money.FromUnits(20, Brl);

        Assert.True(dez < vinte);
        Assert.True(vinte > dez);
        Assert.True(dez <= Money.FromUnits(10, Brl));
    }

    [Fact]
    public void Sum_de_sequencia_vazia_mantem_a_moeda()
    {
        Money total = Money.Sum([], Brl);

        Assert.Equal(0, total.MinorUnits);
        Assert.Equal(Brl, total.Currency);
    }
}
