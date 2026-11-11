#!/usr/bin/env bash
# Medição da Fase 2 (Tarefa 1.0 do plano): o que o saldo e o "valor usado" do banco incluem, o
# sinal dos valores, as categorias da Pluggy, o formato das datas e de onde vem o banco.
# Só leitura (POST /auth e GET), só ESTRUTURA na saída: contagens, nomes de campo, nomes
# genéricos de categoria e booleanos. Nunca imprime chave, saldo, valor, descrição, nome,
# número ou código. Nada é gravado em disco. Modelo: testar-pluggy.sh do CEO (v2, com cursor).
#
# Rodado pela PESSOA, nunca por agente (usa as chaves de verdade), a partir da raiz do repositório:
#   ITEM=<id do item> bash infra/pluggy/medir-saldos.sh
# Ambiente: ITEM (obrigatório), SLOT (1 ou 2; padrão 1). As chaves vêm de
# infra/secrets/pluggy_<slot>_client_id e _client_secret; no slot 1, se elas estiverem ausentes
# ou vazias, valem os nomes de 11/11 (pluggy_client_id e pluggy_client_secret).
# Para os testes: PLUGGY_URL, MEDIR_HOJE (AAAA-MM-DD) e MEDIR_ESPERA (segundos entre as duas
# leituras de saldo; padrão 60). Argumento opcional: a raiz do repositório.
set -euo pipefail

raiz="${1:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
dir="$raiz/infra/secrets"
api="${PLUGGY_URL:-https://api.pluggy.ai}"
desde="2026-01-01"
hoje="${MEDIR_HOJE:-$(TZ=America/Sao_Paulo date +%F)}"
espera="${MEDIR_ESPERA:-60}"
slot="${SLOT:-1}"

for c in curl jq; do command -v "$c" >/dev/null || { echo "FALTA $c (sudo apt install -y $c)"; exit 1; }; done
[ -n "${ITEM:-}" ] || { echo "uso: ITEM=<id> [SLOT=1|2] bash infra/pluggy/medir-saldos.sh"; exit 2; }
[[ "$slot" =~ ^[12]$ ]] || { echo "uso: ITEM=<id> [SLOT=1|2] bash infra/pluggy/medir-saldos.sh"; exit 2; }

arq_id="$dir/pluggy_${slot}_client_id"; arq_sec="$dir/pluggy_${slot}_client_secret"
if { [ ! -s "$arq_id" ] || [ ! -s "$arq_sec" ]; } && [ "$slot" = 1 ] \
   && [ -s "$dir/pluggy_client_id" ] && [ -s "$dir/pluggy_client_secret" ]; then
  arq_id="$dir/pluggy_client_id"; arq_sec="$dir/pluggy_client_secret"
  echo "segredos: pluggy_client_id e pluggy_client_secret (nomes de 11/11)"
fi
for f in "$arq_id" "$arq_sec"; do
  if [ ! -s "$f" ] || [ ! -r "$f" ]; then echo "FALTA $(basename "$f") legível"; exit 1; fi
done

# Autenticação: o corpo vai pela entrada padrão, nunca pela linha de comando.
resposta="$(jq -n --rawfile id "$arq_id" --rawfile sec "$arq_sec" \
  '{clientId: ($id|rtrimstr("\n")), clientSecret: ($sec|rtrimstr("\n"))}' |
  curl -sS -X POST "$api/auth" -H 'Content-Type: application/json' --data @- -w '\n%{http_code}')"
codigo="${resposta##*$'\n'}"; corpo="${resposta%$'\n'*}"
[ "$codigo" = 200 ] || { echo "auth: HTTP $codigo"; exit 1; }
chave="$(jq -r '.apiKey' <<<"$corpo")"; unset resposta corpo
echo "auth: ok (chave temporária só em memória)"

# GET autenticado; a chave vai por descritor de arquivo, fora da lista de processos.
get() {
  local s
  s="$(curl -sS "$api$1" -H @<(printf 'X-API-KEY: %s\n' "$chave") -w '\n%{http_code}')"
  GET_CODIGO="${s##*$'\n'}"; GET_CORPO="${s%$'\n'*}"
}
# Status e código do erro; nunca a mensagem nem o corpo.
erro() { echo "$1: HTTP $GET_CODIGO $(jq -r '.code // empty' <<<"$GET_CORPO" 2>/dev/null || true)"; }

