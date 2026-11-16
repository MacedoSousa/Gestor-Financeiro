-- V0021 — Fase 2, etapa 6 (Tarefa 6.1; no plano, "V0016", número já usado): a caixa "A conferir" (D11), o que o sistema
-- não decidiu sozinho. Nada é descartado: um item resolvido fica RESOLVIDO, com a resolução; nunca é apagado.
--
-- Item criado ou atualizado pela chave, idempotente:
--   pendente-sumiu:<transacao>, pendente-sem-par:<transacao>, sem-par:<transacao> (transferência ou fatura sem par
--   único) e movimento:<transacao> (aplicação ou resgate na corrente, D12).
-- Os CHECKs de tipo e resolução têm nome: a Tarefa 6.3 (rendimento, PR seguinte, D24) acrescenta os dela.
-- conta_id e transacao_id são do próprio Lancamentos (a conta é do Contas: sem FK entre schemas). O valor tem o sinal do
-- sistema. Sem texto livre: a descrição vem da transação (cifrada lá).

CREATE TABLE lancamentos.itens_a_conferir (
    id              uuid        PRIMARY KEY,
    casal_id        uuid        NOT NULL REFERENCES identidade.casais (id),
    tipo            text        NOT NULL
                                CONSTRAINT ck_itens_a_conferir_tipo
                                CHECK (tipo IN ('TRANSFERENCIA_SEM_PAR', 'PENDENTE_SUMIU', 'PENDENTE_SEM_PAR', 'APORTE_OU_RESGATE')),
    chave           text        NOT NULL CHECK (length(chave) BETWEEN 1 AND 120),
    conta_id        uuid        NOT NULL,
    transacao_id    uuid        NULL REFERENCES lancamentos.transacoes (id),
    valor_centavos  bigint      NOT NULL CHECK (valor_centavos BETWEEN -100000000000 AND 100000000000),
    data            date        NOT NULL,
    estado          text        NOT NULL DEFAULT 'ABERTO' CHECK (estado IN ('ABERTO', 'RESOLVIDO')),
    resolucao       text        NULL
                                CONSTRAINT ck_itens_a_conferir_resolucao
                                CHECK (resolucao IN ('TRANSFERENCIA', 'GASTO', 'RECEITA', 'APORTE', 'RESGATE', 'CIENTE',
                                                     'PAREADO_AUTOMATICAMENTE')),
    criado_em       timestamptz NOT NULL DEFAULT now(),
    atualizado_em   timestamptz NOT NULL DEFAULT now(),
    is_deleted      boolean     NOT NULL DEFAULT false,
    deleted_at      timestamptz NULL,
    deleted_by      uuid        NULL,
    CONSTRAINT ck_itens_a_conferir_resolvido CHECK ((estado = 'RESOLVIDO') = (resolucao IS NOT NULL))
);

CREATE UNIQUE INDEX ux_a_conferir_chave ON lancamentos.itens_a_conferir (casal_id, chave) WHERE NOT is_deleted;
CREATE INDEX ix_a_conferir_abertos ON lancamentos.itens_a_conferir (casal_id, conta_id) WHERE NOT is_deleted AND estado = 'ABERTO';

SELECT audit.proteger('lancamentos.itens_a_conferir');
