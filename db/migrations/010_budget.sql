-- Nemus 010 - Orcamento mensal por envelope (fase 4).
--
-- O METODO. Todo real que existe nas contas do orcamento esta ou dentro de um
-- envelope (categoria de despesa), ou esperando para ser atribuido. Nao ha
-- terceiro lugar. Sobra e estouro rolam para o mes seguinte.
--
-- SO A ATRIBUICAO E GUARDADA. Atividade vem do razao e disponivel e soma
-- acumulada, ambos calculados nas visoes abaixo. Materializar o disponivel
-- criaria um cache que diverge em silencio quando uma transacao antiga e
-- editada: os meses seguintes ficariam errados sem erro nenhum aparecer.
--
-- Como o estouro tambem rola, disponivel e uma soma acumulada simples - uma
-- funcao de janela, sem recursao mes a mes.

-- ---------------------------------------------------------------------------
-- 1. A unica tabela.

-- Alvo da FK composta abaixo: prova, de forma declarativa, que so categoria
-- de despesa recebe atribuicao. Receita nao entra em envelope - ela vira
-- dinheiro pronto para atribuir. Mesmo truque de credit_card_terms (002).
ALTER TABLE categories
    ADD CONSTRAINT uq_categories_id_kind UNIQUE (id, kind);

CREATE TABLE budget_assignments (
    id             UUID        PRIMARY KEY,
    category_id    UUID        NOT NULL,
    category_kind  TEXT        NOT NULL DEFAULT 'EXPENSE',
    month          DATE        NOT NULL,
    amount         BIGINT      NOT NULL,
    currency_code  CHAR(3)     NOT NULL REFERENCES currencies (code),
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),

    -- Orcamento e por mes, nao por data: o mes e sempre o dia 1.
    CONSTRAINT ck_budget_assignments_month   CHECK (EXTRACT(DAY FROM month) = 1),
    CONSTRAINT ck_budget_assignments_expense CHECK (category_kind = 'EXPENSE'),

    -- Uma atribuicao por categoria e mes. Mudar o valor e UPDATE, nao linha nova.
    CONSTRAINT uq_budget_assignments_category_month UNIQUE (category_id, month),

    -- Categoria de receita nao consegue receber atribuicao, e uma categoria
    -- com atribuicao nao consegue virar receita: a FK barra os dois.
    CONSTRAINT fk_budget_assignments_category FOREIGN KEY (category_id, category_kind)
        REFERENCES categories (id, kind) ON DELETE CASCADE
);

COMMENT ON COLUMN budget_assignments.amount IS
    'Sem CHECK de positivo, de proposito: tirar dinheiro de um envelope para cobrir o estouro de outro e o coracao do metodo.';

-- ---------------------------------------------------------------------------
-- 2. O que o orcamento enxerga do razao.
--
-- Uma definicao so, usada pelas duas visoes seguintes. Tres regras:
--
--   a) So entra transacao que toca alguma conta do orcamento (ativo ou
--      passivo com is_on_budget). Despesa paga direto de um investimento
--      fora do orcamento nao consome envelope - o dinheiro nunca esteve
--      no orcamento.
--
--   b) A categoria conta na perna da conta EXTERNA, onde o frontend e a
--      importacao a colocam. Ali o sinal ja e o do orcamento: gasto de
--      R$ 45,90 e +4590 na conta de despesa. Estorno entra negativo e
--      recompoe o envelope sem caso especial. Reembolso recebido e
--      categorizado na propria categoria de despesa (perna na conta de
--      receita, negativa) tambem recompoe.
--
--   c) Perna na conta de despesa sem categoria de despesa e dinheiro que
--      saiu do orcamento sem envelope. Nao some: reduz o pronto para
--      atribuir, e v_budget_integrity conta quantas ha. Dinheiro sem
--      categoria tem que incomodar, senao o orcamento vira decoracao.

