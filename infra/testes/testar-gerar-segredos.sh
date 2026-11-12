#!/usr/bin/env bash
# Testes do infra/scripts/gerar-segredos.sh (sem docker), numa raiz temporária com uma cópia do
# script. Nunca toca em infra/secrets do repositório.
#
# Os segredos são 0444 (os containers leem como postgres, uid 70, e app, uid 1654; o compose
# não aplica "mode" em segredo de arquivo) e quem protege no host é a pasta infra/secrets em
# 0700. Exceção: gf_chave_servico fica 0400 (a api recusa chave legível por grupo/outros; ela
# recebe chown 1654). Pasta com outro modo: o script falha em vez de gravar.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
umask 022
T="$(mktemp -d)"
trap 'chmod -R u+w "$T" 2>/dev/null; rm -rf "$T"' EXIT
falhas=0

ok()    { echo "ok $1"; }
falha() { echo "FALHA $1"; falhas=$((falhas + 1)); }
caso()  { if "$1"; then ok "$1"; else falha "$1"; fi; }

# Raiz nova com a cópia do script. Uso: r=$(nova_raiz <nome>)
nova_raiz() {
  mkdir -p "$T/$1/infra/scripts"
  cp "$REPO/infra/scripts/gerar-segredos.sh" "$T/$1/infra/scripts/"
  echo "$T/$1"
}

pasta_0700_e_segredos_0444_menos_a_chave() {
  local r
  r="$(nova_raiz nova)"
  "$r/infra/scripts/gerar-segredos.sh" >"$T/saida" 2>&1 || { sed 's/^/  | /' "$T/saida"; return 1; }
  [ "$(stat -c %a "$r/infra/secrets")" = 700 ] || { echo "  pasta $(stat -c %a "$r/infra/secrets")"; return 1; }
  [ "$(find "$r/infra/secrets" -type f | wc -l)" = 10 ] || { echo "  quantidade de segredos"; return 1; }
  [ "$(find "$r/infra/secrets" -type f ! -name gf_chave_servico -printf '%m\n' | sort -u)" = 444 ] \
    || { echo "  modos [$(find "$r/infra/secrets" -type f -printf '%f=%m ')]"; return 1; }
  [ "$(stat -c %a "$r/infra/secrets/gf_chave_servico")" = 400 ] || { echo "  modo da gf_chave_servico"; return 1; }
}

# Segunda execução mantém o que existe (mesmo conteúdo e inode).
segunda_execucao_mantem() {
  local r antes
  r="$(nova_raiz duas)"
  "$r/infra/scripts/gerar-segredos.sh" >/dev/null 2>&1 || return 1
  antes="$(cd "$r/infra/secrets" && for f in *; do echo "$f $(stat -c %i "$f") $(sha256sum <"$f")"; done)"
  "$r/infra/scripts/gerar-segredos.sh" >"$T/saida" 2>&1 || { sed 's/^/  | /' "$T/saida"; return 1; }
  [ "$(cd "$r/infra/secrets" && for f in *; do echo "$f $(stat -c %i "$f") $(sha256sum <"$f")"; done)" = "$antes" ]
}

pasta_com_outro_modo_falha() {
  local r modo
  for modo in 755 750 711; do
    r="$(nova_raiz "modo-$modo")"
    mkdir -m "$modo" "$r/infra/secrets"
    if "$r/infra/scripts/gerar-segredos.sh" >"$T/saida" 2>&1; then echo "  aceitou pasta $modo"; return 1; fi
    grep -qx "FALHA infra/secrets precisa estar em 0700" "$T/saida" || { sed 's/^/  | /' "$T/saida"; return 1; }
    [ -z "$(ls -A "$r/infra/secrets")" ] || { echo "  gravou com a pasta $modo"; return 1; }
    [ "$(stat -c %a "$r/infra/secrets")" = "$modo" ] || { echo "  mudou o modo da pasta $modo"; return 1; }
  done
}

