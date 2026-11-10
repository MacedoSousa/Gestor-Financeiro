---
description: Testa as telas do branch no navegador e compara com docs/design/telas
---

1. Leia `.claude/rules/frontend.md` e `docs/design/README.md`. Descubra quais telas o branch mudou (`git diff main...HEAD --stat -- frontend/`).
2. Suba o ambiente local com **dados sintéticos** (veja "Rodando localmente" no `README.md`; API com `GF_DEV_CHAVE_EFEMERA=1` em Development). Nunca use o banco de produção.
3. Para cada tela mudada, abra no navegador em 1440 px e em 800 px (abaixo de 900 px a barra lateral vira topo):
   - compare com a prancha correspondente em `docs/design/telas/` (layout, hierarquia, espaçamento, um único destaque verde-água);
   - percorra os fluxos do plano do bloco, incluindo erro (campo inválido, 409, sessão expirada);
   - confira acessibilidade: navegação só por teclado, foco visível, `label` em todo campo, contraste 4.5:1, valores com a classe `num`.
4. Tire capturas só com dados sintéticos e guarde-as fora do git.
5. Anote os achados em `.revisoes/<branch>.md`, seção "Navegador", no formato do modelo. Não edite código.
