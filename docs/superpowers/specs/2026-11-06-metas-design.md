# Metas (Fase 1, bloco 3) — design

**Status:** aprovado em conversa, seção a seção, em 06/11/2026. A decisão marcada **[a revisar]** foi tomada ao escrever esta spec.
**Objetivo:** o casal vê, por meta (casa, carro…), quanto já guardou, quanto aportou no mês e **quando chega** — e simula na tela quanto muda se aportar mais ou menos. Responde ao "quando chega à casa e ao carro" do `docs/SPEC.md`.

**Base:**
- `docs/SPEC.md` (módulo Investimentos: "posições, aportes, metas"; regra Investir);
- `docs/design/telas/Metas.dc.html`;
- spec do orçamento (`2026-11-06-orcamento-design.md`, decisão 5: regra do aporte);
- `CLAUDE.md` (regra 10: números vêm do FinanceEngine).

## Escopo

**Entra:**
- metas com valor alvo, data desejada opcional, aporte planejado, rendimento líquido estimado e uma ou mais contas Poupança/Investimento;
- projeção "chega em", diferença para a data desejada e aporte necessário;
- simulador de aporte;
- tabela "Contas de investimento";
- conciliação de saldo ("Atualizar saldo") com lançamento de "Rendimentos";
- projeto `GestorFinanceiro.Financas`.

**Fica fora:**
- ativos, rentabilidade e liquidez (a "Carteira" do canvas), que dependem do Open Finance;
- CDI/BCB, IR e IOF (Fase 4);
- Gemini;
- dividir uma conta entre metas.

## Decisões

1. **FinanceEngine em projeto próprio** (`backend/src/Financas/GestorFinanceiro.Financas.csproj`):
   - só cálculo puro, sem `ProjectReference` nem pacote, referenciado por Investimentos e Assistente;
   - o SharedKernel fica só com infraestrutura;
   - dinheiro em **centavos `long`**, taxa em **`decimal`** (fração ao mês); os módulos convertem `Dinheiro` na borda;
   - o arquivo sai do Assistente.
2. **Simulador calculado no servidor** (`GET /metas/{id}/simulacao?aporte=`, sem efeito colateral):
   - o front usa uma query do TanStack Query com o aporte na chave;
   - cliques seguidos não travam os botões;
   - a tela mantém o último resultado enquanto a nova resposta chega e só mostra a mais recente.
3. **Uma conta pertence a no máximo uma meta**, garantido por índice único parcial no banco. Só entram contas `POUPANCA` ou `INVESTIMENTO` do casal e não arquivadas; uma conta já vinculada que depois for arquivada continua contando.
4. **Guardado** = soma dos saldos de hoje das contas da meta (mesmo cálculo do `GET /saldos`).
5. **Aporte do mês** = regra da fatia Investir, por conta: entrada vinda de conta de outro tipo. Movimento entre poupança/investimento — inclusive entre contas de metas diferentes — e resgate não contam.
6. **Projeção** parte do **aporte planejado** da meta:
   - no mês corrente entra `máx(0, planejado − aporte do mês)`, porque o já aportado está no guardado;
   - do mês seguinte em diante, o planejado inteiro;
   - aporte no fim de cada mês.
7. **Data desejada** (opcional, precisão de mês):
   - mostra "N meses antes/na data/depois" e o aporte necessário para chegar nela;
   - sem data, a meta mostra só "chega em".
8. **Rendimento líquido estimado (% ao mês)**, por meta, de 0 a 5 com até 4 casas, com a nota "IR e IOF entram na Fase 4".
9. **Conciliação** (`POST /api/v1/contas/{id}/conciliacao`, módulo Lancamentos):
   - o casal informa o saldo do banco;
   - sob o lock do casal, o servidor recalcula o saldo de hoje;
   - se ele difere do saldo exibido, responde `409`;
   - se a diferença é positiva, lança "Rendimentos" na mesma transação;
   - se é zero ou negativa, não lança.
