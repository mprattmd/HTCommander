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
# codesign --verify --strict (and notarization) treats EVERY file there as nested code
# that must be signed — not just the Mach-O dylibs. So we sign every nested file except
# the apphost, then seal the bundle (which signs the apphost with entitlements).
#
# Managed .dll/.json aren't Mach-O, so codesign stores their signature *detached* in
# com.apple.cs.* xattrs. Replacing an existing detached signature in place can fail with
# "Operation not permitted" (the file may be LaunchServices-registered after a prior run),
# so we delete our own prior cs.* xattrs first and let codesign write fresh ones. The
# SIP-restricted com.apple.provenance can't be removed, but it doesn't block a fresh sign.
echo "==> [1/4] Strip debug symbols (.pdb) — they break bundle sealing and shouldn't ship"
find "$APP" -name '*.pdb' -delete

echo "==> [2/4] Codesign every nested file except the apphost (inside-out, idempotent)"
find "$APP/Contents/MacOS" -type f ! -name "$EXE" -print0 \
  | while IFS= read -r -d '' f; do
      for a in CodeDirectory CodeRequirements CodeSignature; do
        xattr -d "com.apple.cs.$a" "$f" 2>/dev/null || true   # clear prior detached sig
      done
      printf '%s\0' "$f"
    done \
  | xargs -0 codesign --force --options runtime --timestamp --sign "$SIGN_ID"

echo "==> [3/4] Codesign the bundle (signs the apphost w/ entitlements) + verify"
codesign --force --options runtime --timestamp --entitlements "$ENT" --sign "$SIGN_ID" "$APP"
codesign --verify --strict --verbose=2 "$APP"

if [ "${1:-}" = "--no-notarize" ]; then
  echo "==> Skipping notarization (--no-notarize). Bundle is signed but NOT notarized."
  exit 0
fi

echo "==> Notarize (profile: $PROFILE) — submitting and waiting…"
ZIP=/tmp/htcommander-notarize.zip
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"
xcrun notarytool submit "$ZIP" --keychain-profile "$PROFILE" --wait
rm -f "$ZIP"

echo "==> Staple + validate"
xcrun stapler staple "$APP"
xcrun stapler validate "$APP"
spctl -a -vv -t exec "$APP" || true     # 'exec' assesses an .app; 'install' is for pkg/dmg
echo "==> Done. $APP is signed, notarized, and stapled."
