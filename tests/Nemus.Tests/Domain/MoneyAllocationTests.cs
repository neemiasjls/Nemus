using Nemus.Domain.Monetary;
using Xunit;

namespace Nemus.Tests.Domain;

public sealed class MoneyAllocationTests
{
    private static readonly Currency Brl = Currency.Brl;

    [Fact]
    public void Cem_reais_em_tres_nao_perde_o_centavo()
    {
        Money[] parcelas = Money.FromUnits(100, Brl).Allocate(3);

        Assert.Equal(3, parcelas.Length);
        Assert.Equal([3334L, 3333L, 3333L], parcelas.Select(p => p.MinorUnits));
        Assert.Equal(10000, Money.Sum(parcelas, Brl).MinorUnits);
    }

    [Fact]
    public void Residual_no_fim_quando_pedido()
    {
        Money[] parcelas = Money.FromUnits(100, Brl).Allocate(3, RemainderPlacement.Last);

        Assert.Equal([3333L, 3333L, 3334L], parcelas.Select(p => p.MinorUnits));
        Assert.Equal(10000, Money.Sum(parcelas, Brl).MinorUnits);
    }

    [Theory]
    [MemberData(nameof(CasosDeReparticao))]
    public void Soma_das_partes_e_sempre_exatamente_o_todo(long minorUnits, int parts)
    {
        Money original = Money.FromMinorUnits(minorUnits, Brl);

        foreach (RemainderPlacement placement in Enum.GetValues<RemainderPlacement>())
        {
            Money[] slices = original.Allocate(parts, placement);

            Assert.Equal(parts, slices.Length);
            Assert.Equal(
                original.MinorUnits,
                Money.Sum(slices, Brl).MinorUnits);

            // Nenhuma parte difere de outra por mais de um centavo.
            long menor = slices.Min(s => s.MinorUnits);
            long maior = slices.Max(s => s.MinorUnits);
            Assert.True(maior - menor <= 1, $"Diferenca de {maior - menor} entre parcelas.");
        }
    }

    public static TheoryData<long, int> CasosDeReparticao()
    {
        var data = new TheoryData<long, int>();

        long[] valores = [1, 2, 7, 100, 9999, 10000, 100000, 123457, -100, -9999, -1, 0];
        int[] partes = [1, 2, 3, 4, 6, 7, 10, 12, 18, 24, 99];

        foreach (long valor in valores)
        {
            foreach (int parte in partes)
            {
                data.Add(valor, parte);
            }
        }

        return data;
    }

    [Fact]
    public void Reparticao_exaustiva_de_um_a_mil_centavos_em_ate_doze_parcelas()
    {
        // Varredura completa da faixa onde o residual aparece.
        for (long centavos = 1; centavos <= 1000; centavos++)
        {
            Money valor = Money.FromMinorUnits(centavos, Brl);

            for (int parcelas = 1; parcelas <= 12; parcelas++)
            {
                Money[] slices = valor.Allocate(parcelas);
                long soma = Money.Sum(slices, Brl).MinorUnits;

                Assert.True(
                    soma == centavos,
                    $"{centavos} centavos em {parcelas}x somou {soma}.");
            }
        }
    }

    [Fact]
    public void Reparticao_com_zero_partes_lanca()
    {
        Money valor = Money.FromUnits(10, Brl);

        Assert.Throws<ArgumentOutOfRangeException>(() => valor.Allocate(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => valor.Allocate(-1));
    }

    [Fact]
    public void Valor_negativo_reparte_mantendo_o_sinal()
    {
        Money[] slices = Money.FromUnits(-100, Brl).Allocate(3);

        Assert.All(slices, s => Assert.True(s.IsNegative));
        Assert.Equal(-10000, Money.Sum(slices, Brl).MinorUnits);
    }
}
