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
- `npm run check` = tipos + lint (ESLint, zero avisos) + testes (Vitest) + build; é o que o CI roda. Teste de unidade fica ao lado do arquivo, em `*.test.ts`.
- Referência visual das telas: `docs/design/` (HTML exportado do canvas; leia o README de lá antes de criar uma tela).

## Movimento e AnimatedWebs

Animação aqui serve para orientar (o que mudou, o que está carregando, o que respondeu ao clique), nunca para enfeitar número.

- **Só CSS** (`transition`, `@keyframes`) com os tokens de movimento de `tokens.css` (`--mov-rapido`, `--mov-medio`, `--mov-lento`, `--curva`). Nenhuma duração acima de 320 ms; nada em laço, exceto indicador de carregamento.
- **`prefers-reduced-motion: reduce` desliga tudo** (regra global em `app.css`). Todo componente funciona e se entende sem animação.
- **Dinheiro não anima.** Nada de contagem subindo (count-up) nem número trocando aos poucos: o valor na tela e o lido pelo leitor de tela são sempre o real. Pode animar a barra de progresso, a opacidade de entrada de uma seção e o estado de um botão.
- **Só `transform` e `opacity`.** Nada que mude o tamanho do layout ou faça número pular de lugar.
- **Um único destaque verde-água por tela** continua valendo: brilho, aurora, gradiente animado ou glow não criam um segundo destaque.
- **Sem nada de fora:** a CSP do Caddy só permite a própria origem. Sem vídeo, imagem ou fonte de CDN, sem WebGL, sem canvas de fundo.
- **Componente do AnimatedWebs** (`animatedwebs-components`): é referência, não dependência. O código entra em `src/componentes/`, reescrito com os tokens e o CSS do projeto (o projeto não usa Tailwind). Se o componente exigir biblioteca nova (motion, gsap, three), pare: dependência nova é decisão do CEO.
- **Fora do projeto:** `animatedwebs-site`, `animatedwebs-hero`, `animatedwebs-backgrounds` e `animatedwebs-style` (este só com ADR).
- Toda tela mexida passa pelo QA: `/testar-no-navegador` e `/auditar-acessibilidade` no Antigravity.
