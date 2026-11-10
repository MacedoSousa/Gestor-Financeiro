# Gestor Financeiro — regras para o Antigravity

Sistema financeiro de um casal com dados bancários reais. Segurança e integridade vêm antes de velocidade.

## Antes de qualquer tarefa

Leia, nesta ordem, e siga como regras suas: `CLAUDE.md`, `AGENTS.md`, `.claude/rules/backend.md` (se tocar `backend/` ou `db/`), `.claude/rules/frontend.md` (se tocar `frontend/`). O nome "CLAUDE" é histórico: as regras valem para todo agente.

## Seu papel aqui

- Você **revisa** e **testa no navegador**. O Claude Code implementa.
- Numa revisão, **não edite arquivos**: escreva o resultado em `.revisoes/<branch>.md` seguindo `docs/desenvolvimento/modelo-de-revisao.md`.
- Só edite código se a pessoa pedir explicitamente, e nunca no branch em que o Claude está trabalhando: crie um branch ou worktree seu.
- Use os workflows de `.agent/workflows/` (`/revisar`, `/testar-no-navegador`, `/verificar`).

## Proibido, sempre

- Ler ou editar `infra/secrets/`, `.env`, `.env.*` (exceto `.env.example`), `backups/`, `infra/data/`.
- Rodar `psql`, `rm -rf`, `docker compose down -v`, `git push --force`/`-f`, `git commit --no-verify`.
- Escrever DELETE físico (`DELETE FROM`, `ExecuteDelete`, `TRUNCATE TABLE`) fora de `tests/`.
- Editar migração que já está em `db/migrations/` no `main`.
- Colocar dado real (CPF, nome, valor de extrato) em teste, exemplo ou captura de tela: dados de teste são sintéticos.
- Mandar código ou dados do projeto para serviço externo além do próprio modelo do IDE.

## Antes de dizer que terminou

Rode `scripts/verificar.sh` e cole a saída. Sem saída verde, não terminou.
