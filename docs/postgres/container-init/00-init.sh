#!/bin/bash
# Nocturne PostgreSQL init script (container bootstrap)
# =====================================================
#
# This script runs automatically on first container start (via the standard
# /docker-entrypoint-initdb.d/ mechanism) for the Aspire dev stack and the
# self-hosted docker-compose bundle. It creates the nocturne_migrator and
# nocturne_app roles that Nocturne requires.
#
# This runs only against a fresh data directory, so the SQL does not need to
# be idempotent. For bring-your-own-PostgreSQL deployments where you need
# idempotency against an existing database, use bootstrap-roles.sql instead.
#
# Expected environment variables (set by the compose/AppHost that owns the
# Postgres container):
#
#   POSTGRES_DB                  database name (set by the Postgres image)
#   POSTGRES_USER                superuser name (set by the Postgres image)
#   NOCTURNE_MIGRATOR_PASSWORD   password for nocturne_migrator
#   NOCTURNE_APP_PASSWORD        password for nocturne_app
#   NOCTURNE_WEB_PASSWORD        password for nocturne_web (bot state)
#
# Passwords are passed to psql via -v variables (not shell interpolation
# inside the SQL text) so that values containing quotes or dollar signs are
# handled safely by psql's :'var' quoting.

set -euo pipefail

: "${POSTGRES_DB:?POSTGRES_DB must be set}"
: "${POSTGRES_USER:?POSTGRES_USER must be set}"
: "${NOCTURNE_MIGRATOR_PASSWORD:?NOCTURNE_MIGRATOR_PASSWORD must be set}"
: "${NOCTURNE_APP_PASSWORD:?NOCTURNE_APP_PASSWORD must be set}"
: "${NOCTURNE_WEB_PASSWORD:?NOCTURNE_WEB_PASSWORD must be set}"

psql \
    --username "$POSTGRES_USER" \
    --dbname "$POSTGRES_DB" \
    --set ON_ERROR_STOP=on \
    --set DBNAME="$POSTGRES_DB" \
    --set migrator_password="$NOCTURNE_MIGRATOR_PASSWORD" \
    --set app_password="$NOCTURNE_APP_PASSWORD" \
    --set web_password="$NOCTURNE_WEB_PASSWORD" \
    <<'SQL'
CREATE ROLE nocturne_migrator LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD :'migrator_password';
CREATE ROLE nocturne_app      LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD :'app_password';
CREATE ROLE nocturne_web      LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE PASSWORD :'web_password';

ALTER DATABASE :"DBNAME" OWNER TO nocturne_migrator;
ALTER SCHEMA public OWNER TO nocturne_migrator;

GRANT CONNECT ON DATABASE :"DBNAME" TO nocturne_app;
GRANT USAGE ON SCHEMA public TO nocturne_app;

-- nocturne_web: owns its own chat_state_* tables (created on first run by
-- @chat-adapter/state-pg in the SvelteKit app). Needs CREATE on public. It
-- does NOT get default privileges on migrator-owned tables, so even a bug
-- in the web layer can't touch Nocturne's tenant tables.
GRANT CONNECT ON DATABASE :"DBNAME" TO nocturne_web;
GRANT USAGE, CREATE ON SCHEMA public TO nocturne_web;

ALTER DEFAULT PRIVILEGES FOR ROLE nocturne_migrator IN SCHEMA public
    GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO nocturne_app;
ALTER DEFAULT PRIVILEGES FOR ROLE nocturne_migrator IN SCHEMA public
    GRANT USAGE, SELECT ON SEQUENCES TO nocturne_app;
SQL