10. **Categoria "Rendimentos"** (receita):
    - padrão para casais novos e inserida nos existentes pela V0009;
    - **[a revisar]** identificada por um **papel `RENDIMENTOS`** (só em categoria de receita), porque o nome pode ser renomeado — mesmo raciocínio do papel do orçamento. Com o papel, ela não pode ser apagada (regra existente para categorias com papel).
11. **Receitas em contas Poupança/Investimento ficam fora de `ReceitasAsync`** (a sugestão da receita prevista do orçamento), para rendimento não inflar a receita.

## Modelo de dados

**`V0009__rendimentos.sql`** (Lancamentos):
- **papéis permitidos:** `DROP CONSTRAINT categorias_papel_check` e `ADD CONSTRAINT categorias_papel_check CHECK (papel IN ('INVESTIMENTO','DIVIDAS','RENDIMENTOS'))`.
- **papel × tipo:** `DROP CONSTRAINT ck_categorias_papel_so_despesa` e `ADD CONSTRAINT ck_categorias_papel_tipo CHECK (papel IS NULL OR (papel IN ('INVESTIMENTO','DIVIDAS') AND tipo = 'DESPESA') OR (papel = 'RENDIMENTOS' AND tipo = 'RECEITA'))`.
- **casais existentes:**
  - quem já tem uma categoria ativa de receita com `lower(nome) = 'rendimentos'` e sem papel ganha `papel = 'RENDIMENTOS'` nela;
  - quem não tem ganha uma categoria nova "Rendimentos" (receita, papel `RENDIMENTOS`).
- **idempotente:** não duplica nada se rodar de novo.
- **guardas no código** (serviço e domínio): categoria com `INVESTIMENTO`/`DIVIDAS` continua de despesa; com `RENDIMENTOS`, continua de receita. A mensagem de troca de tipo vira "Categoria com papel não muda de tipo."

**`V0010__metas.sql`** (Investimentos):
- `CREATE SCHEMA investimentos` + `GRANT USAGE ... TO gf_app`.
- **`investimentos.metas`:**
  - `id`, `casal_id`;
  - `nome text CHECK (length(nome) BETWEEN 1 AND 60)` em claro;
  - `valor_alvo_centavos bigint CHECK (valor_alvo_centavos > 0 AND valor_alvo_centavos <= 100000000000)`;
  - `data_desejada date NULL CHECK (extract(day FROM data_desejada) = 1)`;
  - `aporte_planejado_centavos bigint CHECK (BETWEEN 0 AND 100000000000)`;
  - `taxa_mensal numeric(8,6) CHECK (taxa_mensal BETWEEN 0 AND 0.05)`;
  - colunas padrão;
  - `ux_metas_nome ON (casal_id, lower(nome)) WHERE NOT is_deleted`.
- **`investimentos.metas_contas`:**
  - `id`, `casal_id`, `meta_id uuid NOT NULL REFERENCES investimentos.metas(id)`, `conta_id uuid NOT NULL` (sem FK entre schemas);
  - colunas padrão;
  - **`ux_metas_contas_conta ON (casal_id, conta_id) WHERE NOT is_deleted`**.
- `SELECT audit.proteger(...)` nas duas tabelas.
- Apagar meta = soft delete da meta e dos vínculos na mesma transação.

## Regras de cálculo

Valem para todas as regras: hoje no fuso de Brasília; M = mês corrente.

| Grandeza | Regra |
|---|---|
| Guardado | Σ `SaldosAsync(hoje)` das contas da meta (inclui arquivadas) |
| Progresso | `floor(guardado × 100 / alvo)`, limitado a [0, 100] |
| Aporte do mês | Σ `AportesPorContaAsync(M)` das contas da meta |
| Aporte do 1º mês da projeção | `máx(0, aporte mensal − aporte do mês)` |
| Chega em | `n = MesesAteMeta(guardado, alvo, aporte 1º mês, aporte mensal, taxa)`; chega em = `M + n meses` |
| Diferença | com data: meses de "chega em" até a data desejada (negativo = antes) |
| Aporte necessário | com data e não alcançada: `AporteNecessario(guardado, alvo, meses até a data, taxa, aporte do mês)` = menor P (centavos) tal que o 1º mês contribui `máx(0, P − aporte do mês)` e os demais P; busca binária, arredonda para cima |

