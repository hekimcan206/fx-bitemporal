#!/bin/sh
# Runs once, when the PostgreSQL data directory is first initialised (docker-entrypoint-initdb.d). The integration tests
# run this exact script, so the least-privilege setup is tested, not assumed.
#   fx_migrator  owns the "fx" and "fx_admin" schemas and runs migrations (DDL).
#   fx_app       is what the service connects as. The migrations narrow its rights on the version table to SELECT,
#                INSERT and UPDATE of known_during only: it can close a version, never edit or delete one.
#   btree_gist   is created here, by the superuser, because the bitemporal exclusion constraint needs "=" on text
#                inside a GiST index and the migrator is deliberately not a superuser.
set -eu

: "${MIGRATOR_PASSWORD:?MIGRATOR_PASSWORD must be set}"
: "${APP_PASSWORD:?APP_PASSWORD must be set}"

psql -v ON_ERROR_STOP=1 \
     -v migrator_password="$MIGRATOR_PASSWORD" \
     -v app_password="$APP_PASSWORD" \
     -v db="$POSTGRES_DB" \
     --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<'SQL'
CREATE ROLE fx_migrator LOGIN PASSWORD :'migrator_password';
CREATE ROLE fx_app LOGIN PASSWORD :'app_password';

REVOKE ALL ON DATABASE :"db" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"db" TO fx_migrator, fx_app;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

CREATE EXTENSION IF NOT EXISTS btree_gist WITH SCHEMA public;

CREATE SCHEMA fx AUTHORIZATION fx_migrator;
CREATE SCHEMA fx_admin AUTHORIZATION fx_migrator;
GRANT USAGE ON SCHEMA fx TO fx_app;
SQL
