# ADR-014 — Despesa de boleto criada por contrato idempotente

**Decisão:** o módulo `Boletos` é dono do que vence (boletos, contas recorrentes e vencimentos). Ao pagar ou confirmar um débito, ele chama `Lancamentos.Contracts.IDespesaDeVencimento`, que cria uma despesa comum com `origem = 'BOLETO'` e `id_externo = <id do vencimento>`. A chave única `(origem, id_externo)` que já existe torna a operação idempotente: repetir devolve a despesa existente. A despesa `BOLETO` não é editada nem apagada pelo extrato; desfaz-se pela tela de Boletos.

**Por quê:** os módulos só se falam por contratos (regra 7), e cada um grava no próprio schema, sem transação distribuída. A idempotência pela chave natural substitui a transação: se o `Boletos` cair depois de a despesa existir, tentar de novo não duplica. O orçamento, o patrimônio e o extrato continuam lendo só `lancamentos.transacoes`, sem filtrar "agendados". Na Fase 2, o pagamento do boleto chega da Pluggy com `boletoMetadata.digitableLine` ou `barcode`; o índice cego do código de barras guardado no vencimento liga o débito do banco à despesa `BOLETO` sem duplicar.

**Descartado:**
- *Boletos dentro de Lancamentos, como despesas agendadas* — uma transação só, mas mistura "o que vence" com "o que aconteceu": orçamento, patrimônio e extrato passariam a filtrar agendados, e o módulo `Boletos` do SPEC ficaria vazio.
- *Evento de domínio e job criando a despesa* — o desacoplamento mais limpo, mas exige montar agora o Hangfire (ADR-010), a fila e o monitoramento, e a despesa apareceria com atraso depois do clique em "Pago".

## Detalhes

- O `CHECK` de `origem` em `lancamentos.transacoes` passa a aceitar `BOLETO`; o OpenAPI e o `LancamentoDto` também.
- `DesfazerAsync(vencimentoId)` faz soft delete da despesa; o índice único é parcial (`WHERE NOT is_deleted`), então pagar de novo depois de desfazer cria outra despesa, e a auditoria guarda as duas.
- A conta e a categoria são validadas no `Lancamentos`, dono das regras de lançamento; o `Boletos` só repassa.
- Spec: `docs/superpowers/specs/2026-11-11-boletos-design.md`.
