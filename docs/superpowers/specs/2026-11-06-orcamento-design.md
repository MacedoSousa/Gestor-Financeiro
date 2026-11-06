# Orçamento (Fase 1, bloco 2) — design

**Status:** aprovado em conversa, seção a seção, em 06/11/2026.
**Objetivo:** o casal sabe quanto ainda pode gastar no mês, por categoria, e para onde vai cada real da receita prevista (Investir, Teto de gastos, Dívidas, Folga). Junto com o bloco 1, permite fechar um mês à mão.

**Base:** `docs/SPEC.md` (módulo Orcamento: "teto por categoria e mês, saldo restante"), `docs/design/telas/Orcamento.dc.html`, regras do bloco 2 no fim de `docs/superpowers/plans/2026-11-05-contas-e-lancamentos.md`, `CLAUDE.md`.

## Escopo

**Entra:**
- teto por categoria e mês, com usado e restante, e "Copiar do mês anterior";
- divisão da renda ("Para onde vai cada real");
- tipo de conta `INVESTIMENTO`;
- estorno como receita numa categoria de despesa;
- papel das categorias (Investimento, Dívidas).

**Fica fora:**
- **o bloco "Dívidas" do canvas** (contratos, parcela X de N): é um bloco próprio. Até ele existir, a fatia Dívidas é o teto da categoria com papel `DIVIDAS`;
- metas e dashboard.

## Decisões

1. **"Usado" calculado na hora por contrato do Lancamentos** (abordagem A).
   - O Orcamento guarda só tetos e receita prevista, e não lê o schema de outro módulo.
   - Alternativas descartadas: projeção por eventos (complexidade sem retorno para dois usuários) e view SQL lida pelo Orcamento (viola a regra 7).
2. **Compra no cartão** conta pela data da compra (`transacoes.data`), cada parcela na própria data.
   - Transferências e pagamento de fatura nunca são gasto.
3. **Estorno** é uma `RECEITA` numa categoria de despesa **sem papel**. Ele abate o usado da categoria no mês da data do estorno. Não há coluna nova.
   - Receita aceita categoria de receita (receita comum) ou de despesa sem papel (estorno).
   - Receita em categoria com papel `INVESTIMENTO` ou `DIVIDAS` → `400` com "Rendimentos e devoluções de investimento ou de dívida entram como receita comum, não como estorno."
   - Despesa continua exigindo categoria de despesa.
4. **Papel da categoria:** coluna `papel` (`NULL`, `INVESTIMENTO` ou `DIVIDAS`) em `lancamentos.categorias`, porque o nome pode ser renomeado.
   - Único por casal entre as ativas.
   - As categorias padrão nascem com papel ("Investimento" → `INVESTIMENTO`, "Dívidas e financiamentos" → `DIVIDAS`); a migração preenche as existentes pelo nome padrão.
   - O papel não se edita pela API e sobrevive a renomear.
5. **Aporte** (usado de Investir):
   - é a perna de entrada de uma transferência numa conta `POUPANCA` ou `INVESTIMENTO` cuja perna de saída está numa conta de **outro** tipo;
   - poupança ↔ investimento (ou entre duas delas) é movimentação interna: não é aporte nem resgate;
   - resgate (saída de poupança ou investimento para outro tipo) não abate o usado; só aparece no extrato.
6. **Aporte em dobro:** o caminho recomendado é a transferência.
   - No lançamento rápido, uma **despesa** na categoria com papel `INVESTIMENTO` numa conta que não seja poupança nem investimento mostra o aviso: "Para guardar dinheiro, prefira Transferência para a conta de investimento. Assim o aporte aparece no saldo dela e não corre o risco de contar duas vezes."
   - O aviso não bloqueia. Não há deduplicação automática.
7. **Teto:**
   - `valor_centavos` de 0 a 100.000.000.000;
   - **teto zero** ("não gastar nada") ≠ **sem teto** (ausência de linha);
   - remover teto é soft delete.
8. **Copiar do mês anterior:**
   - copia os tetos do **último mês anterior a M que tenha algum teto**, só nas categorias **sem linha** em M;
   - nunca sobrescreve e respeita teto zero;
   - repetir não muda nada;
   - a receita prevista não é copiada.
