# Orçamento (Fase 1, bloco 2) — plano de implementação

> **Para agentes:** SUB-SKILL OBRIGATÓRIA: use superpowers:subagent-driven-development (recomendado) ou superpowers:executing-plans para executar este plano tarefa a tarefa. Os passos usam checkbox (`- [ ]`).

**Objetivo:** tetos por categoria e mês com usado e restante, cópia do mês anterior e divisão da renda (Investir, Teto de gastos, Dívidas, Folga), com estorno, papel de categoria e conta do tipo Investimento.

**Arquitetura:**
- O módulo **Orcamento** (schema `orcamento`) guarda só tetos e receita prevista.
- O "usado" vem, na hora, de dois contratos novos em `Lancamentos.Contracts` (`IConsultaDeCategorias` e `IConsultaDeUso`), que somam no banco sem decifrar nada.
- O Contas ganha o tipo `INVESTIMENTO`. O Lancamentos ganha a coluna `papel` nas categorias e a regra do estorno.

**Tecnologias:** .NET 10, EF Core 10 + Npgsql, xUnit + Testcontainers, React 19 + TanStack Query + openapi-fetch, Vitest.

**Spec:** `docs/superpowers/specs/2026-11-06-orcamento-design.md`. Leia antes de começar; este plano não repete as decisões dela.

## Restrições globais

- **Regras do `CLAUDE.md`, inteiras:**
  - sem DELETE físico;
  - `audit.proteger` em toda tabela nova;
  - unicidade só com índice parcial `WHERE NOT is_deleted`;
  - filtros `SoftDelete` e `Casal` sempre ligados;
  - nenhum módulo lê o schema de outro;
  - API só por DTO em `/api/v1`, com erros em Problem Details;
  - o casal vem da sessão, nunca do corpo.
- **Migrações novas:** `V0005__contas_investimento.sql`, `V0006__categorias_papel.sql` e `V0007__orcamento.sql`. Nunca editar V0001–V0004.
- **Dinheiro:** `long` em centavos no domínio, `bigint` no banco, `valorCentavos` nos DTOs. Percentual é `decimal?` com 1 casa (`MidpointRounding.AwayFromZero`); nunca `double`.
- **Limites:** `valorCentavos` de teto e de receita prevista de `0` a `100_000_000_000`. Mês `AAAA-MM` de `2020-01` até hoje + 24 meses, com hoje no fuso `America/Sao_Paulo`.
- **Textos fixos** (copiar exatamente):
  - estorno com papel: `Rendimentos e devoluções de investimento ou de dívida entram como receita comum, não como estorno.`
  - aviso de aporte: `Para guardar dinheiro, prefira Transferência para a conta de investimento. Assim o aporte aparece no saldo dela e não corre o risco de contar duas vezes.`
  - folga negativa: `Os tetos passam da receita em R$ X` (X com `formatarReais`).
  - mês sem tetos: `Este mês ainda não tem tetos.`
  - categoria arquivada: `Categoria arquivada: escolha outra.`
- **Mudou o contrato:** `dotnet build backend/src/Host/GestorFinanceiro.Api && npm --prefix frontend run gen:api` no mesmo commit.
- **Antes de cada commit:**
  - `dotnet test backend/GestorFinanceiro.slnx` verde;
  - 0 avisos em `dotnet build backend/GestorFinanceiro.slnx --no-incremental`;
  - `npm --prefix frontend run check` verde (tipos, lint, testes e build).
- **Commits:** em pt-BR, terminando com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Branch `fase-1/orcamento`, que já existe e tem o commit da spec.

## Foco da revisão

São os casos que a spec implica, mas que nenhum teste listado nela cobre. Cada um ganhou um teste na tarefa responsável:

1. **Copiar não ressuscita categoria arquivada ou apagada.** Um teto no mês de origem cuja categoria hoje está arquivada ou apagada não é copiado, porque seria um "teto novo em categoria arquivada". Teste `Copiar_pula_categoria_arquivada_ou_apagada` (Tarefa 5).
2. **Editar um lançamento até virar estorno passa pela mesma regra.** Um `PUT` que muda uma despesa para `RECEITA` mantendo a categoria Investimento responde `400` com o texto fixo. Teste `Editar_para_receita_em_categoria_com_papel_responde_400` (Tarefa 2).
3. **A categoria com papel foi apagada.** A fatia Investir continua com os aportes (teto nulo) e a tela não quebra. Teste `Fatia_sem_categoria_de_papel_mostra_so_os_aportes` (Tarefa 5).
4. **Mês 2020-01:** a sugestão de receita olha 2019-12 sem responder `400`. Teste `Sugestao_de_2020_01_olha_2019_12_sem_erro` (Tarefa 5).
5. **Lançamento apagado não conta no usado.** O soft delete e o filtro de casal valem também nos `SUM`. Teste `Lancamento_apagado_e_de_outro_casal_nao_entram_no_uso` (Tarefa 3).

