-- V0010 — Módulo Investimentos: metas do casal e as contas (poupança/investimento) de cada meta.
-- conta_id vem do módulo Contas, por isso não há FK entre schemas. Nome em claro (rótulo do casal, não dado sensível).
-- taxa_mensal é fração ao mês (0,85% = 0.0085). data_desejada é sempre o dia 1.
-- Uma conta pertence a no máximo uma meta: unicidade só entre vínculos ativos (índice parcial).

CREATE SCHEMA IF NOT EXISTS investimentos;
GRANT USAGE ON SCHEMA investimentos TO gf_app;

CREATE TABLE investimentos.metas (
    id                         uuid          PRIMARY KEY,
    casal_id                   uuid          NOT NULL REFERENCES identidade.casais (id),
    nome                       text          NOT NULL CHECK (length(nome) BETWEEN 1 AND 60),
    valor_alvo_centavos        bigint        NOT NULL CHECK (valor_alvo_centavos > 0 AND valor_alvo_centavos <= 100000000000),
    data_desejada              date          NULL CHECK (extract(day FROM data_desejada) = 1),
    aporte_planejado_centavos  bigint        NOT NULL CHECK (aporte_planejado_centavos BETWEEN 0 AND 100000000000),
    taxa_mensal                numeric(8,6)  NOT NULL CHECK (taxa_mensal BETWEEN 0 AND 0.05),
    criado_em                  timestamptz   NOT NULL DEFAULT now(),
    atualizado_em              timestamptz   NOT NULL DEFAULT now(),
    is_deleted                 boolean       NOT NULL DEFAULT false,
    deleted_at                 timestamptz   NULL,
    deleted_by                 uuid          NULL
);

CREATE UNIQUE INDEX ux_metas_nome ON investimentos.metas (casal_id, lower(nome)) WHERE NOT is_deleted;

SELECT audit.proteger('investimentos.metas');

CREATE TABLE investimentos.metas_contas (
    id             uuid        PRIMARY KEY,
    casal_id       uuid        NOT NULL REFERENCES identidade.casais (id),
    meta_id        uuid        NOT NULL REFERENCES investimentos.metas (id),
    conta_id       uuid        NOT NULL,
    criado_em      timestamptz NOT NULL DEFAULT now(),
    atualizado_em  timestamptz NOT NULL DEFAULT now(),
    is_deleted     boolean     NOT NULL DEFAULT false,
    deleted_at     timestamptz NULL,
    deleted_by     uuid        NULL
);

CREATE UNIQUE INDEX ux_metas_contas_conta ON investimentos.metas_contas (casal_id, conta_id) WHERE NOT is_deleted;
CREATE INDEX ix_metas_contas_meta ON investimentos.metas_contas (meta_id) WHERE NOT is_deleted;

SELECT audit.proteger('investimentos.metas_contas');
