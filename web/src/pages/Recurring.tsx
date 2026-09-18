import { useCallback, useEffect, useState, type FormEvent } from 'react';
import { Icon } from '../components/Icon';
import { Metric } from '../components/Metrics';
import { EmptyState, NewButton, PageHeader, Panel } from '../components/Panel';
import {
  api,
  ApiError,
  type RecurringExpense,
  type RecurringMonth,
  type SaveRecurringInput,
} from '../lib/api';
import { capitalize, currentMonth, formatAmount, formatDay, monthLongLabel, today } from '../lib/format';
import { parseToMinorUnits } from '../lib/money';
import type { AppData, PageProps } from '../lib/types';

/**
 * Gastos fixos: o que se repete todo mes.
 *
 * NADA AQUI VIRA LANCAMENTO. Esta tela mostra uma PREVISAO e a confronta com
 * o razao. Criar a despesa sozinho no dia do vencimento faria o saldo mentir
 * ate a data real e duplicaria o gasto quando o extrato entrasse - por isso a
 * coluna nao diz "pago", diz "ja veio".
 *
 * O "ja veio" e cruzamento por categoria e valor, nao confirmacao: dentro da
 * mesma categoria, cada gasto fixo fica com o lancamento de valor mais
 * proximo do previsto. E como aluguel e condominio em "Casa" se separam.
 */
export function Recurring({ data, reload, navigate }: PageProps) {
  const month = currentMonth();

  const [view, setView] = useState<RecurringMonth | null>(null);
  const [editing, setEditing] = useState<RecurringExpense | null>(null);
  const [formOpen, setFormOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    setError(null);
    try {
      setView(await api.recurring(month));
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Falha ao carregar os gastos fixos.');
    } finally {
      setLoading(false);
    }
  }, [month]);

  useEffect(() => {
    void load();
  }, [load]);

  const expenseCategories = data.categories.filter((c) => c.kind === 'EXPENSE');

  async function assignToBudget() {
    setError(null);
    setNotice(null);

    try {
      const result = await api.assignRecurring(month);
      setNotice(
        result.filled === 0
          ? 'Todos as categorias desses gastos já tinham valor. Nada foi sobrescrito.'
          : `${result.filled} ${result.filled === 1 ? 'categoria preenchida' : 'categorias preenchidas'}` +
            (result.skipped > 0 ? `, ${result.skipped} já tinha valor e ficou como estava.` : '.'),
      );
      await reload();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Não foi possível separar ao orçamento.');
    }
  }

  async function remove(item: RecurringExpense) {
    setError(null);
    try {
      await api.removeRecurring(item.id);
      await load();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Não foi possível arquivar.');
    }
  }

  if (loading) {
    return (
      <div className="skeleton" aria-busy="true" aria-label="Carregando">
        <div className="skeleton-block skeleton-large" />
      </div>
    );
  }

  const items = view?.items ?? [];
  const pending = items.filter((i) => !i.isMatched);

  return (
    <>
      <PageHeader
        title="Gastos fixos"
        subtitle={`${capitalize(monthLongLabel(month))} · o que se repete todo mês`}
        actions={
          <>
            {items.length > 0 && (
              <button type="button" className="secondary" onClick={() => void assignToBudget()}>
                <Icon name="budget" size={15} />
                <span>Separar o dinheiro</span>
              </button>
            )}
            <NewButton
              open={formOpen}
              label="Novo gasto fixo"
              onToggle={() => {
                setEditing(null);
                setFormOpen(!formOpen);
              }}
            />
          </>
        }
      />

      {error && (
        <div className="error-banner" role="alert">
          <span>{error}</span>
        </div>
      )}

      {notice && (
        <p className="note note-center" role="status">
          {notice}
        </p>
      )}

      {(formOpen || editing) && (
        <Panel title={editing ? `Editar ${editing.name}` : 'Novo gasto fixo'} className="panel-form">
          <RecurringForm
            data={data}
            editing={editing}
            categories={expenseCategories}
            onCancel={() => {
              setEditing(null);
              setFormOpen(false);
            }}
            onDone={async () => {
              setEditing(null);
              setFormOpen(false);
              await load();
            }}
          />
        </Panel>
      )}

      {expenseCategories.length === 0 ? (
        <Panel>
          <EmptyState
            title="Ainda não há categorias de despesa."
            text="Todo gasto fixo mora numa categoria — é ela que liga a previsão ao dinheiro separado do orçamento."
            action={<button onClick={() => navigate('categories')}>Criar categorias</button>}
          />
        </Panel>
      ) : items.length === 0 ? (
        <Panel>
          <EmptyState
            title="Nenhum gasto fixo cadastrado."
            text="Aluguel, internet, academia, assinatura: o que sai todo mês no mesmo valor. A lista não lança nada — ela mostra o que esperar e confere com o que já entrou no sistema."
            action={<button onClick={() => setFormOpen(true)}>Cadastrar o primeiro</button>}
          />
        </Panel>
      ) : (
        <>
          <div className="metrics metrics-3">
            <Metric
              featured
              label="Previsto no mês"
              value={view?.expectedMinorUnits ?? 0}
              detail={`${items.length} ${items.length === 1 ? 'gasto fixo' : 'gastos fixos'}`}
            />
            <Metric
              label="Já veio"
              value={view?.matchedMinorUnits ?? 0}
              tone="credit"
              detail={`${items.length - pending.length} de ${items.length} encontrados no sistema`}
            />
            <Metric
              label="Ainda vem"
              value={view?.pendingMinorUnits ?? 0}
              tone={pending.length > 0 ? 'debit' : 'neutral'}
              detail={pending.length === 0 ? 'Tudo já passou pelo sistema' : 'Previsto do que falta'}
            />
          </div>

          <Panel title="A lista do mês">
            <RecurringTable items={items} onEdit={setEditing} onRemove={(i) => void remove(i)} />
          </Panel>

          <p className="note note-center">
            Nada nesta tela vira lançamento. O gasto de verdade chega pelo extrato ou pela mão, e a
            coluna <strong>Situação</strong> só diz se ele já apareceu — comparando categoria e
            valor, não confirmando pagamento.
          </p>
        </>
      )}
    </>
  );
}

