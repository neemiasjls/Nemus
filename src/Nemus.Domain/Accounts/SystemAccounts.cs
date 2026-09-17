namespace Nemus.Domain.Accounts;

/// <summary>
/// Contas criadas pela migration 002. Os UUIDs sao fixos e precisam bater
/// exatamente com o SQL - ha um teste que confere isso contra o banco.
/// </summary>
public static class SystemAccounts
{
    /// <summary>Contrapartida do saldo de abertura de qualquer conta.</summary>
    public static readonly Guid OpeningBalances =
        new("00000000-0000-7000-8000-000000000001");

    /// <summary>Contrapartida externa padrao de uma despesa.</summary>
    public static readonly Guid ExternalExpenses =
        new("00000000-0000-7000-8000-000000000002");

    /// <summary>Contrapartida externa padrao de uma receita.</summary>
    public static readonly Guid ExternalRevenue =
        new("00000000-0000-7000-8000-000000000003");

    /// <summary>Absorve diferenca encontrada na conciliacao de extrato.</summary>
    public static readonly Guid ReconciliationAdjustment =
        new("00000000-0000-7000-8000-000000000004");

    public static IReadOnlyList<Guid> All { get; } =
    [
        OpeningBalances,
        ExternalExpenses,
        ExternalRevenue,
        ReconciliationAdjustment,
    ];

    public static bool IsSystem(Guid accountId) => All.Contains(accountId);
}
