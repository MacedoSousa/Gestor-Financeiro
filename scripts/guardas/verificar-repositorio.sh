#!/usr/bin/env bash
# Roda as guardas no repositório inteiro. É o que o CI executa, então vale mesmo para quem
# commitou com --no-verify ou sem os hooks instalados.
# Inclui arquivos novos ainda fora do git (não ignorados): o hook Stop roda antes do commit.
# Uso: scripts/guardas/verificar-repositorio.sh [raiz]   (padrão: raiz do git)
set -uo pipefail

raiz="${1:-$(git rev-parse --show-toplevel)}"
cd "$raiz" || exit 2
# shellcheck source=scripts/guardas/regras.sh
source scripts/guardas/regras.sh

falhou=0

# Rastreados + novos não ignorados, sem os apagados do disco.
arquivos() { git ls-files --cached --others --exclude-standard "$@" | sort -u | while read -r f; do [ -f "$f" ] && echo "$f"; done; }

while read -r arquivo; do
  if eh_caminho_de_segredo "$arquivo"; then
    echo "Segredo versionado: $arquivo" >&2
    falhou=1
  fi
  if eh_codigo_vigiado "$arquivo"; then
    # shellcheck disable=SC2094 # o nome só aparece na mensagem; o arquivo é apenas lido
    checar_delete_fisico "$arquivo" < "$arquivo" || falhou=1
  fi
done < <(arquivos)

mapfile -t migracoes < <(arquivos 'db/migrations/*.sql')
for m in "${migracoes[@]}"; do
  checar_nome_de_migracao "$m" || falhou=1
  checar_conteudo_de_migracao "$m" || falhou=1
done
[ "${#migracoes[@]}" -gt 0 ] && { checar_sequencia_de_migracoes "${migracoes[@]}" || falhou=1; }

if [ "$falhou" -ne 0 ]; then
  echo "Guardas do projeto: FALHOU" >&2
  exit 1
fi
echo "Guardas do projeto: ok (${#migracoes[@]} migrações, $(arquivos | wc -l) arquivos)"