# Campos que podem dizer o banco: caminhos com nome de banco/instituição/conector e caminhos
# cujo valor é texto de exatamente 3 dígitos (código de banco). Só o caminho sai, nunca o valor.
# shellcheck disable=SC2016 # programa jq: os $ são do jq
CAMPOS_DO_BANCO='[paths(scalars) as $p | {c: ($p | map(tostring) | join(".")), v: getpath($p)}
  | select((.c | test("bank|institution|connector|compe|ispb"; "i")) or ((.v|type) == "string" and (.v | test("^[0-9]{3}$"))))
  | select(.c | test("(^|\\.)[0-9]+(\\.|$)") | not)
  | "\(.c) · código: \(if (.v|type) == "string" and (.v | test("^[0-9]{3}$")) then "sim" else "não" end)"]'

get "/items/$ITEM"
[ "$GET_CODIGO" = 200 ] || { erro "item"; unset chave; exit 1; }
jq -r '"item: conector=\(.connector.name // "?") estado=\(.status // "?") execução=\(.executionStatus // "?") consentimento_vence=\(.consentExpiresAt // "—" | .[0:10])"' <<<"$GET_CORPO"
bancos="$(jq -c "$CAMPOS_DO_BANCO | map(\"item.\" + .)" <<<"$GET_CORPO")"

get "/accounts?itemId=$ITEM"
[ "$GET_CODIGO" = 200 ] || { erro "contas"; unset chave; exit 1; }
contas="$GET_CORPO"
echo "contas: $(jq '.results | length' <<<"$contas")"
bancos="$(jq -c --argjson b "$bancos" "[.results[] | ${CAMPOS_DO_BANCO}[]] + \$b | unique" <<<"$contas")"

