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
- Phase 2d (Linux BlueZ): `RadioBluetoothLinux : IRadioTransport` in `HTCommander.Platform.Linux` (+ `BlueZRadioDiscovery : IRadioTransportDiscovery`). **Hardware-validated against a UV-PRO**: connects on RFCOMM ch1 (BT_SECURITY_MEDIUM), sends GET_DEV_INFO/READ_STATUS/GET_HT_STATUS and decodes the GAIA-framed `cmd|0x8000` responses; OnConnected/ReceivedData/onDisconnected all fire; clean teardown.

### Conventions / gotchas
- `gh` defaults to **upstream** — always pass `--repo mprattmd/HTCommander`.
- Core's pure helpers from the old `Utils` live in `CoreUtils` (class `Utils` stays WinForms-only in `src/radio/Utils.cs`).
- `RadioChannelType` + `RadioLockState`/`SetLockData`/`SetUnlockData`/`TransmitDataFrameData` were moved out of `src/radio/Radio.cs` into `cross/HTCommander.Core/radio/RadioDataTypes.cs` (now top-level `HTCommander` types).
- When moving files using `System.Timers` into Core, add `using Timer = System.Timers.Timer;` (Core's ImplicitUsings makes `Timer` ambiguous).
- `src/HTCommander.csproj` uses implicit globbing, so deleting a file removes it from the build.

## Next steps (best done on Linux with a radio paired)

1. ~~**BlueZ transport**~~ ✅ DONE (code) — `cross/HTCommander.Platform.Linux/RadioBluetoothLinux.cs` + `Bluetooth/{BlueZDBus,NativeRfcomm}.cs`. Design notes:
   - **Discovery + adapter power-on** go through BlueZ over D-Bus (`Tmds.DBus`). The D-Bus proxy interfaces **must be `public`** (Tmds.DBus emits proxies via Reflection.Emit → `TypeLoadException` on non-public interfaces).
   - **The RFCOMM/SPP data stream uses a raw kernel `AF_BLUETOOTH`/`BTPROTO_RFCOMM` socket** (P/Invoke in `NativeRfcomm.cs`), NOT D-Bus: BlueZ only exposes RFCOMM via `Profile1` + Unix-fd-passing, which the high-level `Tmds.DBus` API doesn't support. `RfcommStream : Stream` wraps the fd (blocking reads; `Disconnect` shuts the fd down to unblock the loop).
   - **Channel discovery:** BlueZ no longer exposes the SPP RFCOMM channel over D-Bus, so `NativeRfcomm.Connect` **scans channels 1..30** (first to accept wins). Override with env `HTCOMMANDER_RFCOMM_CHANNEL=N` to skip the scan once the real channel is known.
   - **Authenticated link:** the SPP RFCOMM connect requires `setsockopt(SOL_BLUETOOTH, BT_SECURITY, BT_SECURITY_MEDIUM)` before `connect()`, else the kernel returns EACCES.
   - GAIA encode/decode + accumulator read loop ported verbatim from `RadioBluetoothWin`. Self-contained ctor `(macAddress, ILogger?, Action<string>? onDisconnected)` — no dependency on the WinForms `Radio` (so step 3 / Avalonia can inject it).
   - **Pairing gotcha:** a stale bond surfaces as BlueZ `br-connection-key-missing` / EACCES on connect even though `Paired: yes`. Check `bluetoothctl info <addr>` shows **`Bonded: yes`**; if not, `remove` + re-pair (radio in pairing mode). The UV-PRO on this box is `38:D2:00:01:7F:0E`, SPP on channel 1.
   - Channel scan (1..30) works; for speed set `HTCOMMANDER_RFCOMM_CHANNEL=1` for this radio. A proper SDP query could replace the scan later but isn't needed.
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