**Situações:**
- **`ALCANCADA`:** só quando guardado ≥ alvo. Sem "chega em" nem aporte necessário.
- **`NAO_CHEGA`:** quando o resultado é nulo (sem aporte e sem rendimento, ou mais de 1.200 meses). Texto: "Com esse aporte, a meta não chega em 100 anos."
- **`CHEGA`:** nos demais casos.

**Data desejada no passado** e meta não alcançada: `dataDesejadaPassou = true`, sem aporte necessário.

A **simulação** usa as mesmas regras, com o aporte da consulta no lugar do planejado.

**Nota na tela:** "Aporte do mês conta só dinheiro novo." Rendimento lançado aumenta o guardado, não o aporte.

## FinanceEngine (`GestorFinanceiro.Financas`)

`public static class FinanceEngine`:
- `int? MesesAteMeta(long atualCentavos, long alvoCentavos, long aportePrimeiroMesCentavos, long aporteMensalCentavos, decimal taxaMensal)`. O comportamento atual, quando os dois aportes são iguais, se mantém.
- `long? AporteNecessario(long atualCentavos, long alvoCentavos, int meses, decimal taxaMensal, long jaAportadoNoMesCentavos)`:
  - `0` se já alcançada;
  - `null` se `meses ≤ 0`, ou se nem 100.000.000.000 basta.
- `decimal TaxaDePoupanca(long receitaCentavos, long investidoCentavos)` e `DateOnly MelhorDiaDeCompra(DateOnly fechamento)` mudam só de tipo.
- Cálculo interno em `decimal`.

## API

### Metas (`/api/v1/metas`, tag `Metas`, módulo Investimentos)

Valem para todas as rotas:
- sessão obrigatória, casal da sessão;
- Problem Details, `Cache-Control: no-store`;
- mês em `AAAA-MM`;
- taxa em **percentual ao mês** na API (`rendimentoMensalPercentual`, ex. `0.85`), guardada como fração.

| Rota | Resposta |
|---|---|
| `GET /metas` | `200 PainelDeMetasDto` |
| `POST /metas` | `201 MetaDto` + `Location` |
| `PUT /metas/{id}` | `204`; `contaIds` substitui o conjunto (vínculos que saem: soft delete) |
| `DELETE /metas/{id}` | `204`; soft delete da meta e dos vínculos |
| `GET /metas/{id}/simulacao?aporte=` | `200 ProjecaoDto`, sem efeito colateral |

**Tipos:**
- **`NovaMetaDto`** (`POST` e `PUT`):
  - `nome`, `valorAlvoCentavos`, `dataDesejada?`, `aportePlanejadoCentavos`, `rendimentoMensalPercentual`, `contaIds`;
  - os campos numéricos são `long?`/`decimal?` no DTO, e ausente → `400` no campo (como o `valorCentavos` do orçamento).
- **`PainelDeMetasDto`:** `mes`, `metas: [MetaDto]`, `contas: [ContaDeInvestimentoDto]` (todas as Poupança/Investimento do casal).
- **`MetaDto`:**
  - `id`, `nome`, `valorAlvoCentavos`, `dataDesejada?`, `aportePlanejadoCentavos`, `rendimentoMensalPercentual`;
  - `contas: [{ contaId, apelido, tipo, saldoCentavos, arquivada }]`;
  - `guardadoCentavos`, `progressoPercentual` (int), `aporteDoMesCentavos`, `projecao: ProjecaoDto`.
- **`ProjecaoDto`:**
  - `aporteMensalCentavos`, `situacao` (`ALCANCADA` | `CHEGA` | `NAO_CHEGA`);
  - `mesesAteMeta?`, `chegaEm?`;
  - `diferencaMeses?`, `dataDesejadaPassou`, `aporteNecessarioCentavos?`.
