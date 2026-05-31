//
// htbt-test.swift — standalone harness to prove libhtbt works against the radio.
//
// Build (links the bridge source directly, no dylib needed):
//   swiftc -O -o htbt-test htbt.swift htbt-test.swift \
//       -framework Foundation -framework IOBluetooth
//
//   ./htbt-test                     # list paired radios
//   ./htbt-test 38-d2-00-01-7f-0e   # connect + print incoming GAIA frames
//
// This is the first thing to run on the Mac (radio paired in System Settings):
// it isolates the IOBluetooth RFCOMM path from the whole .NET app.
//
import Foundation

private func hex(_ p: UnsafePointer<UInt8>?, _ n: Int32) -> String {
    guard let p = p else { return "" }
    var s = ""
    for i in 0..<Int(n) { s += String(format: "%02X ", p[i]) }
    return s.trimmingCharacters(in: .whitespaces)
}

@main
struct HtbtTest {
    static func main() {
        let args = CommandLine.arguments

        if args.count < 2 {
            var buf = [CChar](repeating: 0, count: 8192)
            let n = htbt_list_radios(&buf, Int32(buf.count))
            if n <= 0 { print("No paired devices (or buffer too small): \(n)"); return }
            print("BT available: \(htbt_bluetooth_available() == 1)")
            print("Paired devices (name<TAB>addr):")
            print(String(cString: buf))
            print("Re-run with an address to connect, e.g.:  ./htbt-test 38-d2-00-01-7f-0e")
            return
        }

        let addr = args[1]
        print("Connecting to \(addr) (probing GAIA channel 1..30)…")

        let onData: @convention(c) (UnsafePointer<UInt8>?, Int32) -> Void = { p, n in
            print("RX \(n): \(hex(p, n))")
        }
        let onEvent: @convention(c) (Int32) -> Void = { kind in
            let names = ["connected", "closed", "error"]
            let label = (kind >= 0 && Int(kind) < names.count) ? names[Int(kind)] : "?"
            print("EVENT: \(label) (\(kind))")
        }

        // htbt_connect must run off the main thread (it dispatches the IOBluetooth
        // probe to the main run loop), so drive it from a background thread and pump
        // the main run loop here so those callbacks are delivered.
        let worker = Thread {
            let h = addr.withCString { htbt_connect($0, 0, onData, onEvent) }
            if h < 0 { print("Failed to find a GAIA channel."); exit(1) }
            print("Connected, handle=\(h). Listening for 20s…")
            Thread.sleep(forTimeInterval: 20)
            htbt_close(h)
            print("Closed.")
            exit(0)
        }
        worker.stackSize = 1 << 20
        worker.start()

        RunLoop.main.run()   // pump main; the worker calls exit() when done
    }
}
