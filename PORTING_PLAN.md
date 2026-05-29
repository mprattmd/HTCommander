# HTCommander Cross-Platform Port Plan

**Goal:** Port HTCommander from Windows-only (.NET 9 WinForms) to a cross-platform app running on Windows, Linux, and macOS.

**Fork:** `mprattmd/HTCommander` (origin) ← forked from `Ylianst/HTCommander` (upstream).

This plan is based on a full evaluation of the codebase (5 parallel analysis passes: UI, Bluetooth, audio/speech, settings/platform APIs, and portable core).

---

## 1. Strategy

WinForms → **Avalonia** (cross-platform XAML UI; also covers macOS). Extract all platform-neutral logic into a shared `HTCommander.Core` library, isolate platform-specific concerns (settings, audio, Bluetooth) behind interfaces, and build the Avalonia UI on top.

### First-cut scope (deliberately deferred to keep momentum)
- ❌ Whisper STT (Whisper.net) — cleanly flagged off
- ❌ System.Speech TTS — Windows-only, `#if WINDOWS`
- ❌ Spectrogram view + WaveformGenerator — UI-only, non-critical
- ✅ Everything else: radio control, APRS, AX.25, BBS, terminal, mail, audio I/O, map

---

## 2. Target Solution Structure

```
HTCommander.Core            (net9.0)          ← portable: protocols, codecs, DSP, broker, abstractions
HTCommander.Platform.Win    (net9.0-windows)  ← Registry, NAudio, WinRT Bluetooth, System.Speech
HTCommander.Platform.Linux  (net9.0)          ← JSON config, PortAudio, BlueZ/D-Bus
HTCommander.Platform.Mac    (net9.0)          ← JSON config, PortAudio, CoreBluetooth (⚠ see §6)
HTCommander.UI.Avalonia     (net9.0)          ← cross-platform UI (the big lift)
HTCommander.Windows         (net9.0-windows)  ← existing WinForms app, kept buildable during transition
```

`RuntimeIdentifiers`: `win-x64;win-x86;linux-x64;osx-x64;osx-arm64`

---

## 3. Codebase Evaluation Summary

### Tier 1 — Portable as-is (~3,500+ LOC, zero changes) → move into `HTCommander.Core`
`AprsParser/*`, `AX25Address.cs`, `AX25Packet.cs`, `BSSPacket.cs`, `RadioBssSettings.cs`, `TncDataFragment.cs`, `hamlib/*` (25 files: AFSK/PSK/G3RUH/HDLC/FX.25), `sbc/*` (8 files: SBC codec), `MorseCodeEngine.cs`, `DmtfEngine.cs`, `AgwpeServerClass.cs`, `DataBrokerClient.cs`. All use only `System.*`.

### Tier 2 — Mostly portable (minor abstraction)
| File | Coupling | Fix |
|------|----------|-----|
| `Utils/DataBroker.cs` | `System.Windows.Forms.Control _uiContext` for thread marshalling | Introduce `IUiDispatcher` (WinForms `Invoke` / Avalonia `Dispatcher.UIThread`) |
| `radio/AX25Session.cs` | `DataBrokerClient` | portable once broker abstracted |
| `radio/SoftwareModem.cs` | `System.Timers.Timer`, DataBroker | injectable timer + broker |
| `BBS.cs`, `Yapp.cs`, `WinLink/WinlinkClient.cs` | `using System.Windows.Forms;` + `MainForm` ref (logging/UI) | extract protocol core, replace `MainForm` → `ILogger` |
| `AprsStack.cs` | `MainForm` ref | `IAprsTransport` interface |

### Tier 3 — Coupled, needs rework
| File | Coupling | Fix |
|------|----------|-----|
| `Utils/RegistryHelper.cs` | `Microsoft.Win32` | `IConfigStore` → JSON impl (see §4) |
| `WebServerClass.cs` | `Windows.Storage.Streams` (WinRT), `MainForm` | use `System.Net.WebSockets`, `ILogger` |
| `radio/Utils.cs` | `DllImport("user32.dll")` SendMessage | `#if WINDOWS`, no-op elsewhere |

### Tier 4 — UI (out of core; rewritten in Avalonia) — see §7

---

## 4. Settings: Registry → JSON ✅ clean

Registry access is **fully centralized**: all persistence flows through `Utils/RegistryHelper.cs`, driven by `DataBroker` "device 0" (104 known settings). `Program.cs` calls `DataBroker.Initialize("HTCommander")`.

**Plan:** Define `IConfigStore` (`ReadString/Int/Bool`, `Write*`, `DeleteValue`). Implement:
- `RegistryConfigStore` (Windows, = current behavior)
- `JsonConfigStore` (Linux/macOS): `~/.config/htcommander/settings.json` (XDG) / `~/Library/Application Support/HTCommander/`. Preserve the existing `~~JSON:Type:` complex-value convention.

`Environment.SpecialFolder.LocalApplicationData` is already used for app data (`VoiceHandler`, `BBS`, `Yapp`) and resolves correctly per-OS — minor: prefer XDG paths on Linux.

`System.IO.Ports` (GPS) is **already cross-platform** — no hardcoded `COMx`. Zero changes.

---

## 5. Audio: NAudio → PortAudio ✅ feasible

NAudio is used directly in **11 files** (no existing abstraction). Introduce `IAudioCapture`, `IAudioPlayback`, `IAudioDeviceEnumerator`, `IWaveFormat`, `IWaveFileReader/Writer`. Two impls: `NAudio*` (Windows) and `PortAudio*` (cross-platform).

