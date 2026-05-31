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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HTCommander.Core.Abstractions;
using HTCommander.Platform.Linux.Bluetooth;
using Tmds.DBus;

namespace HTCommander.Platform.Linux;

/// <summary>
/// Linux BlueZ implementation of <see cref="IRadioTransport"/>. Mirrors
/// RadioBluetoothWin: same GAIA framing, accumulator read loop and write path,
/// but uses BlueZ (D-Bus) for adapter power-on and a raw kernel RFCOMM socket
/// (<see cref="NativeRfcomm"/>) for the SPP data stream.
///
/// Kept free of any dependency on the WinForms <c>Radio</c> type: the MAC,
/// logger and disconnect notification are injected, so this can be driven from
/// the Avalonia app or a portable Radio (porting plan step 3) on any platform.
/// </summary>
public sealed class RadioBluetoothLinux : IRadioTransport
{
    private readonly string macAddress;          // normalized, no separators, upper-case
    private readonly ILogger? logger;
    private readonly Action<string>? onDisconnected;

    private volatile bool running = false;
    private bool isConnecting = false;
    private NativeRfcomm.RfcommStream? stream = null;
    private CancellationTokenSource? connectionCts = null;
    private Task? connectionTask = null;
    private readonly object connectionLock = new object();

    public event Action? OnConnected;
    public event Action<IRadioTransport, Exception, byte[]>? ReceivedData;

    // Device model names that identify a compatible radio (matches RadioBluetoothWin).
    private static readonly string[] TargetDeviceNames =
        { "UV-PRO", "UV-50PRO", "GA-5WB", "VR-N75", "VR-N76", "VR-N7500", "VR-N7600", "DB50-B" };

    /// <param name="macAddress">Target radio BD_ADDR, e.g. "AA:BB:CC:DD:EE:FF".</param>
    /// <param name="logger">Optional logger for transport debug output.</param>
    /// <param name="onDisconnected">
    /// Optional callback invoked with a human-readable reason when the transport
    /// drops (peer closed, connect failed, etc.). Mirrors RadioBluetoothWin's
    /// call into <c>Radio.Disconnect</c>; the consumer wires this to its own
    /// disconnect handling.
    /// </param>
    public RadioBluetoothLinux(string macAddress, ILogger? logger = null, Action<string>? onDisconnected = null)
    {
        this.macAddress = (macAddress ?? string.Empty).Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
        this.logger = logger;
        this.onDisconnected = onDisconnected;
    }

    private void Debug(string msg) => logger?.Debug("Transport: " + msg);

    public bool Connect()
    {
        lock (connectionLock)
        {
            if (running || isConnecting) return false;
            isConnecting = true;
        }
        connectionTask = Task.Run(StartAsync);
        return true;
    }

