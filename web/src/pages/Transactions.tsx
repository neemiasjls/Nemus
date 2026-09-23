import { useMemo, useState, type FormEvent } from 'react';
import { Icon } from '../components/Icon';
import { EmptyState, NewButton, PageHeader, Panel } from '../components/Panel';
import { TransactionTable } from '../components/TransactionTable';
import { api } from '../lib/api';
import { needsCategory, primaryCategory } from '../lib/analysis';
import { today } from '../lib/format';
import { parseToMinorUnits } from '../lib/money';
import type { AppData, PageProps } from '../lib/types';

export function Transactions({ data, reload, navigate }: PageProps) {
  // O botao fixo da barra lateral manda para ca com "?novo": o formulario
  // ja abre. Lancar um gasto e a acao que mais se repete no app, e ela nao
  // pode custar tres cliques - abrir a aba, achar o botao, clicar.
  const [formOpen, setFormOpen] = useState(() => window.location.hash.includes('?novo'));
  const [accountFilter, setAccountFilter] = useState('');
  const [search, setSearch] = useState('');
  // O orcamento manda para ca com "#/lancamentos?sem-categoria" quando ha
  // dinheiro fora de categoria: a tela ja abre filtrada no que falta fazer.
  const [pendingOnly, setPendingOnly] = useState(() =>
    window.location.hash.includes('?sem-categoria'),
  );

  const ownAccounts = data.accounts.filter((a) => a.isInternal && !a.isSystem);
  const pending = useMemo(() => data.transactions.filter(needsCategory), [data.transactions]);

  const filtered = useMemo(() => {
    const term = search.trim().toLocaleLowerCase('pt-BR');

    return data.transactions.filter((t) => {
      if (pendingOnly && !needsCategory(t)) return false;
      if (accountFilter !== '' && !t.entries.some((e) => e.accountId === accountFilter)) return false;
      if (term === '') return true;

      return (
        t.description.toLocaleLowerCase('pt-BR').includes(term) ||
        (primaryCategory(t) ?? '').toLocaleLowerCase('pt-BR').includes(term)
      );
    });
  }, [data.transactions, accountFilter, search, pendingOnly]);

  const total = data.transactionTotal;

  return (
    <>
      <PageHeader
        title="Lançamentos"
        subtitle={total === 1 ? '1 lançamento no sistema' : `${total} lançamentos no sistema`}
        actions={
          ownAccounts.length > 0 && (
            <NewButton open={formOpen} label="Novo lançamento" onToggle={() => setFormOpen((v) => !v)} />
          )
        }
      />

      {formOpen && (
        <Panel title="Novo lançamento" className="panel-form">
          <TransactionForm
            data={data}
            onDone={async () => {
              setFormOpen(false);
              await reload();
            }}
          />
        </Panel>
      )}

      {ownAccounts.length === 0 ? (
        <Panel>
          <EmptyState
            title="Nenhuma conta ainda."
            text="Todo lançamento sai de uma conta ou entra nela. Comece criando uma."
            action={<button onClick={() => navigate('accounts')}>Criar conta</button>}
          />
        </Panel>
      ) : (
        <Panel>
          <div className="filters">
            <label className="search">
              <Icon name="search" size={15} />
              <span className="sr-only">Buscar</span>
              <input
                type="search"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="Buscar por descrição ou categoria"
              />
            </label>
            <label className="account-filter">
              <span className="sr-only">Conta</span>
              <select value={accountFilter} onChange={(e) => setAccountFilter(e.target.value)}>
                <option value="">Todas as contas</option>
                {ownAccounts.map((a) => (
                  <option key={a.id} value={a.id}>
                    {a.name}
                  </option>
                ))}
              </select>
            </label>
            {pending.length > 0 && (
              <button
                type="button"
                className={pendingOnly ? 'toggle active' : 'toggle'}
                aria-pressed={pendingOnly}
                onClick={() => setPendingOnly((v) => !v)}
                title="Gasto sem categoria fica fora das categorias e some do ainda sem destino"
              >
                <Icon name="alert" size={14} />
                <span>
                  {pending.length === 1 ? '1 sem categoria' : `${pending.length} sem categoria`}
                </span>
              </button>
            )}
          </div>

          {filtered.length === 0 ? (
            <EmptyState
              title={
                data.transactions.length === 0
                  ? 'Nenhum lançamento ainda.'
                  : pendingOnly
                    ? 'Nada sem categoria.'
                    : 'Nada encontrado.'
              }
              text={
                data.transactions.length === 0
                  ? 'Use o botão acima ou importe um extrato do banco.'
                  : pendingOnly
                    ? 'Todo gasto já tem categoria.'
                    : 'Tente outro termo ou outra conta.'
              }
            />
          ) : (
            <TransactionTable
              transactions={filtered}
              detailed
              categories={data.categories}
              onChanged={() => void reload()}
            />
          )}
        </Panel>
      )}
    </>
  );
}

type Kind = 'expense' | 'income' | 'transfer';

const KINDS: { value: Kind; label: string; ajuda: string }[] = [
  { value: 'expense', label: 'Gasto', ajuda: 'Dinheiro que saiu: mercado, gasolina, uma assinatura.' },
  {
    value: 'income',
    label: 'Entrada',
    ajuda: 'Dinheiro que veio de fora e te deixou com mais: salário, uma venda, um reembolso.',
  },
  {
    value: 'transfer',
    label: 'Transferência',
    ajuda: 'Dinheiro seu mudando de lugar — da conta para a poupança, ou pagar a fatura do cartão. Você não fica mais rico nem mais pobre.',
  },
];

