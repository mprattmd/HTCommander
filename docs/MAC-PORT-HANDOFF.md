# macOS port — handoff & implementation guide

Goal: get HTCommander running natively on **macOS** with **Bluetooth working** against a
Benshi UV‑PRO (classic RFCOMM / GAIA). Everything except Bluetooth is essentially free on
macOS; this doc is the plan for the Bluetooth backend plus the dev setup.

> Picked up mid‑project on Linux. Current shipped build: **v0.4.2‑linux**. The app works on
> Linux end‑to‑end (BlueZ). This is about adding a macOS transport.

---

## 0. TL;DR

- Develop **on the Mac** — IOBluetooth only exists on macOS and can only be tested against
  the real radio there.
- The radio is **classic Bluetooth RFCOMM (SPP/GAIA), NOT BLE** → use **IOBluetooth**
  (`IOBluetoothRFCOMMChannel`). **CoreBluetooth will not work.**
- IOBluetooth is Objective‑C with no clean .NET binding → write a tiny **Swift `.dylib`**
  exposing C functions, and **P/Invoke** it from a new **`HTCommander.Platform.Mac`** project.
- Implement two interfaces (`IRadioTransport` + `IRadioTransportDiscovery`) mirroring the
  Linux backend, and add a **platform seam** so the UI picks the Mac backend on macOS.

---

## 1. Dev environment on the Mac

1. Install the **.NET 9 SDK** (`dotnet --version` ≥ 9).
2. Install **Xcode command‑line tools** (`xcode-select --install`) — needed for `swiftc`/clang.
3. IDE (pick one):
   - **JetBrains Rider** — best .NET experience on macOS, great debugger, Avalonia‑aware;
     **free for non‑commercial use**. Recommended.
   - **VS Code + “C# Dev Kit”** (Microsoft) — lighter, free, fine.
   - ❌ Not Visual Studio for Mac (discontinued / EOL 2024).
4. Clone the repo and confirm the baseline builds:
   ```bash
   git clone https://github.com/mprattmd/HTCommander
   cd HTCommander
   dotnet build HTCommander.CrossPlatform.sln
   ```
   The Avalonia UI + Core should build on macOS already. (The Linux platform project
   references BlueZ/Tmds.DBus — it builds but its transport won’t be used on macOS.)
5. **Pair the radio** in macOS **System Settings → Bluetooth** before running the app.
6. You can run **Claude Code on the Mac** to iterate against the real radio.

---

## 2. The architecture seam (what plugs in where)

Two small interfaces in `cross/HTCommander.Core/Abstractions/`:

```csharp
public interface IRadioTransport            // one per radio connection
{
    event Action OnConnected;
    event Action<IRadioTransport, Exception, byte[]> ReceivedData;  // decoded GAIA payloads
    bool Connect();
    void Disconnect();
    void EnqueueWrite(int expectedResponse, byte[] cmdData);        // queue a command
}

public interface IRadioTransportDiscovery    // find paired radios
{
    bool CheckBluetooth();
    IReadOnlyList<string> GetDeviceNames();
    IReadOnlyList<string> FindCompatibleDevices();
    // (the Linux impl also exposes FindCompatibleRadios() -> IReadOnlyList<RadioDeviceInfo>)
}
public sealed record RadioDeviceInfo(string Name, string Address);
```

**Reference implementation (copy its behavior):**
`cross/HTCommander.Platform.Linux/RadioBluetoothLinux.cs` — note especially:
- `Connect()` opens RFCOMM and **discovers the GAIA channel by trial** (the radio reassigns
  its SPP/GAIA RFCOMM channel between sessions — try candidate channels, send a GAIA probe,
  keep the one that replies; see the “GAIA channel N validated” / “no GAIA reply; skipping”
  logic). The Mac code must do the same dance via SDP + channel open.
- It frames/deframes GAIA packets, raising `ReceivedData` with **decoded payloads** (not raw
  RFCOMM bytes). Reuse the same GAIA framing — factor it into Core if convenient so both
  platforms share it.
