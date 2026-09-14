#!/usr/bin/env bash
# Builds Spit.dmg for friends (prd-spit-mac-windows.md rules 10, 12, 13, 14).
#
#   mac/scripts/package.sh             # test, build Release (arm64), sign, write build/Spit.dmg, verify it
#   mac/scripts/package.sh --release   # the same, but refuses anything but the Apple Development identity,
#                                      # then uploads Spit.dmg and SHA256SUMS.txt to the vX.Y.Z draft release
#
# Environment:
#   VOICE_SIGN_IDENTITY       sign with this identity instead of looking one up
#   VOICE_FORCE_NO_IDENTITY=1 behave as if no Apple Development identity exists (tests the refusal path)
#   SPIT_SKIP_TESTS=1         skip `xcodebuild test` (never with --release)
set -euo pipefail
cd "$(dirname "$0")/.."

RELEASE=0
for arg in "$@"; do
  case "$arg" in
    --release) RELEASE=1 ;;
    *) echo "usage: $0 [--release]" >&2; exit 2 ;;
  esac
done

TEAM=FZC6P6XRGD
VERSION=$(../scripts/check-version.sh)
echo "Spit $VERSION"

# Pick the identity before spending minutes on a build that could never ship.
# This used to be IDENTITY="${VOICE_SIGN_IDENTITY:--}", which quietly re-signed the app ad-hoc even after
# the project moved to a real Apple Development identity. macOS ties TCC grants to the signature, so every
# build silently dropped Microphone / Input Monitoring / Accessibility with nothing pointing at the cause.
if [[ -n "${VOICE_SIGN_IDENTITY:-}" ]]; then
  IDENTITY="$VOICE_SIGN_IDENTITY"
elif [[ "${VOICE_FORCE_NO_IDENTITY:-}" != "1" ]] && security find-identity -v -p codesigning 2>/dev/null | grep -q "Apple Development"; then
  IDENTITY="Apple Development"
else
  if [[ $RELEASE -eq 1 ]]; then
    # Rule 13: an ad-hoc signature changes on every build, so every friend would lose all three
    # permissions on every update — with no error, only a hotkey that stops working.
    echo "error: refusing to build a release without an Apple Development identity" >&2
    exit 1
  fi
  IDENTITY="-"
  echo "warning: no Apple Development identity found — signing ad-hoc." >&2
  echo "         TCC permissions will need re-granting after every build." >&2
fi

if [[ $RELEASE -eq 1 ]]; then
  command -v gh >/dev/null || { echo "error: --release needs the gh CLI" >&2; exit 1; }
  gh release view "v$VERSION" >/dev/null 2>&1 || {
    echo "error: no release v$VERSION on GitHub yet — push the tag and let release-windows.yml create the draft" >&2
    exit 1
  }
  if [[ "$(gh release view "v$VERSION" --json isDraft --jq .isDraft)" != "true" ]]; then
    echo "error: release v$VERSION is already published — never replace a published asset; bump VERSION" >&2
    exit 1
  fi
fi

xcodegen generate

if [[ $RELEASE -eq 1 || "${SPIT_SKIP_TESTS:-}" != "1" ]]; then
  xcodebuild -project Voice.xcodeproj -scheme Voice -derivedDataPath build/test-dd -quiet test
fi

xcodebuild -project Voice.xcodeproj -scheme Voice -configuration Release -derivedDataPath build \
  MARKETING_VERSION="$VERSION" -quiet build
APP=build/Build/Products/Release/Spit.app

# The embedded profile comes from a free Personal Team and expires in 7 days. It is irrelevant here (the
# app declares no entitlements), so it is stripped — which invalidates the signature and is why the app is
# re-signed immediately below.
rm -f "$APP/Contents/embedded.provisionprofile"
echo "Signing with identity: $IDENTITY"
codesign --force --deep --sign "$IDENTITY" "$APP"
codesign --verify --deep --strict "$APP"
codesign -dv --verbose=2 "$APP" 2>&1 | grep -E "Signature|TeamIdentifier" >&2

if [[ $RELEASE -eq 1 ]]; then
  ACTUAL_TEAM=$(codesign -dv "$APP" 2>&1 | sed -n 's/^TeamIdentifier=//p')
  if [[ "$ACTUAL_TEAM" != "$TEAM" ]]; then
    echo "error: signed with TeamIdentifier '$ACTUAL_TEAM', release requires $TEAM" >&2
    exit 1
  fi
fi

# Rule 12: Spit.app plus an Applications symlink, built with hdiutil (ships with macOS; no create-dmg).
STAGING=build/dmg-staging
rm -rf "$STAGING" build/Spit.dmg
mkdir -p "$STAGING"
ditto "$APP" "$STAGING/Spit.app"
ln -s /Applications "$STAGING/Applications"
hdiutil create -volname Spit -srcfolder "$STAGING" -ov -format UDZO build/Spit.dmg >/dev/null
rm -rf "$STAGING"

scripts/verify-dmg.sh build/Spit.dmg "$VERSION" $([[ $RELEASE -eq 1 ]] && echo "$TEAM")

if [[ $RELEASE -eq 1 ]]; then
  gh release upload "v$VERSION" build/Spit.dmg --clobber
  # SHA256SUMS.txt covers whichever of the two installers are attached; release-windows.yml regenerates it
  # the same way when it uploads Spit-Setup.exe, so the last uploader always leaves a complete file.
  SUMS_DIR=$(mktemp -d)
  trap 'rm -rf "$SUMS_DIR"' EXIT
  gh release download "v$VERSION" --pattern 'Spit.dmg' --pattern 'Spit-Setup.exe' --dir "$SUMS_DIR"
  (cd "$SUMS_DIR" && shasum -a 256 Spit.dmg $( [[ -f Spit-Setup.exe ]] && echo Spit-Setup.exe ) > SHA256SUMS.txt)
  gh release upload "v$VERSION" "$SUMS_DIR/SHA256SUMS.txt" --clobber
  cat "$SUMS_DIR/SHA256SUMS.txt"
  [[ -f "$SUMS_DIR/Spit-Setup.exe" ]] || echo "note: Spit-Setup.exe is not attached yet; SHA256SUMS.txt lists Spit.dmg only" >&2
fi

echo "build/Spit.dmg"
