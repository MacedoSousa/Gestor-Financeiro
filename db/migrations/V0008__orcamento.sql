-- V0008 — Módulo Orcamento: teto por categoria e mês, e receita prevista do mês.
-- categoria_id vem do módulo Lancamentos, por isso não há FK entre schemas. Nada cifrado: só ids e valores.
-- mes é sempre o dia 1. Unicidade só entre linhas ativas (índices parciais), para o soft delete liberar a vaga.

CREATE SCHEMA IF NOT EXISTS orcamento;
GRANT USAGE ON SCHEMA orcamento TO gf_app;

CREATE TABLE orcamento.tetos (
    id              uuid        PRIMARY KEY,
    casal_id        uuid        NOT NULL REFERENCES identidade.casais (id),
    mes             date        NOT NULL CHECK (extract(day FROM mes) = 1),
    categoria_id    uuid        NOT NULL,
    valor_centavos  bigint      NOT NULL CHECK (valor_centavos BETWEEN 0 AND 100000000000),
    criado_em       timestamptz NOT NULL DEFAULT now(),
    atualizado_em   timestamptz NOT NULL DEFAULT now(),
    is_deleted      boolean     NOT NULL DEFAULT false,
    deleted_at      timestamptz NULL,
    deleted_by      uuid        NULL
);

CREATE UNIQUE INDEX ux_tetos ON orcamento.tetos (casal_id, mes, categoria_id) WHERE NOT is_deleted;

SELECT audit.proteger('orcamento.tetos');

CREATE TABLE orcamento.receitas_previstas (
    id              uuid        PRIMARY KEY,
    casal_id        uuid        NOT NULL REFERENCES identidade.casais (id),
    mes             date        NOT NULL CHECK (extract(day FROM mes) = 1),
    valor_centavos  bigint      NOT NULL CHECK (valor_centavos BETWEEN 0 AND 100000000000),
    criado_em       timestamptz NOT NULL DEFAULT now(),
    atualizado_em   timestamptz NOT NULL DEFAULT now(),
    is_deleted      boolean     NOT NULL DEFAULT false,
    deleted_at      timestamptz NULL,
    deleted_by      uuid        NULL
);

CREATE UNIQUE INDEX ux_receitas_previstas ON orcamento.receitas_previstas (casal_id, mes) WHERE NOT is_deleted;

SELECT audit.proteger('orcamento.receitas_previstas');
