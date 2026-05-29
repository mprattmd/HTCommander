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
- Phase 3 (Avalonia shell, C0): `AvaloniaUiDispatcher : IUiDispatcher`, a composition root in `App.axaml.cs` (JsonConfigStore + DataBroker.Initialize + Linux backends), and a `MainWindow` shell (connection bar + tabbed area with placeholders). `MainViewModel` lists radios via `BlueZRadioDiscovery.FindCompatibleRadios()` and connects via `RadioBluetoothLinux`, showing live GAIA frames. The Avalonia app now references `Platform.Linux`. **Verified on Linux**: headless VM test discovers the UV-PRO + auto-selects it; the GUI launches and runs on a real display. Per-tab ports (C1..Cn) are the next fan-out.
- Phase 2 (audio): `IAudio*` abstractions in `Core/Abstractions/Audio/` (`AudioFormat`, `AudioDevice`, `IAudioDeviceEnumerator`, `IAudioCapture`, `IAudioPlayback`, `IWaveFileWriter/Reader`) + portable WAV in `Core/Audio/WavFile.cs`. PortAudio backend in `Platform.Linux/Audio/` (`PortAudioDeviceEnumerator/Capture/Playback`). **Hardware-validated on Linux**: device enumeration (23 render / 22 capture), 32k/16/mono capture + playback streams, WAV byte-exact round-trip. NAudio impls (Windows) + wiring of `RadioAudio.cs`/`Microphone.cs` onto these interfaces still TODO (Windows-side, see below).

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
   - **Channel discovery (GAIA-validated):** the radio assigns its SPP/GAIA RFCOMM channel **dynamically** — observed moving between 1 and 4 across sessions — and other RFCOMM channels accept but speak non-GAIA services (BS AOC, Voice Gateway). So `RadioBluetoothLinux.ConnectGaiaChannel` scans 1..30 and, for each channel that connects, sends a `GET_DEV_INFO` probe and keeps the channel only if the reply starts with a GAIA header (`0xFF 0x01`); the probe reply is fed into the read accumulator so no bytes are lost. `NativeRfcomm.WriteAll`/`ReadWithTimeout` back the probe. `HTCOMMANDER_RFCOMM_CHANNEL=N` restricts the scan to one channel (still GAIA-validated). **Do NOT** assume a fixed channel or "first channel that accepts" — both are wrong for this radio.
   - **Authenticated link:** the SPP RFCOMM connect requires `setsockopt(SOL_BLUETOOTH, BT_SECURITY, BT_SECURITY_MEDIUM)` before `connect()`, else the kernel returns EACCES.
   - GAIA encode/decode + accumulator read loop ported verbatim from `RadioBluetoothWin`. Self-contained ctor `(macAddress, ILogger?, Action<string>? onDisconnected)` — no dependency on the WinForms `Radio` (so step 3 / Avalonia can inject it).
   - **Pairing gotcha:** a stale bond surfaces as BlueZ `br-connection-key-missing` / EACCES on connect even though `Paired: yes`. Check `bluetoothctl info <addr>` shows **`Bonded: yes`**; if not, `remove` + re-pair (radio in pairing mode). The UV-PRO on this box is `38:D2:00:01:7F:0E`, SPP on channel 1.
   - Channel is discovered fresh each connect via the GAIA-validated scan above (SDP is unreliable here — `sdptool` often hangs on this radio).
2. ~~**PortAudio audio**~~ ✅ DONE (Linux backend) — `IAudio*` interfaces in `Core/Abstractions/Audio/`, portable WAV in `Core/Audio/WavFile.cs`, PortAudio impls in `Platform.Linux/Audio/` (NuGet `PortAudioSharp2` 1.0.6; native `libportaudio.so.2`). SBC codec already in Core. Design notes:
   - `PortAudioSharp.Stream` collides with `System.IO.Stream` → aliased `PaStream`. The `Stream.Callback` delegate is held in a field to keep it alive for native code.
   - Device `Id` = PortAudio device index as a string; null/empty = system default. Streams open at `AudioFormat.RadioPcm` (32k/16/mono) and rely on the host API (PipeWire/ALSA) to resample; on bare ALSA hw a device may reject 32 kHz.
   - **Deadlock gotcha (fixed):** the output callback must NOT share a lock with Stop(). `PortAudioPlayback` uses two locks — `lifeLock` (Start/Stop/SetDevice, never taken by the callback) and `bufLock` (ring buffer, taken briefly by the callback) — so the blocking native `Stop()/Close()` can't deadlock against the callback. Capture's callback is lockless.
   - **STILL TODO (Windows-side, unverifiable on Linux):** NAudio impls of `IAudio*` for Windows, and refactoring `RadioAudio.cs`/`Microphone.cs`/`VoiceHandler.cs` off NAudio onto these interfaces. Deferred (like the transport factory) to a Windows/CI-verified pass — see below.
