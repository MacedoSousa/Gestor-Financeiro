# Agentes neste repositório

Dois agentes trabalham aqui, com papéis diferentes e **as mesmas regras**. A fonte das regras é uma só:

1. [`CLAUDE.md`](CLAUDE.md) — regras que não se negociam e comandos. Vale para qualquer agente, não só para o Claude.
2. [`.claude/rules/backend.md`](.claude/rules/backend.md) e [`.claude/rules/frontend.md`](.claude/rules/frontend.md) — regras por pasta.
3. [`docs/SPEC.md`](docs/SPEC.md) e [`docs/adr/`](docs/adr/) — arquitetura e decisões.

Nenhum arquivo de regra de agente (este, `.agent/rules/`) repete regra: só aponta para os de cima.

## Papéis (ADR-0012)

| Agente | Faz | Não faz |
| --- | --- | --- |
| **Claude Code** | Spec, plano, implementação com TDD, commits | Revisar o próprio código como revisão final |
| **Antigravity** | Revisão cruzada de diffs (modelo Gemini), teste no navegador contra `docs/design/`, leitura do repositório inteiro | Editar código no mesmo branch enquanto o Claude implementa |
| **CI** | Decide: guardas, testes, contrato OpenAPI, gitleaks | — |

Um escritor por vez. Agentes em paralelo só em worktrees ou branches separados.

## Guardas (valem para todos)

- `.githooks/pre-commit` — bloqueia DELETE físico, segredo no stage, migração já commitada alterada, migração sem `audit.proteger`, `UNIQUE` sem índice parcial e buraco na numeração. Ligue com `scripts/instalar-hooks.sh`.
- `scripts/verificar.sh` — roda guardas e testes da parte que mudou. Ninguém diz "terminei" sem a saída verde dele.
- CI (`.github/workflows/ci.yml`) — roda as mesmas guardas no repositório inteiro; `--no-verify` não escapa.

## Passagem de trabalho

Revisões e relatórios de teste no navegador vão para `.revisoes/<branch>.md` (fora do git), no formato de [`docs/desenvolvimento/modelo-de-revisao.md`](docs/desenvolvimento/modelo-de-revisao.md). Processo completo: [`docs/desenvolvimento/dois-agentes.md`](docs/desenvolvimento/dois-agentes.md).
