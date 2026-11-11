#!/usr/bin/env bash
# shellcheck source-path=SCRIPTDIR
# Restauração de um pacote do backup.sh (Task 5).
# Uso: restaurar.sh <pacote.tar.age> -i <identidade|-> (--verificar | --instalar)
#
#   -i <arquivo>  identidade age (chave privada), lida e nunca copiada nem registrada no log.
#                 Não pode estar dentro de GF_RAIZ. "-i -" lê a identidade da entrada padrão.
#   --verificar   decifra, confere o pacote e roda verificar-dump.sh --manifesto --cadeia num
#                 Postgres descartável. Não toca na instalação.
#   --instalar    instala segredos e dek.servico, sobe o postgres do compose do zero, restaura
#                 o dump, confere cadeia, contagens e cadeia.txt e imprime os próximos passos.
#                 Recusa se infra/data/postgres tiver dados, se o postgres do compose estiver
#                 rodando ou se um segredo existente for diferente do pacote (igual é mantido,
#                 sem reescrever). Nunca apaga nem move infra/data, nem derruba nada.
#
# Ambiente:
#   GF_TMP              obrigatório, absoluto: o conteúdo decifrado só existe ali, e o trap o
#                       apaga em qualquer saída
#   GF_RAIZ             raiz da instalação (padrão: dois níveis acima deste script)
#   GF_COMPOSE          (--instalar) padrão: $GF_RAIZ/infra/docker-compose.yml
#   GF_COMPOSE_PROJETO  (--instalar) opcional, nome do projeto do compose (-p); os testes usam
#   GF_BACKUP_DIR       lido por ler_env, para o aviso dos cadeia-*.txt posteriores ao pacote
#
# O pacote é não confiável até o SHA256SUMS conferir. Ordem: argumentos, GF_TMP e identidade →
# decifrar o tar para GF_TMP, sem extrair → listar e validar os membros → extrair com
# --no-same-owner --no-same-permissions → cobertura e sha256sum -c do SHA256SUMS → ramo.
#
# Log (Decisão 9): etapa, nome de arquivo, contagem e código. Nunca o conteúdo de um segredo,
# a identidade, nem a saída de erro do age, do pg_restore ou do psql.
set -euo pipefail
umask 077

DIR_SCRIPTS="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=lib.sh
source "$DIR_SCRIPTS/lib.sh"

uso() { echo "uso: restaurar.sh <pacote.tar.age> -i <identidade|-> (--verificar | --instalar)" >&2; exit 2; }

# ---------------------------------------------------------------- argumentos, GF_TMP, identidade

pacote="" identidade="" modo=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    -i) { [ "$#" -ge 2 ] && [ -z "$identidade" ]; } || uso; identidade="$2"; shift 2 ;;
    --verificar|--instalar) [ -z "$modo" ] || uso; modo="${1#--}"; shift ;;
    -*) uso ;;
    *) [ -z "$pacote" ] || uso; pacote="$1"; shift ;;
  esac
done
{ [ -n "$pacote" ] && [ -n "$identidade" ] && [ -n "$modo" ]; } || uso

{ [ -f "$pacote" ] && [ -r "$pacote" ]; } || falhar "pacote ilegivel"
pacote="$(realpath -e -- "$pacote")"
nome_pacote="$(basename "$pacote")"

