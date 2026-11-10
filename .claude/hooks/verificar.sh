#!/usr/bin/env bash
# Stop: antes de o Claude encerrar, roda os testes da parte que mudou.
# Exit 2 = impede o encerramento e devolve a saída para o Claude corrigir.
# A lógica mora em scripts/verificar.sh (a mesma que o Antigravity e o VS Code usam).
set -uo pipefail

cd "${CLAUDE_PROJECT_DIR:-.}" || exit 0
[ -z "$(git status --porcelain 2>/dev/null)" ] && exit 0

if ! scripts/verificar.sh >/dev/null; then
  exit 2
fi
exit 0
