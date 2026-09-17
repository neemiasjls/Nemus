using Nemus.Domain.Primitives;

namespace Nemus.Domain.Ledger;

public enum ImportSource
{
    Manual = 0,
    Ofx = 1,
    Pluggy = 2,
}

public static class ImportSourceExtensions
{
    public static string ToCode(this ImportSource source) => source switch
    {
        ImportSource.Manual => "MANUAL",
        ImportSource.Ofx => "OFX",
        ImportSource.Pluggy => "PLUGGY",
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Origem desconhecida."),
    };

    public static ImportSource FromCode(string code) => code switch
    {
        "MANUAL" => ImportSource.Manual,
        "OFX" => ImportSource.Ofx,
        "PLUGGY" => ImportSource.Pluggy,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Origem desconhecida."),
    };
}

/// <summary>
/// PILAR 3 - a identidade que torna a importacao idempotente.
///
/// O FITID do OFX so e unico dentro de uma conta de uma instituicao: dois
/// bancos podem perfeitamente emitir o mesmo. Por isso a chave carrega
/// tambem a referencia da conta de origem. Este tipo espelha exatamente o
/// indice unico ux_transactions_idempotency.
/// </summary>
public readonly record struct ExternalReference
{
    private ExternalReference(ImportSource source, string? accountRef, string externalId)
    {
        Source = source;
        AccountRef = accountRef;
        ExternalId = externalId;
    }

    public ImportSource Source { get; }

    /// <summary>ACCTID do OFX ou accountId do Pluggy.</summary>
    public string? AccountRef { get; }

    /// <summary>FITID do OFX ou id da transacao no Pluggy.</summary>
    public string ExternalId { get; }

    public static Result<ExternalReference> Create(
        ImportSource source, string externalId, string? accountRef = null)
    {
        if (source == ImportSource.Manual)
        {
            return LedgerErrors.ManualHasNoExternalId();
        }

        if (string.IsNullOrWhiteSpace(externalId))
        {
            return LedgerErrors.ExternalIdRequired();
        }

        return new ExternalReference(
            source,
            string.IsNullOrWhiteSpace(accountRef) ? null : accountRef.Trim(),
            externalId.Trim());
    }

    /// <summary>
    /// Mesma composicao do indice unico no Postgres. Serve para deduplicar
    /// em memoria, antes de tocar o banco.
    /// </summary>
    public string IdempotencyKey => $"{Source.ToCode()}|{AccountRef ?? string.Empty}|{ExternalId}";

    public override string ToString() => IdempotencyKey;
}