- **`ContaDeInvestimentoDto`:** `contaId`, `apelido`, `instituicao?`, `tipo`, `arquivada`, `saldoCentavos`, `metaId?`, `metaNome?`.

**Validação:**

| Caso | Resposta |
|---|---|
| `nome` 1–60 | `400` |
| `valorAlvoCentavos` 1..100.000.000.000 | `400` |
| `aportePlanejadoCentavos` 0..100.000.000.000 | `400` |
| `rendimentoMensalPercentual` 0..5, até 4 casas | `400` |
| `dataDesejada` a partir de M **quando criada ou alterada** | `400` (no `PUT`, uma data passada que não mudou é aceita) |
| sem contas | `400` `contaIds` "Escolha ao menos uma conta." |
| conta de outro tipo | `400` "Só contas de poupança ou investimento entram numa meta." |
| conta arquivada entrando | `400` "Conta arquivada: escolha outra." (ficar é permitido) |
| conta inexistente ou de outro casal | `404` |
| conta em outra meta | `409` "Esta conta já pertence a outra meta." |
| nome repetido (sem caixa) | `409` "Já existe uma meta com esse nome." |
| meta de outro casal | `404` (inclusive simulação) |
| `aporte` da simulação fora de 0..100.000.000.000 | `400` |

**Concorrência.** Escritas em transação com `pg_advisory_xact_lock` por casal. Um `23505` em `ux_metas_contas_conta` vira o `409` da conta; em `ux_metas_nome`, o `409` do nome.

**Dependências.** Investimentos referencia `Contas.Contracts`, `Lancamentos.Contracts` e `Financas`; DTOs em `Investimentos.Contracts`.

### Conciliação (Lancamentos)

**Rota:** `POST /api/v1/contas/{id}/conciliacao`, corpo `{ saldoInformadoCentavos, saldoExibidoCentavos }`, ambos obrigatórios, de −100.000.000.000 a 100.000.000.000.

**Pré-condições:**
- conta do casal (senão `404`);
- tipo `POUPANCA` ou `INVESTIMENTO` (senão `400` "Só contas de poupança ou investimento são conciliadas aqui.");
- não arquivada (senão `400` "Conta arquivada: escolha outra.").

**Fluxo**, em transação com `pg_advisory_xact_lock` por casal:
1. recalcula o saldo de hoje;
2. se ele difere de `saldoExibidoCentavos` → `409` "O saldo mudou desde que a tela foi aberta. Confira de novo.";
3. calcula `diferenca = informado − saldo de hoje`:
   - **`> 0`:** lança uma `RECEITA` de `diferenca` na conta, categoria com papel `RENDIMENTOS`, data hoje, descrição "Rendimentos (conciliação)", origem `MANUAL`, `id_externo` = `conciliacao:<uuid novo>`;
   - **`= 0` ou `< 0`:** não lança.

**Resposta** `200 ConciliacaoDto(Resultado, DiferencaCentavos, LancamentoDto? Lancamento)`:
- `Resultado` = `LANCADO` | `CONFERE` | `MENOR`;
- a tela mostra "O saldo já confere; nada a lançar." ou "O saldo no banco é menor que o do sistema: pode haver um resgate ou uma transferência ainda não lançados."

**Por que não duplica.** Uma segunda conciliação igual em paralelo espera o lock, encontra o saldo já atualizado e recebe `409`.

### Contratos novos em `Lancamentos.Contracts`

Implementados com soma no banco, sem decifrar:
- `IConsultaDeSaldos.SaldosAsync(DateOnly em)` → `[SaldoDeConta(ContaId, SaldoCentavos)]`, mesmo cálculo do `GET /saldos`.
- `IConsultaDeUso.AportesPorContaAsync(DateOnly mes)` → `[AporteDaConta(ContaId, AporteCentavos)]`, que passa a ser a única implementação da regra; `AportesAsync` vira a soma dela.
- `ReceitasAsync` passa a ignorar receitas em contas `POUPANCA`/`INVESTIMENTO`.
- `PapelDeCategoria.Rendimentos = "RENDIMENTOS"`.

