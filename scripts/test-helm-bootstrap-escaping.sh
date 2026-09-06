#!/usr/bin/env bash
# Regression test for the Helm bootstrap Job's password handling.
#
# run.sh passes role passwords to psql via -v variables (psql :'var' quoting),
# matching the compose bundle pattern: no shell interpolation into SQL text
# and no plaintext temp files. This test extracts run.sh verbatim from the
# Helm template, runs it against mock pg_isready/psql binaries, and asserts:
#   - passwords reach psql ONLY as -v arguments (never interpolated into SQL)
#   - no temp SQL file is created (no mktemp, no plaintext on disk)
#   - the bootstrap SQL is read from ${SCRIPTS_DIR:-/scripts} (configurable,
#     no sudo / host writes needed)
# Coverage includes single quotes, dollar signs, backslashes and a newline
# in the password values.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
template="$repo_root/deploy/helm/nocturne/templates/bootstrap-configmap.yaml"
roles_sql="$repo_root/deploy/helm/nocturne/files/bootstrap-roles.sql"

work="$(mktemp -d)"   # ephemeral; OS cleans /tmp
# Extract run.sh verbatim (it is a literal block scalar in the template, so
# this does not need Helm to render).
awk '/^  run\.sh: \|-$/{flag=1;next} /^\{\{- end \}\}/{flag=0} flag{sub(/^    /,""); print}' \
  "$template" > "$work/run.sh"
test -s "$work/run.sh"
grep -q 'psql -v ON_ERROR_STOP=1' "$work/run.sh"
if grep -qF -e 'mktemp' -e 'escape_sql_literal' -e "trap '" -e 'trap "' "$work/run.sh"; then
  echo "FAIL: run.sh must not use temp files or manual escaping" >&2
  exit 1
fi

# Mock pg_isready (always healthy) and psql (records argv and the target SQL
# file's contents so we can assert how passwords were passed).
mkdir -p "$work/bin" "$work/scripts" "$work/out"
printf '%s\n' '#!/bin/sh' 'exit 0' > "$work/bin/pg_isready"
cat > "$work/bin/psql" <<'MOCK'
#!/bin/sh
prev=""
n=0
for a in "$@"; do
  case "$prev" in
    -v) case "$a" in ON_ERROR_STOP=*) ;; *) printf '%s\n' "$a" > "$PSQL_OUT/${a%%=*}.val" ;; esac ;;
    -f) cat "$a" > "$PSQL_OUT/sqlbody.txt" ;;
  esac
  prev="$a"
done
MOCK
chmod +x "$work/bin/pg_isready" "$work/bin/psql"
cp "$roles_sql" "$work/scripts/bootstrap-roles.sql"

# Note: psql's actual :'var' quoting is a psql feature; the mock records the
# raw -v values. Correctness of :'var' handling itself is Postgres' contract
# (same pattern as deploy/docker-compose/docker-compose.yaml).
PSQL_OUT="$work/out" PATH="$work/bin:$PATH" \
  PGHOST=localhost PGPORT=5432 PGDATABASE=nocturne PGUSER=postgres PGPASSWORD=admin \
  SCRIPTS_DIR="$work/scripts" \
  MIGRATOR_PASSWORD="mig'ra\$tor\\x" APP_PASSWORD="ap'p\$" WEB_PASSWORD="$(printf 'we\nb')" \
  /bin/sh "$work/run.sh" >/dev/null 2>&1

# Passwords passed via -v, values preserved exactly (quote, dollar, backslash,
# newline). run.sh passes them verbatim; $( )-style stripping does not apply
# because we assert the full argv values including the embedded newline.
grep -Fqx "migrator_password=mig'ra\$tor\\x" "$work/out/migrator_password.val"
grep -Fqx "app_password=ap'p\$" "$work/out/app_password.val"
printf 'web_password=we\nb\n' | cmp -s - "$work/out/web_password.val"

# The SQL file passed via -f is the chart's bootstrap-roles.sql (configurable
# SCRIPTS_DIR honoured), unmodified — passwords never written into it.
grep -q "set_config('nocturne.migrator_password', :'migrator_password', false);" "$work/out/sqlbody.txt"
grep -q "set_config('nocturne.app_password',      :'app_password',      false);" "$work/out/sqlbody.txt"
grep -q "set_config('nocturne.web_password',      :'web_password',      false);" "$work/out/sqlbody.txt"
if grep -qE "mig'ra|ap'p|we.b" "$work/out/sqlbody.txt"; then
  echo "FAIL: passwords leaked into the SQL file (plaintext)" >&2
  exit 1
fi

echo "OK: bootstrap password handling regression test passed (psql -v, no temp files, SCRIPTS_DIR honoured)."
