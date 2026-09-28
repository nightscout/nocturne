#!/usr/bin/env bash
# Writes the image tags for this build to $GITHUB_OUTPUT. Every job in docker-publish.yml
# calls it, so the images each job pushes and the ones the report job verifies agree.
#   version      the tag every image gets, and the only one the dotnet images get
#   web_tags     comma-separated full references for the web image
# Reads REGISTRY, IMAGE_REPOSITORY, EVENT_NAME and PR_NUMBER from the environment.
set -euo pipefail

web="${REGISTRY}/${IMAGE_REPOSITORY}/nocturne-web"

if [[ "$EVENT_NAME" == "pull_request" ]]; then
  version="pr-${PR_NUMBER}-${GITHUB_SHA::7}"
  web_tags="${web}:${version}"
elif [[ "$GITHUB_REF" == refs/tags/v* ]]; then
  version="${GITHUB_REF#refs/tags/v}"
  web_tags="${web}:${version},${web}:latest"
elif [[ "$GITHUB_REF" == refs/heads/master || "$GITHUB_REF" == refs/heads/main ]]; then
  version=latest
  web_tags="${web}:${version}"
else
  safe_ref=$(echo "${GITHUB_REF_NAME}" | tr '/_' '-' | tr -cd '[:alnum:].-')
  version="${safe_ref}-${GITHUB_SHA::7}"
  web_tags="${web}:${version}"
fi

{
  echo "version=${version}"
  echo "web_tags=${web_tags}"
} >> "$GITHUB_OUTPUT"
