---
paths:
  - "backend/**"
  - "db/**"
---

# Backend

- Um módulo = um projeto `GestorFinanceiro.Modules.<X>`, schema `<x>` no Postgres, um `DbContext` que herda `ModuloDbContext`.
- Pastas dentro do módulo: `Contracts/` (público), `Dominio/`, `Aplicacao/`, `Infra/`, `Api/`. Domínio não depende de `Infra` nem de ASP.NET.
- Entidades herdam `Entidade` (Id = `Guid.CreateVersion7()`) e implementam `ISoftDeletable` e, se forem do casal, `ICasalScoped`.
- Colunas em snake_case são geradas pela convenção do `ModuloDbContext`; nomes de tabela sempre explícitos em `ToTable("plural", "schema")`.
- Comunicação entre módulos: interface em `Contracts` (síncrona) ou evento de domínio. Nunca referência a `internal` de outro módulo.
- Integrações externas (Pluggy, gateway, Gemini) ficam atrás de uma interface no módulo `Integracoes`; nos testes, usar fakes.
- Webhook: validar, gravar o id do evento (único), responder 202 em menos de 10 s e processar em job. Erro temporário → 5xx.
- Migração nova: tabela + índices parciais + `SELECT audit.proteger(...)` + grants no mesmo arquivo.
