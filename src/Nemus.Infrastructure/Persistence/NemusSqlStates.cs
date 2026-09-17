namespace Nemus.Infrastructure.Persistence;

/// <summary>
/// SQLSTATEs proprios levantados pelos gatilhos das migrations 003, 005 e 006.
/// Sao a contraparte no banco das invariantes que o dominio ja garante -
/// rede de seguranca para quem escrever SQL direto.
/// </summary>
public static class NemusSqlStates
{
    /// <summary>Soma das pernas diferente de zero.</summary>
    public const string TransactionUnbalanced = "NM001";

    /// <summary>Menos de duas pernas.</summary>
    public const string TransactionTooFewEntries = "NM002";

    /// <summary>Pernas em moedas diferentes.</summary>
    public const string TransactionMixedCurrencies = "NM003";

    /// <summary>Tentativa de remover conta de sistema.</summary>
    public const string SystemAccountProtected = "NM004";

    /// <summary>Categoria alem de dois niveis.</summary>
    public const string CategoryDepthExceeded = "NM010";

    /// <summary>Subcategoria com tipo diferente do pai.</summary>
    public const string CategoryKindMismatch = "NM011";

    /// <summary>Numero de parcelas diferente do declarado.</summary>
    public const string InstallmentCountMismatch = "NM020";

    /// <summary>Soma das parcelas diferente do valor financiado.</summary>
    public const string InstallmentSumMismatch = "NM021";

    /// <summary>Violacao de unicidade. E o que a reimportacao idempotente absorve.</summary>
    public const string UniqueViolation = "23505";

    public static bool IsLedgerInvariant(string? sqlState) =>
        sqlState is TransactionUnbalanced or TransactionTooFewEntries or TransactionMixedCurrencies;
}
