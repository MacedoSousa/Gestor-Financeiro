#!/usr/bin/env bash
# Testes das unidades do systemd do backup (sem docker e sem sudo): o timer, o service, a
# unidade de falha e o tmpfiles.conf em infra/systemd/. Nada é instalado nesta máquina.
#
# O "systemd-analyze verify" exige que o ExecStart exista. As unidades apontam para a
# instalação (/opt/gestor-financeiro, o mesmo caminho do gestor-financeiro.service), que não
# existe aqui nem no CI; por isso o verify roda numa cópia em que só esse prefixo vira o
# repositório, e o caminho real é conferido à parte. SYSTEMD_UNIT_PATH deixa de fora o
# /etc/systemd/system do host (unidades de outros programas geravam avisos alheios ao teste).
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
SD="$REPO/infra/systemd"
SERVICE="$SD/gestor-backup.service"
TIMER="$SD/gestor-backup.timer"
FALHOU="$SD/gestor-backup-falhou@.service"
TMPFILES="$SD/gestor-backup.tmpfiles.conf"
UNIDADES=("$SERVICE" "$TIMER" "$FALHOU")

T="$(mktemp -d)"
trap 'rm -rf "$T"' EXIT
falhas=0

ok()    { echo "ok $1"; }
falha() { echo "FALHA $1"; falhas=$((falhas + 1)); }
caso()  { if "$1"; then ok "$1"; else falha "$1"; fi; }

# Linha exata (sem espaços sobrando) no arquivo. Uso: tem_linha <arquivo> <linha>
tem_linha() {
  [ -f "$1" ] || { echo "  falta $(basename "$1")"; return 1; }
  grep -qxF -- "$2" "$1" || { echo "  $(basename "$1") sem [$2]"; return 1; }
}

# Diretório de instalação, lido do gestor-financeiro.service que já existe.
instalacao() { sed -n 's/^WorkingDirectory=//p' "$SD/gestor-financeiro.service"; }

as_tres_unidades_passam_no_verify() {
  local u inst saida="$T/verify.saida"
  inst="$(instalacao)"
  [ -n "$inst" ] || { echo "  gestor-financeiro.service sem WorkingDirectory"; return 1; }
  mkdir -p "$T/verify"
  for u in "${UNIDADES[@]}"; do
    [ -f "$u" ] || { echo "  falta $(basename "$u")"; return 1; }
    sed "s#$inst/#$REPO/#g" "$u" >"$T/verify/$(basename "$u")"
  done
  if ! (cd "$T/verify" && SYSTEMD_UNIT_PATH="$T/verify:/usr/lib/systemd/system:/lib/systemd/system" \
      systemd-analyze verify gestor-backup.service gestor-backup.timer gestor-backup-falhou@.service) >"$saida" 2>&1; then
    sed 's/^/  | /' "$saida"; return 1
  fi
  [ ! -s "$saida" ] || { echo "  verify com avisos:"; sed 's/^/  | /' "$saida"; return 1; }
}

timer_as_03_e_persistente() {
  tem_linha "$TIMER" "OnCalendar=*-*-* 03:00" &&
  tem_linha "$TIMER" "Persistent=true" &&
  tem_linha "$TIMER" "WantedBy=timers.target"
}

service_endurecido() {
  tem_linha "$SERVICE" "Type=oneshot" &&
  tem_linha "$SERVICE" "ProtectSystem=strict" &&
  tem_linha "$SERVICE" "ReadWritePaths=/var/backups/gestor -/media /root/.config/rclone" &&
  tem_linha "$SERVICE" "RuntimeDirectory=gestor-backup" &&
  tem_linha "$SERVICE" "UMask=0077" &&
  tem_linha "$SERVICE" "OnFailure=gestor-backup-falhou@%n.service"
}

# Com ProtectSystem=strict, o docker e o rclone não podem tentar gravar em /root.
service_define_o_ambiente() {
  tem_linha "$SERVICE" "Environment=GF_TMP=%t/gestor-backup" &&
  tem_linha "$SERVICE" "Environment=DOCKER_CONFIG=%t/gestor-backup/docker" &&
  tem_linha "$SERVICE" "Environment=RCLONE_CONFIG=/root/.config/rclone/rclone.conf" &&
  tem_linha "$SERVICE" "Environment=RCLONE_CACHE_DIR=%t/gestor-backup/rclone-cache"
}

