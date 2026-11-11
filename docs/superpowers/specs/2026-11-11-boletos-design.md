# Boletos, contas recorrentes e débito automático (Fase 1, bloco 5) — design

**Status:** rascunho do CEO em 10/11/2026. Seção 1 discutida em conversa; seções 2 a 6 aguardam aprovação da pessoa. Nada é implementado antes da aprovação.
**Objetivo:** o casal vê, no calendário e no painel, tudo o que vence no mês (faturas, boletos, contas recorrentes estimadas e débitos automáticos), sem esquecer nada e sem contar nada duas vezes. Pagar um boleto vira uma despesa normal no orçamento.

**Base:**
- `docs/SPEC.md` (módulo `Boletos`; "chaves fortes (… linha digitável) para mesclar");
- `docs/design/telas/Boletos.dc.html` (calendário, fatura, "Adicionar boleto");
- spec do dashboard (`2026-11-09-dashboard-design.md`), que deixou para este bloco os boletos nos vencimentos, o débito automático e o link "Calendário";
- ADR-007 (DDA copiado à mão do app do Itaú), ADR-014 (despesa de boleto por contrato idempotente);
- `CLAUDE.md`, regras 1 a 9.

**Decisões da pessoa (conversa de 10/11):**
- ao pagar, o sistema marca pago **e cria a despesa** na conta escolhida;
- boleto em aberto **só conta no orçamento quando é pago**; o painel avisa "Boletos a pagar no mês: R$ X";
- o bloco inclui **boletos, contas recorrentes estimadas e débito automático**;
- estimativa = **média dos últimos 3 pagamentos**; antes disso, um valor digitado;
- débito automático: no vencimento fica **"Aguardando débito"**; um clique confirma com o valor já preenchido;
- arquitetura **A** (ADR-014).

## Escopo

**Entra:** módulo `Boletos` (schema `boletos`); cadastro de boleto pela linha digitável (bancário, 47 dígitos; arrecadação/consumo, 48; código de barras de 44 também aceito); contas recorrentes com estimativa; débito automático; pagar, confirmar débito, desfazer pagamento, apagar, pular mês; boletos nos "Próximos vencimentos" e o aviso no "Ainda dá para gastar"; tela `/boletos` (calendário do mês, lista, fatura do cartão, adicionar boleto, contas recorrentes); link "Calendário" no painel.

**Fica fora:** foto do boleto pelo WhatsApp (Fase 3); conciliação com o Open Finance (Fase 2, mas a chave já nasce pronta); Pix copia-e-cola e QR Code; boleto parcelado no cartão; feriados bancários (fim de semana entra); cálculo de juros e multa (o valor pago é digitado); notificações.

## 1. Dados e regras

### Tabelas (schema `boletos`, todas com `casal_id`, soft delete, `audit.proteger` e índices parciais)

**`contas_recorrentes`** — o cadastro da conta que se repete:
`nome_cifrado`; `categoria_id`; `conta_pagamento_id`; `dia_vencimento` (1 a 31; mês sem o dia usa o último); `valor_inicial_centavos` (estimativa sem histórico, > 0); `debito_automatico`; `inicio` (primeiro mês, `date` no dia 1); `arquivada`.

**`vencimentos`** — tudo o que vence de fato:
`descricao_cifrada`; `valor_centavos`; `vencimento` (`date`); `categoria_id`; `conta_pagamento_id`; `debito_automatico`; `conta_recorrente_id` e `mes` (opcionais, juntos); `linha_cifrada` (a linha como foi colada, só dígitos), `codigo_barras_hmac` (índice cego do código de barras de 44 dígitos) e `tipo_linha` (`BANCARIO` ou `ARRECADACAO`); `dispensado`; pagamento: `pago_em`, `valor_pago_centavos`, `transacao_id`.

