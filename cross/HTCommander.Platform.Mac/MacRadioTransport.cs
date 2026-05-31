/*
Copyright 2026 Ylian Saint-Hilaire

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using HTCommander.Core.Abstractions;

namespace HTCommander.Platform.Mac;

/// <summary>
/// macOS IOBluetooth implementation of <see cref="IRadioTransport"/>. Mirrors
/// <c>RadioBluetoothLinux</c>: same GAIA framing and accumulator-based deframing,
/// but the RFCOMM stream comes from <c>libhtbt.dylib</c> (IOBluetooth) instead of
/// a kernel socket. The native bridge owns the run loop + GAIA-channel probe and
/// pushes RAW bytes up; this class does the GAIA deframing so both platforms share
/// that logic verbatim.
/// </summary>
public sealed class MacRadioTransport : IRadioTransport
{
    private readonly string macAddress;     // "aa-bb-cc-dd-ee-ff" for IOBluetoothDevice
    private readonly ILogger? logger;
    private readonly Action<string>? onDisconnected;

    private int handle = -1;
    private volatile bool running;
    private volatile bool accepting;   // true once callbacks are wired, so the GAIA
                                       // probe-reply that arrives DURING connect isn't dropped
    private bool isConnecting;
    private readonly object connectionLock = new object();
    private Task? connectionTask;

    // Native callbacks are stored in fields so the GC can't collect the delegates
    // while native code still holds the function pointers.
    private NativeHtbt.DataCallback? dataCb;
    private NativeHtbt.EventCallback? eventCb;

    // GAIA accumulator (mirrors RadioBluetoothLinux's read-loop state). Native data
    // callbacks arrive serialized on the bridge's run-loop thread; the lock guards
    // against a concurrent Disconnect.
    private readonly object accumLock = new object();
    private readonly byte[] accumulator = new byte[4096];
    private int accumulatorPtr;
    private int accumulatorLen;

    public event Action? OnConnected;
    public event Action<IRadioTransport, Exception, byte[]>? ReceivedData;

    public MacRadioTransport(string macAddress, ILogger? logger = null, Action<string>? onDisconnected = null)
    {
        this.macAddress = NormalizeAddress(macAddress);
        this.logger = logger;
        this.onDisconnected = onDisconnected;
    }

    // IOBluetoothDevice(addressString:) accepts "aa-bb-cc-dd-ee-ff"; accept colon or
    // separator-free input and normalize to the dashed lower-case form.
    private static string NormalizeAddress(string addr)
    {
        string a = (addr ?? string.Empty).Trim().Replace(":", "-").ToLowerInvariant();
        if (!a.Contains('-') && a.Length == 12)
        {
            var sb = new System.Text.StringBuilder(17);
            for (int i = 0; i < 12; i += 2)
            {
                if (i > 0) sb.Append('-');
                sb.Append(a, i, 2);
            }
            a = sb.ToString();
        }
        return a;
    }

    private void Debug(string msg) => logger?.Debug("Transport: " + msg);

    public bool Connect()
    {
        lock (connectionLock)
        {
            if (running || isConnecting) return false;
            isConnecting = true;
        }
        connectionTask = Task.Run(Start);
        return true;
    }

    private void Start()
    {
        // Optional fixed-channel override, parity with Linux HTCOMMANDER_RFCOMM_CHANNEL.
        byte preferred = 0;
        string? env = Environment.GetEnvironmentVariable("HTCOMMANDER_RFCOMM_CHANNEL");
        if (!string.IsNullOrEmpty(env) && byte.TryParse(env, out byte pc) && pc >= 1 && pc <= 30) preferred = pc;

        dataCb = OnNativeData;
        eventCb = OnNativeEvent;
        lock (accumLock) { accumulatorPtr = 0; accumulatorLen = 0; }
        // Accept callbacks now: the GAIA channel-validation reply (the first device-info
        // frame) is forwarded by the bridge DURING NativeHtbt.Connect, before it returns
        // the handle — without this it would be dropped.
        accepting = true;

        int h;
        try
        {
            Debug("Connecting (discovering GAIA channel)...");
            h = NativeHtbt.Connect(macAddress, preferred, dataCb, eventCb);
        }
        catch (DllNotFoundException)
        {
            accepting = false;
            lock (connectionLock) { isConnecting = false; }
            logger?.Error("Transport: libhtbt.dylib not found — build it with mac/htbt/build.sh.");
            onDisconnected?.Invoke("Bluetooth bridge missing");
            return;
        }
        catch (Exception ex)
        {
            accepting = false;
            lock (connectionLock) { isConnecting = false; }
            Debug("Connect failed: " + ex.Message);
            onDisconnected?.Invoke("Unable to connect");
            return;
        }

        if (h < 0)
        {
            accepting = false;
            lock (connectionLock) { isConnecting = false; }
            onDisconnected?.Invoke("Unable to connect");
            return;
        }

        lock (connectionLock)
        {
            handle = h;
            running = true;
            isConnecting = false;
        }
        Debug($"GAIA channel validated (handle {h}).");
        OnConnected?.Invoke();
    }

    public void Disconnect()
    {
        int h;
        lock (connectionLock)
        {
            if (!running && handle < 0 && connectionTask == null) return;
            running = false;
            accepting = false;
            h = handle;
            handle = -1;
        }
        if (h >= 0) { try { NativeHtbt.Close(h); } catch (Exception) { } }
        if (connectionTask != null)
        {
            try { connectionTask.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        }
        lock (connectionLock) { connectionTask = null; }
    }

    public void EnqueueWrite(int expectedResponse, byte[] cmdData)
    {
        if (!running || handle < 0) return;
        byte[] bytes = GaiaEncode(cmdData);
        try { NativeHtbt.Write(handle, bytes, bytes.Length); }
        catch (Exception ex) { Debug("Error sending: " + ex.Message); }
    }

    // --- Native callbacks ---------------------------------------------------

    private void OnNativeData(IntPtr data, int len)
    {
        if (!accepting || len <= 0 || data == IntPtr.Zero) return;
        byte[] chunk = new byte[len];
        Marshal.Copy(data, chunk, 0, len);

        lock (accumLock)
        {
            // Compact if the incoming chunk wouldn't otherwise fit.
            if (accumulatorPtr + accumulatorLen + len > accumulator.Length && accumulatorPtr > 0)
            {
                Array.Copy(accumulator, accumulatorPtr, accumulator, 0, accumulatorLen);
                accumulatorPtr = 0;
            }
            int space = accumulator.Length - (accumulatorPtr + accumulatorLen);
            int n = Math.Min(space, len);
            if (n <= 0) { Debug("GAIA accumulator overflow; dropping chunk."); return; }
            Array.Copy(chunk, 0, accumulator, accumulatorPtr + accumulatorLen, n);
            accumulatorLen += n;

            int cmdSize;
            byte[]? cmd;
            while ((cmdSize = GaiaDecode(accumulator, accumulatorPtr, accumulatorLen, out cmd)) != 0)
            {
                if (cmdSize < 0) cmdSize = accumulatorLen;   // resync on garbage
                accumulatorPtr += cmdSize;
                accumulatorLen -= cmdSize;
                if (cmd != null) ReceivedData?.Invoke(this, null!, cmd);
            }

            if (accumulatorLen == 0) { accumulatorPtr = 0; }
            else if (accumulatorPtr > 2048)
            {
                Array.Copy(accumulator, accumulatorPtr, accumulator, 0, accumulatorLen);
                accumulatorPtr = 0;
            }
        }
    }

    private void OnNativeEvent(int kind)
    {
        if (kind == 0) return;   // connected — already signalled via the handle return
        bool wasRunning;
        lock (connectionLock)
        {
            wasRunning = running;
            running = false;
            accepting = false;
            handle = -1;
        }
        if (wasRunning)
        {
            Debug(kind == 2 ? "Connection error." : "Connection closed.");
            onDisconnected?.Invoke(kind == 2 ? "Connection error." : "Connection closed.");
        }
    }

    // --- GAIA framing (identical to RadioBluetoothLinux) --------------------

    // Decode a GAIA frame: returns bytes consumed, 0 if incomplete, -1 on a bad header.
    private static int GaiaDecode(byte[] data, int index, int len, out byte[]? cmd)
    {
        cmd = null;
        if (len < 8) return 0;
        if (data[index] != 0xFF || data[index + 1] != 0x01) return -1;

        byte payloadLen = data[index + 3];
        int hasChecksum = data[index + 2] & 1;
        int totalLen = payloadLen + 8 + hasChecksum;
        if (totalLen > len) return 0;

        cmd = new byte[4 + payloadLen];
        Array.Copy(data, index + 4, cmd, 0, cmd.Length);
        return totalLen;
    }

    // Encode a command into a GAIA frame.
    private static byte[] GaiaEncode(byte[] cmd)
    {
        byte[] bytes = new byte[cmd.Length + 4];
        bytes[0] = 0xFF;
        bytes[1] = 0x01;
        bytes[3] = (byte)(cmd.Length - 4);
        Array.Copy(cmd, 0, bytes, 4, cmd.Length);
        return bytes;
    }
}
