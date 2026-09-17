-- Nemus 005 - PILAR 1. A invariante de partidas dobradas, no banco.
--
-- CHECK nao cruza linhas, entao a soma das pernas precisa de CONSTRAINT
-- TRIGGER diferido: as pernas entram uma a uma e a verificacao roda no
-- COMMIT. Isto e rede de seguranca. O dominio C# nunca deveria deixar
-- chegar ate aqui - mas quem escrever SQL direto tambem nao consegue furar.
--
-- Codigos de erro proprios, para o app distinguir do resto:
--   NM001 transacao desbalanceada
--   NM002 menos de duas pernas
--   NM003 pernas em moedas diferentes

CREATE OR REPLACE FUNCTION nemus_check_transaction(p_tx_id UUID) RETURNS VOID
LANGUAGE plpgsql AS $fn$
DECLARE
    v_sum        BIGINT;
    v_count      INT;
    v_currencies INT;
BEGIN
    -- Transacao apagada de vez (hard delete): as pernas foram junto por
    -- cascade e nao ha nada a verificar.
    IF NOT EXISTS (SELECT 1 FROM transactions WHERE id = p_tx_id) THEN
        RETURN;
    END IF;

    SELECT COALESCE(SUM(amount), 0), COUNT(*), COUNT(DISTINCT currency_code)
      INTO v_sum, v_count, v_currencies
      FROM entries
     WHERE transaction_id = p_tx_id;

    IF v_count < 2 THEN
        RAISE EXCEPTION
            'Transacao % tem % lancamento(s); partidas dobradas exigem ao menos 2.',
            p_tx_id, v_count
            USING ERRCODE = 'NM002';
    END IF;

    IF v_currencies > 1 THEN
        RAISE EXCEPTION
            'Transacao % mistura % moedas. Multimoeda exige conta de variacao cambial (fora da fase 1).',
            p_tx_id, v_currencies
            USING ERRCODE = 'NM003';
    END IF;

    IF v_sum <> 0 THEN
        RAISE EXCEPTION
            'Transacao % esta desbalanceada: a soma das pernas e %, deveria ser 0.',
            p_tx_id, v_sum
            USING ERRCODE = 'NM001';
    END IF;
END;
$fn$;

-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION nemus_entries_balance_guard() RETURNS TRIGGER
LANGUAGE plpgsql AS $fn$
BEGIN
    IF TG_OP = 'DELETE' THEN
        PERFORM nemus_check_transaction(OLD.transaction_id);
    ELSIF TG_OP = 'INSERT' THEN
        PERFORM nemus_check_transaction(NEW.transaction_id);
    ELSE
        -- UPDATE pode mover a perna de uma transacao para outra;
        -- as duas precisam continuar validas.
        PERFORM nemus_check_transaction(NEW.transaction_id);
        IF OLD.transaction_id <> NEW.transaction_id THEN
            PERFORM nemus_check_transaction(OLD.transaction_id);
        END IF;
    END IF;

    RETURN NULL;
END;
$fn$;

CREATE CONSTRAINT TRIGGER trg_entries_balanced
    AFTER INSERT OR UPDATE OR DELETE ON entries
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION nemus_entries_balance_guard();

-- Sem este segundo gatilho ficaria um buraco: uma transacao inserida sem
-- nenhuma perna jamais dispara o gatilho de entries.
CREATE OR REPLACE FUNCTION nemus_transactions_balance_guard() RETURNS TRIGGER
LANGUAGE plpgsql AS $fn$
BEGIN
    PERFORM nemus_check_transaction(NEW.id);
    RETURN NULL;
END;
$fn$;

CREATE CONSTRAINT TRIGGER trg_transactions_balanced
    AFTER INSERT OR UPDATE ON transactions
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION nemus_transactions_balance_guard();

-- ---------------------------------------------------------------------------
-- Conta de sistema nao se apaga.

CREATE OR REPLACE FUNCTION nemus_protect_system_accounts() RETURNS TRIGGER
LANGUAGE plpgsql AS $fn$
BEGIN
    IF OLD.is_system THEN
        RAISE EXCEPTION 'A conta de sistema "%" nao pode ser removida.', OLD.name
            USING ERRCODE = 'NM004';
    END IF;
    RETURN OLD;
END;
$fn$;

CREATE TRIGGER trg_accounts_protect_system
    BEFORE DELETE ON accounts
    FOR EACH ROW EXECUTE FUNCTION nemus_protect_system_accounts();
