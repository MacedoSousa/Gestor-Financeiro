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

Próximos passos:
  1. docker compose -f infra/docker-compose.yml up -d postgres
  2. Rode as migrações e crie a chave de dados (o código de recuperação aparece UMA vez — imprima):
       dotnet run --project backend/src/Host/GestorFinanceiro.Api -- --migrar
       dotnet run --project backend/src/Host/GestorFinanceiro.Api -- --inicializar-chaves
  3. Faça backup de infra/secrets/ num lugar fora deste PC (sem eles, os dados cifrados não abrem).
  4. Para rodar a API em container (usuário 'app', uid 1654), dê a ela a posse dos arquivos que ela lê:
       sudo chown 1654 infra/secrets/{gf_chave_servico,pgpass_app,pgpass_migrador}
EOF
