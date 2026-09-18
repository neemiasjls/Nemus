/**
 * Cliente da API.
 *
 * ONDE FICA O TOKEN DE SESSAO. Em localStorage. Ate a fase 4 ficava em
 * sessionStorage, que morre quando a aba fecha - fazia sentido quando a
 * credencial era um token aleatorio colado da configuracao do servidor.
 * Agora que a entrada e usuario e senha, expirar a cada aba fechada empurra
 * para senha curta e facil de redigitar, que e pior. O controle de verdade
 * passou para o servidor: a sessao vence em 30 dias e "Sair" a revoga na
 * hora, em todo lugar.
 *
 * O ideal seria cookie httpOnly, que o JavaScript nao consegue ler nem mesmo
 * se houver XSS. Nao da aqui sem custo alto: frontend e API ficam em
 * dominios diferentes (Cloudflare e Render), o que exige SameSite=None e
 * traz protecao contra CSRF junto. Com a CSP desta pagina - script so do
 * proprio dominio, nada inline - o bearer token e a escolha proporcional.
 */

const BASE_URL: string = (import.meta.env.VITE_NEMUS_API ?? '').replace(/\/+$/, '');
const TOKEN_KEY = 'nemus.session';

/** Chave antiga, do tempo do token unico. Nao serve mais para nada. */
const LEGACY_TOKEN_KEY = 'nemus.token';

export function getToken(): string | null {
  try {
    return localStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}

export function setToken(token: string): void {
  try {
    localStorage.setItem(TOKEN_KEY, token.trim());
  } catch {
    /* navegador com armazenamento bloqueado: segue sem lembrar */
  }
}

export function clearToken(): void {
  try {
    localStorage.removeItem(TOKEN_KEY);
    sessionStorage.removeItem(LEGACY_TOKEN_KEY);
  } catch {
    /* idem */
  }
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const token = getToken();

  const response = await fetch(`${BASE_URL}${path}`, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...(init?.headers ?? {}),
    },
  });

  if (response.status === 204) {
    return undefined as T;
  }

  const text = await response.text();
  const payload = text ? JSON.parse(text) : null;

  if (!response.ok) {
    throw new ApiError(
      response.status,
      payload?.code ?? 'error',
      payload?.message ?? 'Não foi possível completar a operação.',
    );
  }

  return payload as T;
}

// --- tipos espelhando os DTOs da API -------------------------------------
// Todo campo monetario termina em MinorUnits e e INTEIRO. Nao existe
// `number` decimal atravessando esta fronteira.

export interface AuthStatus {
  needsFirstAccess: boolean;
}

export interface SignInResult {
  token: string;
  username: string;
  expiresAt: string;
}

export interface SessionInfo {
  username: string;
  expiresAt: string;
  lastLoginAt: string | null;
}

export interface Account {
  id: string;
  name: string;
  type: string;
  isInternal: boolean;
  currencyCode: string;
  balanceMinorUnits: number;
  isOnBudget: boolean;
  isSystem: boolean;
}

export interface Category {
  id: string;
  parentId: string | null;
  name: string;
  kind: string;
  sortOrder: number;
}

export interface Entry {
  id: string;
  accountId: string;
  accountName: string;
  accountType: string;
  accountIsInternal: boolean;
  amountMinorUnits: number;
  categoryId: string | null;
  categoryName: string | null;
  memo: string | null;
}

export interface Transaction {
  id: string;
  occurredOn: string;
  description: string;
  currencyCode: string;
  kind: string;
  source: string;
  notes: string | null;
  entries: Entry[];
}

export interface TransactionPage {
  items: Transaction[];
  total: number;
  limit: number;
  offset: number;
}

export interface Integrity {
  isIntact: boolean;
  sumOfAllBalances: number;
  sumInternalBalances: number;
  sumExternalBalances: number;
  unbalancedTransactions: number;
  undersizedTransactions: number;
  emptyTransactions: number;
  brokenInstallmentPlans: number;
}

export interface FlowPoint {
  month: string;
  incomeMinorUnits: number;
  expenseMinorUnits: number;
}

export interface MonthlyFlow {
  currencyCode: string;
  months: FlowPoint[];
}