## Telas

**`/metas`** (base `Metas.dc.html`, com a Carteira trocada pelas contas):
- **Cabeçalho:**
  - "Metas e investimentos";
  - subtítulo "Projeções com o rendimento líquido estimado de cada meta · IR e IOF entram na Fase 4";
  - botão "+ Nova meta" (o destaque).
- **Cartão por meta:**
  - nome; "Chega em" `mmm/aaaa` | "Meta alcançada" | "Não chega em 100 anos";
  - **barra com `role="progressbar"`, `aria-valuenow`, `aria-valuemin="0"`, `aria-valuemax="100"`** e o percentual escrito; "R$ X guardados · alvo R$ Y";
  - com data: "Desejada: mar/2029 · N meses antes/depois" ou "na data" ou "já passou", e "Para chegar em mar/2029: R$ Z por mês";
  - "Aporte deste mês: R$ A de R$ P planejados" e "Aporte do mês conta só dinheiro novo.";
  - **simulador:**
    - "Aporte mensal" com − e + (passo R$ 500; limites 0 e R$ 1 bilhão) e "Faltam N meses";
    - fora do planejado: "Simulação: o planejado continua R$ P", "Voltar ao planejado" e "Salvar como planejado" (`PUT`);
    - "chega em" e meses em `aria-live="polite"`, com um "atualizando…" discreto;
  - **contas da meta** com saldo e "Atualizar saldo" por conta; "Editar".
- **"Atualizar saldo"** (`<dialog>`):
  - campo "Saldo no banco hoje" e "Saldo no sistema: R$ S";
  - **prévia** antes de salvar: "Diferença: R$ D, lançada como Rendimentos hoje" | "O saldo já confere; nada a lançar." | a mensagem de saldo menor;
  - "Confirmar" só lança quando a diferença é positiva, e manda o saldo exibido junto;
  - um `409` mostra a mensagem e recarrega o saldo.
- **Nova/Editar meta** (`<dialog>`):
  - campos: nome, valor alvo, data desejada (`<input type="month">`, opcional), aporte planejado, "Rendimento líquido estimado (% ao mês)" com a nota de IR e IOF;
  - contas como caixas de marcar: uma conta em outra meta aparece desabilitada com "(meta X)"; uma arquivada só aparece se já estiver nesta;
  - "Apagar meta" com confirmação no diálogo ("As contas ficam livres; o dinheiro não muda.");
  - um erro mantém os valores digitados.
- **Tabela "Contas de investimento":** Conta, Instituição, Tipo, Meta ("—"), Saldo, e o total "R$ X em poupança e investimento". Abaixo de 900 px, rola dentro do cartão.
- **Estados vazios:**
  - sem contas: "Cadastre uma conta de poupança ou investimento para criar metas.", com link para `/contas`;
  - sem metas: "Crie a primeira meta: a casa, o carro, a reserva de emergência."
- **Regras do front:** nada em `localStorage`, `label` em todo campo, `num` + `formatarReais`, contraste ≥ 4.5:1 nos textos, mês "mar/2029", taxa digitada "0,85".

## Testes

**Financas (unitários):**
- os testes atuais migram;
- `MesesAteMeta` com o 1º mês em três casos: **nada aportado, aporte parcial, aporte completo**; já chegou → 0; nunca chega → nulo; taxa 0; limite de 1.200;
- `AporteNecessario`:
  - ida e volta: com P chega em ≤ meses, com P − 1 centavo não;
  - arredonda para cima;
  - considera o já aportado;
  - `meses ≤ 0` → nulo; já chegou → 0;
- **arquitetura:** `Financas` sem referência a assembly do projeto (provar com caso que falha).

**Investimentos (unitários):** `CalculoDaMeta`
- progresso 99,99% → 99 e limite 100;
- alcançada só com guardado ≥ alvo;
- situações;
- diferença antes, na data e depois;
- data passada; sem data;
- percentual ↔ fração com 4 casas.

