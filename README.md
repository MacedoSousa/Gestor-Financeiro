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
| Infra | Docker Compose, Caddy, DuckDNS, Ubuntu 26.04 com LUKS |
| Integrações | Meu Pluggy (Open Finance), gateway WhatsApp, Gemini (tier pago), OCR local na GPU |

## Estrutura

```
backend/
  src/SharedKernel/         Dinheiro, soft delete, filtros, criptografia
  src/Modules/<Modulo>/     um projeto por módulo; só o namespace Contracts é público
  src/Host/GestorFinanceiro.Api/
  tests/                    testes unitários, de arquitetura e de integração (Testcontainers)
db/migrations/              SQL versionado (Vxxxx__descricao.sql)
contracts/                  openapi.json gerado no build da API
frontend/                   SPA React
infra/                      docker-compose, Caddy, scripts do Postgres
docs/                       SPEC, ADRs, telas de referência (design/), specs e planos (superpowers/)
```

## Rodando localmente

Pré-requisitos: .NET SDK 10, Node 22 ou mais novo, Docker.

```bash
# 1. Segredos locais (uma vez)
./infra/scripts/gerar-segredos.sh          # cria infra/secrets/* (fora do git)

# 2. Banco
docker compose -f infra/docker-compose.yml up -d postgres

# 3. Migrações (papel gf_migrator) e chaves (uma vez)
API=backend/src/Host/GestorFinanceiro.Api
dotnet run --project $API -- --migrar
dotnet run --project $API -- --inicializar-chaves
dotnet run --project $API -- --codigo-instalacao   # código de uso único, vale 24 h

# 4. API e front
dotnet run --project $API
npm --prefix frontend ci && npm --prefix frontend run dev
```

Primeiro uso: abra `/cadastro`, preencham juntos com o código de instalação e **imprimam o código de recuperação** que aparece no fim (ele só aparece uma vez). Depois, cada um faz o primeiro acesso com CPF + senha e registra a passkey e o TOTP.

Testes: `dotnet test backend/GestorFinanceiro.slnx` (unitários, arquitetura e integração; a integração precisa do Docker) e `npm --prefix frontend run check` (tipos, ESLint, Vitest e build).

## Sempre ligado (servidor de casa)

O sistema sobe sozinho quando o PC liga:

1. **Serviço:** copie `infra/systemd/gestor-financeiro.service` para `/etc/systemd/system/`, ajuste `WorkingDirectory` e rode `sudo systemctl enable --now docker gestor-financeiro`. Todo container já tem `restart: unless-stopped`.
2. **Disco cifrado sem digitar senha no boot:** com LUKS, destrave pelo chip TPM2 (`sudo systemd-cryptenroll --tpm2-device=auto --tpm2-pcrs=7 /dev/<particao>`) e mantenha a senha do LUKS impressa como reserva.
3. **Energia:** na BIOS, ative "Restore on AC Power Loss = Power On"; no Ubuntu, desligue a suspensão (`sudo systemctl mask sleep.target suspend.target hibernate.target hybrid-sleep.target`). Um nobreak pequeno evita desligar no meio de uma gravação.
4. **Atualizações:** `unattended-upgrades` ligado; reinício automático de madrugada (`Unattended-Upgrade::Automatic-Reboot-Time "04:00"`).

## Fases

| Fase | Entrega | Situação |
| --- | --- | --- |
| 0 · Fundação | Repo, CI, cofre, soft delete, auditoria | Concluída |
| 1 · Controle manual | Cadastro, login, contas, lançamentos, orçamento, metas, dashboard | Código concluído; fecha após um mês real conferido com o extrato do Itaú |
| 2 · Open Finance | Meu Pluggy, webhook, conciliação | — |
| 3 · WhatsApp | Gateway, parser, comprovantes na GPU | — |
| 4 · Assistente | FinanceEngine, Gemini, relatório mensal | — |
| 5 · Endurecimento | Backups fora de casa, hash fora do PC | — |

Detalhes do que já entrou e do que vem em seguida: [`docs/SPEC.md`](docs/SPEC.md).
