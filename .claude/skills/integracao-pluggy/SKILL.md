---
name: integracao-pluggy
description: Use em qualquer tarefa que toque a Pluggy (cliente, importação, conexões, medição, testes). Resume as regras que valem para a Fase 2 e o que nunca fazer com as chaves e os dados reais.
---

# Integração com a Pluggy

Spec: `docs/superpowers/specs/2026-11-11-fase-2-open-finance-design.md` (a seção 9 e os complementos prevalecem). Plano: `docs/superpowers/plans/2026-11-11-fase-2-open-finance.md`. ADR-015.

## Nunca

- Ler `infra/secrets/` nem usar as chaves reais. Script contra a Pluggy de verdade é rodado **pela pessoa**, e só imprime estrutura (contagens, nomes de campo, sim ou não).
- Chamar rota de pagamento ou `PATCH`/`PUT`/`DELETE` na Pluggy (o teste de arquitetura recusa).
- Pôr id da Pluggy, `itemId`, valor, descrição ou chave em log, exceção, URL de log ou resposta. O `HttpClient` da Pluggy já vem sem o log padrão (`RemoveAllLoggers`); não reative.
- Usar `double` em valor: `ConversaoPluggy.Centavos` (decimal, exato, recusa mais de duas casas).
- Espalhar código de categoria da Pluggy: só em `Lancamentos.Contracts.CategoriasDoBanco`.

## Sempre

- Testes contra a Pluggy falsa (`infra/pluggy/testes/pluggy-falso.py`) ou respostas gravadas **sintéticas**, no formato medido em 11/11 (403 na listagem de items, 410 no endpoint antigo, cursor `after` no `/v2/transactions`).
- Sinal pelo tipo da conta (`ConversaoPluggy.ValorNoSistema`): no cartão, compra positiva vira gasto.
- Data de transação: dia em São Paulo do instante (medido: nenhuma às 00:00Z); data só-dia (vencimento): `DiaDoCalendario`.
- Importar é "criar ou atualizar pela chave natural" `(OPEN_FINANCE, id)`, idempotente; o que o sistema cria usa origem `SISTEMA` e as chaves fixas da spec (D12).
- Contas ligadas vêm só do banco (opção B): lançamento manual nelas é `409`.
- Tela nova: skill `melhorar-tela` e o agente `testador-de-telas`. Antes do PR: `{ git log --format='%h %s%n%b' origin/main..HEAD; echo '---'; git diff origin/main...HEAD; } > .git/revisao.diff` e depois o agente `revisor-seguranca`.
