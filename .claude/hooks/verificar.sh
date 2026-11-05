#!/usr/bin/env bash
# Stop: antes de o Claude encerrar, roda os testes da parte que mudou.
# Exit 2 = impede o encerramento e devolve a saída para o Claude corrigir.
set -uo pipefail

cd "${CLAUDE_PROJECT_DIR:-.}"

mudou="$(git status --porcelain 2>/dev/null | awk '{print $2}')"
[ -z "$mudou" ] && exit 0

falhou=0
relatorio=""

rodar() {
  local saida
  if ! saida="$("$@" 2>&1)"; then
    falhou=1
    relatorio+="\$ $*"$'\n'"$(printf '%s\n' "$saida" | tail -n 40)"$'\n\n'
  fi
}

if printf '%s\n' "$mudou" | grep -qE '^(backend|db)/' && command -v dotnet >/dev/null 2>&1; then
  rodar dotnet test backend/GestorFinanceiro.slnx --nologo -v q
fi

if printf '%s\n' "$mudou" | grep -qE '^(frontend|contracts)/' && [ -d frontend/node_modules ]; then
  rodar npm --prefix frontend run check --silent
fi

if [ "$falhou" -ne 0 ]; then
  echo "Testes falhando — corrija antes de concluir:" >&2
  printf '%s' "$relatorio" >&2
  exit 2
fi

exit 0