# Clone novo: o git cria infra/secrets (por causa do README.md) com o modo do umask. Só com o
# README, não há segredo a proteger: o script fecha a pasta em 0700, avisa e gera.
clone_com_so_o_readme_fecha_a_pasta() {
  local r modo
  for modo in 775 755; do
    r="$(nova_raiz "clone-$modo")"
    mkdir -m "$modo" "$r/infra/secrets"
    cp "$REPO/infra/secrets/README.md" "$r/infra/secrets/README.md"
    "$r/infra/scripts/gerar-segredos.sh" >"$T/saida" 2>&1 || { sed 's/^/  | /' "$T/saida"; return 1; }
    grep -qx "infra/secrets fechada em 0700 (só tinha o README)" "$T/saida" \
      || { echo "  sem o aviso (pasta $modo)"; sed 's/^/  | /' "$T/saida"; return 1; }
    [ "$(stat -c %a "$r/infra/secrets")" = 700 ] || { echo "  pasta $(stat -c %a "$r/infra/secrets")"; return 1; }
    [ "$(find "$r/infra/secrets" -type f ! -name README.md | wc -l)" = 10 ] || { echo "  quantidade de segredos"; return 1; }
    cmp -s "$REPO/infra/secrets/README.md" "$r/infra/secrets/README.md" || { echo "  mexeu no README"; return 1; }
  done
}

# Decisão do #24: pasta aberta com qualquer outra coisa além do README nunca é corrigida em
# silêncio (pode já ter segredo exposto); continua falhando sem gravar nem mudar o modo.
pasta_aberta_com_mais_que_o_readme_falha() {
  local r variante antes
  for variante in readme_e_segredo readme_e_outro so_outro readme_link readme_e_subpasta; do
    r="$(nova_raiz "aberta-$variante")"
    mkdir -m 775 "$r/infra/secrets"
    case "$variante" in
      readme_e_segredo)  cp "$REPO/infra/secrets/README.md" "$r/infra/secrets/"; echo s >"$r/infra/secrets/pg_superusuario" ;;
      readme_e_outro)    cp "$REPO/infra/secrets/README.md" "$r/infra/secrets/"; echo x >"$r/infra/secrets/.x" ;;
      so_outro)          echo x >"$r/infra/secrets/notas.txt" ;;
      readme_link)       ln -s "$REPO/infra/secrets/README.md" "$r/infra/secrets/README.md" ;;
      readme_e_subpasta) cp "$REPO/infra/secrets/README.md" "$r/infra/secrets/"; mkdir "$r/infra/secrets/sub" ;;
    esac
    antes="$(cd "$r/infra/secrets" && find . -printf '%P %y %s\n' | sort)"
    if "$r/infra/scripts/gerar-segredos.sh" >"$T/saida" 2>&1; then echo "  aceitou ($variante)"; return 1; fi
    grep -qx "FALHA infra/secrets precisa estar em 0700" "$T/saida" || { sed 's/^/  | /' "$T/saida"; return 1; }
    [ "$(cd "$r/infra/secrets" && find . -printf '%P %y %s\n' | sort)" = "$antes" ] || { echo "  gravou ($variante)"; return 1; }
    [ "$(stat -c %a "$r/infra/secrets")" = 775 ] || { echo "  mudou o modo ($variante)"; return 1; }
  done
}

PLUGGY="pluggy_1_client_id pluggy_1_client_secret pluggy_2_client_id pluggy_2_client_secret"

# D23: os quatro segredos da Pluggy nascem vazios (slot não configurado), 0444, para o compose
# subir. Quem grava o conteúdo é a pessoa, pelo terminal.
segredos_da_pluggy_nascem_vazios() {
  local r f
  r="$(nova_raiz pluggy-vazios)"
  "$r/infra/scripts/gerar-segredos.sh" >"$T/saida" 2>&1 || { sed 's/^/  | /' "$T/saida"; return 1; }
  for f in $PLUGGY; do
    [ -f "$r/infra/secrets/$f" ] || { echo "  falta $f"; return 1; }
    [ "$(stat -c %s "$r/infra/secrets/$f")" = 0 ] || { echo "  $f não está vazio"; return 1; }
    [ "$(stat -c %a "$r/infra/secrets/$f")" = 444 ] || { echo "  modo de $f"; return 1; }
    grep -qx "criado   $f" "$T/saida" || { echo "  sem 'criado $f' na saída"; return 1; }
  done
}