Regras do banco:
- `UNIQUE (casal_id, codigo_barras_hmac) WHERE NOT is_deleted` — o mesmo boleto não entra duas vezes;
- `UNIQUE (conta_recorrente_id, mes) WHERE NOT is_deleted` — um vencimento por conta recorrente e mês;
- `CHECK (linha_cifrada IS NOT NULL OR conta_recorrente_id IS NOT NULL)` — sem linha só quando vem de uma conta recorrente (energia em débito automático, que ninguém vê como boleto);
- `CHECK (dispensado OR (valor_centavos > 0 AND valor_centavos <= 100000000000))`;
- `CHECK ((pago_em IS NULL) = (valor_pago_centavos IS NULL) AND (pago_em IS NULL) = (transacao_id IS NULL))`.

**Por que o HMAC é do código de barras e não da linha:** a linha digitável e o código de barras de 44 dígitos se convertem um no outro. Na Fase 2, a Pluggy entrega o pagamento de boleto com `paymentData.boletoMetadata.digitableLine` **ou** `barcode`; indexar o código de barras normalizado faz o vínculo funcionar nos dois casos.

### Previsto não é gravado

Para cada conta recorrente ativa e cada mês a partir de `inicio`, se não existe vencimento (pago, em aberto ou dispensado), a consulta monta um **previsto** com "≈". Estimativa: média dos últimos 3 `valor_pago_centavos` daquela conta recorrente, arredondada para o centavo com `MidpointRounding.AwayFromZero`; com menos de 3, a média do que houver; sem nenhum, `valor_inicial_centavos`. O cálculo é de `Boletos.Dominio`, testado sem banco.

### Estados (calculados, nunca gravados; nenhum job muda estado)

| Estado | Quando |
| --- | --- |
| `PREVISTO` | conta recorrente sem vencimento no mês |
| `A_PAGAR` | não pago, sem débito automático, hoje ≤ vencimento efetivo |
| `VENCIDO` | não pago, sem débito automático, hoje > vencimento efetivo |
| `DEBITO_PROGRAMADO` | não pago, com débito automático, hoje < vencimento |
| `AGUARDANDO_DEBITO` | não pago, com débito automático, hoje ≥ vencimento |
| `PAGO` | `pago_em` preenchido |
| `DISPENSADO` | o mês foi pulado (só para conta recorrente) |

**Vencimento efetivo:** se o vencimento cai em sábado ou domingo, vale a segunda-feira seguinte (o boleto pode ser pago no dia útil seguinte sem juros). Feriados ficam fora deste bloco. "Hoje" é `Brasilia.Hoje`, o mesmo relógio do painel (fuso de São Paulo).

Um previsto com débito automático segue a mesma régua (`DEBITO_PROGRAMADO` / `AGUARDANDO_DEBITO`), com o valor estimado.

### Ações

- **Cadastrar boleto:** pela linha digitável (seção 2). Se a linha tem valor, ele vem preenchido e pode ser corrigido; se tem vencimento (só no bancário), idem. Se a conta recorrente for escolhida e já houver previsto no mês, o boleto o substitui (o previsto some porque passa a existir vencimento no mês).
- **Pagar / confirmar débito:** pede a data (padrão hoje, nunca no futuro), o valor pago (padrão o valor do vencimento; muda por juros ou desconto) e a conta (padrão a do vencimento; corrente ou cartão). Cria a despesa pelo contrato do ADR-014 com a categoria do vencimento. Para um previsto, grava primeiro o vencimento do mês e depois paga, na mesma requisição.
- **Desfazer pagamento:** apaga (soft delete) a despesa pelo contrato e limpa o pagamento. A auditoria guarda os dois lados. A regra C7 do Lancamentos vale também aqui: se a despesa foi no cartão e a fatura dela já fechou, o desfazer é recusado com `409` "A fatura deste cartão já fechou; este pagamento não pode mais ser desfeito." O contrato não passa por cima de regra do Lancamentos.
- **Apagar:** só vencimento não pago (soft delete). Pago → `409` "Desfaça o pagamento antes de apagar."
- **Pular mês:** só para previsto: grava um vencimento `dispensado` naquele mês. Desfazer o pulo apaga (soft delete) esse registro e o previsto volta.
- **Arquivar conta recorrente:** para de gerar previstos a partir do mês seguinte; vencimentos já gravados ficam.

### Regras de validação

