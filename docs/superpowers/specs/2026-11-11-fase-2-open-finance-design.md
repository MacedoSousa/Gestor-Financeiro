# Fase 2 · Open Finance pelo Meu Pluggy — design

**Status:** aprovada pela pessoa em 11/11/2026 (seções 1 a 6 discutidas em conversa). A revisão do Dev contra o código vem antes do plano.
**Objetivo:** contas, cartões e investimentos do Itaú entram sozinhos, uma vez por dia, sem duplicar e sem o casal digitar; o painel passa a refletir o banco.

**Base:** ADR-007 (Meu Pluggy), ADR-010 (jobs no Postgres), ADR-014 (despesa de boleto), ADR-015 (conexão por cônjuge, só leitura, sem webhook), `CLAUDE.md` regras 1 a 10, notas de pesquisa `2026-11-11-fase-2-open-finance-notas.md` (substituídas por esta spec onde divergem).

**Pré-requisito:** nenhum dado real entra antes de o backup estar rodando com uma restauração ensaiada (Task 6 do backup e o ensaio real).

**Medido em 11/11 com a conexão real (teste de fumaça só de estrutura, nada gravado):**
- autenticação ok; `GET /v2/items` → `403` ("not enabled to list its items"): o Meu Pluggy não deixa listar as conexões, então o `itemId` é informado pela pessoa;
- item com conector `MeuPluggy`, consentimento até 2027-11-11;
- conta corrente: 479 transações desde 03/01/2026, todas `POSTED`, **nenhuma com `boletoMetadata`**;
- cartão: 216 transações desde 07/01/2026, **32 `PENDING`**, 24 com dados de parcela, algumas com data futura (até 03/2027);
- 9 investimentos, todos `FIXED_INCOME/CDB`;
- `GET /transactions` (página) responde `410`: só `GET /v2/transactions` com `dateFrom` e cursor `after`.

**Decisões da pessoa (11/11):**
- Fase 2 antes dos Boletos; uma conta do Meu Pluggy por cônjuge, cada um com os próprios bancos;
- a conta conjunta, quando disponível, entra pela conexão de um cônjuge só (do primeiro cônjuge);
- cada cônjuge tem o próprio cartão; as contas, cartões e investimentos do Itaú ainda não existem no sistema e nascem pela importação;
- primeira importação: **todo o ano de 2026** (desde 1º/01), dentro do limite de 12 meses do Open Finance;
- categorias: o sistema aprende um mapa com as correções; revisão em lote antes de contar no orçamento; a pessoa pode trocar ou criar categorias na revisão;
- **opção B:** nas contas ligadas ao banco, tudo vem do banco; nada se lança à mão nelas;
- compras pendentes do cartão entram como **"a confirmar"** e contam no orçamento;
- investimentos agrupados **por meta** (ex.: "CDBs Casa", "CDBs Carro"), não um por aplicação.

## Escopo

**Entra:** cliente HTTP da Pluggy só de leitura; conexões por cônjuge; vínculo de contas, cartões e investimentos; importação do histórico de 2026; sincronização diária e "Atualizar agora"; mapa de categorias e revisão em lote; transferências entre contas ligadas e pagamento de fatura reconhecidos; caixa "Conciliar"; aviso de consentimento; tela de conexões.

**Fica fora:** webhooks; pendentes de conta corrente (não foram observadas); empréstimos, SCR e consórcios (bloco de Dívidas); vínculo de boleto pago pela linha digitável (fica pronto quando o bloco Boletos chegar: a chave `codigo_barras_hmac` é a mesma); outros bancos além do Itaú (o desenho já aceita, mas não é testado com dados reais nesta fase); enriquecimento pago da Pluggy.

## 1. Conexões e contas

- **Conexão** = um cônjuge + as credenciais dele no Meu Pluggy. No Meu Pluggy **cada banco é um item** (medido em 11/11: o segundo banco conectado gerou um `itemId` novo), então a conexão tem **um ou mais items**: a pessoa copia o `itemId` de cada banco do dashboard da Pluggy na tela "Conexões" (a API não lista items no Meu Pluggy). O sistema valida cada `itemId` com `GET /items/{id}` antes de gravar. Tabela `integracoes.items` (conexão, `item_id_cifrado`, banco, estado, consentimento, última sincronização); os campos de estado e consentimento descritos abaixo para a conexão valem por item. Segredos `infra/secrets/pluggy_<conjuge_id>_client_id` e `..._client_secret` (0444 em pasta 0700, ADR-005), lidos só pelo `Integracoes`. Os dois arquivos gravados em 11/11 (`pluggy_client_*`) são renomeados para o cônjuge que os criou na instalação; o plano traz o comando.
- Schema `integracoes`, tabelas com casal, soft delete e `audit.proteger`:
  - `conexoes` — cônjuge, estado (`ATIVA`, `CONSENTIMENTO_VENCENDO`, `CONSENTIMENTO_VENCIDO`, `ERRO`), item da Pluggy (`item_id_cifrado`), `consentimento_vence_em`, última sincronização com sucesso e última tentativa;
  - `vinculos` — conexão, tipo (`CONTA`, `CARTAO`, `INVESTIMENTO`), id externo cifrado + `id_externo_hmac`, `conta_id` do sistema, titular (`CONJUGE_1`, `CONJUGE_2` ou `CONJUNTA`), `identidade_hmac` (HMAC de banco + agência + número; para cartão, do final do número + titular), `historico_desde`.
  - `UNIQUE (casal_id, identidade_hmac) WHERE NOT is_deleted`: a mesma conta física não se liga duas vezes. Vinda pela outra conexão, ela aparece em "Conexões" como "Esta conta já vem da conexão de <cônjuge>" e é ignorada.
