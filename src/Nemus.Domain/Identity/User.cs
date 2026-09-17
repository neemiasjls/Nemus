using System.Text.RegularExpressions;
using Nemus.Domain.Primitives;

namespace Nemus.Domain.Identity;

/// <summary>
/// Quem entra no app. O dominio guarda a REGRA (o que e nome valido, o que e
/// senha aceitavel) e nunca a senha em si: o que chega aqui ja e hash, e quem
/// sabe transformar uma coisa na outra e a infraestrutura.
/// </summary>
public sealed partial class User
{
    /// <summary>
    /// Piso de tamanho, sem exigir simbolo nem maiuscula. Regra de composicao
    /// empurra para "Senha@123", que e curta e previsivel; tamanho e o que de
    /// fato encarece a forca bruta. E a recomendacao atual do NIST.
    /// </summary>
    public const int MinPasswordLength = 10;

    /// <summary>
    /// Teto por seguranca do servidor, nao do usuario: PBKDF2 processa a
    /// entrada inteira, entao senha gigante vira trabalho gratuito para quem
    /// quiser derrubar a API.
    /// </summary>
    public const int MaxPasswordLength = 256;

    /// <summary>
    /// Lista curta de proposito: as que aparecem no topo de todo vazamento e
    /// as obvias em portugues. Nao substitui uma lista grande de senhas
    /// vazadas - e o piso, nao o teto.
    /// </summary>
    private static readonly string[] Common =
    [
        "1234567890", "0123456789", "123456789", "12345678", "password", "senha123",
        "senhasenha", "qwertyuiop", "asdfghjkl", "minhasenha", "deixaeuentrar",
    ];

    private User(
        Guid id, string username, string passwordHash, bool isActive, DateTimeOffset createdAt)
    {
        Id = id;
        Username = username;
        PasswordHash = passwordHash;
        IsActive = isActive;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }
    public string Username { get; }
    public string PasswordHash { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }

    public static Result<User> Create(
        string? username,
        string passwordHash,
        Guid? id = null,
        DateTimeOffset? createdAt = null)
    {
        Result<string> normalized = NormalizeUsername(username);
        if (normalized.IsFailure)
        {
            return normalized.Error;
        }

        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            return new Error("auth.password_hash_required", "Senha ausente.");
        }

        return new User(
            id ?? UuidV7.NewGuid(),
            normalized.Value,
            passwordHash,
            isActive: true,
            createdAt ?? DateTimeOffset.UtcNow);
    }

    public static User Rehydrate(
        Guid id,
        string username,
        string passwordHash,
        bool isActive,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? lastLoginAt) =>
        new(id, username, passwordHash, isActive, createdAt)
        {
            UpdatedAt = updatedAt,
            LastLoginAt = lastLoginAt,
        };

    /// <summary>
    /// Minusculo e sem espaco, sempre. Sem isto "Neemias" e "neemias" viram
    /// duas contas, e a que voce nao criou e a que o atacante registra.
    /// </summary>
    public static Result<string> NormalizeUsername(string? raw)
    {
        string candidate = (raw ?? string.Empty).Trim().ToLowerInvariant();

        return UsernamePattern().IsMatch(candidate)
            ? candidate
            : new Error(
                "auth.username_invalid",
                "Nome de usuario: de 3 a 40 caracteres, apenas letras minusculas, numeros, ponto, hifen ou sublinhado.");
    }

    /// <summary>
    /// A senha nunca e guardada, mas a regra de qual senha aceitar e decisao
    /// de negocio - por isso mora aqui, e nao junto do algoritmo de hash.
    /// </summary>
    public static Result ValidatePassword(string? password, string? username = null)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return new Error("auth.password_required", "Informe a senha.");
        }

        if (password.Length < MinPasswordLength)
        {
            return new Error(
                "auth.password_too_short",
                $"A senha precisa de pelo menos {MinPasswordLength} caracteres. "
                + "Tamanho protege mais do que simbolo: prefira uma frase que voce lembre.");
        }

        if (password.Length > MaxPasswordLength)
        {
            return new Error(
                "auth.password_too_long",
                $"A senha passa de {MaxPasswordLength} caracteres.");
        }

        string lowered = password.ToLowerInvariant();
        string? owner = string.IsNullOrWhiteSpace(username) ? null : username.Trim().ToLowerInvariant();

        if (owner is { Length: > 0 } && lowered.Contains(owner, StringComparison.Ordinal))
        {
            return new Error(
                "auth.password_contains_username",
                "A senha nao pode conter o nome de usuario - e o primeiro palpite de quem tenta entrar.");
        }

        if (HasSingleRepeatedCharacter(lowered) || IsCommon(lowered))
        {
            return new Error(
                "auth.password_too_common",
                "Essa senha esta nas listas que qualquer ataque tenta primeiro. Escolha outra.");
        }

        return Result.Success();
    }

    /// <summary>Grava o novo hash. Quem chama precisa revogar as sessoes abertas.</summary>
    public void ChangePassword(string passwordHash, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            throw new ArgumentException("Hash de senha vazio.", nameof(passwordHash));
        }

        PasswordHash = passwordHash;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }

    public void RegisterLogin(DateTimeOffset at)
    {
        LastLoginAt = at;
        UpdatedAt = at;
    }

    public void Deactivate(DateTimeOffset? at = null)
    {
        IsActive = false;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }

    private static bool HasSingleRepeatedCharacter(string password) =>
        password.Distinct().Count() == 1;

    private static bool IsCommon(string password) =>
        Common.Contains(password, StringComparer.Ordinal)
        || Common.Any(weak =>
            password.StartsWith(weak, StringComparison.Ordinal) && password.Length <= weak.Length + 2);

    [GeneratedRegex("^[a-z0-9._-]{3,40}$")]
    private static partial Regex UsernamePattern();

    public override string ToString() => Username;
}
