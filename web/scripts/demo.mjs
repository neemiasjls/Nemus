/*
 * Demonstracao local: uma API falsa com seis meses de lancamentos plausiveis,
 * e o frontend apontado para ela.
 *
 *     npm run demo
 *
 * POR QUE EXISTE. Enquanto a API .NET nao estiver hospedada, o site publicado
 * so mostra a tela de entrada - e sem dados nao da para avaliar a interface.
 *
 * NAO SAO NUMEROS SOLTOS. Isto e um razao de verdade em miniatura: todo
 * lancamento tem pernas que somam zero, e saldos, patrimonio e integridade sao
 * calculados a partir delas, do jeito que o banco faria. Se a soma de todos os
 * saldos nao der zero, o script se recusa a subir.
 *
 * As datas sao relativas a hoje: os seis meses terminam no mes corrente, que e
 * o que o painel usa para "gastos do mes" e "resultado do mes".
 *
 * O ORCAMENTO segue as tres regras de v_budget_entries (migration 010) e se
 * prova do mesmo jeito: se em algum mes envelopes + pronto para atribuir nao
 * baterem com o saldo das contas do orcamento, o script tambem nao sobe.
 *
 * Nada disto vai para producao: o app nao importa este arquivo e o build nao
 * o inclui. QUALQUER USUARIO E SENHA ENTRAM - aqui nao ha hash, nem sessao, nem
 * banco; o login de verdade mora na API .NET e e testado na suite. Escritas sao
 * aceitas e descartadas, menos a atribuicao do orcamento, que vale enquanto o
 * processo estiver no ar - sem ela nao da para experimentar a tela.
 */

import { createServer } from 'node:http';
import { createServer as createViteServer } from 'vite';

const API_PORT = 5199;
const WEB_PORT = 5173;

// ---------------------------------------------------------------------------
// Gerador deterministico: mesma semente, mesmos lancamentos a cada execucao.

let seed = 2026;
function random() {
  seed = (seed * 1103515245 + 12345) % 2147483648;
  return seed / 2147483648;
}
const between = (min, max) => Math.round(min + random() * (max - min));
const pick = (list) => list[Math.floor(random() * list.length)];

const ID = {
  checking: '01900000-0000-7000-8000-0000000000a1',
  savings: '01900000-0000-7000-8000-0000000000a2',
  treasury: '01900000-0000-7000-8000-0000000000a3',
  card: '01900000-0000-7000-8000-0000000000b1',
  // Mesmos UUIDs das contas de sistema da migration 002.
  openingBalances: '00000000-0000-7000-8000-000000000001',
  externalExpenses: '00000000-0000-7000-8000-000000000002',
  externalRevenue: '00000000-0000-7000-8000-000000000003',
  reconciliation: '00000000-0000-7000-8000-000000000004',
};

const baseAccounts = [
  { id: ID.checking, name: 'Itaú conta corrente', type: 'ASSET', isInternal: true, isSystem: false, isOnBudget: true },
  { id: ID.savings, name: 'Poupança Itaú', type: 'ASSET', isInternal: true, isSystem: false, isOnBudget: true },
  { id: ID.treasury, name: 'Tesouro Selic', type: 'ASSET', isInternal: true, isSystem: false, isOnBudget: false },
  { id: ID.card, name: 'Cartão Nubank', type: 'LIABILITY', isInternal: true, isSystem: false, isOnBudget: true },
  { id: ID.openingBalances, name: 'Saldos iniciais', type: 'EQUITY', isInternal: true, isSystem: true, isOnBudget: false },
  { id: ID.externalExpenses, name: 'Despesas externas', type: 'EXPENSE', isInternal: false, isSystem: true, isOnBudget: false },
  { id: ID.externalRevenue, name: 'Receitas externas', type: 'REVENUE', isInternal: false, isSystem: true, isOnBudget: false },
  { id: ID.reconciliation, name: 'Ajuste de conciliação', type: 'EQUITY', isInternal: true, isSystem: true, isOnBudget: false },
];
const accountById = Object.fromEntries(baseAccounts.map((a) => [a.id, a]));

const categories = [
  { id: 'k-housing', parentId: null, name: 'Moradia', kind: 'EXPENSE', sortOrder: 0 },
  { id: 'k-rent', parentId: 'k-housing', name: 'Aluguel', kind: 'EXPENSE', sortOrder: 0 },
  { id: 'k-condo', parentId: 'k-housing', name: 'Condomínio', kind: 'EXPENSE', sortOrder: 1 },
  { id: 'k-power', parentId: 'k-housing', name: 'Energia', kind: 'EXPENSE', sortOrder: 2 },
  { id: 'k-food', parentId: null, name: 'Alimentação', kind: 'EXPENSE', sortOrder: 1 },
  { id: 'k-groceries', parentId: 'k-food', name: 'Mercado', kind: 'EXPENSE', sortOrder: 0 },
  { id: 'k-dining', parentId: 'k-food', name: 'Restaurantes', kind: 'EXPENSE', sortOrder: 1 },
  { id: 'k-transport', parentId: null, name: 'Transporte', kind: 'EXPENSE', sortOrder: 2 },
  { id: 'k-leisure', parentId: null, name: 'Lazer', kind: 'EXPENSE', sortOrder: 3 },
  { id: 'k-health', parentId: null, name: 'Saúde', kind: 'EXPENSE', sortOrder: 4 },
  { id: 'k-subscriptions', parentId: null, name: 'Assinaturas', kind: 'EXPENSE', sortOrder: 5 },
  // Envelopes que acumulam de um mes para o outro: e onde o rollover aparece.
  { id: 'k-goals', parentId: null, name: 'Metas', kind: 'EXPENSE', sortOrder: 6 },
  { id: 'k-emergency', parentId: 'k-goals', name: 'Reserva de emergência', kind: 'EXPENSE', sortOrder: 0 },
  { id: 'k-trip', parentId: 'k-goals', name: 'Viagem de fim de ano', kind: 'EXPENSE', sortOrder: 1 },
  { id: 'k-salary', parentId: null, name: 'Salário', kind: 'INCOME', sortOrder: 0 },
  { id: 'k-interest', parentId: null, name: 'Rendimentos', kind: 'INCOME', sortOrder: 1 },
];
const categoryName = Object.fromEntries(categories.map((c) => [c.id, c.name]));