# Passo 7 dos próximos passos (do "  7." até o parágrafo seguinte), para os casos abaixo.
passo_7() {
  local r
  r="$(nova_raiz "passo7-$1")"
  "$r/infra/scripts/gerar-segredos.sh" >"$T/saida" 2>&1 || { sed 's/^/  | /' "$T/saida"; return 1; }
  sed -n '/^  7\. /,/^$/p' "$T/saida" >"$T/passo7"
  [ -s "$T/passo7" ] || { echo "  sem o passo 7"; return 1; }
}

# Número da linha (no passo 7) do primeiro comando que contém o texto; vazio se não houver.
linha_de() { grep -nF -- "$1" "$T/passo7" | head -1 | cut -d: -f1; }

COMPOSE_OPT="sudo docker compose -f /opt/gestor-financeiro/infra/docker-compose.yml"
SHRED_11_11="sudo shred -fu ~/Gestor-Financeiro/infra/secrets/pluggy_client_id ~/Gestor-Financeiro/infra/secrets/pluggy_client_secret"
CMP_11_11="sudo cmp ~/Gestor-Financeiro/infra/secrets/pluggy_client_id /opt/gestor-financeiro/infra/secrets/pluggy_1_client_id && sudo cmp ~/Gestor-Financeiro/infra/secrets/pluggy_client_secret /opt/gestor-financeiro/infra/secrets/pluggy_1_client_secret && echo iguais"

