import { useState } from 'react';
import { Icon } from '../components/Icon';
import { EmptyState, PageHeader, Panel } from '../components/Panel';
import { api, type Account, type ImportPreview, type ImportResult } from '../lib/api';
import { formatDay, formatSigned } from '../lib/format';
import type { PageProps } from '../lib/types';

export function ImportPage({ data, reload, navigate }: PageProps) {
  const ownAccounts = data.accounts.filter((a) => a.isInternal && !a.isSystem);

  if (ownAccounts.length === 0) {
    return (
      <>
        <PageHeader title="Importar extrato" />
        <Panel>
          <EmptyState
            title="Crie uma conta antes."
            text="O extrato precisa cair em alguma conta do sistema."
            action={<button onClick={() => navigate('accounts')}>Criar conta</button>}
          />
        </Panel>
      </>
    );
  }

  return (
    <>
      <PageHeader title="Importar extrato" subtitle="O arquivo OFX do seu banco, sem digitar nada." />

      <div className="grid-import">
        <Panel title="Enviar arquivo">
          <ImportForm accounts={ownAccounts} onImported={reload} />
        </Panel>

        <Panel title="Como funciona">
          <ol className="steps">
            <li>
              <span>
                <strong>Baixe o extrato em OFX</strong> no aplicativo ou no internet banking.
                Quase todo banco oferece; às vezes o formato aparece com o nome de algum
                programa de finanças.
              </span>
            </li>
            <li>
              <span>
                <strong>Escolha a conta</strong> do sistema que recebe as linhas.
              </span>
            </li>
            <li>
              <span>
                <strong>Simule primeiro.</strong> Você vê o que entraria, sem gravar nada.
              </span>
            </li>
            <li>
              <span>
                <strong>Importe.</strong> Cada linha do extrato vira um lançamento.
              </span>
            </li>
          </ol>

          <div className="guarantee">
            <Icon name="check" size={16} />
            <p>
              <strong>Reimportar nunca duplica.</strong> Cada linha do banco tem identidade
              própria, e o sistema recusa a mesma identidade duas vezes. Baixou períodos que se
              sobrepõem? Pode importar os dois.
            </p>
          </div>
        </Panel>
      </div>
    </>
  );
}

function readableSize(bytes: number): string {
  return bytes < 1024 ? `${bytes} bytes` : `${Math.max(1, Math.round(bytes / 1024))} KB`;
}

function ImportForm({ accounts, onImported }: { accounts: Account[]; onImported: () => Promise<void> }) {
  const [accountId, setAccountId] = useState(accounts[0]?.id ?? '');
  const [file, setFile] = useState<File | null>(null);
  const [dragging, setDragging] = useState(false);
  const [preview, setPreview] = useState<ImportPreview | null>(null);
  const [result, setResult] = useState<ImportResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<'preview' | 'import' | null>(null);

  function select(next: File | null) {
    setFile(next);
    setPreview(null);
    setResult(null);
    setError(null);
  }

  async function send(dryRun: boolean) {
    if (!file) return;

    setError(null);
    setBusy(dryRun ? 'preview' : 'import');

    try {
      const response = await api.importOfx(accountId, file, dryRun);
      if (dryRun) {
        setPreview(response as ImportPreview);
        setResult(null);
      } else {
        setResult(response as ImportResult);
        setPreview(null);
        await onImported();
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Falha ao importar.');
    } finally {
      setBusy(null);
    }
  }

  const dropzoneClass = ['dropzone', file && 'has-file', dragging && 'dragging'].filter(Boolean).join(' ');

  return (
    <div className="form">
      <label className="field">
        <span>Conta que recebe o extrato</span>
        <select value={accountId} onChange={(e) => setAccountId(e.target.value)}>
          {accounts.map((a) => (
            <option key={a.id} value={a.id}>
              {a.name}
            </option>
          ))}
        </select>
      </label>

      <label
        className={dropzoneClass}
        onDragOver={(e) => {
          e.preventDefault();
          setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={(e) => {
          e.preventDefault();
          setDragging(false);
          select(e.dataTransfer.files[0] ?? null);
        }}
      >
        <input
          type="file"
          className="sr-only"
          accept=".ofx,.qfx"
          onChange={(e) => select(e.target.files?.[0] ?? null)}
        />
        <Icon name={file ? 'check' : 'file'} size={26} />
        <span className="dropzone-title">{file ? file.name : 'Arraste o arquivo OFX ou clique para escolher'}</span>
        <span className="dropzone-text">
          {file ? `${readableSize(file.size)} · clique para trocar` : 'Arquivos .ofx de até 4 MB'}
        </span>
      </label>

      <div className="button-row">
        <button
          type="button"
          className="secondary"
          disabled={!file || busy !== null}
          onClick={() => void send(true)}
        >
          {busy === 'preview' ? 'Simulando…' : 'Simular'}
        </button>
        <button type="button" disabled={!file || busy !== null} onClick={() => void send(false)}>
          {busy === 'import' ? 'Importando…' : 'Importar'}
        </button>
      </div>

      {error && (
        <p className="error" role="alert">
          {error}
        </p>
      )}

      {preview && (
        <div className="preview">
          <div className="preview-head">
            <span>
              <strong>{preview.transactionCount}</strong>{' '}
              {preview.transactionCount === 1 ? 'lançamento' : 'lançamentos'} no arquivo
            </span>
            {preview.startsOn && (
              <span className="hint">
                {formatDay(preview.startsOn)} a {preview.endsOn ? formatDay(preview.endsOn) : '…'}
              </span>
            )}
          </div>
          {preview.fileAlreadyImported && (
            <p className="warning">Este arquivo já foi importado. Importar de novo não duplica nada.</p>
          )}
          {preview.duplicatesWithinFile > 0 && (
            <p className="warning">
              {preview.duplicatesWithinFile} linha(s) repetida(s) dentro do próprio arquivo serão ignoradas.
            </p>
          )}
          <table>
            <tbody>
              {preview.sample.map((line, i) => (
                <tr key={`${line.occurredOn}-${i}`}>
                  <td className="date">{formatDay(line.occurredOn)}</td>
                  <td>{line.description}</td>
                  <td className={`amount ${line.amountMinorUnits < 0 ? 'debit' : 'credit'}`}>
                    {formatSigned(line.amountMinorUnits)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          {preview.transactionCount > preview.sample.length && (
            <p className="preview-more">…e mais {preview.transactionCount - preview.sample.length}.</p>
          )}
        </div>
      )}

      {result && (
        <div className="result" role="status">
          <Icon name="check" size={20} />
          <div>
            <strong>
              {result.inserted} {result.inserted === 1 ? 'lançamento importado' : 'lançamentos importados'}
            </strong>
            {result.skippedAsDuplicate > 0 && (
              <p>
                {result.skippedAsDuplicate} já estava(m) no sistema e foi(ram) ignorado(s) — é assim
                que a idempotência se parece.
              </p>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
