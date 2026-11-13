# Fase 2 · Open Finance pelo Meu Pluggy — Plano de implementação

> **Para agentes:** SUB-SKILL OBRIGATÓRIA: superpowers:subagent-driven-development (recomendada) ou superpowers:executing-plans, tarefa por tarefa. Os passos usam `- [ ]` para acompanhamento.

**Objetivo:** contas, cartões e investimentos do Itaú entram sozinhos uma vez por dia, sem duplicar e sem o casal digitar, e o painel reflete o banco.

**Arquitetura:**
- O módulo `Integracoes` tem o cliente HTTP da Pluggy (só leitura), as conexões, os items e os vínculos. Ele orquestra a importação chamando contratos de escrita idempotentes dos módulos donos: `Contas`, `Investimentos` e `Lancamentos`. Cada chamada é uma transação do módulo dono, e a retomada é segura pela chave natural (D17).
- Um serviço em segundo plano da API sincroniza todo dia às 04:30, sem Hangfire (D22).
- O que o sistema não decide sozinho vai para a caixa "A conferir" do `Lancamentos`.

**Tecnologia:** .NET 10, EF Core + Npgsql, PostgreSQL 17, `IHttpClientFactory`, `BackgroundService` + `PeriodicTimer`, React + Vite + Vitest, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-11-fase-2-open-finance-design.md`. A **seção 9** e os **"Complementos do CEO à conferência do Dev (PR #28)"** prevalecem sobre as seções 1 a 8. Os itens são citados como D1…D28.

## Regras globais

Valem para toda tarefa:
- **Regras do `CLAUDE.md`, de 1 a 10.**
  - Migração nova pela skill `nova-migracao`: próximo número livre, a partir de `V0011`; índice único sempre parcial; `audit.proteger` em toda tabela.
  - Contrato da API mudou → `dotnet build backend/src/Host/GestorFinanceiro.Api && npm --prefix frontend run gen:api` no mesmo commit.
- **Cliente da Pluggy, só leitura:**
  - só `POST /auth` e `GET` de `/items/{id}`, `/accounts`, `/v2/transactions`, `/bills` e `/investments`;
  - `GET /v2/items` dá 403 no Meu Pluggy e `GET /transactions` dá 410: nunca usar.
- **Chave temporária da Pluggy (2 h):** só em memória. Nunca em log, banco, exceção ou resposta.
- **Log e erros da Pluggy:** registram só o status HTTP e o campo de código do erro; nunca o corpo (D26).
- **Segredos:** `infra/secrets/pluggy_{1,2}_client_{id,secret}`, nomes fixos por slot (D23). Arquivo vazio = slot não configurado. Agente nunca grava conteúdo neles, nunca os lê e nunca roda nada com eles. Agentes nunca operam em `/opt/gestor-financeiro` (D28).
- **Dados de teste:** sempre sintéticos. A Pluggy é sempre falsa (`ClientePluggyFalso` ou servidor falso local); nenhum teste chama `api.pluggy.ai`.
- **Origens:** `OPEN_FINANCE` (vem do banco), `SISTEMA` (criado pelo sistema, permitido em conta ligada; D12), `MANUAL`.
- **Chaves naturais (D12):**
  - transação importada: `id_externo` = id da transação na Pluggy, em claro (D7);
  - rendimento diário: `rendimento:<conta_id>:<AAAA-MM-DD>`;
  - ajuste de IR/IOF ou taxa: `ajuste-investimento:<conta_id>:<AAAA-MM-DD>`;
  - lado manual de um par: `par:<id da transação importada>`.

  O contrato de importação é **"criar ou atualizar pela chave natural"**: a segunda sincronização do dia atualiza o lançamento do sistema pela mesma chave (decisão do CEO, 11/11, 18h58).
- **Datas:** a data de uma transação da Pluggy é o dia em `America/Sao_Paulo` do instante ISO recebido.
- **Dinheiro:** valores da Pluggy são lidos como `decimal` (`JsonElement.GetDecimal`) e convertidos para centavos `long` sem `double`.
- **Números da spec:**

  | Regra | Valor |
  |---|---|
  | histórico | desde **2026-01-01** |
  | pendente → definitiva | data até **3 dias** |
  | par de transferência | mesmo valor e data até **1 dia útil** |
  | job diário | **04:30** (`America/Sao_Paulo`) |
  | "Atualizar agora" | no máximo **1 vez a cada 10 minutos** por conexão |
  | aviso de consentimento | **15 dias** antes de `consentExpiresAt` |
  | diferença com o banco | só aparece depois de **2 sincronizações seguidas** |

- **Apelido sugerido (D4 e decisão de 18h58):**
  - `"<Banco> Corrente · <primeiro nome>"`, `"<Banco> Poupança · <primeiro nome>"`, `"<Banco> Cartão · <primeiro nome>"`;
  - conta conjunta: `"<Banco> <Tipo> · Conjunta"`;
  - grupo de investimento: o nome do grupo.
- **Unicidade (decisões de 18h58 e ADR-015):** `integracoes.conexoes` tem `UNIQUE (casal_id, slot) WHERE NOT is_deleted` e `UNIQUE (casal_id, usuario_id) WHERE NOT is_deleted` (uma conexão por cônjuge).
- **Antes de cada PR:**
  - `scripts/verificar.sh --tudo` termina em "Verificação: tudo verde.";
  - `dotnet test backend/tests/GestorFinanceiro.ArchitectureTests` está verde;
  - skill `revisao-cruzada` ao fim da etapa;
  - o PR leva "NÃO FAZER MERGE sem o sinal do CEO no chat.".

## Foco da revisão

Situações que a spec implica e que nenhum teste das etapas pegaria sozinho. Cada linha tem um teste na tarefa dona.
1. **Transação de valor zero** (IOF estornado, ajuste de R$ 0,00) não quebra o lote: o `CHECK (valor_centavos <> 0)` derrubaria a importação inteira da conta. O esperado é ignorar a transação e contá-la no resumo. Teste: `Valor_zero_nao_entra_e_nao_derruba_o_lote` (Tarefa 4.3).
2. **Compra perto da meia-noite** (`2026-02-01T02:30:00Z` é 31/01 em São Paulo) cai no dia e no mês do banco, não no de UTC. Teste: `Data_usa_o_dia_de_Sao_Paulo` (Tarefa 1.2).
3. **Valor com casas decimais** (`10.1`, `0.07`, `1234.565`) vira centavos exatos. `1234.565` é recusado como formato inválido, e não arredondado. Teste: `Valor_decimal_vira_centavos_exatos` (Tarefa 1.2).
4. **Descrição vazia ou nula do banco** vira `"(sem descrição)"`; `descricao_cifrada` é `NOT NULL`. Teste: `Descricao_vazia_vira_sem_descricao` (Tarefa 4.3).
5. **Duas sincronizações do mesmo item ao mesmo tempo** (job das 04:30 e "Atualizar agora"): a segunda espera a trava do item e não duplica nada. Teste: `Sincronizacoes_simultaneas_nao_duplicam` (Tarefa 7.1).

## Estrutura de arquivos

O padrão de cada módulo é `Contracts/`, `Dominio/`, `Aplicacao/`, `Infra/` e `Api/` (`.claude/rules/backend.md`).

```
backend/src/Modules/Integracoes/
  Contracts/ConexoesDoCasal.cs          leitura para o Painel: estado das conexões (D16)
  Pluggy/IClientePluggy.cs              interface do cliente (internal)
  Pluggy/ClientePluggy.cs               HTTP, auth em memória, cursor, 429
  Pluggy/ModelosPluggy.cs               records internos (ItemPluggy, ContaPluggy, …)
  Pluggy/ConversaoPluggy.cs             valor → centavos, data → dia de São Paulo
  Pluggy/SegredosDaPluggy.cs            lê pluggy_<slot>_* de Pluggy:PastaDeSegredos
  Dominio/Conexao.cs, Item.cs, Vinculo.cs, Execucao.cs
  Aplicacao/ServicoDeConexoes.cs        cadastro de conexão e item (etapa 2)
  Aplicacao/Descoberta.cs, Nascimento.cs (etapa 3)
  Aplicacao/Sincronizacao.cs            importação por item (etapas 4 e 6)
  Aplicacao/SincronizacaoDiaria.cs      BackgroundService (etapa 7)
  Infra/IntegracoesDbContext.cs, Api/EndpointsDeConexoes.cs
backend/src/Modules/Contas/Contracts/IContasLigadas.cs          (etapa 3)
backend/src/Modules/Investimentos/Contracts/IMetasDeContas.cs   (etapa 3)
backend/src/Modules/Lancamentos/Contracts/IImportacaoDeLancamentos.cs (etapas 4 a 6)
backend/src/Modules/Lancamentos/Aplicacao/Importacao.cs, Revisao.cs, Pareamento.cs, AConferir.cs, RendimentoAutomatico.cs
infra/pluggy/medir-saldos.sh + infra/pluggy/testes/                 (etapa 1, medição D3 e origem do banco)
frontend/src/paginas/Conexoes.tsx, RevisarImportados.tsx, AConferir.tsx
```

---

## Etapa 1 · Cliente da Pluggy só de leitura (PR 1)

### Tarefa 1.0: Medição D3 e origem do banco (primeiro commit do PR da etapa 1; rodada pela pessoa)

Decisão 9 (CEO 11/11 19h): este é o primeiro commit do PR da etapa 1.

**Arquivos:**
- Criar: `infra/pluggy/medir-saldos.sh`; `infra/pluggy/testes/pluggy-falso.py` (servidor local só com a biblioteca padrão, que responde JSON sintético); `infra/pluggy/testes/testar-medicao.sh`.
- Modelo: `~/testar-pluggy.sh` (v2, com cursor) do CEO. Mesmo cuidado:
  - a chave vai por descritor de arquivo;
  - o corpo do `/auth` vai pela entrada padrão;
  - nada é gravado em disco;
  - a saída nunca tem valor, saldo, descrição, nome ou número.

**O que o script responde, só com estrutura e booleanos:**
1. Por cartão, qual fórmula fecha com o "valor usado" do banco (`balance`), sem imprimir valores: (a) confirmadas da fatura aberta; (b) a + pendentes; (c) b + parcelas futuras; (d) a + parcelas futuras. Saída: `cartão N: fecha com (c)` ou `cartão N: nenhuma; diferença <1% | 1–10% | >10%`.
2. Por conta corrente: quantas pendentes existem e se o `balance` muda entre duas leituras com 60 s de intervalo (se a Pluggy atualiza o saldo sem novas transações).
3. Sinal: contagem de `type` (DEBIT/CREDIT) × sinal de `amount`, por tipo de conta.
4. Categorias distintas da Pluggy (id + nome genérico, como "Credit card payment" e "Investments", com contagem). Isso fixa os identificadores usados na etapa 6.
5. Formato de data: se `date` tem hora e fuso.
6. Quantas transações têm valor zero e quantas têm descrição vazia.
7. De onde vem o banco (decisão 1): quais campos da conta e do item existem (ex.: `bankData`, `institution`, `connector`) e se algum traz o **código do banco** (3 dígitos). Saída: nome do campo e `código: sim/não`, sem valores. Se o código vier, ele entra na trava de conta repetida (Tarefa 3.2).

**Uso pela pessoa, nunca por agente:**
```
ITEM=<id> SLOT=1 bash infra/pluggy/medir-saldos.sh
```
As chaves vêm de `infra/secrets/pluggy_<slot>_*`.

- [ ] **Passo 1:** `testar-medicao.sh`, que falha. Ele sobe `pluggy-falso.py` numa porta livre com dados sintéticos, em que o cartão fecha com (c), e roda o script com `PLUGGY_URL=http://127.0.0.1:<porta>` e segredos sintéticos numa pasta temporária. Espera:
  - a linha `cartão 1: fecha com (c)`;
  - uma linha `banco: campo <nome do campo> · código: sim` para a conta sintética que traz o código, sem o valor do código na saída;
  - nenhum dos valores sintéticos (`12345`, `"Mercado Sintético"`) na saída;
  - nenhum arquivo novo fora da pasta temporária.
