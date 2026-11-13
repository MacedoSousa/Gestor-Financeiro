# App Android (MVP) — design

**Status:** proposta da E1, com as decisões do CEO de 12/11/2026. Aguarda a aprovação do CEO no PR.
**Objetivo:** o casal usa o Gestor Financeiro no celular por um app nativo: entra com passkey, vê a visão geral, o orçamento e as metas com os mesmos números do site e lança uma despesa em até 4 toques. Nenhum número é calculado no aparelho.

**Base:**
- `CLAUDE.md` (regras 1 a 10) e `.claude/rules/android.md`;
- ADR-006 (DuckDNS, porta 443), ADR-011 (sessão em cookie, login, celular novo, bloqueio), ADR-012 (papéis) e ADR-015 (opção B: contas ligadas ao banco não aceitam lançamento manual);
- ADR-016, escrita nesta etapa: app como cliente, origem aceita, keystore e distribuição;
- o prompt de papel do Dev Sênior Mobile e o backlog e a estratégia de testes do CEO, transcritos abaixo.

**Papéis:** o CEO é o PO desta frente. O Dev Sênior Mobile (Claude Code) implementa. A revisão cruzada é feita pela sessão Claude QA (worktree `~/Gestor-Financeiro-qa`).

## Decisões do CEO nesta etapa (12/11)

1. **Celular novo segue a ADR-011, sem mudança no servidor** (opção A). O APP-01 e o APP-02 foram reescritos (ver Backlog).
2. **"Reprovado" sai do APP-01.** O servidor não tem esse estado. A mensagem é "O pedido não foi aprovado a tempo", com "Pedir outro". Recusar no site é melhoria futura: #44.
3. **Aprovadas:**
   - o CPF fica só em memória;
   - a retomada do pedido;
   - a consulta do estado com intervalo crescente.
4. **Acréscimos à máquina de estados:**
   - todo estado com requisição tem SemConexão/ServidorFora, com "Tentar de novo", e mantém o estado de origem;
   - PedidosDemais (429) mostra o tempo de espera e volta a DigitandoCPF;
   - em Travado, um destravamento cancelado ou falho continua em Travado, com "Tentar de novo" e "Sair";
   - Voltar em AguardandoAprovação abandona o pedido.
5. **Estrutura:** um só módulo `app`, com os pacotes `ui`, `viewmodel`, `dados`, `api`, `seguranca` e `movimento`. Um teste de arquitetura (Konsist) roda dentro do `testDebugUnitTest`.
6. **Distribuição:** APK no Release do GitHub (o repositório é público).
   - Nenhum segredo, chave ou dado do casal vai no APK nem nas notas do Release.
   - R8 fica ligado no release.
   - Um APK reempacotado não usa a passkey, por causa do `assetlinks.json`.
7. **Versões e identificação:**
   - `minSdk 30` e `targetSdk 36`;
   - package `br.gestorfinanceiro.app`, e o debug com o sufixo `.debug`;
   - `versionCode = X·10000 + Y·100 + Z` para a tag `app-vX.Y.Z`;
   - cabeçalho `X-Requested-With: gestor-web` (o mesmo do site);
   - User-Agent `GestorFinanceiro-Android/<versão>`.
8. **Assinatura:** a keystore de release nunca fica nesta máquina, e só a `main` é assinada.
   - Na **E5**, o workflow que assina gera o APK da `main` como artefato de curta duração. O aceite é o teste da passkey real no tablet, logo depois do merge.
   - Na **E8**, a tag `app-v*` gera o APK no Release.
9. **429 sem `Retry-After`:** o app mostra um tempo de espera genérico. Mandar o cabeçalho é melhoria futura: #45.
10. **Revisões do spec (12/11, `revisor-seguranca` e `android-revisor`):**
    - **Assinatura (era Bloqueia):**
      - Environment `android-release` com aprovador obrigatório, liberado só para a `main` e as tags `app-v*`;
      - tag `app-v*` protegida;
      - só a variante `release` é assinada;
      - nenhum gatilho de PR nos workflows que assinam;
      - actions fixadas por SHA;
      - guarda no hook do Bash (ver "Assinatura e publicação");
      - PR que edita workflow que assina exige a aprovação do CEO no PR;
      - na ADR-016, a keystore fora da máquina **reduz** o risco, não elimina; a conta de máquina separada para os agentes é pergunta a decidir antes da E5.
    - **Dinheiro não anima,** como no site (`.claude/rules/frontend.md`): os números aparecem prontos. Animam as barras, a entrada das seções e os botões.
    - **Barras:** a razão usado/teto serve só para a largura da barra, que é apresentação. Percentual mostrado como texto vem da API, e os textos vêm de `Situacao`/`Estado`.
    - **Nenhuma CA de desenvolvimento** é instalada no tablet. O certificado vai embutido só no debug, restrito ao host local.
    - **401 no cadastro:** a E3 dá um `type` próprio no Problem Details para "autorização ausente", e o app decide pelo `type`.
    - **O cookie de registro e o id da credencial** ficam guardados cifrados por no máximo 10 min e são apagados ao terminar ou falhar.
    - **Ajustes aceitos:**
      - cliente gerado commitado, com o spike dos centavos como primeira tarefa do plano;
      - Tink falhando fechado;
      - nada em disco, com regra no Konsist;
      - log `HEADERS` só no debug;
      - erro por seção;
      - `assetlinks.json` como o único caminho fora do geobloqueio e do CrowdSec, com teste do JSON;
      - tela do código citando a #44;
      - as demais sugestões.

## Abordagens consideradas

| Decisão | Opções | Escolha e por quê |
|---|---|---|
| Celular novo | **A.** Seguir a ADR-011: o celular mostra o código e o cônjuge aprova no site. **B.** Fluxo novo, com o código vindo do site e a sessão antes da credencial | **A.** O servidor não muda. B abriria uma sessão sem passkey nem TOTP no aparelho e exigiria mudar a ADR-011 |
| Estrutura do código | **A.** Um módulo com pacotes e teste de arquitetura. **B.** Vários módulos Gradle (`core:*`, `feature:*`) | **A.** São 10 histórias para um casal. Compila rápido, a lista positiva do gradle não ganha uma tarefa por módulo, e o Konsist garante a fronteira, como o `ArchitectureTests` faz no backend. O Macrobenchmark da E8 pede um módulo de teste à parte (pergunta 7) |
| Distribuição | **A.** Release do GitHub. **B.** APK servido pelo próprio servidor | **A.** É simples e não precisa de login no celular. O domínio já é público pelo site, e o APK não leva segredo |

## Backlog do MVP

Cada história fecha com todos os critérios conferidos no PR. **APP-01 e APP-02 foram reescritos** na E1 (decisão 1). O texto original fica registrado para comparação.

### APP-01 Ativar celular novo (reescrito)
*Original: "Ativar celular novo pelo código de 6 dígitos (ADR-011): código válido abre sessão e segue para cadastrar passkey; código errado conta para o bloqueio progressivo; expirado pede novo no site; nenhum campo de senha; sessão cifrada com chave do Keystore."*