// ---------------------------------------------------------------------------
// Lancamentos.

const transactions = [];
let sequence = 0;

function post(date, description, legs, source = 'OFX', kind = 'STANDARD') {
  sequence += 1;
  transactions.push({
    id: `t${String(sequence).padStart(4, '0')}`,
    occurredOn: date,
    description,
    currencyCode: 'BRL',
    kind,
    source,
    notes: null,
    entries: legs.map((leg, i) => ({
      id: `e${sequence}-${i}`,
      accountId: leg.account,
      accountName: accountById[leg.account].name,
      accountType: accountById[leg.account].type,
      accountIsInternal: accountById[leg.account].isInternal,
      amountMinorUnits: leg.amount,
      categoryId: leg.category ?? null,
      categoryName: leg.category ? categoryName[leg.category] : null,
      memo: null,
    })),
  });
}

const expense = (date, description, from, amount, category, source) =>
  post(date, description, [
    { account: from, amount: -amount },
    { account: ID.externalExpenses, amount, category },
  ], source);
const income = (date, description, to, amount, category, source) =>
  post(date, description, [
    { account: ID.externalRevenue, amount: -amount, category },
    { account: to, amount },
  ], source);
const transfer = (date, description, from, to, amount) =>
  post(date, description, [{ account: from, amount: -amount }, { account: to, amount }], 'MANUAL', 'TRANSFER');
const open = (date, to, amount) =>
  post(date, 'Saldo inicial', [
    { account: to, amount },
    { account: ID.openingBalances, amount: -amount },
  ], 'MANUAL', 'OPENING_BALANCE');

const iso = (year, month, day) => `${year}-${String(month).padStart(2, '0')}-${String(day).padStart(2, '0')}`;

const now = new Date();
const today = { year: now.getFullYear(), month: now.getMonth() + 1, day: now.getDate() };

// Seis meses terminando no corrente; o saldo inicial vem na vespera do primeiro.
const months = [];
for (let back = 5; back >= 0; back -= 1) {
  const first = new Date(today.year, today.month - 1 - back, 1);
  months.push({ year: first.getFullYear(), month: first.getMonth() + 1 });
}
const eve = new Date(months[0].year, months[0].month - 1, 0);
const openingDate = iso(eve.getFullYear(), eve.getMonth() + 1, eve.getDate());

open(openingDate, ID.checking, 684000);
open(openingDate, ID.savings, 1215000);
open(openingDate, ID.treasury, 2380000);

let previousCardSpend = 0;

for (const { year, month } of months) {
  const isCurrent = year === today.year && month === today.month;
  const lastDay = new Date(year, month, 0).getDate();
  const happens = (day) => day <= lastDay && (!isCurrent || day <= today.day);
  const d = (day) => iso(year, month, day);
  let cardSpend = 0;

  const onCard = (day, description, amount, category) => {
    if (!happens(day)) return;
    expense(d(day), description, ID.card, amount, category);
    cardSpend += amount;
  };

  if (happens(5)) income(d(5), 'Salário — Empresa Ltda', ID.checking, 1480000, 'k-salary');
  if (happens(6)) transfer(d(6), 'Aplicação na poupança', ID.checking, ID.savings, 150000);
  if (happens(8) && previousCardSpend > 0) {
    transfer(d(8), 'Pagamento da fatura Nubank', ID.checking, ID.card, previousCardSpend);
  }
  if (happens(10)) expense(d(10), 'Aluguel', ID.checking, 280000, 'k-rent');
  if (happens(10)) expense(d(10), 'Condomínio Ed. Aurora', ID.checking, between(62000, 68000), 'k-condo');
  if (happens(15)) expense(d(15), 'Enel — conta de luz', ID.checking, between(17800, 29500), 'k-power');
  if (happens(28)) income(d(28), 'Rendimento da poupança', ID.savings, between(8900, 10400), 'k-interest', 'MANUAL');

  for (let i = 0; i < 4; i += 1) {
    onCard(between(2, 27), pick(['Pão de Açúcar', 'Assaí Atacadista', 'Carrefour', 'Hortifruti']), between(14000, 46000), 'k-groceries');
  }
  for (let i = 0; i < 5; i += 1) {
    onCard(between(1, 28), pick(['PAG*IFOOD', 'PAG*IFOOD SAO PAULO', 'Madero', 'Coco Bambu']), between(3800, 12800), 'k-dining');
  }
  for (let i = 0; i < 6; i += 1) {
    onCard(between(1, 28), pick(['Uber *Trip', '99 Pop', 'Shell Posto']), between(1600, 9800), 'k-transport');
  }
  for (let i = 0; i < 2; i += 1) {
    onCard(between(4, 26), pick(['Cinemark', 'Ingresso.com', 'Livraria Cultura']), between(6000, 22000), 'k-leisure');
  }
  onCard(between(3, 25), 'Drogasil', between(4500, 16000), 'k-health');
  onCard(3, 'Spotify', 2190, 'k-subscriptions');
  onCard(12, 'Netflix', 5590, 'k-subscriptions');

  // Um estorno no terceiro mes, para mostrar que devolucao abate o gasto sozinha.
  if (month === months[2].month && happens(19)) {
    post(d(19), 'Estorno — Livraria Cultura', [
      { account: ID.card, amount: 8990 },
      { account: ID.externalExpenses, amount: -8990, category: 'k-leisure' },
    ]);
    cardSpend -= 8990;
  }

  // A passagem sai do envelope que vinha juntando dinheiro havia meses.
  if (month === months[3].month) onCard(14, 'LATAM Airlines', 168000, 'k-trip');

  // No mes corrente: um jantar que estoura o envelope, e um PIX sem categoria
  // - os dois casos que a tela de orcamento existe para apontar.
  if (isCurrent) {
    onCard(1, 'Coco Bambu — aniversário', 38000, 'k-dining');
    if (happens(1)) expense(d(1), 'PIX enviado — João Silva', ID.checking, 4500);
  }

  previousCardSpend = cardSpend;
}

