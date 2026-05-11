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

-- Passwords come in via PostgreSQL session custom-GUCs set by run.sh
-- before this file is sourced. psql's `:'var'` substitution doesn't reach
-- inside dollar-quoted DO blocks (psql 14+), so we read from session
-- settings instead.
DO $$
DECLARE
    migrator_password text := current_setting('nocturne.migrator_password');
    app_password text := current_setting('nocturne.app_password');
    web_password text := current_setting('nocturne.web_password');
    current_db text := current_database();
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
