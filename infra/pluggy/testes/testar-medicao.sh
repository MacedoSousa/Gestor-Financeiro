#!/usr/bin/env bash
# Testes do infra/pluggy/medir-saldos.sh contra a Pluggy falsa (pluggy-falso.py), só com dados
# sintéticos. Nunca toca na Pluggy de verdade nem em infra/secrets do repositório: os segredos
# sintéticos ficam numa pasta temporária e PLUGGY_URL aponta para 127.0.0.1.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$DIR/../medir-saldos.sh"
T="$(mktemp -d)"
servidor=""
limpar() { [ -z "$servidor" ] || kill "$servidor" 2>/dev/null || true; rm -rf "$T"; }
trap limpar EXIT
falhas=0
ok()    { echo "ok $1"; }
falha() { echo "FALHA $1"; falhas=$((falhas + 1)); }
caso()  { if "$1"; then ok "$1"; else falha "$1"; fi; }

mkdir -p "$T/servidor" "$T/raiz/infra/secrets" "$T/home" "$T/legado/infra/secrets"
PLUGGY_FALSO_INVESTIMENTOS="$T/servidor/investimentos.json" python3 "$DIR/pluggy-falso.py" "$T/servidor/porta" "$T/servidor/registro" &
servidor=$!
for _ in $(seq 50); do [ -s "$T/servidor/porta" ] && break; sleep 0.1; done
[ -s "$T/servidor/porta" ] || { echo "FALHA a Pluggy falsa não subiu"; exit 1; }
URL="http://127.0.0.1:$(cat "$T/servidor/porta")"

printf 'id-sintetico\n' >"$T/raiz/infra/secrets/pluggy_1_client_id"
printf 'segredo-sintetico\n' >"$T/raiz/infra/secrets/pluggy_1_client_secret"
# Nomes de 11/11 (antes dos slots): o slot 1 também os aceita.
printf 'id-sintetico\n' >"$T/legado/infra/secrets/pluggy_client_id"
printf 'segredo-sintetico\n' >"$T/legado/infra/secrets/pluggy_client_secret"

# Roda o script na raiz <raiz>, com HOME e pasta corrente temporárias. Saída em $T/saida.
medir() {
  local raiz="$1"; shift
  (cd "$T/home" && env HOME="$T/home" PLUGGY_URL="$URL" MEDIR_HOJE=2026-11-11 MEDIR_ESPERA=0 "$@" \
    bash "$SCRIPT" "$raiz") >"$T/saida" 2>&1
}
mostrar() { sed 's/^/  | /' "$T/saida"; }
tem() { grep -qF -- "$1" "$T/saida" || { echo "  saída sem: $1"; mostrar; return 1; }; }
nao_tem() { ! grep -qF -- "$1" "$T/saida" || { echo "  saída com: $1"; mostrar; return 1; }; }

# A Pluggy falsa serve as aplicações do JSON sintético, relido a cada requisição (D21).
investimentos_do_falso() {
  local chave
  chave="$(printf '{"clientId":"id-sintetico","clientSecret":"segredo-sintetico"}' |
    curl -sS -X POST "$URL/auth" -H 'Content-Type: application/json' --data @- | jq -r .apiKey)"
  [ "$(curl -sS "$URL/investments?itemId=item-sintetico" -H "X-API-KEY: $chave" | jq '.results | length')" = 0 ] || return 1
  printf '[{"id":"i1","type":"FIXED_INCOME","subtype":"CDB","name":"CDB Sintético","balance":5000.00},{"id":"i2","type":"MUTUAL_FUND","name":"Fundo Sintético","balance":null}]' \
    >"$T/servidor/investimentos.json"
  [ "$(curl -sS "$URL/investments?itemId=item-sintetico" -H "X-API-KEY: $chave" | jq -c '[.results[].balance == null]')" = '[false,true]' ] || return 1
  rm -f "$T/servidor/investimentos.json"
}

cartao_fecha_com_c() {
  medir "$T/raiz" ITEM=item-sintetico SLOT=1 || { mostrar; return 1; }
  tem "cartão 1: fecha com (c)"
}

banco_mostra_o_campo_do_codigo_sem_o_valor() {
  medir "$T/raiz" ITEM=item-sintetico SLOT=1 || { mostrar; return 1; }
  tem "banco: campo institution.code · código: sim" || return 1
  nao_tem "987"
}

saida_sem_valores_nem_descricoes() {
  local proibido
  medir "$T/raiz" ITEM=item-sintetico SLOT=1 || { mostrar; return 1; }
  for proibido in 12345 1500 250.1 "Mercado Sintético" "Conta Sintética" "Banco Sintético" 99999 4321 \
      "PORTADOR SINTETICO" id-sintetico segredo-sintetico chave-temporaria-sintetica item-sintetico; do
    nao_tem "$proibido" || return 1
  done
}

