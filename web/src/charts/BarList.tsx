import type { NamedAmount } from '../lib/analysis';
import { formatAmount } from '../lib/format';

/*
 * Todos os graficos sao SVG escrito a mao, sem biblioteca.
 *
 * Por que: a CSP e script-src 'self' - biblioteca de CDN nem carregaria -, e
 * as bibliotecas de grafico populares pesam mais do que o app inteiro.
 *
 * Sobre ponto flutuante: a largura de uma barra e GEOMETRIA, em pixels, e ai
 * float e o certo. O que nunca vira float e o valor exibido, que continua
 * saindo inteiro de formatAmount().
 */

export function BarList({ items, limit = 6 }: { items: NamedAmount[]; limit?: number }) {
  if (items.length === 0) {
    return <p className="empty">Nenhum gasto categorizado neste período.</p>;
  }

  const visible = items.slice(0, limit);
  const rest = items.slice(limit).reduce((sum, item) => sum + item.amount, 0);
  const rows = rest > 0 ? [...visible, { name: 'Outras', amount: rest }] : visible;

  const largest = Math.max(...rows.map((row) => row.amount));
  const total = items.reduce((sum, item) => sum + item.amount, 0);

  return (
    <ul className="bar-list">
      {rows.map((row) => {
        const width = largest > 0 ? Math.max(1.5, (row.amount / largest) * 100) : 0;
        const share = total > 0 ? Math.round((row.amount * 100) / total) : 0;

        return (
          <li key={row.name}>
            <div className="bar-head">
              <span className="bar-name">{row.name}</span>
              <span className="bar-amount">{formatAmount(row.amount)}</span>
            </div>
            <div className="bar-row">
              <svg className="bar-track" viewBox="0 0 100 8" preserveAspectRatio="none" aria-hidden="true">
                <rect className="bar-bg" width="100" height="8" rx="1" />
                <rect className="bar-fill" width={width} height="8" rx="1" />
              </svg>
              <span className="bar-share">{share}%</span>
            </div>
          </li>
        );
      })}
    </ul>
  );
}
