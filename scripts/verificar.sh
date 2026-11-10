#!/usr/bin/env bash
# Roda os testes da parte que mudou (mesma lógica do hook Stop do Claude Code).
# Qualquer agente usa antes de dizer que terminou: Claude Code (hook Stop), Antigravity (workflow /verificar)
# e a tarefa "Verificar" do VS Code.
# Uso: scripts/verificar.sh [--tudo]     Saída: 0 = verde, 1 = algo falhou (relatório em stderr).
set -uo pipefail

cd "$(git rev-parse --show-toplevel)" || exit 1

if [ "${1:-}" = "--tudo" ]; then
  mudou=$'backend/\nfrontend/\n'
else
  mudou="$( { git diff --name-only HEAD 2>/dev/null; git ls-files --others --exclude-standard; } | sort -u)"
fi
[ -z "$mudou" ] && { echo "Nada mudou; nada a verificar."; exit 0; }

falhou=0
relatorio=""

rodar() {
  local saida
  echo "\$ $*"
  if ! saida="$("$@" 2>&1)"; then
    falhou=1
    relatorio+="\$ $*"$'\n'"$(printf '%s\n' "$saida" | tail -n 40)"$'\n\n'
  fi
}

rodar scripts/guardas/verificar-repositorio.sh

if printf '%s\n' "$mudou" | grep -qE '^(backend|db)/'; then
  if command -v dotnet >/dev/null 2>&1; then
    rodar dotnet test backend/GestorFinanceiro.slnx --nologo -v q
  else
    echo "AVISO: dotnet não encontrado; backend NÃO testado (instale o .NET SDK 10)." >&2
  fi
fi

if printf '%s\n' "$mudou" | grep -qE '^(frontend|contracts)/'; then
  if [ -d frontend/node_modules ]; then
    rodar npm --prefix frontend run check --silent
  else
    echo "AVISO: frontend/node_modules ausente; front NÃO testado (rode npm --prefix frontend ci)." >&2
  fi
fi

if printf '%s\n' "$mudou" | grep -qE '^(scripts|\.githooks)/'; then
  rodar scripts/guardas/testes.sh
fi

if [ "$falhou" -ne 0 ]; then
  echo "Testes falhando — corrija antes de concluir:" >&2
  printf '%s' "$relatorio" >&2
  exit 1
fi
echo "Verificação: tudo verde."
