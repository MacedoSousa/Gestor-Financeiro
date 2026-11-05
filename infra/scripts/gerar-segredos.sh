#!/usr/bin/env bash
# Gera os segredos locais em infra/secrets/ (fora do git). Não sobrescreve nada que já exista.
set -euo pipefail

raiz="$(cd "$(dirname "$0")/../.." && pwd)"
dir="$raiz/infra/secrets"
mkdir -p "$dir"
chmod 700 "$dir"
umask 077

gerar_senha() { openssl rand -base64 33 | tr -d '/+=' | cut -c1-40; }

criar() {
  local nome="$1"; shift
  if [ -e "$dir/$nome" ]; then
    echo "mantido  $nome"
  else
    "$@" > "$dir/$nome"
    chmod 400 "$dir/$nome"
    echo "criado   $nome"
  fi
}

criar pg_superusuario   gerar_senha
criar pg_senha_app      gerar_senha
criar pg_senha_migrador gerar_senha
criar pgpass_app        bash -c "echo \"*:*:gestor:gf_app:\$(cat '$dir/pg_senha_app')\""
criar pgpass_migrador   bash -c "echo \"*:*:gestor:gf_migrator:\$(cat '$dir/pg_senha_migrador')\""
criar gf_chave_servico  openssl rand 32

cat <<EOF

Próximos passos (a API em container roda como o usuário 'app', uid 1654):
  1. Dê à API a posse dos arquivos que ela lê e crie a pasta das chaves já com o dono certo
     (se o Docker criar infra/data/chaves sozinho, ela fica com o root e a API não grava nem lê):
       sudo chown 1654 infra/secrets/{gf_chave_servico,pgpass_app,pgpass_migrador}
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

Instalações anteriores: um dek.recuperacao antigo e o código em papel que veio com ele estão
obsoletos (não são mais lidos). Guarde ou destrua de propósito; não confunda com o código novo.
EOF
