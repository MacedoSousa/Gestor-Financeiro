#!/usr/bin/env bash
# shellcheck source-path=SCRIPTDIR
# Testes de enviar.sh <pacote> <diario|semanal> <cadeia-arquivo> (Task 4). Sem docker.
# Pendrive: /dev/shm é um ponto de montagem de verdade e a pasta é um mktemp -d dentro dele.
# rclone: backend local (GF_BACKUP_RCLONE_REMOTO=:local:<tmp>), sem configuração nem rede.
# Toda chave GF_BACKUP_* é passada pelo ambiente e GF_RAIZ aponta para uma raiz vazia, para
# nunca ler o infra/.env de verdade.
set -euo pipefail

TESTES_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ENVIAR="$TESTES_DIR/../enviar.sh"

umask 077
T="$(mktemp -d)"
PENDRIVES=()
limpar() {
  local p
  for p in "${PENDRIVES[@]}"; do rm -rf "$p"; done
  rm -rf "$T"
}
trap limpar EXIT
trap 'exit 130' INT TERM
falhas=0

ok()    { echo "ok $1"; }
falha() { echo "FALHA $1"; falhas=$((falhas + 1)); }
caso()  { if "$1"; then ok "$1"; else falha "$1"; fi; }

MONT=/dev/shm
PEN="" REM="" PACOTE="" CADEIA="" DATA=""

# Prepara um caso: pacote e cadeia locais da <data>, pendrive e remoto vazios.
preparar() {
  DATA="$1"
  local base="$T/caso-$RANDOM$RANDOM"
  mkdir -p "$base/local/diario" "$base/local/cadeia" "$base/remoto" "$T/raiz"
  PACOTE="$base/local/diario/gestor-$DATA.tar.age"
  CADEIA="$base/local/cadeia/cadeia-$DATA.txt"
  head -c 4096 /dev/urandom >"$PACOTE"
  printf '00000000-0000-4000-8000-0000000000b1 3 %064d\n' 0 >"$CADEIA"
  PEN="$(mktemp -d -p "$MONT")"; PENDRIVES+=("$PEN")
  REM="$base/remoto"
}

# Roda o envio com a configuração atual (MONT, PEN, REM). Saída combinada em $T/saida.
# Uso: enviar <tipo> [VAR=valor ...]  (as atribuições sobrescrevem o ambiente).
enviar() {
  local tipo="$1"; shift
  env GF_RAIZ="$T/raiz" GF_BACKUP_PENDRIVE_MONTAGEM="$MONT" GF_BACKUP_PENDRIVE_PASTA="$PEN" \
    GF_BACKUP_RCLONE_REMOTO=":local:$REM" "$@" "$ENVIAR" "$PACOTE" "$tipo" "$CADEIA" >"$T/saida" 2>&1
}

mostrar() { sed 's/^/  | /' "$T/saida"; }
hash_de() { sha256sum "$1" | cut -d' ' -f1; }
sem_parcial() { [ -z "$(find "$1" -name '*.parcial' -print 2>/dev/null)" ]; }

# --------------------------------------------------------------- testes do plano

pendrive_montado_recebe_e_confere() {
  preparar 2026-11-10
  enviar diario || { mostrar; return 1; }
  [ "$(hash_de "$PEN/diario/gestor-$DATA.tar.age")" = "$(hash_de "$PACOTE")" ] || return 1
  [ "$(hash_de "$PEN/cadeia/cadeia-$DATA.txt")" = "$(hash_de "$CADEIA")" ] || return 1
  sem_parcial "$PEN"
}

pendrive_nao_montado_avisa_e_segue() {
  preparar 2026-11-10
  local comum="$T/nao-montado"; mkdir -p "$comum/gestor"
  enviar diario GF_BACKUP_PENDRIVE_MONTAGEM="$comum" GF_BACKUP_PENDRIVE_PASTA="$comum/gestor" || { mostrar; return 1; }
  grep -q "aviso pendrive nao montado" "$T/saida" || { mostrar; return 1; }
  [ -z "$(ls -A "$comum/gestor")" ] || { echo "  gravou na pasta não montada"; return 1; }
  [ -f "$REM/diario/gestor-$DATA.tar.age" ]
}

