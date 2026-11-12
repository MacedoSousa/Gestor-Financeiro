---
name: android-verificar
description: Verificação do app Android do Gestor Financeiro antes de commit ou PR — cliente OpenAPI em dia, lint, testes, build release e checagens de segurança. Use sempre antes de dizer que uma tarefa em android/ está pronta.
---

# Verificar o app Android

Rode a partir da raiz do repositório e mostre a saída real de cada passo. Se um passo falhar, pare, corrija e rode de novo desde o início. Nunca declare pronto sem todos verdes.

Cada tarefa do Gradle roda num comando próprio, do jeito que está liberado no `.claude/settings.json`. O hook `guardar-bash.sh` só deixa passar essas tarefas e as opções `--offline`, `--console=plain`, `--stacktrace` e `-q` (ver "Comandos" em `.claude/rules/android.md`).

## 1. Cliente da API em dia com o contrato
```bash
cd android && ./gradlew openApiGenerate
git status --porcelain -- android/<pasta do cliente gerado>
```
A pasta do cliente gerado é a definida no `build.gradle.kts` (spec da E1). Saída não vazia = o contrato mudou e o cliente regenerado entra no mesmo commit (regra 8 do `CLAUDE.md`).

## 2. Qualidade e testes
```bash
cd android && ./gradlew lint
cd android && ./gradlew ktlintCheck
cd android && ./gradlew testDebugUnitTest
```
(Se o ktlint ainda não estiver configurado, registre isso no PR.)

## 3. Build release
```bash
cd android && ./gradlew assembleRelease
```
Localmente o build sai sem assinatura; a assinatura só acontece no CI, com GitHub Secrets.

## 4. Checagens de segurança
Só em arquivos rastreados pelo git e, onde pode haver segredo, só com o nome do arquivo (`-l`): nunca imprima a linha.
```bash
git ls-files -z android | xargs -0 -r grep -lE 'storePassword|keyPassword' || true
git ls-files -z android/app/src/main | xargs -0 -r grep -nE 'Log\.(d|i|v|w|e|wtf)\(|println\(|printStackTrace\(|Timber\.' || true
git ls-files -z android | xargs -0 -r grep -nE 'HttpLoggingInterceptor|Level\.(BODY|HEADERS)|redactHeader' || true
git ls-files -z android | xargs -0 -r grep -nE 'usesCleartextTraffic="true"|cleartextTrafficPermitted="true"|allowBackup|EncryptedSharedPreferences' || true
```
Revise cada ocorrência:
- Qualquer arquivo listado na primeira busca é bloqueante: senha de assinatura só em variável de ambiente do CI.
- Log com valor, CPF, cookie ou token, ou `printStackTrace`, é bloqueante.
- `HttpLoggingInterceptor` só no build debug; em `BODY`/`HEADERS` exige `redactHeader("Cookie")` e `redactHeader("Set-Cookie")`.
- Cleartext e `EncryptedSharedPreferences` não podem existir; `allowBackup` tem de ser `false` ou ter regras de backup que excluem a sessão.

## 5. Guardas do projeto
```bash
scripts/verificar.sh
```

## 6. Revisão
Gere `.git/revisao.diff` (comando em `CLAUDE.md`, "Agentes do projeto") e chame **os dois** revisores no mesmo diff: o `revisor-seguranca` (regras 1 a 10 do projeto) e o `android-revisor` (regras do app). O `android-revisor` complementa o `revisor-seguranca`, não o substitui. Resolva os itens Bloqueia e Importante; depois, skill `revisao-cruzada` e PR em `--draft`.
