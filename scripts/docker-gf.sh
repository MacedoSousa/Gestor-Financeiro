#!/usr/bin/env bash
# Porta única do docker para os agentes (D24). A instalação de verdade roda neste PC (projeto "gestor" do compose,
# containers gestor-*, /opt/gestor-financeiro); o hook do Bash barra o docker direto e deixa passar este script, que
# confere os argumentos já separados pelo shell, por lista positiva:
#   ps, images, version, info                       leitura
#   pull <imagem>                                   nunca uma imagem gestor*
#   run  --name gf-* [opções da lista] <imagem> ...  o comando de dentro do container não é conferido
#   exec|logs|stop|restart|rm|kill|inspect|port|top|wait <container gf-*> ...
#                                                   o container é conferido no docker (nome e projeto do compose)
#   cp   <gf-*:caminho> <local> | <local> <gf-*:caminho>
#   build -t gf-* [-f arquivo] <contexto>   (contexto sem segredo: para as imagens do projeto, uma cópia limpa,
#                                             git archive HEAD | tar -x -C /tmp/gf-ctx; o README de infra/secrets
#                                             que vem junto não conta como segredo)
# compose não passa por aqui: os testes do repositório o chamam por dentro, com -p aleatório, e um compose qualquer pode
# montar o que quiser (revisão da guarda, rodada 3).
# Caminho do host (montagem, -f, contexto, cp) só dentro do repositório, de /tmp/gf-* ou do scratchpad da sessão
# (/tmp/claude-<uid>/<projeto>/<sessão>/scratchpad...);
# nunca /opt nem o socket do docker. Porta publicada só em 127.0.0.1.
# Uso: scripts/docker-gf.sh <subcomando> [argumentos]. DOCKER_GF_BIN troca o binário (os testes usam um falso).
set -euo pipefail

raiz="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=scripts/guardas/regras.sh
source "$raiz/scripts/guardas/regras.sh"
DOCKER="${DOCKER_GF_BIN:-docker}"

recusa() { echo "docker-gf: recusado: $* (D24)." >&2; exit 2; }
nosso() { [[ "$1" =~ ^gf-[A-Za-z0-9_.-]+$ ]]; }

# Caminho do host que um container ou o build pode ver: dentro do repositório ou de /tmp.
caminho_permitido() {
  local r
  r="$(realpath -m -- "$1")"
  # O scratchpad é o da sessão (/tmp/claude-<uid>/<projeto>/<sessão>/scratchpad...), não o /tmp/claude-* inteiro.
  case "$r" in "$raiz" | "$raiz"/* | /tmp/gf-* | /tmp/claude-*/*/*/scratchpad*) ;; *) return 1 ;; esac
  [[ "$r" != *docker.sock* ]] || return 1
  # Nem segredo de verdade (regra 5): o caminho em si, ou um diretório que tenha infra/secrets, infra/data, backups ou
  # .env dentro (o container roda como root e leria o que o próprio usuário não lê). Monte um subdiretório.
  eh_caminho_de_segredo "${r#"$raiz"/}" && return 1
  # Os lugares de segredo do repositório: nem eles, nem algo dentro deles, nem um diretório que os contenha (a raiz do
  # repositório, infra/). Monte um subdiretório (scripts/, frontend/...).
  local lugar
  for lugar in "$raiz/infra/secrets" "$raiz/infra/data" "$raiz/backups" "$raiz/.env" "$raiz/infra/.env"; do
    if [ "$r" = "$lugar" ] || [[ "$r" == "$lugar"/* || "$lugar" == "$r"/* ]]; then return 1; fi
  done
  # Noutra árvore (outra cópia do repositório em /tmp): backups e .env pela existência; infra/secrets e infra/data por
  # tem_segredo (o README versionado não conta; o que não dá para ler conta).
  if [ -d "$r" ]; then
    for lugar in backups .env infra/.env; do [ -e "$r/$lugar" ] && return 1; done
    for lugar in "$r/infra/secrets" "$r/infra/data" "$r/secrets" "$r/data"; do
      [ "$lugar" = "$r/secrets" ] || [ "$lugar" = "$r/data" ] && [ "${r##*/}" != infra ] && continue
      tem_segredo "$lugar" && return 1
    done
    [ "${r##*/}" = secrets ] || [ "${r##*/}" = data ] && [ "$(basename -- "$(dirname -- "$r")")" = infra ] && tem_segredo "$r" && return 1
  fi
  return 0
}

# Diretório de segredo com algo além do README versionado (infra/secrets/README.md); o que não dá para ler conta como
# segredo (o container roda como root e leria).
tem_segredo() {
  [ -e "$1" ] || return 1
  [ -r "$1" ] && [ -x "$1" ] || return 0
  [ -n "$(find "$1" -mindepth 1 ! -name README.md 2>/dev/null | head -n1)" ]
}