- `EnqueueWrite` serializes outgoing commands.
- `BlueZRadioDiscovery` (same file) implements discovery + `FindCompatibleRadios()`.

**Current coupling to fix:** `MainViewModel` hard‑references the Linux types:
- `cross/HTCommander.UI.Avalonia/ViewModels/MainViewModel.cs:46` → `new BlueZRadioDiscovery()`
- `…:666` → `new RadioBluetoothLinux(radio.Address, logger, onDisconnected)`

Introduce a **platform factory** so the UI doesn’t name a platform type. Minimal approach:
```csharp
// Core abstraction
public interface IRadioPlatform {
    IRadioTransportDiscovery CreateDiscovery();
    IRadioTransport CreateTransport(string address, ILogger? log, Action<string>? onDisconnected);
}
```
Wire the concrete platform in `App.axaml.cs` based on `OperatingSystem.IsMacOS()` /
`IsLinux()`, and have `MainViewModel` take an `IRadioPlatform` instead of `new`‑ing Linux types.

---

## 3. The Bluetooth bridge — Swift dylib + P/Invoke

### 3a. Swift helper (`mac/htbt/htbt.swift` → `libhtbt.dylib`)

Wrap IOBluetooth classic RFCOMM and expose a **C ABI** with `@_cdecl`. Sketch:

```swift
import Foundation
import IOBluetooth

// Callbacks into .NET (function pointers passed from C#)
typealias DataCallback = @convention(c) (UnsafePointer<UInt8>?, Int32) -> Void
typealias EventCallback = @convention(c) (Int32) -> Void   // 0=connected,1=closed,2=error

final class HtRfcomm: NSObject, IOBluetoothRFCOMMChannelDelegate {
    var channel: IOBluetoothRFCOMMChannel?
    var onData: DataCallback?
    var onEvent: EventCallback?

    func open(addr: String, rfcommChannelID: UInt8) -> Bool {
        guard let dev = IOBluetoothDevice(addressString: addr) else { return false }
        var ch: IOBluetoothRFCOMMChannel?
        let rc = dev.openRFCOMMChannelSync(&ch, withChannelID: rfcommChannelID, delegate: self)
        if rc == kIOReturnSuccess { channel = ch; onEvent?(0); return true }
        return false
    }
    func write(_ p: UnsafePointer<UInt8>, _ n: Int32) {
        channel?.writeSync(UnsafeMutableRawPointer(mutating: p), length: UInt16(n))
    }
    func close() { channel?.close(); channel = nil; onEvent?(1) }

    func rfcommChannelData(_ ch: IOBluetoothRFCOMMChannel!, data: UnsafeMutableRawPointer!, length: Int) {
        onData?(data.assumingMemoryBound(to: UInt8.self), Int32(length))
    }
    func rfcommChannelClosed(_ ch: IOBluetoothRFCOMMChannel!) { onEvent?(1) }
}

// Discovery: enumerate paired devices, read SDP for the SPP/GAIA service channel.
// IOBluetoothDevice.pairedDevices(); for each, .getServiceRecord(for: kBluetoothSDPUUID16ServiceClassSerialPort)
// and read the RFCOMM channel id from the service record.

// --- C exports (objects tracked by an int handle in a dictionary) ---
@_cdecl("htbt_open")  public func htbt_open(_ addr: UnsafePointer<CChar>, _ chan: UInt8,
    _ onData: DataCallback, _ onEvent: EventCallback) -> Int32 { /* create HtRfcomm, return handle */ }
@_cdecl("htbt_write") public func htbt_write(_ h: Int32, _ p: UnsafePointer<UInt8>, _ n: Int32) { }
@_cdecl("htbt_close") public func htbt_close(_ h: Int32) { }
@_cdecl("htbt_list_radios") public func htbt_list_radios(/* out buffer */) -> Int32 { /* name|addr|channel pairs */ }
```

Build:
```bash
swiftc -emit-library -o libhtbt.dylib mac/htbt/htbt.swift \
  -framework Foundation -framework IOBluetooth
```
> IOBluetooth runs its delegate callbacks on a run loop — make sure the channel is opened on a
> thread with a live `RunLoop` (or use `openRFCOMMChannelAsync` + spin a run loop). Easiest:
> dedicate a thread that calls `RunLoop.current.run()`.

