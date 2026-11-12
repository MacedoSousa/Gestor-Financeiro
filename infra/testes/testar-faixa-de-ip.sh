#!/usr/bin/env bash
# Faixa de IP da rede "app" (D16): web e api têm IP fixo; duckdns, whatsapp e ocr pegam IP
# automático. Se o Docker der a um automático o IP de um fixo, o fixo não sobe ("Address already
# in use"). No boot das 04:00 a ordem de subida é aleatória, então o teste força o pior caso:
# primeiro todos os de IP automático, depois web e api.
#
# Usa uma cópia do infra/docker-compose.yml real com só o prefixo 172.30.0. trocado por
# 172.31.<n>. (para não colidir com a instalação de verdade, se estiver de pé): sub-rede,
# ip_range e IPs fixos vêm do arquivo do repositório. Um override troca a imagem de todos os
# serviços por uma mínima que só dorme, sem segredos, volumes, portas, GPU nem restart; o que se testa é a
# alocação de IP, não os programas. Projeto do compose com nome aleatório; derruba com
# "docker compose down", sem -v.
#
# Uso: infra/testes/testar-faixa-de-ip.sh      (precisa de docker)
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
IMAGEM="postgres:17-alpine"

umask 077
ID="$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n')"
PROJETO="gf-teste-faixa-$ID"
OCTETO=$((100 + 0x${ID:0:2} % 128))   # 172.31.<100..227>.0/24
PREFIXO="172.31.$OCTETO."
T="$(mktemp -d)"
COMPOSE=(docker compose --env-file "$T/vazio.env" -f "$T/compose.yml" -f "$T/override.yml"
  --project-directory "$T" -p "$PROJETO" --profile whatsapp --profile ocr)

# Variáveis exigidas pela interpolação do compose real.
export DOMINIO=teste-faixa.invalid DUCKDNS_TOKEN=teste-faixa DUCKDNS_SUBDOMINIO=teste-faixa

limpar() {
  set +e
  [ -f "$T/override.yml" ] && "${COMPOSE[@]}" down >/dev/null 2>&1
  rm -rf "$T"
}
trap limpar EXIT
trap 'exit 130' INT TERM

falhar() { echo "FALHA $*"; exit 1; }

: >"$T/vazio.env"
sed 's/172\.30\.0\./'"$PREFIXO"'/g' "$REPO/infra/docker-compose.yml" >"$T/compose.yml"
if grep -q '172\.30\.' "$T/compose.yml"; then falhar "sobrou 172.30. na cópia do compose (a sub-rede real mudou?)"; fi

AUTOMATICOS=(duckdns whatsapp ocr)
FIXOS=(web api)
{
  echo "services:"
  for s in postgres "${FIXOS[@]}" "${AUTOMATICOS[@]}"; do
    cat <<EOF
  $s:
    image: $IMAGEM
    pull_policy: missing
    restart: "no"
    build: !reset null
    entrypoint: ["sleep", "600"]
    command: !reset []
    env_file: !reset []
    secrets: !reset []
    volumes: !reset []
    ports: !reset []
    depends_on: !reset {}
    healthcheck: !reset {}
    deploy: !reset {}
EOF
  done
} >"$T/override.yml"

"${COMPOSE[@]}" config -q 2>"$T/config.err" || { sed 's/^/  | /' "$T/config.err"; falhar "compose config"; }

ip_de() {
  docker inspect -f "{{(index .NetworkSettings.Networks \"${PROJETO}_app\").IPAddress}}" \
    "$("${COMPOSE[@]}" ps -q "$1")" 2>/dev/null
}

# 1. Os de IP automático primeiro, numa rede recém-criada.
"${COMPOSE[@]}" up -d "${AUTOMATICOS[@]}" >/dev/null 2>"$T/up1.err" \
  || { sed 's/^/  | /' "$T/up1.err"; falhar "subir ${AUTOMATICOS[*]}"; }
for s in "${AUTOMATICOS[@]}"; do echo "  $s: $(ip_de "$s")"; done

# 2. Depois web e api, com os IPs fixos.
if ! "${COMPOSE[@]}" up -d "${FIXOS[@]}" >/dev/null 2>"$T/up2.err"; then
  grep -i -E 'address|error' "$T/up2.err" | sort -u | head -3 | sed 's/^/  | /'
  falhar "web e api não sobem depois dos de IP automático"
fi

# 3. Fixos nos IPs do compose; automáticos fora da faixa dos fixos (.2–.127).
for par in "web:${PREFIXO}2" "api:${PREFIXO}10"; do
  s="${par%%:*}"; esperado="${par#*:}"
  [ "$(ip_de "$s")" = "$esperado" ] || falhar "$s com IP $(ip_de "$s"), esperado $esperado"
done
for s in "${AUTOMATICOS[@]}"; do
  ultimo="$(ip_de "$s")"; ultimo="${ultimo##*.}"
  if [ -z "$ultimo" ] || [ "$ultimo" -lt 128 ]; then falhar "$s com IP $(ip_de "$s"), fora de .128–.255"; fi
done
echo "ok web .2 e api .10 sobem depois de duckdns, whatsapp e ocr (automáticos em .128–.255)"

# 4. O gateway continua o .1: com ip_range e sem gateway, o Docker usaria o primeiro IP da faixa.
gateway="$(docker network inspect -f '{{range .IPAM.Config}}{{.Gateway}}{{end}}' "${PROJETO}_app")"
[ "$gateway" = "${PREFIXO}1" ] || falhar "gateway da rede app é $gateway, esperado ${PREFIXO}1"
echo "ok gateway da rede app em .1"
