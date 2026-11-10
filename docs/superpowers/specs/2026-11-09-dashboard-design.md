# Dashboard da Visão geral (Fase 1, bloco 4) — design

**Status:** aprovado em conversa, seção a seção, em 09/11/2026.
**Objetivo:** ao abrir o sistema, o casal vê num só lugar quanto ainda dá para gastar no mês, quanto tem nas contas, quanto planeja aportar, o patrimônio e a evolução dele, as metas, o orçamento por categoria, as faturas a vencer e os últimos lançamentos — tudo com dados reais dos módulos que já existem. É o último bloco de código da Fase 1.

**Base:**
- `docs/SPEC.md` (objetivo: "quanto ainda pode gastar no mês, quanto investir e quando chega à casa e ao carro");
- `docs/design/telas/Main.dc.html`;
- specs do orçamento (`2026-11-06-orcamento-design.md`) e das metas (`2026-11-06-metas-design.md`);
- `CLAUDE.md` (regras 5, 7, 8 e 9).

## Escopo

**Entra:**
- os 4 cartões do topo: "Ainda dá para gastar", "Saldo das contas", "Aporte planejado" e "Patrimônio do casal";
- aviso de fatura recém-fechada;
- patrimônio em 12 meses;
- metas;
- orçamento do mês por categoria;
- próximos vencimentos (só faturas de cartão);
- últimos lançamentos;
- saudação com o apelido de quem está logado e o botão "+ Novo lançamento" (o `LancamentoRapido` existente);
- módulo novo `Painel`.

**Fica fora:**
- boletos e débito automático nos vencimentos, e o link "Calendário" (bloco Boletos);
- "Sincronizado há 2 h" e a contagem "Lançamentos 3" do menu (caixa "Conciliar", Fase 2);
- a busca do topo;
- navegação de mês (‹ ›): o dashboard é sempre do **mês corrente**; o passado aparece no gráfico;
- dívidas fora do cartão (empréstimo, consórcio): bloco próprio de dívidas.

## Decisões

1. **Módulo `GestorFinanceiro.Modules.Painel`**, sem schema, sem `DbContext` e sem migração. Só lê contratos (`Contracts`) de Identidade, Contas, Lancamentos, Orcamento e Investimentos. Cada número é calculado no módulo dono; o Painel só combina (restante por dia, patrimônio, variação, aviso, ordenação e corte de listas). O `Assistente` fica para a Fase 4.
2. **Um endpoint**, `GET /api/v1/visao-geral`, com cada parte da tela como seção independente: se uma consulta falha, só aquela seção mostra erro.
3. **Nenhum número é calculado no front.** O front formata centavos e datas, escolhe rótulo e cor pelo estado que o servidor manda, converte valores em coordenadas do gráfico e escolhe a saudação pela hora do navegador.
4. **Saldo das contas** = contas `CORRENTE` e `DINHEIRO` não arquivadas (escolha do casal; o mockup mostrava só a conta conjunta).
5. **Aporte planejado** = soma do aporte planejado das metas, com "já aportado" no lugar do "até dia 10" do mockup (não existe dia de aporte).
6. **Patrimônio** = soma dos saldos de todas as contas, inclusive arquivadas; o cartão entra com o próprio saldo, negativo pelo que está em aberto. Enquanto o bloco de dívidas não existe, a nota diz "Não inclui dívidas fora do cartão (empréstimo, consórcio)".
7. **Histórico do patrimônio**: uma conta só entra a partir do mês em que já existia no sistema (`saldo_inicial_em ≤ fim do mês`). O `SaldosAsync(em)` atual devolve o saldo inicial também antes dessa data e faz uma query por conta; ele **não muda** (metas e conciliação dependem dele). O histórico ganha contrato próprio, numa query só.
8. **Fatura a pagar pela `fatura_vencimento`, não pela data.** Parcela tem `data = compra + i meses` e a fatura em `fatura_vencimento`; os dois nem sempre andam juntos (compra em 06/11 com fechamento dia 5: a parcela 2 tem data 06/12 e fatura 12/01, que em 10/12 é a aberta). A regra fica em **Lancamentos**, dono de fatura.

