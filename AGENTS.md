# Agentes neste repositório

Três papéis trabalham aqui (CEO, Dev e QA), com **as mesmas regras**. A fonte das regras é uma só:

1. [`CLAUDE.md`](CLAUDE.md) — regras que não se negociam e comandos. Vale para qualquer agente, não só para o Claude.
2. [`.claude/rules/backend.md`](.claude/rules/backend.md) e [`.claude/rules/frontend.md`](.claude/rules/frontend.md) — regras por pasta.
3. [`docs/SPEC.md`](docs/SPEC.md) e [`docs/adr/`](docs/adr/) — arquitetura e decisões.

Nenhum arquivo de regra de agente (este, `.agent/rules/`) repete regra: só aponta para os de cima.

## Papéis (ADR-012)

| Papel | Quem | Faz | Não faz |
| --- | --- | --- | --- |
| **CEO** | Claude no claude.ai | Prioridade, specs, ADRs, regras, ordens de serviço (`docs/desenvolvimento/despacho.md`) | Código |
| **Dev fullstack sênior** | Claude Code | Plano, implementação com TDD, migrações, respostas às revisões, commits | Aprovar a própria entrega |
| **QA e documentação** | Antigravity (Gemini) | Revisão, teste no navegador, acessibilidade, textos, documentação do que já existe | Código, regras, specs, ADRs |
| **Decide** | Pessoa + CI | Aprova spec, plano e PR; CI roda guardas e testes | — |

Skills de cada papel: [`docs/desenvolvimento/papeis-e-skills.md`](docs/desenvolvimento/papeis-e-skills.md). Um escritor por vez em cada branch.

## Guardas (valem para todos)

- `.githooks/pre-commit` — bloqueia DELETE físico, segredo no stage, migração já commitada alterada, migração sem `audit.proteger`, `UNIQUE` sem índice parcial e buraco na numeração. Ligue com `scripts/instalar-hooks.sh`.
- `scripts/verificar.sh` — roda guardas e testes da parte que mudou. Ninguém diz "terminei" sem a saída verde dele.
- CI (`.github/workflows/ci.yml`) — roda as mesmas guardas no repositório inteiro; `--no-verify` não escapa.
- **Merge:** só pela pessoa, com os Checks verdes e o sinal do CEO, que cita o commit do topo do PR ("merge do #N no commit abc1234"). A pessoa só faz o merge se o último commit na aba Commits do PR for esse. Antes de enviar um commit para um PR, o Dev confere se ele ainda está aberto (`gh pr view <n> --json state`); se já foi mergeado, rebase sobre o `main` e PR novo. Todo relato do Dev termina com o commit do topo do PR. Commit `WIP:` não entra no `main`.

## Passagem de trabalho

O QA entrega revisões e relatórios de teste no navegador no texto da resposta, no formato de [`docs/desenvolvimento/modelo-de-revisao.md`](docs/desenvolvimento/modelo-de-revisao.md), sem gravar arquivo nem usar gh. O Dev salva o texto em `.revisoes/<branch>.md` (fora do git), publica no PR com `gh pr comment` e responde aos achados ali. Processo completo: [`docs/desenvolvimento/dois-agentes.md`](docs/desenvolvimento/dois-agentes.md).
