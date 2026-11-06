-- V0007 — Categoria com papel (INVESTIMENTO, DIVIDAS) é sempre de despesa; o tipo não pode virar RECEITA.

ALTER TABLE lancamentos.categorias ADD CONSTRAINT ck_categorias_papel_so_despesa CHECK (papel IS NULL OR tipo = 'DESPESA');
