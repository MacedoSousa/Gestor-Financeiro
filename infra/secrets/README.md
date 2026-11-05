# Segredos

Esta pasta guarda senhas do Postgres e a chave de serviço. **Nada aqui vai para o git** (`.gitignore`), e o Claude Code está proibido de ler ou editar estes arquivos (`.claude/settings.json`).

Gere tudo com `infra/scripts/gerar-segredos.sh`. Guarde uma cópia cifrada fora do PC (por exemplo, num pendrive guardado com o código de recuperação impresso).

| Arquivo | Para quê |
| --- | --- |
| `pg_superusuario` | Senha do `postgres` (só para manutenção) |
| `pg_senha_app`, `pgpass_app` | Papel `gf_app`, usado pela API |
| `pg_senha_migrador`, `pgpass_migrador` | Papel `gf_migrator`, dono dos schemas |
| `gf_chave_servico` | 32 bytes que desembrulham a DEK para os jobs noturnos |
| `dek.servico`, `dek.recuperacao` | Envelopes da DEK (criados por `--inicializar-chaves`) |
