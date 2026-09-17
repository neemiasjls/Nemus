using Nemus.Domain.Primitives;

namespace Nemus.Domain.Monetary;

/// <summary>
/// Moeda e a escala da sua menor unidade. A escala vive aqui, nao numa
/// constante global: BRL usa 2 (centavos), e migrar para milliunits (3)
/// seria trocar este dado, nao reescrever o modelo.
/// </summary>
public readonly record struct Currency
{
    private readonly string? _code;
    private readonly byte _decimalPlaces;

    private Currency(string code, byte decimalPlaces)
    {
        _code = code;
        _decimalPlaces = decimalPlaces;
    }

    public static readonly Currency Brl = new("BRL", 2);
    public static readonly Currency Usd = new("USD", 2);
    public static readonly Currency Eur = new("EUR", 2);

    private static readonly Dictionary<string, Currency> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["BRL"] = Brl,
            ["USD"] = Usd,
            ["EUR"] = Eur,
        };

    /// <summary>
    /// False para <c>default(Currency)</c>. Money recusa moeda indefinida,
    /// entao <c>default(Money)</c> nao consegue participar de aritmetica.
    /// </summary>
    public bool IsDefined => _code is not null;

    public string Code => _code ?? throw new InvalidOperationException(
        "Currency nao inicializada. Use Currency.Brl ou Currency.From(\"BRL\").");

    /// <summary>Casas decimais da menor unidade. BRL = 2.</summary>
    public int DecimalPlaces
    {
        get
        {
            _ = Code; // dispara a excecao acima se indefinida
            return _decimalPlaces;
        }
    }

    /// <summary>10^DecimalPlaces. Fator entre unidade e menor unidade.</summary>
    public long MinorUnitsPerUnit
    {
        get
        {
            long factor = 1;
            for (int i = 0; i < DecimalPlaces; i++)
            {
                factor *= 10;
            }

            return factor;
        }
    }

    public static Currency From(string code)
    {
        Result<Currency> result = TryFrom(code);
        return result.IsSuccess
            ? result.Value
            : throw new ArgumentException(result.Error.Message, nameof(code));
    }

    public static Result<Currency> TryFrom(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new Error("currency.empty", "Codigo de moeda vazio.");
        }

        return Known.TryGetValue(code.Trim(), out Currency currency)
            ? currency
            : new Error("currency.unknown", $"Moeda \"{code}\" desconhecida.");
    }

    public override string ToString() => _code ?? "(indefinida)";
}
