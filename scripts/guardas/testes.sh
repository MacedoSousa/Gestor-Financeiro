#!/usr/bin/env bash
# Testes das guardas (regras.sh, pre-commit, hook do Claude). Rodam num repositório git temporário.
# Uso: scripts/guardas/testes.sh      Saída: 0 = todos passaram.
set -uo pipefail

raiz="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=scripts/guardas/regras.sh
source "$raiz/scripts/guardas/regras.sh"

passou=0; falhou=0
ok()    { passou=$((passou + 1)); echo "ok    $1"; }
falha() { falhou=$((falhou + 1)); echo "FALHA $1"; }
espera_sucesso() { local d="$1"; shift; if "$@" >/dev/null 2>&1; then ok "$d"; else falha "$d"; fi; }
espera_falha()   { local d="$1"; shift; if "$@" >/dev/null 2>&1; then falha "$d"; else ok "$d"; fi; }

tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
sql() { local nome="$1"; shift; printf '%s\n' "$@" > "$tmp/$nome"; echo "$tmp/$nome"; }

# --- regras.sh -------------------------------------------------------------
espera_falha   "DELETE FROM é barrado"            bash -c "source '$raiz/scripts/guardas/regras.sh'; echo 'DELETE  FROM x' | checar_delete_fisico t"
espera_falha   "ExecuteDelete é barrado"          bash -c "source '$raiz/scripts/guardas/regras.sh'; echo 'q.ExecuteDeleteAsync()' | checar_delete_fisico t"
espera_falha   "TRUNCATE TABLE é barrado"         bash -c "source '$raiz/scripts/guardas/regras.sh'; echo 'truncate table x' | checar_delete_fisico t"
espera_sucesso "api.DELETE( do front passa"       bash -c "source '$raiz/scripts/guardas/regras.sh'; echo 'api.DELETE(\"/x\")' | checar_delete_fisico t"
espera_sucesso "trigger BEFORE DELETE passa"      bash -c "source '$raiz/scripts/guardas/regras.sh'; echo 'BEFORE DELETE OR TRUNCATE ON t' | checar_delete_fisico t"

espera_sucesso "infra/secrets/x é segredo"        eh_caminho_de_segredo infra/secrets/pg_senha_app
espera_sucesso ".env é segredo"                   eh_caminho_de_segredo infra/.env
espera_sucesso "caminho absoluto de segredo"      eh_caminho_de_segredo /repo/infra/secrets/x
espera_falha   ".env.example não é segredo"       eh_caminho_de_segredo infra/.env.example
espera_falha   "README dos segredos não é"        eh_caminho_de_segredo infra/secrets/README.md
espera_falha   "código comum não é segredo"       eh_caminho_de_segredo backend/src/x.cs

espera_sucesso "backend .cs é vigiado"            eh_codigo_vigiado backend/src/Modules/X/A.cs
espera_falha   "teste não é vigiado"              eh_codigo_vigiado backend/tests/X/A.cs
espera_falha   "markdown não é vigiado"           eh_codigo_vigiado docs/a.md

espera_sucesso "nome V0011__boletos.sql"          checar_nome_de_migracao db/migrations/V0011__boletos.sql
espera_falha   "nome sem 4 dígitos"               checar_nome_de_migracao db/migrations/V11__boletos.sql
espera_falha   "nome com maiúscula"               checar_nome_de_migracao db/migrations/V0011__Boletos.sql

espera_sucesso "sequência contínua"               checar_sequencia_de_migracoes a/V0001__a.sql a/V0002__b.sql a/V0003__c.sql
espera_falha   "sequência com buraco"             checar_sequencia_de_migracoes a/V0001__a.sql a/V0003__c.sql
espera_falha   "sequência repetida"               checar_sequencia_de_migracoes a/V0001__a.sql a/V0001__b.sql
espera_falha   "sequência sem V0001"              checar_sequencia_de_migracoes a/V0002__a.sql

