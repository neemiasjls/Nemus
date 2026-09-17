-- Nemus 006 - Parcelamento de cartao. Estrutura criada agora, usada na fase 6.
--
-- Um "12x sem juros" de R$ 1.200 tem duas verdades simultaneas:
--
--   contabil     no instante da compra voce ja deve R$ 1.200 ao cartao.
--                O passivo e integral e imediato. Isso e purchase_tx_id:
--                UMA transacao, -120000 na conta do cartao.
--
--   orcamentaria o compromisso consome R$ 100 do orcamento em cada um dos
--                12 meses. Isso e a tabela installments: um cronograma.
--
-- Sao dimensoes diferentes, e por isso parcelamento nao cabe so no razao.
-- O banco vai mandar as 12 linhas mesmo assim (ou 1, ou 12 com descricao
-- "IFOOD 03/12" - varia por instituicao). Por isso cada PARCELA tem seu
-- proprio external_id: o importador reconcilia a linha recebida contra a
-- parcela ja prevista, em vez de criar transacao nova.

CREATE TABLE installment_plans (
    id                    UUID        PRIMARY KEY,
    card_account_id       UUID        NOT NULL,
    card_account_type     TEXT        NOT NULL DEFAULT 'LIABILITY',
    purchase_tx_id        UUID        NOT NULL REFERENCES transactions (id) ON DELETE CASCADE,
    payee_id              UUID        NULL REFERENCES payees (id) ON DELETE SET NULL,
    description           TEXT        NOT NULL,
    currency_code         CHAR(3)     NOT NULL REFERENCES currencies (code),
    total_amount          BIGINT      NOT NULL,
    financed_amount       BIGINT      NOT NULL,
    interest_amount       BIGINT      GENERATED ALWAYS AS (financed_amount - total_amount) STORED,
    installment_count     SMALLINT    NOT NULL,
    purchase_date         DATE        NOT NULL,
    first_statement_month DATE        NOT NULL,
    source                TEXT        NOT NULL DEFAULT 'MANUAL',
    external_id           TEXT        NULL,
    created_at            TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT ck_plans_total       CHECK (total_amount > 0),
    CONSTRAINT ck_plans_financed    CHECK (financed_amount >= total_amount),
    CONSTRAINT ck_plans_count       CHECK (installment_count BETWEEN 1 AND 99),
    CONSTRAINT ck_plans_description CHECK (length(btrim(description)) > 0),
    CONSTRAINT ck_plans_source      CHECK (source IN ('MANUAL', 'OFX', 'PLUGGY')),
    CONSTRAINT ck_plans_is_card     CHECK (card_account_type = 'LIABILITY'),

    -- Competencia e sempre o dia 1: fatura e mes, nao data.
    CONSTRAINT ck_plans_first_month CHECK (EXTRACT(DAY FROM first_statement_month) = 1),

    CONSTRAINT fk_plans_card FOREIGN KEY (card_account_id, card_account_type)
        REFERENCES accounts (id, type_code) ON DELETE RESTRICT
);

CREATE UNIQUE INDEX ux_plans_idempotency
    ON installment_plans (source, external_id) WHERE external_id IS NOT NULL;

CREATE INDEX ix_plans_card ON installment_plans (card_account_id);

COMMENT ON COLUMN installment_plans.total_amount IS
    'Preco a vista da compra.';
COMMENT ON COLUMN installment_plans.financed_amount IS
    'Soma exata das parcelas. Igual a total_amount quando e sem juros; maior quando ha juros embutidos.';

-- ---------------------------------------------------------------------------