CREATE VIEW v_budget_entries
WITH (security_invoker = true)
AS
WITH on_budget_accounts AS (
    SELECT id
      FROM accounts
     WHERE is_on_budget
       AND type_code IN ('ASSET', 'LIABILITY')
),
budget_transactions AS (
    SELECT DISTINCT e.transaction_id
      FROM entries e
      JOIN on_budget_accounts ob ON ob.id = e.account_id
)
SELECT e.id                                                   AS entry_id,
       e.transaction_id,
       e.currency_code,
       date_trunc('month', t.occurred_on::timestamp)::date    AS month,
       e.amount,
       (ob.id IS NOT NULL)                                    AS is_on_budget_account,
       CASE WHEN NOT ty.is_internal AND c.kind = 'EXPENSE'
            THEN e.category_id END                            AS envelope_id,
       (a.type_code = 'EXPENSE' AND c.kind IS DISTINCT FROM 'EXPENSE')
                                                              AS is_uncategorized_spending
  FROM entries e
  JOIN budget_transactions bt ON bt.transaction_id = e.transaction_id
  JOIN transactions t         ON t.id = e.transaction_id
  JOIN accounts a             ON a.id = e.account_id
  JOIN account_types ty       ON ty.code = a.type_code
  LEFT JOIN on_budget_accounts ob ON ob.id = e.account_id
  LEFT JOIN categories c      ON c.id = e.category_id
 WHERE t.deleted_at IS NULL;

COMMENT ON VIEW v_budget_entries IS
    'Pernas das transacoes que tocam o orcamento, com o mes de competencia (occurred_on). envelope_id so e preenchido em perna externa com categoria de despesa.';

-- ---------------------------------------------------------------------------
-- 3. Envelopes.
--
--   atividade(C, M)  = menos o que saiu de C em M
--   disponivel(C, M) = soma, de todos os meses ate M, de atribuido + atividade
--
-- Esparsa: so tem linha onde houve atribuicao ou movimento. O disponivel de
-- um mes sem linha e o da ultima linha anterior - quem le pega a mais
-- recente com month <= M. Gerar a grade densa de categoria x mes daria o
-- mesmo resultado com muito mais linhas e uma data de corte arbitraria.

CREATE VIEW v_budget_months
WITH (security_invoker = true)
AS
WITH facts AS (
    SELECT category_id, currency_code, month, amount AS assigned, 0::BIGINT AS spent
      FROM budget_assignments
    UNION ALL
    SELECT envelope_id, currency_code, month, 0::BIGINT, amount
      FROM v_budget_entries
     WHERE envelope_id IS NOT NULL
),
monthly AS (
    SELECT category_id, currency_code, month,
           SUM(assigned)::BIGINT AS assigned,
           SUM(spent)::BIGINT    AS spent
      FROM facts
     GROUP BY category_id, currency_code, month
)
SELECT category_id,
       currency_code,
       month,
       assigned,
       (-spent)::BIGINT AS activity,
       SUM(assigned - spent) OVER (
           PARTITION BY category_id, currency_code ORDER BY month)::BIGINT AS available
  FROM monthly;

COMMENT ON VIEW v_budget_months IS
    'Por categoria e mes: atribuido, atividade (negativa quando se gasta) e disponivel acumulado. Estouro rola negativo ate ser coberto.';

-- ---------------------------------------------------------------------------
-- 4. Pronto para atribuir.
--
-- Calculado pelo lado das ENTRADAS, e nao como "saldo menos envelopes". Se
-- fosse saldo menos envelopes, a verificacao da secao 5 seria verdadeira por
-- definicao e nao verificaria nada.
--
--   entrada liquida(M) = pernas nas contas do orcamento + pernas de envelope
--
-- Numa despesa categorizada as duas se anulam (-100 na conta, +100 no
-- envelope). Sobra o que entrou sem envelope: receita, saldo inicial,
-- transferencia vinda de fora do orcamento. E, com sinal negativo, o que
-- saiu sem categoria ou foi para fora do orcamento.
--
--   pronto(M) = soma, de todos os meses ate M, de entrada liquida - atribuido

CREATE VIEW v_budget_ready_to_assign
WITH (security_invoker = true)
AS
WITH facts AS (
    SELECT currency_code, month, amount AS net_inflow, 0::BIGINT AS assigned
      FROM v_budget_entries
     WHERE is_on_budget_account OR envelope_id IS NOT NULL
    UNION ALL
    SELECT currency_code, month, 0::BIGINT, amount
      FROM budget_assignments
),
monthly AS (
    SELECT currency_code, month,
           SUM(net_inflow)::BIGINT AS net_inflow,
           SUM(assigned)::BIGINT   AS assigned
      FROM facts
     GROUP BY currency_code, month
)
SELECT currency_code,
       month,
       net_inflow,
       assigned,
       SUM(net_inflow - assigned) OVER (
           PARTITION BY currency_code ORDER BY month)::BIGINT AS ready_to_assign
  FROM monthly;