1. **CPF.** A pessoa digita o CPF, com máscara e conferência dos dígitos verificadores no campo, só para avisar erro de digitação. O app manda `POST /api/v1/auth/dispositivo/pedido`.
   - *Mudou:* o celular não digita código; ele digita o CPF.
2. **Espera da aprovação.** A tela mostra:
   - o **código de 6 dígitos em tamanho grande**, lido pelo TalkBack dígito a dígito;
   - o aviso **"Seu cônjuge deve conferir este código no site antes de aprovar"**. Quem sabe um CPF pode criar um pedido pelo APK público, e o cônjuge só deve aprovar o código que vê nesta tela. Recusar no site é a #44.
   - o **tempo restante** até a validade de 10 min.
3. **Consulta do estado.** O app consulta `GET /api/v1/auth/dispositivo/pedido` em intervalo crescente: 2 s, 4 s, 8 s e depois a cada 15 s, bem abaixo do limite do Caddy (60/min por IP). Para em segundo plano e volta ao reabrir, com uma consulta imediata.
4. **Aprovado.** O app resgata a autorização de registro (`POST …/pedido/resgate`) e segue para o APP-02.
   - *Mudou:* a sessão não abre aqui. Ela só abre quando a passkey ou o TOTP é cadastrado (ADR-011).
   - Um 409 no resgate, depois de um resgate que já deu certo neste aparelho (o processo morreu no meio), tem mensagem própria: "Este pedido já foi usado. Peça de novo." Não usa "não foi aprovado a tempo", que seria falso.
5. **Expirado.** A mensagem é **"O pedido não foi aprovado a tempo"**, com **"Pedir outro"** ali mesmo. Com o app ainda vivo, o pedido novo usa o CPF que está em memória, sem digitar de novo.
   - *Mudou:* não existe "código errado" nem "reprovado" (decisão 2), e o pedido novo sai no próprio celular, não no site.
6. **Pedidos demais (429).** Mostra o tempo de espera e volta a DigitandoCPF. Usa o `Retry-After` quando vier; até a #45, mostra "Aguarde alguns minutos".
7. **App fechado durante a espera.** Se o pedido ainda estiver no prazo, a espera é retomada com o mesmo código e o tempo restante. Senão, recomeça do CPF.
   - O `PedidoCriadoDto` só traz o código, sem a validade. **Decisão do Dev:** o prazo local é de 10 min (`TemposDoLogin.Pedido`), contado a partir do envio pelo relógio de parede, e guardado junto com o pedido. A palavra final é sempre do servidor: um `EXPIRADO` encerra a espera antes do prazo local, e o prazo local vencido leva a PedidoExpirado sem esperar a consulta.
8. **CPF.** Não é guardado no aparelho depois de enviado.
   - Fica só na memória do ViewModel e nunca vai para disco, `SavedStateHandle`, `rememberSaveable`, log ou captura.
   - É apagado da memória quando o pedido é resgatado ou abandonado, ou quando a pessoa sai de PedidoExpirado sem "Pedir outro".
   - O campo não tem aprendizado do teclado nem preenchimento automático (ver "Nada em disco").
9. **Voltar em AguardandoAprovação.** Abandona o pedido no app e apaga os dados guardados dele. No servidor, o pedido expira sozinho.
10. **Sem senha.** Nenhum campo de senha.
11. **Sessão cifrada** com chave do Keystore (ver "Sessão").

### APP-02 Cadastrar passkey (reescrito)
*Original: "Cadastrar passkey (Credential Manager + API existente): cancelar não quebra, oferece de novo em Ajustes; sem bloqueio de tela no aparelho, o app explica o que falta."*

1. Com a autorização de registro, o app pede as opções (`POST /auth/passkey/registro/opcoes`), cria a passkey pelo Credential Manager e a grava (`POST /auth/passkey/registro`). A resposta 204 traz o cookie de sessão: a pessoa está dentro.
   - **Apelido do aparelho:** obrigatório em `NovaPasskeyDto` e `NovoTotpDto`. Vem preenchido com o modelo (`Build.MODEL`, por exemplo "SM-T220"), e a pessoa pode editar.
2. **Cancelar não quebra.** A tela EscolherCredencial oferece **"Tentar de novo"** e **"Usar app autenticador (TOTP)"**.
   - O TOTP passa por `POST /auth/totp/registro` e depois `/auth/totp/confirmacao` com o primeiro código.
   - **Abrir o autenticador:** o app oferece abrir o `otpauth://` no app autenticador do próprio celular, por um seletor de apps que mostra quem vai receber. Declara `<queries>` para o esquema e trata a falta de app com uma mensagem. Mostra também o segredo para digitar à mão.
   - **Copiar o segredo:** usa `ClipDescription.EXTRA_IS_SENSITIVE`. O segredo nunca vai para log nem captura.
   - **Risco aceito:** qualquer app que declare o esquema `otpauth` pode receber o segredo. O seletor mitiga, porque a pessoa vê o destino.
   - *Mudou:* sem sessão, não há "Ajustes" para onde mandar a pessoa.
3. **Respostas do cadastro.** O app decide pelo `type` do Problem Details, nunca pelo `title`. O `type` próprio para "autorização ausente" entra na E3.
   - **Autorização ausente ou vencida** (vale 10 min e só uma vez): volta para DigitandoCPF com a mensagem "O tempo para cadastrar acabou. Peça de novo."
   - **Código TOTP errado ou atestação recusada:** fica na mesma tela, com a mensagem do servidor. A autorização continua valendo.
   - **423 na confirmação do TOTP:** mostra o bloqueio, como no APP-03.
   - **400 (apelido inválido):** mostra o erro no campo.
4. **Processo morto no meio do cadastro** (abrir o autenticador pode fazer o tablet fechar o app). O cookie `__Host-gf_registro` e o `credencialId` do TOTP ficam guardados cifrados, como o pedido, por no máximo 10 min. São apagados quando o cadastro termina ou falha de vez, e ao reabrir o app volta para a mesma etapa.
5. **Passkey depois, em Ajustes.** Quem entrou por TOTP vê "Cadastrar passkey" em Ajustes. O fluxo usa a autorização que o usuário logado já pode emitir (`POST /dispositivos/credenciais/autorizacao`, que pede confirmação recente pelo `/auth/confirmar`).
   - *Mudou:* "oferece de novo em Ajustes" vale para quem já tem sessão.
6. **Sem bloqueio de tela.** Se o aparelho não tem bloqueio de tela (`KeyguardManager.isDeviceSecure` falso), o app explica o que falta ("Para usar o app, ative o bloqueio de tela com PIN, padrão, senha ou digital") e abre os ajustes do sistema. **Decisão do Dev:** sem bloqueio de tela o app não segue, nem para o TOTP, porque o APP-04 depende dele.