- **Conta de pagamento:** `CORRENTE` ou `CARTAO`, não arquivada (decisão da pessoa em 11/11: "tudo digital"; o cartão depende do mês). Poupança, dinheiro e investimento ficam de fora.
- **Boleto pago no cartão:** a despesa é uma compra à vista no cartão, com data = data do pagamento; ela entra na fatura pelo mesmo caminho de qualquer compra no cartão (`IConsultaDeContas.VencimentoDaFatura`; o `CalendarioDaFatura` é interno do `Contas`) e no orçamento pela data da compra, como qualquer compra. O boleto fica `PAGO` na hora. Parcelar o boleto no cartão (com tarifa) fica fora deste bloco.
- **Categoria:** de despesa, não arquivada no cadastro e na edição; papel `DIVIDAS` aceito (financiamento por boleto); papel `INVESTIMENTO` recusado (é aporte, não gasto). Se a categoria for arquivada depois do cadastro, pagar é recusado com `409` "A categoria deste boleto foi arquivada. Escolha outra antes de pagar."
- **Despesa de boleto no extrato:** aparece com origem `BOLETO` e **não pode ser editada nem apagada pelo extrato** (`409` "Esta despesa veio de um boleto. Para mudar, desfaça o pagamento em Boletos."). Sem isso, o boleto ficaria "Pago" apontando para uma despesa que não existe.
- **Conta e categoria em uso pelos Boletos:** o `Boletos` registra a sua implementação de `IVerificadorDeUsoDaConta` (o `ServicoDeContas` já consulta todas): conta usada em vencimento não apagado ou em conta recorrente não arquivada não pode ser apagada. Para categoria, nasce em `Lancamentos.Contracts` o `IVerificadorDeUsoDaCategoria`, no mesmo padrão; o `ServicoDeCategorias` passa a consultar todos (a checagem de lançamentos atual vira a implementação do próprio `Lancamentos`), e o `Boletos` registra a dele. Em uso → `409` "Esta conta é usada em boletos ou contas recorrentes." / "Esta categoria é usada em boletos ou contas recorrentes."
- **Concorrência:** pagar um vencimento já pago, ou apagar um que foi pago no meio-tempo → `409`. Repetir a mesma requisição de pagamento (mesma `ChaveIdempotente`) devolve o mesmo resultado.

### Aviso no painel

"Boletos a pagar no mês: R$ X" soma `A_PAGAR`, `VENCIDO` (inclusive de meses anteriores), `DEBITO_PROGRAMADO`, `AGUARDANDO_DEBITO` e `PREVISTO` com vencimento até o fim do mês corrente. Com algum previsto na soma, o valor sai com "≈". *Complemento do CEO sobre a seção 1: `DEBITO_PROGRAMADO` também entra, porque é dinheiro que sai no mês.*

## 2. Linha digitável

Domínio puro em `Boletos.Dominio.LinhaDigitavel`, sem banco, 100% coberto por teste. **O plano começa conferindo esta seção contra o layout FEBRABAN publicado**; diferença vira pergunta ao CEO, não palpite.

**Entrada:** qualquer texto; mantém só os dígitos (pontos, espaços e traços são da formatação). Aceita 47 (bancário), 48 (arrecadação) ou 44 (código de barras de qualquer um dos dois). Saída: `tipo`, `codigoBarras` (44), `valorCentavos?`, `vencimento?`, `banco?` (3 dígitos, só bancário).

**Módulo 10:** da direita para a esquerda, pesos 2, 1, 2, 1…; produto acima de 9 soma os dígitos; DV = (10 − soma mod 10) mod 10.

**Bancário (47 dígitos)** — campos: 1 = d[0..8] + DV d[9]; 2 = d[10..19] + DV d[20]; 3 = d[21..30] + DV d[31]; 4 = DV geral d[32]; 5 = fator d[33..36] + valor d[37..46].
- DVs dos campos 1, 2 e 3 por módulo 10.
- Código de barras = d[0..3] + d[32] + d[33..46] + d[4..8] + d[10..19] + d[21..30].
- DV geral: módulo 11 sobre os 43 dígitos do código de barras sem a posição 4 (índice 4), pesos 2 a 9 da direita para a esquerda, repetindo; DV = 11 − (soma mod 11); se der 0, 10 ou 11, o DV é 1.
- Valor: d[37..46] em centavos; `0000000000` = "valor a definir" (o casal digita).
- Fator de vencimento: `0000` = sem vencimento. Senão, o vencimento é 22/02/2025 + (fator − 1000) dias. O fator passou de 9999 para 1000 em 22/02/2025; boletos do ciclo anterior não existem mais em aberto.