boa="$(sql V0011__boa.sql \
  "CREATE TABLE boletos.vencimentos (id uuid PRIMARY KEY, is_deleted boolean NOT NULL DEFAULT false);" \
  "CREATE UNIQUE INDEX ux_v ON boletos.vencimentos (casal_id, linha_hmac)" \
  "    WHERE NOT is_deleted;" \
  "-- comentário com UNIQUE não conta" \
  "SELECT audit.proteger('boletos.vencimentos');")"
espera_sucesso "migração correta (WHERE na linha seguinte)" checar_conteudo_de_migracao "$boa"

sem_audit="$(sql V0011__a.sql "CREATE TABLE boletos.vencimentos (id uuid PRIMARY KEY);")"
espera_falha   "tabela sem audit.proteger"        checar_conteudo_de_migracao "$sem_audit"

unico_simples="$(sql V0011__b.sql \
  "CREATE TABLE boletos.x (id uuid PRIMARY KEY);" \
  "CREATE UNIQUE INDEX ux_x ON boletos.x (nome);" \
  "SELECT audit.proteger('boletos.x');")"
espera_falha   "UNIQUE sem índice parcial"        checar_conteudo_de_migracao "$unico_simples"

restricao="$(sql V0011__c.sql "CREATE TABLE boletos.y (id uuid PRIMARY KEY, nome text UNIQUE);" "SELECT audit.proteger('boletos.y');")"
espera_falha   "UNIQUE dentro do CREATE TABLE"    checar_conteudo_de_migracao "$restricao"

# Exceção estreita (FK composta, decisão do CEO na E3): (casal_id, id) contém a chave primária, então o soft
# delete não muda nada nela; o Postgres não aceita FK para índice parcial.
alvo_de_fk="$(sql V0011__g.sql "ALTER TABLE boletos.x ADD CONSTRAINT uq_x_casal_id UNIQUE (casal_id, id);")"
espera_sucesso "UNIQUE (casal_id, id) para FK composta" checar_conteudo_de_migracao "$alvo_de_fk"

outra_coluna="$(sql V0011__h.sql "ALTER TABLE boletos.x ADD CONSTRAINT uq_x_nome UNIQUE (casal_id, nome);")"
espera_falha   "UNIQUE (casal_id, coluna de negócio) continua recusado" checar_conteudo_de_migracao "$outra_coluna"

so_id="$(sql V0011__i.sql "ALTER TABLE boletos.x ADD CONSTRAINT uq_x_id UNIQUE (casal_id, id, nome);")"
espera_falha   "UNIQUE (casal_id, id, mais colunas) continua recusado" checar_conteudo_de_migracao "$so_id"

indice_alvo="$(sql V0011__j.sql "CREATE UNIQUE INDEX ux_x_casal_id ON boletos.x (casal_id, id);")"
espera_falha   "índice UNIQUE (casal_id, id) sem ADD CONSTRAINT continua recusado" checar_conteudo_de_migracao "$indice_alvo"

sem_schema="$(sql V0011__e.sql "CREATE TABLE sem_schema (id uuid PRIMARY KEY);")"
espera_falha   "CREATE TABLE sem schema"          checar_conteudo_de_migracao "$sem_schema"

sem_schema_audit="$(sql V0011__f.sql "CREATE TABLE sem_schema (id uuid PRIMARY KEY);" "SELECT audit.proteger('sem_schema');")"
espera_falha   "CREATE TABLE sem schema, mesmo com audit" checar_conteudo_de_migracao "$sem_schema_audit"

sem_schema_if="$(sql V0011__g.sql "CREATE TABLE IF NOT EXISTS sem_schema (id uuid PRIMARY KEY);")"
espera_falha   "CREATE TABLE IF NOT EXISTS sem schema" checar_conteudo_de_migracao "$sem_schema_if"