CREATE TABLE installments (
    id              UUID     PRIMARY KEY,
    plan_id         UUID     NOT NULL REFERENCES installment_plans (id) ON DELETE CASCADE,
    sequence        SMALLINT NOT NULL,
    amount          BIGINT   NOT NULL,
    statement_month DATE     NOT NULL,
    due_date        DATE     NOT NULL,
    settled_tx_id   UUID     NULL REFERENCES transactions (id) ON DELETE SET NULL,
    source          TEXT     NOT NULL DEFAULT 'MANUAL',
    external_id     TEXT     NULL,

    CONSTRAINT ck_installments_amount   CHECK (amount > 0),
    CONSTRAINT ck_installments_sequence CHECK (sequence >= 1),
    CONSTRAINT ck_installments_source   CHECK (source IN ('MANUAL', 'OFX', 'PLUGGY')),
    CONSTRAINT ck_installments_month    CHECK (EXTRACT(DAY FROM statement_month) = 1),
    CONSTRAINT uq_installments_seq      UNIQUE (plan_id, sequence)
);

CREATE UNIQUE INDEX ux_installments_idempotency
    ON installments (source, external_id) WHERE external_id IS NOT NULL;

CREATE INDEX ix_installments_month ON installments (statement_month);

COMMENT ON COLUMN installments.settled_tx_id IS
    'Preenchido quando a parcela aparece na fatura fechada e e reconciliada. NULL = ainda e compromisso futuro.';

-- ---------------------------------------------------------------------------
-- A soma das parcelas e exatamente o valor financiado. R$ 100 em 3x sao
-- 33,33 + 33,33 + 33,34 - o centavo residual tem que existir em algum lugar,
-- e nunca pode sumir. Diferido para permitir inserir plano e parcelas na
-- mesma transacao SQL.

CREATE OR REPLACE FUNCTION nemus_check_installment_plan(p_plan_id UUID) RETURNS VOID
LANGUAGE plpgsql AS $fn$
DECLARE
    v_expected_total BIGINT;
    v_expected_count SMALLINT;
    v_actual_total   BIGINT;
    v_actual_count   INT;
BEGIN
    SELECT financed_amount, installment_count
      INTO v_expected_total, v_expected_count
      FROM installment_plans WHERE id = p_plan_id;

    IF NOT FOUND THEN
        RETURN;
    END IF;

    SELECT COALESCE(SUM(amount), 0), COUNT(*)
      INTO v_actual_total, v_actual_count
      FROM installments WHERE plan_id = p_plan_id;

    IF v_actual_count <> v_expected_count THEN
        RAISE EXCEPTION
            'Plano % declara % parcelas mas tem % cadastradas.',
            p_plan_id, v_expected_count, v_actual_count
            USING ERRCODE = 'NM020';
    END IF;

    IF v_actual_total <> v_expected_total THEN
        RAISE EXCEPTION
            'Plano %: soma das parcelas e %, deveria ser %. Diferenca de %.',
            p_plan_id, v_actual_total, v_expected_total, v_actual_total - v_expected_total
            USING ERRCODE = 'NM021';
    END IF;
END;
$fn$;

CREATE OR REPLACE FUNCTION nemus_installments_guard() RETURNS TRIGGER
LANGUAGE plpgsql AS $fn$
BEGIN
    IF TG_OP = 'DELETE' THEN
        PERFORM nemus_check_installment_plan(OLD.plan_id);
    ELSE
        PERFORM nemus_check_installment_plan(NEW.plan_id);
        IF TG_OP = 'UPDATE' AND OLD.plan_id <> NEW.plan_id THEN
            PERFORM nemus_check_installment_plan(OLD.plan_id);
        END IF;
    END IF;
    RETURN NULL;
END;
$fn$;

CREATE CONSTRAINT TRIGGER trg_installments_sum
    AFTER INSERT OR UPDATE OR DELETE ON installments
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION nemus_installments_guard();

CREATE OR REPLACE FUNCTION nemus_plans_guard() RETURNS TRIGGER
LANGUAGE plpgsql AS $fn$
BEGIN
    PERFORM nemus_check_installment_plan(NEW.id);
    RETURN NULL;
END;
$fn$;

CREATE CONSTRAINT TRIGGER trg_plans_sum
    AFTER INSERT OR UPDATE ON installment_plans
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION nemus_plans_guard();
