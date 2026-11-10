# Desenvolvimento com Claude Code e Antigravity

Decisão e motivos: [ADR-012](../adr/0012-dois-agentes.md). Resumo para agentes: [`AGENTS.md`](../../AGENTS.md).

## Preparar o clone (uma vez)

```bash
scripts/instalar-hooks.sh                # liga .githooks/pre-commit para todo commit
npm --prefix frontend ci                 # o verificar.sh precisa do node_modules
scripts/guardas/testes.sh                # confere que as guardas funcionam neste clone
```

No VS Code/Antigravity, `Terminal → Run Task` mostra as tarefas de `.vscode/tasks.json`: **Verificar**, **Verificar tudo**, **Guardas**, **Testes do backend**, **Check do front**.

## Configurar o Antigravity

Os nomes de menu e de pasta abaixo são da versão em que este guia foi escrito; confira na sua.

1. **Regras do workspace:** o Antigravity lê `.agent/rules/` e `.agent/workflows/` deste repositório. A regra `00-gestor-financeiro.md` manda ler o `CLAUDE.md` antes de tudo. Não copie as regras para lá.
2. **Execução no terminal:** deixe a política em "pedir revisão" (não automática). Se usar lista de bloqueio, inclua: `psql`, `rm -rf`, `docker compose down -v`, `git push --force`, `git push -f`, `git commit --no-verify`. É a mesma lista de `deny` do `.claude/settings.json`.
3. **Arquivos sensíveis:** o Antigravity não lê o `.claude/settings.json`. Exclua do acesso do agente `infra/secrets/`, `.env`, `.env.*`, `backups/` e `infra/data/` pela configuração dele. O arquivo `.aiexclude` na raiz lista os mesmos caminhos para ferramentas Gemini que o leem. Na dúvida, mantenha segredos e backups fora da pasta do workspace.
4. **Modelo:** para revisão, use um modelo Gemini. Revisar com Claude dentro do Antigravity tira o ganho de ter outro modelo olhando.
5. **Vários agentes do Antigravity em paralelo:** um por worktree ou branch, nunca no branch do Claude.

## Fluxo de um bloco

| Etapa | Quem | Saída |
| --- | --- | --- |
| Spec | Claude (`superpowers:brainstorming`), aprovada pela pessoa | `docs/superpowers/specs/AAAA-MM-DD-<bloco>-design.md` |
| Leitura crítica da spec | Antigravity (`/revisar` apontando para a spec) | `.revisoes/<branch>.md` |
| Plano | Claude (`superpowers:writing-plans`) | `docs/superpowers/plans/AAAA-MM-DD-<bloco>.md` |
| Tarefa *n* | Claude: teste que falha → código → `scripts/verificar.sh` verde → commit | commit no branch `fase-N/<bloco>` |
| Revisão da tarefa | Antigravity: `/revisar`; se mudou tela, `/testar-no-navegador` | `.revisoes/<branch>.md` |
| Resposta | Claude: skill `revisao-cruzada` | correções com teste, respostas no arquivo |
| PR | pessoa | CI verde (guardas, testes, contrato, gitleaks) |

## Convenções que os dois seguem

- **Branch:** `fase-N/<bloco>` (ex.: `fase-1/boletos`). Correções de infraestrutura: `infra/<assunto>`; de documentação: `docs/<assunto>`.
- **Commit:** em português, `Área: o que mudou` (ex.: `Boletos: validação da linha digitável`), uma mudança com seu teste por commit.
- **Migração:** skill `nova-migracao`; o `pre-commit` recusa o que fugir dela.
- **Contrato:** mudou endpoint → `contracts/openapi.json` e `frontend/src/api/schema.d.ts` no mesmo commit.
- **Pronto:** só com a saída verde de `scripts/verificar.sh` colada. "AVISO: ... NÃO testado" significa que aquela parte não foi verificada.
- **Dados:** sempre sintéticos, inclusive em capturas de tela e na revisão.

## Quando um agente discorda do outro

Vale o que está escrito: `CLAUDE.md`, a spec aprovada e os testes. Se nenhum resolve, a decisão é da pessoa, e ela vai para a spec ou vira ADR. Nenhum agente "ganha" reescrevendo o trabalho do outro.
