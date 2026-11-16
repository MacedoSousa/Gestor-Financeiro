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

# Em /tmp/gf-*: o docker-gf.sh só aceita caminho do host no repositório, em /tmp/gf-* ou no scratchpad.
tmp="$(mktemp -d /tmp/gf-guardas.XXXXXX)"; trap 'rm -rf "$tmp"' EXIT
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
# shellcheck disable=SC2088 # til literal de propósito: é o texto que a guarda recebe
espera_sucesso "~/.android é segredo"          eh_caminho_de_segredo "~/.android"
espera_sucesso "arquivo do ~/.android é segredo"  eh_caminho_de_segredo /home/x/.android/adbkey
# shellcheck disable=SC2088 # idem
espera_sucesso "~/.gradle/gradle.properties"    eh_caminho_de_segredo "~/.gradle/gradle.properties"
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

# D24: a instalação de verdade (/opt, projeto "gestor" do compose, containers gestor-*) é só da pessoa.
# /opt em qualquer grafia que o texto deixa ver.
espera_falha   "ls /opt é barrado"                checar_comando_bash "ls /opt"
espera_falha   "caminho em /opt é barrado"        checar_comando_bash "cat /opt/gestor-financeiro/infra/docker-compose.yml"
espera_falha   "cd /opt/ é barrado"               checar_comando_bash "cd /opt/ && ls"
espera_falha   "/opt entre aspas é barrado"       checar_comando_bash "ls \"/opt/gestor-financeiro\""
espera_falha   "/opt depois de = é barrado"       checar_comando_bash "GF_RAIZ=/opt/gestor-financeiro make"
espera_falha   "/opt por glob é barrado"          checar_comando_bash "ls /op*/gestor-financeiro"
espera_falha   "/opt por glob ? é barrado"        checar_comando_bash "ls /o?t"
espera_falha   "//opt"                            checar_comando_bash "ls //opt/gestor-financeiro"
espera_falha   "/./opt"                           checar_comando_bash "ls /./opt/gestor-financeiro"
espera_falha   "/tmp/../opt"                      checar_comando_bash "ls /tmp/../opt"
espera_falha   "/{opt,}"                          checar_comando_bash "ls /{opt,}/gestor-financeiro"
espera_falha   "/{o,}pt"                          checar_comando_bash "ls /{o,}pt/gestor-financeiro"
espera_falha   "git -C/opt"                       checar_comando_bash "git -C/opt/gestor-financeiro log"
espera_falha   "tar -C/opt"                       checar_comando_bash "tar -C/opt/gestor-financeiro -c infra"
espera_falha   "../opt"                           checar_comando_bash "cd ../../../opt"
espera_falha   "PATH com :/opt"                   checar_comando_bash "PATH=/usr/bin:/opt/x ls"
espera_falha   "rsync host:/opt"                  checar_comando_bash "rsync a localhost:/opt/gestor-financeiro/"
espera_falha   "cd / e opt relativo"              checar_comando_bash "cd / && ls opt/gestor-financeiro"
espera_falha   "pushd / e opt relativo"           checar_comando_bash "pushd / && ls opt/x"
espera_falha   "cd /tmp/.. e opt"                 checar_comando_bash "cd /tmp/.. && ls opt"
espera_falha   "cd ~/../.. e opt"                 checar_comando_bash "cd ~/../.. && ls opt"
espera_falha   "find a partir de /"               checar_comando_bash "find / -name docker-compose.yml 2>/dev/null"
espera_falha   "grep -r a partir de /"            checar_comando_bash "grep -rn POSTGRES /etc / 2>/dev/null"
espera_falha   "du /*"                            checar_comando_bash "du -sh /*"
espera_sucesso "/optar não é /opt"                checar_comando_bash "ls /tmp/optar"
espera_sucesso "palavra opt solta passa"          checar_comando_bash "grep -rn opt docs"
espera_sucesso "caminho com /opt no meio passa"   checar_comando_bash "ls frontend/node_modules/x/opt"
espera_sucesso "find no repositório passa"        checar_comando_bash "find . -name '*.sh'"
espera_sucesso "chaves em caminho relativo passam" checar_comando_bash "ls src/{a,b}.ts"