responde_as_outras_perguntas() {
  medir "$T/raiz" ITEM=item-sintetico SLOT=1 || { mostrar; return 1; }
  # A espera não pode parecer travamento: o aviso vem antes (no teste, MEDIR_ESPERA=0).
  tem "aguardando 0 s para a segunda leitura do saldo..." || return 1
  tem "corrente 1: pendentes=0 · saldo mudou entre as leituras: não" || return 1
  tem "sinal BANK/CHECKING_ACCOUNT: DEBIT(-)=2 DEBIT(+)=0 DEBIT(0)=1 CREDIT(-)=0 CREDIT(+)=1 CREDIT(0)=0" || return 1
  tem "sinal CREDIT/CREDIT_CARD: DEBIT(-)=0 DEBIT(+)=4 DEBIT(0)=0 CREDIT(-)=1 CREDIT(+)=0 CREDIT(0)=0" || return 1
  tem "categoria 05100000 Credit card payment: 2" || return 1
  tem "data: com hora · fuso Z" || return 1
  # Hora das datas, por tipo de conta: meia-noite UTC (dia sem hora?), 03:00Z (meia-noite em São Paulo) ou outra.
  tem "horas BANK/CHECKING_ACCOUNT: 00:00Z=1 03:00Z=1 outras=2" || return 1
  tem "horas CREDIT/CREDIT_CARD: 00:00Z=1 03:00Z=1 outras=3" || return 1
  tem "valor zero: 1 · descrição vazia: 1" || return 1
  tem "parcelas: 1 com dados de parcela" || return 1
  tem "  billId: nas faturas=2 fora das faturas=0 sem billId=3" || return 1
  tem "fim da medição (nada gravado)"
}

# Usa só /v2/transactions com cursor, nunca /transactions; só POST em /auth; nenhum outro verbo.
so_le_e_usa_o_cursor() {
  : >"$T/servidor/registro"
  medir "$T/raiz" ITEM=item-sintetico SLOT=1 || { mostrar; return 1; }
  grep -q '^GET /v2/transactions?.*after=pagina-2' "$T/servidor/registro" || { echo "  sem cursor"; return 1; }
  ! grep -qE '^GET /transactions' "$T/servidor/registro" || { echo "  usou /transactions"; return 1; }
  ! grep -vE '^(GET |POST /auth$)' "$T/servidor/registro" | grep -q . || { echo "  verbo proibido"; return 1; }
}

nada_gravado() {
  local antes depois
  antes="$(find "$T" -path "$T/servidor" -prune -o -path "$T/saida" -prune -o -print | sort)"
  medir "$T/raiz" ITEM=item-sintetico SLOT=1 || { mostrar; return 1; }
  depois="$(find "$T" -path "$T/servidor" -prune -o -path "$T/saida" -prune -o -print | sort)"
  [ "$antes" = "$depois" ] || { echo "  arquivos novos:"; diff <(echo "$antes") <(echo "$depois") | sed 's/^/  | /'; return 1; }
}

nomes_de_11_11_valem_para_o_slot_1() {
  medir "$T/legado" ITEM=item-sintetico SLOT=1 || { mostrar; return 1; }
  tem "segredos: pluggy_client_id e pluggy_client_secret (nomes de 11/11)" || return 1
  tem "cartão 1: fecha com (c)"
}

sem_item_ou_segredo_falha_sem_rede() {
  : >"$T/servidor/registro"
  if medir "$T/raiz" SLOT=1; then echo "  rodou sem ITEM"; return 1; fi
  tem "uso: ITEM=<id> [SLOT=1|2] bash infra/pluggy/medir-saldos.sh" || return 1
  if medir "$T/home" ITEM=item-sintetico SLOT=2; then echo "  rodou sem segredo"; return 1; fi
  tem "FALTA pluggy_2_client_id legível" || return 1
  [ ! -s "$T/servidor/registro" ] || { echo "  chamou a rede"; return 1; }
}

credencial_recusada_mostra_so_o_status() {
  printf 'outro\n' >"$T/home/x"
  mkdir -p "$T/errado/infra/secrets"
  printf 'id-errado\n' >"$T/errado/infra/secrets/pluggy_1_client_id"
  printf 'segredo-errado\n' >"$T/errado/infra/secrets/pluggy_1_client_secret"
  if medir "$T/errado" ITEM=item-sintetico SLOT=1; then echo "  aceitou"; rm -rf "$T/errado" "$T/home/x"; return 1; fi
  rm -rf "$T/errado" "$T/home/x"
  tem "auth: HTTP 401" || return 1
  nao_tem "credenciais inválidas" || return 1
  nao_tem "segredo-errado"
}

caso investimentos_do_falso
caso cartao_fecha_com_c
caso banco_mostra_o_campo_do_codigo_sem_o_valor
caso saida_sem_valores_nem_descricoes
caso responde_as_outras_perguntas
caso so_le_e_usa_o_cursor
caso nada_gravado
caso nomes_de_11_11_valem_para_o_slot_1
caso sem_item_ou_segredo_falha_sem_rede
caso credencial_recusada_mostra_so_o_status

# MOSTRAR=1: imprime uma rodada completa contra a Pluggy falsa (para conferir o formato).
if [ "${MOSTRAR:-}" = 1 ]; then medir "$T/raiz" ITEM=item-sintetico SLOT=1 || true; mostrar; fi

[ "$falhas" -eq 0 ] || { echo "$falhas teste(s) falharam"; exit 1; }
echo "todos os testes da medição passaram"
