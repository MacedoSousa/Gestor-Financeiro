-- V0013 — Módulo Integracoes: vínculos entre o que o banco tem (conta, cartão, aplicação) e o sistema (D18).
--
-- O vínculo aponta para o item (D18), sempre do mesmo casal: FK composta (item_id, casal_id), decisão do CEO na E3.
-- conta_id é do módulo Contas: sem FK entre schemas. Nulo = aplicação aguardando grupo, ou conta marcada "não importar"
-- (nao_importar_em preenchido; decisão 16). Várias aplicações do mesmo grupo apontam para a mesma conta.
-- id_externo é o id da conta ou da aplicação na Pluggy: identificador opaco, em claro e sem HMAC (D7).
-- identidade_hmac (ICofre.IndiceCego, propósito integracoes.identidade) trava a mesma conta física vinda por dois items:
-- conta = agência e conta só com os dígitos (decisão 11); cartão = final do cartão e portador normalizado (decisão 6);
-- aplicação = nulo. O nome do banco digitado nunca entra nele (decisão 1).
-- importado_ate, diferenca_* e faturas_divergentes são preenchidos pela sincronização (etapas 4 e 7).

CREATE TABLE integracoes.vinculos (
    id                    uuid        PRIMARY KEY,
    casal_id              uuid        NOT NULL REFERENCES identidade.casais (id),
    item_id               uuid        NOT NULL,
    tipo                  text        NOT NULL CHECK (tipo IN ('CONTA', 'CARTAO', 'INVESTIMENTO')),
    id_externo            text        NOT NULL CHECK (length(id_externo) BETWEEN 1 AND 100),
    conta_id              uuid        NULL,
    identidade_hmac       bytea       NULL,
    historico_desde       date        NULL,
    importado_ate         date        NULL,
    diferenca_centavos    bigint      NULL,
    diferencas_seguidas   smallint    NOT NULL DEFAULT 0 CHECK (diferencas_seguidas >= 0),
    faturas_divergentes   jsonb       NULL,
    nao_importar_em       timestamptz NULL,
    criado_em             timestamptz NOT NULL DEFAULT now(),
    atualizado_em         timestamptz NOT NULL DEFAULT now(),
    is_deleted            boolean     NOT NULL DEFAULT false,
    deleted_at            timestamptz NULL,
    deleted_by            uuid        NULL,
    CONSTRAINT fk_vinculos_item_do_casal FOREIGN KEY (item_id, casal_id) REFERENCES integracoes.items (id, casal_id),
    -- Conta "não importar" não vira conta.
    CONSTRAINT ck_vinculos_nao_importar_sem_conta CHECK (nao_importar_em IS NULL OR conta_id IS NULL),
    CONSTRAINT ck_vinculos_identidade_so_de_conta CHECK (tipo <> 'INVESTIMENTO' OR identidade_hmac IS NULL)
);

CREATE UNIQUE INDEX ux_vinculos_externo    ON integracoes.vinculos (item_id, tipo, id_externo) WHERE NOT is_deleted;
CREATE UNIQUE INDEX ux_vinculos_identidade ON integracoes.vinculos (casal_id, identidade_hmac) WHERE NOT is_deleted AND identidade_hmac IS NOT NULL;
CREATE INDEX        ix_vinculos_conta      ON integracoes.vinculos (casal_id, conta_id) WHERE NOT is_deleted;

SELECT audit.proteger('integracoes.vinculos');