com_schema="$(sql V0011__h.sql "CREATE TABLE a.b (id uuid PRIMARY KEY);" "SELECT audit.proteger('a.b');")"
espera_sucesso "CREATE TABLE a.b com audit passa"  checar_conteudo_de_migracao "$com_schema"

apaga="$(sql V0011__d.sql "DELETE FROM boletos.x;")"
espera_falha   "DELETE físico em migração"        checar_conteudo_de_migracao "$apaga"

# Docker do snap (ADR-013): um docker falso na frente do PATH.
docker_falso() { mkdir -p "$tmp/$1"; printf '#!/bin/sh\nexit 0\n' >"$tmp/$1/docker"; chmod +x "$tmp/$1/docker"; }
docker_falso snap/bin
docker_falso usr/bin
mkdir -p "$tmp/sem-docker"; ln -s "$tmp/snap/bin/docker" "$tmp/sem-docker/link-para-snap"
docker_falso link/bin; rm "$tmp/link/bin/docker"; ln -s "$tmp/snap/bin/docker" "$tmp/link/bin/docker"
espera_sucesso "docker em /snap/bin é do snap"     env PATH="$tmp/snap/bin:/usr/bin:/bin" bash -c "source '$raiz/scripts/guardas/regras.sh'; docker_e_do_snap"
espera_sucesso "link para o docker do snap é do snap" env PATH="$tmp/link/bin:/usr/bin:/bin" bash -c "source '$raiz/scripts/guardas/regras.sh'; docker_e_do_snap"
# Como no snap de verdade: /snap/bin/docker é um link para /usr/bin/snap.
mkdir -p "$tmp/real/snap/bin" "$tmp/real/usr/bin"; printf '#!/bin/sh\nexit 0\n' >"$tmp/real/usr/bin/snap"
chmod +x "$tmp/real/usr/bin/snap"; ln -s "$tmp/real/usr/bin/snap" "$tmp/real/snap/bin/docker"
espera_sucesso "/snap/bin/docker → /usr/bin/snap é do snap" env PATH="$tmp/real/snap/bin:/usr/bin:/bin" bash -c "source '$raiz/scripts/guardas/regras.sh'; docker_e_do_snap"
espera_falha   "docker em /usr/bin não é do snap"  env PATH="$tmp/usr/bin:/usr/bin:/bin" bash -c "source '$raiz/scripts/guardas/regras.sh'; docker_e_do_snap"
espera_falha   "sem docker no PATH não é do snap"  env PATH="$tmp/sem-docker" /bin/bash -c "source '$raiz/scripts/guardas/regras.sh'; docker_e_do_snap"

# --- pre-commit ------------------------------------------------------------
repo="$tmp/repo"
mkdir -p "$repo" && cd "$repo" || exit 1
git init -q . && git config user.email t@t && git config user.name t
mkdir -p scripts/guardas .githooks db/migrations backend/src backend/tests infra/secrets
cp "$raiz/scripts/guardas/regras.sh" scripts/guardas/ && cp "$raiz/.githooks/pre-commit" .githooks/
git config core.hooksPath .githooks
printf '%s\n' "CREATE TABLE a.t (id uuid PRIMARY KEY);" "SELECT audit.proteger('a.t');" > db/migrations/V0001__inicio.sql
git add -A
if git commit -qm base >/dev/null 2>&1; then ok "pre-commit aceita base válida"; else falha "pre-commit aceita base válida"; fi

# Commit de teste. Sem nada no stage o teste é inválido: devolve 99, que nenhum espera_* aceita como resposta certa.
commit() {
  git add -A >/dev/null 2>&1
  if git diff --cached --quiet; then echo "teste inválido: nada no stage" >&2; return 99; fi
  git commit -qm teste >/dev/null 2>&1
}
deve_recusar() { local d="$1"; commit; local r=$?; if [ "$r" -eq 1 ]; then ok "$d"; else falha "$d (saída $r)"; fi; desfaz; }
deve_aceitar() { local d="$1"; commit; local r=$?; if [ "$r" -eq 0 ]; then ok "$d"; else falha "$d (saída $r)"; fi; }
desfaz() { git reset -q --hard HEAD >/dev/null 2>&1; git clean -qfd >/dev/null 2>&1; mkdir -p backend/src backend/tests infra/secrets db/migrations; }