- [ ] **Passo 2:** `infra/pluggy/testes/testar-medicao.sh` → FALHA. Escrever o script. Rodar → PASSA. shellcheck pela imagem → limpo. Ligar o teste no job `backup` do CI, que já tem as ferramentas.
- [ ] **Passo 3:** commit "Pluggy: medição de saldos e origem do banco (estrutura)", o primeiro do PR da etapa 1. A pessoa roda o script; o resultado entra no plano, nesta tarefa, como "Resultado da medição: fórmula do cartão …; código do banco …; categorias …".
  - A etapa 1 segue sem esperar.
  - Usam o resultado: a Tarefa 3.2 (código do banco), a 4.5 (fórmula do cartão) e a 6.2 (ids das categorias).
  - As tarefas 4.2 e 4.3 podem andar antes do resultado (decisão 9).

**Resultado da medição** (rodada pela pessoa em 11/11, ~20h55; item do Itaú e um segundo banco com corrente e dois cartões; só estrutura):
- **Datas:** a corrente tem hora real em todas as transações. No cartão, 30 estão às 03:00Z e o resto em outras horas. Nenhuma está às 00:00Z, nos dois bancos. A conversão para o dia de São Paulo está certa.
- **Sinal:** na corrente, DEBIT é negativo e CREDIT é positivo. No cartão, DEBIT é positivo (compra) e CREDIT é negativo (pagamento ou estorno). O segundo banco confirma a corrente.
- **Categorias do Itaú presentes:**
  - `05100000` Credit card payment, `03000000` Investments, `03020000` Fixed income, `03060000` Proceeds interests and dividends;
  - `04000000` Same person transfer, `05010000` Transfer - Bank Slip, `15030000` Tax on financial operations, `15010000` Income taxes;
  - 4 transações sem categoria.

  O segundo banco tem 7 transações `04000000` entre contas da mesma pessoa em bancos diferentes.
- **Banco:** nenhum campo traz o código do banco; `bankData.transferNumber` existe nos dois bancos, e o conector é sempre `MeuPluggy`.
- **Cartão:** nenhuma das fórmulas (a) a (d) fecha o saldo; a diferença passa de 10%. Há 184 transações dentro de faturas pelo `billId` e 32 sem `billId` (as pendentes).
- **Segundo banco:** tem dois cartões sem transação no ano, um com 11 faturas e outro sem nenhuma.
- As decisões do CEO sobre este resultado estão em "Decisões do CEO" (11/11 21h).

### Tarefa 1.1: Teste de arquitetura "só leitura na Pluggy"

**Arquivos:**
- Modificar: `backend/tests/GestorFinanceiro.ArchitectureTests/RegrasDeArquitetura.cs`

**Interfaces:**
- Produz: o teste `Cliente_da_Pluggy_so_le`, que varre o texto dos `.cs` de `backend/src/Modules/Integracoes/`.

- [ ] **Passo 1:** escrever `Cliente_da_Pluggy_so_le`. Ele falha se algum `.cs` do módulo contiver:
  - qualquer uma das rotas `"/payments"`, `"/smart-transfers"`, `"/boletos"`, `"/boleto-connections"` ou `"/pix-biometric"`;
  - `HttpMethod.Patch`, `HttpMethod.Delete`, `HttpMethod.Put`, `PatchAsync`, `DeleteAsync` ou `PutAsync`;
  - um `PostAsync` com rota diferente de `"/auth"`.

  Para provar que o teste pega, ele inclui um caso sobre um texto sintético com `DeleteAsync`, como já faz `Verificador_de_acesso_a_dados_acusa_Lancamentos`.
- [ ] **Passo 2:** `dotnet test backend/tests/GestorFinanceiro.ArchitectureTests --filter Cliente_da_Pluggy_so_le` → o caso sintético passa e o do módulo passa (ainda vazio). Depois, inserir `DeleteAsync` num `.cs` de rascunho do módulo → FALHA; remover o rascunho.
- [ ] **Passo 3:** escrever `IgnoreQueryFilters_so_nos_lugares_permitidos` (complemento D27). Ele varre `backend/src/` e só permite:
  - `IgnoreQueryFilters([Filtros.Casal])` em `Identidade/Aplicacao/ServicoDeSessoes.cs`, `PedidosDeDispositivo.cs`, `RecuperacaoDeAcesso.cs` e `PrimeiroAcesso.cs`;
  - `IgnoreQueryFilters([Filtros.SoftDelete])` em `Lancamentos/Aplicacao/Importacao.cs`, que nasce na etapa 4 (D6);
  - qualquer outro lugar, ou `IgnoreQueryFilters()` sem nome → FALHA.

  Rodar → PASSA com o código de hoje. Inserir um uso de rascunho em `Contas` → FALHA; remover o rascunho.
- [ ] **Passo 4:** commit "Arquitetura: cliente da Pluggy só lê e filtros só nos lugares permitidos".

### Tarefa 1.2: Conversões da Pluggy

**Arquivos:**
- Criar: `backend/src/Modules/Integracoes/Pluggy/ConversaoPluggy.cs`
- Teste: `backend/tests/GestorFinanceiro.UnitTests/ConversaoPluggyTests.cs`

**Interfaces:**
- Produz (`internal static class ConversaoPluggy`):
  - `long Centavos(JsonElement valor)`: lança `FormatException` com mais de 2 casas decimais;
  - `DateOnly DiaDeSaoPaulo(string instanteIso)`.

- [ ] **Passo 1:** testes que falham:
  - `Valor_decimal_vira_centavos_exatos`: `10.1 → 1010`, `0.07 → 7`, `-25.5 → -2550`, `1234.565` lança `FormatException`;
  - `Data_usa_o_dia_de_Sao_Paulo`: `"2026-02-01T02:30:00.000Z" → 2026-01-31` e `"2026-03-10T12:00:00.000Z" → 2026-03-10`.
- [ ] **Passo 2:** `dotnet test backend/tests/GestorFinanceiro.UnitTests --filter ConversaoPluggyTests` → FALHA (tipo inexistente). Liberar `InternalsVisibleTo` do teste no `.csproj` do módulo, como nos outros módulos.
- [ ] **Passo 3:** implementar com `JsonElement.GetDecimal()` e `TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")`.
- [ ] **Passo 4:** rodar → PASSA. Commit "Integracoes: conversão de valor e data da Pluggy".

### Tarefa 1.3: `ClientePluggy`

**Arquivos:**
- Criar: `Pluggy/IClientePluggy.cs`, `Pluggy/ClientePluggy.cs`, `Pluggy/ModelosPluggy.cs` e `Pluggy/SegredosDaPluggy.cs` (em `backend/src/Modules/Integracoes/`)
- Modificar: `IntegracoesModule.cs` (`AddHttpClient<ClientePluggy>` com base `https://api.pluggy.ai`, configurável em `Pluggy:Url`)
- Teste: `backend/tests/GestorFinanceiro.UnitTests/ClientePluggyTests.cs`, com um `HttpMessageHandler` falso e respostas JSON **sintéticas** no formato medido em 11/11

**Interfaces:**
- Produz:
  ```csharp
  internal sealed record CredenciaisPluggy(int Slot, string ClientId, string ClientSecret);
  internal sealed record ItemPluggy(string Id, string Status, string? ExecutionStatus, DateTimeOffset? ConsentimentoVenceEm, DateTimeOffset? AtualizadoEm);
  internal sealed record ContaPluggy(string Id, string Tipo, string? Subtipo, string Nome, string? Numero, string? AgenciaEConta,
      long SaldoCentavos, long? LimiteCentavos, string? Portador, string? FinalDoCartao);
  internal sealed record TransacaoPluggy(string Id, DateOnly Data, long ValorCentavos, string Tipo /* DEBIT|CREDIT */, string Status /* POSTED|PENDING */,
      string? Descricao, string? CategoriaId, string? Categoria, int? Parcela, int? Parcelas, string? FaturaId);
  internal sealed record FaturaPluggy(string Id, DateOnly Vencimento, DateOnly? Fechamento, long TotalCentavos);
  internal sealed record InvestimentoPluggy(string Id, string Tipo, string? Subtipo, string Nome, long SaldoCentavos);
  internal sealed class ErroDaPluggy(int status, string? codigo, TimeSpan? tentarDepois) : Exception; // Message nunca contém o corpo
  internal interface IClientePluggy
  {
      Task<ItemPluggy> ObterItemAsync(CredenciaisPluggy c, string itemId, CancellationToken ct);
      Task<IReadOnlyList<ContaPluggy>> ListarContasAsync(CredenciaisPluggy c, string itemId, CancellationToken ct);
      IAsyncEnumerable<TransacaoPluggy> ListarTransacoesAsync(CredenciaisPluggy c, string contaId, DateOnly desde, CancellationToken ct);
      Task<IReadOnlyList<FaturaPluggy>> ListarFaturasAsync(CredenciaisPluggy c, string contaId, CancellationToken ct);
      Task<IReadOnlyList<InvestimentoPluggy>> ListarInvestimentosAsync(CredenciaisPluggy c, string itemId, CancellationToken ct);
  }
  internal sealed class SegredosDaPluggy { public CredenciaisPluggy? Ler(int slot); } // null se arquivo ausente ou vazio
  ```

