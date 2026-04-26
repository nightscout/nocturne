#!/usr/bin/env bash
# scripts/build.sh — Build Nocturne containers locally or in CI.
#
# Usage:
#   ./scripts/build.sh                    # build with tag "dev", no push
#   ./scripts/build.sh v1.2.3             # build with tag "v1.2.3", no push
#   ./scripts/build.sh latest --push      # build and push with tag "latest"
#
# Environment variables (optional):
#   REGISTRY          Container registry   (default: ghcr.io)
#   IMAGE_REPOSITORY  Image repository     (default: detected from git remote)
#   CONTAINER_RID     .NET RID for API container (default: auto-detected, e.g. linux-x64)
#   SKIP_API          Skip API container    (default: false)
#   SKIP_WEB          Skip Web container    (default: false)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

VERSION="${1:-dev}"
PUSH=""
if [[ "${2:-}" == "--push" ]]; then
  PUSH="--push"
fi

REGISTRY="${REGISTRY:-ghcr.io}"
if [[ -z "${IMAGE_REPOSITORY:-}" ]]; then
  IMAGE_REPOSITORY=$(git -C "$REPO_ROOT" remote get-url origin | sed -E 's|.*github\.com[:/]||;s|\.git$||' | tr '[:upper:]' '[:lower:]')
fi

echo "==> Build configuration"
echo "    Version:    $VERSION"
echo "    Registry:   $REGISTRY"
echo "    Repository: $IMAGE_REPOSITORY"
echo "    Push:       ${PUSH:-no}"
echo ""

# ---------------------------------------------------------------------------
# Step 1: Prepare — appsettings, restore, web dependencies
# ---------------------------------------------------------------------------
echo "==> Preparing build environment"
cd "$REPO_ROOT"

echo "    Restoring .NET dependencies"
dotnet restore --verbosity quiet

echo "    Installing web dependencies"
(cd src/Web && pnpm install --frozen-lockfile)

echo "    Building bridge package"
(cd src/Web/packages/bridge && pnpm run build)

# ---------------------------------------------------------------------------
# Step 1b: Generate architectural diagrams
# ---------------------------------------------------------------------------
echo "==> Generating architectural diagrams"
dotnet tool restore --verbosity quiet
dotnet build tools/Nocturne.Tools.DiagramGen/Nocturne.Tools.DiagramGen.csproj -c Release --verbosity quiet
bash scripts/diagrams/generate-diagrams.sh

# ---------------------------------------------------------------------------
# Step 2: Generate API client (NSwag + Zod + remote codegen)
# ---------------------------------------------------------------------------
# IMPORTANT: This must run BEFORE dotnet publish, because publish marks the
# build outputs as up-to-date, causing a subsequent dotnet build to skip
# the post-build generation targets (NSwag, Zod, remote codegen).
echo "==> Generating API client"
dotnet build -c Release src/API/Nocturne.API/Nocturne.API.csproj --verbosity quiet

# ---------------------------------------------------------------------------
# Step 3: Verify generated files
# ---------------------------------------------------------------------------
echo "==> Verifying generated API client files"
GENERATED_DIR="src/Web/packages/app/src/lib/api/generated"
MISSING=()
for f in passkeys patientRecords chartDatas profiles alerts; do
  if [[ ! -f "$GENERATED_DIR/${f}.generated.remote.ts" ]]; then
    MISSING+=("$f")
  fi
done
if [[ ${#MISSING[@]} -gt 0 ]]; then
  echo "ERROR: Missing generated remote files: ${MISSING[*]}" >&2
  ls -la "$GENERATED_DIR/"*.generated.remote.ts 2>/dev/null || echo "  No generated remote files found" >&2
  exit 1
fi
REMOTE_COUNT=$(find "$GENERATED_DIR" -name "*.generated.remote.ts" | wc -l)
echo "    Found $REMOTE_COUNT generated remote files"
if [[ "$REMOTE_COUNT" -lt 40 ]]; then
  echo "ERROR: Expected at least 40 generated remote files but found only $REMOTE_COUNT" >&2
  exit 1
fi

# ---------------------------------------------------------------------------
# Step 4: Build API container
# ---------------------------------------------------------------------------
if [[ "${SKIP_API:-false}" != "true" ]]; then
  echo "==> Building API container"
  # Default to linux-x64 so cross-compilation from ARM Macs works out of the box.
  API_RID="${CONTAINER_RID:-linux-x64}"
  PUBLISH_ARGS=(
    -c Release
    -r "$API_RID"
    -p:PublishProfile=DefaultContainer
    -p:ContainerRepository="$IMAGE_REPOSITORY/nocturne-api"
    -p:ContainerImageTag="$VERSION"
  )
  if [[ -n "$PUSH" ]]; then
    # Push to remote registry
    PUBLISH_ARGS+=(-p:ContainerRegistry="$REGISTRY")
  fi
  # Without ContainerRegistry, .NET SDK publishes to the local Docker daemon
  dotnet publish src/API/Nocturne.API/Nocturne.API.csproj "${PUBLISH_ARGS[@]}"
  echo "    Tagged: $IMAGE_REPOSITORY/nocturne-api:$VERSION"
else
  echo "==> Skipping API container (SKIP_API=true)"
fi

# ---------------------------------------------------------------------------
# Step 5: Build Web container
# ---------------------------------------------------------------------------
if [[ "${SKIP_WEB:-false}" != "true" ]]; then
  echo "==> Building Web container"
  DOCKER_ARGS=(
    --platform linux/amd64
    --tag "$REGISTRY/$IMAGE_REPOSITORY/nocturne-web:$VERSION"
    --file "$REPO_ROOT/Dockerfile.web"
  )
  if [[ -n "$PUSH" ]]; then
    DOCKER_ARGS+=("$PUSH")
  else
    DOCKER_ARGS+=(--load)
  fi
  docker buildx build "${DOCKER_ARGS[@]}" "$REPO_ROOT"
  echo "    Tagged: $REGISTRY/$IMAGE_REPOSITORY/nocturne-web:$VERSION"
else
  echo "==> Skipping Web container (SKIP_WEB=true)"
fi

echo ""
echo "==> Build complete!"
echo "    nocturne-api:$VERSION"
echo "    nocturne-web:$VERSION"
