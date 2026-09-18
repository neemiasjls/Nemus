import { useMemo, type ReactNode } from 'react';
import { BarList } from '../charts/BarList';
import { ColumnChart } from '../charts/ColumnChart';
import { Icon } from '../components/Icon';
import { Metric } from '../components/Metrics';
import { EmptyState, PageHeader, Panel } from '../components/Panel';
import { TransactionTable } from '../components/TransactionTable';
import { flowFromReport, monthSummary, spendingByCategory } from '../lib/analysis';
import type { BudgetMonth } from '../lib/api';
import { envelopeUsage, envelopesByAttention, readyState } from '../lib/budget';
import { capitalize, currentMonth, formatAmount, monthLongLabel } from '../lib/format';
import type { PageProps } from '../lib/types';

/**
 * O painel abre pelo gasto do mes, porque e para isso que o app existe:
 * quanto saiu, quanto ainda ha nas categorias, quanto falta dar destino.
 * Patrimonio fica, mas como contexto, no ultimo cartao.
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

  const brl = data.netWorth.find((n) => n.currencyCode === 'BRL') ?? data.netWorth[0];
  const netWorth = brl?.netWorthMinorUnits ?? 0;
  const truncated = data.transactionTotal > data.transactions.length;
  const monthName = capitalize(monthLongLabel(month));
  const monthOnly = monthLongLabel(month).split(' ')[0];

  const budget = data.budget;
  const available = budget?.availableMinorUnits ?? 0;
  const ready = budget?.readyToAssignMinorUnits ?? 0;
  const readyDetail = {
    unassigned: 'Esperando uma categoria',
    balanced: 'Todo real tem destino',
    overassigned: 'Você separou mais do que tem',
  }[readyState(ready)];

  return (
    <>
      <PageHeader
        title="Visão geral"
        subtitle={`${monthName} · ${summary.count} ${summary.count === 1 ? 'lançamento' : 'lançamentos'} no mês`}
      />

      <div className="metrics">
        <Metric
          featured
          label={`Gastos de ${monthOnly}`}
          value={expense}
          detail={`${formatAmount(income, true)} entrou no mês`}
        />
        <Metric
          label="Já separado"
          value={available}
          tone={available < 0 ? 'debit' : 'credit'}
          detail="Somando todas as categorias"
        />
        <Metric label="Ainda sem destino" value={ready} tone={ready < 0 ? 'debit' : 'neutral'} detail={readyDetail} />
        <Metric
          label="Patrimônio líquido"
          value={netWorth}
          tone={netWorth < 0 ? 'debit' : 'neutral'}
          detail="Ativos menos o que você deve"
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

  if (items.length === 0) {
    return (
      <EmptyState
        title="Nenhuma categoria com dinheiro separado."
        text="Divida o que está ainda sem destino entre as categorias de despesa. É isso que dá destino a cada real."
        action={
          <button className="secondary" onClick={() => navigate('budget')}>
            Distribuir o dinheiro
          </button>
        }
      />
    );
  }

  return (
    <ul className="glance">
      {items.map((c) => {
        const usage = envelopeUsage(c);
        return (
          <li key={c.categoryId} className="glance-row">
            <div className="glance-head">
              <span className="glance-name">{c.name}</span>
              <span className={`glance-amount ${usage.state === 'over' ? 'debit' : ''}`}>
                {usage.state === 'over'
                  ? `estourou ${formatAmount(-c.availableMinorUnits)}`
                  : `${formatAmount(c.availableMinorUnits)} livre`}
              </span>
            </div>
            <svg className={`categoria-bar ${usage.state}`} viewBox="0 0 100 4" preserveAspectRatio="none" aria-hidden="true">
              <rect className="bar-bg" width="100" height="4" rx="2" />
              {usage.fill > 0 && <rect className="bar-fill" width={usage.fill} height="4" rx="2" />}
            </svg>
            <p className="subtext">
              {formatAmount(usage.spent)} gastos de {formatAmount(Math.max(usage.funded, 0))}
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
