//
// htbt.swift — IOBluetooth classic-RFCOMM bridge for HTCommander (macOS).
//
// Copyright 2026 Ylian Saint-Hilaire. Licensed under the Apache License 2.0.
//
// Exposes a small C ABI (via @_cdecl) that HTCommander.Platform.Mac P/Invokes.
// The Benshi UV-PRO speaks classic Bluetooth RFCOMM (SPP/GAIA), NOT BLE, so we
// must use IOBluetooth (IOBluetoothRFCOMMChannel). CoreBluetooth does not work.
//
// Design mirrors cross/HTCommander.Platform.Linux/RadioBluetoothLinux.cs:
//   * The GAIA RFCOMM channel is assigned DYNAMICALLY by the radio and moves
//     between sessions, so we PROBE candidate channels with a GAIA GET_DEV_INFO
//     and keep the one that replies with a GAIA header (0xFF 0x01).
//   * We deliver RAW RFCOMM bytes to the C# side, which does GAIA deframing
//     exactly like the Linux read loop (so both platforms share that logic).
//
// Build:  swiftc -O -emit-library -o libhtbt.dylib htbt.swift \
//             -framework Foundation -framework IOBluetooth
// (see build.sh)
//
// NOTE: the run-loop timing + per-channel probe windows below are the parts most
// likely to need tuning against the real radio — iterate with the standalone
// test (see README.md) before wiring the GUI.

import Foundation
import IOBluetooth

// C callbacks into .NET. Kept alive on the managed side (stored in fields).
private typealias DataCallback = @convention(c) (UnsafePointer<UInt8>?, Int32) -> Void
private typealias EventCallback = @convention(c) (Int32) -> Void   // 0=connected, 1=closed, 2=error

private let EVT_CONNECTED: Int32 = 0
private let EVT_CLOSED: Int32 = 1
private let EVT_ERROR: Int32 = 2

// One RFCOMM connection to a radio. All IOBluetooth work runs on a dedicated
// thread with a live RunLoop so the channel's data-delegate callbacks fire.
private final class HtRfcomm: NSObject, IOBluetoothRFCOMMChannelDelegate {
    private let address: String
    private let onData: DataCallback
    private let onEvent: EventCallback

    private var channel: IOBluetoothRFCOMMChannel?
    private var worker: Thread?

    // During the probe we buffer incoming bytes here and check for a GAIA reply;
    // once validated we forward bytes straight through onData.
    private var probing = false
    private var probeBytes = [UInt8]()

    // GAIA-framed BASIC/GET_DEV_INFO probe, identical bytes to the Linux backend:
    // GaiaEncode({0x00,0x02,0x00,0x04,0x03}) => FF 01 00 05 00 02 00 04 03
    private static let gaiaProbe: [UInt8] = [0xFF, 0x01, 0x00, 0x05, 0x00, 0x02, 0x00, 0x04, 0x03]

    init(address: String, onData: @escaping DataCallback, onEvent: @escaping EventCallback) {
        self.address = address
        self.onData = onData
        self.onEvent = onEvent
    }

    /// Opens the validated GAIA channel. Blocks until a channel is found or all
    /// candidates are exhausted. Returns true on success; on success a dedicated
    /// run-loop thread keeps delivering data callbacks until close().
    func start(preferred: UInt8) -> Bool {
        var ok = false
        let done = DispatchSemaphore(value: 0)
        let t = Thread {
            ok = self.connectGaia(preferred: preferred)
            done.signal()
            if ok {
                // Keep this thread's run loop alive so rfcommChannelData: keeps firing.
                while self.channel != nil {
                    RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.25))
                }
            }
        }
        t.stackSize = 512 * 1024
        worker = t
        t.start()
        done.wait()
        return ok
    }

    private func connectGaia(preferred: UInt8) -> Bool {
        let candidates: [UInt8] = preferred > 0 ? [preferred] : Array(1...30)
        for ch in candidates {
            if openAndProbe(channelID: ch) { return true }
        }
        return false
    }

    private func openAndProbe(channelID: UInt8) -> Bool {
        guard let dev = IOBluetoothDevice(addressString: address) else { return false }
        probing = true
        probeBytes.removeAll(keepingCapacity: true)

        var ch: IOBluetoothRFCOMMChannel?
        let rc = dev.openRFCOMMChannelSync(&ch, withChannelID: channelID, delegate: self)
        guard rc == kIOReturnSuccess, let opened = ch else { return false }
        channel = opened

        // Send the GAIA probe.
        var probe = HtRfcomm.gaiaProbe
        let wrc = probe.withUnsafeMutableBytes { raw in
            opened.writeSync(raw.baseAddress, length: UInt16(raw.count))
        }
        if wrc != kIOReturnSuccess {
            opened.close(); channel = nil; probing = false; return false
        }

        // Spin the run loop up to ~1.2s waiting for a GAIA reply (0xFF 0x01).
        let deadline = Date().addingTimeInterval(1.2)
        while Date() < deadline {
            RunLoop.current.run(mode: .default, before: Date().addingTimeInterval(0.05))
            if probeBytes.count >= 2 && probeBytes[0] == 0xFF && probeBytes[1] == 0x01 {
                probing = false
                // Forward the probe reply so the C# GAIA accumulator stays aligned
                // (mirrors how the Linux backend seeds its accumulator with the reply).
                probeBytes.withUnsafeBufferPointer { onData($0.baseAddress, Int32($0.count)) }
                return true
            }
        }

        // Not a GAIA channel — close and try the next.
        opened.close(); channel = nil; probing = false
        return false
    }

    func write(_ ptr: UnsafePointer<UInt8>, _ len: Int32) {
        guard let ch = channel else { return }
        _ = ch.writeSync(UnsafeMutableRawPointer(mutating: ptr), length: UInt16(len))
    }

    func close() {
        channel?.close()
        channel = nil   // also drops the run-loop thread out of its while-loop
    }

    // MARK: IOBluetoothRFCOMMChannelDelegate

    func rfcommChannelData(_ rfcommChannel: IOBluetoothRFCOMMChannel!,
                           data dataPointer: UnsafeMutableRawPointer!,
                           length dataLength: Int) {
        guard dataLength > 0 else { return }
        let p = dataPointer.assumingMemoryBound(to: UInt8.self)
        if probing {
            probeBytes.append(contentsOf: UnsafeBufferPointer(start: p, count: dataLength))
        } else {
            onData(p, Int32(dataLength))
        }
    }

    func rfcommChannelClosed(_ rfcommChannel: IOBluetoothRFCOMMChannel!) {
        channel = nil
        onEvent(EVT_CLOSED)
    }
}

