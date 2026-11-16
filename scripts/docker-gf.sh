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
#   build -t gf-* [-f arquivo] <contexto>
#   compose -p gf-* [-f arquivo] [--project-directory dir] [--env-file arquivo] <subcomando> ...
# Caminho do host (montagem, -f, contexto) só dentro do repositório ou de /tmp, nunca /opt nem o socket do docker.
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
  case "$r" in "$raiz" | "$raiz"/* | /tmp | /tmp/*) ;; *) return 1 ;; esac
  [[ "$r" != *docker.sock* ]]
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

# --opcao=valor vira "--opcao valor".
declare -a ARGS=()
separar_iguais() {
  ARGS=()
  local a
  for a in "$@"; do
    if [[ "$a" =~ ^--[a-z][a-z-]*= ]]; then ARGS+=("${a%%=*}" "${a#*=}"); else ARGS+=("$a"); fi
  done
}

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
    nome_do_gestor "${1%%:*}" && recusa "pull de '$1'"
    exec "$DOCKER" pull "$1" ;;

  run)
    nome=""
    while [ $# -gt 0 ]; do
      case "$1" in
        --rm | -d | --detach | -i | -t | -it | -ti | --init | --read-only) saida+=("$1"); shift ;;
        --name) nosso "${2:-}" || recusa "run com --name '${2:-}' (use gf-*)"; nome="$2"; saida+=("$1" "$2"); shift 2 ;;
        -p | --publish)
          [[ "${2:-}" =~ ^(127\.0\.0\.1:)?[0-9]+:[0-9]+(/(tcp|udp))?$ ]] || recusa "run com -p '${2:-}' (só [127.0.0.1:]porta:porta)"
          saida+=("$1" "$2"); shift 2 ;;
        -e | --env | -w | --workdir | -u | --user | -l | --label | --entrypoint | --tmpfs | --hostname | -h)
          [ $# -ge 2 ] || recusa "run com $1 sem valor"; saida+=("$1" "$2"); shift 2 ;;
        --network | --net)
          case "${2:-}" in none | bridge) ;; *) nosso "${2:-}" || recusa "run na rede '${2:-}'" ;; esac
          saida+=("$1" "$2"); shift 2 ;;
        -v | --volume)
          origem="${2%%:*}"
          if [[ "$origem" == /* || "$origem" == .* ]]; then caminho_permitido "$origem" || recusa "run montando '$origem' (só o repositório ou /tmp)"
          else nosso "$origem" || recusa "run com o volume '$origem' (use gf-*)"; fi
          saida+=("$1" "$2"); shift 2 ;;
        --) shift; break ;;
        -*) recusa "run com a opção $1 (fora da lista)" ;;
        *) break ;;
      esac
    done
    [ -n "$nome" ] || recusa "run sem --name gf-*"
    [ $# -gt 0 ] || recusa "run sem imagem"
    nome_do_gestor "${1%%:*}" && recusa "run da imagem '$1'"
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
      else caminho_permitido "$lado" || recusa "cp com '$lado' (só o repositório ou /tmp)"; fi
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

  compose)
    if ! { [ "${1:-}" = -p ] || [ "${1:-}" = --project-name ]; } || ! nosso "${2:-}"; then
      recusa "compose sem -p gf-* logo depois de compose"
    fi
    saida+=("$1" "$2"); shift 2
    while [ $# -gt 0 ]; do
      case "$1" in
        -f | --file | --env-file)
          caminho_permitido "${2:-}" || recusa "compose com o arquivo '${2:-}' (só o repositório ou /tmp)"
          if [ "$1" != --env-file ] && grep -qE '^[[:space:]]*(container_name|external)[[:space:]]*:' -- "$2"; then
            recusa "compose com nome fixo ou recurso externo em '$2'"
          fi
          saida+=("$1" "$2"); shift 2 ;;
        --project-directory) caminho_permitido "${2:-}" || recusa "compose com o diretório '${2:-}'"; saida+=("$1" "$2"); shift 2 ;;
        --profile | --ansi | --progress) saida+=("$1" "${2:-}"); shift 2 ;;
        -*) recusa "compose com a opção $1 antes do subcomando (fora da lista)" ;;
        *) break ;;
      esac
    done
    [ $# -gt 0 ] || recusa "compose sem subcomando"
    exec "$DOCKER" compose "${saida[@]}" "$@" ;;

  *) recusa "subcomando '$sub' (fora da lista)" ;;
esac
