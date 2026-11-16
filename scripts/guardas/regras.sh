#!/usr/bin/env bash
# Regras do CLAUDE.md que dá para conferir por texto. Fonte única para:
#   - .claude/hooks/bloquear-delete-fisico.sh  (Claude Code, antes de editar)
#   - .githooks/pre-commit                      (qualquer agente ou pessoa, antes do commit)
#   - scripts/guardas/verificar-repositorio.sh  (CI, no repositório inteiro)
# Só funções; quem usa faz `source`. Cada checagem escreve o motivo em stderr e devolve 1 se falhar.

# Regra 1: nada de DELETE físico.
GUARDA_DELETE_FISICO='DELETE[[:space:]]+FROM|ExecuteDelete|TRUNCATE[[:space:]]+TABLE'

# Arquivos que guardam segredo: nenhum agente edita, ninguém commita.
eh_caminho_de_segredo() {
  case "$1" in
    *.env.example) return 1 ;;
    infra/secrets/README.md|*/infra/secrets/README.md) return 1 ;;
    infra/secrets/*|*/infra/secrets/*|.env|*/.env|.env.*|*/.env.*|backups/*|*/backups/*|infra/data/*|*/infra/data/*) return 0 ;;
    *.key|*.pem|*.dump|*chave-servico*) return 0 ;;
    # App Android: keystores, senha da assinatura, chaves do adb/emulador e do Gradle do usuário.
    *.jks|*.keystore|*.p12|*.pfx|*.bks|*.pepk|keystore.properties|*/keystore.properties) return 0 ;;
    .android|.android/*|*/.android|*/.android/*) return 0 ;;
    .gradle/gradle.properties|*/.gradle/gradle.properties) return 0 ;;
    # Chave JSON de conta de serviço (Play Console, Google Cloud) pelo nome; pelo conteúdo, ver abaixo.
    *service-account*.json|*service_account*.json|*conta-de-servico*.json|*conta_de_servico*.json) return 0 ;;
  esac
  return 1
}

# Lê um JSON no stdin; devolve 0 se for chave de conta de serviço, seja qual for o nome do arquivo.
eh_json_de_conta_de_servico() {
  grep -qE '"type"[[:space:]]*:[[:space:]]*"service_account"|"private_key"[[:space:]]*:'
}

# Gradle: só passam as tarefas liberadas no .claude/settings.json e estas opções (lista positiva).
# Abreviação (signingR, :app:sigRep), tarefa de módulo, -I, --scan, -D, -p etc. ficam de fora.
GRADLE_TAREFAS_LIBERADAS=" lint testDebugUnitTest assembleDebug assembleRelease openApiGenerate ktlintCheck verifyRoborazziDebug recordRoborazziDebug koverXmlReport connectedDebugAndroidTest "
GRADLE_OPCOES_LIBERADAS=" --offline --console=plain --stacktrace -q "

# Tira aspas e barras invertidas, como o shell faria antes de rodar.
sem_aspas() { tr -d "\"'\\\\"; }

# Palavras de um comando, uma por linha, sem aspas nem \ (separa também em ; & | ( ) < > =).
palavras_do_comando() {
  printf '%s\n' "$1" | sem_aspas | tr -s ' \t;&|()<>=' '\n'
}

# Trechos de um comando (um por linha), separados por ; && || | & ( ).
trechos_do_comando() {
  printf '%s\n' "$1" | sed -E 's/\|\||&&|[;|&()`]/\n/g'
}

