namespace Nemus.Api.Contracts;

/// <summary>
/// DTOs de entrada. Sao tipos PROPRIOS da borda HTTP, deliberadamente
/// separados dos drafts do dominio.
///
/// ITEM 8 DA AUDITORIA - MASS ASSIGNMENT. TransactionDraft tem Id, CreatedAt,
/// ImportBatchId e External. Ligar aquele tipo direto ao corpo da requisicao
/// entregaria ao cliente:
///
///   Id             escolher a chave primaria
///   CreatedAt      forjar a trilha de auditoria
///   ImportBatchId  pendurar o lancamento num lote alheio
///   External       o pior. Quem controla a identidade externa controla o
///                  indice de idempotencia: da para forjar a chave de uma
///                  linha que o banco ainda vai mandar, e a importacao
///                  seguinte descarta o lancamento verdadeiro como
///                  duplicata. O dinheiro sumiria do extrato sem erro nenhum
///                  aparecer.
///
/// Nenhum destes campos existe abaixo. O que o cliente nao pode informar,
/// ele nao tem como informar - a defesa e o formato do tipo, nao uma
/// validacao que alguem pode esquecer de chamar.
///
/// DINHEIRO NA REDE. Todo valor trafega em unidades minimas, como INTEIRO.
/// JSON nao distingue inteiro de real, e praticamente todo parser de
/// JavaScript le 1234.56 como ponto flutuante de 64 bits. Aceitar decimal
/// aqui furaria o pilar 2 na fronteira, depois de todo o cuidado que ele
/// recebe por dentro. 1234 significa R$ 12,34.
/// </summary>
public sealed record CreateAccountRequest
{
    public string? Name { get; init; }

    /// <summary>ASSET, LIABILITY, EQUITY, REVENUE ou EXPENSE.</summary>
    public string? Type { get; init; }

    public string? CurrencyCode { get; init; }
    public string? Institution { get; init; }
    public bool IsOnBudget { get; init; } = true;

    /// <summary>
    /// Saldo inicial em unidades minimas. Vira uma transacao de abertura
    /// contra a conta de patrimonio - dinheiro nao aparece do nada nem
    /// quando e o primeiro registro da conta.
    /// </summary>
    public long OpeningBalanceMinorUnits { get; init; }

    public DateOnly? OpeningBalanceDate { get; init; }
}

/// <summary>
/// Entrar, e tambem criar o primeiro acesso. A senha viaja no CORPO, nunca na
/// URL: caminho e query aparecem em log de servidor, historico de navegador e
/// cabecalho Referer.
/// </summary>
public sealed record SignInRequest
{
    public string? Username { get; init; }
    public string? Password { get; init; }
}

public sealed record ChangePasswordRequest
{
    public string? CurrentPassword { get; init; }
    public string? NewPassword { get; init; }
}

public sealed record CreateCategoryRequest
{
    public string? Name { get; init; }

    /// <summary>EXPENSE ou INCOME. Ignorado quando ParentId vem preenchido: subcategoria herda o tipo do pai.</summary>
    public string? Kind { get; init; }

    public Guid? ParentId { get; init; }
    public int SortOrder { get; init; }
}

public sealed record CreateEntryRequest
{
    public Guid AccountId { get; init; }

    /// <summary>Unidades minimas, com sinal. Negativo saiu, positivo entrou.</summary>
    public long AmountMinorUnits { get; init; }

    public Guid? CategoryId { get; init; }
    public string? Memo { get; init; }
}

/// <summary>
/// Lancamento manual em forma geral: N pernas somando zero. O dominio
/// recusa qualquer conjunto que nao feche.
/// </summary>
public sealed record CreateTransactionRequest
{
    public DateOnly OccurredOn { get; init; }
    public string? Description { get; init; }
    public string? CurrencyCode { get; init; }
    public string? Notes { get; init; }

    /// <summary>STANDARD ou TRANSFER. Os demais tipos nascem de fluxo proprio, nao de lancamento manual.</summary>
    public string? Kind { get; init; }

    public IReadOnlyList<CreateEntryRequest>? Entries { get; init; }
}

/// <summary>
/// Fechamento e vencimento de um cartao. Sem eles o parcelamento nao tem como
/// dizer em qual fatura cada parcela cai.
/// </summary>
public sealed record CardTermsRequest
{
    public int ClosingDay { get; init; }
    public int DueDay { get; init; }
    public long? CreditLimitMinorUnits { get; init; }
    public Guid? PaymentAccountId { get; init; }
}

/// <summary>
/// Uma compra parcelada. O servidor monta as duas metades: a transacao do
/// razao (o cartao devendo tudo, hoje) e o cronograma de parcelas.
///
/// FinancedAmountMinorUnits so precisa vir quando ha juros - e a soma do que
/// voce vai pagar. Sem ele, "sem juros": financiado igual ao preco a vista.
/// </summary>
public sealed record CreateInstallmentPurchaseRequest
{
    public Guid CardAccountId { get; init; }
    public DateOnly OccurredOn { get; init; }
    public string? Description { get; init; }
    public long TotalAmountMinorUnits { get; init; }
    public long? FinancedAmountMinorUnits { get; init; }
    public int InstallmentCount { get; init; }
    public Guid? CategoryId { get; init; }

    /// <summary>Categoria do juro, quando ha. Separada de proposito: juro nao e o produto.</summary>
    public Guid? InterestCategoryId { get; init; }
}

/// <summary>
/// Categoria de um lancamento que ja existe. Nulo tira a categoria - e o
/// caminho de volta, para quem categorizou errado.
/// </summary>
public sealed record CategorizeRequest
{
    public Guid? CategoryId { get; init; }
}

/// <summary>
/// Valor atribuido a um envelope num mes. Substitui o anterior, nao soma:
/// quem manda a mesma requisicao duas vezes chega ao mesmo estado. Negativo
/// e permitido - e como se tira dinheiro de um envelope. Zero limpa.
/// </summary>
public sealed record SetAssignmentRequest
{
    public long AmountMinorUnits { get; init; }
    public string? CurrencyCode { get; init; }
}

/// <summary>
/// Atalho para o caso mais comum: uma despesa. Duas pernas montadas pelo
/// servidor contra a conta de despesa externa, para o cliente nao precisar
/// entender partidas dobradas so para registrar um cafe.
/// </summary>
public sealed record CreateExpenseRequest
{
    public DateOnly OccurredOn { get; init; }
    public string? Description { get; init; }
    public Guid AccountId { get; init; }

    /// <summary>Positivo. O sinal das pernas e responsabilidade do razao.</summary>
    public long AmountMinorUnits { get; init; }

    public Guid? CategoryId { get; init; }
    public string? Notes { get; init; }
}

/// <summary>
/// Um gasto fixo. Sem id no corpo: criar e POST, alterar e PUT na rota que ja
/// carrega o id — repetir o mesmo PUT deixa o mesmo estado.
/// </summary>
public sealed record SaveRecurringExpenseRequest
{
    public string? Name { get; init; }
    public Guid CategoryId { get; init; }

    /// <summary>Positivo. Gasto fixo negativo seria receita disfarcada.</summary>
    public long AmountMinorUnits { get; init; }

    public string? CurrencyCode { get; init; }

    /// <summary>1 a 31. O 31 quer dizer "ultimo dia do mes".</summary>
    public int DueDay { get; init; }

    public Guid? AccountId { get; init; }

    /// <summary>Luz e agua mudam todo mes; aluguel nao.</summary>
    public bool IsEstimate { get; init; }

    public DateOnly StartsOn { get; init; }
    public DateOnly? EndsOn { get; init; }
}
