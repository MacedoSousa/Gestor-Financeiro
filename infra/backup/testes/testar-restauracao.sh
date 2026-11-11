#!/usr/bin/env bash
# shellcheck source-path=SCRIPTDIR
# Testes de restaurar.sh <pacote> -i <identidade|-> (--verificar | --instalar) (Task 5).
# Uso: testar-restauracao.sh [caso ...]      (sem argumentos, roda todos)
#
# O pacote de teste sai do backup.sh de verdade, numa raiz falsa com segredos sintéticos e
# um par age gerado aqui. As variantes (pacote adulterado, membro estranho) são refeitas a
# partir dele e cifradas com a mesma chave de teste.
#
# O --instalar roda numa raiz temporária com o compose-restauracao.yml (só o postgres, dados
# em tmpfs), num projeto do compose com nome aleatório, derrubado com "down" sem -v.
# Nunca toca na instalação real: infra/data e infra/secrets do repositório ficam de fora.
#
# "age" e "docker" passam por um embrulho no PATH do restaurar.sh que só anota a chamada,
# para os testes saberem se a falha veio antes de decifrar ou antes do docker.
set -euo pipefail

# shellcheck source=comum.sh
source "$(dirname "${BASH_SOURCE[0]}")/comum.sh"
RESTAURAR="$TESTES_DIR/../restaurar.sh"
COMPOSE_TESTE="$TESTES_DIR/compose-restauracao.yml"

umask 077
ID_EXECUCAO="$(aleatorio)"
GF_EXECUCAO="teste-$ID_EXECUCAO"
GF_TESTE_UID="$(id -u)"
GF_TESTE_GID="$(id -g)"
export GF_EXECUCAO GF_TESTE_UID GF_TESTE_GID

T="$(mktemp -d)"
CHAVE="$T/chave.txt"
PUBLICA=""
BIN="$T/bin"
CHAMADAS="$T/chamadas"
CASO="$T/caso"
TMP_RUN="$CASO/tmp"
SAIDA="$T/saida"
TODAS="$T/todas-as-saidas"
BACKUP_DIR="$T/backup"
PACOTE="$BACKUP_DIR/diario/gestor-2026-11-10.tar.age"
RAIZ_VERIFICAR="$T/raiz-verificar"
VARIANTES="$T/variantes"
falhas=0
sobras=0

# Conteúdos sintéticos dos segredos: nenhum deles pode aparecer na saída (log_sem_conteudo).
declare -A SEGREDOS=(
  [pg_superusuario]="senha-super-sintetica-q1"
  [pg_senha_app]="senha-app-sintetica-q2"
  [pg_senha_migrador]="senha-migrador-sintetica-q3"
  [pgpass_app]="*:*:gestor:gf_app:senha-app-sintetica-q2"
  [pgpass_migrador]="*:*:gestor:gf_migrator:senha-migrador-sintetica-q3"
  [gf_chave_servico]="chave-servico-sintetica-q4"
)
DEK="dek-sintetica-q5"
ALTERADO="senha-alterada-sintetica-q6"

PROJETOS=()   # "<arquivo do compose>|<projeto>"
derrubar_projetos() {
  local p
  for p in "${PROJETOS[@]}"; do
    docker compose -f "${p%%|*}" -p "${p##*|}" down >/dev/null 2>&1 || true
  done
  PROJETOS=()
}

limpar() {
  derrubar_projetos
  fonte_derrubar || true
  rm -rf "$T"
}
trap limpar EXIT
trap 'exit 130' INT TERM

ok()    { echo "ok $1"; }
falha() { echo "FALHA $1"; falhas=$((falhas + 1)); }

descartaveis_desta_execucao() {
  docker ps -aq --filter "label=gf.execucao=$GF_EXECUCAO" --filter name=gf-verificacao-
}

