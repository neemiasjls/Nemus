using Nemus.Domain.Accounts;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;
using Xunit;

namespace Nemus.Tests.Domain;

/// <summary>
/// TESTE (b) - transacao desbalanceada e rejeitada.
///
/// A parte que interessa nao e so "Create devolve erro". E que, depois de
/// construida, nao existe caminho na API publica que leve uma Transaction a
/// um estado desbalanceado: nao ha AddEntry, nao ha setter de valor, e
/// ReplaceEntries revalida antes de trocar qualquer coisa.
/// </summary>
public sealed class TransactionInvariantTests
{
    private static readonly Currency Brl = Currency.Brl;
    private static readonly Guid Corrente = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Poupanca = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Externa = SystemAccounts.ExternalExpenses;

    private static readonly DateOnly Hoje = new(2026, 3, 15);

    [Fact]
    public void Soma_diferente_de_zero_e_rejeitada()
    {
        Result<Transaction> result = Transaction.Create(new TransactionDraft
        {
            OccurredOn = Hoje,
            Description = "Compra torta",
            Currency = Brl,
            Entries =
            [
                new EntryDraft(Corrente, Money.FromMinorUnits(-10000, Brl)),
                new EntryDraft(Externa, Money.FromMinorUnits(9999, Brl)),
            ],
        });

        Assert.True(result.IsFailure);
        Assert.Equal("ledger.unbalanced", result.Error.Code);
        Assert.Contains("-1", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Uma_perna_so_e_rejeitada()
    {
        Result<Transaction> result = Transaction.Create(new TransactionDraft
        {
            OccurredOn = Hoje,
            Description = "Dinheiro do nada",
            Currency = Brl,
            Entries = [new EntryDraft(Corrente, Money.FromMinorUnits(10000, Brl))],
        });

        Assert.True(result.IsFailure);
        Assert.Equal("ledger.too_few_entries", result.Error.Code);
    }

    [Fact]
    public void Nenhuma_perna_e_rejeitada()
    {
        Result<Transaction> result = Transaction.Create(new TransactionDraft
        {
            OccurredOn = Hoje,
            Description = "Vazia",
            Currency = Brl,
            Entries = [],
        });

        Assert.True(result.IsFailure);
        Assert.Equal("ledger.too_few_entries", result.Error.Code);
    }

    [Fact]
    public void Perna_com_valor_zero_e_rejeitada()
    {
        // Um par de zeros somaria zero e passaria numa checagem ingenua.
        Result<Transaction> result = Transaction.Create(new TransactionDraft
        {
            OccurredOn = Hoje,
            Description = "Pernas vazias",
            Currency = Brl,
            Entries =
            [
                new EntryDraft(Corrente, Money.FromMinorUnits(0, Brl)),
                new EntryDraft(Externa, Money.FromMinorUnits(0, Brl)),
            ],
        });

        Assert.True(result.IsFailure);
        Assert.Equal("ledger.zero_amount_entry", result.Error.Code);
    }

    [Fact]
    public void Mistura_de_moedas_e_rejeitada()
    {
        Result<Transaction> result = Transaction.Create(new TransactionDraft
        {
            OccurredOn = Hoje,
            Description = "Cambio disfarcado",
            Currency = Brl,
            Entries =
            [
                new EntryDraft(Corrente, Money.FromMinorUnits(-10000, Brl)),
                new EntryDraft(Externa, Money.FromMinorUnits(10000, Currency.Usd)),
            ],
        });

        Assert.True(result.IsFailure);
        Assert.Equal("ledger.currency_mismatch", result.Error.Code);
    }

    [Fact]
    public void Descricao_vazia_e_rejeitada()
    {
        Result<Transaction> result = Transaction.Create(new TransactionDraft
        {
            OccurredOn = Hoje,
            Description = "   ",
            Currency = Brl,
            Entries =
            [
                new EntryDraft(Corrente, Money.FromMinorUnits(-10000, Brl)),
                new EntryDraft(Externa, Money.FromMinorUnits(10000, Brl)),
            ],
        });

        Assert.True(result.IsFailure);
        Assert.Equal("ledger.description_required", result.Error.Code);
    }

    [Fact]
    public void Transacao_valida_fecha_em_zero()
    {
        Result<Transaction> result = Transaction.Spend(
            Hoje, "Mercado", Corrente, Externa, Money.FromUnits(150, Brl));

        Assert.True(result.IsSuccess, result.Error.ToString());

        Transaction transaction = result.Value;
        Assert.True(transaction.Balance.IsZero);
        Assert.Equal(2, transaction.Entries.Count);
        Assert.Equal(-15000, transaction.Entries[0].Amount.MinorUnits);
        Assert.Equal(15000, transaction.Entries[1].Amount.MinorUnits);
    }

    [Fact]
    public void Compra_dividida_fecha_em_zero_com_tres_pernas()
    {
        Guid mercado = Guid.NewGuid();
        Guid limpeza = Guid.NewGuid();

        Result<Transaction> result = Transaction.Create(new TransactionDraft
        {
            OccurredOn = Hoje,
            Description = "Supermercado",
            Currency = Brl,
            Entries =
            [
                new EntryDraft(Corrente, Money.FromUnits(-300, Brl)),
                new EntryDraft(Externa, Money.FromUnits(220, Brl)) { CategoryId = mercado },
                new EntryDraft(Externa, Money.FromUnits(80, Brl)) { CategoryId = limpeza },
            ],
        });

        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.True(result.Value.Balance.IsZero);
        Assert.Equal(3, result.Value.Entries.Count);

        // A categoria vive na perna: e o que torna a compra dividida possivel.
        Assert.Equal(mercado, result.Value.Entries[1].CategoryId);
        Assert.Equal(limpeza, result.Value.Entries[2].CategoryId);
    }

    [Fact]
    public void ReplaceEntries_recusa_conjunto_invalido_e_preserva_o_anterior()
    {
        Transaction transaction = Transaction.Spend(
            Hoje, "Mercado", Corrente, Externa, Money.FromUnits(150, Brl)).Value;

        Result result = transaction.ReplaceEntries(
        [
            new EntryDraft(Corrente, Money.FromUnits(-150, Brl)),
            new EntryDraft(Externa, Money.FromUnits(149, Brl)),
        ]);

        Assert.True(result.IsFailure);
        Assert.Equal("ledger.unbalanced", result.Error.Code);

        // O estado anterior segue intacto: nao ha janela desbalanceada.
        Assert.True(transaction.Balance.IsZero);
        Assert.Equal(-15000, transaction.Entries[0].Amount.MinorUnits);
    }

    [Fact]
    public void ReplaceEntries_aceita_conjunto_valido()
    {
        Transaction transaction = Transaction.Spend(
            Hoje, "Mercado", Corrente, Externa, Money.FromUnits(150, Brl)).Value;

        Result result = transaction.ReplaceEntries(
        [
            new EntryDraft(Corrente, Money.FromUnits(-200, Brl)),
            new EntryDraft(Externa, Money.FromUnits(200, Brl)),
        ]);

        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.True(transaction.Balance.IsZero);
        Assert.Equal(-20000, transaction.Entries[0].Amount.MinorUnits);
    }

    [Fact]
    public void Transferencia_exige_exatamente_duas_pernas()
    {
        Result<Transaction> result = Transaction.Create(new TransactionDraft
        {
            OccurredOn = Hoje,
            Description = "Transferencia esquisita",
            Currency = Brl,
            Kind = TransactionKind.Transfer,
            Entries =
            [
                new EntryDraft(Corrente, Money.FromUnits(-300, Brl)),
                new EntryDraft(Poupanca, Money.FromUnits(200, Brl)),
                new EntryDraft(Externa, Money.FromUnits(100, Brl)),
            ],
        });

        Assert.True(result.IsFailure);
        Assert.Equal("ledger.transfer_needs_two_entries", result.Error.Code);
    }

    [Fact]
    public void Transferencia_para_a_mesma_conta_e_rejeitada()
    {
        Result<Transaction> result = Transaction.Transfer(
            Hoje, "Para mim mesmo", Corrente, Corrente, Money.FromUnits(100, Brl));

        Assert.True(result.IsFailure);
        Assert.Equal("ledger.transfer_same_account", result.Error.Code);
    }

    [Fact]
    public void A_unica_mutacao_direta_da_perna_e_a_categoria()
    {
        // Recategorizar nao toca em valor, entao nao pode desbalancear.
        // Nao existe na API publica de Entry nada que mude Amount.
        Transaction transaction = Transaction.Spend(
            Hoje, "Mercado", Corrente, Externa, Money.FromUnits(150, Brl)).Value;

        Guid categoria = Guid.NewGuid();
        transaction.Entries[1].Recategorize(categoria);

        Assert.Equal(categoria, transaction.Entries[1].CategoryId);
        Assert.True(transaction.Balance.IsZero);

        Assert.Null(typeof(Entry).GetProperty(nameof(Entry.Amount))!.SetMethod);
    }

    [Fact]
    public void Soft_delete_apaga_a_transacao_inteira_e_nao_perna_isolada()
    {
        Transaction transaction = Transaction.Spend(
            Hoje, "Mercado", Corrente, Externa, Money.FromUnits(150, Brl)).Value;

        Assert.True(transaction.SoftDelete().IsSuccess);
        Assert.True(transaction.IsDeleted);

        // As duas pernas continuam la, juntas: quando o saldo ignora a
        // transacao, ignora as duas de uma vez e a soma global segue zero.
        Assert.Equal(2, transaction.Entries.Count);
        Assert.True(transaction.Balance.IsZero);

        Assert.Equal("ledger.already_deleted", transaction.SoftDelete().Error.Code);
    }

    [Fact]
    public void Saldo_de_abertura_tem_contrapartida_no_patrimonio()
    {
        Result<Transaction> result = Transaction.OpeningBalance(
            Hoje, Corrente, SystemAccounts.OpeningBalances, Money.FromUnits(5000, Brl));

        Assert.True(result.IsSuccess, result.Error.ToString());
        Assert.True(result.Value.Balance.IsZero);

        // Sem contrapartida, saldo inicial seria dinheiro surgindo do nada
        // e a soma global deixaria de fechar em zero.
        Assert.Equal(TransactionKind.OpeningBalance, result.Value.Kind);
        Assert.Equal(SystemAccounts.OpeningBalances, result.Value.Entries[1].AccountId);
        Assert.Equal(-500000, result.Value.Entries[1].Amount.MinorUnits);
    }
}
