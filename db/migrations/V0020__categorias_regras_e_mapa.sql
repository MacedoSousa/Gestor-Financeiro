-- V0020 — Fase 2, etapa 5 (Tarefa 5.1; no plano, "V0015", número já usado): regras por descrição e mapa da categoria do
-- banco, que só SUGEREM a categoria de uma linha "a revisar" (a revisão é do casal, D14 e D15).
--
-- regras_de_categoria: "sempre que vier assim". A descrição do banco normalizada (Importacao.NormalizarDescricao) é dado
-- sensível (regra 4): só cifrada, AAD 'lancamentos.regras_de_categoria|descricao_normalizada_cifrada|<id>'. A busca e a
-- unicidade usam descricao_hmac (ICofre.IndiceCego, propósito lancamentos.regra, com o casal no texto).
-- mapa_categoria_externa: "sempre que o banco disser <categoria>" (decisão 2). Só o botão grava aqui; nada de aprendizado
-- silencioso. categoria_externa é o código da Pluggy (8 dígitos); nome_externo, o nome genérico dela (não é dado pessoal).
-- transacoes.categoria_externa_nome: o nome da categoria da Pluggy junto do código, para a tela de revisão.

CREATE TABLE lancamentos.regras_de_categoria (
    id                            uuid        PRIMARY KEY,
    casal_id                      uuid        NOT NULL REFERENCES identidade.casais (id),
    descricao_normalizada_cifrada bytea       NOT NULL,
    descricao_hmac                bytea       NOT NULL,
    categoria_id                  uuid        NOT NULL REFERENCES lancamentos.categorias (id),
    criado_em                     timestamptz NOT NULL DEFAULT now(),
    atualizado_em                 timestamptz NOT NULL DEFAULT now(),
    is_deleted                    boolean     NOT NULL DEFAULT false,
    deleted_at                    timestamptz NULL,
    deleted_by                    uuid        NULL
);

CREATE UNIQUE INDEX ux_regras_descricao ON lancamentos.regras_de_categoria (casal_id, descricao_hmac) WHERE NOT is_deleted;

SELECT audit.proteger('lancamentos.regras_de_categoria');

CREATE TABLE lancamentos.mapa_categoria_externa (
    id                 uuid        PRIMARY KEY,
    casal_id           uuid        NOT NULL REFERENCES identidade.casais (id),
    categoria_externa  text        NOT NULL CHECK (categoria_externa ~ '^[0-9]{8}$'),
    nome_externo       text        NULL CHECK (length(nome_externo) BETWEEN 1 AND 60),
    categoria_id       uuid        NOT NULL REFERENCES lancamentos.categorias (id),
    criado_em          timestamptz NOT NULL DEFAULT now(),
    atualizado_em      timestamptz NOT NULL DEFAULT now(),
    is_deleted         boolean     NOT NULL DEFAULT false,
    deleted_at         timestamptz NULL,
    deleted_by         uuid        NULL
);

CREATE UNIQUE INDEX ux_mapa_externa ON lancamentos.mapa_categoria_externa (casal_id, categoria_externa) WHERE NOT is_deleted;

SELECT audit.proteger('lancamentos.mapa_categoria_externa');

ALTER TABLE lancamentos.transacoes
    ADD COLUMN categoria_externa_nome text NULL
        CONSTRAINT ck_transacoes_categoria_externa_nome CHECK (length(categoria_externa_nome) BETWEEN 1 AND 60);

-- A tela "Revisar importados" lista as linhas a revisar do casal.
CREATE INDEX ix_transacoes_a_revisar ON lancamentos.transacoes (casal_id) WHERE NOT is_deleted AND revisao = 'A_REVISAR';
