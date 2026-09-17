namespace Nemus.Domain.Primitives;

/// <summary>
/// Falha de dominio identificada por codigo estavel. O codigo e o que testes
/// e camadas superiores comparam; a mensagem e para humano e pode mudar.
/// </summary>
public readonly record struct Error(string Code, string Message)
{
    public static readonly Error None = new(string.Empty, string.Empty);

    public bool IsNone => string.IsNullOrEmpty(Code);

    public override string ToString() => IsNone ? "(sem erro)" : $"{Code}: {Message}";
}
