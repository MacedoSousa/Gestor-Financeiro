# ADR-016 — App Android: cliente da API, origem da passkey, keystore e distribuição por APK

**Decisão:**
- **Cliente da API.** O app Android nativo (Kotlin + Compose) é só um cliente da API `/api/v1`, com cliente gerado do OpenAPI e as mesmas regras de login e sessão do site (ADR-011).
- **Origem da passkey.** O servidor aceita, além da origem do site, uma única origem de app: `android:apk-key-hash:<SHA-256 do certificado de release>`. O domínio publica o `/.well-known/assetlinks.json` para o package `br.gestorfinanceiro.app`.
- **Keystore.** A keystore de release é gerada e guardada fora desta máquina e só é usada no CI, por um GitHub Environment com aprovador obrigatório.
- **O que se assina.** Só a `main` e as tags `app-vX.Y.Z` são assinadas.
- **Distribuição.** O APK das tags é distribuído pelo Release do GitHub.

**Por quê:**
- **Por que um app nativo.** O casal usa o sistema principalmente no celular. Um app nativo dá passkey pelo Credential Manager, destravamento e travamento nativos, FLAG_SECURE e animações fluidas no tablet que é o piso de desempenho.
- **Por que só cliente.**
  - Mantém uma fonte única para os números: nenhum cálculo no aparelho, como no site (`FinanceEngine`, regra 10).
  - Mantém uma fonte única para as regras de login: o bloqueio, a aprovação do celular novo e os tempos ficam no servidor.
- **Por que a origem pelo certificado.** A origem do app é o hash do certificado de assinatura, e o `assetlinks.json` liga o domínio a esse certificado. Assim, a passkey criada no site serve no app (mesmo RP ID), e um APK reempacotado com outra chave não consegue usá-la.
- **Por que a keystore fora daqui.** Esta máquina é a mesma que roda o sistema e recebe agentes. Tirar a keystore dela **reduz** o risco de um agente ou de um vazamento local assinar um APK que o celular aceitaria como atualização.
  - **Reduz, mas não elimina:** os agentes usam a mesma conta do GitHub que aprova o Environment.
  - Separar essa conta é pergunta em aberto, a decidir antes da E5 (ver Consequências).

**Detalhes:**
- **Complementa a ADR-011.** Num cliente nativo, `HttpOnly` e `SameSite` não protegem nada: quem protege o cookie é o `CookieJar` do app, que o guarda cifrado com Tink AEAD, com o keyset protegido por chave do Android Keystore. Se o keyset não estiver no Keystore, o app falha fechado e não guarda a sessão.
- **Cabeçalhos:**
  - o app manda `X-Requested-With: gestor-web` só para passar na checagem do servidor (que não muda); num app nativo isso não é defesa contra CSRF;
  - o app manda `User-Agent: GestorFinanceiro-Android/<versão>`. O próprio cliente declara esse valor, então ele é rótulo, não prova.
- **Nada financeiro em disco.** `allowBackup` fica desligado, e `dataExtractionRules` excluem tudo.
- **Origem:**
  - a origem do APK de debug nunca entra na configuração de produção; o debug testa a passkey com uma implementação falsa;
  - a origem (base64url) e a impressão do `assetlinks.json` (hex com `:`) saem de uma única variável com o certificado de release, e o Caddy falha ao subir se ela estiver vazia;
  - quem aplica os valores em `/opt` é a pessoa (o agente não opera lá);
  - `/.well-known/assetlinks.json` é o único caminho fora do geobloqueio e do CrowdSec, porque quem o lê são servidores do Google, em geral fora do Brasil.
- **Keystore e assinatura:**
  - gerada fora desta máquina;
  - duas cópias offline em lugares diferentes, com a senha guardada separada das cópias;
  - os segredos (keystore em base64, senha e alias) ficam no GitHub Environment `android-release`, com aprovador obrigatório (a pessoa) e liberado só para a `main` e as tags `app-v*`;
  - um ruleset protege a criação de tags `app-v*`;
  - só a variante `release` (não depurável, sem log HTTP) é assinada;
  - os workflows que assinam não têm gatilho `pull_request` nem `pull_request_target`, e as actions de terceiros são fixadas por SHA;
  - PR que edita workflow que assina só entra com a aprovação do CEO no PR;
  - o hook `guardar-bash.sh` impede o agente de:
    - criar ou empurrar tag `app-v*`;
    - rodar `gh release create/upload/edit` ou `gh workflow run`;
    - chamar a API de deployment, Environment, ruleset ou proteção;
  - runbook no APP-10;
  - sem Play App Signing.
- **Distribuição:**
  - `versionCode = X·10000 + Y·100 + Z`;
  - R8 ligado no release;
  - nenhum segredo, chave ou dado do casal no APK nem nas notas do Release, que é público como o repositório;
  - atualizar por cima exige a mesma chave e um `versionCode` maior;
  - a impressão do certificado fica publicada no `android/README.md`, para conferir o APK com `apksigner verify --print-certs`;
  - o endereço da API entra no build por uma variável do GitHub, fora do repositório, como o `{$DOMINIO}` do Caddyfile. Quem baixa o APK descobre o domínio, que já é público pelo site.
- **E5:** o workflow da E5 assina o APK da `main`, passando pelo Environment, e o deixa como artefato de curta duração. O aceite da passkey real no tablet é feito logo depois do merge, com esse APK.

**Descartado:**
- **WebView com o site ou PWA:** sem biometria nem travamento nativos, com o código da página na mesma origem do cookie e dependente da versão da WebView do aparelho.
- **Token próprio para o app (JWT ou API key):** criaria um segundo caminho de autenticação para manter e auditar. O cookie da ADR-011, guardado cifrado, cumpre o papel.
- **Play Store e Play App Signing:** a Google passaria a guardar a chave e a publicação exigiria conta de desenvolvedor e revisão, sem necessidade para um casal.
- **APK servido pelo próprio servidor:** muda o servidor e a superfície exposta, sem ganho real, porque o domínio já é público.
- **Assinar o release nesta máquina, ou assinar branch que não seja a `main`:** contraria a regra da keystore e permitiria assinar código não revisado.
- **Aceitar a origem do debug em produção:** qualquer pessoa com a chave de debug (que fica em `~/.android`) poderia usar passkeys do casal.

**Consequências:**
- **A passkey depende da porta 443.** O Google precisa ler o `assetlinks.json` pela internet; até a porta 443 abrir (ADR-006), o app entra só por TOTP.
- **Trocar ou perder a keystore quebra o app instalado:** a passkey para de funcionar e não dá mais para atualizar por cima. O backup offline é obrigatório.
- **Trabalho no servidor (E3):**
  - acrescentar a origem do app;
  - servir o `assetlinks.json` no Caddy, com teste do JSON;
  - testar que a validação de origem aceita o formato `android:apk-key-hash:`;
  - dar um `type` próprio no Problem Details para "autorização ausente", separado da falha de código ou atestação.
- **Configuração no GitHub:** o Environment, o ruleset das tags e a proteção da `main` são aplicados pela pessoa.
- **Pergunta em aberto, a decidir antes da E5:** uma conta de máquina separada para os agentes, sem permissão de aprovar o Environment nem de mexer em tags, rulesets e workflows que assinam.
- **Distinguir app e site na auditoria:** o User-Agent do app aparece no log do Caddy, mas o servidor não o guarda (ADR-011). Falta uma decisão (pergunta 1 do spec).

Spec: `docs/superpowers/specs/2026-11-12-app-android-design.md`. Relaciona-se com as ADRs 006, 011, 012 e 015.
