# Contas e lançamentos (Fase 1, bloco 1) — plano de implementação

> **Para agentes:** SUB-SKILL OBRIGATÓRIA: use superpowers:subagent-driven-development (recomendado) ou superpowers:executing-plans para executar este plano tarefa a tarefa. Os passos usam checkbox (`- [ ]`).

**Objetivo:** o casal registra à mão as contas (incluindo a conta conjunta Itaú) e os cartões, as categorias e os lançamentos do dia a dia: despesas, receitas, compras parceladas e transferências, inclusive o pagamento da fatura. Com isso dá para fechar um mês à mão, que é o critério da Fase 1.

**Arquitetura:**
- O módulo **Contas** (schema `contas`) é dono das contas e cartões: titular, dados bancários cifrados, saldo inicial, dias de fechamento e vencimento. Ele expõe `IConsultaDeContas` em `Contracts`.
- O módulo **Lancamentos** (schema `lancamentos`) é dono das categorias e das transações. Ele usa só o `Contracts` de Contas, para validar a conta e saber de qual fatura é cada compra. Também calcula saldos e totais de fatura, porque é ele quem tem as transações.
- A dependência vai num sentido só (Lancamentos → Contas.Contracts). Não há FK entre schemas.
- A caixa "Conciliar" fica para o bloco do Open Finance.

**Tecnologias:** .NET 10, EF Core 10 + Npgsql, xUnit + Testcontainers (projeto de integração existente), React 19 + TanStack Query + openapi-fetch.

**Especificação:** `docs/SPEC.md` (módulos Contas e Lancamentos; tarefa "Módulo Lancamentos: tabela `transacoes` com `UNIQUE (origem, id_externo) WHERE NOT is_deleted`"), `docs/design/telas/Lancamentos.dc.html` (extrato, filtros, lançamento rápido), `docs/design/telas/Main.dc.html` (cartão da conta: "Ag. •••• · C/C ••••-7"), `docs/design/README.md`, `CLAUDE.md`, `.claude/rules/backend.md`, `.claude/rules/frontend.md`. Planos anteriores, com as convenções: `docs/superpowers/plans/2026-11-05-cadastro-do-casal.md` e `docs/superpowers/plans/2026-11-05-login.md`.

## Decisões já tomadas

1. **Cartão com fatura** (escolha do usuário):
   - Cada cartão tem dia de fechamento e dia de vencimento.
   - Toda compra no cartão recebe a data de vencimento da fatura a que pertence (`fatura_vencimento`). Uma compra feita depois do fechamento vai para a fatura seguinte.
   - O endpoint de saldos devolve, por cartão, o total da fatura aberta e da última fechada.
   - A tela de faturas e boletos fica para o bloco Boletos.
2. **Parcelas ligadas** (escolha do usuário):
   - Uma compra em N vezes (2 ≤ N ≤ 24, só em cartão) gera N lançamentos com o mesmo `grupo_id`, `parcela` 1..N e `parcelas` = N, cada um numa fatura seguinte.
   - O valor é dividido em centavos; a sobra da divisão vai para a primeira parcela. Sem juros.
   - Editar ou apagar a compra pede um escopo: só esta parcela, ou esta e as próximas. Parcelas de faturas já fechadas nunca mudam pelo escopo "esta e as próximas".
3. **Transferências** (escolha do usuário):
   - Uma transferência é um par de lançamentos com o mesmo `grupo_id` e `natureza = TRANSFERENCIA`: um negativo na conta de origem e um positivo na de destino, na mesma data e com o mesmo valor absoluto.
   - Transferências não entram em despesa nem em receita.
   - Pagar a fatura é uma transferência da conta para o cartão. Editar ou apagar um lado age sobre os dois.
4. **"Quem" vem do titular da conta** (escolha do usuário):
   - Cada conta tem `titular_usuario_id`, que é nulo quando a conta é conjunta. O extrato mostra o apelido do titular ou "Conjunta".
   - Quem digitou o lançamento fica em `autor_usuario_id`, para a auditoria. Isso não aparece como "Quem".
