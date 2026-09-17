using Nemus.Domain.Monetary;
using Nemus.Domain.Primitives;

namespace Nemus.Domain.Ledger;

/// <summary>
/// PILAR 1 - raiz de agregado do razao.
///
/// Nao ha construtor publico, nao ha setter de pernas, nao ha AddEntry
/// avulso. O unico caminho para existir uma Transaction passa por
/// <see cref="Create"/>, que valida antes de construir. Consequencia: uma
/// transacao desbalanceada nao e representavel - nao existe sequencia de
/// chamadas que produza uma.
///
/// Essa e a diferenca entre validar e tornar impossivel. Validar depende de
/// alguem lembrar de chamar; impossivel nao depende de ninguem.
/// </summary>
public sealed class Transaction
{
    /// <summary>
    /// Teto de sanidade. Compra dividida real tem uma dezena de pernas;
    /// milhares indicam bug de importacao, nao caso de uso.
    /// </summary>
    public const int MaxEntries = 256;

    private readonly List<Entry> _entries;

    private Transaction(
        Guid id,
        DateOnly occurredOn,
        DateTimeOffset? bookedAt,
        string description,
        Guid? payeeId,
        Currency currency,
        TransactionKind kind,
        string? notes,
        ExternalReference? external,
        Guid? importBatchId,
        DateTimeOffset createdAt,
        List<Entry> entries)
    {
        Id = id;
        OccurredOn = occurredOn;
        BookedAt = bookedAt;
        Description = description;
        PayeeId = payeeId;
        Currency = currency;
        Kind = kind;
        Notes = notes;
        External = external;
        ImportBatchId = importBatchId;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        _entries = entries;
    }

    public Guid Id { get; }

    /// <summary>Competencia: quando o fato aconteceu. E a data do orcamento.</summary>
    public DateOnly OccurredOn { get; private set; }

    /// <summary>Liquidacao bancaria, quando conhecida.</summary>
    public DateTimeOffset? BookedAt { get; private set; }

    public string Description { get; private set; }
    public Guid? PayeeId { get; private set; }
    public Currency Currency { get; }
    public TransactionKind Kind { get; }
    public string? Notes { get; private set; }

    /// <summary>Nulo em lancamento manual; presente em tudo que foi importado.</summary>
    public ExternalReference? External { get; }

    public Guid? ImportBatchId { get; }
    public ImportSource Source => External?.Source ?? ImportSource.Manual;

    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? DeletedAt { get; private set; }
    public bool IsDeleted => DeletedAt.HasValue;

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>Sempre zero. Se algum dia nao for, ha bug neste arquivo.</summary>
    public Money Balance => Money.Sum(_entries.Select(entry => entry.Amount), Currency);

    /// <summary>Modulo do movimento: soma das pernas positivas.</summary>
    public Money Magnitude => Money.Sum(
        _entries.Where(entry => entry.Amount.IsPositive).Select(entry => entry.Amount),
        Currency);

    // -----------------------------------------------------------------------

    public static Result<Transaction> Create(TransactionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        Result validation = Validate(draft.Description, draft.Currency, draft.Entries, draft.Kind);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        Guid id = draft.Id ?? UuidV7.NewGuid();
        DateTimeOffset createdAt = draft.CreatedAt ?? DateTimeOffset.UtcNow;

        return new Transaction(
            id,
            draft.OccurredOn,
            draft.BookedAt,
            draft.Description.Trim(),
            draft.PayeeId,
            draft.Currency,
            draft.Kind,
            string.IsNullOrWhiteSpace(draft.Notes) ? null : draft.Notes.Trim(),
            draft.External,
            draft.ImportBatchId,
            createdAt,
            MaterializeEntries(id, draft.Entries));
    }

    /// <summary>
    /// Despesa simples: sai da conta, entra na contraparte externa.
    /// Duas pernas, fechando em zero - a contraparte existe justamente para
    /// que o dinheiro nunca "suma do sistema".
    /// </summary>
    public static Result<Transaction> Spend(
        DateOnly occurredOn,
        string description,
        Guid fromAccountId,
        Guid toExternalAccountId,
        Money amount,
        Guid? categoryId = null,
        Guid? payeeId = null,
        ExternalReference? external = null,
        DateTimeOffset? createdAt = null)
    {
        if (!amount.IsPositive)
        {
            return new Error("ledger.spend_amount_positive",
                "Informe o valor gasto como positivo; o sinal das pernas e responsabilidade do razao.");
        }

        return Create(new TransactionDraft
        {
            OccurredOn = occurredOn,
            Description = description,
            Currency = amount.Currency,
            PayeeId = payeeId,
            External = external,
            CreatedAt = createdAt,
            Entries =
            [
                new EntryDraft(fromAccountId, amount.Negated),
                new EntryDraft(toExternalAccountId, amount) { CategoryId = categoryId },
            ],
        });
    }

