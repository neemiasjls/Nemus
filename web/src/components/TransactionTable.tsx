import { Fragment, useState } from 'react';
import { api, ApiError, type Category, type Transaction } from '../lib/api';
import { magnitude, netWorthEffect, primaryCategory, soleExternalLeg } from '../lib/analysis';
import { formatAmount, formatDay, formatSigned } from '../lib/format';
import { Icon } from './Icon';

/**
 * Lista de lancamentos em dois modos.
 *
 * Compacta, no painel: data, descricao e o efeito no patrimonio.
 *
 * Detalhada, na pagina de lancamentos: cada linha abre para mostrar as
 * partidas na forma do razao em T. As partidas ficam recolhidas por padrao -
 * numa versao anterior todas apareciam abertas o tempo todo, e a lista virava
 * um paredao.
 */
export function TransactionTable({
  transactions,
  detailed = false,
  categories,
  onChanged,
}: {
  transactions: Transaction[];
  detailed?: boolean;
  /** Com a lista, a categoria vira campo na propria linha. Sem ela, so texto. */
  categories?: Category[];
  onChanged?: () => void;
}) {
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [deletingId, setDeletingId] = useState<string | null>(null);

  async function remove(id: string) {
    if (!window.confirm('Excluir este lançamento? Ele deixa de contar em todos os saldos.')) {
      return;
    }

    setDeletingId(id);
    try {
      await api.deleteTransaction(id);
      setExpandedId(null);
      onChanged?.();
    } finally {
      setDeletingId(null);
    }
  }

  const toggle = (id: string) => setExpandedId((current) => (current === id ? null : id));

  return (
    <table className={`transactions-table${detailed ? ' detailed' : ''}`}>
      <thead>
        <tr>
          <th className="col-date">Data</th>
          <th>Descrição</th>
          <th className="amount">Valor</th>
          {detailed && (
            <th className="col-actions">
              <span className="sr-only">Detalhes</span>
            </th>
          )}
        </tr>
      </thead>
      <tbody>
        {transactions.map((t) => {
          const effect = netWorthEffect(t);
          const category = primaryCategory(t);
          const isTransfer = effect === 0;
          const isExpanded = expandedId === t.id;

          return (
            <Fragment key={t.id}>
              <tr
                className={`row${isExpanded ? ' open' : ''}${detailed ? ' clickable' : ''}`}
                onClick={detailed ? () => toggle(t.id) : undefined}
              >
                <td className="col-date date">{formatDay(t.occurredOn)}</td>
                <td>
                  <div className="description">
                    <span className="description-text">{t.description}</span>
                    {t.source === 'OFX' && <span className="tag">OFX</span>}
                  </div>
                  {/*
                    Categoria como segunda linha da descricao, e nao como coluna
                    propria. A coluna era escondida em tela estreita, e a linha
                    de detalhe com colSpan fixo passava a criar uma coluna
                    fantasma: as outras linhas encolhiam e sobrava uma faixa
                    vazia a direita. Com 4 colunas sempre, nada precisa sumir.
                  */}
                  <div className="subtext">
                    {detailed && categories && soleExternalLeg(t) ? (
                      <CategoryPicker
                        transaction={t}
                        categories={categories}
                        onChanged={onChanged}
                      />
                    ) : detailed ? (
                      category ? (
                        <span className="chip">{category}</span>
                      ) : isTransfer ? (
                        <span className="chip neutral">Transferência</span>
                      ) : (
                        <span className="uncategorized">Sem categoria</span>
                      )
                    ) : (
                      category ?? (isTransfer ? 'Transferência' : 'Sem categoria')
                    )}
                  </div>
                </td>
                <td className={`amount ${isTransfer ? 'neutral' : effect < 0 ? 'debit' : 'credit'}`}>
                  {isTransfer ? formatAmount(magnitude(t)) : formatSigned(effect)}
                </td>
                {detailed && (
                  <td className="col-actions">
                    <button
                      type="button"
                      className="icon-button row-chevron"
                      aria-expanded={isExpanded}
                      aria-label={isExpanded ? 'Recolher partidas' : 'Ver partidas'}
                      onClick={(event) => {
                        event.stopPropagation();
                        toggle(t.id);
                      }}
                    >
                      <Icon name="chevron" size={16} />
                    </button>
                  </td>
                )}
              </tr>

              {detailed && isExpanded && (
                <tr className="detail">
                  <td colSpan={4}>
                    <TAccount transaction={t} />
                    <div className="detail-footer">
                      <span>
                        {t.entries.length} partidas · soma{' '}
                        <strong>{formatAmount(t.entries.reduce((sum, e) => sum + e.amountMinorUnits, 0))}</strong>
                      </span>
                      <button
                        type="button"
                        className="danger"
                        disabled={deletingId === t.id}
                        onClick={() => void remove(t.id)}
                      >
                        {deletingId === t.id ? 'Excluindo…' : 'Excluir lançamento'}
                      </button>
                    </div>
                  </td>
                </tr>
              )}
            </Fragment>
          );
        })}
      </tbody>
    </table>
  );
}