# docker: direto só para ler; o resto por scripts/docker-gf.sh.
espera_sucesso "docker ps passa"                  checar_comando_bash "docker ps --format '{{.Names}}'"
espera_sucesso "docker images passa"              checar_comando_bash "docker images"
espera_sucesso "docker version passa"             checar_comando_bash "docker version"
espera_sucesso "docker-gf.sh passa"               checar_comando_bash "scripts/docker-gf.sh run -d --rm --name gf-qa-pg -e POSTGRES_DB=gestor postgres:17-alpine"
espera_sucesso "docker-gf.sh com \$PWD passa"     checar_comando_bash "scripts/docker-gf.sh run --rm --name gf-sc -v \"\$PWD:/mnt:ro\" koalaman/shellcheck:stable -x a.sh"
espera_sucesso "grep docker no texto passa"       checar_comando_bash "grep -rn docker infra"
espera_sucesso "testes do backup passam"          checar_comando_bash "infra/backup/testes/rodar-todos.sh"
espera_sucesso "testes de subida passam"          checar_comando_bash "infra/testes/testar-subida.sh"
espera_sucesso "dotnet test passa"                checar_comando_bash "dotnet test backend/tests/GestorFinanceiro.IntegrationTests"
espera_falha   "docker run direto"                checar_comando_bash "docker run -d --rm --name gf-qa-pg postgres:17-alpine"
espera_falha   "docker rm direto"                 checar_comando_bash "docker rm -f gf-qa-pg"
espera_falha   "docker compose direto"            checar_comando_bash "docker compose -p gf-x up -d"
espera_falha   "docker compose -p gestor"         checar_comando_bash "docker compose -p gestor ps"
espera_falha   "compose sem -p dentro de infra"   checar_comando_bash "cd infra && docker compose down -v"
espera_falha   "docker-compose"                   checar_comando_bash "docker-compose -f infra/docker-compose.yml ps"
espera_falha   "docker exec gestor-*"             checar_comando_bash "docker exec gestor-postgres-1 psql -U postgres"
espera_falha   "sudo docker"                      checar_comando_bash "sudo docker restart gestor-web-1"
espera_falha   "env -i docker"                    checar_comando_bash "env -i docker compose down"
espera_falha   "/usr/bin/env docker"              checar_comando_bash "/usr/bin/env docker compose down"
espera_falha   "bash -ec com docker"              checar_comando_bash "bash -ec \"docker compose -f infra/docker-compose.yml down\""
espera_falha   "timeout docker"                   checar_comando_bash "timeout -s KILL 60 docker compose down"
espera_falha   "eval docker"                      checar_comando_bash "eval docker compose down"
espera_falha   "xargs docker"                     checar_comando_bash "docker ps -q | xargs -r docker rm -f"
espera_falha   "docker ps por xargs"              checar_comando_bash "echo x | xargs docker ps"
espera_falha   "crase com docker"                 checar_comando_bash "\`docker compose -f infra/docker-compose.yml down\`"
espera_falha   "echo crase docker"                checar_comando_bash "echo \`docker stop gestor-api-1\`"
espera_falha   "\$(which docker)"                 checar_comando_bash "\$(which docker) compose -f infra/docker-compose.yml down"
espera_falha   "git rebase -x docker"             checar_comando_bash "git rebase -x 'docker compose down' HEAD~1"
espera_falha   "sed /e com docker"                checar_comando_bash "sed 's/x/docker compose down/e' a"
espera_falha   "strace com docker"                checar_comando_bash "strace -o docker -v docker stop gestor-api-1"
espera_falha   "docker --context antes"           checar_comando_bash "docker --context default ps"
espera_falha   "curl no docker.sock"              checar_comando_bash "curl --unix-socket /var/run/docker.sock http://localhost/containers/json"
espera_falha   "DOCKER_HOST"                      checar_comando_bash "DOCKER_HOST=tcp://127.0.0.1:2375 docker ps"
espera_sucesso "grep docker.sock no texto passa"  checar_comando_bash "grep -rn docker.sock docs"