# Termina antes do reinício das 04:00 e não disputa CPU e disco com o resto.
service_tempo_e_prioridade() {
  tem_linha "$SERVICE" "TimeoutStartSec=50min" &&
  tem_linha "$SERVICE" "Nice=10" &&
  tem_linha "$SERVICE" "IOSchedulingClass=idle"
}

# O ReadWritePaths falha se a pasta não existir; o tmpfiles.conf cria as obrigatórias (sem "-"):
# a do backup (padrão de GF_BACKUP_DIR no backup.sh) e a config do rclone.
tmpfiles_cria_a_pasta_do_backup() {
  tem_linha "$TMPFILES" "d /var/backups/gestor 0700 root root -" || return 1
  tem_linha "$TMPFILES" "d /root/.config/rclone 0700 root root -" || return 1
  grep -q 'ler_env GF_BACKUP_DIR /var/backups/gestor)' "$REPO/infra/backup/backup.sh" \
    || { echo "  o padrão de GF_BACKUP_DIR no backup.sh não é /var/backups/gestor"; return 1; }
}

# Sem pendrive, /media pode não existir: o "-" faz o systemd ignorar em vez de falhar.
media_e_opcional() {
  local linha
  linha="$(grep '^ReadWritePaths=' "$SERVICE" 2>/dev/null || true)"
  [ -n "$linha" ] || { echo "  sem ReadWritePaths"; return 1; }
  grep -qE '(=| )-/media( |$)' <<<"$linha" || { echo "  /media sem o -"; return 1; }
  ! grep -qE '(=| )/media( |$)' <<<"$linha" || { echo "  /media obrigatório"; return 1; }
}

execstart_no_caminho_da_instalacao() {
  local inst
  inst="$(instalacao)"
  [ "$inst" = /opt/gestor-financeiro ] || { echo "  gestor-financeiro.service instala em [$inst]"; return 1; }
  tem_linha "$SERVICE" "ExecStart=$inst/infra/backup/backup.sh" || return 1
  [ "$(grep -c '^Exec' "$SERVICE")" = 1 ] || { echo "  service com mais de um Exec*"; return 1; }
}

# O infra/.env tem o token do DuckDNS: nenhuma unidade o carrega. Tudo roda como root (o
# backup lê infra/secrets, a chave do rclone e escreve em /var/backups/gestor).
sem_environmentfile_nem_outro_usuario() {
  local u
  for u in "${UNIDADES[@]}"; do
    [ -f "$u" ] || { echo "  falta $(basename "$u")"; return 1; }
    ! grep -qE '^[[:space:]]*EnvironmentFile' "$u" || { echo "  $(basename "$u") com EnvironmentFile"; return 1; }
    ! grep -E '^[[:space:]]*(User|DynamicUser)=' "$u" | grep -qvx 'User=root' \
      || { echo "  $(basename "$u") com outro usuário"; return 1; }
  done
}

# A unidade de falha só registra no journal o nome da unidade que falhou (%i); nada mais.
falha_so_chama_logger_com_i() {
  local exec
  [ -f "$FALHOU" ] || { echo "  falta $(basename "$FALHOU")"; return 1; }
  exec="$(grep -E '^[[:space:]]*Exec' "$FALHOU" || true)"
  [ "$(grep -c . <<<"$exec")" = 1 ] || { echo "  Exec* na unidade de falha: [$exec]"; return 1; }
  [[ "$exec" =~ ^ExecStart=/usr/bin/logger\ .*%i ]] || { echo "  unidade de falha: [$exec]"; return 1; }
  tem_linha "$FALHOU" 'ExecStart=/usr/bin/logger -p user.err "gestor-backup falhou: %i"'
}

caso as_tres_unidades_passam_no_verify
caso timer_as_03_e_persistente
caso service_endurecido
caso service_define_o_ambiente
caso service_tempo_e_prioridade
caso tmpfiles_cria_a_pasta_do_backup
caso media_e_opcional
caso execstart_no_caminho_da_instalacao
caso sem_environmentfile_nem_outro_usuario
caso falha_so_chama_logger_com_i

[ "$falhas" -eq 0 ] || { echo "$falhas teste(s) falharam"; exit 1; }
