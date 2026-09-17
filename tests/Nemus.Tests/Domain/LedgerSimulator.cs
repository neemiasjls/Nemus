using Nemus.Domain.Accounts;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;

namespace Nemus.Tests.Domain;

/// <summary>
/// Razao em memoria, com a mesma semantica do banco: aplicar uma transacao
/// soma cada perna na sua conta; excluir remove as pernas todas de uma vez.
/// </summary>
internal sealed class LedgerSimulator
{
    private readonly Dictionary<Guid, long> _balances = [];
    private readonly Dictionary<Guid, AccountType> _types = [];
    private readonly List<Transaction> _live = [];

    public IReadOnlyList<Transaction> Live => _live;
    public int TransactionCount => _live.Count;

    public void RegisterAccount(Guid id, AccountType type)
    {
        _types[id] = type;
        _balances[id] = 0;
    }

    public void Apply(Transaction transaction)
    {
        foreach (Entry entry in transaction.Entries)
        {
            _balances[entry.AccountId] = checked(_balances[entry.AccountId] + entry.Amount.MinorUnits);
        }

        _live.Add(transaction);
    }

    /// <summary>
    /// Exclusao logica: as duas pernas saem juntas. E exatamente por saírem
    /// juntas que a soma global continua zero depois.
    /// </summary>
    public void SoftDelete(Transaction transaction)
    {
        foreach (Entry entry in transaction.Entries)
        {
            _balances[entry.AccountId] = checked(_balances[entry.AccountId] - entry.Amount.MinorUnits);
        }

        transaction.SoftDelete();
        _live.Remove(transaction);
    }

    public long SumOfAllBalances => _balances.Values.Sum();

    public long SumInternal => _balances
        .Where(pair => _types[pair.Key].IsInternal())
        .Sum(pair => pair.Value);

    public long SumExternal => _balances
        .Where(pair => !_types[pair.Key].IsInternal())
        .Sum(pair => pair.Value);

    public long BalanceOf(Guid accountId) => _balances[accountId];

    public IReadOnlyDictionary<Guid, long> Snapshot() => new Dictionary<Guid, long>(_balances);
}

/// <summary>
/// Gerador de transacoes aleatorias. Random semeado em vez de FsCheck: o
/// seed aparece na mensagem de falha, entao qualquer falha e reproduzivel
/// rodando o mesmo caso - e o gerador produz, por construcao, exatamente a
/// forma que este dominio aceita.
/// </summary>
internal static class LedgerGenerator
{
    private const long MaxLegMinorUnits = 1_000_000;

    /// <summary>Transacao que fecha em zero por construcao.</summary>
    public static TransactionDraft Balanced(
        Random rng, IReadOnlyList<Guid> accounts, Currency currency, DateOnly date)
    {
        int legs = rng.Next(2, 6);
        var amounts = new long[legs];

        long running = 0;
        for (int i = 0; i < legs - 1; i++)
        {
            amounts[i] = NonZero(rng);
            running += amounts[i];
        }

        // Se o que sobrou for zero, a ultima perna seria zero - o que o
        // dominio recusa, e com razao. Empurra uma unidade.
        if (running == 0)
        {
            amounts[0] += 1;
            running += 1;
        }

        amounts[legs - 1] = -running;

        var entries = new List<EntryDraft>(legs);
        for (int i = 0; i < legs; i++)
        {
            Guid account = accounts[rng.Next(accounts.Count)];
            entries.Add(new EntryDraft(account, Money.FromMinorUnits(amounts[i], currency)));
        }

        return new TransactionDraft
        {
            OccurredOn = date.AddDays(rng.Next(-365, 366)),
            Description = $"Operacao aleatoria {rng.Next(100000)}",
            Currency = currency,
            Entries = entries,
        };
    }

    /// <summary>Transacao que NAO fecha em zero. Deve ser sempre recusada.</summary>
    public static TransactionDraft Unbalanced(
        Random rng, IReadOnlyList<Guid> accounts, Currency currency, DateOnly date)
    {
        TransactionDraft balanced = Balanced(rng, accounts, currency, date);

        var entries = balanced.Entries.ToList();
        int index = rng.Next(entries.Count);
        long drift = NonZero(rng);

        entries[index] = entries[index] with
        {
            Amount = Money.FromMinorUnits(entries[index].Amount.MinorUnits + drift, currency),
        };

        // A deriva pode, por azar, zerar a perna; nesse caso ela seria
        // recusada por outro motivo, o que ainda serve ao teste.
        return balanced with { Entries = entries };
    }

    private static long NonZero(Random rng)
    {
        long value = rng.NextInt64(-MaxLegMinorUnits, MaxLegMinorUnits);
        return value == 0 ? 1 : value;
    }
}
