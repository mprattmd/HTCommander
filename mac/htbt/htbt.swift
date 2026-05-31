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

// Verbose probe diagnostics to stderr when HTBT_DEBUG is set (1/true/yes).
private let htbtDebug: Bool = {
    if let v = ProcessInfo.processInfo.environment["HTBT_DEBUG"] {
        return v == "1" || v.lowercased() == "true" || v.lowercased() == "yes"
    }
    return false
}()
private func dbg(_ s: @autoclosure () -> String) {
    if htbtDebug { FileHandle.standardError.write(("[htbt] " + s() + "\n").data(using: .utf8)!) }
}

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

    // The validated GAIA channel once connected.
    private var channel: IOBluetoothRFCOMMChannel?

    // --- Probe state machine (all touched only on the main run loop) ---
    private var dev: IOBluetoothDevice?
    private var candidates: [UInt8] = []
    private var candidateIndex = 0
    private var activeChannel: IOBluetoothRFCOMMChannel?   // channel currently being probed
    private var probing = false
    private var probeBytes = [UInt8]()
    private var openTimer: Timer?
    private var dataTimer: Timer?
    private var resultSemaphore: DispatchSemaphore?
    private var connected = false                         // final probe result

    private let openTimeout: TimeInterval = 2.5
    private let dataTimeout: TimeInterval = 1.3

    // GAIA-framed BASIC/GET_DEV_INFO probe, identical bytes to the Linux backend.
    // GaiaEncode({0x00,0x02,0x00,0x04,0x03}): the payload-length byte (index 3) is
    // cmd.Length-4 = 1 (the first 4 bytes are the command header) => FF 01 00 01 ...
    // (A wrong length byte here makes the radio silently ignore the frame.)
    private static let gaiaProbe: [UInt8] = [0xFF, 0x01, 0x00, 0x01, 0x00, 0x02, 0x00, 0x04, 0x03]

    init(address: String, onData: @escaping DataCallback, onEvent: @escaping EventCallback) {
        self.address = address
        self.onData = onData
        self.onEvent = onEvent
    }

    /// Connects + discovers the GAIA channel. Blocks the CALLING thread until the probe
    /// finishes, then returns true on success.
    ///
    /// IOBluetooth delivers RFCOMM open-completion + data callbacks on the MAIN run loop,
    /// so the whole probe runs there as an EVENT-DRIVEN state machine (open → on-complete
    /// → write probe → on-data/timeout → next). We must NOT nest a run loop inside a GCD
    /// main-queue block (that starves the IOBluetooth callbacks → every open times out),
    /// so the state machine is driven purely by delegate callbacks + Timers on the host's
    /// main run loop. REQUIREMENT: htbt_connect is called from a NON-main thread (the
    /// .NET side from a background Task; the standalone test from a background Thread) and
    /// the host pumps the main run loop.
    func start(preferred: UInt8) -> Bool {
        if Thread.isMainThread {
            dbg("ERROR: htbt_connect must be called off the main thread")
            return false
        }
        let done = DispatchSemaphore(value: 0)
        resultSemaphore = done
        DispatchQueue.main.async { self.beginProbe(preferred: preferred) }
        done.wait()
        return connected
    }

    // Kick off on the main run loop: warm the link, query SDP, then (after a short
    // delay for the async SDP) build the candidate list and probe the first channel.
    private func beginProbe(preferred: UInt8) {
        guard let device = IOBluetoothDevice(addressString: address) else {
            dbg("IOBluetoothDevice(addressString:) returned nil for \(address)")
            finish(false); return
        }
        dev = device
        let oc = device.openConnection()              // warm the ACL link
        let sdp = device.performSDPQuery(nil)          // async; records populate shortly
        dbg("openConnection -> \(String(format: "0x%08X", oc)); performSDPQuery -> \(String(format: "0x%08X", sdp)); name=\(device.name ?? "?") connected=\(device.isConnected())")

        if preferred > 0 {
            candidates = [preferred]
        }
        // Give SDP ~1s to populate, then build candidates + start probing.
        Timer.scheduledTimer(withTimeInterval: 1.0, repeats: false) { [weak self] _ in
            guard let self = self else { return }
            if preferred == 0 {
                // Probe the SDP-advertised RFCOMM channels (the SerialPort/SPP 0x1101
                // GAIA channel is among them, e.g. ch4). Default to SDP-only because a
                // full 1..30 sweep WEDGES this radio's RFCOMM; opt in with HTBT_FULLSCAN.
                self.candidates = self.sdpRfcommChannels(device)
                if ProcessInfo.processInfo.environment["HTBT_FULLSCAN"] != nil {
                    for ch in UInt8(1)...UInt8(30) where !self.candidates.contains(ch) {
                        self.candidates.append(ch)
                    }
                }
            }
            self.candidateIndex = 0
            dbg("probing channels: \(self.candidates.map { String($0) }.joined(separator: ","))")
            self.probeNext()
        }
    }

    // Collects the RFCOMM channel IDs published in the device's SDP service records.
    private func sdpRfcommChannels(_ device: IOBluetoothDevice) -> [UInt8] {
        var result = [UInt8]()
        guard let services = device.services else { return result }
        for case let rec as IOBluetoothSDPServiceRecord in services {
            var chID: BluetoothRFCOMMChannelID = 0
            if rec.getRFCOMMChannelID(&chID) == kIOReturnSuccess && chID != 0 {
                dbg("SDP service '\(rec.getServiceName() ?? "?")' -> RFCOMM ch \(chID)")
                if !result.contains(chID) { result.append(chID) }
            }
        }
        return result
    }

    // Open the next candidate channel; on success the open-complete delegate continues.
    private func probeNext() {
        guard let device = dev else { finish(false); return }
        if candidateIndex >= candidates.count { finish(false); return }
        let chId = candidates[candidateIndex]

        probing = true
        probeBytes.removeAll(keepingCapacity: true)

        var ch: IOBluetoothRFCOMMChannel?
        let rc = device.openRFCOMMChannelAsync(&ch, withChannelID: chId, delegate: self)
        if rc != kIOReturnSuccess || ch == nil {
            dbg("ch \(chId): openAsync rejected rc=\(String(format: "0x%08X", rc))")
            advance(); return
        }
        activeChannel = ch
        // Guard against an open that never completes.
        openTimer = Timer.scheduledTimer(withTimeInterval: openTimeout, repeats: false) { [weak self] _ in
            guard let self = self, let active = self.activeChannel else { return }
            dbg("ch \(chId): open timed out")
            active.close(); self.advance()
        }
    }

    // Move to the next candidate channel.
    private func advance() {
        invalidateTimers()
        activeChannel = nil
        candidateIndex += 1
        probeNext()
    }

    private func invalidateTimers() {
        openTimer?.invalidate(); openTimer = nil
        dataTimer?.invalidate(); dataTimer = nil
    }

    // Resolve the probe: keep the channel on success, signal the waiting caller.
    private func finish(_ ok: Bool) {
        invalidateTimers()
        probing = false
        connected = ok
        if ok { channel = activeChannel } else { activeChannel = nil }
        resultSemaphore?.signal()
        resultSemaphore = nil
    }

    func write(_ ptr: UnsafePointer<UInt8>, _ len: Int32) {
        // Keep all IOBluetooth access on the main thread; copy the bytes for the hop.
        let bytes = Array(UnsafeBufferPointer(start: ptr, count: Int(len)))
        DispatchQueue.main.async { [weak self] in
            guard let ch = self?.channel else { return }
            var b = bytes
            _ = b.withUnsafeMutableBytes { ch.writeSync($0.baseAddress, length: UInt16($0.count)) }
        }
    }

    func close() {
        DispatchQueue.main.async { [weak self] in
            guard let self = self else { return }
            self.invalidateTimers()
            self.channel?.close()
            self.activeChannel?.close()
            self.channel = nil
            self.activeChannel = nil
        }
    }

    // MARK: IOBluetoothRFCOMMChannelDelegate

    func rfcommChannelOpenComplete(_ rfcommChannel: IOBluetoothRFCOMMChannel!, status error: IOReturn) {
        guard rfcommChannel === activeChannel else { return }   // ignore stale channels
        openTimer?.invalidate(); openTimer = nil
        let chId = rfcommChannel?.getID() ?? 0
        dbg("openComplete ch \(chId) status=\(String(format: "0x%08X", error))")
        if error != kIOReturnSuccess {
            rfcommChannel?.close(); advance(); return
        }
        // Send the GAIA probe and wait (via the data delegate) for a GAIA reply.
        var probe = HtRfcomm.gaiaProbe
        let wrc = probe.withUnsafeMutableBytes { rfcommChannel.writeSync($0.baseAddress, length: UInt16($0.count)) }
        if wrc != kIOReturnSuccess {
            dbg("ch \(chId): probe writeSync failed wrc=\(String(format: "0x%08X", wrc))")
            rfcommChannel?.close(); advance(); return
        }
        dataTimer = Timer.scheduledTimer(withTimeInterval: dataTimeout, repeats: false) { [weak self] _ in
            guard let self = self, let active = self.activeChannel else { return }
            dbg("ch \(chId): no GAIA reply (\(self.probeBytes.count) bytes)")
            active.close(); self.advance()
        }
    }

    func rfcommChannelData(_ rfcommChannel: IOBluetoothRFCOMMChannel!,
                           data dataPointer: UnsafeMutableRawPointer!,
                           length dataLength: Int) {
        guard dataLength > 0 else { return }
        let p = dataPointer.assumingMemoryBound(to: UInt8.self)
        if probing {
            guard rfcommChannel === activeChannel else { return }
            probeBytes.append(contentsOf: UnsafeBufferPointer(start: p, count: dataLength))
            if probeBytes.count >= 2 && probeBytes[0] == 0xFF && probeBytes[1] == 0x01 {
                dbg("ch \(rfcommChannel?.getID() ?? 0): GAIA validated (\(probeBytes.count) bytes)")
                probing = false
                // Forward the probe reply so the C# GAIA accumulator stays aligned.
                probeBytes.withUnsafeBufferPointer { onData($0.baseAddress, Int32($0.count)) }
                finish(true)
            }
        } else {
            onData(p, Int32(dataLength))
        }
    }

    func rfcommChannelClosed(_ rfcommChannel: IOBluetoothRFCOMMChannel!) {
        if rfcommChannel === channel {
            channel = nil
            onEvent(EVT_CLOSED)
        }
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
