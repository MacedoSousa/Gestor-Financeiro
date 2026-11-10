#!/usr/bin/env bash
# Roda todos os testes do backup (testes/testar-*.sh). Sai ≠0 se algum falhar.
# Precisa de docker e da imagem postgres:17-alpine.
set -euo pipefail

dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
falhos=()

for teste in "$dir"/testar-*.sh; do
  echo "== $(basename "$teste")"
  if ! bash "$teste"; then
    falhos+=("$(basename "$teste")")
  fi
done

if [ "${#falhos[@]}" -gt 0 ]; then
  echo "FALHA em: ${falhos[*]}"
  exit 1
fi
echo "todos os testes do backup passaram"