transactions.sort((a, b) =>
  a.occurredOn === b.occurredOn ? b.id.localeCompare(a.id) : b.occurredOn.localeCompare(a.occurredOn),
);

// ---------------------------------------------------------------------------
// Saldos calculados das pernas, como o banco faria.

const balance = Object.fromEntries(baseAccounts.map((a) => [a.id, 0]));
for (const t of transactions) {
  for (const e of t.entries) balance[e.accountId] += e.amountMinorUnits;
}

const accounts = baseAccounts.map((a) => ({ ...a, currencyCode: 'BRL', balanceMinorUnits: balance[a.id] }));
const sumWhere = (predicate) => accounts.filter(predicate).reduce((sum, a) => sum + a.balanceMinorUnits, 0);

if (sumWhere(() => true) !== 0) {
  throw new Error(`Razao da demonstracao desbalanceado: soma ${sumWhere(() => true)}. Corrija o gerador.`);
}

const assets = sumWhere((a) => a.type === 'ASSET');
const liabilities = sumWhere((a) => a.type === 'LIABILITY');

// ---------------------------------------------------------------------------
// Orcamento. As mesmas tres regras de v_budget_entries:
//   a) so transacao que toca conta do orcamento (ativo/passivo com isOnBudget);
//   b) a categoria conta na perna da conta externa, com o sinal que ja tem;
//   c) perna na conta de despesa sem categoria de despesa e saida sem envelope.

const expenseCategories = new Set(categories.filter((c) => c.kind === 'EXPENSE').map((c) => c.id));
const onBudgetAccounts = new Set(
  baseAccounts.filter((a) => a.isOnBudget && (a.type === 'ASSET' || a.type === 'LIABILITY')).map((a) => a.id),
);

// Recalculado a cada leitura, e nao uma vez so: categorizar um lancamento
// pela tela precisa mexer no orcamento na hora.
const budgetLegs = () => transactions
  .filter((t) => t.entries.some((e) => onBudgetAccounts.has(e.accountId)))
  .flatMap((t) =>
    t.entries.map((e) => ({
      transactionId: t.id,
      month: t.occurredOn.slice(0, 7),
      amount: e.amountMinorUnits,
      onBudget: onBudgetAccounts.has(e.accountId),
      envelope: !e.accountIsInternal && expenseCategories.has(e.categoryId) ? e.categoryId : null,
      uncategorized: e.accountType === 'EXPENSE' && !expenseCategories.has(e.categoryId),
    })),
  );

/** `${categoria}|${mes}` -> valor atribuido. */
const assignments = new Map();
const total = (list) => list.reduce((sum, leg) => sum + leg.amount, 0);

function assignedUpTo(month, categoryId) {
  let sum = 0;
  for (const [key, amount] of assignments) {
    const [category, assignedMonth] = key.split('|');
    if (assignedMonth <= month && (categoryId === undefined || category === categoryId)) sum += amount;
  }
  return sum;
}

