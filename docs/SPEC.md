# SPEC — Gestor Financeiro

Documento vivo com a arquitetura: [Gestor Financeiro — Arquitetura](https://claude.ai/artifact/9U18LGXymA5p9SW5zEbrfP). Este arquivo é o resumo que o Claude Code lê.

## Objetivo

Um casal (dois CPFs, uma conta conjunta Itaú) vê num só lugar contas, cartões, investimentos, boletos e gastos do dia a dia, para saber **quanto ainda pode gastar no mês, quanto investir e quando chega à casa e ao carro**. Não é ferramenta de cobrança entre os cônjuges.

## Módulos

| Módulo | Schema | Responsabilidade |
| --- | --- | --- |
| Identidade | `identidade` | Casal, cônjuges (CPF cifrado, `conjuge_id`), login, envelopes da DEK |
| Contas | `contas` | Contas, cartões, faturas, saldos |
| Lancamentos | `lancamentos` | Transações de todas as origens, categorias, conciliação |
| Orcamento | `orcamento` | Teto por categoria e mês, saldo restante |
| Boletos | `boletos` | Boletos (DDA manual/WhatsApp), vencimentos |
| Investimentos | `investimentos` | Posições, aportes, metas |
| Integracoes | `integracoes` | Pluggy, gateway de mensagens, leitor de comprovantes |
| Assistente | `assistente` | FinanceEngine determinístico, Gemini, relatório mensal |
| Auditoria | `audit` | Leitura e verificação da cadeia de eventos |

## Garantias

- **Sem DELETE físico**: trigger `BEFORE DELETE/TRUNCATE` em toda tabela + `gf_app` sem privilégio de DELETE + `SoftDeleteInterceptor` + hook do Claude + teste de arquitetura.
- **Auditoria**: trigger `audit.registrar()` grava cada INSERT/UPDATE em `audit.eventos` (append-only), com autor, IP e hash encadeado por casal. `audit.verificar_cadeia(casal)` acusa adulteração.
- **Sem duplicatas**: chave natural por origem `(origem, id_externo)` com índice único parcial; chaves fortes (E2E do Pix, NFC-e, linha digitável) para mesclar; o resto vai para a caixa "Conciliar". Nada é descartado em silêncio.
- **Sigilo**: AES-256-GCM com AAD por linha; HMAC (índice cego) para busca; envelope encryption (senha de cada cônjuge, código de recuperação, chave de serviço).
- **Exposição**: só a porta 443 aberta, em `seu-nome.duckdns.org`, atrás do Caddy (TLS por DNS-01, limite de requisições, CSP); passkey/TOTP obrigatórios. Banco, gateway e OCR nunca publicados (ADR-006).

## Fases

| Fase | Entrega | Pronto quando | Situação |
| --- | --- | --- | --- |
| 0 · Fundação | Repo, CI, migração V0001, cofre, soft delete, auditoria, casca do front | CI verde; `DELETE` falha no banco; cadeia confere | Concluída (PR #1, #2) |
| 1 · Controle manual | Cadastro, login (passkey/TOTP), contas, lançamentos, orçamento, metas, dashboard | Um mês fechado à mão | Em andamento: faltam metas e dashboard |
| 2 · Open Finance | Meu Pluggy, webhook, sync diário, conciliação em 3 níveis | Conta conjunta sem duplicatas | — |
| 3 · WhatsApp | Gateway (chip dedicado), parser pt-BR, comprovantes na GPU | Bot responde o saldo da categoria | — |
| 4 · Assistente | FinanceEngine completo (CDI/IR/IOF via BCB SGS), Gemini, relatório dia 1 | Todo número do texto confere | — |
| 5 · Endurecimento | Backups 3-2-1, hash da cadeia fora do PC, revisão de segurança | Restauração ensaiada | — |

## Entregue

| PR | Bloco | O que entrou | Migração |
| --- | --- | --- | --- |
| #1, #2 | Fase 0 | Esqueleto, cofre, auditoria por trigger, CI com Postgres real, contrato OpenAPI | V0001 |
| #3 | Cadastro do casal | Formulário único, CPF cifrado, vínculo de cônjuge, envelopes da DEK, código de instalação (uso único, 24 h) | — |
| #4 | Login | Sessão em cookie, passkey e TOTP, primeiro acesso por senha que fecha para sempre, bloqueio progressivo (passkey fora), celular novo por código de 6 dígitos (ADR-011) | V0002 |
| #5 | Contas e lançamentos | Contas e cartões com fatura, categorias, lançamentos manuais, parcelas, transferências e pagamento de fatura | V0003, V0004 |
| #6 | Ferramentas do front | Barra lateral vira topo abaixo de 900 px, ESLint e Vitest no `npm run check` | — |
| #7 | Orçamento | Tetos por categoria e mês, cópia do mês anterior, receita prevista, divisão da renda, conta Investimento, estorno, papel das categorias | V0005–V0008 |

Specs e planos de cada bloco: `docs/superpowers/specs/` e `docs/superpowers/plans/`.

## Regras de negócio já decididas

- **Orçamento conta pela data da compra.** Compra no cartão entra no mês em que foi feita; transferências e pagamento de fatura nunca contam como gasto.
- **Estorno** é uma receita lançada numa categoria de despesa sem papel; abate o usado do mês da data do estorno. Receita em Investimento ou Dívidas é recusada (400).
- **Papel da categoria** (`INVESTIMENTO`, `DIVIDAS`) identifica as fatias da divisão da renda, não o nome. Teto de gastos soma só as categorias sem papel.
- **Investir** = usado da categoria Investimento + aportes: entrada em conta poupança ou investimento vinda de conta de outro tipo. Movimentação entre duas contas de investimento e resgate não contam.
- **Teto zero** significa "não gastar nada" e é diferente de "sem teto" (sem linha). A cópia do mês anterior só preenche categorias sem linha, a partir do último mês que tem algum teto.

## Próximas tarefas

- [ ] Metas (casa, carro) ligadas às contas de investimento, com projeção pelo FinanceEngine
- [ ] Dashboard da Visão geral com dados reais
- [ ] Bloco próprio de dívidas (contrato, juros, saldo devedor, parcelas); até lá, a fatia Dívidas vem da categoria com papel `DIVIDAS`
- [ ] Antes de abrir a 443: revisar sessões ativas de cada cônjuge (ADR-011)
- [ ] Teste de arquitetura de unicidade analisando o comando SQL inteiro, não linha a linha
