#!/usr/bin/env bash
# shellcheck source-path=SCRIPTDIR
# Funções dos testes do backup: sobe um banco-fonte sintético, gera dump e adultera evento.
# Uso: source "$(dirname "$0")/comum.sh"   (o chamador define o trap que chama fonte_derrubar)
# Dados 100% sintéticos: casais e UUIDs fixos, como em db/testes/verificar-protecoes.sh.

TESTES_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RAIZ_REPO="$(cd "$TESTES_DIR/../../.." && pwd)"
# shellcheck source=../lib.sh
source "$TESTES_DIR/../lib.sh"

CASAL_1="00000000-0000-4000-8000-0000000000b1"
CASAL_2="00000000-0000-4000-8000-0000000000b2"
NOME_SINTETICO_1="Familia Sintetica Xyzzy"
NOME_SINTETICO_2="Familia Sintetica Plugh"
FONTE_CONTAINER=""

# psql no banco-fonte, sem esconder o stderr (dados sintéticos).
psql_fonte() {
  local papel="$1"; shift
  docker exec -i "$FONTE_CONTAINER" psql -X -q -t -A -v ON_ERROR_STOP=1 -U "$papel" -d gestor "$@"
}

# Sobe o banco-fonte, aplica db/migrations como gf_migrator e semeia 2 casais com INSERT e UPDATE.
fonte_subir() {
  FONTE_CONTAINER="gf-teste-fonte-$(aleatorio)"
  export FONTE_CONTAINER
  docker run -d --rm --network none --tmpfs /var/lib/postgresql/data \
    --name "$FONTE_CONTAINER" -e POSTGRES_PASSWORD="$(aleatorio)" -e POSTGRES_DB=gestor \
    "$GF_IMAGEM_POSTGRES" >/dev/null
  esperar_postgres "$FONTE_CONTAINER"

  # Papéis como no CI (.github/workflows/ci.yml).
  psql_fonte postgres \
    -c "CREATE ROLE gf_migrator LOGIN PASSWORD 'ci'; CREATE ROLE gf_app LOGIN PASSWORD 'ci';" \
    -c "ALTER DATABASE gestor OWNER TO gf_migrator;" >/dev/null

  local migracao
  while IFS= read -r migracao; do
    psql_fonte gf_migrator --single-transaction <"$migracao" >/dev/null
  done < <(find "$RAIZ_REPO/db/migrations" -maxdepth 1 -name 'V*.sql' | LC_ALL=C sort)

  psql_fonte gf_app >/dev/null <<SQL
SELECT set_config('app.ip', 'teste-backup', false);
INSERT INTO identidade.casais (id, nome_familia) VALUES ('$CASAL_1', '$NOME_SINTETICO_1');
UPDATE identidade.casais SET nome_familia = '$NOME_SINTETICO_1 B' WHERE id = '$CASAL_1';
INSERT INTO identidade.casais (id, nome_familia) VALUES ('$CASAL_2', '$NOME_SINTETICO_2');
UPDATE identidade.casais SET nome_familia = '$NOME_SINTETICO_2 B' WHERE id = '$CASAL_2';
UPDATE identidade.casais SET nome_familia = '$NOME_SINTETICO_2 C' WHERE id = '$CASAL_2';
SQL
}

fonte_derrubar() {
  [ -n "$FONTE_CONTAINER" ] && docker rm -f "$FONTE_CONTAINER" >/dev/null 2>&1
  FONTE_CONTAINER=""
}

# pg_dump -Fc do banco-fonte, como postgres (igual ao backup real).
fonte_dump() {
  local saida="$1"
  docker exec "$FONTE_CONTAINER" pg_dump -U postgres -d gestor -Fc >"$saida"
}

# Adultera, como superusuário, o primeiro evento do casal 1. Imprime o seq adulterado.
fonte_adulterar_evento() {
  psql_fonte postgres <<SQL
ALTER TABLE audit.eventos DISABLE TRIGGER trg_eventos_imutavel;
UPDATE audit.eventos SET alteracoes = alteracoes || '{"adulterado": true}'::jsonb
 WHERE seq = (SELECT min(seq) FROM audit.eventos WHERE casal_id = '$CASAL_1')
RETURNING seq;
ALTER TABLE audit.eventos ENABLE TRIGGER trg_eventos_imutavel;
SQL
}
