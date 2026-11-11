#!/usr/bin/env bash
# shellcheck source-path=SCRIPTDIR
# Cópia do pacote do dia para fora do PC: pendrive (opcional) e rclone (obrigatório).
# Uso: enviar.sh <pacote> <diario|semanal> <cadeia-arquivo>      (chamado pelo backup.sh)
#
# Configuração (ler_env, nunca source do .env): GF_BACKUP_PENDRIVE_MONTAGEM,
# GF_BACKUP_PENDRIVE_PASTA e GF_BACKUP_RCLONE_REMOTO.
#
# Em cada destino: o pacote vai para diario/ e, com tipo semanal (domingo), também para
# semanal/; a cadeia vai para cadeia/. Rotação: diario 7 sempre, semanal 4 aos domingos;
# cadeia/ nunca é rotacionada (Decisão 1).
#
# Pendrive primeiro. "nao configurado" (as duas variáveis vazias) e "nao montado" só avisam;
# só uma das duas preenchida é "configuracao incompleta" e falha. Qualquer outra falha do
# pendrive conta para a saída. O rclone roda sempre, mesmo se o pendrive falhar.
# Sai ≠0 se o rclone falhar ou se o pendrive falhar (fora os dois avisos).
#
# Log (Decisão 9): só etapa, pasta, nome de arquivo, contagem e código. A saída do rclone
# (stdout e stderr) nunca vai para o log.
set -euo pipefail
umask 077

DIR_SCRIPTS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
source "$DIR_SCRIPTS/lib.sh"

uso() { echo "uso: enviar.sh <pacote> <diario|semanal> <cadeia-arquivo>" >&2; exit 2; }

[ "$#" -eq 3 ] || uso
pacote="$1" tipo="$2" cadeia="$3"

PADRAO_PACOTE='^gestor-[0-9]{4}-[0-9]{2}-[0-9]{2}\.tar\.age$'
PADRAO_CADEIA='^cadeia-[0-9]{4}-[0-9]{2}-[0-9]{2}\.txt$'
nome_pacote="$(basename "$pacote")"
nome_cadeia="$(basename "$cadeia")"

{ [ -f "$pacote" ] && [ ! -L "$pacote" ] && [[ "$nome_pacote" =~ $PADRAO_PACOTE ]]; } \
  || falhar "envio: pacote invalido"
case "$tipo" in diario|semanal) ;; *) falhar "envio: tipo invalido" ;; esac
{ [ -f "$cadeia" ] && [ ! -L "$cadeia" ] && [[ "$nome_cadeia" =~ $PADRAO_CADEIA ]]; } \
  || falhar "envio: cadeia invalida"

GF_RAIZ="${GF_RAIZ:-$(cd "$DIR_SCRIPTS/../.." && pwd)}"

pastas=(diario)
[ "$tipo" != semanal ] || pastas+=(semanal)

falhou=0
erro() { echo "FALHA $*" >&2; falhou=1; }

# O .parcial em andamento no pendrive some se o script for interrompido.
parcial_atual=""
trap '[ -z "$parcial_atual" ] || rm -f -- "$parcial_atual"' EXIT
trap 'exit 130' INT TERM

sha() { sha256sum -- "$1" | cut -d' ' -f1; }

# ---------------------------------------------------------------- pendrive

# Copia <origem> para <pasta destino> como .parcial, confere o sha256 e renomeia.
copiar_pendrive() {
  local origem="$1" destino="$2" nome
  nome="$(basename "$origem")"
  mkdir -p -- "$destino" 2>/dev/null || { erro "pendrive mkdir pasta=$(basename "$destino")"; return 1; }
  parcial_atual="$destino/.$nome.parcial"
  if cp -- "$origem" "$parcial_atual" 2>/dev/null \
    && [ "$(sha "$origem")" = "$(sha "$parcial_atual")" ] \
    && mv -f -- "$parcial_atual" "$destino/$nome" 2>/dev/null; then
    parcial_atual=""
    log "pendrive: $(basename "$destino")/$nome"
    return 0
  fi
  rm -f -- "$parcial_atual"
  parcial_atual=""
  erro "pendrive copia arquivo=$nome pasta=$(basename "$destino")"
  return 1
}

