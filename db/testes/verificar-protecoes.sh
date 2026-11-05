#!/usr/bin/env bash
# Confere, num banco recém-migrado, as garantias do V0001:
# DELETE/TRUNCATE bloqueados, auditoria gravada e cadeia de hashes íntegra.
# Uso: db/testes/verificar-protecoes.sh "host=localhost dbname=gestor"   (senhas via PGPASSWORD)
set -uo pipefail

conexao="${1:?informe a conexão, ex.: host=localhost dbname=gestor}"
casal="00000000-0000-4000-8000-0000000000c1"
falhas=0

como() { local papel="$1"; shift; psql "$conexao user=$papel" -X -q -t -A -v ON_ERROR_STOP=1 "$@"; }

ok()   { echo "ok    $1"; }
erro() { echo "FALHA $1"; falhas=$((falhas + 1)); }

deve_falhar() {
  local descricao="$1" papel="$2" sql="$3"
  if como "$papel" -c "$sql" >/dev/null 2>&1; then erro "$descricao"; else ok "$descricao"; fi
}

# A tabela precisa ter linha: trigger de linha não dispara num DELETE que não acha nada.
como gf_app -c "SELECT set_config('app.ip', 'ci', false);
                INSERT INTO identidade.casais (id, nome_familia) VALUES ('$casal', 'CI');
                UPDATE identidade.casais SET nome_familia = 'CI 2' WHERE id = '$casal';" >/dev/null \
  && ok "gf_app insere e altera" || erro "gf_app insere e altera"

deve_falhar "gf_app não consegue DELETE"            gf_app   "DELETE FROM identidade.casais WHERE id = '$casal'"
deve_falhar "superusuário bloqueado no DELETE"      postgres "DELETE FROM identidade.casais WHERE id = '$casal'"
deve_falhar "superusuário bloqueado no TRUNCATE"    postgres "TRUNCATE identidade.casais CASCADE"
deve_falhar "gf_app não escreve na auditoria"       gf_app   "INSERT INTO audit.eventos (tabela, entidade_id, acao, alteracoes, hash) VALUES ('x', gen_random_uuid(), 'INSERT', '{}', '\\x00')"
deve_falhar "auditoria não aceita UPDATE"           postgres "UPDATE audit.eventos SET ip = 'x'"

eventos="$(como postgres -c "SELECT count(*) FROM audit.eventos WHERE casal_id = '$casal'")"
[ "$eventos" = "2" ] && ok "auditoria gravou INSERT e UPDATE" || erro "auditoria gravou INSERT e UPDATE (achou $eventos)"

quebras="$(como gf_app -c "SELECT count(*) FROM audit.verificar_cadeia('$casal')")"
[ "$quebras" = "0" ] && ok "cadeia de hashes íntegra" || erro "cadeia de hashes íntegra"

[ "$falhas" -eq 0 ] || { echo "$falhas verificação(ões) falharam"; exit 1; }