# D12: o passo 7 é o roteiro completo para pôr a etapa no ar em /opt, feito pela pessoa, com sudo, nesta ordem:
# pull → backup → gerar-segredos.sh → cópia das chaves de 11/11 para o slot 1 → stat → shred dos originais →
# build → migração → subida → conferência.
passo_7_roda_em_opt_com_sudo_e_na_ordem() {
  local esperado num anterior=0
  passo_7 ordem || return 1
  grep -qF "na pasta /opt/gestor-financeiro, com sudo" "$T/passo7" || { echo "  não diz onde roda"; return 1; }
  grep -qF "fora de sudo -i" "$T/passo7" || { echo "  não avisa para rodar fora de sudo -i"; return 1; }
  grep -qF "Depois do merge do PR da etapa 2" "$T/passo7" || { echo "  não diz que é depois do merge"; return 1; }
  for esperado in \
    "sudo git -C /opt/gestor-financeiro pull" \
    "sudo systemctl start gestor-backup.service" \
    "sudo journalctl -u gestor-backup -n 5 --no-pager" \
    "sudo /opt/gestor-financeiro/infra/scripts/gerar-segredos.sh" \
    "sudo install -m 444 -o root ~/Gestor-Financeiro/infra/secrets/pluggy_client_id /opt/gestor-financeiro/infra/secrets/pluggy_1_client_id" \
    "sudo stat -c '%n %U %a %s' /opt/gestor-financeiro/infra/secrets/pluggy_1_client_id" \
    "$CMP_11_11" \
    "$SHRED_11_11" \
    "$COMPOSE_OPT build api web" \
    "$COMPOSE_OPT stop api" \
    "$COMPOSE_OPT run --rm api --migrar" \
    "$COMPOSE_OPT up -d api web" \
    "$COMPOSE_OPT ps api web" \
    "$COMPOSE_OPT exec -T postgres wget -q -O - http://api:8080/api/v1/saude"; do
    num="$(linha_de "$esperado")"
    [ -n "$num" ] || { echo "  falta: $esperado"; return 1; }
    [ "$num" -gt "$anterior" ] || { echo "  fora de ordem: $esperado"; return 1; }
    anterior="$num"
  done
  # Todo comando (linha que começa por sudo, read ou um passo "a)") usa sudo e caminho absoluto em /opt: nenhum
  # "infra/" relativo, nenhum docker ou git sem sudo.
  if grep -E '^\s+(sudo |read |[a-z]\) )' "$T/passo7" | grep -E '(^|[ =])infra/' >/dev/null; then
    echo "  comando com caminho relativo:"; grep -E '(^|[ =])infra/' "$T/passo7" | sed 's/^/  | /'; return 1
  fi
  if grep -E '^\s+(docker|git) ' "$T/passo7" >/dev/null; then echo "  docker ou git sem sudo"; return 1; fi
  # Gravação pelo terminal, sem eco e sem deixar o valor no histórico. O read mostra um aviso: sem ele, a senha do
  # sudo pedida logo depois poderia ser digitada no read e gravada no arquivo de segredo.
  # O echo depois do read quebra a linha: o pedido de senha do sudo não aparece colado no aviso.
  for f in pluggy_2_client_id pluggy_2_client_secret; do
    grep -qF "read -rsp 'valor (não aparece): ' v; echo; printf '%s' \"\$v\" | sudo tee /opt/gestor-financeiro/infra/secrets/$f >/dev/null; unset v" "$T/passo7" \
      || { echo "  sem o comando de gravação de $f (aviso no read, echo e tee sem eco)"; return 1; }
  done
  if grep -E 'read -rs v' "$T/passo7" >/dev/null; then echo "  read sem aviso"; return 1; fi
  if grep -F "read -rsp" "$T/passo7" | grep -vqF "v; echo; printf"; then echo "  read sem echo depois"; return 1; fi
  grep -qF "$COMPOSE_OPT restart api" "$T/passo7" || { echo "  sem o restart da api"; return 1; }
  # Quem exige os quatro arquivos é o compose (a api trata ausente como slot não configurado).
  grep -qF "o compose exige os quatro" "$T/passo7" || { echo "  não diz que é o compose que exige os quatro"; return 1; }
  grep -qF "Na instalação que já existe, só o passo 7 (os passos 1 a 6 são da primeira instalação)." "$T/saida" \
    || { echo "  sem o aviso de que só o passo 7 vale na instalação existente"; return 1; }
  grep -qF "Depois de regravar as chaves de um slot, reinicie a api (a chave temporária fica em cache por até 110 min)." "$T/passo7" \
    || { echo "  sem o aviso de reiniciar a api"; return 1; }
  grep -qF "sudo docker compose -f /opt/gestor-financeiro/infra/docker-compose.yml restart api" "$T/passo7" \
    || { echo "  sem o comando de reiniciar a api"; return 1; }
}

# Backup antes de mexer no banco: o backup (e a conferência do "backup ok") vem antes da migração e da subida,
# e o passo manda parar se ele não terminar bem.
passo_7_backup_antes_da_migracao() {
  local backup journal migrar
  passo_7 backup || return 1
  backup="$(linha_de "sudo systemctl start gestor-backup.service")"
  journal="$(linha_de "sudo journalctl -u gestor-backup -n 5 --no-pager")"
  migrar="$(linha_de "$COMPOSE_OPT run --rm api --migrar")"
  [ -n "$backup" ] && [ -n "$journal" ] && [ -n "$migrar" ] || { echo "  falta backup, journal ou migração"; return 1; }
  [ "$backup" -lt "$journal" ] && [ "$journal" -lt "$migrar" ] || { echo "  backup depois da migração"; return 1; }
  [ "$migrar" -lt "$(linha_de "$COMPOSE_OPT up -d api web")" ] || { echo "  subida antes da migração"; return 1; }
  grep -qF "backup ok" "$T/passo7" || { echo "  não diz o que conferir no journal"; return 1; }
  # A frase inteira: "pare" sozinho casaria com "aparecer".
  grep -qF "Se o start der erro ou o \"backup ok\" não" "$T/passo7" && grep -qF "pare aqui e não siga para os próximos itens" "$T/passo7" \
    || { echo "  não manda parar se o backup falhar"; return 1; }
}

