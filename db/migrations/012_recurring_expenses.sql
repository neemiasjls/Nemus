-- Nemus 012 - Gastos fixos (fase 8).
--
-- O QUE E. A lista do que se repete todo mes: aluguel, internet, academia,
-- assinatura. E a metade que faltava do caderno de gastos - a outra metade,
-- "os que vou adicionando", o razao ja guarda desde a fase 1.
--
-- O QUE NAO E: LANCAMENTO. Gasto fixo nao vira transacao, nem sozinho no dia
-- do vencimento, nem nunca. A alternativa - postar a despesa automaticamente
-- - foi descartada por dois motivos concretos:
--
--   a) o saldo mentiria entre a data prevista e a data real. Aluguel lancado
--      no dia 10 e pago no dia 12 deixa a conta errada por dois dias;
--
--   b) quando o extrato OFX entrasse, o mesmo aluguel apareceria duas vezes,
--      e a importacao idempotente (pilar 3) nao teria como saber que a linha
--      do banco e a mesma coisa que o lancamento que o proprio app inventou.
--
-- O razao so pode conter o que aconteceu. Isto aqui e PREVISAO, e previsao
-- nao e fato. A tela responde "ja veio este mes?" olhando o razao - derivado,
-- nunca guardado, e por isso nunca desatualizado.
--
-- NAO HA COLUNA DE "PAGO". Seria exatamente o cache que a fase 4 recusou no
-- orcamento: um campo que diverge em silencio quando um lancamento antigo e
-- editado ou apagado, e que ninguem percebe estar errado.

CREATE TABLE recurring_expenses (
    id             UUID        PRIMARY KEY,
    name           TEXT        NOT NULL,
    category_id    UUID        NOT NULL,
    category_kind  TEXT        NOT NULL DEFAULT 'EXPENSE',
    amount         BIGINT      NOT NULL,
    currency_code  CHAR(3)     NOT NULL REFERENCES currencies (code),
    due_day        SMALLINT    NOT NULL,
    account_id     UUID        NULL REFERENCES accounts (id) ON DELETE SET NULL,

    -- Luz e agua mudam de valor todo mes; aluguel nao. A diferenca importa na
    -- hora de comparar o previsto com o que veio: para o estimado, divergir e
    -- o normal, e a tolerancia da comparacao e outra.
    is_estimate    BOOLEAN     NOT NULL DEFAULT FALSE,

    starts_on      DATE        NOT NULL,
    ends_on        DATE        NULL,
    archived_at    TIMESTAMPTZ NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT ck_recurring_name    CHECK (length(btrim(name)) BETWEEN 1 AND 120),
    CONSTRAINT ck_recurring_expense CHECK (category_kind = 'EXPENSE'),

    -- Positivo, ao contrario de budget_assignments. La o negativo e o coracao
    -- do metodo (tirar de um envelope para cobrir outro); aqui um gasto fixo
    -- negativo nao quer dizer nada.
    CONSTRAINT ck_recurring_amount  CHECK (amount > 0),

    -- Dia 31 existe: quem vence no ultimo dia do mes vence no dia 31 em marco
    -- e no 28 em fevereiro. Quem le a coluna e que aparara para o mes real -
    -- mesma convencao de credit_card_terms (002).
    CONSTRAINT ck_recurring_due_day CHECK (due_day BETWEEN 1 AND 31),

    CONSTRAINT ck_recurring_period  CHECK (ends_on IS NULL OR ends_on >= starts_on),

    -- Receita nao e gasto fixo. Salario que cai todo mes tambem se repete,
    -- mas vira dinheiro pronto para atribuir, nao envelope a encher.
    CONSTRAINT fk_recurring_category FOREIGN KEY (category_id, category_kind)
        REFERENCES categories (id, kind) ON DELETE CASCADE
);

COMMENT ON TABLE recurring_expenses IS
    'O que se repete todo mes. Previsao, nao lancamento: nada aqui toca saldo. Se ja veio no mes e derivado do razao, nunca gravado.';

COMMENT ON COLUMN recurring_expenses.amount IS
    'Quanto se espera pagar. Com is_estimate, e referencia e nao promessa - a tela pode sugerir a media dos ultimos meses no lugar.';

COMMENT ON COLUMN recurring_expenses.ends_on IS
    'Assinatura cancelada ganha ends_on em vez de sumir: o historico dos meses em que ela existiu continua explicavel.';

-- Cadastrar "Aluguel" duas vezes dobraria a previsao do mes em silencio, e a
-- previsao dobrada e pior que previsao nenhuma - ela parece certa. O indice e
-- parcial porque arquivado pode repetir nome: o antigo fica de historico.
CREATE UNIQUE INDEX ux_recurring_expenses_name
    ON recurring_expenses (lower(btrim(name)))
 WHERE archived_at IS NULL;

-- Sem indice de busca: sao dezenas de linhas, lidas inteiras de uma vez. Um
-- indice aqui seria custo de escrita para uma varredura que ja e instantanea.

-- ---------------------------------------------------------------------------
-- Mesmo controle de acesso do resto do schema (ver 008).

ALTER TABLE recurring_expenses ENABLE ROW LEVEL SECURITY;

DO $fn$
DECLARE
    v_role TEXT;
BEGIN
    FOREACH v_role IN ARRAY ARRAY['anon', 'authenticated'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = v_role) THEN
            EXECUTE format('REVOKE ALL ON recurring_expenses FROM %I', v_role);
        END IF;
    END LOOP;
END
$fn$;
