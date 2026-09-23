import { useMemo, type ReactNode } from 'react';
import { BarList } from '../charts/BarList';
import { ColumnChart } from '../charts/ColumnChart';
import { Icon } from '../components/Icon';
import { Metric } from '../components/Metrics';
import { EmptyState, PageHeader, Panel } from '../components/Panel';
import { TransactionTable } from '../components/TransactionTable';
import { flowFromReport, monthSummary, spendingByCategory } from '../lib/analysis';
import type { BudgetMonth } from '../lib/api';
import { envelopeUsage, envelopesByAttention } from '../lib/budget';
import { capitalize, currentMonth, formatAmount, monthLongLabel, shiftMonth } from '../lib/format';
import type { PageProps } from '../lib/types';

/**
 * O painel abre pelo gasto do mes, porque e para isso que o app existe, e
 * segue a planilha de onde os dados vieram: entrou, gastou, sobrou.
 */
export function Overview({ data, navigate }: PageProps) {
  const month = currentMonth();

  // Os seis meses vem somados do servidor: ver lib/analysis.ts sobre por que
  // somar isto no navegador dava numero menor que o real.
  const flow = useMemo(
    () => (data.monthlyFlow ? flowFromReport(data.monthlyFlow) : []),
    [data.monthlyFlow],
  );

  // O mes corrente e a ultima coluna do mesmo relatorio, entao o cartao do
  // topo e o grafico nunca discordam. A contagem de lancamentos continua
  // vindo da lista, que e o que a tela de fato mostra.
  const current = flow.at(-1);
  const summary = useMemo(() => monthSummary(data.transactions, month), [data.transactions, month]);
  const income = current?.income ?? summary.income;
  const expense = current?.expense ?? summary.expense;

  const spending = useMemo(() => spendingByCategory(data.transactions, month), [data.transactions, month]);

  const userAccounts = data.accounts.filter((a) => !a.isSystem);

  if (userAccounts.length === 0) {
    return <Onboarding navigate={navigate} />;
  }

  const truncated = data.transactionTotal > data.transactions.length;
  const monthName = capitalize(monthLongLabel(month));
  const monthOnly = monthLongLabel(month).split(' ')[0];
  const previousOnly = monthLongLabel(shiftMonth(month, -1)).split(' ')[0];

  /*
   * OS QUATRO CARTOES SAO A PLANILHA DO DONO: entrou, gastou, sobrou.
   *
   * A sobra e o saldo das contas do dia a dia (as que entram no orcamento,
   * cartao incluido). Ela ja carrega o mes anterior sozinha - e o "SALDO
   * ANTERIOR" da planilha, que la precisa ser copiado a mao e aqui vem do
   * razao. Conferido contra a planilha em set/2026: -735,38 nos dois.
   *
   * O saldo anterior sai por diferenca: sobra - resultado do mes. Vale
   * enquanto nao houver lancamento datado depois deste mes.
   *
   * "Ja separado" e "ainda sem destino" sairam daqui: sao numeros do
   * orcamento, e para quem ainda nao usa o orcamento eles acumulam todo o
   * gasto desde o primeiro dia e nao querem dizer nada. Moram na tela de
   * Orcamento, onde fazem sentido.
   */
  const leftover = data.accounts
    .filter((a) => a.isOnBudget && !a.isSystem && a.currencyCode === 'BRL')
    .reduce((sum, a) => sum + a.balanceMinorUnits, 0);
  const result = income - expense;
  const carried = leftover - result;

  const budget = data.budget;

  return (
    <>
      <PageHeader
        title="Visão geral"
        subtitle={monthName}
      />

      <div className="metrics">
        <Metric
          featured
          label={`Gastos de ${monthOnly}`}
          value={expense}
          detail={`${summary.count} ${summary.count === 1 ? 'lançamento' : 'lançamentos'}`}
        />
        <Metric label={`Entrou em ${monthOnly}`} value={income} tone="credit" detail="Salário, vendas e o que te devolveram" />
        <Metric
          label="Resultado do mês"
          value={result}
          tone={result < 0 ? 'debit' : 'credit'}
          detail={result < 0 ? 'Gastou mais do que entrou' : 'Entrou mais do que gastou'}
        />
        <Metric
          label="Sobra"
          value={leftover}
          tone={leftover < 0 ? 'debit' : 'neutral'}
          detail={`${formatAmount(carried, true)} veio de ${previousOnly}`}
        />
      </div>

      <div className="grid-2">
        <Panel
          title={`Seu dinheiro em ${monthOnly}`}
          actions={
            <button type="button" className="link" onClick={() => navigate('budget')}>
              Abrir orçamento <Icon name="chevron" size={13} />
            </button>
          }
        >
          <BudgetGlance budget={budget} navigate={navigate} />
        </Panel>
        <Panel title="Gastos por categoria" actions={<span className="hint">{monthName}</span>}>
          <BarList items={spending} />
        </Panel>
      </div>

      <div className="grid-2 grid-aside">
        <Panel title="Entradas e saídas" actions={<span className="hint">Últimos 6 meses</span>}>
          <ColumnChart months={flow} />
        </Panel>
        <Panel
          title="Últimos lançamentos"
          actions={
            <button type="button" className="link" onClick={() => navigate('transactions')}>
              Ver todos <Icon name="chevron" size={13} />
            </button>
          }
        >
          {data.transactions.length === 0 ? (
            <EmptyState title="Nenhum lançamento ainda." text="Lance uma despesa ou importe um extrato." />
          ) : (
            <TransactionTable transactions={data.transactions.slice(0, 7)} />
          )}
        </Panel>
      </div>

      {truncated && (
        <p className="note note-center">
          Entradas, saídas e o gasto do mês somam o sistema inteiro. Já a lista e o gasto por
          categoria acima olham os {data.transactions.length} lançamentos mais recentes de{' '}
          {data.transactionTotal} —{' '}
          <button type="button" className="link" onClick={() => navigate('reports')}>
            os relatórios somam todos
          </button>
          .
        </p>
      )}
    </>
  );
}

