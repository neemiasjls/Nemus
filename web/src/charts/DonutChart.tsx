import type { NamedAmount } from '../lib/analysis';
import { formatAmount, formatCompact } from '../lib/format';

const RADIUS = 44;
const CIRCUMFERENCE = 2 * Math.PI * RADIUS;

/**
 * Composicao em rosca. Cada fatia e um circulo com stroke-dasharray - um
 * atributo SVG, nao estilo inline, entao passa pela CSP.
 *
 * Mais de cinco fatias viram ruido visual; o que passa disso e agrupado em
 * "Outras".
 */
export function DonutChart({ items, centerLabel }: { items: NamedAmount[]; centerLabel: string }) {
  if (items.length === 0) {
    return <p className="empty">Nenhuma conta com saldo positivo.</p>;
  }

  const largest = items.slice(0, 5);
  const rest = items.slice(5).reduce((sum, item) => sum + item.amount, 0);
  const slices = rest > 0 ? [...largest, { name: 'Outras', amount: rest }] : largest;
  const total = slices.reduce((sum, slice) => sum + slice.amount, 0);

  let travelled = 0;

  return (
    <div className="donut">
      <div className="donut-figure">
        <svg viewBox="0 0 120 120" className="donut-svg" role="img" aria-label="Composição dos ativos">
          <circle className="donut-track" cx="60" cy="60" r={RADIUS} />
          {slices.map((slice, i) => {
            const length = total > 0 ? (slice.amount / total) * CIRCUMFERENCE : 0;
            // Um fio de folga entre fatias, para elas nao se fundirem.
            const visibleLength = Math.max(0, length - 1.2);
            const circle = (
              <circle
                key={slice.name}
                className={`donut-slice slice-${i}`}
                cx="60"
                cy="60"
                r={RADIUS}
                strokeDasharray={`${visibleLength} ${CIRCUMFERENCE - visibleLength}`}
                strokeDashoffset={-travelled}
                transform="rotate(-90 60 60)"
              >
                <title>{`${slice.name}: R$ ${formatAmount(slice.amount)}`}</title>
              </circle>
            );
            travelled += length;
            return circle;
          })}
        </svg>
        <div className="donut-center">
          <span className="donut-total">R$ {formatCompact(total)}</span>
          <span className="donut-label">{centerLabel}</span>
        </div>
      </div>

      <ul className="donut-legend">
        {slices.map((slice, i) => (
          <li key={slice.name}>
            <span className={`dot slice-bg-${i}`} aria-hidden="true" />
            <span className="legend-name">{slice.name}</span>
            <span className="legend-amount">{formatAmount(slice.amount)}</span>
            <span className="legend-share">
              {total > 0 ? Math.round((slice.amount * 100) / total) : 0}%
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}
