import type { BudgetCategory } from './api';

/*
 * Leitura do orcamento para a tela. Nenhum numero e calculado aqui: disponivel,
 * atividade e pronto para atribuir vem prontos do servidor, das visoes da
 * migration 010. O que se faz aqui e so arrumar para exibir.
 */

export interface EnvelopeGroup {
  /** Grupo com subcategorias vira cabecalho; sem subcategorias, e ele mesmo o envelope. */
  head: BudgetCategory;
  children: BudgetCategory[];
  /** Soma do grupo e das subcategorias. */
  totals: { assigned: number; activity: number; available: number };
}

/**
 * Monta a arvore de dois niveis na ordem em que a API manda (pai antes dos
 * filhos). Subcategoria cujo grupo nao veio - grupo arquivado e zerado - sobe
 * para o primeiro nivel em vez de sumir: o dinheiro dela precisa aparecer.
 */
export function groupEnvelopes(categories: BudgetCategory[]): EnvelopeGroup[] {
  const present = new Set(categories.map((c) => c.categoryId));
  const groups: EnvelopeGroup[] = [];
  const byId = new Map<string, EnvelopeGroup>();

  for (const category of categories) {
    if (category.parentId !== null && present.has(category.parentId)) {
      continue;
    }
    const group: EnvelopeGroup = { head: category, children: [], totals: { assigned: 0, activity: 0, available: 0 } };
    groups.push(group);
    byId.set(category.categoryId, group);
  }

  for (const category of categories) {
    if (category.parentId !== null && present.has(category.parentId)) {
      byId.get(category.parentId)?.children.push(category);
    }
  }

  for (const group of groups) {
    for (const member of [group.head, ...group.children]) {
      group.totals.assigned += member.assignedMinorUnits;
      group.totals.activity += member.activityMinorUnits;
      group.totals.available += member.availableMinorUnits;
    }
  }

  return groups;
}

/** O grupo tem dinheiro ou movimento proprio, alem do das subcategorias? */
export const hasOwnMoney = (c: BudgetCategory) =>
  c.assignedMinorUnits !== 0 || c.activityMinorUnits !== 0 || c.availableMinorUnits !== 0;

export type EnvelopeState = 'idle' | 'ok' | 'spent' | 'over';

export interface EnvelopeUsage {
  state: EnvelopeState;
  /** Quanto o envelope tinha para o mes: o que rolou mais o atribuido. */
  funded: number;
  /** Quanto saiu no mes, sem contar estorno. */
  spent: number;
  /** Largura da barra, 0 a 100. Geometria de SVG, nao dinheiro. */
  fill: number;
}

/**
 * disponivel = rolou + atribuido + atividade, entao o que o envelope tinha
 * para gastar no mes e disponivel - atividade.
 */
export function envelopeUsage(c: BudgetCategory): EnvelopeUsage {
  const spent = Math.max(0, -c.activityMinorUnits);
  const funded = c.availableMinorUnits - c.activityMinorUnits;

  if (c.availableMinorUnits < 0) {
    return { state: 'over', funded, spent, fill: 100 };
  }
  if (funded <= 0) {
    return { state: 'idle', funded, spent, fill: 0 };
  }
  if (c.availableMinorUnits === 0 && spent > 0) {
    return { state: 'spent', funded, spent, fill: 100 };
  }

  return { state: spent > 0 ? 'ok' : 'idle', funded, spent, fill: Math.min(100, (spent / funded) * 100) };
}

export type ReadyState = 'unassigned' | 'balanced' | 'overassigned';

export function readyState(readyToAssign: number): ReadyState {
  if (readyToAssign > 0) return 'unassigned';
  if (readyToAssign < 0) return 'overassigned';
  return 'balanced';
}

/**
 * Envelopes que pedem atencao primeiro: estourados (do mais negativo), depois
 * os mais consumidos. Envelope sem dinheiro e sem gasto nao entra.
 */
export function envelopesByAttention(categories: BudgetCategory[]): BudgetCategory[] {
  const rank = (c: BudgetCategory) => {
    const usage = envelopeUsage(c);
    if (usage.state === 'over') return -1_000_000_000_000 + c.availableMinorUnits;
    return -usage.fill;
  };

  return categories
    .filter((c) => envelopeUsage(c).state !== 'idle' || c.availableMinorUnits > 0)
    .sort((a, b) => rank(a) - rank(b));
}