# Cada caso começa com $CASO limpo, um GF_TMP vazio e o registro de chamadas zerado. Depois
# dele: GF_TMP tem que estar vazio, nenhum descartável desta execução pode sobrar, e os
# projetos do compose que o caso subiu são derrubados (down, sem -v).
caso() {
  local nome="$1"
  cd "$T"
  rm -rf "$CASO"; mkdir -p "$TMP_RUN"; : >"$CHAMADAS"
  # Sem subshell: o caso registra em PROJETOS o que subiu, e a limpeza abaixo precisa disso.
  cd "$CASO"
  if "$nome"; then ok "$nome"; else falha "$nome"; fi
  cd "$T"
  if [ -n "$(find "$TMP_RUN" -mindepth 1 -print -quit 2>/dev/null)" ]; then
    echo "  GF_TMP não ficou vazio depois de $nome"
    sobras=$((sobras + 1))
  fi
  if [ -n "$(descartaveis_desta_execucao)" ]; then
    echo "  sobrou descartável desta execução depois de $nome"
    sobras=$((sobras + 1))
    descartaveis_desta_execucao | xargs -r docker rm -f >/dev/null
  fi
  derrubar_projetos
}

# ---------------------------------------------------------------- preparação

# Embrulhos de age e docker: anotam a chamada e executam o programa real.
preparar_embrulhos() {
  local prog real
  mkdir -p "$BIN"
  for prog in age docker; do
    real="$(command -v "$prog")"
    cat >"$BIN/$prog" <<EOF
#!/usr/bin/env bash
echo "$prog \${1:-}" >>"$CHAMADAS"
exec "$real" "\$@"
EOF
    chmod 700 "$BIN/$prog"
  done
}

