-- V0018 — Fase 2, ordem D21 do CEO: investimentos visíveis antes da E6.
--
-- valor_banco_centavos: só de vínculo de aplicação (INVESTIMENTO). É o balance (valor líquido atual) que o banco mandou na
-- última sincronização. Nulo = ainda não sincronizado, ou o banco não mandou um valor válido (ausente, nulo, não numérico
-- ou com mais de 2 casas; D21, item 4). O grupo (a conta INVESTIMENTO) mostra a soma das aplicações dele.
-- A diferença do grupo para o saldo do sistema fica em diferenca_centavos de cada aplicação do grupo (a mesma em todas),
-- o mesmo padrão da conferência da corrente. Nada é lançado: o rendimento entra no sistema na E6.

ALTER TABLE integracoes.vinculos
    ADD COLUMN valor_banco_centavos bigint NULL
        CONSTRAINT ck_vinculos_valor_banco_so_de_aplicacao CHECK (valor_banco_centavos IS NULL OR tipo = 'INVESTIMENTO');