### 3b. C# side (`cross/HTCommander.Platform.Mac/`)

New project referencing `HTCommander.Core`. `MacRadioTransport : IRadioTransport`:

```csharp
internal static class Native {
    const string Lib = "htbt";   // libhtbt.dylib next to the app
    public delegate void DataCb(IntPtr data, int len);
    public delegate void EventCb(int kind);
    [DllImport(Lib)] public static extern int  htbt_open(string addr, byte chan, DataCb d, EventCb e);
    [DllImport(Lib)] public static extern void htbt_write(int h, byte[] p, int n);
    [DllImport(Lib)] public static extern void htbt_close(int h);
}
```
- `Connect()`: discover the GAIA RFCOMM channel (via `htbt_list_radios` SDP info, then the same
  **probe‑and‑validate** trial as Linux), `htbt_open`, raise `OnConnected`.
- Marshal the `DataCb` bytes → run them through the **shared GAIA deframer** → raise
  `ReceivedData` with decoded payloads (match `RadioBluetoothLinux` exactly).
- `EnqueueWrite` → frame the command → `htbt_write`.
- **Keep the delegate instances alive** (store as fields) so the GC doesn’t collect the
  callbacks passed to native code.

`MacRadioDiscovery : IRadioTransportDiscovery` → wrap `htbt_list_radios`, filter to compatible
radio names (same name heuristics as `BlueZRadioDiscovery.FindCompatibleRadios`).

### 3c. Packaging / permissions
- For a terminal `dotnet run`, macOS will **prompt for Bluetooth permission** the first time.
- For a distributable `.app` bundle, add `NSBluetoothAlwaysUsageDescription` to `Info.plist`
  (and the Bluetooth entitlement if you sandbox/notarize). Ship `libhtbt.dylib` alongside the
  executable (or set `rpath`).

---

## 4. Step‑by‑step

1. **Branch:** `git checkout -b mac-bluetooth`.
2. Confirm `dotnet build HTCommander.CrossPlatform.sln` works on the Mac.
3. Write + build `libhtbt.dylib`; test it standalone with a tiny Swift `main` that opens the
   channel and prints incoming bytes (proves IOBluetooth RFCOMM works to your paired radio).
4. Create `cross/HTCommander.Platform.Mac/` with `MacRadioTransport` + `MacRadioDiscovery`
   (P/Invoke the dylib). Reuse the Linux GAIA framing/validation logic (factor it into Core if
   you want both to share it).
5. Add the `IRadioPlatform` seam; pick Linux vs Mac in `App.axaml.cs`; stop `MainViewModel`
   from `new`‑ing Linux types.
6. Run, pick the radio, Connect — verify “GAIA channel N validated” and live status/battery.
7. Audio: PortAudio has a **CoreAudio** backend, so voice/clips/waterfall should work; verify
   device enumeration on macOS and fix any device‑id quirks.
8. Package a `.app` (optional) and add the Bluetooth Info.plist key.

**Definition of done:** connect to the UV‑PRO over Bluetooth on macOS; see live telemetry;
send/receive a packet (APRS) on 1200 to confirm the data path. (Then everything else already
works.)

---

## 4b. Findings from the first on-radio session (2026-05-30, macOS)

**The macOS IOBluetooth path WORKS — the radio answers GAIA on the Mac.** Verified live:
a clean scan opened RFCOMM channels and the UV-PRO replied to a GAIA `GET_DEV_INFO`
with a full 19-byte device-info frame (`FF 01 00 0B 00 02 80 04 …`) on **ch1 and ch4**.

What it took to get there (each a real gotcha for the bridge):
1. **GAIA probe length byte.** The probe must be `FF 01 00 01 00 02 00 04 03` — the
   payload-length byte (index 3) is `cmd.Length-4 = 1`, NOT the full 5. A wrong length
   byte makes the radio **silently ignore the frame** (channel opens, zero reply) — this
   was the single thing that masked everything else. Fixed in `htbt.swift`.
