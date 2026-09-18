import { useEffect, useState } from 'react';
import { ColumnChart } from '../charts/ColumnChart';
import { Icon } from '../components/Icon';
import { EmptyState, PageHeader, Panel } from '../components/Panel';
import { api, ApiError, type CategoryTrend, type MonthlyFlow } from '../lib/api';
import { flowFromReport } from '../lib/analysis';
import { currentMonth, formatAmount, monthShortLabel } from '../lib/format';
import type { PageProps } from '../lib/types';

/**
 * Relatorios.
 *
 * Tudo aqui vem somado do servidor. A pagina nao recebe lancamento nenhum -
 * e por isso que os numeros valem para o razao inteiro e nao para a pagina
 * de 200 linhas que as outras telas carregam.
 *
 * A pergunta que a tabela responde nao e "quanto gastei", que o painel ja
 * responde, e sim "quanto gasto NISSO, mes a mes" - que e o que se precisa
 * saber antes de decidir onde cortar.
 */
const WINDOWS = [3, 6, 12] as const;

export function Reports({ navigate }: PageProps) {
  const [months, setMonths] = useState<number>(6);
  const [flow, setFlow] = useState<MonthlyFlow | null>(null);
  const [trend, setTrend] = useState<CategoryTrend | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    let cancelled = false;
    const until = currentMonth();

    setLoading(true);
    setError(null);

    Promise.all([api.monthlyFlow(until, months), api.categoryTrend(until, months)])
      .then(([nextFlow, nextTrend]) => {
        if (cancelled) return;
        setFlow(nextFlow);
        setTrend(nextTrend);
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        setError(e instanceof ApiError ? e.message : 'Falha ao carregar os relatórios.');
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [months]);

  const spent = flow?.months.reduce((sum, m) => sum + m.expenseMinorUnits, 0) ?? 0;
  const earned = flow?.months.reduce((sum, m) => sum + m.incomeMinorUnits, 0) ?? 0;

  return (
    <>
      <PageHeader
        title="Relatórios"
        subtitle={`Últimos ${months} meses, somados sobre todo o sistema`}
        actions={
          <div className="window-picker" role="group" aria-label="Tamanho da janela">
            {WINDOWS.map((option) => (
              <button
                key={option}
                type="button"
                className={`chip-button${option === months ? ' active' : ''}`}
                aria-pressed={option === months}
                onClick={() => setMonths(option)}
              >
                {option} meses
              </button>
            ))}
          </div>
        }
      />

      {error && (
        <div className="error-banner" role="alert">
          <span>{error}</span>
        </div>
      )}

      <Panel title="Entradas e saídas">
        {loading || !flow ? (
          <p className="empty">Somando…</p>
        ) : (
          <>
            <ColumnChart months={flowFromReport(flow)} />
            <p className="chart-note">
              {formatAmount(earned, true)} entrou e {formatAmount(spent, true)} saiu no período.
            </p>
          </>
        )}
      </Panel>

      <Panel title="Gasto por categoria, mês a mês">
        {loading || !trend ? (
          <p className="empty">Somando…</p>
        ) : trend.categories.length === 0 ? (
          <EmptyState
            title="Nada para comparar ainda"
            text="Assim que houver gasto lançado, esta tabela mostra quanto cada categoria consome por mês."
            action={
              <button type="button" onClick={() => navigate('transactions')}>
                Ver lançamentos
              </button>
            }
          />
        ) : (
          <TrendTable trend={trend} months={months} onCategorize={() => navigate('transactions')} />
        )}
      </Panel>
    </>
  );
}

/**
 * A media divide pelos meses da JANELA, nao pelos meses em que houve gasto.
 * Uma conta que veio em dois de seis meses pesa dois sextos no mes tipico -
 * dividir por dois faria uma despesa ocasional parecer mensal.
 */
function TrendTable({
  trend,
  months,
  onCategorize,
}: {
  trend: CategoryTrend;
  months: number;
  onCategorize: () => void;
}) {
  const totals = trend.months.map((_, column) =>
    trend.categories.reduce((sum, row) => sum + row.amountsMinorUnits[column]!, 0),
  );
  const grandTotal = totals.reduce((sum, value) => sum + value, 0);

  return (
    <div className="table-scroll">
      <table className="trend-table">
        <thead>
          <tr>
            <th scope="col">Categoria</th>
            {trend.months.map((month) => (
              <th key={month} scope="col" className="amount">
                {monthShortLabel(month)}
              </th>
            ))}
            <th scope="col" className="amount">
              Total
            </th>
            <th scope="col" className="amount">
              Média
            </th>
          </tr>
        </thead>

        <tbody>
          {trend.categories.map((row) => {
            const loose = row.categoryId === null;

            return (
              <tr key={row.categoryId ?? 'sem-categoria'} className={loose ? 'loose' : undefined}>
                <th scope="row">
                  {loose ? (
                    <button type="button" className="link-button" onClick={onCategorize}>
                      <Icon name="alert" size={14} />
                      <span>{row.name}</span>
                    </button>
                  ) : (
                    row.name
                  )}
                </th>

                {row.amountsMinorUnits.map((amount, column) => (
                  <td key={trend.months[column]} className={`amount${amount === 0 ? ' zero' : ''}`}>
                    {amount === 0 ? '—' : formatAmount(amount)}
                  </td>
                ))}

                <td className="amount total">{formatAmount(row.totalMinorUnits)}</td>
                <td className="amount subtext">{formatAmount(row.averageMinorUnits)}</td>
              </tr>
            );
          })}
        </tbody>

        <tfoot>
          <tr>
            <th scope="row">Total</th>
            {totals.map((amount, column) => (
              <td key={trend.months[column]} className="amount">
                {formatAmount(amount)}
              </td>
            ))}
            <td className="amount total">{formatAmount(grandTotal)}</td>
            <td className="amount subtext">{formatAmount(Math.trunc(grandTotal / months))}</td>
          </tr>
        </tfoot>
      </table>
    </div>
  );
}