/**
 * A categoria, editavel na propria linha.
 *
 * E o caminho que faltava para o extrato importado: o OFX nao traz categoria,
 * e ate aqui a unica saida era apagar o lancamento e digitar de novo. A
 * mudanca vai na perna da conta externa, que e onde o orcamento procura.
 */
function CategoryPicker({
  transaction,
  categories,
  onChanged,
}: {
  transaction: Transaction;
  categories: Category[];
  onChanged?: () => void;
}) {
  const [editing, setEditing] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const leg = soleExternalLeg(transaction);
  if (!leg) {
    return null;
  }

  const nameOf = (c: Category) => {
    const parent = c.parentId ? categories.find((p) => p.id === c.parentId) : undefined;
    return parent ? `${parent.name} · ${c.name}` : c.name;
  };

  const options = (kind: string) =>
    categories
      .filter((c) => c.kind === kind)
      .map((c) => ({ id: c.id, label: nameOf(c) }))
      .sort((a, b) => a.label.localeCompare(b.label, 'pt-BR'));

  async function save(value: string) {
    setSaving(true);
    setError(null);

    try {
      await api.categorize(transaction.id, value === '' ? null : value);
      setEditing(false);
      onChanged?.();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Não foi possível categorizar.');
    } finally {
      setSaving(false);
    }
  }

  if (!editing) {
    return (
      <button
        type="button"
        className={`chip chip-button${leg.categoryId ? '' : ' chip-empty'}`}
        onClick={(event) => {
          event.stopPropagation();
          setEditing(true);
        }}
        title="Mudar a categoria"
      >
        {leg.categoryName ?? 'Sem categoria'}
        <Icon name="chevron" size={11} />
      </button>
    );
  }

  return (
    <span className="category-edit" onClick={(event) => event.stopPropagation()}>
      <select
        value={leg.categoryId ?? ''}
        disabled={saving}
        onChange={(event) => void save(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === 'Escape') setEditing(false);
        }}
        onBlur={() => {
          if (!saving) setEditing(false);
        }}
        aria-label={`Categoria de ${transaction.description}`}
        autoFocus
      >
        <option value="">Sem categoria</option>
        <optgroup label="Despesas">
          {options('EXPENSE').map((o) => (
            <option key={o.id} value={o.id}>
              {o.label}
            </option>
          ))}
        </optgroup>
        <optgroup label="Receitas">
          {options('INCOME').map((o) => (
            <option key={o.id} value={o.id}>
              {o.label}
            </option>
          ))}
        </optgroup>
      </select>
      {error && <span className="picker-error">{error}</span>}
    </span>
  );
}

/**
 * As partidas na forma do razao em T: saiu a esquerda, entrou a direita.
 * Cada lado mostra magnitude positiva, como num livro de verdade; o sentido
 * esta no titulo da coluna. Os dois lados sempre fecham no mesmo total - e
 * ver isso e o ponto inteiro de partidas dobradas.
 */
function TAccount({ transaction }: { transaction: Transaction }) {
  const outgoing = transaction.entries.filter((e) => e.amountMinorUnits < 0);
  const incoming = transaction.entries.filter((e) => e.amountMinorUnits > 0);

  return (
    <div className="t-account">
      <div className="t-account-side">
        <div className="t-account-title">Saiu de</div>
        {outgoing.map((e) => (
          <div key={e.id} className="leg">
            <span className="leg-account">{e.accountName}</span>
            <span className="leg-amount">{formatAmount(-e.amountMinorUnits)}</span>
          </div>
        ))}
      </div>
      <div className="t-account-side">
        <div className="t-account-title">Entrou em</div>
        {incoming.map((e) => (
          <div key={e.id} className="leg">
            <span className="leg-account">
              {e.accountName}
              {e.categoryName && <em> · {e.categoryName}</em>}
            </span>
            <span className="leg-amount">{formatAmount(e.amountMinorUnits)}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