- [ ] **Passo 1:** testes que falham:
  - `Auth_manda_credenciais_no_corpo_e_guarda_a_chave_so_em_memoria`: um segundo `GET` reusa a chave sem novo `/auth`; nenhum campo ou propriedade pública expõe a `apiKey`;
  - `Chave_expirada_reautentica`: o relógio avança 2 h → novo `/auth`;
  - `Transacoes_seguem_o_cursor_after`: 3 páginas sintéticas encadeadas por `next`; usa `/v2/transactions?accountId=…&dateFrom=2026-01-01`; nunca `/transactions`;
  - `Erro_401_vira_ErroDaPluggy_sem_corpo`: o corpo sintético tem `"segredo-sintetico"`, e nem `Message` nem `ToString()` da exceção o contêm;
  - `Erro_429_respeita_Retry_After_ate_3_vezes`: `Retry-After: 1` duas vezes, depois 200 → sucesso; quatro 429 → `ErroDaPluggy(429)`;
  - `Consentimento_vencido_vem_no_item`: `consentExpiresAt` no passado → `ConsentimentoVenceEm` preenchido;
  - `Segredo_vazio_e_slot_nao_configurado`: arquivo de 0 bytes → `Ler(1) == null`.
- [ ] **Passo 2:** `dotnet test backend/tests/GestorFinanceiro.UnitTests --filter ClientePluggyTests` → FALHA.
- [ ] **Passo 3:** implementar.
  - A chave é cacheada por slot por 1 h 50 min, num `ConcurrentDictionary` em memória.
  - O 429 respeita `Retry-After`, com teto de 60 s por espera.
  - Os mapeamentos usam `ConversaoPluggy`.
  - `Portador` vem de `creditData.holder` e `FinalDoCartao` de `number`, quando existirem.
- [ ] **Passo 4:** rodar a classe toda e `Cliente_da_Pluggy_so_le` → PASSA. Commit "Integracoes: cliente da Pluggy só de leitura".

**Pronto quando:** `scripts/verificar.sh --tudo` está verde e nenhum teste toca a rede.

---

## Etapa 2 · Schema `integracoes`, conexões e segredos por cônjuge (PR 2)

### Tarefa 2.1: Segredos da Pluggy no compose e no `gerar-segredos.sh`

**Arquivos:**
- Modificar:
  - `infra/docker-compose.yml`: secrets `pluggy_1_client_id`, `pluggy_1_client_secret`, `pluggy_2_client_id` e `pluggy_2_client_secret` só na `api`, mais `Pluggy__PastaDeSegredos: /run/secrets`;
  - `infra/scripts/gerar-segredos.sh`: cria os quatro **vazios**, 0444, se não existirem;
  - `infra/testes/testar-gerar-segredos.sh`.

- [ ] **Passo 1:** no `testar-gerar-segredos.sh`:
  - `pasta_0700_e_segredos_0444_menos_a_chave` passa a esperar 10 arquivos;
  - caso novo `segredos_da_pluggy_nascem_vazios`: os quatro existem com 0 bytes e 0444;
  - caso novo `pluggy_existente_nao_e_tocado`: um arquivo com conteúdo sintético mantém inode e sha.
- [ ] **Passo 2:** `infra/testes/testar-gerar-segredos.sh` → FALHA.
- [ ] **Passo 3:** implementar. Os "Próximos passos" do script ganham, para a pessoa:
  ```
  read -rs v; printf '%s' "$v" | sudo tee infra/secrets/pluggy_1_client_id >/dev/null; unset v
  ```
  e o mesmo para os outros três arquivos.
- [ ] **Passo 4:** `infra/testes/testar-gerar-segredos.sh` e `infra/testes/testar-subida.sh` → PASSA, porque o compose sobe com os arquivos vazios. Commit "Segredos da Pluggy por slot".

### Tarefa 2.2: Migração e domínio de conexões e items

**Arquivos:**
- Criar:
  - `db/migrations/V0011__integracoes_conexoes.sql`;
  - `Dominio/Conexao.cs`, `Dominio/Item.cs` e `Infra/IntegracoesDbContext.cs` (em `backend/src/Modules/Integracoes/`).
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/ConexoesTests.cs`

**Migração (V0011):**
- `integracoes.conexoes`: `id`, `casal_id`, `usuario_id uuid NOT NULL`, `slot smallint NOT NULL CHECK (slot IN (1,2))` e as colunas padrão.
  - `ux_conexoes_slot (casal_id, slot) WHERE NOT is_deleted`;
  - `ux_conexoes_usuario (casal_id, usuario_id) WHERE NOT is_deleted`.
- `integracoes.items`:
  - `id`, `casal_id`, `conexao_id REFERENCES integracoes.conexoes`, `item_id_cifrado bytea NOT NULL`, `item_id_hmac bytea NOT NULL`, `banco text NOT NULL CHECK (length BETWEEN 1 AND 60)`;
  - `estado text NOT NULL CHECK (estado IN ('ATIVO','CONSENTIMENTO_VENCENDO','CONSENTIMENTO_VENCIDO','ERRO'))`, `motivo_erro text NULL` (código traduzido, nunca corpo), `consentimento_vence_em timestamptz NULL`, `ultima_sincronizacao_em timestamptz NULL`, `ultima_tentativa_em timestamptz NULL`;
  - `ux_items_item (casal_id, item_id_hmac) WHERE NOT is_deleted`.
- `audit.proteger` nas duas tabelas.
- AAD do item: `integracoes.items|item_id_cifrado|<id>`; HMAC por `ICofre.IndiceCego(itemId, "integracoes.item")`.

- [ ] **Passo 1:** testes que falham:
  - `Slot_repetido_no_casal_e_recusado`;
  - `Segunda_conexao_do_mesmo_conjuge_e_recusada`;
  - `Item_id_so_existe_cifrado`: `SELECT` direto não acha o texto do item sintético;
  - `Mesmo_item_duas_vezes_e_recusado`;
  - `Slot_apagado_libera_a_vaga`.
- [ ] **Passo 2:** `dotnet test backend/tests/GestorFinanceiro.IntegrationTests --filter ConexoesTests` → FALHA.
- [ ] **Passo 3:** migração, entidades (`Entidade`, `ICasalScoped`, soft delete), `IntegracoesDbContext : ModuloDbContext` e registro no `IntegracoesModule`, seguindo o `InvestimentosModule`.
- [ ] **Passo 4:** rodar → PASSA. Commit "Integracoes: conexões e items".

### Tarefa 2.3: API e tela de Conexões (cadastro)

**Arquivos:**
- Criar: `Aplicacao/ServicoDeConexoes.cs` e `Api/EndpointsDeConexoes.cs` (em `backend/src/Modules/Integracoes/`); `frontend/src/api/conexoes.ts`; `frontend/src/paginas/Conexoes.tsx`; `frontend/src/lib/conexoes.ts` e o teste dele.
- Modificar: `Program.cs` (`MapIntegracoes`) e a navegação em `frontend/src/layout/Layout.tsx`.

**Interfaces (API `/api/v1/integracoes`):**
- `GET /conexoes` → `ConexoesDto(IReadOnlyList<ConexaoDto>)`.
  - `ConexaoDto(Guid Id, Guid UsuarioId, string Apelido, int Slot, bool SlotConfigurado, bool EhSua, IReadOnlyList<ItemDto> Items)`;
  - `ItemDto(Guid Id, string Banco, string Estado, string? Motivo, DateOnly? ConsentimentoVenceEm, DateTimeOffset? UltimaSincronizacaoEm)`.
- `POST /conexoes` com corpo `{ slot }`. Cria a conexão do **usuário da sessão** (D20 e D21).
  - 409 "Este slot já é usado no casal." ou "Você já tem uma conexão.";
  - 400 "As chaves deste slot não foram gravadas no servidor." quando `SegredosDaPluggy.Ler(slot)` é nulo.
- `POST /conexoes/{id}/items` com corpo `{ itemId, banco }`. Só o dono da conexão; senão, 403.
  - Autentica com o slot da conexão e chama `ObterItemAsync`. Falhou → 400 "O banco recusou este item com as chaves do slot N." (D20 e D23).
  - Grava com o estado e o consentimento do banco.
- `DELETE /conexoes/{id}/items/{itemId}`: soft delete, só o dono.

- [ ] **Passo 1:** testes de integração que falham, em `ConexoesTests`, com `ClientePluggyFalso` registrado via `ApiDeTeste(servicos: …)`:
  - `Cadastro_usa_o_usuario_da_sessao`;
  - `Outro_conjuge_ve_mas_nao_altera` (403);
  - `Slot_sem_chaves_e_recusado`;
  - `Item_recusado_pelo_banco_nao_e_gravado`;
  - `Item_aceito_guarda_estado_e_consentimento`;
  - `Resposta_nunca_traz_item_id_nem_chaves`.
- [ ] **Passo 2:** rodar → FALHA.
- [ ] **Passo 3:** implementar. Os erros saem em Problem Details. Regerar o cliente do front.
- [ ] **Passo 4:** front: página "Conexões" com o slot escolhido explicitamente ("Estas chaves estão nos arquivos `pluggy_1_*` / `pluggy_2_*`") e o campo do `itemId` copiado do dashboard. Testes em `lib/conexoes.test.ts`, para o texto do estado e o aviso de slot não configurado.
- [ ] **Passo 5:** `npm --prefix frontend run check` e os testes de integração → PASSA. Commit "Integracoes: cadastro de conexões e items".

**Pronto quando:**
- `scripts/verificar.sh --tudo` está verde;
- o QA cadastrou, com a Pluggy simulada, uma conexão e um item no navegador.

---

## Etapa 3 · Nascimento e vínculo de contas (PR 3)

### Tarefa 3.1: Contratos de escrita do Contas e do Investimentos

**Arquivos:**
- Criar:
  - `db/migrations/V0012__contas_ligadas.sql`: `ALTER TABLE contas.contas ADD COLUMN ligada_em timestamptz NULL`;
  - `backend/src/Modules/Contas/Contracts/IContasLigadas.cs`;
  - `backend/src/Modules/Investimentos/Contracts/IMetasDeContas.cs`.
- Modificar:
  - `Contas/Contracts/IConsultaDeContas.cs`: `ContaResumo` ganha `bool Ligada = false` no fim;
  - `Contas/Dominio/Conta.cs`, `Contas/Infra/ConsultaDeContas.cs`, `Contas/Aplicacao/ServicoDeContas.cs`.
- Teste: `ContasTests.cs`, `MetasTests.cs`, e `ModulosTests.cs` (registros).

**Interfaces:**
```csharp
// Contas.Contracts
public sealed record NovaContaLigada(Guid ContaId, string Tipo, string Apelido, string? Instituicao, Guid? TitularUsuarioId,
    long SaldoInicialCentavos, DateOnly SaldoInicialEm, int? DiaFechamento, int? DiaVencimento, long? LimiteCentavos);
