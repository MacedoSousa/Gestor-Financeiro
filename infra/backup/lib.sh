#!/usr/bin/env bash
# Funções comuns dos scripts de backup. Uso: source "$(dirname "$0")/lib.sh"
#
# Regra de log (Decisão 9): nada de dado pessoal. Registra-se etapa, nome de arquivo,
# tamanho, contagem e seq. A saída de erro do pg_restore/psql pode citar conteúdo de
# linha (CONTEXT) e por isso nunca é repassada: só o código de saída.

GF_IMAGEM_POSTGRES="postgres:17-alpine"
GF_DESCARTAVEL=""

log() {
  printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2
}

# Registra "FALHA <motivo>" no stderr e encerra com erro.
falhar() {
  echo "FALHA $*" >&2
  exit 1
}

aleatorio() {
  od -An -N8 -tx1 /dev/urandom | tr -d ' \n'
}

# Espera o Postgres definitivo do container aceitar conexão. Testa por TCP em 127.0.0.1:
# o servidor temporário do docker-entrypoint (que roda os scripts de init) só ouve no socket.
esperar_postgres() {
  local container="$1" tentativa
  for tentativa in $(seq 1 120); do
    if docker exec "$container" pg_isready -q -h 127.0.0.1 -U postgres >/dev/null 2>&1; then
      return 0
    fi
    [ "$tentativa" -lt 120 ] && sleep 0.5
  done
  log "postgres do container $container não ficou pronto"
  return 1
}

# Sobe o banco descartável e põe o nome em GF_DESCARTAVEL.
# O nome é definido antes do docker run, para o trap do chamador conseguir derrubá-lo
# mesmo se a interrupção vier no meio. Sem rede e com os dados em tmpfs.
# Cria gf_migrator e gf_app NOLOGIN: os donos e GRANTs do dump citam esses papéis.
# Marca o container com o rótulo gf.execucao=<GF_EXECUCAO> (padrão "avulsa"). Os testes usam
# um id próprio por execução e só conferem/removem os containers com esse rótulo, para
# nunca derrubar o descartável de um backup de produção rodando ao mesmo tempo.
subir_descartavel() {
  GF_DESCARTAVEL="gf-verificacao-$(aleatorio)"
  docker run -d --rm --network none --tmpfs /var/lib/postgresql/data \
    --name "$GF_DESCARTAVEL" --label "gf.execucao=${GF_EXECUCAO:-avulsa}" \
    -e POSTGRES_PASSWORD="$(aleatorio)$(aleatorio)" -e POSTGRES_DB=gestor \
    "$GF_IMAGEM_POSTGRES" >/dev/null 2>&1 || { log "descartável: docker run falhou"; return 1; }
  esperar_postgres "$GF_DESCARTAVEL" || return 1
  psql_descartavel "$GF_DESCARTAVEL" gestor \
    -c "CREATE ROLE gf_migrator NOLOGIN; CREATE ROLE gf_app NOLOGIN;" >/dev/null \
    || { log "descartável: papéis não criados"; return 1; }
}

# Ignora INT/TERM só no subshell: um segundo ctrl-c (que vai para o grupo inteiro de
# processos) não pode abortar o docker rm no meio da limpeza.
derrubar_descartavel() {
  local container="${1:-}"
  [ -n "$container" ] || return 0
  ( trap '' INT TERM; docker rm -f "$container" >/dev/null 2>&1 ) || true
}

# psql como postgres no container, pelo socket local (o container não tem rede).
# Uso: psql_descartavel <container> <banco> [args do psql...]. O stderr é descartado.
psql_descartavel() {
  local container="$1" banco="$2"; shift 2
  docker exec -i "$container" psql -X -q -t -A -v ON_ERROR_STOP=1 -U postgres -d "$banco" "$@" 2>/dev/null
}

