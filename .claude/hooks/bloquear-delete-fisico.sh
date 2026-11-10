#!/usr/bin/env bash
# PreToolUse (Edit|Write|MultiEdit): impede que código com DELETE físico seja escrito.
# Recebe o JSON da ferramenta no stdin. Exit 2 = bloqueia e devolve a mensagem ao Claude.
# As regras moram em scripts/guardas/regras.sh (as mesmas do pre-commit e do CI).
set -euo pipefail

raiz="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "$0")/../.." && pwd)}"
# shellcheck source=scripts/guardas/regras.sh
source "$raiz/scripts/guardas/regras.sh"

entrada="$(cat)"

caminho="$(printf '%s' "$entrada" | grep -oE '"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' | head -n1 | sed -E 's/.*"([^"]*)"$/\1/')"
relativo="${caminho#"$raiz"/}"

# Segredos nunca são editados pelo Claude.
if eh_caminho_de_segredo "$relativo"; then
  echo "Bloqueado: $caminho guarda segredos e não pode ser editado pelo Claude." >&2
  exit 2
fi

# Só olha código e SQL; testes podem citar os padrões proibidos para verificá-los.
eh_codigo_vigiado "$relativo" || exit 0

if ! printf '%s' "$entrada" | checar_delete_fisico "$relativo" 2>/dev/null; then
  echo "Bloqueado: DELETE físico é proibido neste projeto (regra 1 do CLAUDE.md)." >&2
  echo "Use soft delete: marque IsDeleted = true (ou chame Remove(), que o SoftDeleteInterceptor converte)." >&2
  exit 2
fi

exit 0
