-- V0009 — Categoria "Rendimentos" ganha o papel estável RENDIMENTOS (receita); o papel passa a depender do tipo.
-- Não há tabela nova (a auditoria da lancamentos.categorias já vem da V0004). O UPDATE e o INSERT são idempotentes.
-- Só recebe a categoria nova o casal que já tem categorias ativas: quem não tem nenhuma recebe as padrão (com Rendimentos) na primeira consulta.

ALTER TABLE lancamentos.categorias DROP CONSTRAINT categorias_papel_check;
ALTER TABLE lancamentos.categorias ADD CONSTRAINT categorias_papel_check CHECK (papel IN ('INVESTIMENTO', 'DIVIDAS', 'RENDIMENTOS'));

ALTER TABLE lancamentos.categorias DROP CONSTRAINT ck_categorias_papel_so_despesa;
ALTER TABLE lancamentos.categorias ADD CONSTRAINT ck_categorias_papel_tipo CHECK (
    papel IS NULL
    OR (papel IN ('INVESTIMENTO', 'DIVIDAS') AND tipo = 'DESPESA')
    OR (papel = 'RENDIMENTOS' AND tipo = 'RECEITA'));

UPDATE lancamentos.categorias SET papel = 'RENDIMENTOS'
WHERE tipo = 'RECEITA' AND lower(nome) = 'rendimentos' AND papel IS NULL AND NOT is_deleted;

INSERT INTO lancamentos.categorias (id, casal_id, nome, tipo, papel)
SELECT gen_random_uuid(), c.casal_id, 'Rendimentos', 'RECEITA', 'RENDIMENTOS'
FROM (SELECT DISTINCT casal_id FROM lancamentos.categorias WHERE NOT is_deleted) c
WHERE NOT EXISTS (SELECT 1 FROM lancamentos.categorias x
                  WHERE x.casal_id = c.casal_id AND NOT x.is_deleted AND (x.papel = 'RENDIMENTOS' OR lower(x.nome) = 'rendimentos'));