---

### Tarefa 1: Contas — tipo Investimento

**Arquivos:**
- Criar: `db/migrations/V0005__contas_investimento.sql`
- Modificar: `backend/src/Modules/Contas/Dominio/Conta.cs` (enum `TipoDeConta`), `Aplicacao/ServicoDeContas.cs` (validação e mensagem), `Contracts/Contas.cs` e `Contracts/IConsultaDeContas.cs` (comentários de `Tipo`), `frontend/src/api/contas.ts` (`TIPOS_DE_CONTA`)
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/ContasTests.cs`

**Interfaces:**
- Produz: o tipo `"INVESTIMENTO"` em `ContaResumo.Tipo` e `ContaDto.Tipo`. A mensagem de tipo inválido passa a ser `Tipo inválido: use CORRENTE, POUPANCA, INVESTIMENTO, CARTAO ou DINHEIRO.` No front: `{ valor: "INVESTIMENTO", rotulo: "Investimento" }` entre Poupança e Cartão.

- [ ] **Passo 1: Teste que falha**: `Conta_do_tipo_investimento_e_aceita`.
  - O `POST /api/v1/contas` com `tipo = "INVESTIMENTO"` responde `201`.
  - O `GET` devolve `tipo == "INVESTIMENTO"`.
  - Um `INSERT` direto com `tipo = 'OUTRO'` (superusuário) dá `23514`.
- [ ] **Passo 2: Rodar e ver falhar** (`400` no tipo).
- [ ] **Passo 3: Implementar.**
  - V0005: `ALTER TABLE contas.contas DROP CONSTRAINT contas_tipo_check;` e `ADD CONSTRAINT contas_tipo_check CHECK (tipo IN ('CORRENTE','POUPANCA','INVESTIMENTO','CARTAO','DINHEIRO'))`.
  - Domínio: valor `Investimento` no enum.
- [ ] **Passo 4: Suíte verde, contrato regerado e `npm --prefix frontend run check`.**
- [ ] **Passo 5: Commit** — `Contas: tipo Investimento`

---

### Tarefa 2: Lancamentos — papel das categorias e estorno

**Arquivos:**
- Criar: `db/migrations/V0006__categorias_papel.sql`
- Modificar: `backend/src/Modules/Lancamentos/Dominio/Categoria.cs`, `Aplicacao/ServicoDeCategorias.cs`, `Aplicacao/ServicoDeLancamentos.cs` (`ValidarCategoriaAsync`), `Infra/LancamentosDbContext.cs`, `Contracts/Lancamentos.cs`
- Criar: `backend/src/Modules/Lancamentos/Contracts/PapelDeCategoria.cs`
- Teste: `CategoriasTests.cs`, `LancamentosTests.cs`, `MigracaoDeLancamentosTests.cs` (em `backend/tests/GestorFinanceiro.IntegrationTests/`)

**Interfaces:**
- **Produz:**
  - `public static class PapelDeCategoria { public const string Investimento = "INVESTIMENTO"; public const string Dividas = "DIVIDAS"; }` em `Contracts`.
  - `Categoria.Papel` (`string?`, só leitura fora da fábrica) e `Categoria.Criar(Guid casalId, string nome, TipoDeCategoria tipo, string? papel = null)`.
  - `CategoriaDto` ganha `string? Papel` como último parâmetro, posicional.
  - `internal const string ServicoDeLancamentos.TituloEstornoComPapel` com o texto fixo das Restrições globais.
- **Regra nova em `ValidarCategoriaAsync`** (vale para criar e editar):
  - `DESPESA` exige categoria `Despesa`;
  - `RECEITA` aceita categoria `Receita`, ou `Despesa` com `Papel == null` (estorno);
  - `RECEITA` em categoria com papel → `Invalido("categoriaId", TituloEstornoComPapel)`.

- [ ] **Passo 1: Testes que falham.**
  - `Categorias_padrao_nascem_com_papel`: no `GET /categorias`, "Investimento" tem `papel == "INVESTIMENTO"`, "Dívidas e financiamentos" tem `"DIVIDAS"`, e as outras 10 têm `null`.
  - `Renomear_mantem_papel`: `PUT` em "Investimento" com nome "Investimentos" → papel continua `"INVESTIMENTO"`. Mandar `papel` no corpo é ignorado, porque o DTO de entrada não tem o campo.
  - `Migracao_preenche_papel_pelo_nome` (em `MigracaoDeLancamentosTests`): aplicar até V0005, inserir as categorias com os nomes padrão, aplicar V0006 e conferir os papéis. Se o `MigradorSql` não aplicar só até uma versão, inserir as categorias no banco-modelo sem papel e rodar o `UPDATE` da V0006 de novo: ele precisa ser idempotente.
  - `Papel_unico_por_casal`: um segundo `INSERT` com o mesmo papel ativo no casal → `23505`. Com o primeiro `is_deleted = true`, o segundo entra.
  - `Estorno_receita_em_categoria_de_despesa_sem_papel_e_aceito`: `POST /lancamentos` `RECEITA` em "Mercado" → `201` com `valorCentavos > 0`.
  - `Receita_em_categoria_com_papel_responde_400`: `RECEITA` em "Investimento" e em "Dívidas e financiamentos" → `400`, e `errors.categoriaId[0]` é o texto fixo.
  - `Editar_para_receita_em_categoria_com_papel_responde_400` (Foco 2): uma despesa em "Investimento" com `PUT` `natureza = "RECEITA"` → `400`, mesmo texto, e nada muda no banco.
  - `Categoria_de_receita_em_despesa_responde_400`: já existe e continua passando.
- [ ] **Passo 2: Rodar e ver falhar.**
- [ ] **Passo 3: Implementar.**
  - V0006:
    - `ADD COLUMN papel text NULL CHECK (papel IN ('INVESTIMENTO','DIVIDAS'))`;
    - `UPDATE ... SET papel = 'INVESTIMENTO' WHERE tipo = 'DESPESA' AND nome = 'Investimento' AND papel IS NULL AND NOT is_deleted` (e o equivalente para `'Dívidas e financiamentos'` → `'DIVIDAS'`);
    - `CREATE UNIQUE INDEX ux_categorias_papel ...` como na spec.
  - `GarantirPadraoAsync` cria as duas categorias com papel.
- [ ] **Passo 4: Suíte verde, contrato regerado e check do front.**
- [ ] **Passo 5: Commit** — `Lancamentos: papel das categorias e estorno`

---

### Tarefa 3: Lancamentos — contratos de uso para o orçamento

**Arquivos:**
- Criar: `backend/src/Modules/Lancamentos/Contracts/Orcamento.cs` (interfaces e records), `Infra/ConsultaDeUso.cs`, `Infra/ConsultaDeCategorias.cs`
- Modificar: `LancamentosModule.cs` (registrar `IConsultaDeUso` e `IConsultaDeCategorias` como scoped)
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/UsoDoOrcamentoTests.cs` (chama os serviços do container da `ApiDeTeste` num escopo com a sessão do casal; se a `ApiDeTeste` não expuser isso, usar um endpoint de teste só no projeto de testes, como no `ApiDeTesteTests`)

