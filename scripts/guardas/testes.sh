#!/usr/bin/env bash
# Testes das guardas (regras.sh, pre-commit, hook do Claude). Rodam num repositório git temporário.
# Uso: scripts/guardas/testes.sh      Saída: 0 = todos passaram.
set -uo pipefail

raiz="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=scripts/guardas/regras.sh
source "$raiz/scripts/guardas/regras.sh"

passou=0; falhou=0
ok()    { passou=$((passou + 1)); echo "ok    $1"; }
falha() { falhou=$((falhou + 1)); echo "FALHA $1"; }
espera_sucesso() { local d="$1"; shift; if "$@" >/dev/null 2>&1; then ok "$d"; else falha "$d"; fi; }
espera_falha()   { local d="$1"; shift; if "$@" >/dev/null 2>&1; then falha "$d"; else ok "$d"; fi; }

tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
sql() { local nome="$1"; shift; printf '%s\n' "$@" > "$tmp/$nome"; echo "$tmp/$nome"; }

# --- regras.sh -------------------------------------------------------------
espera_falha   "DELETE FROM é barrado"            bash -c "source '$raiz/scripts/guardas/regras.sh'; echo 'DELETE  FROM x' | checar_delete_fisico t"
espera_falha   "ExecuteDelete é barrado"          bash -c "source '$raiz/scripts/guardas/regras.sh'; echo 'q.ExecuteDeleteAsync()' | checar_delete_fisico t"
espera_falha   "TRUNCATE TABLE é barrado"         bash -c "source '$raiz/scripts/guardas/regras.sh'; echo 'truncate table x' | checar_delete_fisico t"
espera_sucesso "api.DELETE( do front passa"       bash -c "source '$raiz/scripts/guardas/regras.sh'; echo 'api.DELETE(\"/x\")' | checar_delete_fisico t"
espera_sucesso "trigger BEFORE DELETE passa"      bash -c "source '$raiz/scripts/guardas/regras.sh'; echo 'BEFORE DELETE OR TRUNCATE ON t' | checar_delete_fisico t"

espera_sucesso "infra/secrets/x é segredo"        eh_caminho_de_segredo infra/secrets/pg_senha_app
espera_sucesso ".env é segredo"                   eh_caminho_de_segredo infra/.env
espera_sucesso "caminho absoluto de segredo"      eh_caminho_de_segredo /repo/infra/secrets/x
espera_falha   ".env.example não é segredo"       eh_caminho_de_segredo infra/.env.example
espera_falha   "README dos segredos não é"        eh_caminho_de_segredo infra/secrets/README.md
espera_falha   "código comum não é segredo"       eh_caminho_de_segredo backend/src/x.cs

# Segredos do app Android e chaves de conta de serviço.
espera_sucesso "keystore .jks é segredo"          eh_caminho_de_segredo android/app/release.jks
espera_sucesso "keystore .keystore é segredo"     eh_caminho_de_segredo android/release.keystore
espera_sucesso "keystore .p12 é segredo"          eh_caminho_de_segredo a/upload.p12
espera_sucesso "keystore .pfx é segredo"          eh_caminho_de_segredo a/upload.pfx
espera_sucesso "keystore .bks é segredo"          eh_caminho_de_segredo a/x.bks
espera_sucesso "chave .pepk é segredo"            eh_caminho_de_segredo a/output.pepk
espera_sucesso "keystore.properties é segredo"    eh_caminho_de_segredo android/keystore.properties
espera_sucesso "keystore.properties na raiz"      eh_caminho_de_segredo keystore.properties
espera_sucesso "~/.android é segredo"             eh_caminho_de_segredo "~/.android"
espera_sucesso "arquivo do ~/.android é segredo"  eh_caminho_de_segredo /home/x/.android/adbkey
espera_sucesso "~/.gradle/gradle.properties"      eh_caminho_de_segredo "~/.gradle/gradle.properties"
espera_sucesso "gradle.properties do usuário"     eh_caminho_de_segredo /home/x/.gradle/gradle.properties
espera_sucesso "JSON de service account"          eh_caminho_de_segredo infra/play-service-account.json
espera_sucesso "JSON de conta de serviço"         eh_caminho_de_segredo a/conta-de-servico-play.json
espera_falha   "gradle.properties do projeto não é" eh_caminho_de_segredo android/gradle.properties
espera_falha   "build.gradle.kts não é segredo"   eh_caminho_de_segredo android/app/build.gradle.kts
espera_falha   "pasta android/ não é segredo"     eh_caminho_de_segredo android/README.md