# Todas as transações de uma conta desde $desde, pelo cursor; só os campos usados na medição.
todas_as_transacoes() {
  local conta="$1" depois="" paginas=0 proximo acumulado="[]"
  while :; do
    get "/v2/transactions?accountId=$conta&dateFrom=$desde${depois:+&after=$depois}"
    [ "$GET_CODIGO" = 200 ] || { erro "  transações" >&2; break; }
    acumulado="$(jq -c --argjson a "$acumulado" '$a + [.results[] | {date, amount, type, status,
      vazia: ((.description // "") | length == 0), cat: (.categoryId // "(sem)"), nome: (.category // "(sem)"),
      bill: (.creditCardMetadata.billId // null), parcela: (.creditCardMetadata.installmentNumber != null)}]' <<<"$GET_CORPO")"
    paginas=$((paginas + 1))
    proximo="$(jq -r '.next // empty' <<<"$GET_CORPO")"
    [ -n "$proximo" ] || break
    depois="$(sed -n 's/.*[?&]after=\([^&]*\).*/\1/p' <<<"$proximo")"; [ -n "$depois" ] || break
    [ "$paginas" -lt 50 ] || { echo "  (parado em 50 páginas)" >&2; break; }
  done
  printf '%s' "$acumulado"
}

# Fórmulas do cartão (D3), comparadas com o valor usado sem imprimir nenhum dos dois.
# u = quanto a transação usa do limite: DEBIT soma, CREDIT abate (pelo módulo do valor).
# shellcheck disable=SC2016 # programa jq: os $ são do jq
FORMULAS='def u: (if .type == "CREDIT" then -1 else 1 end) * (.amount | fabs);
  def soma(f): map(select(f) | u) | add // 0;
  (.faturas) as $faturas | .t as $t | .saldo as $saldo |
  ($t | map(.bill) | map(select(. != null)) | length > 0) as $por_bill |
  ($faturas | map(.dueDate[0:10]) | max // "0000") as $ultimo_venc |
  def aberta: if $por_bill then (.bill == null or (.bill as $b | ($faturas | map(.id) | index($b)) == null))
              else (.date[0:10] > $ultimo_venc) end;
  def futura: .date[0:10] > $hoje;
  ($t | soma(aberta and (futura | not) and .status == "POSTED")) as $a |
  ($t | soma(aberta and (futura | not) and .status == "PENDING")) as $p |
  ($t | soma(futura)) as $f |
  {a: $a, b: ($a + $p), c: ($a + $p + $f), d: ($a + $f)} as $f4 |
  ($saldo | fabs) as $s |
  [$f4 | to_entries[] | select(((.value | fabs) - $s | fabs) < 0.005) | "(\(.key))"] as $fecham |
  (if $por_bill then "por billId" else "por janela desde o último vencimento" end) as $modo |
  if ($fecham | length) > 0 then "fecha com \($fecham | join(" e ")) · fatura aberta \($modo)"
  else ([$f4[] | ((.|fabs) - $s | fabs) / (if $s == 0 then 1 else $s end)] | min) as $r |
       "nenhuma; diferença \(if $r < 0.01 then "<1%" elif $r <= 0.10 then "1–10%" else ">10%" end) · fatura aberta \($modo)"
  end'

tudo="[]"; n_cartao=0; n_corrente=0; correntes=()
for conta in $(jq -r '.results[].id' <<<"$contas"); do
  tipo="$(jq -r --arg c "$conta" '.results[] | select(.id == $c) | "\(.type)/\(.subtype // "-")"' <<<"$contas")"
  t="$(todas_as_transacoes "$conta")"
  tudo="$(jq -c --argjson a "$tudo" --arg tipo "$tipo" '$a + map(. + {tipo: $tipo})' <<<"$t")"
  case "$tipo" in
    CREDIT/*)
      n_cartao=$((n_cartao + 1))
      get "/bills?accountId=$conta"
      if [ "$GET_CODIGO" = 200 ]; then faturas="$(jq -c '.results // []' <<<"$GET_CORPO")"; else erro "  faturas"; faturas="[]"; fi
      saldo="$(jq -r --arg c "$conta" '.results[] | select(.id == $c) | .balance // 0' <<<"$contas")"
      echo "cartão $n_cartao: $(jq -nr --argjson t "$t" --argjson faturas "$faturas" --argjson saldo "$saldo" \
        --arg hoje "$hoje" '{t: $t, faturas: $faturas, saldo: $saldo} | '"$FORMULAS")"
      echo "  faturas: $(jq 'length' <<<"$faturas") · transações: $(jq 'length' <<<"$t")"
      # Se /bills também listar a fatura aberta, "fora das faturas" fica 0 e a fórmula não fecha.
      echo "  billId: nas faturas=$(jq --argjson f "$faturas" '[.[] | select(.bill != null and (.bill as $b | ($f | map(.id) | index($b)) != null))] | length' <<<"$t") fora das faturas=$(jq --argjson f "$faturas" '[.[] | select(.bill != null and (.bill as $b | ($f | map(.id) | index($b)) == null))] | length' <<<"$t") sem billId=$(jq '[.[] | select(.bill == null)] | length' <<<"$t")"
      ;;
    BANK/*)
      n_corrente=$((n_corrente + 1))
      correntes+=("$n_corrente:$conta:$(jq '[.[] | select(.status == "PENDING")] | length' <<<"$t")")
      ;;
  esac
done

# Segunda leitura dos saldos: a Pluggy atualiza o saldo da corrente sem transação nova?
if [ "${#correntes[@]}" -gt 0 ]; then
  sleep "$espera"
  get "/accounts?itemId=$ITEM"
  if [ "$GET_CODIGO" = 200 ]; then
    for c in "${correntes[@]}"; do
      IFS=: read -r n id pend <<<"$c"
      mudou="$(jq -nr --argjson a "$contas" --argjson b "$GET_CORPO" --arg id "$id" \
        '[$a, $b] | map(.results[] | select(.id == $id) | .balance) | if .[0] == .[1] then "não" else "sim" end')"
      echo "corrente $n: pendentes=$pend · saldo mudou entre as leituras: $mudou"
    done
  else
    erro "segunda leitura"
  fi
fi

jq -r 'group_by(.tipo)[] | .[0].tipo as $tipo |
  def n(t; s): map(select(.type == t and (if s == "-" then .amount < 0 elif s == "+" then .amount > 0 else .amount == 0 end))) | length;
  "sinal \($tipo): DEBIT(-)=\(n("DEBIT"; "-")) DEBIT(+)=\(n("DEBIT"; "+")) DEBIT(0)=\(n("DEBIT"; "0")) CREDIT(-)=\(n("CREDIT"; "-")) CREDIT(+)=\(n("CREDIT"; "+")) CREDIT(0)=\(n("CREDIT"; "0"))"' <<<"$tudo"
jq -r 'group_by(.cat)[] | "categoria \(.[0].cat) \(.[0].nome): \(length)"' <<<"$tudo"
jq -r 'map(.date // "") |
  (if all(test("T[0-9]{2}:[0-9]{2}")) then "com hora" elif any(test("T[0-9]{2}:[0-9]{2}")) then "misto" else "sem hora" end) as $h |
  (if all(test("Z$")) then "Z" elif all(test("[+-][0-9]{2}:?[0-9]{2}$")) then "deslocamento" elif any(test("Z$|[+-][0-9]{2}:?[0-9]{2}$")) then "misto" else "sem fuso" end) as $f |
  "data: \($h) · fuso \($f)"' <<<"$tudo"
jq -r '"valor zero: \(map(select(.amount == 0)) | length) · descrição vazia: \(map(select(.vazia)) | length)"' <<<"$tudo"
jq -r '"parcelas: \(map(select(.parcela)) | length) com dados de parcela"' <<<"$tudo"
if [ "$(jq 'length' <<<"$bancos")" -gt 0 ]; then
  jq -r '.[] | "banco: campo \(.)"' <<<"$bancos"
else
  echo "banco: nenhum campo com nome de banco ou código de 3 dígitos"
fi

unset chave
echo "fim da medição (nada gravado)"
