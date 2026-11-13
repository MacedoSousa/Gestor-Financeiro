---
paths:
  - "android/**"
  - ".github/workflows/android*.yml"
---

# App Android

App nativo (Kotlin + Jetpack Compose, Material 3) que é só um cliente da API `/api/v1`. O "PO do app" é o CEO atuando nesta frente (ADR-012); não há papel novo. Valem o `CLAUDE.md` e o fluxo do `AGENTS.md` (PR em `--draft`, `gh pr view` antes de mandar commit, merge só com o sinal do CEO).

## Escopo
- Trabalho do app fica em `android/` e `.github/workflows/android*.yml`. Mudança na API (origem WebAuthn, `assetlinks.json`, contrato) é PR de servidor próprio, com aprovação do CEO.

## Nenhuma regra de negócio no aparelho
- Orçamento, faturas, metas, "ainda dá para gastar" e tudo do `FinanceEngine` vêm prontos da API. Formatar moeda e data em pt-BR, sim; calcular saldo, teto, fatura, meta ou projeção, nunca.
- Contas ligadas ao banco não aceitam lançamento manual (opção B da Fase 2).

## Cliente da API
- O cliente HTTP é gerado do contrato OpenAPI (regra 8 do `CLAUDE.md`). Nunca editar o código gerado à mão; se falta algo no contrato, é mudança de servidor.

## Login e sessão
- Celular novo entra pelo código de 6 dígitos; dia a dia por passkey (`androidx.credentials`); TOTP como alternativa; biometria (`BIOMETRIC_STRONG` ou `DEVICE_CREDENTIAL`) só destrava localmente. Nada de senha no app.
- Sessão e CSRF: seguir a ADR-011 (`docs/adr/0011-sessao-em-cookie.md`). Como o app guarda e envia a sessão, e o valor do cabeçalho no app, ficam para o spec da E1. Até lá, nada de `EncryptedSharedPreferences` (descontinuado).
- Logout limpa tudo o que o app guardou.

## Vazamento
- `FLAG_SECURE` em toda tela que mostra valores. Nada financeiro em disco no MVP.
- Só HTTPS; cleartext desligado em release (`network_security_config` sem `cleartextTrafficPermitted="true"`). Sem pinning de certificado folha.
- `android:allowBackup="false"`, ou regras de backup que excluem a sessão.
- Nunca logar valor, CPF, cookie ou token (regra 5). `HttpLoggingInterceptor` só no build debug, nunca em `BODY`/`HEADERS` sem `redactHeader("Cookie")` e `redactHeader("Set-Cookie")`. Nada de `printStackTrace`.
- Keystore, senhas de assinatura, `keystore.properties`, `~/.android` e `~/.gradle/gradle.properties` não são lidos, impressos nem commitados por agentes; a assinatura de release só acontece no CI, com GitHub Secrets. As guardas (`scripts/guardas/regras.sh`) barram isso na edição, no Bash, no pre-commit e no CI.

## Qualidade
- Camadas: ui (Compose) → ViewModel (`StateFlow`, estado imutável, eventos únicos) → repositório → cliente gerado. Hilt; Navigation Compose.
- Toda tela tem carregando, vazio, erro com nova tentativa e sessão expirada que leva ao login sem perder o que foi digitado.
- Layout adaptativo (`WindowSizeClass`): celular é o alvo; o tablet não pode quebrar e é o piso de desempenho.
- Acessibilidade: `contentDescription`, toque mínimo de 48 dp, contraste, TalkBack e escala de fonte.
- Animações com propósito; tokens de duração e curva só em `ui/theme/Motion.kt`; respeitar a escala de animação do sistema (`ANIMATOR_DURATION_SCALE` 0 = sem animação decorativa).
- TDD: ViewModels e repositórios com JUnit + Turbine + MockWebServer; fluxos de login com testes de Compose UI.

## Comandos
- `./gradlew` (ou `gradle`) só com as tarefas liberadas no `.claude/settings.json` e as opções `--offline`, `--console=plain`, `--stacktrace` e `-q`; o hook `guardar-bash.sh` barra o resto (abreviação, tarefa de módulo, `--scan`, `-I`, `-D`, `-p`...).
- Mensagem de commit e corpo de PR ou comentário sempre por arquivo: `git commit -F <arquivo>`, `gh pr create --body-file <arquivo>`, `gh pr comment <n> --body-file <arquivo>`. O hook olha o texto do comando inteiro e barraria um caminho de segredo citado em `-m` ou `--body`.

## Antes do PR
1. Skill `android-verificar` (inclui as guardas e `scripts/verificar.sh`).
2. Agente `revisor-seguranca` **e** agente `android-revisor` no mesmo diff (o segundo complementa o primeiro, não o substitui).
3. Skill `revisao-cruzada`. Nesta frente, a revisão cruzada é feita pela sessão Claude QA (worktree `~/Gestor-Financeiro-qa`), não pelo Antigravity.
4. `gh pr create --draft`; o relato termina com o commit do topo do PR.
