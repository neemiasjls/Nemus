import type { MonthFlow } from '../lib/analysis';
import { formatAmount, formatCompact, monthShortLabel } from '../lib/format';

/**
 * Topo "redondo" para o eixo: 15.234 vira 20.000, 3.100 vira 5.000. Eixo que
 * termina em numero quebrado e a primeira coisa que faz grafico parecer
 * amador.
 */
function axisTop(largest: number): number {
  if (largest <= 0) return 10_000;

  const order = 10 ** Math.floor(Math.log10(largest));
  const normalized = largest / order;
  const step = [1, 2, 2.5, 5, 10].find((n) => n >= normalized) ?? 10;

  return Math.ceil(step * order);
}

const WIDTH = 640;
const HEIGHT = 250;
const LEFT = 56;
const RIGHT = 10;
const TOP = 14;
const BOTTOM = 30;

export function ColumnChart({ months }: { months: MonthFlow[] }) {
  const largest = Math.max(0, ...months.flatMap((m) => [m.income, m.expense]));

  if (largest === 0) {
    return <p className="empty">Sem entradas nem saídas no período.</p>;
  }

  const top = axisTop(largest);
  const plotWidth = WIDTH - LEFT - RIGHT;
  const plotHeight = HEIGHT - TOP - BOTTOM;
  const slot = plotWidth / months.length;
  const barWidth = Math.min(24, slot * 0.27);
  const baseline = TOP + plotHeight;

  const y = (value: number) => baseline - (Math.max(0, value) / top) * plotHeight;

  return (
    <figure className="chart">
      <svg viewBox={`0 0 ${WIDTH} ${HEIGHT}`} role="img" aria-label="Entradas e saídas por mês">
        {[0, 0.25, 0.5, 0.75, 1].map((fraction) => {
          const value = Math.round(top * fraction);
          const lineY = y(value);
          return (
            <g key={fraction}>
              <line
                className={fraction === 0 ? 'axis-base' : 'gridline'}
                x1={LEFT}
                x2={WIDTH - RIGHT}
                y1={lineY}
                y2={lineY}
              />
              <text className="axis" x={LEFT - 10} y={lineY + 4} textAnchor="end">
                {formatCompact(value)}
              </text>
            </g>
          );
        })}

        {months.map((month, i) => {
          const center = LEFT + slot * i + slot / 2;
          const net = month.income - month.expense;

          return (
            <g key={month.key}>
              <rect
                className="column-income"
                x={center - barWidth - 2}
                y={y(month.income)}
                width={barWidth}
                height={baseline - y(month.income)}
                rx="2"
              >
                <title>{`Entradas: R$ ${formatAmount(month.income)}`}</title>
              </rect>
              <rect
                className="column-expense"
                x={center + 2}
                y={y(month.expense)}
                width={barWidth}
                height={baseline - y(month.expense)}
                rx="2"
              >
                <title>{`Saídas: R$ ${formatAmount(month.expense)}`}</title>
              </rect>
              <text className="axis axis-month" x={center} y={HEIGHT - 11} textAnchor="middle">
                {monthShortLabel(month.key)}
              </text>
              <title>{`Resultado: R$ ${formatAmount(net)}`}</title>
            </g>
          );
        })}
      </svg>

      <figcaption className="chart-legend">
        <span className="legend-income">Entradas</span>
        <span className="legend-expense">Saídas</span>
      </figcaption>
    </figure>
  );
}
