# Packaging (Linux)

Build a self-contained Linux app for the cross-platform HTCommander
(`cross/HTCommander.UI.Avalonia`). No .NET runtime is required on the target —
the publish bundles the runtime and native libs (Skia, HarfBuzz, PortAudio).

## AppImage / AppDir

```bash
./packaging/build-appimage.sh          # RID=linux-x64 by default
```

- Produces `packaging/AppDir/` (immediately runnable: `packaging/AppDir/AppRun`).
- If `appimagetool` (+ FUSE) is on PATH, it also emits `packaging/HTCommander-linux-x64.AppImage`.
- `appimagetool`: https://github.com/AppImage/AppImageKit/releases (needs a network fetch + FUSE; not available in all CI/sandboxes — the AppDir works without it).

## Plain self-contained folder (no AppImage tooling)

```bash
dotnet publish cross/HTCommander.UI.Avalonia/HTCommander.UI.Avalonia.csproj \
  -c Release -r linux-x64 --self-contained true -o out/
./out/HTCommander.UI.Avalonia
```

Verified: the self-contained `linux-x64` binary (~105 MB) launches standalone on
Fedora (PipeWire) with all native libs bundled.

## Runtime requirements on the target
- A Bluetooth adapter with BlueZ (the app talks to BlueZ over D-Bus and opens
  raw RFCOMM/L2CAP sockets; the radio must be paired — see HANDOFF.md).
- PipeWire/PulseAudio or ALSA for audio.

## TODO
- `osx-x64`/`osx-arm64` + `win-x64` bundles once those platform backends exist.
- Optionally a `.rpm`/`.deb` and a Flatpak manifest.