3. **Make Radio.cs portable** — move `Radio.cs` (+ `Gps/`, audio glue) into Core, injecting an `IRadioTransport` factory + `IAudio*` instead of `new RadioBluetoothWin(this)` (Radio.cs:~575). Then the Avalonia app can drive a real radio on Linux. **Prereqs now in place:** the transport (`RadioBluetoothLinux`) and audio (`PortAudio*`) backends both exist and are hardware-validated. Remaining: define the `IRadioTransportFactory` seam, NAudio audio impls, and do the move as one Windows-verified pass (this dev box has no WindowsDesktop SDK, so `src/` WinForms code can't be compiled here — verify on Windows/CI).
4. **Phase 3 — Avalonia UI**:
   - ✅ **C0 (shell) DONE** — `cross/HTCommander.UI.Avalonia/`: `Platform/AvaloniaUiDispatcher.cs` (`IUiDispatcher`), `Platform/CallbackLogger.cs`, `ViewModels/{ViewModelBase,MainViewModel}.cs`, `MainWindow.axaml(.cs)`, composition root in `App.axaml.cs`. Uses manual `INotifyPropertyChanged` (no MVVM toolkit) + compiled bindings (`x:DataType`); button actions wired in code-behind. The connection panel drives the real Linux transport as a working demo.
   - ✅ **Live telemetry (hardware-confirmed)** — the shell polls `GET_HT_STATUS` (BASIC/20, every 1.5s while connected) and parses replies with Core `RadioHtStatus`, binding power/TX-RX/squelch/channel/RSSI/region/GPS into a Radio-tab status panel. Confirmed end-to-end against the UV-PRO via the shell VM (auto-discovered the live GAIA channel, showed Power=On/Idle/etc.).
   - ✅ **`RadioController` (Core) — the portable command/control seed** — `cross/HTCommander.Core/radio/RadioController.cs`. Owns the GAIA BASIC command/response loop over an `IRadioTransport` (builds group/cmd/data payloads; transport does GAIA framing) and **publishes parsed results to `DataBroker`** (`HtStatus`, `BatteryAsPercentage`, `DeviceInfo` as a `RadioDeviceSummary`). On connect it requests device info + battery + HT status and polls HT status. `MainViewModel` no longer parses frames itself — it **subscribes to DataBroker (device 0)** and binds, so the shell is a pure DataBroker consumer (the target architecture). Self-contained: no `src/` changes, parses inline (didn't move `RadioDevInfo`/`RadioSettings`, which use the WinForms `Utils`). **Hardware-confirmed**: FW v144, 30 ch, Battery 50%, live status from the UV-PRO. This is the incremental, Linux-verifiable path toward the full Radio.cs→Core move; extend it next with settings/APRS (DATA_RXD) parsing.
   - ✅ **Channels** — `RadioController` reads every memory channel (`READ_RF_CH`, one per channel after device info) and publishes a `RadioChannelSummary` (id, name, RX/TX Hz, modulation) per channel to `DataBroker "Channel"`. The shell accumulates them into a **Channels tab** (id · name · RX MHz · modulation). Channel parse is inline (dual RX/TX 30-bit freq + 2-bit modulation + UTF-8 name) — `RadioChannelInfo` wasn't moved (it uses the WinForms `Radio` enums). **Hardware-confirmed**: all 30 UV-PRO channels read with correct names/freqs, incl. repeater +5 MHz TX offset and the APRS channel at 144.3900 MHz.
   - ✅ **Settings tab (first real tab)** — `ViewModels/SettingsViewModel.cs`: audio output/input device pickers from `IAudioDeviceEnumerator` (PortAudio), output-volume slider, and a "test tone" through the selected output. Persists via `DataBroker` (device 0) → `JsonConfigStore` using the WinForms keys `OutputAudioDevice`/`InputAudioDevice`/`OutputVolume`. **Verified on Linux**: 25/24 devices enumerated; selection + volume round-trip through `~/.config/htcommander/…json` (incl. the `~~JSON:Single:` type-tag convention) and reload correctly.
   - ⚠️ **Consolidation note:** Phase 1 had left *unused, unimplemented* audio interface stubs in `Core/Abstractions/` (`IAudioDevice`, `IAudioDeviceEnumerator`, `IAudioCapture`, `IAudioPlayback`, `IWaveFormat`, `AudioDataEventArgs`). The implemented + hardware-validated set lives in `Core/Abstractions/Audio/` (`AudioFormat` struct, `AudioDevice` record, `IAudioCapture/Playback/DeviceEnumerator`, `IWaveFileWriter/Reader`). The stubs were **deleted** to remove the name clash; the Windows NAudio impls (still TODO) target the `…Audio` set.
   - **Gotcha — wedged RFCOMM channel:** if a connected process dies UNCLEANLY (SIGKILL, e.g. a test `timeout` firing while connected), the radio holds the SPP RFCOMM DLC open and refuses new connects on that channel (errno 203 on ch1) while other channels still accept; `sdptool` may also hang. The normal `Disconnect()` path closes cleanly and reconnects fine — only unclean kills wedge it. Fix: toggle Bluetooth / power-cycle on the radio. (Don't run hardware tests under an outer `timeout` that can SIGKILL mid-connection; let the test call `Disconnect()` itself.)
   - **C1..Cn (next, parallelizable)** — port one tab per agent: Voice, APRS, Map (GMap.NET → **Mapsui**), Mail, Terminal, Contacts, BBS, Torrent, Packets, Settings. Each binds to the `DataBroker` already in Core. The shell's tab placeholders mark the slots. NOTE: most tab logic still lives in `src/` WinForms code and isn't portable until Radio.cs / the relevant handlers move to Core — port the view + view model and back it with Core services as they become available.
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