espera_sucesso "conteúdo de conta de serviço"     bash -c "source '$raiz/scripts/guardas/regras.sh'; printf '{\n  \"type\": \"service_account\",\n  \"project_id\": \"x\"\n}' | eh_json_de_conta_de_servico"
espera_sucesso "JSON com private_key"             bash -c "source '$raiz/scripts/guardas/regras.sh'; echo '{\"private_key\": \"-----BEGIN\"}' | eh_json_de_conta_de_servico"
espera_falha   "package.json não é conta de serviço" bash -c "source '$raiz/scripts/guardas/regras.sh'; echo '{\"name\": \"x\", \"type\": \"module\"}' | eh_json_de_conta_de_servico"

# Comandos Bash do agente (hook PreToolUse Bash).
espera_sucesso "gradlew lint passa"               checar_comando_bash "./gradlew lint"
espera_sucesso "cd android && gradlew passa"      checar_comando_bash "cd android && ./gradlew testDebugUnitTest"
espera_falha   "gradlew --scan é barrado"         checar_comando_bash "./gradlew lint --scan"
espera_falha   "gradlew -I é barrado"             checar_comando_bash "./gradlew -I /tmp/x.gradle lint"
espera_falha   "gradlew -Iarquivo é barrado"      checar_comando_bash "./gradlew -I/tmp/x.gradle lint"
espera_falha   "gradlew --init-script é barrado"  checar_comando_bash "./gradlew --init-script /tmp/x.gradle lint"
espera_falha   "gradlew --init-script= é barrado" checar_comando_bash "./gradlew --init-script=/tmp/x.gradle lint"
espera_falha   "gradlew properties é barrado"     checar_comando_bash "cd android && ./gradlew properties"
espera_falha   "gradlew :app:properties é barrado" checar_comando_bash "./gradlew :app:properties"
espera_falha   "gradlew signingReport é barrado"  checar_comando_bash "./gradlew signingReport"
espera_falha   "gradlew :app:signingReport é barrado" checar_comando_bash "./gradlew :app:signingReport"
espera_falha   "gradlew sem ./ também é vigiado"  checar_comando_bash "android/gradlew --scan"
espera_falha   "cat keystore.properties é barrado" checar_comando_bash "cat android/keystore.properties"
espera_falha   "base64 de keystore é barrado"     checar_comando_bash "base64 '/home/x/upload.jks' > /tmp/a"
espera_falha   "ler ~/.android é barrado"         checar_comando_bash "ls ~/.android"
espera_falha   "ler gradle.properties do usuário" checar_comando_bash "cat ~/.gradle/gradle.properties"
espera_falha   "keytool no keystore é barrado"    checar_comando_bash "keytool -list -keystore=android/release.keystore"
espera_sucesso "git status passa"                 checar_comando_bash "git status"
espera_sucesso "grep -l por senha no android passa" checar_comando_bash "git ls-files -z android | xargs -0 grep -lE 'storePassword|keyPassword' || true"
espera_sucesso "properties fora do gradlew passa" checar_comando_bash "grep -rn properties docs"