# Sem fallback para /tmp: o decifrado só pode existir no tmpfs que o chamador escolheu.
[[ "${GF_TMP:-}" == /* ]] || falhar "GF_TMP obrigatorio"
{ [ -d "$GF_TMP" ] && [ -w "$GF_TMP" ]; } || falhar "GF_TMP inexistente"

GF_RAIZ="${GF_RAIZ:-$(cd "$DIR_SCRIPTS/../.." && pwd)}"
export GF_RAIZ
raiz_real="$(realpath -e -- "$GF_RAIZ")" || falhar "GF_RAIZ inexistente"

if [ "$identidade" = - ]; then
  log "identidade: entrada padrao"
else
  { [ -f "$identidade" ] && [ -r "$identidade" ]; } || falhar "identidade ilegivel"
  # realpath: um link de fora apontando para dentro do repositório também é recusado.
  identidade="$(realpath -e -- "$identidade")"
  case "$identidade" in
    "${raiz_real%/}"/*) falhar "identidade dentro do repositorio" ;;
  esac
  log "identidade: arquivo"
fi
command -v age >/dev/null || falhar "age nao instalado"

# ---------------------------------------------------------------- área de trabalho

trabalho=""
limpar() { [ -z "$trabalho" ] || rm -rf -- "$trabalho"; }
trap limpar EXIT
trap 'exit 130' INT TERM

trabalho="$(mktemp -d "$GF_TMP/gf-restauracao.XXXXXXXX")"
tar_claro="$trabalho/pacote.tar"
dir="$trabalho/pacote"
mkdir "$dir"

log "restauracao: pacote=$nome_pacote modo=$modo"

# ---------------------------------------------------------------- 1. decifrar (sem extrair)
# A saída de erro do age fica de fora do log.
codigo=0
age -d -i "$identidade" -o "$tar_claro" "$pacote" 2>/dev/null || codigo=$?
[ "$codigo" -eq 0 ] || falhar "decifrar codigo=$codigo"

# ---------------------------------------------------------------- 2. membros
# Nomes por "tar -t" e tipo pelo primeiro caractere de "tar -tv", linha a linha (o GNU tar
# escapa quebra de linha no nome, então cada membro é uma linha). Só arquivo regular ("-") e
# diretório ("d"); link simbólico ("l"), link físico ("h") e o resto abortam.
nomes="$(LC_ALL=C tar -tf "$tar_claro" 2>/dev/null)" || falhar "pacote ilegivel etapa=listar"
tipos="$(LC_ALL=C tar -tvf "$tar_claro" 2>/dev/null | cut -c1)" || falhar "pacote ilegivel etapa=listar"
[ -n "$nomes" ] || falhar "pacote vazio"
mapfile -t lista_nomes <<<"$nomes"
mapfile -t lista_tipos <<<"$tipos"
[ "${#lista_nomes[@]}" -eq "${#lista_tipos[@]}" ] || falhar "pacote membro inesperado"

membro_regular_valido() {
  case "$1" in
    SHA256SUMS|MANIFESTO|cadeia.txt|gestor.dump|chaves/dek.servico) return 0 ;;
    secrets/*) nome_de_segredo_valido "${1#secrets/}" ;;
    *) return 1 ;;
  esac
}

declare -A membros=()
regulares=()
segredos=()
for i in "${!lista_nomes[@]}"; do
  nome="${lista_nomes[$i]}"
  case "${lista_tipos[$i]}" in
    -) membro_regular_valido "$nome" || falhar "pacote membro inesperado" ;;
    d) case "$nome" in secrets|secrets/|chaves|chaves/) continue ;; *) falhar "pacote membro inesperado" ;; esac ;;
    *) falhar "pacote membro inesperado" ;;
  esac
  [ -z "${membros[$nome]+x}" ] || falhar "pacote membro repetido arquivo=$nome"
  membros[$nome]=1
  regulares+=("$nome")
  [[ "$nome" != secrets/* ]] || segredos+=("${nome#secrets/}")
done
for nome in SHA256SUMS MANIFESTO cadeia.txt gestor.dump chaves/dek.servico; do
  [ -n "${membros[$nome]+x}" ] || falhar "pacote membro ausente arquivo=$nome"
done
log "pacote: membros=${#regulares[@]} segredos=${#segredos[@]}"

# ---------------------------------------------------------------- 3. extrair
codigo=0
tar -C "$dir" -xf "$tar_claro" --no-same-owner --no-same-permissions 2>/dev/null || codigo=$?
[ "$codigo" -eq 0 ] || falhar "extrair codigo=$codigo"
rm -f -- "$tar_claro"
# Defesa em profundidade: depois de extrair, só arquivos e diretórios.
[ -z "$(find "$dir" -mindepth 1 ! -type f ! -type d -print -quit)" ] || falhar "pacote membro inesperado"

# ---------------------------------------------------------------- 4. SHA256SUMS
# Cobertura exata: cada membro (menos o próprio SHA256SUMS) tem um hash, e cada hash é de um
# membro. Depois, sha256sum -c. Os nomes no log são só os já validados acima.
declare -A com_hash=()
n=0
while IFS= read -r linha || [ -n "$linha" ]; do
  n=$((n + 1))
  [[ "$linha" =~ ^[0-9a-f]{64}\ \ (.+)$ ]] || falhar "sha256 formato linha=$n"
  arquivo="${BASH_REMATCH[1]}"
  if [ "$arquivo" = SHA256SUMS ] || [ -z "${membros[$arquivo]+x}" ]; then
    membro_regular_valido "$arquivo" || arquivo="invalido"
    falhar "sha256 arquivo ausente arquivo=$arquivo"
  fi
  [ -z "${com_hash[$arquivo]+x}" ] || falhar "sha256 repetido arquivo=$arquivo"
  com_hash[$arquivo]=1
done <"$dir/SHA256SUMS"
for arquivo in "${regulares[@]}"; do
  [ "$arquivo" = SHA256SUMS ] || [ -n "${com_hash[$arquivo]+x}" ] || falhar "sha256 sem hash arquivo=$arquivo"
done

codigo=0
conferencia="$(cd "$dir" && LC_ALL=C sha256sum -c --quiet SHA256SUMS 2>/dev/null)" || codigo=$?
if [ "$codigo" -ne 0 ]; then
  while IFS= read -r linha; do
    [ -n "$linha" ] || continue
    echo "FALHA sha256 ${linha%%: *}" >&2
  done <<<"$conferencia"
  falhar "sha256 codigo=$codigo"
fi
log "sha256: ${#com_hash[@]} arquivos conferem"

# MANIFESTO: só "chave=valor".
if grep -Evq '^[a-z_]+=[^[:space:]]*$' "$dir/MANIFESTO"; then falhar "manifesto formato"; fi

valor_manifesto() {
  local valor
  valor="$(grep -m1 "^$1=" "$dir/MANIFESTO" | cut -d= -f2- || true)"
  [[ "$valor" =~ ^[0-9]+$ ]] || falhar "manifesto sem $1 valido"
  echo "$valor"
}

# ---------------------------------------------------------------- ramo --verificar

if [ "$modo" = verificar ]; then
  codigo=0
  "$DIR_SCRIPTS/verificar-dump.sh" "$dir/gestor.dump" --manifesto "$dir/MANIFESTO" --cadeia "$dir/cadeia.txt" \
    || codigo=$?
  [ "$codigo" -eq 0 ] || falhar "verificacao do dump codigo=$codigo"
  log "restauracao verificada: pacote=$nome_pacote"
  exit 0
fi

# ---------------------------------------------------------------- ramo --instalar

esperado_total="$(valor_manifesto eventos_total)"
esperado_max_seq="$(valor_manifesto eventos_max_seq)"

GF_COMPOSE="${GF_COMPOSE:-$GF_RAIZ/infra/docker-compose.yml}"
[ -f "$GF_COMPOSE" ] || falhar "compose inexistente"
compose=(docker compose -f "$GF_COMPOSE")
[ -z "${GF_COMPOSE_PROJETO:-}" ] || compose+=(-p "$GF_COMPOSE_PROJETO")

dir_segredos="$GF_RAIZ/infra/secrets"
dir_chaves="$GF_RAIZ/infra/data/chaves"
dir_pg="$GF_RAIZ/infra/data/postgres"

# Recusas: tudo conferido antes de gravar qualquer coisa.
if [ -e "$dir_pg" ] || [ -L "$dir_pg" ]; then
  { [ -d "$dir_pg" ] && [ ! -L "$dir_pg" ]; } || falhar "volume do postgres invalido"
  # Pasta ilegível (dono postgres, 0700) conta como "com dados": falha fechada.
  conteudo_pg="$(find "$dir_pg" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" \
    || falhar "volume do postgres ilegivel"
  [ -z "$conteudo_pg" ] || falhar "volume do postgres com dados"
fi

rodando="$("${compose[@]}" ps -q postgres 2>/dev/null)" || falhar "compose ps"
[ -z "$rodando" ] || falhar "postgres em execucao"

# Compara um arquivo do pacote com o que já existe. 0 = ausente (instalar), 1 = igual (manter).
# Diferente, ilegível ou que não seja arquivo regular aborta.
comparar_existente() {
  local origem="$1" destino="$2" rotulo="$3" codigo=0
  if [ ! -e "$destino" ] && [ ! -L "$destino" ]; then return 0; fi
  { [ -f "$destino" ] && [ ! -L "$destino" ]; } || falhar "segredo existente invalido arquivo=$rotulo"
  cmp -s -- "$origem" "$destino" || codigo=$?
  case "$codigo" in
    0) return 1 ;;
    1) falhar "segredo diferente arquivo=$rotulo" ;;
    *) falhar "segredo ilegivel arquivo=$rotulo" ;;
  esac
}

instalar_segredos=()
mantidos=0
for nome in "${segredos[@]}"; do
  if comparar_existente "$dir/secrets/$nome" "$dir_segredos/$nome" "$nome"; then
    instalar_segredos+=("$nome")
  else
    log "segredo igual mantido: $nome"
    mantidos=$((mantidos + 1))
  fi
done
instalar_dek=0
if comparar_existente "$dir/chaves/dek.servico" "$dir_chaves/dek.servico" dek.servico; then
  instalar_dek=1
else
  log "segredo igual mantido: dek.servico"
fi

# Segredos e dek.servico, 0400.
mkdir -p -- "$dir_segredos" "$dir_chaves"
for nome in "${instalar_segredos[@]}"; do
  install -m 400 -T -- "$dir/secrets/$nome" "$dir_segredos/$nome"
done
[ "$instalar_dek" -eq 0 ] || install -m 400 -T -- "$dir/chaves/dek.servico" "$dir_chaves/dek.servico"
log "segredos: instalados=$(( ${#instalar_segredos[@]} + instalar_dek )) mantidos=$((mantidos + 1 - instalar_dek))"

# Postgres do zero (o init cria os papéis a partir dos segredos).
codigo=0
"${compose[@]}" up -d postgres >/dev/null 2>&1 || codigo=$?
[ "$codigo" -eq 0 ] || falhar "compose up codigo=$codigo"
container="$("${compose[@]}" ps -q postgres 2>/dev/null)" || falhar "compose ps"
[ -n "$container" ] || falhar "postgres nao subiu"
esperar_postgres "$container" || falhar "postgres nao ficou pronto"
log "postgres: pronto"

codigo=0
restaurar_dump "$container" gestor "$dir/gestor.dump" || codigo=$?
[ "$codigo" -eq 0 ] || falhar "restauracao codigo=$codigo"
log "pg_restore: ok"

# Conferência do instalado: cadeia de cada casal, contagens do MANIFESTO e cadeia.txt.
falhas=0
quebras="$(psql_silencioso "$container" gestor -F ' ' -c "
  SELECT coalesce(c.casal_id::text, 'nulo'), v.seq, v.motivo
    FROM (SELECT DISTINCT casal_id FROM audit.eventos) c
   CROSS JOIN LATERAL audit.verificar_cadeia(c.casal_id) v
   ORDER BY c.casal_id NULLS LAST")" || falhar "consulta da cadeia codigo=$?"
while read -r casal seq motivo; do
  [ -n "$casal" ] || continue
  echo "FALHA cadeia casal=$casal seq=$seq motivo=$motivo" >&2
  falhas=$((falhas + 1))
done <<<"$quebras"

contagens="$(psql_silencioso "$container" gestor -F ' ' \
  -c "SELECT count(*), coalesce(max(seq), 0) FROM audit.eventos")" || falhar "consulta de contagem codigo=$?"
read -r obtido_total obtido_max_seq <<<"$contagens"
if [ "$obtido_total" != "$esperado_total" ]; then
  echo "FALHA contagem esperado=$esperado_total obtido=$obtido_total campo=eventos_total" >&2
  falhas=$((falhas + 1))
fi
if [ "$obtido_max_seq" != "$esperado_max_seq" ]; then
  echo "FALHA contagem esperado=$esperado_max_seq obtido=$obtido_max_seq campo=eventos_max_seq" >&2
  falhas=$((falhas + 1))
fi

recalculada="$(gerar_cadeia "$container" gestor)" || falhar "geracao do cadeia.txt"
if [ "$recalculada" != "$(cat "$dir/cadeia.txt")" ]; then
  echo "FALHA cadeia.txt instalado diverge do pacote" >&2
  falhas=$((falhas + 1))
fi
[ "$falhas" -eq 0 ] || falhar "instalado nao confere com o pacote"
log "instalado confere: eventos_total=$obtido_total eventos_max_seq=$obtido_max_seq"

# ---------------------------------------------------------------- próximos passos
# Data do pacote: pelo nome; senão, pelo criado_em do MANIFESTO.
if [[ "$nome_pacote" =~ ^gestor-([0-9]{4}-[0-9]{2}-[0-9]{2})\.tar\.age$ ]]; then
  data_pacote="${BASH_REMATCH[1]}"
else
  data_pacote="$(grep -m1 '^criado_em=' "$dir/MANIFESTO" | cut -d= -f2 | cut -c1-10 || true)"
fi

cat <<EOF

Restauração instalada a partir de $nome_pacote (eventos_total=$obtido_total, eventos_max_seq=$obtido_max_seq).
O postgres ficou de pé. Próximos passos, na raiz $GF_RAIZ:
  1. Dê à API a posse do que ela lê:
       sudo chown 1654 infra/secrets/{gf_chave_servico,pgpass_app,pgpass_migrador}
       sudo chown -R 1654 infra/data/chaves
  2. Suba o resto:
       docker compose -f infra/docker-compose.yml up -d
EOF

backup_dir="$(ler_env GF_BACKUP_DIR /var/backups/gestor)"
if [[ ! "$data_pacote" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}$ ]] || [[ "$backup_dir" != /* ]]; then
  echo "  3. Âncora do backup: confira à mão os cadeia-*.txt posteriores ao pacote em GF_BACKUP_DIR/cadeia/."
else
  posteriores=()
  while IFS= read -r arquivo; do
    [[ "$arquivo" =~ ^cadeia-([0-9]{4}-[0-9]{2}-[0-9]{2})\.txt$ ]] || continue
    [[ "${BASH_REMATCH[1]}" > "$data_pacote" ]] && posteriores+=("$arquivo")
  done < <(find "$backup_dir/cadeia" -maxdepth 1 -type f -name 'cadeia-*.txt' -printf '%f\n' 2>/dev/null | LC_ALL=C sort)
  if [ "${#posteriores[@]}" -eq 0 ]; then
    echo "  3. Âncora do backup: nenhum cadeia-*.txt posterior a $data_pacote em $backup_dir/cadeia/."
  else
    echo "  3. Âncora do backup: estes cadeia-*.txt são posteriores ao pacote ($data_pacote) e fariam o"
    echo "     próximo backup falhar na âncora. Mova-os (nunca apague) para cadeia/anteriores-a-restauracao/:"
    echo "       mkdir -p $backup_dir/cadeia/anteriores-a-restauracao"
    for arquivo in "${posteriores[@]}"; do
      echo "       mv $backup_dir/cadeia/$arquivo $backup_dir/cadeia/anteriores-a-restauracao/"
    done
  fi
fi
