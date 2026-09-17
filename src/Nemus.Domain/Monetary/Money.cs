using System.Globalization;
using System.Text;
using Nemus.Domain.Primitives;

namespace Nemus.Domain.Monetary;

/// <summary>
/// PILAR 2 - dinheiro e inteiro.
///
/// Nao existe neste tipo, em ponto nenhum, construtor, operador, conversao,
/// propriedade ou parametro de tipo <c>decimal</c>, <c>double</c> ou
/// <c>float</c>. Nao e disciplina, e superficie de API: nao ha o que
/// escrever para guardar dinheiro em ponto flutuante. O teste de arquitetura
/// em Nemus.Tests varre o assembly inteiro por reflexao e falha se algum dia
/// isso deixar de ser verdade.
///
/// Toda aritmetica e <c>checked</c>: estouro de BIGINT lanca em vez de dar a
/// volta em silencio. Num razao, saldo errado e pior que excecao.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    private Money(long minorUnits, Currency currency)
    {
        MinorUnits = minorUnits;
        Currency = currency;
    }

    /// <summary>Valor na menor unidade da moeda. BRL = centavos.</summary>
    public long MinorUnits { get; }

    public Currency Currency { get; }

    public static Money FromMinorUnits(long minorUnits, Currency currency)
    {
        EnsureDefined(currency);
        return new Money(minorUnits, currency);
    }

    /// <summary>
    /// Conveniencia para valor redondo: <c>Money.FromUnits(50, Currency.Brl)</c>
    /// e R$ 50,00. Continua sendo inteiro puro.
    /// </summary>
    public static Money FromUnits(long units, Currency currency)
    {
        EnsureDefined(currency);
        return new Money(checked(units * currency.MinorUnitsPerUnit), currency);
    }

    public static Money Zero(Currency currency) => FromMinorUnits(0, currency);

    public bool IsZero => MinorUnits == 0;
    public bool IsPositive => MinorUnits > 0;
    public bool IsNegative => MinorUnits < 0;

    public Money Negated => new(checked(-MinorUnits), Currency);
    public Money Abs => MinorUnits < 0 ? Negated : this;

    public static Money operator +(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.MinorUnits + right.MinorUnits), left.Currency);
    }

    public static Money operator -(Money left, Money right)
    {
        EnsureSameCurrency(left, right);
        return new Money(checked(left.MinorUnits - right.MinorUnits), left.Currency);
    }

    public static Money operator -(Money value) => value.Negated;

    public static Money operator *(Money value, int factor) =>
        new(checked(value.MinorUnits * factor), value.Currency);

    public static Money operator *(int factor, Money value) => value * factor;

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;
    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;
    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(this, other);
        return MinorUnits.CompareTo(other.MinorUnits);
    }

    /// <summary>
    /// Soma uma sequencia. A moeda e obrigatoria como parametro porque a
    /// soma de uma sequencia vazia tambem precisa ter moeda - senao o
    /// caso vazio vira <c>default(Money)</c> e envenena a conta seguinte.
    /// </summary>
    public static Money Sum(IEnumerable<Money> values, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(values);
        EnsureDefined(currency);

        long total = 0;
        foreach (Money value in values)
        {
            EnsureCurrency(value, currency);
            total = checked(total + value.MinorUnits);
        }

        return new Money(total, currency);
    }

    // -----------------------------------------------------------------------
    // Reparticao

    /// <summary>
    /// Reparte em <paramref name="parts"/> pedacos cuja soma e exatamente
    /// este valor. R$ 100,00 em 3 vira 33,34 + 33,33 + 33,33 (ou o residual
    /// no fim, conforme <paramref name="placement"/>): o centavo que sobra
    /// tem que existir em algum lugar e nao pode evaporar.
    ///
    /// A colocacao do residual varia por emissor de cartao no Brasil, entao
    /// e parametro em vez de convencao fixa. O que nao varia, e o que os
    /// testes cobram, e a soma fechar exata.
    /// </summary>
    public Money[] Allocate(int parts, RemainderPlacement placement = RemainderPlacement.First)
    {
        if (parts <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parts), parts, "O numero de partes precisa ser positivo.");
        }

        long sign = MinorUnits < 0 ? -1 : 1;
        long magnitude = MinorUnits < 0 ? -MinorUnits : MinorUnits;

        long quotient = magnitude / parts;
        int remainder = (int)(magnitude % parts);

        var slices = new Money[parts];
        for (int i = 0; i < parts; i++)
        {
            slices[i] = new Money(sign * quotient, Currency);
        }

        for (int i = 0; i < remainder; i++)
        {
            int index = placement == RemainderPlacement.First ? i : parts - 1 - i;
            slices[index] = new Money(slices[index].MinorUnits + sign, Currency);
        }

        return slices;
    }

    // -----------------------------------------------------------------------
    // Leitura de texto
    //
    // Nao passa por decimal nem por double em momento algum: acumula digito
    // a digito num long. E o caminho que a importacao de OFX (fase 3) e do
    // Pluggy (fase 5) vao usar, e onde a maioria dos sistemas perde centavo.

    /// <summary>Formato de maquina: "-1234.56". Ponto decimal, virgula de milhar.</summary>
    public static Result<Money> ParseInvariant(string text, Currency currency) =>
        ParseCore(text, currency, decimalSeparator: '.', groupSeparator: ',');

    /// <summary>Formato brasileiro: "-1.234,56". Virgula decimal, ponto de milhar.</summary>
    public static Result<Money> ParsePtBr(string text, Currency currency) =>
        ParseCore(text, currency, decimalSeparator: ',', groupSeparator: '.');

    private static Result<Money> ParseCore(
        string text, Currency currency, char decimalSeparator, char groupSeparator)
    {
        if (!currency.IsDefined)
        {
            return new Error("money.currency_undefined", "Moeda indefinida.");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return new Error("money.parse_empty", "Valor monetario vazio.");
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        bool negative = false;

        if (span[0] is '+' or '-')
        {
            negative = span[0] == '-';
            span = span[1..];
        }

        if (span.IsEmpty)
        {
            return new Error("money.parse_invalid", $"Valor \"{text}\" nao tem digitos.");
        }

        long integerPart = 0;
        long fractionPart = 0;
        int fractionDigits = 0;
        bool seenDecimalSeparator = false;
        bool seenDigit = false;

        foreach (char c in span)
        {
            if (c == groupSeparator && !seenDecimalSeparator)
            {
                continue;
            }

            if (c == decimalSeparator)
            {
                if (seenDecimalSeparator)
                {
                    return new Error(
                        "money.parse_invalid",
                        $"Valor \"{text}\" tem mais de um separador decimal.");
                }

                seenDecimalSeparator = true;
                continue;
            }

            if (c is < '0' or > '9')
            {
                return new Error(
                    "money.parse_invalid",
                    $"Valor \"{text}\" contem o caractere invalido '{c}'.");
            }

            seenDigit = true;
            int digit = c - '0';

            try
            {
                if (seenDecimalSeparator)
                {
                    fractionDigits++;
                    if (fractionDigits > currency.DecimalPlaces)
                    {
                        // Arredondar aqui seria perder dinheiro em silencio.
                        return new Error(
                            "money.parse_precision",
                            $"Valor \"{text}\" tem mais de {currency.DecimalPlaces} casas decimais, "
                            + $"o que {currency.Code} nao representa.");
                    }

                    fractionPart = checked((fractionPart * 10) + digit);
                }
                else
                {
                    integerPart = checked((integerPart * 10) + digit);
                }
            }
            catch (OverflowException)
            {
                return new Error("money.parse_overflow", $"Valor \"{text}\" excede a faixa de BIGINT.");
            }
        }

        if (!seenDigit)
        {
            return new Error("money.parse_invalid", $"Valor \"{text}\" nao tem digitos.");
        }

        for (int i = fractionDigits; i < currency.DecimalPlaces; i++)
        {
            fractionPart *= 10;
        }

        try
        {
            long minorUnits = checked((integerPart * currency.MinorUnitsPerUnit) + fractionPart);
            return new Money(negative ? -minorUnits : minorUnits, currency);
        }
        catch (OverflowException)
        {
            return new Error("money.parse_overflow", $"Valor \"{text}\" excede a faixa de BIGINT.");
        }
    }

    // -----------------------------------------------------------------------
    // Escrita de texto (tambem sem ponto flutuante)

    public override string ToString() => Format(CultureInfo.InvariantCulture, withGrouping: false);

    public string ToPtBrString() => Format(new CultureInfo("pt-BR"), withGrouping: true);

    public string Format(IFormatProvider? provider = null, bool withGrouping = true)
    {
        NumberFormatInfo format = NumberFormatInfo.GetInstance(provider ?? CultureInfo.InvariantCulture);

        // Magnitude em ulong para dar conta de long.MinValue sem estourar.
        ulong magnitude = MinorUnits < 0
            ? unchecked((ulong)-(MinorUnits + 1)) + 1
            : (ulong)MinorUnits;

        var factor = (ulong)Currency.MinorUnitsPerUnit;
        ulong units = magnitude / factor;
        ulong fraction = magnitude % factor;

        string unitsText = units.ToString(CultureInfo.InvariantCulture);
        if (withGrouping)
        {
            unitsText = ApplyGrouping(unitsText, format.NumberGroupSeparator);
        }

        var builder = new StringBuilder();
        if (MinorUnits < 0)
        {
            builder.Append(format.NegativeSign);
        }

        builder.Append(unitsText);

        if (Currency.DecimalPlaces > 0)
        {
            builder.Append(format.NumberDecimalSeparator);
            builder.Append(fraction.ToString(CultureInfo.InvariantCulture)
                .PadLeft(Currency.DecimalPlaces, '0'));
        }

        return builder.ToString();
    }

    private static string ApplyGrouping(string digits, string separator)
    {
        if (digits.Length <= 3)
        {
            return digits;
        }

        var builder = new StringBuilder(digits.Length + (separator.Length * (digits.Length / 3)));
        int leading = digits.Length % 3;
        if (leading == 0)
        {
            leading = 3;
        }

        builder.Append(digits, 0, leading);
        for (int i = leading; i < digits.Length; i += 3)
        {
            builder.Append(separator).Append(digits, i, 3);
        }

        return builder.ToString();
    }

    // -----------------------------------------------------------------------

    private static void EnsureDefined(Currency currency)
    {
        if (!currency.IsDefined)
        {
            throw new ArgumentException(
                "Money exige moeda definida; default(Currency) nao serve.", nameof(currency));
        }
    }

    private static void EnsureCurrency(Money value, Currency expected)
    {
        if (value.Currency != expected)
        {
            throw new InvalidOperationException(
                $"Esperava {expected} mas encontrou {value.Currency}.");
        }
    }

    private static void EnsureSameCurrency(Money left, Money right)
    {
        EnsureDefined(left.Currency);
        EnsureDefined(right.Currency);

        if (left.Currency != right.Currency)
        {
            throw new InvalidOperationException(
                $"Operacao entre moedas diferentes ({left.Currency} e {right.Currency}). "
                + "Conversao exige taxa explicita e conta de variacao cambial.");
        }
    }
}

/// <summary>Onde cai o residual de centavos numa reparticao inexata.</summary>
public enum RemainderPlacement
{
    /// <summary>Primeiras parcelas ficam um centavo maiores.</summary>
    First = 0,

    /// <summary>Ultimas parcelas ficam um centavo maiores.</summary>
    Last = 1,
}
