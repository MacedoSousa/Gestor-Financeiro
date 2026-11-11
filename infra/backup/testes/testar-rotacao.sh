#!/usr/bin/env bash
# shellcheck source-path=SCRIPTDIR
# Testes de rotacionar.sh <pasta absoluta> <quantos> (Task 3). Sem docker: só arquivos num tmp.
# Os "pacotes" são arquivos comuns com um conteúdo marcador, para conferir que nada do
# conteúdo vai para o log e que o que sobra continua íntegro.
set -euo pipefail

TESTES_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROTACIONAR="$TESTES_DIR/../rotacionar.sh"
MARCADOR="conteudo-sintetico-xyzzy"

umask 077
T="$(mktemp -d)"
trap 'rm -rf "$T"' EXIT
falhas=0

ok()    { echo "ok $1"; }
falha() { echo "FALHA $1"; falhas=$((falhas + 1)); }
caso()  { if "$1"; then ok "$1"; else falha "$1"; fi; }

# Pasta nova (absoluta) para um caso. Uso: pasta=$(nova_pasta <nome>)
nova_pasta() { rm -rf "${T:?}/$1"; mkdir -p "$T/$1/diario"; echo "$T/$1/diario"; }

# Cria pacotes falsos gestor-2026-11-<dd>.tar.age para cada dia dado.
pacotes() {
  local pasta="$1" dia; shift
  for dia in "$@"; do printf '%s %s\n' "$MARCADOR" "$dia" >"$pasta/gestor-2026-11-$dia.tar.age"; done
}

# Lista ordenada dos nomes na pasta (inclui ocultos e links).
nomes() { find "$1" -mindepth 1 -maxdepth 1 -printf '%f\n' | LC_ALL=C sort; }

rodar() { "$ROTACIONAR" "$@" >"$T/saida" 2>&1; }

mantem_7_mais_novos() {
  local p dia i=0
  p="$(nova_pasta mantem)"
  pacotes "$p" 01 02 03 04 05 06 07 08 09 10
  # mtimes embaralhados: os nomes mais antigos ficam com mtime mais novo.
  for dia in 01 02 03 04 05 06 07 08 09 10; do
    i=$((i + 1))
    touch -d "2026-12-$(printf '%02d' $((20 - i))) 12:00" "$p/gestor-2026-11-$dia.tar.age"
  done
  rodar "$p" 7 || { cat "$T/saida"; return 1; }
  [ "$(nomes "$p")" = "$(printf 'gestor-2026-11-%s.tar.age\n' 04 05 06 07 08 09 10)" ] \
    || { echo "  restou: $(nomes "$p" | tr '\n' ' ')"; return 1; }
}

ignora_outros_arquivos() {
  local p
  p="$(nova_pasta outros)"
  pacotes "$p" 01 02 03 04 05 06 07 08
  echo notas >"$p/notas.txt"
  echo parcial >"$p/.gestor-x.parcial"
  echo parcial >"$p/.gestor-2026-11-09.parcial"
  rodar "$p" 7 || { cat "$T/saida"; return 1; }
  [ ! -e "$p/gestor-2026-11-01.tar.age" ] || return 1
  [ -f "$p/notas.txt" ] && [ -f "$p/.gestor-x.parcial" ] && [ -f "$p/.gestor-2026-11-09.parcial" ] \
    && [ "$(find "$p" -name 'gestor-*.tar.age' | wc -l)" -eq 7 ]
}

menos_que_o_limite_nao_apaga() {
  local p antes
  p="$(nova_pasta poucos)"
  pacotes "$p" 01 02 03
  antes="$(nomes "$p")"
  rodar "$p" 7 || { cat "$T/saida"; return 1; }
  [ "$(nomes "$p")" = "$antes" ]
}

quantos_zero_recusa() {
  local p antes
  p="$(nova_pasta zero)"
  pacotes "$p" 01 02 03
  antes="$(nomes "$p")"
  if rodar "$p" 0; then return 1; fi
  [ "$(nomes "$p")" = "$antes" ]
}

# Só nome exato gestor-AAAA-MM-DD.tar.age entra na contagem. gestor-2026-1a-10.tar.age passa
# no glob do find e, se contasse, seria o "mais novo" por nome e empurraria um pacote real.
so_nome_exato() {
  local p
  p="$(nova_pasta exato)"
  pacotes "$p" 01 02 03 04 05 06 07 08
  for estranho in gestor-2026-11-10.tar.age.bak gestor-x.tar.age GESTOR-2026-11-10.tar.age gestor-2026-1a-10.tar.age; do
    echo estranho >"$p/$estranho"
  done
  rodar "$p" 7 || { cat "$T/saida"; return 1; }
  [ "$(nomes "$p")" = "$(printf '%s\n' GESTOR-2026-11-10.tar.age gestor-2026-11-02.tar.age gestor-2026-11-03.tar.age \
    gestor-2026-11-04.tar.age gestor-2026-11-05.tar.age gestor-2026-11-06.tar.age gestor-2026-11-07.tar.age \
    gestor-2026-11-08.tar.age gestor-2026-11-10.tar.age.bak gestor-2026-1a-10.tar.age gestor-x.tar.age | LC_ALL=C sort)" ] \
    || { echo "  restou: $(nomes "$p" | tr '\n' ' ')"; return 1; }
}