# Gera o pacote de teste com o backup.sh real: banco-fonte sintético, raiz falsa com os
# segredos acima, rotação e envio falsos (sem efeito).
preparar_pacote() {
  local r="$T/raiz-backup" b="$T/raiz-backup/infra/backup" nome
  age-keygen -o "$CHAVE" 2>/dev/null
  PUBLICA="$(age-keygen -y "$CHAVE")"
  printf '%s\n' "$PUBLICA" >"$T/destinatarios.txt"
  mkdir -p "$b" "$r/infra/secrets" "$r/infra/data/chaves" "$r/db" "$RAIZ_VERIFICAR" "$T/tmp-backup" "$VARIANTES"
  cp "$TESTES_DIR/../backup.sh" "$TESTES_DIR/../lib.sh" "$TESTES_DIR/../verificar-dump.sh" "$b/"
  printf '#!/usr/bin/env bash\nexit 0\n' >"$b/rotacionar.sh"
  printf '#!/usr/bin/env bash\nexit 0\n' >"$b/enviar.sh"
  chmod 700 "$b"/*.sh
  ln -s "$RAIZ_REPO/db/migrations" "$r/db/migrations"
  for nome in "${!SEGREDOS[@]}"; do printf '%s\n' "${SEGREDOS[$nome]}" >"$r/infra/secrets/$nome"; done
  printf '%s\n' "$DEK" >"$r/infra/data/chaves/dek.servico"

  fonte_subir
  GF_BACKUP_DIR="$BACKUP_DIR" GF_BACKUP_DESTINATARIOS="$T/destinatarios.txt" PG_CONTAINER="$FONTE_CONTAINER" \
    GF_TMP="$T/tmp-backup" "$b/backup.sh" --data 2026-11-10 2>"$T/backup.stderr" \
    || { echo "FALHA preparação: backup.sh"; sed 's/^/  | /' "$T/backup.stderr"; exit 1; }
  fonte_derrubar
  [ -f "$PACOTE" ] || { echo "FALHA preparação: pacote não gerado"; exit 1; }
}

# ---------------------------------------------------------------- utilidades

# Uso: [VAR=valor ...] rodar_cru <args>. Roda o restaurar.sh (ou $SCRIPT) com os embrulhos no
# PATH; stdout e stderr vão para $SAIDA e se acumulam em $TODAS (log_sem_conteudo).
rodar_cru() {
  local codigo=0
  PATH="$BIN:$PATH" "${SCRIPT:-$RESTAURAR}" "$@" >"$SAIDA" 2>&1 || codigo=$?
  cat "$SAIDA" >>"$TODAS"
  return "$codigo"
}

# --verificar e casos sem instalação: GF_TMP do caso e uma GF_RAIZ vazia.
rodar() { GF_TMP="$TMP_RUN" GF_RAIZ="$RAIZ_VERIFICAR" rodar_cru "$@"; }

mostrar() { sed 's/^/  | /' "$SAIDA"; }
chamou() { grep -q "^$1" "$CHAMADAS"; }
tem() { grep -qF -- "$1" "$SAIDA" || { echo "  saída sem: $1"; mostrar; return 1; }; }

# Abre o pacote de teste em <dir> (em claro, só dentro do tmp do teste).
abrir_base() {
  rm -rf "$1"; mkdir -p "$1"
  age -d -i "$CHAVE" "$PACOTE" | tar -C "$1" -xf -
}

# Uso: empacotar <dir> <saida> [opções do tar]. Empacota os arquivos e links de <dir> (nunca
# diretórios) e cifra para a chave de teste.
empacotar() {
  local dir="$1" saida="$2"; shift 2
  local membros=()
  mapfile -t membros < <(cd "$dir" && find . -mindepth 1 ! -type d -printf '%P\n' | LC_ALL=C sort)
  tar -C "$dir" "$@" -cf - -- "${membros[@]}" 2>/dev/null | age -r "$PUBLICA" -o "$saida"
}

# Raiz temporária para o --instalar. Define R (raiz) e P (projeto do compose, aleatório).
preparar_raiz() {
  R="$CASO/raiz-$1"
  P="gf-teste-rest-$ID_EXECUCAO-$(aleatorio | cut -c1-6)"
  mkdir -p "$R/infra"
  cp "$COMPOSE_TESTE" "$R/infra/compose-restauracao.yml"
  ln -s "$RAIZ_REPO/infra/postgres" "$R/infra/postgres"
  PROJETOS+=("$R/infra/compose-restauracao.yml|$P")
}

compose_r() { docker compose -f "$R/infra/compose-restauracao.yml" -p "$P" "$@"; }
psql_r() { compose_r exec -T postgres psql -X -q -t -A -v ON_ERROR_STOP=1 -U postgres -d gestor "$@"; }

instalar() {
  GF_TMP="$TMP_RUN" GF_RAIZ="$R" GF_COMPOSE="$R/infra/compose-restauracao.yml" GF_COMPOSE_PROJETO="$P" \
    GF_BACKUP_DIR="${BACKUP_TESTE:-$BACKUP_DIR}" rodar_cru "$PACOTE" -i "$CHAVE" --instalar
}

# "<inode> <mtime>" de um arquivo, para provar que ele não foi reescrito.
marca() { stat -c '%i %.9Y' -- "$1"; }

# ---------------------------------------------------------------- plano

# --cadeia é usado: com o cadeia.txt trocado (e o SHA256SUMS refeito), o --verificar reprova.
verificar_ida_e_volta() {
  rodar "$PACOTE" -i "$CHAVE" --verificar || { mostrar; return 1; }
  tem "verificação ok" || return 1

  local d="$VARIANTES/cadeia-trocada"
  abrir_base "$d"
  sed -i '1s/ [0-9a-f]\{64\}$/ '"$(printf '0%.0s' {1..64})"'/' "$d/cadeia.txt"
  (cd "$d" && grep -v '  SHA256SUMS$' SHA256SUMS | cut -c67- | xargs sha256sum -- >SHA256SUMS.novo && mv SHA256SUMS.novo SHA256SUMS)
  empacotar "$d" "$VARIANTES/cadeia-trocada.tar.age"
  if rodar "$VARIANTES/cadeia-trocada.tar.age" -i "$CHAVE" --verificar; then
    echo "  cadeia.txt trocado passou"; return 1
  fi
  tem "FALHA cadeia.txt"
}

identidade_errada_falha() {
  age-keygen -o "$CASO/outra.txt" 2>/dev/null
  if rodar "$PACOTE" -i "$CASO/outra.txt" --verificar; then echo "  decifrou com a chave errada"; return 1; fi
  tem "FALHA decifrar" || return 1
  ! chamou docker || { echo "  chamou docker"; return 1; }
}

sha256sums_nao_conferem_aborta() {
  local d="$VARIANTES/alterado"
  abrir_base "$d"
  printf '%s\n' "$ALTERADO" >"$d/secrets/pg_superusuario"
  empacotar "$d" "$VARIANTES/alterado.tar.age"
  if rodar "$VARIANTES/alterado.tar.age" -i "$CHAVE" --verificar; then echo "  aceitou"; return 1; fi
  tem "FALHA sha256 secrets/pg_superusuario" || return 1
  ! chamou docker || { echo "  chamou docker"; return 1; }
}

instalar_em_raiz_vazia() {
  local base="$CASO/base" f total max_seq quebras obtido BACKUP_TESTE="$CASO/backup"
  preparar_raiz vazia
  abrir_base "$base"
  mkdir -p "$BACKUP_TESTE/cadeia"
  cp "$BACKUP_DIR/cadeia/cadeia-2026-11-10.txt" "$BACKUP_TESTE/cadeia/"
  echo "nulo 1 $(printf 'a%.0s' {1..64})" >"$BACKUP_TESTE/cadeia/cadeia-2026-11-09.txt"
  echo "nulo 1 $(printf 'b%.0s' {1..64})" >"$BACKUP_TESTE/cadeia/cadeia-2026-11-12.txt"

  instalar || { mostrar; return 1; }

  for f in "$base"/secrets/*; do
    cmp -s "$f" "$R/infra/secrets/$(basename "$f")" || { echo "  segredo $(basename "$f") diferente"; return 1; }
    [ "$(stat -c %a "$R/infra/secrets/$(basename "$f")")" = 400 ] || { echo "  modo de $(basename "$f")"; return 1; }
  done
  cmp -s "$base/chaves/dek.servico" "$R/infra/data/chaves/dek.servico" || { echo "  dek diferente"; return 1; }
  [ "$(stat -c %a "$R/infra/data/chaves/dek.servico")" = 400 ] || { echo "  modo do dek"; return 1; }

  quebras="$(psql_r -c "SELECT count(*) FROM (SELECT DISTINCT casal_id FROM audit.eventos) c
                         CROSS JOIN LATERAL audit.verificar_cadeia(c.casal_id) v")"
  [ "$quebras" = 0 ] || { echo "  verificar_cadeia: $quebras quebra(s)"; return 1; }
  total="$(grep '^eventos_total=' "$base/MANIFESTO" | cut -d= -f2)"
  max_seq="$(grep '^eventos_max_seq=' "$base/MANIFESTO" | cut -d= -f2)"
  obtido="$(psql_r -F ' ' -c "SELECT count(*), max(seq) FROM audit.eventos")"
  [ "$obtido" = "$total $max_seq" ] || { echo "  contagens: $obtido, manifesto $total $max_seq"; return 1; }
  [ "$(psql_r -c "SELECT count(*) FROM identidade.casais")" = 2 ] || { echo "  casais"; return 1; }

  # Próximos passos: chown, up -d e o aviso só dos cadeia-*.txt posteriores; nada movido.
  tem "chown 1654" || return 1
  tem "up -d" || return 1
  tem "cadeia-2026-11-12.txt" || return 1
  tem "anteriores-a-restauracao" || return 1
  ! grep -qF "cadeia-2026-11-09.txt" "$SAIDA" || { echo "  avisou cadeia anterior"; return 1; }
  { [ -f "$BACKUP_TESTE/cadeia/cadeia-2026-11-12.txt" ] && [ ! -e "$BACKUP_TESTE/cadeia/anteriores-a-restauracao" ]; } \
    || { echo "  moveu cadeia"; return 1; }
}

instalar_recusa_volume_com_dados() {
  preparar_raiz volume
  mkdir -p "$R/infra/data/postgres"
  echo 17 >"$R/infra/data/postgres/PG_VERSION"
  if instalar; then echo "  instalou"; return 1; fi
  tem "FALHA volume do postgres com dados" || return 1
  { [ ! -e "$R/infra/secrets" ] && [ ! -e "$R/infra/data/chaves" ]; } || { echo "  mexeu em segredos"; return 1; }
  [ "$(cat "$R/infra/data/postgres/PG_VERSION")" = 17 ] || { echo "  mexeu no volume"; return 1; }
  [ -z "$(compose_r ps -aq 2>/dev/null)" ] || { echo "  subiu container"; return 1; }
}

instalar_recusa_segredo_diferente() {
  local antes
  preparar_raiz diferente
  mkdir -p "$R/infra/secrets"
  printf '%s\n' "$ALTERADO" >"$R/infra/secrets/pg_superusuario"
  chmod 400 "$R/infra/secrets/pg_superusuario"
  antes="$(marca "$R/infra/secrets/pg_superusuario")"
  if instalar; then echo "  instalou"; return 1; fi
  tem "FALHA segredo diferente arquivo=pg_superusuario" || return 1
  [ "$(marca "$R/infra/secrets/pg_superusuario")" = "$antes" ] || { echo "  arquivo reescrito"; return 1; }
  [ "$(cat "$R/infra/secrets/pg_superusuario")" = "$ALTERADO" ] || { echo "  conteúdo mudou"; return 1; }
  [ "$(find "$R/infra/secrets" -mindepth 1 | wc -l)" = 1 ] || { echo "  instalou outros segredos"; return 1; }
  [ ! -e "$R/infra/data/chaves" ] || { echo "  instalou o dek"; return 1; }
  [ -z "$(compose_r ps -aq 2>/dev/null)" ] || { echo "  subiu container"; return 1; }
}

# ---------------------------------------------------------------- CEO

# Membros perigosos: cada pacote sai ≠0 com "FALHA pacote membro inesperado", antes do docker,
# sem escrever nada fora de GF_TMP (o caso confere que GF_TMP termina vazio).
pacote_com_caminho_estranho_recusa() {
  local variante d opcoes
  for variante in fora absoluto link_simbolico link_fisico extra; do
    d="$VARIANTES/estranho-$variante"
    abrir_base "$d"
    opcoes=()
    case "$variante" in
      fora)           echo intruso >"$d/extra.txt"; opcoes=(-P '--transform=s,^extra\.txt$,../fora.txt,') ;;
      absoluto)       echo intruso >"$d/extra.txt"; opcoes=(-P '--transform=s,^extra\.txt$,/etc/x,') ;;
      link_simbolico) ln -s /etc/passwd "$d/secrets/x" ;;
      link_fisico)    ln "$d/secrets/pg_superusuario" "$d/secrets/y" ;;
      extra)          echo intruso >"$d/extra.txt" ;;
    esac
    empacotar "$d" "$VARIANTES/estranho-$variante.tar.age" "${opcoes[@]}"
    : >"$CHAMADAS"
    if rodar "$VARIANTES/estranho-$variante.tar.age" -i "$CHAVE" --verificar; then
      echo "  $variante: aceitou"; return 1
    fi
    tem "FALHA pacote membro inesperado" || { echo "  ($variante)"; return 1; }
    ! chamou docker || { echo "  $variante: chamou docker"; return 1; }
    [ -z "$(find "$CASO" -mindepth 1 ! -path "$TMP_RUN" ! -path "$TMP_RUN/*" -print -quit)" ] \
      || { echo "  $variante: escreveu fora de GF_TMP"; return 1; }
    [ -z "$(find "$T" -name fora.txt -print -quit)" ] || { echo "  $variante: fora.txt extraído"; return 1; }
    [ -z "$(find "$TMP_RUN" -mindepth 1 -print -quit)" ] || { echo "  $variante: GF_TMP não vazio"; return 1; }
  done
}

# Arquivo sem hash, e hash de arquivo que não está no pacote: os dois abortam antes do docker.
sha256sums_incompleto_aborta() {
  local d="$VARIANTES/incompleto"
  abrir_base "$d"
  sed -i '/  secrets\/pg_superusuario$/d' "$d/SHA256SUMS"
  empacotar "$d" "$VARIANTES/incompleto.tar.age"
  if rodar "$VARIANTES/incompleto.tar.age" -i "$CHAVE" --verificar; then echo "  aceitou sem hash"; return 1; fi
  tem "FALHA sha256 sem hash arquivo=secrets/pg_superusuario" || return 1
  ! chamou docker || { echo "  chamou docker (sem hash)"; return 1; }

  d="$VARIANTES/fantasma"
  abrir_base "$d"
  printf '%s  secrets/fantasma\n' "$(printf 'c%.0s' {1..64})" >>"$d/SHA256SUMS"
  empacotar "$d" "$VARIANTES/fantasma.tar.age"
  : >"$CHAMADAS"
  if rodar "$VARIANTES/fantasma.tar.age" -i "$CHAVE" --verificar; then echo "  aceitou hash de ausente"; return 1; fi
  tem "FALHA sha256 arquivo ausente arquivo=secrets/fantasma" || return 1
  ! chamou docker || { echo "  chamou docker (ausente)"; return 1; }
}

# Também por um link de fora apontando para dentro (realpath).
identidade_dentro_do_repositorio_recusa() {
  local raiz="$CASO/raiz-repo"
  mkdir -p "$raiz"
  cp "$CHAVE" "$raiz/qualquer.txt"
  ln -s "$raiz/qualquer.txt" "$CASO/link.txt"
  for identidade in "$raiz/qualquer.txt" "$CASO/link.txt"; do
    : >"$CHAMADAS"
    if GF_TMP="$TMP_RUN" GF_RAIZ="$raiz" rodar_cru "$PACOTE" -i "$identidade" --verificar; then
      echo "  aceitou $(basename "$identidade")"; return 1
    fi
    tem "FALHA identidade dentro do repositorio" || return 1
    ! chamou age || { echo "  decifrou ($(basename "$identidade"))"; return 1; }
  done
}

identidade_pela_entrada_padrao() {
  rodar "$PACOTE" -i - --verificar <"$CHAVE" || { mostrar; return 1; }
  tem "verificação ok"
}

gf_tmp_obrigatorio() {
  local valor
  if env -u GF_TMP GF_RAIZ="$RAIZ_VERIFICAR" PATH="$BIN:$PATH" "$RESTAURAR" "$PACOTE" -i "$CHAVE" --verificar \
    >"$SAIDA" 2>&1; then
    echo "  aceitou sem GF_TMP"; return 1
  fi
  cat "$SAIDA" >>"$TODAS"
  tem "FALHA GF_TMP obrigatorio" || return 1
  for valor in "" relativo; do
    if GF_TMP="$valor" GF_RAIZ="$RAIZ_VERIFICAR" rodar_cru "$PACOTE" -i "$CHAVE" --verificar; then
      echo "  aceitou GF_TMP=[$valor]"; return 1
    fi
    tem "FALHA GF_TMP obrigatorio" || return 1
  done
  ! chamou age || { echo "  decifrou"; return 1; }
  [ ! -e "$CASO/relativo" ] || { echo "  criou GF_TMP relativo"; return 1; }
}

# Depois do sucesso e de uma falha forçada no meio (verificar-dump falso, chamado já com o
# dump extraído), GF_TMP fica vazio e nenhum descartável sobra.
nada_em_claro_sobra() {
  rodar "$PACOTE" -i "$CHAVE" --verificar || { mostrar; return 1; }
  [ -z "$(find "$TMP_RUN" -mindepth 1 -print -quit)" ] || { echo "  sobrou depois do sucesso"; return 1; }
  [ -z "$(descartaveis_desta_execucao)" ] || { echo "  descartável depois do sucesso"; return 1; }

  local falso="$CASO/falso"
  mkdir -p "$falso"
  cp "$RESTAURAR" "$TESTES_DIR/../lib.sh" "$falso/"
  cat >"$falso/verificar-dump.sh" <<EOF
#!/usr/bin/env bash
[ -s "\$1" ] && touch "$CASO/meio"
exit 1
EOF
  chmod 700 "$falso"/*.sh
  if SCRIPT="$falso/restaurar.sh" rodar "$PACOTE" -i "$CHAVE" --verificar; then echo "  falha forçada passou"; return 1; fi
  [ -f "$CASO/meio" ] || { echo "  não chegou ao verificar-dump"; mostrar; return 1; }
  [ -z "$(find "$TMP_RUN" -mindepth 1 -print -quit)" ] || { echo "  sobrou depois da falha"; return 1; }
  [ -z "$(descartaveis_desta_execucao)" ] || { echo "  descartável depois da falha"; return 1; }
}

instalar_recusa_postgres_rodando() {
  local base="$CASO/base" nome antes
  preparar_raiz rodando
  abrir_base "$base"
  mkdir -p "$R/infra/secrets"
  for nome in pg_superusuario pg_senha_app pg_senha_migrador; do
    install -m 400 "$base/secrets/$nome" "$R/infra/secrets/$nome"
  done
  compose_r up -d postgres >/dev/null 2>&1 || { echo "  postgres de teste não subiu"; return 1; }
  [ -n "$(compose_r ps -q postgres)" ] || { echo "  postgres de teste não está rodando"; return 1; }
  antes="$(for nome in "$R"/infra/secrets/*; do echo "$(basename "$nome") $(marca "$nome")"; done)"

  if instalar; then echo "  instalou"; return 1; fi
  tem "FALHA postgres em execucao" || return 1
  [ "$(for nome in "$R"/infra/secrets/*; do echo "$(basename "$nome") $(marca "$nome")"; done)" = "$antes" ] \
    || { echo "  mexeu em segredos"; return 1; }
  [ ! -e "$R/infra/data/chaves" ] || { echo "  instalou o dek"; return 1; }
}

instalar_aceita_segredo_igual() {
  local base="$CASO/base" antes
  preparar_raiz igual
  abrir_base "$base"
  mkdir -p "$R/infra/secrets"
  install -m 400 "$base/secrets/pg_superusuario" "$R/infra/secrets/pg_superusuario"
  antes="$(marca "$R/infra/secrets/pg_superusuario")"
  instalar || { mostrar; return 1; }
  [ "$(marca "$R/infra/secrets/pg_superusuario")" = "$antes" ] || { echo "  reescreveu o segredo igual"; return 1; }
  cmp -s "$base/secrets/pg_senha_app" "$R/infra/secrets/pg_senha_app" || { echo "  não instalou os outros"; return 1; }
  [ "$(psql_r -c "SELECT count(*) FROM identidade.casais")" = 2 ] || { echo "  banco não restaurado"; return 1; }
}

# Roda por último: confere a saída acumulada de todos os casos (sucessos e falhas, -i com
# arquivo e pela entrada padrão, --verificar e --instalar).
log_sem_conteudo() {
  local nome segredo chave_privada
  [ -s "$TODAS" ] || { echo "  nenhuma saída acumulada"; return 1; }
  { grep -q "verificação ok" "$TODAS" && grep -q "^FALHA" "$TODAS"; } \
    || { echo "  saída acumulada sem sucesso e falha"; return 1; }
  for nome in "${!SEGREDOS[@]}"; do
    segredo="${SEGREDOS[$nome]}"
    ! grep -qF -- "$segredo" "$TODAS" || { echo "  conteúdo de $nome na saída"; return 1; }
  done
  ! grep -qF -- "$DEK" "$TODAS" || { echo "  conteúdo do dek na saída"; return 1; }
  ! grep -qF -- "$ALTERADO" "$TODAS" || { echo "  conteúdo alterado na saída"; return 1; }
  chave_privada="$(grep '^AGE-SECRET-KEY-' "$CHAVE")"
  ! grep -qF -- "$chave_privada" "$TODAS" || { echo "  chave privada na saída"; return 1; }
  ! grep -q 'AGE-SECRET-KEY' "$TODAS" || { echo "  AGE-SECRET-KEY na saída"; return 1; }
  ! grep -qF -- "$CHAVE" "$TODAS" || { echo "  caminho da identidade na saída"; return 1; }
}

# ---------------------------------------------------------------- execução

TODOS=(
  verificar_ida_e_volta identidade_errada_falha sha256sums_nao_conferem_aborta instalar_em_raiz_vazia
  instalar_recusa_volume_com_dados instalar_recusa_segredo_diferente
  pacote_com_caminho_estranho_recusa sha256sums_incompleto_aborta identidade_dentro_do_repositorio_recusa
  identidade_pela_entrada_padrao gf_tmp_obrigatorio nada_em_claro_sobra instalar_recusa_postgres_rodando
  instalar_aceita_segredo_igual log_sem_conteudo
)
[ "$#" -gt 0 ] || set -- "${TODOS[@]}"

preparar_embrulhos
preparar_pacote
: >"$TODAS"
for nome in "$@"; do caso "$nome"; done

if [ -n "$(docker compose ls -aq 2>/dev/null | grep "^gf-teste-rest-$ID_EXECUCAO" || true)" ]; then
  echo "FALHA sobrou projeto do compose desta execução"
  sobras=$((sobras + 1))
fi
{ [ "$falhas" -eq 0 ] && [ "$sobras" -eq 0 ]; } || { echo "$falhas teste(s) falharam, $sobras sobra(s)"; exit 1; }
