# Despacho: ordens de serviço do CEO

O CEO (Claude no claude.ai) lê o estado do projeto e manda a cada papel só o que ele precisa. Os agentes não conversam entre si: a pessoa leva a ordem de um lado para o outro, e o estado fica no repositório (plano com caixas marcadas, `.revisoes/<branch>.md`, saída do `scripts/verificar.sh`).

## O que o CEO lê antes de despachar

A pessoa cola, ou o CEO pede:

```bash
git branch --show-current && git status --short && git log --oneline -5
grep -n "\- \[ \]" docs/superpowers/plans/<plano-do-bloco>.md | head -5   # próximas tarefas
cat .revisoes/<branch>.md 2>/dev/null                                      # achados abertos
scripts/verificar.sh 2>&1 | tail -20                                       # último resultado
```

## Roteamento

| Situação | Vai para |
| --- | --- |
| Ideia, prioridade, regra de negócio, escopo, aprovação | CEO decide (pergunta à pessoa se faltar dado) |
| Spec nova ou mudança de regra/ADR | CEO escreve; pessoa aprova |
| Spec aprovada sem plano | Dev: `writing-plans` |
| Plano com tarefa aberta e branch limpo | Dev: próxima tarefa, com TDD |
| Tarefa commitada sem revisão | QA: `/revisar` (+ `/testar-no-navegador`, `/auditar-acessibilidade`, `/revisar-textos` se mudou tela) |
| `.revisoes` com achado sem resposta | Dev: skill `revisao-cruzada` |
| Bloco fechado, revisões respondidas | QA: `/documentar`; depois a pessoa abre o PR |
| `verificar.sh` ou CI vermelho | Dev: `systematic-debugging`, nada de tarefa nova até ficar verde |
| Achado que questiona a spec ou o desenho | CEO decide; vira mudança de spec ou "Discordo" registrado |

Ordens independentes podem rodar em paralelo só em worktrees separados. Nunca dois escritores no mesmo branch.

## Formato da ordem de serviço

```markdown
### Ordem <n> · <Dev | QA> · <modo: painel do Claude Code | janela de agentes do Antigravity | terminal>
- Branch: <nome> (<criar a partir de X | já existe>)
- Leia só: <arquivos e seções exatas>
- Tarefa: <uma coisa, verificável>
- Fora do escopo: <o que não fazer>
- Pronto quando: <teste/saída esperada>; `scripts/verificar.sh` verde colado
- Entregue em: <commit `Área: ...` | `.revisoes/<branch>.md` seção X>
- Depois: <próxima ordem ou "volte ao CEO">
```

O CEO nunca põe na ordem segredo, conteúdo de `infra/secrets/` ou dado real; e nunca pula a aprovação da pessoa em spec e plano.
