#!/usr/bin/env bash
# shellcheck source-path=SCRIPTDIR
# Backup diário: dump → verificação num descartável → pacote tar cifrado com age.
# Uso: backup.sh [--data AAAA-MM-DD]     (--data existe para os testes; padrão: hoje em São Paulo)
#
# Ambiente (só GF_TMP é obrigatório):
#   GF_RAIZ        raiz do repositório (padrão: dois níveis acima deste script)
#   PG_CONTAINER   container do Postgres (padrão: docker compose ... ps -q postgres)
#   GF_TMP         obrigatório, absoluto: onde fica o que existe em claro (produção:
#                  RuntimeDirectory do systemd). Sem fallback para /tmp.
#   GF_BACKUP_*    lidos de $GF_RAIZ/infra/.env por ler_env; o ambiente tem precedência
#
# Gera $GF_BACKUP_DIR/diario/gestor-<data>.tar.age e $GF_BACKUP_DIR/cadeia/cadeia-<data>.txt;
# aos domingos, um hard link do pacote em semanal/. Só depois de tudo isso dar certo chama
# rotacionar.sh e enviar.sh (se existirem). O código de saída do enviar.sh é o do backup.
#
# Nada em claro sai de $GF_TMP: o dump, os segredos e o tar só existem ali (umask 077) e são
# apagados ao sair, com sucesso ou erro. Em $GF_BACKUP_DIR só entra o pacote já cifrado.
set -euo pipefail
umask 077

DIR_SCRIPTS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
source "$DIR_SCRIPTS/lib.sh"

uso() { echo "uso: backup.sh [--data AAAA-MM-DD]" >&2; exit 2; }

data=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --data) [ "$#" -ge 2 ] || uso; data="$2"; shift 2 ;;
    *) uso ;;
  esac
done
[ -n "$data" ] || data="$(TZ=America/Sao_Paulo date +%F)"
{ [[ "$data" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]] && [ "$(date -d "$data" +%F 2>/dev/null)" = "$data" ]; } \
  || falhar "data invalida"