# 2º revisor, achado 2: lista positiva do gradlew/gradle (tarefas do settings.json + 4 opções).
espera_sucesso "gradlew com as 4 opções liberadas" checar_comando_bash "./gradlew --offline --console=plain --stacktrace -q testDebugUnitTest"
espera_sucesso "gradlew com várias tarefas liberadas" checar_comando_bash "cd android && ./gradlew lint ktlintCheck koverXmlReport"
espera_sucesso "gradlew com saída redirecionada"  checar_comando_bash "./gradlew assembleDebug > /tmp/build.log 2>&1"
espera_sucesso "gradlew seguido de tail"          checar_comando_bash "./gradlew lint | tail -20"
espera_falha   "abreviação camelCase é barrada"   checar_comando_bash "./gradlew signingR"
espera_falha   "abreviação de properties é barrada" checar_comando_bash "./gradlew prope"
espera_falha   ":app:sigRep é barrado"            checar_comando_bash "./gradlew :app:sigRep"
espera_falha   ":app:lint fora da lista é barrado" checar_comando_bash "./gradlew :app:lint"
espera_falha   "gradlew entre aspas é vigiado"    checar_comando_bash "\"./gradlew\" signingReport"
espera_falha   "gradlew entre aspas simples"      checar_comando_bash "'./gradlew' signingReport"
espera_falha   "gradle do sistema é vigiado"      checar_comando_bash "gradle -p android signingReport"
espera_falha   "gradle por caminho absoluto"      checar_comando_bash "/usr/bin/gradle tasks"
espera_falha   "gradlew por bash -c é vigiado"    checar_comando_bash "bash -c './gradlew signingReport'"
espera_falha   "gradlew com env antes é vigiado"  checar_comando_bash "env JAVA_HOME=/x ./gradlew dependencies"
espera_falha   "tarefa fora da lista é barrada"   checar_comando_bash "./gradlew dependencies"
espera_falha   "opção fora da lista é barrada"    checar_comando_bash "./gradlew --console=rich lint"
espera_falha   "gradlew -D é barrado"             checar_comando_bash "./gradlew -Dorg.gradle.jvmargs=x lint"

# 2º revisor, achado 3: glob, barra invertida e cd para ~/.gradle.
espera_falha   "glob release.jk? é barrado"       checar_comando_bash "cat android/release.jk?"
espera_falha   "glob *.j*s é barrado"             checar_comando_bash "cat android/*.j*s"
espera_falha   "glob ~/.andr*/adbkey é barrado"   checar_comando_bash "cat ~/.andr*/adbkey"
espera_falha   "glob keystore.prop* é barrado"    checar_comando_bash "cat android/keystore.prop*"
espera_falha   "glob *.p1? é barrado"             checar_comando_bash "base64 a/*.p1?"
espera_falha   "barra invertida no nome é barrada" checar_comando_bash 'cat android/keystore\.properties'
espera_falha   "cd ~/.gradle e cat gradle.properties" checar_comando_bash "cd ~/.gradle && cat gradle.properties"
espera_falha   "cd ~/.gradle e glob gradle.prop*" checar_comando_bash "cd ~/.gradle; cat gradle.prop*"
espera_sucesso "gradle.properties do projeto passa" checar_comando_bash "cat android/gradle.properties"
espera_sucesso "glob comum passa"                 checar_comando_bash "ls android/app/src/main/*.kt"