pendrive_nao_configurado_avisa_e_segue() {
  preparar 2026-11-10
  enviar diario GF_BACKUP_PENDRIVE_MONTAGEM= GF_BACKUP_PENDRIVE_PASTA= || { mostrar; return 1; }
  grep -q "aviso pendrive nao configurado" "$T/saida" || { mostrar; return 1; }
  [ -f "$REM/diario/gestor-$DATA.tar.age" ]
}

# Só uma das duas variáveis do pendrive preenchida é erro de configuração, não "nao configurado".
pendrive_configuracao_incompleta_falha() {
  local metade
  for metade in montagem pasta; do
    preparar 2026-11-10
    if [ "$metade" = montagem ]; then
      if enviar diario GF_BACKUP_PENDRIVE_PASTA=; then echo "  só montagem: saiu com 0"; return 1; fi
    else
      if enviar diario GF_BACKUP_PENDRIVE_MONTAGEM=; then echo "  só pasta: saiu com 0"; return 1; fi
    fi
    grep -q "FALHA pendrive configuracao incompleta" "$T/saida" || { echo "  só $metade"; mostrar; return 1; }
    ! grep -q "aviso pendrive nao configurado" "$T/saida" || { echo "  só $metade virou aviso"; return 1; }
    [ -z "$(ls -A "$PEN")" ] || { echo "  só $metade: gravou no pendrive"; return 1; }
    [ -f "$REM/diario/gestor-$DATA.tar.age" ] || { echo "  só $metade: rclone não recebeu"; return 1; }
  done
}

rclone_recebe_e_confere() {
  preparar 2026-11-10
  enviar diario || { mostrar; return 1; }
  [ -f "$REM/diario/gestor-$DATA.tar.age" ] && [ -f "$REM/cadeia/cadeia-$DATA.txt" ] || return 1
  rclone check --one-way --checksum "$(dirname "$PACOTE")" ":local:$REM/diario" >/dev/null 2>&1 || return 1
  rclone check --one-way --checksum "$(dirname "$CADEIA")" ":local:$REM/cadeia" >/dev/null 2>&1
}

rclone_nao_configurado_falha() {
  preparar 2026-11-10
  if enviar diario GF_BACKUP_RCLONE_REMOTO=; then echo "  saiu com 0"; return 1; fi
  grep -q "FALHA rclone nao configurado" "$T/saida" || { mostrar; return 1; }
  [ "$(hash_de "$PEN/diario/gestor-$DATA.tar.age")" = "$(hash_de "$PACOTE")" ]
}

rclone_inacessivel_falha() {
  preparar 2026-11-10
  if enviar diario GF_BACKUP_RCLONE_REMOTO=:local:/caminho/sem/permissao; then echo "  saiu com 0"; return 1; fi
  grep -q "FALHA rclone" "$T/saida" || { mostrar; return 1; }
}

rotacao_nos_dois_destinos() {
  local dia
  preparar 2026-11-10
  mkdir -p "$PEN/diario" "$PEN/cadeia" "$REM/diario" "$REM/cadeia"
  for dia in 01 02 03 04 05 06 07 08; do
    echo antigo >"$PEN/diario/gestor-2026-10-$dia.tar.age"; echo antigo >"$REM/diario/gestor-2026-10-$dia.tar.age"
    echo cadeia >"$PEN/cadeia/cadeia-2026-10-$dia.txt";    echo cadeia >"$REM/cadeia/cadeia-2026-10-$dia.txt"
  done
  enviar diario || { mostrar; return 1; }
  local destino
  for destino in "$PEN" "$REM"; do
    [ "$(find "$destino/diario" -name 'gestor-*.tar.age' | wc -l)" -eq 7 ] \
      || { echo "  diario em $destino: $(find "$destino/diario" -mindepth 1 -printf '%f ')"; return 1; }
    [ ! -e "$destino/diario/gestor-2026-10-02.tar.age" ] && [ -e "$destino/diario/gestor-2026-10-03.tar.age" ] || return 1
    [ "$(find "$destino/cadeia" -name 'cadeia-*.txt' | wc -l)" -eq 9 ] || { echo "  cadeia rotacionada"; return 1; }
  done
}