## Regras de cálculo

Hoje no fuso de Brasília; M = mês corrente; `fimDoMes(X)` = último dia de X.

### Cartões do topo

| Cartão | Valor | Nota |
| --- | --- | --- |
| Ainda dá para gastar | `restante = teto − usado` da fatia `TetoDeGastos` do orçamento de M (só categorias sem papel) | `≈ R$ X por dia até DD/MM` com `X = floor(restante / dias)`, `dias` = de hoje até `fimDoMes(M)`, inclusive (dia 5 de 30 → 26); e `N% do teto usado` com `N = floor(usado × 100 / teto)` |
| Saldo das contas | Σ saldos de hoje das contas `CORRENTE` e `DINHEIRO` não arquivadas | apelido e saldo por conta: até 3 contas, todas; com 4 ou mais, as 2 de maior saldo e "+N contas" |
| Aporte planejado | Σ `AportePlanejadoCentavos` das metas | `{meta} R$ X · …` e `já aportado R$ Y` (Σ `AporteDoMesCentavos` das metas) |
| Patrimônio do casal | Σ saldos de hoje de todas as contas | `▲/▼ R$ X no mês (Y%)`, X = hoje − `fimDoMes(M−1)`, Y com 1 casa sobre `fimDoMes(M−1)`; e "Não inclui dívidas fora do cartão (empréstimo, consórcio)" |

**Casos de borda:**
- `restante ≤ 0`: "Passou R$ X do teto", sem valor por dia;
- teto de gastos nulo: "Sem teto de gastos neste mês" e link para o Orçamento (sem valor);
- sem metas: "Nenhuma meta" e link para Metas;
- variação: sem `%` quando o patrimônio de `fimDoMes(M−1)` é ≤ 0 ou nulo; sem a linha inteira quando ele é nulo.

### Aviso de fatura

Para cada cartão cuja última fatura fechada tem `hoje − fechamento ≤ 5` dias: "Compras no cartão {apelido} a partir de hoje só vencem em {vencimento da aberta} — a fatura fechou em {fechamento}". Uma linha por cartão. O texto vem pronto do servidor.

### Patrimônio em 12 meses

- Pontos de M−11 a M; cada ponto é o patrimônio em `fimDoMes(X)`, e o de M é o de hoje.
- Uma conta entra no mês X só se `saldo_inicial_em ≤ fimDoMes(X)` (inclusive arquivadas).
- Mês sem nenhuma conta: `null` (sem ponto).
- Rótulos (`dez`…`nov`) e o intervalo ("dez/2025 → nov/2026") vêm do servidor.

### Metas

Até 3, na ordem da tela de Metas. Por meta: nome, progresso, guardado, alvo e situação da projeção existente:
- `CHEGA` → "previsão mmm/aaaa";
- `ALCANCADA` → "Alcançada";
- `NAO_CHEGA` → "Não chega em 100 anos com o aporte planejado".

Vazio: "Nenhuma meta ainda" e link para criar.

### Orçamento do mês

- Categorias **sem papel** com teto em M, ordenadas por `usado / teto` decrescente (empate: nome), até 6, com usado e teto.
- Passou do teto: estado `PASSOU` (barra cheia em alerta).
- Restantes: "+N categorias".
- Sem nenhum teto: "Sem tetos neste mês" e link.

### Próximos vencimentos (faturas)

Por cartão (inclusive arquivado), com F = última fatura fechada e A = fatura aberta:

