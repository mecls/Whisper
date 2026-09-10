#!/usr/bin/env bash
# Builds a Release .app, strips the personal-team provisioning profile (irrelevant without entitlements,
# and it expires in 7 days), and zips it for a teammate.
set -euo pipefail
cd "$(dirname "$0")/.."
xcodegen generate
xcodebuild -project Voice.xcodeproj -scheme Voice -configuration Release -derivedDataPath build -quiet build
APP=build/Build/Products/Release/Voice.app
rm -f "$APP/Contents/embedded.provisionprofile"
IDENTITY="${VOICE_SIGN_IDENTITY:--}"
echo "Signing with identity: $IDENTITY"
codesign --force --deep --sign "$IDENTITY" "$APP"
VERSION=$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$APP/Contents/Info.plist")
ditto -c -k --keepParent "$APP" "build/Voice-$VERSION.zip"
echo "build/Voice-$VERSION.zip"
