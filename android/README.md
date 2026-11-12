# App Android do Gestor Financeiro

App nativo (Kotlin + Jetpack Compose, Material 3) que é só um cliente da API `/api/v1`.
O código entra nas próximas fatias (spec + ADR do app, depois o servidor do app). Esta pasta,
por enquanto, registra o ambiente de desenvolvimento.

## Ambiente de desenvolvimento (registrado em 2026-11-12)

| Ferramenta | Versão | Canal |
|---|---|---|
| JDK | OpenJDK 17.0.21 (`openjdk-17-jdk-headless`) | apt (Ubuntu) |
| Android SDK Command-line Tools | 23.0 (`commandlinetools-linux-16111833`, SHA-1 conferido) | dl.google.com |
| SDK Platform | `platforms;android-36` (rev. 2) | sdkmanager |
| Build-Tools | 36.0.0 | sdkmanager |
| Platform-Tools (adb) | 37.0.1 | sdkmanager |
| Emulator | 37.2.13 | sdkmanager |
| Imagem do emulador | `system-images;android-36;google_apis;x86_64` (rev. 7) | sdkmanager |
| AVD de teste | `gf-pixel-api36` (perfil `pixel_7`) | avdmanager |

O SDK fica em `~/Android/Sdk`. Cada pessoa põe no próprio shell:

```bash
export ANDROID_HOME="$HOME/Android/Sdk"
export PATH="$ANDROID_HOME/cmdline-tools/latest/bin:$ANDROID_HOME/platform-tools:$ANDROID_HOME/emulator:$PATH"
```

O `sdkmanager` dos Command-line Tools 23 avisa que está obsoleto em favor de `android sdk`;
os dois funcionam.

### Emulador e virtualização

Sem KVM: testes no aparelho.

## Regras

As regras do app estão em `.claude/rules/android.md`; a verificação antes de todo PR, em
`.claude/skills/android-verificar/SKILL.md`. Keystore, `keystore.properties` e
`local.properties` ficam fora do git (`.gitignore`), e as guardas do projeto barram commit de
keystore e o acesso de agentes a esses arquivos pelas ferramentas de edição e pelo Bash. Isso
reduz o risco, mas não substitui manter o keystore de release fora desta máquina: ele vive
nos GitHub Secrets do CI.
