#!/usr/bin/env bash
#
# Build a self-contained Linux AppImage of the HTCommander cross-platform app.
#
# Requires: .NET SDK 9. For the final .AppImage step also: appimagetool
# (https://github.com/AppImage/AppImageKit/releases) on PATH, plus FUSE.
# Without appimagetool, the script still produces a runnable AppDir (run AppDir/AppRun).
#
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/.." && pwd)"
RID="${RID:-linux-x64}"
APPDIR="$HERE/AppDir"
PROJ="$ROOT/cross/HTCommander.UI.Avalonia/HTCommander.UI.Avalonia.csproj"

echo ">> Publishing self-contained ($RID)..."
rm -rf "$APPDIR"
dotnet publish "$PROJ" -c Release -r "$RID" --self-contained true -o "$APPDIR/usr/bin"

echo ">> Assembling AppDir..."
install -Dm644 "$HERE/htcommander.desktop" "$APPDIR/htcommander.desktop"
install -Dm644 "$HERE/htcommander.svg"     "$APPDIR/htcommander.svg"
# Some tools look for the icon under the hicolor theme too.
install -Dm644 "$HERE/htcommander.svg"     "$APPDIR/usr/share/icons/hicolor/scalable/apps/htcommander.svg"
install -Dm755 "$HERE/AppRun"              "$APPDIR/AppRun"

if command -v appimagetool >/dev/null 2>&1; then
  echo ">> Building AppImage with appimagetool..."
  ARCH="${ARCH:-x86_64}" appimagetool "$APPDIR" "$HERE/HTCommander-$RID.AppImage"
  echo ">> Done: $HERE/HTCommander-$RID.AppImage"
else
  echo ">> appimagetool not found — AppDir is ready and runnable:"
  echo "     $APPDIR/AppRun"
  echo "   Install appimagetool (+FUSE) to produce a single-file .AppImage."
fi