- **Nascimento:** na primeira sincronização de uma conexão, o sistema lista o que encontrou e mostra "Encontramos 1 conta, 1 cartão e 1 investimento no Itaú. Criar?". A pessoa confirma cada item, ajusta o apelido, escolhe o titular e, para investimento, a meta (opcional). Nada é criado sem confirmação. Também é possível ligar a uma conta que já existe (contas futuras de outros bancos).
- Conta ligada mostra o selo "Itaú · sincronizado" e a data da última atualização.

## 2. Histórico de 2026 e saldo inicial

- A primeira importação de cada vínculo busca desde **2026-01-01** (`historico_desde`). Se o banco entregar menos, guarda a data mais antiga recebida e mostra "Histórico desde DD/MM".
- **Saldo inicial** da conta nascida = saldo de hoje informado pelo banco − soma das transações importadas, na data do primeiro dia do histórico. A conferência é automática: se o saldo calculado do sistema hoje não bater com o do banco, a conexão mostra "Diferença de R$ X com o banco" e nada é ajustado em silêncio.
- **Cartão:** as faturas vêm pela lista de faturas do banco; a fatura de cada compra continua sendo a do calendário do cartão do sistema. Se o banco informar outra fatura para a mesma compra, vale a do banco e a divergência é registrada (o plano define onde).
- **Investimento:** as aplicações são **agrupadas por meta**: na confirmação, a pessoa diz a qual grupo cada aplicação pertence ("CDBs Casa", "CDBs Carro" ou "sem meta"); cada grupo é **uma** conta do tipo `INVESTIMENTO`, ligada à meta, e vários vínculos (um por aplicação) apontam para a mesma conta. Aplicação nova que aparecer depois cai em "Conexões" para escolher o grupo; até lá, não entra em nenhum. O saldo da conta é a soma das aplicações do grupo; o saldo diário do banco substitui a "conciliação de saldo" manual: a diferença positiva entre o saldo do banco e o do sistema continua virando "Rendimentos" (regra já existente), agora automática a cada sincronização.
- **Orçamento:** meses sem teto não acusam estouro; as transações do passado aparecem no extrato, nos gráficos e no patrimônio.

## 3. Categorias

- Cada transação importada nasce **"A revisar"**, com uma categoria sugerida nesta ordem: (1) regra de descrição do casal, (2) mapa categoria-da-Pluggy → categoria do casal, (3) sem sugestão.
- **Revisão em lote** (tela "Revisar importados"): agrupada por descrição parecida, com aceitar em massa. Ao corrigir, a pessoa escolhe "só esta" ou "sempre que vier assim" (cria ou atualiza regra). Criar categoria nova está disponível ali mesmo.
- Transação "A revisar" já conta no saldo e no patrimônio, mas **só conta no orçamento depois de revisada**; o painel mostra "N lançamentos a revisar".
- Tabelas `lancamentos.regras_de_categoria` (descrição normalizada cifrada + HMAC → categoria) e `lancamentos.mapa_categoria_externa` (categoria da Pluggy → categoria do casal), no módulo dono das categorias.

## 4. Contas ligadas são do banco

