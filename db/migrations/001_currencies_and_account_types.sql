-- Nemus 001 — Moedas e tipos de conta.
--
-- A escala monetaria vive em currencies.decimal_places, nunca numa constante
-- global. Trocar BRL de centavos (2) para milliunits (3) e um UPDATE mais uma
-- reescala dos dados existentes, nao uma migration de schema.

CREATE TABLE currencies (
    code            CHAR(3)     PRIMARY KEY,
    name            TEXT        NOT NULL,
    symbol          TEXT        NOT NULL,
    decimal_places  SMALLINT    NOT NULL,
    is_active       BOOLEAN     NOT NULL DEFAULT TRUE,

    CONSTRAINT ck_currencies_code     CHECK (code ~ '^[A-Z]{3}$'),
    CONSTRAINT ck_currencies_decimals CHECK (decimal_places BETWEEN 0 AND 4),
    CONSTRAINT ck_currencies_name     CHECK (length(btrim(name)) > 0)
);

COMMENT ON TABLE  currencies IS 'Moedas ISO 4217 conhecidas pelo sistema.';
COMMENT ON COLUMN currencies.decimal_places IS
    'Casas decimais da menor unidade. BRL=2 (centavos). Todo valor monetario e '
    'BIGINT nesta escala. Jamais NUMERIC, jamais ponto flutuante.';

INSERT INTO currencies (code, name, symbol, decimal_places) VALUES
    ('BRL', 'Real brasileiro',  'R$',  2),
    ('USD', 'Dolar americano',  'US$', 2),
    ('EUR', 'Euro',             'EUR', 2);

-- ---------------------------------------------------------------------------

CREATE TABLE account_types (
    code            TEXT      PRIMARY KEY,
    display_name    TEXT      NOT NULL,
    normal_balance  CHAR(1)   NOT NULL,
    is_internal     BOOLEAN   NOT NULL,
    sort_order      SMALLINT  NOT NULL,

    CONSTRAINT ck_account_types_normal_balance CHECK (normal_balance IN ('D', 'C'))
);

COMMENT ON COLUMN account_types.is_internal IS
    'TRUE = patrimonio do usuario (ASSET/LIABILITY/EQUITY). FALSE = contraparte '
    'fora do perimetro (REVENUE/EXPENSE). A soma dos saldos internos e o inverso '
    'exato da soma dos externos; a soma de TODOS e sempre zero.';

INSERT INTO account_types (code, display_name, normal_balance, is_internal, sort_order) VALUES
    ('ASSET',     'Ativo',           'D', TRUE,  1),
    ('LIABILITY', 'Passivo',         'C', TRUE,  2),
    ('EQUITY',    'Patrimonio',      'C', TRUE,  3),
    ('REVENUE',   'Receita externa', 'C', FALSE, 4),
    ('EXPENSE',   'Despesa externa', 'D', FALSE, 5);
