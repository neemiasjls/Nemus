import { formatMinorUnits } from './money';

/*
 * Formatacao para exibicao. Nada aqui faz conta com dinheiro: recebe
 * unidades minimas inteiras e devolve texto.
 */

const MINUS = '−';

/**
 * Menos tipografico (U+2212) em vez de hifen. Numa coluna de valores o
 * hifen e curto e alto demais, e a coluna parece desalinhada.
 */
export function formatAmount(minor: number, withSymbol = false): string {
  return formatMinorUnits(minor, 2, withSymbol).replace('-', MINUS);
}

/** Com sinal explicito dos dois lados: +1.234,56 ou −1.234,56. */
export function formatSigned(minor: number): string {
  return minor > 0 ? `+${formatAmount(minor)}` : formatAmount(minor);
}

/** Divisao inteira exata para nao-negativos: a menos o resto e multiplo de b. */
const intDiv = (a: number, b: number) => (a - (a % b)) / b;

/**
 * Valor curto para eixo de grafico e centro de rosca: 1.234.567 centavos
 * vira "12,3 mil". Feito com divisao inteira de proposito - mesmo sendo so
 * rotulo, a regra do projeto e que ponto flutuante nao toca dinheiro.
 */
export function formatCompact(minor: number): string {
  const sign = minor < 0 ? MINUS : '';
  const wholeUnits = intDiv(Math.abs(minor), 100);

  const scaled = (whole: number, tenth: number, suffix: string) =>
    `${sign}${whole}${tenth === 0 ? '' : `,${tenth}`} ${suffix}`;

  if (wholeUnits >= 1_000_000) {
    return scaled(intDiv(wholeUnits, 1_000_000), intDiv(wholeUnits % 1_000_000, 100_000), 'mi');
  }
  if (wholeUnits >= 1_000) {
    return scaled(intDiv(wholeUnits, 1_000), intDiv(wholeUnits % 1_000, 100), 'mil');
  }
  return `${sign}${wholeUnits}`;
}

const MONTHS = ['jan', 'fev', 'mar', 'abr', 'mai', 'jun', 'jul', 'ago', 'set', 'out', 'nov', 'dez'];
const MONTHS_LONG = [
  'janeiro', 'fevereiro', 'março', 'abril', 'maio', 'junho',
  'julho', 'agosto', 'setembro', 'outubro', 'novembro', 'dezembro',
];

/** "2026-09-08" vira "08 set". */
export function formatDay(iso: string): string {
  const parts = iso.split('-');
  const month = MONTHS[Number(parts[1]) - 1];
  return month ? `${parts[2]} ${month}` : iso;
}

/** Chave de mes "2026-09" a partir de data ISO. */
export const monthKeyOf = (iso: string) => iso.slice(0, 7);

/** "2026-09" vira "set". */
export function monthShortLabel(key: string): string {
  return MONTHS[Number(key.slice(5, 7)) - 1] ?? key;
}

/** "2026-09" vira "setembro de 2026". */
export function monthLongLabel(key: string): string {
  const month = MONTHS_LONG[Number(key.slice(5, 7)) - 1];
  return month ? `${month} de ${key.slice(0, 4)}` : key;
}

/**
 * Hoje no fuso de quem usa, em ISO.
 *
 * Uma versao anterior usava toISOString(), que e UTC: a partir das 21h no
 * Brasil ela ja devolvia o dia SEGUINTE, e o formulario vinha preenchido com
 * a data errada. Mesma armadilha do fuso no OFX, do outro lado da tela.
 */
export function today(): string {
  const now = new Date();
  const month = String(now.getMonth() + 1).padStart(2, '0');
  const day = String(now.getDate()).padStart(2, '0');
  return `${now.getFullYear()}-${month}-${day}`;
}

export const currentMonth = () => today().slice(0, 7);

/** "2026-01" com -1 vira "2025-12". Aritmetica de calendario em inteiros, sem Date. */
export function shiftMonth(key: string, delta: number): string {
  const index = Number(key.slice(0, 4)) * 12 + Number(key.slice(5, 7)) - 1 + delta;
  const year = Math.floor(index / 12);
  const month = index - year * 12 + 1;
  return `${year}-${String(month).padStart(2, '0')}`;
}

/** ASSET e companhia sao codigo do banco; na tela, portugues. */
export const ACCOUNT_TYPE_LABEL: Record<string, string> = {
  ASSET: 'Ativo',
  LIABILITY: 'Passivo',
  EQUITY: 'Patrimônio',
  REVENUE: 'Receita externa',
  EXPENSE: 'Despesa externa',
};

/** "setembro de 2026" vira "Setembro de 2026". */
export const capitalize = (text: string) => text.charAt(0).toUpperCase() + text.slice(1);
