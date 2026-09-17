using Npgsql;

namespace Nemus.Infrastructure.Persistence;

/// <summary>
/// Retrato da integridade do razao, lido de v_ledger_integrity.
/// Num razao intacto todos os campos sao zero.
/// </summary>
public sealed record LedgerIntegrity(
    long TotalAmount,
    long TotalAmountIncludingDeleted,
    long UnbalancedTransactions,
    long UndersizedTransactions,
    long EmptyTransactions,
    long SumOfAllBalances,
    long SumInternalBalances,
    long SumExternalBalances,
    long BrokenInstallmentPlans)
{
    /// <summary>
    /// A pergunta que o teste do razao faz. Repare que somar os saldos
    /// internos com os externos precisa dar zero: e o que prova que nenhum
    /// centavo entrou nem saiu do nada.
    /// </summary>
    public bool IsIntact =>
        TotalAmount == 0
        && TotalAmountIncludingDeleted == 0
        && UnbalancedTransactions == 0
        && UndersizedTransactions == 0
        && EmptyTransactions == 0
        && SumOfAllBalances == 0
        && SumInternalBalances == -SumExternalBalances
        && BrokenInstallmentPlans == 0;

    public string Describe() => IsIntact
        ? "Razao integro."
        : $"Razao inconsistente: soma={SumOfAllBalances}, desbalanceadas={UnbalancedTransactions}, "
          + $"subdimensionadas={UndersizedTransactions}, vazias={EmptyTransactions}, "
          + $"interno={SumInternalBalances}, externo={SumExternalBalances}, "
          + $"planos quebrados={BrokenInstallmentPlans}.";
}

public sealed class LedgerIntegrityReader
{
    private readonly NpgsqlDataSource _dataSource;

    public LedgerIntegrityReader(NpgsqlDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public async Task<LedgerIntegrity> ReadAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = _dataSource.CreateCommand("""
            SELECT total_amount, total_amount_including_deleted, unbalanced_transactions,
                   undersized_transactions, empty_transactions, sum_of_all_balances,
                   sum_internal_balances, sum_external_balances, broken_installment_plans
              FROM v_ledger_integrity
            """);

        await using NpgsqlDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("v_ledger_integrity nao retornou linha.");
        }

        return new LedgerIntegrity(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8));
    }
}
