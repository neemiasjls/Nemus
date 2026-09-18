import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { DonutChart } from '../charts/DonutChart';
import { Icon } from '../components/Icon';
import { NewButton, PageHeader, Panel } from '../components/Panel';
import { api, ApiError, type Account, type CardTerms } from '../lib/api';
import { ACCOUNT_TYPE_LABEL, formatAmount, today } from '../lib/format';
import { parseToMinorUnits } from '../lib/money';
import type { PageProps } from '../lib/types';

const sumBalances = (accounts: Account[]) => accounts.reduce((sum, a) => sum + a.balanceMinorUnits, 0);

export function Accounts({ data, reload }: PageProps) {
  const [formOpen, setFormOpen] = useState(false);

  const assets = data.accounts.filter((a) => a.type === 'ASSET' && !a.isSystem);
  const liabilities = data.accounts.filter((a) => a.type === 'LIABILITY' && !a.isSystem);
  const systemAccounts = data.accounts.filter((a) => a.isSystem);
  const grandTotal = sumBalances(data.accounts);

  const whereMoneyIs = useMemo(
    () =>
      assets
        .filter((a) => a.balanceMinorUnits > 0)
        .map((a) => ({ name: a.name, amount: a.balanceMinorUnits }))
        .sort((a, b) => b.amount - a.amount),
    [assets],
  );

  return (
    <>
      <PageHeader
        title="Contas"
        subtitle="Onde o dinheiro está e o que você deve."
        actions={<NewButton open={formOpen} label="Nova conta" onToggle={() => setFormOpen((v) => !v)} />}
      />

      {formOpen && (
        <Panel title="Nova conta" className="panel-form">
          <AccountForm
            onDone={async () => {
              setFormOpen(false);
              await reload();
            }}
          />
        </Panel>
      )}

      {liabilities.length > 0 && <Cards accounts={liabilities} />}

      <div className="grid-2">
        <AccountGroup
          title="Ativos"
          description="Contas, poupança, investimentos"
          accounts={assets}
          emptyText="Nenhuma conta de ativo ainda."
        />
        <AccountGroup
          title="Passivos"
          description="Cartões e empréstimos"
          accounts={liabilities}
          emptyText="Nenhum cartão ou empréstimo."
        />
      </div>

      <div className="grid-2 grid-aside">
        <Panel title="Onde está o dinheiro">
          <DonutChart items={whereMoneyIs} centerLabel="em ativos" />
        </Panel>

        <Panel title="Contas do sistema">
          <p className="lead">
            Contas que o sistema cria sozinho. É por causa delas que a soma de{' '}
            <em>todos</em> os saldos fecha exatamente em zero: o dinheiro nunca sai do sistema —
            ele vai para uma conta que representa o mundo lá fora.
          </p>
          <table className="accounts-table">
            <tbody>
              {systemAccounts.map((a) => (
                <tr key={a.id}>
                  <td>
                    <div className="account-name">{a.name}</div>
                    <div className="subtext">{ACCOUNT_TYPE_LABEL[a.type] ?? a.type}</div>
                  </td>
                  <td className={`amount ${a.balanceMinorUnits < 0 ? 'debit' : ''}`}>
                    {formatAmount(a.balanceMinorUnits)}
                  </td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <td>Soma de todas as contas</td>
                <td className={`amount ${grandTotal === 0 ? 'credit' : 'debit'}`}>{formatAmount(grandTotal)}</td>
              </tr>
            </tfoot>
          </table>
        </Panel>
      </div>
    </>
  );
}

/**
 * Fechamento e vencimento de cada cartao.
 *
 * Nao e enfeite de cadastro: sao estes dois numeros que dizem em qual fatura
 * uma compra cai, e sem eles o parcelamento nao tem competencia - as parcelas
 * ficariam penduradas em nenhum mes.
 */
function Cards({ accounts }: { accounts: Account[] }) {
  const [terms, setTerms] = useState<CardTerms[]>([]);
  const [editing, setEditing] = useState<string | null>(null);

  async function load() {
    try {
      setTerms(await api.listCards());
    } catch {
      /* sem condicoes cadastradas ainda, ou API fora: a tela mostra "faltando" */
    }
  }

  useEffect(() => {
    void load();
  }, []);

  return (
    <Panel
      title="Cartões"
      actions={<span className="hint">Quando fecha e quando vence</span>}
    >
      <table className="accounts-table">
        <tbody>
          {accounts.map((card) => {
            const current = terms.find((t) => t.accountId === card.id);

            return (
              <tr key={card.id}>
                <td>
                  <div className="account-name">{card.name}</div>
                  {editing === card.id ? (
                    <CardTermsForm
                      account={card}
                      current={current}
                      onDone={async () => {
                        setEditing(null);
                        await load();
                      }}
                      onCancel={() => setEditing(null)}
                    />
                  ) : (
                    <div className="subtext">
                      {current
                        ? `Fecha dia ${current.closingDay} · vence dia ${current.dueDay}`
                        : 'Sem fechamento e vencimento — o parcelamento precisa deles'}
                    </div>
                  )}
                </td>
                <td className="amount">
                  {editing !== card.id && (
                    <button
                      type="button"
                      className={current ? 'link' : 'secondary'}
                      onClick={() => setEditing(card.id)}
                    >
                      {current ? 'Mudar' : (
                        <>
                          <Icon name="card" size={14} />
                          <span>Configurar</span>
                        </>
                      )}
                    </button>
                  )}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </Panel>
  );
}

function CardTermsForm({
  account,
  current,
  onDone,
  onCancel,
}: {
  account: Account;
  current: CardTerms | undefined;
  onDone: () => Promise<void>;
  onCancel: () => void;
}) {
  const [closingDay, setClosingDay] = useState(String(current?.closingDay ?? 25));
  const [dueDay, setDueDay] = useState(String(current?.dueDay ?? 5));
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setSaving(true);

    try {
      await api.saveCardTerms(account.id, {
        closingDay: Number(closingDay),
        dueDay: Number(dueDay),
      });
      await onDone();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Não foi possível salvar.');
    } finally {
      setSaving(false);
    }
  }

  return (
    <form className="card-terms" onSubmit={submit}>
      <label className="field">
        <span>Fecha dia</span>
        <input
          type="number"
          min={1}
          max={31}
          value={closingDay}
          onChange={(e) => setClosingDay(e.target.value)}
          inputMode="numeric"
        />
      </label>
      <label className="field">
        <span>Vence dia</span>
        <input
          type="number"
          min={1}
          max={31}
          value={dueDay}
          onChange={(e) => setDueDay(e.target.value)}
          inputMode="numeric"
        />
      </label>
      <div className="button-row">
        <button type="submit" disabled={saving}>
          {saving ? 'Salvando…' : 'Salvar'}
        </button>
        <button type="button" className="secondary" onClick={onCancel}>
          Cancelar
        </button>
      </div>
      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}
    </form>
  );
}

function AccountGroup({
  title,
  description,
  accounts,
  emptyText,
}: {
  title: string;
  description: string;
  accounts: Account[];
  emptyText: string;
}) {
  const total = sumBalances(accounts);

  return (
    <Panel title={title} actions={<span className="hint">{description}</span>}>
      {accounts.length === 0 ? (
        <p className="empty">{emptyText}</p>
      ) : (
        <table className="accounts-table">
          <tbody>
            {accounts.map((a) => (
              <tr key={a.id}>
                <td>
                  <div className="account-name">{a.name}</div>
                  <div className="subtext">{a.isOnBudget ? 'No orçamento' : 'Fora do orçamento'}</div>
                </td>
                <td className={`amount ${a.balanceMinorUnits < 0 ? 'debit' : ''}`}>
                  {formatAmount(a.balanceMinorUnits)}
                </td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            <tr>
              <td>Total</td>
              <td className={`amount ${total < 0 ? 'debit' : ''}`}>{formatAmount(total)}</td>
            </tr>
          </tfoot>
        </table>
      )}
    </Panel>
  );
}

function AccountForm({ onDone }: { onDone: () => Promise<void> }) {
  const [name, setName] = useState('');
  const [type, setType] = useState<'ASSET' | 'LIABILITY'>('ASSET');
  const [balance, setBalance] = useState('');
  const [date, setDate] = useState(today());
  const [onBudget, setOnBudget] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);

    let openingMinor = 0;
    if (balance.trim() !== '') {
      const parsed = parseToMinorUnits(balance);
      if (!parsed.ok) {
        setError(parsed.error);
        return;
      }
      if (parsed.value < 0) {
        setError('Informe o valor sem sinal: o tipo da conta já diz o sentido.');
        return;
      }
      // Divida entra negativa no razao. Pedir o valor "sem sinal" e inverter
      // aqui evita a pergunta confusa "a sua divida e positiva ou negativa?".
      openingMinor = type === 'LIABILITY' ? -parsed.value : parsed.value;
    }

    setSaving(true);
    try {
      await api.createAccount({
        name,
        type,
        currencyCode: 'BRL',
        isOnBudget: onBudget,
        openingBalanceMinorUnits: openingMinor,
        openingBalanceDate: date,
      });
      await onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Não foi possível criar a conta.');
    } finally {
      setSaving(false);
    }
  }

  return (
    <form className="form" onSubmit={submit}>
      <div className="segmented" role="radiogroup" aria-label="Tipo de conta">
        <button
          type="button"
          role="radio"
          aria-checked={type === 'ASSET'}
          className={type === 'ASSET' ? 'active kind-income' : ''}
          onClick={() => setType('ASSET')}
        >
          Ativo
        </button>
        <button
          type="button"
          role="radio"
          aria-checked={type === 'LIABILITY'}
          className={type === 'LIABILITY' ? 'active kind-expense' : ''}
          onClick={() => setType('LIABILITY')}
        >
          Passivo
        </button>
      </div>

      <div className="fields">
        <label className="field field-wide">
          <span>Nome</span>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder={type === 'ASSET' ? 'Ex.: Itaú conta corrente' : 'Ex.: Cartão Nubank'}
            required
          />
        </label>
        <label className="field">
          <span>{type === 'ASSET' ? 'Saldo atual' : 'Quanto você deve hoje'}</span>
          <span className="field-money">
            <span className="prefix">R$</span>
            <input value={balance} onChange={(e) => setBalance(e.target.value)} placeholder="0,00" inputMode="decimal" />
          </span>
        </label>
        <label className="field">
          <span>Saldo em</span>
          <input type="date" value={date} onChange={(e) => setDate(e.target.value)} />
        </label>
      </div>

      <label className="check">
        <input type="checkbox" checked={onBudget} onChange={(e) => setOnBudget(e.target.checked)} />
        <span>
          <strong>Entra no orçamento</strong>
          <span className="note">
            Desmarque para investimento ou reserva que não é dinheiro do dia a dia: o saldo dela não vira dinheiro a
            separar, e gasto pago direto por ela não sai de categoria.
          </span>
        </span>
      </label>

      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      <div className="form-footer">
        <p className="note">
          {type === 'ASSET'
            ? 'O saldo inicial vira um lançamento de abertura: dinheiro não aparece do nada nem no primeiro dia.'
            : 'A dívida entra com saldo negativo. É o que você deve, e reduz o patrimônio.'}
        </p>
        <button type="submit" disabled={saving}>
          {saving ? 'Criando…' : 'Criar conta'}
        </button>
      </div>
    </form>
  );
}
