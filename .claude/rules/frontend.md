---
paths:
  - "frontend/**"
---

# Front

- Só fala com a própria API (`/api/v1`) pelo cliente gerado em `src/api/`. Nunca chamar Pluggy, Gemini ou qualquer terceiro do navegador.
- Tipos da API vêm de `src/api/schema.d.ts` (gerado por `npm run gen:api`); não escrever tipos de resposta à mão.
- Dados de servidor com TanStack Query; estado de tela com `useState`. Nada de dado financeiro em `localStorage`.
- Visual: tokens em `src/styles/tokens.css` (tema escuro, Geist / Geist Mono). Valores monetários sempre com a classe `num` e `formatarReais`.
- Acessibilidade: `<button>` e `<a>` reais, `label` em todo input, contraste 4.5:1.
- Desktop primeiro; abaixo de 900 px a barra lateral vira topo.
