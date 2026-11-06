-- V0002 — Login do casal: bloqueio progressivo, credenciais (passkey/TOTP), sessões e pedidos de dispositivo.
-- Nada em claro: sessões e pedidos guardam só HMAC do token; o segredo TOTP só existe cifrado.

ALTER TABLE identidade.usuarios
    ADD COLUMN falhas_seguidas             integer     NOT NULL DEFAULT 0,
    ADD COLUMN bloqueios                   integer     NOT NULL DEFAULT 0,
    ADD COLUMN bloqueado_ate               timestamptz NULL,
    ADD COLUMN primeiro_acesso_encerrado_em timestamptz NULL;

CREATE TABLE identidade.credenciais (
    id               uuid        PRIMARY KEY,
    casal_id         uuid        NOT NULL REFERENCES identidade.casais (id),
    usuario_id       uuid        NOT NULL REFERENCES identidade.usuarios (id),
    tipo             text        NOT NULL CHECK (tipo IN ('PASSKEY', 'TOTP')),
    apelido          text        NOT NULL CHECK (length(apelido) BETWEEN 1 AND 60),
    credential_id    bytea       NULL,
    chave_publica    bytea       NULL,
    contador         bigint      NULL,
    segredo_cifrado  bytea       NULL,
    ultimo_passo     bigint      NULL,
    confirmada       boolean     NOT NULL DEFAULT false,
    ultimo_uso_em    timestamptz NULL,
    criado_em        timestamptz NOT NULL DEFAULT now(),
    atualizado_em    timestamptz NOT NULL DEFAULT now(),
    is_deleted       boolean     NOT NULL DEFAULT false,
    deleted_at       timestamptz NULL,
    deleted_by       uuid        NULL,
    CONSTRAINT ck_credenciais_por_tipo CHECK (
        (tipo = 'PASSKEY' AND credential_id IS NOT NULL AND chave_publica IS NOT NULL AND contador IS NOT NULL
                          AND segredo_cifrado IS NULL AND ultimo_passo IS NULL)
        OR
        (tipo = 'TOTP' AND segredo_cifrado IS NOT NULL
                       AND credential_id IS NULL AND chave_publica IS NULL AND contador IS NULL))
);

CREATE UNIQUE INDEX ux_credenciais_credential_id ON identidade.credenciais (credential_id) WHERE NOT is_deleted AND credential_id IS NOT NULL;
CREATE INDEX ix_credenciais_usuario ON identidade.credenciais (usuario_id) WHERE NOT is_deleted;

CREATE TABLE identidade.sessoes (
    id             uuid        PRIMARY KEY,
    casal_id       uuid        NOT NULL REFERENCES identidade.casais (id),
    usuario_id     uuid        NOT NULL REFERENCES identidade.usuarios (id),
    token_hmac     bytea       NOT NULL,
    expira_em      timestamptz NOT NULL,
    ultimo_uso_em  timestamptz NOT NULL,
    confirmada_em  timestamptz NOT NULL,
    criado_em      timestamptz NOT NULL DEFAULT now(),
    atualizado_em  timestamptz NOT NULL DEFAULT now(),
    is_deleted     boolean     NOT NULL DEFAULT false,
    deleted_at     timestamptz NULL,
    deleted_by     uuid        NULL
);

CREATE UNIQUE INDEX ux_sessoes_token ON identidade.sessoes (token_hmac) WHERE NOT is_deleted;
CREATE INDEX ix_sessoes_usuario ON identidade.sessoes (usuario_id) WHERE NOT is_deleted;

CREATE TABLE identidade.pedidos_dispositivo (
    id             uuid        PRIMARY KEY,
    casal_id       uuid        NOT NULL REFERENCES identidade.casais (id),
    usuario_id     uuid        NOT NULL REFERENCES identidade.usuarios (id),
    token_hmac     bytea       NOT NULL,
    codigo         char(6)     NOT NULL,
    estado         text        NOT NULL CHECK (estado IN ('PENDENTE', 'APROVADO', 'USADO')),
    expira_em      timestamptz NOT NULL,
    aprovado_por   uuid        NULL REFERENCES identidade.usuarios (id),
    criado_em      timestamptz NOT NULL DEFAULT now(),
    atualizado_em  timestamptz NOT NULL DEFAULT now(),
    is_deleted     boolean     NOT NULL DEFAULT false,
    deleted_at     timestamptz NULL,
    deleted_by     uuid        NULL
);

CREATE UNIQUE INDEX ux_pedidos_token ON identidade.pedidos_dispositivo (token_hmac) WHERE NOT is_deleted;
CREATE INDEX ix_pedidos_usuario ON identidade.pedidos_dispositivo (usuario_id) WHERE NOT is_deleted;

SELECT audit.proteger('identidade.credenciais');
SELECT audit.proteger('identidade.sessoes');
SELECT audit.proteger('identidade.pedidos_dispositivo');
