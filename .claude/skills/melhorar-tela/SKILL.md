---
name: melhorar-tela
description: Use para qualquer melhoria visual ou de interação numa tela do front (hierarquia, espaçamento, estados, microinterações, componentes animados do AnimatedWebs). Orquestra frontend-design, interaction-design e animatedwebs-components dentro das regras de .claude/rules/frontend.md e da referência docs/design/.
---

# Melhorar uma tela

Uma tela de finanças boa responde rápido "quanto ainda dá", "o que vence" e "o que mudou". O visual serve a isso.

## Antes de mexer

1. Leia `.claude/rules/frontend.md` (inclusive "Movimento e AnimatedWebs"), `docs/design/README.md` e a prancha da tela em `docs/design/telas/`.
2. Leia a ordem de serviço do CEO: ela diz o objetivo da tela e o que está fora do escopo. Sem objetivo claro, pergunte antes.
3. Liste o que a tela tem hoje: estados (carregando, vazio, erro, sucesso), ações e números. Melhorias começam pelos estados que faltam, não pela animação.

## Ordem de trabalho

1. **Estrutura** (`frontend-design`, `design-elevation`): hierarquia, alinhamento, espaçamento e tipografia com os tokens existentes. Nenhuma cor nova sem ser token.
2. **Estados e feedback** (`interaction-design`): carregando (esqueleto, não spinner solto), vazio com próxima ação, erro com como resolver, confirmação depois de salvar.
3. **Movimento** (`animatedwebs-components`, só depois de 1 e 2): procure o componente, leia o código e **reescreva** em `src/componentes/` com CSS do projeto e os tokens `--mov-*`. Se pedir biblioteca, pare e avise o CEO.
4. **Textos**: rótulos e mensagens seguem a spec; se a spec não tiver o texto, proponha e marque para o QA (`/revisar-textos`).

## Testes

- Lógica de exibição (formatação, estados, regras) em `src/lib/*.test.ts`, com teste antes do código.
- Nada de teste que dependa de tempo de animação.
- `npm --prefix frontend run check` e `scripts/verificar.sh` verdes.

## Entrega

Commit `Área: o que mudou na tela`. Peça ao QA, em uma linha: `/testar-no-navegador` e `/auditar-acessibilidade` no branch `<branch>`, telas `<rotas>`, em 1440 px, 800 px e com movimento reduzido.