    public void Disconnect()
    {
        lock (connectionLock)
        {
            if (running == false && connectionTask == null) return;
            running = false;
            try { connectionCts?.Cancel(); } catch (Exception) { }
            // Closing the stream shuts the fd down, unblocking the read loop.
            try { stream?.Dispose(); } catch (Exception) { }
        }

        if (connectionTask != null)
        {
            try { connectionTask.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        }

        lock (connectionLock)
        {
            try { stream?.Dispose(); } catch (Exception) { }
            stream = null;
            try { connectionCts?.Dispose(); } catch (Exception) { }
            connectionCts = null;
            connectionTask = null;
        }
    }

    public void EnqueueWrite(int expectedResponse, byte[] cmdData)
    {
        Stream? s = stream;
        if (!running || s == null) return;
        byte[] bytes = GaiaEncode(cmdData);
        try
        {
            s.Write(bytes, 0, bytes.Length);
            s.Flush();
        }
        catch (Exception ex) { Debug("Error sending: " + ex.Message); }
    }

    // --- GAIA framing (identical to RadioBluetoothWin) ---------------------

    // Decode GAIA frame, returns bytes consumed or 0 if incomplete, -1 on error.
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

    // Encode command into GAIA frame.
    private static byte[] GaiaEncode(byte[] cmd)
    {
        byte[] bytes = new byte[cmd.Length + 4];
        bytes[0] = 0xFF;
        bytes[1] = 0x01;
        bytes[3] = (byte)(cmd.Length - 4);
        Array.Copy(cmd, 0, bytes, 4, cmd.Length);
        return bytes;
    }

    // Connects and returns the fd of the RFCOMM channel that actually speaks GAIA.
    // Each candidate channel is probed with GET_DEV_INFO; we keep the one that
    // replies with a GAIA frame (0xFF 0x01). Necessary because the radio assigns
    // the SPP/GAIA channel dynamically and other channels answer non-GAIA services.
    private int ConnectGaiaChannel(byte[] bdaddr, int preferred, out byte[] initialData, out int usedChannel)
    {
        initialData = Array.Empty<byte>();
        usedChannel = 0;
        byte[] probe = GaiaEncode(new byte[] { 0x00, 0x02, 0x00, 0x04, 0x03 }); // BASIC/GET_DEV_INFO
        IEnumerable<int> candidates = preferred > 0 ? new[] { preferred } : Enumerable.Range(1, 30);

        foreach (int ch in candidates)
        {
            int fd = NativeRfcomm.TryConnect(bdaddr, ch, preferred > 0 ? 8000 : 1500);
            if (fd < 0) continue;

            if (!NativeRfcomm.WriteAll(fd, probe)) { NativeRfcomm.CloseFd(fd); continue; }
            byte[] buf = new byte[1024];
            int n = NativeRfcomm.ReadWithTimeout(fd, buf, 1200);
            if (n >= 2 && buf[0] == 0xFF && buf[1] == 0x01)
            {
                initialData = new byte[n];
                Array.Copy(buf, initialData, n);
                usedChannel = ch;
                return fd;
            }

            Debug($"RFCOMM channel {ch} connected but no GAIA reply; skipping.");
            NativeRfcomm.CloseFd(fd);
        }
        return -1;
    }

    private static string BytesToHex(byte[] data, int offset, int len)
    {
        var sb = new System.Text.StringBuilder(len * 3);
        for (int i = 0; i < len; i++) { sb.Append(data[offset + i].ToString("X2")); sb.Append(' '); }
        return sb.ToString().TrimEnd();
    }

    // --- Connect + read loop -----------------------------------------------

    private void StartAsync()
    {
        CancellationToken cancellationToken;
        lock (connectionLock)
        {
            connectionCts = new CancellationTokenSource();
            cancellationToken = connectionCts.Token;
        }

        if (!NativeRfcomm.TryParseBdAddr(macAddress, out byte[] bdaddr))
        {
            lock (connectionLock) { isConnecting = false; }
            Debug("Invalid MAC address: " + macAddress);
            onDisconnected?.Invoke("Unable to connect");
            return;
        }

        // Best-effort: make sure a Bluetooth adapter is powered before connecting.
        try { BlueZ.PowerOnAdapter().GetAwaiter().GetResult(); } catch (Exception) { }

        // Optional fixed channel override; otherwise GAIA-validated scan of 1..30.
        // The radio assigns its SPP/GAIA RFCOMM channel DYNAMICALLY (observed moving
        // between sessions), and other RFCOMM channels accept but speak non-GAIA
        // services — so we must probe each channel and keep the one that replies
        // with a GAIA frame, not just the first that connects.
        int preferred = 0;
        string? envCh = Environment.GetEnvironmentVariable("HTCOMMANDER_RFCOMM_CHANNEL");
        if (!string.IsNullOrEmpty(envCh) && int.TryParse(envCh, out int pc) && pc > 0 && pc <= 30) preferred = pc;

        int fd = -1;
        byte[] initialData = Array.Empty<byte>();
        int retry = 3;
        while (retry > 0 && !cancellationToken.IsCancellationRequested)
        {
            Debug("Connecting (discovering GAIA channel)...");
            fd = ConnectGaiaChannel(bdaddr, preferred, out initialData, out int usedChannel);
            if (fd >= 0) { Debug($"GAIA channel {usedChannel} validated."); break; }
            retry--;
            if (retry > 0) Thread.Sleep(500);
        }

        if (fd < 0 || cancellationToken.IsCancellationRequested)
        {
            if (fd >= 0) NativeRfcomm.CloseFd(fd);
            lock (connectionLock) { isConnecting = false; }
            onDisconnected?.Invoke("Unable to connect");
            return;
        }

        try
        {
            byte[] accumulator = new byte[4096];
            int accumulatorPtr = 0, accumulatorLen = 0;

            // Seed with the GAIA validation reply already read during discovery, so
            // no bytes are lost and frame alignment is preserved.
            if (initialData.Length > 0 && initialData.Length <= accumulator.Length)
            {
                Array.Copy(initialData, 0, accumulator, 0, initialData.Length);
                accumulatorLen = initialData.Length;
            }

            lock (connectionLock)
            {
                isConnecting = false;
                if (cancellationToken.IsCancellationRequested)
                {
                    running = false;
                    NativeRfcomm.CloseFd(fd);
                    return;
                }
                stream = new NativeRfcomm.RfcommStream(fd);
            }

            running = true;
            OnConnected?.Invoke();

            while (running && !cancellationToken.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = stream!.Read(accumulator, accumulatorPtr + accumulatorLen,
                                             accumulator.Length - (accumulatorPtr + accumulatorLen));
                }
                catch (Exception)
                {
                    if (!running) break;
                    throw;
                }

                if (!running) break;
                if (bytesRead == 0)
                {
                    running = false;
                    onDisconnected?.Invoke("Connection closed by remote host.");
                    break;
                }

                accumulatorLen += bytesRead;
                if (accumulatorLen < 8) continue;

                int cmdSize;
                byte[]? cmd;
                while ((cmdSize = GaiaDecode(accumulator, accumulatorPtr, accumulatorLen, out cmd)) != 0)
                {
                    if (cmdSize < 0)
                    {
                        cmdSize = accumulatorLen;
                        Debug($"GAIA: {BytesToHex(accumulator, accumulatorPtr, accumulatorLen)}");
                    }
                    accumulatorPtr += cmdSize;
                    accumulatorLen -= cmdSize;

                    if (cmd != null) { ReceivedData?.Invoke(this, null, cmd); }
                }

                if (accumulatorLen == 0) { accumulatorPtr = 0; }
                if (accumulatorPtr > 2048)
                {
                    Array.Copy(accumulator, accumulatorPtr, accumulator, 0, accumulatorLen);
                    accumulatorPtr = 0;
                }
            }
        }
        catch (Exception ex)
        {
            if (running) { Debug($"Connection error: {ex.Message}"); }
        }
        finally
        {
            lock (connectionLock)
            {
                running = false;
                isConnecting = false;
                try { stream?.Dispose(); } catch (Exception) { }
                stream = null;
            }
            Debug("Connection closed.");
            onDisconnected?.Invoke("Connection closed.");
        }
    }
}