public abstract record ResultadoDaContaLigada
{
    public sealed record Pronta(Guid ContaId) : ResultadoDaContaLigada;           // criada agora ou já existia com esse id
    public sealed record ApelidoRepetido(string Sugestao) : ResultadoDaContaLigada;
    public sealed record Invalida(IReadOnlyDictionary<string, string[]> Erros) : ResultadoDaContaLigada;
}
public interface IContasLigadas
{
    Task<ResultadoDaContaLigada> CriarAsync(NovaContaLigada pedido, CancellationToken ct);       // idempotente pelo ContaId
    Task<bool> MarcarLigadaAsync(Guid contaId, CancellationToken ct);                         // false se não existe no casal
    Task DefinirSaldoInicialAsync(Guid contaId, long centavos, DateOnly em, CancellationToken ct); // só conta ligada
}
// Investimentos.Contracts
public interface IMetasDeContas { Task<bool> LigarContaAMetaAsync(Guid contaId, Guid metaId, CancellationToken ct); } // idempotente
```

- [ ] **Passo 1:** testes que falham:
  - `Conta_ligada_nasce_com_ligada_em`;
  - `CriarAsync_duas_vezes_com_o_mesmo_id_nao_duplica`;
  - `Apelido_repetido_devolve_a_sugestao`;
  - `Put_nao_muda_o_saldo_inicial_de_conta_ligada`: 409 "Esta conta vem do banco. O saldo inicial é calculado." (D3);
  - `Put_de_conta_ligada_aceita_apelido_e_dias`;
  - `Conta_ligada_pode_ser_arquivada_e_desarquivada`;
  - `Apagar_conta_ligada_da_409`: "Esta conta vem do banco. Arquive para parar a sincronização." (decisão 7);
  - `ContaResumo_expoe_Ligada`;
  - `LigarContaAMeta_e_idempotente`.
- [ ] **Passo 2:** rodar `--filter "ContasTests|MetasTests"` → FALHA.
- [ ] **Passo 3:** implementar. A ordem de lock é a existente: casal → conta. Atualizar `Modulo_so_depende_de_Contracts_de_outro_modulo` com os pares `Integracoes→Contas`, `Integracoes→Investimentos`, `Integracoes→Lancamentos`, `Integracoes→Identidade` e `Painel→Integracoes`.
- [ ] **Passo 4:** rodar → PASSA, mais os testes de arquitetura. Commit "Contas e Investimentos: contratos de conta ligada".

### Tarefa 3.2: Vínculos e descoberta

**Arquivos:**
- Criar: `db/migrations/V0013__integracoes_vinculos.sql`, `Integracoes/Dominio/Vinculo.cs` e `Integracoes/Aplicacao/Descoberta.cs`.
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/NascimentoTests.cs`

**Migração (V0013), `integracoes.vinculos`:**
- colunas: `id`, `casal_id`, `item_id REFERENCES integracoes.items` (D18), `tipo text CHECK (tipo IN ('CONTA','CARTAO','INVESTIMENTO'))`, `id_externo text NOT NULL` (em claro, D7), `conta_id uuid NULL` (nulo = aplicação aguardando grupo), `identidade_hmac bytea NULL`, `historico_desde date NULL`, `importado_ate date NULL` (último dia coberto pela importação do vínculo; decisão 7), `diferenca_centavos bigint NULL`, `diferencas_seguidas smallint NOT NULL DEFAULT 0`, `faturas_divergentes jsonb NULL` (decisão 12), `nao_importar_em timestamptz NULL` (conta encontrada marcada "não importar"; decisão 16);
- `ux_vinculos_externo (item_id, tipo, id_externo) WHERE NOT is_deleted`;
- `ux_vinculos_identidade (casal_id, identidade_hmac) WHERE NOT is_deleted AND identidade_hmac IS NOT NULL`;
- `audit.proteger`.
- `identidade_hmac`, com `ICofre.IndiceCego(…, "integracoes.identidade")`:
  - conta: `ConversaoPluggy.TransferNumberNormalizado(AgenciaEConta)`, ou seja, o `bankData.transferNumber` só com os dígitos, sem código do banco (decisão 11);
  - cartão: `"<FinalDoCartao>|<Portador normalizado>"` (portador por HMAC, decisão 6);
  - investimento: nulo.
- O nome do banco digitado na tela do item **nunca** entra no `identidade_hmac`; ele serve só ao apelido (decisão 1).

**Interfaces:**
- Produz: `Descoberta.ListarAsync(Guid itemId, CancellationToken ct) → DescobertaDto`.
  - `DescobertaDto(IReadOnlyList<ContaEncontradaDto> Contas, IReadOnlyList<AplicacaoEncontradaDto> Aplicacoes)`;
  - `ContaEncontradaDto(string IdExterno, string Tipo, string NomeNoBanco, string ApelidoSugerido, int? DiaFechamentoSugerido, int? DiaVencimentoSugerido, long? LimiteCentavos, string? JaVemDe)`.

  `JaVemDe` é o apelido do cônjuge da outra conexão, quando o `identidade_hmac` já existe. A tela mostra "Esta conta já vem da conexão de <cônjuge>" e a ignora.

- [ ] **Passo 1:** testes que falham:
  - `Descoberta_sugere_apelido_por_tipo_e_titular`: "Itaú Corrente · Ana", "Itaú Cartão · Ana";
  - `Conta_conjunta_sugere_Conjunta`: "Itaú Corrente · Conjunta";
  - `Dias_do_cartao_vem_das_faturas`: 3 faturas sintéticas com fechamento 03 e vencimento 10 → 3 e 10 (D1);
  - `Conta_que_ja_vem_da_outra_conexao_e_marcada`;
  - `Nome_do_banco_diferente_nas_duas_conexoes_ainda_trava`: "Itaú" numa conexão e "itau unibanco" na outra, mesma agência e conta → marcada (decisão 1);
  - `Descoberta_nao_grava_nada`.
- [ ] **Passo 2:** rodar `--filter NascimentoTests` → FALHA.
- [ ] **Passo 3:** implementar. Os dias sugeridos são a moda do dia de `Fechamento` e de `Vencimento` das faturas; sem faturas, nulo e a pessoa preenche.
- [ ] **Passo 4:** rodar → PASSA. Commit "Integracoes: vínculos e descoberta".

### Tarefa 3.3: Nascimento (confirmação)

**Arquivos:**
- Criar: `Integracoes/Aplicacao/Nascimento.cs`; endpoints `GET /integracoes/items/{id}/descoberta` e `POST /integracoes/items/{id}/nascimento`; `frontend/src/paginas/conexoes/Nascimento.tsx`.
- Teste: `NascimentoTests.cs`

**Interfaces:**
- `POST /integracoes/items/{id}/nascimento` com o corpo:
  ```
  { contas: [{ idExterno, acao: "CRIAR"|"LIGAR"|"NAO_IMPORTAR", contaExistenteId?, apelido, titularUsuarioId?, diaFechamento?, diaVencimento? }],
    aplicacoes: [{ idExterno, grupo: { nome, metaId? } | null }] }
  ```
  Só o dono da conexão (D21).
- Cada conta: o vínculo é gravado primeiro com um `conta_id` novo (Guid v7), e depois `IContasLigadas.CriarAsync` é chamado com esse id. O mesmo id torna a retomada idempotente.
- Aplicações: cada grupo distinto vira **uma** conta `INVESTIMENTO`, e cada aplicação vira um vínculo com o mesmo `conta_id`. Com `metaId`, chama `IMetasDeContas.LigarContaAMetaAsync`. Aplicação `null` fica com `conta_id` nulo, aguardando grupo.
- `NAO_IMPORTAR` (decisão 16): grava o vínculo com `conta_id` nulo e `nao_importar_em = agora`. Não vira conta, não sincroniza e não é perguntada de novo: a `Descoberta` a lista à parte, como "não importada", e não como conta encontrada.
  - "Conexões" mostra as contas não importadas com "Incluir". `POST /integracoes/vinculos/{id}/inclusao` recebe os mesmos campos de `CRIAR`/`LIGAR`, limpa `nao_importar_em` e segue como um nascimento normal. A primeira sincronização importa o histórico desde 2026-01-01.
  - É diferente da conta que já vem da outra conexão (`JaVemDe`), que é ignorada pela trava e nem vira vínculo.
- O saldo inicial provisório é 0 em `2026-01-01`. A conta corrente e o cartão são recalculados na etapa 4. O grupo de investimento é a soma dos saldos das aplicações, na data do nascimento.