9. **Receita prevista:**
   - digitada por mês, de 0 a 100.000.000.000;
   - sugestão = soma das receitas que não são estorno do mês de calendário anterior.
10. **Meses:** de 2020-01 até 24 meses à frente de hoje (fuso de Brasília). Meses passados podem ser editados; a auditoria registra.

## Modelo de dados

**`V0005__contas_investimento.sql`** (Contas):
- troca o CHECK de `contas.contas.tipo` para incluir `INVESTIMENTO` (`DROP CONSTRAINT` + `ADD CONSTRAINT`; a V0003 não é editada).

**`V0006__categorias_papel.sql`** (Lancamentos):
- `ADD COLUMN papel text NULL CHECK (papel IN ('INVESTIMENTO','DIVIDAS'))` em `lancamentos.categorias`;
- `UPDATE` pelo nome padrão;
- `CREATE UNIQUE INDEX ux_categorias_papel ON lancamentos.categorias (casal_id, papel) WHERE papel IS NOT NULL AND NOT is_deleted`.

**`V0007__categorias_papel_so_despesa.sql`** (Lancamentos):
- `CHECK (papel IS NULL OR tipo = 'DESPESA')` em `lancamentos.categorias`: categoria com papel nunca vira de receita (o serviço também recusa a troca de tipo).

**`V0008__orcamento.sql`** (Orcamento):
- `CREATE SCHEMA orcamento` + `GRANT USAGE ... TO gf_app`.
- `orcamento.tetos`:
  - `id`, `casal_id`, `mes date CHECK (extract(day FROM mes) = 1)`, `categoria_id uuid NOT NULL` (sem FK entre schemas);
  - `valor_centavos bigint NOT NULL CHECK (valor_centavos BETWEEN 0 AND 100000000000)`;
  - colunas padrão;
  - `ux_tetos ON (casal_id, mes, categoria_id) WHERE NOT is_deleted`.
- `orcamento.receitas_previstas`:
  - `id`, `casal_id`, `mes` (mesmo CHECK), `valor_centavos` (mesmo intervalo);
  - colunas padrão;
  - `ux_receitas_previstas ON (casal_id, mes) WHERE NOT is_deleted`.
- `SELECT audit.proteger(...)` nas duas tabelas novas.
- Nada cifrado: só ids e valores.

## Regras de cálculo (mês M)

- **Por categoria de despesa:**
  - `usado = Σ despesas − Σ estornos` com `data` em M; pode ser negativo;
  - `restante = teto − usado` só com teto; negativo = acima do teto.
- **Linhas da tabela:**
  - as categorias de despesa ativas;
  - as arquivadas com teto ou usado ≠ 0 em M, marcadas "(arquivada)".

| Fatia | Teto | Usado |
|---|---|---|
| Investir | teto da categoria `INVESTIMENTO` | usado dela + aportes de M |
| Dívidas | teto da categoria `DIVIDAS` | usado dela |
| Teto de gastos | soma dos tetos das categorias de despesa **sem papel** | soma do usado de todas as categorias de despesa **sem papel**, com ou sem teto |
| Folga | `receita prevista − (Investir + Teto de gastos + Dívidas)` (tetos) | — |

- **Investimento e Dívidas aparecem apenas nas próprias fatias**, nunca no Teto de gastos.
- **Percentuais:** sobre a receita prevista.
- **Sem receita prevista:** não há folga nem percentuais.
- **Folga negativa:** alerta "Os tetos passam da receita em R$ X".

## API (`/api/v1/orcamento`, tag `Orcamento`)

Regras gerais: sessão obrigatória, casal da sessão, Problem Details, `mes` = `AAAA-MM` (padrão: hoje em Brasília).

| Rota | Resposta |
|---|---|
| `GET /orcamento?mes=` | `200 OrcamentoDoMesDto` |
| `PUT /orcamento/tetos/{mes}/{categoriaId}` `{ valorCentavos }` | `204` (cria ou atualiza) |
| `DELETE /orcamento/tetos/{mes}/{categoriaId}` | `204` (soft delete); sem teto → `404` |
| `PUT /orcamento/receita/{mes}` `{ valorCentavos }` | `204` |
| `DELETE /orcamento/receita/{mes}` | `204`; sem receita → `404` |
| `POST /orcamento/{mes}/copia` | `200 { copiados, origem }` (`origem` nulo se não há de onde copiar) |

