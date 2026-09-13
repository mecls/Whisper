#!/usr/bin/env bash
# One version for both clients (prd-spit-mac-windows.md rule 6). The repo-root VERSION file is the
# source; the Windows projects read it directly, but XcodeGen cannot read a file, so mac/project.yml
# carries a copy. This fails the moment the copy drifts, and — given a tag — when the tag disagrees.
#
#   scripts/check-version.sh            # VERSION is well-formed and project.yml matches it
#   scripts/check-version.sh v0.2.0     # …and the tag matches too
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION=$(tr -d '[:space:]' < VERSION)
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "error: VERSION must be X.Y.Z, found '$VERSION'" >&2
  exit 1
fi

MAC=$(sed -nE 's/^[[:space:]]*MARKETING_VERSION:[[:space:]]*"?([^"[:space:]]+)"?[[:space:]]*$/\1/p' mac/project.yml)
if [[ "$MAC" != "$VERSION" ]]; then
  echo "error: mac/project.yml MARKETING_VERSION is '$MAC' but VERSION is '$VERSION' — change both" >&2
  exit 1
fi

if [[ $# -ge 1 ]]; then
  TAG="$1"
  if [[ "$TAG" != "v$VERSION" ]]; then
    echo "error: tag '$TAG' does not match VERSION '$VERSION' (expected v$VERSION)" >&2
    exit 1
  fi
fi

echo "$VERSION"
