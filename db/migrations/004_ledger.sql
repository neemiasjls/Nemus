-- Nemus 004 - O razao: lotes de importacao, transacoes e lancamentos.

CREATE TABLE import_batches (
    id          UUID        PRIMARY KEY,
    source      TEXT        NOT NULL,
    file_sha256 TEXT        NULL,
    account_id  UUID        NULL REFERENCES accounts (id) ON DELETE SET NULL,
    imported_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    row_count   INT         NOT NULL DEFAULT 0,

    CONSTRAINT ck_import_batches_source CHECK (source IN ('MANUAL', 'OFX', 'PLUGGY')),
    CONSTRAINT ck_import_batches_sha    CHECK (file_sha256 IS NULL OR file_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_import_batches_rows   CHECK (row_count >= 0)
);

CREATE UNIQUE INDEX ux_import_batches_file
    ON import_batches (source, file_sha256) WHERE file_sha256 IS NOT NULL;

COMMENT ON TABLE import_batches IS
    'Idempotencia em duas camadas. Esta e a do arquivo (hash), atalho barato. A que garante correcao e a do item, em transactions.external_id.';

-- ---------------------------------------------------------------------------

CREATE TABLE transactions (
    id                 UUID        PRIMARY KEY,
    occurred_on        DATE        NOT NULL,
    booked_at          TIMESTAMPTZ NULL,
    description        TEXT        NOT NULL,
    payee_id           UUID        NULL REFERENCES payees (id) ON DELETE SET NULL,
    currency_code      CHAR(3)     NOT NULL REFERENCES currencies (code),
    kind               TEXT        NOT NULL,
    notes              TEXT        NULL,
    source             TEXT        NOT NULL,
    source_account_ref TEXT        NULL,
    external_id        TEXT        NULL,
    import_batch_id    UUID        NULL REFERENCES import_batches (id) ON DELETE SET NULL,
    created_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
    deleted_at         TIMESTAMPTZ NULL,

    CONSTRAINT ck_tx_kind CHECK (kind IN (
        'STANDARD', 'TRANSFER', 'OPENING_BALANCE', 'INSTALLMENT_PURCHASE', 'CARD_PAYMENT')),
    CONSTRAINT ck_tx_source      CHECK (source IN ('MANUAL', 'OFX', 'PLUGGY')),
    CONSTRAINT ck_tx_description CHECK (length(btrim(description)) > 0),

    -- Tudo que veio de fora obrigatoriamente carrega identidade externa,
    -- senao a reimportacao nao tem como se reconhecer.
    CONSTRAINT ck_tx_external_required CHECK (source = 'MANUAL' OR external_id IS NOT NULL),

    -- Alvo da FK composta de entries: trava a moeda do lancamento na moeda
    -- da transacao.
    CONSTRAINT uq_tx_id_currency UNIQUE (id, currency_code)
);

COMMENT ON COLUMN transactions.occurred_on IS
    'Competencia: quando o fato economico aconteceu. E a data que o orcamento usa.';
COMMENT ON COLUMN transactions.booked_at IS
    'Liquidacao no banco. Difere de occurred_on em compra com cartao, TED agendada e PIX de fim de semana.';

-- PILAR 3 - IDEMPOTENCIA.
-- O FITID do OFX so e unico dentro de uma conta de uma instituicao; dois
-- bancos podem emitir o mesmo. Por isso a chave inclui a conta de origem.
-- Repare que NAO ha filtro por deleted_at: se voce apagou uma transacao
-- importada de proposito, reimportar o arquivo deve continuar nao a
-- recriando. Excluir soft-deleted do indice reviveria o que voce descartou.
CREATE UNIQUE INDEX ux_transactions_idempotency
    ON transactions (source, COALESCE(source_account_ref, ''), external_id)
    WHERE external_id IS NOT NULL;

CREATE INDEX ix_transactions_occurred_on ON transactions (occurred_on)
    WHERE deleted_at IS NULL;
CREATE INDEX ix_transactions_payee ON transactions (payee_id)
    WHERE payee_id IS NOT NULL AND deleted_at IS NULL;
CREATE INDEX ix_transactions_batch ON transactions (import_batch_id)
    WHERE import_batch_id IS NOT NULL;

-- ---------------------------------------------------------------------------
-- Lancamentos: as pernas. N >= 2, somando exatamente zero.
--
-- PILAR 2 - amount e BIGINT na escala de currencies.decimal_places.
-- Nunca NUMERIC, nunca DOUBLE PRECISION, nunca REAL.

CREATE TABLE entries (
    id             UUID     PRIMARY KEY,
    transaction_id UUID     NOT NULL,
    account_id     UUID     NOT NULL,
    currency_code  CHAR(3)  NOT NULL,
    amount         BIGINT   NOT NULL,
    category_id    UUID     NULL REFERENCES categories (id) ON DELETE SET NULL,
    memo           TEXT     NULL,
    sort_order     SMALLINT NOT NULL,

    CONSTRAINT ck_entries_amount_nonzero CHECK (amount <> 0),
    CONSTRAINT ck_entries_sort_order     CHECK (sort_order >= 0),
    CONSTRAINT uq_entries_tx_sort        UNIQUE (transaction_id, sort_order),

    -- As duas FKs compostas abaixo tornam estruturalmente impossivel um
    -- lancamento em moeda diferente da sua transacao ou da sua conta.
    -- Nenhum trigger faz isso melhor do que uma FK.
    CONSTRAINT fk_entries_transaction FOREIGN KEY (transaction_id, currency_code)
        REFERENCES transactions (id, currency_code) ON DELETE CASCADE,
    CONSTRAINT fk_entries_account FOREIGN KEY (account_id, currency_code)
        REFERENCES accounts (id, currency_code) ON DELETE RESTRICT
);

COMMENT ON COLUMN entries.amount IS
    'Sinalizado. Negativo = saiu desta conta, positivo = entrou. A soma das pernas de uma transacao e sempre zero.';
COMMENT ON COLUMN entries.category_id IS
    'Categoria vive na perna, nao na transacao: e o que permite compra dividida (fase 2) com uma linha so de extrato.';

CREATE INDEX ix_entries_account     ON entries (account_id) INCLUDE (amount);
CREATE INDEX ix_entries_transaction ON entries (transaction_id);
CREATE INDEX ix_entries_category    ON entries (category_id) WHERE category_id IS NOT NULL;