# O container existe, se chama gf-* e não é de um projeto do compose que não seja gf-*.
container_nosso() {
  local alvo="$1" nome projeto
  nosso "$alvo" || return 1
  nome="$("$DOCKER" inspect -f '{{.Name}}' -- "$alvo" 2>/dev/null)" || return 1
  [[ "${nome#/}" == gf-* ]] || return 1
  projeto="$("$DOCKER" inspect -f '{{index .Config.Labels "com.docker.compose.project"}}' -- "$alvo" 2>/dev/null)" || return 1
  [ -z "$projeto" ] || [[ "$projeto" == gf-* ]]
}
exigir_container() { container_nosso "$1" || recusa "$sub em '$1' (só container gf-* que exista e não seja do projeto gestor)"; }

# --opcao=valor vira "--opcao valor", só nas opções do docker: para na primeira palavra que não é opção nem valor de
# opção (a imagem ou o container), e o comando de dentro do container chega como veio (ls --color=never).
declare -a ARGS=()
separar_iguais() {
  ARGS=()
  local a anterior="" parou=0
  for a in "$@"; do
    if [ "$parou" -eq 0 ] && [[ "$a" != -* ]] && [[ -z "$anterior" || "$anterior" != -* || "$anterior" == --*=* ]]; then parou=1; fi
    if [ "$parou" -eq 0 ] && [[ "$a" =~ ^--[a-z][a-z-]*= ]]; then ARGS+=("${a%%=*}" "${a#*=}"); else ARGS+=("$a"); fi
    anterior="$a"
  done
}

# Imagem da instalação de verdade pelo nome, também com registro ou namespace (docker.io/library/gestor-api).
imagem_do_gestor() { local img="${1%%@*}"; img="${img##*/}"; nome_do_gestor "${img%%:*}"; }