5. **Propostas deste plano (revisar):**
   - **Sinal e natureza:**
     - `valor_centavos` é `bigint` com sinal: negativo é saída, positivo é entrada.
     - `natureza` é `DESPESA`, `RECEITA` ou `TRANSFERENCIA`. A API recebe um valor positivo mais a natureza e grava o sinal.
     - Um estorno é uma `RECEITA` na conta ou no cartão.
   - **Lançamento manual e idempotência:**
     - `origem = 'MANUAL'` e `id_externo` = uma chave de idempotência (UUID) gerada pelo front a cada formulário aberto.
     - Repetir o mesmo POST devolve o lançamento já criado (`200`), não duplica.
     - Isso usa o próprio `UNIQUE (origem, id_externo) WHERE NOT is_deleted` do SPEC.
     - Parcelas e o par da transferência derivam a chave: `<chave>:1..N` e `<chave>:origem`/`<chave>:destino`.
   - **Busca por texto:** como a descrição e a contraparte são cifradas, a busca decifra no servidor só os lançamentos do mês filtrado e compara sem acento e sem diferenciar maiúsculas. O banco nunca vê o texto da busca.
   - **Categorias padrão:**
     - criadas por casal na primeira consulta de categorias, se o casal não tiver nenhuma;
     - despesa: Mercado, Restaurantes, Transporte, Casa e contas, Saúde, Lazer, Educação, Investimento, Dívidas e financiamentos, Outros;
     - receita: Salário, Outras receitas;
     - o casal pode criar, renomear e arquivar categorias; arquivar é soft delete.
     - Transferências não têm categoria.
   - **Dados bancários:**
     - `agencia_cifrada` e `numero_cifrado` são opcionais, via `ICofre`. A API devolve só a forma mascarada: `•••• ` e o último dígito do número (como em Main.dc.html).
     - Instituição e apelido da conta (ex.: "Itaú conjunta") ficam em claro.
   - **Saldo:** saldo atual = `saldo_inicial_centavos` (em Contas, na data `saldo_inicial_em`) + soma das transações com `data >= saldo_inicial_em`.
   - **Lançamento rápido:**
     - o formulário da tela tem valor, descrição, categoria, conta e data (padrão hoje), e um seletor Despesa / Receita / Transferência;
     - quando a conta é um cartão aparece "Parcelas" (1 a 24);
     - na transferência, os campos viram conta de origem e conta de destino.
   - **Navegação:** "Lançamentos" ganha a tela real. Entra o item "Contas e cartões" na seção Sistema da barra lateral, em `/contas`. Visão geral e orçamento continuam como estão; o dashboard é outro bloco.

## Restrições globais

- **Regras do `CLAUDE.md` valem integralmente:**
  - sem DELETE físico (apagar = `Remove` → soft delete);
  - auditoria só por trigger (`SELECT audit.proteger('schema.tabela');` em toda tabela nova, e `GRANT USAGE ON SCHEMA <schema> TO gf_app;` na migração que cria o schema);
  - unicidade só com índice parcial `WHERE NOT is_deleted`;
  - filtros `SoftDelete` e `Casal` sempre ligados nas consultas;
  - módulos não leem o schema um do outro;
  - API só por DTO em `/api/v1`, com erros em Problem Details.
- **Migrações novas:** `db/migrations/V0003__contas.sql` e `db/migrations/V0004__lancamentos.sql`. Nunca editar V0001 e V0002.
- **Dinheiro:** `Dinheiro` (centavos em `long`) no domínio, `bigint` no banco, `valorCentavos` (inteiro) nos DTOs. Nunca `double`/`float`/`decimal` para guardar valor. No front, `formatarReais` para exibir, e uma função nova de conversão de texto BR para centavos.
- **Dado sensível:**
  - `transacoes.descricao_cifrada` (obrigatória) e `transacoes.contraparte_cifrada` (opcional), via `ICofre.Cifrar` com AAD `lancamentos.transacoes|<coluna>|{id:N}`;
  - `contas.agencia_cifrada` e `contas.numero_cifrado`, com AAD `contas.contas|<coluna>|{id:N}`;
  - nada disso aparece em log, exceção ou corpo de erro, nem em claro na auditoria (as colunas `*_cifrada`/`*_cifrado` já aparecem como "alterado").
- **Tamanhos:** descrição 1–140 caracteres; contraparte 0–140; apelido da conta 1–60; nome de categoria 1–40.
- **Autenticação:** todas as rotas novas exigem sessão (a política padrão do grupo `/api/v1` já faz isso). O `casal_id` vem da sessão, nunca do corpo da requisição.
- **Contrato:** mudou o contrato → `dotnet build backend/src/Host/GestorFinanceiro.Api && npm --prefix frontend run gen:api` no mesmo commit.
- **Antes de cada commit:** `dotnet test backend/GestorFinanceiro.slnx` verde (precisa de Docker) e 0 avisos em `dotnet build backend/GestorFinanceiro.slnx --no-incremental`.
- **Commits e branch:** commits curtos em pt-BR, terminando com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Branch `fase-1/contas-e-lancamentos`, criada a partir da `main`.