function budgetMonth(month) {
  const upTo = budgetLegs().filter((leg) => leg.month <= month);
  const inMonth = upTo.filter((leg) => leg.month === month);

  const rows = categories
    .filter((c) => c.kind === 'EXPENSE')
    .map((c) => ({
      categoryId: c.id,
      parentId: c.parentId,
      name: c.name,
      isArchived: false,
      assignedMinorUnits: assignments.get(`${c.id}|${month}`) ?? 0,
      activityMinorUnits: -total(inMonth.filter((leg) => leg.envelope === c.id)),
      availableMinorUnits: assignedUpTo(month, c.id) - total(upTo.filter((leg) => leg.envelope === c.id)),
    }));

  const sumOf = (field) => rows.reduce((sum, row) => sum + row[field], 0);
  const ready = total(upTo.filter((leg) => leg.onBudget || leg.envelope)) - assignedUpTo(month);
  const balance = total(upTo.filter((leg) => leg.onBudget));
  const loose = inMonth.filter((leg) => leg.uncategorized);

  return {
    month,
    currencyCode: 'BRL',
    readyToAssignMinorUnits: ready,
    netInflowMinorUnits: total(inMonth.filter((leg) => leg.onBudget || leg.envelope)),
    assignedMinorUnits: sumOf('assignedMinorUnits'),
    activityMinorUnits: sumOf('activityMinorUnits'),
    availableMinorUnits: sumOf('availableMinorUnits'),
    onBudgetBalanceMinorUnits: balance,
    isBalanced: sumOf('availableMinorUnits') + ready === balance,
    uncategorizedTransactions: new Set(loose.map((leg) => leg.transactionId)).size,
    uncategorizedMinorUnits: total(loose),
    categories: rows,
  };
}

// Atribuicoes plausiveis: o planejado de cada envelope, e o que sobra do mes
// vai para a reserva. No mes corrente ficam R$ 1.350 sem destino, e
// restaurantes recebe pouco - a tela precisa ter o que apontar.
const planned = {
  'k-rent': 280000,
  'k-condo': 66000,
  'k-power': 26000,
  'k-groceries': 125000,
  'k-dining': 42000,
  'k-transport': 32000,
  'k-leisure': 25000,
  'k-health': 12000,
  'k-subscriptions': 7780,
  'k-trip': 40000,
};
const monthKeys = months.map(({ year, month }) => iso(year, month, 1).slice(0, 7));
const salaryArrived = today.day >= 5;

for (const key of monthKeys) {
  const isCurrent = key === monthKeys[monthKeys.length - 1];
  if (isCurrent && !salaryArrived) break;

  for (const [category, amount] of Object.entries(planned)) {
    assignments.set(`${category}|${key}`, isCurrent && category === 'k-dining' ? 20000 : amount);
  }
  const rest = budgetMonth(key).readyToAssignMinorUnits - (isCurrent ? 135000 : 0);
  if (rest > 0) assignments.set(`k-emergency|${key}`, rest);
}

for (const key of monthKeys) {
  const view = budgetMonth(key);
  if (!view.isBalanced) {
    throw new Error(`Orcamento da demonstracao nao fecha em ${key}. Corrija o gerador.`);
  }
}

const readJson = (req) =>
  new Promise((resolve) => {
    let body = '';
    req.on('data', (chunk) => (body += chunk));
    req.on('end', () => {
      try {
        resolve(JSON.parse(body || '{}'));
      } catch {
        resolve(null);
      }
    });
  });

// ---------------------------------------------------------------------------
// Parcelamento. O cartao fecha dia 25 e vence dia 5; as duas compras abaixo
// ja estao no razao como UMA transacao cada (o cartao devendo tudo no ato) -
// o cronograma so diz em qual fatura cada parcela cai.

const cardTerms = [
  { accountId: ID.card, closingDay: 25, dueDay: 5, creditLimitMinorUnits: 1200000, paymentAccountId: ID.checking },
];

const monthKeyOf = (date) => date.slice(0, 7);
const addMonths = (key, delta) => {
  const index = Number(key.slice(0, 4)) * 12 + Number(key.slice(5, 7)) - 1 + delta;
  const year = Math.floor(index / 12);
  return `${year}-${String(index - year * 12 + 1).padStart(2, '0')}`;
};

/** As N chaves de mes que terminam em `until`, da mais antiga para a atual. */
const monthsEndingAt = (until, count) =>
  Array.from({ length: count }, (_, i) => addMonths(until, i - (count - 1)));

const plans = [];

function installmentPurchase(monthsAgo, day, description, totalMinor, count, category) {
  const first = new Date(today.year, today.month - 1 - monthsAgo, day);
  const purchaseDate = iso(first.getFullYear(), first.getMonth() + 1, day);
  if (purchaseDate > iso(today.year, today.month, today.day)) return;

  // Uma transacao so: o cartao deve o valor inteiro desde hoje.
  expense(purchaseDate, description, ID.card, totalMinor, category, 'MANUAL');

  // Reparticao com o centavo residual nas primeiras, como o dominio faz.
  const base = Math.floor(totalMinor / count);
  const remainder = totalMinor - base * count;
  const amounts = Array.from({ length: count }, (_, i) => base + (i < remainder ? 1 : 0));

  // Compra ate o dia 25 cai na fatura do proprio mes.
  const firstMonth = day <= 25 ? monthKeyOf(purchaseDate) : addMonths(monthKeyOf(purchaseDate), 1);

  plans.push({
    id: `p${plans.length + 1}`,
    cardAccountId: ID.card,
    cardName: accountById[ID.card].name,
    description,
    currencyCode: 'BRL',
    totalMinorUnits: totalMinor,
    financedMinorUnits: totalMinor,
    interestMinorUnits: 0,
    installmentCount: count,
    installmentMinorUnits: amounts[0],
    purchaseDate,
    firstStatementMonth: firstMonth,
    lastStatementMonth: addMonths(firstMonth, count - 1),
    schedule: amounts.map((amount, i) => ({ month: addMonths(firstMonth, i), amount })),
  });
}