# 2º revisor, achado 4: mensagem por arquivo, palavra .x do jq, gradlew só no trecho dele.
espera_sucesso "git commit -F passa"              checar_comando_bash "git commit -q -F /tmp/msg.txt"
espera_sucesso "gh pr create --body-file passa"   checar_comando_bash "gh pr create --draft --title 'chore(android): ferramentas do app' --body-file /tmp/pr.md"
espera_sucesso "gh pr comment --body-file passa"  checar_comando_bash "gh pr comment 40 --body-file /tmp/revisao.md"
espera_sucesso "jq '.key' passa"                  checar_comando_bash "jq -r '.key' a.json"
espera_sucesso "gh api --jq '.[].key' passa"      checar_comando_bash "gh api repos/x/y/keys --jq '.[].key'"
espera_falha   "cat .env continua barrado"        checar_comando_bash "cat .env"
espera_falha   "ls .android continua barrado"     checar_comando_bash "cd ~ && ls .android"
# 3º revisor, achado 4: só os sufixos do jq (.key, .pem, .dump) ficam livres sem "/".
espera_falha   "cd ~ e cat .keystore barrado"     checar_comando_bash "cd ~ && cat .keystore"
espera_falha   "cat .chave-servico.json barrado"  checar_comando_bash "cat .chave-servico.json"
espera_sucesso "-I e properties fora do trecho do gradlew" checar_comando_bash "./gradlew lint && grep -rnI properties docs"
espera_sucesso "gradlew depois de ; e ||"         checar_comando_bash "true; ./gradlew lint || echo falhou"
espera_falha   "flag barrada no 2º trecho do gradlew" checar_comando_bash "cd android && ./gradlew lint --scan | tail"

# .gitignore cobre os segredos do app.
for ignorado in android/app/release.jks android/release.keystore android/keystore.properties android/local.properties a/x.p12; do
  espera_sucesso ".gitignore ignora $ignorado"   git -C "$raiz" check-ignore -q --no-index "$ignorado"
done

espera_sucesso "backend .cs é vigiado"            eh_codigo_vigiado backend/src/Modules/X/A.cs
espera_falha   "teste não é vigiado"              eh_codigo_vigiado backend/tests/X/A.cs
espera_falha   "markdown não é vigiado"           eh_codigo_vigiado docs/a.md

espera_sucesso "nome V0011__boletos.sql"          checar_nome_de_migracao db/migrations/V0011__boletos.sql
espera_falha   "nome sem 4 dígitos"               checar_nome_de_migracao db/migrations/V11__boletos.sql
espera_falha   "nome com maiúscula"               checar_nome_de_migracao db/migrations/V0011__Boletos.sql

espera_sucesso "sequência contínua"               checar_sequencia_de_migracoes a/V0001__a.sql a/V0002__b.sql a/V0003__c.sql
espera_falha   "sequência com buraco"             checar_sequencia_de_migracoes a/V0001__a.sql a/V0003__c.sql
espera_falha   "sequência repetida"               checar_sequencia_de_migracoes a/V0001__a.sql a/V0001__b.sql
espera_falha   "sequência sem V0001"              checar_sequencia_de_migracoes a/V0002__a.sql

boa="$(sql V0011__boa.sql \
  "CREATE TABLE boletos.vencimentos (id uuid PRIMARY KEY, is_deleted boolean NOT NULL DEFAULT false);" \
  "CREATE UNIQUE INDEX ux_v ON boletos.vencimentos (casal_id, linha_hmac)" \
  "    WHERE NOT is_deleted;" \
  "-- comentário com UNIQUE não conta" \
  "SELECT audit.proteger('boletos.vencimentos');")"
espera_sucesso "migração correta (WHERE na linha seguinte)" checar_conteudo_de_migracao "$boa"

sem_audit="$(sql V0011__a.sql "CREATE TABLE boletos.vencimentos (id uuid PRIMARY KEY);")"
espera_falha   "tabela sem audit.proteger"        checar_conteudo_de_migracao "$sem_audit"

unico_simples="$(sql V0011__b.sql \
  "CREATE TABLE boletos.x (id uuid PRIMARY KEY);" \
  "CREATE UNIQUE INDEX ux_x ON boletos.x (nome);" \
  "SELECT audit.proteger('boletos.x');")"
espera_falha   "UNIQUE sem índice parcial"        checar_conteudo_de_migracao "$unico_simples"

restricao="$(sql V0011__c.sql "CREATE TABLE boletos.y (id uuid PRIMARY KEY, nome text UNIQUE);" "SELECT audit.proteger('boletos.y');")"
espera_falha   "UNIQUE dentro do CREATE TABLE"    checar_conteudo_de_migracao "$restricao"