# Palavra com glob (* ? [) que pode casar com segredo: cita .android, .gradle, keystore, .jk ou .p1,
# ou tem um trecho com "." que casa com o nome de um keystore, de uma chave ou de um dotfile de segredo.
glob_cita_segredo() {
  local palavra="$1" seg nome cand
  local -a segs
  case "${palavra,,}" in *.android*|*.gradle*|*keystore*|*.jk*|*.p1*) return 0 ;; esac
  IFS=/ read -ra segs <<< "$palavra"
  for seg in "${segs[@]}"; do
    [[ "$seg" == *[\*\?\[]* && "$seg" == *.* ]] || continue
    nome="${seg%%.*}"
    [[ -z "$nome" || "$nome" == *[\*\?\[]* ]] && nome=x
    for cand in .android .env keystore.properties gradle.properties "$nome.jks" "$nome.keystore" "$nome.p12" "$nome.pfx" "$nome.bks" "$nome.pepk" "$nome.key" "$nome.pem"; do
      # shellcheck disable=SC2053 # $seg é o padrão de propósito
      [[ "$cand" == $seg ]] && return 0
    done
  done
  return 1
}

# Uma palavra do comando cita segredo?
palavra_cita_segredo() {
  local palavra="$1"
  if [[ "$palavra" == *[\*\?\[]* ]] && glob_cita_segredo "$palavra"; then return 0; fi
  # Só os filtros do jq com sufixo de segredo ficam livres ('.key', '.pem', '.dump', '.[].key');
  # .keystore, .x.jks, .chave-servico.json etc. caem na regra de caminho como qualquer palavra.
  case "$palavra" in .key|.pem|.dump|.\[*) return 1 ;; esac
  eh_caminho_de_segredo "$palavra"
}

# Um trecho que chama gradlew/gradle (em qualquer forma: aspas, caminho, env, bash -c) só pode
# ter tarefas e opções da lista positiva; redirecionamento de saída é aceito.
checar_trecho_gradle() {
  local -a p
  local i=0 n palavra pula=0 falhou=0
  read -ra p <<< "$(printf '%s' "$1" | sem_aspas)"
  n=${#p[@]}
  while [ "$i" -lt "$n" ]; do
    palavra="${p[$i]}"
    if [[ "$palavra" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]]; then i=$((i + 1)); continue; fi
    case "$palavra" in
      env|command|exec|nice|nohup|time|bash|sh|-c|-lc|-l|-e|-x) i=$((i + 1)) ;;
      timeout) i=$((i + 2)) ;;
      *) break ;;
    esac
  done
  [ "$i" -lt "$n" ] || return 0
  case "${p[$i]##*/}" in gradlew|gradle) ;; *) return 0 ;; esac
  for palavra in "${p[@]:i+1}"; do
    if [ "$pula" -eq 1 ]; then pula=0; continue; fi
    case "$palavra" in
      '>'|'>>'|'<'|[0-9]'>'|[0-9]'>>'|'&>') pula=1; continue ;;
      '>'*|'<'*|[0-9]'>'*|'&>'*) continue ;;
    esac
    [[ "$GRADLE_TAREFAS_LIBERADAS$GRADLE_OPCOES_LIBERADAS" == *" $palavra "* ]] && continue
    echo "Bloqueado: gradle com '$palavra', fora da lista liberada (tarefas do settings.json e $GRADLE_OPCOES_LIBERADAS)." >&2
    falhou=1
  done
  return "$falhou"
}

# --- D24: a instalação de verdade é só da pessoa ------------------------------------------------
# Ela mora em /opt/gestor-financeiro e roda no projeto "gestor" do compose (o infra/docker-compose.yml declara
# "name: gestor"; os containers são gestor-*). Nenhum agente lê, edita ou mexe nela. O docker dos agentes passa só por
# scripts/docker-gf.sh, que confere os argumentos já separados pelo shell (lista positiva, nomes gf-*); o hook barra o
# docker direto, fora a leitura (ps, images, version, info). É uma guarda de texto: pega o descuido e as grafias comuns,
# não um caminho montado em variável de propósito, nem o que roda dentro de um script ou programa (bash /tmp/x.sh,
# python3 -c "subprocess..."): esses ficam com a revisão.

