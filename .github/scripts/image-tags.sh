#!/usr/bin/env bash
# Writes the image tags for this build to $GITHUB_OUTPUT. Every job in docker-publish.yml calls
# it, so the images each job pushes and the ones the report job verifies agree.
#   version      the primary tag every image is pushed with
#   extra_tags   space-separated further tags pointed at the same manifest
#   web_tags     comma-separated full references for the web image (primary + extra)
#
#   push to main       develop, main-<sha7>
#   v1.2.3 tag         1.2.3, latest
#   v1.2.3-rc.1 tag    1.2.3-rc.1        (a pre-release never moves latest)
#
# Pull requests publish nothing. Reads REGISTRY and IMAGE_REPOSITORY from the environment.
set -euo pipefail

if [[ "$GITHUB_REF" == refs/tags/v* ]]; then
  version="${GITHUB_REF#refs/tags/v}"
  if [[ "$version" == *-* ]]; then
    extra=""
  else
    extra="latest"
  fi
elif [[ "$GITHUB_REF" == refs/heads/main ]]; then
  version="develop"
  extra="main-${GITHUB_SHA::7}"
else
  echo "::error::images are published from main and v* tags only, not ${GITHUB_REF}"
  exit 1
fi

web="${REGISTRY}/${IMAGE_REPOSITORY}/nocturne-web"
web_tags="${web}:${version}"
for tag in $extra; do
  web_tags+=",${web}:${tag}"
done

{
  echo "version=${version}"
  echo "extra_tags=${extra}"
  echo "web_tags=${web_tags}"
} >> "$GITHUB_OUTPUT"
