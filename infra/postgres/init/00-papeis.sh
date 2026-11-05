#!/usr/bin/env bash
# Roda uma única vez, na criação do volume do Postgres.
# gf_migrator: dono dos schemas, aplica db/migrations.  gf_app: a API, sem DELETE nem TRUNCATE.
set -euo pipefail

senha_app="$(cat /run/secrets/pg_senha_app)"
senha_migrador="$(cat /run/secrets/pg_senha_migrador)"

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v senha_app="$senha_app" -v senha_migrador="$senha_migrador" <<-'EOSQL'
	CREATE ROLE gf_migrator LOGIN PASSWORD :'senha_migrador';
	CREATE ROLE gf_app LOGIN PASSWORD :'senha_app' CONNECTION LIMIT 30;
	ALTER DATABASE gestor OWNER TO gf_migrator;
	REVOKE ALL ON DATABASE gestor FROM PUBLIC;
	GRANT CONNECT ON DATABASE gestor TO gf_app;
	ALTER ROLE gf_app SET statement_timeout = '15s';
EOSQL