echo "x" > infra/secrets/pg_senha
deve_recusar "pre-commit barra segredo"

echo "-- mudou" >> db/migrations/V0001__inicio.sql
deve_recusar "pre-commit barra migração já commitada"

printf '%s\n' "CREATE TABLE a.u (id uuid PRIMARY KEY);" "SELECT audit.proteger('a.u');" > db/migrations/V0002__nova.sql
deve_aceitar "pre-commit aceita migração nova correta"

printf '%s\n' "CREATE TABLE a.v (id uuid PRIMARY KEY);" > db/migrations/V0003__sem_audit.sql
deve_recusar "pre-commit barra migração sem audit"

printf '%s\n' "CREATE TABLE a.w (id uuid PRIMARY KEY);" "SELECT audit.proteger('a.w');" > db/migrations/V0005__pulou.sql
deve_recusar "pre-commit barra buraco na numeração"

echo 'await db.X.ExecuteDeleteAsync();' > backend/src/A.cs
deve_recusar "pre-commit barra DELETE em código"

echo 'Assert.Throws(() => Sql("DELETE FROM x"));' > backend/tests/A.cs
deve_aceitar "pre-commit deixa teste citar DELETE"

# --- varredura (verificar-repositorio.sh) ---------------------------------
desfaz
cp "$raiz/scripts/guardas/verificar-repositorio.sh" scripts/guardas/
git add -A >/dev/null 2>&1 && git commit -qm varredura >/dev/null 2>&1
espera_sucesso "varredura passa no repositório válido" scripts/guardas/verificar-repositorio.sh "$repo"
printf '%s\n' "CREATE TABLE a.z (id uuid PRIMARY KEY, nome text UNIQUE);" "SELECT audit.proteger('a.z');" > db/migrations/V0003__nova_invalida.sql
espera_falha   "varredura pega migração nova ainda fora do git" scripts/guardas/verificar-repositorio.sh "$repo"
desfaz
echo 'db.X.ExecuteDelete();' > backend/src/B.cs
espera_falha   "varredura pega código novo ainda fora do git" scripts/guardas/verificar-repositorio.sh "$repo"
desfaz

# --- hook do Claude ---------------------------------------------------------
hook="$raiz/.claude/hooks/bloquear-delete-fisico.sh"
confere_hook() { if [ "$(roda_hook "$3")" = "$2" ]; then ok "$1"; else falha "$1"; fi; }
roda_hook() { printf '%s' "$1" | CLAUDE_PROJECT_DIR="$raiz" "$hook" >/dev/null 2>&1; echo $?; }
confere_hook "hook do Claude barra DELETE" 2 '{"file_path":"'"$raiz"'/backend/src/A.cs","new_string":"DELETE FROM x"}'
confere_hook "hook do Claude deixa soft delete" 0 '{"file_path":"'"$raiz"'/backend/src/A.cs","new_string":"x.IsDeleted = true"}'
confere_hook "hook do Claude barra segredo" 2 '{"file_path":"'"$raiz"'/infra/secrets/x","content":"a"}'
confere_hook "hook do Claude deixa .env.example" 0 '{"file_path":"'"$raiz"'/infra/.env.example","content":"a"}'
confere_hook "hook do Claude deixa teste citar DELETE" 0 '{"file_path":"'"$raiz"'/backend/tests/A.cs","new_string":"DELETE FROM x"}'

echo ""
echo "$passou passaram, $falhou falharam"
[ "$falhou" -eq 0 ]