// Handle registry: native objects are tracked by an Int32 handle so the C ABI
// stays pointer-free across the P/Invoke boundary.
private var registry = [Int32: HtRfcomm]()
private var nextHandle: Int32 = 1
private let registryLock = NSLock()

/// Connect to a radio and discover its GAIA RFCOMM channel.
/// preferred > 0 restricts the probe to that one channel (else scans 1..30).
/// Returns a handle (>= 1) on success, or -1 if no GAIA channel was found.
@_cdecl("htbt_connect")
public func htbt_connect(_ addr: UnsafePointer<CChar>, _ preferred: UInt8,
                         _ onData: @escaping @convention(c) (UnsafePointer<UInt8>?, Int32) -> Void,
                         _ onEvent: @escaping @convention(c) (Int32) -> Void) -> Int32 {
    let address = String(cString: addr)
    let conn = HtRfcomm(address: address, onData: onData, onEvent: onEvent)
    guard conn.start(preferred: preferred) else { return -1 }

    registryLock.lock()
    let h = nextHandle; nextHandle += 1
    registry[h] = conn
    registryLock.unlock()

    // Success is signalled by the >= 0 handle return; the managed side raises its
    // OnConnected from there. onEvent is reserved for asynchronous close/error.
    return h
}

/// Write raw (already GAIA-framed) bytes to the radio.
@_cdecl("htbt_write")
public func htbt_write(_ handle: Int32, _ data: UnsafePointer<UInt8>, _ len: Int32) {
    registryLock.lock(); let conn = registry[handle]; registryLock.unlock()
    conn?.write(data, len)
}

/// Close and release a connection.
@_cdecl("htbt_close")
public func htbt_close(_ handle: Int32) {
    registryLock.lock(); let conn = registry.removeValue(forKey: handle); registryLock.unlock()
    conn?.close()
}

/// True if Bluetooth is available (a host controller is present & powered).
@_cdecl("htbt_bluetooth_available")
public func htbt_bluetooth_available() -> Int32 {
    let host = IOBluetoothHostController.default()
    // powerState == kIOBluetoothHCIPowerStateON (1) when usable.
    return (host?.powerState.rawValue == 1) ? 1 : 0
}

/// List paired Bluetooth devices as "name\taddr\n" lines into outBuf (NUL-terminated).
/// Returns bytes written, or a negative (needed+1) if the buffer is too small.
@_cdecl("htbt_list_radios")
public func htbt_list_radios(_ outBuf: UnsafeMutablePointer<CChar>, _ cap: Int32) -> Int32 {
    guard let paired = IOBluetoothDevice.pairedDevices() else { return 0 }
    var s = ""
    for case let dev as IOBluetoothDevice in paired {
        let name = dev.name ?? ""
        let addr = dev.addressString ?? ""     // e.g. "38-d2-00-01-7f-0e"
        if addr.isEmpty { continue }
        s += "\(name)\t\(addr)\n"
    }
    let bytes = Array(s.utf8)
    if Int32(bytes.count) >= cap { return -Int32(bytes.count + 1) }
    bytes.withUnsafeBufferPointer { src in
        if let base = src.baseAddress {
            outBuf.withMemoryRebound(to: UInt8.self, capacity: bytes.count) { dst in
                dst.update(from: base, count: bytes.count)
            }
        }
    }
    outBuf[bytes.count] = 0
    return Int32(bytes.count)
}