# A api tem IP fixo no compose: com ela de pé, o "run --rm api" falha com "Address already in use". Ela para antes
# da migração; se a migração falhar, a pessoa para e religa a api antiga (start não recria o container), e a nova
# não sobe sem a migração.
passo_7_migracao_com_a_api_parada_e_parada_se_falhar() {
  local stop migrar condicao up start erro anotar pull voltar
  passo_7 migracao || return 1
  stop="$(linha_de "$COMPOSE_OPT stop api")"
  migrar="$(linha_de "$COMPOSE_OPT run --rm api --migrar")"
  condicao="$(linha_de "Só se o --migrar terminar sem erro (última linha: \"migrações em dia.\"):")"
  up="$(linha_de "$COMPOSE_OPT up -d api web")"
  [ -n "$stop" ] && [ -n "$migrar" ] && [ -n "$condicao" ] && [ -n "$up" ] || { echo "  falta stop, migração, condição ou subida"; return 1; }
  [ "$stop" -lt "$migrar" ] && [ "$migrar" -lt "$condicao" ] && [ "$condicao" -lt "$up" ] || { echo "  ordem stop → migração → condição → subida"; return 1; }
  erro="$(linha_de "Se o --migrar der erro, pare aqui e religue a api e o web antigos:")"
  [ -n "$erro" ] || { echo "  não manda parar se a migração falhar"; return 1; }
  start="$(linha_de "$COMPOSE_OPT start api web")"
  [ -n "$start" ] && [ "$start" -gt "$erro" ] || { echo "  sem o start da api e do web antigos depois da frase do erro"; return 1; }
  # Código e imagens novos continuam em /opt: o próximo "up" (o do boot, inclusive o reinício das 04:00) subiria a
  # api nova sobre o banco não migrado. Até resolver, nada de up nem de reiniciar; para voltar de vez, o commit
  # anotado antes do pull e o build.
  grep -qF "Até resolver, não rode nenhum up nem reinicie o PC" "$T/passo7" || { echo "  sem o aviso de não subir nem reiniciar"; return 1; }
  anotar="$(linha_de "sudo git -C /opt/gestor-financeiro rev-parse --short HEAD")"
  pull="$(linha_de "sudo git -C /opt/gestor-financeiro pull")"
  [ -n "$anotar" ] && [ "$anotar" -lt "$pull" ] || { echo "  sem anotar o commit antes do pull"; return 1; }
  voltar="$(linha_de "sudo git -C /opt/gestor-financeiro checkout <commit anotado no item a>")"
  [ -n "$voltar" ] && [ "$voltar" -gt "$start" ] || { echo "  sem o checkout do commit anotado depois do start"; return 1; }
  [ "$(linha_de "$COMPOSE_OPT build api web")" -lt "$voltar" ] && grep -n -F "$COMPOSE_OPT build api web" "$T/passo7" | cut -d: -f1 | awk -v v="$voltar" '$1 > v' | grep -q . \
    || { echo "  sem o build depois do checkout de volta"; return 1; }
}

# De a) a g) de uma vez, longe do backup das 03:00 e do reinício das 04:00.
passo_7_longe_do_backup_e_do_reinicio() {
  passo_7 horario || return 1
  grep -qF "longe das 03:00 (backup) e das 04:00 (reinício automático)" "$T/passo7" || { echo "  sem o aviso de horário"; return 1; }
}

# Os originais de 11/11 só são apagados (shred) depois de o stat mostrar a cópia certa no slot 1.
passo_7_shred_so_depois_do_stat() {
  local stat cmp condicao shred
  passo_7 shred || return 1
  stat="$(linha_de "sudo stat -c '%n %U %a %s' /opt/gestor-financeiro/infra/secrets/pluggy_1_client_id")"
  cmp="$(linha_de "$CMP_11_11")"
  condicao="$(linha_de "Só se o stat mostrar root 444 e tamanho maior que 0 nos dois do slot 1 e o cmp")"
  shred="$(linha_de "$SHRED_11_11")"
  [ -n "$stat" ] && [ -n "$cmp" ] && [ -n "$condicao" ] && [ -n "$shred" ] || { echo "  falta stat, cmp, condição ou shred"; return 1; }
  [ "$stat" -lt "$cmp" ] && [ "$cmp" -lt "$condicao" ] && [ "$condicao" -lt "$shred" ] || { echo "  shred antes do stat e do cmp, ou sem a condição"; return 1; }
  [ "$(grep -cF "shred" "$T/passo7")" -ge 1 ] && ! grep -F "shred" "$T/passo7" | grep -qF "/opt/" \
    || { echo "  shred tocando /opt"; return 1; }
}