# Caminho em /opt: no começo da palavra, depois de : = , @ ou de uma opção colada (-C/opt), com barras repetidas,
# relativo (../opt), entre chaves (/{opt,}) ou por glob no primeiro nível (/op*, /o?t, /*).
palavra_cita_opt() {
  local n primeiro r
  n="$(printf '%s' "$1" | tr -s '/')"
  # Caminho absoluto normalizado (/./opt, /a/../opt) e chaves em caminho absoluto (/{o,}pt).
  if [[ "$n" == /* ]]; then
    [[ "$n" == *\{* ]] && return 0
    if [[ "$n" != *[\*\?\[]* ]]; then
      r="$(realpath -m -- "$n" 2>/dev/null || printf '%s' "$n")"
      [[ "$r" == /opt || "$r" == /opt/* ]] && return 0
    fi
  fi
  [[ "$n" =~ (^|[:=,@]|^-[A-Za-z]+)/opt(/|$|[^A-Za-z0-9._-]) ]] && return 0
  [[ "$n" =~ (^|/)\.\./(\.\./)*opt(/|$) ]] && return 0
  [[ "$n" == *\{*opt*\}* && "$n" == */* ]] && return 0
  [[ "$n" == /* ]] || return 1
  primeiro="${n#/}"; primeiro="${primeiro%%/*}"
  [[ -n "$primeiro" && "$primeiro" == *[\*\?\[]* ]] || return 1
  # shellcheck disable=SC2053 # o primeiro nível é um glob: o lado direito do == é o padrão de propósito
  [[ "opt" == $primeiro ]]
}

# Anda pelos segmentos de um caminho a partir de base (o diretório do comando), sem seguir links: devolve 0 se ele chega
# em /opt (também por glob ou chaves no nível da raiz: ../../op*, ../../o{p,}t). CAMINHO_FINAL fica com o resultado.
caminho_chega_em_opt() {
  local palavra="$1" base="$2" atual seg
  local -a segs
  # As variáveis de diretório que se escrevem à mão: $HOME, $PWD (a base) e $OLDPWD (desconhecido: conta como /).
  palavra="${palavra//\$\{HOME\}/$HOME}"; palavra="${palavra//\$HOME/$HOME}"
  palavra="${palavra//\$\{PWD\}/$base}"; palavra="${palavra//\$PWD/$base}"
  palavra="${palavra//\$\{OLDPWD\}//}"; palavra="${palavra//\$OLDPWD//}"
  # shellcheck disable=SC2088 # o til é o texto que a guarda recebe; ele é trocado pelo $HOME aqui
  case "$palavra" in
    /*) atual=/ ;;
    "~" | "~/"*) palavra="$HOME/${palavra#\~}"; atual=/ ;;
    # ~- e ~+ (o diretório anterior e o atual do shell) e ~usuário: a guarda não sabe onde caem, contam como /.
    "~"[-+]* | "~"[A-Za-z_]*) palavra="/${palavra#*/}"; [[ "$1" == */* ]] || palavra=/; atual=/ ;;
    *) atual="$base" ;;
  esac
  IFS=/ read -ra segs <<< "$palavra"
  for seg in "${segs[@]}"; do
    case "$seg" in
      "" | .) continue ;;
      ..) atual="$(dirname -- "$atual")" ;;
      *)
        if [ "$atual" = / ]; then
          if [ "$seg" = opt ] || [[ "$seg" == *\{* ]]; then CAMINHO_FINAL=/opt; return 0; fi
          # shellcheck disable=SC2053 # o segmento pode ser um glob: o lado direito do == é o padrão de propósito
          if [[ "$seg" == *[\*\?\[]* && "opt" == $seg ]]; then CAMINHO_FINAL=/opt; return 0; fi
        fi
        atual="${atual%/}/$seg" ;;
    esac
  done
  CAMINHO_FINAL="$atual"
  return 1
}

# A palavra é o nome deste programa: pelo nome do arquivo, também por glob (/usr/bin/dock?r) ou chaves ({docker,}).
palavra_e_programa() {
  local base="${1##*/}" alvo="$2" cand
  [ "$base" = "$alvo" ] && return 0
  if [[ "$base" == *\{* ]]; then
    for cand in $(printf '%s' "$base" | tr -d '{}' | tr ',' ' '); do [ "$cand" = "$alvo" ] && return 0; done
  fi
  # shellcheck disable=SC2053 # a palavra pode ser um glob: o lado direito do == é o padrão de propósito
  [[ "$base" == *[\*\?\[]* && "$alvo" == $base ]]
}

# Nome do projeto, container, volume, rede ou imagem da instalação de verdade (gestor, gestor-*, gestor_*, /gestor-*).
nome_do_gestor() {
  case "${1#/}" in gestor|gestor-*|gestor_*|-pgestor*) return 0 ;; esac
  return 1
}

# O que é dos agentes: containers, projetos, imagens e redes com nome gf-* (testes e QA).
nome_nosso() { [[ "${1#/}" == gf-* && "$1" != *'$'* ]]; }

# Comandos que só leem texto: uma palavra "docker", "pkill" etc. nos argumentos deles não é execução. git, sed e awk
# ficam de fora (git rebase -x, sed .../e e awk system() executam comandos).
comando_de_texto() {
  case "${1##*/}" in
    grep|egrep|fgrep|rg|echo|printf|cat|head|tail|less|wc|jq|ls|diff|sort|uniq|cut|tr|shellcheck|stat|file) return 0 ;;
  esac
  return 1
}

# Índice da primeira palavra do trecho que executa um destes programas (pelo nome do arquivo), em qualquer posição:
# depois de env, sudo, timeout, stdbuf, setsid, eval, script -c, bash -c "..." etc. Num comando só de texto, nenhuma.
# Devolve o índice em INDICE_DO_PROGRAMA (ou -1). Usa TRECHO_P.
achar_programa() {
  local i alvo
  INDICE_DO_PROGRAMA=-1
  [ "${#TRECHO_P[@]}" -gt 0 ] || return 0
  local primeiro=0
  while [ "$primeiro" -lt "${#TRECHO_P[@]}" ] && [[ "${TRECHO_P[$primeiro]}" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]]; do primeiro=$((primeiro + 1)); done
  [ "$primeiro" -lt "${#TRECHO_P[@]}" ] || return 0
  if comando_de_texto "${TRECHO_P[$primeiro]}"; then return 0; fi
  for i in "${!TRECHO_P[@]}"; do
    for alvo in "$@"; do
      # Também colado depois de = (GIT_PAGER="docker ...", LESSOPEN=...).
      if palavra_e_programa "${TRECHO_P[$i]}" "$alvo" || palavra_e_programa "${TRECHO_P[$i]##*=}" "$alvo"; then INDICE_DO_PROGRAMA=$i; return 0; fi
      # Plugins do docker chamados direto (docker-buildx, docker-compose); docker-compose.yml é arquivo, não plugin. Um nome
      # de branch docker-* também cai aqui (risco aceito: escolha outro nome).
      if [ "$alvo" = docker ] && [[ "${TRECHO_P[$i]##*/}" =~ ^docker-[a-z]+$ ]]; then INDICE_DO_PROGRAMA=$i; return 0; fi
    done
  done
}

# Índice do programa que o trecho roda, pulando atribuições, env, sudo, timeout, nice, nohup, bash/sh/source/. e as opções
# deles (-c, -e...). -1 se não houver. Usa TRECHO_P.
programa_do_trecho() {
  local i=0 palavra
  INDICE_DO_PROGRAMA=-1
  while [ "$i" -lt "${#TRECHO_P[@]}" ]; do
    palavra="${TRECHO_P[$i]}"
    if [[ "$palavra" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]]; then i=$((i + 1)); continue; fi
    case "${palavra##*/}" in
      env | sudo | nice | nohup | exec | command | time | bash | sh | source | . | stdbuf | setsid | xargs) i=$((i + 1)) ;;
      timeout) i=$((i + 2)) ;;
      -*) i=$((i + 1)) ;;
      *) INDICE_DO_PROGRAMA=$i; return 0 ;;
    esac
  done
}