**Interfaces:**
- **Produz** (em `GestorFinanceiro.Modules.Lancamentos.Contracts`):
  - `public sealed record CategoriaDeDespesa(Guid Id, string Nome, string? Papel, bool Arquivada);`
  - `public interface IConsultaDeCategorias { Task<IReadOnlyList<CategoriaDeDespesa>> ListarDeDespesaAsync(CancellationToken ct = default); }`. Inclui as arquivadas, exclui as apagadas e cria as padrão do casal se ele ainda não tiver nenhuma (mesmo `GarantirPadraoAsync`).
  - `public sealed record UsoDaCategoria(Guid CategoriaId, long UsadoCentavos);`
  - `public interface IConsultaDeUso`:
    - `Task<IReadOnlyList<UsoDaCategoria>> UsoPorCategoriaAsync(DateOnly mes, CancellationToken ct = default);`
    - `Task<long> AportesAsync(DateOnly mes, CancellationToken ct = default);`
    - `Task<long> ReceitasAsync(DateOnly mes, CancellationToken ct = default);`
  - `mes` é sempre o dia 1. O período é `[mes, mes.AddMonths(1))` por `transacoes.data`.
- **Cálculo** (tudo `SUM` no banco):
  - **uso:** `−SUM(valor_centavos)` das transações cuja categoria é `DESPESA`, agrupado por categoria. A despesa entra negativa e o estorno positivo, então o estorno abate sozinho.
  - **receitas:** `SUM(valor_centavos)` de `natureza = RECEITA` com categoria `RECEITA`.
  - **aportes:** os ids de conta `POUPANCA`/`INVESTIMENTO` vêm de `IConsultaDeContas.ListarAsync` (inclui arquivadas). Soma das pernas com `natureza = TRANSFERENCIA`, `valor > 0` e `ContaId` nesse conjunto, desde que exista no mesmo `GrupoId` uma perna com `valor < 0` e `ContaId` **fora** do conjunto.
