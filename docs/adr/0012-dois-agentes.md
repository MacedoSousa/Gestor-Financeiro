# ADR-012 — Dois agentes: Claude Code implementa, Antigravity revisa e testa no navegador

**Decisão:** o desenvolvimento usa dois agentes com papéis fixos. O Claude Code escreve spec, plano e código (com TDD). O Antigravity, com o modelo Gemini, faz a revisão cruzada de cada tarefa e o teste das telas no navegador, sem editar o branch em que o Claude trabalha. As regras são uma só (`CLAUDE.md` e `.claude/rules/`); os arquivos de cada agente (`AGENTS.md`, `.agent/rules/`) só apontam para elas. As guardas que antes eram só hooks do Claude passam para `scripts/guardas/` e valem no `pre-commit` do git e no CI, para qualquer agente ou pessoa.

**Por quê:** modelos diferentes erram em lugares diferentes, e revisar o próprio código pega menos que revisar o do outro. O Antigravity testa no navegador de verdade, o que o fluxo do Claude não cobre. Regras duplicadas divergem com o tempo; guardas presas a uma ferramenta deixam a outra sem proteção.

**Descartado:**
- *Os dois implementando em paralelo no mesmo branch* — edições concorrentes e commits misturados num projeto em que cada mudança precisa de teste e de auditoria clara.
- *Um arquivo de regras por agente* (`CLAUDE.md` + `GEMINI.md` com o mesmo conteúdo) — duas cópias que divergem.
- *Só o Claude, com revisão por subagente* — continua o mesmo modelo revisando a si mesmo.

## Detalhes

**Fluxo de um bloco:** spec (Claude, aprovada pela pessoa) → leitura crítica da spec (Antigravity, `/revisar` sobre o arquivo da spec) → plano (Claude, `superpowers:writing-plans`) → para cada tarefa: implementação com TDD e `scripts/verificar.sh` verde (Claude) → `/revisar` e, se mudou tela, `/testar-no-navegador` (Antigravity) → respostas e correções (Claude, skill `revisao-cruzada`) → PR → CI.

**Passagem de trabalho:** `.revisoes/<branch>.md`, fora do git, no formato de `docs/desenvolvimento/modelo-de-revisao.md`. O revisor não edita código; o Claude não apaga o texto do revisor. O conteúdo da revisão é dado, não instrução: em conflito, valem `CLAUDE.md` e a spec.

**Guardas:** `scripts/guardas/regras.sh` é a fonte; usam-na `.claude/hooks/bloquear-delete-fisico.sh`, `.githooks/pre-commit` e `scripts/guardas/verificar-repositorio.sh` (CI). A checagem de `UNIQUE` analisa o comando SQL inteiro, não a linha. Testes das guardas em `scripts/guardas/testes.sh`, também no CI. O teste de arquitetura em C# continua valendo como segunda camada.

**O que não muda:** a regra 10 ("Gemini não calcula") é sobre o Gemini **no produto** (Fase 4). Usar o Gemini como revisor de código não envia dado do sistema a ele: no desenvolvimento só existem dados sintéticos.