**Lancamentos (integração):**
- `IConsultaDeSaldos` = `GET /saldos`;
- `AportesPorConta` separa por conta, e os testes do orçamento continuam verdes;
- `Receita_em_poupanca_ou_investimento_fica_fora_de_ReceitasAsync`;
- "Rendimentos" com papel nasce nos casais novos;
- V0009:
  - insere onde falta;
  - atribui o papel a "rendimentos" (sem caixa) que já existe, sem duplicar;
  - idempotente;
  - CHECK papel × tipo (`23514`);
- categoria com papel `RENDIMENTOS` não vira despesa (`400`) e não é apagada (`409`);
- **conciliação:**
  - lança a diferença positiva;
  - confere (zero) e menor (negativa) não lançam;
  - **tela desatualizada → `409`**;
  - **duas conciliações iguais em paralelo lançam uma vez só** (provar a falha sem o lock);
  - conta de outro tipo `400`, arquivada `400`, de outro casal `404`.

**Investimentos (integração, `MetasTests`):**
- **CRUD:** o `PUT` substitui as contas; apagar libera as contas.
- **Regras de conta e nome:**
  - conta em outra meta `409`;
  - **duas metas em paralelo com a mesma conta**: uma grava, a outra `409` (provar a falha sem lock e índice);
  - tipo errado `400`;
  - arquivada entrando `400`, ficando ok;
  - outro casal `404`;
  - nome repetido sem caixa `409`.
- **Limites:**
  - alvo 0 ✗ / 1 ✓ / 100.000.000.000 ✓ / + 1 ✗;
  - aporte −1 ✗ / 0 ✓;
  - rendimento 0 ✓ / 5 ✓ / 5,0001 ✗ / 0,12345 ✗;
  - campo numérico ausente `400`.
- **Data desejada:** mês corrente ✓; mês anterior ✗; **`Editar_meta_mantendo_data_passada_e_aceito`**; alterar para outra data passada `400`.
- **Painel:**
  - guardado com conta arquivada;
  - aporte do mês por meta;
  - **movimento entre metas não é aporte**;
  - rendimento conciliado aumenta o guardado e não o aporte;
  - isolamento entre casais.
- **Simulação:** sem efeito colateral (nenhuma linha nem evento de auditoria novo); `aporte` inválido `400`; outro casal `404`.

**Front (Vitest):**
- prévia da conciliação (positiva, zero, negativa);
- taxa "0,85" → 0.85, 4 casas, acima de 5 inválido;
- "2029-03" → "mar/2029";
- limites do passo do simulador.

**Navegador:**
- larguras 1440, 900 e 390;
- **cliques rápidos no simulador com respostas fora de ordem** (`page.route` com atraso aleatório): fica a mais recente e os botões não travam;
- "Atualizar saldo" maior, igual, menor e com tela desatualizada (`409`);
- conta desabilitada no formulário quando está em outra meta;
- apagar meta libera a conta;
- `progressbar` com os atributos;
- armazenamento vazio; contraste.

## Critério de pronto

- **Duas metas reais no navegador** (casa e carro, com contas, aportes, um rendimento conciliado e um movimento entre elas). Cada número dos cartões (guardado, progresso, aporte do mês, chega em, meses, diferença, aporte necessário, e o simulador em dois valores) é conferido contra uma conta à parte feita antes de olhar a tela, num script ou planilha independente do código. A comparação vai no PR, número por número.
- CI verde e `docs/SPEC.md` atualizado (Metas marcada, regras novas).

## Entrega

Branch `fase-1/metas`, um PR, com tarefas em commits separados:
1. `Financas` e engine;
2. Lancamentos: contratos, "Rendimentos", V0009 e conciliação;
3. Investimentos: domínio;
4. Investimentos: V0010 e API;
5. tela: painel e simulador;
6. diálogos: meta e "Atualizar saldo";
7. navegador e PR.
