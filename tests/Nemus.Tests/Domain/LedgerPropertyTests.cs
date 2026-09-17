using Nemus.Domain.Accounts;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Xunit;

namespace Nemus.Tests.Domain;

/// <summary>
/// TESTE (a) - a soma de todos os saldos permanece consistente apos N
/// operacoes aleatorias.
///
/// "Consistente" aqui e a versao forte e verificavel: a soma de TODOS os
/// saldos e exatamente zero, sempre. Isso so e possivel porque a contraparte
/// de uma despesa e uma conta de verdade (REVENUE/EXPENSE) e nao um buraco
/// por onde o dinheiro sai do sistema. Se a despesa "sumisse", a soma nao
/// fecharia e o teste viraria tautologia.
///
/// Corolario que tambem e verificado: a soma das contas internas e o inverso
/// exato da soma das externas. E o que prova que nenhum centavo nasceu nem
/// evaporou no meio do caminho.
/// </summary>
public sealed class LedgerPropertyTests
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly DateOnly Base = new(2026, 1, 1);

    public static TheoryData<int> Seeds()
    {
        var data = new TheoryData<int>();
        for (int seed = 1; seed <= 50; seed++)
        {
            data.Add(seed);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Soma_de_todos_os_saldos_permanece_zero(int seed)
    {
        RunSimulation(seed, operations: 200);
    }

    [Fact]
    public void Simulacao_longa_de_cinco_mil_operacoes()
    {
        RunSimulation(seed: 20260907, operations: 5000);
    }

    private static void RunSimulation(int seed, int operations)
    {
        var rng = new Random(seed);
        var simulator = new LedgerSimulator();

        // Mistura de contas internas e externas, como no banco de verdade.
        var accounts = new List<Guid>();
        AccountType[] layout =
        [
            AccountType.Asset, AccountType.Asset, AccountType.Asset,
            AccountType.Liability, AccountType.Liability,
            AccountType.Equity,
            AccountType.Expense, AccountType.Expense,
            AccountType.Revenue,
        ];

        foreach (AccountType type in layout)
        {
            Guid id = UuidV7.NewGuid();
            simulator.RegisterAccount(id, type);
            accounts.Add(id);
        }

        int applied = 0;
        int rejected = 0;
        int deleted = 0;

        for (int step = 0; step < operations; step++)
        {
            int roll = rng.Next(100);

            if (roll < 15)
            {
                // Tentativa invalida: precisa ser recusada E nao pode mover
                // saldo nenhum.
                IReadOnlyDictionary<Guid, long> before = simulator.Snapshot();

                Result<Transaction> result = Transaction.Create(
                    LedgerGenerator.Unbalanced(rng, accounts, Brl, Base));

                Assert.True(
                    result.IsFailure,
                    $"[seed {seed}, passo {step}] transacao invalida foi aceita.");

                IReadOnlyDictionary<Guid, long> after = simulator.Snapshot();
                Assert.Equal(before, after);
                rejected++;
            }
            else if (roll < 25 && simulator.TransactionCount > 0)
            {
                Transaction victim = simulator.Live[rng.Next(simulator.TransactionCount)];
                simulator.SoftDelete(victim);
                deleted++;
            }
            else
            {
                Result<Transaction> result = Transaction.Create(
                    LedgerGenerator.Balanced(rng, accounts, Brl, Base));

                Assert.True(
                    result.IsSuccess,
                    $"[seed {seed}, passo {step}] transacao valida recusada: {result.Error}");

                Transaction transaction = result.Value;

                Assert.True(
                    transaction.Balance.IsZero,
                    $"[seed {seed}, passo {step}] transacao construida nao fecha em zero.");

                simulator.Apply(transaction);
                applied++;
            }

            // As invariantes, verificadas a cada passo e nao so no fim.
            Assert.True(
                simulator.SumOfAllBalances == 0,
                $"[seed {seed}, passo {step}] soma de todos os saldos = "
                + $"{simulator.SumOfAllBalances}, deveria ser 0.");

            Assert.True(
                simulator.SumInternal == -simulator.SumExternal,
                $"[seed {seed}, passo {step}] interno {simulator.SumInternal} nao e o inverso "
                + $"de externo {simulator.SumExternal}.");
        }

        // Sanidade do proprio teste: se nada tivesse acontecido, a soma
        // tambem daria zero e o teste nao provaria coisa alguma.
        Assert.True(applied > 0, $"[seed {seed}] nenhuma transacao aplicada.");
        Assert.True(rejected > 0, $"[seed {seed}] nenhuma tentativa invalida exercitada.");
        Assert.True(deleted > 0, $"[seed {seed}] nenhuma exclusao exercitada.");
        Assert.NotEqual(0, simulator.SumInternal);
    }

    [Fact]
    public void Toda_transacao_gerada_fecha_em_zero_por_construcao()
    {
        var rng = new Random(42);
        var accounts = Enumerable.Range(0, 6).Select(_ => UuidV7.NewGuid()).ToList();

        for (int i = 0; i < 2000; i++)
        {
            TransactionDraft draft = LedgerGenerator.Balanced(rng, accounts, Brl, Base);
            long sum = draft.Entries.Sum(entry => entry.Amount.MinorUnits);

            Assert.Equal(0, sum);
            Assert.DoesNotContain(draft.Entries, entry => entry.Amount.IsZero);
        }
    }

    [Fact]
    public void Toda_transacao_invalida_gerada_e_recusada()
    {
        var rng = new Random(7);
        var accounts = Enumerable.Range(0, 6).Select(_ => UuidV7.NewGuid()).ToList();

        for (int i = 0; i < 2000; i++)
        {
            TransactionDraft draft = LedgerGenerator.Unbalanced(rng, accounts, Brl, Base);
            Result<Transaction> result = Transaction.Create(draft);

            Assert.True(result.IsFailure, $"Iteracao {i}: invalida foi aceita.");
            Assert.Contains(
                result.Error.Code,
                new[] { "ledger.unbalanced", "ledger.zero_amount_entry", "ledger.overflow" },
                StringComparer.Ordinal);
        }
    }
}