    /// <summary>Transferencia entre contas internas. Nao e despesa nem receita.</summary>
    public static Result<Transaction> Transfer(
        DateOnly occurredOn,
        string description,
        Guid fromAccountId,
        Guid toAccountId,
        Money amount,
        ExternalReference? external = null,
        DateTimeOffset? createdAt = null)
    {
        if (!amount.IsPositive)
        {
            return new Error("ledger.transfer_amount_positive",
                "Informe o valor transferido como positivo.");
        }

        if (fromAccountId == toAccountId)
        {
            return new Error("ledger.transfer_same_account",
                "Origem e destino da transferencia sao a mesma conta.");
        }

        return Create(new TransactionDraft
        {
            OccurredOn = occurredOn,
            Description = description,
            Currency = amount.Currency,
            Kind = TransactionKind.Transfer,
            External = external,
            CreatedAt = createdAt,
            Entries =
            [
                new EntryDraft(fromAccountId, amount.Negated),
                new EntryDraft(toAccountId, amount),
            ],
        });
    }

    /// <summary>
    /// Saldo de abertura contra a conta de patrimonio. Sem isso, o saldo
    /// inicial de uma conta seria dinheiro aparecendo do nada e a soma
    /// global deixaria de fechar em zero.
    /// </summary>
    public static Result<Transaction> OpeningBalance(
        DateOnly occurredOn,
        Guid accountId,
        Guid equityAccountId,
        Money amount,
        string description = "Saldo inicial",
        DateTimeOffset? createdAt = null)
    {
        if (amount.IsZero)
        {
            return new Error("ledger.opening_balance_zero",
                "Saldo de abertura zero nao precisa de lancamento.");
        }

        return Create(new TransactionDraft
        {
            OccurredOn = occurredOn,
            Description = description,
            Currency = amount.Currency,
            Kind = TransactionKind.OpeningBalance,
            CreatedAt = createdAt,
            Entries =
            [
                new EntryDraft(accountId, amount),
                new EntryDraft(equityAccountId, amount.Negated),
            ],
        });
    }

    /// <summary>
    /// Reconstroi a partir do banco. Revalida de proposito: se o dado
    /// gravado estiver corrompido, a leitura falha alto em vez de propagar
    /// um razao quebrado para dentro da aplicacao.
    /// </summary>
    public static Result<Transaction> Rehydrate(
        Guid id,
        DateOnly occurredOn,
        DateTimeOffset? bookedAt,
        string description,
        Guid? payeeId,
        Currency currency,
        TransactionKind kind,
        string? notes,
        ExternalReference? external,
        Guid? importBatchId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? deletedAt,
        IReadOnlyList<EntryDraft> entries)
    {
        Result validation = Validate(description, currency, entries, kind);
        if (validation.IsFailure)
        {
            return validation.Error;
        }

        return new Transaction(
            id, occurredOn, bookedAt, description, payeeId, currency, kind,
            notes, external, importBatchId, createdAt,
            MaterializeEntries(id, entries))
        {
            UpdatedAt = updatedAt,
            DeletedAt = deletedAt,
        };
    }

    // -----------------------------------------------------------------------

    /// <summary>
    /// Troca todas as pernas de uma vez. Nao existe "adicionar uma perna":
    /// qualquer mudanca de valor reescreve o conjunto inteiro e revalida,
    /// entao a transacao nunca passa por um estado intermediario invalido.
    /// </summary>
    public Result ReplaceEntries(IReadOnlyList<EntryDraft> entries, DateTimeOffset? at = null)
    {
        Result validation = Validate(Description, Currency, entries, Kind);
        if (validation.IsFailure)
        {
            return validation;
        }

        _entries.Clear();
        _entries.AddRange(MaterializeEntries(Id, entries));
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
        return Result.Success();
    }

