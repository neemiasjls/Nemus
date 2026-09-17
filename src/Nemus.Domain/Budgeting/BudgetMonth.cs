using System.Globalization;
using Nemus.Domain.Primitives;

namespace Nemus.Domain.Budgeting;

/// <summary>
/// Um mes de orcamento. Orcamento e por mes, nao por data - dai um tipo
/// proprio em vez de DateOnly solto, que deixaria passar "15 de marco" onde
/// so faz sentido "marco".
/// </summary>
public readonly record struct BudgetMonth : IComparable<BudgetMonth>
{
    private BudgetMonth(int year, int month)
    {
        Year = year;
        Month = month;
    }

    public int Year { get; }
    public int Month { get; }

    public DateOnly FirstDay => new(Year, Month, 1);

    /// <summary>Primeiro dia do mes seguinte: o limite exclusivo de "ate o fim deste mes".</summary>
    public DateOnly NextFirstDay => FirstDay.AddMonths(1);

    public BudgetMonth Previous => Of(FirstDay.AddMonths(-1));
    public BudgetMonth Next => Of(NextFirstDay);

    public static BudgetMonth Of(DateOnly date) => new(date.Year, date.Month);

    /// <summary>Le "2026-09". Recusa qualquer outra forma, inclusive "2026-9".</summary>
    public static Result<BudgetMonth> Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !DateOnly.TryParseExact(
                text.Trim() + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly first))
        {
            return new Error("budget.month_invalid", "Mes invalido. Use o formato AAAA-MM, por exemplo 2026-09.");
        }

        return Of(first);
    }

    public bool Contains(DateOnly date) => date.Year == Year && date.Month == Month;

    public int CompareTo(BudgetMonth other) =>
        Year != other.Year ? Year.CompareTo(other.Year) : Month.CompareTo(other.Month);

    public static bool operator <(BudgetMonth left, BudgetMonth right) => left.CompareTo(right) < 0;
    public static bool operator >(BudgetMonth left, BudgetMonth right) => left.CompareTo(right) > 0;
    public static bool operator <=(BudgetMonth left, BudgetMonth right) => left.CompareTo(right) <= 0;
    public static bool operator >=(BudgetMonth left, BudgetMonth right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month:D2}");
}
