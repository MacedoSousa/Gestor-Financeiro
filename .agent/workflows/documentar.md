---
description: Atualiza a documentação para refletir o que já está no código, só nos arquivos permitidos ao QA
---

1. Confirme que o Dev não está trabalhando neste branch (tarefa commitada, `git status` limpo). Se estiver, crie `docs/<assunto>` a partir do branch atual.
2. Descubra o que mudou: `git log main..HEAD --oneline` e `git diff main...HEAD --stat`. Leia a spec e o plano do bloco.
3. Atualize **só** estes arquivos, e só com o que o código e os testes já mostram:
   - `docs/SPEC.md`: linha nova em "Entregue" (PR, bloco, o que entrou, migrações) e marcação em "Próximas tarefas". Regras de negócio novas só se já estiverem na spec aprovada; copie de lá, não reescreva.
   - `README.md`: comandos, situação das fases, passos de uso que mudaram.
   - `docs/guia/<tela>.md`: guia de uso para o casal (o que a tela mostra, como fazer cada ação, o que cada aviso significa), em linguagem simples e com exemplos sintéticos.
   - `docs/desenvolvimento/**`: processo e comandos de desenvolvimento.
4. Não edite `CLAUDE.md`, `.claude/**`, `.agent/rules/**`, `docs/adr/**`, `docs/superpowers/**` nem código. Se achar regra ou decisão faltando, inclua na sua resposta, numa seção 'Para o CEO'.
5. Rode `scripts/verificar.sh`. Commit `Docs: <o que mudou>`. O `pre-commit` vai conferir as guardas.
6. Termine listando os arquivos alterados e qualquer divergência entre doc e código encontrada.
