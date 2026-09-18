import { useEffect, useState, type FormEvent } from 'react';
import { Metric } from '../components/Metrics';
import { EmptyState, NewButton, PageHeader, Panel } from '../components/Panel';
import { api, ApiError, type CardTerms, type InstallmentPlan, type UpcomingCommitment } from '../lib/api';
import { capitalize, formatAmount, monthLongLabel, today } from '../lib/format';
import { parseToMinorUnits } from '../lib/money';
import type { AppData, PageProps } from '../lib/types';

/**
 * Parcelamento de cartao.
 *
 * As duas verdades de um "12x sem juros" aparecem separadas aqui, porque sao
 * separadas mesmo:
 *
 *   no razao       a divida inteira e de hoje. Ela ja esta no saldo do
 *                  cartao e no seu patrimonio, sem esperar as faturas.
 *
 *   no calendario  o compromisso se espalha pelas proximas faturas. E o
 *                  "quanto do meu mes que vem ja esta gasto antes de
 *                  comecar" - a pergunta que parcelamento cria.
 */
export function Installments({ data, reload, navigate }: PageProps) {
  const [plans, setPlans] = useState<InstallmentPlan[] | null>(null);
  const [upcoming, setUpcoming] = useState<UpcomingCommitment[]>([]);
  const [cards, setCards] = useState<CardTerms[]>([]);
  const [formOpen, setFormOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function load() {
    setError(null);
    try {
      const [loadedPlans, loadedUpcoming, loadedCards] = await Promise.all([
        api.listInstallments(),
        api.upcomingInstallments(6),
        api.listCards(),
      ]);
      setPlans(loadedPlans);
      setUpcoming(loadedUpcoming);
      setCards(loadedCards);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Não foi possível carregar os parcelamentos.');
      setPlans([]);
    }
  }

  useEffect(() => {
    void load();
    // Uma vez ao abrir: a pagina recarrega sozinha depois de cada criacao.
  }, []);

  const liabilities = data.accounts.filter((a) => a.type === 'LIABILITY' && !a.isSystem);
  const configured = liabilities.filter((a) => cards.some((c) => c.accountId === a.id));

  const open = plans?.filter((p) => !p.isFinished) ?? [];
  const committed = open.reduce((sum, p) => sum + p.remainingMinorUnits, 0);
  const nextMonth = upcoming[0];

  if (plans === null) {
    return (
      <>
        <PageHeader title="Parcelas" subtitle="O que já está comprometido nas próximas faturas." />
        <div className="skeleton" aria-busy="true" aria-label="Carregando">
          <div className="skeleton-block skeleton-large" />
        </div>
      </>
    );
  }

  return (
    <>
      <PageHeader
        title="Parcelas"
        subtitle={
          open.length === 0
            ? 'O que já está comprometido nas próximas faturas.'
            : `${open.length} ${open.length === 1 ? 'compra em aberto' : 'compras em aberto'} · ${formatAmount(committed, true)} a pagar`
        }
        actions={
          configured.length > 0 && (
            <NewButton open={formOpen} label="Compra parcelada" onToggle={() => setFormOpen((v) => !v)} />
          )
        }
      />

      {error && (
        <div className="error-banner" role="alert">
          <span>{error}</span>
        </div>
      )}

      {configured.length === 0 ? (
        <Panel>
          <EmptyState
            title={
              liabilities.length === 0
                ? 'Nenhum cartão cadastrado.'
                : 'Falta dizer quando o cartão fecha.'
            }
            text={
              liabilities.length === 0
                ? 'Parcelamento é de cartão. Crie um cartão em Contas — no sistema ele é uma conta de passivo.'
                : 'Sem o dia do fechamento e o do vencimento não dá para saber em qual fatura cada parcela cai. Configure em Contas.'
            }
            action={<button onClick={() => navigate('accounts')}>Ir para Contas</button>}
          />
        </Panel>
      ) : (
        <>
          {formOpen && (
            <Panel title="Compra parcelada" className="panel-form">
              <InstallmentForm
                data={data}
                cards={configured.map((a) => ({
                  id: a.id,
                  name: a.name,
                  terms: cards.find((c) => c.accountId === a.id)!,
                }))}
                onDone={async () => {
                  setFormOpen(false);
                  await load();
                  await reload();
                }}
              />
            </Panel>
          )}

          <div className="metrics metrics-3">
            <Metric
              featured
              label="A pagar em parcelas"
              value={committed}
              detail={open.length === 0 ? 'Nada parcelado em aberto' : 'Soma das parcelas que ainda vêm'}
            />
            <Metric
              label={nextMonth ? `Fatura de ${monthShort(nextMonth.month)}` : 'Próxima fatura'}
              value={nextMonth?.amountMinorUnits ?? 0}
              detail="Já comprometido antes do mês começar"
            />
            <Metric
              label="Juros contratados"
              value={open.reduce((sum, p) => sum + p.interestMinorUnits, 0)}
              tone={open.some((p) => p.interestMinorUnits > 0) ? 'debit' : 'neutral'}
              detail={`Em ${open.length} de ${plans.length} compras`}
            />
          </div>

          <div className="grid-2 grid-aside">
            <Panel title="Próximas faturas" actions={<span className="hint">6 meses</span>}>
              <UpcomingList upcoming={upcoming} />
            </Panel>

            <Panel title="Compras parceladas">
              {plans.length === 0 ? (
                <EmptyState
                  title="Nenhuma compra parcelada."
                  text="Quando registrar uma, o cartão passa a dever o valor inteiro hoje, e as parcelas aparecem no calendário das próximas faturas."
                />
              ) : (
                <PlanTable plans={plans} />
              )}
            </Panel>
          </div>

          <p className="note note-center">
            No sistema, uma compra parcelada é <strong>uma</strong> transação: o cartão deve tudo desde
            o dia da compra. As parcelas são compromisso de calendário, não lançamentos — por isso
            elas não mexem em saldo nenhum até a fatura chegar.
          </p>
        </>
      )}
    </>
  );
}

const monthShort = (key: string) => monthLongLabel(key).split(' ')[0];

function UpcomingList({ upcoming }: { upcoming: UpcomingCommitment[] }) {
  if (upcoming.length === 0) {
    return <p className="empty">Nenhuma parcela nas próximas faturas.</p>;
  }

  const largest = Math.max(...upcoming.map((u) => u.amountMinorUnits), 1);

  return (
    <ul className="glance">
      {upcoming.map((month) => (
        <li key={month.month} className="glance-row">
          <div className="glance-head">
            <span className="glance-name">{capitalize(monthLongLabel(month.month))}</span>
            <span className="glance-amount">{formatAmount(month.amountMinorUnits)}</span>
          </div>
          <svg className="categoria-bar spent" viewBox="0 0 100 4" preserveAspectRatio="none" aria-hidden="true">
            <rect className="bar-bg" width="100" height="4" rx="2" />
            <rect className="bar-fill" width={(month.amountMinorUnits / largest) * 100} height="4" rx="2" />
          </svg>
          <p className="subtext">
            {month.planCount === 1 ? '1 compra' : `${month.planCount} compras`}
          </p>
        </li>
      ))}
    </ul>
  );
}

function PlanTable({ plans }: { plans: InstallmentPlan[] }) {
  return (
    <div className="table-scroll">
      <table className="plans-table">
        <thead>
          <tr>
            <th>Compra</th>
            <th className="amount">Parcela</th>
            <th className="amount">Falta</th>
          </tr>
        </thead>
        <tbody>
          {plans.map((plan) => {
            const done = Math.min(plan.paidCount, plan.installmentCount);

            return (
              <tr key={plan.id} className={plan.isFinished ? 'plan-done' : undefined}>
                <td>
                  <div className="description">
                    <span className="description-text">{plan.description}</span>
                    {plan.interestMinorUnits > 0 && (
                      <span className="tag" title={`Juros de ${formatAmount(plan.interestMinorUnits, true)}`}>
                        COM JUROS
                      </span>
                    )}
                  </div>
                  <div className="subtext">
                    {plan.cardName} · {done} de {plan.installmentCount} · até{' '}
                    {monthShort(plan.lastStatementMonth)}
                  </div>
                  <svg className="categoria-bar ok" viewBox="0 0 100 4" preserveAspectRatio="none" aria-hidden="true">
                    <rect className="bar-bg" width="100" height="4" rx="2" />
                    <rect className="bar-fill" width={(done / plan.installmentCount) * 100} height="4" rx="2" />
                  </svg>
                </td>
                <td className="amount">{formatAmount(plan.installmentMinorUnits)}</td>
                <td className={`amount ${plan.isFinished ? '' : 'debit'}`}>
                  {plan.isFinished ? '—' : formatAmount(plan.remainingMinorUnits)}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function InstallmentForm({
  data,
  cards,
  onDone,
}: {
  data: AppData;
  cards: { id: string; name: string; terms: CardTerms }[];
  onDone: () => Promise<void>;
}) {
  const [cardId, setCardId] = useState(cards[0]?.id ?? '');
  const [date, setDate] = useState(today());
  const [description, setDescription] = useState('');
  const [amount, setAmount] = useState('');
  const [count, setCount] = useState('12');
  const [categoryId, setCategoryId] = useState('');
  const [withInterest, setWithInterest] = useState(false);
  const [financed, setFinanced] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const card = cards.find((c) => c.id === cardId);
  const expenseCategories = data.categories.filter((c) => c.kind === 'EXPENSE');

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);

    const total = parseToMinorUnits(amount);
    if (!total.ok) {
      setError(total.error);
      return;
    }

    const parts = Number(count);
    if (!Number.isInteger(parts) || parts < 1 || parts > 99) {
      setError('O número de parcelas vai de 1 a 99.');
      return;
    }

    let financedMinor: number | null = null;
    if (withInterest) {
      const parsed = parseToMinorUnits(financed);
      if (!parsed.ok) {
        setError(parsed.error);
        return;
      }
      if (parsed.value < total.value) {
        setError('O total parcelado não pode ser menor que o preço à vista.');
        return;
      }
      financedMinor = parsed.value;
    }

    setSaving(true);
    try {
      await api.createInstallmentPurchase({
        cardAccountId: cardId,
        occurredOn: date,
        description,
        totalAmountMinorUnits: total.value,
        financedAmountMinorUnits: financedMinor,
        installmentCount: parts,
        categoryId: categoryId === '' ? null : categoryId,
      });
      await onDone();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Não foi possível registrar a compra.');
    } finally {
      setSaving(false);
    }
  }

  return (
    <form className="form" onSubmit={submit}>
      <div className="fields">
        <label className="field">
          <span>Cartão</span>
          <select value={cardId} onChange={(e) => setCardId(e.target.value)}>
            {cards.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        <label className="field">
          <span>Data da compra</span>
          <input type="date" value={date} onChange={(e) => setDate(e.target.value)} />
        </label>
        <label className="field field-wide">
          <span>O que foi</span>
          <input
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Ex.: Sofá da sala"
            required
          />
        </label>
      </div>

      <div className="fields">
        <label className="field">
          <span>Preço à vista</span>
          <span className="field-money">
            <span className="prefix">R$</span>
            <input value={amount} onChange={(e) => setAmount(e.target.value)} placeholder="0,00" inputMode="decimal" />
          </span>
        </label>
        <label className="field">
          <span>Parcelas</span>
          <input
            type="number"
            min={1}
            max={99}
            value={count}
            onChange={(e) => setCount(e.target.value)}
            inputMode="numeric"
          />
        </label>
        <label className="field">
          <span>Categoria</span>
          <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
            <option value="">Sem categoria</option>
            {expenseCategories.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
      </div>

      <label className="check">
        <input type="checkbox" checked={withInterest} onChange={(e) => setWithInterest(e.target.checked)} />
        <span>
          <strong>Tem juros</strong>
          <span className="note">
            Marque quando a soma das parcelas passar do preço à vista. O juro entra como partida
            separada — misturado no preço, você nunca saberia quanto o parcelamento custou.
          </span>
        </span>
      </label>

      {withInterest && (
        <label className="field">
          <span>Total parcelado</span>
          <span className="field-money">
            <span className="prefix">R$</span>
            <input value={financed} onChange={(e) => setFinanced(e.target.value)} placeholder="0,00" inputMode="decimal" />
          </span>
        </label>
      )}

      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      <div className="form-footer">
        <p className="note">
          {card
            ? `Fecha dia ${card.terms.closingDay}, vence dia ${card.terms.dueDay}. Compra até o fechamento cai na fatura do próprio mês.`
            : 'Escolha o cartão.'}
        </p>
        <button type="submit" disabled={saving}>
          {saving ? 'Registrando…' : 'Registrar compra'}
        </button>
      </div>
    </form>
  );
}
