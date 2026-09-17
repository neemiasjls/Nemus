-- Nemus 011 - Usuario e sessao (login proprio).
--
-- O QUE MUDA. Ate aqui a API tinha um segredo unico no ambiente: quem tinha o
-- token tinha o razao. Agora ha usuario e senha, e o token do ambiente muda de
-- papel - ele deixa de abrir os lancamentos e passa a servir so para criar o
-- primeiro acesso.
--
-- DUAS COISAS NAO FICAM GUARDADAS AQUI:
--
--   a senha       so o hash, PBKDF2-SHA256 com sal por usuario. Nem eu, nem o
--                 banco, nem o log sabem a senha.
--   o token de    so o SHA-256 dele. O token existe apenas no navegador de
--   sessao        quem entrou; vazou o banco, o atacante nao ganha sessao
--                 usavel - ele teria que inverter o hash.
--
-- Sessao em tabela, e nao JWT assinado, por um motivo pratico: JWT nao se
-- revoga. "Sair" e "trocar a senha" precisam ter efeito imediato, e com
-- tabela isso e um UPDATE.

CREATE TABLE users (
    id             UUID        PRIMARY KEY,
    username       TEXT        NOT NULL,
    password_hash  TEXT        NOT NULL,
    is_active      BOOLEAN     NOT NULL DEFAULT TRUE,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_login_at  TIMESTAMPTZ NULL,

    -- Nome de usuario e identidade, nao texto livre: sem espaco, sem
    -- maiuscula, para "Neemias" e "neemias" nunca serem duas contas.
    CONSTRAINT ck_users_username CHECK (username ~ '^[a-z0-9._-]{3,40}$'),

    -- Hash auto-descritivo: algoritmo$iteracoes$sal$hash. Guardar o algoritmo
    -- junto permite trocar de PBKDF2 para outro sem invalidar senha existente
    -- - a linha antiga continua legivel e e reescrita no proximo login.
    CONSTRAINT ck_users_password_hash CHECK (password_hash ~ '^[a-z0-9-]+\$[0-9]+\$[A-Za-z0-9+/=]+\$[A-Za-z0-9+/=]+$')
);

CREATE UNIQUE INDEX ux_users_username ON users (username);

COMMENT ON TABLE users IS
    'Um app pessoal costuma ter uma linha aqui. A tabela existe para que senha nunca precise viver em variavel de ambiente, e para que trocar de senha nao exija redeploy.';

-- ---------------------------------------------------------------------------

CREATE TABLE sessions (
    id           UUID        PRIMARY KEY,
    user_id      UUID        NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    token_hash   TEXT        NOT NULL,
    created_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    expires_at   TIMESTAMPTZ NOT NULL,
    last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    revoked_at   TIMESTAMPTZ NULL,

    CONSTRAINT ck_sessions_token_hash CHECK (token_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_sessions_expiry     CHECK (expires_at > created_at)
);

CREATE UNIQUE INDEX ux_sessions_token ON sessions (token_hash);
CREATE INDEX ix_sessions_user ON sessions (user_id) WHERE revoked_at IS NULL;

COMMENT ON COLUMN sessions.token_hash IS
    'SHA-256 do token entregue ao navegador. Sem sal e sem custo de proposito: o token ja e 256 bits aleatorios, nao uma senha adivinhavel - o que se quer aqui e busca por indice, nao resistencia a dicionario.';
COMMENT ON COLUMN sessions.revoked_at IS
    'Sair do app, trocar a senha ou derrubar sessao antiga. Preenchido em vez de apagar a linha, para a sessao sumir sem sumir o historico.';

-- ---------------------------------------------------------------------------
-- Mesmo controle de acesso do resto do schema (ver 008). Aqui ele importa
-- ainda mais: estas duas tabelas nunca podem ser lidas pela API REST publica
-- do Supabase.

ALTER TABLE users    ENABLE ROW LEVEL SECURITY;
ALTER TABLE sessions ENABLE ROW LEVEL SECURITY;

DO $fn$
DECLARE
    v_role TEXT;
BEGIN
    FOREACH v_role IN ARRAY ARRAY['anon', 'authenticated'] LOOP
        IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = v_role) THEN
            EXECUTE format('REVOKE ALL ON users, sessions FROM %I', v_role);
        END IF;
    END LOOP;
END
$fn$;
