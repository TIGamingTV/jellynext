#!/bin/bash
set -euo pipefail

# Announces a new plugin release to a Jellyfin plugin manifest repository via repository_dispatch.
#
# Every value arrives through the environment rather than as a command-line argument. The changelog
# is arbitrary Markdown, and interpolating it into a shell command line lets backticks and $(...)
# in CHANGELOG.md execute on the runner.
#
# Required:
#   MANIFEST_REPO   owner/repo of the manifest repository to notify
#   PAT_TOKEN       token with write access to MANIFEST_REPO
#   PLUGIN_GUID     the plugin's GUID
#   CHECKSUM        md5 of the release zip
#   TARGET_ABI      minimum Jellyfin version
#   SOURCE_URL      download URL of the release asset
#   VERSION         the plugin version
#   CHANGELOG_FILE  path to a file holding the release notes
#
# Missing MANIFEST_REPO or PAT_TOKEN is not an error: publishing a manifest is optional, and a fork
# without them still produces a perfectly good GitHub release.

MANIFEST_REPO="${MANIFEST_REPO:-}"
PAT_TOKEN="${PAT_TOKEN:-}"

if [ -z "$MANIFEST_REPO" ] || [ -z "$PAT_TOKEN" ]; then
  echo "::notice::Skipping plugin manifest update. Set the PLUGIN_MANIFEST_REPO repository variable and the PAT_TOKEN secret to publish to a plugin repository."
  exit 0
fi

: "${PLUGIN_GUID:?PLUGIN_GUID is required}"
: "${CHECKSUM:?CHECKSUM is required}"
: "${TARGET_ABI:?TARGET_ABI is required}"
: "${SOURCE_URL:?SOURCE_URL is required}"
: "${VERSION:?VERSION is required}"
: "${CHANGELOG_FILE:?CHANGELOG_FILE is required}"

if [ ! -f "$CHANGELOG_FILE" ]; then
  echo "::error::Changelog file not found: $CHANGELOG_FILE"
  exit 1
fi

# jq handles the escaping of quotes, apostrophes, newlines and everything else.
JSON_PAYLOAD=$(jq -n \
  --arg guid "$PLUGIN_GUID" \
  --arg checksum "$CHECKSUM" \
  --rawfile changelog "$CHANGELOG_FILE" \
  --arg targetAbi "$TARGET_ABI" \
  --arg sourceUrl "$SOURCE_URL" \
  --arg version "$VERSION" \
  '{
    event_type: "external_trigger",
    client_payload: {
      guid: $guid,
      checksum: $checksum,
      changelog: $changelog,
      targetAbi: $targetAbi,
      sourceUrl: $sourceUrl,
      version: $version
    }
  }')

printf 'Dispatching to %s:\n%s\n' "$MANIFEST_REPO" "$JSON_PAYLOAD"

RESPONSE_BODY=$(mktemp)
trap 'rm -f "$RESPONSE_BODY"' EXIT

API_URL="${GITHUB_API_URL:-https://api.github.com}/repos/${MANIFEST_REPO}/dispatches"

# curl exits 0 on 4xx/5xx, so the status code has to be inspected explicitly - otherwise a missing
# token or a mistyped repository reports success and the manifest is silently never updated.
CURL_EXIT=0
HTTP_STATUS=$(curl -sS --retry 3 --retry-connrefused -o "$RESPONSE_BODY" -w '%{http_code}' -X POST \
  -H "Accept: application/vnd.github+json" \
  -H "Authorization: Bearer ${PAT_TOKEN}" \
  -H "X-GitHub-Api-Version: 2022-11-28" \
  "$API_URL" \
  -d "${JSON_PAYLOAD}") || CURL_EXIT=$?

if [ "$CURL_EXIT" -ne 0 ]; then
  echo "::error::Could not reach ${API_URL} (curl exit ${CURL_EXIT})."
  cat "$RESPONSE_BODY"
  exit 1
fi

if [ "$HTTP_STATUS" != "204" ]; then
  echo "::error::Plugin repository dispatch to ${MANIFEST_REPO} failed with HTTP ${HTTP_STATUS}."
  cat "$RESPONSE_BODY"
  echo
  case "$HTTP_STATUS" in
    401) echo "::error::PAT_TOKEN is missing, expired, or invalid." ;;
    403) echo "::error::PAT_TOKEN lacks permission to dispatch to ${MANIFEST_REPO}." ;;
    404) echo "::error::${MANIFEST_REPO} does not exist, or PAT_TOKEN cannot see it. A fork must point PLUGIN_MANIFEST_REPO at a manifest repository it owns." ;;
  esac
  exit 1
fi

echo "Plugin repository update triggered successfully for ${MANIFEST_REPO}."
