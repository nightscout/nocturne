#!/usr/bin/env bash
# Prints a `dotnet test --filter` expression built from tests/quarantine.txt:
#   exclude  every test but the quarantined ones (the blocking run)
#   include  only the quarantined ones (the non-blocking run)
set -euo pipefail

mode=${1:?usage: quarantine.sh exclude|include}
list="$(dirname "$0")/quarantine.txt"

case $mode in
  exclude) op='!='; join='&' ;;
  include) op='='; join='|' ;;
  *) echo "usage: quarantine.sh exclude|include" >&2; exit 2 ;;
esac

sed -e 's/#.*//' -e 's/|.*//' -e 's/[[:space:]]*$//' -e 's/^[[:space:]]*//' "$list" \
  | grep -v '^$' \
  | sed "s/^/FullyQualifiedName$op/" \
  | paste -sd "$join" -