# O client_secret de 11/11 também vai para o slot 1 (não para o 2), com o mesmo dono e modo, e é conferido.
passo_7_copia_o_client_secret_para_o_slot_1() {
  local id segredo
  passo_7 secret || return 1
  id="$(linha_de "sudo install -m 444 -o root ~/Gestor-Financeiro/infra/secrets/pluggy_client_id ")"
  segredo="$(linha_de "sudo install -m 444 -o root ~/Gestor-Financeiro/infra/secrets/pluggy_client_secret /opt/gestor-financeiro/infra/secrets/pluggy_1_client_secret")"
  [ -n "$segredo" ] || { echo "  sem a cópia do client_secret para o slot 1"; return 1; }
  [ -n "$id" ] && [ "$segredo" -gt "$id" ] || { echo "  client_secret antes do client_id"; return 1; }
  # A pasta em /opt é 0700 do root: o glob não expandiria no shell da pessoa, então o stat lista os arquivos.
  grep -F "sudo stat -c '%n %U %a %s' " "$T/passo7" | grep -qF "/opt/gestor-financeiro/infra/secrets/pluggy_1_client_secret" \
    || { echo "  sem a conferência do client_secret"; return 1; }
  if grep -F "pluggy_client_secret" "$T/passo7" | grep -qF "pluggy_2_"; then echo "  client_secret de 11/11 indo para o slot 2"; return 1; fi
}

# Um segredo da Pluggy já preenchido (valor sintético) não é tocado: mesmo inode, mesmo sha, mesmo modo.
pluggy_existente_nao_e_tocado() {
  local r antes
  r="$(nova_raiz pluggy-existente)"
  mkdir -m 700 "$r/infra/secrets"
  printf '%s' 'id-sintetico-de-teste' >"$r/infra/secrets/pluggy_1_client_id"
  chmod 444 "$r/infra/secrets/pluggy_1_client_id"
  antes="$(stat -c '%i %a %s' "$r/infra/secrets/pluggy_1_client_id") $(sha256sum <"$r/infra/secrets/pluggy_1_client_id")"
  "$r/infra/scripts/gerar-segredos.sh" >"$T/saida" 2>&1 || { sed 's/^/  | /' "$T/saida"; return 1; }
  grep -qx "mantido  pluggy_1_client_id" "$T/saida" || { echo "  sem 'mantido pluggy_1_client_id'"; return 1; }
  [ "$(stat -c '%i %a %s' "$r/infra/secrets/pluggy_1_client_id") $(sha256sum <"$r/infra/secrets/pluggy_1_client_id")" = "$antes" ] \
    || { echo "  pluggy_1_client_id mudou"; return 1; }
  [ "$(stat -c %s "$r/infra/secrets/pluggy_2_client_id")" = 0 ] || { echo "  pluggy_2_client_id não nasceu vazio"; return 1; }
}

caso pasta_0700_e_segredos_0444_menos_a_chave
caso segredos_da_pluggy_nascem_vazios
caso passo_7_roda_em_opt_com_sudo_e_na_ordem
caso passo_7_copia_o_client_secret_para_o_slot_1
caso passo_7_backup_antes_da_migracao
caso passo_7_shred_so_depois_do_stat
caso passo_7_migracao_com_a_api_parada_e_parada_se_falhar
caso passo_7_longe_do_backup_e_do_reinicio
caso pluggy_existente_nao_e_tocado
caso segunda_execucao_mantem
caso pasta_com_outro_modo_falha
caso clone_com_so_o_readme_fecha_a_pasta
caso pasta_aberta_com_mais_que_o_readme_falha

[ "$falhas" -eq 0 ] || { echo "$falhas teste(s) falharam"; exit 1; }
