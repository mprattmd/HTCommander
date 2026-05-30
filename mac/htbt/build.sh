#!/usr/bin/env bash
# Build the IOBluetooth RFCOMM bridge (libhtbt.dylib) for HTCommander.Platform.Mac.
# Requires Xcode command-line tools (swiftc). macOS only.
set -euo pipefail
cd "$(dirname "$0")"

OUT_DYLIB="libhtbt.dylib"
# Drop the dylib where the .NET project picks it up as a native runtime asset.
DEST="../../cross/HTCommander.Platform.Mac/runtimes/osx/native"

echo "Building $OUT_DYLIB ..."
swiftc -O -emit-library -o "$OUT_DYLIB" htbt.swift \
    -framework Foundation -framework IOBluetooth

mkdir -p "$DEST"
cp "$OUT_DYLIB" "$DEST/$OUT_DYLIB"
echo "Copied -> $DEST/$OUT_DYLIB"
echo "Done."
