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
 * os saldos e exatamente zero.
 *
 * O TEXTO NAO USA JARGAO. "Razao integro" e "Sigma saldos" sao corretos e
 * nao querem dizer nada para quem esta olhando o proprio dinheiro. O que a
 * pessoa precisa saber e se pode confiar nos numeros da tela - e isso se diz
 * em portugues. A prova exata continua existindo, no titulo que aparece ao
 * passar o mouse, para quando alguem quiser conferir.
 */
export function IntegritySeal({ integrity }: { integrity: Integrity }) {
  const ok = integrity.isIntact;

  return (
    <div
      className={`seal ${ok ? 'seal-ok' : 'seal-bad'}`}
      title={
        ok
          ? `Somando todas as contas, sobra exatamente ${formatAmount(integrity.sumOfAllBalances, true)} — nenhum centavo apareceu nem sumiu.`
          : `${integrity.unbalancedTransactions} lançamento(s) com entrada e saída que não batem.`
      }
    >
      <span className="seal-icon">
        <Icon name={ok ? 'check' : 'alert'} size={13} />
      </span>
      <span className="seal-text">
        <strong>{ok ? 'Tudo confere' : 'Tem coisa fora do lugar'}</strong>
        <span>
          {ok
            ? 'Nenhum centavo se perdeu'
            : `${integrity.unbalancedTransactions} lançamento${integrity.unbalancedTransactions === 1 ? '' : 's'} com problema`}
        </span>
      </span>
    </div>
  );
}