function TransactionForm({ data, onDone }: { data: AppData; onDone: () => Promise<void> }) {
  const ownAccounts = data.accounts.filter((a) => a.isInternal && !a.isSystem);
  // Derivada dos dados, nao escrita a mao: o UUID da conta de sistema vive
  // no banco e no dominio, e o frontend nao precisa de uma terceira copia.
  const externalRevenue = data.accounts.find((a) => a.isSystem && a.type === 'REVENUE');

  const [kind, setKind] = useState<Kind>('expense');
  const [date, setDate] = useState(today());
  const [description, setDescription] = useState('');
  const [source, setSource] = useState(ownAccounts[0]?.id ?? '');
  const [destination, setDestination] = useState(ownAccounts[1]?.id ?? ownAccounts[0]?.id ?? '');
  const [categoryId, setCategoryId] = useState('');
  const [amount, setAmount] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const categoriesOfKind = data.categories.filter(
    (c) => c.kind === (kind === 'income' ? 'INCOME' : 'EXPENSE'),
  );

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);

    const parsed = parseToMinorUnits(amount);
    if (!parsed.ok) {
      setError(parsed.error);
      return;
    }
    if (parsed.value <= 0) {
      setError('Informe um valor maior que zero.');
      return;
    }
    if (kind === 'transfer' && source === destination) {
      setError('Origem e destino precisam ser contas diferentes.');
      return;
    }
    if (kind === 'income' && !externalRevenue) {
      setError('A conta de receitas externas não foi encontrada.');
      return;
    }

    const category = categoryId === '' ? null : categoryId;
    const common = { occurredOn: date, description, currencyCode: 'BRL' };

    setSaving(true);
    try {
      if (kind === 'expense') {
        await api.createExpense({
          occurredOn: date,
          description,
          accountId: source,
          amountMinorUnits: parsed.value,
          categoryId: category,
        });
      } else if (kind === 'income' && externalRevenue) {
        await api.createTransaction({
          ...common,
          kind: 'STANDARD',
          entries: [
            { accountId: source, amountMinorUnits: parsed.value },
            { accountId: externalRevenue.id, amountMinorUnits: -parsed.value, categoryId: category },
          ],
        });
      } else {
        await api.createTransaction({
          ...common,
          kind: 'TRANSFER',
          entries: [
            { accountId: source, amountMinorUnits: -parsed.value },
            { accountId: destination, amountMinorUnits: parsed.value },
          ],
        });
      }
      await onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Não foi possível salvar.');
    } finally {
      setSaving(false);
    }
  }

  const sourceLabel = kind === 'transfer' ? 'De' : kind === 'income' ? 'Entrou em' : 'Pago com';

  const placeholder =
    kind === 'income' ? 'Ex.: Salário' : kind === 'transfer' ? 'Ex.: Pagamento da fatura' : 'Ex.: Mercado';

  return (
    <form className="form" onSubmit={submit}>
      <div className="segmented" role="radiogroup" aria-label="Tipo de lançamento">
        {KINDS.map((k) => (
          <button
            key={k.value}
            type="button"
            role="radio"
            aria-checked={kind === k.value}
            className={kind === k.value ? `active kind-${k.value}` : ''}
            onClick={() => {
              setKind(k.value);
              setCategoryId('');
            }}
          >
            {k.label}
          </button>
        ))}
      </div>

      {/* A ajuda fica DEBAIXO da escolha, nao no rodape do formulario:
          a duvida e antes de escolher, nao depois de preencher. */}
      <p className="kind-help">{KINDS.find((k) => k.value === kind)?.ajuda}</p>

      <div className="fields">
        <label className="field field-wide">
          <span>Descrição</span>
          <input value={description} onChange={(e) => setDescription(e.target.value)} placeholder={placeholder} required />
        </label>

        <label className="field">
          <span>Valor</span>
          <span className="field-money">
            <span className="prefix">R$</span>
            <input
              value={amount}
              onChange={(e) => setAmount(e.target.value)}
              placeholder="0,00"
              inputMode="decimal"
              required
            />
          </span>
        </label>

        <label className="field">
          <span>Data</span>
          <input type="date" value={date} onChange={(e) => setDate(e.target.value)} required />
        </label>

        <label className="field">
          <span>{sourceLabel}</span>
          <select value={source} onChange={(e) => setSource(e.target.value)}>
            {ownAccounts.map((a) => (
              <option key={a.id} value={a.id}>
                {a.name}
              </option>
            ))}
          </select>
        </label>

        {kind === 'transfer' ? (
          <label className="field">
            <span>Para</span>
            <select value={destination} onChange={(e) => setDestination(e.target.value)}>
              {ownAccounts.map((a) => (
                <option key={a.id} value={a.id}>
                  {a.name}
                </option>
              ))}
            </select>
          </label>
        ) : (
          <label className="field">
            <span>Categoria</span>
            <select value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
              <option value="">Sem categoria</option>
              {categoriesOfKind.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.parentId ? `— ${c.name}` : c.name}
                </option>
              ))}
            </select>
          </label>
        )}
      </div>

      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      <div className="form-footer">
        <p className="note">
          {kind === 'transfer'
            ? 'Sai de uma conta sua e entra na outra. Nenhum gasto é criado.'
            : kind === 'income'
              ? 'O dinheiro entra na conta escolhida e a categoria diz de onde veio.'
              : 'Sai da conta escolhida e entra na categoria. O outro lado é feito sozinho.'}
        </p>
        <button type="submit" disabled={saving}>
          {saving ? 'Salvando…' : 'Salvar lançamento'}
        </button>
      </div>
    </form>
  );
}
