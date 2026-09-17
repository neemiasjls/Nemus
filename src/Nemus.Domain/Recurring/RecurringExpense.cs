using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;

namespace Nemus.Domain.Recurring;

/// <summary>
/// Um gasto que se repete todo mes: aluguel, internet, assinatura.
///
/// E PREVISAO, NAO LANCAMENTO. Nada aqui toca saldo. O gasto de verdade
/// chega pelo extrato ou pela mao, como qualquer outro, e a tela cruza os
/// dois - ver <see cref="RecurringMatch"/>. Criar a transacao sozinho no dia
/// do vencimento faria o saldo mentir ate a data real e duplicaria a despesa
/// quando o OFX entrasse.
/// </summary>
public sealed class RecurringExpense
{
    public const int MaxNameLength = 120;

    /// <summary>Mesmo teto de sanidade do envelope: R$ 1 bilhao.</summary>
    public const long MaxAmountMinorUnits = 100_000_000_000;

    private RecurringExpense(
        Guid id,
        string name,
        Guid categoryId,
        Money amount,
        int dueDay,
        Guid? accountId,
        bool isEstimate,
        DateOnly startsOn,
        DateOnly? endsOn,
        DateTimeOffset? archivedAt)
    {
        Id = id;
        Name = name;
        CategoryId = categoryId;
        Amount = amount;
        DueDay = dueDay;
        AccountId = accountId;
        IsEstimate = isEstimate;
        StartsOn = startsOn;
        EndsOn = endsOn;
        ArchivedAt = archivedAt;
    }

    public Guid Id { get; }
    public string Name { get; }
    public Guid CategoryId { get; }
    public Money Amount { get; }

    /// <summary>1 a 31. Aparado para o mes real por <see cref="DueDateIn"/>.</summary>
    public int DueDay { get; }

    /// <summary>De onde costuma sair. Opcional: serve para lembrar, nao para validar.</summary>
    public Guid? AccountId { get; }

    /// <summary>Luz e agua mudam todo mes; aluguel nao.</summary>
    public bool IsEstimate { get; }

    public DateOnly StartsOn { get; }
    public DateOnly? EndsOn { get; }
    public DateTimeOffset? ArchivedAt { get; }

    public bool IsArchived => ArchivedAt is not null;

    /// <summary>
    /// A data de vencimento dentro de um mes concreto.
    ///
    /// Dia 31 e a forma de dizer "ultimo dia do mes": em fevereiro vira 28, em
    /// abril 30. Recusar o dia 31 no cadastro obrigaria quem paga no ultimo dia
    /// a escolher o dia 28 e errar em dez meses do ano.
    /// </summary>
    public DateOnly DueDateIn(int year, int month) =>
        new(year, month, Math.Min(DueDay, DateTime.DaysInMonth(year, month)));

    /// <summary>
    /// Vale neste mes? Fora do periodo - ou arquivado - o gasto nao entra na
    /// previsao, mas continua existindo para explicar os meses passados.
    /// </summary>
    public bool IsActiveIn(int year, int month)
    {
        if (IsArchived)
        {
            return false;
        }

        var firstDay = new DateOnly(year, month, 1);
        DateOnly lastDay = firstDay.AddMonths(1).AddDays(-1);

        return StartsOn <= lastDay && (EndsOn is null || EndsOn >= firstDay);
    }

    public static Result<RecurringExpense> Create(
        string name,
        Guid categoryId,
        Money amount,
        int dueDay,
        DateOnly startsOn,
        Guid? accountId = null,
        bool isEstimate = false,
        DateOnly? endsOn = null,
        Guid? id = null,
        DateTimeOffset? archivedAt = null)
    {
        string trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            return new Error("recurring.name_required", "De um nome ao gasto fixo, por exemplo \"Aluguel\".");
        }

        if (trimmed.Length > MaxNameLength)
        {
            return new Error(
                "recurring.name_too_long", $"O nome do gasto fixo passa de {MaxNameLength} caracteres.");
        }

        if (categoryId == Guid.Empty)
        {
            return new Error("recurring.category_required", "Escolha a categoria de despesa do gasto fixo.");
        }

        if (!amount.Currency.IsDefined)
        {
            return new Error("recurring.currency_required", "Gasto fixo precisa de moeda definida.");
        }

        // Ao contrario do envelope, aqui negativo nao quer dizer nada: um
        // gasto fixo de valor negativo seria uma receita disfarcada.
        if (!amount.IsPositive)
        {
            return new Error("recurring.amount_positive", "O valor do gasto fixo tem que ser maior que zero.");
        }

        if (amount.MinorUnits > MaxAmountMinorUnits)
        {
            return new Error(
                "recurring.amount_out_of_range",
                "Valor fora do limite de um gasto fixo. Confira se nao sobrou zero no fim.");
        }

        if (dueDay is < 1 or > 31)
        {
            return new Error("recurring.due_day_range", "O dia do vencimento vai de 1 a 31.");
        }

        if (endsOn is not null && endsOn < startsOn)
        {
            return new Error("recurring.period_inverted", "A data de fim nao pode ser anterior a de inicio.");
        }

        return new RecurringExpense(
            id ?? UuidV7.NewGuid(),
            trimmed,
            categoryId,
            amount,
            dueDay,
            accountId,
            isEstimate,
            startsOn,
            endsOn,
            archivedAt);
    }
}
