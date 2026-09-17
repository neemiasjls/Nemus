/**
 * Dinheiro no frontend.
 *
 * O PROBLEMA. Em JavaScript nao existe inteiro: `number` e IEEE-754 de 64
 * bits. `0.1 + 0.2` da `0.30000000000000004`, e `1.005.toFixed(2)` da
 * "1.00". Num app de financas isso nao e curiosidade academica - e o
 * centavo que some do extrato sem ninguem notar.
 *
 * A REGRA. Nenhum valor monetario vira float em ponto nenhum deste arquivo.
 * A API manda e recebe unidades minimas como inteiro (1234 = R$ 12,34), e
 * tudo aqui e aritmetica inteira. A formatacao monta a string separando
 * parte inteira e centavos com divisao e resto, nunca com `toFixed`.
 *
 * `number` aguenta inteiro exato ate 2^53, o que da uns 90 trilhoes de
 * reais em centavos. Folgado para financas pessoais - e o limite existe e e
 * conhecido, em vez de um erro de arredondamento silencioso.
 */

const THOUSANDS_SEPARATOR = '.';
const DECIMAL_SEPARATOR = ',';

/** Formata unidades minimas para exibicao: 123456 -> "1.234,56". */
export function formatMinorUnits(
  minorUnits: number,
  decimalPlaces = 2,
  withSymbol = false,
): string {
  if (!Number.isSafeInteger(minorUnits)) {
    // Melhor gritar do que exibir numero errado com ar de certo.
    throw new Error(`Valor monetario nao inteiro: ${minorUnits}`);
  }

  const negative = minorUnits < 0;
  const magnitude = Math.abs(minorUnits);

  const divisor = 10 ** decimalPlaces;
  const whole = Math.floor(magnitude / divisor);
  const fraction = magnitude % divisor;

  const groupedWhole = String(whole).replace(/\B(?=(\d{3})+(?!\d))/g, THOUSANDS_SEPARATOR);
  const fractionDigits = String(fraction).padStart(decimalPlaces, '0');

  const body =
    decimalPlaces > 0
      ? `${groupedWhole}${DECIMAL_SEPARATOR}${fractionDigits}`
      : groupedWhole;

  const sign = negative ? '-' : '';
  return withSymbol ? `${sign}R$ ${body}` : `${sign}${body}`;
}

/**
 * Le o que a pessoa digitou e devolve unidades minimas.
 *
 * Aceita "1234,56", "1.234,56", "1234.56", "1.500" e "1234". Recusa em vez
 * de arredondar quando ha casas demais: quem digitou "10,005" quis dizer
 * alguma coisa, e escolher por essa pessoa em silencio e pior do que pedir
 * para corrigir.
 *
 * Tambem recusa agrupamento que nao e agrupamento. Uma versao anterior
 * descartava todo separador antes do decimal, e "1.250,001.500,00" - o que
 * sai quando se digita em cima de um valor sem apaga-lo - virava
 * R$ 1.250.001.500,00 sem erro nenhum.
 */
export function parseToMinorUnits(
  input: string,
  decimalPlaces = 2,
): { ok: true; value: number } | { ok: false; error: string } {
  const cleaned = input.trim().replace(/\s/g, '').replace(/^R\$/i, '').trim();

  if (cleaned === '') {
    return { ok: false, error: 'Informe um valor.' };
  }

  const negative = cleaned.startsWith('-');
  const unsigned = negative ? cleaned.slice(1) : cleaned;

  if (!/^[\d.,]+$/.test(unsigned)) {
    return { ok: false, error: 'Use apenas números, ponto e vírgula.' };
  }

  const parts = splitDecimal(unsigned);
  if (parts === null) {
    return { ok: false, error: 'Valor mal formado. Escreva, por exemplo, 1.234,56.' };
  }

  const { whole: wholePart, fraction: fractionPart } = parts;

  if (fractionPart.length > decimalPlaces) {
    return {
      ok: false,
      error: `No máximo ${decimalPlaces} casas decimais.`,
    };
  }

  const digits = wholePart + fractionPart.padEnd(decimalPlaces, '0');

  if (digits === '' || !/^\d+$/.test(digits)) {
    return { ok: false, error: 'Valor inválido.' };
  }

  // Aritmetica inteira do comeco ao fim: nenhum parseFloat, nenhum *100.
  const value = Number(digits);

  if (!Number.isSafeInteger(value)) {
    return { ok: false, error: 'Valor grande demais.' };
  }

  return { ok: true, value: negative ? -value : value };
}

/**
 * Separa parte inteira e fracao, ja sem os separadores de milhar.
 *
 *   1. Com virgula e ponto, o ultimo e o decimal - e ele so pode aparecer
 *      uma vez. O outro so pode agrupar.
 *   2. Com um simbolo so, repetido, ele agrupa: "1.234.567".
 *   3. Com um simbolo so, uma vez, ele e decimal - menos o ponto seguido de
 *      exatamente tres digitos: "1.500" e mil e quinhentos no Brasil.
 *
 * Agrupar e primeiro grupo com 1 a 3 digitos e os demais com 3 exatos.
 * Qualquer outra coisa devolve null.
 */
function splitDecimal(text: string): { whole: string; fraction: string } | null {
  const commas = text.split(',').length - 1;
  const dots = text.split('.').length - 1;

  let decimal: ',' | '.' | null;
  if (commas > 0 && dots > 0) {
    decimal = text.lastIndexOf(',') > text.lastIndexOf('.') ? ',' : '.';
    if ((decimal === ',' ? commas : dots) > 1) return null;
  } else if (commas + dots === 0) {
    decimal = null;
  } else {
    const symbol = commas > 0 ? ',' : '.';
    const digitsAfter = text.length - text.indexOf(symbol) - 1;
    decimal = commas + dots > 1 || (symbol === '.' && digitsAfter === 3) ? null : symbol;
  }

  let whole = text;
  let fraction = '';
  if (decimal !== null) {
    const cut = text.lastIndexOf(decimal);
    whole = text.slice(0, cut);
    fraction = text.slice(cut + 1);
  }

  if (/[.,]/.test(whole)) {
    const groups = whole.split(/[.,]/);
    const first = groups[0] ?? '';
    if (first.length < 1 || first.length > 3 || groups.slice(1).some((group) => group.length !== 3)) return null;
    whole = groups.join('');
  }

  if (whole === '' && fraction === '') return null;
  return { whole, fraction };
}