## Foco da revisão

1. **Compra no dia do fechamento e meses curtos:**
   - compra exatamente no dia de fechamento entra na fatura que fecha naquele dia;
   - com fechamento no dia 31, em fevereiro o fechamento cai no último dia do mês;
   - um vencimento anterior ao fechamento no calendário vence no mês seguinte.

   Teste: `CalendarioDaFaturaTests` (Tarefa 1).
2. **Duplo clique em "Salvar"** → um único lançamento, mesmo com duas requisições simultâneas com a mesma chave. Teste: `Mesma_chave_em_paralelo_cria_um_so_lancamento` (Tarefa 4).
3. **Parcelamento de R$ 100,00 em 3 vezes** → 33,34 + 33,33 + 33,33, somando exatamente 100,00, em três faturas seguidas. Teste: `Parcelas_somam_o_total_e_caem_em_faturas_seguidas` (Tarefa 4).
4. **Transferência para a mesma conta, ou para uma conta de outro casal** → `400`/`404`, e nada é gravado. Teste: `Transferencia_invalida_nao_grava_nada` (Tarefa 5).
5. **Um cônjuge vê e edita os lançamentos do casal, mas nunca os de outro casal**, inclusive pelo id direto na URL (`404`). Teste: `Lancamento_de_outro_casal_responde_404` (Tarefa 4).

---

### Tarefa 1: Módulo Contas — contas, cartões e calendário da fatura

**Arquivos:**
- Criar: `db/migrations/V0003__contas.sql`
- Criar: `backend/src/Modules/Contas/Dominio/Conta.cs`, `Dominio/CalendarioDaFatura.cs`
- Criar: `backend/src/Modules/Contas/Infra/ContasDbContext.cs`, `Aplicacao/ServicoDeContas.cs`, `Infra/ConsultaDeContas.cs`, `Api/EndpointsDeContas.cs`
- Criar: `backend/src/Modules/Contas/Contracts/IConsultaDeContas.cs`, `Contracts/Contas.cs` (DTOs)
- Modificar: `backend/src/Modules/Contas/ContasModule.cs`, o csproj do módulo (as mesmas referências do Identidade)
- Modificar: `backend/tests/GestorFinanceiro.ArchitectureTests/RegrasDeArquitetura.cs` (regra nova abaixo)
- Teste: `backend/tests/GestorFinanceiro.UnitTests/CalendarioDaFaturaTests.cs`, `backend/tests/GestorFinanceiro.IntegrationTests/ContasTests.cs`

**Interfaces:**
- **V0003:**
  - `CREATE SCHEMA contas` e `GRANT USAGE ON SCHEMA contas TO gf_app`;
  - tabela `contas.contas`:
    - `id`, `casal_id`;
    - `tipo text CHECK (tipo IN ('CORRENTE','POUPANCA','CARTAO','DINHEIRO'))`;
    - `apelido text NOT NULL` (1–60) e `instituicao text NULL` (≤ 60);
    - `titular_usuario_id uuid NULL` (nulo = conjunta; sem FK para `identidade`, que é schema de outro módulo);
    - `agencia_cifrada bytea NULL` e `numero_cifrado bytea NULL`;
    - `saldo_inicial_centavos bigint NOT NULL DEFAULT 0` e `saldo_inicial_em date NOT NULL`;
    - `dia_fechamento smallint NULL` e `dia_vencimento smallint NULL` (1–31);
    - `limite_centavos bigint NULL`;
    - colunas padrão de auditoria e soft delete;
    - `CHECK ((tipo = 'CARTAO') = (dia_fechamento IS NOT NULL AND dia_vencimento IS NOT NULL))`;
  - índice `ux_contas_apelido ON (casal_id, lower(apelido)) WHERE NOT is_deleted`;
  - `SELECT audit.proteger('contas.contas');`.
- **`CalendarioDaFatura`** (internal static, `Dominio`): `static DateOnly VencimentoDaFatura(DateOnly dataCompra, int diaFechamento, int diaVencimento)`.
  - Regra: o fechamento do mês da compra é `min(diaFechamento, último dia do mês)`.
  - Se a compra for no dia do fechamento ou antes, a fatura é a que fecha nesse mês; senão, é a do mês seguinte.
  - O vencimento é `min(diaVencimento, último dia)` no mês do fechamento se `diaVencimento > diaFechamento`; caso contrário, no mês seguinte ao fechamento.
  - Também `static DateOnly FechamentoDaFatura(DateOnly vencimento, int diaFechamento, int diaVencimento)`, o inverso, usado para saber se uma fatura já fechou.