**`OrcamentoDoMesDto`:**
- `mes`;
- `receitaPrevistaCentavos?`, `receitaSugeridaCentavos`;
- `fatias`:
  - `investir { tetoCentavos?, usadoCentavos, aportesCentavos, percentual? }`;
  - `dividas { tetoCentavos?, usadoCentavos, percentual? }`;
  - `tetoDeGastos { tetoCentavos?, usadoCentavos, percentual? }`;
- `folgaCentavos?`;
- `categorias: [{ categoriaId, nome, papel?, arquivada, tetoCentavos?, usadoCentavos, restanteCentavos? }]`;
- `mesParaCopiar?`.

**Validação:**
- mês fora de 2020-01 até hoje + 24 meses, ou mal formado → `400` no campo `mes`;
- `valorCentavos` < 0 ou > 100.000.000.000 → `400` no campo `valorCentavos`;
- categoria inexistente ou de outro casal → `404`;
- categoria de receita → `400`;
- teto **novo** em categoria arquivada → `400` "Categoria arquivada: escolha outra." (atualizar ou remover é permitido).

**Concorrência:**
- `PUT` e cópia em transação com `pg_advisory_xact_lock` por casal e mês;
- `23505` no índice parcial vira atualização.

**Contratos novos em `Lancamentos.Contracts`** (implementados com `SUM` no banco, sem decifrar):
- `IConsultaDeCategorias.ListarDeDespesaAsync()` → `[CategoriaDeDespesa(Id, Nome, Papel?, Arquivada)]`;
- `IConsultaDeUso`:
  - `UsoPorCategoriaAsync(DateOnly mes)` → `[UsoDaCategoria(CategoriaId, UsadoCentavos)]`;
  - `AportesAsync(DateOnly mes)` → `long`;
  - `ReceitasAsync(DateOnly mes)` → `long`.

## Telas

**`/orcamento`** (base `Orcamento.dc.html`, sem o bloco Dívidas):
- **Cabeçalho:**
  - setas de mês;
  - "Mês de AAAA · receita prevista R$ X" (ou "· sem receita prevista");
  - "Copiar de {mês}" quando há `mesParaCopiar` e categorias sem linha.
- **Aviso no topo**, quando M não tem nenhum teto e há origem: "Este mês ainda não tem tetos." + botão de copiar (o destaque da tela enquanto aparece).
- **Receita prevista:** campo + "Salvar"; sugestão "No mês passado entraram R$ X" + "Usar" (preenche, não salva).
- **"Para onde vai cada real":**
  - barra empilhada (Investir `#2DD4BF`, Teto de gastos `#60A5FA`, Dívidas `#F59E0B`, Folga `#3A4656`);
  - legenda com valor e %, e "usado R$ X de R$ Y" por fatia;
  - em Investir, "aportes R$ A + categoria R$ B";
  - folga negativa: sem segmento de folga e com `role="alert"`;
  - sem receita: "Defina a receita prevista para ver a divisão".
- **"Teto por categoria":**
  - grupos "Investir e dívidas" (as duas com papel; o texto diz que não entram no Teto de gastos) e "Gastos";
  - colunas Categoria, Teto (campo com rótulo "Teto {categoria}" escondido visualmente), Usado (barra + número), Restante.
- **Edição do teto:**
  - salva ao sair do campo ou com Enter; **Esc desfaz a digitação e sai sem salvar**;
  - campo vazio ao sair do foco volta ao valor anterior; remover teto é só pelo botão "Remover teto";
  - **status "Salvando…" / "Salvo" por linha numa região `aria-live="polite"`**;
  - **um erro ao salvar aparece na própria linha e mantém o valor digitado** para corrigir;
  - categoria arquivada sem teto: campo desabilitado.
- **Restante:** negativo em cor de alerta com "acima do teto"; sem teto, "—".
- **Abaixo de 900 px:** a tabela rola dentro do cartão.

**Outras telas:**
- **Lançamento rápido:**
  - seletor Despesa / Receita / **Estorno** / Transferência;
  - Estorno grava `RECEITA` e oferece só categorias de despesa **sem papel**;
  - aviso de aporte em dobro (decisão 6).
