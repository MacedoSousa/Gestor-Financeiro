#!/usr/bin/env bash
# Testes do infra/scripts/gerar-segredos.sh (sem docker), numa raiz temporária com uma cópia do
# script. Nunca toca em infra/secrets do repositório.
#
# Os segredos são 0444 (os containers leem como postgres, uid 70, e app, uid 1654; o compose
# não aplica "mode" em segredo de arquivo) e quem protege no host é a pasta infra/secrets em
# 0700. Exceção: gf_chave_servico fica 0400 (a api recusa chave legível por grupo/outros; ela
# recebe chown 1654). Pasta com outro modo: o script falha em vez de gravar.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
umask 022
T="$(mktemp -d)"
trap 'chmod -R u+w "$T" 2>/dev/null; rm -rf "$T"' EXIT
falhas=0

ok()    { echo "ok $1"; }
falha() { echo "FALHA $1"; falhas=$((falhas + 1)); }
caso()  { if "$1"; then ok "$1"; else falha "$1"; fi; }

# Raiz nova com a cópia do script. Uso: r=$(nova_raiz <nome>)
nova_raiz() {
  mkdir -p "$T/$1/infra/scripts"
  cp "$REPO/infra/scripts/gerar-segredos.sh" "$T/$1/infra/scripts/"
  echo "$T/$1"
}

pasta_0700_e_segredos_0444_menos_a_chave() {
  local r
  r="$(nova_raiz nova)"
  "$r/infra/scripts/gerar-segredos.sh" >"$T/saida" 2>&1 || { sed 's/^/  | /' "$T/saida"; return 1; }
  [ "$(stat -c %a "$r/infra/secrets")" = 700 ] || { echo "  pasta $(stat -c %a "$r/infra/secrets")"; return 1; }
  [ "$(find "$r/infra/secrets" -type f | wc -l)" = 6 ] || { echo "  quantidade de segredos"; return 1; }
  [ "$(find "$r/infra/secrets" -type f ! -name gf_chave_servico -printf '%m\n' | sort -u)" = 444 ] \
    || { echo "  modos [$(find "$r/infra/secrets" -type f -printf '%f=%m ')]"; return 1; }
  [ "$(stat -c %a "$r/infra/secrets/gf_chave_servico")" = 400 ] || { echo "  modo da gf_chave_servico"; return 1; }
}

# Segunda execução mantém o que existe (mesmo conteúdo e inode).
segunda_execucao_mantem() {
  local r antes
  r="$(nova_raiz duas)"
  "$r/infra/scripts/gerar-segredos.sh" >/dev/null 2>&1 || return 1
  antes="$(cd "$r/infra/secrets" && for f in *; do echo "$f $(stat -c %i "$f") $(sha256sum <"$f")"; done)"
  "$r/infra/scripts/gerar-segredos.sh" >"$T/saida" 2>&1 || { sed 's/^/  | /' "$T/saida"; return 1; }
  [ "$(cd "$r/infra/secrets" && for f in *; do echo "$f $(stat -c %i "$f") $(sha256sum <"$f")"; done)" = "$antes" ]
}

pasta_com_outro_modo_falha() {
  local r modo
  for modo in 755 750 711; do
    r="$(nova_raiz "modo-$modo")"
    mkdir -m "$modo" "$r/infra/secrets"
    if "$r/infra/scripts/gerar-segredos.sh" >"$T/saida" 2>&1; then echo "  aceitou pasta $modo"; return 1; fi
    grep -qx "FALHA infra/secrets precisa estar em 0700" "$T/saida" || { sed 's/^/  | /' "$T/saida"; return 1; }
    [ -z "$(ls -A "$r/infra/secrets")" ] || { echo "  gravou com a pasta $modo"; return 1; }
    [ "$(stat -c %a "$r/infra/secrets")" = "$modo" ] || { echo "  mudou o modo da pasta $modo"; return 1; }
  done
}

caso pasta_0700_e_segredos_0444_menos_a_chave
caso segunda_execucao_mantem
caso pasta_com_outro_modo_falha

[ "$falhas" -eq 0 ] || { echo "$falhas teste(s) falharam"; exit 1; }