- **`IConsultaDeContas`** (Contracts):
  - `Task<ContaResumo?> ObterAsync(Guid contaId, CancellationToken ct)`;
  - `Task<IReadOnlyList<ContaResumo>> ListarAsync(CancellationToken ct)`;
  - `DateOnly? VencimentoDaFatura(ContaResumo conta, DateOnly dataCompra)`: devolve `null` se não for cartão;
  - `sealed record ContaResumo(Guid Id, string Apelido, string Tipo, Guid? TitularUsuarioId, long SaldoInicialCentavos, DateOnly SaldoInicialEm, int? DiaFechamento, int? DiaVencimento)`.
  - A consulta respeita o filtro `Casal`: a conta de outro casal volta `null`.
- **Endpoints** (`/api/v1/contas`, tag `Contas`):
  - `GET /` → `[ContaDto]`;
  - `POST /` (`NovaContaDto`) → `201`;
  - `PUT /{id}` → `204`;
  - `DELETE /{id}` → soft delete e `204`.
  - `ContaDto` traz `id`, `tipo`, `apelido`, `instituicao`, `titular` (`{ usuarioId, apelido }`, ou `null` para conjunta), `agenciaMascarada`, `numeroMascarado` (`"••••-7"`), `saldoInicialCentavos`, `saldoInicialEm`, `diaFechamento`, `diaVencimento` e `limiteCentavos`.
  - Agência e número em claro nunca saem da API.
  - O titular precisa ser um cônjuge do mesmo casal (via `IConsultaDeUsuarios` do Identidade), senão `400`.
  - Apelido repetido no casal (sem diferenciar maiúsculas) → `409` com `title = "Já existe uma conta com esse nome."`.
- **Regra de arquitetura nova:** `Modulo_so_depende_de_Contracts_de_outro_modulo`. Para cada par de módulos A ≠ B, os tipos de A não dependem de nenhum namespace de B fora de `B.Contracts` (NetArchTest).

- [ ] **Passo 1: Testes que falham**
  - `CalendarioDaFaturaTests` (teoria):

    | Compra | Fechamento | Vencimento | Vencimento da fatura |
    | --- | --- | --- | --- |
    | 2026-11-05 | 5 | 12 | 2026-11-12 |
    | 2026-11-06 | 5 | 12 | 2026-12-12 |
    | 2026-02-28 | 31 | 10 | 2026-03-10 |
    | 2026-03-01 | 31 | 10 | 2026-04-10 |
    | 2026-12-20 | 25 | 5 | 2027-01-05 |
    | 2026-12-26 | 25 | 5 | 2027-02-05 |
    | 2026-01-30 | 30 | 30 | 2026-02-28 |

    Também o caminho inverso `FechamentoDaFatura`.
  - `ContasTests` (integração, sessão via `ClienteLogadoAsync`):
    - `Cria_conta_conjunta_e_cartao`: conta conjunta Itaú com titular nulo, e cartão de um cônjuge com dias de fechamento e vencimento;
    - `Numero_e_agencia_so_saem_mascarados_e_ficam_cifrados_no_banco`;
    - `Cartao_sem_dias_responde_400`;
    - `Titular_de_outro_casal_responde_400`;
    - `Apelido_repetido_responde_409`;
    - `Conta_de_outro_casal_responde_404`;
    - `Apagar_conta_e_soft_delete`.
- [ ] **Passo 2: Rodar e ver falhar.** **Passo 3: Implementar.** **Passo 4: Suíte verde + contrato regerado + `npm --prefix frontend run check`.**
- [ ] **Passo 5: Commit** — `Contas: contas, cartões e calendário da fatura`

---

### Tarefa 2: Lancamentos — migração, entidades e categorias

**Arquivos:**
- Criar: `db/migrations/V0004__lancamentos.sql`
- Criar: `backend/src/Modules/Lancamentos/Dominio/Transacao.cs`, `Dominio/Categoria.cs`, `Dominio/Parcelamento.cs`
- Criar: `backend/src/Modules/Lancamentos/Infra/LancamentosDbContext.cs`, `Aplicacao/ServicoDeCategorias.cs`, `Api/EndpointsDeCategorias.cs`, `Contracts/Lancamentos.cs` (DTOs)
- Modificar: `backend/src/Modules/Lancamentos/LancamentosModule.cs` e o csproj (referência ao projeto de Contas, só para usar `Contracts`)
- Teste: `backend/tests/GestorFinanceiro.UnitTests/ParcelamentoTests.cs`, `backend/tests/GestorFinanceiro.IntegrationTests/CategoriasTests.cs`, `backend/tests/GestorFinanceiro.IntegrationTests/MigracaoDeLancamentosTests.cs`

