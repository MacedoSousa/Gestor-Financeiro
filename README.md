# Gestor Financeiro

Sistema financeiro do casal: conta conjunta Itaú, cartões, investimentos, boletos e gastos do dia a dia num só lugar, rodando no PC de casa. Responde três perguntas: **quanto ainda dá para gastar este mês, quanto dá para investir e quando chegamos à casa e ao carro.**

- Arquitetura completa: [`docs/SPEC.md`](docs/SPEC.md) e decisões em [`docs/adr/`](docs/adr/)
- Regras para o Claude Code: [`CLAUDE.md`](CLAUDE.md)

## Stack

| Camada | Tecnologia |
| --- | --- |
| API | .NET 10 (ASP.NET Core Minimal APIs), EF Core 10, monólito modular |
| Banco | PostgreSQL 17 — schema por módulo, auditoria por trigger, sem DELETE físico |
| Front | React 19 + TypeScript + Vite, cliente gerado do OpenAPI |
| Infra | Docker Compose, Caddy, DuckDNS, Ubuntu 24.04 com LUKS |
| Integrações | Meu Pluggy (Open Finance), gateway WhatsApp, Gemini (tier pago), OCR local na GPU |

## Estrutura

```
backend/
  src/SharedKernel/         Dinheiro, soft delete, filtros, criptografia
  src/Modules/<Modulo>/     um projeto por módulo; só o namespace Contracts é público
  src/Host/GestorFinanceiro.Api/
  tests/                    testes unitários e de arquitetura
db/migrations/              SQL versionado (Vxxxx__descricao.sql)
contracts/                  openapi.json gerado no build da API
frontend/                   SPA React
infra/                      docker-compose, Caddy, scripts do Postgres
docs/                       SPEC e ADRs
```

## Rodando localmente

Pré-requisitos: .NET SDK 10, Node 22, Docker.

```bash
# 1. Segredos locais (uma vez)
./infra/scripts/gerar-segredos.sh          # cria infra/secrets/* (fora do git)

# 2. Banco
docker compose -f infra/docker-compose.yml up -d postgres

# 3. Migrações (papel gf_migrator) e API
cd backend
dotnet run --project src/Host/GestorFinanceiro.Api -- --migrar
dotnet run --project src/Host/GestorFinanceiro.Api

# 4. Front
cd ../frontend && npm ci && npm run dev
```

Testes: `dotnet test backend/GestorFinanceiro.slnx` e `npm --prefix frontend run check`.

## Fases

| Fase | Entrega | Pronto quando |
| --- | --- | --- |
| 0 · Fundação | Repo, CI, login, chaves, soft delete, auditoria | Um `DELETE` no banco falha e a cadeia de auditoria confere |
| 1 · Controle manual | Contas, lançamentos, orçamento, dashboard | Um mês inteiro fechado à mão |
| 2 · Open Finance | Meu Pluggy, webhook, conciliação | Conta conjunta sem duplicatas |
| 3 · WhatsApp | Gateway, parser, comprovantes na GPU | O bot responde o saldo da categoria |
| 4 · Assistente | FinanceEngine, Gemini, relatório mensal | Todo número do texto confere com o cálculo |
| 5 · Endurecimento | Backups fora de casa, hash fora do PC | Restauração ensaiada |
