-- V0004 — Módulo Lancamentos: categorias e transações (todas as origens).
-- Descrição e contraparte só existem cifradas. conta_id vem do módulo Contas, por isso não há FK entre schemas.
-- Valor com sinal: negativo = saída, positivo = entrada.

CREATE SCHEMA IF NOT EXISTS lancamentos;
GRANT USAGE ON SCHEMA lancamentos TO gf_app;

CREATE TABLE lancamentos.categorias (
    id             uuid        PRIMARY KEY,
    casal_id       uuid        NOT NULL REFERENCES identidade.casais (id),
    nome           text        NOT NULL CHECK (length(nome) BETWEEN 1 AND 40),
    tipo           text        NOT NULL CHECK (tipo IN ('DESPESA', 'RECEITA')),
    arquivada      boolean     NOT NULL DEFAULT false,
    criado_em      timestamptz NOT NULL DEFAULT now(),
    atualizado_em  timestamptz NOT NULL DEFAULT now(),
    is_deleted     boolean     NOT NULL DEFAULT false,
    deleted_at     timestamptz NULL,
    deleted_by     uuid        NULL
);

CREATE UNIQUE INDEX ux_categorias_nome ON lancamentos.categorias (casal_id, lower(nome)) WHERE NOT is_deleted;

SELECT audit.proteger('lancamentos.categorias');

CREATE TABLE lancamentos.transacoes (
    id                   uuid        PRIMARY KEY,
    casal_id             uuid        NOT NULL REFERENCES identidade.casais (id),
    conta_id             uuid        NOT NULL,
    categoria_id         uuid        NULL REFERENCES lancamentos.categorias (id),
    data                 date        NOT NULL,
    valor_centavos       bigint      NOT NULL CHECK (valor_centavos <> 0),
    natureza             text        NOT NULL CHECK (natureza IN ('DESPESA', 'RECEITA', 'TRANSFERENCIA')),
    descricao_cifrada    bytea       NOT NULL,
    contraparte_cifrada  bytea       NULL,
    origem               text        NOT NULL CHECK (origem IN ('MANUAL', 'OPEN_FINANCE', 'WHATSAPP')),
    id_externo           text        NOT NULL,
    autor_usuario_id     uuid        NULL,
    fatura_vencimento    date        NULL,
    grupo_id             uuid        NULL,
    parcela              smallint    NULL,
    parcelas             smallint    NULL,
    criado_em            timestamptz NOT NULL DEFAULT now(),
    atualizado_em        timestamptz NOT NULL DEFAULT now(),
    is_deleted           boolean     NOT NULL DEFAULT false,
    deleted_at           timestamptz NULL,
    deleted_by           uuid        NULL,
    CONSTRAINT ck_transacoes_sinal CHECK (
        (natureza = 'DESPESA' AND valor_centavos < 0)
        OR (natureza = 'RECEITA' AND valor_centavos > 0)
        OR natureza = 'TRANSFERENCIA'),
    CONSTRAINT ck_transacoes_transferencia_sem_categoria CHECK (natureza <> 'TRANSFERENCIA' OR categoria_id IS NULL),
    CONSTRAINT ck_transacoes_parcelas CHECK (
        (parcela IS NULL AND parcelas IS NULL)
        OR (parcela IS NOT NULL AND parcelas IS NOT NULL AND parcela >= 1 AND parcela <= parcelas AND parcelas <= 24))
);

CREATE UNIQUE INDEX ux_transacoes_origem_id_externo ON lancamentos.transacoes (origem, id_externo) WHERE NOT is_deleted;
CREATE INDEX ix_transacoes_casal_data ON lancamentos.transacoes (casal_id, data) WHERE NOT is_deleted;
CREATE INDEX ix_transacoes_conta_data ON lancamentos.transacoes (conta_id, data) WHERE NOT is_deleted;
CREATE INDEX ix_transacoes_grupo ON lancamentos.transacoes (grupo_id) WHERE grupo_id IS NOT NULL AND NOT is_deleted;

SELECT audit.proteger('lancamentos.transacoes');