### APP-03 Entrar no dia a dia
Passkey primeiro e TOTP como alternativa, na mesma tela. A passkey criada no site funciona no app (mesmo RP ID, ver "Servidor"). Erro de rede, servidor fora e bloqueio progressivo têm mensagens próprias:
- 423: "Conta bloqueada por tentativas erradas. Entre com a passkey ou peça ao seu cônjuge para destravar." A passkey entra mesmo com a conta bloqueada (ADR-011).
- O TOTP pede CPF e código a cada vez, porque é assim que o servidor recebe. O CPF não é guardado.

### APP-04 Destravar
- **Quando trava:**
  - depois de 5 min em segundo plano, contados por relógio monotônico (`elapsedRealtime`) entre `ON_STOP` e `ON_START`;
  - toda vez que o app abre com uma sessão guardada.
- **Antes do primeiro quadro:** a decisão de travar acontece no `ON_START`, antes de desenhar, então nenhum valor aparece por um instante.
- **Bloqueio de tela removido:** se a pessoa tirar o bloqueio de tela com uma sessão guardada, Travado leva a SemBloqueioDeTela.
- **Como destrava:** BIOMETRIC_STRONG ou PIN/padrão (`DEVICE_CREDENTIAL`).
- **Destravou mas a sessão expirou:** com o destravamento certo, se o servidor disser que a sessão expirou, o app leva ao login.
- **Apps recentes:** o conteúdo fica escondido (FLAG_SECURE).
- **Cancelou ou falhou:** continua em Travado, com "Tentar de novo" e "Sair" (decisão 4).

### APP-05 Sair
- Encerra a sessão na API (`POST /auth/sair`) e apaga o cookie, as chaves e o estado local.
- O botão Voltar não mostra telas com valores: a pilha de navegação é zerada.
- Detalhes em "Sessão".

### APP-06 Visão geral
- Tudo vem do módulo Painel (`GET /api/v1/visao-geral`); nenhum cálculo no app. Os números são idênticos aos do site.
- **"Tudo" quer dizer as 10 seções do `VisaoGeralDto`**, com paridade com o site:
  - ainda dá para gastar, saldos e aporte planejado;
  - patrimônio e o gráfico do patrimônio em 12 meses;
  - avisos de fatura, metas, orçamento, vencimentos e últimos lançamentos.
- **O gráfico** é desenhado com Canvas próprio, sem biblioteca, como linha simples com os pontos da API (decisão do Dev). Tem descrição para o TalkBack com o primeiro e o último valor, e segue a regra da escala de animação.
- A fatura vencida fica no topo.
- **Os números aparecem prontos, sem contagem** (decisão 10, igual à regra "Dinheiro não anima" do site). O valor na tela e o lido pelo TalkBack são sempre o real. Animam a entrada das seções, as barras e os botões, sem animação se a escala do sistema for 0.
  - *Mudou:* o backlog pedia "números animados".
- **Estados:** carregando, vazio, erro e sessão expirada para a tela.
- **Erro por seção:** cada seção do `VisaoGeralDto` é um `SecaoDto<T>(Dados, Erro)`. A seção com `Erro` mostra o texto do servidor no lugar dela, e as outras seguem normais.
- **Textos de situação** vêm dos campos da API: `Situacao` do orçamento (COM_TETO, ESGOTADO, PASSOU, SEM_TETO), `Estado` da categoria (NORMAL, PASSOU) e da fatura (VENCIDA, FECHADA, ABERTA), e `Situacao` da meta (CHEGA, ALCANCADA, NAO_CHEGA). O percentual mostrado como texto é o `PercentualUsado` ou o `ProgressoPercentual` da API.

### APP-07 Orçamento do mês
- Teto, usado e restante por categoria vêm da API (`GET /api/v1/orcamento`).
- **Os textos são os mesmos do site, tela a tela, e vêm de `Situacao`/`Estado` quando a API os dá** (decisão 10):
  - no painel, "Teto atingido" (`ESGOTADO`) e "Passou R$ X do teto" (`PASSOU`, com o valor do `RestanteCentavos`), como em `frontend/src/paginas/visao-geral/CartoesDoTopo.tsx`;
  - na tela do orçamento, "R$ X acima do teto", como em `LinhaDoTeto.tsx`.
- **Quando falta `Situacao`:** o `TetoDaCategoriaDto` de `/orcamento` hoje não tem `Situacao`. O plano confere isso no começo. Se faltar, o PR de servidor da E3 acrescenta o campo ao DTO e regera os dois clientes (regra 8); o app não deduz a situação.
- **Barras:** a largura é `usado / teto`, limitada a 100%, igual ao site (`razaoDeUso`). É só apresentação; nenhum percentual calculado aparece como texto.
- **Sem teto** (`TetoCentavos` nulo): não há barra.
- As barras são animadas.

### APP-08 Metas
- Guardado, aporte do mês e "chega em" vêm da API (`GET /api/v1/metas`).
- A barra e o percentual usam o `ProgressoPercentual` da API, e o texto da situação vem da `Situacao`.
- Sem simulador no MVP.

### APP-09 Lançamento rápido
- **Até 4 toques**, com a data de hoje preenchida.
- **Contas:** as ligadas ao banco (`ContaDto.Ligada`) e as arquivadas não aparecem (opção B da Fase 2). O servidor recusa do mesmo jeito, e o app mostra o erro dele.
- **Chave idempotente:** cada lançamento leva uma `ChaveIdempotente` gerada uma vez por rascunho. "Tentar de novo" depois de erro de rede reenvia a mesma chave, e o servidor devolve o lançamento que já existe em vez de duplicar.
- **Erros:** a API valida e o app mostra o erro dela (Problem Details).
- **Depois de lançar:** a Visão geral e o orçamento se atualizam, buscando de novo.
- **Rascunho e sessão expirada:** o rascunho sobrevive à sessão expirada só em memória (decisão do Dev). Se o sistema matar o processo, ele se perde, porque nada financeiro vai para disco.

### APP-10 Instalar e atualizar
- A tag `app-v*`, criada pela pessoa, gera o APK assinado no Release (E8). Na E5, o APK da `main` sai assinado como artefato de curta duração, sempre pelo Environment `android-release`.
- O `android/README.md` publica a impressão SHA-256 do certificado, para conferir o APK baixado com `apksigner verify --print-certs`.
- O runbook manda tirar do navegador a permissão "instalar apps desconhecidos" depois de instalar.
- Atualizar por cima mantém a passkey e a sessão: mesma chave de assinatura e `versionCode` crescente.
- Runbook com o backup da keystore, que nunca fica nesta máquina.