**Arrecadação (48 dígitos)** — 4 blocos de 11 dígitos + 1 DV cada.
- Código de barras = os 4 blocos de 11, sem os DVs. Primeiro dígito `8`; o segundo é o segmento; o terceiro é o identificador de valor.
- Identificador `6` ou `7` → módulo 10; `8` ou `9` → módulo 11 de arrecadação: pesos 2 a 9 da direita para a esquerda; resto 0 ou 1 → DV 0; senão DV = 11 − resto.
- Cada bloco confere com o seu DV pelo módulo do identificador; o DV geral é o índice 3 do código de barras, calculado sobre os outros 43 dígitos pelo mesmo módulo.
- Valor: índices 4 a 14 do código de barras, em centavos, só quando o identificador é `6` ou `8` (valor efetivo). Com `7` ou `9`, é valor de referência: o casal digita.
- Vencimento: a arrecadação não tem campo padronizado; o casal digita (obrigatório).

**Limite do próprio padrão:** no bancário, valor e vencimento ficam no campo 5, protegido só pelo DV geral, e a regra "0, 10 ou 11 vira 1" deixa passar uma parte das trocas de um dígito (medido pelo CEO em 10/11: 346 de 20.000 trocas aleatórias, ~1,7%). Por isso **o formulário sempre mostra o valor e o vencimento extraídos para o casal confirmar** antes de salvar; nada extraído é gravado sem passar pela tela.

**Mensagens** (o primeiro problema encontrado; nunca ecoam a linha):
- tamanho: "A linha digitável tem 47 números (boleto) ou 48 (conta de consumo). Encontramos N."
- DV de campo/bloco: "O trecho N não confere. Confira os números e cole de novo."
- DV geral: "O código não confere. Confira os números e cole de novo."
- arrecadação sem `8` no início: "Conta de consumo começa com 8. Confira os números e cole de novo."

**Testes sem dado real:** os testes geram linhas válidas com um codificador de teste (`montar → analisar` devolve o mesmo) e têm alguns exemplos calculados à mão no próprio teste. Nunca um boleto de verdade em teste, exemplo ou captura de tela.

## 3. Contratos e endpoints

### Contratos entre módulos

- **`Lancamentos.Contracts.IDespesaDeVencimento`** (ADR-014):
  - `DespesaDeVencimentoDto(Guid VencimentoId, Guid ContaId, Guid CategoriaId, DateOnly Data, long ValorCentavos, string Descricao)`. O casal e o usuário vêm do contexto da requisição (`IUsuarioAtual`), como em qualquer lançamento; a descrição vai em claro no DTO e o `Lancamentos` a cifra, como já faz.
  - `RegistrarAsync(DespesaDeVencimentoDto)` → `Guid transacaoId`. Cria despesa com `origem = 'BOLETO'` e `id_externo = <id do vencimento>`. Se já existe despesa não apagada com essa chave, devolve a existente (idempotente). Valida conta e categoria pelas regras da seção 1.
  - `DesfazerAsync(Guid vencimentoId)` → soft delete da despesa daquele vencimento; sem despesa, não faz nada; fatura já fechada → resultado de recusa (o `Boletos` responde `409`, seção 1).
- **`Lancamentos.Contracts.IConsultaDeFaturas`** ganha `DoMesAsync(DateOnly mes)`: as faturas de cada cartão com **vencimento** naquele mês (cartão, apelido, fechamento, vencimento, valor até agora e estado). O `AVencerAsync()` atual continua para o painel. O calendário de `/boletos` usa o método novo.
- **`Boletos.Contracts.IConsultaDeVencimentos`** para o Painel: `DoMesAsync(DateOnly hoje)` → itens com `VencimentoId?`, `ContaRecorrenteId?`, `Descricao`, `Vencimento`, `Estado`, `ValorCentavos`, `Estimado`, mais o total "a pagar no mês" e se ele é estimado.
- Boletos lê `IConsultaDeContas` e `IConsultaDeCategorias` (já existem).