- **Consome:** `IConsultaDeContas` (Contas.Contracts) e `PapelDeCategoria` (Tarefa 2).

- [ ] **Passo 1: Testes que falham.** Relógio fixo em 05/11/2026; valores criados pela API.
  - `UsoPorCategoria_subtrai_estorno_e_conta_parcela_pela_data`:
    - em "Mercado": despesa de 10.000 em 03/11 e estorno de 2.500 em 04/11;
    - no cartão: compra de 30.000 em 3x em 05/11;
    - novembro: Mercado = 7.500, e a categoria da compra = 10.000;
    - dezembro: a categoria da compra = 10.000, e Mercado não aparece ou aparece com 0.
  - `Transferencia_e_pagamento_de_fatura_nao_entram_no_uso`: uma transferência entre correntes e um pagamento de fatura não aparecem em nenhuma categoria.
  - `Aporte_corrente_para_poupanca_conta`: 50.000 → `AportesAsync(nov) == 50_000`.
  - `Aporte_corrente_para_investimento_conta`: 20.000 → `20_000`.
  - `Poupanca_para_investimento_e_movimentacao_interna`: 30.000 da poupança para o investimento → `0`.
  - `Resgate_nao_conta_nem_abate`: um aporte de 40.000 e um resgate de 15.000 (investimento → corrente) → `40_000`.
  - `Receitas_do_mes_nao_incluem_estorno`: salário de 900.000 e estorno de 2.500 → `ReceitasAsync == 900_000`.
  - `Lancamento_apagado_e_de_outro_casal_nao_entram_no_uso` (Foco 5): uma despesa apagada pela API e uma transação de outro casal inserida por SQL não mudam o uso nem os aportes.
  - `ListarDeDespesa_traz_papel_e_arquivadas`: 10 categorias de despesa com os papéis da Tarefa 2; uma arquivada vem com `Arquivada = true`.
- [ ] **Passo 2: Rodar e ver falhar.** **Passo 3: Implementar.** **Passo 4: Suíte verde.**
- [ ] **Passo 5: Commit** — `Lancamentos: contratos de uso para o orçamento`

---

### Tarefa 4: Orcamento — mês e cálculo (domínio puro)

**Arquivos:**
- Criar: `backend/src/Modules/Orcamento/Dominio/MesDoOrcamento.cs`, `Dominio/CalculoDoOrcamento.cs`, `Contracts/Orcamento.cs` (DTOs), `Aplicacao/Brasilia.cs` (cópia do helper do Lancamentos, `internal`)
- Teste: `backend/tests/GestorFinanceiro.UnitTests/MesDoOrcamentoTests.cs`, `CalculoDoOrcamentoTests.cs` (o projeto de unidade enxerga os `internal` via `InternalsVisibleTo`; se o Orcamento ainda não tiver isso, acrescente como nos outros módulos)

**Interfaces:**
- **`MesDoOrcamento`:**
  - `internal readonly record struct MesDoOrcamento(DateOnly Inicio)`;
  - `static bool TryLer(string? texto, DateOnly hoje, out MesDoOrcamento mes, out string? erro)`:
    - `null` → o mês de `hoje`;
    - fora de `2020-01` até `hoje` + 24 meses → erro `Mês inválido: use AAAA-MM entre 2020-01 e {limite:yyyy-MM}.`;
  - `MesDoOrcamento Anterior()`, sem validar o limite;
  - `string Texto` → `"yyyy-MM"`.