separar_palavras() { read -ra TRECHO_P <<< "$(printf '%s' "$1" | sem_aspas)"; }

# Alvo calculado no trecho: $(...), crase, variável (fora $PWD) ou xargs antes do programa.
alvo_calculado() {
  local ate="$1" i palavra
  for i in "${!TRECHO_P[@]}"; do
    palavra="${TRECHO_P[$i]//\$\{PWD\}/}"; palavra="${palavra//\$PWD/}"
    [[ "$palavra" == *'$'* || "$palavra" == *'`'* ]] && return 0
    [ "${COMANDO_TEM_CRASE:-0}" -eq 1 ] && return 0
    [ "$i" -lt "$ate" ] && [ "${TRECHO_P[$i]##*/}" = xargs ] && return 0
  done
  return 1
}

# pkill, killall e fuser -k casam por nome ou porta e alcançam a instalação de verdade; kill só com PID literal.
checar_trecho_kill() {
  local i
  separar_palavras "$1"
  achar_programa pkill killall killall5 skill
  if [ "$INDICE_DO_PROGRAMA" -ge 0 ]; then
    echo "Bloqueado: ${TRECHO_P[$INDICE_DO_PROGRAMA]##*/} casa por nome e alcança a instalação de verdade; pare pelo PID que você gravou (D24)." >&2
    return 1
  fi
  achar_programa fuser
  if [ "$INDICE_DO_PROGRAMA" -ge 0 ]; then
    for i in "${TRECHO_P[@]:INDICE_DO_PROGRAMA+1}"; do
      if [[ "$i" == -*k* && "$i" != --* ]] || [ "$i" = --kill ]; then
        echo "Bloqueado: fuser -k mata pela porta e alcança a instalação de verdade; pare pelo PID que você gravou (D24)." >&2
        return 1
      fi
    done
  fi
  achar_programa kill
  if [ "$INDICE_DO_PROGRAMA" -ge 0 ] && alvo_calculado "$INDICE_DO_PROGRAMA"; then
    echo "Bloqueado: kill com alvo calculado (\$(...), variável ou xargs); use o PID literal que você gravou (D24)." >&2
    return 1
  fi
  return 0
}