installmentPurchase(4, 12, 'Sofá da sala — Mobly', 240000, 10, 'k-leisure');
installmentPurchase(1, 8, 'Notebook — Kabum', 360000, 12, 'k-subscriptions');

const currentMonthKey = `${today.year}-${String(today.month).padStart(2, '0')}`;

function planView(plan) {
  const paid = plan.schedule.filter((i) => i.month <= currentMonthKey).length;
  const remaining = plan.schedule
    .filter((i) => i.month > currentMonthKey)
    .reduce((sum, i) => sum + i.amount, 0);

  const { schedule, ...rest } = plan;
  return { ...rest, paidCount: paid, remainingMinorUnits: remaining, isFinished: remaining === 0 };
}

function upcoming(months) {
  const result = [];
  for (let i = 0; i < months; i += 1) {
    const month = addMonths(currentMonthKey, i);
    const parcels = plans.flatMap((p) => p.schedule.filter((s) => s.month === month).map((s) => ({ p, s })));
    if (parcels.length === 0) continue;
    result.push({
      month,
      currencyCode: 'BRL',
      amountMinorUnits: parcels.reduce((sum, x) => sum + x.s.amount, 0),
      planCount: new Set(parcels.map((x) => x.p.id)).size,
    });
  }
  return result;
}

// Gastos fixos. Previsao, nao lancamento: nada aqui entra em `transactions`.
const recurring = [
  { id: 'r-rent',  name: 'Aluguel',     categoryId: 'k-rent',          amountMinorUnits: 280_000, dueDay: 10, accountId: ID.checking, isEstimate: false },
  { id: 'r-condo', name: 'Condominio',  categoryId: 'k-condo',         amountMinorUnits: 65_000,  dueDay: 10, accountId: ID.checking, isEstimate: false },
  { id: 'r-power', name: 'Energia',     categoryId: 'k-power',         amountMinorUnits: 23_000,  dueDay: 20, accountId: ID.checking, isEstimate: true },
  { id: 'r-netflix', name: 'Netflix', categoryId: 'k-subscriptions', amountMinorUnits: 5_590, dueDay: 12, accountId: null, isEstimate: false },
  { id: 'r-spotify', name: 'Spotify', categoryId: 'k-subscriptions', amountMinorUnits: 2_190, dueDay: 3,  accountId: null, isEstimate: false },
].map((r) => ({ ...r, currencyCode: 'BRL', startsOn: '2026-01-01', endsOn: null, archivedAt: null }));

/**
 * Mesmo criterio do servidor: dentro da categoria, cada gasto fixo fica com o
 * lancamento de valor mais proximo, e cada lancamento serve a um so. E como
 * aluguel e condominio na mesma categoria se separam.
 */
function matchRecurring(month) {
  const candidates = transactions
    .filter((t) => t.occurredOn.slice(0, 7) === month)
    .flatMap((t) =>
      t.entries
        .filter((e) => e.accountType === 'EXPENSE' && e.categoryId && e.amountMinorUnits > 0)
        .map((e) => ({
          transactionId: t.id,
          categoryId: e.categoryId,
          amountMinorUnits: e.amountMinorUnits,
          occurredOn: t.occurredOn,
        })),
    );

  const pairs = [];
  for (const r of recurring) {
    for (const c of candidates) {
      if (c.categoryId !== r.categoryId) continue;

      const distance = Math.abs(c.amountMinorUnits - r.amountMinorUnits);
      const percent = r.isEstimate ? 100 : 25;

      if (distance <= 2_000 || distance * 100 <= r.amountMinorUnits * percent) {
        pairs.push({ r, c, distance });
      }
    }
  }

  pairs.sort((a, b) => a.distance - b.distance || a.c.occurredOn.localeCompare(b.c.occurredOn));

  const hit = new Map();
  const taken = new Set();
  for (const { r, c } of pairs) {
    if (hit.has(r.id) || taken.has(c.transactionId)) continue;
    hit.set(r.id, c);
    taken.add(c.transactionId);
  }

  return hit;
}

function recurringMonth(month) {
  const hit = matchRecurring(month);
  const lastDay = new Date(Number(month.slice(0, 4)), Number(month.slice(5, 7)), 0).getDate();

  const items = recurring.map((r) => {
    const c = hit.get(r.id) ?? null;
    return {
      ...r,
      categoryName: categories.find((k) => k.id === r.categoryId)?.name ?? '?',
      accountName: r.accountId ? accountById[r.accountId]?.name ?? null : null,
      dueDate: `${month}-${String(Math.min(r.dueDay, lastDay)).padStart(2, '0')}`,
      isArchived: false,
      isMatched: c !== null,
      transactionId: c?.transactionId ?? null,
      actualMinorUnits: c?.amountMinorUnits ?? null,
      occurredOn: c?.occurredOn ?? null,
      differenceMinorUnits: c ? c.amountMinorUnits - r.amountMinorUnits : null,
    };
  });

  return {
    month,
    currencyCode: 'BRL',
    expectedMinorUnits: items.reduce((sum, i) => sum + i.amountMinorUnits, 0),
    matchedMinorUnits: items.reduce((sum, i) => sum + (i.actualMinorUnits ?? 0), 0),
    // Pendente e o PREVISTO do que ainda nao veio, nao "esperado menos realizado".
    pendingMinorUnits: items.filter((i) => !i.isMatched).reduce((sum, i) => sum + i.amountMinorUnits, 0),
    items,
  };
}

