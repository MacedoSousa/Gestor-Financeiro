---
description: Revisa os textos de interface do branch (rótulos, botões, erros, estados) contra a spec, sem editar código
---

1. Leia a spec do bloco em `docs/superpowers/specs/` (os textos dela valem como definidos) e `git diff main...HEAD -- frontend/ backend/`.
2. Liste todo texto que a pessoa vê: títulos, rótulos, botões, placeholders, mensagens de erro (inclusive Problem Details da API), estados vazios, confirmações, avisos do painel.
3. Confira cada um:
   - **Igual à spec** quando a spec define o texto.
   - **Português do Brasil, direto e no tom do casal:** frase curta, verbo no botão ("Pagar boleto", não "Confirmar"), sem jargão técnico ("409", "payload") e sem culpar quem usa.
   - **Erro diz o que fazer:** o que houve + como resolver ("A linha digitável tem 47 ou 48 números. Confira e cole de novo.").
   - **Números e datas:** `R$ 1.234,56`, `DD/MM`, "≈" para estimado, mês por extenso onde a spec pede.
   - **Consistência:** o mesmo conceito com o mesmo nome em todas as telas (ex.: sempre "vencimento", nunca "data limite" numa e "vencimento" noutra).
   - **Nada pessoal em erro** (regra 5): nenhum CPF, nome ou valor dentro de mensagem de erro.
4. Escreva a seção "Textos" em `.revisoes/<branch>.md` com o texto atual, o proposto e o motivo. Divergência da spec é **Importante**; texto que expõe dado pessoal é **Bloqueia**.
