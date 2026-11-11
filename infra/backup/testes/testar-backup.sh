#!/usr/bin/env bash
# shellcheck source-path=SCRIPTDIR
# Testes de backup.sh e de ler_env (lib.sh).
#
# O backup roda numa raiz falsa (GF_RAIZ sintético): infra/backup/ com cópias de backup.sh,
# lib.sh e verificar-dump.sh e com rotacionar.sh/enviar.sh falsos que só deixam marcas;
# infra/secrets/ e infra/data/chaves/ com conteúdo sintético; infra/.env sintético;
# db/migrations apontando para o do repositório. Nunca toca nos segredos reais.
set -euo pipefail

# shellcheck source=comum.sh
source "$(dirname "${BASH_SOURCE[0]}")/comum.sh"
BACKUP_REAL="$TESTES_DIR/../backup.sh"

umask 077
GF_EXECUCAO="teste-$(aleatorio)"
export GF_EXECUCAO
T="$(mktemp -d)"
RAIZ_FALSA="$T/raiz"
MARCAS="$T/marcas"
CHAVE="$T/chave.txt"
DESTINATARIOS="$T/destinatarios.txt"
TMP_RUN="$T/tmp-run"
falhas=0
sobras=0

PENDRIVES=()
limpar() {
  local p
  fonte_derrubar
  for p in "${PENDRIVES[@]}"; do rm -rf "$p"; done
  rm -rf "$T"
}
trap limpar EXIT
trap 'exit 130' INT TERM

ok()    { echo "ok $1"; }
falha() { echo "FALHA $1"; falhas=$((falhas + 1)); }

descartaveis_desta_execucao() {
  docker ps -aq --filter "label=gf.execucao=$GF_EXECUCAO" --filter name=gf-verificacao-
}

# Cada caso recebe um par age novo, marcas limpas e um GF_TMP vazio.
caso() {
  local nome="$1"
  rm -f "$CHAVE" "$DESTINATARIOS"
  age-keygen -o "$CHAVE" 2>/dev/null
  age-keygen -y "$CHAVE" >"$DESTINATARIOS"
  rm -rf "$MARCAS" "$TMP_RUN"; mkdir "$MARCAS" "$TMP_RUN"
  if "$nome"; then ok "$nome"; else falha "$nome"; fi
  if [ -n "$(descartaveis_desta_execucao)" ]; then
    echo "  sobrou descartável desta execução depois de $nome"
    sobras=$((sobras + 1))
    descartaveis_desta_execucao | xargs -r docker rm -f >/dev/null
  fi
}

