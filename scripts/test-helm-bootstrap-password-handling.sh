#!/usr/bin/env bash
# End-to-end test for the Helm bootstrap Job's password handling.
#
# Renders the chart with `helm template`, takes run.sh and bootstrap-roles.sql
# from the rendered ConfigMap and the image from the rendered Job, then runs
# them the way the Job does (/scripts mounted read-only, uid 1000, read-only
# root filesystem) against a real PostgreSQL container. It asserts:
#
#   - passwords containing quotes, dollar signs, backslashes, psql variable
#     syntax and a trailing newline are stored byte-for-byte: each role
#     authenticates with its exact password, and not with a trimmed one
#   - no password ever appears in the Job's output (stdout + stderr), on
#     success or on failure -- that output is what `kubectl logs` shows --
#     nor in the Postgres server log. Failures covered: admin without
#     CREATEROLE, a password policy rejecting a password, an error with a
#     position inside the role DDL; plus a psqlrc that echoes queries
#   - re-running with rotated passwords (pre-upgrade) takes the ALTER path:
#     new passwords work, old ones stop working
#   - the roles get the attributes and grants the rest of the stack relies on
#   - a failing bootstrap exits non-zero
#
# Requires docker and helm. Run from anywhere: bash scripts/test-helm-bootstrap-password-handling.sh
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
chart="$repo_root/deploy/helm/nocturne"

