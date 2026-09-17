namespace Nemus.Api.Contracts;

/// <summary>
/// DTOs de saida.
///
/// ITEM 17 DA AUDITORIA - APARAR A RESPOSTA. Cada campo aqui foi escolhido.
/// Serializar a entidade inteira e como uma resposta cresce sozinha: alguem
/// adiciona uma coluna interna num agregado e ela comeca a ser publicada sem
/// ninguem decidir isso.
///
/// Dinheiro sai em unidades minimas, inteiro, pela mesma razao que entra
/// assim. O cliente formata para exibir; o transporte permanece exato.
/// </summary>
public sealed record AccountResponse(
    Guid Id,
    string Name,
    string Type,
    bool IsInternal,
    string CurrencyCode,
    long BalanceMinorUnits,
    bool IsOnBudget,
    bool IsSystem);

/// <summary>
/// O que a tela de entrada precisa saber antes de decidir o que mostrar. E o
/// unico dado que sai da API sem sessao.
/// </summary>
public sealed record AuthStatusResponse(bool NeedsFirstAccess);

/// <summary>
/// Resposta do login. O token aparece AQUI e em nenhum outro lugar: o banco
/// guarda so o hash dele, e o servidor nao tem como mostra-lo de novo.
/// </summary>
public sealed record SignInResponse(string Token, string Username, DateTimeOffset ExpiresAt);

public sealed record SessionResponse(string Username, DateTimeOffset ExpiresAt, DateTimeOffset? LastLoginAt);

public sealed record CardTermsResponse(
    Guid AccountId,
    int ClosingDay,
    int DueDay,
    long? CreditLimitMinorUnits,
    Guid? PaymentAccountId);

/// <summary>
/// Um parcelamento como a tela le. PaidCount conta parcela cuja competencia
/// ja passou - nao e liquidacao conferida contra fatura, que chega quando a
/// importacao souber reconciliar.
/// </summary>
public sealed record InstallmentPlanResponse(
    Guid Id,
    Guid CardAccountId,
    string CardName,
    string Description,
    string CurrencyCode,
    long TotalMinorUnits,
    long FinancedMinorUnits,
    long InterestMinorUnits,
    int InstallmentCount,
    int PaidCount,
    long InstallmentMinorUnits,
    long RemainingMinorUnits,
    DateOnly PurchaseDate,
    string FirstStatementMonth,
    string LastStatementMonth,
    bool IsFinished);

public sealed record InstallmentCreatedResponse(
    Guid Id,
    Guid PurchaseTransactionId,
    int InstallmentCount,
    long InstallmentMinorUnits,
    long InterestMinorUnits,
    string FirstStatementMonth,
    DateOnly FirstDueDate);

/// <summary>Quanto de uma fatura futura ja esta comprometido antes de o mes comecar.</summary>
public sealed record UpcomingCommitmentResponse(
    string Month,
    string CurrencyCode,
    long AmountMinorUnits,
    int PlanCount);

public sealed record CategoryResponse(
    Guid Id,
    Guid? ParentId,
    string Name,
    string Kind,
    int SortOrder);

public sealed record EntryResponse(
    Guid Id,
    Guid AccountId,
    string AccountName,
    string AccountType,
    bool AccountIsInternal,
    long AmountMinorUnits,
    Guid? CategoryId,
    string? CategoryName,
    string? Memo);

public sealed record TransactionResponse(
    Guid Id,
    DateOnly OccurredOn,
    string Description,
    string CurrencyCode,
    string Kind,
    string Source,
    string? Notes,
    IReadOnlyList<EntryResponse> Entries);

/// <summary>
/// Resposta do POST. Deliberadamente menor que TransactionResponse: montar a
/// visao completa exigiria reler o lancamento com os joins de conta e
/// categoria, e o cliente recarrega a lista logo em seguida de qualquer
/// jeito. Preencher nome de conta com string vazia so para "ter o campo"
/// seria devolver dado falso.
/// </summary>
public sealed record TransactionCreatedResponse(
    Guid Id,
    DateOnly OccurredOn,
    string Description,
    string CurrencyCode,
    string Kind,
    long MagnitudeMinorUnits,
    int EntryCount);

public sealed record TransactionPageResponse(
    IReadOnlyList<TransactionResponse> Items,
    int Total,
    int Limit,
    int Offset);

public sealed record NetWorthResponse(
    string CurrencyCode,
    long AssetsMinorUnits,
    long LiabilitiesMinorUnits,
    long NetWorthMinorUnits);

/// <summary>
/// O retrato de v_ledger_integrity. Publicar isto e proposital: e a
/// afirmacao verificavel de que nenhum centavo entrou ou saiu do nada, e a
/// tela mostra isso para quem usa.
/// </summary>
public sealed record IntegrityResponse(
    bool IsIntact,
    long SumOfAllBalances,
    long SumInternalBalances,
    long SumExternalBalances,
    long UnbalancedTransactions,
    long UndersizedTransactions,
    long EmptyTransactions,
    long BrokenInstallmentPlans);

