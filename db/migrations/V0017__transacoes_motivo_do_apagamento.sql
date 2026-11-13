-- V0017 — Fase 2, ordem D19 do CEO: apagar por ausência só depois de 2 sincronizações seguidas, e só isso volta.
--
-- ausencias: quantas sincronizações completas seguidas (lote não vazio, item UPDATED na Pluggy) vieram sem a linha do
-- banco; zera quando ela vem. Na segunda, a linha é apagada (soft delete). Contador por lançamento: a ausência é de cada
-- linha, e um vínculo pode ter linhas sumindo em sincronizações diferentes.
-- apagada_por: o motivo do apagamento, para distinguir os casos.
--   CASAL: o casal apagou pela tela. Nunca volta.
--   AUSENCIA_NO_BANCO: sumiu do banco. Volta se o banco a mandar de novo (is_deleted = false; a auditoria grava RESTORE).
--   PENDENTE_PROMOVIDA: a pendente virou a definitiva. Nunca volta.
-- Nulo em linha apagada = de antes desta regra, ou de outro caminho: tratado como "nunca volta".

ALTER TABLE lancamentos.transacoes
    ADD COLUMN ausencias smallint NOT NULL DEFAULT 0 CONSTRAINT ck_transacoes_ausencias CHECK (ausencias BETWEEN 0 AND 2),
    ADD COLUMN apagada_por text NULL
        CONSTRAINT ck_transacoes_apagada_por CHECK (apagada_por IN ('CASAL', 'AUSENCIA_NO_BANCO', 'PENDENTE_PROMOVIDA'));

ALTER TABLE lancamentos.transacoes ADD CONSTRAINT ck_transacoes_apagada_por_so_apagada CHECK (apagada_por IS NULL OR is_deleted);
ALTER TABLE lancamentos.transacoes ADD CONSTRAINT ck_transacoes_ausencias_so_do_banco CHECK (ausencias = 0 OR origem = 'OPEN_FINANCE');
