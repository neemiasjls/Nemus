import type { ReactNode } from 'react';

export type IconName =
  | 'dashboard'
  | 'budget'
  | 'transactions'
  | 'reports'
  | 'repeat'
  | 'accounts'
  | 'categories'
  | 'import'
  | 'sun'
  | 'moon'
  | 'system'
  | 'plus'
  | 'close'
  | 'signOut'
  | 'chevron'
  | 'search'
  | 'file'
  | 'check'
  | 'alert'
  | 'lock'
  | 'card';

/*
 * Icones desenhados a mao, em traco, sobre grade de 24. Nenhuma biblioteca:
 * a CSP so aceita script do proprio dominio, e um pacote de icones inteiro
 * para usar dezesseis seria peso morto no bundle.
 */
const STROKES: Record<IconName, ReactNode> = {
  dashboard: (
    <>
      <rect x="3.5" y="3.5" width="7" height="8" rx="1.2" />
      <rect x="13.5" y="3.5" width="7" height="5" rx="1.2" />
      <rect x="13.5" y="11.5" width="7" height="9" rx="1.2" />
      <rect x="3.5" y="14.5" width="7" height="6" rx="1.2" />
    </>
  ),
  // O metodo e de categorias; o icone e literal.
  budget: (
    <>
      <rect x="3" y="5.5" width="18" height="13" rx="1.8" />
      <path d="M3.6 7.2l8.4 6.1 8.4-6.1" />
    </>
  ),
  transactions: (
    <>
      <path d="M8.5 6.5h11.5M8.5 12h11.5M8.5 17.5h11.5" />
      <path d="M4.5 6.5h.01M4.5 12h.01M4.5 17.5h.01" />
    </>
  ),
  // Seta que volta para si mesma: o gasto que se repete todo mes.
  repeat: (
    <>
      <path d="M4.5 11.5V10a4 4 0 0 1 4-4h9" />
      <path d="M14.5 3l3 3-3 3" />
      <path d="M19.5 12.5V14a4 4 0 0 1-4 4h-9" />
      <path d="M9.5 21l-3-3 3-3" />
    </>
  ),
  // Barras de altura diferente: o relatorio e sobre comparar meses.
  reports: (
    <>
      <path d="M4 20.5h16.5" />
      <rect x="5" y="12" width="3.6" height="6" rx="0.8" />
      <rect x="10.2" y="7.5" width="3.6" height="10.5" rx="0.8" />
      <rect x="15.4" y="4" width="3.6" height="14" rx="0.8" />
    </>
  ),
  accounts: (
    <>
      <rect x="3" y="6" width="18" height="13" rx="2" />
      <path d="M3 10.5h18" />
      <path d="M15.5 15h2.5" />
    </>
  ),
  categories: (
    <>
      <path d="M3.5 12.2V4.5a1 1 0 0 1 1-1h7.7l8.3 8.3-8.7 8.7z" />
      <circle cx="8" cy="8" r="1.4" />
    </>
  ),
  import: (
    <>
      <path d="M12 3.5v11" />
      <path d="M7.5 10l4.5 4.5 4.5-4.5" />
      <path d="M4 16.5v2a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-2" />
    </>
  ),
  sun: (
    <>
      <circle cx="12" cy="12" r="4" />
      <path d="M12 2.5v2M12 19.5v2M2.5 12h2M19.5 12h2M5.3 5.3l1.4 1.4M17.3 17.3l1.4 1.4M5.3 18.7l1.4-1.4M17.3 6.7l1.4-1.4" />
    </>
  ),
  moon: <path d="M19.5 14.8A8 8 0 1 1 9.2 4.5a6.3 6.3 0 0 0 10.3 10.3z" />,
  system: (
    <>
      <rect x="3" y="4" width="18" height="12.5" rx="1.6" />
      <path d="M8.5 20.5h7M12 16.5v4" />
    </>
  ),
  plus: <path d="M12 5v14M5 12h14" />,
  close: <path d="M6.5 6.5l11 11M17.5 6.5l-11 11" />,
  signOut: (
    <>
      <path d="M14.5 4H18a1.5 1.5 0 0 1 1.5 1.5v13A1.5 1.5 0 0 1 18 20h-3.5" />
      <path d="M9.5 8l-4 4 4 4" />
      <path d="M5.5 12h10" />
    </>
  ),
  chevron: <path d="M9.5 6l6 6-6 6" />,
  search: (
    <>
      <circle cx="11" cy="11" r="6" />
      <path d="M20 20l-4.3-4.3" />
    </>
  ),
  file: (
    <>
      <path d="M13.5 3.5H6.5A1.5 1.5 0 0 0 5 5v14a1.5 1.5 0 0 0 1.5 1.5h11A1.5 1.5 0 0 0 19 19V9z" />
      <path d="M13.5 3.5V9H19" />
    </>
  ),
  check: <path d="M5 12.5l4.5 4.5L19 7.5" />,
  // Cartao com a tarja: parcelamento e coisa de cartao.
  card: (
    <>
      <rect x="2.8" y="5.5" width="18.4" height="13" rx="2.2" />
      <path d="M2.8 9.8h18.4" />
      <path d="M6.5 14.5h3.5" />
    </>
  ),
  lock: (
    <>
      <rect x="4.5" y="10.5" width="15" height="9.5" rx="1.8" />
      <path d="M8 10.5V7.8a4 4 0 0 1 8 0v2.7" />
    </>
  ),
  alert: (
    <>
      <path d="M12 4l9 16H3z" />
      <path d="M12 10v4.5M12 17.5h.01" />
    </>
  ),
};

export function Icon({ name, size = 18 }: { name: IconName; size?: number }) {
  return (
    <svg
      className="icon"
      width={size}
      height={size}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth={1.7}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      {STROKES[name]}
    </svg>
  );
}

/** A marca: a inicial em traco sobre o fio do livro-razao. */
export function Logo({ size = 30 }: { size?: number }) {
  return (
    <svg className="logo" width={size} height={size} viewBox="0 0 32 32" aria-hidden="true">
      <rect className="logo-bg" width="32" height="32" rx="7" />
      <path
        className="logo-letter"
        d="M10.5 21.5V10l11 11.5V10"
        fill="none"
        strokeWidth="2.4"
        strokeLinecap="square"
        strokeLinejoin="miter"
      />
      <path className="logo-rule" d="M9.5 25.2h13" strokeWidth="1.4" />
    </svg>
  );
}
