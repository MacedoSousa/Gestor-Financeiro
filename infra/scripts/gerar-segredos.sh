#!/usr/bin/env bash
# Gera os segredos locais em infra/secrets/ (fora do git). Não sobrescreve nada que já exista.
#
# Os segredos são 0444: o compose monta segredo de arquivo como bind mount e ignora "mode", e os
# containers leem com outro uid (postgres 70, api 1654). Quem protege no host é a pasta, em
# 0700: se ela já existir com outro modo, o script falha em vez de gravar. Exceção: a pasta do
# clone, só com o README.md, é fechada em 0700 com aviso.
# Exceção: gf_chave_servico fica 0400, porque a api recusa chave de serviço legível por
# grupo/outros (ChaveDeServico.cs); ela recebe chown 1654 (próximos passos, abaixo).
# Teste: infra/testes/testar-gerar-segredos.sh e infra/testes/testar-subida.sh.
set -euo pipefail

raiz="$(cd "$(dirname "$0")/../.." && pwd)"
dir="$raiz/infra/secrets"
umask 077
mkdir -p "$raiz/infra"
[ -d "$dir" ] || mkdir -m 700 "$dir"
if [ -L "$dir" ] || [ "$(stat -c %a "$dir")" != 700 ]; then
  # Clone novo: o git cria a pasta (por causa do README.md) com o modo do umask. Só com o
  # README não há segredo a proteger, então ela é fechada. Qualquer outra coisa ali: falha,
  # nunca corrige em silêncio uma pasta que pode já ter segredo exposto.
  if [ ! -L "$dir" ] && [ -f "$dir/README.md" ] && [ ! -L "$dir/README.md" ] \
     && [ "$(find "$dir" -mindepth 1 -maxdepth 1 -printf x 2>/dev/null)" = x ]; then
    chmod 700 "$dir"
    echo "infra/secrets fechada em 0700 (só tinha o README)" >&2
  else
    echo "FALHA infra/secrets precisa estar em 0700" >&2
    exit 1
  fi
fi

gerar_senha() { openssl rand -base64 33 | tr -d '/+=' | cut -c1-40; }

criar() {
  local nome="$1"; shift
  if [ -e "$dir/$nome" ]; then
    echo "mantido  $nome"
  else
    "$@" > "$dir/$nome"
    if [ "$nome" = gf_chave_servico ]; then chmod 400 "$dir/$nome"; else chmod 444 "$dir/$nome"; fi
    echo "criado   $nome"
  fi
}

criar pg_superusuario   gerar_senha
criar pg_senha_app      gerar_senha
criar pg_senha_migrador gerar_senha
criar pgpass_app        bash -c "echo \"*:*:gestor:gf_app:\$(cat '$dir/pg_senha_app')\""
criar pgpass_migrador   bash -c "echo \"*:*:gestor:gf_migrator:\$(cat '$dir/pg_senha_migrador')\""
criar gf_chave_servico  openssl rand 32
# Pluggy (D23): um par de chaves por slot, com nome fixo. Nascem vazios (slot não configurado),
# só para o compose subir; quem grava o conteúdo é a pessoa, pelo terminal (próximos passos).
for slot in 1 2; do
  criar "pluggy_${slot}_client_id"     true
  criar "pluggy_${slot}_client_secret" true
done

cat <<EOF

Próximos passos (a API em container roda como o usuário 'app', uid 1654):
  1. Dê à API a posse da chave de serviço (0400; os outros segredos são 0444 numa pasta 0700 e
     não precisam de chown) e crie a pasta das chaves já com o dono certo (se o Docker criar
     infra/data/chaves sozinho, ela fica com o root e a API não grava nem lê):
       sudo chown 1654 infra/secrets/gf_chave_servico
       sudo install -d -o 1654 -m 700 infra/data/chaves
  2. docker compose -f infra/docker-compose.yml up -d postgres
  3. Rode as migrações e crie a chave de dados. --inicializar-chaves cria só a DEK e o envelope
     de serviço (infra/data/chaves/dek.servico); não mostra código de recuperação:
       docker compose -f infra/docker-compose.yml run --rm api --migrar
       docker compose -f infra/docker-compose.yml run --rm api --inicializar-chaves
  4. Gere o código de instalação COMO o usuário da API (o arquivo vai para infra/data/chaves):
       docker compose -f infra/docker-compose.yml run --rm api --codigo-instalacao
     Vale 24 h e é de uso único; gerar de novo invalida o anterior.
  5. Suba tudo (docker compose -f infra/docker-compose.yml up -d), abra https://<seu domínio>/cadastro
     e cadastre o casal com esse código. O código de recuperação aparece UMA vez, no fim do
     cadastro — imprima e guarde em papel.
  6. Faça backup de infra/secrets/ e de infra/data/chaves/dek.servico num lugar fora deste PC
     (sem eles, os dados cifrados não abrem).
  7. Open Finance (Meu Pluggy): cada cônjuge grava as próprias chaves num slot, pelo terminal
     (o valor não aparece na tela nem fica no histórico). Slot vazio = não configurado. Um
     comando por arquivo; cole o valor e tecle Enter:
       read -rs v; printf '%s' "\$v" | sudo tee infra/secrets/pluggy_1_client_id >/dev/null; unset v
       read -rs v; printf '%s' "\$v" | sudo tee infra/secrets/pluggy_1_client_secret >/dev/null; unset v
       read -rs v; printf '%s' "\$v" | sudo tee infra/secrets/pluggy_2_client_id >/dev/null; unset v
       read -rs v; printf '%s' "\$v" | sudo tee infra/secrets/pluggy_2_client_secret >/dev/null; unset v
     Depois: docker compose -f infra/docker-compose.yml up -d api
     Instalação que já existia: depois do git pull, rode este script de novo ANTES do docker compose up
     (a api exige os quatro arquivos; ele só cria os que faltam). As chaves gravadas em 11/11
     (pluggy_client_*, no clone ~/Gestor-Financeiro) viram o slot 1 de quem as criou (D23):
       sudo install -m 0444 ~/Gestor-Financeiro/infra/secrets/pluggy_client_id infra/secrets/pluggy_1_client_id
       sudo install -m 0444 ~/Gestor-Financeiro/infra/secrets/pluggy_client_secret infra/secrets/pluggy_1_client_secret

Instalações anteriores: um dek.recuperacao antigo e o código em papel que veio com ele estão
obsoletos (não são mais lidos). Guarde ou destrua de propósito; não confunda com o código novo.
EOF
