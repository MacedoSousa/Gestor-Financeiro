-- V0001 — Fundação: auditoria append-only encadeada por hash, bloqueio de DELETE físico e módulo Identidade.
-- Roda como gf_migrator (dono dos objetos). A aplicação conecta como gf_app, sem DELETE nem TRUNCATE.
-- Papéis gf_migrator e gf_app são criados por infra/postgres/init/00-papeis.sh.

CREATE SCHEMA IF NOT EXISTS audit;
CREATE SCHEMA IF NOT EXISTS identidade;

REVOKE ALL ON SCHEMA public FROM PUBLIC;

-- ---------------------------------------------------------------------------
-- Auditoria
-- ---------------------------------------------------------------------------

CREATE TABLE audit.eventos (
    seq            bigserial PRIMARY KEY,
    id             uuid        NOT NULL DEFAULT gen_random_uuid(),
    casal_id       uuid        NULL,
    tabela         text        NOT NULL,
    entidade_id    uuid        NOT NULL,
    acao           text        NOT NULL CHECK (acao IN ('INSERT', 'UPDATE', 'SOFT_DELETE', 'RESTORE')),
    alteracoes     jsonb       NOT NULL,
    usuario_id     uuid        NULL,
    ip             text        NULL,
    ocorrido_em    timestamptz NOT NULL DEFAULT clock_timestamp(),
    hash_anterior  bytea       NULL,
    hash           bytea       NOT NULL
);

CREATE INDEX ix_eventos_casal_seq ON audit.eventos (casal_id, seq DESC);
CREATE INDEX ix_eventos_entidade  ON audit.eventos (tabela, entidade_id);

-- Grava um evento por INSERT/UPDATE. Colunas *_cifrado, *_cifrada e *_hmac entram só como "alterado".
-- SECURITY DEFINER: a aplicação não tem permissão de escrever em audit.eventos diretamente.
CREATE FUNCTION audit.registrar() RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, audit
AS $$
DECLARE
    v_novo     jsonb := to_jsonb(NEW);
    v_antigo   jsonb := CASE WHEN TG_OP = 'UPDATE' THEN to_jsonb(OLD) ELSE '{}'::jsonb END;
    v_alt      jsonb := '{}'::jsonb;
    v_chave    text;
    v_acao     text;
    v_tabela   text := TG_TABLE_SCHEMA || '.' || TG_TABLE_NAME;
    v_casal    uuid := (v_novo ->> 'casal_id')::uuid;
    v_usuario  uuid := nullif(current_setting('app.usuario_id', true), '')::uuid;
    v_ip       text := nullif(current_setting('app.ip', true), '');
    v_anterior bytea;
    v_hash     bytea;
BEGIN
    FOR v_chave IN SELECT jsonb_object_keys(v_novo) LOOP
        CONTINUE WHEN v_chave IN ('criado_em', 'atualizado_em');
        IF TG_OP = 'INSERT' OR (v_novo -> v_chave) IS DISTINCT FROM (v_antigo -> v_chave) THEN
            IF v_chave ~ '_(cifrado|cifrada|hmac)$' THEN
                v_alt := v_alt || jsonb_build_object(v_chave,
                    CASE WHEN v_novo -> v_chave = 'null'::jsonb THEN 'vazio' ELSE 'alterado' END);
            ELSIF TG_OP = 'INSERT' THEN
                v_alt := v_alt || jsonb_build_object(v_chave, v_novo -> v_chave);
            ELSE
                v_alt := v_alt || jsonb_build_object(v_chave, jsonb_build_object('de', v_antigo -> v_chave, 'para', v_novo -> v_chave));
            END IF;
        END IF;
    END LOOP;

    IF TG_OP = 'UPDATE' AND v_alt = '{}'::jsonb THEN
        RETURN NULL;
    END IF;

    v_acao := CASE
        WHEN TG_OP = 'INSERT' THEN 'INSERT'
        WHEN (v_novo ->> 'is_deleted')::boolean AND NOT coalesce((v_antigo ->> 'is_deleted')::boolean, false) THEN 'SOFT_DELETE'
        WHEN NOT (v_novo ->> 'is_deleted')::boolean AND coalesce((v_antigo ->> 'is_deleted')::boolean, false) THEN 'RESTORE'
        ELSE 'UPDATE'
    END;

    IF v_casal IS NULL AND TG_TABLE_NAME = 'casais' THEN
        v_casal := (v_novo ->> 'id')::uuid;
    END IF;

    -- Uma cadeia de hashes por casal; o lock serializa eventos concorrentes do mesmo casal.
    PERFORM pg_advisory_xact_lock(hashtextextended(coalesce(v_casal::text, 'sistema'), 0));

    SELECT e.hash INTO v_anterior
      FROM audit.eventos e
     WHERE e.casal_id IS NOT DISTINCT FROM v_casal
     ORDER BY e.seq DESC
     LIMIT 1;

    v_hash := sha256(
        coalesce(v_anterior, ''::bytea)
        || convert_to(concat_ws('|', v_tabela, v_novo ->> 'id', v_acao, v_alt::text,
                                coalesce(v_usuario::text, ''), coalesce(v_ip, '')), 'UTF8'));

    INSERT INTO audit.eventos (casal_id, tabela, entidade_id, acao, alteracoes, usuario_id, ip, hash_anterior, hash)
    VALUES (v_casal, v_tabela, (v_novo ->> 'id')::uuid, v_acao, v_alt, v_usuario, v_ip, v_anterior, v_hash);

    RETURN NULL;
