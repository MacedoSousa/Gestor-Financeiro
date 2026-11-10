# Papéis e skills

Três papéis, regras únicas (`CLAUDE.md`). Decisão: ADR-012 (atualização de papéis).

| Papel | Quem | Onde roda | Decide | Escreve |
| --- | --- | --- | --- | --- |
| **CEO** | Claude no claude.ai (Projeto "Gestor Financeiro") | Chat | Prioridade, escopo, regra de negócio, arquitetura (ADR), aprovação de spec | Specs, ADRs, `CLAUDE.md`, `.claude/rules/`, ordens de serviço |
| **Dev fullstack sênior** | Claude Code | VS Code (painel ou `claude` no terminal) | Como implementar dentro da spec | Plano, código, testes, migrações, commits |
| **QA e documentação** | Antigravity (Gemini) | Janela de agentes | Se a entrega passa ou não | `.revisoes/<branch>.md`; README, SPEC (Entregue/Próximas), guias em `docs/desenvolvimento/` e `docs/guia/` |
| **Pessoa** | você | Terminal | Aprova spec, plano e PR; responde perguntas de produto | git, PR, merge |

Ninguém faz o papel do outro: o CEO não escreve código, o Dev não aprova a própria entrega, o QA não muda código nem regra.

## CEO — skills (claude.ai)

| Skill | Para quê neste projeto |
| --- | --- |
| `brainstorming` | Perguntas, abordagens e design por seções antes de cada bloco |
| `architecture`, `system-design` | ADR e modelo de dados/contratos dos módulos |
| `tech-debt` | Priorizar dívida técnica entre blocos |
| `ux-strategy`, `journey-mapping` | Decidir o que a tela precisa responder ao casal (antes do visual) |
| `ux-copy`, `ux-writing` | Textos da spec: rótulos, estados, erros em pt-BR |
| `code-review` | Revisão de arquitetura de um PR quando o QA levanta dúvida de desenho |
| `docs` | Documento compartilhável fora do repositório, quando pedido |

Ferramenta do CEO: `docs/desenvolvimento/despacho.md` (ordens de serviço).

## Dev — skills (Claude Code)

Precisam estar instaladas no Claude Code (plugins `superpowers`, AnimatedWebs, design, engenharia, docker). As do projeto vêm com o repositório.

| Skill | Quando |
| --- | --- |
| `superpowers:writing-plans` | Depois da spec aprovada |
| `superpowers:executing-plans` / `subagent-driven-development` | Tarefa a tarefa do plano |
| `superpowers:test-driven-development` | Sempre: teste que falha primeiro |
| `superpowers:systematic-debugging` | Qualquer falha antes de propor correção |
| `superpowers:verification-before-completion` | Antes de dizer "pronto": `scripts/verificar.sh` |
| `superpowers:using-git-worktrees` | Trabalho em paralelo com o QA |
| `superpowers:receiving-code-review` + `revisao-cruzada` (projeto) | Responder `.revisoes/<branch>.md` |
| `superpowers:finishing-a-development-branch` | Fechar o branch para PR |
| `nova-migracao` (projeto) | Toda mudança de schema |
| `melhorar-tela` (projeto) | Toda melhoria visual; orquestra as de baixo dentro das regras do front |
| `frontend-design`, `design-elevation` | Hierarquia, espaçamento, tipografia, com os tokens existentes |
| `animatedwebs-components` | Microinterações e componentes animados, nos limites de `.claude/rules/frontend.md` |
| `interaction-design` | Estados, transições e feedback das ações |
| `docker-compose-patterns`, `docker-build-strategies` | Infra: fixar imagens, endurecer containers |

**Fora do projeto, de propósito:** `animatedwebs-site`, `animatedwebs-hero`, `animatedwebs-backgrounds` (vídeo e WebGL de página de marketing; a CSP bloqueia mídia externa e não combina com um painel financeiro) e `animatedwebs-style` (trocaria o design system; só com ADR do CEO).

## QA e documentação — workflows (Antigravity)

O Antigravity não usa as skills do Claude; as mesmas práticas viram workflows em `.agent/workflows/`.

| Workflow | Origem da prática | Saída |
| --- | --- | --- |
| `/revisar` | `code-review` (segurança, correção, regras 1–10) | `.revisoes/<branch>.md` |
| `/testar-no-navegador` | `artifact`/browser + `docs/design/` | seção "Navegador" |
| `/auditar-acessibilidade` | `accessibility-audit` (WCAG 2.2 AA) | seção "Acessibilidade" |
| `/revisar-textos` | `ux-writing` | seção "Textos" |
| `/documentar` | `documentation` | commit só em arquivos de documentação |
| `/verificar` | `verification-before-completion` | saída do `scripts/verificar.sh` |
