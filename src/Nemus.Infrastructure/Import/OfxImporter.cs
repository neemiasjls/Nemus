using System.Security.Cryptography;
using Nemus.Domain.Accounts;
using Nemus.Domain.Ledger;
using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;

namespace Nemus.Infrastructure.Import;

/// <summary>
/// Resultado da traducao de um extrato em transacoes do razao. Duplicatas
/// dentro do proprio arquivo sao contadas a parte: acontece de verdade
/// quando a pessoa baixa periodos que se sobrepoem e cola os dois arquivos.
/// </summary>
public sealed record OfxMapping(
    IReadOnlyList<Transaction> Transactions,
    int DuplicatesWithinFile);

public static class OfxImporter
{
    /// <summary>
    /// Traduz um extrato em transacoes de duas pernas.
    ///
    /// SINAL. No OFX, TRNAMT negativo e dinheiro saindo da conta. A perna da
    /// conta bancaria recebe o valor como veio; a contraparte recebe o
    /// oposto. Assim a soma fecha em zero sem nenhum caso especial, e o
    /// sentido do dinheiro continua sendo o que o banco disse.
    ///
    /// CONTRAPARTE. Saida vai contra "Despesas externas", entrada contra
    /// "Receitas externas". Sao contas de verdade, nao um buraco - e por
    /// isso que a soma de TODOS os saldos continua exatamente zero depois de
    /// importar.
    ///
    /// A categorizacao nao acontece aqui de proposito: adivinhar categoria
    /// na importacao mistura duas responsabilidades e torna o resultado nao
    /// reproduzivel. Isso e o motor de regras da fase 7, que roda depois e
    /// pode ser reaplicado.
    /// </summary>
    public static Result<OfxMapping> Map(
        OfxStatement statement,
        Guid accountId,
        Guid? importBatchId = null,
        DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(statement);

        if (accountId == Guid.Empty)
        {
            return new Error("ofx.account_required", "Informe a conta do razao que recebe o extrato.");
        }

        var transactions = new List<Transaction>(statement.Entries.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int duplicates = 0;

        foreach (OfxEntry entry in statement.Entries)
        {
            Result<ExternalReference> identity = ExternalReference.Create(
                ImportSource.Ofx, entry.FitId, statement.AccountId);

            if (identity.IsFailure)
            {
                return identity.Error;
            }

            // Deduplica em memoria antes de tocar o banco. O indice unico
            // pegaria de qualquer jeito, mas duas linhas com a mesma chave
            // no mesmo lote fariam a segunda ser contada como "ja existia",
            // escondendo que o arquivo e que estava repetido.
            if (!seen.Add(identity.Value.IdempotencyKey))
            {
                duplicates++;
                continue;
            }

            Guid counterparty = entry.Amount.IsNegative
                ? SystemAccounts.ExternalExpenses
                : SystemAccounts.ExternalRevenue;

            Result<Transaction> transaction = Transaction.Create(new TransactionDraft
            {
                OccurredOn = entry.PostedOn,
                Description = entry.Description,
                Currency = statement.Currency,
                Kind = TransactionKind.Standard,
                External = identity.Value,
                ImportBatchId = importBatchId,
                CreatedAt = createdAt,
                Entries =
                [
                    new EntryDraft(accountId, entry.Amount),
                    new EntryDraft(counterparty, entry.Amount.Negated),
                ],
            });

            if (transaction.IsFailure)
            {
                return transaction.Error;
            }

            transactions.Add(transaction.Value);
        }

        return new OfxMapping(transactions, duplicates);
    }

    /// <summary>
    /// Hash do arquivo, para a camada barata de idempotencia em
    /// import_batches. E atalho, nao garantia: dois downloads do mesmo
    /// periodo podem diferir num byte de cabecalho e gerar hashes
    /// diferentes. Quem garante correcao e o FITID de cada linha.
    /// </summary>
    public static string ComputeFileHash(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    }
}