/** As categorias que pedem atencao: estourados primeiro, depois os mais consumidos. */
function BudgetGlance({ budget, navigate }: { budget: BudgetMonth | null; navigate: PageProps['navigate'] }) {
  const items = budget ? envelopesByAttention(budget.categories).slice(0, 6) : [];

  // Sem nenhuma categoria com limite, a lista seria so "quanto gastou em
  // cada uma" - que e exatamente o painel ao lado. Melhor dizer o que falta.
  const anyFunded = items.some((c) => envelopeUsage(c).funded > 0);

  if (!anyFunded) {
    return (
      <EmptyState
        title="Você ainda não definiu limites."
        text="Diga quanto quer gastar em cada categoria no mês. A partir daí, aqui aparece quanto ainda pode gastar em cada uma — e quais passaram do ponto."
        action={
          <button className="secondary" onClick={() => navigate('budget')}>
            Definir limites
          </button>
        }
      />
    );
  }

  return (
    <ul className="glance">
      {items.map((c) => {
        const usage = envelopeUsage(c);
        const unbudgeted = usage.state === 'unbudgeted';
        return (
          <li key={c.categoryId} className="glance-row">
            <div className="glance-head">
              <span className="glance-name">{c.name}</span>
              <span className={`glance-amount ${usage.state === 'over' ? 'debit' : ''}`}>
                {unbudgeted
                  ? formatAmount(usage.spent, true)
                  : usage.state === 'over'
                    ? `estourou ${formatAmount(-c.availableMinorUnits, true)}`
                    : `${formatAmount(c.availableMinorUnits, true)} livre`}
              </span>
            </div>
            <svg className={`envelope-bar ${usage.state}`} viewBox="0 0 100 4" preserveAspectRatio="none" aria-hidden="true">
              <rect className="bar-bg" width="100" height="4" rx="2" />
              {usage.fill > 0 && <rect className="bar-fill" width={usage.fill} height="4" rx="2" />}
            </svg>
            <p className="subtext">
              {unbudgeted
                ? 'gasto no mês · nada separado para isso'
                : `${formatAmount(usage.spent, true)} gastos de ${formatAmount(Math.max(usage.funded, 0), true)}`}
            </p>
          </li>
        );
      })}
    </ul>
  );
}

/** Tela de quem acabou de chegar: orienta em vez de mostrar graficos vazios. */
function Onboarding({ navigate }: { navigate: PageProps['navigate'] }) {
  return (
    <>
      <PageHeader title="Boas-vindas" subtitle="Três passos para o sistema começar a trabalhar." />
      <div className="onboarding">
        <OnboardingStep
          number={1}
          title="Crie suas contas"
          text="Conta corrente, poupança, cartão. O saldo de hoje entra como saldo inicial."
          action={<button onClick={() => navigate('accounts')}>Criar conta</button>}
        />
        <OnboardingStep
          number={2}
          title="Organize categorias"
          text="Moradia, mercado, transporte. Cada categoria de despesa vira uma categoria do orçamento."
          action={
            <button className="secondary" onClick={() => navigate('categories')}>
              Criar categorias
            </button>
          }
        />
        <OnboardingStep
          number={3}
          title="Traga o extrato"
          text="Importe o OFX do banco. Reimportar o mesmo arquivo nunca duplica nada."
          action={
            <button className="secondary" onClick={() => navigate('import')}>
              Importar extrato
            </button>
          }
        />
      </div>
    </>
  );
}

function OnboardingStep({
  number,
  title,
  text,
  action,
}: {
  number: number;
  title: string;
  text: string;
  action: ReactNode;
}) {
  return (
    <div className="onboarding-step">
      <span className="onboarding-number">{number}</span>
      <h3>{title}</h3>
      <p>{text}</p>
      {action}
    </div>
  );
}
