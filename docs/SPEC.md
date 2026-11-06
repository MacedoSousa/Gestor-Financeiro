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

| Fase | Entrega | Pronto quando |
| --- | --- | --- |
| 0 · Fundação | Repo, CI, migração V0001, cofre, soft delete, auditoria, casca do front | CI verde; `DELETE` falha no banco; cadeia confere |
| 1 · Controle manual | Login (passkey/TOTP), contas, lançamentos, orçamento, metas, dashboard | Um mês fechado à mão |
| 2 · Open Finance | Meu Pluggy, webhook, sync diário, conciliação em 3 níveis | Conta conjunta sem duplicatas |
| 3 · WhatsApp | Gateway (chip dedicado), parser pt-BR, comprovantes na GPU | Bot responde o saldo da categoria |
| 4 · Assistente | FinanceEngine completo (CDI/IR/IOF via BCB SGS), Gemini, relatório dia 1 | Todo número do texto confere |
| 5 · Endurecimento | Backups 3-2-1, hash da cadeia fora do PC, revisão de segurança | Restauração ensaiada |

## Próximas tarefas (Fase 0 → 1)

- [ ] Primeiro build no CI e commit de `contracts/openapi.json` gerado
- [ ] Teste de integração com Postgres (Testcontainers) cobrindo `audit.verificar_cadeia`
- [x] Cadastro do casal: CPF + nome + vínculo de cônjuge + envelopes da DEK em `identidade.chaves`
- [x] Login com sessão em cookie (BFF), passkey e TOTP, bloqueio após 5 falhas
- [ ] Lint no front (ESLint) dentro de `npm run check`
- [ ] Módulo Lancamentos: tabela `transacoes` com `UNIQUE (origem, id_externo) WHERE NOT is_deleted`