**Interfaces:**
- **V0004:**
  - `CREATE SCHEMA lancamentos` e `GRANT USAGE`;
  - tabela `lancamentos.categorias`: `id`, `casal_id`, `nome text NOT NULL`, `tipo text CHECK (tipo IN ('DESPESA','RECEITA'))`, mais as colunas padrão; índice `ux_categorias_nome ON (casal_id, lower(nome)) WHERE NOT is_deleted`.
  - tabela `lancamentos.transacoes`:
    - `id`, `casal_id`, `conta_id uuid NOT NULL` (sem FK entre schemas);
    - `categoria_id uuid NULL REFERENCES lancamentos.categorias(id)`;
    - `data date NOT NULL`, `valor_centavos bigint NOT NULL CHECK (valor_centavos <> 0)`;
    - `natureza text CHECK (natureza IN ('DESPESA','RECEITA','TRANSFERENCIA'))`;
    - `descricao_cifrada bytea NOT NULL` e `contraparte_cifrada bytea NULL`;
    - `origem text NOT NULL CHECK (origem IN ('MANUAL','OPEN_FINANCE','WHATSAPP'))`, `id_externo text NOT NULL`;
    - `autor_usuario_id uuid NULL`, `fatura_vencimento date NULL`;
    - `grupo_id uuid NULL`, `parcela smallint NULL`, `parcelas smallint NULL`;
    - colunas padrão;
    - CHECKs:
      - sinal coerente com a natureza: `DESPESA` → `< 0`, `RECEITA` → `> 0`;
      - `TRANSFERENCIA` sem categoria;
      - `parcela`/`parcelas` ambos nulos ou `1 ≤ parcela ≤ parcelas ≤ 24`.
  - **Índices:**
    - `ux_transacoes_origem_id_externo ON (origem, id_externo) WHERE NOT is_deleted` (texto do SPEC);
    - `ix_transacoes_casal_data ON (casal_id, data) WHERE NOT is_deleted`;
    - `ix_transacoes_conta_data ON (conta_id, data) WHERE NOT is_deleted`;
    - `ix_transacoes_grupo ON (grupo_id) WHERE grupo_id IS NOT NULL AND NOT is_deleted`.
  - `audit.proteger` nas duas tabelas.
- **`Parcelamento`** (internal static): `static IReadOnlyList<Dinheiro> Dividir(Dinheiro total, int parcelas)`. A soma é exata e a sobra de centavos vai para a primeira parcela. `parcelas` fora de 1..24 lança `ArgumentOutOfRangeException`.
- **`Transacao`:** fábricas `CriarDespesa`, `CriarReceita` e `CriarPernaDeTransferencia`, que gravam o sinal e cifram a descrição e a contraparte com o AAD da restrição global; `string LerDescricao(ICofre)` e `string? LerContraparte(ICofre)`.
- **Categorias** (`/api/v1/categorias`, tag `Lancamentos`):
  - `GET /` → `[CategoriaDto(Guid Id, string Nome, string Tipo)]`; cria as categorias padrão na primeira chamada do casal, sob um `pg_advisory_xact_lock` por casal para duas chamadas simultâneas não duplicarem;
  - `POST /` → `201`; `PUT /{id}` → `204`; `DELETE /{id}` → soft delete.
  - Arquivar uma categoria com lançamentos é permitido; os lançamentos continuam com a categoria, que aparece com "(arquivada)" no extrato.

- [ ] **Passo 1: Testes que falham**
  - `ParcelamentoTests`:
    - `Dividir(10000, 3)` → `[3334, 3333, 3333]`;
    - `Dividir(100, 3)` → `[34, 33, 33]`;
    - `Dividir(1, 2)` lança `ArgumentException` (parcela de zero centavos é inválida);
    - `Dividir(x, 25)` lança.
  - `MigracaoDeLancamentosTests`:
    - `(origem, id_externo)` repetido entre ativos → `23505`; com o primeiro marcado como removido, o segundo entra;
    - os CHECKs de sinal e de transferência sem categoria dão `23514`;
    - não existe coluna `descricao` nem `contraparte` sem `_cifrada`.
  - `CategoriasTests`:
    - `Primeira_consulta_cria_as_categorias_padrao` (as 12 da decisão 5: 10 de despesa e 2 de receita);
    - `Consultas_simultaneas_nao_duplicam_padrao`;
    - `Nome_repetido_responde_409`;
    - `Categoria_de_outro_casal_responde_404`.