**Origem `BOLETO` no Lancamentos:**
- migração: o `CHECK` da V0004 não tem nome no SQL, então o Postgres o chamou de `transacoes_origem_check`. A migração confere esse nome (o teste de integração lê `pg_constraint`) e faz `ALTER TABLE lancamentos.transacoes DROP CONSTRAINT transacoes_origem_check, ADD CONSTRAINT transacoes_origem_check CHECK (origem IN ('MANUAL', 'OPEN_FINANCE', 'WHATSAPP', 'BOLETO'))`, sem tocar em linha;
- código: o enum `OrigemDaTransacao`, o conversor do `LancamentosDbContext`, o `LancamentoDto.Origem`, o OpenAPI e o filtro `origem` do extrato (`ConsultaDoExtrato`), com o rótulo "Boleto" no front;
- regra: editar ou apagar transação com origem `BOLETO` pelos endpoints de Lancamentos → `409` (seção 1).

### Endpoints (Boletos.Api; DTOs só em `Contracts`; erros em Problem Details; tudo sob o filtro `Casal`)

| Método e rota | O que faz |
| --- | --- |
| `POST /api/v1/linha-digitavel/analise` | Analisa sem gravar; devolve tipo, valor e vencimento para preencher o formulário. `422` com a mensagem da seção 2 |
| `GET /api/v1/vencimentos?mes=AAAA-MM` | Vencimentos e previstos do mês, com estado, mais as faturas com vencimento no mês (`IConsultaDeFaturas.DoMesAsync`) para o calendário |
| `POST /api/v1/vencimentos` | Cadastra boleto (`ChaveIdempotente`, linha, descrição, valor, vencimento, categoria, conta, débito automático, conta recorrente opcional). `409` se o código de barras já existe |
| `PUT /api/v1/vencimentos/{id}` | Edita descrição, valor, vencimento, categoria, conta e débito automático de um vencimento não pago |
| `DELETE /api/v1/vencimentos/{id}` | Soft delete de vencimento não pago |
| `POST /api/v1/vencimentos/{id}/pagamento` | Paga ou confirma débito (`ChaveIdempotente`, data, valor pago, conta) |
| `DELETE /api/v1/vencimentos/{id}/pagamento` | Desfaz o pagamento |
| `POST /api/v1/contas-recorrentes/{id}/meses/{AAAA-MM}/pagamento` | Paga um previsto (grava o vencimento do mês e paga) |
| `POST /api/v1/contas-recorrentes/{id}/meses/{AAAA-MM}/dispensa` | Pula o mês; `DELETE` na mesma rota desfaz |
| `GET`, `POST`, `PUT /api/v1/contas-recorrentes` | Lista, cria e edita (inclusive arquivar) |

`contracts/openapi.json` e `frontend/src/api/schema.d.ts` mudam no mesmo commit de cada endpoint.

## 4. Painel e tela

### Painel (`GET /api/v1/visao-geral`)

- `VencimentoDto` ganha `Tipo` (`FATURA` ou `BOLETO`), `VencimentoId?`, `ContaRecorrenteId?`, `Descricao?`, `Estimado` e `EstadoDoBoleto?`.
  - `ContaId` e `Apelido` continuam obrigatórios: na fatura, o cartão; no boleto, a conta de pagamento (o painel mostra "Energia · Itaú").
  - `Fechamento` e `Estado` passam a ser opcionais e só de fatura; `EstadoDoBoleto` é só de boleto, com os valores da seção 1. Os dois nunca vêm preenchidos juntos (teste no contrato), e o front escolhe o rótulo pelo `Tipo`. Assim `VENCIDA` (fatura) e `VENCIDO` (boleto) nunca dividem o mesmo campo.