- **Editor de lançamento:** estorno aparece como "Estorno" e troca só entre categorias de despesa sem papel.
- **Contas:** tipo "Investimento".

**Regras do front:** nada em `localStorage`, `label` em todo campo, valores com `num` e `formatarReais`, contraste ≥ 4.5:1, valor escrito na legenda (não só cor).

## Testes

**Unitários:**
- `CalculoDoOrcamentoTests`:
  - fatias, percentuais e folga;
  - teto zero ≠ sem teto;
  - Teto de gastos só com categorias sem papel;
  - folga negativa;
  - sem receita;
  - usado negativo.
- `MesDoOrcamentoTests`: 2019-12 ✗, 2020-01 ✓, hoje + 24 ✓, hoje + 25 ✗.

**Integração — Lancamentos:**
- `UsoPorCategoria_subtrai_estorno_e_conta_parcela_pela_data`;
- `Transferencia_e_pagamento_de_fatura_nao_entram_no_uso`;
- aportes:
  - `Aporte_corrente_para_poupanca_conta`;
  - `Aporte_corrente_para_investimento_conta`;
  - `Poupanca_para_investimento_e_movimentacao_interna`;
  - `Resgate_nao_conta_nem_abate`;
- `Receitas_do_mes_nao_incluem_estorno`;
- estorno:
  - `Estorno_receita_em_categoria_de_despesa_sem_papel_e_aceito`;
  - `Receita_em_categoria_com_papel_responde_400` (com a mensagem da decisão 3);
  - `Categoria_de_receita_em_despesa_responde_400` (continua);
- papel:
  - `Categorias_padrao_nascem_com_papel`;
  - `Renomear_mantem_papel`;
  - `Migracao_preenche_papel_pelo_nome`;
  - `Papel_unico_por_casal`.

**Integração — Contas:** `Conta_do_tipo_investimento_e_aceita` (API e CHECK no banco).

**Integração — Orcamento:**
- CRUD:
  - criar, atualizar e remover teto;
  - `Teto_zero_e_diferente_de_sem_teto`;
  - receita prevista;
  - `Mes_passado_pode_ser_editado`;
- cópia:
  - `Copiar_preenche_so_categorias_sem_linha_e_respeita_teto_zero`;
  - `Copiar_usa_o_ultimo_mes_com_teto_mesmo_pulando_meses`;
  - `Copiar_de_novo_nao_muda_nada`;
- fatias: `Aporte_em_Investimento_e_parcela_em_Dividas_nao_alteram_o_Teto_de_gastos`;
- limites:
  - valor 0 e 100.000.000.000 ✓; −1 e 100.000.000.001 → `400` no campo;
  - mês 2019-12 → `400`; 2020-01 ✓; hoje + 24 ✓; hoje + 25 → `400`;
- categorias:
  - de outro casal → `404`;
  - de receita → `400`;
  - arquivada: teto novo → `400`; atualizar ou remover → `204`;
- concorrência: `PUT_simultaneo_no_mesmo_teto_grava_uma_linha` (provar que falha sem o lock);
- isolamento: o uso de outro casal nunca aparece.

**Arquitetura:** o Orcamento referencia só `Lancamentos.Contracts` (provar com um caso que falha).

**Front:**
- Vitest: função pura da barra (fatias, percentuais, folga negativa, sem receita) e conversão do teto digitado (vazio, zero, acima do limite).
- Navegador:
  - larguras 1440, 900 e 390;
  - Esc desfaz;
  - erro na linha mantém o valor (erro forçado por interceptação de rede);
  - aviso de mês sem tetos e cópia;
  - estorno (sem as categorias com papel);
  - aporte por transferência;
  - alerta de folga negativa;
  - contraste.

## Critério de pronto

- **Mês inteiro fechado à mão no navegador:** receita prevista, tetos, despesas, compra parcelada, estorno, aporte por transferência, resgate e pagamento de fatura. Cada número da tela confere com uma conta feita à parte, registrada no PR.
- CI verde e `docs/SPEC.md` atualizado.

## Entrega

Branch `fase-1/orcamento`, um PR, com tarefas em commits separados:
1. Contas: tipo Investimento;
2. Lancamentos: papel, estorno e contratos;
3. Orcamento: migração, domínio e API;
4. Telas.