**Fora do MVP:** notificações, widget, offline, conciliação da Fase 2, simulador de metas, iOS, recusar pedido no site (#44) e `Retry-After` (#45).

## Arquitetura

Um só módulo Gradle `app` (decisão 5). Pacote base `br.gestorfinanceiro.app`.

```
br.gestorfinanceiro.app
├── ui/            Compose: telas, componentes, tema (ui/theme), navegação, robôs só em test/
│   └── theme/Motion.kt   tokens de movimento (único lugar com duração e curva)
├── viewmodel/     um ViewModel por tela; StateFlow<EstadoDaTela> imutável; eventos únicos por Channel
├── dados/         repositórios; traduzem o cliente gerado em resultados do app (Sucesso, SemConexao,
│                  ServidorFora, SessaoExpirada, Recusado(problem), Bloqueado, Limite(retryAfter?))
├── api/           cliente gerado do OpenAPI (openApiGenerate; nunca editado à mão) e configuração do
│                  OkHttp: CookieJar cifrado, X-Requested-With, User-Agent, timeouts
├── seguranca/     CofreDeSessao (DataStore + Tink), PasskeyGateway, DestravamentoLocal, Relogio,
│                  FLAG_SECURE, controle do travamento
└── movimento/     escala de animação do sistema (EscalaDeMovimento) e helpers de animação que leem os tokens
```

**Regras do teste de arquitetura** (Konsist, no `testDebugUnitTest`):
- **`ui`:**
  - não importa `api` nem `dados`; fala só com `viewmodel`;
  - é o único lugar com `androidx.compose.*`, fora `movimento`.
- **`viewmodel`:** não importa `api` nem `android.*`, exceto `androidx.lifecycle`.
- **`dados`:** é o único que importa `api`.
- **`api`:** o código gerado só é importado por `dados` e pela configuração do OkHttp.
- **`seguranca`:**
  - não importa `ui`;
  - só ele toca `androidx.credentials`, `androidx.biometric`, Tink e `KeyStore`.
- **Valores e tokens:**
  - fora de `ui/theme`, ninguém cria `AnimationSpec`, `tween`, `spring`, `keyframes` nem `CubicBezierEasing` com valores próprios: só os tokens do `Motion.kt`. Durações que não são animação, como os 2/4/8/15 s da consulta e os 5 min do travamento, ficam livres;
  - nenhum tipo `Double`/`Float` carrega valor em dinheiro: os centavos chegam como `Long` e só a formatação pt-BR os transforma em texto.
- **Nada em disco:**
  - `rememberSaveable`, `rememberTextFieldState` e `SavedStateHandle` não guardam CPF, código, segredo TOTP, apelido, valor nem rascunho; as telas de login e de lançamento não os usam;
  - ninguém usa `okhttp3.Cache`;
  - `HttpLoggingInterceptor` só aparece em `src/debug`.
- **Sem SDK de terceiros:** nenhum SDK de crash, analytics ou anúncio entra no app.

**Peças:**
- **Injeção:** Hilt.
- **Navegação:** Navigation Compose com rotas tipadas.
- **Activity:** uma só, com FLAG_SECURE na janela (vale para todas as telas, inclusive a do código).
  - Diálogos, menus e bottom sheets herdam o FLAG_SECURE (`SecureFlagPolicy.Inherit`), com teste.
  - No API 33 ou mais, também `setRecentsScreenshotEnabled(false)`.
- **Cliente gerado:** openapi-generator, gerador `kotlin` com a biblioteca `jvm-retrofit2` e kotlinx-serialization.
  - **Fica commitado** numa pasta própria, como o cliente do front. A checagem de contrato do CI roda o `openApiGenerate` e um `git status --porcelain` só nessa pasta. A regra 8 vale: mudou o contrato, os dois clientes são regerados no mesmo commit.
  - **Primeira tarefa do plano: o spike dos centavos.** O contrato é OpenAPI 3.1.1, e os campos `*Centavos` saem como `["integer","string"]` com `pattern`. O spike gera o cliente e confere que todo `*Centavos` vira `Long`/`Long?`.
  - **Se não virar:** ou `typeMappings`/`schemaMappings` no Gradle, ou um PR de servidor que publique int64 só como `integer` (regera os dois clientes). O código gerado nunca é editado à mão.
- **Endereço da API:** propriedade de build `GF_API_BASE_URL`. No CI, vem de uma variável do GitHub (não é segredo, mas fica fora do repositório, como o `{$DOMINIO}` do Caddyfile).
- **Formatação:** moeda e data em pt-BR por funções puras, testadas na JVM.
- **Telas grandes:** `WindowSizeClass`. No celular (compacta), uma coluna. No tablet (média ou expandida), a Visão geral e o orçamento em duas colunas e o lançamento como painel lateral. O tablet A7 Lite é o piso de desempenho.

## Máquina de estados do login

```mermaid
stateDiagram-v2
    [*] --> Abertura
    Abertura --> SemBloqueioDeTela: 1º aparelho sem bloqueio de tela
    Abertura --> Travado: 2º sessão guardada
    Abertura --> CadastroGuardado: 3º registro guardado e no prazo
    Abertura --> AguardandoAprovacao: 4º pedido guardado e no prazo
    Abertura --> BoasVindas: 5º nada guardado
    CadastroGuardado --> CadastrandoPasskey: etapa da passkey
    CadastroGuardado --> CadastrandoTotp: etapa do TOTP
    SemBloqueioDeTela --> Abertura: bloqueio ativado

    BoasVindas --> Entrar: "Já uso o app"
    BoasVindas --> DigitandoCPF: "Celular novo"

    DigitandoCPF --> AguardandoAprovacao: POST pedido 200
    DigitandoCPF --> PedidosDemais: 429
    PedidosDemais --> DigitandoCPF: depois do aviso
    AguardandoAprovacao --> Resgatando: APROVADO
    AguardandoAprovacao --> PedidoExpirado: EXPIRADO ou prazo local vencido
    AguardandoAprovacao --> BoasVindas: Voltar (abandona e apaga o pedido)
    PedidoExpirado --> AguardandoAprovacao: "Pedir outro" (CPF em memória)
    PedidoExpirado --> DigitandoCPF: "Pedir outro" (sem CPF em memória)
    Resgatando --> CadastrandoPasskey: 204 (autorização de registro)
    Resgatando --> PedidoExpirado: 409 sem resgate anterior
    Resgatando --> PedidoJaUsado: 409 depois de resgate já feito
    PedidoJaUsado --> DigitandoCPF: "Pedir de novo"

    CadastrandoPasskey --> Dentro: 204 com sessão
    CadastrandoPasskey --> EscolherCredencial: cancelou ou erro do Credential Manager
    CadastrandoPasskey --> CadastrandoPasskey: atestação recusada (mensagem, autorização vale)
    EscolherCredencial --> CadastrandoPasskey: "Tentar de novo"
    EscolherCredencial --> CadastrandoTotp: "Usar app autenticador"
    CadastrandoTotp --> Dentro: confirmação 204 com sessão
    CadastrandoTotp --> CadastrandoTotp: código errado ou 400 (mensagem, autorização vale)
    CadastrandoTotp --> Bloqueado: 423
    CadastrandoPasskey --> DigitandoCPF: type "autorização ausente"
    CadastrandoTotp --> DigitandoCPF: type "autorização ausente"

    Entrar --> Dentro: passkey 204 ou TOTP 204
    Entrar --> Entrar: 401 (mensagem na tela)
    Entrar --> Bloqueado: 423 no TOTP
    Bloqueado --> Entrar: "Entrar com passkey"

    Travado --> ConferindoSessao: destravou
    Travado --> Travado: cancelou ou falhou ("Tentar de novo")
    Travado --> Saindo: "Sair"
    Travado --> SemBloqueioDeTela: bloqueio de tela removido
    ConferindoSessao --> Dentro: GET /auth/eu 200
    ConferindoSessao --> SessaoExpirada: 401

    Dentro --> Travado: 5 min em segundo plano
    Dentro --> SessaoExpirada: qualquer 401
    Dentro --> Saindo: "Sair"
    SessaoExpirada --> Entrar
    Saindo --> BoasVindas: apaga tudo
```

**Regras transversais:**
- **Falhas de rede (decisão 4).** Todo estado que faz requisição (DigitandoCPF, AguardandoAprovacao, Resgatando, CadastrandoPasskey, CadastrandoTotp, Entrar, ConferindoSessao, Saindo e as telas do APP-06 a 09) tem dois subestados:
  - **SemConexão:** sem rede ou timeout;
  - **ServidorFora:** 5xx, 502/503 do Caddy ou TLS recusado.

  Os dois mostram "Tentar de novo" e mantêm o estado de origem: o código continua na tela e o rascunho continua preenchido. Na AguardandoAprovacao, a consulta automática segue tentando no mesmo ritmo e o tempo restante continua correndo.
- **Prioridade na Abertura:** a ordem numerada do diagrama. Sem bloqueio de tela, nada mais é avaliado.
- **Bloqueado depois do cadastro:** volta a Entrar, onde a passkey (se já existir) entra mesmo com a conta bloqueada.
- **Erros de cadastro:** o app decide pelo `type` do Problem Details, não pelo status nem pelo `title` (ver APP-02, item 3).
- **Sessão expirada (APP-04).** Na ConferindoSessao, só o 401 leva a SessaoExpirada. Sem rede, o app fica em SemConexão: não deixa ver valores e não apaga a sessão.
- **Na memória da máquina:** só ids, estados e o CPF da tela de celular novo. Nunca valores.
- **Testes:** a máquina é uma classe pura no `viewmodel`, com as transições testadas na JVM (uma por seta do diagrama).

## Sessão: como é guardada e como é apagada

**O que se guarda:**

| Item | Onde | Quanto tempo |
|---|---|---|
| Cookie `__Host-gf_sessao` | DataStore, cifrado | Até sair ou até o servidor dizer 401 |
| Cookie `__Host-gf_pedido` + código + validade | DataStore, cifrado | Até o pedido sair da espera (aprovado, expirado, abandonado) |
| Cookie `__Host-gf_registro` + etapa + `credencialId` do TOTP | DataStore, cifrado | No máximo 10 min; apagado quando o cadastro termina ou falha de vez |
| CPF | Só memória do ViewModel | Até o pedido ser resgatado ou abandonado, ou até sair de PedidoExpirado sem "Pedir outro" |
| Valores financeiros, rascunho do lançamento | Só memória | Nada vai para disco no MVP |

**Como se cifra:**
- Tink AEAD (AES-256-GCM), com o keyset protegido por uma chave mestra do Android Keystore (`AndroidKeysetManager`, `android-keystore://`). A chave mestra não sai do Keystore.
- **Onde fica cada coisa:**
  - o keyset fica num arquivo de SharedPreferences próprio (`withSharedPref`), cifrado pela chave mestra;
  - os itens ficam no DataStore, cifrados pelo keyset.
- **Falha fechado:** se `isUsingKeystore()` der falso (o Tink teria gravado o keyset em claro), o app apaga esse keyset, não guarda sessão nem pedido e leva ao login. Um teste instrumentado confere que o keyset gravado está cifrado.
- **Versão do Tink:** fixada no catálogo de versões.
- **AAD:** cada valor é cifrado com AAD `nome-do-item|versão`, então um valor não pode ser trocado por outro.
- **Chave mestra:**
  - com `setUnlockedDeviceRequired(true)`, só funciona com o aparelho desbloqueado;
  - não exige autenticação do usuário a cada uso (decisão do Dev, pergunta 4): o APP-04 protege a interface.

**Como se envia:**
- Um `CookieJar` próprio no OkHttp devolve os três cookies só para o host de `GF_API_BASE_URL`, só por HTTPS e só no caminho `/`.
- Toda requisição leva `X-Requested-With: gestor-web` e `User-Agent: GestorFinanceiro-Android/<versionName>`. O cabeçalho só passa na checagem do servidor, e o User-Agent é um rótulo declarado pelo cliente, não prova.
- **Log HTTP:**
  - o `HttpLoggingInterceptor` existe só em `src/debug` (`debugImplementation`), e o release nem compila a classe;
  - nível no máximo `HEADERS`, nunca `BODY`, porque o corpo leva CPF, `otpauth` e valores;
  - `redactHeader("Cookie")` e `redactHeader("Set-Cookie")`;
  - tudo isso com teste.

**Nada em disco (além da tabela):**
- `rememberSaveable`, `rememberTextFieldState` e `SavedStateHandle` não guardam CPF, código, segredo, apelido, valor nem rascunho, porque o estado salvo da instância sobrevive à morte do processo;
- sem `okhttp3.Cache`;
- a regra do Konsist cobre esses pontos, e um teste instrumentado confere `filesDir` e `cacheDir` depois de usar as telas;
- CPF, código TOTP e descrição: sem aprendizado do teclado (`imeOptions` com `IME_FLAG_NO_PERSONALIZED_LEARNING`), sem autocorreção e, no CPF e no TOTP, sem preenchimento automático.

**Como se apaga:**
- **Sair (APP-05):**
  1. `POST /auth/sair`, com o cookie;
  2. com ou sem sucesso, apaga o arquivo do DataStore, o arquivo de SharedPreferences do keyset e a chave mestra do Keystore (pelo alias);
  3. limpa o `CookieJar` em memória e os singletons do Hilt que guardem estado (repositórios com dados em memória);
  4. chama `CredentialManager.clearCredentialState()`;
  5. reinicia a Activity com a pilha zerada.

  Um teste instrumentado confere que, depois de Sair, o diretório de dados do app não tem mais nada da sessão.

  Se o `POST` falhar por rede, o app avisa: "Saímos deste celular. A sessão no servidor expira sozinha em até 7 dias; para encerrá-la agora, remova este aparelho no site."
- **401 em qualquer chamada autenticada:** apaga só o cookie de sessão e leva a SessaoExpirada, mantendo os rascunhos em memória.
- **Pedido abandonado, expirado ou resgatado:** apaga o cookie do pedido, o código e a validade.
- **Backup e transferência:**
  - `android:allowBackup="false"`;
  - `dataExtractionRules` excluem tudo, para backup na nuvem e transferência entre aparelhos;
  - a sessão nunca vai para outro celular.

## Servidor (E3)

**Muda:**
1. **Origem WebAuthn do app.** O `Seguranca:WebAuthn:Origens` ganha `android:apk-key-hash:<SHA-256 do certificado de release, base64url sem padding>`. O hash vem de quem guarda a keystore, não desta máquina.
   - A configuração de produção fica em `/opt` e é aplicada pela pessoa com `sudo`.
   - O teste confirma que `OpcoesDoWebAuthn.EhValida` aceita esse formato, que não é URL `https`. Se não aceitar, a validação muda para aceitar `android:apk-key-hash:` além de URLs absolutas.
   - **A origem do APK de debug nunca entra em produção.**
2. **`/.well-known/assetlinks.json` no Caddy.**
   - **Conteúdo:** só a relação `delegate_permission/common.get_login_creds` para o `br.gestorfinanceiro.app`. Sem `handle_all_urls`, porque o app não abre links do domínio.
   - **Impressão do certificado:** a origem (base64url) e a impressão do `assetlinks.json` (hex com `:`) saem de uma única variável de ambiente com o certificado de release. Um teste confere que as duas formas batem, e o Caddy falha ao subir se a variável estiver vazia, como o `${DOMINIO:?}` do `docker-compose.yml`.
   - **Resposta:** 200, `Content-Type: application/json`, sem redirect e fora das zonas de limite.
   - **Ordem no Caddyfile:** o `handle` desse caminho vem antes do `try_files {path} /index.html`, que de outro jeito devolveria 200 em HTML.
   - **Rede:** é o **único caminho fora do geobloqueio e do CrowdSec** (ADR-006), porque quem o lê são servidores do Google, em geral fora do Brasil.
   - Um APK reempacotado tem outro certificado: o Credential Manager não oferece a passkey a ele, e a origem dele não está na lista.
3. **`type` próprio no Problem Details** para "autorização ausente ou vencida" nos endpoints de cadastro de passkey e TOTP, separado da falha de código ou atestação. Hoje os dois casos respondem 401 com `title` diferente.
4. **`Situacao` no `TetoDaCategoriaDto`**, só se o plano confirmar que falta (APP-07). Regera os dois clientes.
5. **Testes da E3:**
   - a origem web e a do app são aceitas, e uma origem estranha é recusada;
   - o `assetlinks.json` responde 200, com `Content-Type` JSON, sem redirect, e o corpo é o JSON esperado com a impressão certa;
   - os dois `type` do cadastro.

**Não muda:**
- os endpoints e o restante do contrato (só os itens 3 e 4 acima);
- os cookies `__Host-`, os tempos, o bloqueio progressivo e o pedido de celular novo;
- o cabeçalho anti-CSRF (o app manda `gestor-web`);
- o RP ID (o domínio do site, por isso a passkey do site serve no app);
- os limites do Caddy;
- o fato de o servidor não guardar User-Agent (ver perguntas).

**Condição para a passkey funcionar:** o Google precisa ler o `assetlinks.json` pela internet, então a porta 443 tem de estar aberta (ADR-006). Até lá, o app entra só por TOTP, e o teste de ponta a ponta da passkey espera a 443.

## Movimento

Tokens só em `ui/theme/Motion.kt`, sobre as curvas do Material 3:

| Token | Duração | Curva | Uso |
|---|---|---|---|
| `curta` | 100 ms | `standard` (0.2, 0, 0, 1) | toque, troca de cor, estado de botão |
| `media` | 250 ms | `emphasizedDecelerate` (0.05, 0.7, 0.1, 1) | entrar de componente, cartão, aviso |
| `saida` | 200 ms | `emphasizedAccelerate` (0.3, 0, 0.8, 0.15) | sair de componente |
| `tela` | 400 ms | `emphasized` (0.2, 0, 0, 1) | transição entre telas |
| `secao` | 300 ms | `emphasizedDecelerate` | entrada das seções da Visão geral (opacidade e deslocamento curto) |
| `barra` | 500 ms | `emphasizedDecelerate` | barras do orçamento e das metas (APP-07, APP-08), com 40 ms de atraso entre itens, no máximo 6 escalonados |

**Dinheiro não anima** (decisão 10, igual a `.claude/rules/frontend.md`):
- nada de contagem subindo nem número trocando aos poucos;
- o valor na tela e o lido pelo TalkBack são sempre o real;
- animam só barras, entrada de seções, transições e estado de botões.

**Regra da escala de animação:**
- **De onde vem a escala:** `movimento/EscalaDeMovimento` lê `Settings.Global.ANIMATOR_DURATION_SCALE` ao abrir e acompanha mudanças por `ContentObserver`. Tudo passa por ela, sem confiar no comportamento implícito do Compose.
- **Toda duração é `token × escala`.**
- **Escala 0** (inclui o "Remover animações" da acessibilidade):
  - sem animação decorativa;
  - a barra aparece no tamanho final, e a seção aparece já no lugar;
  - transição de tela troca na hora (`snap`);
  - o indicador de carregamento continua, porque é informação, não decoração.
- **Algarismos tabulares nos valores,** para o layout não pular quando um número muda.

## Versões, identificação e build

- **Versões:** `minSdk 30` (Android 11) e `targetSdk 36`. O JDK e o SDK seguem o `android/README.md`.
  - Por que 30: nas APIs 28 e 29, o `BiometricPrompt` não aceita `BIOMETRIC_STRONG or DEVICE_CREDENTIAL` juntos; abaixo de 28, o androidx cai para o Keyguard. A partir da 30, um só prompt cobre biometria forte e PIN/padrão, e o A7 Lite já atende.
- **Package:** `applicationId br.gestorfinanceiro.app`. O debug usa `br.gestorfinanceiro.app.debug`, que pode ficar instalado lado a lado e não tem passkey em produção.
- **Versão do APK:**
  - a tag `app-vX.Y.Z` define `versionName X.Y.Z` e `versionCode X·10000 + Y·100 + Z`;
  - Y e Z ficam entre 0 e 99;
  - o CI recusa tag fora do padrão ou com `versionCode` que não cresce.
- **Release:**
  - R8 ligado (`isMinifyEnabled` e `isShrinkResources`), com regras para o kotlinx-serialization e os modelos gerados;
  - sem `debuggable`;
  - `network_security_config` sem cleartext.
- **Debug:**
  - aponta para a pilha local de desenvolvimento (`GF_API_BASE_URL` de debug);
  - **nenhuma CA é instalada no tablet** (decisão 10). Uma CA de usuário no sistema valeria para o navegador e para os outros apps, e a chave privada dela fica nesta máquina;
  - o certificado da CA local (`tls internal`, separada do Caddy de produção) vai embutido só no `network_security_config` do debug, num `<domain-config>` restrito ao host local, sem `<certificates src="user"/>`. Os cookies `__Host-` exigem HTTPS;
  - **o build de debug falha** se o host de `GF_API_BASE_URL` do debug for igual ao do release ou não for um host local. O debug nunca aponta para o servidor de verdade (dados reais).
- **Testes do manifesto mesclado do release:** `allowBackup="false"`, `dataExtractionRules` presentes, sem `debuggable`, sem cleartext e sem `HttpLoggingInterceptor` no classpath.
- **No APK e nas notas do Release:** nenhum segredo, chave ou dado do casal. As notas listam só as mudanças, sem nomes, valores ou capturas com dados.

## Assinatura e publicação

- **Quem assina:** só o CI, pelo GitHub Environment `android-release`.
  - Aprovador obrigatório: a pessoa.
  - Liberado só para a `main` e as tags `app-v*`.
  - Os segredos (keystore em base64, senha e alias) ficam no Environment, não no repositório.
- **O que se assina:** só a variante `release`, e só a `main` (E5, artefato de curta duração) ou uma tag `app-v*` (E8, Release).
- **Workflows que assinam:**
  - sem gatilho `pull_request` nem `pull_request_target`;
  - actions de terceiros fixadas por SHA;
  - PR que os edita só entra com a aprovação do CEO no PR.
- **No GitHub, aplicado pela pessoa:**
  - um ruleset protege a criação de tags `app-v*`;
  - a `main` tem proteção.
- **Guarda no hook do Bash** (`scripts/guardas/regras.sh`, `checar_trecho_publicacao`, com testes em `testes.sh`). O agente não pode:
  - criar nem empurrar tag `app-v*`, nem usar `git push --tags`, `--follow-tags` ou `--mirror`;
  - rodar `gh release create/upload/edit` nem `gh workflow run`;
  - chamar `gh api` em tag `app-v*`, `pending_deployments` (aprovação), `dispatches`, `environments`, `rulesets` ou proteção.

  Listar e ver continuam livres.
- **Limite conhecido:** os agentes usam a mesma conta do GitHub que aprova o Environment. A keystore fora da máquina e a guarda **reduzem** o risco, não eliminam. Uma conta de máquina separada para os agentes é pergunta a decidir antes da E5 (pergunta 9).
- **Aceite da E5:** logo depois do merge na `main`, o APK assinado da `main` é instalado no tablet e a passkey real é testada.

## Estratégia de testes

Transcrita do CEO:
- **Unidade (JVM):** ViewModels, repositórios, formatação pt-BR, erros da API e máquina de estados do login. JUnit + coroutines-test + Turbine.
- **HTTP (JVM):** cliente gerado + repositório, com MockWebServer e fixtures do contrato (200, 401, 409, 429, sessão expirada, cookie).
- **Contrato (CI):** `openApiGenerate` + `git diff` só na pasta do cliente gerado.
- **Interface (JVM):** padrão Robot com Compose UI Test + Robolectric. Cada tela nova vem com o robô dela.
- **Captura (JVM):** Roborazzi em celular e tablet, claro e escuro, fonte grande e animação desligada.
- **Instrumentado:** `connectedDebugAndroidTest` no emulador `gf-pixel-api36` e no tablet A7 Lite (Keystore real, FLAG_SECURE, PIN).
- **Servidor (E3):** origem web e do app aceitas, origem estranha recusada; `assetlinks.json` com 200, JSON e sem redirect.
- **Desempenho (E8):** Macrobenchmark de início frio e rolagem no tablet.
- **Manual:** passkey de ponta a ponta, celular novo, TalkBack e escala de fonte (sessão Claude QA).
- **Regras:**
  - PasskeyGateway e DestravamentoLocal ficam atrás de interfaces, com versões falsas nos testes;
  - nenhum dado real em fixture, captura ou log;
  - Kover com meta de 80% em ViewModel e repositório;
  - teste vermelho bloqueia o merge.

**Detalhes do Dev:**

| Camada | O que se testa | Como |
|---|---|---|
| `viewmodel` | Máquina de estados (uma transição por seta), estados de tela, eventos únicos, ritmo da consulta (2, 4, 8, 15 s), travamento aos 5 min | `Relogio` falso e `TestDispatcher` com tempo virtual; Turbine no `StateFlow` |
| `dados` | Tradução de cada resposta em resultado do app: 200, 204 com `Set-Cookie`, 401 de cada `type` (autorização ausente, falha de código ou atestação), 409, 423, 429 com e sem `Retry-After`, 5xx, timeout, Problem Details e seção do painel com `Erro` | MockWebServer com fixtures JSON geradas a partir de exemplos do contrato; CPFs de gerador válido; nomes e valores inventados |
| Disco e manifesto | Nada de CPF, valor ou rascunho em `filesDir`/`cacheDir` depois de usar as telas; keyset cifrado (`isUsingKeystore`); nada da sessão depois de Sair; manifesto mesclado do release | Instrumentado e teste do manifesto |
| `api` + `CookieJar` | Cookie só para o host certo e só por HTTPS; `X-Requested-With` e User-Agent em toda requisição; nenhum cookie no log; log no máximo `HEADERS` | MockWebServer com TLS de teste |
| `seguranca` | O CofreDeSessao cifra e decifra, recusa AAD trocado e some depois de Sair | Na JVM com uma chave de teste; com o Keystore real no instrumentado |
| `ui` | Fluxos de login, Visão geral, orçamento, metas e lançamento | Robôs: cada tela tem `XRobot`, com ações (`digitarCpf`, `tocarPedirOutro`) e conferências (`confereCodigo`, `confereMensagem`); o teste lê como roteiro |
| Captura | As telas principais em celular e tablet, claro e escuro, fonte 200% e escala 0 | Roborazzi; as imagens de referência só têm dados sintéticos |
| Arquitetura | As regras de pacote acima | Konsist dentro do `testDebugUnitTest` |

**Interfaces falsas:**
- **`PasskeyGateway`:**
  - `criar(opcoesJson)` e `obter(opcoesJson)` devolvem um `ResultadoDePasskey`: Sucesso(respostaJson), Cancelado, SemBloqueioDeTela, SemPasskeyDisponivel ou Erro;
  - a versão falsa é programável por teste;
  - a real usa o `CredentialManager` e converte as opções do servidor para o JSON WebAuthn que ele espera.
- **`DestravamentoLocal`:**
  - `disponibilidade()` devolve Disponivel, SemBloqueioDeTela ou SemHardware;
  - `destravar()` devolve Destravado, Cancelado ou Falhou;
  - a falsa é programável;
  - a real usa o `BiometricPrompt` com `BIOMETRIC_STRONG or DEVICE_CREDENTIAL`.
- **`Relogio`:** tempo monotônico e de parede, para os 5 min e o tempo restante do pedido.

## Etapas que já têm dono

A ordem completa fica para o plano (`writing-plans`), que só começa depois da aprovação deste spec.
- **E1:** este spec e a ADR-016.
- **E3:** servidor: origem do app, `assetlinks.json` e os testes de servidor. É a próxima fatia depois desta, na ordem do prompt de papel (ferramentas → spec + ADR → servidor do app). É feita pelo Dev Sênior Mobile num PR de servidor próprio (backend e Caddy), com aprovação do CEO, como manda `.claude/rules/android.md`. O plano do app inclui esse PR.
- **E4 (#43):** `verificar.sh` e o hook `Stop` testam `android/`; wrapper com `distributionSha256Sum`. Precisa estar pronta antes do primeiro commit com código em `android/`.
- **E5:** workflow que assina o APK da `main` no CI, pelo Environment `android-release`, e o deixa como artefato de curta duração. O aceite é o teste da passkey real no tablet, logo depois do merge. Antes da E5, o CEO decide a pergunta 9.
- **Primeira tarefa do plano do app:** o spike dos centavos no cliente gerado (ver "Arquitetura").
- **E8:** tag `app-v*`, Release e Macrobenchmark no tablet.

## Riscos

1. **A passkey depende da 443 aberta.** Sem o Google ler o `assetlinks.json`, o app não usa a passkey. Até lá, só TOTP, e o teste de ponta a ponta da E5 espera a 443.
2. **Trocar a chave de assinatura quebra tudo de uma vez:**
   - **Efeito:**
     - a passkey no app para de funcionar (a origem e o `assetlinks.json` mudam);
     - o Android não aceita atualizar por cima: é preciso desinstalar, o que perde a sessão.
   - **Mitigação:**
     - backup offline da keystore (runbook do APP-10);
     - sem Play App Signing (fora da Play Store).
3. **Perder a keystore** tem o mesmo efeito que trocá-la. O runbook manda guardar duas cópias offline em lugares diferentes, com a senha separada da cópia.
4. **APK público:**
   - quem baixa conhece o domínio e os caminhos da API;
   - não ganha acesso: o login continua sendo passkey, TOTP ou aprovação do cônjuge;
   - o R8 dificulta a leitura, mas não é proteção.
5. **A chave mestra não exige autenticação a cada uso.** Malware com o UID do app ou um aparelho com root pode decifrar o cookie com o aparelho desbloqueado (`setUnlockedDeviceRequired`). Fica aceito no MVP: o Keystore impede copiar a chave para outro aparelho, e remover o dispositivo no site encerra as outras sessões.
6. **Sair sem rede:** a sessão segue válida no servidor por até 7 dias. O app avisa e indica remover o aparelho no site.
7. **Formato das opções da passkey:** o Credential Manager espera o JSON do WebAuthn nível 3. Se os DTOs do servidor não baterem, a conversão fica no `PasskeyGateway`. Se faltar campo no contrato, vira PR de servidor na E3.
8. **A validação de origem do Fido2NetLib** pode recusar o formato `android:apk-key-hash:`. A E3 tem o teste que confirma isso.
9. **Desempenho no A7 Lite:** as animações de barra e de entrada das seções podem travar no tablet. Por isso o escalonamento é limitado a 6 itens e há o Macrobenchmark da E8.
10. **Debug sem passkey:** o build de debug não testa a passkey real. Por isso existe a E5.
11. **Aprovação por engano.**
    - **O cenário:** com o APK público, quem sabe um CPF cria um pedido de celular novo, e o cônjuge pode aprová-lo achando que é do parceiro.
    - **Mitigação:**
      - a tela do código manda conferir os 6 dígitos com a pessoa ao lado;
      - a #44 traz a recusa e o alerta ao casal.
12. **A mesma conta do GitHub para agentes e aprovação.** As condições do Environment e a guarda do hook reduzem o risco, mas uma conta comprometida aprova a própria assinatura. Pergunta 9.
13. **Gerador Kotlin e OpenAPI 3.1:** os centavos podem não virar `Long`. O spike da primeira tarefa do plano decide o caminho, antes de qualquer tela.
14. **Segredo TOTP por `otpauth://`:** outro app que declare o esquema pode recebê-lo. O seletor mostra o destino, e a pessoa pode digitar o segredo à mão.
15. **Fallback do Caddy:** sem o `handle` do `assetlinks.json` antes do `try_files`, o caminho responderia 200 em HTML e a passkey falharia sem aviso. O teste do corpo JSON da E3 cobre isso.

## Perguntas em aberto (para o CEO)

1. **User-Agent e auditoria.** A ADR-011 diz que o servidor não guarda User-Agent, e a auditoria é por trigger no banco. O `GestorFinanceiro-Android/<versão>` aparece hoje só no log de acesso do Caddy. Para a auditoria distinguir app e site, o servidor precisa gravar a origem da sessão (por exemplo, `sessoes.cliente = 'app' | 'web'`), o que muda a ADR-011 e vira trabalho de servidor. O próprio cliente declara o User-Agent, então ele serve de rótulo, não de prova. Fica só no log do Caddy no MVP?
2. **Package definitivo.** `br.gestorfinanceiro.app` não pode mudar depois do primeiro Release sem perder a atualização por cima. Confirma?
3. **Bloqueio de tela obrigatório.** Sem bloqueio de tela, o app não deixa entrar nem pelo TOTP (decisão do Dev, APP-02). Confirma?
4. **Prender a chave ao destravamento.** Exigir autenticação do usuário na chave mestra fecha o risco 5, mas complica o Tink e o fluxo de travar. Fica fora do MVP?
5. **Pilha local de desenvolvimento.** A CA já está decidida: nenhuma CA no tablet, certificado embutido só no debug. Falta decidir onde entra o Caddy local com `tls internal`, separado do de produção: na E4 ou numa etapa própria?
6. **O que são os "4 toques" do lançamento rápido (APP-09).**
   - **O problema:** o `NovoLancamentoDto` exige conta, natureza, valor, data, descrição e categoria, e o servidor recusa descrição vazia. O site (`LancamentoRapido.tsx`) obriga a digitar a descrição.
   - **Proposta:**
     - só despesa no MVP;
     - conta e categoria vêm pré-selecionadas com a última usada, guardada só como id, sem valor nem texto;
     - digitar não conta como toque. Os toques são abrir o lançamento, escolher a categoria, tocar no valor e salvar;
     - a descrição é obrigatória e digitada, como no site, e o contrato não muda;
     - fora do MVP: receita, transferência, parcelas e o aviso "aporte em dobro", que no site é regra no cliente e no app seria regra de negócio no aparelho.
7. **Módulo de benchmark e lista do gradle.**
   - **O problema:** o Macrobenchmark da E8 precisa de um módulo `com.android.test` à parte, e a meta de 80% do Kover precisa do `koverVerify`, que não está na lista positiva.
   - **Proposta:**
     - abrir exceção na decisão 5 só para um módulo `:benchmark` (sem código de produção);
     - a pessoa acrescenta `koverVerify` e as tarefas de benchmark ao `.claude/settings.json` e à `GRADLE_TAREFAS_LIBERADAS` quando a E8 chegar.
8. **Onde rodam os testes instrumentados.**
   - **O problema:** esta máquina não tem KVM, então o emulador `gf-pixel-api36` não roda aqui.
   - **Proposta:**
     - no CI, o `connectedDebugAndroidTest` roda num emulador do runner do GitHub, que tem KVM, e **bloqueia o merge**;
     - no tablet A7 Lite, roda à mão pela depuração USB antes de cada Release, com o resultado no PR;
     - o que só o aparelho real comprova (Keystore de hardware, PIN de verdade) fica no checklist manual da sessão Claude QA.
9. **Conta de máquina separada para os agentes** (a decidir antes da E5).
   - **O problema:** hoje os agentes usam a mesma conta do GitHub que aprova o Environment `android-release`.
   - **Proposta:**
     - uma conta de máquina com escrita só em branches;
     - sem permissão de aprovar o Environment;
     - sem mexer em tags, rulesets, proteção nem nos workflows que assinam;
     - a conta da pessoa aprova e faz o merge.
