import type { Integrity } from '../lib/api';
import { formatAmount } from '../lib/format';
import { Icon } from './Icon';

type Tone = 'credit' | 'debit' | 'neutral';

export function Metric({
  label,
  value,
  detail,
  tone = 'neutral',
  featured = false,
}: {
  label: string;
  value: number;
  detail?: string;
  tone?: Tone;
  featured?: boolean;
}) {
  const text = formatAmount(value, true);
  // "R$ 96.420,66" tem 12 caracteres; de milhao para cima passa de 13 e o
  // CSS desce um degrau de tamanho para continuar cabendo no cartao.
  const isLong = text.length > 13;

  return (
    <div className={`metric${featured ? ' featured' : ''}`}>
      <p className="metric-label">{label}</p>
      <p className={`metric-value ${tone}${isLong ? ' long' : ''}`}>{text}</p>
      {detail && <p className="metric-detail">{detail}</p>}
    </div>
  );
}

/**
 * Nao e emblema decorativo: e a afirmacao verificavel de que a soma de todos
 * os saldos e exatamente zero. Por isso mostra a propria soma.
 */
export function IntegritySeal({ integrity }: { integrity: Integrity }) {
  const ok = integrity.isIntact;

  return (
    <div
      className={`seal ${ok ? 'seal-ok' : 'seal-bad'}`}
      title={
        ok
          ? 'A soma de todos os saldos é exatamente zero: nenhum centavo entrou nem saiu do nada.'
          : `Transações desbalanceadas: ${integrity.unbalancedTransactions}.`
      }
    >
      <span className="seal-icon">
        <Icon name={ok ? 'check' : 'alert'} size={13} />
      </span>
      <span className="seal-text">
        <strong>{ok ? 'Razão íntegro' : 'Razão inconsistente'}</strong>
        <span>Σ saldos = {formatAmount(integrity.sumOfAllBalances)}</span>
      </span>
    </div>
  );
}
