#!/usr/bin/env bash
# shellcheck source-path=SCRIPTDIR
# Retenção: mantém os <quantos> pacotes mais novos de uma pasta e remove o resto.
# Uso: rotacionar.sh <pasta absoluta> <quantos>     (backup.sh: diario 7; aos domingos, semanal 4)
#
# Só conta arquivos regulares com nome exato gestor-AAAA-MM-DD.tar.age, direto na pasta: não
# segue links (um link com nome de pacote fica onde está), ignora .parcial, notas e qualquer
# outro nome. A ordem é pelo nome (data ISO), nunca pelo mtime. Nunca apaga fora da pasta.
# Remover um pacote de diario/ não afeta o hard link dele em semanal/.
# Log: só nomes de arquivo e a contagem, nunca caminho completo nem conteúdo.
set -euo pipefail
umask 077

# shellcheck source=lib.sh
source "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/lib.sh"

uso() { echo "uso: rotacionar.sh <pasta absoluta> <quantos>" >&2; exit 2; }

[ "$#" -eq 2 ] || uso
pasta="$1" quantos="$2"

# Validação antes de listar qualquer coisa.
[[ "$pasta" == /* ]] || falhar "rotacao: pasta precisa ser caminho absoluto"
{ [ -d "$pasta" ] && [ ! -L "$pasta" ]; } || falhar "rotacao: pasta inexistente"
[[ "$quantos" =~ ^[1-9][0-9]*$ ]] || falhar "rotacao: quantos precisa ser inteiro >= 1"

nome_pasta="$(basename "$pasta")"
padrao='^gestor-[0-9]{4}-[0-9]{2}-[0-9]{2}\.tar\.age$'

# -type f sem -L: links simbólicos ficam de fora. O glob do find é largo; o regex é o filtro.
pacotes=()
while IFS= read -r nome; do
  [[ "$nome" =~ $padrao ]] && pacotes+=("$nome")
done < <(find "$pasta" -mindepth 1 -maxdepth 1 -type f -name 'gestor-????-??-??.tar.age' -printf '%f\n' \
  | LC_ALL=C sort -r)

removidos=0
for ((i = quantos; i < ${#pacotes[@]}; i++)); do
  rm -f -- "$pasta/${pacotes[$i]}"
  log "rotacao: removido ${pacotes[$i]}"
  removidos=$((removidos + 1))
done

mantidos=$(( ${#pacotes[@]} - removidos ))
log "rotacao: pasta=$nome_pasta manteve=$mantidos removeu=$removidos"