public sealed record BudgetCategoryResponse(
    Guid CategoryId,
    Guid? ParentId,
    string Name,
    bool IsArchived,
    long AssignedMinorUnits,
    long ActivityMinorUnits,
    long AvailableMinorUnits);

/// <summary>
/// O orcamento de um mes. IsBalanced publica a invariante do metodo:
/// disponivel nos envelopes + pronto para atribuir = saldo das contas do
/// orcamento, este ultimo lido direto do razao. A tela mostra a conta, como
/// faz com a soma zero do razao.
/// </summary>
public sealed record BudgetMonthResponse(
    string Month,
    string CurrencyCode,
    long ReadyToAssignMinorUnits,
    long NetInflowMinorUnits,
    long AssignedMinorUnits,
    long ActivityMinorUnits,
    long AvailableMinorUnits,
    long OnBudgetBalanceMinorUnits,
    bool IsBalanced,
    int UncategorizedTransactions,
    long UncategorizedMinorUnits,
    IReadOnlyList<BudgetCategoryResponse> Categories);

public sealed record ImportPreviewLine(
    DateOnly OccurredOn,
    string Description,
    long AmountMinorUnits);

/// <summary>
/// Simulacao da importacao: o que aconteceria, sem ter acontecido. Conferir
/// antes de mexer no razao e barato; desfazer importacao errada nao e.
/// </summary>
public sealed record ImportPreviewResponse(
    string? StatementAccountId,
    DateOnly? StartsOn,
    DateOnly? EndsOn,
    int TransactionCount,
    int DuplicatesWithinFile,
    bool FileAlreadyImported,
    IReadOnlyList<ImportPreviewLine> Sample);

/// <summary>
/// PILAR 3 visivel para quem usa. Reimportar o mesmo arquivo devolve
/// Inserted = 0 e SkippedAsDuplicate = total: nada duplicou, e da para ver
/// que nada duplicou.
/// </summary>
public sealed record ImportResultResponse(
    int Inserted,
    int SkippedAsDuplicate,
    int DuplicatesWithinFile,
    int Total);

public sealed record MonthFlowResponse(
    string Month,
    long IncomeMinorUnits,
    long ExpenseMinorUnits);

/// <summary>
/// Entrada e saida mes a mes, somadas pelo banco sobre o razao inteiro - nao
/// sobre a pagina que a tela por acaso carregou.
/// </summary>
public sealed record MonthlyFlowResponse(
    string CurrencyCode,
    IReadOnlyList<MonthFlowResponse> Months);

/// <summary>
/// Uma linha da tabela de tendencia. <see cref="AmountsMinorUnits"/> tem um
/// valor por mes, na mesma ordem de <see cref="CategoryTrendResponse.Months"/>
/// - por isso a lista de meses vem uma vez so, e nao repetida em cada linha.
/// </summary>
public sealed record CategoryTrendRowResponse(
    Guid? CategoryId,
    string Name,
    IReadOnlyList<long> AmountsMinorUnits,
    long TotalMinorUnits,
    long AverageMinorUnits);

public sealed record CategoryTrendResponse(
    string CurrencyCode,
    IReadOnlyList<string> Months,
    IReadOnlyList<CategoryTrendRowResponse> Categories);

/// <summary>
/// Um gasto fixo visto de dentro de um mes: o cadastro mais o cruzamento com
/// o razao. <see cref="IsMatched"/> e resultado de comparacao por categoria e
/// valor, nao confirmacao de pagamento - por isso a tela diz "ja veio", e nao
/// "pago".
/// </summary>
public sealed record RecurringExpenseResponse(
    Guid Id,
    string Name,
    Guid CategoryId,
    string CategoryName,
    long AmountMinorUnits,
    string CurrencyCode,
    int DueDay,
    DateOnly DueDate,
    Guid? AccountId,
    string? AccountName,
    bool IsEstimate,
    DateOnly StartsOn,
    DateOnly? EndsOn,
    bool IsArchived,
    bool IsMatched,
    Guid? TransactionId,
    long? ActualMinorUnits,
    DateOnly? OccurredOn,
    long? DifferenceMinorUnits);

/// <summary>
/// O mes inteiro de gastos fixos. Os tres totais existem porque a pergunta
/// nunca e so "quanto", e sim "quanto ja saiu e quanto ainda vem".
/// </summary>
public sealed record RecurringMonthResponse(
    string Month,
    string CurrencyCode,
    long ExpectedMinorUnits,
    long MatchedMinorUnits,
    long PendingMinorUnits,
    IReadOnlyList<RecurringExpenseResponse> Items);

/// <summary>
/// ITEM 15 DA AUDITORIA - NAO VAZAR CONTEUDO. So codigo estavel e mensagem
/// escrita para ser lida por quem usa. Nunca stack trace, nunca SQL, nunca
/// nome de tabela, nunca o texto cru que o usuario mandou de volta.
/// </summary>
public sealed record ErrorResponse(string Code, string Message);
