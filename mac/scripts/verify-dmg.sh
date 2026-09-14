#!/usr/bin/env bash
# Inspects a built Spit.dmg without launching the app (launching would start a second Spit that fights the
# running one for the hotkey). Every check prints PASS or FAIL; any FAIL exits non-zero.
#
#   mac/scripts/verify-dmg.sh [dmg] [expected-version] [required-team-id]
set -euo pipefail
cd "$(dirname "$0")/.."

DMG=${1:-build/Spit.dmg}
EXPECTED_VERSION=${2:-$(../scripts/check-version.sh)}
REQUIRED_TEAM=${3:-}
BUNDLE_ID=co.miraside.voice

FAILURES=0
pass() { echo "PASS  $1"; }
fail() { echo "FAIL  $1" >&2; FAILURES=$((FAILURES + 1)); }
check() { local name=$1; shift; if "$@" >/dev/null 2>&1; then pass "$name"; else fail "$name"; fi; }

[[ -f "$DMG" ]] || { echo "FAIL  $DMG does not exist" >&2; exit 1; }

check "hdiutil verify" hdiutil verify "$DMG"

MOUNT=$(mktemp -d /tmp/spit-dmg.XXXXXX)
cleanup() { hdiutil detach "$MOUNT" -quiet >/dev/null 2>&1 || hdiutil detach "$MOUNT" -force -quiet >/dev/null 2>&1 || true; rmdir "$MOUNT" 2>/dev/null || true; }
trap cleanup EXIT
hdiutil attach "$DMG" -readonly -nobrowse -noautoopen -mountpoint "$MOUNT" -quiet

VOLNAME=$(diskutil info "$MOUNT" | sed -nE 's/^[[:space:]]*Volume Name:[[:space:]]*(.*)$/\1/p')
[[ "$VOLNAME" == "Spit" ]] && pass "volume name is Spit" || fail "volume name is Spit (found '$VOLNAME')"

APP="$MOUNT/Spit.app"
[[ -d "$APP" ]] && pass "Spit.app present" || fail "Spit.app present"
[[ -L "$MOUNT/Applications" && "$(readlink "$MOUNT/Applications")" == "/Applications" ]] \
  && pass "Applications -> /Applications symlink" || fail "Applications -> /Applications symlink"

ENTRIES=$(ls -A "$MOUNT" | grep -vE '^(\.fseventsd|\.Trashes|\.DS_Store)$' | sort | tr '\n' ' ')
[[ "$ENTRIES" == "Applications Spit.app " ]] && pass "no stray files in the volume" || fail "no stray files in the volume (found: $ENTRIES)"

if [[ -d "$APP" ]]; then
  PLIST="$APP/Contents/Info.plist"
  plist() { /usr/libexec/PlistBuddy -c "Print $1" "$PLIST" 2>/dev/null || true; }

  check "codesign --verify --deep --strict" codesign --verify --deep --strict "$APP"

  TEAM=$(codesign -dv "$APP" 2>&1 | sed -n 's/^TeamIdentifier=//p')
  SIG=$(codesign -dv "$APP" 2>&1 | sed -n 's/^Signature=//p')
  if [[ "$SIG" == "adhoc" ]]; then
    [[ -z "$REQUIRED_TEAM" ]] && echo "WARN  signed ad-hoc (acceptable only outside --release)" || fail "signed by team $REQUIRED_TEAM (found ad-hoc)"
  elif [[ -n "$REQUIRED_TEAM" ]]; then
    [[ "$TEAM" == "$REQUIRED_TEAM" ]] && pass "TeamIdentifier=$REQUIRED_TEAM" || fail "TeamIdentifier=$REQUIRED_TEAM (found '$TEAM')"
  else
    pass "signed by team $TEAM"
  fi

  EXE="$APP/Contents/MacOS/$(plist CFBundleExecutable)"
  ARCHS=$(lipo -archs "$EXE" 2>/dev/null || true)
  [[ "$ARCHS" == "arm64" ]] && pass "executable is arm64 only" || fail "executable is arm64 only (found '$ARCHS')"

  V=$(plist CFBundleShortVersionString)
  [[ "$V" == "$EXPECTED_VERSION" ]] && pass "CFBundleShortVersionString=$EXPECTED_VERSION" || fail "CFBundleShortVersionString=$EXPECTED_VERSION (found '$V')"

  ID=$(plist CFBundleIdentifier)
  [[ "$ID" == "$BUNDLE_ID" ]] && pass "CFBundleIdentifier=$BUNDLE_ID" || fail "CFBundleIdentifier=$BUNDLE_ID (found '$ID')"

  for key in CFBundleName CFBundleDisplayName; do
    VAL=$(plist $key)
    [[ "$VAL" == "Spit" ]] && pass "$key=Spit" || fail "$key=Spit (found '$VAL')"
  done

  MIN=$(plist LSMinimumSystemVersion)
  [[ "$MIN" == "15.0" ]] && pass "LSMinimumSystemVersion=15.0" || fail "LSMinimumSystemVersion=15.0 (found '$MIN')"

  [[ ! -e "$APP/Contents/embedded.provisionprofile" ]] && pass "no expiring provisioning profile" || fail "no expiring provisioning profile"

  grep -q "^Spit records your speech" <<<"$(plist NSMicrophoneUsageDescription)" \
    && pass "microphone text names Spit" || fail "microphone text names Spit"

  [[ -f "$APP/Contents/Resources/Assets.car" ]] && pass "app icon catalog present" || fail "app icon catalog present"
fi

if [[ $FAILURES -gt 0 ]]; then
  echo "$FAILURES check(s) failed for $DMG" >&2
  exit 1
fi
echo "All checks passed for $DMG"