# pg_restore como postgres, lendo o dump pelo stdin. O stderr é descartado.
_pg_restore() {
  local container="$1" banco="$2" arquivo="$3"; shift 3
  docker exec -i "$container" pg_restore -U postgres -d "$banco" --exit-on-error "$@" \
    <"$arquivo" >/dev/null 2>&1
}

# Uso: restaurar_dump <container> <banco> <arquivo.dump> [--duas-fases] [--sem-disable-triggers]
# Padrão: restore completo com --disable-triggers --exit-on-error (Decisão 8).
# --duas-fases e --sem-disable-triggers existem só para o teste que prova a Decisão 8.
restaurar_dump() {
  local container="$1" banco="$2" arquivo="$3"; shift 3
  local duas_fases=0 triggers=(--disable-triggers) codigo=0
  while [ "$#" -gt 0 ]; do
    case "$1" in
      --duas-fases) duas_fases=1 ;;
      --sem-disable-triggers) triggers=() ;;
      *) log "restaurar_dump: opção desconhecida $1"; return 2 ;;
    esac
    shift
  done

  if [ "$duas_fases" -eq 0 ]; then
    _pg_restore "$container" "$banco" "$arquivo" "${triggers[@]}" || codigo=$?
    [ "$codigo" -eq 0 ] || log "pg_restore etapa=completo codigo=$codigo"
    return "$codigo"
  fi

  _pg_restore "$container" "$banco" "$arquivo" --section=pre-data --section=post-data || codigo=$?
  if [ "$codigo" -ne 0 ]; then
    log "pg_restore etapa=esquema codigo=$codigo"
    return "$codigo"
  fi
  _pg_restore "$container" "$banco" "$arquivo" --data-only "${triggers[@]}" || codigo=$?
  [ "$codigo" -eq 0 ] || log "pg_restore etapa=dados codigo=$codigo"
  return "$codigo"
}

# Formato cadeia.txt (Decisão 1): o último evento de cada casal,
# "<casal_id|nulo> <seq> <hash em hex minúsculo>", ordenado por casal_id, nulo no fim.
gerar_cadeia() {
  local container="$1" banco="$2"
  psql_descartavel "$container" "$banco" -F ' ' -c "
    SELECT coalesce(casal_id::text, 'nulo'), seq, encode(hash, 'hex')
      FROM (SELECT DISTINCT ON (casal_id) casal_id, seq, hash
              FROM audit.eventos
             ORDER BY casal_id NULLS LAST, seq DESC) ultimos
     ORDER BY casal_id NULLS LAST"
}

# Uso: ler_env <CHAVE> [padrao]. Lê só a linha "^CHAVE=" de $GF_RAIZ/infra/.env, com grep:
# nunca faz source/eval do .env (ele tem o DUCKDNS_TOKEN, que não pode chegar ao ambiente
# de nenhum subprocesso). A última ocorrência vale; aspas simples ou duplas em volta do valor
# são removidas. Valor vazio ou chave ausente → padrao.
# Uma variável de ambiente de mesmo nome, se definida (mesmo vazia), tem precedência (testes).
ler_env() {
  local chave="$1" padrao="${2:-}" arquivo linha valor
  [[ "$chave" =~ ^[A-Z_][A-Z0-9_]*$ ]] || { log "ler_env: chave inválida"; return 2; }
  if [ -n "${!chave+x}" ]; then
    printf '%s\n' "${!chave}"
    return 0
  fi
  arquivo="${GF_RAIZ:?GF_RAIZ não definido}/infra/.env"
  linha=""
  if [ -r "$arquivo" ]; then
    linha="$(grep -E "^${chave}=" "$arquivo" | tail -n 1 || true)"
  fi
  valor="${linha#*=}"
  valor="${valor%$'\r'}"
  if [[ "$valor" =~ ^\"(.*)\"$ ]] || [[ "$valor" =~ ^\'(.*)\'$ ]]; then
    valor="${BASH_REMATCH[1]}"
  fi
  [ -n "$valor" ] || valor="$padrao"
  printf '%s\n' "$valor"
}
