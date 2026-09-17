-- Nemus 008 - Controle de acesso.
--
-- Motivo: em plataforma gerenciada (Supabase e semelhantes) o schema public
-- e exposto automaticamente por uma API REST, e a chave anon dessa API e
-- publica por design - ela vai embutida no frontend. Sem o que esta aqui,
-- publicar o schema significa publicar o razao inteiro para leitura E
-- escrita.
--
-- Nada disto muda modelagem: nenhuma tabela, coluna ou constraint e tocada.
-- E so permissao.
--
-- Em Postgres local o efeito e praticamente nulo (o dono da tabela ignora
-- RLS), e e de proposito: uma migration so, valendo nos dois lugares, sem
-- schema divergente entre o que voce testa e o que roda.

-- ---------------------------------------------------------------------------
-- 1. RLS ligado, sem nenhuma policy = nega tudo.
--
-- Quem e dono da tabela (postgres) e quem tem BYPASSRLS continuam passando,
-- entao a API .NET nao sente diferenca. Quem chega pela API REST como anon
-- ou authenticated para na porta.
--
-- Nao uso FORCE ROW LEVEL SECURITY: isso valeria tambem para o dono e
-- quebraria as migrations e o proprio app.

ALTER TABLE currencies         ENABLE ROW LEVEL SECURITY;
ALTER TABLE account_types      ENABLE ROW LEVEL SECURITY;
ALTER TABLE accounts           ENABLE ROW LEVEL SECURITY;
ALTER TABLE credit_card_terms  ENABLE ROW LEVEL SECURITY;
ALTER TABLE categories         ENABLE ROW LEVEL SECURITY;
ALTER TABLE payees             ENABLE ROW LEVEL SECURITY;
ALTER TABLE payee_aliases      ENABLE ROW LEVEL SECURITY;
ALTER TABLE import_batches     ENABLE ROW LEVEL SECURITY;
ALTER TABLE transactions       ENABLE ROW LEVEL SECURITY;
ALTER TABLE entries            ENABLE ROW LEVEL SECURITY;
ALTER TABLE installment_plans  ENABLE ROW LEVEL SECURITY;
ALTER TABLE installments       ENABLE ROW LEVEL SECURITY;

-- A tabela de historico do runner tambem: ela nasce fora das migrations, mas
-- e criada antes de qualquer uma rodar, entao sempre existe neste ponto.
ALTER TABLE __nemus_migrations ENABLE ROW LEVEL SECURITY;

-- ---------------------------------------------------------------------------
-- 2. Views com security_invoker.
--
-- Desde sempre, view no Postgres roda com a permissao de QUEM A CRIOU, nao
-- de quem consulta. Com RLS ligado nas tabelas mas as views no padrao, uma
-- view sobre entries seria exatamente o tunel por onde o RLS vaza: as
-- tabelas fechadas, e v_account_balances entregando os saldos assim mesmo.

ALTER VIEW v_account_balances SET (security_invoker = true);
ALTER VIEW v_net_worth        SET (security_invoker = true);
ALTER VIEW v_ledger_integrity SET (security_invoker = true);

-- ---------------------------------------------------------------------------
-- 3. search_path fixo nas funcoes.
--
-- Funcao sem search_path definido resolve nomes pelo caminho de busca de
-- QUEM chama. Se alguem conseguir criar uma tabela chamada "transactions"
-- num schema que venha antes de public, os gatilhos de invariante passam a
-- consultar a tabela do atacante e param de proteger coisa nenhuma.
--
-- Fixar o caminho e uma linha por funcao e elimina a classe inteira.

ALTER FUNCTION nemus_check_transaction(uuid)        SET search_path = public, pg_catalog;
ALTER FUNCTION nemus_entries_balance_guard()        SET search_path = public, pg_catalog;
ALTER FUNCTION nemus_transactions_balance_guard()   SET search_path = public, pg_catalog;
ALTER FUNCTION nemus_assert_category_shape()        SET search_path = public, pg_catalog;
ALTER FUNCTION nemus_protect_system_accounts()      SET search_path = public, pg_catalog;
ALTER FUNCTION nemus_check_installment_plan(uuid)   SET search_path = public, pg_catalog;
ALTER FUNCTION nemus_installments_guard()           SET search_path = public, pg_catalog;
ALTER FUNCTION nemus_plans_guard()                  SET search_path = public, pg_catalog;

-- ---------------------------------------------------------------------------
-- 4. Retirar privilegio dos papeis da API REST.
--
-- RLS ja bastaria, mas privilegio negado e uma parede independente: se
-- alguem criar uma policy permissiva por engano la na frente, o GRANT
-- ausente ainda segura. Cinto e suspensorio, de proposito.
--
-- Os papeis anon/authenticated so existem em Supabase. O bloco abaixo os
-- ignora quando nao existem, para a migration continuar valendo no Postgres
-- local sem virar um arquivo diferente por ambiente.

DO $fn$
DECLARE
    v_role TEXT;
BEGIN
    FOREACH v_role IN ARRAY ARRAY['anon', 'authenticated'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = v_role) THEN
            EXECUTE format('REVOKE ALL ON ALL TABLES    IN SCHEMA public FROM %I', v_role);
            EXECUTE format('REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM %I', v_role);
            EXECUTE format('REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM %I', v_role);
            EXECUTE format('REVOKE ALL ON SCHEMA public FROM %I', v_role);

            -- Tabela criada no futuro nao pode nascer aberta.
            EXECUTE format(
                'ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM %I', v_role);
            EXECUTE format(
                'ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM %I', v_role);

            RAISE NOTICE 'Privilegios retirados de %.', v_role;
        END IF;
    END LOOP;
END
$fn$;
