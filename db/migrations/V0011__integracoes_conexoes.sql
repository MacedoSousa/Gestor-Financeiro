-- V0011 — Módulo Integracoes: conexões do Meu Pluggy (uma por cônjuge, ADR-015) e os items de cada conexão.
-- usuario_id vem do módulo Identidade: sem FK entre schemas (só casal_id aponta para identidade.casais).
-- A conexão guarda só o cônjuge e o slot das chaves (pluggy_<slot>_client_*, D23); estado e consentimento são do item (D18).
-- O itemId da Pluggy anda junto das credenciais (D7): só cifrado (AAD integracoes.items|item_id_cifrado|<id>);
-- a unicidade no casal é pelo HMAC (ICofre.IndiceCego, propósito integracoes.item).
-- Unicidade só entre linhas não apagadas: apagar uma conexão libera o slot e o cônjuge.

CREATE SCHEMA IF NOT EXISTS integracoes;
GRANT USAGE ON SCHEMA integracoes TO gf_app;

CREATE TABLE integracoes.conexoes (
    id             uuid        PRIMARY KEY,
    casal_id       uuid        NOT NULL REFERENCES identidade.casais (id),
    usuario_id     uuid        NOT NULL,
    slot           smallint    NOT NULL CHECK (slot IN (1, 2)),
    criado_em      timestamptz NOT NULL DEFAULT now(),
    atualizado_em  timestamptz NOT NULL DEFAULT now(),
    is_deleted     boolean     NOT NULL DEFAULT false,
    deleted_at     timestamptz NULL,
    deleted_by     uuid        NULL
);

CREATE UNIQUE INDEX ux_conexoes_slot    ON integracoes.conexoes (casal_id, slot)       WHERE NOT is_deleted;
CREATE UNIQUE INDEX ux_conexoes_usuario ON integracoes.conexoes (casal_id, usuario_id) WHERE NOT is_deleted;

SELECT audit.proteger('integracoes.conexoes');

CREATE TABLE integracoes.items (
    id                        uuid        PRIMARY KEY,
    casal_id                  uuid        NOT NULL REFERENCES identidade.casais (id),
    conexao_id                uuid        NOT NULL REFERENCES integracoes.conexoes (id),
    item_id_cifrado           bytea       NOT NULL,
    item_id_hmac              bytea       NOT NULL,
    banco                     text        NOT NULL CHECK (length(banco) BETWEEN 1 AND 60),
    estado                    text        NOT NULL
                                          CHECK (estado IN ('ATIVO', 'CONSENTIMENTO_VENCENDO', 'CONSENTIMENTO_VENCIDO', 'ERRO')),
    -- Código do status da Pluggy (ex.: LOGIN_ERROR), traduzido na tela; nunca o corpo da resposta (D26).
    motivo_erro               text        NULL CHECK (length(motivo_erro) BETWEEN 1 AND 60),
    consentimento_vence_em    timestamptz NULL,
    ultima_sincronizacao_em   timestamptz NULL,
    ultima_tentativa_em       timestamptz NULL,
    criado_em                 timestamptz NOT NULL DEFAULT now(),
    atualizado_em             timestamptz NOT NULL DEFAULT now(),
    is_deleted                boolean     NOT NULL DEFAULT false,
    deleted_at                timestamptz NULL,
    deleted_by                uuid        NULL
);

CREATE UNIQUE INDEX ux_items_item     ON integracoes.items (casal_id, item_id_hmac) WHERE NOT is_deleted;
CREATE INDEX        ix_items_conexao  ON integracoes.items (conexao_id)             WHERE NOT is_deleted;

SELECT audit.proteger('integracoes.items');
