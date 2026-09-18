import type { MonthlyFlow, Transaction } from './api';
import { monthKeyOf } from './format';

/*
 * Numeros do painel, calculados no navegador a partir dos lancamentos
 * carregados.
 *
 * A REGRA: despesa e receita sao lidas nas contas EXTERNAS. Gasto e o que
 * entrou em "Despesas externas"; receita e o que saiu de "Receitas externas".
 * Isso da duas propriedades de graca:
 *
 *   transferencia nao conta   Mover da corrente para a poupanca so mexe em
 *                             contas internas. Nao e gasto nem receita, e
 *                             aqui simplesmente nao aparece.
 *
 *   estorno se desconta       Devolucao entra negativa na perna de despesa e
 *                             abate o gasto sozinha, sem caso especial.
 *
 * ATE ONDE ESTAS CONTAS VALEM. Elas so enxergam o que foi carregado - os 200
 * lancamentos mais recentes, que e o teto de pagina da API. Isso basta para o
 * que e por natureza local: o que esta na tabela da tela, o efeito de uma
 * linha, se falta categoria nela.
 *
 * TOTAL DE PERIODO NAO SE CALCULA MAIS AQUI. Somar seis meses sobre uma
 * pagina de 200 linhas dava numero menor que o real, e menor sem dizer
 * quanto. Esses totais agora vem prontos de /api/reports, somados pelo banco
 * sobre o razao inteiro - ver ReportQueries no servidor. O que sobrou abaixo
 * para periodo (flowByMonth, monthSummary) serve a demo e a telas que ja
 * trabalham dentro do mes carregado; numero de painel vem do servidor.
 */

export interface NamedAmount {
  name: string;
  amount: number;
}

export interface MonthFlow {
  key: string;
  income: number;
  expense: number;
}

export interface MonthSummary {
  income: number;
  expense: number;
  net: number;
  count: number;
}

const isExpense = (type: string) => type === 'EXPENSE';
const isRevenue = (type: string) => type === 'REVENUE';

/**
 * Contas que compoem o patrimonio: ativo e passivo. EQUITY fica de fora de
 * proposito - "Saldos iniciais" e a CONTRAPARTIDA do patrimonio, nao parte
 * dele. Somar as duas coisas anula o saldo de abertura inteiro.
 */
const isBalanceSheet = (type: string) => type === 'ASSET' || type === 'LIABILITY';

export function spendingByCategory(transactions: Transaction[], month?: string): NamedAmount[] {
  const totals = new Map<string, number>();

  for (const t of transactions) {
    if (month && monthKeyOf(t.occurredOn) !== month) continue;

    for (const e of t.entries) {
      if (!isExpense(e.accountType)) continue;
      const name = e.categoryName ?? 'Sem categoria';
      totals.set(name, (totals.get(name) ?? 0) + e.amountMinorUnits);
    }
  }

  return [...totals]
    .map(([name, amount]) => ({ name, amount }))
    .filter((item) => item.amount > 0)
    .sort((a, b) => b.amount - a.amount);
}

/** Movimento por id de categoria no mes - base da pagina de categorias. */
export function activityByCategory(transactions: Transaction[], month: string): Map<string, number> {
  const totals = new Map<string, number>();

  for (const t of transactions) {
    if (monthKeyOf(t.occurredOn) !== month) continue;

    for (const e of t.entries) {
      if (e.categoryId === null) continue;

      if (isExpense(e.accountType)) {
        totals.set(e.categoryId, (totals.get(e.categoryId) ?? 0) + e.amountMinorUnits);
      } else if (isRevenue(e.accountType)) {
        totals.set(e.categoryId, (totals.get(e.categoryId) ?? 0) - e.amountMinorUnits);
      }
    }
  }

  return totals;
}

/** As N chaves de mes que terminam em reference, da mais antiga para a atual. */
export function monthsEndingAt(reference: string, count: number): string[] {
  let year = Number(reference.slice(0, 4));
  let month = Number(reference.slice(5, 7));
  const keys: string[] = [];

  for (let i = 0; i < count; i++) {
    keys.unshift(`${year}-${String(month).padStart(2, '0')}`);
    month -= 1;
    if (month === 0) {
      month = 12;
      year -= 1;
    }
  }

  return keys;
}

/**
 * Converte o relatorio do servidor na forma que os graficos ja consomem.
 * Existe para que trocar a FONTE do numero nao obrigasse a reescrever o
 * desenho: o grafico continua recebendo { key, income, expense }.
 */
export function flowFromReport(report: MonthlyFlow): MonthFlow[] {
  return report.months.map((m) => ({
    key: m.month,
    income: m.incomeMinorUnits,
    expense: m.expenseMinorUnits,
  }));
}

export function flowByMonth(transactions: Transaction[], count: number, reference: string): MonthFlow[] {
  const months = new Map(
    monthsEndingAt(reference, count).map((key) => [key, { key, income: 0, expense: 0 }]),
  );

  for (const t of transactions) {
    const target = months.get(monthKeyOf(t.occurredOn));
    if (!target) continue;

    for (const e of t.entries) {
      if (isExpense(e.accountType)) target.expense += e.amountMinorUnits;
      else if (isRevenue(e.accountType)) target.income -= e.amountMinorUnits;
    }
  }

  return [...months.values()];
}

export function monthSummary(transactions: Transaction[], month: string): MonthSummary {
  const [flow] = flowByMonth(transactions, 1, month);
  const income = flow?.income ?? 0;
  const expense = flow?.expense ?? 0;

  return {
    income,
    expense,
    net: income - expense,
    count: transactions.filter((t) => monthKeyOf(t.occurredOn) === month).length,
  };
}

/**
 * Quanto o lancamento mudou o patrimonio: soma das pernas em ativo e passivo.
 * Despesa da negativo, receita positivo, e transferencia - inclusive pagar a
 * fatura do cartao com a conta corrente - da exatamente zero.
 */
/**
 * Tem gasto sem categoria? E a perna na conta de despesa externa sem
 * categoria - a mesma regra que v_budget_entries usa para contar "saiu sem
 * categoria". Receita sem categoria nao entra: ela vira dinheiro sem destino,
 * que e destino legitimo.
 */
export function needsCategory(t: Transaction): boolean {
  return t.entries.some((e) => !e.accountIsInternal && e.accountType === 'EXPENSE' && e.categoryId === null);
}

/** A unica perna externa, quando ha exatamente uma. E onde a categoria mora. */
export function soleExternalLeg(t: Transaction): Transaction['entries'][number] | null {
  const external = t.entries.filter((e) => !e.accountIsInternal);
  return external.length === 1 ? external[0]! : null;
}

export function netWorthEffect(t: Transaction): number {
  return t.entries
    .filter((e) => isBalanceSheet(e.accountType))
    .reduce((sum, e) => sum + e.amountMinorUnits, 0);
}

/** Soma das pernas positivas. Somar todas daria zero - que e o ponto do razao. */
export function magnitude(t: Transaction): number {
  return t.entries
    .filter((e) => e.amountMinorUnits > 0)
    .reduce((sum, e) => sum + e.amountMinorUnits, 0);
}

export function primaryCategory(t: Transaction): string | null {
  return t.entries.find((e) => e.categoryName !== null)?.categoryName ?? null;
}
