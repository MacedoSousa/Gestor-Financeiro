#!/usr/bin/env bash
# Regras do CLAUDE.md que dá para conferir por texto. Fonte única para:
#   - .claude/hooks/bloquear-delete-fisico.sh  (Claude Code, antes de editar)
#   - .githooks/pre-commit                      (qualquer agente ou pessoa, antes do commit)
#   - scripts/guardas/verificar-repositorio.sh  (CI, no repositório inteiro)
# Só funções; quem usa faz `source`. Cada checagem escreve o motivo em stderr e devolve 1 se falhar.

# Regra 1: nada de DELETE físico.
GUARDA_DELETE_FISICO='DELETE[[:space:]]+FROM|ExecuteDelete|TRUNCATE[[:space:]]+TABLE'

# Arquivos que guardam segredo: nenhum agente edita, ninguém commita.
eh_caminho_de_segredo() {
  case "$1" in
    *.env.example) return 1 ;;
    infra/secrets/README.md|*/infra/secrets/README.md) return 1 ;;
    infra/secrets/*|*/infra/secrets/*|.env|*/.env|.env.*|*/.env.*|backups/*|*/backups/*|infra/data/*|*/infra/data/*) return 0 ;;
    *.key|*.pem|*.dump|*chave-servico*) return 0 ;;
  esac
  return 1
}

# Código e SQL de produção (testes podem citar os padrões proibidos para verificá-los).
eh_codigo_vigiado() {
  case "$1" in
    */tests/*|tests/*|scripts/guardas/*) return 1 ;;
    *.cs|*.sql|*.ts|*.tsx) return 0 ;;
  esac
  return 1
}

# Lê texto no stdin; falha se tiver DELETE físico.
checar_delete_fisico() {
  local origem="$1"
  if grep -qiE "$GUARDA_DELETE_FISICO"; then
    echo "Bloqueado: DELETE físico em $origem (regra 1 do CLAUDE.md). Use soft delete: IsDeleted = true ou Remove()." >&2
    return 1
  fi
}

# Nome de migração: V0001__descricao_em_snake.sql
checar_nome_de_migracao() {
  local nome; nome="$(basename "$1")"
  if [[ ! "$nome" =~ ^V[0-9]{4}__[a-z0-9_]+\.sql$ ]]; then
    echo "Migração com nome fora do padrão: $nome (esperado V0001__descricao.sql)." >&2
    return 1
  fi
}

# Numeração sem buraco nem repetição, a partir de V0001. Recebe os caminhos como argumentos.
checar_sequencia_de_migracoes() {
  local esperado=1 numero nome falhou=0
  while read -r nome; do
    [ -z "$nome" ] && continue
    numero=$((10#${nome:1:4}))
    if [ "$numero" -ne "$esperado" ]; then
      printf 'Sequência de migrações quebrada: %s (esperado V%04d).\n' "$nome" "$esperado" >&2
      falhou=1
      esperado=$numero
    fi
    esperado=$((esperado + 1))
  done < <(for f in "$@"; do basename "$f"; done | sort)
  return "$falhou"
}

# SQL sem comentários de linha, um comando por linha (separados por ';'). Lê do stdin.
comandos_sql() {
  sed -E 's/--.*$//' | tr '\n' ' ' | tr ';' '\n' | sed -E 's/[[:space:]]+/ /g; s/^ //; s/ $//' | grep -v '^$' || true
}

# Regras 2 e 3 numa migração: toda tabela criada chama audit.proteger no mesmo arquivo;
# todo comando com UNIQUE é índice parcial (WHERE NOT is_deleted). Analisa o comando inteiro, não a linha.
checar_conteudo_de_migracao() {
  local arquivo="$1" falhou=0 comandos tabela
  comandos="$(comandos_sql < "$arquivo")"

  while read -r tabela; do
    [ -z "$tabela" ] && continue
    if [[ "$tabela" != *.* ]]; then
      echo "$(basename "$arquivo"): CREATE TABLE $tabela sem schema; toda tabela tem schema (regras 2 e 7)." >&2
      falhou=1
    fi
  done < <(printf '%s\n' "$comandos" | grep -ioE 'CREATE TABLE (IF NOT EXISTS )?[^ (]+' | awk '{print $NF}')

  while read -r tabela; do
    [ -z "$tabela" ] && continue
    [ "$tabela" = "audit.eventos" ] && continue
    if ! grep -qF "audit.proteger('$tabela')" "$arquivo"; then
      echo "$(basename "$arquivo"): tabela $tabela sem SELECT audit.proteger('$tabela') (regra 2)." >&2
      falhou=1
    fi
  done < <(printf '%s\n' "$comandos" | grep -ioE 'CREATE TABLE (IF NOT EXISTS )?[a-z_]+\.[a-z_]+' | awk '{print tolower($NF)}')

  while read -r comando; do
    [ -z "$comando" ] && continue
    if ! printf '%s' "$comando" | grep -qF 'WHERE NOT is_deleted'; then
      echo "$(basename "$arquivo"): UNIQUE sem 'WHERE NOT is_deleted' (regra 3): ${comando:0:120}" >&2
      falhou=1
    fi
  done < <(printf '%s\n' "$comandos" | grep -iE '\bUNIQUE\b')

  if grep -qiE "$GUARDA_DELETE_FISICO" < <(comandos_sql < "$arquivo"); then
    echo "$(basename "$arquivo"): DELETE físico na migração (regra 1)." >&2
    falhou=1
  fi
  return "$falhou"
}
