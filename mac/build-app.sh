#!/usr/bin/env bash
# Build a double-clickable HTCommander.app for macOS.
#   ./mac/build-app.sh [osx-arm64|osx-x64]   (default: osx-arm64)
# Produces dist/mac/HTCommander.app (self-contained — bundles the .NET runtime,
# the IOBluetooth bridge libhtbt.dylib, and libportaudio for voice).
set -euo pipefail
cd "$(dirname "$0")/.."                      # repo root

RID="${1:-osx-arm64}"
APP="HTCommander"
EXE="HTCommander.UI.Avalonia"               # AssemblyName of the Avalonia project
PROJ="cross/HTCommander.UI.Avalonia/HTCommander.UI.Avalonia.csproj"
PUBDIR="cross/HTCommander.UI.Avalonia/bin/Release/net9.0/$RID/publish"
BUNDLE="dist/mac/$APP.app"
DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"

echo "==> [1/4] Building the IOBluetooth bridge (libhtbt.dylib)"
( cd mac/htbt && ./build.sh )

echo "==> [2/4] dotnet publish ($RID, self-contained)"
"$DOTNET" publish "$PROJ" -c Release -r "$RID" --self-contained true -p:PublishSingleFile=false

echo "==> [3/4] Assembling $BUNDLE"
rm -rf "$BUNDLE"
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources"
cp -R "$PUBDIR"/. "$BUNDLE/Contents/MacOS/"

# Native libraries next to the executable (.NET resolves DllImport from the app dir).
cp cross/HTCommander.Platform.Mac/runtimes/osx/native/libhtbt.dylib "$BUNDLE/Contents/MacOS/"
for pa in /opt/homebrew/lib/libportaudio.2.dylib /usr/local/lib/libportaudio.2.dylib; do
  if [ -f "$pa" ]; then cp "$pa" "$BUNDLE/Contents/MacOS/libportaudio.dylib"; break; fi
done
[ -f "$BUNDLE/Contents/MacOS/libportaudio.dylib" ] || \
  echo "    (!) libportaudio not found — voice/audio will be disabled. brew install portaudio"

cat > "$BUNDLE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>$APP</string>
  <key>CFBundleDisplayName</key><string>$APP</string>
  <key>CFBundleIdentifier</key><string>com.mprattmd.htcommander</string>
  <key>CFBundleVersion</key><string>0.4.2</string>
  <key>CFBundleShortVersionString</key><string>0.4.2</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleExecutable</key><string>$EXE</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSBluetoothAlwaysUsageDescription</key><string>HTCommander talks to your handheld radio over Bluetooth.</string>
  <key>NSBluetoothPeripheralUsageDescription</key><string>HTCommander talks to your handheld radio over Bluetooth.</string>
  <key>NSMicrophoneUsageDescription</key><string>HTCommander uses the microphone for voice transmit (PTT).</string>
</dict>
</plist>
PLIST

chmod +x "$BUNDLE/Contents/MacOS/$EXE"

echo "==> [4/4] Done: $BUNDLE"
echo "    Run it:  open \"$BUNDLE\"     (first launch: right-click → Open if unsigned)"
echo "    Note: unsigned/unnotarized — for distribution, codesign + notarize the bundle."