# kill: só PID literal.
espera_falha   "pkill"                            checar_comando_bash "pkill -f GestorFinanceiro.Api"
espera_falha   "killall"                          checar_comando_bash "killall dotnet"
espera_falha   "env -i pkill"                     checar_comando_bash "env -i pkill -f GestorFinanceiro.Api"
espera_falha   "/usr/bin/env killall"             checar_comando_bash "/usr/bin/env killall dotnet"
espera_falha   "echo crase pkill"                 checar_comando_bash "echo \`pkill -f GestorFinanceiro.Api\`"
espera_falha   "\$(command -v pkill)"             checar_comando_bash "\$(command -v pkill) dotnet"
# shellcheck disable=SC2016 # o texto do comando, sem expandir, é o que a guarda recebe
espera_falha   "kill \$(pgrep)"                   checar_comando_bash 'kill $(pgrep -f GestorFinanceiro.Api)'
espera_falha   "kill com crase"                   checar_comando_bash "kill \`pgrep dotnet\`"
espera_falha   "pgrep | xargs kill"               checar_comando_bash "pgrep -f GestorFinanceiro.Api | xargs kill"
espera_falha   "fuser -k"                         checar_comando_bash "fuser -k 5000/tcp"
espera_sucesso "kill por PID passa"               checar_comando_bash "kill 107430"

# Scripts de produção do backup: só pelos testes.
espera_falha   "backup.sh direto"                 checar_comando_bash "GF_TMP=/tmp/x infra/backup/backup.sh"
espera_falha   "restaurar.sh direto"              checar_comando_bash "bash infra/backup/restaurar.sh p.tar.age -i - --instalar"
espera_falha   "backup.sh por caminho relativo"   checar_comando_bash "cd infra && backup/backup.sh"
espera_falha   "./backup.sh"                      checar_comando_bash "cd infra/backup && ./backup.sh"

# scripts/docker-gf.sh contra um docker falso: anota a chamada; inspect responde nome e projeto pelo nome pedido.
mkdir -p "$tmp/docker-gf"
cat >"$tmp/docker-gf/docker" <<'FALSO'
#!/usr/bin/env bash
if [ "$1" = inspect ] && [ "$2" = -f ]; then
  alvo="${*: -1}"
  case "$alvo" in gf-*-de-gestor) projeto=gestor ;; gf-*) projeto="" ;; *) exit 1 ;; esac
  case "$3" in *Name*) echo "/$alvo" ;; *) echo "$projeto" ;; esac
  exit 0