    public Result Describe(string description, DateTimeOffset? at = null)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return LedgerErrors.DescriptionRequired();
        }

        Description = description.Trim();
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
        return Result.Success();
    }

    public void Reschedule(DateOnly occurredOn, DateTimeOffset? at = null)
    {
        OccurredOn = occurredOn;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }

    public void AssignPayee(Guid? payeeId, DateTimeOffset? at = null)
    {
        PayeeId = payeeId;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }

    public void MarkBooked(DateTimeOffset bookedAt, DateTimeOffset? at = null)
    {
        BookedAt = bookedAt;
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }

    public void Annotate(string? notes, DateTimeOffset? at = null)
    {
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        UpdatedAt = at ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Exclusao logica. A transacao inteira sai junto, com as duas pernas,
    /// entao a soma global continua zero. Nunca apagar perna isolada.
    /// </summary>
    public Result SoftDelete(DateTimeOffset? at = null)
    {
        if (IsDeleted)
        {
            return LedgerErrors.AlreadyDeleted();
        }

        DateTimeOffset now = at ?? DateTimeOffset.UtcNow;
        DeletedAt = now;
        UpdatedAt = now;
        return Result.Success();
    }

    // -----------------------------------------------------------------------

    private static Result Validate(
        string description,
        Currency currency,
        IReadOnlyList<EntryDraft>? entries,
        TransactionKind kind)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return LedgerErrors.DescriptionRequired();
        }

        if (!currency.IsDefined)
        {
            return LedgerErrors.CurrencyRequired();
        }

        if (entries is null || entries.Count < 2)
        {
            return LedgerErrors.TooFewEntries(entries?.Count ?? 0);
        }

        if (entries.Count > MaxEntries)
        {
            return LedgerErrors.TooManyEntries(entries.Count);
        }

        if (kind == TransactionKind.Transfer && entries.Count != 2)
        {
            return LedgerErrors.TransferNeedsTwoEntries(entries.Count);
        }

        if (kind == TransactionKind.OpeningBalance && entries.Count != 2)
        {
            return LedgerErrors.OpeningBalanceNeedsTwoEntries(entries.Count);
        }

        long residual = 0;
        for (int index = 0; index < entries.Count; index++)
        {
            EntryDraft entry = entries[index];

            if (!entry.Amount.Currency.IsDefined || entry.Amount.Currency != currency)
            {
                return LedgerErrors.CurrencyMismatch(
                    index,
                    currency.Code,
                    entry.Amount.Currency.IsDefined ? entry.Amount.Currency.Code : "(indefinida)");
            }

            if (entry.Amount.IsZero)
            {
                return LedgerErrors.ZeroAmountEntry(index);
            }

            try
            {
                residual = checked(residual + entry.Amount.MinorUnits);
            }
            catch (OverflowException)
            {
                return new Error("ledger.overflow",
                    "A soma das pernas excede a faixa de BIGINT.");
            }
        }

        // O coracao do pilar 1.
        return residual != 0
            ? LedgerErrors.Unbalanced(residual, currency.Code)
            : Result.Success();
    }

    private static List<Entry> MaterializeEntries(Guid transactionId, IReadOnlyList<EntryDraft> drafts)
    {
        var entries = new List<Entry>(drafts.Count);
        for (int index = 0; index < drafts.Count; index++)
        {
            EntryDraft draft = drafts[index];
            entries.Add(new Entry(
                draft.Id ?? UuidV7.NewGuid(),
                transactionId,
                draft.AccountId,
                draft.Amount,
                draft.CategoryId,
                string.IsNullOrWhiteSpace(draft.Memo) ? null : draft.Memo.Trim(),
                (short)index));
        }

        return entries;
    }

    public override string ToString() =>
        $"{OccurredOn:yyyy-MM-dd} {Description} ({Magnitude} {Currency}, {_entries.Count} pernas)";
}

/// <summary>
/// Descricao de uma transacao antes de ela existir.
///
/// NAO LIGUE ISTO DIRETO A CORPO DE REQUISICAO HTTP.
///
/// Este tipo e de uso interno e existe para o repositorio e para os casos de
/// uso, nao para desserializar JSON de cliente. Quatro campos aqui sao
/// perigosos nas maos de quem manda a requisicao:
///
///   Id             deixa o cliente escolher a chave primaria, e portanto
///                  colidir de proposito com transacao existente.
///   CreatedAt      permite forjar a trilha de auditoria.
///   ImportBatchId  pendura a transacao num lote de importacao alheio.
///   External       o pior deles. Quem controla a identidade externa
///                  controla o indice de idempotencia: da para envenenar a
///                  chave e fazer com que um lancamento legitimo do banco
///                  seja descartado como duplicata na proxima importacao.
///                  O dinheiro some do extrato sem nenhum erro aparecer.
///
/// Na fase 2, a API deve receber um DTO proprio, com apenas os campos que o
/// usuario tem direito de informar (data, descricao, valor, categoria,
/// favorecido), e montar o TransactionDraft no servidor. Id, CreatedAt e
/// External sao decisao do servidor, sempre.
/// </summary>
public sealed record TransactionDraft
{
    public Guid? Id { get; init; }
    public required DateOnly OccurredOn { get; init; }
    public required string Description { get; init; }
    public required Currency Currency { get; init; }
    public required IReadOnlyList<EntryDraft> Entries { get; init; }

    public TransactionKind Kind { get; init; } = TransactionKind.Standard;
    public Guid? PayeeId { get; init; }
    public DateTimeOffset? BookedAt { get; init; }
    public string? Notes { get; init; }
    public ExternalReference? External { get; init; }
    public Guid? ImportBatchId { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
}
