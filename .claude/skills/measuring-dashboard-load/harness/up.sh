#!/usr/bin/env bash
# Usage: up.sh <main|branch>  - fresh e2e stack on the :perf-<variant> images, Caddy edge, 90-day 'perf' tenant.
set -euo pipefail
V=$1
HARNESS="$(cd "$(dirname "$0")" && pwd)"
cd "$(cd "$HARNESS/../../../.." && pwd)"
export PERF_HARNESS_DIR="$(cygpath -m "$HARNESS" 2>/dev/null || echo "$HARNESS")"
export NOCTURNE_API_IMAGE=nocturne-api:perf-$V NOCTURNE_WEB_IMAGE=nocturne-web:perf-$V
C="docker compose -p nocturne-perf -f e2e/docker-compose.yml -f $PERF_HARNESS_DIR/compose.perf.yml"
$C down --volumes --remove-orphans --timeout 5 >/dev/null 2>&1 || true
$C up -d --wait --wait-timeout 300 --force-recreate --renew-anon-volumes --remove-orphans
curl -sf -X POST http://127.0.0.1:1630/api/v4/dev-only/admin/seed-tenant -H 'content-type: application/json'   -d '{"slug":"perf","displayName":"Perf","ownerUsername":"dev","sampleData":true,"sampleDataDays":90}' -o /dev/null -w 'seed HTTP %{http_code}\n'