GF_RAIZ="${GF_RAIZ:-$(cd "$DIR_SCRIPTS/../.." && pwd)}"
export GF_RAIZ
GF_BACKUP_DIR="$(ler_env GF_BACKUP_DIR /var/backups/gestor)"
# Vazio no ambiente (que tem precedência sobre o .env) ou relativo gravaria em / ou na pasta
# corrente, como root em produção.
[[ "$GF_BACKUP_DIR" == /* ]] || falhar "GF_BACKUP_DIR precisa ser caminho absoluto"
export GF_BACKUP_DIR
# O dump, os segredos e o tar em claro só existem em GF_TMP (RuntimeDirectory em produção).
# Sem ele não há fallback: /tmp pode ser disco e sobreviver a uma queda.
[[ "${GF_TMP:-}" == /* ]] || falhar "GF_TMP obrigatorio"
destinatarios="$(ler_env GF_BACKUP_DESTINATARIOS /etc/gestor-backup/destinatarios.txt)"

# ---------------------------------------------------------------- pré-condições
# Tudo aqui roda antes de criar qualquer arquivo ou diretório.

{ [ -f "$destinatarios" ] && [ -r "$destinatarios" ]; } || falhar "destinatarios ilegivel arquivo=$(basename "$destinatarios")"
if grep -qi 'AGE-SECRET-KEY' "$destinatarios"; then
  falhar "destinatarios contem chave privada arquivo=$(basename "$destinatarios")"
fi
grep -Eq '^age1[0-9a-z]+[[:space:]]*$' "$destinatarios" \
  || falhar "destinatarios sem chave publica age1 arquivo=$(basename "$destinatarios")"

dir_segredos="$GF_RAIZ/infra/secrets"
dek="$GF_RAIZ/infra/data/chaves/dek.servico"
{ [ -f "$dek" ] && [ -r "$dek" ]; } || falhar "chaves/dek.servico ausente"
segredos=()
while IFS= read -r arquivo; do
  segredos+=("$arquivo")
done < <(find "$dir_segredos" -maxdepth 1 -type f ! -name README.md -printf '%f\n' 2>/dev/null | LC_ALL=C sort)
[ "${#segredos[@]}" -gt 0 ] || falhar "infra/secrets sem segredos"

ultima_migracao="$(find "$GF_RAIZ/db/migrations/" -maxdepth 1 -name 'V*.sql' -printf '%f\n' 2>/dev/null \
  | grep -Eo '^V[0-9]+' | LC_ALL=C sort | tail -n 1 || true)"
[ -n "$ultima_migracao" ] || falhar "db/migrations sem migracao"

command -v age >/dev/null || falhar "age nao instalado"

if [ -z "${PG_CONTAINER:-}" ]; then
  PG_CONTAINER="$(docker compose -f "$GF_RAIZ/infra/docker-compose.yml" ps -q postgres 2>/dev/null || true)"
fi
[ -n "$PG_CONTAINER" ] || falhar "container do postgres nao encontrado"

# ---------------------------------------------------------------- área de trabalho

diario="$GF_BACKUP_DIR/diario"
semanal="$GF_BACKUP_DIR/semanal"
dir_cadeia="$GF_BACKUP_DIR/cadeia"
pacote="$diario/gestor-$data.tar.age"
parcial="$diario/.gestor-$data.parcial"
cadeia_final="$dir_cadeia/cadeia-$data.txt"
cadeia_parcial="$dir_cadeia/.cadeia-$data.parcial"
link_parcial="$semanal/.gestor-$data.parcial"

trap 'exit 130' INT TERM
mkdir -p "$diario" "$semanal" "$dir_cadeia"

# Dois backups ao mesmo tempo (timer + manual) escreveriam o mesmo .parcial. A trava vem
# antes do trap de limpeza: quem não obtém a trava sai sem apagar nada, porque os .parcial
# deste dia são da execução que está com ela.
exec 9>"$GF_BACKUP_DIR/.backup.trava"
flock -n 9 || falhar "outro backup em andamento"

# Só o que este script criou é apagado: o diretório de trabalho (dentro de GF_TMP) e os
# .parcial deste dia. Nunca o GF_TMP inteiro, que em produção é do systemd.
trabalho=""
limpar() {
  [ -z "$trabalho" ] || rm -rf "$trabalho"
  rm -f "$parcial" "$cadeia_parcial" "$link_parcial"
}
trap limpar EXIT

trabalho="$(mktemp -d "$GF_TMP/gf-backup.XXXXXXXX")"
conteudo="$trabalho/pacote"
emitido="$trabalho/emitido"
mkdir "$conteudo" "$conteudo/secrets" "$conteudo/chaves" "$emitido"

log "backup: data=$data"

# ---------------------------------------------------------------- 1. dump
codigo=0
docker exec "$PG_CONTAINER" pg_dump -U postgres -d gestor -Fc >"$conteudo/gestor.dump" 2>/dev/null || codigo=$?
[ "$codigo" -eq 0 ] || falhar "pg_dump codigo=$codigo"
log "dump: gestor.dump bytes=$(wc -c <"$conteudo/gestor.dump")"

pg_versao="$(psql_descartavel "$PG_CONTAINER" gestor -c 'SHOW server_version' | cut -d' ' -f1)" \
  || falhar "versao do postgres"

# ---------------------------------------------------------------- 2/3. verificação e âncora
# A âncora é o cadeia-*.txt mais novo pelo nome, só no nível de cadeia/ (subpastas, como
# anteriores-a-restauracao/, ficam de fora).
ancora="$(find "$dir_cadeia" -maxdepth 1 -type f -name 'cadeia-*.txt' -printf '%f\n' | LC_ALL=C sort | tail -n 1)"
args_verificacao=(--emitir "$emitido")
if [ -n "$ancora" ]; then
  log "ancora: $ancora"
  args_verificacao+=(--ancora "$dir_cadeia/$ancora")
else
  log "ancora: nenhum cadeia-*.txt anterior, conferencia pulada"
fi

codigo=0
"$DIR_SCRIPTS/verificar-dump.sh" "$conteudo/gestor.dump" "${args_verificacao[@]}" || codigo=$?
[ "$codigo" -eq 0 ] || falhar "verificacao do dump codigo=$codigo"

eventos_total="$(grep -m1 '^eventos_total=' "$emitido/contagens" | cut -d= -f2)"
eventos_max_seq="$(grep -m1 '^eventos_max_seq=' "$emitido/contagens" | cut -d= -f2)"
[[ "$eventos_total" =~ ^[0-9]+$ && "$eventos_max_seq" =~ ^[0-9]+$ ]] || falhar "contagens emitidas invalidas"
cp "$emitido/cadeia.txt" "$conteudo/cadeia.txt"

# ---------------------------------------------------------------- 4. MANIFESTO
for arquivo in "${segredos[@]}"; do
  cp "$dir_segredos/$arquivo" "$conteudo/secrets/$arquivo"
done
cp "$dek" "$conteudo/chaves/dek.servico"

membros=(gestor.dump cadeia.txt)
for arquivo in "${segredos[@]}"; do membros+=("secrets/$arquivo"); done
membros+=(chaves/dek.servico)

{
  echo "criado_em=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
  echo "pg_versao=$pg_versao"
  echo "ultima_migracao=$ultima_migracao"
  echo "eventos_total=$eventos_total"
  echo "eventos_max_seq=$eventos_max_seq"
} >"$conteudo/MANIFESTO"
membros+=(MANIFESTO)

# SHA256SUMS no formato do sha256sum ("<hash>  <arquivo>"), com caminhos relativos à raiz do
# tar: a restauração confere com "sha256sum -c SHA256SUMS" depois de extrair.
codigo=0
( cd "$conteudo" && sha256sum -- "${membros[@]}" ) >"$conteudo/SHA256SUMS" || codigo=$?
[ "$codigo" -eq 0 ] || falhar "sha256sum codigo=$codigo"
log "manifesto: eventos_total=$eventos_total eventos_max_seq=$eventos_max_seq sha256sums=${#membros[@]}"

# ---------------------------------------------------------------- 5/6. pacote cifrado
codigo=0
tar -C "$conteudo" --owner=0 --group=0 --numeric-owner -cf - SHA256SUMS "${membros[@]}" \
  | age -R "$destinatarios" -o "$parcial" || codigo=$?
[ "$codigo" -eq 0 ] || falhar "tar|age codigo=$codigo"
sync -f "$parcial" 2>/dev/null || true
mv -f "$parcial" "$pacote"
log "pacote: $(basename "$pacote") bytes=$(wc -c <"$pacote")"

cp "$conteudo/cadeia.txt" "$cadeia_parcial"
mv -f "$cadeia_parcial" "$cadeia_final"
log "cadeia: $(basename "$cadeia_final") linhas=$(grep -c . "$cadeia_final" || true)"

# O que estava em claro some já; o trap apaga de novo se algo abaixo falhar antes.
rm -rf "$trabalho"
trabalho=""

# ---------------------------------------------------------------- 7. semanal (domingo)
tipo=diario
if [ "$(date -d "$data" +%u)" = 7 ]; then
  tipo=semanal
  ln -f "$pacote" "$link_parcial"
  mv -f "$link_parcial" "$semanal/gestor-$data.tar.age"
  log "semanal: gestor-$data.tar.age"
fi

# ---------------------------------------------------------------- 8/9. rotação e envio
# A rotação só chega aqui com o pacote do dia gravado. Se ela falhar, o envio ainda roda
# (o pacote novo precisa sair da máquina), mas o backup termina com erro.
# Interface da Task 3: rotacionar.sh <pasta> <quantos>. Diários: 7; semanais (domingo): 4.
codigo_rotacao=0
if [ -x "$DIR_SCRIPTS/rotacionar.sh" ]; then
  rotacoes=("$diario 7")
  [ "$tipo" != semanal ] || rotacoes+=("$semanal 4")
  for rotacao in "${rotacoes[@]}"; do
    codigo=0
    "$DIR_SCRIPTS/rotacionar.sh" "${rotacao% *}" "${rotacao##* }" || codigo=$?
    if [ "$codigo" -ne 0 ]; then
      log "rotacao pasta=$(basename "${rotacao% *}") codigo=$codigo"
      codigo_rotacao=$codigo
    fi
  done
else
  log "rotacionar.sh ausente, rotacao pulada"
fi

codigo_envio=0
if [ -x "$DIR_SCRIPTS/enviar.sh" ]; then
  "$DIR_SCRIPTS/enviar.sh" "$pacote" "$tipo" "$cadeia_final" || codigo_envio=$?
  [ "$codigo_envio" -eq 0 ] || log "envio codigo=$codigo_envio"
else
  log "enviar.sh ausente, envio pulado"
fi

[ "$codigo_envio" -eq 0 ] || exit "$codigo_envio"
[ "$codigo_rotacao" -eq 0 ] || falhar "rotacao codigo=$codigo_rotacao"
log "backup ok: $(basename "$pacote")"
