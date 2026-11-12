#!/usr/bin/env bash
# Teste de subida do zero: o infra/docker-compose.yml real sobe o postgres e a api com segredos
# gerados pelo próprio infra/scripts/gerar-segredos.sh, sem nenhum ajuste de dono ou modo nos
# arquivos de segredo (nenhum "chown" à mão).
#
# Tudo acontece num diretório temporário (a raiz falsa) e num projeto do compose com nome
# aleatório; nada encosta em infra/secrets nem em infra/data do repositório. O compose real é
# usado com --project-directory na raiz falsa (./secrets, ./data e ./postgres/init apontam
# para ela) e um override mínimo, só com o que a raiz falsa não tem:
#   - api: a imagem construída antes a partir do repositório, no lugar do "build" (o contexto
#     ".." da raiz falsa não tem o código);
#   - rede "app": outra sub-rede, para não colidir com a instalação real se ela estiver de pé.
# O passo 1 do gerar-segredos.sh (pasta das chaves e gf_chave_servico com dono 1654) é feito
# por um container, porque o teste não usa sudo. Os outros segredos ficam como o script os
# gravou. Derruba com "docker compose down", sem -v.
#
# Passos: postgres saudável → api --migrar → api --inicializar-chaves → api de pé → /saude e
# /casal/cadastro respondem (esse consulta o banco) → a conexão da api no postgres é do gf_app.
#
# Uso: infra/testes/testar-subida.sh      (precisa de docker; a primeira execução constrói a api)
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
LIMITE="${GF_LIMITE_SUBIDA:-180}"   # segundos para cada espera
IMAGEM_PG="postgres:17-alpine"

umask 077
ID="$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n')"
PROJETO="gf-teste-subida-$ID"
IMAGEM_API="gf-teste-subida-api:$ID"
T="$(mktemp -d)"
R="$T/raiz"
COMPOSE=(docker compose --env-file "$T/vazio.env" -f "$REPO/infra/docker-compose.yml" -f "$T/override.yml"
  --project-directory "$R/infra" -p "$PROJETO")

# Variáveis exigidas pela interpolação do compose real (web e duckdns não sobem).
export DOMINIO=teste-subida.invalid DUCKDNS_TOKEN=teste-subida DUCKDNS_SUBDOMINIO=teste-subida

# Root dentro de um container, na raiz falsa: cria a pasta das chaves com dono 1654 e, na
# limpeza, apaga o que o postgres (uid 70) e a api (uid 1654) deixaram.
como_root() {
  docker run --rm --network none -v "$R/infra/data:/dados" -v "$R/infra/secrets:/segredos" "$IMAGEM_PG" \
    sh -c "$1" >/dev/null 2>&1
}

limpar() {
  set +e
  if [ -f "$T/override.yml" ]; then "${COMPOSE[@]}" down >/dev/null 2>&1; fi
  [ -d "$R/infra/data" ] && [ -d "$R/infra/secrets" ] && como_root 'rm -rf /dados/postgres /dados/chaves'
  docker image rm "$IMAGEM_API" >/dev/null 2>&1
  rm -rf "$T"
}
trap limpar EXIT
trap 'exit 130' INT TERM

falhar() { echo "FALHA $*"; exit 1; }

# ---------------------------------------------------------------- raiz falsa

mkdir -p "$R/infra/scripts" "$R/infra/data/postgres"
: >"$T/vazio.env"
cp "$REPO/infra/scripts/gerar-segredos.sh" "$R/infra/scripts/"
ln -s "$REPO/infra/postgres" "$R/infra/postgres"
"$R/infra/scripts/gerar-segredos.sh" >/dev/null || falhar "gerar-segredos.sh"
echo "segredos: $(find "$R/infra/secrets" -type f | wc -l) arquivos, pasta $(stat -c %a "$R/infra/secrets")," \
  "arquivos $(find "$R/infra/secrets" -type f -printf '%m\n' | sort -u | tr '\n' ' ')"

como_root 'install -d -o 1654 -m 700 /dados/chaves && chown 1654 /segredos/gf_chave_servico' \
  || falhar "passo 1 do gerar-segredos.sh (chown 1654)"