END;
$$;

CREATE FUNCTION audit.bloquear_delete() RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'Exclusão física proibida em %.%: use soft delete (is_deleted = true).', TG_TABLE_SCHEMA, TG_TABLE_NAME
        USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE FUNCTION audit.bloquear_alteracao() RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'audit.eventos aceita apenas inserção.' USING ERRCODE = 'insufficient_privilege';
END;
$$;

CREATE TRIGGER trg_eventos_imutavel BEFORE UPDATE OR DELETE ON audit.eventos
    FOR EACH ROW EXECUTE FUNCTION audit.bloquear_alteracao();
CREATE TRIGGER trg_eventos_sem_truncate BEFORE TRUNCATE ON audit.eventos
    FOR EACH STATEMENT EXECUTE FUNCTION audit.bloquear_alteracao();

-- Liga auditoria + bloqueio de exclusão e concede à aplicação só SELECT/INSERT/UPDATE.
-- Toda migração que cria tabela chama: SELECT audit.proteger('schema.tabela');
CREATE FUNCTION audit.proteger(p_tabela regclass) RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    EXECUTE format('CREATE TRIGGER trg_auditoria AFTER INSERT OR UPDATE ON %s FOR EACH ROW EXECUTE FUNCTION audit.registrar()', p_tabela);
    EXECUTE format('CREATE TRIGGER trg_sem_delete BEFORE DELETE ON %s FOR EACH ROW EXECUTE FUNCTION audit.bloquear_delete()', p_tabela);
    EXECUTE format('CREATE TRIGGER trg_sem_truncate BEFORE TRUNCATE ON %s FOR EACH STATEMENT EXECUTE FUNCTION audit.bloquear_delete()', p_tabela);
    EXECUTE format('REVOKE ALL ON %s FROM PUBLIC', p_tabela);
    EXECUTE format('GRANT SELECT, INSERT, UPDATE ON %s TO gf_app', p_tabela);
END;
$$;

-- Verificação da cadeia: devolve o primeiro evento cujo hash não confere (nenhuma linha = íntegra).
CREATE FUNCTION audit.verificar_cadeia(p_casal uuid)
RETURNS TABLE (seq bigint, motivo text)
LANGUAGE sql
STABLE
AS $$
    WITH ordenados AS (
        SELECT e.*,
               lag(e.hash) OVER (ORDER BY e.seq) AS hash_esperado_anterior
          FROM audit.eventos e
         WHERE e.casal_id IS NOT DISTINCT FROM p_casal
    )
    SELECT o.seq,
           CASE WHEN o.hash_anterior IS DISTINCT FROM o.hash_esperado_anterior THEN 'elo quebrado'
                ELSE 'hash não confere' END
      FROM ordenados o
     WHERE o.hash_anterior IS DISTINCT FROM o.hash_esperado_anterior
        OR o.hash <> sha256(
               coalesce(o.hash_anterior, ''::bytea)
               || convert_to(concat_ws('|', o.tabela, o.entidade_id::text, o.acao, o.alteracoes::text,
                                       coalesce(o.usuario_id::text, ''), coalesce(o.ip, '')), 'UTF8'))
     ORDER BY o.seq
     LIMIT 1;