/// <summary>
/// BlueZ-backed discovery of Bluetooth radios. Replaces the static discovery
/// helpers on RadioBluetoothWin. Synchronous facade over the async D-Bus calls
/// (same shape the WinForms callers expect).
/// </summary>
public sealed class BlueZRadioDiscovery : IRadioTransportDiscovery
{
    private static readonly string[] TargetDeviceNames =
        { "UV-PRO", "UV-50PRO", "GA-5WB", "VR-N75", "VR-N76", "VR-N7500", "VR-N7600", "DB50-B" };

    public bool CheckBluetooth()
    {
        try { return Task.Run(BlueZ.HasAdapter).GetAwaiter().GetResult(); }
        catch (Exception) { return false; }
    }

    public IReadOnlyList<string> GetDeviceNames()
    {
        try
        {
            return Task.Run(async () => (await BlueZ.GetDevices())
                    .Select(d => d.Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList())
                .GetAwaiter().GetResult();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    public IReadOnlyList<string> FindCompatibleDevices()
    {
        try
        {
            return Task.Run(async () => (await BlueZ.GetDevices())
                    .Where(d => TargetDeviceNames.Contains(d.Name))
                    .Select(d => d.Name)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList())
                .GetAwaiter().GetResult();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Like <see cref="FindCompatibleDevices"/> but returns the BD_ADDR alongside
    /// the name, which a caller needs to actually open the transport. (The
    /// <see cref="IRadioTransportDiscovery"/> contract only exposes names.)
    /// </summary>
    public IReadOnlyList<RadioDeviceInfo> FindCompatibleRadios()
    {
        try
        {
            return Task.Run(async () => (await BlueZ.GetDevices())
                    .Where(d => TargetDeviceNames.Contains(d.Name) && !string.IsNullOrEmpty(d.Address))
                    .Select(d => new RadioDeviceInfo(d.Name, d.Address))
                    .OrderBy(d => d.Name)
                    .ToList())
                .GetAwaiter().GetResult();
        }
        catch (Exception) { return Array.Empty<RadioDeviceInfo>(); }
    }
}

/// <summary>Thin async helpers over the BlueZ system bus.</summary>
internal static class BlueZ
{
    private const string BusName = "org.bluez";
    private const string AdapterIface = "org.bluez.Adapter1";
    private const string DeviceIface = "org.bluez.Device1";

    internal readonly struct BtDevice
    {
        public BtDevice(string name, string address) { Name = name; Address = address; }
        public string Name { get; }
        public string Address { get; }
    }

    private static async Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjects()
    {
        var manager = Connection.System.CreateProxy<IObjectManager>(BusName, "/");
        return await manager.GetManagedObjectsAsync();
    }

    private static string? FindAdapterPath(
        IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>> objects)
    {
        foreach (var kv in objects)
        {
            if (kv.Value.ContainsKey(AdapterIface)) return kv.Key.ToString();
        }
        return null;
    }

    /// <summary>True if at least one Bluetooth adapter is present.</summary>
    public static async Task<bool> HasAdapter()
    {
        var objects = await GetManagedObjects();
        return FindAdapterPath(objects) != null;
    }

    /// <summary>Powers on the first available adapter. Best-effort.</summary>
    public static async Task PowerOnAdapter()
    {
        var objects = await GetManagedObjects();
        string? path = FindAdapterPath(objects);
        if (path == null) return;
        var adapter = Connection.System.CreateProxy<IAdapter1>(BusName, path);
        await adapter.SetAsync("Powered", true);
    }

    /// <summary>Enumerates all known (paired/seen) Bluetooth devices.</summary>
    public static async Task<List<BtDevice>> GetDevices()
    {
        var result = new List<BtDevice>();
        var objects = await GetManagedObjects();
        foreach (var kv in objects)
        {
            if (!kv.Value.TryGetValue(DeviceIface, out var props)) continue;
            string name = props.TryGetValue("Name", out var n) ? n?.ToString() ?? "" : "";
            string addr = props.TryGetValue("Address", out var a) ? a?.ToString() ?? "" : "";
            result.Add(new BtDevice(name, addr));
        }
        return result;
    }
}
