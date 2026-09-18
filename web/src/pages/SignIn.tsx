import { useEffect, useState, type FormEvent } from 'react';
import { Icon, Logo } from '../components/Icon';
import { ThemeSwitcher } from '../components/ThemeSwitcher';
import { api, ApiError, clearToken, setToken } from '../lib/api';

type Mode = 'checking' | 'login' | 'first-access' | 'offline';

/**
 * Entrada do app: usuario e senha.
 *
 * A tela pergunta antes se o Nemus ja tem dono. Se ainda nao tiver, ela vira
 * o formulario de primeiro acesso - que pede tambem o token de instalacao,
 * para quem achar a URL antes de voce nao virar o dono do seu razao.
 */
export function SignIn({ onSignIn }: { onSignIn: () => void }) {
  const [mode, setMode] = useState<Mode>('checking');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [bootstrapToken, setBootstrapToken] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [working, setWorking] = useState(false);

  useEffect(() => {
    let cancelled = false;

    api
      .authStatus()
      .then((status) => {
        if (!cancelled) setMode(status.needsFirstAccess ? 'first-access' : 'login');
      })
      .catch(() => {
        if (!cancelled) setMode('offline');
      });

    return () => {
      cancelled = true;
    };
  }, []);

  const firstAccess = mode === 'first-access';

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);

    if (firstAccess && password !== confirmation) {
      setError('As duas senhas não são iguais.');
      return;
    }

    setWorking(true);

    try {
      const result = firstAccess
        ? await api.firstAccess(username, password, bootstrapToken)
        : await api.login(username, password);

      setToken(result.token);
      onSignIn();
    } catch (e) {
      clearToken();
      setError(
        e instanceof ApiError
          ? e.message
          : 'Não foi possível falar com a API. No plano gratuito ela dorme quando fica parada; tente de novo em alguns segundos.',
      );
      setWorking(false);
    }
  }

  return (
    <div className="sign-in">
      <aside className="sign-in-brand">
        <div className="brand">
          <Logo size={34} />
          <span className="wordmark">Nemus</span>
        </div>

        <div>
          <h1 className="sign-in-headline">
            Cada real,
            <br />
            nos dois lados
            <br />
            do <em>livro</em>.
          </h1>
          <p className="sign-in-text">
            Razão de partidas dobradas para finanças pessoais. Nenhum centavo entra ou sai
            do nada — e o sistema prova isso a cada lançamento.
          </p>
        </div>

        <SampleLedger />

        <ul className="principles">
          <li>
            <Icon name="check" size={14} />
            Dinheiro sempre inteiro, em centavos
          </li>
          <li>
            <Icon name="check" size={14} />
            Extrato importado sem duplicar
          </li>
          <li>
            <Icon name="check" size={14} />
            Todo real com uma categoria
          </li>
        </ul>
      </aside>

      <main className="sign-in-side">
        <div className="sign-in-theme">
          <ThemeSwitcher />
        </div>

        <form className="sign-in-card" onSubmit={submit}>
          <div className="brand mobile-only">
            <Logo size={30} />
            <span className="wordmark">Nemus</span>
          </div>

          <div>
            <h2>{firstAccess ? 'Primeiro acesso' : 'Entrar'}</h2>
            <p className="lead">
              {mode === 'checking' && 'Falando com a API…'}
              {mode === 'offline' && 'A API não respondeu. Confira se ela está no ar e recarregue a página.'}
              {mode === 'login' && 'Use o usuário e a senha que você criou.'}
              {firstAccess && 'Este Nemus ainda não tem dono. Escolha o usuário e a senha que vão ser seus.'}
            </p>
          </div>

          <label className="field">
            <span>Usuário</span>
            <input
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              autoComplete="username"
              spellCheck={false}
              autoCapitalize="none"
              disabled={mode === 'checking' || mode === 'offline'}
              placeholder="letras minúsculas, sem espaço"
              autoFocus
            />
          </label>

          <label className="field">
            <span>Senha</span>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete={firstAccess ? 'new-password' : 'current-password'}
              disabled={mode === 'checking' || mode === 'offline'}
            />
          </label>

          {firstAccess && (
            <>
              <label className="field">
                <span>Repita a senha</span>
                <input
                  type="password"
                  value={confirmation}
                  onChange={(e) => setConfirmation(e.target.value)}
                  autoComplete="new-password"
                />
              </label>

              <label className="field">
                <span>Token de instalação</span>
                <input
                  type="password"
                  value={bootstrapToken}
                  onChange={(e) => setBootstrapToken(e.target.value)}
                  autoComplete="off"
                  spellCheck={false}
                />
                <span className="note">
                  É o valor de <code>NEMUS_BOOTSTRAP_TOKEN</code> na configuração do servidor. Só é
                  pedido desta vez: depois que o acesso existir, esta tela não volta.
                </span>
              </label>
            </>
          )}

          {error && (
            <p className="error" role="alert">
              {error}
            </p>
          )}

          <button
            type="submit"
            className="wide"
            disabled={working || mode === 'checking' || mode === 'offline' || username.trim() === '' || password === ''}
          >
            {working ? 'Verificando…' : firstAccess ? 'Criar acesso e entrar' : 'Entrar'}
          </button>

          <p className="note">
            {firstAccess
              ? 'A senha é guardada só como hash — nem o servidor consegue lê-la de volta. Prefira uma frase longa a uma palavra com símbolos.'
              : 'A sessão vale por 30 dias neste navegador, e “Sair” encerra na hora.'}
          </p>
        </form>
      </main>
    </div>
  );
}

/** Um lancamento de exemplo, fechado em zero: o produto explicado numa imagem. */
function SampleLedger() {
  return (
    <div className="sample-t" aria-hidden="true">
      <div className="sample-t-head">
        <span>Mercado da esquina</span>
        <span className="date">08 set</span>
      </div>
      <div className="t-account">
        <div className="t-account-side">
          <div className="t-account-title">Saiu de</div>
          <div className="leg">
            <span className="leg-account">Conta corrente</span>
            <span className="leg-amount">187,40</span>
          </div>
        </div>
        <div className="t-account-side">
          <div className="t-account-title">Entrou em</div>
          <div className="leg">
            <span className="leg-account">Alimentação</span>
            <span className="leg-amount">187,40</span>
          </div>
        </div>
      </div>
      <div className="sample-t-footer">
        <span>2 partidas</span>
        <span>
          soma <strong className="credit">0,00</strong>
        </span>
      </div>
    </div>
  );
}
