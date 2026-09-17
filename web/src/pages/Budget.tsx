import { useEffect, useRef, useState, type KeyboardEvent } from 'react';
import { Icon } from '../components/Icon';
import { EmptyState, PageHeader, Panel } from '../components/Panel';
import { api, ApiError, type BudgetCategory, type BudgetMonth } from '../lib/api';
import {
  envelopeUsage,
  groupEnvelopes,
  hasOwnMoney,
  readyState,
  type EnvelopeGroup,
} from '../lib/budget';
import { capitalize, currentMonth, formatAmount, monthLongLabel, shiftMonth } from '../lib/format';
import { formatMinorUnits, parseToMinorUnits } from '../lib/money';
import type { PageProps } from '../lib/types';

/**
 * O orcamento de envelopes. Todo real das contas do orcamento esta num
 * envelope ou esperando atribuicao; esta tela existe para levar o "pronto
 * para atribuir" a zero e mostrar, a qualquer momento, que a conta fecha.
 */
export function Budget({ data, reload, navigate }: PageProps) {
  const thisMonth = currentMonth();
  const [month, setMonth] = useState(thisMonth);
  const [view, setView] = useState<BudgetMonth | null>(data.budget?.month === thisMonth ? data.budget : null);
  const [loading, setLoading] = useState(view === null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);

    api
      .budget(month)
      .then((loaded) => {
        if (!cancelled) setView(loaded);
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof Error ? e.message : 'Não foi possível carregar o orçamento.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [month]);

  async function assign(categoryId: string, amount: number) {
    setError(null);
    try {
      setView(await api.assign(month, categoryId, amount));
      // A visao geral mostra o mes corrente; sem isto ela ficaria para tras.
      if (month === thisMonth) void reload();
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) {
        void reload();
      }
      setError(e instanceof Error ? e.message : 'Não foi possível salvar a atribuição.');
      throw e;
    }
  }

  const hasExpenseCategories = data.categories.some((c) => c.kind === 'EXPENSE');
  const monthName = capitalize(monthLongLabel(month));
  const overspent = view?.categories.filter((c) => c.availableMinorUnits < 0) ?? [];
  const groups = view ? groupEnvelopes(view.categories) : [];
  // Grupo com subcategorias e cabecalho, nao envelope - a menos que tenha dinheiro proprio.
  const envelopeCount = groups.reduce(
    (count, g) => count + (g.children.length === 0 ? 1 : g.children.length + (hasOwnMoney(g.head) ? 1 : 0)),
    0,
  );

  const monthNav = (
    <div className="month-nav" role="group" aria-label="Mês do orçamento">
      <button type="button" className="secondary icon-only flip" onClick={() => setMonth(shiftMonth(month, -1))} aria-label="Mês anterior">
        <Icon name="chevron" size={16} />
      </button>
      <span className="month-nav-label" aria-live="polite">
        {monthName}
      </span>
      <button type="button" className="secondary icon-only" onClick={() => setMonth(shiftMonth(month, 1))} aria-label="Próximo mês">
        <Icon name="chevron" size={16} />
      </button>
      {month !== thisMonth && (
        <button type="button" className="link" onClick={() => setMonth(thisMonth)}>
          Voltar para hoje
        </button>
      )}
    </div>
  );

  if (!hasExpenseCategories) {
    return (
      <>
        <PageHeader title="Orçamento" subtitle="Cada real com um destino." />
        <Panel>
          <EmptyState
            title="Nenhum envelope ainda."
            text="Envelope é categoria de despesa. Crie as suas — moradia, mercado, transporte — e volte para dividir o dinheiro entre elas."
            action={<button onClick={() => navigate('categories')}>Criar categorias</button>}
          />
        </Panel>
      </>
    );
  }

  return (
    <>
      <PageHeader
        title="Orçamento"
        subtitle={
          view
            ? `${envelopeCount} ${envelopeCount === 1 ? 'envelope' : 'envelopes'} · ${formatAmount(-view.activityMinorUnits, true)} gastos em ${monthLongLabel(month)}`
            : 'Cada real com um destino.'
        }
        actions={monthNav}
      />

      {error && (
        <div className="error-banner" role="alert">
          <span>{error}</span>
        </div>
      )}

      {view === null ? (
        <div className="skeleton" aria-busy="true" aria-label="Carregando">
          <div className="skeleton-block skeleton-large" />
        </div>
      ) : (
        <div className={`budget${loading ? ' is-loading' : ''}`} aria-busy={loading}>
          <ReadyToAssign view={view} />

          <div className="metrics metrics-3">
            <MonthFigure label={`Atribuído em ${monthShort(month)}`} value={view.assignedMinorUnits} detail="Colocado nos envelopes neste mês" />
            <MonthFigure
              label={`Gasto em ${monthShort(month)}`}
              value={-view.activityMinorUnits}
              detail={view.activityMinorUnits > 0 ? 'Estornos maiores que os gastos' : 'Saiu dos envelopes'}
            />
            <MonthFigure
              label={`Entrou em ${monthShort(month)}`}
              value={view.netInflowMinorUnits}
              detail="Receita e saldo novo, ainda sem envelope"
            />
          </div>

          {view.uncategorizedTransactions > 0 && (
            <div className="callout callout-warning" role="note">
              <Icon name="alert" size={17} />
              <p>
                <strong>
                  {view.uncategorizedTransactions === 1
                    ? '1 lançamento sem categoria'
                    : `${view.uncategorizedTransactions} lançamentos sem categoria`}
                </strong>{' '}
                em {monthLongLabel(month)}, somando {formatAmount(view.uncategorizedMinorUnits, true)}. Enquanto não
                tiverem envelope, esse dinheiro sai do pronto para atribuir.
              </p>
              <button
                type="button"
                className="secondary"
                onClick={() => {
                  window.location.hash = '#/lancamentos?sem-categoria';
                }}
              >
                Categorizar
              </button>
            </div>
          )}

          {overspent.length > 0 && (
            <div className="callout callout-debit" role="note">
              <Icon name="alert" size={17} />
              <p>
                <strong>
                  {overspent.length === 1 ? '1 envelope estourou' : `${overspent.length} envelopes estouraram`}
                </strong>
                . O estouro rola para o mês seguinte até ser coberto — use <em>Cobrir</em> para tirar do pronto
                para atribuir, ou reduza outro envelope.
              </p>
            </div>
          )}

          <Panel title="Envelopes" actions={<span className="hint">Clique no valor atribuído para mudar</span>}>
            <EnvelopeTable groups={groups} onAssign={assign} view={view} />
          </Panel>
        </div>
      )}
    </>
  );
}

const monthShort = (key: string) => monthLongLabel(key).split(' ')[0];

function MonthFigure({ label, value, detail }: { label: string; value: number; detail: string }) {
  return (
    <div className="metric">
      <p className="metric-label">{label}</p>
      <p className="metric-value">{formatAmount(value, true)}</p>
      <p className="metric-detail">{detail}</p>
    </div>
  );
}

/**
 * O numero que o metodo manda zerar, e ao lado a prova de que a conta fecha.
 * Mesmo espirito do selo do razao: nao e enfeite, e a soma verificavel.
 */
function ReadyToAssign({ view }: { view: BudgetMonth }) {
  const state = readyState(view.readyToAssignMinorUnits);
  const message = {
    unassigned: 'Dinheiro esperando um envelope. Distribua até zerar.',
    balanced: 'Todo real tem um destino.',
    overassigned: 'Atribuído além do que existe nas contas. Tire de algum envelope.',
  }[state];

  return (
    <section className={`ready ready-${state}`} aria-label="Pronto para atribuir">
      <div className="ready-main">
        <p className="ready-label">Pronto para atribuir</p>
        <p className="ready-value">{formatAmount(view.readyToAssignMinorUnits, true)}</p>
        <p className="ready-text">{message}</p>
      </div>

      {/* Soma de fita de calculadora: duas parcelas, fio duplo, total. */}
      <div className="equation" role="group" aria-label="A conta do orçamento">
        <div className="equation-line">
          <span className="equation-label">
            <span className="equation-op" aria-hidden="true" />
            Nos envelopes
          </span>
          <span className="equation-value">{formatAmount(view.availableMinorUnits, true)}</span>
        </div>
        <div className="equation-line">
          <span className="equation-label">
            <span className="equation-op" aria-hidden="true">+</span>
            A atribuir
          </span>
          <span className="equation-value">{formatAmount(view.readyToAssignMinorUnits, true)}</span>
        </div>
        <div className="equation-line equation-total">
          <span className="equation-label">
            <span className="equation-op" aria-hidden="true">=</span>
            Nas contas do orçamento
          </span>
          <span className="equation-value">
            {formatAmount(view.onBudgetBalanceMinorUnits, true)}
            <span
              className={`equation-check ${view.isBalanced ? 'ok' : 'bad'}`}
              title={
                view.isBalanced
                  ? 'O saldo das contas, lido direto do razão, bate com envelopes mais o que falta atribuir.'
                  : 'A conta não fecha: o saldo do razão difere de envelopes mais pronto para atribuir.'
              }
            >
              <Icon name={view.isBalanced ? 'check' : 'alert'} size={13} />
              <span className="sr-only">{view.isBalanced ? 'A conta fecha' : 'A conta não fecha'}</span>
            </span>
          </span>
        </div>
      </div>
    </section>
  );
}

function EnvelopeTable({
  groups,
  view,
  onAssign,
}: {
  groups: EnvelopeGroup[];
  view: BudgetMonth;
  onAssign: (categoryId: string, amount: number) => Promise<void>;
}) {
  return (
    <div className="table-scroll">
      <table className="budget-table">
        <thead>
          <tr>
            <th>Envelope</th>
            <th className="amount col-assigned">Atribuído</th>
            <th className="amount col-activity">Atividade</th>
            <th className="amount col-available">Disponível</th>
          </tr>
        </thead>
        {groups.map((group) =>
          group.children.length === 0 ? (
            <tbody key={group.head.categoryId} className="envelope-single">
              <EnvelopeRow category={group.head} onAssign={onAssign} />
            </tbody>
          ) : (
            <tbody key={group.head.categoryId}>
              <tr className="envelope-group">
                <th scope="rowgroup">{group.head.name}</th>
                <td className="amount">{formatAmount(group.totals.assigned)}</td>
                <td className="amount activity">{group.totals.activity === 0 ? '—' : formatAmount(group.totals.activity)}</td>
                <td className={`amount ${group.totals.available < 0 ? 'debit' : ''}`}>{formatAmount(group.totals.available)}</td>
              </tr>
              {hasOwnMoney(group.head) && (
                <EnvelopeRow category={group.head} label={`${group.head.name} (sem subcategoria)`} nested onAssign={onAssign} />
              )}
              {group.children.map((child) => (
                <EnvelopeRow key={child.categoryId} category={child} nested onAssign={onAssign} />
              ))}
            </tbody>
          ),
        )}
        <tfoot>
          <tr>
            <td>Total</td>
            <td className="amount">{formatAmount(view.assignedMinorUnits)}</td>
            <td className="amount activity">{formatAmount(view.activityMinorUnits)}</td>
            <td className={`amount ${view.availableMinorUnits < 0 ? 'debit' : ''}`}>{formatAmount(view.availableMinorUnits)}</td>
          </tr>
        </tfoot>
      </table>
    </div>
  );
}

function EnvelopeRow({
  category,
  label,
  nested = false,
  onAssign,
}: {
  category: BudgetCategory;
  label?: string;
  nested?: boolean;
  onAssign: (categoryId: string, amount: number) => Promise<void>;
}) {
  const usage = envelopeUsage(category);
  const name = label ?? category.name;
  const available = category.availableMinorUnits;

  return (
    <tr className={`envelope-row${nested ? ' nested' : ''}`}>
      <td>
        <div className="envelope-name">
          <span>{name}</span>
          {category.isArchived && <span className="tag">ARQUIVADA</span>}
        </div>
        <svg className={`envelope-bar ${usage.state}`} viewBox="0 0 100 4" preserveAspectRatio="none" aria-hidden="true">
          <rect className="bar-bg" width="100" height="4" rx="2" />
          {usage.fill > 0 && <rect className="bar-fill" width={usage.fill} height="4" rx="2" />}
        </svg>
      </td>
      <td className="amount">
        <AssignedInput
          value={category.assignedMinorUnits}
          label={name}
          onSave={(amount) => onAssign(category.categoryId, amount)}
        />
      </td>
      <td className="amount activity">{category.activityMinorUnits === 0 ? '—' : formatAmount(category.activityMinorUnits)}</td>
      <td className="amount">
        <div className="available-cell">
          {available < 0 && (
            <button
              type="button"
              className="link cover"
              aria-label={`Cobrir o estouro de ${name}: atribuir mais ${formatAmount(-available, true)}`}
              title={`Atribui mais ${formatAmount(-available, true)} a ${name}, tirando do pronto para atribuir.`}
              onClick={() => void onAssign(category.categoryId, category.assignedMinorUnits - available).catch(() => undefined)}
            >
              Cobrir
            </button>
          )}
          <span className={`pill ${available < 0 ? 'pill-debit' : available > 0 ? 'pill-credit' : 'pill-zero'}`}>
            {formatAmount(available)}
          </span>
        </div>
      </td>
    </tr>
  );
}

/**
 * Valor atribuido, editavel no lugar. Enter ou sair do campo grava; Esc
 * desiste. Negativo e aceito - e como se tira dinheiro de um envelope.
 */
function AssignedInput({
  value,
  label,
  onSave,
}: {
  value: number;
  label: string;
  onSave: (amount: number) => Promise<void>;
}) {
  const [editing, setEditing] = useState(false);
  const [text, setText] = useState('');
  const [invalid, setInvalid] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  // Enter e Esc tiram o campo da tela, e o blur que vem junto nao pode gravar
  // de novo nem gravar o que acabou de ser cancelado.
  const busy = useRef(false);
  const cancelled = useRef(false);

  function start() {
    setText(value === 0 ? '' : formatMinorUnits(value));
    setInvalid(null);
    cancelled.current = false;
    setEditing(true);
  }

  async function commit() {
    if (busy.current || cancelled.current) return;

    const parsed = text.trim() === '' ? ({ ok: true, value: 0 } as const) : parseToMinorUnits(text);
    if (!parsed.ok) {
      setInvalid(parsed.error);
      return;
    }
    if (parsed.value === value) {
      setEditing(false);
      return;
    }

    busy.current = true;
    setSaving(true);
    try {
      await onSave(parsed.value);
      setEditing(false);
    } catch {
      /* a pagina mostra o erro; o campo continua aberto com o que foi digitado */
    } finally {
      busy.current = false;
      setSaving(false);
    }
  }

  function onKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Enter') {
      event.preventDefault();
      void commit();
    } else if (event.key === 'Escape') {
      cancelled.current = true;
      setEditing(false);
    }
  }

  if (!editing) {
    return (
      <button type="button" className="assigned-button" onClick={start} aria-label={`Atribuído a ${label}: ${formatAmount(value, true)}. Alterar`}>
        {value === 0 ? <span className="assigned-empty">—</span> : formatAmount(value)}
      </button>
    );
  }

  return (
    <span className="assigned-edit">
      <input
        className="assigned-input"
        value={text}
        onChange={(e) => {
          setText(e.target.value);
          setInvalid(null);
        }}
        onKeyDown={onKeyDown}
        // Abre com tudo selecionado: digitar substitui o valor, nao se cola nele.
        onFocus={(e) => e.currentTarget.select()}
        onBlur={() => void commit()}
        inputMode="decimal"
        placeholder="0,00"
        disabled={saving}
        aria-label={`Valor atribuído a ${label}`}
        aria-invalid={invalid !== null}
        title={invalid ?? undefined}
        autoFocus
      />
      {invalid && <span className="assigned-error">{invalid}</span>}
    </span>
  );
}
