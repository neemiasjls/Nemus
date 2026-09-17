import { useState, type FormEvent } from 'react';
import { Icon } from '../components/Icon';
import { PageHeader, Panel } from '../components/Panel';
import { api, ApiError, setToken } from '../lib/api';
import { formatDay } from '../lib/format';
import type { PageProps } from '../lib/types';

/**
 * Trocar a senha. Duas coisas acontecem juntas no servidor, e a tela diz as
 * duas: a senha nova passa a valer e TODAS as sessoes abertas caem - inclusive
 * as de outros aparelhos. Esta aba ganha uma sessao nova na mesma resposta.
 */
export function Password({ data, reload }: PageProps) {
  const [current, setCurrent] = useState('');
  const [next, setNext] = useState('');
  const [confirmation, setConfirmation] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);
  const [saving, setSaving] = useState(false);

  async function submit(event: FormEvent) {
    event.preventDefault();
    setError(null);
    setDone(false);

    if (next !== confirmation) {
      setError('As duas senhas novas não são iguais.');
      return;
    }

    setSaving(true);

    try {
      const result = await api.changePassword(current, next);
      setToken(result.token);
      setCurrent('');
      setNext('');
      setConfirmation('');
      setDone(true);
      await reload();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Não foi possível trocar a senha.');
    } finally {
      setSaving(false);
    }
  }

  const session = data.session;

  return (
    <>
      <PageHeader
        title="Trocar senha"
        subtitle={
          session
            ? `Entrou como ${session.username}${session.lastLoginAt ? ` · último acesso em ${formatDay(session.lastLoginAt.slice(0, 10))}` : ''}`
            : 'Seu acesso a este Nemus.'
        }
      />

      <div className="grid-2 grid-aside">
        <Panel title="Nova senha">
          <form className="form" onSubmit={submit}>
            <label className="field">
              <span>Senha atual</span>
              <input
                type="password"
                value={current}
                onChange={(e) => setCurrent(e.target.value)}
                autoComplete="current-password"
                required
              />
            </label>

            <div className="fields">
              <label className="field">
                <span>Nova senha</span>
                <input
                  type="password"
                  value={next}
                  onChange={(e) => setNext(e.target.value)}
                  autoComplete="new-password"
                  required
                />
              </label>
              <label className="field">
                <span>Repita a nova</span>
                <input
                  type="password"
                  value={confirmation}
                  onChange={(e) => setConfirmation(e.target.value)}
                  autoComplete="new-password"
                  required
                />
              </label>
            </div>

            {error && (
              <p className="error" role="alert">
                {error}
              </p>
            )}

            {done && (
              <p className="callout callout-ok" role="status">
                <Icon name="check" size={16} />
                <span>Senha trocada. As outras sessões foram encerradas; esta continua valendo.</span>
              </p>
            )}

            <div className="form-footer">
              <p className="note">
                Pelo menos 10 caracteres. Uma frase que você lembra protege mais do que uma palavra
                curta cheia de símbolos.
              </p>
              <button type="submit" disabled={saving}>
                {saving ? 'Trocando…' : 'Trocar senha'}
              </button>
            </div>
          </form>
        </Panel>

        <Panel title="Como o acesso funciona">
          <ul className="steps">
            <li>
              <strong>A senha não é guardada.</strong> O servidor grava só um hash lento dela, com
              sal próprio. Nem quem tem o banco inteiro consegue lê-la de volta.
            </li>
            <li>
              <strong>A sessão vence em 30 dias</strong> e pode ser derrubada a qualquer momento —
              por “Sair”, por esta troca de senha, ou direto no banco.
            </li>
            <li>
              <strong>Errar a senha várias vezes trava</strong> por 15 minutos, por usuário e origem.
              Acertar zera a contagem na hora.
            </li>
            <li>
              <strong>Esqueceu a senha?</strong> Não há e-mail de recuperação neste app. Quem tem
              acesso ao banco define outra com <code>dotnet run --project src/Nemus.MigrationTool -- user password SEU_USUARIO</code>.
            </li>
          </ul>
        </Panel>
      </div>
    </>
  );
}
