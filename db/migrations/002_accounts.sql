-- Nemus 002 - Contas e condicoes de cartao de credito.

CREATE TABLE accounts (
    id             UUID        PRIMARY KEY,
    name           TEXT        NOT NULL,
    type_code      TEXT        NOT NULL REFERENCES account_types (code),
    currency_code  CHAR(3)     NOT NULL REFERENCES currencies (code),
    institution    TEXT        NULL,
    is_on_budget   BOOLEAN     NOT NULL DEFAULT TRUE,
    is_system      BOOLEAN     NOT NULL DEFAULT FALSE,
    external_ref   TEXT        NULL,
    is_archived    BOOLEAN     NOT NULL DEFAULT FALSE,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT ck_accounts_name CHECK (length(btrim(name)) > 0),

    -- Alvos das FKs compostas de entries e credit_card_terms. Sao indices
    -- redundantes com a PK; e o preco de amarrar moeda e tipo de forma
    -- declarativa, em vez de por trigger.
    CONSTRAINT uq_accounts_id_currency UNIQUE (id, currency_code),
    CONSTRAINT uq_accounts_id_type     UNIQUE (id, type_code)
);

COMMENT ON COLUMN accounts.is_on_budget IS
    'Fase 4. Conta cujo saldo alimenta o pronto-para-atribuir do orcamento. Investimento e emprestimo costumam ficar fora.';

COMMENT ON COLUMN accounts.external_ref IS
    'Identidade da conta na origem: ACCTID do OFX ou accountId do Pluggy. Compoe a chave de idempotencia junto de transactions.external_id.';

CREATE UNIQUE INDEX ux_accounts_name
    ON accounts (lower(btrim(name)), type_code) WHERE NOT is_archived;

CREATE UNIQUE INDEX ux_accounts_external_ref
    ON accounts (external_ref) WHERE external_ref IS NOT NULL;

CREATE INDEX ix_accounts_type ON accounts (type_code) WHERE NOT is_archived;

-- ---------------------------------------------------------------------------
-- Contas de sistema. UUIDs fixos: o dominio e os testes dependem deles.
-- Formato v7-valido (nibble de versao 7, variante 8) por consistencia.

INSERT INTO accounts (id, name, type_code, currency_code, is_system, is_on_budget) VALUES
    ('00000000-0000-7000-8000-000000000001', 'Saldos iniciais',       'EQUITY',  'BRL', TRUE, FALSE),
    ('00000000-0000-7000-8000-000000000002', 'Despesas externas',     'EXPENSE', 'BRL', TRUE, FALSE),
    ('00000000-0000-7000-8000-000000000003', 'Receitas externas',     'REVENUE', 'BRL', TRUE, FALSE),
    ('00000000-0000-7000-8000-000000000004', 'Ajuste de conciliacao', 'EQUITY',  'BRL', TRUE, FALSE);

-- ---------------------------------------------------------------------------
-- Condicoes de cartao. Tabela separada porque so se aplica a LIABILITY;
-- colunas nulaveis em accounts viram lixo. A coluna account_type existe
-- apenas para que a FK composta prove, de forma declarativa, que o cartao
-- e mesmo um passivo.

CREATE TABLE credit_card_terms (
    account_id         UUID     PRIMARY KEY,
    account_type       TEXT     NOT NULL DEFAULT 'LIABILITY',
    closing_day        SMALLINT NOT NULL,
    due_day            SMALLINT NOT NULL,
    credit_limit       BIGINT   NULL,
    payment_account_id UUID     NULL REFERENCES accounts (id) ON DELETE SET NULL,

    CONSTRAINT ck_cct_is_liability CHECK (account_type = 'LIABILITY'),
    CONSTRAINT ck_cct_closing_day  CHECK (closing_day BETWEEN 1 AND 31),
    CONSTRAINT ck_cct_due_day      CHECK (due_day     BETWEEN 1 AND 31),
    CONSTRAINT ck_cct_limit        CHECK (credit_limit IS NULL OR credit_limit >= 0),

    CONSTRAINT fk_cct_account FOREIGN KEY (account_id, account_type)
        REFERENCES accounts (id, type_code) ON DELETE CASCADE
);

COMMENT ON TABLE credit_card_terms IS
    'Fechamento e vencimento definem em qual fatura uma compra ou parcela cai. Sem isto o parcelamento da fase 6 nao tem como decidir a competencia.';
