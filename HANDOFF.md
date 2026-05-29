# Cross-Platform Port — Handoff

**For:** a fresh Claude Code session (or developer) continuing the HTCommander
Windows→cross-platform port, especially after switching dev machines.
**Branch:** `crossplatform-port` (origin = `mprattmd/HTCommander` fork; `upstream` = `Ylianst/HTCommander`).
**Plan:** see [PORTING_PLAN.md](PORTING_PLAN.md). This file is the live status + next steps.

## What exists now (all CI-green: Linux/Windows/macOS + WinForms-on-Windows)

Two solutions:
- `HTCommander.sln` — original Windows-only WinForms app (has a `.vdproj` installer, build `src/HTCommander.csproj` directly, not the sln, in CI).
- `HTCommander.CrossPlatform.sln` — the port. Projects under `cross/`:
  - **HTCommander.Core** (net9.0, `Nullable=annotations`) — portable protocols/DSP + abstractions. 73 files. Builds everywhere.
  - **HTCommander.Platform.Linux** (net9.0) — Linux backends. Has `JsonConfigStore`.
  - **HTCommander.UI.Avalonia** (net9.0) — Avalonia app shell (template only so far).

The WinForms app (`src/`) now **references HTCommander.Core**; the duplicated source was removed from `src/`.

### Done
- Phase 0–1: Core extraction (AprsParser, hamlib, sbc, AX.25/BSS codecs, DataBroker, DataBrokerClient, AX25Session, SoftwareModem, RadioHtStatus) + 14 abstraction interfaces in `Core/Abstractions/`.
- Phase 2a–c: backends `RegistryHelper : IConfigStore` + `WinFormsUiDispatcher` (Windows, in `src/`), `JsonConfigStore` (Linux); WinForms consolidated onto Core.
- Phase 2d (seam): `IRadioTransport` abstraction; `RadioBluetoothWin : IRadioTransport`; `Radio.cs` decoupled from the concrete transport.

### Conventions / gotchas
- `gh` defaults to **upstream** — always pass `--repo mprattmd/HTCommander`.
- Core's pure helpers from the old `Utils` live in `CoreUtils` (class `Utils` stays WinForms-only in `src/radio/Utils.cs`).
- `RadioChannelType` + `RadioLockState`/`SetLockData`/`SetUnlockData`/`TransmitDataFrameData` were moved out of `src/radio/Radio.cs` into `cross/HTCommander.Core/radio/RadioDataTypes.cs` (now top-level `HTCommander` types).
- When moving files using `System.Timers` into Core, add `using Timer = System.Timers.Timer;` (Core's ImplicitUsings makes `Timer` ambiguous).
- `src/HTCommander.csproj` uses implicit globbing, so deleting a file removes it from the build.

## Next steps (best done on Linux with a radio paired)

1. **BlueZ transport** — make `RadioBluetoothLinux` implement `IRadioTransport` in `HTCommander.Platform.Linux` (port the existing stub at `src/radio/RadioBluetoothLinux.cs`, which uses `Tmds.DBus`). Radios use **Classic Bluetooth RFCOMM/SPP** (NOT BLE). Mirror `src/radio/RadioBluetoothWin.cs`. Gaps to fill: device filtering (target names in RadioBluetoothWin), RFCOMM service discovery, socket→stream wrapping, async read loop, GAIA frame decode/encode, OnConnected/ReceivedData events, disconnect/cleanup. **Validate by pairing a UV-Pro/VR-N76/etc. and connecting.**
2. **PortAudio audio** — implement `IAudioCapture`/`IAudioPlayback`/`IAudioDeviceEnumerator` in Platform.Linux (`sudo dnf install portaudio portaudio-devel`; NuGet e.g. `PortAudioSharp2`). SBC codec is already in Core.
3. **Make Radio.cs portable** — move `Radio.cs` (+ `Gps/`, audio glue) into Core, injecting an `IRadioTransport` factory + `IAudio*` instead of `new RadioBluetoothWin(this)` (Radio.cs:~574). Then the Avalonia app can drive a real radio on Linux.
4. **Phase 3 — Avalonia UI**: build the shell + an `IUiDispatcher` over `Dispatcher.UIThread`, then port tabs (parallelizable, one per tab): Voice, APRS, Map (GMap.NET → **Mapsui**), Mail, Terminal, Contacts, BBS, Torrent, Packets, Settings. UI binds to the `DataBroker` already in Core.
5. **Phase 4** — Linux packaging (AppImage/Flatpak/rpm).

## Build on Fedora
```bash
sudo dnf install dotnet-sdk-9.0 bluez portaudio portaudio-devel
git clone https://github.com/mprattmd/HTCommander.git
cd HTCommander && git checkout crossplatform-port
dotnet build HTCommander.CrossPlatform.sln
dotnet run --project cross/HTCommander.UI.Avalonia   # runs the (stub) Avalonia app
```
CI (`.github/workflows/build.yml`) keeps verifying all platforms on every push.