- [ ] **Passo 2–4** como nas outras tarefas.
- [ ] **Passo 5: Commit** — `Lancamentos: migração, transações cifradas e categorias`

---

### Tarefa 3: Saldos e totais de fatura

**Arquivos:**
- Criar: `backend/src/Modules/Lancamentos/Aplicacao/ConsultaDeSaldos.cs`, `Api/EndpointsDeSaldos.cs`
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/SaldosTests.cs` (usa inserção direta de transações pela entidade, porque os endpoints de lançamento vêm na Tarefa 4)

**Interfaces:**
- `GET /api/v1/saldos?em=YYYY-MM-DD` (padrão: hoje no fuso `America/Sao_Paulo`) → `[SaldoDaContaDto(Guid ContaId, long SaldoCentavos, FaturaDto? FaturaAberta, FaturaDto? FaturaFechada)]`.
  - `FaturaDto(DateOnly Vencimento, DateOnly Fechamento, long TotalCentavos)`: o total é a soma de despesas e estornos com aquele `fatura_vencimento`, positiva quando há gasto. Transferências não entram.
  - `SaldoCentavos` = saldo inicial + soma das transações com `data >= saldo_inicial_em` e `data <= em`.
  - A fatura aberta é a que contém `em`. A fechada é a imediatamente anterior, se existir.
- O cálculo soma no banco (`SUM`) por conta; não decifra nada.

- [ ] **Passo 1: Testes que falham**:
  - `Saldo_soma_saldo_inicial_e_transacoes_do_periodo`;
  - `Transacao_antes_do_saldo_inicial_nao_conta`;
  - `Fatura_aberta_e_fechada_do_cartao`: compras antes e depois do fechamento, um estorno e o pagamento (transferência), que não entra no total da fatura;
  - `Saldo_nao_mistura_casais`.
- [ ] **Passo 2–5.** Commit — `Saldos das contas e totais de fatura`

---

### Tarefa 4: Lançamentos — criar, listar, editar e apagar (despesa, receita, parcelas)

**Arquivos:**
- Criar: `backend/src/Modules/Lancamentos/Aplicacao/ServicoDeLancamentos.cs`, `Aplicacao/ConsultaDoExtrato.cs`, `Api/EndpointsDeLancamentos.cs`
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/LancamentosTests.cs`

**Interfaces:**
- `POST /api/v1/lancamentos`, corpo `NovoLancamentoDto(Guid ChaveIdempotente, Guid ContaId, string Natureza /* DESPESA|RECEITA */, long ValorCentavos /* > 0 */, DateOnly Data, string Descricao, string? Contraparte, Guid CategoriaId, int Parcelas /* 1 padrão */)`:
  - se a chave já existe (`MANUAL`, `id_externo = chave` ou `chave:1`), responde `200` com o lançamento existente, sem criar de novo;
  - a criação roda numa transação com `pg_advisory_xact_lock` pela chave, então duas requisições simultâneas com a mesma chave criam um só;
  - `Parcelas > 1` só em cartão (senão `400`); gera as N parcelas com `fatura_vencimento` de meses seguidos;
  - a conta precisa existir no casal (`IConsultaDeContas`), senão `404`;
  - a categoria precisa ser do casal e do tipo da natureza, senão `400`;
  - responde `201 LancamentoDto` (a primeira parcela) com `Location`.
- `GET /api/v1/lancamentos?mes=YYYY-MM&contaId=&titular=&origem=&q=` → `ExtratoDto(int Total, [LancamentoDto])`, ordenado por data decrescente.
  - `titular` aceita `conjunta`, `eu` ou `conjuge` e filtra pelo titular da conta.
  - O apelido de "Quem" vem de `IConsultaDeUsuarios.ListarDoCasalAsync` (Contracts do Identidade). O csproj de Lancamentos referencia o Identidade só para isso, e a regra de arquitetura da Tarefa 1 garante que só `Contracts` é usado.
  - `q` é a busca decifrada da decisão 5.
  - `LancamentoDto` traz `id`, `data`, `descricao`, `contraparte`, `categoria` (`{id, nome, arquivada}`), `conta` (`{id, apelido, tipo}`), `quem` (apelido do titular ou `"Conjunta"`), `origem`, `natureza`, `valorCentavos` com sinal, `parcela`, `parcelas`, `grupoId` e `faturaVencimento`.