sem_schema="$(sql V0011__e.sql "CREATE TABLE sem_schema (id uuid PRIMARY KEY);")"
espera_falha   "CREATE TABLE sem schema"          checar_conteudo_de_migracao "$sem_schema"

sem_schema_audit="$(sql V0011__f.sql "CREATE TABLE sem_schema (id uuid PRIMARY KEY);" "SELECT audit.proteger('sem_schema');")"
espera_falha   "CREATE TABLE sem schema, mesmo com audit" checar_conteudo_de_migracao "$sem_schema_audit"

sem_schema_if="$(sql V0011__g.sql "CREATE TABLE IF NOT EXISTS sem_schema (id uuid PRIMARY KEY);")"
espera_falha   "CREATE TABLE IF NOT EXISTS sem schema" checar_conteudo_de_migracao "$sem_schema_if"

com_schema="$(sql V0011__h.sql "CREATE TABLE a.b (id uuid PRIMARY KEY);" "SELECT audit.proteger('a.b');")"
espera_sucesso "CREATE TABLE a.b com audit passa"  checar_conteudo_de_migracao "$com_schema"

apaga="$(sql V0011__d.sql "DELETE FROM boletos.x;")"
espera_falha   "DELETE físico em migração"        checar_conteudo_de_migracao "$apaga"

# Docker do snap (ADR-013): um docker falso na frente do PATH.
docker_falso() { mkdir -p "$tmp/$1"; printf '#!/bin/sh\nexit 0\n' >"$tmp/$1/docker"; chmod +x "$tmp/$1/docker"; }
docker_falso snap/bin
docker_falso usr/bin
mkdir -p "$tmp/sem-docker"; ln -s "$tmp/snap/bin/docker" "$tmp/sem-docker/link-para-snap"
docker_falso link/bin; rm "$tmp/link/bin/docker"; ln -s "$tmp/snap/bin/docker" "$tmp/link/bin/docker"
espera_sucesso "docker em /snap/bin é do snap"     env PATH="$tmp/snap/bin:/usr/bin:/bin" bash -c "source '$raiz/scripts/guardas/regras.sh'; docker_e_do_snap"
espera_sucesso "link para o docker do snap é do snap" env PATH="$tmp/link/bin:/usr/bin:/bin" bash -c "source '$raiz/scripts/guardas/regras.sh'; docker_e_do_snap"
# Como no snap de verdade: /snap/bin/docker é um link para /usr/bin/snap.
mkdir -p "$tmp/real/snap/bin" "$tmp/real/usr/bin"; printf '#!/bin/sh\nexit 0\n' >"$tmp/real/usr/bin/snap"
chmod +x "$tmp/real/usr/bin/snap"; ln -s "$tmp/real/usr/bin/snap" "$tmp/real/snap/bin/docker"
espera_sucesso "/snap/bin/docker → /usr/bin/snap é do snap" env PATH="$tmp/real/snap/bin:/usr/bin:/bin" bash -c "source '$raiz/scripts/guardas/regras.sh'; docker_e_do_snap"
espera_falha   "docker em /usr/bin não é do snap"  env PATH="$tmp/usr/bin:/usr/bin:/bin" bash -c "source '$raiz/scripts/guardas/regras.sh'; docker_e_do_snap"
espera_falha   "sem docker no PATH não é do snap"  env PATH="$tmp/sem-docker" /bin/bash -c "source '$raiz/scripts/guardas/regras.sh'; docker_e_do_snap"