- **Critical paths:** `radio/RadioAudio.cs` (1,283 LOC, WASAPI out) → `IAudioPlayback`; `radio/Microphone.cs` (132 LOC, WASAPI capture) → `IAudioCapture`.
- **SBC codec (`sbc/*`)** is pure C# — carries over free.
- **Defer:** `SpectrogramForm` (Spectrogram pkg), `WaveformGenerator` — skip in first cut.

---

## 6. Bluetooth ⚠️ the real risk

The radios use **Classic Bluetooth RFCOMM / SPP** (not BLE GATT) — confirmed in `RadioBluetoothWin.cs` (`RfcommServiceId.SerialPort`, `StreamSocket`). There is **no transport abstraction today**: `Radio.cs` hardcodes `new RadioBluetoothWin(this)` and `MainForm.cs` calls its statics directly.

**Plan:**
1. Define `IRadioTransport` (`Connect/Disconnect`, `EnqueueWrite`, events `OnConnected`/`ReceivedData`, statics `CheckBluetooth`/`FindCompatibleDevices`/`GetDeviceNames`). Refactor `Radio.cs` + `MainForm.cs` to select impl via `RuntimeInformation.IsOSPlatform`.
2. **Linux:** finish the existing `radio/RadioBluetoothLinux.cs` BlueZ/D-Bus stub (~133 → ~400 LOC). Missing: device filtering, RFCOMM service discovery, socket/stream wrapping, async read loop, GAIA framing (reuse Windows logic), events, disconnect/cleanup, retries. Currently excluded from build (`<Compile Remove>` in csproj) + needs `Tmds.DBus` package.
3. **macOS — BLOCKER:** CoreBluetooth has **no public API for classic RFCOMM**. Options: (a) private `IOBluetooth` APIs (fragile, no App Store), (b) BLE GATT would need radio firmware support, (c) ship macOS UI but radio connectivity stubbed initially. **"Avalonia gets macOS for free" applies to UI/build, not radio connectivity.** Recommend: Linux first for full radio support; macOS as UI-complete + Bluetooth follow-up.

---

## 7. UI: WinForms → Avalonia (largest effort, highly parallelizable)

~23,400 LOC UI, 59 `.Designer.cs`, 39 dialogs, 11 tab controls, 7 reusable controls. The `DataBroker` pub/sub backbone is portable, so each tab/dialog is a **mostly independent** port — ideal for parallel agents.

**Risk-ranked:**
- 🔴 **GMap.NET.WinForms** (`MapTabUserControl`, `MapLocationForm`, `CustomTilePrefetcher`, `AirplaneMarker`) — no Avalonia support. Replace with **Mapsui** (Avalonia-native).
- 🔴 **Custom painting** (6 files: `VoiceControl`, `ChatControl`, `RadioPanelControl`, `AmplitudeHistoryBar`, `TorrentBlocksUserControl`, `MailAttachmentControl`) — port `System.Drawing` → Avalonia `DrawingContext`/`FormattedText`.
- 🟡 **Designer layouts** (59 files) — manual XAML conversion.
- 🟡 **Thread marshalling** (`InvokeRequired`/`BeginInvoke`, 11 files) → `Dispatcher.UIThread.InvokeAsync` (formulaic; subsumed by `IUiDispatcher`).
- 🟡 **Drag & drop** (~64 refs) → Avalonia `DragDrop`.
- 🟢 Standard dialogs/controls → direct XAML.

---

## 8. Phased Workstreams (★ = parallelizable across agents)

**Phase 0 — Scaffolding (serial, do first)**
- Create multi-project solution (§2), wire RIDs, keep `HTCommander.Windows` (WinForms) compiling as the reference build.

**Phase 1 — Core + Abstractions (mostly ★)**
- ★ A1: Move Tier 1 files into `HTCommander.Core` (mechanical).
- ★ A2: Define interfaces — `IConfigStore`, `IUiDispatcher`, `ILogger`, `ITimer`, `IAudio*`, `IRadioTransport`, `IAprsTransport`.
- ★ A3: Refactor Tier 2 files to depend on interfaces (DataBroker, AX25Session, SoftwareModem, BBS, Yapp, Winlink, AprsStack).

**Phase 2 — Platform backends (★ all independent)**
- ★ B1: `JsonConfigStore` (Registry → JSON).
- ★ B2: PortAudio audio impls + NAudio impls behind `IAudio*`.
- ★ B3: Finish Linux BlueZ `IRadioTransport`; refactor `Radio.cs`/`MainForm.cs` to factory.
- ★ B4: `#if WINDOWS`-guard P/Invoke, System.Speech, Whisper, Spectrogram, app.manifest.

**Phase 3 — Avalonia UI (★ per-tab/dialog, large fan-out)**
- C0: Avalonia app shell + MainForm equivalent + `IUiDispatcher` impl (serial, first).
- ★ C1..Cn: one agent per tab/dialog (Voice, APRS, Map→Mapsui, Mail, Terminal, Contacts, BBS, Torrent, Packets, Debug, Settings, + custom controls).

**Phase 4 — Integration & packaging**
- Linux first (full radio), then macOS (UI-complete, BT follow-up). AppImage/deb (Linux), DMG (macOS).

---

## 9. Immediate Next Steps
1. Install .NET 9 SDK on the dev Mac (see README/setup).
2. Phase 0 scaffolding.
3. Fan out Phase 1–2 agents (A1/A2 first, since everything depends on the abstractions).
