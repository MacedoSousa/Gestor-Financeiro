# ADR-010 — Jobs e filas no PostgreSQL

**Decisão:** jobs agendados e filas (sync da Pluggy, processamento de webhook e de mensagens, relatório mensal) usam Hangfire com armazenamento no próprio PostgreSQL.

**Por quê:** um serviço a menos para manter, proteger e fazer backup.

**Descartado:** Redis.

**Atualização (11/11/2026, Fase 2):** o primeiro job (sincronização diária da Pluggy) usa um serviço em segundo plano da API com timer e a tabela `integracoes.execucoes`, sem Hangfire: o Hangfire cria o próprio schema e apaga jobs fisicamente, o que contraria a regra 1 e as permissões do `gf_app`. O Hangfire volta a ser avaliado quando houver fila de verdade (Fase 3), com papel e schema próprios como exceção documentada.
