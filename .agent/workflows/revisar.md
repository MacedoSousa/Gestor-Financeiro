---
description: Revisão cruzada do branch atual contra as regras do projeto, sem editar código
---

1. Leia `CLAUDE.md`, `AGENTS.md` e a regra de `.claude/rules/` da área tocada.
2. Descubra o que mudou: `git diff main...HEAD --stat` e depois `git diff main...HEAD`. Se houver spec ou plano do bloco em `docs/superpowers/`, leia também.
3. Confira, nesta ordem, e anote arquivo e linha de cada achado:
   - **Regras que não se negociam** (1 a 10 do `CLAUDE.md`): DELETE físico, `audit.proteger`, unicidade parcial, dado sensível fora de `*_cifrado` + `*_hmac`, dado pessoal em log/erro, dinheiro fora de `Dinheiro`/centavos, módulo enxergando `internal` ou schema de outro, endpoint sem DTO/Problem Details, filtro `Casal` desligado, Gemini calculando.
   - **Correção:** casos de borda (mês com 28–31 dias, zero, negativo, data no passado, concorrência/409), arredondamento em centavos, fuso de Brasília.
   - **Testes:** todo comportamento novo tem teste; o teste falharia sem o código? Dados sintéticos?
   - **Contrato:** mudou endpoint → `contracts/openapi.json` e `frontend/src/api/schema.d.ts` no mesmo commit.
   - **Spec:** o código faz o que a spec diz, nem mais nem menos.
4. Rode `scripts/verificar.sh` e guarde a saída.
5. Escreva `.revisoes/<branch>.md` no formato de `docs/desenvolvimento/modelo-de-revisao.md`. Classifique cada achado como **Bloqueia**, **Importante** ou **Sugestão**. Não edite nenhum outro arquivo.
6. Termine dizendo quantos achados de cada tipo há e onde está o arquivo.
