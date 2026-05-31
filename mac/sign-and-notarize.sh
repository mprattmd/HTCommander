#!/usr/bin/env bash
# Local dry-run of the CI codesign + notarize + staple flow (release.yml).
# Assumes the .app is already built:  ./mac/build-app.sh osx-arm64
#
# Notary credentials come from a keychain profile so no secrets touch the CLI/history.
# Create the profile once (uses the dev account's Apple ID + app-specific password):
#   xcrun notarytool store-credentials htcommander-notary \
#     --apple-id "<dev-account-email>" --team-id CWKB73K3FV --password "<app-specific-pw>"
#
# Then:  ./mac/sign-and-notarize.sh [--no-notarize]
set -euo pipefail
cd "$(dirname "$0")/.."                      # repo root

APP="dist/mac/HTCommander.app"
ENT="mac/HTCommander.entitlements"
EXE="HTCommander.UI.Avalonia"                # the bundle's main executable (apphost)
PROFILE="${NOTARY_PROFILE:-htcommander-notary}"
# Auto-detect the single Developer ID Application identity unless SIGN_ID is set.
SIGN_ID="${SIGN_ID:-$(security find-identity -v -p codesigning \
  | sed -n 's/.*"\(Developer ID Application: .*\)"/\1/p' | head -1)}"

[ -d "$APP" ] || { echo "!! $APP not found — run ./mac/build-app.sh osx-arm64 first"; exit 1; }
[ -n "$SIGN_ID" ] || { echo "!! No Developer ID Application identity in the keychain"; exit 1; }
echo "==> Signing identity: $SIGN_ID"

# The .NET self-contained layout drops the whole payload into Contents/MacOS, and
# codesign treats EVERY file there as code that must be signed — not just Mach-O.
# So: drop debug symbols, sign every remaining file except the apphost, then seal
# the bundle (which signs the apphost and records the rest). Signing only Mach-O
# files leaves the managed .dll/.json unsigned and the bundle seal fails.
echo "==> [1/4] Strip debug symbols (.pdb) — they break bundle sealing and shouldn't ship"
find "$APP" -name '*.pdb' -delete

echo "==> [2/4] Codesign every nested file except the apphost (inside-out, batched)"
find "$APP/Contents/MacOS" -type f ! -name "$EXE" -print0 \
  | xargs -0 codesign --force --options runtime --timestamp --entitlements "$ENT" --sign "$SIGN_ID"

echo "==> [3/4] Codesign the bundle + verify"
codesign --force --options runtime --timestamp --entitlements "$ENT" --sign "$SIGN_ID" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"

if [ "${1:-}" = "--no-notarize" ]; then
  echo "==> Skipping notarization (--no-notarize). Bundle is signed but NOT notarized."
  exit 0
fi

echo "==> Notarize (profile: $PROFILE) — submitting and waiting…"
ditto -c -k --keepParent "$APP" /tmp/htcommander-notarize.zip
xcrun notarytool submit /tmp/htcommander-notarize.zip --keychain-profile "$PROFILE" --wait

echo "==> Staple + validate"
xcrun stapler staple "$APP"
xcrun stapler validate "$APP"
spctl -a -vv -t install "$APP" || true
echo "==> Done. $APP is signed, notarized, and stapled."
