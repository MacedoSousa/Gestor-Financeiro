# Dashboard da Visão geral (Fase 1, bloco 4) — plano de implementação

> **Para agentes:** SUB-SKILL OBRIGATÓRIA: use superpowers:subagent-driven-development (recomendado) ou superpowers:executing-plans para executar este plano tarefa a tarefa. Os passos usam checkbox (`- [ ]`).

**Objetivo:** a Visão geral com dados reais — 4 cartões, aviso de fatura, patrimônio em 12 meses, metas, orçamento por categoria, faturas a vencer e últimos lançamentos — por um módulo `Painel` que só lê contratos.

**Arquitetura:**
- **Lancamentos** ganha três consultas para outros módulos: faturas por cartão (restante e atraso), patrimônio por mês e últimos lançamentos.
- **Orcamento** e **Investimentos** expõem o que `GET /orcamento` e `GET /metas` já montam.
- O módulo novo **Painel** (sem schema) combina tudo em `GET /api/v1/visao-geral`, com seções independentes.
- O front só formata.

**Tecnologias:** .NET 10, EF Core 10 + Npgsql, xUnit + Testcontainers + NetArchTest, React 19 + TanStack Query + openapi-fetch, Vitest.

**Spec:** `docs/superpowers/specs/2026-11-09-dashboard-design.md`. Leia antes; este plano não repete as regras dela, só as decisões abaixo.

## Decisões do plano sobre a spec

- **D1 — ordem dos últimos lançamentos:** `data` desc e depois `Id` desc. O `Id` é UUID v7 (ordem de criação); `criado_em` não está mapeado na entidade. Lançamentos com `data > hoje` (parcelas futuras) **não** entram.
- **D2 — restante exatamente zero:** situação `ESGOTADO` ("Teto atingido"), sem valor por dia. `PASSOU` só com restante < 0. Com teto de gastos 0, o percentual é nulo.
- **D3 — valor por dia** vem do servidor já truncado em reais: `PorDiaCentavos = floor(restante / dias / 100) × 100`. Exemplo: 210.000 em 26 dias → 8.000.
- **D4 — percentuais:** `floor` em inteiro. Teto 0 com usado > 0 → 100 e `PASSOU`; teto 0 com usado 0 → 0. A variação do patrimônio usa `Math.Round(x, 1, MidpointRounding.AwayFromZero)`.
- **D5 — lançamentos anteriores ao `saldo_inicial_em`** ficam fora do devido da fatura, como no saldo (já estão no saldo inicial).
- **D6 — patrimônio de hoje** = último ponto de `PatrimonioPorMesAsync` (o de M é calculado em hoje). Variação = esse ponto − o ponto de M−1. Uma fonte só.
- **D7 — id de correlação** = `HttpContext.TraceIdentifier`, passado do endpoint para a montagem.
- **D8 — nomes no OpenAPI:** `SecaoDto<T>` genérico. O front lê os tipos pelo caminho (`components["schemas"]["VisaoGeralDto"]["metas"]`), nunca pelo nome gerado do genérico.

## Restrições globais

- **Regras do `CLAUDE.md`, inteiras:**
  - filtros `SoftDelete` e `Casal` sempre ligados;
  - nenhum módulo lê o schema de outro; só `Contracts` é público;
  - API só por DTO em `/api/v1` com Problem Details;
  - casal só da sessão;
  - nada pessoal em log: o log de seção tem **só** nome da seção, tipo da exceção e id de correlação.
- **Sem migração** neste bloco.
- **Painel:**
  - sem `DbContext`;
  - sem referência a `Microsoft.EntityFrameworkCore*` ou `Npgsql*`;
  - referencia `SharedKernel` e os projetos Identidade, Contas, Lancamentos, Orcamento e Investimentos, usando só os `Contracts` deles.
- **Hoje** = `Brasilia.Hoje(TimeProvider)`. M = mês de hoje.
- **Textos fixos** (copiar exatamente):
  - `Não foi possível carregar` (erro de seção)
  - `Compras no cartão {apelido} a partir de hoje só vencem em {dd/MM} — a fatura fechou em {dd/MM}`
  - `Não inclui dívidas fora do cartão (empréstimo, consórcio)`
  - `Sem teto de gastos neste mês`
  - `Teto atingido`
  - `Passou {R$} do teto`
  - `Nenhuma meta`
  - `Nenhuma meta ainda`
  - `Alcançada`
  - `Não chega em 100 anos com o aporte planejado`
  - `Sem tetos neste mês`
  - `Nenhuma fatura a vencer`
  - `inclui {R$} em atraso`
  - `Ver valores por mês`
  - `Tentar de novo`
- **Contrato:** mudou → `dotnet build backend/src/Host/GestorFinanceiro.Api && npm --prefix frontend run gen:api` no mesmo commit.
- **Antes de cada commit:**
  - `dotnet test backend/GestorFinanceiro.slnx` verde;
  - 0 avisos em `dotnet build backend/GestorFinanceiro.slnx --no-incremental`;
  - `npm --prefix frontend run check` verde.