# Os scripts de produção do backup rodam como root, contra a instalação de verdade; os testes os chamam por dentro.
checar_trecho_producao() {
  local programa primeiro=0 palavra
  separar_palavras "$1"
  while [ "$primeiro" -lt "${#TRECHO_P[@]}" ] && [[ "${TRECHO_P[$primeiro]}" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]]; do primeiro=$((primeiro + 1)); done
  [ "$primeiro" -lt "${#TRECHO_P[@]}" ] || return 0
  # Ler ou versionar o script (git, sed, cat...) não o roda; qualquer outro comando que o cite, em qualquer posição
  # (timeout -s KILL 600 ..., nice -n 19 ..., env -C dir ./backup.sh), conta como rodar.
  case "${TRECHO_P[$primeiro]##*/}" in
    git | sed | awk | vim | vi | nano | code | gh | find | chmod | chown | cp | mv | ln | test | "[" | realpath | basename | dirname | diff) return 0 ;;
    bash | sh) [ "${TRECHO_P[$((primeiro + 1))]:-}" = -n ] && return 0 ;;
  esac
  comando_de_texto "${TRECHO_P[$primeiro]}" && return 0
  # Pelo nome exato (um glob como *.sh não é o script).
  programa=""
  for palavra in "${TRECHO_P[@]}"; do
    case "${palavra##*/}" in backup.sh | restaurar.sh | enviar.sh) programa="$palavra"; break ;; esac
  done
  [ -n "$programa" ] || return 0
  caminho_chega_em_opt "$programa" "${2:-$PWD}" || true
  if [[ "$CAMINHO_FINAL" != */testes/* ]]; then
    echo "Bloqueado: $programa é script de produção (instalação de verdade); rode os testes em infra/backup/testes (D24)." >&2
    return 1
  fi
  return 0
}

# docker direto (em qualquer posição do trecho: env -i, bash -c "...", eval, timeout...) só para ler; o resto vai por
# scripts/docker-gf.sh. O socket, DOCKER_HOST e DOCKER_CONTEXT nunca.
checar_trecho_docker() {
  local k palavra primeiro=0
  separar_palavras "$1"
  [ "${#TRECHO_P[@]}" -gt 0 ] || return 0
  while [ "$primeiro" -lt "${#TRECHO_P[@]}" ] && [[ "${TRECHO_P[$primeiro]}" =~ ^[A-Za-z_][A-Za-z0-9_]*= ]]; do primeiro=$((primeiro + 1)); done
  if [ "$primeiro" -ge "${#TRECHO_P[@]}" ] || ! comando_de_texto "${TRECHO_P[$primeiro]}"; then
    for palavra in "${TRECHO_P[@]}"; do
      # shellcheck disable=SC2053 # a palavra pode ser um glob (/var/run/dock*.sock): o lado direito do == é o padrão
      if [[ "$palavra" == *docker.sock* || "$palavra" == DOCKER_HOST=* || "$palavra" == DOCKER_CONTEXT=* || "$palavra" == DOCKER_GF_BIN=* ]] \
         || [[ "$palavra" == *[\*\?\[]* && ( "/var/run/docker.sock" == $palavra || "/run/docker.sock" == $palavra || "docker.sock" == ${palavra##*/} ) ]]; then
        echo "Bloqueado: docker pelo socket ou por outro host/contexto ($palavra) (D24)." >&2
        return 1
      fi
    done
  fi
  achar_programa docker docker-compose podman nerdctl
  k="$INDICE_DO_PROGRAMA"
  [ "$k" -ge 0 ] || return 0
  if [ "$k" -eq "$primeiro" ] && [ "${TRECHO_P[$k]}" = docker ] && ! alvo_calculado "$k"; then
    case "${TRECHO_P[$((k + 1))]:-}" in ps | images | version | info | --version) return 0 ;; esac
  fi
  echo "Bloqueado: docker direto; use scripts/docker-gf.sh (lista positiva, nomes gf-*; só ps, images, version e info passam direto) (D24)." >&2
  return 1
}

