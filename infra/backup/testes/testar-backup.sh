#!/usr/bin/env bash
# shellcheck source-path=SCRIPTDIR
# Testes de backup.sh e de ler_env (lib.sh).
#
# O backup roda numa raiz falsa (GF_RAIZ sintético): infra/backup/ com cópias de backup.sh,
# lib.sh e verificar-dump.sh e com rotacionar.sh/enviar.sh falsos que só deixam marcas;
# infra/secrets/ e infra/data/chaves/ com conteúdo sintético; infra/.env sintético;
# db/migrations apontando para o do repositório. Nunca toca nos segredos reais.
set -euo pipefail

# shellcheck source=comum.sh
source "$(dirname "${BASH_SOURCE[0]}")/comum.sh"
BACKUP_REAL="$TESTES_DIR/../backup.sh"

umask 077
GF_EXECUCAO="teste-$(aleatorio)"
export GF_EXECUCAO
T="$(mktemp -d)"
RAIZ_FALSA="$T/raiz"
MARCAS="$T/marcas"
CHAVE="$T/chave.txt"
DESTINATARIOS="$T/destinatarios.txt"
TMP_RUN="$T/tmp-run"
falhas=0
sobras=0

limpar() {
  fonte_derrubar
  rm -rf "$T"
}
trap limpar EXIT
trap 'exit 130' INT TERM

ok()    { echo "ok $1"; }
falha() { echo "FALHA $1"; falhas=$((falhas + 1)); }

descartaveis_desta_execucao() {
  docker ps -aq --filter "label=gf.execucao=$GF_EXECUCAO" --filter name=gf-verificacao-
}

# Cada caso recebe um par age novo, marcas limpas e um GF_TMP vazio.
caso() {
  local nome="$1"
  rm -f "$CHAVE" "$DESTINATARIOS"
  age-keygen -o "$CHAVE" 2>/dev/null
  age-keygen -y "$CHAVE" >"$DESTINATARIOS"
  rm -rf "$MARCAS" "$TMP_RUN"; mkdir "$MARCAS" "$TMP_RUN"
  if "$nome"; then ok "$nome"; else falha "$nome"; fi
  if [ -n "$(descartaveis_desta_execucao)" ]; then
    echo "  sobrou descartável desta execução depois de $nome"
    sobras=$((sobras + 1))
    descartaveis_desta_execucao | xargs -r docker rm -f >/dev/null
  fi
}