/**
 * Uma linha da tabela de tendência. `amountsMinorUnits` tem um valor por mês,
 * na mesma ordem de `CategoryTrend.months` — por isso os meses vêm uma vez
 * só, e não repetidos em cada linha.
 */
export interface CategoryTrendRow {
  categoryId: string | null;
  name: string;
  amountsMinorUnits: number[];
  totalMinorUnits: number;
  averageMinorUnits: number;
}

export interface CategoryTrend {
  currencyCode: string;
  months: string[];
  categories: CategoryTrendRow[];
}

/**
 * Um gasto fixo visto de dentro de um mês. `isMatched` é resultado de
 * comparação por categoria e valor, não confirmação de pagamento — por isso
 * a tela diz "já veio", e não "pago".
 */
export interface RecurringExpense {
  id: string;
  name: string;
  categoryId: string;
  categoryName: string;
  amountMinorUnits: number;
  currencyCode: string;
  dueDay: number;
  dueDate: string;
  accountId: string | null;
  accountName: string | null;
  isEstimate: boolean;
  startsOn: string;
  endsOn: string | null;
  isArchived: boolean;
  isMatched: boolean;
  transactionId: string | null;
  actualMinorUnits: number | null;
  occurredOn: string | null;
  differenceMinorUnits: number | null;
}

export interface RecurringMonth {
  month: string;
  currencyCode: string;
  expectedMinorUnits: number;
  matchedMinorUnits: number;
  pendingMinorUnits: number;
  items: RecurringExpense[];
}

export interface SaveRecurringInput {
  name: string;
  categoryId: string;
  amountMinorUnits: number;
  dueDay: number;
  accountId: string | null;
  isEstimate: boolean;
  startsOn: string;
  endsOn: string | null;
}

export interface AssignRecurringResult {
  month: string;
  filled: number;
  skipped: number;
  readyToAssignMinorUnits: number;
  assignedMinorUnits: number;
}

export interface NetWorth {
  currencyCode: string;
  assetsMinorUnits: number;
  liabilitiesMinorUnits: number;
  netWorthMinorUnits: number;
}

/** Uma categoria num mes. O que gastou e negativo. */
export interface BudgetCategory {
  categoryId: string;
  parentId: string | null;
  name: string;
  isArchived: boolean;
  assignedMinorUnits: number;
  activityMinorUnits: number;
  availableMinorUnits: number;
}

/**
 * O orcamento de um mes. isBalanced e a invariante do metodo, conferida no
 * servidor: disponivel + ainda sem destino = saldo das contas do orcamento.
 */
export interface BudgetMonth {
  month: string;
  currencyCode: string;
  readyToAssignMinorUnits: number;
  netInflowMinorUnits: number;
  assignedMinorUnits: number;
  activityMinorUnits: number;
  availableMinorUnits: number;
  onBudgetBalanceMinorUnits: number;
  isBalanced: boolean;
  uncategorizedTransactions: number;
  uncategorizedMinorUnits: number;
  categories: BudgetCategory[];
}

export interface CardTerms {
  accountId: string;
  closingDay: number;
  dueDay: number;
  creditLimitMinorUnits: number | null;
  paymentAccountId: string | null;
}

/** Uma compra parcelada com o andamento dela. */
export interface InstallmentPlan {
  id: string;
  cardAccountId: string;
  cardName: string;
  description: string;
  currencyCode: string;
  totalMinorUnits: number;
  financedMinorUnits: number;
  interestMinorUnits: number;
  installmentCount: number;
  paidCount: number;
  installmentMinorUnits: number;
  remainingMinorUnits: number;
  purchaseDate: string;
  firstStatementMonth: string;
  lastStatementMonth: string;
  isFinished: boolean;
}

export interface UpcomingCommitment {
  month: string;
  currencyCode: string;
  amountMinorUnits: number;
  planCount: number;
}

export interface ImportPreviewLine {
  occurredOn: string;
  description: string;
  amountMinorUnits: number;
}

export interface ImportPreview {
  statementAccountId: string | null;
  startsOn: string | null;
  endsOn: string | null;
  transactionCount: number;
  duplicatesWithinFile: number;
  fileAlreadyImported: boolean;
  sample: ImportPreviewLine[];
}