- **Devido até F** = saldo inicial do cartão + Σ despesas e receitas com `fatura_vencimento ≤ vencimento de F` (qualquer data) + Σ transferências (pagamentos e o que mais entrar ou sair por transferência) com `data ≤ hoje`.
- **Restante a pagar** = `máx(0, −devido)`.
- **Em atraso** = `restante − total de F`, quando > 0.
- F aparece se `vencimento ≥ hoje` e `restante > 0`, com "Fechada em DD/MM". Com atraso, a linha diz "inclui R$ X em atraso" em tom de alerta.
- A aparece se o total até hoje (lançamentos com `fatura_vencimento` = vencimento de A e `data ≤ hoje`) for > 0, com "Aberta · fecha em DD/MM".
- Lista por vencimento crescente, até 5. Vazio: "Nenhuma fatura a vencer".

### Últimos lançamentos

Os 5 mais recentes de todas as contas, por `data` e depois `criado_em`, decrescente. Transferência entra perna a perna. Linha: inicial de quem (titular; conjunta mostra o ícone do casal), descrição, "Hoje", "Ontem" ou "DD/MM", origem e valor com sinal. Sem hora (o lançamento só tem data).

## Contratos novos (nos módulos donos)

Implementações `internal` no próprio módulo; o Painel só conhece as interfaces.

| Módulo | Contrato | Devolve |
| --- | --- | --- |
| Orcamento | `IConsultaDoOrcamento.DoMesAsync(DateOnly mes)` | `OrcamentoDoMesDto`, pelo mesmo serviço do `GET /orcamento` |
| Investimentos | `IConsultaDeMetas.PainelAsync()` | `PainelDeMetasDto`, pelo mesmo serviço do `GET /metas` |
| Lancamentos | `IConsultaDeSaldos.PatrimonioPorMesAsync(DateOnly primeiroMes, int meses)` | `IReadOnlyList<PatrimonioDoMes(string Mes, long? TotalCentavos)>`, numa query |
| Lancamentos | `IConsultaDeFaturas.AVencerAsync()` | por cartão: F (vencimento, fechamento, total, restante, atraso) e A (vencimento, fechamento, total até hoje) |
| Lancamentos | `IConsultaDoExtrato.UltimosAsync(int quantos)` | `IReadOnlyList<LancamentoDto>`, decifrado como no extrato |

Já existentes e reaproveitados:
- `IConsultaDeSaldos.SaldosAsync(hoje)` e `IConsultaDeContas.ListarAsync()`: saldo das contas e patrimônio de hoje;
- `IConsultaDeUsuarios.ListarDoCasalAsync(casalId)`, filtrado pelo usuário da sessão: apelido da saudação.

Todos respeitam os filtros `SoftDelete` e `Casal`.

## Módulo `Painel`

- `PainelModule.cs` (`AddPainel`, `MapPainel`), registrado no Host.
- `Aplicacao/MontagemDoPainel.cs`: chama os contratos **em sequência** (cada módulo tem o próprio `DbContext`; ~8 queries baratas, sem paralelismo por ora) e monta o DTO.
- `Dominio/RegrasDoPainel.cs`: funções puras com as regras desta spec que são do Painel.
- `Api/EndpointsDoPainel.cs`.
- `Contracts/VisaoGeral.cs`: os DTOs.

**Falha de seção:** a montagem captura a exceção de cada seção, registra em log **o nome da seção, o tipo da exceção e o id de correlação** — nunca a mensagem, valores ou descrições — e devolve `Erro = "Não foi possível carregar"` naquela seção. `OperationCanceledException` do pedido não é capturada.

## API

`GET /api/v1/visao-geral`, tag `Painel`, exige sessão, `Cache-Control: no-store`.

