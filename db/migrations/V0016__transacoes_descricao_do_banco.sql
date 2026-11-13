-- V0016 — Fase 2, Tarefa 4.3: a descrição como o banco mandou, à parte da descrição do casal.
--
-- Depois da revisão a descricao_cifrada é do casal (decisão 5) e não muda mais pelo banco. A comparação "descrição
-- parecida" (pendente que vira definitiva) e as regras de categoria da etapa 5 usam o texto do banco, que fica aqui.
-- Dado sensível (regra 4): só cifrado, AAD 'lancamentos.transacoes|descricao_banco_cifrada|<id>'. Nulo fora do OPEN_FINANCE.

ALTER TABLE lancamentos.transacoes ADD COLUMN descricao_banco_cifrada bytea NULL;
ALTER TABLE lancamentos.transacoes ADD CONSTRAINT ck_transacoes_descricao_banco_so_do_banco
    CHECK (descricao_banco_cifrada IS NULL OR origem = 'OPEN_FINANCE');