- [ ] **Passo 1:** testes que falham:
  - `Nada_e_criado_sem_confirmacao`;
  - `Confirmacao_cria_contas_ligadas_e_vinculos`;
  - `Repetir_o_POST_nao_duplica`;
  - `Falha_no_meio_e_retomada_completa_sem_duplicar`: o fake do `IContasLigadas` falha na segunda conta e depois volta;
  - `Dois_CDBs_no_mesmo_grupo_viram_uma_conta`;
  - `Grupo_com_meta_liga_a_conta_a_meta`;
  - `Ligar_a_conta_existente_marca_ligada`;
  - `Outro_conjuge_recebe_403`;
  - `Nao_importar_registra_e_nao_pergunta_de_novo`: segunda descoberta lista a conta como não importada, não como encontrada;
  - `Conta_nao_importada_nao_vira_conta_nem_sincroniza`;
  - `Incluir_depois_cria_a_conta_ligada`.
- [ ] **Passo 2:** rodar → FALHA.
- [ ] **Passo 3:** implementar o back. Depois, o front com a lista "Encontramos N contas, M cartões e K investimentos no <banco>. Criar?", com apelido, titular e grupo editáveis, e "não importar" em cada conta. Regerar o cliente.
- [ ] **Passo 4:** `--filter NascimentoTests` e `npm --prefix frontend run check` → PASSA. Commit "Integracoes: nascimento de contas ligadas".

**Pronto quando:**
- `scripts/verificar.sh --tudo` está verde;
- o QA confirmou o nascimento no navegador, com a Pluggy simulada;
- a conta ligada mostra o selo "<banco> · sincronizado".

---

## Etapa 4 · Importação, histórico de 2026, saldo inicial e bloqueio (PR 4)

### Tarefa 4.2: Estados, origem `SISTEMA` e `CHECK`s (Lancamentos)

**Arquivos:**
- Criar: `db/migrations/V0014__transacoes_importacao.sql`
- Modificar: `Lancamentos/Dominio/Transacao.cs` (enums `RevisaoDaTransacao { ARevisar, Revisada }`, `ConfirmacaoDaTransacao { AConfirmar, Confirmada }` e `OrigemDaTransacao.Sistema`) e `LancamentosDbContext`.
- Teste: `MigracaoDeLancamentosTests.cs`

**Migração (V0014):**
- `revisao text NOT NULL DEFAULT 'REVISADA' CHECK (revisao IN ('A_REVISAR','REVISADA'))` e `confirmacao text NOT NULL DEFAULT 'CONFIRMADA' CHECK (confirmacao IN ('A_CONFIRMAR','CONFIRMADA'))`.
- O `CHECK` de origem ganha `'SISTEMA'`.
- `ck_transacoes_categoria_ou_a_revisar CHECK (categoria_id IS NOT NULL OR revisao = 'A_REVISAR' OR natureza = 'TRANSFERENCIA')` (D15).
- `ck_transacoes_parcelas` recriado: o limite de 24 vale só para `origem = 'MANUAL'` (D13).
- Colunas novas: `categoria_externa text NULL` (id da categoria da Pluggy; usada nas etapas 5 e 6) e `fatura_id_externo text NULL`.

- [ ] **Passo 1:** testes que falham:
  - `Lancamentos_de_hoje_passam_na_migracao`: dados sintéticos pré-V0014 com despesa, receita, transferência e parcelado;
  - `Despesa_sem_categoria_so_a_revisar`;
  - `Transferencia_revisada_sem_categoria_passa`;
  - `Parcela_36_de_banco_passa_e_manual_nao`;
  - `Origem_SISTEMA_aceita`.
- [ ] **Passo 2:** rodar → FALHA. Implementar. Rodar → PASSA. Commit "Lancamentos: estados de revisão e confirmação".

### Tarefa 4.3: Contrato de importação ("criar ou atualizar pela chave natural")

**Arquivos:**
- Criar: `Lancamentos/Contracts/IImportacaoDeLancamentos.cs` e `Lancamentos/Aplicacao/Importacao.cs`
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/ImportacaoTests.cs`

**Interfaces:**
```csharp
public sealed record LancamentoImportado(string IdExterno, DateOnly Data, long ValorCentavos, string? Descricao,
    bool AConfirmar, int? Parcela, int? Parcelas, string? CategoriaExterna, string? FaturaIdExterno);
public sealed record ResumoDaImportacao(int Criados, int Atualizados, int Ignorados, int ApagadosPeloBanco, int PendentesPromovidas, int PendentesSemPar);
public interface IImportacaoDeLancamentos
{
    /// Cria ou atualiza pela chave (OPEN_FINANCE, IdExterno), numa transação, sob o lock do casal e da conta.
    /// Janela: transações OPEN_FINANCE da conta com data >= desde e ausentes do lote são tratadas como apagadas pelo banco
    /// (confirmadas → soft delete) ou como pendentes a promover (a confirmar → par único na definitiva).
    Task<ResumoDaImportacao> ImportarAsync(Guid contaId, DateOnly desde, IReadOnlyList<LancamentoImportado> lote, CancellationToken ct);
}
```

**Regras:**
- O valor chega já no sinal do sistema: a `Sincronizacao` usa `ConversaoPluggy.ValorNoSistema(tipoDaConta, valor)`, decisão 10. Natureza pelo sinal: menor que 0 → `DESPESA`, maior que 0 → `RECEITA`. Todo item novo nasce `A_REVISAR`, com `categoria_id` nulo; a sugestão vem na etapa 5.
- A chave é procurada **também entre as apagadas**, com `IgnoreQueryFilters([Filtros.SoftDelete])` só dentro de `Importacao.cs` (regra 9, exceção D6). Apagada → ignorada, conta em `Ignorados`, nada é recriado.
- Atualização: muda `data`, `valor`, `confirmacao` e `parcela`. A `descricao` só é atualizada enquanto `A_REVISAR`. Categoria e descrição de linha `REVISADA` são do casal e nunca mudam.
- Pendente que sumiu do lote, com definitiva **única** (mesma conta, mesmo valor, data até 3 dias, descrição parecida): a definitiva recebe a categoria e a revisão da pendente, e a pendente vai para soft delete. Sem par único, fica como está e conta em `PendentesSemPar`. Na etapa 6, ela vai para "A conferir".
- "Descrição parecida": as descrições normalizadas (maiúsculas, sem acento, sem dígitos, espaços colapsados) são iguais, ou uma é prefixo da outra.
- Valor 0 → ignorado. Descrição nula ou vazia → `"(sem descrição)"`.
- Fatura de cartão: calculada pelo calendário do sistema (D2). `fatura_id_externo` é guardado só para conferência.

- [ ] **Passo 1:** testes que falham:
  - `Importar_cria_a_revisar_com_origem_OPEN_FINANCE`;
  - `Importar_de_novo_nao_duplica`;
  - `Reimportar_atualiza_valor_e_data`;
  - `Categoria_revisada_pelo_casal_e_preservada`;
  - `Descricao_revisada_pelo_casal_e_preservada`: o banco manda outra descrição para a mesma chave → a do casal fica (decisão 5);
  - `Apagada_pelo_banco_vira_soft_delete_com_auditoria`;
  - `Linha_apagada_nao_e_recriada`;
  - `Pendente_vira_definitiva_com_outro_id_mantendo_a_categoria`;
  - `Pendente_sem_par_unico_fica`;
  - `Parcela_futura_entra_no_mes_dela_e_fora_do_patrimonio_ate_a_data`;
  - `Valor_zero_nao_entra_e_nao_derruba_o_lote` (Foco 1);
  - `Descricao_vazia_vira_sem_descricao` (Foco 4);
  - `Falha_no_meio_nao_deixa_nada_visivel`: o lote com a 3ª linha inválida faz rollback total.
- [ ] **Passo 2:** rodar `--filter ImportacaoTests` → FALHA. Implementar. Rodar → PASSA. Commit "Lancamentos: importação por chave natural".

### Tarefa 4.4: Bloqueios em conta ligada, orçamento sem "a revisar" e saldo inicial

**Arquivos:**
- Modificar: `Lancamentos/Aplicacao/ServicoDeLancamentos.cs`, `Transferencias.cs`, `Conciliacao.cs` e `Lancamentos/Infra/ConsultaDeUso.cs`
- Teste: `LancamentosTests.cs`, `TransferenciasTests.cs`, `ConciliacaoTests.cs` e `UsoDoOrcamentoTests.cs`

**Regras:**
- Conta ligada (`ContaResumo.Ligada`):
  - `POST /lancamentos`, `POST /transferencias` e `DELETE /lancamentos/{id}` → 409 "Esta conta vem do banco. Os lançamentos chegam sozinhos.";
  - `PUT /lancamentos/{id}` só aceita mudar descrição e categoria; mudar valor, data ou conta → o mesmo 409;
  - `POST /contas/{id}/conciliacao` → 409 "Esta conta vem do banco. O saldo é conferido sozinho." (D11).
- Orçamento (D14):
  - `UsoPorCategoriaAsync`, `ReceitasAsync` e `AportesPorContaAsync` filtram `revisao = 'REVISADA'`;
  - `A_CONFIRMAR` conta.

- [ ] **Passo 1:** testes que falham:
  - `Lancamento_manual_em_conta_ligada_da_409`;
  - `Em_conta_ligada_so_descricao_e_categoria_mudam`;
  - `Transferencia_manual_com_conta_ligada_da_409`;
  - `Conciliacao_manual_em_conta_ligada_da_409`;
  - `A_revisar_fica_fora_do_uso_e_da_receita`;
  - `A_confirmar_conta_no_uso`.
- [ ] **Passo 2:** rodar → FALHA. Implementar. Rodar → PASSA. Commit "Lancamentos: conta ligada é do banco".

### Tarefa 4.5: Sincronização de um item (histórico, saldo inicial, conferência)

**Arquivos:**
- Criar: `Integracoes/Aplicacao/Sincronizacao.cs`; endpoint `POST /integracoes/items/{id}/sincronizacao`, que a tela chama logo depois do nascimento e que a etapa 7 limita.
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/SincronizacaoTests.cs`

