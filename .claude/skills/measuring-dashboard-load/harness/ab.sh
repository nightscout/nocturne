#!/usr/bin/env bash
# Usage: ab.sh <variantA> <variantB> [rounds]  - alternating fresh stacks; results/<variant>-r<n>.json
set -uo pipefail
HARNESS="$(cd "$(dirname "$0")" && pwd)"; ROOT="$(cd "$HARNESS/../../../.." && pwd)"
export PERF_HARNESS_DIR="$(cygpath -m "$HARNESS" 2>/dev/null || echo "$HARNESS")"
cd "$ROOT"; mkdir -p "$HARNESS/results"
A=$1; B=$2; R=${3:-2}
for round in $(seq 1 $R); do for v in $A $B; do
  echo "=== round $round $v"
  bash "$HARNESS/up.sh" $v 2>&1 | tail -1
  sleep 20  # let the seeded tenant's background work settle
  node "$HARNESS/bench.cjs" "$v${SUFFIX:-}-r$round" > "$HARNESS/results/bench-$v${SUFFIX:-}-r$round.json" 2>/dev/null || echo "bench failed"
  node "$HARNESS/perf.cjs" "$v${SUFFIX:-}-r$round" 5 > "$HARNESS/results/perf-$v${SUFFIX:-}-r$round.json"
done; done
docker compose -p nocturne-perf -f e2e/docker-compose.yml -f "$PERF_HARNESS_DIR/compose.perf.yml" down --volumes --timeout 5 >/dev/null 2>&1
echo AB-DONE
