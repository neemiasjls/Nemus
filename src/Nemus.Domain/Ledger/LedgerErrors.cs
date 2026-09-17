using Nemus.Domain.Primitives;

namespace Nemus.Domain.Ledger;

/// <summary>
/// Codigos estaveis. Testes comparam por codigo, nunca por mensagem.
/// Os que tem contraparte no banco trazem o SQLSTATE correspondente.
/// </summary>
public static class LedgerErrors
{
    public static Error DescriptionRequired() =>
        new("ledger.description_required", "Transacao precisa de descricao.");

    public static Error CurrencyRequired() =>
        new("ledger.currency_required", "Transacao precisa de moeda definida.");

    /// <summary>Contraparte de NM002 no banco.</summary>
    public static Error TooFewEntries(int count) =>
        new("ledger.too_few_entries",
            $"Partidas dobradas exigem ao menos 2 lancamentos; recebi {count}.");

    public static Error ZeroAmountEntry(int index) =>
        new("ledger.zero_amount_entry",
            $"O lancamento na posicao {index} tem valor zero. Perna sem valor nao e perna.");

    /// <summary>Contraparte de NM003 no banco.</summary>
    public static Error CurrencyMismatch(int index, string expected, string found) =>
        new("ledger.currency_mismatch",
            $"O lancamento na posicao {index} esta em {found}, mas a transacao e em {expected}.");

    /// <summary>Contraparte de NM001 no banco. O erro central do PILAR 1.</summary>
    public static Error Unbalanced(long residual, string currency) =>
        new("ledger.unbalanced",
            $"Transacao desbalanceada: a soma das pernas e {residual} ({currency}), deveria ser 0.");

    public static Error ExternalIdRequired() =>
        new("ledger.external_id_required",
            "Transacao importada precisa de identidade externa, senao a reimportacao duplica.");

    public static Error ManualHasNoExternalId() =>
        new("ledger.manual_has_no_external_id",
            "Lancamento manual nao tem identidade externa; use ImportSource.Ofx ou ImportSource.Pluggy.");

    public static Error TransferNeedsTwoEntries(int count) =>
        new("ledger.transfer_needs_two_entries",
            $"Transferencia tem exatamente 2 pernas; recebi {count}.");

    public static Error OpeningBalanceNeedsTwoEntries(int count) =>
        new("ledger.opening_balance_needs_two_entries",
            $"Saldo de abertura tem exatamente 2 pernas; recebi {count}.");

    public static Error AlreadyDeleted() =>
        new("ledger.already_deleted", "Transacao ja esta excluida.");

    public static Error TooManyEntries(int count) =>
        new("ledger.too_many_entries",
            $"Transacao com {count} pernas excede o limite de {Transaction.MaxEntries}.");
}
