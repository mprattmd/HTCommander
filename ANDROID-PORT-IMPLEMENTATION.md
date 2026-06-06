# HTCommander Android — Implementation Guide

Companion to [ANDROID-PORT-PLAN.md](ANDROID-PORT-PLAN.md) (the *why* and scope). This is the *how*: a sequenced checklist with real signatures from the current seam and copy-pasteable skeletons. **Round one = radio control + data only; audio is a no-op stub** (see plan §6).

Work top to bottom. Each step has a "done when" so you can stop and resume cleanly.

---

## Step 0 — RFCOMM spike (retire the biggest risk first)

**Goal:** prove an Android device can open Classic RFCOMM to the radio and get a GAIA reply, before building anything else.

1. Pair the radio to the Android phone in system Bluetooth settings (bonding is required — Android RFCOMM only connects to bonded devices).
2. Minimal Android app (Kotlin throwaway is fine, or jump straight to the .NET skeleton in Step 2). Pseudocode:
   ```
   adapter   = BluetoothAdapter.getDefaultAdapter()
   device    = adapter.getRemoteDevice("AA:BB:CC:DD:EE:FF")   // radio BD_ADDR
   socket    = device.createRfcommSocketToServiceRecord(SPP_UUID)  // 00001101-0000-1000-8000-00805F9B34FB
   adapter.cancelDiscovery()
   socket.connect()
   socket.outputStream.write(gaiaProbeFrame)   // FF 01 ... — reuse a known GAIA "get device info"
   read socket.inputStream → expect a frame starting FF 01
   ```
3. Reuse a real GAIA probe frame from the existing transports (the Mac/Linux backends already build one — grep for the `0xFF, 0x01` framing).

**Done when:** you see a `FF 01` header come back over the socket. If this fails or the channel won't open, stop and rethink before investing in the UI head — this is the load-bearing assumption.

> If you'd rather spike directly in .NET, skip to Step 1+2, build `HTCommander.Platform.Android`, and run the transport from a tiny console/activity. Same risk retired, less throwaway code.

---

## Step 1 — Scaffold `HTCommander.Platform.Android`

Mirror the Mac backend's project shape. New folder `cross/HTCommander.Platform.Android/`.

`HTCommander.Platform.Android.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0-android</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>annotations</Nullable>   <!-- matches Core / Platform.Mac -->
    <SupportedOSPlatformVersion>26</SupportedOSPlatformVersion> <!-- API 26+; 31+ for new BT perms -->
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\HTCommander.Core\HTCommander.Core.csproj" />
  </ItemGroup>
</Project>
```

Add it to `HTCommander.CrossPlatform.sln`.

**Done when:** `dotnet build cross/HTCommander.Platform.Android` succeeds (you may need the Android workload: `dotnet workload install android`).

---

## Step 2 — `AndroidRadioTransport : IRadioTransport`

The interface you must satisfy (from [IRadioTransport.cs](cross/HTCommander.Core/Abstractions/IRadioTransport.cs)):
```csharp
public interface IRadioTransport {
    event Action OnConnected;
    event Action<IRadioTransport, Exception, byte[]> ReceivedData;
    bool Connect();
    void Disconnect();
    void EnqueueWrite(int expectedResponse, byte[] cmdData);
}
```

Skeleton (`AndroidRadioTransport.cs`):
```csharp
using Android.Bluetooth;
using HTCommander.Core.Abstractions;
using Java.Util;

namespace HTCommander.Platform.Android;

public sealed class AndroidRadioTransport : IRadioTransport
{
    // Standard Serial Port Profile UUID — the radio advertises SPP/RFCOMM.
    static readonly UUID SppUuid = UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")!;

    readonly string _address;
    readonly ILogger? _log;
    readonly Action<string>? _onDisconnected;
    BluetoothSocket? _socket;
    Thread? _readLoop;
    volatile bool _running;

    public event Action? OnConnected;
    public event Action<IRadioTransport, Exception, byte[]>? ReceivedData;

    public AndroidRadioTransport(string address, ILogger? log, Action<string>? onDisconnected)
        { _address = address; _log = log; _onDisconnected = onDisconnected; }

    public bool Connect()
    {
        if (_running) return false;
        var adapter = BluetoothAdapter.DefaultAdapter;
        var device  = adapter?.GetRemoteDevice(_address);
        if (device is null) return false;

        _socket = device.CreateRfcommSocketToServiceRecord(SppUuid);
        adapter!.CancelDiscovery();          // discovery slows/breaks connect
        _socket!.Connect();                  // blocking; consider running off the UI thread
        _running = true;
        OnConnected?.Invoke();

        _readLoop = new Thread(ReadLoop) { IsBackground = true };
        _readLoop.Start();
        return true;
    }

    void ReadLoop()
    {
        var buf = new byte[4096];
        try {
            var stream = _socket!.InputStream!;
            while (_running) {
                int n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                var chunk = new byte[n];
                Array.Copy(buf, chunk, n);
                // TODO: feed `chunk` into the SAME GAIA de-framer the Mac/Linux
                // transports use, then raise ReceivedData per decoded payload.
                ReceivedData?.Invoke(this, null!, chunk);
            }
        }
        catch (Exception ex) { ReceivedData?.Invoke(this, ex, null!); }
        finally { _running = false; _onDisconnected?.Invoke("read loop ended"); }
    }

    public void EnqueueWrite(int expectedResponse, byte[] cmdData)
    {
        // Round one can write synchronously; add a queue if ordering/backpressure bites.
        try { _socket?.OutputStream?.Write(cmdData, 0, cmdData.Length); }
        catch (Exception ex) { _log?.Log($"write failed: {ex.Message}"); }
    }

    public void Disconnect()
    {
        _running = false;
        try { _socket?.Close(); } catch { }
        _socket = null;
    }
}
```

