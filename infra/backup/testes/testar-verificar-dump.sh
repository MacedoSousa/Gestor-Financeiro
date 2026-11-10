#!/usr/bin/env bash
# shellcheck source-path=SCRIPTDIR
# Testes de verificar-dump.sh e de restaurar_dump/gerar_cadeia (lib.sh).
# Sobe um banco-fonte sintético uma vez e reaproveita em todos os casos.
set -euo pipefail

# shellcheck source=comum.sh
source "$(dirname "${BASH_SOURCE[0]}")/comum.sh"
VERIFICAR="$TESTES_DIR/../verificar-dump.sh"

umask 077
# Id desta execução: vai como rótulo nos descartáveis (inclusive os do verificar-dump.sh).
GF_EXECUCAO="teste-$(aleatorio)"
export GF_EXECUCAO
GF_TMP="$(mktemp -d)"
GF_DESCARTAVEL=""
falhas=0
sobras=0

limpar() {
  [ -n "$GF_DESCARTAVEL" ] && derrubar_descartavel "$GF_DESCARTAVEL"
  fonte_derrubar
  rm -rf "$GF_TMP"
}
trap limpar EXIT
trap 'exit 130' INT TERM

ok()    { echo "ok $1"; }
falha() { echo "FALHA $1"; falhas=$((falhas + 1)); }

# Descartáveis criados por esta execução (rótulo gf.execucao). Nunca olha os de outra execução.
descartaveis_desta_execucao() {
  docker ps -aq --filter "label=gf.execucao=$GF_EXECUCAO" --filter name=gf-verificacao-
}

# Roda um caso e confere que nenhum descartável desta execução sobrou depois dele.
# Sobra é falha; depois de registrada, remove só os containers com o rótulo desta execução.
caso() {
  local nome="$1"
  if "$nome"; then ok "$nome"; else falha "$nome"; fi
  if [ -n "$(descartaveis_desta_execucao)" ]; then
    echo "  sobrou descartável desta execução depois de $nome"
    sobras=$((sobras + 1))
    descartaveis_desta_execucao | xargs -r docker rm -f >/dev/null
  fi
}

# count(*) e max(seq) de audit.eventos e count(*) de identidade.casais.
CONTAGENS_SQL="SELECT (SELECT count(*) FROM audit.eventos) || ' ' ||
                      (SELECT coalesce(max(seq), 0) FROM audit.eventos) || ' ' ||
                      (SELECT count(*) FROM identidade.casais)"

contagens_fonte() { psql_fonte postgres -c "$CONTAGENS_SQL"; }

# Sobe um descartável, restaura com as opções dadas e põe "<código> <contagens>" em RESULTADO.
# O log do restaurar_dump (etapa e código) fica em $GF_TMP/restore.log.
# Roda no shell atual (não em $(...)) para o trap de limpar() enxergar o container.
# Retorna falha se o descartável não subir: o caso não pode passar sem ter restaurado nada.
RESULTADO=""
restaurar_e_contar() {
  local codigo=0 contagens
  RESULTADO=""
  if ! subir_descartavel; then
    echo "  descartável não subiu"
    derrubar_descartavel "$GF_DESCARTAVEL"
    GF_DESCARTAVEL=""
    return 1
  fi
  restaurar_dump "$GF_DESCARTAVEL" gestor "$GF_TMP/integro.dump" "$@" 2>"$GF_TMP/restore.log" || codigo=$?
  contagens="$(psql_descartavel "$GF_DESCARTAVEL" gestor -c "$CONTAGENS_SQL" || echo "erro")"
  derrubar_descartavel "$GF_DESCARTAVEL"
  GF_DESCARTAVEL=""
  RESULTADO="$codigo $contagens"
}

# ---------------------------------------------------------------- casos

dump_integro_passa() {
  "$VERIFICAR" "$GF_TMP/integro.dump" 2>"$GF_TMP/stderr"
}

manifesto_com_contagem_errada_falha() {
  local total max_seq
  read -r total max_seq _ <<<"$(contagens_fonte)"
  printf 'eventos_total=%s\neventos_max_seq=%s\n' "$((total + 1))" "$max_seq" >"$GF_TMP/MANIFESTO"
  if "$VERIFICAR" "$GF_TMP/integro.dump" --manifesto "$GF_TMP/MANIFESTO" 2>"$GF_TMP/stderr"; then return 1; fi
  grep -q "FALHA contagem esperado=$((total + 1)) obtido=$total" "$GF_TMP/stderr"
}

manifesto_correto_passa() {
  local total max_seq
  read -r total max_seq _ <<<"$(contagens_fonte)"
  printf 'criado_em=x\neventos_total=%s\neventos_max_seq=%s\nsha256 abc gestor.dump\n' \
    "$total" "$max_seq" >"$GF_TMP/MANIFESTO"
  "$VERIFICAR" "$GF_TMP/integro.dump" --manifesto "$GF_TMP/MANIFESTO" 2>"$GF_TMP/stderr"
}