work="$(mktemp -d)"
id="nocturne-bootstrap-test-$$"
net="$id" pg="$id-pg"
cleanup() {
  docker rm -f "$pg" >/dev/null 2>&1 || true
  docker network rm "$net" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

# --- Render the chart --------------------------------------------------------

if ! ls "$chart"/charts/*/Chart.yaml >/dev/null 2>&1; then
  (cd "$chart" && helm dependency build . >/dev/null && for tgz in charts/*.tgz; do tar xzf "$tgz" -C charts/; done)
fi

# External-DB mode renders the bootstrap without the subchart's Secret helpers;
# the ConfigMap and the Job's container spec are identical in both modes.
helm template t "$chart" \
  --set baseDomain=nocturne.example.com \
  --set instanceKey.existingSecret=k \
  --set externalDatabase.host=db \
  --set bootstrap.adminSecret.existingSecret=admin \
  --set externalDatabase.appSecret.existingSecret=app \
  --set externalDatabase.migratorSecret.existingSecret=migrator \
  --set externalDatabase.webSecret.existingSecret=web \
  --show-only templates/bootstrap-configmap.yaml \
  --show-only templates/bootstrap-job.yaml > "$work/rendered.yaml"

# Pull a `  <key>: |-` block scalar out of the rendered ConfigMap.
extract() {
  awk -v key="  $1: |-" '
    $0 == key { on = 1; next }
    on && /^    / { sub(/^    /, ""); print; next }
    on && /^$/ { print; next }
    on { exit }
  ' "$work/rendered.yaml"
}
mkdir -p "$work/scripts"
extract run.sh > "$work/scripts/run.sh"
extract bootstrap-roles.sql > "$work/scripts/bootstrap-roles.sql"
test -s "$work/scripts/run.sh" || fail "run.sh not found in rendered ConfigMap"
test -s "$work/scripts/bootstrap-roles.sql" || fail "bootstrap-roles.sql not found in rendered ConfigMap"
# The ConfigMap is mounted with defaultMode 0555 for uid 1000.
chmod 755 "$work" "$work/scripts"
chmod 555 "$work/scripts"/*

image="$(awk '/^kind: Job/ { job = 1 } job && $1 == "image:" { print $2; exit }' "$work/rendered.yaml")"
test -n "$image" || fail "bootstrap image not found in rendered Job"

# --- Postgres ----------------------------------------------------------------

docker network create "$net" >/dev/null
docker run -d --name "$pg" --network "$net" \
  -e POSTGRES_PASSWORD=admin -e POSTGRES_DB=nocturne "$image" >/dev/null
# The image's init phase listens on the unix socket only, so a TCP probe
# passes only once the real server is up.
for _ in $(seq 60); do
  docker exec "$pg" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1 && break
  sleep 1
done
docker exec "$pg" pg_isready -h 127.0.0.1 -U postgres >/dev/null || fail "postgres did not start"

# Runs the rendered run.sh the way the Job does. Output lands in $work/out.
scripts_dir="$work/scripts"
extra_env=()
run_bootstrap() { # admin_user admin_password migrator app web
  set +e
  docker run --rm --network "$net" --user 1000:1000 --read-only \
    -v "$scripts_dir:/scripts:ro" \
    -e PGHOST="$pg" -e PGPORT=5432 -e PGDATABASE=nocturne \
    -e PGUSER="$1" -e PGPASSWORD="$2" \
    -e MIGRATOR_PASSWORD="$3" -e APP_PASSWORD="$4" -e WEB_PASSWORD="$5" \
    ${extra_env[@]+"${extra_env[@]}"} \
    "$image" /bin/sh /scripts/run.sh > "$work/out" 2>&1
  status=$?
  set -e
}

# Output must not contain any password, whole or as its distinctive core.
assert_no_leak() { # label password...
  local label="$1"; shift
  for pw in "$@"; do
    core="${pw//[$'\n']/}"
    if grep -qF -e "$core" "$work/out"; then
      echo "--- bootstrap output ($label) ---" >&2; cat "$work/out" >&2
      fail "$label: a password appears in the bootstrap Job output"
    fi
  done
  # Every test password carries this marker, so a partial echo is caught too.
  if grep -q CANARY "$work/out"; then
    echo "--- bootstrap output ($label) ---" >&2; cat "$work/out" >&2
    fail "$label: part of a password appears in the bootstrap Job output"
  fi
}

# Connects over TCP from inside the network: the image trusts loopback, so
# this is the path that actually checks the password (scram-sha-256).
can_login() { # role password
  docker exec -e PGPASSWORD="$2" "$pg" \
    psql -h "$pg" -U "$1" -d nocturne -XtAc 'select current_user' >/dev/null 2>&1
}
assert_login() { can_login "$1" "$2" || fail "$3: $1 cannot log in with its password"; }
assert_no_login() { ! can_login "$1" "$2" || fail "$3: $1 logs in with a password it should reject"; }

sql() { docker exec "$pg" psql -U postgres -d nocturne -XtAc "$1"; }

# --- 1. Fresh install ---------------------------------------------------------

mig1="CANARY-m'ig\"; DROP ROLE postgres; --"
# shellcheck disable=SC1003,SC2016 # the literal $, quotes and backslash are the point
app1='CANARY-a$$p:'"'"'app_password'"'"'\x\\'
web1=$'CANARY-web $1 \x07\n'     # trailing newline, as `echo pw | kubectl create secret` leaves

run_bootstrap postgres admin "$mig1" "$app1" "$web1"
[ "$status" -eq 0 ] || { cat "$work/out" >&2; fail "fresh install: bootstrap exited $status"; }
assert_no_leak "fresh install" "$mig1" "$app1" "$web1"
grep -qx DO "$work/out" || { cat "$work/out" >&2; fail "fresh install: DO block did not run"; }

assert_login nocturne_migrator "$mig1" "fresh install"
assert_login nocturne_app "$app1" "fresh install"
assert_login nocturne_web "$web1" "fresh install"
assert_no_login nocturne_web "${web1%$'\n'}" "fresh install (trailing newline must be kept)"
assert_no_login nocturne_app "wrong" "fresh install (sanity: auth is enforced)"
[ "$(sql "select count(*) from pg_roles where rolname = 'postgres'")" = 1 ] \
  || fail "fresh install: injected SQL ran"

[ "$(sql "select string_agg(rolname || ':' || rolsuper::int || rolbypassrls::int || rolcreaterole::int || rolcreatedb::int || rolcanlogin::int, ',' order by rolname)
          from pg_roles where rolname like 'nocturne\_%'")" \
  = "nocturne_app:00001,nocturne_migrator:00001,nocturne_web:00001" ] \
  || fail "fresh install: unexpected role attributes"
[ "$(sql "select pg_get_userbyid(datdba) from pg_database where datname = 'nocturne'")" = nocturne_migrator ] \
  || fail "fresh install: database not owned by nocturne_migrator"
[ "$(sql "select pg_get_userbyid(nspowner) from pg_namespace where nspname = 'public'")" = nocturne_migrator ] \
  || fail "fresh install: public schema not owned by nocturne_migrator"
[ "$(sql "select has_database_privilege('nocturne_app', 'nocturne', 'CONNECT')
          and has_schema_privilege('nocturne_app', 'public', 'USAGE')
          and not has_schema_privilege('nocturne_app', 'public', 'CREATE')
          and has_schema_privilege('nocturne_web', 'public', 'CREATE')")" = t ] \
  || fail "fresh install: unexpected grants"

# --- 2. Re-run with rotated passwords (pre-upgrade hook) ----------------------

mig2="CANARY-rotated-m\$ig'"
app2='CANARY-rotated-a\p"p'
web2="CANARY-rotated-web"

run_bootstrap postgres admin "$mig2" "$app2" "$web2"
[ "$status" -eq 0 ] || { cat "$work/out" >&2; fail "re-run: bootstrap exited $status"; }
assert_no_leak "re-run" "$mig2" "$app2" "$web2"
assert_login nocturne_migrator "$mig2" "re-run"
assert_login nocturne_app "$app2" "re-run"
assert_login nocturne_web "$web2" "re-run"
assert_no_login nocturne_migrator "$mig1" "re-run (old password must stop working)"
assert_no_login nocturne_app "$app1" "re-run (old password must stop working)"
assert_no_login nocturne_web "$web1" "re-run (old password must stop working)"

# --- 2b. A psqlrc that echoes queries (e.g. baked into a custom image) -------

printf '%s\n' '\set ECHO queries' > "$work/scripts/psqlrc"
chmod 444 "$work/scripts/psqlrc"
extra_env=(-e PSQLRC=/scripts/psqlrc)
run_bootstrap postgres admin "$mig2" "$app2" "$web2"
extra_env=()
[ "$status" -eq 0 ] || { cat "$work/out" >&2; fail "psqlrc: bootstrap exited $status"; }
assert_no_leak "psqlrc" "$mig2" "$app2" "$web2"

# --- 3. Failing bootstrap: admin without CREATEROLE ---------------------------
# A failing statement inside the DO block carries the password literal; the
# error must not print it, and the Job must fail.

sql "create role weak_admin login password 'weak'" >/dev/null
mig3="CANARY-failing-mig" app3="CANARY-failing-app" web3="CANARY-failing-web"

run_bootstrap weak_admin weak "$mig3" "$app3" "$web3"
[ "$status" -ne 0 ] || { cat "$work/out" >&2; fail "failing bootstrap: exited 0"; }
grep -q "ERROR:  permission denied" "$work/out" \
  || { cat "$work/out" >&2; fail "failing bootstrap: did not fail the way this test expects"; }
assert_no_leak "failing bootstrap" "$mig3" "$app3" "$web3"
assert_login nocturne_migrator "$mig2" "failing bootstrap (existing password must be untouched)"

# --- 4. Failing bootstrap: password policy rejects a password ----------------
# Managed Postgres often enforces one. passwordcheck rejects < 8 characters.

sql "alter role postgres set session_preload_libraries = 'passwordcheck'" >/dev/null
run_bootstrap postgres admin "CANARY1" "$app2" "$web2"
sql "alter role postgres reset session_preload_libraries" >/dev/null
[ "$status" -ne 0 ] || { cat "$work/out" >&2; fail "password policy: exited 0"; }
grep -q "ERROR:  password is too short" "$work/out" \
  || { cat "$work/out" >&2; fail "password policy: did not fail the way this test expects"; }
assert_no_leak "password policy" "CANARY1" "$app2" "$web2"

# --- 5. Failing bootstrap: error with a position inside the role DDL ----------
# The shipped DDL doesn't hit one on PostgreSQL 17, but a server that rejects
# one of its options would report QUERY/LINE, i.e. the statement with its
# password literal. Simulate that by breaking one statement.

mkdir -p "$work/broken"
chmod 755 "$work/broken"
sed 's/NOCREATEROLE PASSWORD %L/NOCREATEROLE BOGUS PASSWORD %L/' "$work/scripts/bootstrap-roles.sql" > "$work/broken/bootstrap-roles.sql"
cp "$work/scripts/run.sh" "$work/broken/run.sh"
chmod 555 "$work/broken"/*
scripts_dir="$work/broken"
run_bootstrap postgres admin "CANARY-positional-mig" "CANARY-positional-app" "CANARY-positional-web"
scripts_dir="$work/scripts"
[ "$status" -ne 0 ] || { cat "$work/out" >&2; fail "positional error: exited 0"; }
grep -q 'ERROR:  unrecognized role option "bogus"' "$work/out" \
  || { cat "$work/out" >&2; fail "positional error: did not fail the way this test expects"; }
assert_no_leak "positional error" "CANARY-positional-mig" "CANARY-positional-app" "CANARY-positional-web"
assert_login nocturne_migrator "$mig2" "positional error (existing password must be untouched)"

# --- 6. Missing password: fails before touching the database -----------------

run_bootstrap postgres admin "CANARY-m" "" "CANARY-w"
[ "$status" -ne 0 ] || fail "missing password: exited 0"
grep -q "APP_PASSWORD is required" "$work/out" || { cat "$work/out" >&2; fail "missing password: wrong error"; }
assert_login nocturne_app "$app2" "missing password (existing password must be untouched)"

# --- Server log ---------------------------------------------------------------
# The failures above were each logged by the server too.

docker logs "$pg" > "$work/server.log" 2>&1
if grep CANARY "$work/server.log" >&2; then
  fail "a password appears in the Postgres server log"
fi

echo "OK: helm bootstrap password handling (fresh install, rotation, failures, missing password) against $image"
