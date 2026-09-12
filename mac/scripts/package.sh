#!/usr/bin/env bash
# Builds a Release .app, strips the personal-team provisioning profile (irrelevant without entitlements,
# and it expires in 7 days), and zips it for a teammate.
set -euo pipefail
cd "$(dirname "$0")/.."
xcodegen generate
xcodebuild -project Voice.xcodeproj -scheme Voice -configuration Release -derivedDataPath build -quiet build
APP=build/Build/Products/Release/Voice.app
# The embedded profile comes from a free Personal Team and expires in 7 days. It is
# irrelevant here (the app declares no entitlements), so it is stripped — which
# invalidates the signature and is why the app is re-signed immediately below.
rm -f "$APP/Contents/embedded.provisionprofile"

# Pick the identity rather than defaulting to ad-hoc. This used to be
# IDENTITY="${VOICE_SIGN_IDENTITY:--}", which meant that even after the project was
# switched to a real Apple Development identity, this line quietly re-signed the app
# ad-hoc and threw the stable identity away. macOS ties TCC grants to the signature,
# so the visible symptom was the Microphone / Input Monitoring / Accessibility
# permissions being dropped on every single build, with nothing pointing at the cause.
if [[ -n "${VOICE_SIGN_IDENTITY:-}" ]]; then
  IDENTITY="$VOICE_SIGN_IDENTITY"
elif security find-identity -v -p codesigning 2>/dev/null | grep -q "Apple Development"; then
  IDENTITY="Apple Development"
else
  IDENTITY="-"
  echo "warning: no Apple Development identity found — signing ad-hoc." >&2
  echo "         TCC permissions will need re-granting after every build." >&2
fi
echo "Signing with identity: $IDENTITY"
codesign --force --deep --sign "$IDENTITY" "$APP"

# Prove the signature is what we intended: a valid team identifier is what keeps the
# permission grants alive across rebuilds, so a silent fallback to ad-hoc must fail loudly.
codesign -dv --verbose=2 "$APP" 2>&1 | grep -E "Signature|TeamIdentifier" >&2
VERSION=$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$APP/Contents/Info.plist")
ditto -c -k --keepParent "$APP" "build/Voice-$VERSION.zip"
echo "build/Voice-$VERSION.zip"
