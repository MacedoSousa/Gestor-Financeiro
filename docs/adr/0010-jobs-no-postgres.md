# ADR-010 — Jobs e filas no PostgreSQL

**Decisão:** jobs agendados e filas (sync da Pluggy, processamento de webhook e de mensagens, relatório mensal) usam Hangfire com armazenamento no próprio PostgreSQL.

**Por quê:** um serviço a menos para manter, proteger e fazer backup.

**Descartado:** Redis.
