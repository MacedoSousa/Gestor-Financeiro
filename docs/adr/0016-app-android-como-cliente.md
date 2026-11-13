# ADR-016 — App Android: cliente da API, origem da passkey, keystore e distribuição por APK

**Decisão:**
- **Cliente da API.** O app Android nativo (Kotlin + Compose) é só um cliente da API `/api/v1`, com cliente gerado do OpenAPI e as mesmas regras de login e sessão do site (ADR-011).
- **Origem da passkey.** O servidor aceita, além da origem do site, uma única origem de app: `android:apk-key-hash:<SHA-256 do certificado de release>`. O domínio publica o `/.well-known/assetlinks.json` para o package `br.gestorfinanceiro.app`.
- **Keystore.** A keystore de release é gerada e guardada fora desta máquina e só é usada no CI, por GitHub Secrets.
- **Distribuição.** O APK assinado é distribuído pelo Release do GitHub, a partir da tag `app-vX.Y.Z`.

**Por quê:**
- **Por que um app nativo.** O casal usa o sistema principalmente no celular. Um app nativo dá passkey pelo Credential Manager, destravamento por biometria, FLAG_SECURE e animações fluidas no tablet que é o piso de desempenho.
- **Por que só cliente.**
  - Mantém uma fonte única para os números: nenhum cálculo no aparelho, como no site (`FinanceEngine`, regra 10).
  - Mantém uma fonte única para as regras de login: o bloqueio, a aprovação do celular novo e os tempos ficam no servidor.
- **Por que a origem pelo certificado.** A origem do app é o hash do certificado de assinatura, e o `assetlinks.json` liga o domínio a esse certificado. Assim, a passkey criada no site serve no app (mesmo RP ID), e um APK reempacotado com outra chave não consegue usá-la.
- **Por que a keystore fora daqui.** Esta máquina é a mesma que roda o sistema e recebe agentes. Tirar a keystore dela elimina o risco de um agente ou um vazamento local assinar um APK que o celular aceitaria como atualização.

**Detalhes:**
- **Sessão no app:**
  - os cookies `__Host-` são guardados cifrados com Tink AEAD, com o keyset protegido por chave do Android Keystore;
  - o app manda `X-Requested-With: gestor-web` (o servidor não muda) e `User-Agent: GestorFinanceiro-Android/<versão>`;
  - nada financeiro vai para disco;
  - `allowBackup` fica desligado.
- **Origem:**
  - a origem do APK de debug nunca entra na configuração de produção; o debug testa a passkey com uma implementação falsa;
  - a passkey real é testada no APK assinado pelo workflow manual da E5;
  - a origem e a impressão do `assetlinks.json` vêm de quem guarda a keystore e são aplicadas pela pessoa em `/opt` (o agente não opera lá).
- **Keystore:**
  - gerada fora desta máquina;
  - duas cópias offline em lugares diferentes, com a senha guardada separada das cópias;
  - os GitHub Secrets recebem a keystore em base64, a senha e o alias;
  - runbook no APP-10;
  - sem Play App Signing.
- **Distribuição:**
  - `versionCode = X·10000 + Y·100 + Z`;
  - R8 ligado no release;
  - nenhum segredo, chave ou dado do casal no APK nem nas notas do Release, que é público como o repositório;
  - atualizar por cima exige a mesma chave e um `versionCode` maior.
  - O endereço da API entra no build por uma variável do GitHub, fora do repositório, como o `{$DOMINIO}` do Caddyfile. Quem baixa o APK descobre o domínio, que já é público pelo site.

**Descartado:**
- **App web (PWA) ou WebView com o site:** sem Credential Manager nativo nem FLAG_SECURE confiável, e a mesma origem do site deixaria o cookie à mercê do JavaScript de qualquer página aberta.
- **Token próprio para o app (JWT ou API key):** criaria um segundo caminho de autenticação para manter e auditar. O cookie da ADR-011, guardado cifrado, cumpre o papel.
- **Play Store e Play App Signing:** a Google passaria a guardar a chave e a publicação exigiria conta de desenvolvedor e revisão, sem necessidade para um casal.
- **APK servido pelo próprio servidor:** muda o servidor e a superfície exposta, sem ganho real, porque o domínio já é público.
- **Assinar o release nesta máquina:** contraria a regra de manter a keystore fora daqui.
- **Aceitar a origem do debug em produção:** qualquer pessoa com a chave de debug (que fica em `~/.android`) poderia usar passkeys do casal.

**Consequências:**
- **A passkey depende da porta 443.** O Google precisa ler o `assetlinks.json` pela internet; até a porta 443 abrir (ADR-006), o app entra só por TOTP.
- **Trocar ou perder a keystore quebra o app instalado:** a passkey para de funcionar e não dá mais para atualizar por cima. O backup offline é obrigatório.
- **Trabalho no servidor (E3):** acrescentar a origem do app, servir o `assetlinks.json` no Caddy e testar que a validação de origem aceita o formato `android:apk-key-hash:`.
- **Distinguir app e site na auditoria:** o User-Agent do app aparece no log do Caddy, mas o servidor não o guarda (ADR-011). Para a auditoria separar app e site, falta uma decisão (pergunta 1 do spec).

Spec: `docs/superpowers/specs/2026-11-12-app-android-design.md`. Relaciona-se com as ADRs 006, 011, 012 e 015.