// Sessao de mentira: existe so para a tela ter um nome para mostrar.
let demoUser = 'demo';
const inThirtyDays = () => new Date(Date.now() + 30 * 86400000).toISOString();

const routes = {
  '/api/auth/status': () => ({ needsFirstAccess: false }),
  '/api/auth/me': () => ({
    username: demoUser,
    expiresAt: inThirtyDays(),
    lastLoginAt: new Date().toISOString(),
  }),
  '/api/accounts': () => accounts,
  '/api/accounts/cards': () => cardTerms,
  '/api/installments': () => plans.map(planView),
  '/api/categories': () => categories,
  '/api/summary/net-worth': () => [
    // Mesma definicao da migration 009: ativo + passivo, sem EQUITY.
    { currencyCode: 'BRL', assetsMinorUnits: assets, liabilitiesMinorUnits: liabilities, netWorthMinorUnits: assets + liabilities },
  ],
  '/api/summary/integrity': () => ({
    isIntact: true,
    sumOfAllBalances: 0,
    sumInternalBalances: sumWhere((a) => a.isInternal),
    sumExternalBalances: sumWhere((a) => !a.isInternal),
    unbalancedTransactions: 0,
    undersizedTransactions: 0,
    emptyTransactions: 0,
    brokenInstallmentPlans: 0,
  }),
};

