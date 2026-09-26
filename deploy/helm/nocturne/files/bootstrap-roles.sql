-- Nocturne PostgreSQL role bootstrap (Helm chart copy)
-- =====================================================
--
-- This is a chart-local copy of docs/postgres/bootstrap-roles.sql, modified
-- to read passwords from psql variables (`-v migrator_password=...`) instead
-- of hardcoded REPLACE_ME placeholders. The chart's bootstrap Job runs this
-- against the target database with admin credentials.
--
-- Three roles are created (idempotent):
--   nocturne_migrator  Owns schema, runs migrations. NOBYPASSRLS.
--   nocturne_app       API runtime, owns nothing, NOBYPASSRLS — load-bearing
--                      for tenant isolation via FORCE ROW LEVEL SECURITY.
--   nocturne_web       SvelteKit bot-framework state (chat_state_* tables).
--                      Owns those tables; not tenant-scoped, no PHI.
--
-- See https://github.com/nightscout/nocturne/blob/main/docs/postgres/bootstrap-roles.sql
-- for the canonical version and full security rationale.

\set ON_ERROR_STOP on

-- Passwords arrive via psql -v variables. Unlike the compose bundle and
-- docs/postgres/container-init/00-init.sh, which use :'var' directly in the
-- DDL, this copy needs a DO block for idempotency (CREATE vs ALTER), and
-- psql's :'var' substitution doesn't reach inside dollar-quoted bodies. So the
-- values are handed over through session custom-GUCs set here, outside the
-- block; psql's :'var' quoting makes that safe for any embedded quote, dollar
-- sign, backslash or newline.
--
-- set_config() returns the value it sets, so each result is discarded with
-- \g /dev/null -- a bare `SELECT set_config(...);` prints the plaintext
-- password to the Job's pod logs.
SELECT set_config('nocturne.migrator_password', :'migrator_password', false) \g /dev/null
SELECT set_config('nocturne.app_password',      :'app_password',      false) \g /dev/null
SELECT set_config('nocturne.web_password',      :'web_password',      false) \g /dev/null

DO $$
DECLARE
    migrator_password text := current_setting('nocturne.migrator_password');
    app_password text := current_setting('nocturne.app_password');
    web_password text := current_setting('nocturne.web_password');
    current_db text := current_database();
    err_state text;
    err_message text;
    err_detail text;
BEGIN
    -- The role DDL below carries each password as a PASSWORD '...' literal,
    -- and an error raised by it would repeat that statement: as CONTEXT, and
    -- as QUERY/LINE for errors with a position, both in psql's output (the
    -- Job's pod logs, on every retry) and in the server log. Catch it and
    -- re-raise the same error without the statement.
    BEGIN
        -- nocturne_migrator: owns the schema, runs migrations
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nocturne_migrator') THEN
            EXECUTE format(
                'CREATE ROLE nocturne_migrator LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD %L',
                migrator_password);
        ELSE
            EXECUTE format(
                'ALTER ROLE nocturne_migrator LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD %L',
                migrator_password);
        END IF;

        -- nocturne_app: runtime-only, owns nothing
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nocturne_app') THEN
            EXECUTE format(
                'CREATE ROLE nocturne_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD %L',
                app_password);
        ELSE
            EXECUTE format(
                'ALTER ROLE nocturne_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD %L',
                app_password);
        END IF;

        -- nocturne_web: SvelteKit bot-framework state storage.
        IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nocturne_web') THEN
            EXECUTE format(
                'CREATE ROLE nocturne_web LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD %L',
                web_password);
        ELSE
            EXECUTE format(
                'ALTER ROLE nocturne_web LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD %L',
                web_password);
        END IF;
    EXCEPTION WHEN OTHERS THEN
        GET STACKED DIAGNOSTICS
            err_state = RETURNED_SQLSTATE,
            err_message = MESSAGE_TEXT,
            err_detail = PG_EXCEPTION_DETAIL;
        IF err_detail <> '' THEN
            RAISE EXCEPTION USING ERRCODE = err_state, MESSAGE = err_message, DETAIL = err_detail;
        END IF;
        RAISE EXCEPTION USING ERRCODE = err_state, MESSAGE = err_message;
    END;

    -- Hand ownership of the database and public schema to the migrator
    EXECUTE format('ALTER DATABASE %I OWNER TO nocturne_migrator', current_db);
    EXECUTE 'ALTER SCHEMA public OWNER TO nocturne_migrator';

    -- Runtime role: connect + use schema, nothing more
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO nocturne_app', current_db);
    EXECUTE 'GRANT USAGE ON SCHEMA public TO nocturne_app';

    -- Web role: needs CREATE on public for chat_state_* tables
    EXECUTE format('GRANT CONNECT ON DATABASE %I TO nocturne_web', current_db);
    EXECUTE 'GRANT USAGE, CREATE ON SCHEMA public TO nocturne_web';

    -- Default privileges so future migrator-created tables grant CRUD to app
    EXECUTE 'ALTER DEFAULT PRIVILEGES FOR ROLE nocturne_migrator IN SCHEMA public '
         || 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nocturne_app';
    EXECUTE 'ALTER DEFAULT PRIVILEGES FOR ROLE nocturne_migrator IN SCHEMA public '
         || 'GRANT USAGE, SELECT ON SEQUENCES TO nocturne_app';

    -- Grant on existing objects (no-op on fresh DBs)
    EXECUTE 'GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO nocturne_app';
    EXECUTE 'GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO nocturne_app';
END
$$;
