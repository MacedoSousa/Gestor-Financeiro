---
description: Auditoria WCAG 2.2 AA das telas mudadas no branch, sem editar código
---

1. Leia `.claude/rules/frontend.md` (acessibilidade e "Movimento e AnimatedWebs"). Descubra as telas mudadas: `git diff main...HEAD --stat -- frontend/`.
2. Suba o ambiente local com dados sintéticos (como em `/testar-no-navegador`).
3. Em cada tela, confira e anote o critério WCAG de cada falha:
   - **Teclado:** tudo alcançável com Tab/Shift+Tab, ordem lógica, Enter/Espaço acionam, Esc fecha diálogo, foco volta ao gatilho, foco sempre visível (2.1.1, 2.4.3, 2.4.7, 2.4.11).
   - **Nomes e estrutura:** `<button>`/`<a>` reais, `label` em todo campo, um `h1`, títulos em ordem, regiões (`main`, `nav`) (1.3.1, 4.1.2).
   - **Contraste:** texto 4.5:1, texto grande e ícones 3:1, inclusive em hover, foco e desabilitado (1.4.3, 1.4.11).
   - **Movimento:** com "reduzir movimento" ligado no sistema, nada anima e nada se perde; nenhuma animação passa de 320 ms nem pisca (2.3.1, 2.3.3).
   - **Números:** valores com a classe `num`; o leitor de tela lê o valor real, sem contagem animada; mudanças importantes anunciadas (`role="status"`/`alert`) (4.1.3).
   - **Zoom e largura:** 200% de zoom e 800 px sem perder conteúdo nem rolar para o lado (1.4.4, 1.4.10).
   - **Alvos:** botões e links com pelo menos 24×24 px (2.5.8).
4. Entregue a revisão no texto da sua resposta, no formato de `docs/desenvolvimento/modelo-de-revisao.md`. Não grave arquivo e não use gh: o Dev salva e publica no PR. Os achados vão na seção "Acessibilidade" do texto entregue (mesma tabela do modelo, com a coluna Referência = critério WCAG). Falha de teclado, foco ou contraste é **Bloqueia**.