- `PUT /api/v1/lancamentos/{id}?escopo=este|proximos` (`EditarLancamentoDto`, com os mesmos campos editáveis menos a conta e as parcelas) → `204`. O escopo `proximos` aplica a mudança às parcelas do mesmo grupo com `parcela >= atual` cuja fatura ainda não fechou.
- `DELETE /api/v1/lancamentos/{id}?escopo=este|proximos` → soft delete e `204`.
- Lançamento de outro casal → `404`. Nenhum endpoint aceita `casalId` no corpo.

- [ ] **Passo 1: Testes que falham**:
  - `Cria_despesa_e_aparece_no_extrato_do_mes`;
  - `Descricao_e_contraparte_ficam_cifradas_no_banco_e_fora_da_auditoria`;
  - `Repetir_a_mesma_chave_devolve_o_mesmo_lancamento`;
  - `Mesma_chave_em_paralelo_cria_um_so_lancamento` (provar que falha sem o lock);
  - `Parcelas_somam_o_total_e_caem_em_faturas_seguidas` (R$ 100,00 em 3 → 3334, 3333, 3333);
  - `Parcelas_em_conta_corrente_respondem_400`;
  - `Editar_proximas_nao_altera_parcela_de_fatura_fechada`;
  - `Apagar_proximas_e_soft_delete_das_parcelas_seguintes`;
  - `Busca_encontra_por_descricao_sem_acento_e_sem_caixa`;
  - `Filtro_por_titular_conjunta_e_conjuge`;
  - `Lancamento_de_outro_casal_responde_404`;
  - `Categoria_de_receita_em_despesa_responde_400`.
- [ ] **Passo 2–5.** Commit — `Lançamentos manuais: despesa, receita e parcelas`

---

### Tarefa 5: Transferências e pagamento de fatura