$$;

-- ---------------------------------------------------------------------------
-- Identidade
-- ---------------------------------------------------------------------------

CREATE TABLE identidade.casais (
    id             uuid        PRIMARY KEY,
    nome_familia   text        NOT NULL CHECK (length(nome_familia) BETWEEN 1 AND 100),
    criado_em      timestamptz NOT NULL DEFAULT now(),
    atualizado_em  timestamptz NOT NULL DEFAULT now(),
    is_deleted     boolean     NOT NULL DEFAULT false,
    deleted_at     timestamptz NULL,
    deleted_by     uuid        NULL
);

CREATE TABLE identidade.usuarios (
    id                uuid        PRIMARY KEY,
    casal_id          uuid        NOT NULL REFERENCES identidade.casais (id),
    nome_completo     text        NOT NULL CHECK (length(nome_completo) BETWEEN 1 AND 150),
    cpf_cifrado       bytea       NOT NULL,
    cpf_hmac          bytea       NOT NULL,
    telefone_cifrado  bytea       NULL,
    telefone_hmac     bytea       NULL,
    conjuge_id        uuid        NULL REFERENCES identidade.usuarios (id),
    criado_em         timestamptz NOT NULL DEFAULT now(),
    atualizado_em     timestamptz NOT NULL DEFAULT now(),
    is_deleted        boolean     NOT NULL DEFAULT false,
    deleted_at        timestamptz NULL,
    deleted_by        uuid        NULL,
    CONSTRAINT ck_usuarios_conjuge_diferente CHECK (conjuge_id IS NULL OR conjuge_id <> id)
);

CREATE UNIQUE INDEX ux_usuarios_cpf ON identidade.usuarios (cpf_hmac) WHERE NOT is_deleted;
CREATE UNIQUE INDEX ux_usuarios_telefone ON identidade.usuarios (telefone_hmac) WHERE NOT is_deleted AND telefone_hmac IS NOT NULL;
CREATE INDEX ix_usuarios_casal ON identidade.usuarios (casal_id) WHERE NOT is_deleted;

-- Envelopes da DEK do casal (senha de cada cônjuge, recuperação, serviço). Ver ADR-005.
CREATE TABLE identidade.chaves (
    id             uuid        PRIMARY KEY,
    casal_id       uuid        NOT NULL REFERENCES identidade.casais (id),
    tipo           text        NOT NULL CHECK (tipo IN ('USUARIO', 'RECUPERACAO', 'SERVICO')),
    usuario_id     uuid        NULL REFERENCES identidade.usuarios (id),
    dek_cifrada    bytea       NOT NULL,
    salt           bytea       NULL,
    versao         integer     NOT NULL DEFAULT 1,
    criado_em      timestamptz NOT NULL DEFAULT now(),
    atualizado_em  timestamptz NOT NULL DEFAULT now(),
    is_deleted     boolean     NOT NULL DEFAULT false,
    deleted_at     timestamptz NULL,
    deleted_by     uuid        NULL,
    CONSTRAINT ck_chaves_usuario CHECK ((tipo = 'USUARIO') = (usuario_id IS NOT NULL))
);

CREATE UNIQUE INDEX ux_chaves_ativa ON identidade.chaves (casal_id, tipo, coalesce(usuario_id, '00000000-0000-0000-0000-000000000000'::uuid)) WHERE NOT is_deleted;

SELECT audit.proteger('identidade.casais');
SELECT audit.proteger('identidade.usuarios');
SELECT audit.proteger('identidade.chaves');

-- ---------------------------------------------------------------------------
-- Permissões da aplicação
-- ---------------------------------------------------------------------------

GRANT USAGE ON SCHEMA audit, identidade TO gf_app;
GRANT SELECT ON audit.eventos TO gf_app;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON audit.eventos FROM gf_app, PUBLIC;
GRANT EXECUTE ON FUNCTION audit.verificar_cadeia(uuid) TO gf_app;
REVOKE EXECUTE ON FUNCTION audit.proteger(regclass) FROM PUBLIC;
