#!/usr/bin/env bash
# PreToolUse (Bash): impede comando que cite caminho de segredo (keystore, .env, ~/.android...)
# e ./gradlew com --scan, -I/--init-script, properties ou signingReport.
# Recebe o JSON da ferramenta no stdin. Exit 2 = bloqueia e devolve a mensagem ao Claude.
# As regras moram em scripts/guardas/regras.sh (as mesmas do pre-commit e do CI).
set -uo pipefail

raiz="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "$0")/../.." && pwd)}"
# shellcheck source=scripts/guardas/regras.sh
source "$raiz/scripts/guardas/regras.sh"

if ! comando="$(jq -r '.tool_input.command // empty' 2>/dev/null)"; then
  echo "Bloqueado: guardar-bash.sh não conseguiu ler a entrada (jq instalado?)." >&2
  exit 2
fi
[ -z "$comando" ] && exit 0

checar_comando_bash "$comando" || exit 2
exit 0