fi
echo "$*" >>"$(dirname "$0")/chamadas"
FALSO
chmod +x "$tmp/docker-gf/docker"
gf() { DOCKER_GF_BIN="$tmp/docker-gf/docker" "$raiz/scripts/docker-gf.sh" "$@"; }
espera_sucesso "gf: run descartável"              gf run -d --rm --name gf-qa-pg -p 127.0.0.1:55439:5432 -e POSTGRES_DB=gestor postgres:17-alpine
espera_sucesso "gf: run com o repositório montado" gf run --rm --name gf-sc -v "$raiz:/mnt:ro" -w /mnt koalaman/shellcheck:stable -x a.sh
espera_sucesso "gf: exec no próprio container"   gf exec gf-qa-pg psql -U postgres -d gestor -c "select 1"
espera_sucesso "gf: logs --tail"                  gf logs --tail 50 gf-qa-pg
espera_sucesso "gf: rm -f"                        gf rm -f gf-qa-pg
espera_sucesso "gf: inspect -f"                   gf inspect -f '{{json .State}}' gf-qa-pg
espera_sucesso "gf: cp do container para /tmp"    gf cp gf-qa-pg:/tmp/x "$tmp/x"
espera_sucesso "gf: build -t gf-"                 gf build -q -t gf-teste-api:1 "$raiz"
espera_falha   "gf: compose não passa por aqui"   gf compose -p gf-teste-1 -f "$raiz/infra/docker-compose.yml" ps
espera_sucesso "gf: pull"                         gf pull postgres:17-alpine
espera_falha   "gf: run sem --name"               gf run -d --rm postgres:17-alpine
espera_falha   "gf: run com --name gestor-"       gf run --name gestor-x alpine true
espera_falha   "gf: run com opção fora da lista"  gf run --name gf-x --use-api-socket docker:cli docker stop gestor-api-1
espera_falha   "gf: run com --oom-kill-disable"   gf run --name gf-x --oom-kill-disable --privileged alpine sh
espera_falha   "gf: run --privileged"             gf run --name gf-x --privileged alpine sh
espera_falha   "gf: run --network container:"     gf run --name gf-x --network container:gestor-postgres-1 alpine sh
espera_falha   "gf: run --network=container:"     gf run --name gf-x --network=container:gestor-postgres-1 alpine sh
espera_falha   "gf: run --pid"                    gf run --name gf-x --pid host alpine sh
espera_falha   "gf: run -v /run"                  gf run --name gf-x -v /run:/var/run docker:cli docker ps
espera_falha   "gf: run -v /."                    gf run --name gf-x -v /.:/h alpine ls
espera_falha   "gf: run -v /var/lib/docker"       gf run --name gf-x -v /var/lib/docker:/d alpine ls
espera_falha   "gf: run -v volume do gestor"      gf run --name gf-x -v gestor_dados:/d alpine ls
espera_falha   "gf: run -p em todas as interfaces com IP" gf run --name gf-x -p 0.0.0.0:5432:5432 alpine true
espera_falha   "gf: run da imagem gestor-api"     gf run --name gf-x gestor-api
espera_falha   "gf: exec em gestor-*"             gf exec gestor-postgres-1 psql -U postgres
espera_falha   "gf: exec por ID"                  gf exec 3f2a9c1e psql
espera_falha   "gf: exec -ite desloca o alvo"     gf exec -ite gf-x gestor-postgres-1 psql
espera_falha   "gf: exec em gf-* do projeto gestor" gf exec gf-x-de-gestor sh
espera_falha   "gf: stop /gestor-api-1"           gf stop /gestor-api-1
espera_falha   "gf: logs por ID"                  gf logs 3f2a9c1e
espera_falha   "gf: cp de gestor-*"               gf cp gestor-postgres-1:/run/secrets/x "$tmp/x"
espera_falha   "gf: cp para fora do repo e /tmp"  gf cp gf-qa-pg:/x /home/x
espera_falha   "gf: build -t gestor-"             gf build -t gestor-api "$raiz"
espera_falha   "gf: build -tgestor- colado"       gf build -tgestor-api "$raiz"
espera_falha   "gf: compose sem -p"               gf compose -f "$raiz/infra/docker-compose.yml" down
espera_falha   "gf: compose -p gestor"            gf compose -p gestor ps
espera_falha   "gf: compose -p depois do -f"      gf compose -f "$raiz/infra/docker-compose.yml" -p gf-x ps
espera_falha   "gf: compose com /opt"             gf compose -p gf-x -f /opt/gestor-financeiro/infra/docker-compose.yml ps
espera_falha   "gf: volume"                       gf volume rm algum
espera_falha   "gf: system prune"                 gf system prune -f
espera_falha   "gf: pull gestor-api"              gf pull gestor-api

# Revisão da guarda, rodada 3.
espera_falha   "gf: run com -- antes das opções"  gf run --name gf-x -- --privileged --pid host -v /:/host alpine sh
espera_falha   "gf: compose run com -v /"         gf compose -p gf-x -f "$raiz/infra/docker-compose.yml" run --rm -v /:/host postgres
espera_falha   "gf: run -p sem 127.0.0.1"         gf run --name gf-x -p 5432:5432 alpine true
espera_falha   "gf: run -v /tmp inteiro"          gf run --name gf-x -v /tmp:/t alpine ls
espera_falha   "gf: run de docker.io/library/gestor-api" gf run --name gf-x docker.io/library/gestor-api
espera_sucesso "gf: run com volume anônimo"       gf run -d --rm --name gf-qa-pg -v /var/lib/postgresql/data postgres:17-alpine
espera_sucesso "gf: exec com --color= no comando de dentro" gf exec gf-qa-pg ls --color=never /
if tail -n1 "$tmp/docker-gf/chamadas" | grep -q -- '--color=never'; then ok "gf: o comando de dentro chega como veio"; else falha "gf: o comando de dentro chega como veio"; fi
com_cwd() { local base="$1"; shift; GUARDA_CWD="$base" checar_comando_bash "$@"; }
espera_falha   "cd ../../.. e opt relativo"       com_cwd /home/x/repo "cd ../../.. && ls opt/gestor-financeiro"
espera_falha   "find ../../.."                    com_cwd /home/x/repo "find ../../.. -name docker-compose.yml"
espera_falha   "find ~/../.."                     com_cwd /home/x/repo "find ~/../.. -name docker-compose.yml"
espera_falha   "../../../op*"                     com_cwd /home/x/repo "ls ../../../op*/gestor-financeiro"
espera_falha   "../../../o{p,}t"                  com_cwd /home/x/repo "ls ../../../o{p,}t"
espera_falha   "timeout 60 find /"                com_cwd /home/x/repo "timeout 60 find / -name x"
espera_falha   "ln -s / raiz"                     com_cwd /home/x/repo "ln -s / raiz"
espera_sucesso "../outro passa"                   com_cwd /home/x/repo "ls ../outro"
espera_sucesso "git add de backup.sh passa"       checar_comando_bash "git add infra/backup/backup.sh"
espera_sucesso "git diff de restaurar.sh passa"   checar_comando_bash "git diff -- infra/backup/restaurar.sh"
espera_sucesso "sed -n em backup.sh passa"        checar_comando_bash "sed -n 1,40p infra/backup/backup.sh"
espera_falha   "testes/../backup.sh"              com_cwd /home/x/repo "infra/backup/testes/../backup.sh"
espera_falha   "programa por glob"                checar_comando_bash "/usr/bin/dock?r stop gestor-api-1"
espera_falha   "programa por chaves"              checar_comando_bash "{docker,} stop gestor-api-1"
espera_falha   "pkill por glob"                   checar_comando_bash "/usr/bin/pkil? dotnet"
espera_falha   "socket por glob"                  checar_comando_bash "curl --unix-socket /var/run/dock*.sock http://localhost/containers/json"
espera_falha   "docker quebrado com barra e linha" checar_comando_bash $'doc\
ker stop gestor-api-1'
espera_falha   "plugin docker-buildx direto"      checar_comando_bash "/usr/libexec/docker/cli-plugins/docker-buildx build -t gestor-api ."
espera_falha   "skill"                            checar_comando_bash "skill -KILL dotnet"
espera_falha   "DOCKER_GF_BIN"                    checar_comando_bash "DOCKER_GF_BIN=/tmp/x.sh scripts/docker-gf.sh ps"

