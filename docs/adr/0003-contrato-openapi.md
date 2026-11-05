# ADR-003 — Contrato OpenAPI e cliente gerado

**Decisão:** o build da API gera `contracts/openapi.json`; o front gera `src/api/schema.d.ts` com `openapi-typescript` e chama a API com `openapi-fetch`. Rotas versionadas em `/api/v1`, erros em Problem Details (RFC 9457).

**Por quê:** front e API evoluem sem quebrar um ao outro; o CI falha se o contrato commitado divergir do gerado.

**Descartado:** tipos escritos à mão no front.
