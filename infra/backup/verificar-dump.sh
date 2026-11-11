#!/usr/bin/env bash
# shellcheck source-path=SCRIPTDIR
# Restaura um dump (pg_dump -Fc) num Postgres descartável e confere a auditoria.
# Uso: verificar-dump.sh <arquivo.dump> [--manifesto <MANIFESTO>] [--cadeia <cadeia.txt>]
#                         [--ancora <cadeia-AAAA-MM-DD.txt>] [--emitir <dir>]
#
#   --ancora  cada linha "<casal|nulo> <seq> <hash>" do arquivo tem que existir em audit.eventos
#             do restaurado (mesmo casal_id, seq e hash). Pega cadeia reescrita e recalculada.
#   --emitir  se tudo passar, escreve no <dir>, a partir do banco restaurado (descreve
#             exatamente o dump): "contagens" (eventos_total=, eventos_max_seq=) e "cadeia.txt".
#
# Sai com 0 se íntegro. Senão sai ≠0 com uma ou mais linhas no stderr:
#   FALHA cadeia casal=<uuid|nulo> seq=<n> motivo=<...>    audit.verificar_cadeia reprovou
#   FALHA contagem esperado=<n> obtido=<m> campo=<...>     count(*)/max(seq) ≠ MANIFESTO
#   FALHA cadeia.txt casal=<uuid|nulo>                     último evento ≠ cadeia.txt
#   FALHA ancora casal=<uuid|nulo> seq=<n>                 linha da âncora ausente no restaurado
#   FALHA ancora formato linha=<n>                         linha da âncora mal formada
#
# O descartável: sem rede, dados em tmpfs, nome gf-verificacao-<aleatório>, removido por trap.
set -euo pipefail
umask 077

# shellcheck source=lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

uso() {
  echo "uso: verificar-dump.sh <arquivo.dump> [--manifesto <MANIFESTO>] [--cadeia <cadeia.txt>]" \
    "[--ancora <arquivo>] [--emitir <dir>]" >&2
  exit 2
}

dump="" manifesto="" cadeia="" ancora="" emitir=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --manifesto) [ "$#" -ge 2 ] || uso; manifesto="$2"; shift 2 ;;
    --cadeia)    [ "$#" -ge 2 ] || uso; cadeia="$2"; shift 2 ;;
    --ancora)    [ "$#" -ge 2 ] || uso; ancora="$2"; shift 2 ;;
    --emitir)    [ "$#" -ge 2 ] || uso; emitir="$2"; shift 2 ;;
    -*) uso ;;
    *) [ -z "$dump" ] || uso; dump="$1"; shift ;;
  esac
done
[ -n "$dump" ] || uso
{ [ -f "$dump" ] && [ -r "$dump" ]; } || falhar "dump inexistente ou ilegível arquivo=$(basename "$dump")"
[ -z "$manifesto" ] || [ -r "$manifesto" ] || falhar "manifesto ilegível arquivo=$(basename "$manifesto")"
[ -z "$cadeia" ] || [ -r "$cadeia" ] || falhar "cadeia.txt ilegível arquivo=$(basename "$cadeia")"
[ -z "$ancora" ] || [ -r "$ancora" ] || falhar "ancora ilegível arquivo=$(basename "$ancora")"
[ -z "$emitir" ] || [ -d "$emitir" ] || falhar "diretório de emissão inexistente"

