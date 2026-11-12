-- V0012 — Fase 2, etapa 3: contas ligadas ao banco (D5) e FK composta do Integracoes (decisão do CEO na E3).
--
-- contas.contas.ligada_em: a conta vem do Open Finance. Gravada só pelo Contas, pelo contrato IContasLigadas que o
-- Integracoes chama (D17). Nula = conta manual.
--
-- FK composta: o item só aponta para uma conexão do mesmo casal (FK (conexao_id, casal_id)). O alvo de uma FK precisa
-- de unicidade não parcial; UNIQUE (casal_id, id) é a única exceção à regra 3: a chave primária está nas colunas, então
-- o soft delete não muda nada nela. integracoes.items ganha o mesmo alvo para os vínculos da V0013.

ALTER TABLE contas.contas ADD COLUMN ligada_em timestamptz NULL;

ALTER TABLE integracoes.conexoes ADD CONSTRAINT uq_conexoes_casal_id UNIQUE (casal_id, id);
ALTER TABLE integracoes.items ADD CONSTRAINT uq_items_casal_id UNIQUE (casal_id, id);

ALTER TABLE integracoes.items DROP CONSTRAINT items_conexao_id_fkey;
ALTER TABLE integracoes.items
    ADD CONSTRAINT fk_items_conexao_do_casal FOREIGN KEY (conexao_id, casal_id) REFERENCES integracoes.conexoes (id, casal_id);