# Monta a raiz falsa. Os .sh de verdade são copiados; rotacionar/enviar são falsos.
montar_raiz_falsa() {
  local b="$RAIZ_FALSA/infra/backup"
  mkdir -p "$b" "$RAIZ_FALSA/infra/secrets" "$RAIZ_FALSA/infra/data/chaves" "$RAIZ_FALSA/db"
  cp "$TESTES_DIR/../backup.sh" "$TESTES_DIR/../lib.sh" "$TESTES_DIR/../verificar-dump.sh" "$b/"
  # Interface da Task 3: rotacionar.sh <pasta absoluta que existe> <inteiro >= 1>.
  cat >"$b/rotacionar.sh" <<EOF
#!/usr/bin/env bash
printf '%s\n' "\$*" >>"$MARCAS/rotacionar.chamadas"
[ "\$#" -eq 2 ] && [[ "\$1" == /* ]] && [ -d "\$1" ] && [[ "\$2" =~ ^[1-9][0-9]*\$ ]] \
  || { echo "rotacionar falso: argumentos invalidos [\$*]" >&2; exit 1; }
touch "$MARCAS/rotacionar"
rm -f "\$1/sentinela"
EOF
  cat >"$b/enviar.sh" <<EOF
#!/usr/bin/env bash
printf '%s\n' "\$@" >"$MARCAS/enviar.args"
env >"$MARCAS/enviar.env"
exit "\$(cat "$MARCAS/enviar.codigo" 2>/dev/null || echo 0)"
EOF
  chmod 700 "$b"/*.sh
  ln -s "$RAIZ_REPO/db/migrations" "$RAIZ_FALSA/db/migrations"
  printf 'senha-sintetica-pg\n' >"$RAIZ_FALSA/infra/secrets/pg_superusuario"
  printf 'chave-servico-sintetica\n' >"$RAIZ_FALSA/infra/secrets/gf_chave_servico"
  printf '# leia-me sintético\n' >"$RAIZ_FALSA/infra/secrets/README.md"
  printf 'dek-sintetica\n' >"$RAIZ_FALSA/infra/data/chaves/dek.servico"
  printf 'DUCKDNS_TOKEN=segredo-teste\n' >"$RAIZ_FALSA/infra/.env"
}

# Roda o backup. Uso: rodar_backup <dir de backup> [args do backup.sh]. stderr em $T/stderr.
rodar_backup() {
  local dir="$1"; shift
  GF_BACKUP_DIR="$dir" GF_BACKUP_DESTINATARIOS="$DESTINATARIOS" PG_CONTAINER="$FONTE_CONTAINER" \
    GF_TMP="$TMP_RUN" "$RAIZ_FALSA/infra/backup/backup.sh" "$@" 2>"$T/stderr"
}

# Decifra o pacote e extrai em <destino>.
abrir_pacote() {
  local pacote="$1" destino="$2"
  rm -rf "$destino"; mkdir -p "$destino"
  age -d -i "$CHAVE" "$pacote" | tar -C "$destino" -xf -
}

mostrar_stderr() { sed 's/^/  | /' "$T/stderr"; }

contagens_fonte() {
  psql_fonte postgres -F ' ' -c "SELECT count(*), coalesce(max(seq), 0) FROM audit.eventos"
}

# ---------------------------------------------------------------- ler_env (sem docker)

ler_env_le_so_a_chave() {
  local antigo_raiz="${GF_RAIZ:-}"
  GF_RAIZ="$T/env-teste"; mkdir -p "$GF_RAIZ/infra"
  cat >"$GF_RAIZ/infra/.env" <<'EOF'
DUCKDNS_TOKEN=segredo-teste
GF_BACKUP_DIR=/primeiro
GF_BACKUP_DIR="/com aspas"
GF_BACKUP_DESTINATARIOS='/simples'
GF_BACKUP_RCLONE_REMOTO=
# GF_BACKUP_PENDRIVE_PASTA=/comentado
 GF_BACKUP_PENDRIVE_MONTAGEM=/com-espaco-antes
EOF
  local r=0
  [ "$(ler_env GF_BACKUP_DIR)" = "/com aspas" ] || { echo "  última ocorrência/aspas"; r=1; }
  [ "$(ler_env GF_BACKUP_DESTINATARIOS)" = "/simples" ] || { echo "  aspas simples"; r=1; }
  [ "$(ler_env GF_BACKUP_RCLONE_REMOTO padrao)" = "padrao" ] || { echo "  vazio usa padrão"; r=1; }
  [ "$(ler_env GF_BACKUP_PENDRIVE_PASTA padrao)" = "padrao" ] || { echo "  comentário"; r=1; }
  [ "$(ler_env GF_BACKUP_PENDRIVE_MONTAGEM x)" = "x" ] || { echo "  só início de linha"; r=1; }
  [ "$(GF_BACKUP_DIR=/do-ambiente ler_env GF_BACKUP_DIR)" = "/do-ambiente" ] || { echo "  ambiente"; r=1; }
  if ler_env 'X;Y' >/dev/null 2>&1; then echo "  chave inválida aceita"; r=1; fi
  rm -f "$GF_RAIZ/infra/.env"
  [ "$(ler_env GF_BACKUP_DIR /var/backups/gestor)" = "/var/backups/gestor" ] || { echo "  sem .env"; r=1; }
  GF_RAIZ="$antigo_raiz"
  return "$r"
}

# ---------------------------------------------------------------- backup.sh

gera_pacote_decifravel() {
  local dir="$T/b-decifravel" lista total max_seq arquivo
  rodar_backup "$dir" --data 2026-11-10 || { mostrar_stderr; return 1; }
  lista="$(age -d -i "$CHAVE" "$dir/diario/gestor-2026-11-10.tar.age" | tar -tf -)" || return 1
  for arquivo in gestor.dump MANIFESTO SHA256SUMS cadeia.txt secrets/pg_superusuario secrets/gf_chave_servico chaves/dek.servico; do
    grep -qx "$arquivo" <<<"$lista" || { echo "  falta $arquivo no pacote"; return 1; }
  done
  ! grep -q "README.md" <<<"$lista" || { echo "  README.md entrou no pacote"; return 1; }
  # MANIFESTO: só chave=valor, com as contagens da fonte; os hashes ficam no SHA256SUMS.
  abrir_pacote "$dir/diario/gestor-2026-11-10.tar.age" "$T/aberto"
  read -r total max_seq _ <<<"$(contagens_fonte)"
  grep -qx "eventos_total=$total" "$T/aberto/MANIFESTO" || return 1
  grep -qx "eventos_max_seq=$max_seq" "$T/aberto/MANIFESTO" || return 1
  grep -Eqx "ultima_migracao=V[0-9]{4}" "$T/aberto/MANIFESTO" || return 1
  grep -Eqx "pg_versao=17\..*" "$T/aberto/MANIFESTO" || return 1
  grep -Eqx "criado_em=.+" "$T/aberto/MANIFESTO" || return 1
  ! grep -Evq '^[a-z_]+=' "$T/aberto/MANIFESTO" || { echo "  MANIFESTO com linha fora de chave=valor"; return 1; }
  # Terça-feira: nada em semanal/; enviar.sh chamado com pacote, tipo e cadeia.
  [ -z "$(ls -A "$dir/semanal" 2>/dev/null)" ] || return 1
  [ "$(cat "$MARCAS/enviar.args")" = "$dir/diario/gestor-2026-11-10.tar.age"$'\n'diario$'\n'"$dir/cadeia/cadeia-2026-11-10.txt" ] \
    || { echo "  args do enviar [$(tr '\n' ' ' <"$MARCAS/enviar.args")]"; return 1; }
  # Dia de semana: só a rotação dos diários.
  [ "$(cat "$MARCAS/rotacionar.chamadas" 2>/dev/null)" = "$dir/diario 7" ] \
    || { echo "  chamadas do rotacionar [$(tr '\n' ';' <"$MARCAS/rotacionar.chamadas" 2>/dev/null)]"; return 1; }
}

# SHA256SUMS no formato do sha256sum, cobrindo todo o resto do pacote; sha256sum -c confere,
# e um byte trocado no dump faz falhar.
sha256sums_confere() {
  local dir="$T/b-sha256sums" esperado
  rodar_backup "$dir" --data 2026-11-10 || { mostrar_stderr; return 1; }
  abrir_pacote "$dir/diario/gestor-2026-11-10.tar.age" "$T/aberto"
  [ -f "$T/aberto/SHA256SUMS" ] || { echo "  sem SHA256SUMS"; return 1; }
  ! grep -Evq '^[0-9a-f]{64}  [^ ].*$' "$T/aberto/SHA256SUMS" || { echo "  formato fora do sha256sum"; return 1; }
  esperado="$(printf '%s\n' MANIFESTO cadeia.txt chaves/dek.servico gestor.dump \
    secrets/gf_chave_servico secrets/pg_superusuario | LC_ALL=C sort)"
  [ "$(cut -c67- "$T/aberto/SHA256SUMS" | LC_ALL=C sort)" = "$esperado" ] \
    || { echo "  cobertura [$(cut -c67- "$T/aberto/SHA256SUMS" | tr '\n' ' ')]"; return 1; }
  ( cd "$T/aberto" && sha256sum --quiet -c SHA256SUMS ) || { echo "  sha256sum -c falhou no pacote íntegro"; return 1; }
  # Um byte trocado no meio do dump.
  printf '\xff' | dd of="$T/aberto/gestor.dump" bs=1 seek=100 conv=notrunc status=none
  if ( cd "$T/aberto" && sha256sum --quiet -c SHA256SUMS >/dev/null 2>&1 ); then
    echo "  sha256sum -c passou com o dump adulterado"; return 1
  fi
}

nada_em_claro_sobra() {
  local dir="$T/b-claro"
  rodar_backup "$dir" --data 2026-11-10 || { mostrar_stderr; return 1; }
  [ -z "$(ls -A "$TMP_RUN")" ] || { echo "  GF_TMP não ficou vazio: $(ls -A "$TMP_RUN")"; return 1; }
  [ -z "$(find "$dir" \( -name '*.dump' -o -name '*.tar' -o -name '*.parcial' -o -name 'MANIFESTO' \) -print)" ] \
    || { echo "  sobrou em claro: $(find "$dir" -type f)"; return 1; }
  # Permissões: diretório e pacote só do dono.
  [ "$(stat -c %a "$dir")" = 700 ] || return 1
  [ "$(stat -c %a "$dir/diario/gestor-2026-11-10.tar.age")" = 600 ] || return 1
  # O pacote é age de fato (não um tar em claro).
  head -c 64 "$dir/diario/gestor-2026-11-10.tar.age" | grep -aq "age-encryption.org/v1"
}

recusa_chave_privada() {
  local dir="$T/b-privada"
  cp "$CHAVE" "$DESTINATARIOS"   # o arquivo de identidade tem "# public key: age1..." e AGE-SECRET-KEY-1...
  if rodar_backup "$dir" --data 2026-11-10; then return 1; fi
  grep -q "^FALHA destinatarios" "$T/stderr" || { mostrar_stderr; return 1; }
  [ ! -e "$dir" ] || { echo "  criou $dir"; return 1; }
  # Também em minúsculas.
  { age-keygen -y "$CHAVE"; tr '[:upper:]' '[:lower:]' <"$CHAVE" | grep -i secret; } >"$DESTINATARIOS"
  if rodar_backup "$dir" --data 2026-11-10; then return 1; fi
  [ ! -e "$dir" ] && [ ! -e "$MARCAS/enviar.args" ]
}

recusa_destinatarios_sem_age1() {
  local dir="$T/b-sem-age1"
  printf '# vazio\n' >"$DESTINATARIOS"
  if rodar_backup "$dir" --data 2026-11-10; then return 1; fi
  grep -q "^FALHA destinatarios" "$T/stderr" && [ ! -e "$dir" ]
}

recusa_sem_dek() {
  local dir="$T/b-sem-dek"
  mv "$RAIZ_FALSA/infra/data/chaves/dek.servico" "$T/dek.guardada"
  local r=0
  if rodar_backup "$dir" --data 2026-11-10; then r=1; fi
  mv "$T/dek.guardada" "$RAIZ_FALSA/infra/data/chaves/dek.servico"
  [ "$r" -eq 0 ] || return 1
  grep -q "^FALHA .*dek.servico" "$T/stderr" && [ -z "$(find "$dir" -type f 2>/dev/null)" ]
}

domingo_vai_para_semanal() {
  local dir="$T/b-domingo"
  rodar_backup "$dir" --data 2026-11-08 || { mostrar_stderr; return 1; }
  [ "$(stat -c %i "$dir/semanal/gestor-2026-11-08.tar.age")" = "$(stat -c %i "$dir/diario/gestor-2026-11-08.tar.age")" ] \
    || return 1
  [ "$(sed -n 2p "$MARCAS/enviar.args")" = semanal ] || return 1
  # Domingo: diários (7) e semanais (4).
  [ "$(cat "$MARCAS/rotacionar.chamadas" 2>/dev/null)" = "$dir/diario 7"$'\n'"$dir/semanal 4" ] \
    || { echo "  chamadas do rotacionar [$(tr '\n' ';' <"$MARCAS/rotacionar.chamadas" 2>/dev/null)]"; return 1; }
  rm -f "$MARCAS/rotacionar.chamadas"
  # Rodar de novo no domingo: o link passa a apontar para o pacote novo.
  rodar_backup "$dir" --data 2026-11-08 || { mostrar_stderr; return 1; }
  [ "$(stat -c %i "$dir/semanal/gestor-2026-11-08.tar.age")" = "$(stat -c %i "$dir/diario/gestor-2026-11-08.tar.age")" ] \
    && [ "$(find "$dir/semanal" -type f | wc -l)" -eq 1 ]
}

# Com o rotacionar.sh de verdade (Task 3) no lugar do falso, num domingo: diario/ fica com 7 e
# semanal/ com 4, e o pacote do dia sobrevive nos dois.
rotacao_real_no_domingo() {
  local dir="$T/b-rotacao-real" b="$RAIZ_FALSA/infra/backup" dia r=0
  mkdir -p "$dir/diario" "$dir/semanal"
  for dia in 01 02 03 04 05 06 07 08; do echo antigo >"$dir/diario/gestor-2026-10-$dia.tar.age"; done
  for dia in 04 11 18 25; do echo antigo >"$dir/semanal/gestor-2026-10-$dia.tar.age"; done
  mv "$b/rotacionar.sh" "$T/rotacionar-falso.sh"
  cp "$TESTES_DIR/../rotacionar.sh" "$b/rotacionar.sh"
  rodar_backup "$dir" --data 2026-11-08 || r=1
  mv -f "$T/rotacionar-falso.sh" "$b/rotacionar.sh"
  [ "$r" -eq 0 ] || { mostrar_stderr; return 1; }
  [ "$(find "$dir/diario" -name 'gestor-*.tar.age' | wc -l)" -eq 7 ] || { echo "  diario: $(find "$dir/diario" -mindepth 1 -printf '%f ')"; return 1; }
  [ "$(find "$dir/semanal" -name 'gestor-*.tar.age' | wc -l)" -eq 4 ] || { echo "  semanal: $(find "$dir/semanal" -mindepth 1 -printf '%f ')"; return 1; }
  [ -f "$dir/diario/gestor-2026-11-08.tar.age" ] && [ -f "$dir/semanal/gestor-2026-11-08.tar.age" ] || return 1
  [ ! -e "$dir/diario/gestor-2026-10-02.tar.age" ] && [ -e "$dir/diario/gestor-2026-10-03.tar.age" ] || return 1
  [ ! -e "$dir/semanal/gestor-2026-10-04.tar.age" ] && [ -e "$dir/semanal/gestor-2026-10-11.tar.age" ] || return 1
  grep -q "rotacao: pasta=diario manteve=7 removeu=2" "$T/stderr" && grep -q "rotacao: pasta=semanal manteve=4 removeu=1" "$T/stderr"
}

# Ponta a ponta com o enviar.sh e o rotacionar.sh de verdade no lugar dos falsos, num domingo.
# Pendrive: pasta num mktemp de /dev/shm (ponto de montagem real); rclone: :local:<tmp>.
# Uso: envio_real <dir> <remoto rclone>. Saída do backup em $T/stderr; código em ENVIO_CODIGO.
ENVIO_PEN="" ENVIO_CODIGO=0
envio_real() {
  local dir="$1" remoto="$2" b="$RAIZ_FALSA/infra/backup"
  ENVIO_PEN="$(mktemp -d -p /dev/shm)"; PENDRIVES+=("$ENVIO_PEN")
  mv "$b/rotacionar.sh" "$T/rotacionar-falso.sh"; mv "$b/enviar.sh" "$T/enviar-falso.sh"
  cp "$TESTES_DIR/../rotacionar.sh" "$TESTES_DIR/../enviar.sh" "$b/"
  ENVIO_CODIGO=0
  GF_BACKUP_DIR="$dir" GF_BACKUP_DESTINATARIOS="$DESTINATARIOS" PG_CONTAINER="$FONTE_CONTAINER" GF_TMP="$TMP_RUN" \
    GF_BACKUP_PENDRIVE_MONTAGEM=/dev/shm GF_BACKUP_PENDRIVE_PASTA="$ENVIO_PEN" GF_BACKUP_RCLONE_REMOTO="$remoto" \
    "$b/backup.sh" --data 2026-11-08 2>"$T/stderr" || ENVIO_CODIGO=$?
  mv -f "$T/rotacionar-falso.sh" "$b/rotacionar.sh"; mv -f "$T/enviar-falso.sh" "$b/enviar.sh"
}

envio_real_no_domingo() {
  local dir="$T/b-envio-real" rem="$T/remoto-envio-real" h destino pasta
  envio_real "$dir" ":local:$rem"
  [ "$ENVIO_CODIGO" -eq 0 ] || { echo "  backup saiu com $ENVIO_CODIGO"; mostrar_stderr; return 1; }
  h="$(sha256sum "$dir/diario/gestor-2026-11-08.tar.age" | cut -d' ' -f1)"
  for destino in "$ENVIO_PEN" "$rem"; do
    for pasta in diario semanal; do
      [ "$(sha256sum "$destino/$pasta/gestor-2026-11-08.tar.age" 2>/dev/null | cut -d' ' -f1)" = "$h" ] \
        || { echo "  $pasta em $destino não confere"; return 1; }
    done
    cmp -s "$destino/cadeia/cadeia-2026-11-08.txt" "$dir/cadeia/cadeia-2026-11-08.txt" \
      || { echo "  cadeia em $destino não confere"; return 1; }
  done
}

envio_real_rclone_falha_backup_falha() {
  local dir="$T/b-envio-real-falha"
  envio_real "$dir" ":local:/caminho/sem/permissao"
  [ "$ENVIO_CODIGO" -ne 0 ] || { echo "  backup saiu com 0"; return 1; }
  [ -f "$dir/diario/gestor-2026-11-08.tar.age" ] || { echo "  sem pacote local"; return 1; }
  [ -f "$ENVIO_PEN/diario/gestor-2026-11-08.tar.age" ] || { echo "  sem pacote no pendrive"; mostrar_stderr; return 1; }
  grep -q "envio codigo=" "$T/stderr" || { mostrar_stderr; return 1; }
}

mesmo_dia_substitui() {
  local dir="$T/b-mesmo-dia" inode_antes
  rodar_backup "$dir" --data 2026-11-10 || { mostrar_stderr; return 1; }
  inode_antes="$(stat -c %i "$dir/diario/gestor-2026-11-10.tar.age")"
  rodar_backup "$dir" --data 2026-11-10 || { mostrar_stderr; return 1; }
  [ "$(find "$dir/diario" -type f | wc -l)" -eq 1 ] || { echo "  diario: $(ls -A "$dir/diario")"; return 1; }
  [ "$(find "$dir/cadeia" -type f | wc -l)" -eq 1 ] || return 1
  [ "$(stat -c %i "$dir/diario/gestor-2026-11-10.tar.age")" != "$inode_antes" ] || return 1
  abrir_pacote "$dir/diario/gestor-2026-11-10.tar.age" "$T/aberto"
}

cadeia_dentro_e_fora() {
  local dir="$T/b-cadeia" fora
  rodar_backup "$dir" --data 2026-11-10 || { mostrar_stderr; return 1; }
  fora="$dir/cadeia/cadeia-2026-11-10.txt"
  abrir_pacote "$dir/diario/gestor-2026-11-10.tar.age" "$T/aberto"
  cmp -s "$fora" "$T/aberto/cadeia.txt" || return 1
  [ "$(wc -l <"$fora")" -eq 2 ] || return 1
  [ "$(cut -d' ' -f1 "$fora")" = "$CASAL_1"$'\n'"$CASAL_2" ] || return 1
  ! grep -Evq '^([0-9a-f-]{36}|nulo) [0-9]+ [0-9a-f]{64}$' "$fora"
}

codigo_do_enviar_e_o_do_backup() {
  local dir="$T/b-enviar" codigo=0
  echo 3 >"$MARCAS/enviar.codigo"
  rodar_backup "$dir" --data 2026-11-10 || codigo=$?
  [ "$codigo" -eq 3 ] || { echo "  código $codigo"; return 1; }
  [ -f "$dir/diario/gestor-2026-11-10.tar.age" ]
}

le_so_chaves_do_env() {
  local dir="$T/b-env" shims="$T/shims" docker_real
  docker_real="$(command -v docker)"
  mkdir -p "$shims"
  cat >"$shims/docker" <<EOF
#!/usr/bin/env bash
env >>"$MARCAS/docker.env"
exec "$docker_real" "\$@"
EOF
  chmod 700 "$shims/docker"
  printf 'DUCKDNS_TOKEN=segredo-teste\nGF_BACKUP_DIR="%s"\nOUTRA=1\n' "$dir" >"$RAIZ_FALSA/infra/.env"
  local codigo=0
  env -u GF_BACKUP_DIR PATH="$shims:$PATH" GF_BACKUP_DESTINATARIOS="$DESTINATARIOS" \
    PG_CONTAINER="$FONTE_CONTAINER" GF_TMP="$TMP_RUN" \
    "$RAIZ_FALSA/infra/backup/backup.sh" --data 2026-11-10 2>"$T/stderr" || codigo=$?
  printf 'DUCKDNS_TOKEN=segredo-teste\n' >"$RAIZ_FALSA/infra/.env"
  [ "$codigo" -eq 0 ] || { mostrar_stderr; return 1; }
  [ -f "$dir/diario/gestor-2026-11-10.tar.age" ] || return 1
  [ -s "$MARCAS/docker.env" ] || { echo "  shim do docker não foi chamado"; return 1; }
  ! grep -q "segredo-teste" "$MARCAS/docker.env" "$MARCAS/enviar.env" || { echo "  token vazou"; return 1; }
  ! grep -q "segredo-teste" "$T/stderr"
}

# Falha no meio do pacote (Review Focus): um shim no PATH no lugar de <ferramenta>.
# Antes, um backup bom do dia anterior; ele tem que continuar intacto.
falha_no_meio() {
  local ferramenta="$1" corpo="$2" dir="$3" shims="$T/shims-$1" antes codigo=0
  rodar_backup "$dir" --data 2026-11-09 || { mostrar_stderr; return 1; }
  antes="$(sha256sum "$dir/diario/gestor-2026-11-09.tar.age" | cut -d' ' -f1)"
  rm -f "$MARCAS/rotacionar" "$MARCAS/rotacionar.chamadas" "$MARCAS/enviar.args"
  mkdir -p "$shims"
  printf '#!/usr/bin/env bash\n%s\n' "$corpo" >"$shims/$ferramenta"
  chmod 700 "$shims/$ferramenta"
  PATH="$shims:$PATH" GF_BACKUP_DIR="$dir" GF_BACKUP_DESTINATARIOS="$DESTINATARIOS" PG_CONTAINER="$FONTE_CONTAINER" \
    GF_TMP="$TMP_RUN" "$RAIZ_FALSA/infra/backup/backup.sh" --data 2026-11-10 2>"$T/stderr" || codigo=$?
  [ "$codigo" -ne 0 ] || { echo "  backup saiu com 0"; return 1; }
  grep -q "^FALHA tar|age codigo=" "$T/stderr" || { mostrar_stderr; return 1; }
  { [ ! -e "$dir/diario/gestor-2026-11-10.tar.age" ] && [ ! -e "$dir/cadeia/cadeia-2026-11-10.txt" ]; } \
    || { echo "  ficou pacote ou cadeia do dia"; return 1; }
  [ -z "$(find "$dir" -name '*.parcial' -print)" ] || { echo "  sobrou .parcial: $(find "$dir" -name '*.parcial')"; return 1; }
  [ -z "$(find "$dir" \( -name '*.dump' -o -name '*.tar' -o -name 'MANIFESTO' -o -name 'cadeia.txt' \) -print)" ] \
    || { echo "  sobrou em claro em GF_BACKUP_DIR"; return 1; }
  [ -z "$(ls -A "$TMP_RUN")" ] || { echo "  GF_TMP não ficou vazio"; return 1; }
  [ "$(sha256sum "$dir/diario/gestor-2026-11-09.tar.age" | cut -d' ' -f1)" = "$antes" ] \
    || { echo "  pacote do dia anterior mudou"; return 1; }
  { [ ! -e "$MARCAS/rotacionar" ] && [ ! -e "$MARCAS/enviar.args" ]; } || { echo "  rotação ou envio rodou"; return 1; }
}

age_falha_no_meio() {
  # Lê a entrada, escreve parte de uma saída age no -o e sai com erro (como disco cheio).
  # shellcheck disable=SC2016  # é o corpo do script falso, expandido só quando ele roda
  falha_no_meio age 'saida=""; while [ "$#" -gt 0 ]; do [ "$1" = -o ] && saida="$2"; shift; done
cat >/dev/null; printf "age-encryption.org/v1\n-> X25519 parcial" >"$saida"; exit 1' "$T/b-age-falha"
}

tar_falha_no_meio() {
  falha_no_meio tar 'printf "tar parcial"; exit 2' "$T/b-tar-falha"
}

# GF_TMP é obrigatório: sem ele, vazio ou relativo, o backup sai antes do dump, e nada vai
# para TMPDIR ou /tmp. Uso: rodar_sem_gf_tmp <dir> <modo: ausente|vazio|relativo>.
rodar_sem_gf_tmp() {
  local dir="$1" modo="$2" tmpdir="$T/tmpdir-$2" cwd="$T/cwd-tmp-$2" codigo=0
  local -a gf_tmp=()
  rm -rf "$tmpdir" "$cwd"; mkdir "$tmpdir" "$cwd"
  case "$modo" in
    ausente)  gf_tmp=(-u GF_TMP) ;;
    vazio)    gf_tmp=(GF_TMP=) ;;
    relativo) gf_tmp=(GF_TMP=tmp-relativo) ;;
  esac
  ( cd "$cwd" && env "${gf_tmp[@]}" TMPDIR="$tmpdir" GF_BACKUP_DIR="$dir" GF_BACKUP_DESTINATARIOS="$DESTINATARIOS" \
      PG_CONTAINER="$FONTE_CONTAINER" "$RAIZ_FALSA/infra/backup/backup.sh" --data 2026-11-10 2>"$T/stderr" ) || codigo=$?
  [ "$codigo" -ne 0 ] || { echo "  saiu com 0 ($modo)"; return 1; }
  grep -qx "FALHA GF_TMP obrigatorio" "$T/stderr" || { mostrar_stderr; return 1; }
  ! grep -q "dump:" "$T/stderr" || { echo "  fez o dump ($modo)"; return 1; }
  { [ -z "$(ls -A "$tmpdir")" ] && [ -z "$(ls -A "$cwd")" ] && [ ! -e "$dir" ]; } \
    || { echo "  criou arquivo ($modo)"; return 1; }
}

gf_tmp_obrigatorio() {
  rodar_sem_gf_tmp "$T/b-tmp-ausente" ausente \
    && rodar_sem_gf_tmp "$T/b-tmp-vazio" vazio \
    && rodar_sem_gf_tmp "$T/b-tmp-relativo" relativo
}

# GF_BACKUP_DIR vazio no ambiente (tem precedência sobre o .env) ou relativo no .env: sai
# antes de criar qualquer coisa. Roda de dentro de uma pasta vazia para ver se algo nasce ali.
backup_dir_vazio_no_ambiente_recusa() {
  local cwd="$T/cwd-vazio" codigo=0
  rm -rf "$cwd"; mkdir "$cwd"
  ( cd "$cwd" && GF_BACKUP_DIR="" GF_BACKUP_DESTINATARIOS="$DESTINATARIOS" PG_CONTAINER="$FONTE_CONTAINER" \
      GF_TMP="$TMP_RUN" "$RAIZ_FALSA/infra/backup/backup.sh" --data 2026-11-10 2>"$T/stderr" ) || codigo=$?
  [ "$codigo" -ne 0 ] || return 1
  grep -qx "FALHA GF_BACKUP_DIR precisa ser caminho absoluto" "$T/stderr" || { mostrar_stderr; return 1; }
  [ -z "$(ls -A "$cwd")" ] && [ -z "$(ls -A "$TMP_RUN")" ] && [ ! -e /diario ]
}

backup_dir_relativo_no_env_recusa() {
  local cwd="$T/cwd-relativo" codigo=0
  rm -rf "$cwd"; mkdir "$cwd"
  printf 'DUCKDNS_TOKEN=segredo-teste\nGF_BACKUP_DIR=backups/gestor\n' >"$RAIZ_FALSA/infra/.env"
  ( cd "$cwd" && env -u GF_BACKUP_DIR GF_BACKUP_DESTINATARIOS="$DESTINATARIOS" PG_CONTAINER="$FONTE_CONTAINER" \
      GF_TMP="$TMP_RUN" "$RAIZ_FALSA/infra/backup/backup.sh" --data 2026-11-10 2>"$T/stderr" ) || codigo=$?
  printf 'DUCKDNS_TOKEN=segredo-teste\n' >"$RAIZ_FALSA/infra/.env"
  [ "$codigo" -ne 0 ] || return 1
  grep -qx "FALHA GF_BACKUP_DIR precisa ser caminho absoluto" "$T/stderr" || { mostrar_stderr; return 1; }
  [ -z "$(ls -A "$cwd")" ] && [ -z "$(ls -A "$TMP_RUN")" ]
}

# Duas execuções ao mesmo tempo. Um age falso segura a primeira com o .parcial já escrito
# até a segunda terminar; sem isso a segunda quase sempre chegaria antes de existir .parcial.
duas_execucoes_ao_mesmo_tempo() {
  local dir="$T/b-concorrente" shims="$T/shims-age" age_real pid codigo1=0 codigo2=0
  age_real="$(command -v age)"
  mkdir -p "$shims"
  cat >"$shims/age" <<EOF
#!/usr/bin/env bash
"$age_real" "\$@" || exit \$?
touch "$MARCAS/age.escreveu"
for _ in \$(seq 1 600); do [ -e "$MARCAS/age.liberar" ] && exit 0; sleep 0.1; done
exit 1
EOF
  chmod 700 "$shims/age"
  mkdir -p "$T/tmp-run-2"
  PATH="$shims:$PATH" GF_BACKUP_DIR="$dir" GF_BACKUP_DESTINATARIOS="$DESTINATARIOS" PG_CONTAINER="$FONTE_CONTAINER" \
    GF_TMP="$TMP_RUN" "$RAIZ_FALSA/infra/backup/backup.sh" --data 2026-11-10 2>"$T/stderr-1" &
  pid=$!
  for _ in $(seq 1 600); do [ -e "$MARCAS/age.escreveu" ] && break; sleep 0.1; done
  [ -e "$MARCAS/age.escreveu" ] || { echo "  primeira execução não chegou ao age"; touch "$MARCAS/age.liberar"; wait "$pid" || true; return 1; }
  [ -e "$dir/diario/.gestor-2026-11-10.parcial" ] || { echo "  sem .parcial durante o age"; touch "$MARCAS/age.liberar"; wait "$pid" || true; return 1; }
  GF_BACKUP_DIR="$dir" GF_BACKUP_DESTINATARIOS="$DESTINATARIOS" PG_CONTAINER="$FONTE_CONTAINER" \
    GF_TMP="$T/tmp-run-2" "$RAIZ_FALSA/infra/backup/backup.sh" --data 2026-11-10 2>"$T/stderr" || codigo2=$?
  local parcial_sobreviveu=0
  [ -e "$dir/diario/.gestor-2026-11-10.parcial" ] && parcial_sobreviveu=1
  touch "$MARCAS/age.liberar"
  wait "$pid" || codigo1=$?
  { [ "$codigo2" -ne 0 ] && grep -q "^FALHA outro backup em andamento" "$T/stderr"; } \
    || { echo "  segunda execução: código $codigo2"; mostrar_stderr; return 1; }
  [ -z "$(ls -A "$T/tmp-run-2")" ] || { echo "  segunda execução criou arquivo em GF_TMP"; return 1; }
  [ "$parcial_sobreviveu" -eq 1 ] || { echo "  segunda execução apagou o .parcial da primeira"; return 1; }
  [ "$codigo1" -eq 0 ] || { echo "  primeira execução: código $codigo1"; sed 's/^/  | /' "$T/stderr-1"; return 1; }
  abrir_pacote "$dir/diario/gestor-2026-11-10.tar.age" "$T/aberto" && [ -s "$T/aberto/gestor.dump" ]
}

ancora_ok_com_eventos_novos() {
  local dir="$T/b-ancora-ok"
  rodar_backup "$dir" --data 2026-11-09 || { mostrar_stderr; return 1; }
  psql_fonte gf_app >/dev/null <<SQL
SELECT set_config('app.ip', 'teste-backup', false);
UPDATE identidade.casais SET nome_familia = '$NOME_SINTETICO_1 D' WHERE id = '$CASAL_1';
UPDATE identidade.casais SET nome_familia = '$NOME_SINTETICO_2 D' WHERE id = '$CASAL_2';
SQL
  rodar_backup "$dir" --data 2026-11-10 || { mostrar_stderr; return 1; }
  grep -q "ancora" "$T/stderr" || { echo "  âncora não foi conferida"; return 1; }
  ! cmp -s "$dir/cadeia/cadeia-2026-11-09.txt" "$dir/cadeia/cadeia-2026-11-10.txt"
}

# Reescreve a cadeia inteira do casal 2 e recalcula os hashes com a fórmula da V0001:
# verificar_cadeia passa, só a âncora pega. Destrutivo na fonte.
fonte_reescrever_cadeia_casal_2() {
  psql_fonte postgres >/dev/null <<SQL
ALTER TABLE audit.eventos DISABLE TRIGGER trg_eventos_imutavel;
UPDATE audit.eventos SET alteracoes = alteracoes || '{"reescrito": true}'::jsonb WHERE casal_id = '$CASAL_2';
DO \$\$
DECLARE r record; anterior bytea := NULL; novo bytea;
BEGIN
  FOR r IN SELECT * FROM audit.eventos WHERE casal_id = '$CASAL_2' ORDER BY seq LOOP
    novo := sha256(coalesce(anterior, ''::bytea)
            || convert_to(concat_ws('|', r.tabela, r.entidade_id::text, r.acao, r.alteracoes::text,
                                    coalesce(r.usuario_id::text, ''), coalesce(r.ip, '')), 'UTF8'));
    UPDATE audit.eventos SET hash_anterior = anterior, hash = novo WHERE seq = r.seq;
    anterior := novo;
  END LOOP;
END
\$\$;
ALTER TABLE audit.eventos ENABLE TRIGGER trg_eventos_imutavel;
SQL
}

ancora_quebrada_falha() {
  local dir="$T/b-ancora-quebrada" seq_ancora quebras
  rodar_backup "$dir" --data 2026-11-09 || { mostrar_stderr; return 1; }
  seq_ancora="$(grep "^$CASAL_2 " "$dir/cadeia/cadeia-2026-11-09.txt" | cut -d' ' -f2)"
  fonte_reescrever_cadeia_casal_2
  # O teste só vale se a cadeia reescrita passar em verificar_cadeia.
  quebras="$(psql_fonte postgres -c "
    SELECT count(*) FROM (SELECT DISTINCT casal_id FROM audit.eventos) c
     CROSS JOIN LATERAL audit.verificar_cadeia(c.casal_id) v")"
  [ "$quebras" = 0 ] || { echo "  verificar_cadeia reprovou a reescrita ($quebras)"; return 1; }
  if rodar_backup "$dir" --data 2026-11-10; then return 1; fi
  grep -q "^FALHA ancora casal=$CASAL_2 seq=$seq_ancora$" "$T/stderr" || { mostrar_stderr; return 1; }
  ! grep -q "casal=$CASAL_1" "$T/stderr" || return 1
  [ ! -e "$dir/diario/gestor-2026-11-10.tar.age" ] && [ ! -e "$dir/cadeia/cadeia-2026-11-10.txt" ] \
    && [ "$(find "$dir/diario" -type f | wc -l)" -eq 1 ] && [ -z "$(ls -A "$TMP_RUN")" ]
}

# Destrutivo na fonte: roda por último.
dump_reprovado_nao_gera_pacote() {
  local dir="$T/b-reprovado" antes depois
  mkdir -p "$dir/diario"
  echo sentinela >"$dir/diario/sentinela"
  echo pacote-antigo >"$dir/diario/gestor-2026-11-01.tar.age"
  antes="$(find "$dir/diario" -printf '%P %i %s %T@\n' | LC_ALL=C sort)"
  fonte_adulterar_evento >/dev/null
  if rodar_backup "$dir" --data 2026-11-10; then return 1; fi
  grep -q "^FALHA cadeia casal=$CASAL_1" "$T/stderr" || { mostrar_stderr; return 1; }
  depois="$(find "$dir/diario" -printf '%P %i %s %T@\n' | LC_ALL=C sort)"
  [ "$antes" = "$depois" ] || { echo "  diario/ mudou: [$antes] → [$depois]"; return 1; }
  [ ! -e "$MARCAS/rotacionar" ] && [ ! -e "$MARCAS/enviar.args" ] && [ -z "$(ls -A "$TMP_RUN")" ]
}

# ---------------------------------------------------------------- execução

[ -x "$BACKUP_REAL" ] || { echo "FALHA backup.sh não existe ou não é executável"; exit 1; }
{ command -v age >/dev/null && command -v age-keygen >/dev/null; } || { echo "FALHA age não instalado"; exit 1; }

caso ler_env_le_so_a_chave

montar_raiz_falsa
fonte_subir

caso gera_pacote_decifravel
caso sha256sums_confere
caso nada_em_claro_sobra
caso recusa_chave_privada
caso recusa_destinatarios_sem_age1
caso recusa_sem_dek
caso domingo_vai_para_semanal
caso rotacao_real_no_domingo
caso envio_real_no_domingo
caso envio_real_rclone_falha_backup_falha
caso mesmo_dia_substitui
caso cadeia_dentro_e_fora
caso codigo_do_enviar_e_o_do_backup
caso le_so_chaves_do_env
caso gf_tmp_obrigatorio
caso age_falha_no_meio
caso tar_falha_no_meio
caso backup_dir_vazio_no_ambiente_recusa
caso backup_dir_relativo_no_env_recusa
caso duas_execucoes_ao_mesmo_tempo
caso ancora_ok_com_eventos_novos
caso ancora_quebrada_falha
caso dump_reprovado_nao_gera_pacote

if [ "$sobras" -eq 0 ]; then ok descartavel_some; else falha descartavel_some; fi

[ "$falhas" -eq 0 ] || { echo "$falhas teste(s) falharam"; exit 1; }
