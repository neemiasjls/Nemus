-- Nemus 007 - Visoes de leitura.
--
-- Saldo e calculado, nao materializado. Em escala pessoal (dezenas de
-- milhares de linhas) o SUM sobre indice e sub-milissegundo. Cache so
-- quando doer, e cache que se prova contra esta visao.
--
-- Atencao ao ::BIGINT em todo SUM: no PostgreSQL, SUM(bigint) devolve
-- NUMERIC, nao BIGINT. Sem o cast explicito, o driver entrega decimal e
-- o pilar 2 vaza justamente na camada de leitura.

CREATE VIEW v_account_balances AS
SELECT a.id                       AS account_id,
       a.name,
       a.type_code,
       t.is_internal,
       a.currency_code,
       a.is_archived,
       COALESCE(b.balance, 0)     AS balance,
       COALESCE(b.entry_count, 0) AS entry_count
  FROM accounts a
  JOIN account_types t ON t.code = a.type_code
  LEFT JOIN (
        SELECT e.account_id,
               SUM(e.amount)::BIGINT AS balance,
               COUNT(*)::BIGINT      AS entry_count
          FROM entries e
          JOIN transactions tx ON tx.id = e.transaction_id
         WHERE tx.deleted_at IS NULL
         GROUP BY e.account_id
  ) b ON b.account_id = a.id;

COMMENT ON VIEW v_account_balances IS
    'Saldo por conta, ignorando transacoes com soft delete. Como o soft delete e sempre da transacao inteira, as duas pernas somem juntas e a soma global continua zero.';

-- ---------------------------------------------------------------------------

CREATE VIEW v_net_worth AS
SELECT currency_code,
       COALESCE(SUM(balance) FILTER (WHERE type_code = 'ASSET'), 0)::BIGINT     AS assets,
       COALESCE(SUM(balance) FILTER (WHERE type_code = 'LIABILITY'), 0)::BIGINT AS liabilities,
       COALESCE(SUM(balance) FILTER (WHERE is_internal), 0)::BIGINT             AS net_worth
  FROM v_account_balances
 GROUP BY currency_code;

COMMENT ON VIEW v_net_worth IS
    'Passivo tem saldo negativo (voce deve), entao patrimonio liquido e a soma direta de todas as contas internas.';

-- ---------------------------------------------------------------------------
-- A visao que o teste do razao consulta. Num razao integro, todo contador
-- de violacao e zero e todas as somas sao zero.

CREATE VIEW v_ledger_integrity AS
SELECT
    (SELECT COALESCE(SUM(e.amount), 0)::BIGINT
       FROM entries e
       JOIN transactions t ON t.id = e.transaction_id
      WHERE t.deleted_at IS NULL)                                      AS total_amount,

    (SELECT COALESCE(SUM(e.amount), 0)::BIGINT FROM entries e)         AS total_amount_including_deleted,

    (SELECT COUNT(*)::BIGINT FROM (
        SELECT transaction_id FROM entries
         GROUP BY transaction_id HAVING SUM(amount) <> 0) x)           AS unbalanced_transactions,

    (SELECT COUNT(*)::BIGINT FROM (
        SELECT transaction_id FROM entries
         GROUP BY transaction_id HAVING COUNT(*) < 2) x)               AS undersized_transactions,

    (SELECT COUNT(*)::BIGINT FROM transactions t
      WHERE NOT EXISTS (SELECT 1 FROM entries e WHERE e.transaction_id = t.id))
                                                                       AS empty_transactions,

    (SELECT COALESCE(SUM(balance), 0)::BIGINT FROM v_account_balances)  AS sum_of_all_balances,

    (SELECT COALESCE(SUM(balance), 0)::BIGINT FROM v_account_balances WHERE is_internal)
                                                                       AS sum_internal_balances,

    (SELECT COALESCE(SUM(balance), 0)::BIGINT FROM v_account_balances WHERE NOT is_internal)
                                                                       AS sum_external_balances,

    (SELECT COUNT(*)::BIGINT FROM (
        SELECT p.id FROM installment_plans p
          LEFT JOIN installments i ON i.plan_id = p.id
         GROUP BY p.id, p.financed_amount, p.installment_count
        HAVING COALESCE(SUM(i.amount), 0) <> p.financed_amount
            OR COUNT(i.id) <> p.installment_count) x)                  AS broken_installment_plans;

COMMENT ON VIEW v_ledger_integrity IS
    'Razao integro: total_amount = 0, sum_of_all_balances = 0, sum_internal_balances = -sum_external_balances, e todos os contadores de violacao em 0.';