- **Commits:**
  - em pt-BR, terminando com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`;
  - na branch `fase-1/dashboard`, que já tem o commit da spec;
  - **sem push**: o controlador faz o push depois da revisão.
- **Testes de integração:**
  - relógio `RelogioFixo` (05/11/2026 12:00 UTC → hoje 05/11);
  - dados criados pela API, como em `MetasTests.cs` (`IniciarAsync`, `CriarContaAsync`);
  - CPFs só de `CpfSintetico`.

## Foco da revisão

São casos que a spec implica e que nenhum teste dela cobre. Cada um tem teste na tarefa responsável:

1. **Parcela de data futura**: não aparece em "Últimos lançamentos" nem no patrimônio de hoje. Testes:
   - `Ultimos_ignora_lancamento_de_data_futura` (Tarefa 2);
   - `Cartao_entra_negativo_e_parcela_futura_nao` (Tarefa 2).
2. **Teto zero:** com teto 0 e usado 0, a situação é `ESGOTADO`, não `SEM_TETO`. Uma categoria com teto 0 e usado > 0 é `PASSOU` e vem primeiro. Testes:
   - `Teto_zero_sem_uso_esta_esgotado`;
   - `Categoria_com_teto_zero_e_uso_vem_primeiro_como_passou` (Tarefa 4).
3. **Cartão cadastrado já devendo** (saldo inicial negativo): a dívida entra no restante da fechada. Teste `Saldo_inicial_negativo_do_cartao_entra_no_restante` (Tarefa 1).
4. **Último dia do mês:** dias = 1, e o valor por dia é o restante inteiro, truncado em reais. Teste `Restante_por_dia_no_ultimo_dia_do_mes` (Tarefa 4).
5. **Outro casal:** os dados dele não vazam para patrimônio, faturas nem últimos. Testes:
   - `Patrimonio_ignora_outro_casal` (Tarefa 2);
   - `Ultimos_vem_por_data_e_id_e_respeitam_o_casal` (Tarefa 2);
   - `Faturas_ignoram_outro_casal` (Tarefa 1).

## Cenário de novembro (Tarefas 5 e 7)

Casal Ana e Bruno; Ana logada.

**Contas** (saldo inicial em 2026-09-01, salvo indicação):

| Conta | Tipo | Saldo inicial |
| --- | --- | --- |
| Conjunta Itaú | `CORRENTE`, conjunta | 1.000.000 |
| Carteira | `DINHEIRO`, Ana | 20.000 |
| Poupança Casa | `POUPANCA` | 5.000.000, em **2026-10-15** |
| XP Carro | `INVESTIMENTO` | 3.000.000 |
| Itaú Visa | `CARTAO`, fecha dia 1 e vence dia 10 | 0 |

**Tetos de novembro:** Mercado 160.000, Restaurantes 60.000, Lazer 50.000.

**Lançamentos:**

| Data | Conta | O quê | Valor |
| --- | --- | --- | --- |
| 20/09 | Itaú Visa | Restaurantes | despesa 5.000 (fatura 10/10, nunca paga) |
| 15/10 | Itaú Visa | Mercado | despesa 100.000 em 10x: p1 de 10.000 com data 15/10 e fatura 10/11; p2 com data 15/11 e fatura 10/12 |
| 25/10 | Conjunta Itaú | Salário | receita 900.000 |
| 01/11 | Conjunta Itaú | Mercado | despesa 31.200 |
| 03/11 | Itaú Visa | Restaurantes | despesa 18.800 (fatura 10/12) |
| 04/11 | Conjunta Itaú → Poupança Casa | transferência | 500.000 |

**Metas:**
- Casa: Poupança Casa, alvo 30.000.000, aporte 500.000, rendimento 0;
- Carro: XP Carro, alvo 8.000.000, aporte 180.000, rendimento 0.

**Esperado** (em centavos). Depois da entrega (10/11), a fatura vencida passou a ter linha própria e o estado `COM_ATRASO` e o campo `AtrasoCentavos` do `VencimentoDto` saíram; o texto das tarefas abaixo fica como foi executado.

| Item | Valor |
| --- | --- |
| Ainda dá para gastar | teto 270.000, usado 60.000 (Mercado 41.200 com a p2 de 15/11, que é de novembro; Restaurantes 18.800), restante 210.000, 26 dias, por dia 8.000, percentual 22, situação `COM_TETO`; fatura vencida 5.000 (a de 10/10) |
| Saldo das contas | 1.388.800 = Conjunta 1.368.800 + Carteira 20.000; outras 0 |
| Aporte planejado | 680.000 (Casa 500.000, Carro 180.000); já aportado 500.000 |
| Patrimônio | 9.855.000; variação −50.000 (fim de outubro 9.905.000); percentual −0,5 |
| Aviso | `Compras no cartão Itaú Visa a partir de hoje só vencem em 10/12 — a fatura fechou em 01/11` |
| Patrimônio em 12 meses | dez/2025 a ago/2026 nulos; set 4.015.000 (sem a Poupança, que começa em 15/10); out 9.905.000; nov 9.855.000; intervalo `dez/2025 → nov/2026` |
| Metas | Casa 18% (5.500.000 de 30.000.000), Carro 37% (3.000.000 de 8.000.000). Situação e "chega em" iguais aos do `GET /metas` (à mão: Casa 2030-12, Carro 2029-02) |
| Orçamento | Restaurantes 18.800/60.000 31%, Mercado 41.200/160.000 25%, Lazer 0/50.000 0%; outras 0 |
| Vencimentos | Itaú Visa `VENCIDA` desde 10/10, valor 5.000, fechada em 01/10 (no topo); Itaú Visa 10/11 `FECHADA`, valor 10.000 (só a dela; o atraso está na vencida), fechada em 01/11; Itaú Visa 10/12 `ABERTA` 18.800, fecha em 01/12 |
| Últimos | as duas pernas de 04/11 (−500.000 e +500.000), 03/11 −18.800, 01/11 −31.200, 25/10 +900.000. A p2 de 15/11 não aparece |
| Apelido | `Ana` |

**Esperado em 11/11** (o dia seguinte ao vencimento de 10/11, sem novos lançamentos; acrescentado depois da entrega):

| Item | Valor |
| --- | --- |
| Ainda dá para gastar | restante 210.000, 20 dias, por dia 10.500, percentual 22, `COM_TETO`; fatura vencida 15.000 |
| Saldo, aporte, patrimônio | iguais aos de 05/11 (1.388.800; 680.000 e 500.000 já aportado; 9.855.000 e −50.000) |
| Aviso | nenhum (o fechamento de 01/11 foi há 10 dias) |
| Vencimentos | Itaú Visa `VENCIDA` desde 10/10 (a mais antiga em aberto), valor 15.000 (os 10.000 da p1 mais os 5.000 de 10/10), fechada em 01/10, no topo; Itaú Visa 10/12 `ABERTA` 18.800. A de 10/11 não aparece mais como fechada |
| Pagando 5.000 em 11/11 | quita a de 10/10; fica `VENCIDA` desde 10/11, valor 10.000 |

---

### Tarefa 1: Lancamentos — faturas por cartão com restante e atraso

**Arquivos:**
- Modificar: `backend/src/Modules/Lancamentos/Contracts/Lancamentos.cs`, `backend/src/Modules/Lancamentos/LancamentosModule.cs`
- Criar: `backend/src/Modules/Lancamentos/Infra/ConsultaDeFaturas.cs`
- Modificar: `backend/src/Modules/Lancamentos/Aplicacao/ConsultaDeSaldos.cs`, para extrair o cálculo dos períodos aberta/fechada num helper `internal` reaproveitado pelas duas consultas
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/FaturasTests.cs`

