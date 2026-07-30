#!/bin/bash
set -euo pipefail

# Adds a released version to manifest.json, the Jellyfin plugin repository this repo serves from
# raw.githubusercontent.com.
#
# Values arrive through the environment, never as command-line arguments. The changelog is arbitrary
# Markdown, and interpolating it into a shell command line would let backticks and $(...) in
# CHANGELOG.md execute.
#
# Required:
#   VERSION         the plugin version, e.g. 1.4.0.0
#   TARGET_ABI      minimum Jellyfin version, e.g. 10.11.0.0
#   SOURCE_URL      download URL of the release asset
#   CHECKSUM        md5 of the release zip
#   CHANGELOG_FILE  path to a file holding the release notes
# Optional:
#   MANIFEST_FILE   defaults to manifest.json
#   REPOSITORY_URL  advertised alongside the version

: "${VERSION:?VERSION is required}"
: "${TARGET_ABI:?TARGET_ABI is required}"
: "${SOURCE_URL:?SOURCE_URL is required}"
: "${CHECKSUM:?CHECKSUM is required}"
: "${CHANGELOG_FILE:?CHANGELOG_FILE is required}"

MANIFEST_FILE="${MANIFEST_FILE:-manifest.json}"
REPOSITORY_URL="${REPOSITORY_URL:-}"

if [ ! -f "$MANIFEST_FILE" ]; then
  echo "::error::$MANIFEST_FILE not found."
  exit 1
fi

if [ ! -f "$CHANGELOG_FILE" ]; then
  echo "::error::Changelog file not found: $CHANGELOG_FILE"
  exit 1
fi

TIMESTAMP=$(date -u +%Y-%m-%dT%H:%M:%SZ)

# Newest first, and replace any existing entry for this version so a re-run is idempotent.
# jq --rawfile keeps the changelog's newlines and backticks intact.
UPDATED=$(jq \
  --arg version "$VERSION" \
  --arg targetAbi "$TARGET_ABI" \
  --arg sourceUrl "$SOURCE_URL" \
  --arg checksum "$CHECKSUM" \
  --arg timestamp "$TIMESTAMP" \
  --arg repositoryUrl "$REPOSITORY_URL" \
  --rawfile changelog "$CHANGELOG_FILE" \
  '
  ($changelog | sub("\\s+$"; "")) as $notes
  | map(
      .versions = (
        [{
          version: $version,
          changelog: $notes,
          targetAbi: $targetAbi,
          sourceUrl: $sourceUrl,
          checksum: $checksum,
          timestamp: $timestamp,
          repositoryName: "JellyNext",
          repositoryUrl: $repositoryUrl
        }]
        + ((.versions // []) | map(select(.version != $version)))
      )
    )
  ' "$MANIFEST_FILE")

printf '%s\n' "$UPDATED" > "$MANIFEST_FILE"

echo "Added v${VERSION} to ${MANIFEST_FILE}:"
jq '.[0].versions[0]' "$MANIFEST_FILE"