dump_truncado_falha() {
  local tamanho
  tamanho="$(wc -c <"$GF_TMP/integro.dump")"
  head -c "$((tamanho / 2))" "$GF_TMP/integro.dump" >"$GF_TMP/truncado.dump"
  if "$VERIFICAR" "$GF_TMP/truncado.dump" >"$GF_TMP/saida" 2>&1; then return 1; fi
  ! grep -q -e "$NOME_SINTETICO_1" -e "$NOME_SINTETICO_2" -e "Sintetica" "$GF_TMP/saida"
}

restore_completo_preserva_auditoria() {
  local esperado obtido
  esperado="0 $(contagens_fonte)"
  restaurar_e_contar || return 1
  obtido="$RESULTADO"
  [ "$obtido" = "$esperado" ] || { echo "  esperado [$esperado] obtido [$obtido]"; return 1; }
}

duas_fases_sem_disable_triggers_nao_e_fiel() {
  local esperado obtido
  esperado="0 $(contagens_fonte)"
  restaurar_e_contar --duas-fases --sem-disable-triggers || return 1
  obtido="$RESULTADO"
  echo "  fonte [$esperado] restaurado [$obtido] log [$(tr '\n' ' ' <"$GF_TMP/restore.log")]"
  # A infidelidade tem que vir da etapa de dados: o esquema restaurou e as tabelas existem.
  if grep -q "etapa=esquema" "$GF_TMP/restore.log"; then return 1; fi
  [[ "$obtido" != *erro* ]] || return 1
  if [[ "$obtido" == 0\ * ]]; then
    # Saiu com 0: só vale se alguma contagem divergir.
    [ "$obtido" != "$esperado" ]
  else
    grep -q "etapa=dados" "$GF_TMP/restore.log"
  fi
}

duas_fases_com_disable_triggers_e_fiel() {
  local esperado obtido
  esperado="0 $(contagens_fonte)"
  restaurar_e_contar --duas-fases || return 1
  obtido="$RESULTADO"
  [ "$obtido" = "$esperado" ] || { echo "  esperado [$esperado] obtido [$obtido]"; return 1; }
}

cadeia_txt_confere() {
  gerar_cadeia "$FONTE_CONTAINER" gestor >"$GF_TMP/cadeia.txt"
  # Uma linha por casal semeado, ordenada por casal_id.
  [ "$(cut -d' ' -f1 "$GF_TMP/cadeia.txt")" = "$CASAL_1"$'\n'"$CASAL_2" ] || return 1
  grep -Eq "^$CASAL_1 [0-9]+ [0-9a-f]{64}$" "$GF_TMP/cadeia.txt" || return 1
  "$VERIFICAR" "$GF_TMP/integro.dump" --cadeia "$GF_TMP/cadeia.txt" 2>"$GF_TMP/stderr"
}

cadeia_txt_divergente_falha() {
  gerar_cadeia "$FONTE_CONTAINER" gestor \
    | sed -E "1s/ [0-9a-f]{64}$/ $(printf '0%.0s' {1..64})/" >"$GF_TMP/cadeia-errada.txt"
  if "$VERIFICAR" "$GF_TMP/integro.dump" --cadeia "$GF_TMP/cadeia-errada.txt" 2>"$GF_TMP/stderr"; then return 1; fi
  grep -q "FALHA cadeia.txt casal=$CASAL_1" "$GF_TMP/stderr" \
    && ! grep -q "casal=$CASAL_2" "$GF_TMP/stderr"
}

# Destrutivo na fonte: roda por último.
evento_adulterado_falha_com_seq() {
  local seq
  seq="$(fonte_adulterar_evento)"
  [ -n "$seq" ] || return 1
  fonte_dump "$GF_TMP/adulterado.dump"
  if "$VERIFICAR" "$GF_TMP/adulterado.dump" 2>"$GF_TMP/stderr"; then return 1; fi
  grep -q "FALHA cadeia casal=$CASAL_1 seq=$seq " "$GF_TMP/stderr"
}

# ---------------------------------------------------------------- execução

[ -x "$VERIFICAR" ] || { echo "FALHA verificar-dump.sh não existe ou não é executável"; exit 1; }

fonte_subir
fonte_dump "$GF_TMP/integro.dump"

caso dump_integro_passa
caso manifesto_correto_passa
caso manifesto_com_contagem_errada_falha
caso dump_truncado_falha
caso restore_completo_preserva_auditoria
caso duas_fases_sem_disable_triggers_nao_e_fiel
caso duas_fases_com_disable_triggers_e_fiel
caso cadeia_txt_confere
caso cadeia_txt_divergente_falha
caso evento_adulterado_falha_com_seq

if [ "$sobras" -eq 0 ]; then ok descartavel_some; else falha descartavel_some; fi

[ "$falhas" -eq 0 ] || { echo "$falhas teste(s) falharam"; exit 1; }