# Âncora: valida o formato antes de subir qualquer coisa. Os valores entram no SQL só depois
# de casar com o regex (uuid/nulo, inteiro, hex de 64), então não há como injetar.
ancora_valores=""
if [ -n "$ancora" ]; then
  n=0
  while IFS= read -r linha || [ -n "$linha" ]; do
    n=$((n + 1))
    if [[ "$linha" =~ ^([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|nulo)\ ([0-9]+)\ ([0-9a-f]{64})$ ]]; then
      casal_sql="'${BASH_REMATCH[1]}'::uuid"
      [ "${BASH_REMATCH[1]}" != nulo ] || casal_sql="NULL::uuid"
      ancora_valores+="${ancora_valores:+,}($casal_sql, ${BASH_REMATCH[2]}::bigint, '${BASH_REMATCH[3]}')"
    else
      falhar "ancora formato linha=$n"
    fi
  done <"$ancora"
  [ -n "$ancora_valores" ] || log "ancora vazia arquivo=$(basename "$ancora")"
fi

# Lê "chave=valor" do MANIFESTO; só aceita inteiro não negativo.
valor_manifesto() {
  local chave="$1" valor
  valor="$(grep -m1 "^$chave=" "$manifesto" | cut -d= -f2- || true)"
  [[ "$valor" =~ ^[0-9]+$ ]] || falhar "manifesto sem $chave válido"
  echo "$valor"
}

if [ -n "$manifesto" ]; then
  esperado_total="$(valor_manifesto eventos_total)"
  esperado_max_seq="$(valor_manifesto eventos_max_seq)"
fi

trap 'derrubar_descartavel "$GF_DESCARTAVEL"' EXIT
trap 'exit 130' INT TERM

log "verificação: dump=$(basename "$dump") bytes=$(wc -c <"$dump")"
subir_descartavel || falhar "descartavel nao subiu"

codigo=0
restaurar_dump "$GF_DESCARTAVEL" gestor "$dump" || codigo=$?
[ "$codigo" -eq 0 ] || falhar "restauracao codigo=$codigo"

falhas=0

# Cadeia de hashes de cada casal (inclusive casal_id nulo).
quebras="$(psql_silencioso "$GF_DESCARTAVEL" gestor -F ' ' -c "
  SELECT coalesce(c.casal_id::text, 'nulo'), v.seq, v.motivo
    FROM (SELECT DISTINCT casal_id FROM audit.eventos) c
   CROSS JOIN LATERAL audit.verificar_cadeia(c.casal_id) v
   ORDER BY c.casal_id NULLS LAST")" || falhar "consulta da cadeia codigo=$?"
while read -r casal seq motivo; do
  [ -n "$casal" ] || continue
  echo "FALHA cadeia casal=$casal seq=$seq motivo=$motivo" >&2
  falhas=$((falhas + 1))
done <<<"$quebras"

# Contagens contra o MANIFESTO.
if [ -n "$manifesto" ]; then
  contagens="$(psql_silencioso "$GF_DESCARTAVEL" gestor -F ' ' \
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
fi

# cadeia.txt recalculado no restaurado tem que ser idêntico ao arquivo.
if [ -n "$cadeia" ]; then
  recalculada="$(gerar_cadeia "$GF_DESCARTAVEL" gestor)" || falhar "geracao do cadeia.txt"
  if [ "$recalculada" != "$(cat "$cadeia")" ]; then
    divergentes="$(LC_ALL=C comm -3 <(printf '%s\n' "$recalculada" | LC_ALL=C sort) <(LC_ALL=C sort "$cadeia") \
      | awk '{print $1}' | LC_ALL=C sort -u)"
    while read -r casal; do
      [ -n "$casal" ] || continue
      echo "FALHA cadeia.txt casal=$casal" >&2
      falhas=$((falhas + 1))
    done <<<"$divergentes"
    # Diferença só de formato (linha em branco, espaço): ainda é divergência.
    [ -n "$divergentes" ] || { echo "FALHA cadeia.txt casal=formato" >&2; falhas=$((falhas + 1)); }
  fi
fi

# Âncora: linhas do cadeia-*.txt anterior que não existem mais no restaurado.
if [ -n "$ancora_valores" ]; then
  ausentes="$(psql_silencioso "$GF_DESCARTAVEL" gestor -F ' ' -c "
    SELECT coalesce(a.casal_id::text, 'nulo'), a.seq
      FROM (VALUES $ancora_valores) a(casal_id, seq, hash)
     WHERE NOT EXISTS (SELECT 1 FROM audit.eventos e
                        WHERE e.casal_id IS NOT DISTINCT FROM a.casal_id
                          AND e.seq = a.seq
                          AND e.hash = decode(a.hash, 'hex'))
     ORDER BY a.casal_id NULLS LAST, a.seq")" || falhar "consulta da ancora codigo=$?"
  while read -r casal seq; do
    [ -n "$casal" ] || continue
    echo "FALHA ancora casal=$casal seq=$seq" >&2
    falhas=$((falhas + 1))
  done <<<"$ausentes"
  [ -n "$ausentes" ] || log "ancora ok arquivo=$(basename "$ancora") linhas=$(grep -c . "$ancora")"
fi

[ "$falhas" -eq 0 ] || exit 1

# Emissão só com tudo aprovado: o backup nunca usa contagens de um dump reprovado.
if [ -n "$emitir" ]; then
  contagens="$(psql_silencioso "$GF_DESCARTAVEL" gestor -F ' ' \
    -c "SELECT count(*), coalesce(max(seq), 0) FROM audit.eventos")" || falhar "consulta de contagem codigo=$?"
  read -r obtido_total obtido_max_seq <<<"$contagens"
  [[ "$obtido_total" =~ ^[0-9]+$ && "$obtido_max_seq" =~ ^[0-9]+$ ]] || falhar "contagem ilegível"
  recalculada="$(gerar_cadeia "$GF_DESCARTAVEL" gestor)" || falhar "geracao do cadeia.txt"
  printf 'eventos_total=%s\neventos_max_seq=%s\n' "$obtido_total" "$obtido_max_seq" >"$emitir/contagens"
  if [ -n "$recalculada" ]; then
    printf '%s\n' "$recalculada" >"$emitir/cadeia.txt"
  else
    : >"$emitir/cadeia.txt"
  fi
  log "emitido: contagens eventos_total=$obtido_total eventos_max_seq=$obtido_max_seq cadeia.txt linhas=$(grep -c . "$emitir/cadeia.txt" || true)"
fi

log "verificação ok: dump=$(basename "$dump")"
