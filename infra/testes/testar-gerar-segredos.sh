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

# Clone novo: o git cria infra/secrets (por causa do README.md) com o modo do umask. Só com o
# README, não há segredo a proteger: o script fecha a pasta em 0700, avisa e gera.
clone_com_so_o_readme_fecha_a_pasta() {
  local r modo
  for modo in 775 755; do
    r="$(nova_raiz "clone-$modo")"
    mkdir -m "$modo" "$r/infra/secrets"
    cp "$REPO/infra/secrets/README.md" "$r/infra/secrets/README.md"
    "$r/infra/scripts/gerar-segredos.sh" >"$T/saida" 2>&1 || { sed 's/^/  | /' "$T/saida"; return 1; }
    grep -qx "infra/secrets fechada em 0700 (só tinha o README)" "$T/saida" \
      || { echo "  sem o aviso (pasta $modo)"; sed 's/^/  | /' "$T/saida"; return 1; }
    [ "$(stat -c %a "$r/infra/secrets")" = 700 ] || { echo "  pasta $(stat -c %a "$r/infra/secrets")"; return 1; }
    [ "$(find "$r/infra/secrets" -type f ! -name README.md | wc -l)" = 6 ] || { echo "  quantidade de segredos"; return 1; }
    cmp -s "$REPO/infra/secrets/README.md" "$r/infra/secrets/README.md" || { echo "  mexeu no README"; return 1; }
  done
}

# Decisão do #24: pasta aberta com qualquer outra coisa além do README nunca é corrigida em
# silêncio (pode já ter segredo exposto); continua falhando sem gravar nem mudar o modo.
pasta_aberta_com_mais_que_o_readme_falha() {
  local r variante antes
  for variante in readme_e_segredo readme_e_outro so_outro readme_link readme_e_subpasta; do
    r="$(nova_raiz "aberta-$variante")"
    mkdir -m 775 "$r/infra/secrets"
    case "$variante" in
      readme_e_segredo)  cp "$REPO/infra/secrets/README.md" "$r/infra/secrets/"; echo s >"$r/infra/secrets/pg_superusuario" ;;
      readme_e_outro)    cp "$REPO/infra/secrets/README.md" "$r/infra/secrets/"; echo x >"$r/infra/secrets/.x" ;;
      so_outro)          echo x >"$r/infra/secrets/notas.txt" ;;
      readme_link)       ln -s "$REPO/infra/secrets/README.md" "$r/infra/secrets/README.md" ;;
      readme_e_subpasta) cp "$REPO/infra/secrets/README.md" "$r/infra/secrets/"; mkdir "$r/infra/secrets/sub" ;;
    esac
    antes="$(cd "$r/infra/secrets" && find . -printf '%P %y %s\n' | sort)"
    if "$r/infra/scripts/gerar-segredos.sh" >"$T/saida" 2>&1; then echo "  aceitou ($variante)"; return 1; fi
    grep -qx "FALHA infra/secrets precisa estar em 0700" "$T/saida" || { sed 's/^/  | /' "$T/saida"; return 1; }
    [ "$(cd "$r/infra/secrets" && find . -printf '%P %y %s\n' | sort)" = "$antes" ] || { echo "  gravou ($variante)"; return 1; }
    [ "$(stat -c %a "$r/infra/secrets")" = 775 ] || { echo "  mudou o modo ($variante)"; return 1; }
  done
}

caso pasta_0700_e_segredos_0444_menos_a_chave
caso segunda_execucao_mantem
caso pasta_com_outro_modo_falha
caso clone_com_so_o_readme_fecha_a_pasta
caso pasta_aberta_com_mais_que_o_readme_falha

[ "$falhas" -eq 0 ] || { echo "$falhas teste(s) falharam"; exit 1; }
