---
name: android-verificar
description: Verificação completa do app Android do Gestor Financeiro antes de commit ou PR — cliente OpenAPI em dia, lint, testes, build release e checagens de segurança. Use sempre antes de dizer que uma tarefa em android/ está pronta.
---

# Verificar o app Android

Rode tudo a partir da raiz do repositório e mostre a saída real de cada passo. Se um passo falhar, pare, corrija e rode de novo desde o início. Nunca declare pronto sem todos verdes.

## 1. Cliente da API em dia com o contrato
```bash
cd android && ./gradlew openApiGenerate && cd .. && git diff --exit-code -- android/
```
Se houver diferença, o contrato mudou e o cliente gerado precisa entrar no commit.

## 2. Qualidade e testes
```bash
cd android && ./gradlew lint ktlintCheck testDebugUnitTest
```
(Se ktlint ainda não estiver configurado no projeto, rode sem ele e registre isso no PR.)

## 3. Build release
```bash
cd android && ./gradlew assembleRelease
```
Localmente o build pode sair sem assinatura; a assinatura só acontece no CI, com GitHub Secrets.

## 4. Checagens de segurança
```bash
grep -rnE "Log\.(d|i|v|w|e)\(|println\(" android/app/src/main || true
grep -rniE "storePassword|keyPassword|\.jks|\.keystore" android/ --include=*.kts --include=*.properties || true
grep -rn "usesCleartextTraffic=\"true\"" android/app/src/main || true
grep -rn "EncryptedSharedPreferences" android/app/src || true
```
Revise cada ocorrência: log com valor, CPF, cookie ou token é bloqueante; senha de keystore fora de variável de ambiente é bloqueante; cleartext e EncryptedSharedPreferences não podem existir.

## 5. Revisão
Chame o agente android-revisor no diff do branch e resolva os itens Bloqueantes e Importantes antes do PR.
