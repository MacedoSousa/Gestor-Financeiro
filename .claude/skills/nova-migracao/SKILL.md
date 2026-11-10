---
name: nova-migracao
description: Use sempre que for criar tabela, índice, coluna ou qualquer mudança de schema em db/migrations. Dá o modelo de migração do projeto (soft delete, auditoria, índice parcial, cifrado + hmac) e a checagem que o pre-commit e o CI vão fazer.
---

# Nova migração

Regras 1 a 4 do `CLAUDE.md` em forma de arquivo. As guardas (`scripts/guardas/regras.sh`) recusam o commit se faltar algo.

## Passos

1. Número: o próximo com 4 dígitos, sem buraco (`ls db/migrations | tail -1`). Nome `V00NN__descricao_em_snake.sql`.
2. **Nunca edite** uma migração que já está no `main`. Mudou de ideia? Migração nova.
3. Escreva o teste primeiro: um teste de integração (Testcontainers) que usa a tabela nova e falha porque ela não existe.
4. Use o modelo abaixo. Comentário no topo diz o porquê das escolhas não óbvias.
5. Rode `scripts/guardas/verificar-repositorio.sh` e `scripts/verificar.sh`.

## Modelo

```sql
-- V00NN — Módulo <X>: <o que a tabela guarda>.
-- <ids de outro módulo não têm FK entre schemas; o que é cifrado e por quê; o que a unicidade garante>

CREATE SCHEMA IF NOT EXISTS <schema>;
GRANT USAGE ON SCHEMA <schema> TO gf_app;

CREATE TABLE <schema>.<plural> (
    id                  uuid        PRIMARY KEY,                       -- Guid.CreateVersion7() no C#
    casal_id            uuid        NOT NULL REFERENCES identidade.casais (id),
    -- dinheiro: sempre bigint em centavos, com CHECK de faixa
    valor_centavos      bigint      NOT NULL CHECK (valor_centavos > 0 AND valor_centavos <= 100000000000),
    -- dado sensível: cifrado (ICofre.Cifrar, AAD 'tabela|coluna|id') + índice cego para busca/unicidade
    descricao_cifrada   bytea       NOT NULL,
    documento_cifrado   bytea       NULL,
    documento_hmac      bytea       NULL,
    criado_em           timestamptz NOT NULL DEFAULT now(),
    atualizado_em       timestamptz NOT NULL DEFAULT now(),
    is_deleted          boolean     NOT NULL DEFAULT false,
    deleted_at          timestamptz NULL,
    deleted_by          uuid        NULL
);

-- Unicidade SEMPRE parcial; nunca UNIQUE na coluna nem no CREATE TABLE.
CREATE UNIQUE INDEX ux_<plural>_documento ON <schema>.<plural> (casal_id, documento_hmac) WHERE NOT is_deleted;
CREATE INDEX ix_<plural>_<coluna> ON <schema>.<plural> (casal_id, <coluna>) WHERE NOT is_deleted;

-- Auditoria, bloqueio de DELETE/TRUNCATE e GRANT SELECT, INSERT, UPDATE para gf_app.
SELECT audit.proteger('<schema>.<plural>');
```

## Não faça

- `DELETE`, `TRUNCATE` ou `GRANT DELETE` — nem em migração de dados. Para "remover", `UPDATE ... SET is_deleted = true`.
- `UNIQUE` sem `WHERE NOT is_deleted`, inclusive como restrição de coluna.
- Coluna em claro para CPF, telefone, número de conta, token, descrição, contraparte ou linha digitável.
- Ler ou referenciar o schema de outro módulo numa consulta (FK para `identidade.casais` é a exceção já estabelecida).