# Revisão da guarda, rodada 4.
# shellcheck disable=SC2016 # o texto do comando, sem expandir, é o que a guarda recebe
espera_falha   "cd \$HOME/../.. e opt"            com_cwd /home/x/repo 'cd $HOME/../.. && ls opt/gestor-financeiro'
# shellcheck disable=SC2016 # idem
espera_falha   "cd \$PWD/../../.. e opt"          com_cwd /home/x/repo 'cd "$PWD/../../.." && cat opt/gestor-financeiro/infra/docker-compose.yml'
# shellcheck disable=SC2016 # idem
espera_falha   "find \${HOME}/../.."              com_cwd /home/x/repo 'find "${HOME}/../.." -name compose.yml'
espera_falha   "timeout -s KILL antes do backup.sh" checar_comando_bash "GF_TMP=/tmp/gf-x timeout -s KILL 600 infra/backup/backup.sh"
espera_falha   "nice -n antes do backup.sh"        checar_comando_bash "GF_TMP=/tmp/gf-x nice -n 19 infra/backup/backup.sh"
espera_falha   "timeout --signal= antes do restaurar.sh" checar_comando_bash "timeout --signal=KILL 60 infra/backup/restaurar.sh p -i -"
espera_falha   "env -C e ./backup.sh"              com_cwd /home/x/repo "env -C infra/backup ./backup.sh"
espera_falha   "cd / e glob op*"                   checar_comando_bash "cd / && ls op*"
espera_falha   "cd / e glob o?t"                   checar_comando_bash "cd / && ls o?t"
espera_falha   "cd / e grep em *"                  checar_comando_bash "cd / && grep -rn POSTGRES *"
espera_falha   "cd -P /"                           checar_comando_bash "cd -P / && ls opt/gestor-financeiro"
espera_falha   "cd -- /"                           checar_comando_bash "cd -- / && ls opt/x"
espera_falha   "if cd /"                           checar_comando_bash "if cd /; then ls opt/x; fi"
espera_falha   "{ cd /; }"                         checar_comando_bash "{ cd /; ls opt/x; }"
espera_falha   "GIT_PAGER com docker"              checar_comando_bash "GIT_PAGER=\"docker stop gestor-api-1\" git log"
espera_sucesso "git add do docker-compose.yml"     checar_comando_bash "git add infra/docker-compose.yml"
espera_sucesso "git diff do docker-compose.yml"    checar_comando_bash "git diff origin/main -- infra/docker-compose.yml"
espera_sucesso "find -name docker-compose.yml"     checar_comando_bash "find . -name docker-compose.yml"
espera_sucesso "glob no repositório passa"         checar_comando_bash "ls src/*.ts"
mkdir -p "$tmp/repo-falso/infra/secrets" "$tmp/repo-falso/scripts"
printf 'x\n' >"$tmp/repo-falso/infra/secrets/pg_senha_app"
espera_falha   "gf: montar diretório com infra/secrets" gf run --rm --name gf-x -v "$tmp/repo-falso:/m:ro" alpine ls /m
espera_sucesso "gf: montar subdiretório sem segredo"  gf run --rm --name gf-x -v "$tmp/repo-falso/scripts:/m:ro" alpine ls /m

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