```
VisaoGeralDto(
  string Mes,                 // "2026-11"
  DateOnly Hoje,
  int DiaDoMes, int DiasNoMes,
  string Apelido,
  SecaoDto<AindaDaParaGastarDto> AindaDaParaGastar,
  SecaoDto<SaldoDasContasDto> SaldoDasContas,
  SecaoDto<AportePlanejadoDto> AportePlanejado,
  SecaoDto<PatrimonioDto> Patrimonio,
  SecaoDto<IReadOnlyList<AvisoDeFaturaDto>> AvisosDeFatura,
  SecaoDto<PatrimonioEm12MesesDto> PatrimonioEm12Meses,
  SecaoDto<MetasDoPainelDto> Metas,
  SecaoDto<OrcamentoDoPainelDto> Orcamento,
  SecaoDto<IReadOnlyList<VencimentoDto>> Vencimentos,
  SecaoDto<IReadOnlyList<LancamentoDto>> UltimosLancamentos)

SecaoDto<T>(T? Dados, string? Erro)   // exatamente um dos dois
```

Os DTOs internos de cada seção carregam valores em centavos e estados (`SEM_TETO`, `PASSOU`, `COM_ATRASO`…), nunca texto pronto, exceto o aviso de fatura. O nome concreto de cada `SecaoDto<T>` no OpenAPI é fixado no plano. Sem sessão: `401`; erro do endpoint inteiro: Problem Details.

## Tela

**Dados:**
- `src/api/painel.ts` → `useVisaoGeral()`, chave `["visao-geral"]`, `refetchOnMount: "always"`.
- Lançamento rápido, conciliação, diálogos de meta e mutações do orçamento também invalidam `["visao-geral"]`.

**Layout** (`Main.dc.html` com os tokens atuais):
- **Cabeçalho:** "Novembro de 2026 · dia 5 de 30", "Bom dia/Boa tarde/Boa noite, {apelido}" (antes de 12:00 / de 12:00 a 17:59 / a partir de 18:00) e o botão "+ Novo lançamento".
- **Cartões:** os 4 na `grade-kpi`, "Ainda dá para gastar" em destaque.
- **Avisos de fatura:** uma faixa por cartão.
- **Linha 1:** Patrimônio em 12 meses (largo) e Metas ("Ver todas").
- **Linha 2:** Orçamento do mês ("Ajustar"), Próximos vencimentos e Últimos lançamentos ("Ver todos").
- **Abaixo de 900 px:** uma coluna.

**Gráfico:**
- SVG à mão (linha e área), sem biblioteca nova; mês `null` fica sem ponto.
- `role="img"` e `aria-label` só com o resumo: "de R$ X em dez/2025 a R$ Y em nov/2026" (primeiro e último mês com dado).
- Logo abaixo, `<details>` "Ver valores por mês" com uma tabela (Mês, Patrimônio) dos 12 meses; mês sem dado mostra "—".

**Estados:**
- carregando: esqueleto por cartão;
- seção com `Erro`: "Não foi possível carregar" e "Tentar de novo" (refaz a consulta); as demais seções continuam;
- falha do endpoint: uma mensagem no lugar da grade; `401` volta ao login;
- vazios: os textos das regras acima.

**Formatação:**
- valores com `num` e `formatarReais`;
- metas e orçamento sem centavos, como no mockup, por uma variante nova que **trunca** para reais (2.499,99 → "2.499", nunca "2.500");
- atraso e "passou do teto" com o token de alerta existente.

`src/lib/painel.ts` concentra a saudação e os rótulos e cores por estado. Nenhuma soma no front.

## Testes

Relógio fixo em 05/11/2026; dados sintéticos.

**Arquitetura** (`RegrasDeArquitetura`):
- `Painel` nos dois testes de módulo existentes (só expõe `Contracts` e `PainelModule`; só depende de `Contracts` de outro módulo);
- `Painel_nao_referencia_EF_Npgsql_nem_DbContext`: o assembly não referencia `Microsoft.EntityFrameworkCore*` nem `Npgsql*` e não tem tipo que herde de `DbContext`;
- prova da falha: o mesmo verificador, rodado contra o assembly de `Lancamentos`, acusa.

