import { useMemo, useState, type FormEvent } from 'react';
import { EmptyState, NewButton, PageHeader, Panel } from '../components/Panel';
import { api, type Category } from '../lib/api';
import { activityByCategory } from '../lib/analysis';
import { capitalize, currentMonth, formatAmount, monthLongLabel } from '../lib/format';
import type { PageProps } from '../lib/types';

export function Categories({ data, reload }: PageProps) {
  const [formOpen, setFormOpen] = useState(false);
  const month = currentMonth();

  const activity = useMemo(() => activityByCategory(data.transactions, month), [data.transactions, month]);

  const expenseCategories = data.categories.filter((c) => c.kind === 'EXPENSE');
  const incomeCategories = data.categories.filter((c) => c.kind === 'INCOME');
  const monthName = capitalize(monthLongLabel(month));

  return (
    <>
      <PageHeader
        title="Categorias"
        subtitle={`Movimento de ${monthLongLabel(month)}`}
        actions={<NewButton open={formOpen} label="Nova categoria" onToggle={() => setFormOpen((v) => !v)} />}
      />

      {formOpen && (
        <Panel title="Nova categoria" className="panel-form">
          <CategoryForm
            categories={data.categories}
            onDone={async () => {
              setFormOpen(false);
              await reload();
            }}
          />
        </Panel>
      )}

      {data.categories.length === 0 ? (
        <Panel>
          <EmptyState
            title="Nenhuma categoria ainda."
            text="Categoria diz para onde o dinheiro foi. Comece pelas grandes: moradia, alimentação, transporte."
          />
        </Panel>
      ) : (
        <div className="grid-2">
          <Panel title="Despesas" actions={<span className="hint">{monthName}</span>}>
            <CategoryTree categories={expenseCategories} activity={activity} tone="debit" />
          </Panel>
          <Panel title="Receitas" actions={<span className="hint">{monthName}</span>}>
            <CategoryTree categories={incomeCategories} activity={activity} tone="credit" />
          </Panel>
        </div>
      )}
    </>
  );
}

/**
 * Arvore de dois niveis. O valor do grupo inclui o das subcategorias - senao
 * "Moradia" apareceria zerada mesmo com aluguel e condominio pagos.
 */
function CategoryTree({
  categories,
  activity,
  tone,
}: {
  categories: Category[];
  activity: Map<string, number>;
  tone: 'credit' | 'debit';
}) {
  if (categories.length === 0) {
    return <p className="empty">Nenhuma ainda.</p>;
  }

  const groups = categories.filter((c) => c.parentId === null);
  const childrenOf = (id: string) => categories.filter((c) => c.parentId === id);
  const groupTotal = (group: Category) =>
    (activity.get(group.id) ?? 0) +
    childrenOf(group.id).reduce((sum, child) => sum + (activity.get(child.id) ?? 0), 0);

  const largest = Math.max(1, ...groups.map(groupTotal));

  return (
    <ul className={`tree tree-${tone}`}>
      {groups.map((group) => {
        const total = groupTotal(group);
        const children = childrenOf(group.id);

        return (
          <li key={group.id} className="tree-group">
            <div className="tree-row">
              <span className="tree-name">{group.name}</span>
              <span className="tree-amount">{total === 0 ? '—' : formatAmount(total)}</span>
            </div>
            <svg className="tree-bar" viewBox="0 0 100 4" preserveAspectRatio="none" aria-hidden="true">
              <rect className="bar-bg" width="100" height="4" rx="1" />
              <rect className="bar-fill" width={Math.max(0, (total / largest) * 100)} height="4" rx="1" />
            </svg>
            {children.length > 0 && (
              <ul>
                {children.map((child) => {
                  const amount = activity.get(child.id) ?? 0;
                  return (
                    <li key={child.id} className="tree-row child">
                      <span className="tree-name">{child.name}</span>
                      <span className="tree-amount">{amount === 0 ? '—' : formatAmount(amount)}</span>
                    </li>
                  );
                })}
              </ul>
            )}
          </li>
        );
      })}
    </ul>
  );
}

function CategoryForm({ categories, onDone }: { categories: Category[]; onDone: () => Promise<void> }) {
  const [name, setName] = useState('');
  const [parentId, setParentId] = useState('');
  const [kind, setKind] = useState<'EXPENSE' | 'INCOME'>('EXPENSE');
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const groups = categories.filter((c) => c.parentId === null);
  const parentKind = groups.find((g) => g.id === parentId)?.kind;

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setSaving(true);

    try {
      await api.createCategory({
        name,
        kind: parentId === '' ? kind : undefined,
        parentId: parentId === '' ? null : parentId,
        sortOrder: 0,
      });
      await onDone();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Não foi possível criar a categoria.');
    } finally {
      setSaving(false);
    }
  }

  return (
    <form className="form" onSubmit={submit}>
      <div className="fields">
        <label className="field field-wide">
          <span>Nome</span>
          <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Ex.: Mercado" required />
        </label>
        <label className="field">
          <span>Posição</span>
          <select value={parentId} onChange={(e) => setParentId(e.target.value)}>
            <option value="">Categoria principal</option>
            {groups.map((g) => (
              <option key={g.id} value={g.id}>
                Dentro de {g.name}
              </option>
            ))}
          </select>
        </label>
      </div>

      {parentId === '' ? (
        <div className="segmented" role="radiogroup" aria-label="Tipo da categoria">
          <button
            type="button"
            role="radio"
            aria-checked={kind === 'EXPENSE'}
            className={kind === 'EXPENSE' ? 'active kind-expense' : ''}
            onClick={() => setKind('EXPENSE')}
          >
            Despesa
          </button>
          <button
            type="button"
            role="radio"
            aria-checked={kind === 'INCOME'}
            className={kind === 'INCOME' ? 'active kind-income' : ''}
            onClick={() => setKind('INCOME')}
          >
            Receita
          </button>
        </div>
      ) : (
        <p className="note">
          Subcategoria herda o tipo do grupo: {parentKind === 'INCOME' ? 'receita' : 'despesa'}.
        </p>
      )}

      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      <div className="form-footer">
        <p className="note">Até dois níveis: grupo e subcategoria. Mais fundo que isso vira burocracia.</p>
        <button type="submit" disabled={saving}>
          {saving ? 'Criando…' : 'Criar categoria'}
        </button>
      </div>
    </form>
  );
}