**Interfaces — produz:**
```csharp
public sealed record FaturaFechadaAPagar(DateOnly Vencimento, DateOnly Fechamento, long TotalCentavos, long RestanteCentavos, long AtrasoCentavos);
public sealed record FaturaAbertaAteHoje(DateOnly Vencimento, DateOnly Fechamento, long TotalAteHojeCentavos);
public sealed record FaturasDoCartao(Guid ContaId, FaturaFechadaAPagar Fechada, FaturaAbertaAteHoje Aberta);
public interface IConsultaDeFaturas { Task<IReadOnlyList<FaturasDoCartao>> AVencerAsync(CancellationToken ct = default); }
```

`AVencerAsync` devolve **todos** os cartões do casal, inclusive arquivados, sem filtrar; quem decide o que mostrar é o Painel. Fechada e aberta seguem a mesma regra do `GET /saldos` em hoje. A fórmula do devido está na spec, junto com D5. `TotalAteHojeCentavos` conta os lançamentos com a fatura da aberta e `data ≤ hoje`.

Cartão de teste: fecha dia 1, vence dia 10, saldo inicial em 2026-09-01. Hoje 05/11 → fechada 10/11 (fechou 01/11) e aberta 10/12.

- [ ] **Passo 1: Testes que falham:**
  - `Compra_em_10x_so_conta_a_parcela_da_fatura_fechada`: 100.000 em 10x em 15/10 → fechada total 10.000, restante 10.000, atraso 0; aberta 0 (a p2 tem data 15/11).
  - `Fatura_anterior_nao_paga_aparece_como_atraso`: mais 5.000 em 20/09 → restante 15.000, atraso 5.000.
  - `Pagamento_parcial_abate_o_restante`: só a 10x e uma transferência de 3.000 da corrente para o cartão em 03/11 → restante 7.000, atraso 0.
  - `Fatura_paga_some_dos_vencimentos`: transferência de 10.000 → restante 0, atraso 0.
  - `Parcela_com_data_passada_e_fatura_aberta_nao_entra_na_fechada`: compra de 2x em 03/11 (p1 com data 03/11 e fatura 10/12) → fechada restante 0; aberta total até hoje = valor da p1.
  - `Saldo_inicial_negativo_do_cartao_entra_no_restante`: saldo inicial −20.000, sem lançamentos → restante 20.000, atraso 20.000.
  - `Faturas_ignoram_outro_casal`: o outro casal tem um cartão com compra; a lista de Ana não tem o cartão dele.
