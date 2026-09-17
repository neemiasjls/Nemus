using Nemus.Domain.Installments;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Xunit;

namespace Nemus.Tests.Domain;

/// <summary>
/// Estrutura da fase 6, verificada desde ja no que ela tem de invariante:
/// a soma das parcelas e exatamente o valor financiado, e a competencia de
/// cada parcela respeita o fechamento do cartao.
/// </summary>
public sealed class InstallmentPlanTests
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly Guid Cartao = Guid.NewGuid();
    private static readonly Guid Compra = Guid.NewGuid();

    private static InstallmentPlanDraft Draft(
        long totalMinorUnits, int count, DateOnly purchaseDate, int closingDay = 20, int dueDay = 5) =>
        new()
        {
            CardAccountId = Cartao,
            PurchaseTransactionId = Compra,
            Description = "Notebook",
            TotalAmount = Money.FromMinorUnits(totalMinorUnits, Brl),
            InstallmentCount = count,
            PurchaseDate = purchaseDate,
            ClosingDay = closingDay,
            DueDay = dueDay,
        };

    [Fact]
    public void Doze_vezes_sem_juros_gera_doze_parcelas_somando_o_total()
    {
        Result<InstallmentPlan> result =
            InstallmentPlan.Create(Draft(120000, 12, new DateOnly(2026, 3, 10)));

        Assert.True(result.IsSuccess, result.Error.ToString());

        InstallmentPlan plan = result.Value;
        Assert.Equal(12, plan.InstallmentCount);
        Assert.True(plan.IsInterestFree);
        Assert.Equal(120000, plan.ScheduledTotal.MinorUnits);
        Assert.All(plan.Installments, i => Assert.Equal(10000, i.Amount.MinorUnits));
    }

    [Fact]
    public void Cem_reais_em_tres_nao_perde_centavo()
    {
        InstallmentPlan plan = InstallmentPlan.Create(Draft(10000, 3, new DateOnly(2026, 3, 10))).Value;

        Assert.Equal([3334L, 3333L, 3333L], plan.Installments.Select(i => i.Amount.MinorUnits));
        Assert.Equal(10000, plan.ScheduledTotal.MinorUnits);
        Assert.Equal(plan.FinancedAmount, plan.ScheduledTotal);
    }

    [Theory]
    [InlineData(10000, 3)]
    [InlineData(99999, 7)]
    [InlineData(1, 12)]
    [InlineData(123457, 18)]
    [InlineData(500000, 24)]
    [InlineData(7, 99)]
    public void Soma_das_parcelas_e_sempre_exatamente_o_financiado(long total, int count)
    {
        InstallmentPlan plan = InstallmentPlan.Create(Draft(total, count, new DateOnly(2026, 3, 10))).Value;

        Assert.Equal(count, plan.InstallmentCount);
        Assert.Equal(total, plan.ScheduledTotal.MinorUnits);
    }

    [Fact]
    public void Com_juros_o_financiado_supera_o_preco_a_vista()
    {
        var draft = Draft(100000, 10, new DateOnly(2026, 3, 10)) with
        {
            FinancedAmount = Money.FromMinorUnits(115000, Brl),
        };

        InstallmentPlan plan = InstallmentPlan.Create(draft).Value;

        Assert.False(plan.IsInterestFree);
        Assert.Equal(15000, plan.InterestAmount.MinorUnits);
        Assert.Equal(115000, plan.ScheduledTotal.MinorUnits);
        Assert.All(plan.Installments, i => Assert.Equal(11500, i.Amount.MinorUnits));
    }

    [Fact]
    public void Financiado_menor_que_o_total_e_rejeitado()
    {
        var draft = Draft(100000, 10, new DateOnly(2026, 3, 10)) with
        {
            FinancedAmount = Money.FromMinorUnits(90000, Brl),
        };

        Result<InstallmentPlan> result = InstallmentPlan.Create(draft);

        Assert.True(result.IsFailure);
        Assert.Equal("installment.financed_below_total", result.Error.Code);
    }

    [Fact]
    public void Competencias_avancam_um_mes_por_parcela()
    {
        // Compra dia 10, fechamento dia 20: entra na fatura do proprio mes.
        InstallmentPlan plan = InstallmentPlan.Create(
            Draft(30000, 3, new DateOnly(2026, 3, 10), closingDay: 20, dueDay: 5)).Value;

        Assert.Equal(new DateOnly(2026, 3, 1), plan.Installments[0].StatementMonth);
        Assert.Equal(new DateOnly(2026, 4, 1), plan.Installments[1].StatementMonth);
        Assert.Equal(new DateOnly(2026, 5, 1), plan.Installments[2].StatementMonth);
    }

    [Fact]
    public void Compra_depois_do_fechamento_cai_na_fatura_seguinte()
    {
        // Compra dia 25, fechamento dia 20: ja perdeu a fatura do mes.
        InstallmentPlan plan = InstallmentPlan.Create(
            Draft(30000, 3, new DateOnly(2026, 3, 25), closingDay: 20, dueDay: 5)).Value;

        Assert.Equal(new DateOnly(2026, 4, 1), plan.Installments[0].StatementMonth);
    }

    [Fact]
    public void Vencimento_anterior_ao_fechamento_cai_no_mes_seguinte()
    {
        // Fecha dia 20, vence dia 5: o dia 5 relevante e o do mes seguinte.
        InstallmentPlan plan = InstallmentPlan.Create(
            Draft(30000, 1, new DateOnly(2026, 3, 10), closingDay: 20, dueDay: 5)).Value;

        Assert.Equal(new DateOnly(2026, 4, 5), plan.Installments[0].DueDate);
    }

    [Fact]
    public void Vencimento_dia_trinta_em_fevereiro_cai_no_ultimo_dia()
    {
        InstallmentPlan plan = InstallmentPlan.Create(
            Draft(30000, 1, new DateOnly(2026, 1, 10), closingDay: 20, dueDay: 30)).Value;

        // Janeiro fecha dia 20, vence dia 30 do proprio mes.
        Assert.Equal(new DateOnly(2026, 1, 30), plan.Installments[0].DueDate);

        InstallmentPlan fevereiro = InstallmentPlan.Create(
            Draft(30000, 1, new DateOnly(2026, 2, 10), closingDay: 20, dueDay: 30)).Value;

        Assert.Equal(new DateOnly(2026, 2, 28), fevereiro.Installments[0].DueDate);
    }

    [Fact]
    public void Sequencias_sao_um_ate_n()
    {
        InstallmentPlan plan = InstallmentPlan.Create(Draft(120000, 12, new DateOnly(2026, 3, 10))).Value;

        Assert.Equal(
            Enumerable.Range(1, 12).Select(i => (short)i),
            plan.Installments.Select(i => i.Sequence));
    }

    [Fact]
    public void Parcela_nasce_como_compromisso_futuro_nao_liquidado()
    {
        InstallmentPlan plan = InstallmentPlan.Create(Draft(120000, 12, new DateOnly(2026, 3, 10))).Value;

        Assert.All(plan.Installments, i => Assert.False(i.IsSettled));
        Assert.Equal(120000, plan.OutstandingAmount.MinorUnits);

        plan.Installments[0].Settle(Guid.NewGuid());
        Assert.Equal(110000, plan.OutstandingAmount.MinorUnits);
    }

    [Fact]
    public void Numero_de_parcelas_fora_da_faixa_e_rejeitado()
    {
        Assert.Equal(
            "installment.count_out_of_range",
            InstallmentPlan.Create(Draft(10000, 0, new DateOnly(2026, 3, 10))).Error.Code);

        Assert.Equal(
            "installment.count_out_of_range",
            InstallmentPlan.Create(Draft(10000, 100, new DateOnly(2026, 3, 10))).Error.Code);
    }
}
