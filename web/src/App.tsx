import { useCallback, useEffect, useState } from 'react';
import { Icon, Logo } from './components/Icon';
import { IntegritySeal } from './components/Metrics';
import { ThemeSwitcher } from './components/ThemeSwitcher';
import { api, ApiError, clearToken, getToken } from './lib/api';
import { EMPTY_DATA, PAGES, ROUTES, type AppData, type PageId } from './lib/types';
import { currentMonth } from './lib/format';
import { Accounts } from './pages/Accounts';
import { Budget } from './pages/Budget';
import { Categories } from './pages/Categories';
import { ImportPage } from './pages/Import';
import { Installments } from './pages/Installments';
import { Overview } from './pages/Overview';
import { Password } from './pages/Password';
import { Recurring } from './pages/Recurring';
import { Reports } from './pages/Reports';
import { SignIn } from './pages/SignIn';
import { Transactions } from './pages/Transactions';

/**
 * Navegacao pelo hash (#/lancamentos). Sem biblioteca de rotas: sao seis
 * paginas, e o hash da de graca as duas coisas que importam - recarregar
 * mantem onde voce estava, e o voltar do navegador funciona. Sem configurar
 * nada no servidor da Cloudflare.
 *
 * O trecho depois do # e o slug em portugues, porque aparece na barra de
 * endereco; o codigo trabalha com o id em ingles.
 */
function pageFromHash(): PageId {
  // O que vem depois de "?" e recado de uma pagina para outra (por exemplo
  // "#/lancamentos?sem-categoria"), nao faz parte da rota.
  const slug = window.location.hash.replace(/^#\/?/, '').split('?')[0];
  return ROUTES.find((page) => page.slug === slug)?.id ?? 'overview';
}

function hashFor(id: PageId): string {
  return `#/${ROUTES.find((page) => page.id === id)?.slug ?? ''}`;
}

export function App() {
  const [signedIn, setSignedIn] = useState(() => getToken() !== null);

  const signOut = useCallback(async () => {
    // Avisa o servidor antes de esquecer o token: sem isso a sessao
    // continuaria valendo por 30 dias para quem tivesse copiado o token.
    try {
      await api.logout();
    } catch {
      /* sessao ja invalida, ou API fora do ar: sair localmente mesmo assim */
    }

    clearToken();
    setSignedIn(false);
  }, []);

  if (!signedIn) {
    return <SignIn onSignIn={() => setSignedIn(true)} />;
  }

  return <Shell onSignOut={() => void signOut()} />;
}

function Shell({ onSignOut }: { onSignOut: () => void }) {
  const [page, setPage] = useState<PageId>(pageFromHash);
  const [data, setData] = useState<AppData>(EMPTY_DATA);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const onHashChange = () => {
      setPage(pageFromHash());
      window.scrollTo({ top: 0 });
    };
    window.addEventListener('hashchange', onHashChange);
    return () => window.removeEventListener('hashchange', onHashChange);
  }, []);

  useEffect(() => {
    const current = ROUTES.find((p) => p.id === page);
    document.title = current ? `${current.label} · Nemus` : 'Nemus';
  }, [page]);

  const navigate = useCallback((target: PageId) => {
    window.location.hash = hashFor(target);
  }, []);

  const reload = useCallback(async () => {
    setError(null);

    try {
      const [accounts, categories, batch, netWorth, integrity, budget, monthlyFlow, session] =
        await Promise.all([
          api.listAccounts(),
          api.listCategories(),
          // 200 e o teto de pagina da API. Esta lista alimenta as tabelas e o
          // que se pode clicar; numero de painel nao sai dela - sai de
          // monthlyFlow, que o banco soma sobre o razao inteiro.
          api.listTransactions({ limit: 200 }),
          api.netWorth(),
          api.integrity(),
          api.budget(currentMonth()),
          api.monthlyFlow(currentMonth(), 6),
          api.me(),
        ]);

      setData({
        accounts,
        categories,
        transactions: batch.items,
        transactionTotal: batch.total,
        netWorth,
        integrity,
        budget,
        monthlyFlow,
        session,
      });
    } catch (e) {
      if (e instanceof ApiError && e.status === 401) {
        onSignOut();
        return;
      }
      setError(e instanceof Error ? e.message : 'Falha ao carregar os dados.');
    } finally {
      setLoading(false);
    }
  }, [onSignOut]);

  useEffect(() => {
    void reload();
  }, [reload]);

  const props = { data, reload, navigate };

  return (
    <div className="app">
      <aside className="sidebar">
        <a className="brand" href={hashFor('overview')} aria-label="Nemus, visão geral">
          <Logo />
          <span className="wordmark">Nemus</span>
        </a>

        <nav className="nav" aria-label="Seções">
          {PAGES.map((p) => (
            <a
              key={p.id}
              href={hashFor(p.id)}
              className={page === p.id ? 'active' : undefined}
              aria-current={page === p.id ? 'page' : undefined}
            >
              <Icon name={p.icon} />
              <span>{p.label}</span>
            </a>
          ))}
        </nav>

        <div className="sidebar-footer">
          {data.integrity && <IntegritySeal integrity={data.integrity} />}

          {data.session && (
            <a
              className={`who${page === 'password' ? ' active' : ''}`}
              href={hashFor('password')}
              title="Trocar senha"
            >
              <Icon name="lock" size={14} />
              <span>{data.session.username}</span>
            </a>
          )}

          <div className="sidebar-controls">
            <ThemeSwitcher />
            <button type="button" className="sign-out" onClick={onSignOut}>
              <Icon name="signOut" size={16} />
              <span>Sair</span>
            </button>
          </div>
        </div>
      </aside>

      <main className="content">
        <div className="content-inner">
          {error && (
            <div className="error-banner" role="alert">
              <span>{error}</span>
              <button type="button" className="secondary" onClick={() => void reload()}>
                Tentar de novo
              </button>
            </div>
          )}

          {loading ? (
            <LoadingSkeleton />
          ) : (
            <>
              {page === 'overview' && <Overview {...props} />}
              {page === 'budget' && <Budget {...props} />}
              {page === 'transactions' && <Transactions {...props} />}
              {page === 'recurring' && <Recurring {...props} />}
              {page === 'reports' && <Reports {...props} />}
              {page === 'accounts' && <Accounts {...props} />}
              {page === 'categories' && <Categories {...props} />}
              {page === 'installments' && <Installments {...props} />}
              {page === 'import' && <ImportPage {...props} />}
              {page === 'password' && <Password {...props} />}
            </>
          )}
        </div>
      </main>
    </div>
  );
}

/** Esqueleto com a forma do painel: a tela nao pula quando os dados chegam. */
function LoadingSkeleton() {
  return (
    <div className="skeleton" aria-busy="true" aria-label="Carregando">
      <div className="skeleton-block skeleton-title" />
      <div className="skeleton-row">
        <div className="skeleton-block" />
        <div className="skeleton-block" />
        <div className="skeleton-block" />
        <div className="skeleton-block" />
      </div>
      <div className="skeleton-block skeleton-large" />
    </div>
  );
}