- **DTOs** (em `GestorFinanceiro.Modules.Orcamento.Contracts`, todos `public sealed record`):
  - `FatiaDto(long? TetoCentavos, long UsadoCentavos, decimal? Percentual)`
  - `FatiaDeInvestirDto(long? TetoCentavos, long UsadoCentavos, long AportesCentavos, decimal? Percentual)`
  - `FatiasDto(FatiaDeInvestirDto Investir, FatiaDto Dividas, FatiaDto TetoDeGastos)`
  - `TetoDaCategoriaDto(Guid CategoriaId, string Nome, string? Papel, bool Arquivada, long? TetoCentavos, long UsadoCentavos, long? RestanteCentavos)`
  - `OrcamentoDoMesDto(string Mes, long? ReceitaPrevistaCentavos, long ReceitaSugeridaCentavos, FatiasDto Fatias, long? FolgaCentavos, IReadOnlyList<TetoDaCategoriaDto> Categorias, string? MesParaCopiar)`
  - `ValorDoMesDto(long ValorCentavos)` (corpo dos dois `PUT`)
  - `CopiaDeTetosDto(int Copiados, string? Origem)`
- **`CalculoDoOrcamento`:**
  - `internal static OrcamentoDoMesDto Calcular(MesDoOrcamento mes, long? receitaPrevista, long receitaSugerida, IReadOnlyList<CategoriaDeDespesa> categorias, IReadOnlyDictionary<Guid, long> tetos, IReadOnlyDictionary<Guid, long> uso, long aportes, MesDoOrcamento? mesParaCopiar)`;
  - linhas: categorias não arquivadas, mais as arquivadas com teto ou uso ≠ 0. Ordem: com papel primeiro (Investimento, Dívidas), depois as outras por nome;
  - percentual = `round(teto * 100 / receita, 1)`; nulo se não há receita, se a receita é 0 ou se não há teto;
  - fatia sem categoria do papel (Foco 3): teto nulo, usado = só os aportes em Investir ou 0 em Dívidas.

- [ ] **Passo 1: Testes que falham.**
  - `MesDoOrcamentoTests` com `hoje = 2026-11-05`:
    - `"2019-12"` ✗, `"2020-01"` ✓, `"2028-11"` ✓, `"2028-12"` ✗;
    - `"2026-13"` e `"nov"` ✗;
    - `null` → `2026-11-01`;
    - `Anterior()` de `2020-01` → `2019-12-01`.
  - `CalculoDoOrcamentoTests`, com cenário-base de receita 1.850.000, Investir 680.000, Dívidas 390.000, Mercado 160.000, Lazer 50.000 e Saúde sem teto:
    - `Fatias_percentuais_e_folga`: Teto de gastos = 210.000; folga = 570.000; percentuais 36,8 / 21,1 / 11,4;
    - `Teto_zero_e_diferente_de_sem_teto`: Lazer com teto 0 → `TetoCentavos == 0` e `Restante == -usado`; Saúde → `TetoCentavos == null` e `Restante == null`;
    - `Teto_de_gastos_soma_so_categorias_sem_papel`: uso de 100.000 em Investimento e de 50.000 em Dívidas não mudam `TetoDeGastos.UsadoCentavos`;
    - `Folga_negativa`: receita 500.000 → `FolgaCentavos == -780_000` (500.000 − 1.280.000);
    - `Sem_receita_nao_ha_folga_nem_percentual`;
    - `Usado_negativo_aparece_como_esta`: uso −2.500 → restante = teto + 2.500;
    - `Arquivada_so_aparece_com_teto_ou_uso`;
    - `Fatia_sem_categoria_de_papel_mostra_so_os_aportes`, sem a categoria Investimento na lista.
- [ ] **Passo 2: Rodar e ver falhar.** **Passo 3: Implementar.** **Passo 4: Suíte verde.**
- [ ] **Passo 5: Commit** — `Orcamento: mês e cálculo das fatias`

---

### Tarefa 5: Orcamento — migração, persistência e API

**Arquivos:**
- Criar:
  - `db/migrations/V0007__orcamento.sql`;
  - `backend/src/Modules/Orcamento/Dominio/Teto.cs`, `Dominio/ReceitaPrevista.cs`;
  - `Infra/OrcamentoDbContext.cs` (herda `ModuloDbContext`; tabelas `tetos` e `receitas_previstas` em `orcamento`);
  - `Aplicacao/ServicoDeOrcamento.cs`, `Api/EndpointsDeOrcamento.cs`.
