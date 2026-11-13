-- V0019 — Fase 2, ordem D22 do CEO: só aplicação ACTIVE entra no saldo do grupo de investimento.
--
-- situacao_banco: só de aplicação. O status que o banco mandou na última leitura, traduzido: ATIVA (ACTIVE), PENDENTE
-- (PENDING), RESGATADA (TOTAL_WITHDRAWAL) ou DESCONHECIDA (ausente ou outro). Nulo = ainda não lido depois desta regra.
-- Só ATIVA entra no saldo inicial do grupo e na conferência; com alguma DESCONHECIDA, o grupo fica sem conferência.
-- refazer_saldo_inicial: o grupo nasceu antes da regra e somou aplicações que não estavam ativas (em /opt, CDBs
-- resgatados). A sincronização seguinte refaz o saldo inicial do grupo só com as ATIVA (pelo contrato do Contas, com
-- auditoria; sem grupo ativo, arquiva com saldo 0) e desmarca. Marcado aqui em toda aplicação já agrupada.

ALTER TABLE integracoes.vinculos
    ADD COLUMN situacao_banco text NULL
        CONSTRAINT ck_vinculos_situacao_banco CHECK (situacao_banco IN ('ATIVA', 'PENDENTE', 'RESGATADA', 'DESCONHECIDA')),
    ADD COLUMN refazer_saldo_inicial boolean NOT NULL DEFAULT false;

ALTER TABLE integracoes.vinculos ADD CONSTRAINT ck_vinculos_situacao_so_de_aplicacao
    CHECK ((situacao_banco IS NULL AND NOT refazer_saldo_inicial) OR tipo = 'INVESTIMENTO');

UPDATE integracoes.vinculos SET refazer_saldo_inicial = true
WHERE tipo = 'INVESTIMENTO' AND conta_id IS NOT NULL AND NOT is_deleted;