- [ ] **Passo 2: `dotnet test backend/tests/GestorFinanceiro.IntegrationTests --filter FaturasTests` falha** (não compila: o contrato não existe).
- [ ] **Passo 3: Implementar** `ConsultaDeFaturas` (`internal`, registrada como `IConsultaDeFaturas`). As somas são `SUM` agrupado no banco, sem decifrar nada. O `GET /saldos` continua igual (`SaldosTests` verdes).
- [ ] **Passo 4: Suíte verde e 0 avisos.**
- [ ] **Passo 5: Commit** — `Lancamentos: faturas a vencer com restante e atraso`

---

### Tarefa 2: Lancamentos — patrimônio por mês e últimos lançamentos

**Arquivos:**
- Modificar: `backend/src/Modules/Lancamentos/Contracts/Orcamento.cs` (o mesmo arquivo de `IConsultaDeSaldos`), `backend/src/Modules/Lancamentos/Contracts/Lancamentos.cs`, `backend/src/Modules/Lancamentos/Infra/ConsultaDeSaldosPorConta.cs`, `backend/src/Modules/Lancamentos/Aplicacao/ConsultaDoExtrato.cs`, `backend/src/Modules/Lancamentos/LancamentosModule.cs`
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/PatrimonioTests.cs`, `backend/tests/GestorFinanceiro.IntegrationTests/UltimosLancamentosTests.cs`

**Interfaces — produz:**
```csharp
public sealed record PatrimonioDoMes(string Mes, long? TotalCentavos);   // Mes = "AAAA-MM"
// em IConsultaDeSaldos:
Task<IReadOnlyList<PatrimonioDoMes>> PatrimonioPorMesAsync(DateOnly primeiroMes, int meses, CancellationToken ct = default);
public interface IConsultaDoExtrato { Task<IReadOnlyList<LancamentoDto>> UltimosAsync(int quantos, CancellationToken ct = default); }
```

**`PatrimonioPorMesAsync`:**
- O ponto de cada mês é `min(fimDoMes, hoje)`.
- Uma conta entra se `SaldoInicialEm ≤ ponto`; arquivadas também.
- Mês sem conta → `null`.
- Uma query agrupando por conta e mês, com o acumulado feito em memória.
- `SaldosAsync(em)` **não muda**.

**`UltimosAsync`:** segue D1. A montagem do `LancamentoDto` (decifrar, `Quem`, categoria) é a mesma do extrato, extraída num método compartilhado e não copiada. Registrar `ConsultaDoExtrato` também como `IConsultaDoExtrato`.

- [ ] **Passo 1: Testes que falham:**
  - `Conta_nao_entra_antes_do_saldo_inicial`: poupança com saldo inicial em 2026-10-15 → setembro sem ela, outubro com ela.
  - `Mes_sem_nenhuma_conta_e_nulo`: agosto/2026 → `null`.
  - `Cartao_entra_negativo_e_parcela_futura_nao`: cartão com 10x de 100.000 em 15/10 → novembro (hoje) tem −10.000 do cartão, não −20.000.
  - `Conta_arquivada_continua_no_patrimonio`.
  - `Patrimonio_de_hoje_igual_a_soma_dos_saldos`: o último ponto é igual a Σ `SaldosAsync(hoje)`.
  - `Patrimonio_ignora_outro_casal`.
  - `Ultimos_vem_por_data_e_id_e_respeitam_o_casal`: 6 lançamentos em datas distintas → `UltimosAsync(5)` traz os 5 mais recentes em ordem e nenhum do outro casal.
  - `Ultimos_ignora_lancamento_de_data_futura`.
- [ ] **Passo 2: Ver falhar.** **Passo 3: Implementar.** **Passo 4: Suíte verde (extrato e saldos intactos).**
- [ ] **Passo 5: Commit** — `Lancamentos: patrimônio por mês e últimos lançamentos`

---

### Tarefa 3: Orcamento e Investimentos — consultas para outros módulos

**Arquivos:**
- Modificar: `backend/src/Modules/Orcamento/Contracts/Orcamento.cs`, `backend/src/Modules/Orcamento/OrcamentoModule.cs`, `backend/src/Modules/Investimentos/Contracts/Metas.cs`, `backend/src/Modules/Investimentos/InvestimentosModule.cs`
- Criar: `backend/src/Modules/Orcamento/Aplicacao/ConsultaDoOrcamento.cs`, `backend/src/Modules/Investimentos/Aplicacao/ConsultaDeMetas.cs`
- Teste: `backend/tests/GestorFinanceiro.IntegrationTests/ConsultasParaOPainelTests.cs`

**Interfaces — produz:**
```csharp
public interface IConsultaDoOrcamento { Task<OrcamentoDoMesDto> DoMesAsync(DateOnly mes, CancellationToken ct = default); }
public interface IConsultaDeMetas { Task<PainelDeMetasDto> PainelAsync(CancellationToken ct = default); }
```

As duas delegam ao serviço existente: `ServicoDeOrcamento.ObterAsync(mes.ToString("yyyy-MM"))` e `ServicoDeMetas.PainelAsync`. Qualquer resultado que não seja o de sucesso (sem casal, inválido) vira `InvalidOperationException` sem dado no texto.

- [ ] **Passo 1: Testes que falham:**
  - `Consulta_do_orcamento_devolve_o_mesmo_que_o_GET`: cria um teto e uma despesa e compara o JSON de `GET /orcamento?mes=2026-11` com o `DoMesAsync(2026-11-01)` resolvido num escopo da API logada.
  - `Consulta_de_metas_devolve_o_mesmo_que_o_GET`: o mesmo para metas.
  - Se resolver o serviço com a sessão no teste der trabalho, use um endpoint só de teste registrado por `ApiDeTeste(servicos: …)`.
- [ ] **Passo 2: Ver falhar.** **Passo 3: Implementar.** **Passo 4: Suíte verde.**
- [ ] **Passo 5: Commit** — `Orcamento e Investimentos: consultas para o painel`

---

### Tarefa 4: Módulo Painel — esqueleto, regras puras e arquitetura

**Arquivos:**
- Criar: `backend/src/Modules/Painel/GestorFinanceiro.Modules.Painel.csproj` (como o de Investimentos: `FrameworkReference` AspNetCore e `ProjectReference` de SharedKernel, Identidade, Contas, Lancamentos, Orcamento e Investimentos; **sem** pacote EF/Npgsql)
- Criar: `backend/src/Modules/Painel/PainelModule.cs` (`AddPainel(this IServiceCollection)`, `MapPainel(this IEndpointRouteBuilder)`, que nesta tarefa ainda não mapeia nada)
- Criar: `backend/src/Modules/Painel/Contracts/VisaoGeral.cs`
- Criar: `backend/src/Modules/Painel/Dominio/RegrasDoPainel.cs`
- Modificar: `backend/GestorFinanceiro.slnx`, `backend/src/Host/GestorFinanceiro.Api/GestorFinanceiro.Api.csproj`, `backend/src/Host/GestorFinanceiro.Api/Program.cs` (`.AddPainel()` e `.MapPainel()`), `backend/tests/GestorFinanceiro.ArchitectureTests/RegrasDeArquitetura.cs`, `backend/tests/GestorFinanceiro.UnitTests/GestorFinanceiro.UnitTests.csproj` (referência ao Painel; `InternalsVisibleTo` como nos outros módulos)
- Teste: `backend/tests/GestorFinanceiro.UnitTests/RegrasDoPainelTests.cs`

**Interfaces — produz** (`Contracts/VisaoGeral.cs`):
```csharp
public sealed record SecaoDto<T>(T? Dados, string? Erro);
public sealed record AindaDaParaGastarDto(string Situacao, long? TetoCentavos, long UsadoCentavos, long? RestanteCentavos,
    long? PorDiaCentavos, int? PercentualUsado, DateOnly FimDoMes);            // Situacao: COM_TETO | ESGOTADO | PASSOU | SEM_TETO