# D23: o slot 1 fica como o script o criou (vazio = não configurado); o slot 2 recebe um valor
# sintético, como a pessoa faria pelo terminal. A api tem de subir nos dois casos.
for f in pluggy_2_client_id pluggy_2_client_secret; do
  chmod u+w "$R/infra/secrets/$f"
  printf '%s' "valor-sintetico-$f" >"$R/infra/secrets/$f"
  chmod 444 "$R/infra/secrets/$f"
done

# Terceiro octeto aleatório entre 100 e 227: 172.31.<n>.0/24.
OCTETO=$((100 + 0x${ID:0:2} % 128))
cat >"$T/override.yml" <<EOF
services:
  api:
    build: !reset null
    image: $IMAGEM_API
    pull_policy: never
    networks: !override
      app: { ipv4_address: 172.31.$OCTETO.10 }
      dados: {}
networks:
  app:
    ipam:
      config: !override
        - subnet: 172.31.$OCTETO.0/24
EOF

echo "construindo a api ($IMAGEM_API)"
docker build -q -f "$REPO/backend/src/Host/GestorFinanceiro.Api/Dockerfile" -t "$IMAGEM_API" "$REPO" >/dev/null \
  || falhar "docker build da api"

# ---------------------------------------------------------------- subida

# Espera o healthcheck do postgres. Mostra só as linhas de erro do log (nunca segredos: o
# init não os imprime).
esperar_postgres_saudavel() {
  local id estado inicio=$SECONDS
  while [ $((SECONDS - inicio)) -lt "$LIMITE" ]; do
    id="$("${COMPOSE[@]}" ps -aq postgres 2>/dev/null || true)"
    estado="$(docker inspect -f '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}}' "$id" 2>/dev/null || true)"
    [ "$estado" != "running healthy" ] || return 0
    sleep 2
  done
  echo "  estado do postgres: [${estado:-sem container}] depois de ${LIMITE}s"
  "${COMPOSE[@]}" logs --no-color postgres 2>&1 | grep -i -E 'denied|error|fatal' | sort -u | head -5 | sed 's/^/  | /'
  return 1
}

# GET http://api:8080<caminho> de dentro do postgres (mesma rede "dados"; a imagem da api não
# tem cliente HTTP).
http_api() { "${COMPOSE[@]}" exec -T postgres wget -q -T 5 -O - "http://api:8080$1" 2>/dev/null; }

esperar_api_saudavel() {
  local inicio=$SECONDS
  while [ $((SECONDS - inicio)) -lt "$LIMITE" ]; do
    http_api /api/v1/saude | grep -q '"ok"' && return 0
    sleep 2
  done
  echo "  /api/v1/saude não respondeu em ${LIMITE}s"
  "${COMPOSE[@]}" ps -a --format '  {{.Service}}: {{.Status}}' 2>/dev/null
  "${COMPOSE[@]}" logs --no-color api 2>&1 | grep -i -E 'denied|exception|fail' | sort -u | head -5 | sed 's/^/  | /'
  return 1
}

"${COMPOSE[@]}" up -d postgres >/dev/null 2>&1 || falhar "compose up postgres"
esperar_postgres_saudavel || falhar "postgres não ficou saudável"
echo "ok postgres saudável"

# "Saudável" não basta: se o init (00-papeis.sh) falhar, o restart do compose sobe o postgres de
# novo com a pasta de dados já criada, sem rodar o init, e ele fica saudável sem os papéis.
papeis="$("${COMPOSE[@]}" exec -T postgres psql -X -q -t -A -U postgres -d gestor \
  -c "SELECT string_agg(rolname, ' ' ORDER BY rolname) FROM pg_roles WHERE rolname IN ('gf_app', 'gf_migrator')" 2>/dev/null || true)"
if [ "$papeis" != "gf_app gf_migrator" ]; then
  echo "  papéis no banco: [$papeis]; reinícios do postgres: $(docker inspect -f '{{.RestartCount}}' "$("${COMPOSE[@]}" ps -aq postgres)")"
  "${COMPOSE[@]}" logs --no-color postgres 2>&1 | grep -i -E 'denied|error|fatal' | sort -u | head -5 | sed 's/^/  | /'
  falhar "o init do postgres não criou gf_app e gf_migrator"
