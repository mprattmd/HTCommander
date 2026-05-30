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