# --------------------------------------------------------------- testes do CEO

domingo_vai_para_os_dois() {
  preparar 2026-11-08
  enviar semanal || { mostrar; return 1; }
  local destino pasta h
  h="$(hash_de "$PACOTE")"
  for destino in "$PEN" "$REM"; do
    for pasta in diario semanal; do
      [ -f "$destino/$pasta/gestor-$DATA.tar.age" ] || { echo "  falta $pasta em $destino"; return 1; }
      [ "$(hash_de "$destino/$pasta/gestor-$DATA.tar.age")" = "$h" ] || { echo "  sha256 $pasta em $destino"; return 1; }
    done
  done
}

cadeia_nunca_rotaciona() {
  local i destino
  preparar 2026-11-10
  mkdir -p "$PEN/cadeia" "$REM/cadeia"
  for i in $(seq 1 20); do
    printf -v nome 'cadeia-2025-%02d-%02d.txt' $(( (i - 1) / 2 + 1 )) $(( i % 2 + 1 ))
    echo antiga >"$PEN/cadeia/$nome"; echo antiga >"$REM/cadeia/$nome"
  done
  enviar diario || { mostrar; return 1; }
  for destino in "$PEN" "$REM"; do
    [ "$(find "$destino/cadeia" -name 'cadeia-*.txt' | wc -l)" -eq 21 ] \
      || { echo "  cadeia em $destino: $(find "$destino/cadeia" -name 'cadeia-*.txt' | wc -l)"; return 1; }
  done
}

pendrive_pasta_fora_da_montagem_falha() {
  preparar 2026-11-10
  local fora="$T/fora-da-montagem"; mkdir -p "$fora"
  if enviar diario GF_BACKUP_PENDRIVE_PASTA="$fora"; then echo "  saiu com 0"; return 1; fi
  grep -q "FALHA pendrive pasta fora da montagem" "$T/saida" || { mostrar; return 1; }
  { [ -z "$(ls -A "$fora")" ] && [ -z "$(ls -A "$PEN")" ]; } || { echo "  gravou no pendrive"; return 1; }
  [ -f "$REM/diario/gestor-$DATA.tar.age" ] || { echo "  rclone não recebeu"; return 1; }
  # Relativa também é fora.
  if enviar diario GF_BACKUP_PENDRIVE_PASTA=gestor; then echo "  aceitou relativa"; return 1; fi
  grep -q "FALHA pendrive pasta fora da montagem" "$T/saida" || { mostrar; return 1; }
  # Dentro só no nome (/dev/shm-x) ou com .. para fora também.
  if enviar diario GF_BACKUP_PENDRIVE_PASTA="$MONT/../tmp"; then echo "  aceitou com .."; return 1; fi
  grep -q "FALHA pendrive pasta fora da montagem" "$T/saida"
}

pendrive_copia_corrompida_nao_fica() {
  preparar 2026-11-10
  local shims="$T/shims-cp"
  mkdir -p "$shims"
  # cp falso: copia tudo menos o último byte.
  cat >"$shims/cp" <<EOF
#!/usr/bin/env bash
destino="\${!#}"; origem="\${@: -2:1}"
tamanho="\$(stat -c %s "\$origem")"
head -c "\$((tamanho - 1))" "\$origem" >"\$destino"
EOF
  chmod 700 "$shims/cp"
  if enviar diario PATH="$shims:$PATH"; then echo "  saiu com 0"; mostrar; return 1; fi
  grep -q "FALHA pendrive" "$T/saida" || { mostrar; return 1; }
  sem_parcial "$PEN" || { echo "  sobrou .parcial"; return 1; }
  [ ! -e "$PEN/diario/gestor-$DATA.tar.age" ] || { echo "  pacote corrompido ficou"; return 1; }
  [ "$(hash_de "$REM/diario/gestor-$DATA.tar.age")" = "$(hash_de "$PACOTE")" ] || { echo "  rclone não recebeu"; return 1; }
}

