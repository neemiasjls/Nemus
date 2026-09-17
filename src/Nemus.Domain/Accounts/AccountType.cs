namespace Nemus.Domain.Accounts;

/// <summary>
/// Os cinco tipos classicos. REVENUE e EXPENSE representam o mundo fora do
/// seu patrimonio: a padaria, o empregador, a Receita Federal.
///
/// Sao contas de verdade, e nao um buraco por onde o dinheiro "sai do
/// sistema", justamente para que a soma de TODOS os saldos seja sempre
/// exatamente zero - propriedade que o teste de razao verifica.
/// </summary>
public enum AccountType
{
    Asset = 1,
    Liability = 2,
    Equity = 3,
    Revenue = 4,
    Expense = 5,
}

public static class AccountTypeExtensions
{
    /// <summary>Pertence ao patrimonio do usuario (dentro do perimetro).</summary>
    public static bool IsInternal(this AccountType type) =>
        type is AccountType.Asset or AccountType.Liability or AccountType.Equity;

    public static string ToCode(this AccountType type) => type switch
    {
        AccountType.Asset => "ASSET",
        AccountType.Liability => "LIABILITY",
        AccountType.Equity => "EQUITY",
        AccountType.Revenue => "REVENUE",
        AccountType.Expense => "EXPENSE",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Tipo de conta desconhecido."),
    };

    public static AccountType FromCode(string code) => code switch
    {
        "ASSET" => AccountType.Asset,
        "LIABILITY" => AccountType.Liability,
        "EQUITY" => AccountType.Equity,
        "REVENUE" => AccountType.Revenue,
        "EXPENSE" => AccountType.Expense,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Codigo de tipo de conta desconhecido."),
    };
}