- Modificar: `OrcamentoModule.cs` (DbContext com os dois interceptors, serviço e `MapOrcamento`), e o Host, se o `MapOrcamento` ainda não estiver encadeado.
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/OrcamentoTests.cs`

**Interfaces:**
- **Consome:** `IConsultaDeCategorias` e `IConsultaDeUso` (Tarefa 3), `MesDoOrcamento`, `CalculoDoOrcamento` e os DTOs (Tarefa 4).
- **Entidades:**
  - `Teto : Entidade, ICasalScoped` (`Mes`, `CategoriaId`, `ValorCentavos`, `Definir(long)`);
  - `ReceitaPrevista : Entidade, ICasalScoped` (`Mes`, `ValorCentavos`, `Definir(long)`).
- **Rotas** (operationIds `ObterOrcamento`, `DefinirTeto`, `RemoverTeto`, `DefinirReceitaPrevista`, `RemoverReceitaPrevista`, `CopiarTetos`): as da tabela de API da spec, com os status de lá. Respostas com `Cache-Control: no-store`.
- **Erros e lock:**
  - mês inválido → `ValidationProblem` em `mes`; valor fora do limite → `ValidationProblem` em `valorCentavos` (`O valor deve ficar entre 0 e 100.000.000.000 centavos.`);
  - categoria de receita → `ValidationProblem` em `categoriaId` (`Escolha uma categoria de despesa.`);
  - categoria inexistente ou de outro casal → `404`;
  - teto novo em categoria arquivada → `Problem(title: "Categoria arquivada: escolha outra.", 400)`;
  - lock: `pg_advisory_xact_lock(hashtextextended('orcamento:' || casal || ':' || mes, 0))`.
- **`mesParaCopiar`:** o maior `mes` de `tetos` ativos com `mes < M`.
- **Cópia:** para cada teto da origem cuja categoria está em `ListarDeDespesaAsync` sem estar arquivada (Foco 1) e sem linha ativa em M, cria um teto em M.

- [ ] **Passo 1: Testes que falham.** Relógio fixo em 05/11/2026; categorias pelo `GET /categorias`.
  - **CRUD:**
    - `Define_atualiza_e_remove_teto`: `PUT` → aparece no `GET`; `PUT` de novo → atualiza, ainda com uma linha ativa no banco; `DELETE` → `204`, `TetoCentavos == null` e a linha com `is_deleted`; `DELETE` de novo → `404`;
    - `Teto_zero_e_diferente_de_sem_teto`;
    - `Receita_prevista_e_sugestao`: com salário de 900.000 lançado em 10/2026, o `GET` de 2026-11 tem `ReceitaSugeridaCentavos == 900_000`; depois de `PUT` 1.850.000, `ReceitaPrevistaCentavos == 1_850_000`; `DELETE` → nula;
    - `Mes_passado_pode_ser_editado`: `PUT` em 2025-03 → `204`.
  - **Cópia:**
    - `Copiar_preenche_so_categorias_sem_linha_e_respeita_teto_zero`: em 09/2026 Mercado 160.000 e Lazer 50.000; em 11/2026 Lazer 0 → cópia em 11 → `Copiados == 1`, `Origem == "2026-09"`, Mercado 160.000 e Lazer continua 0;
    - `Copiar_usa_o_ultimo_mes_com_teto_mesmo_pulando_meses`: tetos só em 08/2026 → `mesParaCopiar` de 11 é `"2026-08"`;
    - `Copiar_de_novo_nao_muda_nada`: `Copiados == 0`;
    - `Copiar_pula_categoria_arquivada_ou_apagada` (Foco 1);
    - sem origem → `200 { copiados: 0, origem: null }`.
  - **Fatias:** `Aporte_em_Investimento_e_parcela_em_Dividas_nao_alteram_o_Teto_de_gastos`:
    - uma transferência corrente → investimento de 50.000, uma despesa em Investimento de 10.000 e uma compra de 30.000 em 3x em Dívidas;
    - resultado: `TetoDeGastos.UsadoCentavos` é igual ao de antes, `Investir.UsadoCentavos == 60_000`, `Investir.AportesCentavos == 50_000` e `Dividas.UsadoCentavos == 10_000`.
  - **Limites:**
    - `Limites_do_valor`: 0 e 100.000.000.000 → `204`; −1 e 100.000.000.001 → `400` com `errors.valorCentavos`, no teto e na receita;
    - `Limites_do_mes`: 2019-12 → `400` com `errors.mes`; 2020-01 → `200`; 2028-11 → `200`; 2028-12 → `400`, no `GET` e no `PUT`;
    - `Sugestao_de_2020_01_olha_2019_12_sem_erro` (Foco 4).
  - **Categorias:** `Categoria_de_outro_casal_responde_404`; `Categoria_de_receita_responde_400`; `Arquivada_aceita_atualizar_e_remover_mas_nao_teto_novo`.
  - **Concorrência:** `PUT_simultaneo_no_mesmo_teto_grava_uma_linha`: 10 `PUT` em paralelo no mesmo mês e categoria → uma linha ativa. Provar que falha sem o lock: comentar o lock, ver `23505` ou duas linhas e voltar o lock.
  - **Isolamento:** `Orcamento_nao_mostra_uso_de_outro_casal`.
  - **Arquitetura:** colocar um uso temporário de um tipo `internal` do Lancamentos no Orcamento (por exemplo, `typeof(Lancamentos.Infra.LancamentosDbContext)` via reflexão por nome), ver `Modulo_so_depende_de_Contracts_de_outro_modulo` falhar para o par (Orcamento, Lancamentos) e remover. Registrar a saída no commit.
- [ ] **Passo 2: Rodar e ver falhar.**
- [ ] **Passo 3: Implementar.** V0007 conforme a spec: schema, `GRANT USAGE`, duas tabelas com colunas padrão, CHECKs, índices parciais e `audit.proteger`.
- [ ] **Passo 4: Suíte verde, contrato regerado e check do front.**
- [ ] **Passo 5: Commit** — `Orcamento: tetos, receita prevista e cópia`

---

### Tarefa 6: Tela de orçamento

**Arquivos:**
- Criar:
  - `frontend/src/api/orcamento.ts`;
  - `frontend/src/lib/orcamento.ts` e `frontend/src/lib/orcamento.test.ts`;
  - `frontend/src/paginas/Orcamento.tsx`, `frontend/src/paginas/orcamento/DivisaoDaRenda.tsx`, `frontend/src/paginas/orcamento/LinhaDoTeto.tsx`.
- Modificar: `frontend/src/App.tsx` (rota `orcamento`), `frontend/src/styles/app.css`
- Base visual: `docs/design/telas/Orcamento.dc.html` (sem o bloco Dívidas)

**Interfaces:**
- **Consome:** as rotas da Tarefa 5 pelo cliente gerado; `chamarFormulario` e `n` de `api/formulario.ts`; `centavosDeTexto`, `formatarReais` e `textoDeCentavos`.
- **`api/orcamento.ts`:**
  - `useOrcamento(mes: string)` com queryKey `["orcamento", mes]`;
  - `definirTeto(mes, categoriaId, centavos)`, `removerTeto(mes, categoriaId)`, `definirReceita(mes, centavos)`, `removerReceita(mes)` e `copiarTetos(mes)`;
  - toda mutação invalida `["orcamento"]`.
- **`lib/orcamento.ts`:**
  - `type Segmento = { fatia: "investir" | "gastos" | "dividas" | "folga"; centavos: number; percentual: number }`;
  - `segmentosDaBarra(o: OrcamentoDoMes): Segmento[]`: vazio sem receita prevista ou com receita 0; sem o segmento `folga` quando a folga ≤ 0; fatias sem teto entram com 0 e não geram segmento;
  - `lerTeto(texto: string): { tipo: "vazio" } | { tipo: "valor"; centavos: number } | { tipo: "invalido"; mensagem: string }`, com limite 100.000.000.000 e a mensagem `Use um valor entre R$ 0,00 e R$ 1.000.000.000,00.`.
- **Comportamento:**
  - conforme a seção Telas da spec, incluindo Esc, `aria-live` por linha e erro na linha que mantém o valor;
  - `LinhaDoTeto` guarda o texto digitado em `useState` e só volta ao valor do servidor depois de salvar com sucesso, ou com Esc;
  - os textos fixos vêm das Restrições globais.

- [ ] **Passo 1: Testes que falham (Vitest)** em `lib/orcamento.test.ts`:
  - `segmentosDaBarra`: o cenário-base da Tarefa 4 dá quatro segmentos com percentuais 36,8 / 11,4 / 21,1 / 30,8; folga negativa dá três segmentos; sem receita dá `[]`;
  - `lerTeto`: `""` e `"   "` → vazio; `"0"` → 0; `"1.600"` → 160.000; `"1.000.000.000,00"` → 100.000.000.000; `"1.000.000.000,01"` e `"abc"` → inválido.
- [ ] **Passo 2: `npm --prefix frontend test` falha.** **Passo 3: Implementar** a lib e a tela.
- [ ] **Passo 4: `npm --prefix frontend run check` verde.**
- [ ] **Passo 5: Commit** — `Tela de orçamento`

---

### Tarefa 7: Estorno, aviso de aporte e tipo Investimento nas telas

**Arquivos:**
- Modificar: `frontend/src/paginas/lancamentos/LancamentoRapido.tsx`, `frontend/src/paginas/lancamentos/EditarLancamento.tsx`, `frontend/src/paginas/Contas.tsx` (só se o select não vier de `TIPOS_DE_CONTA`)
- Criar: `frontend/src/lib/lancamentos.ts` e `frontend/src/lib/lancamentos.test.ts`

**Interfaces:**
- **Consome:** `CategoriaDto.papel` (Tarefa 2) e o tipo `INVESTIMENTO` (Tarefa 1).
- **Produz em `lib/lancamentos.ts`:**
  - `categoriasDoTipo(tipo: "DESPESA" | "RECEITA" | "ESTORNO", categorias: Categoria[]): Categoria[]`: só ativas; `ESTORNO` = despesa sem papel;
  - `avisaAporteEmDobro(tipo, categoria?: Categoria, conta?: Conta): boolean`: `true` só para `DESPESA` em categoria com papel `INVESTIMENTO` numa conta que não seja `POUPANCA` nem `INVESTIMENTO`;
  - `ehEstorno(l: Lancamento, categorias: Categoria[]): boolean`: `RECEITA` cuja categoria é de despesa.
- **Comportamento:**
  - o seletor tem Despesa / Receita / Estorno / Transferência, e Estorno envia `natureza: "RECEITA"`;
  - o aviso aparece abaixo da categoria, como `<p>` normal, sem `role="alert"`, porque não bloqueia;
  - no editor, um estorno mostra o tipo "Estorno" e só oferece categorias de despesa sem papel.

- [ ] **Passo 1: Testes que falham (Vitest)**:
  - `categoriasDoTipo("ESTORNO")` exclui Investimento, Dívidas e as arquivadas;
  - `avisaAporteEmDobro` é `true` para despesa em Investimento na corrente, e `false` na poupança, no investimento, em receita e em outra categoria;
  - `ehEstorno` é `true` para receita em Mercado e `false` para receita em Salário.
- [ ] **Passo 2: Falha.** **Passo 3: Implementar.** **Passo 4: Check verde.**
- [ ] **Passo 5: Commit** — `Telas: estorno, aviso de aporte e conta de investimento`

---

### Tarefa 8: Mês fechado à mão, SPEC e PR

**Arquivos:**
- Modificar: `docs/SPEC.md` (marcar o orçamento nas próximas tarefas da Fase 1)
- Capturas e a conta à parte ficam no scratchpad da sessão, não no repositório.

- [ ] **Passo 1: Ambiente descartável**, como nos blocos anteriores:
  - Postgres próprio numa porta alta, sem tocar em `infra/data`;
  - API com `GF_DEV_CHAVE_EFEMERA=1`;
  - Vite;
  - login real por passkey com o autenticador virtual do Playwright.
- [ ] **Passo 2: Fechar novembro de 2026 à mão.** Contas: corrente conjunta, cartão, poupança e investimento. No mês:
  - receita prevista 1.850.000 (antes, um salário de 900.000 em outubro, para a sugestão);
  - tetos em Mercado, Lazer (0), Investimento e Dívidas;
  - duas despesas em Mercado e um estorno de Mercado;
  - uma compra de 3x no cartão;
  - um aporte por transferência corrente → poupança, um movimento poupança → investimento e um resgate investimento → corrente;
  - o pagamento da fatura.

  Fazer a conta à parte, numa tabela no PR: usado e restante por categoria e as quatro fatias. Conferir cada número da tela.
- [ ] **Passo 3: Fluxos da tela:**
  - larguras 1440, 900 e 390;
  - Esc desfaz;
  - erro na linha mantém o valor (com `page.route` devolvendo 500 no `PUT`);
  - aviso "Este mês ainda não tem tetos." e cópia em dezembro;
  - o estorno não oferece Investimento nem Dívidas;
  - o aviso de aporte aparece na despesa em Investimento na corrente;
  - alerta de folga negativa com receita 500.000;
  - `localStorage` e `sessionStorage` vazios;
  - contraste ≥ 4.5:1 das cores novas.
- [ ] **Passo 4: Desmontar o ambiente**, matando os processos pela porta (`ss -ltnp`), nunca com `pkill -f` em padrão que case com o próprio shell. Marcar o SPEC.
- [ ] **Passo 5: Verificação final:** suíte do backend, build sem avisos, check do front e teste de arquitetura.
- [ ] **Passo 6: Commit, push e PR** com a tabela do mês fechado, as decisões e o que ficou fora (bloco Dívidas, metas, dashboard).
