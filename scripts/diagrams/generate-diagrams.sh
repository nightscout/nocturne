#!/usr/bin/env bash
# scripts/diagrams/generate-diagrams.sh — Regenerate auto-produced mermaid sources.
#
# Mermaid sources (.mmd) are inlined as fenced code blocks in the OpenAPI
# spec; Scalar's docs page lazy-loads mermaid.esm and renders them in the
# browser. No SVG output step is required.
#
# Prerequisites:
#   - dotnet tool restore (installs Dependify.Cli)
#   - EfToMermaid tool project built
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
DIAGRAMS_DIR="$REPO_ROOT/docs/diagrams"
MANIFEST="$DIAGRAMS_DIR/diagrams.yaml"
DIAGRAMGEN="$REPO_ROOT/tools/Nocturne.Tools.DiagramGen"

echo "==> Generating auto diagrams"

# --- Dependify: project dependency graph ---
echo "    Generating project dependency graph"
dotnet dependify graph scan "$REPO_ROOT" \
  --format mermaid \
  > "$DIAGRAMS_DIR/project-dependencies.mmd"

# --- EfToMermaid: all efcore entries from manifest ---
# Parse manifest for entries with "auto: efcore", extract source and optional module.
echo "    Generating EF Core diagrams"

# Use awk to extract source/module pairs for efcore entries
awk '
  /^  - source:/ { source = $NF }
  /auto: efcore/ { efcore = 1 }
  /module:/ { module = $NF }
  /^  - source:/ && efcore && source {
    # Emit previous entry when we hit the next entry
  }
  /^$/ || /^  - source:/ {
    if (efcore && source) {
      print source ":" module
    }
    efcore = 0; module = ""
  }
  END {
    if (efcore && source) {
      print source ":" module
    }
  }
' "$MANIFEST" | while IFS=: read -r source module; do
  output_mmd="$DIAGRAMS_DIR/$source"

  if [[ -n "$module" ]]; then
    echo "      $source (module: $module)"
    dotnet run --project "$DIAGRAMGEN" --no-launch-profile -- "$output_mmd" --module "$module"
  else
    echo "      $source (full model)"
    dotnet run --project "$DIAGRAMGEN" --no-launch-profile -- "$output_mmd"
  fi
done

# --- Verify every manifest entry has a backing .mmd source ---
echo "==> Verifying diagram sources"

grep "source:" "$MANIFEST" | sed 's/.*source: *//' | while read -r source; do
  input="$DIAGRAMS_DIR/$source"
  if [[ ! -f "$input" ]]; then
    echo "ERROR: Diagram source not found: $input" >&2
    exit 1
  fi
done

echo "==> Diagrams complete. Sources: $DIAGRAMS_DIR/"
ls "$DIAGRAMS_DIR/"*.mmd
