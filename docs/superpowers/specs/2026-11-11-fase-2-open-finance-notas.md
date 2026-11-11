# Fase 2 · Open Finance — notas de pesquisa do CEO (não é spec)

**Status:** pesquisa de 10/11/2026 para preparar a conversa de design. Nada aqui está decidido. A spec da Fase 2 nasce destas notas, pelo `brainstorming`, com a pessoa.

**Fonte:** tipos do SDK oficial `pluggy-sdk` 0.94.2 (npm, publicado em 05/11/2026), lidos direto do pacote. Referências citadas pelo próprio SDK: documentação em https://docs.pluggy.ai e OpenAPI em https://api.pluggy.ai/oas3.json — **o CEO não teve acesso a elas nesta pesquisa; conferir antes da spec.** ADR-007 e ADR-010 continuam valendo.

## O que a Pluggy entrega (fatos do SDK)

- **Autenticação:** `clientId` e `clientSecret`; o app troca por uma API key. A conexão do banco é um *item* (`itemId`), criado pelo widget Pluggy Connect com um *connect token*. Listar items é recurso pago: o `itemId` precisa ser guardado quando o item nasce (`onSuccess` ou webhook `item/created`).
- **Consentimento:** o item tem `consentExpiresAt` (Open Finance). ADR-007 já pede aviso 15 dias antes e tratar `USER_AUTHORIZATION_EXPIRED`.
- **Transações:** `GET /v2/transactions` com paginação por cursor (o método por página está obsoleto). Campos que importam:
  - `id` (chave natural da origem `OPEN_FINANCE`), `accountId`, `date`, `description`, `descriptionRaw`;
  - `type` `DEBIT`/`CREDIT` e `amount` **numérico** — converter para centavos lendo o JSON como `decimal`, nunca `double` (regra 8);
  - `status` `POSTED` ou `PENDING`; `balance`; `category`/`categoryId` (categoria da Pluggy, só sugestão);
  - `paymentData`: pagador e recebedor, `referenceNumber`, `authenticationCode` e **`boletoMetadata.digitableLine`/`barcode`**, a chave forte que liga ao vencimento do bloco Boletos (ADR-014);
  - `creditCardMetadata`: `installmentNumber`/`totalInstallments`, `purchaseDate`, `billId`, `billForecastDate` (AAAA-MM), `billPostDate`, `cardNumber` mascarado.
- **Webhooks:** `item/created|updated|error|deleted|waiting_user_input|login_succeeded`, `connector/status_updated`, `transactions/created|updated|deleted` (com `transactionIds`). O payload traz `eventId` (idempotência do processamento). A Pluggy aceita **headers fixos** no cadastro do webhook: é por onde entra o segredo do ADR-007, já que não há assinatura HMAC.
- **Outros recursos:** faturas do cartão (`fetchCreditCardBills`), empréstimos (`fetchLoans`, base para o bloco de dívidas), investimentos (`fetchInvestments`).
- **.NET:** não há SDK oficial; o caminho é um cliente HTTP próprio, só com os endpoints usados, conferido contra o `oas3.json`.

## Perguntas para a conversa de design

1. **Meu Pluggy na prática:** o ADR-007 escolheu o Meu Pluggy (gratuito, até 5 conexões, 24 h). Ele dá `clientId`/`clientSecret` e webhooks, ou só o app? Isso decide se a Fase 2 é "API + webhook" ou "sincronização diária sem webhook". **Primeira coisa a confirmar na documentação.**
2. **Parcelas do cartão (o maior risco de duplicar):** hoje, uma compra parcelada manual cria todas as parcelas futuras de uma vez (`Parcelamento`). O banco manda cada parcela como transação no mês dela (`installmentNumber`). Casar parcela a parcela pela compra original (data da compra, valor total, `totalInstallments`) ou mudar o lançamento manual de parcelado?
3. **Fatura:** o sistema calcula a fatura pelo `CalendarioDaFatura` (dia de fechamento). O banco manda `billId`/`billForecastDate`. Quando os dois discordarem, quem vale?
4. **`PENDING`:** importar só `POSTED`, ou mostrar pendentes à parte? Uma transação pendente pode sumir ou mudar de `id` quando vira lançada.
5. **Conciliação em 3 níveis (SPEC):** (1) chave natural `(OPEN_FINANCE, id)`; (2) chaves fortes — código de barras do boleto, `authenticationCode`/`referenceNumber` do Pix e TED; (3) mesma conta, valor igual e data ± 2 dias → caixa "Conciliar", nunca mescla sozinho. O que conta como "forte" no Itaú precisa ser medido com dados reais, só localmente.
6. **Contas:** como uma conta da Pluggy (`accountId`) se liga a uma conta do sistema; o saldo inicial manual continua valendo ou passa a vir do banco?
7. **Categorias:** a categoria da Pluggy vira só sugestão, ou existe um mapa categoria-da-Pluggy → categoria do casal que aprende?
8. **Exclusões do banco:** `transactions/deleted` vira soft delete do lançamento `OPEN_FINANCE`; e se o casal já tinha recategorizado ou conciliado?
9. **Jobs:** a sincronização diária e o processamento do webhook são os primeiros jobs do Hangfire (ADR-010). Fila no Postgres, reprocessamento por `eventId`, e o que aparece no painel quando a sincronização falha.
10. **Exposição:** o webhook é a primeira rota pública sem sessão. Caminho próprio no Caddy, header secreto, limite de requisições e, se a documentação publicar, lista de IPs da Pluggy; o corpo do webhook nunca é confiado — sempre rebuscar pela API.
11. **Segredos:** `clientSecret` e o segredo do webhook em `infra/secrets/`, como os outros; entram no backup.

## Pré-requisitos antes de começar o código da Fase 2

- Backup diário rodando e uma restauração ensaiada (`restaurar.sh --verificar`) — o plano do backup já põe isso como pré-requisito da Fase 2.
- Bloco Boletos com a chave do código de barras (`codigo_barras_hmac`) no `main`, porque a conciliação nível 2 depende dela.
- Fase 1 rodando com um mês real conferido com o extrato do Itaú (o SPEC fecha a Fase 1 assim).
