#!/usr/bin/env bash
# Roda os testes da parte que mudou (mesma lógica do hook Stop do Claude Code).
# Qualquer agente usa antes de dizer que terminou: Claude Code (hook Stop), Antigravity (workflow /verificar)
# e a tarefa "Verificar" do VS Code.
# Uso: scripts/verificar.sh [--tudo]     Saída: 0 = verde, 1 = algo falhou (relatório em stderr).
set -uo pipefail

cd "$(git rev-parse --show-toplevel)" || exit 1

if [ "${1:-}" = "--tudo" ]; then
  mudou=$'backend/\nfrontend/\nscripts/\n'
else
  mudou="$( { git diff --name-only HEAD 2>/dev/null; git ls-files --others --exclude-standard; } | sort -u)"
fi
[ -z "$mudou" ] && { echo "Nada mudou; nada a verificar."; exit 0; }

falhou=0
avisos=0
relatorio=""

avisar() { echo "AVISO: $*" >&2; avisos=1; }

rodar() {
  local saida
  echo "\$ $*"
  if ! saida="$("$@" 2>&1)"; then
    falhou=1
    relatorio+="\$ $*"$'\n'"$(printf '%s\n' "$saida" | tail -n 40)"$'\n\n'
  fi
}

rodar scripts/guardas/verificar-repositorio.sh

# shellcheck source=scripts/guardas/regras.sh
source scripts/guardas/regras.sh
if docker_e_do_snap; then
  avisar "Docker do snap não é suportado (ADR-013); troque pelo do apt."
fi

if printf '%s\n' "$mudou" | grep -qE '^(backend|db)/'; then
  if command -v dotnet >/dev/null 2>&1; then
    if docker info >/dev/null 2>&1; then
      rodar dotnet test backend/GestorFinanceiro.slnx --nologo -v q
    else
      rodar dotnet test backend/tests/GestorFinanceiro.UnitTests --nologo -v q
      rodar dotnet test backend/tests/GestorFinanceiro.ArchitectureTests --nologo -v q
      avisar "Docker indisponível; testes de integração NÃO testados."
    fi
  else
    avisar "dotnet não encontrado; backend NÃO testado (instale o .NET SDK 10)."
  fi
fi

if printf '%s\n' "$mudou" | grep -qE '^(frontend|contracts)/'; then
  if [ -d frontend/node_modules ]; then
    rodar npm --prefix frontend run check --silent
  else
    avisar "frontend/node_modules ausente; front NÃO testado (rode npm --prefix frontend ci)."
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
if [ "$avisos" -ne 0 ]; then
  echo "Verificação: verde COM AVISOS (não vale para PR)."
else
  echo "Verificação: tudo verde."
fi