export interface ImportResult {
  inserted: number;
  skippedAsDuplicate: number;
  duplicatesWithinFile: number;
  total: number;
}

export const api = {
  health: () => request<{ status: string }>('/health'),

  // --- acesso ---------------------------------------------------------------

  authStatus: () => request<AuthStatus>('/api/auth/status'),

  login: (username: string, password: string) =>
    request<SignInResult>('/api/auth/login', {
      method: 'POST',
      body: JSON.stringify({ username, password }),
    }),

  /**
   * Primeiro acesso: so funciona enquanto nao ha usuario nenhum, e leva o
   * token de instalacao no cabecalho - o mesmo que esta na configuracao do
   * servidor. Depois disso este caminho fecha para sempre.
   */
  firstAccess: (username: string, password: string, bootstrapToken: string) =>
    request<SignInResult>('/api/auth/first-access', {
      method: 'POST',
      body: JSON.stringify({ username, password }),
      headers: { Authorization: `Bearer ${bootstrapToken.trim()}` },
    }),

  me: () => request<SessionInfo>('/api/auth/me'),

  logout: () => request<void>('/api/auth/logout', { method: 'POST' }),

  /** Devolve uma sessao nova: trocar a senha derruba todas as anteriores. */
  changePassword: (currentPassword: string, newPassword: string) =>
    request<SignInResult>('/api/auth/password', {
      method: 'POST',
      body: JSON.stringify({ currentPassword, newPassword }),
    }),

  // --- razao ----------------------------------------------------------------

  listAccounts: () => request<Account[]>('/api/accounts'),

  createAccount: (body: {
    name: string;
    type: string;
    currencyCode: string;
    institution?: string;
    isOnBudget: boolean;
    openingBalanceMinorUnits: number;
    openingBalanceDate?: string;
  }) => request<Account>('/api/accounts', { method: 'POST', body: JSON.stringify(body) }),

  listCategories: () => request<Category[]>('/api/categories'),

  createCategory: (body: { name: string; kind?: string; parentId?: string | null; sortOrder: number }) =>
    request<Category>('/api/categories', { method: 'POST', body: JSON.stringify(body) }),

  // --- cartao e parcelamento ------------------------------------------------

  listCards: () => request<CardTerms[]>('/api/accounts/cards'),

  saveCardTerms: (
    accountId: string,
    body: { closingDay: number; dueDay: number; creditLimitMinorUnits?: number | null; paymentAccountId?: string | null },
  ) =>
    request<CardTerms>(`/api/accounts/${encodeURIComponent(accountId)}/card-terms`, {
      method: 'PUT',
      body: JSON.stringify(body),
    }),

  listInstallments: (onlyOpen = false) =>
    request<InstallmentPlan[]>(`/api/installments${onlyOpen ? '?onlyOpen=true' : ''}`),

  upcomingInstallments: (months = 6) =>
    request<UpcomingCommitment[]>(`/api/installments/upcoming?months=${months}`),

  /**
   * Uma compra parcelada vira duas coisas de uma vez: a transacao do razao
   * (o cartao devendo tudo, hoje) e o cronograma das parcelas.
   */
  createInstallmentPurchase: (body: {
    cardAccountId: string;
    occurredOn: string;
    description: string;
    totalAmountMinorUnits: number;
    financedAmountMinorUnits?: number | null;
    installmentCount: number;
    categoryId?: string | null;
    interestCategoryId?: string | null;
  }) => request<unknown>('/api/installments', { method: 'POST', body: JSON.stringify(body) }),

  listTransactions: (params: { limit?: number; offset?: number; accountId?: string } = {}) => {
    const search = new URLSearchParams();
    if (params.limit) search.set('limit', String(params.limit));
    if (params.offset) search.set('offset', String(params.offset));
    if (params.accountId) search.set('accountId', params.accountId);
    const query = search.toString();
    return request<TransactionPage>(`/api/transactions${query ? `?${query}` : ''}`);
  },

  createExpense: (body: {
    occurredOn: string;
    description: string;
    accountId: string;
    amountMinorUnits: number;
    categoryId?: string | null;
  }) => request<unknown>('/api/transactions/expense', { method: 'POST', body: JSON.stringify(body) }),

  // Lancamento geral: N pernas somando zero. Usado para receita e para
  // transferencia; despesa tem o atalho acima.
  createTransaction: (body: {
    occurredOn: string;
    description: string;
    currencyCode: string;
    kind: 'STANDARD' | 'TRANSFER';
    notes?: string | null;
    entries: {
      accountId: string;
      amountMinorUnits: number;
      categoryId?: string | null;
      memo?: string | null;
    }[];
  }) => request<unknown>('/api/transactions', { method: 'POST', body: JSON.stringify(body) }),

  /**
   * Categoria de um lancamento que ja existe - o caminho do extrato
   * importado, que chega sem nenhuma. Nulo tira a categoria.
   */
  categorize: (id: string, categoryId: string | null) =>
    request<void>(`/api/transactions/${encodeURIComponent(id)}/category`, {
      method: 'PUT',
      body: JSON.stringify({ categoryId }),
    }),

  deleteTransaction: (id: string) =>
    request<void>(`/api/transactions/${id}`, { method: 'DELETE' }),

  // O arquivo vai como corpo cru, nao como multipart. Nao ha campo alem do
  // proprio arquivo, e multipart traria um parser a mais para nada.
  importOfx: (accountId: string, file: File, dryRun: boolean) =>
    request<ImportPreview | ImportResult>(
      `/api/import/ofx?accountId=${encodeURIComponent(accountId)}&dryRun=${dryRun}`,
      {
        method: 'POST',
        body: file,
        headers: { 'Content-Type': 'application/octet-stream' },
      },
    ),

  budget: (month: string) => request<BudgetMonth>(`/api/budget/${encodeURIComponent(month)}`),

  // Substitui o valor atribuido, nao soma: repetir o pedido nao dobra o
  // categoria. A resposta e o mes inteiro, porque o ainda sem destino muda junto.
  assign: (month: string, categoryId: string, amountMinorUnits: number) =>
    request<BudgetMonth>(
      `/api/budget/${encodeURIComponent(month)}/categories/${encodeURIComponent(categoryId)}`,
      { method: 'PUT', body: JSON.stringify({ amountMinorUnits }) },
    ),

  // Gastos fixos. O mês faz parte da pergunta: a lista sozinha é cadastro, e
  // cadastro não diz o que já veio e o que ainda falta.
  recurring: (month: string) =>
    request<RecurringMonth>(`/api/recurring/${encodeURIComponent(month)}`),

  createRecurring: (input: SaveRecurringInput) =>
    request<{ id: string }>('/api/recurring', { method: 'POST', body: JSON.stringify(input) }),

  updateRecurring: (id: string, input: SaveRecurringInput) =>
    request<{ id: string }>(`/api/recurring/${encodeURIComponent(id)}`, {
      method: 'PUT',
      body: JSON.stringify(input),
    }),

  // Arquiva por padrão; purge apaga de vez, para cadastro errado.
  removeRecurring: (id: string, purge = false) =>
    request<void>(`/api/recurring/${encodeURIComponent(id)}?purge=${purge}`, { method: 'DELETE' }),

  // Enche as categorias vazios do mês com o previsto. Não sobrescreve o que
  // já foi separado na mão.
  assignRecurring: (month: string) =>
    request<AssignRecurringResult>(`/api/recurring/${encodeURIComponent(month)}/assign`, {
      method: 'POST',
    }),

  netWorth: () => request<NetWorth[]>('/api/summary/net-worth'),

  integrity: () => request<Integrity>('/api/summary/integrity'),

  // Os dois relatorios somam no banco, sobre o razao inteiro. O navegador
  // recebe um numero por mes em vez de um lancamento por linha - por isso
  // nao dependem da pagina de lancamentos e nao param no teto dela.
  monthlyFlow: (until: string, months = 6) =>
    request<MonthlyFlow>(
      `/api/reports/monthly?until=${encodeURIComponent(until)}&months=${months}`,
    ),

  categoryTrend: (until: string, months = 6) =>
    request<CategoryTrend>(
      `/api/reports/categories?until=${encodeURIComponent(until)}&months=${months}`,
    ),
};
