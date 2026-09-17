-- Nemus 003 - Categorias, favorecidos e normalizacao de descricao bancaria.

CREATE TABLE categories (
    id           UUID        PRIMARY KEY,
    parent_id    UUID        NULL REFERENCES categories (id) ON DELETE RESTRICT,
    name         TEXT        NOT NULL,
    kind         TEXT        NOT NULL,
    is_archived  BOOLEAN     NOT NULL DEFAULT FALSE,
    sort_order   INT         NOT NULL DEFAULT 0,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT ck_categories_kind     CHECK (kind IN ('EXPENSE', 'INCOME')),
    CONSTRAINT ck_categories_name     CHECK (length(btrim(name)) > 0),
    CONSTRAINT ck_categories_not_self CHECK (parent_id IS NULL OR parent_id <> id)
);

COMMENT ON TABLE categories IS
    'Categoria e dimensao do lancamento, nao conta do razao. O envelope da fase 4 sera assigned - spent + rollover calculado sobre esta dimensao, sem razao paralelo.';

CREATE UNIQUE INDEX ux_categories_name
    ON categories (COALESCE(parent_id, '00000000-0000-0000-0000-000000000000'::uuid),
                   lower(btrim(name)));

-- Hierarquia de no maximo dois niveis, e filho herda o tipo do pai.
CREATE OR REPLACE FUNCTION nemus_assert_category_shape() RETURNS TRIGGER
LANGUAGE plpgsql AS $fn$
DECLARE
    v_parent_parent UUID;
    v_parent_kind   TEXT;
BEGIN
    IF NEW.parent_id IS NULL THEN
        RETURN NEW;
    END IF;

    SELECT parent_id, kind INTO v_parent_parent, v_parent_kind
      FROM categories WHERE id = NEW.parent_id;

    IF v_parent_parent IS NOT NULL THEN
        RAISE EXCEPTION 'Categoria "%" excede a profundidade maxima de 2 niveis.', NEW.name
            USING ERRCODE = 'NM010';
    END IF;

    IF v_parent_kind <> NEW.kind THEN
        RAISE EXCEPTION 'Categoria "%" do tipo % nao pode ter pai do tipo %.',
            NEW.name, NEW.kind, v_parent_kind
            USING ERRCODE = 'NM011';
    END IF;

    RETURN NEW;
END;
$fn$;

CREATE TRIGGER trg_categories_shape
    BEFORE INSERT OR UPDATE OF parent_id, kind ON categories
    FOR EACH ROW EXECUTE FUNCTION nemus_assert_category_shape();

-- ---------------------------------------------------------------------------

CREATE TABLE payees (
    id                  UUID        PRIMARY KEY,
    name                TEXT        NOT NULL,
    default_category_id UUID        NULL REFERENCES categories (id) ON DELETE SET NULL,
    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT ck_payees_name CHECK (length(btrim(name)) > 0)
);

CREATE UNIQUE INDEX ux_payees_name ON payees (lower(btrim(name)));

COMMENT ON COLUMN payees.default_category_id IS
    'Semente do motor de regras da fase 7. Nao aplica nada sozinho.';

-- Um mesmo estabelecimento aparece com texto diferente em cada banco.
-- Esta tabela e a camada de normalizacao citada no escopo do projeto.
CREATE TABLE payee_aliases (
    id         UUID        PRIMARY KEY,
    payee_id   UUID        NOT NULL REFERENCES payees (id) ON DELETE CASCADE,
    raw_text   TEXT        NOT NULL,
    source     TEXT        NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),

    CONSTRAINT ck_payee_aliases_source   CHECK (source IN ('MANUAL', 'OFX', 'PLUGGY')),
    CONSTRAINT ck_payee_aliases_raw_text CHECK (length(btrim(raw_text)) > 0)
);

CREATE UNIQUE INDEX ux_payee_aliases_raw ON payee_aliases (lower(btrim(raw_text)));

COMMENT ON TABLE payee_aliases IS
    'Ex.: PAG*IFOOD SAO PAULO BR e IFOOD *IFOOD apontam para o mesmo payee. Sem isto, relatorio por estabelecimento nunca fecha.';