# Monta a raiz falsa. Os .sh de verdade são copiados; rotacionar/enviar são falsos.
montar_raiz_falsa() {
  local b="$RAIZ_FALSA/infra/backup"
  mkdir -p "$b" "$RAIZ_FALSA/infra/secrets" "$RAIZ_FALSA/infra/data/chaves" "$RAIZ_FALSA/db"
  cp "$TESTES_DIR/../backup.sh" "$TESTES_DIR/../lib.sh" "$TESTES_DIR/../verificar-dump.sh" "$b/"
  cat >"$b/rotacionar.sh" <<EOF
#!/usr/bin/env bash
touch "$MARCAS/rotacionar"
rm -f "\$GF_BACKUP_DIR/diario/sentinela"
EOF
  cat >"$b/enviar.sh" <<EOF
#!/usr/bin/env bash
printf '%s\n' "\$@" >"$MARCAS/enviar.args"
env >"$MARCAS/enviar.env"
exit "\$(cat "$MARCAS/enviar.codigo" 2>/dev/null || echo 0)"
EOF
  chmod 700 "$b"/*.sh
  ln -s "$RAIZ_REPO/db/migrations" "$RAIZ_FALSA/db/migrations"
  printf 'senha-sintetica-pg\n' >"$RAIZ_FALSA/infra/secrets/pg_superusuario"
  printf 'chave-servico-sintetica\n' >"$RAIZ_FALSA/infra/secrets/gf_chave_servico"
  printf '# leia-me sintético\n' >"$RAIZ_FALSA/infra/secrets/README.md"
  printf 'dek-sintetica\n' >"$RAIZ_FALSA/infra/data/chaves/dek.servico"
  printf 'DUCKDNS_TOKEN=segredo-teste\n' >"$RAIZ_FALSA/infra/.env"
}

# Roda o backup. Uso: rodar_backup <dir de backup> [args do backup.sh]. stderr em $T/stderr.
rodar_backup() {
  local dir="$1"; shift
  GF_BACKUP_DIR="$dir" GF_BACKUP_DESTINATARIOS="$DESTINATARIOS" PG_CONTAINER="$FONTE_CONTAINER" \
    GF_TMP="$TMP_RUN" "$RAIZ_FALSA/infra/backup/backup.sh" "$@" 2>"$T/stderr"
}

# Decifra o pacote e extrai em <destino>.
abrir_pacote() {
  local pacote="$1" destino="$2"
  rm -rf "$destino"; mkdir -p "$destino"
  age -d -i "$CHAVE" "$pacote" | tar -C "$destino" -xf -
}

mostrar_stderr() { sed 's/^/  | /' "$T/stderr"; }

contagens_fonte() {
  psql_fonte postgres -F ' ' -c "SELECT count(*), coalesce(max(seq), 0) FROM audit.eventos"
}

# ---------------------------------------------------------------- ler_env (sem docker)

ler_env_le_so_a_chave() {
  local antigo_raiz="${GF_RAIZ:-}"
  GF_RAIZ="$T/env-teste"; mkdir -p "$GF_RAIZ/infra"
  cat >"$GF_RAIZ/infra/.env" <<'EOF'
DUCKDNS_TOKEN=segredo-teste
GF_BACKUP_DIR=/primeiro
GF_BACKUP_DIR="/com aspas"
GF_BACKUP_DESTINATARIOS='/simples'
GF_BACKUP_RCLONE_REMOTO=
# GF_BACKUP_PENDRIVE_PASTA=/comentado
 GF_BACKUP_PENDRIVE_MONTAGEM=/com-espaco-antes
EOF
  local r=0
  [ "$(ler_env GF_BACKUP_DIR)" = "/com aspas" ] || { echo "  última ocorrência/aspas"; r=1; }
  [ "$(ler_env GF_BACKUP_DESTINATARIOS)" = "/simples" ] || { echo "  aspas simples"; r=1; }
  [ "$(ler_env GF_BACKUP_RCLONE_REMOTO padrao)" = "padrao" ] || { echo "  vazio usa padrão"; r=1; }
  [ "$(ler_env GF_BACKUP_PENDRIVE_PASTA padrao)" = "padrao" ] || { echo "  comentário"; r=1; }
  [ "$(ler_env GF_BACKUP_PENDRIVE_MONTAGEM x)" = "x" ] || { echo "  só início de linha"; r=1; }
  [ "$(GF_BACKUP_DIR=/do-ambiente ler_env GF_BACKUP_DIR)" = "/do-ambiente" ] || { echo "  ambiente"; r=1; }
  if ler_env 'X;Y' >/dev/null 2>&1; then echo "  chave inválida aceita"; r=1; fi
  rm -f "$GF_RAIZ/infra/.env"
  [ "$(ler_env GF_BACKUP_DIR /var/backups/gestor)" = "/var/backups/gestor" ] || { echo "  sem .env"; r=1; }
  GF_RAIZ="$antigo_raiz"
  return "$r"
}

# ---------------------------------------------------------------- backup.sh

gera_pacote_decifravel() {
  local dir="$T/b-decifravel" lista total max_seq linha arquivo hash
  rodar_backup "$dir" --data 2026-11-10 || { mostrar_stderr; return 1; }
  lista="$(age -d -i "$CHAVE" "$dir/diario/gestor-2026-11-10.tar.age" | tar -tf -)" || return 1
  for arquivo in gestor.dump MANIFESTO cadeia.txt secrets/pg_superusuario secrets/gf_chave_servico chaves/dek.servico; do
    grep -qx "$arquivo" <<<"$lista" || { echo "  falta $arquivo no pacote"; return 1; }
  done
  ! grep -q "README.md" <<<"$lista" || { echo "  README.md entrou no pacote"; return 1; }
  # MANIFESTO: contagens da fonte e sha256 de cada arquivo conferem.
  abrir_pacote "$dir/diario/gestor-2026-11-10.tar.age" "$T/aberto"
  read -r total max_seq _ <<<"$(contagens_fonte)"
  grep -qx "eventos_total=$total" "$T/aberto/MANIFESTO" || return 1
  grep -qx "eventos_max_seq=$max_seq" "$T/aberto/MANIFESTO" || return 1
  grep -Eqx "ultima_migracao=V[0-9]{4}" "$T/aberto/MANIFESTO" || return 1
  grep -Eqx "pg_versao=17\..*" "$T/aberto/MANIFESTO" || return 1
  grep -Eqx "criado_em=.+" "$T/aberto/MANIFESTO" || return 1
  [ "$(grep -c '^sha256 ' "$T/aberto/MANIFESTO")" -eq 5 ] || { echo "  esperava 5 linhas sha256"; return 1; }
  while read -r linha hash arquivo; do
    [ "$linha" = sha256 ] || continue
    [ "$(sha256sum "$T/aberto/$arquivo" | cut -d' ' -f1)" = "$hash" ] || { echo "  sha256 $arquivo"; return 1; }
  done <"$T/aberto/MANIFESTO"
  # Terça-feira: nada em semanal/; enviar.sh chamado com pacote, tipo e cadeia.
  [ -z "$(ls -A "$dir/semanal" 2>/dev/null)" ] || return 1
  [ "$(cat "$MARCAS/enviar.args")" = "$dir/diario/gestor-2026-11-10.tar.age"$'\n'diario$'\n'"$dir/cadeia/cadeia-2026-11-10.txt" ] \
    || { echo "  args do enviar [$(tr '\n' ' ' <"$MARCAS/enviar.args")]"; return 1; }
  [ -f "$MARCAS/rotacionar" ]
}

nada_em_claro_sobra() {
  local dir="$T/b-claro"
  rodar_backup "$dir" --data 2026-11-10 || { mostrar_stderr; return 1; }
  [ -z "$(ls -A "$TMP_RUN")" ] || { echo "  GF_TMP não ficou vazio: $(ls -A "$TMP_RUN")"; return 1; }
  [ -z "$(find "$dir" \( -name '*.dump' -o -name '*.tar' -o -name '*.parcial' -o -name 'MANIFESTO' \) -print)" ] \
    || { echo "  sobrou em claro: $(find "$dir" -type f)"; return 1; }
  # Permissões: diretório e pacote só do dono.
  [ "$(stat -c %a "$dir")" = 700 ] || return 1
  [ "$(stat -c %a "$dir/diario/gestor-2026-11-10.tar.age")" = 600 ] || return 1
  # O pacote é age de fato (não um tar em claro).
  head -c 64 "$dir/diario/gestor-2026-11-10.tar.age" | grep -aq "age-encryption.org/v1"
}

recusa_chave_privada() {
  local dir="$T/b-privada"
  cp "$CHAVE" "$DESTINATARIOS"   # o arquivo de identidade tem "# public key: age1..." e AGE-SECRET-KEY-1...
  if rodar_backup "$dir" --data 2026-11-10; then return 1; fi
  grep -q "^FALHA destinatarios" "$T/stderr" || { mostrar_stderr; return 1; }
  [ ! -e "$dir" ] || { echo "  criou $dir"; return 1; }
  # Também em minúsculas.
  { age-keygen -y "$CHAVE"; tr '[:upper:]' '[:lower:]' <"$CHAVE" | grep -i secret; } >"$DESTINATARIOS"
  if rodar_backup "$dir" --data 2026-11-10; then return 1; fi
  [ ! -e "$dir" ] && [ ! -e "$MARCAS/enviar.args" ]
}

recusa_destinatarios_sem_age1() {
  local dir="$T/b-sem-age1"
  printf '# vazio\n' >"$DESTINATARIOS"
  if rodar_backup "$dir" --data 2026-11-10; then return 1; fi
  grep -q "^FALHA destinatarios" "$T/stderr" && [ ! -e "$dir" ]
}

recusa_sem_dek() {
  local dir="$T/b-sem-dek"
  mv "$RAIZ_FALSA/infra/data/chaves/dek.servico" "$T/dek.guardada"
  local r=0
  if rodar_backup "$dir" --data 2026-11-10; then r=1; fi
  mv "$T/dek.guardada" "$RAIZ_FALSA/infra/data/chaves/dek.servico"
  [ "$r" -eq 0 ] || return 1
  grep -q "^FALHA .*dek.servico" "$T/stderr" && [ -z "$(find "$dir" -type f 2>/dev/null)" ]
}

domingo_vai_para_semanal() {
  local dir="$T/b-domingo"
  rodar_backup "$dir" --data 2026-11-08 || { mostrar_stderr; return 1; }
  [ "$(stat -c %i "$dir/semanal/gestor-2026-11-08.tar.age")" = "$(stat -c %i "$dir/diario/gestor-2026-11-08.tar.age")" ] \
    || return 1
  [ "$(sed -n 2p "$MARCAS/enviar.args")" = semanal ] || return 1
  # Rodar de novo no domingo: o link passa a apontar para o pacote novo.
  rodar_backup "$dir" --data 2026-11-08 || { mostrar_stderr; return 1; }
  [ "$(stat -c %i "$dir/semanal/gestor-2026-11-08.tar.age")" = "$(stat -c %i "$dir/diario/gestor-2026-11-08.tar.age")" ] \
    && [ "$(find "$dir/semanal" -type f | wc -l)" -eq 1 ]
}

mesmo_dia_substitui() {
  local dir="$T/b-mesmo-dia" inode_antes
  rodar_backup "$dir" --data 2026-11-10 || { mostrar_stderr; return 1; }
  inode_antes="$(stat -c %i "$dir/diario/gestor-2026-11-10.tar.age")"
  rodar_backup "$dir" --data 2026-11-10 || { mostrar_stderr; return 1; }
  [ "$(find "$dir/diario" -type f | wc -l)" -eq 1 ] || { echo "  diario: $(ls -A "$dir/diario")"; return 1; }
  [ "$(find "$dir/cadeia" -type f | wc -l)" -eq 1 ] || return 1
  [ "$(stat -c %i "$dir/diario/gestor-2026-11-10.tar.age")" != "$inode_antes" ] || return 1
  abrir_pacote "$dir/diario/gestor-2026-11-10.tar.age" "$T/aberto"
}

cadeia_dentro_e_fora() {
  local dir="$T/b-cadeia" fora
  rodar_backup "$dir" --data 2026-11-10 || { mostrar_stderr; return 1; }
  fora="$dir/cadeia/cadeia-2026-11-10.txt"
  abrir_pacote "$dir/diario/gestor-2026-11-10.tar.age" "$T/aberto"
  cmp -s "$fora" "$T/aberto/cadeia.txt" || return 1
  [ "$(wc -l <"$fora")" -eq 2 ] || return 1
  [ "$(cut -d' ' -f1 "$fora")" = "$CASAL_1"$'\n'"$CASAL_2" ] || return 1
  ! grep -Evq '^([0-9a-f-]{36}|nulo) [0-9]+ [0-9a-f]{64}$' "$fora"
}

codigo_do_enviar_e_o_do_backup() {
  local dir="$T/b-enviar" codigo=0
  echo 3 >"$MARCAS/enviar.codigo"
  rodar_backup "$dir" --data 2026-11-10 || codigo=$?
  [ "$codigo" -eq 3 ] || { echo "  código $codigo"; return 1; }
  [ -f "$dir/diario/gestor-2026-11-10.tar.age" ]
}

le_so_chaves_do_env() {
  local dir="$T/b-env" shims="$T/shims" docker_real
  docker_real="$(command -v docker)"
  mkdir -p "$shims"
  cat >"$shims/docker" <<EOF
#!/usr/bin/env bash
env >>"$MARCAS/docker.env"
exec "$docker_real" "\$@"
EOF
  chmod 700 "$shims/docker"
  printf 'DUCKDNS_TOKEN=segredo-teste\nGF_BACKUP_DIR="%s"\nOUTRA=1\n' "$dir" >"$RAIZ_FALSA/infra/.env"
  local codigo=0
  env -u GF_BACKUP_DIR PATH="$shims:$PATH" GF_BACKUP_DESTINATARIOS="$DESTINATARIOS" \
    PG_CONTAINER="$FONTE_CONTAINER" GF_TMP="$TMP_RUN" \
    "$RAIZ_FALSA/infra/backup/backup.sh" --data 2026-11-10 2>"$T/stderr" || codigo=$?
  printf 'DUCKDNS_TOKEN=segredo-teste\n' >"$RAIZ_FALSA/infra/.env"
  [ "$codigo" -eq 0 ] || { mostrar_stderr; return 1; }
  [ -f "$dir/diario/gestor-2026-11-10.tar.age" ] || return 1
  [ -s "$MARCAS/docker.env" ] || { echo "  shim do docker não foi chamado"; return 1; }
  ! grep -q "segredo-teste" "$MARCAS/docker.env" "$MARCAS/enviar.env" || { echo "  token vazou"; return 1; }
  ! grep -q "segredo-teste" "$T/stderr"
}

ancora_ok_com_eventos_novos() {
  local dir="$T/b-ancora-ok"
  rodar_backup "$dir" --data 2026-11-09 || { mostrar_stderr; return 1; }
  psql_fonte gf_app >/dev/null <<SQL
SELECT set_config('app.ip', 'teste-backup', false);
UPDATE identidade.casais SET nome_familia = '$NOME_SINTETICO_1 D' WHERE id = '$CASAL_1';
UPDATE identidade.casais SET nome_familia = '$NOME_SINTETICO_2 D' WHERE id = '$CASAL_2';
SQL
  rodar_backup "$dir" --data 2026-11-10 || { mostrar_stderr; return 1; }
  grep -q "ancora" "$T/stderr" || { echo "  âncora não foi conferida"; return 1; }
  ! cmp -s "$dir/cadeia/cadeia-2026-11-09.txt" "$dir/cadeia/cadeia-2026-11-10.txt"
}

# Reescreve a cadeia inteira do casal 2 e recalcula os hashes com a fórmula da V0001:
# verificar_cadeia passa, só a âncora pega. Destrutivo na fonte.
fonte_reescrever_cadeia_casal_2() {
  psql_fonte postgres >/dev/null <<SQL
ALTER TABLE audit.eventos DISABLE TRIGGER trg_eventos_imutavel;
UPDATE audit.eventos SET alteracoes = alteracoes || '{"reescrito": true}'::jsonb WHERE casal_id = '$CASAL_2';
DO \$\$
DECLARE r record; anterior bytea := NULL; novo bytea;
BEGIN
  FOR r IN SELECT * FROM audit.eventos WHERE casal_id = '$CASAL_2' ORDER BY seq LOOP
    novo := sha256(coalesce(anterior, ''::bytea)
            || convert_to(concat_ws('|', r.tabela, r.entidade_id::text, r.acao, r.alteracoes::text,
                                    coalesce(r.usuario_id::text, ''), coalesce(r.ip, '')), 'UTF8'));
    UPDATE audit.eventos SET hash_anterior = anterior, hash = novo WHERE seq = r.seq;
    anterior := novo;
  END LOOP;
END
\$\$;
ALTER TABLE audit.eventos ENABLE TRIGGER trg_eventos_imutavel;
SQL
}

ancora_quebrada_falha() {
  local dir="$T/b-ancora-quebrada" seq_ancora quebras
  rodar_backup "$dir" --data 2026-11-09 || { mostrar_stderr; return 1; }
  seq_ancora="$(grep "^$CASAL_2 " "$dir/cadeia/cadeia-2026-11-09.txt" | cut -d' ' -f2)"
  fonte_reescrever_cadeia_casal_2
  # O teste só vale se a cadeia reescrita passar em verificar_cadeia.
  quebras="$(psql_fonte postgres -c "
    SELECT count(*) FROM (SELECT DISTINCT casal_id FROM audit.eventos) c
     CROSS JOIN LATERAL audit.verificar_cadeia(c.casal_id) v")"
  [ "$quebras" = 0 ] || { echo "  verificar_cadeia reprovou a reescrita ($quebras)"; return 1; }
  if rodar_backup "$dir" --data 2026-11-10; then return 1; fi
  grep -q "^FALHA ancora casal=$CASAL_2 seq=$seq_ancora$" "$T/stderr" || { mostrar_stderr; return 1; }
  ! grep -q "casal=$CASAL_1" "$T/stderr" || return 1
  [ ! -e "$dir/diario/gestor-2026-11-10.tar.age" ] && [ ! -e "$dir/cadeia/cadeia-2026-11-10.txt" ] \
    && [ "$(find "$dir/diario" -type f | wc -l)" -eq 1 ] && [ -z "$(ls -A "$TMP_RUN")" ]
}

# Destrutivo na fonte: roda por último.
dump_reprovado_nao_gera_pacote() {
  local dir="$T/b-reprovado" antes depois
  mkdir -p "$dir/diario"
  echo sentinela >"$dir/diario/sentinela"
  echo pacote-antigo >"$dir/diario/gestor-2026-11-01.tar.age"
  antes="$(find "$dir/diario" -printf '%P %i %s %T@\n' | LC_ALL=C sort)"
  fonte_adulterar_evento >/dev/null
  if rodar_backup "$dir" --data 2026-11-10; then return 1; fi
  grep -q "^FALHA cadeia casal=$CASAL_1" "$T/stderr" || { mostrar_stderr; return 1; }
  depois="$(find "$dir/diario" -printf '%P %i %s %T@\n' | LC_ALL=C sort)"
  [ "$antes" = "$depois" ] || { echo "  diario/ mudou: [$antes] → [$depois]"; return 1; }
  [ ! -e "$MARCAS/rotacionar" ] && [ ! -e "$MARCAS/enviar.args" ] && [ -z "$(ls -A "$TMP_RUN")" ]
}

# ---------------------------------------------------------------- execução

[ -x "$BACKUP_REAL" ] || { echo "FALHA backup.sh não existe ou não é executável"; exit 1; }
{ command -v age >/dev/null && command -v age-keygen >/dev/null; } || { echo "FALHA age não instalado"; exit 1; }

caso ler_env_le_so_a_chave

montar_raiz_falsa
fonte_subir

caso gera_pacote_decifravel
caso nada_em_claro_sobra
caso recusa_chave_privada
caso recusa_destinatarios_sem_age1
caso recusa_sem_dek
caso domingo_vai_para_semanal
caso mesmo_dia_substitui
caso cadeia_dentro_e_fora
caso codigo_do_enviar_e_o_do_backup
caso le_so_chaves_do_env
caso ancora_ok_com_eventos_novos
caso ancora_quebrada_falha
caso dump_reprovado_nao_gera_pacote

if [ "$sobras" -eq 0 ]; then ok descartavel_some; else falha descartavel_some; fi

[ "$falhas" -eq 0 ] || { echo "$falhas teste(s) falharam"; exit 1; }