**Regras:**
- Vínculo "não importar" (`nao_importar_em` preenchido, decisão 16) não sincroniza. Ao ser incluído, faz a primeira importação desde `historico_desde`.
- Vínculo cuja conta está **arquivada** (`ContaResumo.Arquivada`) não sincroniza: nada é lido nem importado, o histórico fica e `importado_ate` não anda (decisão 7).
- Por vínculo CONTA ou CARTAO, desde `historico_desde` na primeira importação; depois, desde `max(historico_desde, importado_ate − 10 dias)`. Assim, ao desarquivar, a próxima sincronização importa o período que ficou de fora. Ao terminar, `importado_ate` = hoje. lê `/v2/transactions`, mapeia e chama `ImportarAsync`. Cada vínculo é uma chamada; a falha num vínculo não desfaz os outros.
- `historico_desde`: 2026-01-01, ou a data mais antiga recebida se o banco entregar menos. A tela mostra "Histórico desde DD/MM".
- Saldo inicial, depois da **primeira** importação do vínculo: `DefinirSaldoInicialAsync(conta, saldoBanco − soma das confirmadas importadas até hoje, historico_desde)`.
  - Só para CONTA (corrente e poupança). O cartão não usa o saldo do banco (decisão 12); o saldo dele no sistema é o calculado pelas transações.
  - Saldo inicial do cartão importado, em `historico_desde`: −(total da fatura do banco que fechou antes do início do histórico e vence depois dele); sem essa fatura, 0 (CEO 11/11 21h50). Sem isso, o pagamento em janeiro da fatura de dezembro, que entra como crédito no cartão, deixaria o saldo do cartão positivo.
- Conferência de saldo a cada sincronização, só para CONTA: se o saldo do sistema hoje (`IConsultaDeSaldos`) diferir do esperado pela fórmula, grava `diferenca_centavos` e incrementa `diferencas_seguidas`; se bater, zera.
  - A tela mostra "Diferença de R$ X com o banco" só com `diferencas_seguidas >= 2`. Nada é ajustado.
- Item: `ultima_tentativa_em` sempre; `ultima_sincronizacao_em` só com sucesso.
- Faturas do cartão (decisão 12; substitui a fórmula do saldo da D3 e a comparação com o total calculado pelo sistema): `ConferenciaDasFaturas.Diferencas(faturas do banco, transações do vínculo, historico_desde)`, já feita na etapa 1.
  - Para cada fatura do banco, o total informado é comparado com a soma das transações com aquele `billId`; pendentes, sem `billId`, não entram.
  - Cada diferença é guardada por fatura no vínculo (coluna `faturas_divergentes jsonb NULL` em V0013: id da fatura → diferença e sincronizações seguidas). Ela aparece em Conexões ("Fatura de <mês>: diferença de R$ X com o banco") só se persistir em 2 sincronizações seguidas.
  - Nada muda no calendário nem na regra C7.
- Aplicação nova num item já nascido: vira vínculo com `conta_id` nulo e aparece em Conexões para escolher o grupo, pelo mesmo `POST …/nascimento` (só `aplicacoes`). Até lá, não entra em nenhum grupo.
- Reconciliação de conta ligada órfã (duas abas, E3 nota H), com teste (CEO, D18).

- [ ] **Passo 1:** testes que falham:
  - `Primeira_sincronizacao_importa_2026_e_calcula_saldo_inicial_que_fecha`;
  - `Banco_com_menos_historico_grava_a_data_mais_antiga`;
  - `Diferenca_so_aparece_na_segunda_sincronizacao_seguida`;
  - `Falha_num_vinculo_nao_desfaz_o_outro`;
  - `Sincronizacao_seguinte_usa_a_janela_de_10_dias`;
  - `Conta_arquivada_nao_sincroniza_e_mantem_o_historico`;
  - `Desarquivar_importa_o_periodo_que_ficou_de_fora`;
  - `Fatura_do_banco_diferente_so_aparece_na_segunda_sincronizacao_e_nao_muda_a_fatura`;
  - `Cartao_com_fatura_de_dezembro_paga_em_janeiro_nao_fica_positivo`: fatura sintética que fecha em 2025-12-28 e vence em 2026-01-05 com R$ 800,00; pagamento de R$ 800,00 importado em 2026-01-05 → saldo inicial −80000 em 2026-01-01 e saldo 0 depois do pagamento;
  - `Cartao_sem_fatura_anterior_nasce_com_saldo_inicial_zero_e_sem_conferencia_de_saldo`;
  - `Aplicacao_nova_espera_o_grupo`.
- [ ] **Passo 2:** rodar → FALHA. Implementar. Rodar → PASSA.
- [ ] **Passo 3:** front: o selo, "Histórico desde DD/MM" e a diferença em Conexões; os lançamentos com o selo "a confirmar" e o estado "a revisar" no extrato. `npm --prefix frontend run check`. Commit "Integracoes: importação do histórico e saldo inicial".

**Pronto quando:**
- `scripts/verificar.sh --tudo` está verde;
- o resultado da Tarefa 1.0 está registrado no plano (a 4.5 depende dele);
- o QA viu, com a Pluggy simulada, o histórico importado, o saldo que fecha e o 409 em conta ligada.

---

## Etapa 5 · Categorias: regras, mapa e revisão em lote (PR 5)

### Tarefa 5.1: Regras e mapa (Lancamentos)

**Arquivos:**
- Criar: `db/migrations/V0015__categorias_regras_e_mapa.sql` e `Lancamentos/Aplicacao/SugestaoDeCategoria.cs`
- Modificar: `Importacao.cs` (sugere ao criar)
- Teste: `CategoriasTests.cs` e `ImportacaoTests.cs`

**Migração (V0015):**
- `lancamentos.regras_de_categoria`: `id`, `casal_id`, `descricao_normalizada_cifrada bytea NOT NULL`, `descricao_hmac bytea NOT NULL`, `categoria_id REFERENCES lancamentos.categorias` e as colunas padrão; `ux_regras_descricao (casal_id, descricao_hmac) WHERE NOT is_deleted`.
- `lancamentos.mapa_categoria_externa`: `id`, `casal_id`, `categoria_externa text NOT NULL`, `categoria_id` e as colunas padrão; `ux_mapa_externa (casal_id, categoria_externa) WHERE NOT is_deleted`.
- `audit.proteger` nas duas.

**Regra:** a sugestão é, em ordem, (1) a regra pela `descricao_hmac` da descrição normalizada (a mesma normalização da Tarefa 4.3), (2) o mapa pela `categoria_externa`, (3) nada. A sugestão grava `categoria_id` e mantém `A_REVISAR`.

- [ ] **Passo 1:** testes que falham:
  - `Regra_de_descricao_vence_o_mapa`;
  - `Mapa_sugere_quando_nao_ha_regra`;
  - `Sem_regra_nem_mapa_fica_sem_categoria`;
  - `Descricao_da_regra_so_existe_cifrada`.
- [ ] **Passo 2:** rodar → FALHA. Implementar. Rodar → PASSA. Commit "Lancamentos: regras e mapa de categoria".

### Tarefa 5.2: Revisão em lote

**Arquivos:**
- Criar: `Lancamentos/Aplicacao/Revisao.cs`, endpoints e `frontend/src/paginas/RevisarImportados.tsx`
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/RevisaoTests.cs`

**Interfaces (API):**
- `GET /lancamentos/a-revisar` → `IReadOnlyList<GrupoARevisarDto(string Descricao, int Quantidade, long TotalCentavos, Guid? CategoriaSugeridaId, IReadOnlyList<Guid> Ids)>`, agrupado pela descrição normalizada.
- `POST /lancamentos/revisao` com o corpo `{ ids: Guid[], categoriaId: Guid, aplicar: "SO_ESTA" | "SEMPRE" | "SEMPRE_DO_BANCO" }`.
  - Marca `REVISADA` com a categoria. `SEMPRE` cria ou atualiza a regra pela descrição normalizada do primeiro id.
  - `SEMPRE_DO_BANCO` é o botão "sempre que o banco disser <categoria>": cria ou atualiza a entrada do mapa para a `categoria_externa` do primeiro id. Nenhum outro caminho grava no mapa; não há aprendizado silencioso (decisão 2).
  - Valida: a categoria é de despesa para despesa e de receita para receita, como hoje.
- `GET /lancamentos/categorias-externas` e `PUT /lancamentos/categorias-externas/{id}`: o mapa, editável na mesma tela (decisão 2).

- [ ] **Passo 1:** testes que falham:
  - `Aceitar_em_massa_marca_revisada`;
  - `Sempre_cria_regra_e_a_proxima_importacao_sugere`;
  - `So_esta_nao_cria_regra`;
  - `Sempre_que_o_banco_disser_grava_o_mapa`;
  - `Mapa_nunca_muda_sem_o_botao`: revisar 5 lançamentos da mesma categoria da Pluggy com `SO_ESTA` e `SEMPRE` não cria entrada no mapa;
  - `Revisada_passa_a_contar_no_orcamento`;
  - `Criar_categoria_na_revisao` (usa o `POST /categorias` existente).
- [ ] **Passo 2:** rodar → FALHA. Implementar. Rodar → PASSA.
- [ ] **Passo 3:** front "Revisar importados", agrupado, com aceitar em massa, "só esta / sempre que vier assim / sempre que o banco disser <categoria>" e criar categoria. Regerar o cliente. Testes de `lib`. Commit "Lancamentos: revisão em lote".

**Pronto quando:** verde, e o QA revisou um lote no navegador.

---

## Etapa 6 · Transferências, pagamento de fatura, "A conferir" e rendimentos (PR 6)

### Tarefa 6.1: Tabela "A conferir" e o par manual

**Arquivos:**
- Criar: `db/migrations/V0016__a_conferir.sql`, `Lancamentos/Aplicacao/AConferir.cs` e `Lancamentos/Aplicacao/Pareamento.cs`
- Modificar: `IImportacaoDeLancamentos` (métodos abaixo) e `Importacao.cs`, onde `PendentesSemPar` e a pendente que sumiu passam a gerar itens
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/AConferirTests.cs`

**Migração (V0016), `lancamentos.itens_a_conferir`:**
- colunas: `id`, `casal_id`, `tipo text CHECK (tipo IN ('TRANSFERENCIA_SEM_PAR','PENDENTE_SUMIU','PENDENTE_SEM_PAR','APORTE_OU_RESGATE','DIFERENCA_NEGATIVA'))`, `chave text NOT NULL`, `conta_id uuid NOT NULL`, `transacao_id uuid NULL`, `valor_centavos bigint NOT NULL`, `data date NOT NULL`, `estado text CHECK (estado IN ('ABERTO','RESOLVIDO'))`, `resolucao text NULL`;
- `ux_a_conferir_chave (casal_id, chave) WHERE NOT is_deleted`;
- `audit.proteger`.
- Item criado ou atualizado pela chave: `pendente-sumiu:<transacao_id>`, `sem-par:<transacao_id>`, `movimento:<transacao_id>`, `diferenca:<conta_id>:<AAAA-MM-DD>`. Nada é descartado.