# --- pre-commit ------------------------------------------------------------
repo="$tmp/repo"
mkdir -p "$repo" && cd "$repo" || exit 1
git init -q . && git config user.email t@t && git config user.name t
mkdir -p scripts/guardas .githooks db/migrations backend/src backend/tests infra/secrets
cp "$raiz/scripts/guardas/regras.sh" scripts/guardas/ && cp "$raiz/.githooks/pre-commit" .githooks/
git config core.hooksPath .githooks
printf '%s\n' "CREATE TABLE a.t (id uuid PRIMARY KEY);" "SELECT audit.proteger('a.t');" > db/migrations/V0001__inicio.sql
git add -A
if git commit -qm base >/dev/null 2>&1; then ok "pre-commit aceita base válida"; else falha "pre-commit aceita base válida"; fi

# Commit de teste. Sem nada no stage o teste é inválido: devolve 99, que nenhum espera_* aceita como resposta certa.
commit() {
  git add -A >/dev/null 2>&1
  if git diff --cached --quiet; then echo "teste inválido: nada no stage" >&2; return 99; fi
  git commit -qm teste >/dev/null 2>&1
}
deve_recusar() { local d="$1"; commit; local r=$?; if [ "$r" -eq 1 ]; then ok "$d"; else falha "$d (saída $r)"; fi; desfaz; }
deve_aceitar() { local d="$1"; commit; local r=$?; if [ "$r" -eq 0 ]; then ok "$d"; else falha "$d (saída $r)"; fi; }
desfaz() { git reset -q --hard HEAD >/dev/null 2>&1; git clean -qfd >/dev/null 2>&1; mkdir -p backend/src backend/tests infra/secrets db/migrations; }

echo "x" > infra/secrets/pg_senha
deve_recusar "pre-commit barra segredo"

echo "-- mudou" >> db/migrations/V0001__inicio.sql
deve_recusar "pre-commit barra migração já commitada"

printf '%s\n' "CREATE TABLE a.u (id uuid PRIMARY KEY);" "SELECT audit.proteger('a.u');" > db/migrations/V0002__nova.sql
deve_aceitar "pre-commit aceita migração nova correta"

printf '%s\n' "CREATE TABLE a.v (id uuid PRIMARY KEY);" > db/migrations/V0003__sem_audit.sql
deve_recusar "pre-commit barra migração sem audit"

printf '%s\n' "CREATE TABLE a.w (id uuid PRIMARY KEY);" "SELECT audit.proteger('a.w');" > db/migrations/V0005__pulou.sql
deve_recusar "pre-commit barra buraco na numeração"

echo 'await db.X.ExecuteDeleteAsync();' > backend/src/A.cs
deve_recusar "pre-commit barra DELETE em código"

echo 'Assert.Throws(() => Sql("DELETE FROM x"));' > backend/tests/A.cs
deve_aceitar "pre-commit deixa teste citar DELETE"

mkdir -p android/app
printf 'x' > android/app/release.jks
deve_recusar "pre-commit barra keystore .jks"

mkdir -p android/app
printf 'storePassword=x\n' > android/keystore.properties
deve_recusar "pre-commit barra keystore.properties"

mkdir -p android/app
printf '{"type": "service_account", "private_key": "x"}\n' > android/app/inocente.json
deve_recusar "pre-commit barra JSON de conta de serviço pelo conteúdo"

mkdir -p android/app
printf '{"name": "x", "type": "module"}\n' > android/app/package.json
deve_aceitar "pre-commit aceita JSON comum"

# --- varredura (verificar-repositorio.sh) ---------------------------------
desfaz
cp "$raiz/scripts/guardas/verificar-repositorio.sh" scripts/guardas/
git add -A >/dev/null 2>&1 && git commit -qm varredura >/dev/null 2>&1
espera_sucesso "varredura passa no repositório válido" scripts/guardas/verificar-repositorio.sh "$repo"
printf '%s\n' "CREATE TABLE a.z (id uuid PRIMARY KEY, nome text UNIQUE);" "SELECT audit.proteger('a.z');" > db/migrations/V0003__nova_invalida.sql
espera_falha   "varredura pega migração nova ainda fora do git" scripts/guardas/verificar-repositorio.sh "$repo"
desfaz
echo 'db.X.ExecuteDelete();' > backend/src/B.cs
espera_falha   "varredura pega código novo ainda fora do git" scripts/guardas/verificar-repositorio.sh "$repo"
desfaz
printf '{"type": "service_account"}\n' > backend/src/c.json
espera_falha   "varredura pega JSON de conta de serviço" scripts/guardas/verificar-repositorio.sh "$repo"
desfaz