- Ordem de "Próximos vencimentos": fatura vencida e boletos `VENCIDO` primeiro (mais antigo antes), fora do corte de 5; depois, por vencimento efetivo, faturas e boletos misturados, com corte de 5. `PAGO` e `DISPENSADO` não aparecem.
- `AindaDaParaGastarDto` ganha `BoletosAPagarCentavos?` e `BoletosAPagarEstimado`; o front mostra "Boletos a pagar no mês: R$ X" (ou "≈ R$ X").
- Seção independente: se a consulta de Boletos falha, só os boletos somem e a seção mostra o erro padrão; as faturas continuam.
- O link "Calendário" leva a `/boletos`.

### Tela `/boletos` (substitui o "Em breve"; referência `Boletos.dc.html`)

- **Calendário do mês** (‹ mês ›): cada dia mostra marcadores de fatura e de boleto; clicar no dia filtra a lista.
- **Lista do mês:** descrição, vencimento, valor (com "≈" quando estimado), estado com rótulo e cor por estado, e a ação do estado (`Pagar`, `Confirmar débito`, `Desfazer`, `Pular mês`).
- **Cartão da fatura:** fatura fechada e aberta do cartão, pelos contratos de faturas que já existem (só leitura aqui).
- **Adicionar boleto:** colar a linha → `analise` preenche valor e vencimento → descrição, categoria, conta, débito automático, conta recorrente (opcional) → salvar.
- **Contas recorrentes:** lista e formulário (nome, categoria, conta, dia, valor inicial, débito automático, início, arquivar).
- Regras do front valem: nenhum número calculado no front, `num` nos valores, `label` em todo campo, foco visível, 800 px sem rolagem lateral, movimento conforme "Movimento e AnimatedWebs".

### Textos

| Onde | Texto |
| --- | --- |
| Estados | "A pagar", "Vencido desde DD/MM", "Débito em DD/MM", "Aguardando débito", "Pago em DD/MM", "≈ estimado", "Mês pulado" |
| Ações | "Pagar", "Confirmar débito", "Desfazer pagamento", "Pular este mês", "Apagar boleto" |
| Painel | "Boletos a pagar no mês: R$ X" / "Boletos a pagar no mês: ≈ R$ X" |
| Vazio | "Nada vence neste mês." · "Nenhuma conta recorrente. Cadastre energia, água, internet…" |
| Duplicado | "Este boleto já foi cadastrado." |
| Confirmação | "Pago. A despesa entrou em <categoria>." |

## 5. Testes

- **Domínio (unitários):** linha digitável (os três formatos, cada DV errado, valor zero, fator `0000`, fator do ciclo novo, formatação com pontos e espaços, `montar → analisar`); estimativa (0, 1, 2, 3 e mais pagamentos; arredondamento); estados (cada linha da tabela, fim de semana, virada de mês, mês com 28–31 dias para `dia_vencimento` 29–31).
- **Integração (Testcontainers):** cadastrar, pagar, desfazer, apagar, pular; idempotência do pagamento (mesma chave, duas vezes, uma despesa); despesa `BOLETO` recusada na edição e no apagar do extrato; unicidade do código de barras por casal (outro casal pode ter o mesmo); `409` de concorrência; filtro `Casal`; triggers de auditoria e bloqueio de DELETE nas tabelas novas; previsto some ao cadastrar o boleto do mês.
- **Arquitetura:** Boletos só usa `Contracts` de outros módulos; migrações novas passam nas guardas.
- **Painel:** ordenação e corte com faturas e boletos; soma do aviso com e sem previsto; seção de boletos com erro não derruba faturas.
- **Front:** formatação de estados e valores em `src/lib/*.test.ts`; QA no navegador e acessibilidade.

## 6. Etapas do plano (uma por PR)

1. Linha digitável (domínio puro e `POST .../analise`).
2. Schema, cadastro, pagar e desfazer (migração `boletos`, migração da origem `BOLETO`, `IDespesaDeVencimento`, proteção no extrato).
3. Contas recorrentes, previsto, estimativa e pular mês.
4. Débito automático (estados e confirmação).
5. Painel (vencimentos e aviso).
6. Tela `/boletos` e link "Calendário".