**Interfaces (acrescentadas a `IImportacaoDeLancamentos`):**
```csharp
Task<bool> ParearComoTransferenciaAsync(Guid saidaId, Guid entradaId, CancellationToken ct);         // D9: mantém ids, tira categoria, mesmo grupo
Task<Guid?> CriarParManualAsync(Guid transacaoImportadaId, Guid contaManualId, CancellationToken ct); // D9/D10: chave par:<id>, origem SISTEMA
```

**API:**
- `GET /a-conferir` → `IReadOnlyList<ItemAConferirDto(Guid Id, string Tipo, Guid ContaId, string ContaApelido, DateOnly Data, long ValorCentavos, string? Descricao, IReadOnlyList<OpcaoDto> Opcoes)>`.
- `POST /a-conferir/{id}/resolucao` com o corpo `{ opcao: "TRANSFERENCIA" | "GASTO" | "RECEITA" | "APORTE" | "RESGATE" | "IR_IOF_TAXA" | "DEIXAR_PARA_DEPOIS", contaId?, categoriaId?, outraTransacaoId? }`.

- [ ] **Passo 1:** testes que falham:
  - `Pendente_que_sumiu_aparece_uma_vez`;
  - `Pendente_sem_par_unico_vai_para_a_conferir`;
  - `Transferencia_para_conta_manual_cria_o_lado_manual_com_chave_par`;
  - `Repetir_a_resolucao_nao_duplica`;
  - `Nada_e_descartado` (cada resolução deixa `RESOLVIDO`, nunca apaga o item).
- [ ] **Passo 2:** rodar → FALHA. Implementar. Rodar → PASSA. Commit "Lancamentos: caixa A conferir".

### Tarefa 6.2: Reconhecimento de transferências e de pagamento de fatura

**Arquivos:**
- Criar: `Lancamentos/Aplicacao/Reconhecimento.cs`, chamado pela `Sincronizacao` depois de importar todos os vínculos do casal
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/ReconhecimentoTests.cs`

**Interface:** `Task ReconhecerMovimentosAsync(DateOnly desde, CancellationToken ct)` em `IImportacaoDeLancamentos`.

**Regras:**
- Transferência: saída numa conta ligada e entrada noutra conta ligada do casal, mesmo |valor|, datas até 1 dia útil, **par único** → `ParearComoTransferenciaAsync`. Sem par único → item `TRANSFERENCIA_SEM_PAR`.
  - O pareamento e o reconhecimento por categoria valem **entre items e conexões diferentes do mesmo casal** (decisão 15): dois bancos do mesmo cônjuge, ou contas dos dois cônjuges. A busca do par é por casal, nunca por item.
  - Como cada item sincroniza na sua vez (job por conexão, "Atualizar agora"), uma saída sem par só vira item `TRANSFERENCIA_SEM_PAR` depois de 2 dias úteis da data. Um item aberto que ganha par único depois é resolvido sozinho, com a resolução "pareado automaticamente" (CEO 11/11 21h50).
- Os papéis vêm de `CategoriasDoBanco.Papel(categoria_externa)` (`Lancamentos.Contracts`), a única tabela de códigos (decisão 13). O teste de arquitetura `Codigos_de_categoria_da_Pluggy_so_na_tabela` recusa os códigos em outro arquivo.
- Pagamento de fatura (D10): saída da corrente com papel `PagamentoDeFatura` e crédito no cartão ligado do mesmo valor, até 1 dia útil → par.
  - Cartão não ligado → item, e a resolução cria o lado do cartão (`CriarParManualAsync`).
- Aplicação e resgate (D12): transação da corrente com papel `Aplicacao` ou `ResgateOuRendimento` → item `APORTE_OU_RESGATE`, com o grupo sugerido pelas regras de descrição.
- `MesmaTitularidade` reforça o par de transferência: é candidata mesmo sem a outra perna na mesma data. Sem par único, vai para "A conferir".
- `Boleto` fica guardado para o bloco Boletos. `Iof` e `ImpostoDeRenda` são sugestão de categoria na revisão (etapa 5) e na resolução `IR_IOF_TAXA`.
- Sem papel (`Nenhum`, inclusive sem categoria): nada automático, só as regras de descrição e "A conferir".
- Os papéis sem regra própria (`MesmaTitularidade`, `Boleto`, `Iof`, `ImpostoDeRenda`) seguem como descrito acima (CEO 11/11 21h50).

- [ ] **Passo 1:** testes que falham:
  - `Transferencia_entre_ligadas_vira_par_nunca_gasto_e_receita`;
  - `Transferencia_entre_items_diferentes_do_casal_vira_par`: corrente do item A, conta do item B, mesma pessoa (decisão 15);
  - `Mesma_titularidade_entre_items_diferentes_vira_par`: categoria `04000000` nos dois lados, em items diferentes;
  - `Sem_par_so_vai_para_a_conferir_depois_de_2_dias_uteis`;
  - `Item_sem_par_que_ganha_par_depois_se_resolve_sozinho`;
  - `Dois_candidatos_vao_para_a_conferir`;
  - `Pagamento_de_fatura_usa_as_duas_pernas_do_banco`;
  - `Pagamento_de_fatura_nao_conta_como_gasto`;
  - `Aplicacao_vai_para_a_conferir_como_aporte`.
- [ ] **Passo 2:** rodar → FALHA. Implementar. Rodar → PASSA. Commit "Lancamentos: transferência e pagamento de fatura reconhecidos".

### Tarefa 6.3: Rendimento automático do grupo de investimento

**Arquivos:**
- Criar: `Lancamentos/Aplicacao/RendimentoAutomatico.cs`
- Modificar: `Sincronizacao.cs`, que soma os saldos das aplicações por `conta_id` e chama o rendimento
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/RendimentoAutomaticoTests.cs`

**Interfaces (em `IImportacaoDeLancamentos`):**
- `Task RegistrarSaldoDoBancoAsync(Guid contaId, DateOnly dia, long saldoDoBancoCentavos, CancellationToken ct)`;
- `Task<bool> TemMovimentoAConferirAsync(IReadOnlyCollection<Guid> contasIds, CancellationToken ct)`: há item `APORTE_OU_RESGATE` aberto em alguma dessas contas.

**Regras:**
- A `Sincronizacao` (Integracoes) segura o rendimento **só dos grupos do mesmo item** (decisão 3). Antes de chamar `RegistrarSaldoDoBancoAsync` para os grupos de um item, ela chama `TemMovimentoAConferirAsync` com as contas CONTA daquele item. Se der verdadeiro, pula os grupos desse item; os grupos de outros items seguem.
- Senão, diferença = saldo do banco − saldo do sistema no dia:
  - maior que 0 → receita "Rendimentos", origem `SISTEMA`, chave `rendimento:<conta>:<dia>`. Se a chave já existe no dia, **atualiza** o valor para `valor atual + diferença` (decisão de 18h58);
  - menor que 0 → item `DIFERENCA_NEGATIVA`, chave `diferenca:<conta>:<dia em que abriu>`. Enquanto houver um aberto para a conta, ele é atualizado (valor e data da última leitura), em vez de abrir outro.
- Opções da diferença negativa (decisão 4): só `IR_IOF_TAXA` e `DEIXAR_PARA_DEPOIS`. Não existe "resgate não identificado".
- Resolução `IR_IOF_TAXA` → despesa origem `SISTEMA`, chave `ajuste-investimento:<conta>:<dia>`, na categoria escolhida, criada ou atualizada pela chave; o item fica `RESOLVIDO`.
- Resolução `DEIXAR_PARA_DEPOIS` → nenhum lançamento; o item continua `ABERTO` em "A conferir".

- [ ] **Passo 1:** testes que falham:
  - `Diferenca_positiva_vira_rendimento`;
  - `Segunda_sincronizacao_do_dia_atualiza_o_mesmo_rendimento`;
  - `Diferenca_negativa_vai_para_a_conferir_e_nunca_some`;
  - `IR_IOF_cria_despesa_na_categoria_escolhida`;
  - `Deixar_para_depois_nao_lanca_e_mantem_aberto`;
  - `Diferenca_negativa_aberta_e_atualizada_nao_duplicada`;
  - `Resgate_nao_identificado_nao_e_opcao`: a opção é recusada com 400;
  - `Aporte_pendente_segura_so_os_grupos_do_mesmo_item`: aporte aberto na corrente do item A → grupo do item A sem rendimento; grupo do item B com rendimento;
  - `Rendimento_nao_entra_na_receita_do_orcamento` (regra existente).
- [ ] **Passo 2:** rodar → FALHA. Implementar. Rodar → PASSA.
- [ ] **Passo 3:** front "A conferir", com as opções por tipo. Regerar o cliente. Commit "Lancamentos: rendimento automático e A conferir no front".

**Pronto quando:** verde, e o QA resolveu uma transferência, um pagamento de fatura e uma diferença negativa no navegador.

---

## Etapa 7 · Job diário, "Atualizar agora", falhas, consentimento e Painel (PR 7)

### Tarefa 7.1: Execuções e trava por item

**Arquivos:**
- Criar: `db/migrations/V0017__integracoes_execucoes.sql` e `Integracoes/Dominio/Execucao.cs`
- Modificar: `Sincronizacao.cs`
- Teste: `SincronizacaoTests.cs`

**Migração (V0017), `integracoes.execucoes`:**
- colunas: `id`, `casal_id`, `item_id`, `origem text CHECK (origem IN ('JOB','MANUAL'))`, `iniciada_em`, `terminada_em NULL`, `resultado text NULL CHECK (resultado IN ('OK','ERRO'))`, `motivo text NULL`, `resumo jsonb NULL` (só contagens);
- `audit.proteger`.
- Trava: `pg_advisory_xact_lock(hashtextextended('integracoes.item:' || item_id, 0))`, ou uma sessão por item.

- [ ] **Passo 1:** testes que falham:
  - `Sincronizacoes_simultaneas_nao_duplicam` (Foco 5);
  - `Toda_sincronizacao_registra_execucao`;
  - `Resumo_so_tem_contagens`.
