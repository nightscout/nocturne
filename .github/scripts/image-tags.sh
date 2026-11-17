#!/usr/bin/env bash
# Writes the image tags for this build to $GITHUB_OUTPUT. Every job in docker-publish.yml calls
# it, so the images each job pushes and the ones the report job verifies agree.
#   version      the primary tag every image is pushed with
#   app_version  the release version stamped into the API assembly; empty off a release tag
#   extra_tags   space-separated further tags pointed at the same manifest
#   web_tags     comma-separated full references for the web image (primary + extra)
#   build_tag    the tag the dotnet images are pushed to; never a tag anything deploys by polling
#   promote_tags space-separated tags pointed at build_tag once its image is verified
#
# The dotnet images get build_tag and promote_tags rather than version because `dotnet publish`
# pushes each platform's single-arch image to its tags before it pushes the index: pushed straight
# to :develop, a host polling that tag sees only the first platform for a minute or so, pulls it,
# and crash-loops on exec format error if it is the other architecture.
#
#   push to main       develop, main-<sha7>                   dotnet build_tag main-<sha7>
#   v1.2.3 tag         1.2.3, latest                          dotnet build_tag release-<sha7>
#   v1.2.3-rc.1 tag    1.2.3-rc.1 (never moves latest)        dotnet build_tag release-<sha7>
#
# Pull requests publish nothing. Reads REGISTRY and IMAGE_REPOSITORY from the environment.
set -euo pipefail

if [[ "$GITHUB_REF" == refs/tags/v* ]]; then
  version="${GITHUB_REF#refs/tags/v}"
  if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-.+)?$ ]]; then
    echo "::error::tag ${GITHUB_REF#refs/tags/} is not v<major>.<minor>.<patch>[-<pre-release>]"
    exit 1
  fi
  app_version="$version"
  if [[ "$version" == *-* ]]; then
    extra=""
  else
    extra="latest"
  fi
  build_tag="release-${GITHUB_SHA::7}"
  promote="$version${extra:+ $extra}"
elif [[ "$GITHUB_REF" == refs/heads/main ]]; then
  version="develop"
  app_version=""
  extra="main-${GITHUB_SHA::7}"
  build_tag="$extra"
  promote="$version"
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
  echo "app_version=${app_version}"
  echo "extra_tags=${extra}"
  echo "web_tags=${web_tags}"
  echo "build_tag=${build_tag}"
  echo "promote_tags=${promote}"
} >> "$GITHUB_OUTPUT"