- Transações importadas: `origem = 'OPEN_FINANCE'`, `id_externo = id da transação na Pluggy` (chave única que já existe). Importar de novo o mesmo período não duplica.
- Numa conta ligada **não se cria, não se muda valor/data/conta e não se apaga** lançamento pelo app (`409` "Esta conta vem do banco. Os lançamentos chegam sozinhos."). Descrição e categoria continuam editáveis.
- **Pendentes do cartão ("a confirmar"):** transação `PENDING` entra com o selo "a confirmar" e **conta no orçamento e no "ainda dá para gastar"**. Quando o banco a confirma, ela pode chegar com outro `id`: o sistema troca a pendente pela definitiva (mesmo cartão, mesmo valor, data até 3 dias, descrição parecida), preservando a categoria escolhida. Pendente que some sem definitiva correspondente é apagada (soft delete, com auditoria) e aparece uma vez em "Conciliar" para conferência. Sem par único → "Conciliar".
- **Parcelas com data futura** chegam do banco e ficam no mês da data delas (regra do orçamento: conta a data da compra de cada parcela); não entram no patrimônio antes da data.
- Transação apagada ou alterada pelo banco: atualizada pela chave natural; apagada vira soft delete com auditoria; recategorização feita pelo casal é preservada.
- Parcelamento manual não existe em conta ligada; cada parcela chega do banco no mês dela.
- Contas não ligadas (dinheiro, outros bancos não conectados) continuam manuais.

## 5. Dinheiro que muda de lugar

- **Transferência entre contas ligadas do casal:** saída numa e entrada na outra, mesmo valor, mesma data (ou até 1 dia útil) → vira transferência (`Transferencias`), nunca gasto + receita. Sem par único → caixa "Conciliar".
- **Pagamento de fatura:** saída da corrente que o banco marca como pagamento de fatura do cartão ligado → pagamento de fatura (regra existente: não é gasto).
- **Caixa "Conciliar":** lista o que o sistema não decidiu sozinho, com as opções "é transferência entre…", "é gasto", "é receita". Nada é descartado.
- **Boleto pago:** o Itaú **não manda** os dados do boleto (medido em 11/11). Quando o bloco Boletos existir, o pagamento casa com o vencimento por **conta, valor e data** (até 3 dias depois do vencimento), com a descrição como desempate; sem par único → "Conciliar". Se o banco algum dia mandar `boletoMetadata`, o código de barras normalizado (HMAC) passa a ser a chave forte.

## 6. Sincronização, falhas e segurança

- **Job diário** no Hangfire (ADR-010), às 04:30 (depois do backup das 03:00), por conexão; botão "Atualizar agora" (limite: uma vez a cada 10 minutos por conexão). Sem webhook e sem rota pública.
- A sincronização é idempotente e por cursor; falha no meio não deixa importação pela metade visível (grava por vínculo, em transação).
- **Falha:** a conexão fica em `ERRO` com o motivo traduzido; o painel mostra "Última atualização do Itaú: <data> · Tentar de novo". Nada é apagado.
- **Consentimento:** aviso 15 dias antes de `consentExpiresAt`, nomeando o cônjuge que precisa renovar; vencido, a conexão para e avisa.
- **Só leitura:** o cliente da Pluggy implementa só `POST /auth` e endpoints `GET` de items, contas, transações, faturas e investimentos. Teste de arquitetura proíbe no código as rotas `/payments`, `/smart-transfers`, `/boletos`, `/boleto-connections`, `/pix-biometric` e qualquer `PATCH`/`DELETE` na Pluggy (a sincronização manual usa o `PATCH /items/{id}` documentado só se o plano provar necessário, com decisão do CEO).
- A chave temporária da Pluggy (2 h) vive só em memória; nunca em log, banco ou erro. Dados da Pluggy nunca vão para log (regra 5).

## 7. Testes

- Cliente da Pluggy contra respostas gravadas **sintéticas** (no formato medido em 11/11: `403` na listagem de items, `410` no endpoint antigo, cursor do `/v2/transactions`) (nunca dados reais), incluindo paginação, 401, 429 com `Retry-After` e consentimento vencido.
- Integração (Testcontainers): nascimento com confirmação; agrupamento de CDBs por meta; pendente que vira definitiva com outro id (sem duplicar, mantendo a categoria) e pendente que some; histórico com saldo inicial que fecha; reimportação sem duplicar; conta repetida entre conexões recusada; bloqueio de lançamento manual em conta ligada (`409`); transferência casada; pagamento de fatura; caixa "Conciliar"; regra e mapa de categoria; "A revisar" fora do orçamento; transação apagada pelo banco.
- Arquitetura: só leitura na Pluggy; `Integracoes` só fala com outros módulos por `Contracts`.
- QA: telas de conexões, nascimento e revisão no navegador, com a Pluggy simulada.

## 8. Etapas do plano (uma por PR)

1. Cliente da Pluggy só de leitura + teste de arquitetura.
2. Schema `integracoes`, conexões e segredos por cônjuge.
3. Nascimento e vínculo de contas (tela de confirmação).
4. Importação de transações e histórico de 2026, saldo inicial, bloqueio em conta ligada.
5. Categorias: regras, mapa e revisão em lote.
6. Transferências, pagamento de fatura e caixa "Conciliar".
7. Job diário, "Atualizar agora", falhas e consentimento no painel.
