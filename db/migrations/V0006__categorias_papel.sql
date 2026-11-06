-- V0006 — Categorias ganham um papel estável (INVESTIMENTO, DIVIDAS), para o orçamento achá-las mesmo depois de renomeadas.
-- Não há tabela nova (a auditoria da lancamentos.categorias já vem da V0004). O UPDATE é idempotente.

ALTER TABLE lancamentos.categorias ADD COLUMN papel text NULL CHECK (papel IN ('INVESTIMENTO', 'DIVIDAS'));

UPDATE lancamentos.categorias SET papel = 'INVESTIMENTO'
WHERE tipo = 'DESPESA' AND nome = 'Investimento' AND papel IS NULL AND NOT is_deleted;

UPDATE lancamentos.categorias SET papel = 'DIVIDAS'
WHERE tipo = 'DESPESA' AND nome = 'Dívidas e financiamentos' AND papel IS NULL AND NOT is_deleted;

CREATE UNIQUE INDEX ux_categorias_papel ON lancamentos.categorias (casal_id, papel) WHERE NOT is_deleted AND papel IS NOT NULL;