2. **Async open, not sync.** `openRFCOMMChannelSync` returns a generic
   `kIOReturnError 0xE00002BC`; use `openRFCOMMChannelAsync` + `rfcommChannelOpenComplete:`
   and pump the run loop.
3. **Warm the link first.** A cold `openRFCOMMChannelAsync` against an idle/disconnected
   radio tends not to complete; call `IOBluetoothDevice.openConnection()` first.
4. **Channel is dynamic + SDP is unreliable.** SDP showed SerialPort `0x1101` ("SPP Dev")
   on ch1, but GAIA also answered on ch4 (not in SDP). The bridge now probes SDP channels
   first, then falls back to a full 1..30 GAIA-validated scan.
5. **Radio wedges after repeated opens.** Many rapid open/probe cycles wedge the radio's
   RFCOMM (same as the Linux note); clear it by toggling Bluetooth / power-cycling the
   radio. The reliable runs were right after a fresh connect.

SDP map macOS sees (FYI; channels are dynamic): Voice Gateway `0x111F` (HFP AG) → ch1,
Voice Gateway `0x1203` (GenericAudio) → ch1, **SerialPort `0x1101` ("SPP Dev") → ch1**,
BS AOC (custom 128-bit) → ch2. (The earlier "HFP holds ch1 so GAIA is blocked" theory was
a red herring — the probe bug was the real cause.)

**RESOLVED — the bridge connects end-to-end.** After fixing three things together, the
standalone `htbt-test` connected to the UV-PRO through the real bridge code path:
`openComplete status=0x0` on the probed channels, `ch1: GAIA validated (19 bytes)`,
`RX 19: FF 01 00 0B 00 02 80 04 …` device-info frame delivered to the C# `onData` callback,
`Connected, handle=1`. The fixes:
1. **Event-driven state machine on the MAIN run loop.** IOBluetooth delivers RFCOMM
   open-completion + data on the main run loop. The probe must NOT nest a run loop (inside
   a worker thread OR inside a GCD main-queue block — both starve the callbacks → every
   open times out). `HtRfcomm` now drives open→complete→write→data/timeout purely via the
   delegate + `Timer`s on the main run loop. `htbt_connect` is called off-main (background
   Task / Thread) and dispatches the kickoff to main; the host must pump the main run loop.
2. **SDP-only probing by default.** A full 1..30 sweep WEDGES this radio's RFCOMM; probe
   only the SDP-advertised channels (the SerialPort/SPP `0x1101` GAIA channel is among
   them). `HTBT_FULLSCAN=1` re-enables the full sweep if ever needed.
3. The corrected probe byte (above).

**Managed stack VERIFIED end-to-end (headless).** A small console harness (CFRunLoopRun on
the main thread to mimic Avalonia, connect on a background thread) drove the real C# types:
`MacRadioDiscovery` enumerated the UV-PRO, `MacRadioTransport.Connect` opened the GAIA
channel, the initial device-info frame was decoded by the C# GAIA deframer
(`00 02 80 04 …` = `0x8002` GET_DEV_INFO reply), `OnConnected` fired, then a GET_DEV_INFO
sent via `EnqueueWrite` got a fresh decoded reply — **full TX→RX round-trip through the
managed transport.** Fix that made it work: `MacRadioTransport` now sets an `accepting`
flag before `NativeHtbt.Connect` so the channel-validation reply (delivered DURING connect)
isn't dropped. This is the exact path `RadioController` uses, so telemetry/channels/settings
will flow.

**Remaining (next session): launch the actual GUI.** Plumbing is fully done + verified
(`IRadioPlatform` → `MacRadioPlatform` → `MacRadioTransport` → bridge → radio). Run
`dotnet run --project cross/HTCommander.UI.Avalonia` on the Mac (needs a display + a click
on Connect), confirm telemetry/battery/channels render, then a 1200 APRS packet (the §4
definition of done). Voice RX/TX on macOS is still deferred (uses Linux audio-channel
types). NOTE: the radio wedges after unclean disconnects / full 1..30 scans — toggle its
Bluetooth to clear. (A throwaway harness lives at `/tmp/macprobe` — re-creatable from this
doc; not committed.)

