# Gestor Financeiro — instruções para o Claude

Sistema financeiro pessoal de um casal, rodando num PC de casa. Dados reais de banco passam por aqui: segurança e integridade vêm antes de velocidade de entrega.

## Comandos

- Build e testes do backend: `dotnet test backend/GestorFinanceiro.slnx`
- Só arquitetura: `dotnet test backend/tests/GestorFinanceiro.ArchitectureTests`
- Front (tipos + lint + build): `npm --prefix frontend run check`
- Regerar cliente da API depois de mudar endpoint: `dotnet build backend/src/Host/GestorFinanceiro.Api && npm --prefix frontend run gen:api`
- Nova migração: criar `db/migrations/V{próximo número com 4 dígitos}__descricao.sql`; nunca editar uma migração já commitada.

## Regras que não se negociam

1. **Nada de DELETE físico.** Nem `DELETE FROM`, nem `ExecuteDelete`, nem `TRUNCATE`. Remover = `IsDeleted = true` (o `SoftDeleteInterceptor` faz isso quando se chama `Remove`). O banco também bloqueia por trigger.
2. **Auditoria é do banco.** Toda tabela nova chama `SELECT audit.proteger('schema.tabela');` na migração que a cria. Não escrever log de auditoria no C#.
3. **Unicidade com soft delete** é sempre índice parcial: `CREATE UNIQUE INDEX ... WHERE NOT is_deleted`. Nunca `UNIQUE` simples em coluna de negócio.
4. **Dado sensível** (CPF, telefone, número de conta, token, descrição, contraparte, linha digitável) vai em coluna `*_cifrado`/`*_cifrada` (bytea) via `ICofre.Cifrar`, com AAD `tabela|coluna|id`. Busca e unicidade usam `*_hmac` via `ICofre.IndiceCego`. Nunca SHA-256 puro de CPF.
5. **Nada pessoal em log, exceção ou resposta de erro.** Logar ids, nunca valores de CPF, telefone, nome, descrição ou token.
6. **Dinheiro é `Dinheiro` (centavos em `long`)** no domínio e `bigint` no banco. Nunca `double`/`float`.
7. **Módulos não se enxergam.** Só tipos em `GestorFinanceiro.Modules.<X>.Contracts` (e a classe `<X>Module`) são `public`; o resto é `internal`. Um módulo nunca lê o schema de outro.
8. **API só por DTO.** Endpoints em `/api/v1`, erros em Problem Details (RFC 9457). Mudou contrato → regere o cliente do front no mesmo commit.
9. **Toda consulta respeita os filtros nomeados** `SoftDelete` e `Casal`. `IgnoreQueryFilters` só com o nome do filtro (`["SoftDelete"]`) e só na tela de lixeira; nunca desligar o filtro `Casal`.
10. **Gemini não calcula.** Números vêm do `FinanceEngine`; o Gemini recebe só agregados com apelidos (`Conjuge_1`) e o texto dele é conferido contra os números.

## Como trabalhar aqui

- Comece por um teste que falha. Ao terminar, mostre o comando de teste e a saída.
- Mudança em mais de um módulo: proponha o plano antes (plan mode).
- Dados de teste são sempre sintéticos. CPFs de teste: use geradores válidos, nunca um CPF real.
- Antes de concluir, rode o teste de arquitetura; o hook `Stop` roda de novo e bloqueia se falhar.

## Onde está cada coisa

- Visão da arquitetura e fases: `docs/SPEC.md`; decisões: `docs/adr/`
- Regras específicas: `.claude/rules/backend.md` e `.claude/rules/frontend.md` (carregam só nos arquivos certos)