const apiServer = createServer(async (req, res) => {
  const headers = {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Headers': 'Authorization,Content-Type',
    'Access-Control-Allow-Methods': 'GET,POST,PUT,DELETE,OPTIONS',
    'Content-Type': 'application/json; charset=utf-8',
  };

  if (req.method === 'OPTIONS') {
    res.writeHead(204, headers).end();
    return;
  }

  const url = new URL(req.url, 'http://localhost');

  if (url.pathname === '/api/transactions' && req.method === 'GET') {
    const limit = Number(url.searchParams.get('limit') ?? 50);
    res.writeHead(200, headers);
    res.end(JSON.stringify({ items: transactions.slice(0, limit), total: transactions.length, limit, offset: 0 }));
    return;
  }

  // Entrar e trocar senha: aceita qualquer coisa e devolve uma sessao falsa.
  if ((url.pathname === '/api/auth/login' || url.pathname === '/api/auth/first-access') && req.method === 'POST') {
    const body = await readJson(req);
    const username = String(body?.username ?? '').trim().toLowerCase() || 'demo';

    if (!body?.password) {
      res.writeHead(401, headers).end(JSON.stringify({
        code: 'auth.invalid_credentials',
        message: 'Usuario ou senha nao conferem.',
      }));
      return;
    }

    demoUser = username;
    res.writeHead(200, headers).end(JSON.stringify({
      token: 'demonstracao',
      username,
      expiresAt: inThirtyDays(),
    }));
    return;
  }

  if (url.pathname === '/api/auth/password' && req.method === 'POST') {
    res.writeHead(200, headers).end(JSON.stringify({
      token: 'demonstracao',
      username: demoUser,
      expiresAt: inThirtyDays(),
    }));
    return;
  }

  if (url.pathname === '/api/auth/logout' && req.method === 'POST') {
    res.writeHead(204, headers).end();
    return;
  }

  // Compra parcelada: a demo faz o mesmo que a API - uma transacao no razao
  // com o cartao devendo tudo, e o cronograma das parcelas.
  if (url.pathname === '/api/installments' && req.method === 'POST') {
    const body = await readJson(req);
    const total = Number(body?.totalAmountMinorUnits);
    const count = Number(body?.installmentCount);
    const terms = cardTerms.find((c) => c.accountId === body?.cardAccountId);

    if (!terms) {
      res.writeHead(422, headers).end(JSON.stringify({
        code: 'card.terms_missing',
        message: 'Este cartao ainda nao tem fechamento e vencimento.',
      }));
      return;
    }
    if (!Number.isSafeInteger(total) || total <= 0 || !Number.isInteger(count) || count < 1 || count > 99) {
      res.writeHead(422, headers).end(JSON.stringify({
        code: 'installment.count_out_of_range',
        message: 'Valor ou numero de parcelas invalido.',
      }));
      return;
    }

    const financed = Number(body?.financedAmountMinorUnits ?? total);
    const purchaseDate = String(body?.occurredOn ?? iso(today.year, today.month, today.day));
    const description = String(body?.description ?? 'Compra parcelada');

    expense(purchaseDate, description, body.cardAccountId, financed, body?.categoryId ?? null, 'MANUAL');
    transactions.sort((a, b) =>
      a.occurredOn === b.occurredOn ? b.id.localeCompare(a.id) : b.occurredOn.localeCompare(a.occurredOn),
    );

    const base = Math.floor(financed / count);
    const remainder = financed - base * count;
    const amounts = Array.from({ length: count }, (_, i) => base + (i < remainder ? 1 : 0));
    const day = Number(purchaseDate.slice(8, 10));
    const firstMonth = day <= terms.closingDay ? monthKeyOf(purchaseDate) : addMonths(monthKeyOf(purchaseDate), 1);

    plans.push({
      id: `p${plans.length + 1}`,
      cardAccountId: body.cardAccountId,
      cardName: accountById[body.cardAccountId].name,
      description,
      currencyCode: 'BRL',
      totalMinorUnits: total,
      financedMinorUnits: financed,
      interestMinorUnits: financed - total,
      installmentCount: count,
      installmentMinorUnits: amounts[0],
      purchaseDate,
      firstStatementMonth: firstMonth,
      lastStatementMonth: addMonths(firstMonth, count - 1),
      schedule: amounts.map((amount, i) => ({ month: addMonths(firstMonth, i), amount })),
    });

    res.writeHead(201, headers).end(JSON.stringify(planView(plans[plans.length - 1])));
    return;
  }

  const recurringMonthRoute = url.pathname.match(/^\/api\/recurring\/(\d{4}-(?:0[1-9]|1[0-2]))$/);
  if (recurringMonthRoute && req.method === 'GET') {
    res.writeHead(200, headers).end(JSON.stringify(recurringMonth(recurringMonthRoute[1])));
    return;
  }

  // Enche os envelopes vazios com o previsto. Nao sobrescreve o que ja tem
  // valor: apagar em silencio uma decisao tomada na mao seria o oposto do metodo.
  const recurringAssignRoute = url.pathname.match(/^\/api\/recurring\/(\d{4}-(?:0[1-9]|1[0-2]))\/assign$/);
  if (recurringAssignRoute && req.method === 'POST') {
    const month = recurringAssignRoute[1];
    const expected = new Map();
    for (const r of recurring) {
      expected.set(r.categoryId, (expected.get(r.categoryId) ?? 0) + r.amountMinorUnits);
    }

    let filled = 0;
    let skipped = 0;
    for (const [categoryId, amount] of expected) {
      const key = `${categoryId}|${month}`;
      if (assignments.get(key)) { skipped++; continue; }
      assignments.set(key, amount);
      filled++;
    }

    const view = budgetMonth(month);
    res.writeHead(200, headers).end(JSON.stringify({
      month,
      filled,
      skipped,
      readyToAssignMinorUnits: view.readyToAssignMinorUnits,
      assignedMinorUnits: view.assignedMinorUnits,
    }));
    return;
  }

  if (url.pathname === '/api/recurring' && req.method === 'POST') {
    const body = await readJson(req);
    recurring.push({
      id: `r-${Date.now()}`,
      name: body.name,
      categoryId: body.categoryId,
      amountMinorUnits: body.amountMinorUnits,
      currencyCode: 'BRL',
      dueDay: body.dueDay,
      accountId: body.accountId ?? null,
      isEstimate: !!body.isEstimate,
      startsOn: body.startsOn,
      endsOn: body.endsOn ?? null,
      archivedAt: null,
    });
    res.writeHead(200, headers).end(JSON.stringify({ id: recurring.at(-1).id }));
    return;
  }

  const recurringItemRoute = url.pathname.match(/^\/api\/recurring\/([\w-]+)$/);
  if (recurringItemRoute && req.method === 'PUT') {
    const body = await readJson(req);
    const found = recurring.find((r) => r.id === recurringItemRoute[1]);
    if (found) Object.assign(found, body);
    res.writeHead(200, headers).end(JSON.stringify({ id: recurringItemRoute[1] }));
    return;
  }

  if (recurringItemRoute && req.method === 'DELETE') {
    const at = recurring.findIndex((r) => r.id === recurringItemRoute[1]);
    if (at >= 0) recurring.splice(at, 1);
    res.writeHead(204, headers).end();
    return;
  }

  // Relatorios: a demo espelha a mesma regra de leitura do servidor - gasto e
  // o que entra na conta externa de despesa, receita o que sai da de receita.
  // Sem lancamento nenhum na resposta: so os numeros somados.
  const reportRoute = url.pathname.match(/^\/api\/reports\/(monthly|categories)$/);
  if (reportRoute && req.method === 'GET') {
    const months = Math.max(1, Math.min(120, Number(url.searchParams.get('months') ?? 6)));
    const until = url.searchParams.get('until') ?? new Date().toISOString().slice(0, 7);
    const grid = monthsEndingAt(until, months);

    if (reportRoute[1] === 'monthly') {
      const rows = grid.map((month) => {
        const legs = transactions
          .filter((t) => t.occurredOn.slice(0, 7) === month)
          .flatMap((t) => t.entries);

        return {
          month,
          incomeMinorUnits: -legs.filter((e) => e.accountType === 'REVENUE')
            .reduce((sum, e) => sum + e.amountMinorUnits, 0),
          expenseMinorUnits: legs.filter((e) => e.accountType === 'EXPENSE')
            .reduce((sum, e) => sum + e.amountMinorUnits, 0),
        };
      });

      res.writeHead(200, headers).end(JSON.stringify({ currencyCode: 'BRL', months: rows }));
      return;
    }

    const byCategory = new Map();
    for (const t of transactions) {
      const column = grid.indexOf(t.occurredOn.slice(0, 7));
      if (column < 0) continue;

      for (const e of t.entries) {
        if (e.accountType !== 'EXPENSE') continue;

        const key = e.categoryId ?? '';
        if (!byCategory.has(key)) {
          byCategory.set(key, {
            categoryId: e.categoryId ?? null,
            name: e.categoryName ?? 'Sem categoria',
            amountsMinorUnits: Array(months).fill(0),
          });
        }
        byCategory.get(key).amountsMinorUnits[column] += e.amountMinorUnits;
      }
    }

    const rows = [...byCategory.values()]
      .map((row) => {
        const total = row.amountsMinorUnits.reduce((sum, a) => sum + a, 0);
        // Divisao inteira, como a do servidor: dinheiro nao vira fracao.
        return { ...row, totalMinorUnits: total, averageMinorUnits: Math.trunc(total / months) };
      })
      .filter((row) => row.totalMinorUnits !== 0)
      .sort((a, b) => b.totalMinorUnits - a.totalMinorUnits);

    res.writeHead(200, headers).end(
      JSON.stringify({ currencyCode: 'BRL', months: grid, categories: rows }));
    return;
  }

  if (url.pathname === '/api/installments/upcoming' && req.method === 'GET') {
    res.writeHead(200, headers).end(JSON.stringify(upcoming(Number(url.searchParams.get('months') ?? 6))));
    return;
  }

  const cardTermsRoute = url.pathname.match(/^\/api\/accounts\/([\w-]+)\/card-terms$/);
  if (cardTermsRoute && req.method === 'PUT') {
    const body = await readJson(req);
    const existing = cardTerms.find((c) => c.accountId === cardTermsRoute[1]);
    const updated = {
      accountId: cardTermsRoute[1],
      closingDay: Number(body?.closingDay),
      dueDay: Number(body?.dueDay),
      creditLimitMinorUnits: existing?.creditLimitMinorUnits ?? null,
      paymentAccountId: existing?.paymentAccountId ?? null,
    };

    if (existing) Object.assign(existing, updated);
    else cardTerms.push(updated);

    res.writeHead(200, headers).end(JSON.stringify(updated));
    return;
  }

  const categorizeRoute = url.pathname.match(/^\/api\/transactions\/([\w-]+)\/category$/);
  if (categorizeRoute && req.method === 'PUT') {
    const found = transactions.find((t) => t.id === categorizeRoute[1]);
    const body = await readJson(req);
    const categoryId = body?.categoryId ?? null;
    const external = found?.entries.filter((e) => !e.accountIsInternal) ?? [];

    if (!found) {
      res.writeHead(404, headers).end(JSON.stringify({
        code: 'transaction.not_found',
        message: 'Lancamento nao encontrado.',
      }));
      return;
    }
    if (external.length !== 1) {
      res.writeHead(422, headers).end(JSON.stringify({
        code: external.length === 0 ? 'transaction.transfer_has_no_category' : 'transaction.split_has_many_categories',
        message: external.length === 0
          ? 'Transferencia entre contas suas nao tem categoria.'
          : 'Lancamento dividido: a categoria vive em cada perna.',
      }));
      return;
    }

    external[0].categoryId = categoryId;
    external[0].categoryName = categoryId ? categoryName[categoryId] ?? null : null;
    res.writeHead(204, headers).end();
    return;
  }

  const budgetRoute = url.pathname.match(/^\/api\/budget\/(\d{4}-(?:0[1-9]|1[0-2]))$/);
  if (budgetRoute && req.method === 'GET') {
    res.writeHead(200, headers).end(JSON.stringify(budgetMonth(budgetRoute[1])));
    return;
  }

  const assignRoute = url.pathname.match(/^\/api\/budget\/(\d{4}-(?:0[1-9]|1[0-2]))\/categories\/([\w-]+)$/);
  if (assignRoute && req.method === 'PUT') {
    const [, month, categoryId] = assignRoute;
    const body = await readJson(req);
    const amount = body?.amountMinorUnits;

    if (!Number.isSafeInteger(amount)) {
      res.writeHead(400, headers).end(JSON.stringify({ code: 'request.invalid', message: 'Valor inválido.' }));
      return;
    }
    if (!expenseCategories.has(categoryId)) {
      res.writeHead(422, headers).end(JSON.stringify({
        code: 'budget.income_category',
        message: 'Categoria de receita não recebe atribuição: receita vira dinheiro pronto para atribuir.',
      }));
      return;
    }

    if (amount === 0) assignments.delete(`${categoryId}|${month}`);
    else assignments.set(`${categoryId}|${month}`, amount);

    res.writeHead(200, headers).end(JSON.stringify(budgetMonth(month)));
    return;
  }

  // Escrita e aceita e descartada: a demonstracao e so para olhar.
  if (req.method !== 'GET') {
    res.writeHead(201, headers).end('{}');
    return;
  }

  const route = routes[url.pathname];
  res.writeHead(route ? 200 : 404, headers);
  res.end(JSON.stringify(route ? route() : { code: 'not_found', message: 'Rota inexistente na demonstração.' }));
});

await new Promise((ready) => apiServer.listen(API_PORT, ready));

// Variavel VITE_* ja presente no ambiente vence os arquivos .env.
process.env.VITE_NEMUS_API = `http://localhost:${API_PORT}`;

// Sem strictPort: outro projeto rodando na 5173 nao impede a demonstracao. O
// Vite pega a proxima porta livre, e printUrls abaixo diz qual.
const vite = await createViteServer({ server: { port: WEB_PORT, strictPort: false } });
await vite.listen();

console.log(`\n  Demonstração: ${transactions.length} lançamentos em seis meses, soma de todos os saldos = 0.`);
console.log('  Orçamento fechado em todos os meses: envelopes + pronto para atribuir = contas do orçamento.');
console.log('  Entre com QUALQUER usuário e senha. Escritas são descartadas, menos atribuições do orçamento.\n');
vite.printUrls();
