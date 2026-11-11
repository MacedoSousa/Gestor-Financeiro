# Fase 2 · Open Finance pelo Meu Pluggy — design

**Status:** aprovada pela pessoa em 11/11/2026; revisada pelo Dev contra o código (PR #27) e respondida pelo CEO na seção 9, **que prevalece sobre as seções 1 a 8 onde divergirem**.
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

**Entra:** cliente HTTP da Pluggy só de leitura; conexões por cônjuge; vínculo de contas, cartões e investimentos; importação do histórico de 2026; sincronização diária e "Atualizar agora"; mapa de categorias e revisão em lote; transferências entre contas ligadas e pagamento de fatura reconhecidos; caixa "A conferir"; aviso de consentimento; tela de conexões.

**Fica fora:** webhooks; pendentes de conta corrente (não foram observadas); empréstimos, SCR e consórcios (bloco de Dívidas); vínculo de boleto pago pela linha digitável (fica pronto quando o bloco Boletos chegar: a chave `codigo_barras_hmac` é a mesma); outros bancos além do Itaú (o desenho já aceita, mas não é testado com dados reais nesta fase); enriquecimento pago da Pluggy.

## 1. Conexões e contas

- **Conexão** = um cônjuge + as credenciais dele no Meu Pluggy. No Meu Pluggy **cada banco é um item** (medido em 11/11: o segundo banco conectado gerou um `itemId` novo), então a conexão tem **um ou mais items**: a pessoa copia o `itemId` de cada banco do dashboard da Pluggy na tela "Conexões" (a API não lista items no Meu Pluggy). O sistema valida cada `itemId` com `GET /items/{id}` antes de gravar. Tabela `integracoes.items` (conexão, `item_id_cifrado`, banco, estado, consentimento, última sincronização); os campos de estado e consentimento descritos abaixo para a conexão valem por item. Segredos `infra/secrets/pluggy_<conjuge_id>_client_id` e `..._client_secret` (0444 em pasta 0700, ADR-005), lidos só pelo `Integracoes`. Os dois arquivos gravados em 11/11 (`pluggy_client_*`) são renomeados para o cônjuge que os criou na instalação; o plano traz o comando.
- Schema `integracoes`, tabelas com casal, soft delete e `audit.proteger`:
  - `conexoes` — cônjuge, estado (`ATIVA`, `CONSENTIMENTO_VENCENDO`, `CONSENTIMENTO_VENCIDO`, `ERRO`), item da Pluggy (`item_id_cifrado`), `consentimento_vence_em`, última sincronização com sucesso e última tentativa;
  - `vinculos` — **item** (seção 9, D18), tipo (`CONTA`, `CARTAO`, `INVESTIMENTO`), id externo cifrado + `id_externo_hmac`, `conta_id` do sistema, titular (`CONJUGE_1`, `CONJUGE_2` ou `CONJUNTA`), `identidade_hmac` (HMAC de banco + agência + número; para cartão, do final do número + titular), `historico_desde`.
  - `UNIQUE (casal_id, identidade_hmac) WHERE NOT is_deleted`: a mesma conta física não se liga duas vezes. Vinda pela outra conexão, ela aparece em "Conexões" como "Esta conta já vem da conexão de <cônjuge>" e é ignorada.
- **Nascimento:** na primeira sincronização de uma conexão, o sistema lista o que encontrou e mostra "Encontramos 1 conta, 1 cartão e 1 investimento no Itaú. Criar?". A pessoa confirma cada item, ajusta o apelido, escolhe o titular e, para investimento, a meta (opcional). Nada é criado sem confirmação. Também é possível ligar a uma conta que já existe (contas futuras de outros bancos).
- Conta ligada mostra o selo "Itaú · sincronizado" e a data da última atualização.

## 2. Histórico de 2026 e saldo inicial

- A primeira importação de cada vínculo busca desde **2026-01-01** (`historico_desde`). Se o banco entregar menos, guarda a data mais antiga recebida e mostra "Histórico desde DD/MM".
- **Saldo inicial** da conta nascida = saldo de hoje informado pelo banco − soma das transações importadas, na data do primeiro dia do histórico. A conferência é automática: se o saldo calculado do sistema hoje não bater com o do banco, a conexão mostra "Diferença de R$ X com o banco" e nada é ajustado em silêncio.
- **Cartão:** a fatura de cada compra é a do calendário do cartão do sistema (dias de fechamento e vencimento); as faturas do banco servem só para conferência (seção 9, D2).
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
- **Pendentes do cartão ("a confirmar"):** transação `PENDING` entra com o selo "a confirmar" e **conta no orçamento e no "ainda dá para gastar"**. Quando o banco a confirma, ela pode chegar com outro `id`: o sistema troca a pendente pela definitiva (mesmo cartão, mesmo valor, data até 3 dias, descrição parecida), preservando a categoria escolhida. Pendente que some sem definitiva correspondente é apagada (soft delete, com auditoria) e aparece uma vez em "A conferir" para conferência. Sem par único → "A conferir".
- **Parcelas com data futura** chegam do banco e ficam no mês da data delas (regra do orçamento: conta a data da compra de cada parcela); não entram no patrimônio antes da data.
- Transação apagada ou alterada pelo banco: atualizada pela chave natural; apagada vira soft delete com auditoria; recategorização feita pelo casal é preservada.
- Parcelamento manual não existe em conta ligada; cada parcela chega do banco no mês dela.
- Contas não ligadas (dinheiro, outros bancos não conectados) continuam manuais.

## 5. Dinheiro que muda de lugar

- **Transferência entre contas ligadas do casal:** saída numa e entrada na outra, mesmo valor, mesma data (ou até 1 dia útil) → vira transferência (`Transferencias`), nunca gasto + receita. Sem par único → caixa "A conferir".
- **Pagamento de fatura:** saída da corrente que o banco marca como pagamento de fatura do cartão ligado → pagamento de fatura (regra existente: não é gasto).
- **Caixa "A conferir"** (seção 9, D11): lista o que o sistema não decidiu sozinho, com as opções "é transferência entre…", "é gasto", "é receita". Nada é descartado.
- **Boleto pago:** o Itaú **não manda** os dados do boleto (medido em 11/11). Quando o bloco Boletos existir, o pagamento casa com o vencimento por **conta, valor e data** (até 3 dias depois do vencimento), com a descrição como desempate; sem par único → "A conferir". Se o banco algum dia mandar `boletoMetadata`, o código de barras normalizado (HMAC) passa a ser a chave forte.

## 6. Sincronização, falhas e segurança

- **Job diário** no Hangfire (ADR-010), às 04:30 (depois do backup das 03:00), por conexão; botão "Atualizar agora" (limite: uma vez a cada 10 minutos por conexão). Sem webhook e sem rota pública.
- A sincronização é idempotente e por cursor; falha no meio não deixa importação pela metade visível (grava por vínculo, em transação).
- **Falha:** a conexão fica em `ERRO` com o motivo traduzido; o painel mostra "Última atualização do Itaú: <data> · Tentar de novo". Nada é apagado.
- **Consentimento:** aviso 15 dias antes de `consentExpiresAt`, nomeando o cônjuge que precisa renovar; vencido, a conexão para e avisa.
- **Só leitura:** o cliente da Pluggy implementa só `POST /auth` e endpoints `GET` de items, contas, transações, faturas e investimentos. Teste de arquitetura proíbe no código as rotas `/payments`, `/smart-transfers`, `/boletos`, `/boleto-connections`, `/pix-biometric` e qualquer `PATCH`/`DELETE` na Pluggy (a sincronização manual usa o `PATCH /items/{id}` documentado só se o plano provar necessário, com decisão do CEO).
- A chave temporária da Pluggy (2 h) vive só em memória; nunca em log, banco ou erro. Dados da Pluggy nunca vão para log (regra 5).

## 7. Testes

- Cliente da Pluggy contra respostas gravadas **sintéticas** (no formato medido em 11/11: `403` na listagem de items, `410` no endpoint antigo, cursor do `/v2/transactions`) (nunca dados reais), incluindo paginação, 401, 429 com `Retry-After` e consentimento vencido.
- Integração (Testcontainers): nascimento com confirmação; agrupamento de CDBs por meta; pendente que vira definitiva com outro id (sem duplicar, mantendo a categoria) e pendente que some; histórico com saldo inicial que fecha; reimportação sem duplicar; conta repetida entre conexões recusada; bloqueio de lançamento manual em conta ligada (`409`); transferência casada; pagamento de fatura; caixa "A conferir"; regra e mapa de categoria; "A revisar" fora do orçamento; transação apagada pelo banco.
- Arquitetura: só leitura na Pluggy; `Integracoes` só fala com outros módulos por `Contracts`.
- QA: telas de conexões, nascimento e revisão no navegador, com a Pluggy simulada.

## 8. Etapas do plano (uma por PR)

1. Cliente da Pluggy só de leitura + teste de arquitetura.
2. Schema `integracoes`, conexões e segredos por cônjuge.
3. Nascimento e vínculo de contas (tela de confirmação).
4. Importação de transações e histórico de 2026, saldo inicial, bloqueio em conta ligada.
5. Categorias: regras, mapa e revisão em lote.
6. Transferências, pagamento de fatura e caixa "A conferir".
7. Job diário, "Atualizar agora", falhas e consentimento no painel.

## 9. Respostas do CEO à revisão do Dev (PR #27, 11/11/2026)

Numeração dos itens da revisão. Esta seção prevalece sobre as anteriores.

**Contas**
- **D1 · Dias e limite do cartão importado:** a confirmação vem **pré-preenchida** com o dia de fechamento e de vencimento inferidos das faturas do banco (`/bills`) e com o limite do banco quando ele vier; a pessoa confirma ou corrige. O `CHECK` continua valendo.
- **D2 · Fatura:** vale **só o calendário do sistema**. As faturas do banco são lidas para conferência: se o total de uma fatura do banco diferir do total calculado, "Conexões" mostra a diferença. Nenhuma tabela de faturas nesta fase; a regra C7 não muda.
- **D3 · Saldo inicial:** em conta ligada, o saldo inicial é calculado e **bloqueado para edição**. A conferência com o banco compara só transações confirmadas (sem as "a confirmar"); o plano mede, na etapa 4, se o saldo da Pluggy inclui ou não as pendentes e ajusta a comparação. Cartão: saldo do sistema = −(valor usado informado pelo banco).
- **D4 · Apelido:** a confirmação sugere "<Banco> · <primeiro nome do titular>" (ex.: "Itaú · Ana"); apelido repetido é recusado com a mensagem atual e a sugestão.
- **D5 · Marca de conta ligada:** fica em `contas.contas` (`ligada_em timestamptz NULL`), gravada pelo `Contas` via contrato de escrita chamado pelo `Integracoes`. O `Lancamentos` já lê contas por `IConsultaDeContas`, que passa a expor `Ligada`. Sem ciclo.

**Lancamentos**
- **D6 e D19 · Chave natural e apagadas:** o `Lancamentos` expõe no contrato de importação um "importar por chave natural" que, **dentro do próprio módulo**, enxerga também linhas apagadas para não recriá-las. É uma exceção explícita à regra 9, só para chave natural de importação, sem expor conteúdo de linha apagada; o `CLAUDE.md` ganha a frase.
- **D7 · `id_externo`:** o id de transação e de conta da Pluggy é um identificador opaco, sem dado pessoal: fica **em claro**, como a coluna atual. O `itemId` continua cifrado, por andar junto das credenciais.
- **D8 · Estados:** duas colunas novas em `lancamentos.transacoes`: `revisao` (`A_REVISAR` | `REVISADA`, padrão `REVISADA` para lançamento manual) e `confirmacao` (`A_CONFIRMAR` | `CONFIRMADA`, padrão `CONFIRMADA`).
- **D9 · Transferências:** operação nova no `Lancamentos`, "parear como transferência", que converte duas transações existentes num par (mantém os ids, tira a categoria). Lado em conta **não ligada** (saque para "Dinheiro", Pix para banco não conectado): em "A conferir", a pessoa escolhe "transferência para <conta manual>" e o sistema cria o lado manual, que pode existir porque aquela conta não é ligada.
- **D10 · Pagamento de fatura:** as **duas** transações do banco (saída da corrente e crédito no cartão) viram o par de pagamento de fatura. Cartão não ligado: o lado do cartão é criado como em D9.
- **D11 · Nome da caixa:** "**A conferir**" (a "Conciliação" atual continua sendo o ajuste de saldo). Em conta ligada, a conciliação de saldo manual fica bloqueada: ela é automática.
- **D12 · Rendimentos automáticos:** aplicação e resgate aparecem na corrente e vão para "A conferir" como "aporte em <grupo>" ou "resgate de <grupo>" (par de transferência corrente ↔ grupo; regras de descrição aprendem). O rendimento do dia só é calculado **depois** que não houver aporte ou resgate pendente de conferência para aquele grupo; diferença positiva → "Rendimentos" (regra atual); **diferença negativa nunca fica escondida**: vai para "A conferir" com as opções "IR/IOF ou taxa" (lança uma despesa na categoria escolhida) ou "resgate não identificado".
- **D13 · Parcelas do banco:** ficam **soltas** (cada parcela é uma transação do banco), com "N/M" vindo dos dados de parcela; o limite de 24 parcelas é do parcelamento manual e não se aplica à importação.

**Orcamento**
- **D14 · "A revisar":** fica **fora** do uso do mês e do "ainda dá para gastar"; receita "a revisar" não entra na receita sugerida; aporte ainda não pareado não conta em "Investir". "A confirmar" conta (decisão da pessoa).
- **D15 · Sem categoria:** é o mesmo estado "a revisar" (categoria nula só é permitida com `revisao = A_REVISAR`).

**Painel**
- **D16:** seções novas "N lançamentos a revisar", "N itens a conferir" e "Última atualização de <banco>"; o Painel passa a ler `Integracoes.Contracts` (só leitura). Muda o contrato da API: cliente do front regerado (regra 8).

**Integracoes**
- **D17 · Escrita entre módulos:** cada módulo expõe nos próprios `Contracts` uma interface de escrita idempotente e chama as suas regras e travas: `Contas` (criar conta ligada, marcar ligada), `Investimentos` (ligar conta a meta), `Lancamentos` (importar em lote por chave natural, parear, promover pendente). Cada chamada é uma transação do módulo dono; a retomada depois de falha é segura porque tudo é idempotente pela chave natural (mesmo padrão do ADR-014). "Grava por vínculo, em transação" passa a significar isto.
- **D18 · Item:** o vínculo aponta para o **item**; estado, consentimento e última sincronização ficam no item (`integracoes.items`); a conexão guarda só o cônjuge e o slot de segredo.

**Identidade**
- **D20 · Cônjuge e titular:** a conexão guarda `usuario_id` e um **slot** (1 ou 2): slot 1 é de quem cadastrar a primeira conexão. O titular de cada conta é o que a conta já guarda (nulo = conjunta); o vínculo não repete o titular.
- **D21 · Quem gerencia:** só o titular cadastra, troca o item e renova a própria conexão; o outro vê o estado. O job roda pelo casal, sem usuário; a auditoria registra o autor como o sistema (`integracoes`).

**ADR-010**
- **D22 · Jobs:** nesta fase, **sem Hangfire**: um serviço em segundo plano da API com timer diário e a tabela `integracoes.execucoes` (soft delete, auditoria) para registrar cada execução. O ADR-010 é atualizado; o Hangfire volta a ser avaliado quando houver fila de verdade (Fase 3).

**Compose**
- **D23 · Segredos da Pluggy:** nomes **fixos** por slot: `pluggy_1_client_id`, `pluggy_1_client_secret`, `pluggy_2_client_id`, `pluggy_2_client_secret`, declarados no compose só para a API. O `gerar-segredos.sh` cria os quatro **vazios** (0444) se não existirem, para o compose subir; arquivo vazio = slot não configurado. A pessoa grava o conteúdo pelo terminal (`read -s`), nunca por agente. Os arquivos de 11/11 (`pluggy_client_*`, em `~/Gestor-Financeiro`) passam a ser o slot 1 na instalação de `/opt`.
- **D24 · Pré-requisito:** cumprido em 11/11: instalação em `/opt/gestor-financeiro`, backup ensaiado (restauração verificada) e timer ligado.

**Documentos e regras**
- **D25 · SPEC.md:** a linha da Fase 2 é atualizada neste PR.
- **D26 · Regra 5:** o cliente da Pluggy registra só o status HTTP e o campo de código do erro; nunca o corpo da resposta.

### Complementos do CEO à conferência do Dev (PR #28)

- **D15 (corrigida):** o `CHECK` novo é "categoria nula só com `revisao = A_REVISAR`, **exceto transferência e pagamento de fatura**", que continuam obrigados a ter categoria nula. Lançamentos manuais de hoje já passam.
- **D12 (chaves do que o sistema cria):** origem nova `SISTEMA` no `CHECK` de `origem` (permitida em conta ligada, porque não é lançamento à mão). Chaves naturais, idempotentes: rendimento diário `rendimento:<conta_id>:<AAAA-MM-DD>`; despesa de IR/IOF ou taxa `ajuste-investimento:<conta_id>:<AAAA-MM-DD>`; lado manual criado em D9 e D10 `par:<id da transação importada>`. A conciliação de saldo manual de hoje (contas não ligadas) mantém a chave atual.
- **D3 (cartão e parcelas futuras):** a medição da etapa 4 cobre também o que o "valor usado" do banco inclui (pendentes **e** parcelas futuras). A fórmula de comparação do cartão é a que fechar com os dados medidos e fica escrita no plano; a diferença só aparece em "Conexões" se persistir por duas sincronizações seguidas.
- **D4 (apelido):** a sugestão inclui o tipo: "Itaú Corrente · <nome>", "Itaú Cartão · <nome>"; grupos de investimento usam o nome do grupo ("CDBs Casa").
- **D7 (vínculo):** o id externo no vínculo também fica **em claro e sem HMAC**; unicidade `(item, tipo, id_externo) WHERE NOT is_deleted`. A trava contra a mesma conta física vir por dois items continua pelo `identidade_hmac`. A §1 fica corrigida por esta linha.
- **D20 e D23 (slot):** o slot é **escolhido explicitamente** no cadastro da conexão ("Estas chaves estão nos arquivos `pluggy_1_*` / `pluggy_2_*`"). Ao salvar, o sistema autentica com as chaves daquele slot e faz `GET /items/{id}` com o item informado; se falhar, recusa na hora. Os arquivos de 11/11 viram o slot 1, do cônjuge que os criou.
- **D21 (auditoria):** o campo `ip` registra `sistema:integracoes` nas gravações do job; sem coluna nova.
- **D27 (regra 9 e Identidade):** a exceção que o código já tem passa para o `CLAUDE.md`: o `Identidade` pode desligar o filtro `Casal` só nos fluxos anteriores à sessão (login, sessões, pedido de dispositivo, recuperação, primeiro acesso). O plano acrescenta um teste de arquitetura que lista os lugares permitidos.
- **D28 (instalação de root):** `/opt/gestor-financeiro` passa a pertencer ao root (menos `infra/data/` e os arquivos da API com dono 1654). Operações na instalação de verdade são só da pessoa, com `sudo` (`sudo git -C /opt/gestor-financeiro pull`, `sudo docker compose ...`). Agentes trabalham só em `~/Gestor-Financeiro` e `~/gf-qa`, nunca em `/opt`. Risco aceito e registrado: estar no grupo `docker` equivale a ser root (ADR-013); a troca de dono impede a alteração por engano, não um ataque de quem já roda como o usuário.