**Key reuse:** do **not** reinvent GAIA framing — lift the encode/de-frame logic out of `MacRadioTransport`/`RadioBluetoothLinux` into the read/write paths above (ideally hoist the shared framer into Core if it isn't already).

**Done when:** transport connects and `ReceivedData` fires with a decoded GAIA payload (this subsumes Step 0).

---

## Step 3 — `AndroidRadioDiscovery : IRadioTransportDiscovery`

Interface: `CheckBluetooth()`, `GetDeviceNames()`, `FindCompatibleDevices()`, `FindCompatibleRadios()` → `IReadOnlyList<RadioDeviceInfo>` (record `(string Name, string Address)`).

```csharp
public sealed class AndroidRadioDiscovery : IRadioTransportDiscovery
{
    public bool CheckBluetooth()
        => BluetoothAdapter.DefaultAdapter?.IsEnabled == true;

    public IReadOnlyList<string> GetDeviceNames()
        => Bonded().Select(d => d.Name ?? d.Address!).ToList();

    public IReadOnlyList<string> FindCompatibleDevices()
        => FindCompatibleRadios().Select(r => r.Name).ToList();

    public IReadOnlyList<RadioDeviceInfo> FindCompatibleRadios()
        => Bonded()
            .Where(IsRadio)   // TODO: match by name prefix / device class as the desktop backends do
            .Select(d => new RadioDeviceInfo(d.Name ?? d.Address!, d.Address!))
            .ToList();

    static IEnumerable<BluetoothDevice> Bonded()
        => BluetoothAdapter.DefaultAdapter?.BondedDevices ?? Enumerable.Empty<BluetoothDevice>();

    static bool IsRadio(BluetoothDevice d) => true; // refine using the existing name filter
}
```

Note: Android enumerates **bonded** devices only — there is no inquiry-and-connect path like the native desktop backends. Reuse whatever name/class filter `FindCompatibleRadios` uses elsewhere.

**Done when:** the paired radio shows up in `FindCompatibleRadios()`.

---

## Step 4 — Audio channel: no-op stub (round one)

Satisfy `IRadioAudioChannel` without doing audio. This lets `AndroidRadioPlatform` compile and the UI gate voice off.

```csharp
public sealed class AndroidRadioAudioChannelStub : IRadioAudioChannel
{
    public event Action<byte[], int>? DataReceived;   // never raised in round one
    public bool Connect(int channel = 0) => false;     // audio unavailable on Android v1
    public bool Send(byte[] data) => false;
    public void Disconnect() { }
}
```

**Done when:** it compiles. (Round two replaces this — see plan §6.)

---

## Step 5 — `AndroidRadioPlatform : IRadioPlatform`

Mirror [MacRadioPlatform.cs](cross/HTCommander.Platform.Mac/MacRadioPlatform.cs):
```csharp
public sealed class AndroidRadioPlatform : IRadioPlatform
{
    public IRadioTransportDiscovery CreateDiscovery() => new AndroidRadioDiscovery();

    public IRadioTransport CreateTransport(string address, ILogger? logger = null, Action<string>? onDisconnected = null)
        => new AndroidRadioTransport(address, logger, onDisconnected);

    public IRadioAudioChannel CreateAudioChannel(string address, ILogger? logger = null)
        => new AndroidRadioAudioChannelStub();   // round one: audio deferred
}
```

---

## Step 6 — Config storage

[JsonConfigStore](cross/HTCommander.Platform.Linux/JsonConfigStore.cs) is plain file I/O. Either reuse it pointed at Android app-private storage, or add a thin Android path override. Target dir: `Application.Context.FilesDir.AbsolutePath` (or `Environment.SpecialFolder.LocalApplicationData`, which resolves to app-private storage on Android). Keep the same JSON format and `~~JSON:Type:` convention.

**Done when:** settings round-trip (write a value, restart the app, read it back).

---

## Step 7 — Register the backend in the composition root

In [App.axaml.cs](cross/HTCommander.UI.Avalonia/App.axaml.cs) the selection today is (line ~55):
```csharp
IRadioPlatform radioPlatform = OperatingSystem.IsMacOS()
    ? new HTCommander.Platform.Mac.MacRadioPlatform()
    : new LinuxRadioPlatform();
```
Make it:
```csharp
IRadioPlatform radioPlatform =
      OperatingSystem.IsAndroid() ? new HTCommander.Platform.Android.AndroidRadioPlatform()
    : OperatingSystem.IsMacOS()   ? new HTCommander.Platform.Mac.MacRadioPlatform()
    :                               new LinuxRadioPlatform();
```

**Project references are TFM-conditional** — the Linux/Mac backends won't build for `net9.0-android` and vice-versa. In the UI csproj, gate them:
```xml
<ItemGroup Condition="'$(TargetFramework)' != 'net9.0-android'">
  <ProjectReference Include="..\HTCommander.Platform.Linux\HTCommander.Platform.Linux.csproj" />
  <ProjectReference Include="..\HTCommander.Platform.Mac\HTCommander.Platform.Mac.csproj" />
</ItemGroup>
<ItemGroup Condition="'$(TargetFramework)' == 'net9.0-android'">
  <ProjectReference Include="..\HTCommander.Platform.Android\HTCommander.Platform.Android.csproj" />
</ItemGroup>
```
The `OperatingSystem.IsAndroid()` branch is unreachable on desktop builds (and the type isn't referenced there), so guard the `new AndroidRadioPlatform()` line behind `#if ANDROID` if the desktop compile can't see the type.

---

## Step 8 — Avalonia Android head

Add a `net9.0-android` head so the UI runs on a device. Two options:
- **Multi-target** the existing `HTCommander.UI.Avalonia` (`<TargetFrameworks>net9.0;net9.0-android</TargetFrameworks>`) and add an Android `MainActivity` + `SplashActivity`, or
- **Separate `HTCommander.Android` head project** referencing the UI project (cleaner separation; recommended).

Either way you need:
- `Avalonia.Android` package (same 12.0.4 line as the desktop packages).
- An `[Activity]` deriving from `AvaloniaMainActivity<App>`.
- `AndroidManifest.xml` with Bluetooth permissions (Step 9).

**Done when:** the app launches on an emulator/device and shows the main window.

---

## Step 9 — Manifest & runtime permissions

`AndroidManifest.xml`:
```xml
<!-- Android 12+ (API 31+) -->
<uses-permission android:name="android.permission.BLUETOOTH_CONNECT" />
<uses-permission android:name="android.permission.BLUETOOTH_SCAN" />
<!-- Legacy, pre-31 -->
<uses-permission android:name="android.permission.BLUETOOTH" android:maxSdkVersion="30" />
<uses-permission android:name="android.permission.BLUETOOTH_ADMIN" android:maxSdkVersion="30" />
```
`BLUETOOTH_CONNECT`/`BLUETOOTH_SCAN` are **runtime** permissions on API 31+ — request them (e.g. `ActivityCompat.RequestPermissions`) before discovery/connect, and handle denial.

**Done when:** the app prompts for Bluetooth permission and, once granted, completes Step 2's connect on-device.

---

## Step 10 — On-radio integration & APK

1. Wire the data-mode tabs (Settings, APRS, Packets, Terminal, Mail, BBS); **hide voice/audio UI** on Android (`OperatingSystem.IsAndroid()` gate).
2. Touch/layout passes on those tabs for phone form factor.
3. `dotnet publish -f net9.0-android -c Release` → signed APK; test on a real device against the radio.

---

## Round-one acceptance checklist
- [ ] Phone pairs with the radio; it appears in `FindCompatibleRadios()`.
- [ ] App requests + receives Bluetooth runtime permission.
- [ ] Transport connects over RFCOMM; `OnConnected` fires.
- [ ] A GAIA command round-trips (write → decoded `ReceivedData`).
- [ ] Settings read/write persists across restart.
- [ ] At least one data tab (e.g. Settings or APRS) is usable on a phone screen.
- [ ] Voice/audio UI is hidden; `CreateAudioChannel` returns the stub.

## Deferred to round two (tracked, not forgotten)
- Real `AndroidRadioAudioChannel` over the second RFCOMM stream + audio-channel SDP discovery.
- `IAudioCapture`/`IAudioPlayback` on Android `AudioRecord`/`AudioTrack` (8 kHz mono S16LE, matches `AudioFormat.RadioPcm`).
- Re-enable voice UI on Android.
- RepeaterBook import via the **ContentProvider** (`content://com.zbm2.repeaterbook.RBContentProvider/repeaters`) + `<queries>` manifest entry — *not* the HTTP API. Reuses the shared Core result→channel mapping. See [ANDROID-PORT-PLAN.md](ANDROID-PORT-PLAN.md) §7b and [REPEATERBOOK-DESKTOP-PLAN.md](REPEATERBOOK-DESKTOP-PLAN.md).