public sealed record ContaNoSaldoDto(Guid ContaId, string Apelido, long SaldoCentavos);
public sealed record SaldoDasContasDto(long TotalCentavos, IReadOnlyList<ContaNoSaldoDto> Contas, int OutrasContas);
public sealed record AporteDaMetaDto(Guid MetaId, string Nome, long AportePlanejadoCentavos);
public sealed record AportePlanejadoDto(long TotalCentavos, long JaAportadoCentavos, IReadOnlyList<AporteDaMetaDto> Metas);
public sealed record PatrimonioDto(long TotalCentavos, long? VariacaoCentavos, decimal? VariacaoPercentual);
public sealed record AvisoDeFaturaDto(Guid ContaId, string Texto);
public sealed record PontoDoPatrimonioDto(string Mes, string Rotulo, long? TotalCentavos);   // "2026-11", "nov"
public sealed record PatrimonioEm12MesesDto(string Intervalo, IReadOnlyList<PontoDoPatrimonioDto> Pontos);
public sealed record MetaDoPainelDto(Guid Id, string Nome, int ProgressoPercentual, long GuardadoCentavos, long ValorAlvoCentavos,
    string Situacao, string? ChegaEm);                                          // Situacao e ChegaEm vêm do ProjecaoDto
public sealed record MetasDoPainelDto(IReadOnlyList<MetaDoPainelDto> Metas, int Total);
public sealed record CategoriaDoPainelDto(Guid CategoriaId, string Nome, long TetoCentavos, long UsadoCentavos, int PercentualUsado, string Estado); // NORMAL | PASSOU
public sealed record OrcamentoDoPainelDto(IReadOnlyList<CategoriaDoPainelDto> Categorias, int OutrasCategorias);
public sealed record VencimentoDto(Guid ContaId, string Apelido, DateOnly Vencimento, DateOnly Fechamento, string Estado,
    long ValorCentavos, long AtrasoCentavos);                                   // Estado: FECHADA | COM_ATRASO | ABERTA
