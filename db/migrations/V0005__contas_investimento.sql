-- V0005 — Contas: novo tipo INVESTIMENTO (conta de investimento ou reserva, destino de aportes por transferência).
-- O CHECK do tipo foi declarado inline na V0003, por isso o Postgres o batizou de contas_tipo_check.

ALTER TABLE contas.contas DROP CONSTRAINT contas_tipo_check;
ALTER TABLE contas.contas ADD CONSTRAINT contas_tipo_check CHECK (tipo IN ('CORRENTE', 'POUPANCA', 'INVESTIMENTO', 'CARTAO', 'DINHEIRO'));