# --- hook do Claude ---------------------------------------------------------
hook="$raiz/.claude/hooks/bloquear-delete-fisico.sh"
confere_hook() { if [ "$(roda_hook "$3")" = "$2" ]; then ok "$1"; else falha "$1"; fi; }
roda_hook() { printf '%s' "$1" | CLAUDE_PROJECT_DIR="$raiz" "$hook" >/dev/null 2>&1; echo $?; }
confere_hook "hook do Claude barra DELETE" 2 '{"file_path":"'"$raiz"'/backend/src/A.cs","new_string":"DELETE FROM x"}'
confere_hook "hook do Claude deixa soft delete" 0 '{"file_path":"'"$raiz"'/backend/src/A.cs","new_string":"x.IsDeleted = true"}'
confere_hook "hook do Claude barra segredo" 2 '{"file_path":"'"$raiz"'/infra/secrets/x","content":"a"}'
confere_hook "hook do Claude deixa .env.example" 0 '{"file_path":"'"$raiz"'/infra/.env.example","content":"a"}'
confere_hook "hook do Claude deixa teste citar DELETE" 0 '{"file_path":"'"$raiz"'/backend/tests/A.cs","new_string":"DELETE FROM x"}'
confere_hook "hook do Claude barra editar keystore.properties" 2 '{"file_path":"'"$raiz"'/android/keystore.properties","content":"a"}'

hook_bash="$raiz/.claude/hooks/guardar-bash.sh"
roda_hook_bash() { printf '%s' "$1" | CLAUDE_PROJECT_DIR="$raiz" "$hook_bash" >/dev/null 2>&1; echo $?; }
confere_bash() { if [ "$(roda_hook_bash "$3")" = "$2" ]; then ok "$1"; else falha "$1"; fi; }
confere_bash "hook Bash deixa gradlew lint"        0 '{"tool_name":"Bash","tool_input":{"command":"cd android && ./gradlew lint"}}'
confere_bash "hook Bash barra gradlew --scan"      2 '{"tool_name":"Bash","tool_input":{"command":"./gradlew lint --scan"}}'
confere_bash "hook Bash barra ler keystore.properties" 2 '{"tool_name":"Bash","tool_input":{"command":"cat \"android/keystore.properties\""}}'
confere_bash "hook Bash barra ler ~/.android"      2 '{"tool_name":"Bash","tool_input":{"command":"ls -la ~/.android/"}}'
confere_bash "hook Bash deixa git status"          0 '{"tool_name":"Bash","tool_input":{"command":"git status"}}'
confere_bash "hook Bash deixa entrada sem comando" 0 '{"tool_name":"Bash","tool_input":{}}'
confere_bash "hook Bash barra entrada malformada"  2 'nao-e-json'
# Sem jq no PATH o hook tem de bloquear, nunca deixar passar.
mkdir -p "$tmp/sem-jq"
for f in bash cat grep tr dirname; do ln -sf "$(command -v "$f")" "$tmp/sem-jq/$f"; done
sem_jq="$(printf '%s' '{"tool_input":{"command":"git status"}}' | CLAUDE_PROJECT_DIR="$raiz" PATH="$tmp/sem-jq" "$tmp/sem-jq/bash" "$hook_bash" >/dev/null 2>&1; echo $?)"
if [ "$sem_jq" = "2" ]; then ok "hook Bash barra quando falta o jq"; else falha "hook Bash barra quando falta o jq (saída $sem_jq)"; fi

echo ""
echo "$passou passaram, $falhou falharam"
[ "$falhou" -eq 0 ]