[ $# -gt 0 ] || recusa "sem subcomando"
sub="$1"; shift
separar_iguais "$@"
set -- "${ARGS[@]+"${ARGS[@]}"}"
declare -a saida=()

case "$sub" in
  ps | images | version | info)
    exec "$DOCKER" "$sub" "$@" ;;

  pull)
    [ $# -eq 1 ] || recusa "pull com mais de uma imagem"
    imagem_do_gestor "$1" && recusa "pull de '$1'"
    exec "$DOCKER" pull "$1" ;;

  run)
    nome=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --rm | -d | --detach | -i | -t | -it | -ti | --init | --read-only) saida+=("$1"); shift ;;
        --name) nosso "${2:-}" || recusa "run com --name '${2:-}' (use gf-*)"; nome="$2"; saida+=("$1" "$2"); shift 2 ;;
        -p | --publish)
          [[ "${2:-}" =~ ^127\.0\.0\.1:[0-9]+:[0-9]+(/(tcp|udp))?$ ]] || recusa "run com -p '${2:-}' (só 127.0.0.1:porta:porta)"
          saida+=("$1" "$2"); shift 2 ;;
        -e | --env | -w | --workdir | -u | --user | -l | --label | --entrypoint | --tmpfs | --hostname | -h | --shm-size \
          | --platform | --add-host | --health-cmd | --health-interval | --health-timeout | --health-retries)
          [ $# -ge 2 ] || recusa "run com $1 sem valor"; saida+=("$1" "$2"); shift 2 ;;
        --network | --net)
          case "${2:-}" in none | bridge) ;; *) nosso "${2:-}" || recusa "run na rede '${2:-}'" ;; esac
          saida+=("$1" "$2"); shift 2 ;;
        --env-file) caminho_permitido "${2:-}" || recusa "run com --env-file '${2:-}'"; saida+=("$1" "$2"); shift 2 ;;
        -v | --volume)
          origem="${2%%:*}"
          if [[ "${2:-}" != *:* && "${2:-}" == /* ]]; then : # volume anônimo: só o caminho de dentro do container
          elif [[ "$origem" == /* || "$origem" == .* ]]; then caminho_permitido "$origem" || recusa "run montando '$origem' (só o repositório, /tmp/gf-* ou o scratchpad)"
          else nosso "$origem" || recusa "run com o volume '$origem' (use gf-*)"; fi
          saida+=("$1" "$2"); shift 2 ;;
        --) recusa "run com -- (o docker leria o resto como opção)" ;;
        -*) recusa "run com a opção $1 (fora da lista)" ;;
        *) break ;;
      esac
    done
    [ -n "$nome" ] || recusa "run sem --name gf-*"
    [ $# -gt 0 ] || recusa "run sem imagem"
    imagem_do_gestor "$1" && recusa "run da imagem '$1'"
    exec "$DOCKER" run "${saida[@]}" "$@" ;;

  exec)
    while [ $# -gt 0 ]; do
      case "$1" in
        -i | -t | -it | -ti | -d | --detach | --interactive | --tty) saida+=("$1"); shift ;;
        -e | --env | -u | --user | -w | --workdir) [ $# -ge 2 ] || recusa "exec com $1 sem valor"; saida+=("$1" "$2"); shift 2 ;;
        -*) recusa "exec com a opção $1 (fora da lista)" ;;
        *) break ;;
      esac
    done
    [ $# -ge 2 ] || recusa "exec sem container e comando"
    exigir_container "$1"
    exec "$DOCKER" exec "${saida[@]+"${saida[@]}"}" "$@" ;;

  logs)
    while [ $# -gt 0 ]; do
      case "$1" in
        -f | --follow | -t | --timestamps) saida+=("$1"); shift ;;
        -n | --tail | --since | --until) [ $# -ge 2 ] || recusa "logs com $1 sem valor"; saida+=("$1" "$2"); shift 2 ;;
        -*) recusa "logs com a opção $1 (fora da lista)" ;;
        *) break ;;
      esac
    done
    [ $# -eq 1 ] || recusa "logs com um container só"
    exigir_container "$1"
    exec "$DOCKER" logs "${saida[@]+"${saida[@]}"}" "$1" ;;

  stop | restart | rm | kill | wait)
    while [ $# -gt 0 ]; do
      case "$1" in
        -f | --force | -v | --volumes) [ "$sub" = rm ] || recusa "$sub com $1"; saida+=("$1"); shift ;;
        -t | --time) case "$sub" in stop | restart) saida+=("$1" "${2:-}"); shift 2 ;; *) recusa "$sub com $1" ;; esac ;;
        -s | --signal) [ "$sub" = kill ] || recusa "$sub com $1"; saida+=("$1" "${2:-}"); shift 2 ;;
        -*) recusa "$sub com a opção $1 (fora da lista)" ;;
        *) break ;;
      esac
    done
    [ $# -gt 0 ] || recusa "$sub sem container"
    for alvo in "$@"; do exigir_container "$alvo"; done
    exec "$DOCKER" "$sub" "${saida[@]+"${saida[@]}"}" "$@" ;;

  inspect | port | top)
    if [ "$sub" = inspect ] && { [ "${1:-}" = -f ] || [ "${1:-}" = --format ]; }; then saida+=("$1" "${2:-}"); shift 2; fi
    [ $# -gt 0 ] || recusa "$sub sem container"
    if [ "$sub" = inspect ]; then for alvo in "$@"; do exigir_container "$alvo"; done
    else exigir_container "$1"; fi
    exec "$DOCKER" "$sub" "${saida[@]+"${saida[@]}"}" "$@" ;;

  cp)
    [ $# -eq 2 ] || recusa "cp com origem e destino"
    lados=0
    for lado in "$1" "$2"; do
      if [[ "$lado" == *:* && "$lado" != /* && "$lado" != .* ]]; then exigir_container "${lado%%:*}"; lados=$((lados + 1))
      else caminho_permitido "$lado" || recusa "cp com '$lado' (só o repositório, /tmp/gf-* ou o scratchpad)"; fi
    done
    [ "$lados" -eq 1 ] || recusa "cp com um lado no container gf-* e o outro no host"
    exec "$DOCKER" cp "$1" "$2" ;;

  build)
    while [ $# -gt 1 ]; do
      case "$1" in
        -q | --quiet | --pull | --no-cache) saida+=("$1"); shift ;;
        -t | --tag) nosso "${2%%:*}" || recusa "build com a tag '${2:-}' (use gf-*)"; saida+=("$1" "$2"); shift 2 ;;
        -f | --file) caminho_permitido "${2:-}" || recusa "build com o arquivo '${2:-}'"; saida+=("$1" "$2"); shift 2 ;;
        --build-arg | --target) [ $# -ge 3 ] || recusa "build com $1 sem valor"; saida+=("$1" "$2"); shift 2 ;;
        *) recusa "build com a opção $1 (fora da lista)" ;;
      esac
    done
    [ $# -eq 1 ] || recusa "build sem contexto"
    caminho_permitido "$1" || recusa "build com o contexto '$1'"
    exec "$DOCKER" build "${saida[@]+"${saida[@]}"}" "$1" ;;

  compose) recusa "compose (use os scripts de teste do repositório, que sobem com -p aleatório)" ;;

  *) recusa "subcomando '$sub' (fora da lista)" ;;
esac