# Exceção estreita (FK composta, decisão do CEO na E3): (casal_id, id) contém a chave primária, então o soft
# delete não muda nada nela; o Postgres não aceita FK para índice parcial.
alvo_de_fk="$(sql V0011__g.sql "ALTER TABLE boletos.x ADD CONSTRAINT uq_x_casal_id UNIQUE (casal_id, id);")"
espera_sucesso "UNIQUE (casal_id, id) para FK composta" checar_conteudo_de_migracao "$alvo_de_fk"

outra_coluna="$(sql V0011__h.sql "ALTER TABLE boletos.x ADD CONSTRAINT uq_x_nome UNIQUE (casal_id, nome);")"
espera_falha   "UNIQUE (casal_id, coluna de negócio) continua recusado" checar_conteudo_de_migracao "$outra_coluna"

so_id="$(sql V0011__i.sql "ALTER TABLE boletos.x ADD CONSTRAINT uq_x_id UNIQUE (casal_id, id, nome);")"
espera_falha   "UNIQUE (casal_id, id, mais colunas) continua recusado" checar_conteudo_de_migracao "$so_id"

indice_alvo="$(sql V0011__j.sql "CREATE UNIQUE INDEX ux_x_casal_id ON boletos.x (casal_id, id);")"
espera_falha   "índice UNIQUE (casal_id, id) sem ADD CONSTRAINT continua recusado" checar_conteudo_de_migracao "$indice_alvo"

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
confere_hook "hook do Claude barra editar em /opt" 2 '{"file_path":"/opt/gestor-financeiro/infra/docker-compose.yml","new_string":"a"}'
confere_hook "hook do Claude barra escrever em /opt" 2 '{"file_path":"/opt/x.txt","content":"a"}'
confere_hook "hook do Claude barra //opt" 2 '{"file_path":"//opt/x.txt","content":"a"}'
confere_hook "hook do Claude barra /home/../../opt" 2 '{"file_path":"/home/pedo/../../opt/x.txt","content":"a"}'

hook_bash="$raiz/.claude/hooks/guardar-bash.sh"
roda_hook_bash() { printf '%s' "$1" | CLAUDE_PROJECT_DIR="$raiz" "$hook_bash" >/dev/null 2>&1; echo $?; }
confere_bash() { if [ "$(roda_hook_bash "$3")" = "$2" ]; then ok "$1"; else falha "$1"; fi; }
confere_bash "hook Bash deixa gradlew lint"        0 '{"tool_name":"Bash","tool_input":{"command":"cd android && ./gradlew lint"}}'
confere_bash "hook Bash barra gradlew --scan"      2 '{"tool_name":"Bash","tool_input":{"command":"./gradlew lint --scan"}}'
confere_bash "hook Bash barra ler keystore.properties" 2 '{"tool_name":"Bash","tool_input":{"command":"cat \"android/keystore.properties\""}}'
confere_bash "hook Bash barra ler ~/.android"      2 '{"tool_name":"Bash","tool_input":{"command":"ls -la ~/.android/"}}'
confere_bash "hook Bash deixa git status"          0 '{"tool_name":"Bash","tool_input":{"command":"git status"}}'
confere_bash "hook Bash barra /opt"                2 '{"tool_name":"Bash","tool_input":{"command":"ls /opt/gestor-financeiro"}}'
confere_bash "hook Bash barra docker no gestor"    2 '{"tool_name":"Bash","tool_input":{"command":"docker compose -p gestor down"}}'
confere_bash "hook Bash barra pkill"               2 '{"tool_name":"Bash","tool_input":{"command":"pkill -f GestorFinanceiro.Api"}}'
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
