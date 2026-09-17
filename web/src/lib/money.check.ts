import { formatMinorUnits, parseToMinorUnits } from './money.ts';

/*
 * Verificacao de money.ts, sem framework de teste: roda com
 * node --experimental-strip-types. Os rotulos ficam em portugues porque sao
 * a saida que a pessoa le.
 */

let failures = 0;

function expectEqual(label: string, actual: unknown, expected: unknown) {
  const ok = JSON.stringify(actual) === JSON.stringify(expected);
  if (!ok) {
    failures++;
    console.log(`FALHOU ${label}: obtido ${JSON.stringify(actual)}, esperado ${JSON.stringify(expected)}`);
  }
}

// formatacao
expectEqual('format 0', formatMinorUnits(0), '0,00');
expectEqual('format 1', formatMinorUnits(1), '0,01');
expectEqual('format 123456', formatMinorUnits(123456), '1.234,56');
expectEqual('format negativo', formatMinorUnits(-123456), '-1.234,56');
expectEqual('format milhao', formatMinorUnits(100000000), '1.000.000,00');
expectEqual('format simbolo', formatMinorUnits(123456, 2, true), 'R$ 1.234,56');

// leitura
expectEqual('parse ptbr', parseToMinorUnits('1.234,56'), { ok: true, value: 123456 });
expectEqual('parse virgula', parseToMinorUnits('1234,56'), { ok: true, value: 123456 });
expectEqual('parse ponto', parseToMinorUnits('1234.56'), { ok: true, value: 123456 });
expectEqual('parse inteiro', parseToMinorUnits('1234'), { ok: true, value: 123400 });
expectEqual('parse negativo', parseToMinorUnits('-12,34'), { ok: true, value: -1234 });
expectEqual('parse en-us', parseToMinorUnits('1,234.56'), { ok: true, value: 123456 });
expectEqual('parse com R$', parseToMinorUnits('R$ 99,90'), { ok: true, value: 9990 });
expectEqual('parse uma casa', parseToMinorUnits('12,5'), { ok: true, value: 1250 });

// recusa em vez de arredondar
expectEqual('recusa 3 casas', parseToMinorUnits('10,005').ok, false);
expectEqual('recusa vazio', parseToMinorUnits('').ok, false);
expectEqual('recusa letra', parseToMinorUnits('abc').ok, false);

// milhar com ponto, do jeito brasileiro
expectEqual('milhar com ponto', parseToMinorUnits('1.500'), { ok: true, value: 150000 });
expectEqual('milhares com ponto', parseToMinorUnits('1.234.567'), { ok: true, value: 123456700 });
expectEqual('milhar negativo', parseToMinorUnits('-2.000'), { ok: true, value: -200000 });
expectEqual('ponto decimal curto', parseToMinorUnits('1.50'), { ok: true, value: 150 });

// agrupamento que nao e agrupamento. O primeiro e o que sai de digitar em
// cima de um valor sem apagar - e ja foi lido como R$ 1.250.001.500,00.
expectEqual('recusa valor colado', parseToMinorUnits('1.250,001.500,00').ok, false);
expectEqual('recusa grupo torto', parseToMinorUnits('12.34.56').ok, false);
expectEqual('recusa virgula repetida', parseToMinorUnits('1,250,00').ok, false);
expectEqual('recusa grupo longo', parseToMinorUnits('1234.567,00').ok, false);
expectEqual('recusa so separador', parseToMinorUnits('.').ok, false);

// ida e volta: o classico que quebra em ponto flutuante
for (const cents of [1, 7, 10, 99, 100, 101, 1005, 33333, 999999999]) {
  const text = formatMinorUnits(cents);
  const roundTrip = parseToMinorUnits(text);
  expectEqual(`ida-e-volta ${cents}`, roundTrip, { ok: true, value: cents });
}

// 0.1 + 0.2: o caso que motiva o arquivo inteiro
const tenCents = parseToMinorUnits('0,10');
const twentyCents = parseToMinorUnits('0,20');
if (tenCents.ok && twentyCents.ok) {
  expectEqual('0,10 + 0,20', formatMinorUnits(tenCents.value + twentyCents.value), '0,30');
}

console.log(failures === 0 ? 'TODOS OS CASOS PASSARAM' : `${failures} FALHA(S)`);