function RecurringTable({
  items,
  onEdit,
  onRemove,
}: {
  items: RecurringExpense[];
  onEdit: (item: RecurringExpense) => void;
  onRemove: (item: RecurringExpense) => void;
}) {
  return (
    <div className="table-scroll">
      <table className="recurring-table">
        <thead>
          <tr>
            <th scope="col">Gasto</th>
            <th scope="col">Vence</th>
            <th scope="col" className="amount">
              Previsto
            </th>
            <th scope="col">Situação</th>
            <th scope="col" aria-label="Ações" />
          </tr>
        </thead>
        <tbody>
          {items.map((item) => (
            <tr key={item.id} className={item.isMatched ? undefined : 'pending'}>
              <th scope="row">
                <div className="glance-name">{item.name}</div>
                {/* Chamar o gasto fixo pelo nome da categoria e comum
                    ("Aluguel" dentro de "Aluguel"). Repetir a palavra nao
                    informa nada e so faz a linha parecer duplicada. */}
                <div className="subtext">
                  {item.categoryName !== item.name && item.categoryName}
                  {item.isEstimate && <span className="chip">estimado</span>}
                </div>
              </th>

              <td>
                <span className="due-day">dia {item.dueDay}</span>
              </td>

              <td className="amount">{formatAmount(item.amountMinorUnits)}</td>

              <td>
                {item.isMatched ? (
                  <Status item={item} />
                ) : (
                  <span className="status waiting">
                    <Icon name="alert" size={13} />
                    ainda não veio
                  </span>
                )}
              </td>

              <td className="row-actions">
                <button type="button" className="chip-button" onClick={() => onEdit(item)}>
                  Editar
                </button>
                <button type="button" className="chip-button" onClick={() => onRemove(item)}>
                  Arquivar
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

/**
 * Quando ja veio, o que interessa e a diferenca. Mostrar so "veio" esconderia
 * justamente o mes em que a conta mudou - que e quando alguem olha.
 */
function Status({ item }: { item: RecurringExpense }) {
  const difference = item.differenceMinorUnits ?? 0;

  return (
    <span className="status came">
      <Icon name="check" size={13} />
      {formatAmount(item.actualMinorUnits ?? 0)}
      {item.occurredOn && <span className="subtext"> em {formatDay(item.occurredOn)}</span>}
      {difference !== 0 && (
        <span className={difference > 0 ? 'chip debit' : 'chip credit'}>
          {difference > 0 ? '+' : '−'}
          {formatAmount(Math.abs(difference))}
        </span>
      )}
    </span>
  );
}

function RecurringForm({
  data,
  editing,
  categories,
  onCancel,
  onDone,
}: {
  data: AppData;
  editing: RecurringExpense | null;
  categories: AppData['categories'];
  onCancel: () => void;
  onDone: () => Promise<void>;
}) {
  const [name, setName] = useState(editing?.name ?? '');
  const [categoryId, setCategoryId] = useState(editing?.categoryId ?? categories[0]?.id ?? '');
  // formatAmount, e nao uma divisao por 100: dinheiro nao vira float nem por
  // um passo, nem para preencher um campo de texto.
  const [amount, setAmount] = useState(editing ? formatAmount(editing.amountMinorUnits) : '');
  const [dueDay, setDueDay] = useState(String(editing?.dueDay ?? 10));
  const [accountId, setAccountId] = useState(editing?.accountId ?? '');
  const [isEstimate, setIsEstimate] = useState(editing?.isEstimate ?? false);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const accounts = data.accounts.filter((a) => !a.isSystem && a.isInternal);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);

    const parsed = parseToMinorUnits(amount);
    if (!parsed.ok) {
      setError(parsed.error);
      return;
    }

    const day = Number(dueDay);
    if (!Number.isInteger(day) || day < 1 || day > 31) {
      setError('O dia do vencimento vai de 1 a 31.');
      return;
    }

    const input: SaveRecurringInput = {
      name,
      categoryId,
      amountMinorUnits: parsed.value,
      dueDay: day,
      accountId: accountId === '' ? null : accountId,
      isEstimate,
      startsOn: editing?.startsOn ?? today(),
      endsOn: editing?.endsOn ?? null,
    };

    setSaving(true);
    try {
      if (editing) {
        await api.updateRecurring(editing.id, input);
      } else {
        await api.createRecurring(input);
      }
      await onDone();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Não foi possível salvar o gasto fixo.');
    } finally {
      setSaving(false);
    }
  }

  return (
    <form className="form" onSubmit={submit}>
      <div className="fields">
        <label className="field">
          <span>Nome</span>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Aluguel"
            maxLength={120}
            required
          />
        </label>

        <label className="field">
          <span>Categoria</span>
          <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)} required>
            {categories.map((c) => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>

        <label className="field">
          <span>Valor</span>
          <span className="field-money">
            <span className="prefix">R$</span>
            <input
              value={amount}
              onChange={(e) => setAmount(e.target.value)}
              onFocus={(e) => e.target.select()}
              inputMode="decimal"
              placeholder="0,00"
              required
            />
          </span>
        </label>

        <label className="field">
          <span>Vence no dia</span>
          <input
            type="number"
            min={1}
            max={31}
            value={dueDay}
            onChange={(e) => setDueDay(e.target.value)}
            required
          />
        </label>

        <label className="field">
          <span>Sai de (opcional)</span>
          <select value={accountId} onChange={(e) => setAccountId(e.target.value)}>
            <option value="">—</option>
            {accounts.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name}
              </option>
            ))}
          </select>
        </label>
      </div>

      <label className="toggle">
        <input
          type="checkbox"
          checked={isEstimate}
          onChange={(e) => setIsEstimate(e.target.checked)}
        />
        <span>
          O valor varia todo mês <span className="subtext">(luz, água, gás)</span>
        </span>
      </label>

      {error && (
        <div className="error-banner" role="alert">
          <span>{error}</span>
        </div>
      )}

      <div className="form-footer">
        <p className="note">
          Previsão, não lançamento: cadastrar aqui não mexe em saldo nenhum.
        </p>
        <button type="button" className="secondary" onClick={onCancel}>
          Cancelar
        </button>
        <button type="submit" disabled={saving}>
          {saving ? 'Salvando…' : editing ? 'Salvar' : 'Cadastrar'}
        </button>
      </div>
    </form>
  );
}
