import type {
  Account,
  BudgetMonth,
  Category,
  Integrity,
  MonthlyFlow,
  NetWorth,
  SessionInfo,
  Transaction,
} from './api';
import type { IconName } from '../components/Icon';

export type PageId =
  | 'overview'
  | 'budget'
  | 'transactions'
  | 'reports'
  | 'recurring'
  | 'categories'
  | 'installments'
  | 'accounts'
  | 'import'
  | 'password';

export interface PageDefinition {
  id: PageId;
  /** Aparece na barra de endereco (#/lancamentos), entao fica em portugues. */
  slug: string;
  label: string;
  icon: IconName;
}

/*
 * A ordem do menu segue o uso: o produto e organizar o gasto do mes, entao
 * orcamento, lancamentos e categorias vem antes de contas e importacao.
 */
export const PAGES: PageDefinition[] = [
  { id: 'overview', slug: 'visao-geral', label: 'Visão geral', icon: 'dashboard' },
  { id: 'budget', slug: 'orcamento', label: 'Orçamento', icon: 'budget' },
  { id: 'transactions', slug: 'lancamentos', label: 'Lançamentos', icon: 'transactions' },
  { id: 'recurring', slug: 'gastos-fixos', label: 'Gastos fixos', icon: 'repeat' },
  { id: 'reports', slug: 'relatorios', label: 'Relatórios', icon: 'reports' },
  { id: 'categories', slug: 'categorias', label: 'Categorias', icon: 'categories' },
  { id: 'installments', slug: 'parcelas', label: 'Parcelas', icon: 'card' },
  { id: 'accounts', slug: 'contas', label: 'Contas', icon: 'accounts' },
  { id: 'import', slug: 'importar', label: 'Importar do banco', icon: 'import' },
];

/**
 * Rota que existe mas nao ocupa espaco no menu: chega-se a ela pelo rodape da
 * barra lateral. Trocar a senha e coisa de uma vez por ano; virar item fixo
 * seria dar a ela o mesmo peso de "Lancamentos".
 */
export const HIDDEN_PAGES: PageDefinition[] = [
  { id: 'password', slug: 'senha', label: 'Trocar senha', icon: 'lock' },
];

export const ROUTES: PageDefinition[] = [...PAGES, ...HIDDEN_PAGES];

export interface AppData {
  accounts: Account[];
  categories: Category[];
  transactions: Transaction[];
  /** Total no razao, que pode ser maior do que o que foi carregado. */
  transactionTotal: number;
  netWorth: NetWorth[];
  integrity: Integrity | null;
  /** Orcamento do mes corrente, para a visao geral. A pagina de orcamento busca o mes que estiver vendo. */
  budget: BudgetMonth | null;
  /**
   * Entrada e saida dos ultimos meses, somadas pelo banco. Nao sai dos
   * `transactions` acima de proposito: aquela lista para no teto de pagina
   * da API, e somar sobre ela subestimaria os meses mais cheios.
   */
  monthlyFlow: MonthlyFlow | null;
  /** Quem esta logado e ate quando a sessao vale. */
  session: SessionInfo | null;
}

export const EMPTY_DATA: AppData = {
  accounts: [],
  categories: [],
  transactions: [],
  transactionTotal: 0,
  netWorth: [],
  integrity: null,
  budget: null,
  monthlyFlow: null,
  session: null,
};

export interface PageProps {
  data: AppData;
  reload: () => Promise<void>;
  navigate: (page: PageId) => void;
}
