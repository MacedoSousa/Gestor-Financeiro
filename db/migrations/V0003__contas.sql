-- V0003 — Módulo Contas: contas bancárias, dinheiro em espécie e cartões (com dia de fechamento e de vencimento da fatura).
-- Agência e número da conta só existem cifrados. O titular é do módulo Identidade, por isso não há FK para ele.

CREATE SCHEMA IF NOT EXISTS contas;
GRANT USAGE ON SCHEMA contas TO gf_app;

CREATE TABLE contas.contas (
    id                      uuid        PRIMARY KEY,
    casal_id                uuid        NOT NULL REFERENCES identidade.casais (id),
    tipo                    text        NOT NULL CHECK (tipo IN ('CORRENTE', 'POUPANCA', 'CARTAO', 'DINHEIRO')),
    apelido                 text        NOT NULL CHECK (length(apelido) BETWEEN 1 AND 60),
    instituicao             text        NULL CHECK (length(instituicao) <= 60),
    titular_usuario_id      uuid        NULL,
    agencia_cifrada         bytea       NULL,
    numero_cifrado          bytea       NULL,
    saldo_inicial_centavos  bigint      NOT NULL DEFAULT 0,
    saldo_inicial_em        date        NOT NULL,
    dia_fechamento          smallint    NULL CHECK (dia_fechamento BETWEEN 1 AND 31),
    dia_vencimento          smallint    NULL CHECK (dia_vencimento BETWEEN 1 AND 31),
    limite_centavos         bigint      NULL,
    arquivada               boolean     NOT NULL DEFAULT false,
    criado_em               timestamptz NOT NULL DEFAULT now(),
    atualizado_em           timestamptz NOT NULL DEFAULT now(),
    is_deleted              boolean     NOT NULL DEFAULT false,
    deleted_at              timestamptz NULL,
    deleted_by              uuid        NULL,
    CONSTRAINT ck_contas_cartao_tem_dias CHECK ((tipo = 'CARTAO') = (dia_fechamento IS NOT NULL AND dia_vencimento IS NOT NULL))
);

CREATE UNIQUE INDEX ux_contas_apelido ON contas.contas (casal_id, lower(apelido)) WHERE NOT is_deleted;
CREATE INDEX ix_contas_casal ON contas.contas (casal_id) WHERE NOT is_deleted;

SELECT audit.proteger('contas.contas');