rclone_rotacao_so_nome_exato() {
  local dia estranho
  preparar 2026-11-10
  mkdir -p "$REM/diario"
  for dia in 01 02 03 04 05 06 07 08; do echo antigo >"$REM/diario/gestor-2026-10-$dia.tar.age"; done
  for estranho in gestor-2026-11-10.tar.age.bak notas.txt GESTOR-2026-11-10.tar.age gestor-2026-1a-10.tar.age; do
    echo estranho >"$REM/diario/$estranho"
  done
  enviar diario || { mostrar; return 1; }
  for estranho in gestor-2026-11-10.tar.age.bak notas.txt GESTOR-2026-11-10.tar.age gestor-2026-1a-10.tar.age; do
    [ -e "$REM/diario/$estranho" ] || { echo "  apagou $estranho"; return 1; }
  done
  # 9 válidos (8 antigos + o novo) → 7: saem 10-01 e 10-02.
  [ "$(find "$REM/diario" -name 'gestor-????-??-??.tar.age' | grep -cE '/gestor-[0-9]{4}-[0-9]{2}-[0-9]{2}\.tar\.age$')" -eq 7 ] || return 1
  [ ! -e "$REM/diario/gestor-2026-10-02.tar.age" ] && [ -e "$REM/diario/gestor-2026-10-03.tar.age" ] \
    && [ -e "$REM/diario/gestor-$DATA.tar.age" ]
}

log_sem_saida_do_rclone() {
  preparar 2026-11-10
  local shims="$T/shims-rclone"
  mkdir -p "$shims"
  printf '#!/bin/sh\necho SEGREDO-TESTE\necho SEGREDO-TESTE >&2\nexit 1\n' >"$shims/rclone"
  chmod 700 "$shims/rclone"
  if enviar diario PATH="$shims:$PATH"; then echo "  saiu com 0"; return 1; fi
  ! grep -q "SEGREDO-TESTE" "$T/saida" || { echo "  saída do rclone vazou"; mostrar; return 1; }
  grep -q "FALHA rclone" "$T/saida" || { mostrar; return 1; }
}

argumentos_invalidos_recusa() {
  preparar 2026-11-10
  local outro="$T/gestor-x.tar.age"; cp "$PACOTE" "$outro"
  if "$ENVIAR" "$PACOTE" mensal "$CADEIA" >/dev/null 2>&1; then echo "  aceitou tipo mensal"; return 1; fi
  if "$ENVIAR" "$outro" diario "$CADEIA" >/dev/null 2>&1; then echo "  aceitou nome fora do padrão"; return 1; fi
  if "$ENVIAR" "$PACOTE" diario "$T/nao-existe/cadeia-2026-11-10.txt" >/dev/null 2>&1; then echo "  aceitou cadeia inexistente"; return 1; fi
  if "$ENVIAR" "$PACOTE" diario >/dev/null 2>&1; then echo "  aceitou 2 argumentos"; return 1; fi
  [ -z "$(ls -A "$PEN")" ] && [ -z "$(ls -A "$REM")" ]
}

# --------------------------------------------------------------- execução

command -v rclone >/dev/null || { echo "FALHA rclone ausente"; exit 1; }
mountpoint -q "$MONT" || { echo "FALHA $MONT não é ponto de montagem"; exit 1; }
[ -x "$ENVIAR" ] || { echo "FALHA enviar.sh não existe ou não é executável"; exit 1; }

caso pendrive_montado_recebe_e_confere
caso pendrive_nao_montado_avisa_e_segue
caso pendrive_nao_configurado_avisa_e_segue
caso pendrive_configuracao_incompleta_falha
caso rclone_recebe_e_confere
caso rclone_nao_configurado_falha
caso rclone_inacessivel_falha
caso rotacao_nos_dois_destinos
caso domingo_vai_para_os_dois
caso cadeia_nunca_rotaciona
caso pendrive_pasta_fora_da_montagem_falha
caso pendrive_copia_corrompida_nao_fica
caso rclone_rotacao_so_nome_exato
caso log_sem_saida_do_rclone
caso argumentos_invalidos_recusa

[ "$falhas" -eq 0 ] || { echo "$falhas teste(s) falharam"; exit 1; }
