using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;

namespace Nemus.Domain.Accounts;

public sealed class Account
{
    private Account(
        Guid id,
        string name,
        AccountType type,
        Currency currency,
        string? institution,
        bool isOnBudget,
        bool isSystem,
        string? externalRef,
        DateTimeOffset createdAt)
    {
        Id = id;
        Name = name;
        Type = type;
        Currency = currency;
        Institution = institution;
        IsOnBudget = isOnBudget;
        IsSystem = isSystem;
        ExternalRef = externalRef;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }
    public string Name { get; private set; }
    public AccountType Type { get; }
    public Currency Currency { get; }
    public string? Institution { get; private set; }

    /// <summary>Fase 4: alimenta o pronto-para-atribuir do orcamento.</summary>
    public bool IsOnBudget { get; private set; }

    public bool IsSystem { get; }

    /// <summary>ACCTID do OFX ou accountId do Pluggy.</summary>
    public string? ExternalRef { get; private set; }

    public bool IsArchived { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsInternal => Type.IsInternal();

    public static Result<Account> Create(
        string name,
        AccountType type,
        Currency currency,
        string? institution = null,
        bool isOnBudget = true,
        string? externalRef = null,
        Guid? id = null,
        DateTimeOffset? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new Error("account.name_required", "Conta precisa de nome.");
        }

        if (!currency.IsDefined)
        {
            return new Error("account.currency_required", "Conta precisa de moeda definida.");
        }

        // Conta externa nao entra no orcamento: ela representa o mundo,
        // nao um lugar onde voce guarda dinheiro.
        if (!type.IsInternal() && isOnBudget)
        {
            isOnBudget = false;
        }

        return new Account(
            id ?? UuidV7.NewGuid(),
            name.Trim(),
            type,
            currency,
            string.IsNullOrWhiteSpace(institution) ? null : institution.Trim(),
            isOnBudget,
            isSystem: false,
            string.IsNullOrWhiteSpace(externalRef) ? null : externalRef.Trim(),
            createdAt ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Reconstroi a partir do banco. Revalida por seguranca.</summary>
    public static Result<Account> Rehydrate(
        Guid id,
        string name,
        AccountType type,
        Currency currency,
        string? institution,
        bool isOnBudget,
        bool isSystem,
        string? externalRef,
        bool isArchived,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new Error("account.name_required", "Conta precisa de nome.");
        }

        if (!currency.IsDefined)
        {
            return new Error("account.currency_required", "Conta precisa de moeda definida.");
        }

        return new Account(id, name, type, currency, institution, isOnBudget, isSystem, externalRef, createdAt)
        {
            IsArchived = isArchived,
            UpdatedAt = updatedAt,
        };
    }

    public Result Rename(string name, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return new Error("account.name_required", "Conta precisa de nome.");
        }

        Name = name.Trim();
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
        return Result.Success();
    }

    public Result Archive(DateTimeOffset? at = null)
    {
        if (IsSystem)
        {
            return new Error("account.system_immutable", $"A conta de sistema \"{Name}\" nao pode ser arquivada.");
        }

        IsArchived = true;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
        return Result.Success();
    }

    public Result LinkExternal(string externalRef, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(externalRef))
        {
            return new Error("account.external_ref_required", "Referencia externa vazia.");
        }

        ExternalRef = externalRef.Trim();
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
        return Result.Success();
    }

    public override string ToString() => $"{Name} ({Type.ToCode()}/{Currency})";
}
