-- V0014 — Fase 2, etapa 3: banco do item pelo código COMPE (decisão do CEO, D18).
--
-- No Meu Pluggy o connector do item é o do próprio Meu Pluggy, igual para todos os bancos; por isso o banco vem do código
-- COMPE (3 dígitos) que a pessoa escolhe numa lista fixa (ou em "Outro"). Ele entra na identidade da conta física
-- (integracoes.vinculos.identidade_hmac = HMAC de código + agência + conta): agência e conta iguais em bancos diferentes
-- não colidem. Nulo só nos items cadastrados antes desta migração; a descoberta pede a escolha antes de mostrar as contas.

ALTER TABLE integracoes.items ADD COLUMN codigo_banco char(3) NULL CHECK (codigo_banco ~ '^[0-9]{3}$');