public sealed record VisaoGeralDto(string Mes, DateOnly Hoje, int DiaDoMes, int DiasNoMes, string Apelido,
    SecaoDto<AindaDaParaGastarDto> AindaDaParaGastar, SecaoDto<SaldoDasContasDto> SaldoDasContas,
    SecaoDto<AportePlanejadoDto> AportePlanejado, SecaoDto<PatrimonioDto> Patrimonio,
    SecaoDto<IReadOnlyList<AvisoDeFaturaDto>> AvisosDeFatura, SecaoDto<PatrimonioEm12MesesDto> PatrimonioEm12Meses,
    SecaoDto<MetasDoPainelDto> Metas, SecaoDto<OrcamentoDoPainelDto> Orcamento,
    SecaoDto<IReadOnlyList<VencimentoDto>> Vencimentos, SecaoDto<IReadOnlyList<LancamentoDto>> UltimosLancamentos);
```

`RegrasDoPainel` (`internal static`): uma função por regra da spec, recebendo os tipos dos contratos e `hoje`. Assinaturas:
```csharp
AindaDaParaGastarDto AindaDaParaGastar(FatiaDto tetoDeGastos, DateOnly hoje);
SaldoDasContasDto SaldoDasContas(IReadOnlyList<ContaResumo> contas, IReadOnlyDictionary<Guid, long> saldos);
AportePlanejadoDto AportePlanejado(IReadOnlyList<MetaDto> metas);
PatrimonioDto Patrimonio(IReadOnlyList<PatrimonioDoMes> pontos);                      // D6
PatrimonioEm12MesesDto PatrimonioEm12Meses(IReadOnlyList<PatrimonioDoMes> pontos);
IReadOnlyList<AvisoDeFaturaDto> AvisosDeFatura(IReadOnlyList<FaturasDoCartao> faturas, IReadOnlyDictionary<Guid, ContaResumo> contas, DateOnly hoje);
MetasDoPainelDto Metas(IReadOnlyList<MetaDto> metas);                                 // até 3
OrcamentoDoPainelDto Orcamento(IReadOnlyList<TetoDaCategoriaDto> categorias);         // sem papel, com teto, até 6
IReadOnlyList<VencimentoDto> Vencimentos(IReadOnlyList<FaturasDoCartao> faturas, IReadOnlyDictionary<Guid, ContaResumo> contas, DateOnly hoje); // até 5
```

- [ ] **Passo 1: Testes que falham** (`RegrasDoPainelTests`):
  - `Restante_por_dia_conta_hoje_ate_o_fim_do_mes`: teto 400.000, usado 83.600, hoje 05/11 → restante 316.400, `PorDiaCentavos` 12.100 (R$ 121), percentual 20.
  - `Restante_por_dia_no_ultimo_dia_do_mes`: restante 12.345 em 30/11 → por dia 12.300.
  - `Passou_do_teto_nao_tem_valor_por_dia`: `PASSOU`, restante −500, por dia nulo.
  - `Teto_zero_sem_uso_esta_esgotado`: `ESGOTADO`, percentual nulo.
  - `Sem_teto_devolve_sem_teto`.
  - `Percentual_do_teto_arredonda_para_baixo`: usado 99.999 de 100.000 → 99.
  - `Variacao_do_patrimonio_sem_percentual_quando_mes_anterior_e_zero_negativo_ou_nulo` (três casos). Com M−1 nulo, a variação também é nula. Outro caso: 9.905.000 → 9.855.000 dá −50.000 e −0,5.
  - `Intervalo_e_rotulos_dos_12_meses`: `dez/2025 → nov/2026`, rótulos `dez`…`nov`.
  - `Aviso_de_fatura_so_ate_5_dias_do_fechamento`: fechamento 01/11 → aparece em 06/11 com o texto exato da spec e some em 07/11.
  - `Saldo_das_contas_lista_3_ou_as_2_maiores_e_mais_N`: só `CORRENTE` e `DINHEIRO` não arquivadas; 3 contas → todas; 4 → as 2 maiores e `OutrasContas` 2.
  - `Orcamento_ordena_por_percentual_e_corta_em_6`: 8 categorias → 6 e `OutrasCategorias` 2; empate pelo nome; categoria com papel ou sem teto fica fora.
  - `Categoria_com_teto_zero_e_uso_vem_primeiro_como_passou`.
  - `Vencimentos_ordena_e_corta_em_5`: fechada só com `Vencimento ≥ hoje` e restante > 0; `COM_ATRASO` quando atraso > 0; aberta só com total > 0.
  - `Metas_corta_em_3_e_mantem_a_ordem`.
- [ ] **Passo 2: Testes de arquitetura que falham** (`RegrasDeArquitetura`):
  - incluir `typeof(PainelModule)` em `NomesDosModulos`;
  - `Painel_nao_referencia_EF_Npgsql_nem_DbContext`, que chama `static IReadOnlyList<string> AcessoADados(Assembly a)`: lista as referências diretas que começam com `Microsoft.EntityFrameworkCore` ou `Npgsql` e os tipos que herdam de `DbContext` (comparando pelo nome completo da cadeia de `BaseType`), e exige lista vazia para o Painel;
  - `Verificador_de_acesso_a_dados_acusa_Lancamentos`: o mesmo verificador no assembly de Lancamentos devolve lista não vazia (prova da falha).
- [ ] **Passo 3: Ver falhar.** **Passo 4: Implementar.** **Passo 5: `dotnet test backend/tests/GestorFinanceiro.ArchitectureTests` e unitários verdes; suíte inteira verde.**
- [ ] **Passo 6: Commit** — `Painel: módulo, regras da visão geral e arquitetura`

---

### Tarefa 5: Painel — montagem, endpoint e contrato

**Arquivos:**
- Criar: `backend/src/Modules/Painel/Aplicacao/MontagemDoPainel.cs`, `backend/src/Modules/Painel/Api/EndpointsDoPainel.cs`
- Modificar: `backend/src/Modules/Painel/PainelModule.cs`
- Modificar (gerado): `frontend/src/api/schema.d.ts`
- Criar (teste): `backend/tests/GestorFinanceiro.IntegrationTests/VisaoGeralTests.cs` e, se ainda não houver, `backend/tests/GestorFinanceiro.IntegrationTests/Infra/LogsCapturados.cs` (um `ILoggerProvider` em memória registrado por `ApiDeTeste(servicos: …)`)

**Interfaces — consome:** Tarefas 1 a 4, mais `IConsultaDeContas`, `IConsultaDeSaldos.SaldosAsync`, `IConsultaDeUsuarios.ListarDoCasalAsync` e `IUsuarioAtual`.

**Interfaces — produz:**
- `GET /api/v1/visao-geral` → `200 VisaoGeralDto`, tag `Painel`, nome `ObterVisaoGeral`, com `Cache-Control: no-store`.
- `MontagemDoPainel.MontarAsync(string correlacao, CancellationToken ct)`.

**Montagem:**
- As seções rodam em sequência, cada uma num `try/catch` que **não** captura `OperationCanceledException`.
- No erro, registra `LogWarning("Seção {Secao} falhou ({Tipo}); correlação {Correlacao}", nome, ex.GetType().Name, correlacao)`, sem a exceção e sem a mensagem, e devolve `new SecaoDto<T>(default, "Não foi possível carregar")`.
- Uma leitura que serve a várias seções (contas, faturas, metas, orçamento, patrimônio por mês) é feita uma vez. Se ela falha, todas as seções que dependem dela ficam com `Erro`.
- Sem casal na sessão → `404`, como os outros módulos.

- [ ] **Passo 1: Testes que falham:**
  - `Visao_geral_de_novembro_confere_numero_a_numero`: monta o **Cenário de novembro** pela API e confere cada linha da tabela "Esperado". Metas comparadas com o `GET /metas` da mesma execução.
  - `Casal_sem_nada_devolve_as_secoes_vazias`:
    - `SEM_TETO`;
    - saldo 0 sem contas;
    - aporte 0 sem metas;
    - patrimônio 0 e 12 pontos nulos;
    - listas vazias;
    - nenhum `Erro`.
  - `Secao_com_falha_devolve_erro_so_nela_e_loga_tipo_sem_valores`:
    - substitui `IConsultaDeMetas` por um que lança `InvalidOperationException("Viagem sintética 4471")`;
    - `metas.erro` e `aportePlanejado.erro` = `Não foi possível carregar`, e as demais seções têm `dados`;
    - o log capturado contém `Metas`, `InvalidOperationException` e o `TraceIdentifier`, e não contém `Viagem sintética 4471`.
  - `Sem_sessao_responde_401`.
  - `Resposta_tem_no_store`.
- [ ] **Passo 2: Ver falhar.** **Passo 3: Implementar.** **Passo 4: Suíte verde e contrato regerado (`dotnet build backend/src/Host/GestorFinanceiro.Api && npm --prefix frontend run gen:api`); `npm --prefix frontend run check` verde.**
- [ ] **Passo 5: Commit** — `Painel: GET /visao-geral com seções independentes`

---

### Tarefa 6: Tela da Visão geral

**Arquivos:**
- Criar: `frontend/src/api/painel.ts`, `frontend/src/lib/painel.ts`, `frontend/src/lib/painel.test.ts`
- Criar: `frontend/src/paginas/visao-geral/` com `CartoesDoTopo.tsx`, `GraficoDoPatrimonio.tsx`, `MetasDoPainel.tsx`, `OrcamentoDoPainel.tsx`, `Vencimentos.tsx`, `UltimosLancamentos.tsx` e `Secao.tsx` (moldura com carregando, erro e "Tentar de novo")
- Modificar: `frontend/src/paginas/VisaoGeral.tsx` (reescrita), `frontend/src/lib/dinheiro.ts`, `frontend/src/lib/dinheiro.test.ts`, `frontend/src/styles/app.css`
- Modificar (invalidação de `["visao-geral"]`): `frontend/src/paginas/lancamentos/LancamentoRapido.tsx`, `frontend/src/paginas/lancamentos/EditarLancamento.tsx`, `frontend/src/api/metas.ts`, `frontend/src/paginas/metas/AtualizarSaldo.tsx`, `frontend/src/paginas/Contas.tsx` e as mutações do orçamento (`frontend/src/api/orcamento.ts`)

**Interfaces — consome:** `components["schemas"]["VisaoGeralDto"]`, pelo caminho (D8).

**Interfaces — produz:**
```ts
export function useVisaoGeral(); // queryKey ["visao-geral"], refetchOnMount: "always", retry: false
export function formatarReaisInteiros(centavos: number, opcoes?: { simbolo?: boolean }): string; // trunca para zero
export function saudacao(hora: number): "Bom dia" | "Boa tarde" | "Boa noite";
export function resumoDoGrafico(pontos: { rotulo: string; mes: string; totalCentavos?: number | null }[]): string | null;
export function aparenciaDaFatura(estado: string): { rotulo: string; tom: "alerta" | "normal" };
export function aparenciaDaMeta(situacao: string, chegaEm?: string | null): { texto: string; tom: "ok" | "normal" | "alerta" };
```

**Tela** conforme a seção "Tela" da spec, inclusive:
- o `<details>` com o texto `Ver valores por mês` e a tabela Mês/Patrimônio (`—` quando nulo);
- o `aria-label` do gráfico só com o resumo;
- o botão "+ Novo lançamento", que abre o `LancamentoRapido` com as contas e categorias pelos hooks já existentes;
- a nota do patrimônio com `Não inclui dívidas fora do cartão (empréstimo, consórcio)`.

O mês por extenso ("Novembro de 2026 · dia 5 de 30") sai de `Mes`, `DiaDoMes` e `DiasNoMes`.

- [ ] **Passo 1: Testes que falham (Vitest):**
  - `formatarReaisInteiros(249999)` → `"R$ 2.499"` (com o espaço que o `Intl` usa), `formatarReaisInteiros(249999, { simbolo: false })` → `"2.499"` e `formatarReaisInteiros(-249999)` trunca para −2.499;
  - `saudacao`: 11 → `Bom dia`, 12 → `Boa tarde`, 17 → `Boa tarde`, 18 → `Boa noite`, 0 → `Bom dia`;
  - `resumoDoGrafico` com nulos de dez a ago → `de R$ 40.150,00 em set/2026 a R$ 98.550,00 em nov/2026`; tudo nulo → `null`;
  - `aparenciaDaFatura("COM_ATRASO")` → tom `alerta`; `ABERTA` → rótulo começa com `Aberta`;
  - `aparenciaDaMeta`: `CHEGA` com `2029-02` → `previsão fev/2029`; `ALCANCADA` → `Alcançada`; `NAO_CHEGA` → `Não chega em 100 anos com o aporte planejado`.
- [ ] **Passo 2: `npm --prefix frontend test` falha.** **Passo 3: Implementar.** **Passo 4: `npm --prefix frontend run check` verde.**
- [ ] **Passo 5: Commit** — `Tela da visão geral com dados reais`

---

### Tarefa 7: Conferência no navegador, SPEC e PR pronto

Esta tarefa é do **controlador**, não de subagente.

- [ ] **Passo 1: Ambiente descartável**, como nas metas: Postgres próprio, API com `GF_DEV_CHAVE_EFEMERA=1`, Vite, cookies limpos e passkey por autenticador virtual.
- [ ] **Passo 2: Montar o Cenário de novembro** pela tela ou pela API. O relógio é o do sistema, então as datas se ajustam ao mês real com o mesmo formato, e a tabela "Esperado" é refeita à mão para esse mês antes de abrir a tela.
- [ ] **Passo 3: Conferir** cada número da tela contra a tabela, uma linha por item no PR. Também:
  - seção com erro (pelo teste da Tarefa 5);
  - 900 px;
  - o `<details>`;
  - "+ Novo lançamento" atualizando os cartões.
- [ ] **Passo 4: Desmontar** pelas portas (`ss -ltnp`), nunca `pkill -f`.
- [ ] **Passo 5: Atualizar o `docs/SPEC.md`:**
  - Fase 1 "Código concluído";
  - linha do PR em "Entregue" (módulo Painel, sem migração);
  - em "Regras de negócio já decididas": restante por dia, patrimônio e histórico, restante e atraso da fatura;
  - tarefa do dashboard marcada.
- [ ] **Passo 6: Verificação final** (suíte, build sem avisos, check, arquitetura) e commit `SPEC: dashboard da Fase 1 com código concluído`.
- [ ] **Passo 7: Push e PR fora do rascunho**, com a tabela conferida.