**Unitários** (`RegrasDoPainelTests`):
- `Restante_por_dia_conta_hoje_ate_o_fim_do_mes` (3.164,00 no dia 5 de 30 → 26 dias → R$ 121);
- `Passou_do_teto_nao_tem_valor_por_dia`;
- `Sem_teto_devolve_sem_teto`;
- `Percentual_do_teto_arredonda_para_baixo`;
- `Variacao_do_patrimonio_sem_percentual_quando_mes_anterior_e_zero_negativo_ou_nulo`;
- `Aviso_de_fatura_so_ate_5_dias_do_fechamento` (aparece no 5º dia, some no 6º);
- `Saldo_das_contas_lista_3_ou_as_2_maiores_e_mais_N`;
- `Orcamento_ordena_por_percentual_e_corta_em_6`;
- `Vencimentos_ordena_e_corta_em_5`.

**Integração** (Postgres real, dados pela API):
- faturas:
  - `Compra_em_10x_so_conta_a_parcela_da_fatura_fechada`;
  - `Fatura_anterior_nao_paga_aparece_como_atraso`;
  - `Pagamento_parcial_abate_o_restante`;
  - `Fatura_paga_some_dos_vencimentos`;
  - `Parcela_com_data_passada_e_fatura_aberta_nao_entra_na_fechada`;
- patrimônio por mês:
  - `Conta_nao_entra_antes_do_saldo_inicial`;
  - `Mes_sem_nenhuma_conta_e_nulo`;
  - `Cartao_entra_negativo_e_parcela_futura_nao`;
  - `Conta_arquivada_continua_no_patrimonio`;
- últimos: `Ultimos_vem_por_data_e_criacao_e_respeitam_o_casal` (com um segundo casal que não aparece);
- reaproveitados: `IConsultaDoOrcamento` e `IConsultaDeMetas` devolvem o mesmo que `GET /orcamento` e `GET /metas`;
- endpoint:
  - `Visao_geral_de_novembro_confere_numero_a_numero`;
  - `Casal_sem_nada_devolve_as_secoes_vazias`;
  - `Secao_com_falha_devolve_erro_so_nela_e_loga_tipo_sem_valores` (contrato substituto lança exceção com uma descrição sintética na mensagem; o log tem seção, tipo e correlação e não tem a descrição);
  - `Sem_sessao_responde_401`;
  - `Resposta_tem_no_store`.

**Front (Vitest):**
- truncamento: 2.499,99 → "2.499";
- saudação: 11:59 "Bom dia", 12:00 "Boa tarde", 18:00 "Boa noite";
- rótulo e cor por situação da meta e por estado da fatura (com atraso, aberta, fechada);
- resumo do `aria-label` com meses nulos no começo.

**No navegador** (ambiente descartável, como nas metas):
- Cenário de novembro: conta conjunta, dinheiro, poupança, investimento, cartão com compra em 10x, fatura anterior não paga, tetos, duas metas, lançamentos de setembro e outubro.
- Cada número da tela confere com uma conta feita à parte, registrada no PR.
- Também: seção com erro (pelo caso do teste), 900 px, o `<details>` e o "+ Novo lançamento" atualizando a tela.

## Critério de pronto

- Suíte, build sem avisos, `npm run check` e teste de arquitetura verdes; a tabela do cenário confere número a número.
- O PR marca a Fase 1 como **"Código concluído"** no `docs/SPEC.md`, não "Concluída".
- A Fase 1 só fecha depois que o casal usar o sistema num mês real e conferir o fechamento contra o extrato do Itaú; isso fica no SPEC como próximo passo.

## Entrega

- Branch `fase-1/dashboard`, a partir de `main`. Sem migração.
- Primeiro commit: esta spec e o `docs/SPEC.md` com o PR #9, as regras de metas, o módulo `Painel`, a pendência das dívidas fora do cartão e o próximo passo do mês real.
- No PR final:
  - Fase 1 "Código concluído";
  - linha do PR em "Entregue";
  - regras do dashboard em "Regras de negócio já decididas" (restante por dia, patrimônio e histórico, restante e atraso da fatura);
  - tarefa do dashboard marcada.