**Arquivos:**
- Criar: `backend/src/Modules/Lancamentos/Aplicacao/Transferencias.cs`, `Api/EndpointsDeTransferencias.cs`
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/TransferenciasTests.cs`

**Interfaces:**
- `POST /api/v1/transferencias`, corpo `NovaTransferenciaDto(Guid ChaveIdempotente, Guid ContaOrigemId, Guid ContaDestinoId, long ValorCentavos, DateOnly Data, string Descricao)` → `201 TransferenciaDto(Guid GrupoId, LancamentoDto Saida, LancamentoDto Entrada)`. Grava as duas pernas na mesma transação, com a idempotência da Tarefa 4.
- Origem igual ao destino → `400`. Conta de outro casal → `404`. Nada é gravado em nenhum dos casos.
- Pagar a fatura é uma transferência com destino a um cartão: a perna de entrada não recebe `fatura_vencimento` e não entra no total da fatura (Tarefa 3).
- `PUT` e `DELETE` em qualquer perna (pelas rotas da Tarefa 4) agem sobre o grupo inteiro: valor, data e descrição nas duas pernas.

- [ ] **Passo 1: Testes que falham**:
  - `Transferencia_cria_par_e_nao_conta_como_despesa_nem_receita`;
  - `Pagamento_de_fatura_reduz_saldo_do_cartao_sem_mudar_o_total_da_fatura`;
  - `Transferencia_invalida_nao_grava_nada`;
  - `Apagar_uma_perna_apaga_as_duas`;
  - `Editar_valor_muda_as_duas_pernas`.
- [ ] **Passo 2–5.** Commit — `Transferências entre contas e pagamento de fatura`

---

### Tarefa 6: Telas — contas e cartões, extrato e lançamento rápido

**Arquivos:**
- Criar: `frontend/src/api/contas.ts`, `frontend/src/api/lancamentos.ts`, `frontend/src/paginas/Contas.tsx`, `frontend/src/paginas/Lancamentos.tsx`, `frontend/src/paginas/lancamentos/LancamentoRapido.tsx`, `frontend/src/paginas/lancamentos/EditarLancamento.tsx`
- Modificar: `frontend/src/lib/dinheiro.ts` (`centavosDeTexto(texto: string): number | null`, que aceita "142,50", "1.234,56", "R$ 23" e "23.90" e recusa negativos e textos vazios), `frontend/src/App.tsx` (`/lancamentos` real e `/contas`), `frontend/src/layout/Layout.tsx` ("Contas e cartões" na seção Sistema), `frontend/src/styles/app.css`
- Modificar: `docs/SPEC.md` (marcar `[x]` na tarefa do módulo Lancamentos)

**Comportamento** (base: `docs/design/telas/Lancamentos.dc.html`, sem a caixa "Conciliar"):
- **`/lancamentos`:**
  - cabeçalho "Mês de AAAA · N lançamentos", com setas de mês anterior e próximo;
  - filtros em botões: Todos / apelido do cônjuge 1 / apelido do cônjuge 2 / Conjunta, e Manual (os filtros Open Finance e WhatsApp aparecem desabilitados, com `title` "Em breve");
  - campo de busca;
  - tabela com Data, Descrição (com "parcela i/N" quando houver), Categoria, Quem, Origem e Valor (`num`, sinal −/+, com `formatarReais`);
  - transferências aparecem com a categoria "Transferência" em cinza e sem cor de despesa.
- **Lançamento rápido** (coluna da direita, o único destaque da tela):
  - seletor Despesa / Receita / Transferência; valor (`inputMode="decimal"`); descrição; categoria (só do tipo certo); conta; data; "Parcelas" quando a conta é cartão; origem e destino na transferência.
  - Ao abrir o formulário gera a `chaveIdempotente` com `crypto.randomUUID()`, e só gera outra depois de salvar com sucesso. Assim um duplo clique ou um reenvio não duplica.
  - "Salvar" fica desabilitado durante o envio.
- **Editar:** clicar numa linha abre o editor (`<dialog>`). Em lançamento parcelado, pergunta "Só esta parcela" ou "Esta e as próximas"; em transferência, avisa que muda as duas contas. "Apagar" pede confirmação dentro do próprio diálogo.
- **`/contas`:**
  - lista contas e cartões com saldo (`GET /saldos`) e, nos cartões, a fatura aberta e a fechada (vencimento e total);
  - formulário para criar e editar: tipo, apelido, instituição, titular (cônjuge 1, cônjuge 2 ou Conjunta), agência e número (opcionais, `autoComplete="off"`, nunca reexibidos em claro, só mascarados), saldo inicial e data, e dias de fechamento e vencimento quando for cartão.
  - Estado vazio: "Comece pela conta conjunta Itaú", com o tipo Corrente e o titular Conjunta pré-preenchidos.
- **Regras do front valem:**
  - nada em `localStorage` ou `sessionStorage`;
  - descrições e números de conta só no estado da tela;
  - `label` em todo campo;
  - um destaque por tela;
  - abaixo de 900 px a coluna lateral vai para baixo da tabela.

- [ ] **Passo 1: Implementar.**
- [ ] **Passo 2: `npm --prefix frontend run check`** — saída 0. Testar `centavosDeTexto` com um caso por formato (se o projeto ainda não tiver runner, cobrir pela verificação no navegador e registrar).
- [ ] **Passo 3: Conferir no navegador** (Postgres descartável, API com `GF_DEV_CHAVE_EFEMERA=1`, login real via passkey com o autenticador virtual, como no plano do login). Fluxos:
  - criar a conta conjunta Itaú e um cartão;
  - lançar uma despesa e conferir no extrato e no saldo;
  - lançar uma compra parcelada em 3 no cartão e ver as parcelas nos três meses;
  - pagar a fatura por transferência;
  - editar a parcela 2 com "esta e as próximas";
  - buscar por texto sem acento;
  - duplo clique em "Salvar" → um lançamento só;
  - `localStorage` e `sessionStorage` vazios.
  - Capturas na pasta do workspace SDD, não no repositório.
- [ ] **Passo 4: Comparar com o canvas** (`Lancamentos.dc.html`): tabela, filtros, cartão lateral, contraste ≥ 4.5:1.
- [ ] **Passo 5: Commit** — `Telas de contas, extrato e lançamento rápido`

---

## Regras para o bloco 2 (orçamento)

Decididas pelo usuário agora, para o bloco 2 não reabrir a questão. Este bloco já grava o que elas precisam (`data`, `natureza`, `fatura_vencimento`):

- **Compra no cartão conta no orçamento pela data da compra** (`transacoes.data`), não pela data de vencimento da fatura. Uma compra de 28/11 numa fatura que vence em 10/12 entra no orçamento de novembro. Parcelas contam cada uma na própria `data`.
- **Pagamento de fatura e transferências entre contas nunca entram como gasto.** O orçamento soma só `natureza = 'DESPESA'` (e abate estornos `RECEITA` da mesma categoria, se o bloco 2 decidir assim). Toda linha com `natureza = 'TRANSFERENCIA'` fica fora, inclusive o pagamento da fatura.

## Fora do escopo

- Caixa "Conciliar" e as origens Open Finance e WhatsApp (blocos próprios).
- Tela de faturas e boletos e o status "fatura paga" (bloco Boletos).
- Orçamento por categoria, metas e dashboard da Visão geral (outros blocos da Fase 1).
- Importar extrato por arquivo (OFX/CSV).
