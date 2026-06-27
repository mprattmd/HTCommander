#!/usr/bin/env bash
# Build the IOBluetooth RFCOMM bridge (libhtbt.dylib) for HTCommander.Platform.Mac.
# Requires Xcode command-line tools (swiftc). macOS only.
#   ./build.sh [arm64|x86_64]   (default: host arch)
# The macOS SDK frameworks are universal, so an Apple-Silicon host can emit the
# x86_64 slice — that's how CI cross-builds the Intel bundle on an arm64 runner.
set -euo pipefail
cd "$(dirname "$0")"

# Normalise the requested arch (accept RID-style x64 too) into a swiftc target triple.
ARCH="${1:-$(uname -m)}"
case "$ARCH" in
  arm64|aarch64) ARCH=arm64;  TARGET="arm64-apple-macos11" ;;
  x86_64|x64)    ARCH=x86_64; TARGET="x86_64-apple-macos11" ;;
  *) echo "!! unknown arch '$ARCH' (want arm64 or x86_64)"; exit 1 ;;
esac

OUT_DYLIB="libhtbt.dylib"
# Drop the dylib where the .NET project picks it up as a native runtime asset.
DEST="../../cross/HTCommander.Platform.Mac/runtimes/osx/native"

echo "Building $OUT_DYLIB ($ARCH) ..."
swiftc -O -emit-library -target "$TARGET" -o "$OUT_DYLIB" htbt.swift \
    -framework Foundation -framework IOBluetooth

mkdir -p "$DEST"
cp "$OUT_DYLIB" "$DEST/$OUT_DYLIB"
echo "Copied -> $DEST/$OUT_DYLIB"
echo "Done."