fi
echo "ok init criou gf_app e gf_migrator"

# Roda um modo da api e, se falhar, mostra só as linhas de erro (nunca segredos). A saída fica
# em $T/api$1.saida para conferências depois.
rodar_api() {
  local saida codigo=0
  saida="$("${COMPOSE[@]}" run --rm --no-deps api "$1" 2>&1)" || codigo=$?
  printf '%s\n' "$saida" >"$T/api$1.saida"
  [ "$codigo" -eq 0 ] && return 0
  grep -i -E 'denied|exception|unauthorized|password|role' <<<"$saida" | sort -u | head -5 | sed 's/^/  | /'
  return 1
}
rodar_api --migrar || falhar "api --migrar"
# O Npgsql não pode tentar o GSSAPI (o projeto não usa Kerberos): a imagem não tem a lib e o
# .NET imprimia "Cannot load library libgssapi_krb5.so.2" em toda execução.
if grep -qi libgssapi "$T/api--migrar.saida"; then
  grep -i libgssapi "$T/api--migrar.saida" | sort -u | head -3 | sed 's/^/  | /'
  falhar "a saída do --migrar tem libgssapi"
fi
echo "ok api --migrar"
rodar_api --inicializar-chaves || falhar "api --inicializar-chaves"
echo "ok api --inicializar-chaves"

"${COMPOSE[@]}" up -d --no-build api >/dev/null 2>&1 || falhar "compose up api"
esperar_api_saudavel || falhar "api não ficou saudável"
echo "ok api saudável"

cadastro="$(http_api /api/v1/casal/cadastro)" || falhar "GET /api/v1/casal/cadastro"
grep -q '"disponivel":true' <<<"$cadastro" || falhar "cadastro respondeu [$cadastro]"
echo "ok /api/v1/casal/cadastro consultou o banco"

papeis="$("${COMPOSE[@]}" exec -T postgres psql -X -q -t -A -U postgres -d gestor \
  -c "SELECT DISTINCT usename FROM pg_stat_activity WHERE datname = 'gestor' AND application_name <> 'psql' AND usename IS NOT NULL" 2>/dev/null)" \
  || falhar "pg_stat_activity"
grep -qx gf_app <<<"$papeis" || falhar "a api não está conectada como gf_app (conexões: $(tr '\n' ' ' <<<"$papeis"))"
echo "ok a api conectou com o papel gf_app"

# A conexão da api (string App) também não tenta o GSSAPI.
if "${COMPOSE[@]}" logs --no-color api 2>&1 | grep -qi libgssapi; then
  falhar "o log da api tem libgssapi"
fi
echo "ok sem libgssapi no log da api"

# Segredos da Pluggy (D23): só a api os recebe; o vazio chega vazio e o preenchido chega igual
# (compara tamanho e sha, nunca imprime o conteúdo).
for f in pluggy_1_client_id pluggy_1_client_secret pluggy_2_client_id pluggy_2_client_secret; do
  dentro="$("${COMPOSE[@]}" exec -T api sh -c "wc -c </run/secrets/$f; sha256sum </run/secrets/$f" 2>/dev/null)" \
    || falhar "a api não lê /run/secrets/$f"
  fora="$(wc -c <"$R/infra/secrets/$f"; sha256sum <"$R/infra/secrets/$f")"
  [ "$dentro" = "$fora" ] || falhar "/run/secrets/$f na api difere do arquivo"
  if "${COMPOSE[@]}" exec -T postgres test -e "/run/secrets/$f" 2>/dev/null; then
    falhar "o postgres recebeu $f"
  fi
done
[ "$(wc -c <"$R/infra/secrets/pluggy_1_client_id")" = 0 ] || falhar "pluggy_1_client_id deveria estar vazio"
[ "$("${COMPOSE[@]}" exec -T api printenv Pluggy__PastaDeSegredos 2>/dev/null)" = /run/secrets ] \
  || falhar "Pluggy__PastaDeSegredos da api"
echo "ok segredos da Pluggy só na api (slot 1 vazio, slot 2 sintético)"

echo "subida do zero ok"