# Um link simbólico com nome de pacote, apontando para fora, não é seguido nem apagado.
ignora_links_simbolicos() {
  local p alvo antes
  p="$(nova_pasta links)"
  mkdir -p "$T/links/fora"; alvo="$T/links/fora/alvo.tar.age"
  echo "$MARCADOR alvo" >"$alvo"
  antes="$(sha256sum "$alvo" | cut -d' ' -f1)"
  pacotes "$p" 01 02 03 04 05 06 07 08
  ln -s "$alvo" "$p/gestor-2026-01-01.tar.age"
  rodar "$p" 7 || { cat "$T/saida"; return 1; }
  [ -L "$p/gestor-2026-01-01.tar.age" ] || { echo "  link apagado"; return 1; }
  [ "$(sha256sum "$alvo" | cut -d' ' -f1)" = "$antes" ] || { echo "  alvo mudou"; return 1; }
  [ ! -e "$p/gestor-2026-11-01.tar.age" ] && [ -f "$p/gestor-2026-11-02.tar.age" ]
}

# O semanal é hard link do diário: sair de diario/ não pode estragar o semanal/.
hard_link_semanal_sobrevive() {
  local p semanal antes
  p="$(nova_pasta hardlink)"; semanal="$T/hardlink/semanal"; mkdir -p "$semanal"
  pacotes "$p" 01 02 03 04 05 06 07 08
  ln "$p/gestor-2026-11-01.tar.age" "$semanal/gestor-2026-11-01.tar.age"
  antes="$(sha256sum "$semanal/gestor-2026-11-01.tar.age" | cut -d' ' -f1)"
  rodar "$p" 7 || { cat "$T/saida"; return 1; }
  [ ! -e "$p/gestor-2026-11-01.tar.age" ] || { echo "  não rotacionou"; return 1; }
  [ "$(sha256sum "$semanal/gestor-2026-11-01.tar.age" | cut -d' ' -f1)" = "$antes" ]
}

pasta_relativa_ou_inexistente_recusa() {
  local p antes
  p="$(nova_pasta relativa)"
  pacotes "$p" 01 02 03 04 05 06 07 08
  antes="$(nomes "$p")"
  if ( cd "$T/relativa" && "$ROTACIONAR" diario 7 >"$T/saida" 2>&1 ); then echo "  aceitou relativa"; return 1; fi
  if rodar /nao/existe 7; then echo "  aceitou inexistente"; return 1; fi
  if rodar "$p/gestor-2026-11-08.tar.age" 7; then echo "  aceitou arquivo como pasta"; return 1; fi
  [ "$(nomes "$p")" = "$antes" ]
}

quantos_invalido_recusa() {
  local p antes q
  p="$(nova_pasta invalido)"
  pacotes "$p" 01 02 03 04 05 06 07 08
  antes="$(nomes "$p")"
  for q in abc -1 7x "" " 7" 07x; do
    if rodar "$p" "$q"; then echo "  aceitou [$q]"; return 1; fi
  done
  if rodar "$p"; then echo "  aceitou sem quantidade"; return 1; fi
  if rodar "$p" 7 extra; then echo "  aceitou argumento a mais"; return 1; fi
  [ "$(nomes "$p")" = "$antes" ]
}

# A saída só tem nomes de arquivo e a contagem; nada do conteúdo nem o caminho da pasta.
nenhuma_saida_com_conteudo() {
  local p
  p="$(nova_pasta log)"
  pacotes "$p" 01 02 03 04 05 06 07 08 09 10
  rodar "$p" 7 || { cat "$T/saida"; return 1; }
  ! grep -q "$MARCADOR" "$T/saida" || { echo "  conteúdo no log"; return 1; }
  ! grep -qF "$T" "$T/saida" || { echo "  caminho da pasta no log"; return 1; }
  grep -Eq '^[0-9TZ:-]+ rotacao: pasta=diario manteve=7 removeu=3$' "$T/saida" \
    || { echo "  sem a linha de contagem"; sed 's/^/  | /' "$T/saida"; return 1; }
  ! grep -Evq '^[0-9TZ:-]+ rotacao: (removido gestor-[0-9]{4}-[0-9]{2}-[0-9]{2}\.tar\.age|pasta=diario manteve=7 removeu=3)$' "$T/saida" \
    || { echo "  linha fora do formato"; sed 's/^/  | /' "$T/saida"; return 1; }
}

[ -x "$ROTACIONAR" ] || { echo "FALHA rotacionar.sh não existe ou não é executável"; exit 1; }

caso mantem_7_mais_novos
caso ignora_outros_arquivos
caso menos_que_o_limite_nao_apaga
caso quantos_zero_recusa
caso so_nome_exato
caso ignora_links_simbolicos
caso hard_link_semanal_sobrevive
caso pasta_relativa_ou_inexistente_recusa
caso quantos_invalido_recusa
caso nenhuma_saida_com_conteudo

[ "$falhas" -eq 0 ] || { echo "$falhas teste(s) falharam"; exit 1; }
