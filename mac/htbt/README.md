# libhtbt — IOBluetooth RFCOMM bridge (macOS)

A tiny Swift `.dylib` that wraps **IOBluetooth** classic-RFCOMM and exposes a C ABI
that `HTCommander.Platform.Mac` P/Invokes. The Benshi UV-PRO is classic Bluetooth
RFCOMM (SPP/GAIA), **not BLE**, so CoreBluetooth cannot talk to it — IOBluetooth is
the only option, and it has no clean .NET binding, hence this shim.

## Build

```bash
./build.sh
```

Produces `libhtbt.dylib` and copies it to
`../../cross/HTCommander.Platform.Mac/runtimes/osx/native/libhtbt.dylib`, where the
.NET project picks it up as a native runtime asset (copied next to the app on build).

Requires Xcode command-line tools (`xcode-select --install`).

## C ABI

| Export | Meaning |
|---|---|
| `int htbt_connect(const char* addr, uint8_t preferred, DataCb, EventCb)` | Connect + discover the GAIA RFCOMM channel. Returns a handle ≥ 1, or -1. `preferred > 0` restricts the probe to that channel. |
| `void htbt_write(int handle, const uint8_t* data, int len)` | Write raw (already GAIA-framed) bytes. |
| `void htbt_close(int handle)` | Close + release. |
| `int  htbt_bluetooth_available(void)` | 1 if a powered BT controller is present. |
| `int  htbt_list_radios(char* out, int cap)` | Fills `out` with `name\taddr\n` lines for paired devices. |

`DataCb = void(*)(const uint8_t* data, int len)` — **raw** RFCOMM bytes (C# does the
GAIA deframing, identical to the Linux backend).
`EventCb = void(*)(int kind)` — `0=connected, 1=closed, 2=error`.

## Why the channel is probed, not hard-coded

The radio assigns its SPP/GAIA RFCOMM channel **dynamically** (it moves between
sessions), and other RFCOMM channels accept connections but speak non-GAIA services.
So `htbt_connect` opens each candidate channel, sends a GAIA `GET_DEV_INFO` probe, and
keeps the first channel that replies with a GAIA header (`0xFF 0x01`). This mirrors
`RadioBluetoothLinux.ConnectGaiaChannel`.

## Hardware iteration (do this first, on the Mac, radio paired)

The run-loop timing and per-channel probe window are the parts most likely to need
tuning against the real radio. Test the dylib **standalone** before wiring the GUI:

```bash
# pair the radio in System Settings → Bluetooth first, then:
swiftc -O -o htbt-test htbt.swift htbt-test.swift -framework Foundation -framework IOBluetooth
./htbt-test                       # lists paired radios
./htbt-test 38-d2-00-01-7f-0e     # connect + print incoming GAIA frames
```

macOS will prompt for **Bluetooth permission** on first run from the terminal.

## Packaging note

For a distributable `.app`, add `NSBluetoothAlwaysUsageDescription` to `Info.plist`
and ship `libhtbt.dylib` next to the executable (or set an `@rpath`).