COMMENT ON VIEW v_budget_ready_to_assign IS
    'Por mes: entrada liquida sem envelope, total atribuido e o pronto para atribuir acumulado. Esparsa como v_budget_months.';

-- ---------------------------------------------------------------------------
-- 5. A invariante do orcamento, verificavel. Irma de v_ledger_integrity.
--
--   saldo das contas do orcamento = soma do disponivel + pronto para atribuir
--
-- As tres parcelas vem de caminhos diferentes: o saldo sai de
-- v_account_balances, que nao sabe nada de orcamento; os envelopes e o
-- pronto saem das visoes acima. Um filtro divergente entre elas - transacao
-- apagada contada de um lado so, conta fora do orcamento vazando, mes
-- cortado errado - aparece como difference diferente de zero.

CREATE VIEW v_budget_integrity
WITH (security_invoker = true)
AS
WITH balances AS (
    SELECT b.currency_code, SUM(b.balance)::BIGINT AS on_budget_balance
      FROM v_account_balances b
      JOIN accounts a ON a.id = b.account_id
     WHERE a.is_on_budget
       AND b.type_code IN ('ASSET', 'LIABILITY')
     GROUP BY b.currency_code
),
envelopes AS (
    SELECT currency_code, SUM(assigned + activity)::BIGINT AS total_available
      FROM v_budget_months
     GROUP BY currency_code
),
ready AS (
    SELECT currency_code, SUM(net_inflow - assigned)::BIGINT AS ready_to_assign
      FROM v_budget_ready_to_assign
     GROUP BY currency_code
),
uncategorized AS (
    SELECT currency_code,
           COUNT(DISTINCT transaction_id)::BIGINT AS uncategorized_transactions,
           SUM(amount)::BIGINT                   AS uncategorized_amount
      FROM v_budget_entries
     WHERE is_uncategorized_spending
     GROUP BY currency_code
),
currencies_in_use AS (
    SELECT currency_code FROM balances
    UNION
    SELECT currency_code FROM envelopes
    UNION
    SELECT currency_code FROM ready
)
SELECT cu.currency_code,
       COALESCE(b.on_budget_balance, 0)            AS on_budget_balance,
       COALESCE(en.total_available, 0)             AS total_available,
       COALESCE(r.ready_to_assign, 0)              AS ready_to_assign,
       COALESCE(b.on_budget_balance, 0)
         - COALESCE(en.total_available, 0)
         - COALESCE(r.ready_to_assign, 0)          AS difference,
       COALESCE(u.uncategorized_transactions, 0)   AS uncategorized_transactions,
       COALESCE(u.uncategorized_amount, 0)         AS uncategorized_amount
  FROM currencies_in_use cu
  LEFT JOIN balances b       ON b.currency_code  = cu.currency_code
  LEFT JOIN envelopes en     ON en.currency_code = cu.currency_code
  LEFT JOIN ready r          ON r.currency_code  = cu.currency_code
  LEFT JOIN uncategorized u  ON u.currency_code  = cu.currency_code;

COMMENT ON VIEW v_budget_integrity IS
    'Orcamento integro: difference = 0 em toda moeda. uncategorized_* conta o dinheiro que saiu das contas do orcamento sem envelope - nao e erro, e pendencia.';

-- ---------------------------------------------------------------------------
-- 6. Mesmo controle de acesso do resto do schema (ver 008).

ALTER TABLE budget_assignments ENABLE ROW LEVEL SECURITY;

DO $fn$
DECLARE
    v_role TEXT;
BEGIN
    FOREACH v_role IN ARRAY ARRAY['anon', 'authenticated'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = v_role) THEN
            EXECUTE format(
                'REVOKE ALL ON budget_assignments, v_budget_entries, v_budget_months, '
                || 'v_budget_ready_to_assign, v_budget_integrity FROM %I', v_role);
        END IF;
    END LOOP;
END
$fn$;