- [ ] **Passo 2:** rodar → FALHA. Implementar. Rodar → PASSA. Commit "Integracoes: execuções e trava por item".

### Tarefa 7.2: Serviço diário, "Atualizar agora", falhas e consentimento

**Arquivos:**
- Criar: `Integracoes/Aplicacao/SincronizacaoDiaria.cs` (`BackgroundService` + `PeriodicTimer` de 1 min que dispara às 04:30 de São Paulo, uma vez por dia)
- Modificar: `EndpointsDeConexoes.cs`, `IntegracoesModule.cs` e `docs/adr/0010-jobs-no-postgres.md` (só se a implementação divergir da nota do CEO)
- Teste: `SincronizacaoTests.cs` e `backend/tests/GestorFinanceiro.UnitTests/AgendaDiariaTests.cs`

**Regras:**
- O job roda por casal, como `UsuarioSistema(casalId)` com `Ip = "sistema:integracoes"` (D21). `UsuarioSistema` ganha o parâmetro opcional `ip`.
- "Atualizar agora": `POST /integracoes/conexoes/{id}/atualizacao`, só do dono. Menos de 10 minutos desde a última tentativa → 429 "Espere alguns minutos para atualizar de novo.".
- Erro → item `ERRO` com o motivo traduzido:
  - 401/403 → "As chaves da Pluggy foram recusadas.";
  - item `LOGIN_ERROR` ou `OUTDATED` → "Renove o consentimento no Meu Pluggy.";
  - 429 ou 5xx → "A Pluggy não respondeu. Tentaremos de novo."

  Nada é apagado.
- Consentimento: faltando 15 dias ou menos → `CONSENTIMENTO_VENCENDO`, com o aviso nomeando o cônjuge. Vencido → `CONSENTIMENTO_VENCIDO`, e a sincronização para.

- [ ] **Passo 1:** testes que falham:
  - `Agenda_dispara_as_04_30_de_Sao_Paulo_uma_vez_por_dia`, com `RelogioFixo`;
  - `Atualizar_agora_limita_a_10_minutos`;
  - `Erro_vira_motivo_traduzido_sem_corpo`;
  - `Aviso_15_dias_antes_nomeia_o_conjuge`;
  - `Consentimento_vencido_para_a_sincronizacao`;
  - `Job_grava_auditoria_como_sistema_integracoes`.
- [ ] **Passo 2:** rodar → FALHA. Implementar. Rodar → PASSA. Commit "Integracoes: sincronização diária e Atualizar agora".

### Tarefa 7.3: Painel (D16)

**Arquivos:**
- Criar: `Integracoes/Contracts/ConexoesDoCasal.cs`
- Modificar: `Painel/Contracts/VisaoGeral.cs`, `Painel/Aplicacao/MontagemDoPainel.cs`, `Lancamentos/Contracts` (contagens) e `frontend/src/paginas/VisaoGeral.tsx`
- Teste: `VisaoGeralTests.cs` e `RegrasDoPainelTests.cs`

**Interfaces:**
```csharp
// Integracoes.Contracts
public sealed record EstadoDoItem(string Banco, string Estado, DateTimeOffset? UltimaSincronizacaoEm, string? Motivo, string? ConjugeQuePrecisaRenovar);
public interface IConexoesDoCasal { Task<IReadOnlyList<EstadoDoItem>> ListarAsync(CancellationToken ct); }
// Lancamentos.Contracts (interface nova)
public interface IPendenciasDeLancamentos
{
    Task<int> ARevisarAsync(CancellationToken ct);
    Task<int> AConferirAsync(CancellationToken ct);
}
```
- `VisaoGeralDto` ganha `SecaoDto<PendenciasDto> Pendencias`, com `PendenciasDto(int ARevisar, int AConferir, IReadOnlyList<EstadoDoItem> Bancos)`. O texto é "Última atualização do <banco>: <data> · Tentar de novo".

- [ ] **Passo 1:** testes que falham:
  - `Painel_mostra_a_revisar_a_conferir_e_ultima_atualizacao`;
  - `Falha_da_secao_nao_derruba_o_painel` (padrão `SecaoDto.Erro`);
  - `Painel_nao_referencia_EF` (o teste existente continua passando).
- [ ] **Passo 2:** rodar → FALHA. Implementar. Regerar o cliente. Front. Rodar → PASSA. Commit "Painel: pendências e última atualização do banco".

**Pronto quando:**
- `scripts/verificar.sh --tudo` está verde;
- o QA viu no navegador, com a Pluggy simulada: o job (relógio adiantado), o "Atualizar agora" (inclusive o limite), o erro e o aviso de consentimento.

---

## Decisões do CEO

Decisões sobre o "Para o CEO" da primeira versão deste plano (PR #30). Elas valem sobre o texto das tarefas.

1. **Nome do banco** (CEO 11/11 19h). O nome digitado nunca entra na trava de conta repetida. A trava usa agência e conta, mais o código do banco se a medição mostrar que ele vem da Pluggy. O nome serve só ao apelido. → Tarefas 1.0 (item 7 da medição), 1.3 (`ContaPluggy.CodigoDoBanco`) e 3.2.
2. **Mapa de categorias** (CEO 11/11 19h). Editável em "Revisar importados", com o botão "sempre que o banco disser <categoria>"; sem aprendizado silencioso. → Tarefa 5.2.
3. **Aporte pendente** (CEO 11/11 19h). Segura o rendimento só dos grupos do mesmo item. → Tarefa 6.3.
4. **Diferença negativa** (CEO 11/11 19h). "Resgate não identificado" sai. Ficam "IR/IOF ou taxa" (despesa na categoria escolhida, chave `ajuste-investimento`) e "deixar para depois" (fica em "A conferir", sem lançamento). → Tarefas 6.1 e 6.3.
5. **Descrição depois da revisão** (CEO 11/11 19h). Passa a ser do casal, como a categoria. → Tarefa 4.3.
6. **Portador do cartão por HMAC** (CEO 11/11 19h): confirmado. → Tarefa 3.2.
7. **Arquivar conta ligada** (CEO 11/11 19h). Desliga a sincronização do vínculo e mantém o histórico. Desarquivar religa e importa o período que ficou de fora. Apagar continua recusado. → Tarefas 3.1, 3.2 (`importado_ate`) e 4.5.
8. **Reconhecimento pela categoria da Pluggy** (CEO 11/11 19h), com "A conferir" como reserva: confirmado. → Tarefa 6.2.
9. **Medição** (CEO 11/11 19h). O `medir-saldos.sh`, incluindo de onde vem o banco, é o primeiro commit do PR da etapa 1 e é rodado pela pessoa. As tarefas 4.2 e 4.3 podem andar antes do resultado. → Tarefa 1.0.

### Sobre o resultado da medição (CEO 11/11 21h)

10. **Sinal** (CEO 11/11 21h). O conversor normaliza o sinal por tipo de conta: na corrente, o da Pluggy já é o do sistema; no cartão, DEBIT positivo é gasto e CREDIT negativo é pagamento ou estorno, então o sinal se inverte. → `ConversaoPluggy.ValorNoSistema` (etapa 1) e Tarefa 4.3.
11. **Trava de conta repetida** (CEO 11/11 21h). HMAC do `transferNumber` normalizado (só os dígitos), sem código do banco: a Pluggy não o traz. `ContaPluggy.CodigoDoBanco` sai do modelo. Substitui a parte do código na decisão 1; o nome digitado continua fora da trava. → `ConversaoPluggy.TransferNumberNormalizado` (etapa 1) e Tarefa 3.2.
12. **Conferência do cartão por fatura** (CEO 11/11 21h). Substitui a fórmula do saldo da D3, que a medição mostrou não fechar. Para cada fatura do banco, o total informado é comparado com a soma das transações com aquele `billId`. A diferença aparece em "Conexões" se persistir por duas sincronizações. Pendentes (sem `billId`) não entram. O saldo do cartão no sistema continua sendo o calculado pelas transações. Só é conferida a fatura que vence a partir de dois meses depois do início do histórico, para não acusar compras de antes do histórico (CEO 11/11 21h50). → `ConferenciaDasFaturas` (etapa 1) e Tarefa 4.5.
13. **Reconhecimento por categoria** (CEO 11/11 21h). Usa os códigos medidos: `05100000` pagamento de fatura, `03000000` e `03020000` aplicação, `03060000` resgate ou rendimento, `04000000` mesma titularidade, `05010000` boleto, `15030000` IOF e `15010000` IR. A tabela fica num lugar só, com teste. → `Lancamentos.Contracts.CategoriasDoBanco` (etapa 1) e Tarefa 6.2.
14. **Aviso na espera da medição** (CEO 11/11 21h). Antes da espera, o `medir-saldos.sh` imprime "aguardando 60 s para a segunda leitura do saldo...": a pessoa achou que ele tinha travado. → Tarefa 1.0.
15. **Transferência entre items** (CEO 11/11 21h, medição do segundo banco: 7 transações `04000000` entre contas da mesma pessoa em bancos diferentes). O pareamento de transferência (D9) e o reconhecimento por categoria funcionam entre items do mesmo casal, não só dentro de um item, com teste de duas contas de items diferentes. → Tarefa 6.2.
16. **"Não importar"** (CEO 11/11 21h, medição do segundo banco: dois cartões sem transação no ano). Na tela de confirmação, cada conta encontrada pode ser marcada "não importar". Ela fica registrada como ignorada: não vira conta nem é perguntada de novo a cada sincronização. Pode ser incluída depois em "Conexões". → Tarefas 3.2 (`nao_importar_em`), 3.3 e 4.5.

### Confirmações e ajuste (CEO 11/11 21h50)

17. **Confirmadas** as três decisões do Dev: as faturas dos 2 primeiros meses ficam fora da conferência (12); a transferência sem par vai para "A conferir" depois de 2 dias úteis e se resolve sozinha (15); os papéis sem regra própria seguem como descritos (13).
18. **Saldo inicial do cartão** (CEO 11/11 21h50). É −(total da fatura do banco que fechou antes do início do histórico e vence depois dele); sem essa fatura, 0. Motivo: o pagamento em janeiro da fatura de dezembro deixaria o cartão positivo. → Tarefa 4.5, teste `Cartao_com_fatura_de_dezembro_paga_em_janeiro_nao_fica_positivo`.