enviar_pendrive() {
  local montagem pasta m p d codigo
  montagem="$(ler_env GF_BACKUP_PENDRIVE_MONTAGEM)"
  pasta="$(ler_env GF_BACKUP_PENDRIVE_PASTA)"
  # As duas vazias: pendrive não usado (aviso). Só uma: erro de configuração.
  if [ -z "$montagem" ] && [ -z "$pasta" ]; then
    log "aviso pendrive nao configurado"
    return 0
  fi
  if [ -z "$montagem" ] || [ -z "$pasta" ]; then
    erro "pendrive configuracao incompleta"
    return 0
  fi
  # A pasta tem que ser absoluta e, depois de resolver .. e links, ficar dentro da montagem.
  if [[ "$montagem" != /* || "$pasta" != /* ]]; then
    erro "pendrive pasta fora da montagem"
    return 0
  fi
  m="$(realpath -m -- "$montagem")"
  p="$(realpath -m -- "$pasta")"
  case "$p/" in
    "${m%/}/"*) ;;
    *) erro "pendrive pasta fora da montagem"; return 0 ;;
  esac
  if ! mountpoint -q -- "$m"; then
    log "aviso pendrive nao montado"
    return 0
  fi

  for d in "${pastas[@]}"; do
    copiar_pendrive "$pacote" "$p/$d" || return 0
  done
  copiar_pendrive "$cadeia" "$p/cadeia" || return 0

  # Rotação só depois de tudo copiado, e só em diario/ e semanal/.
  for d in "${pastas[@]}"; do
    codigo=0
    if [ "$d" = semanal ]; then
      "$DIR_SCRIPTS/rotacionar.sh" "$p/semanal" 4 || codigo=$?
    else
      "$DIR_SCRIPTS/rotacionar.sh" "$p/diario" 7 || codigo=$?
    fi
    [ "$codigo" -eq 0 ] || erro "pendrive rotacao pasta=$d codigo=$codigo"
  done
}

# ---------------------------------------------------------------- rclone

# Envia <origem> para <remoto/pasta>/<nome> e confere. Toda saída do rclone vai para /dev/null.
copiar_rclone() {
  local origem="$1" destino="$2" nome codigo=0
  nome="$(basename "$origem")"
  rclone copyto --checksum "$origem" "$destino/$nome" >/dev/null 2>&1 || codigo=$?
  if [ "$codigo" -ne 0 ]; then
    erro "rclone etapa=copyto pasta=${destino##*/} codigo=$codigo"
    return 1
  fi
  rclone check --one-way --checksum "$(dirname "$origem")" "$destino" --include "/$nome" >/dev/null 2>&1 || codigo=$?
  if [ "$codigo" -ne 0 ]; then
    erro "rclone etapa=check pasta=${destino##*/} codigo=$codigo"
    return 1
  fi
  log "rclone: ${destino##*/}/$nome"
}

# Mantém os <quantos> pacotes mais novos de <remoto/pasta>, pela mesma regra de nome do
# rotacionar.sh. Os nomes listados pelo lsf só entram no log se casarem com o padrão.
rotacionar_rclone() {
  local destino="$1" quantos="$2" lista nome codigo=0 i removidos=0
  local -a pacotes=()
  lista="$(rclone lsf --files-only "$destino" 2>/dev/null)" || codigo=$?
  if [ "$codigo" -ne 0 ]; then
    erro "rclone etapa=lsf pasta=${destino##*/} codigo=$codigo"
    return 1
  fi
  while IFS= read -r nome; do
    [[ "$nome" =~ $PADRAO_PACOTE ]] && pacotes+=("$nome")
  done < <(printf '%s\n' "$lista" | LC_ALL=C sort -r)
  for ((i = quantos; i < ${#pacotes[@]}; i++)); do
    codigo=0
    rclone deletefile "$destino/${pacotes[$i]}" >/dev/null 2>&1 || codigo=$?
    if [ "$codigo" -ne 0 ]; then
      erro "rclone etapa=deletefile pasta=${destino##*/} codigo=$codigo"
      return 1
    fi
    log "rclone: removido ${pacotes[$i]}"
    removidos=$((removidos + 1))
  done
  log "rclone rotacao: pasta=${destino##*/} manteve=$(( ${#pacotes[@]} - removidos )) removeu=$removidos"
}

enviar_rclone() {
  local remoto d
  remoto="$(ler_env GF_BACKUP_RCLONE_REMOTO)"
  if [ -z "$remoto" ]; then
    erro "rclone nao configurado"
    return 0
  fi
  if ! command -v rclone >/dev/null; then
    erro "rclone nao instalado"
    return 0
  fi
  remoto="${remoto%/}"

  for d in "${pastas[@]}"; do
    copiar_rclone "$pacote" "$remoto/$d" || return 0
  done
  copiar_rclone "$cadeia" "$remoto/cadeia" || return 0

  rotacionar_rclone "$remoto/diario" 7 || return 0
  [ "$tipo" != semanal ] || rotacionar_rclone "$remoto/semanal" 4 || return 0
}

log "envio: $nome_pacote tipo=$tipo"
enviar_pendrive
enviar_rclone
[ "$falhou" -eq 0 ] || exit 1
log "envio ok: $nome_pacote"