# Busca ou cópia a partir da raiz (find /, grep -r ... /, du /) lê /opt sem citá-lo.
checar_trecho_raiz() {
  local palavra programa="" base="${2:-$PWD}"
  separar_palavras "$1"
  for palavra in "${TRECHO_P[@]}"; do
    case "${palavra##*/}" in find | grep | egrep | fgrep | rg | du | tar | rsync | ls | tree | cp | chmod | chown | zip | ln) programa="${palavra##*/}"; break ;; esac
  done
  [ -n "$programa" ] || return 0
  # Na raiz, o programa recursivo sem caminho escrito lê o "." (find -name x, grep -rn x, rg x, du -sh, ls -R).
  if [ "$base" = / ]; then
    case "$programa" in
      find | rg | du | tree | tar) echo "Bloqueado: $programa em / alcança /opt, a instalação de verdade (D24)." >&2; return 1 ;;
      grep | egrep | fgrep | ls)
        for palavra in "${TRECHO_P[@]}"; do
          if [[ "$palavra" =~ ^-[A-Za-z]*[rR] || "$palavra" == --recursive || "$palavra" == --dereference-recursive ]]; then
            echo "Bloqueado: $programa recursivo em / alcança /opt, a instalação de verdade (D24)." >&2
            return 1
          fi
        done ;;
    esac
  fi
  for palavra in "${TRECHO_P[@]}"; do
    [[ "$palavra" == */* || "$palavra" == . || "$palavra" == .. || "$palavra" == "~"* || "$palavra" == *'$'* ]] || continue
    [[ "$palavra" == -* ]] && continue
    if [[ "$palavra" =~ ^/+\*$ ]] || { ! caminho_chega_em_opt "$palavra" "$base" && [ "$CAMINHO_FINAL" = / ]; }; then
      echo "Bloqueado: $programa a partir de / alcança /opt, a instalação de verdade (D24)." >&2
      return 1
    fi
  done
}

# Comando que o agente vai rodar pelo Bash (hook PreToolUse). Falha se citar caminho de segredo
# (também por glob, \ ou cd para ~/.gradle), se um trecho com gradle sair da lista positiva ou se mexer
# na instalação de verdade (/opt, docker no projeto gestor, pkill/killall; D24).
# Mensagem de commit e corpo de PR vão por arquivo (git commit -F, gh pr ... --body-file).
checar_comando_bash() {
  local comando="$1" palavra trecho falhou=0 cita_gradle_home=0 parte i_cd
  local base="${GUARDA_CWD:-$PWD}"
  # Barra invertida no fim da linha junta as duas (doc\⏎ker é docker).
  comando="${comando//$'\\\n'/}"
  COMANDO_TEM_CRASE=0
  [[ "$comando" == *'`'* ]] && COMANDO_TEM_CRASE=1
  local -a palavras
  mapfile -t palavras < <(palavras_do_comando "$comando")
  for palavra in "${palavras[@]}"; do
    case "$palavra" in .gradle|.gradle/*|*/.gradle|*/.gradle/*) cita_gradle_home=1 ;; esac
  done
  for palavra in "${palavras[@]}"; do
    [ -z "$palavra" ] && continue
    # shellcheck disable=SC2053 # no elif, a palavra pode ser um glob: o lado direito do == é o padrão de propósito
    if palavra_cita_segredo "$palavra"; then
      echo "Bloqueado: o comando cita $palavra, que guarda segredo (regra 5 do CLAUDE.md)." >&2
      falhou=1
    elif [ "$cita_gradle_home" -eq 1 ] && [[ "gradle.properties" == ${palavra##*/} ]]; then
      echo "Bloqueado: o comando cita .gradle e $palavra (gradle.properties do usuário guarda senha)." >&2
      falhou=1
    elif palavra_cita_opt "$palavra"; then
      echo "Bloqueado: o comando cita $palavra; /opt é a instalação de verdade, só a pessoa mexe nela (D24)." >&2
      falhou=1
    fi
  done
  while IFS= read -r trecho; do
    checar_trecho_gradle "$trecho" || falhou=1
    checar_trecho_docker "$trecho" || falhou=1
    checar_trecho_kill "$trecho" || falhou=1
    checar_trecho_producao "$trecho" "$base" || falhou=1
    checar_trecho_raiz "$trecho" "$base" || falhou=1
    separar_palavras "$trecho"
    # Caminho relativo a partir do diretório do comando (o cwd do hook, e o de cada cd): ../../opt, cd / && ls opt/x.
    for palavra in "${TRECHO_P[@]}"; do
      for parte in "$palavra" "${palavra##*=}"; do
        [[ "$parte" == */* || "$parte" == opt* || "$parte" == *\{* || "$parte" == "~"* || "$parte" == *'$'* ]] \
          || { [ "$base" = / ] && [[ "$parte" == *[\*\?\[]* ]]; } || continue
        [[ "$parte" == -* ]] && continue
        if caminho_chega_em_opt "$parte" "$base"; then
          echo "Bloqueado: $palavra chega a /opt a partir de $base; /opt é a instalação de verdade (D24)." >&2
          falhou=1
          break
        fi
      done
    done
    # cd ou pushd, também depois de if/then/do/{ e com opções (-P, -L, --).
    i_cd=0
    while [ "$i_cd" -lt "${#TRECHO_P[@]}" ] && [[ "${TRECHO_P[$i_cd]}" =~ ^(if|then|else|elif|do|\{|!)$ ]]; do i_cd=$((i_cd + 1)); done
    # popd volta a um diretório que a guarda não acompanha: conta como /.
    [ "${TRECHO_P[$i_cd]:-}" = popd ] && base=/
    if [[ "${TRECHO_P[$i_cd]:-}" == cd || "${TRECHO_P[$i_cd]:-}" == pushd ]]; then
      i_cd=$((i_cd + 1))
      while [[ "${TRECHO_P[$i_cd]:-}" == -?* && "${TRECHO_P[$i_cd]}" != - ]]; do i_cd=$((i_cd + 1)); done
      if [ "${TRECHO_P[$i_cd]:-}" = - ]; then
        base=/ # cd - volta ao diretório anterior, que a guarda não conhece: conta como / (como o $OLDPWD)
      else
        while [[ "${TRECHO_P[$i_cd]:-}" == -* ]]; do i_cd=$((i_cd + 1)); done
        caminho_chega_em_opt "${TRECHO_P[$i_cd]:-~}" "$base" || true
        base="$CAMINHO_FINAL"
      fi
    fi
  done < <(trechos_do_comando "$comando")
  return "$falhou"
}

# Código e SQL de produção (testes podem citar os padrões proibidos para verificá-los).
eh_codigo_vigiado() {
  case "$1" in
    */tests/*|tests/*|scripts/guardas/*) return 1 ;;
    *.cs|*.sql|*.ts|*.tsx) return 0 ;;
  esac
  return 1
}

# Lê texto no stdin; falha se tiver DELETE físico.
checar_delete_fisico() {
  local origem="$1"
  if grep -qiE "$GUARDA_DELETE_FISICO"; then
    echo "Bloqueado: DELETE físico em $origem (regra 1 do CLAUDE.md). Use soft delete: IsDeleted = true ou Remove()." >&2
    return 1
  fi
}

# Nome de migração: V0001__descricao_em_snake.sql
checar_nome_de_migracao() {
  local nome; nome="$(basename "$1")"
  if [[ ! "$nome" =~ ^V[0-9]{4}__[a-z0-9_]+\.sql$ ]]; then
    echo "Migração com nome fora do padrão: $nome (esperado V0001__descricao.sql)." >&2
    return 1
  fi
}

# Numeração sem buraco nem repetição, a partir de V0001. Recebe os caminhos como argumentos.
checar_sequencia_de_migracoes() {
  local esperado=1 numero nome falhou=0
  while read -r nome; do
    [ -z "$nome" ] && continue
    numero=$((10#${nome:1:4}))
    if [ "$numero" -ne "$esperado" ]; then
      printf 'Sequência de migrações quebrada: %s (esperado V%04d).\n' "$nome" "$esperado" >&2
      falhou=1
      esperado=$numero
    fi
    esperado=$((esperado + 1))
  done < <(for f in "$@"; do basename "$f"; done | sort)
  return "$falhou"
}

# SQL sem comentários de linha, um comando por linha (separados por ';'). Lê do stdin.
comandos_sql() {
  sed -E 's/--.*$//' | tr '\n' ' ' | tr ';' '\n' | sed -E 's/[[:space:]]+/ /g; s/^ //; s/ $//' | grep -v '^$' || true
}

# Regras 2 e 3 numa migração: toda tabela criada chama audit.proteger no mesmo arquivo;
# todo comando com UNIQUE é índice parcial (WHERE NOT is_deleted). Analisa o comando inteiro, não a linha.
checar_conteudo_de_migracao() {
  local arquivo="$1" falhou=0 comandos tabela
  comandos="$(comandos_sql < "$arquivo")"

  while read -r tabela; do
    [ -z "$tabela" ] && continue
    if [[ "$tabela" != *.* ]]; then
      echo "$(basename "$arquivo"): CREATE TABLE $tabela sem schema; toda tabela tem schema (regras 2 e 7)." >&2
      falhou=1
    fi
  done < <(printf '%s\n' "$comandos" | grep -ioE 'CREATE TABLE (IF NOT EXISTS )?[^ (]+' | awk '{print $NF}')

  while read -r tabela; do
    [ -z "$tabela" ] && continue
    [ "$tabela" = "audit.eventos" ] && continue
    if ! grep -qF "audit.proteger('$tabela')" "$arquivo"; then
      echo "$(basename "$arquivo"): tabela $tabela sem SELECT audit.proteger('$tabela') (regra 2)." >&2
      falhou=1
    fi
  done < <(printf '%s\n' "$comandos" | grep -ioE 'CREATE TABLE (IF NOT EXISTS )?[a-z_]+\.[a-z_]+' | awk '{print tolower($NF)}')

  while read -r comando; do
    [ -z "$comando" ] && continue
    # Única exceção (FK composta, decisão do CEO na E3): UNIQUE (casal_id, id) tem a chave primária nas colunas, então o
    # soft delete não muda nada nela; e o Postgres não aceita FK para índice parcial.
    if printf '%s' "$comando" | grep -qiE '^ALTER TABLE [a-z_]+\.[a-z_]+ ADD CONSTRAINT [a-z_]+ UNIQUE \(casal_id, id\)$'; then
      continue
    fi
    if ! printf '%s' "$comando" | grep -qF 'WHERE NOT is_deleted'; then
      echo "$(basename "$arquivo"): UNIQUE sem 'WHERE NOT is_deleted' (regra 3): ${comando:0:120}" >&2
      falhou=1
    fi
  done < <(printf '%s\n' "$comandos" | grep -iE '\bUNIQUE\b')

  if grep -qiE "$GUARDA_DELETE_FISICO" < <(comandos_sql < "$arquivo"); then
    echo "$(basename "$arquivo"): DELETE físico na migração (regra 1)." >&2
    falhou=1
  fi
  return "$falhou"
}

# ADR-013: o Docker do snap não é suportado (o AppArmor nega file_inherit e redirecionamentos
# de arquivo chegam vazios). Devolve 0 se o docker do PATH vem do snap. Confere o caminho do
# PATH e o resolvido com readlink -f: no snap, /snap/bin/docker é um link para /usr/bin/snap,
# e um link nosso pode apontar para /snap/bin/docker.
docker_e_do_snap() {
  local caminho resolvido
  caminho="$(command -v docker 2>/dev/null)" || return 1
  resolvido="$(readlink -f "$caminho" 2>/dev/null || printf '%s' "$caminho")"
  case "$caminho" in /snap/*|*/snap/bin/*) return 0 ;; esac
  case "$resolvido" in /snap/*|*/snap/bin/*|*/bin/snap) return 0 ;; esac
  return 1
}
