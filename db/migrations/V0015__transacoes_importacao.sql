-- V0015 — Fase 2, Tarefa 4.2: estados de revisão e confirmação, origem SISTEMA e o que a importação do banco guarda.
--
-- revisao: A_REVISAR = chegou do banco e o casal ainda não conferiu (fora do orçamento, D14); REVISADA = do casal.
-- confirmacao: A_CONFIRMAR = pendente no banco (PENDING); CONFIRMADA = lançada. Os lançamentos de hoje ficam REVISADA e
-- CONFIRMADA pelo DEFAULT.
-- Sem categoria só "a revisar" (D15, corrigida), exceto transferência, que continua sempre sem categoria.
-- SISTEMA = criado pelo sistema em conta ligada (rendimento, ajuste, lado manual de um par; D12).
-- Parcelas: o limite de 24 é do parcelamento manual; parcela do banco vem solta, com o N/M dele (D13).
-- categoria_externa: código da categoria da Pluggy (etapas 5 e 6). fatura_id_externo: billId da Pluggy, só para conferência
-- (a fatura do sistema é a do calendário, D2). Os dois são ids opacos do banco, não dado pessoal.

ALTER TABLE lancamentos.transacoes
    ADD COLUMN revisao text NOT NULL DEFAULT 'REVISADA' CONSTRAINT ck_transacoes_revisao CHECK (revisao IN ('A_REVISAR', 'REVISADA')),
    ADD COLUMN confirmacao text NOT NULL DEFAULT 'CONFIRMADA' CONSTRAINT ck_transacoes_confirmacao CHECK (confirmacao IN ('A_CONFIRMAR', 'CONFIRMADA')),
    ADD COLUMN categoria_externa text NULL CONSTRAINT ck_transacoes_categoria_externa CHECK (length(categoria_externa) BETWEEN 1 AND 20),
    ADD COLUMN fatura_id_externo text NULL CONSTRAINT ck_transacoes_fatura_id_externo CHECK (length(fatura_id_externo) BETWEEN 1 AND 100);

ALTER TABLE lancamentos.transacoes DROP CONSTRAINT transacoes_origem_check;
ALTER TABLE lancamentos.transacoes ADD CONSTRAINT ck_transacoes_origem
    CHECK (origem IN ('MANUAL', 'OPEN_FINANCE', 'WHATSAPP', 'SISTEMA'));

ALTER TABLE lancamentos.transacoes ADD CONSTRAINT ck_transacoes_categoria_ou_a_revisar
    CHECK (categoria_id IS NOT NULL OR revisao = 'A_REVISAR' OR natureza = 'TRANSFERENCIA');

ALTER TABLE lancamentos.transacoes DROP CONSTRAINT ck_transacoes_parcelas;
ALTER TABLE lancamentos.transacoes ADD CONSTRAINT ck_transacoes_parcelas CHECK (
    (parcela IS NULL AND parcelas IS NULL)
    OR (parcela IS NOT NULL AND parcelas IS NOT NULL AND parcela >= 1 AND parcela <= parcelas
        AND (origem <> 'MANUAL' OR parcelas <= 24)));

-- A importação procura por conta e data (janela desde o histórico) e as pendentes por conta.
CREATE INDEX ix_transacoes_conta_confirmacao ON lancamentos.transacoes (conta_id, confirmacao) WHERE NOT is_deleted AND confirmacao = 'A_CONFIRMAR';
