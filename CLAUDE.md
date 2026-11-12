# Gestor Financeiro — instruções para o Claude

Sistema financeiro pessoal de um casal, rodando num PC de casa. Dados reais de banco passam por aqui: segurança e integridade vêm antes de velocidade de entrega.

## Comandos

- Build e testes do backend: `dotnet test backend/GestorFinanceiro.slnx`
- Só arquitetura: `dotnet test backend/tests/GestorFinanceiro.ArchitectureTests`
- Integração (precisa de Docker): `dotnet test backend/tests/GestorFinanceiro.IntegrationTests`
- Front (tipos + lint + testes + build): `npm --prefix frontend run check`
- Só os testes do front (Vitest): `npm --prefix frontend test`
- Regerar cliente da API depois de mudar endpoint: `dotnet build backend/src/Host/GestorFinanceiro.Api && npm --prefix frontend run gen:api`
- Nova migração: criar `db/migrations/V{próximo número com 4 dígitos}__descricao.sql`; nunca editar uma migração já commitada (skill `nova-migracao`).
- Guardas e testes da parte que mudou (o que o hook `Stop` roda): `scripts/verificar.sh` (`--tudo` antes de PR)
- Só as guardas: `scripts/guardas/verificar-repositorio.sh`; testes delas: `scripts/guardas/testes.sh`

## Regras que não se negociam

1. **Nada de DELETE físico.** Nem `DELETE FROM`, nem `ExecuteDelete`, nem `TRUNCATE`. Remover = `IsDeleted = true` (o `SoftDeleteInterceptor` faz isso quando se chama `Remove`). O banco também bloqueia por trigger.
2. **Auditoria é do banco.** Toda tabela nova chama `SELECT audit.proteger('schema.tabela');` na migração que a cria. Não escrever log de auditoria no C#.
3. **Unicidade com soft delete** é sempre índice parcial: `CREATE UNIQUE INDEX ... WHERE NOT is_deleted`. Nunca `UNIQUE` simples em coluna de negócio.
4. **Dado sensível** (CPF, telefone, número de conta, token, descrição, contraparte, linha digitável) vai em coluna `*_cifrado`/`*_cifrada` (bytea) via `ICofre.Cifrar`, com AAD `tabela|coluna|id`. Busca e unicidade usam `*_hmac` via `ICofre.IndiceCego`. Nunca SHA-256 puro de CPF.
5. **Nada pessoal em log, exceção ou resposta de erro.** Logar ids, nunca valores de CPF, telefone, nome, descrição ou token.
6. **Dinheiro é `Dinheiro` (centavos em `long`)** no domínio e `bigint` no banco. Nunca `double`/`float`.
7. **Módulos não se enxergam.** Só tipos em `GestorFinanceiro.Modules.<X>.Contracts` (e a classe `<X>Module`) são `public`; o resto é `internal`. Um módulo nunca lê o schema de outro.
8. **API só por DTO.** Endpoints em `/api/v1`, erros em Problem Details (RFC 9457). Mudou contrato → regere o cliente do front no mesmo commit.
9. **Toda consulta respeita os filtros nomeados** `SoftDelete` e `Casal`. `IgnoreQueryFilters` só com o nome do filtro (`["SoftDelete"]`) e só na tela de lixeira ou, dentro do módulo dono, na checagem de chave natural de importação (sem expor o conteúdo da linha apagada; spec da Fase 2, D6); nunca desligar o filtro `Casal`, exceto nos fluxos sem sessão do `Identidade` listados no teste de arquitetura dos filtros (login, passkey, TOTP, sessões, dispositivo, recuperação, primeiro acesso e identificação pelo telefone do bot); fluxo novo entra no teste antes.
10. **Gemini não calcula.** Números vêm do `FinanceEngine`; o Gemini recebe só agregados com apelidos (`Conjuge_1`) e o texto dele é conferido contra os números.

## Como trabalhar aqui

- Comece por um teste que falha. Ao terminar, mostre o comando de teste e a saída.
- Mudança em mais de um módulo: proponha o plano antes (plan mode).
- Dados de teste são sempre sintéticos. CPFs de teste: use geradores válidos, nunca um CPF real.
- Antes de concluir, rode o teste de arquitetura; o hook `Stop` roda `scripts/verificar.sh` e bloqueia se falhar.
- Agentes nunca operam em `/opt/gestor-financeiro` (a instalação de verdade, de root); só a pessoa, com `sudo`.
- Estas regras valem para todo agente, não só para o Claude. Papéis (ADR-012, `AGENTS.md`): o CEO (claude.ai) decide e escreve specs; o Claude Code implementa; o Antigravity faz QA e documentação. Siga a ordem de serviço do CEO; o que questiona a spec sobe para ele. Melhoria visual: skill `melhorar-tela`. Ao terminar uma tarefa, use a skill `revisao-cruzada`; nunca `git commit --no-verify`.

## Onde está cada coisa

- Visão da arquitetura e fases: `docs/SPEC.md`; decisões: `docs/adr/`
- Regras específicas: `.claude/rules/backend.md` e `.claude/rules/frontend.md` (carregam só nos arquivos certos)
- Agentes do projeto: `.claude/agents/revisor-seguranca.md` (antes de todo PR, depois de `rm -f .git/revisao.diff && git fetch -q origin main && { echo "HEAD $(git rev-parse --short HEAD)"; git log --format='%h %s%n%b' origin/main..HEAD; echo '---'; git diff origin/main...HEAD; } > .git/revisao.diff`) e `.claude/agents/testador-de-telas.md` (toda tarefa com tela, pelo Playwright); skill `integracao-pluggy` para tudo da Fase 2
- Guardas (DELETE físico, segredos, migrações): `scripts/guardas/regras.sh`, usadas pelos hooks do Claude, pelo `.githooks/pre-commit` e pelo CI
- Trabalho com o Antigravity: `docs/desenvolvimento/dois-agentes.md`; revisões em `.revisoes/<branch>.md`