Diagnostics live in the bridge behind `HTBT_DEBUG=1` (per-channel open status + probe
bytes). The standalone `htbt-test` (see README) is the iteration tool.

## 4c. Voice channel — wired, needs hardware verification

The radio's voice audio is a SECOND RFCOMM stream (the SDP service named **"BS AOC"**, ch2
in the macOS SDP dump — NOT the silent 0x1203 HFP gateway). It is now plumbed end-to-end,
mirroring the Linux path:
- **Bridge:** `libhtbt` gained `HtAudio` + exports `htbt_open_audio(addr, nameSubstr, …)` /
  `htbt_audio_write` / `htbt_audio_close`. It opens the SDP-named channel (match "AOC") and
  forwards RAW bytes both ways (no GAIA probe), same main-run-loop discipline as the command
  channel.
- **Seam:** new Core `IRadioAudioChannel` (Connect/Send/Disconnect/DataReceived); both
  `RadioAudioChannelLinux` and the new `RadioAudioChannelMac` implement it; created via
  `IRadioPlatform.CreateAudioChannel`. `MainViewModel.StartVoiceRx` now goes through the seam
  instead of `new RadioAudioChannelLinux`.
- **Reuses existing Core/audio:** `RadioVoiceReceiver`/`RadioVoiceTransmitter` (SBC) + PortAudio
  playback/capture — already cross-platform.

**Builds clean; NOT yet hardware-verified.** To test on the Mac: `brew install portaudio` (the
PortAudio backend needs the native `libportaudio.dylib`), connect, toggle **Voice RX**, and key
a nearby radio — you should hear it. Watch `HTBT_DEBUG=1` for `audio: opening 'AOC' on RFCOMM
ch N` / `audio: channel open`. Caveat from Linux: the audio channel competes with the hardware
TNC's TX audio, so it's opened on demand (Voice RX toggle), not on connect. TX (PTT) is
implemented but keys the radio on the air — operator-gated.

## 5. Context you’ll want (gotchas already learned on Linux)

- **GAIA RFCOMM channel moves between sessions** — never hard‑code it; discover + probe.
- **Packet TX/RX uses the radio’s hardware TNC = 1200‑baud AFSK AX.25.** No VARA, no 9600.
- **Radio “Digital mode” must be OFF** to use the app/TNC path (Winlink/BBS/App‑TNC beacon);
  Digital mode is only for the radio’s built‑in beacon and disables the TNC.
- **Winlink over radio** needs a **1200 packet RMS gateway** in range; VARA FM gateways are
  unreachable with this hardware. Winlink over the internet works.
- **Bluetooth audio (HFP/AOC) is opened on demand** (Voice RX toggle), *not* on connect —
  holding it open starves the TNC’s transmit audio. Keep that behavior on macOS.
- Don’t `pkill -f` the app by name on a dev box if other tools match the pattern — kill by PID.

---

## 6. Current app status (Linux, v0.4.2)

Working: Bluetooth connect, telemetry, voice, APRS RX/TX (1200), map (+aprs.fi), GPS,
channels (incl. click‑a‑tile editor), Winlink mail (internet sync; local store + folders),
BBS host, packet capture, settings (auto‑save). Recent fixes: packet TX/RX parity pass,
mail folder list/counts, mail move‑to‑Sent / received‑mail persistence.

Not ported / out of scope: TTS/STT, SSTV, VARA, AGWPE, YAPP/torrent, web server, ADS‑B,
self‑update, detached tabs. See `docs/PARITY.md`.

---

## 7. How to resume with Claude on the Mac

Open this repo on the Mac and say something like:
> “Read docs/MAC-PORT-HANDOFF.md and scaffold `HTCommander.Platform.Mac` + the `libhtbt`
> Swift IOBluetooth RFCOMM helper, then wire the `IRadioPlatform` seam.”

I can scaffold both the Swift helper and the C# transport from anywhere; the build/test loop
(Bluetooth against the real radio) has to happen on the Mac.
