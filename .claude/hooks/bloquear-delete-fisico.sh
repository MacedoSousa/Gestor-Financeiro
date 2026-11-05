#!/usr/bin/env bash
# PreToolUse (Edit|Write|MultiEdit): impede que código com DELETE físico seja escrito.
# Recebe o JSON da ferramenta no stdin. Exit 2 = bloqueia e devolve a mensagem ao Claude.
set -euo pipefail

entrada="$(cat)"

caminho="$(printf '%s' "$entrada" | grep -oE '"file_path"[[:space:]]*:[[:space:]]*"[^"]*"' | head -n1 | sed -E 's/.*"([^"]*)"$/\1/')"

# Segredos nunca são editados pelo Claude.
case "$caminho" in
  */infra/secrets/*|*/.env|*/.env.*)
    if [[ "$caminho" != *".env.example" ]]; then
      echo "Bloqueado: $caminho guarda segredos e não pode ser editado pelo Claude." >&2
      exit 2
    fi
    ;;
esac

# Só olha código e SQL; testes podem citar os padrões proibidos para verificá-los.
case "$caminho" in
  *.cs|*.sql|*.ts|*.tsx) ;;
  *) exit 0 ;;
esac
case "$caminho" in
  */tests/*) exit 0 ;;
esac

if printf '%s' "$entrada" | grep -qE 'DELETE[[:space:]]+FROM|ExecuteDelete|TRUNCATE[[:space:]]+TABLE'; then
  echo "Bloqueado: DELETE físico é proibido neste projeto (regra 1 do CLAUDE.md)." >&2
  echo "Use soft delete: marque IsDeleted = true (ou chame Remove(), que o SoftDeleteInterceptor converte)." >&2
  exit 2
fi

exit 0
