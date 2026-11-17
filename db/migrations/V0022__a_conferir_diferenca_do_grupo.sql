-- V0022 — Fase 2, etapa 6 (Tarefa 6.3, PR E6b, CEO, D25): a diferença do grupo de investimento em "A conferir", sem
-- rendimento automático.
--
-- DIFERENCA_DO_GRUPO: por grupo (conta INVESTIMENTO ligada), no máximo um item aberto, recalculado a cada sincronização:
--   valor_centavos = valor no banco (só as aplicações ACTIVE) − saldo do sistema. A sincronização nunca lança nada; zero
--   fecha o item sozinho (RESOLVIDO_PELO_BANCO, sem lançamento; a auditoria é a desta tabela).
-- valor_banco_centavos: o valor no banco da última leitura, para recalcular a diferença na hora da ação do casal (o saldo do
--   sistema pode ter mudado por um aporte ou resgate resolvido depois da leitura).
-- primeiro_do_grupo: só o primeiro item de cada grupo oferece "ajustar o saldo inicial".
-- Resoluções novas: RENDIMENTO (receita Rendimentos, rendimento:<conta>:<dia>), IR_IOF_TAXA (despesa,
--   ajuste-investimento:<conta>:<dia>), PAREADO_COM_MOVIMENTO (um aporte ou resgate aberto virou o par com o grupo e a
--   diferença zerou), AJUSTAR_SALDO_INICIAL e RESOLVIDO_PELO_BANCO. "Deixar para depois" não resolve.
-- Chave: diferenca:<conta>:<n> (n = 1, 2, ...). A tabela já é protegida pela V0021 (audit.proteger).

ALTER TABLE lancamentos.itens_a_conferir DROP CONSTRAINT ck_itens_a_conferir_tipo;
ALTER TABLE lancamentos.itens_a_conferir ADD CONSTRAINT ck_itens_a_conferir_tipo
    CHECK (tipo IN ('TRANSFERENCIA_SEM_PAR', 'PENDENTE_SUMIU', 'PENDENTE_SEM_PAR', 'APORTE_OU_RESGATE', 'DIFERENCA_DO_GRUPO'));

ALTER TABLE lancamentos.itens_a_conferir DROP CONSTRAINT ck_itens_a_conferir_resolucao;
ALTER TABLE lancamentos.itens_a_conferir ADD CONSTRAINT ck_itens_a_conferir_resolucao
    CHECK (resolucao IN ('TRANSFERENCIA', 'GASTO', 'RECEITA', 'APORTE', 'RESGATE', 'CIENTE', 'PAREADO_AUTOMATICAMENTE',
                         'RENDIMENTO', 'IR_IOF_TAXA', 'PAREADO_COM_MOVIMENTO', 'AJUSTAR_SALDO_INICIAL', 'RESOLVIDO_PELO_BANCO'));

ALTER TABLE lancamentos.itens_a_conferir
    ADD COLUMN valor_banco_centavos bigint NULL CHECK (valor_banco_centavos BETWEEN -100000000000 AND 100000000000),
    ADD COLUMN primeiro_do_grupo boolean NOT NULL DEFAULT false;

ALTER TABLE lancamentos.itens_a_conferir ADD CONSTRAINT ck_itens_a_conferir_diferenca_do_grupo
    CHECK ((tipo = 'DIFERENCA_DO_GRUPO') = (valor_banco_centavos IS NOT NULL) AND (NOT primeiro_do_grupo OR tipo = 'DIFERENCA_DO_GRUPO'));

-- No máximo um item aberto por grupo (D25, 1).
CREATE UNIQUE INDEX ux_a_conferir_diferenca_aberta ON lancamentos.itens_a_conferir (casal_id, conta_id) WHERE NOT is_deleted AND estado = 'ABERTO' AND tipo = 'DIFERENCA_DO_GRUPO';
