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
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HTCommander.Core.Abstractions;
using global::Android.Bluetooth;
using global::Android.Content;
using global::Java.Util;

namespace HTCommander.Platform.Android;

/// <summary>
/// Android implementation of <see cref="IRadioTransport"/> over Classic Bluetooth
/// RFCOMM (<c>BluetoothSocket</c>). Mirrors the Linux/Windows transports: identical
/// GAIA framing, accumulator read loop and write path — only the socket layer is
/// Android-specific.
///
/// Connection strategy (round one): connect to the bonded radio via the SPP service
/// UUID (the OS resolves the RFCOMM channel), send a GET_DEV_INFO probe, and accept
/// the link only if the radio answers with a GAIA frame (0xFF 0x01). If that channel
/// does not speak GAIA, fall back to a reflection-based scan of fixed RFCOMM channels
/// (the radio assigns its GAIA channel dynamically — see the Linux backend notes).
/// </summary>
public sealed class AndroidRadioTransport : IRadioTransport
{
    // Standard Serial Port Profile (SPP) UUID — GAIA rides on SPP.
    private static readonly UUID SppUuid = UUID.FromString("00001101-0000-1000-8000-00805F9B34FB")!;

    private readonly string macAddress;          // canonical "AA:BB:CC:DD:EE:FF"
    private readonly ILogger? logger;
    private readonly Action<string>? onDisconnected;

    private volatile bool running;
    private bool isConnecting;
    private BluetoothSocket? socket;
    private Stream? input;
    private Stream? output;
    private CancellationTokenSource? cts;
    private Task? connectionTask;
    private readonly object connectionLock = new();

    public event Action? OnConnected;
    public event Action<IRadioTransport, Exception, byte[]>? ReceivedData;

    public AndroidRadioTransport(string macAddress, ILogger? logger = null, Action<string>? onDisconnected = null)
    {
        this.macAddress = NormalizeMac(macAddress);
        this.logger = logger;
        this.onDisconnected = onDisconnected;
    }

    private void Debug(string msg) => logger?.Debug("AndroidTransport: " + msg);

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
            if (!running && connectionTask == null) return;
            running = false;
            try { cts?.Cancel(); } catch (Exception) { }
            try { socket?.Close(); } catch (Exception) { }   // unblocks the read loop
        }
        if (connectionTask != null)
        {
            try { connectionTask.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        }
        lock (connectionLock)
        {
            CloseSocket();
            try { cts?.Dispose(); } catch (Exception) { }
            cts = null;
            connectionTask = null;
        }
    }

    public void EnqueueWrite(int expectedResponse, byte[] cmdData)
    {
        Stream? o = output;
        if (!running || o == null) return;
        byte[] bytes = GaiaFraming.Encode(cmdData);
        try { o.Write(bytes, 0, bytes.Length); o.Flush(); }
        catch (Exception ex) { Debug("Error sending: " + ex.Message); }
    }

    // --- Connect + read loop -----------------------------------------------

    private void StartAsync()
    {
        CancellationToken token;
        lock (connectionLock)
        {
            cts = new CancellationTokenSource();
            token = cts.Token;
        }

        BluetoothAdapter? adapter = GetAdapter();
        if (adapter == null || !adapter.IsEnabled)
        {
            Fail("Bluetooth adapter unavailable or off.");
            return;
        }

        BluetoothDevice? device;
        try { device = adapter.GetRemoteDevice(macAddress); }
        catch (Exception ex) { Fail("Bad address " + macAddress + ": " + ex.Message); return; }
        if (device == null) { Fail("Radio " + macAddress + " not found."); return; }

        try { adapter.CancelDiscovery(); } catch (Exception) { }

        // 1) Preferred: SPP UUID (OS picks the channel). 2) Optional fixed channel
        // override. 3) Reflection scan 1..30. Keep the first that replies with GAIA.
        byte[] seed = Array.Empty<byte>();
        BluetoothSocket? sock = TryUuid(device, ref seed);
        if (sock == null)
        {
            int forced = ForcedChannel();
            var channels = forced > 0 ? new[] { forced } : Enumerable1To30();
            foreach (int ch in channels)
            {
                if (token.IsCancellationRequested) break;
                sock = TryChannel(device, ch, ref seed);
                if (sock != null) { Debug($"GAIA validated on RFCOMM channel {ch}."); break; }
            }
        }
        else { Debug("GAIA validated on SPP UUID channel."); }

        if (sock == null || token.IsCancellationRequested)
        {
            try { sock?.Close(); } catch (Exception) { }
            Fail("Unable to connect (no GAIA channel).");
            return;
        }

        try
        {
            byte[] acc = new byte[4096];
            int accPtr = 0, accLen = 0;
            if (seed.Length > 0 && seed.Length <= acc.Length)
            {
                Array.Copy(seed, 0, acc, 0, seed.Length);
                accLen = seed.Length;
            }

            lock (connectionLock)
            {
                isConnecting = false;
                if (token.IsCancellationRequested) { try { sock.Close(); } catch (Exception) { } return; }
                socket = sock;
                input = sock.InputStream;
                output = sock.OutputStream;
            }

            running = true;
            OnConnected?.Invoke();

            // Drain any frames already in the validation seed before the first read.
            DrainFrames(acc, ref accPtr, ref accLen);

            while (running && !token.IsCancellationRequested)
            {
                int n;
                try { n = input!.Read(acc, accPtr + accLen, acc.Length - (accPtr + accLen)); }
                catch (Exception) { if (!running) break; throw; }

                if (!running) break;
                if (n == 0) { running = false; onDisconnected?.Invoke("Connection closed by remote host."); break; }

                accLen += n;
                if (accLen < 8) continue;
                DrainFrames(acc, ref accPtr, ref accLen);

                if (accLen == 0) accPtr = 0;
                if (accPtr > 2048) { Array.Copy(acc, accPtr, acc, 0, accLen); accPtr = 0; }
            }
        }
        catch (Exception ex) { if (running) Debug("Connection error: " + ex.Message); }
        finally
        {
            lock (connectionLock) { running = false; isConnecting = false; CloseSocket(); }
            Debug("Connection closed.");
            onDisconnected?.Invoke("Connection closed.");
        }
    }

    // Pull every complete GAIA frame out of the accumulator and raise ReceivedData.
    private void DrainFrames(byte[] acc, ref int accPtr, ref int accLen)
    {
        int sz;
        byte[]? cmd;
        while ((sz = GaiaFraming.Decode(acc, accPtr, accLen, out cmd)) != 0)
        {
            if (sz < 0) { sz = accLen; }   // bad header: resync by dropping the buffer
            accPtr += sz;
            accLen -= sz;
            if (cmd != null) ReceivedData?.Invoke(this, null!, cmd);
        }
    }

    // --- Connection helpers ------------------------------------------------

    private BluetoothSocket? TryUuid(BluetoothDevice device, ref byte[] seed)
    {
        try
        {
            var s = device.CreateRfcommSocketToServiceRecord(SppUuid);
            if (s == null) return null;
            return ConnectAndValidate(s, ref seed) ? s : Close(s);
        }
        catch (Exception ex) { Debug("UUID connect failed: " + ex.Message); return null; }
    }

    private BluetoothSocket? TryChannel(BluetoothDevice device, int channel, ref byte[] seed)
    {
        try
        {
            // Hidden API: BluetoothDevice.createRfcommSocket(int) — reach it by reflection.
            var method = device.Class.GetMethod("createRfcommSocket", global::Java.Lang.Integer.Type!);
            var s = (BluetoothSocket?)method?.Invoke(device,
                new global::Java.Lang.Object[] { global::Java.Lang.Integer.ValueOf(channel)! });
            if (s == null) return null;
            return ConnectAndValidate(s, ref seed) ? s : Close(s);
        }
        catch (Exception ex) { Debug($"Channel {channel} connect failed: " + ex.Message); return null; }
    }

    // Opens the socket, sends the GET_DEV_INFO probe, and returns true only if the
    // radio answers with a GAIA frame (0xFF 0x01) within the timeout.
    private bool ConnectAndValidate(BluetoothSocket s, ref byte[] seed)
    {
        s.Connect();   // blocking
        var probe = GaiaFraming.Encode(GaiaFraming.GetDevInfoCmd);
        s.OutputStream!.Write(probe, 0, probe.Length);
        s.OutputStream!.Flush();

        byte[] buf = new byte[1024];
        int n = ReadWithTimeout(s.InputStream!, buf, 1500);
        if (n >= 2 && buf[0] == 0xFF && buf[1] == 0x01)
        {
            seed = new byte[n];
            Array.Copy(buf, seed, n);
            return true;
        }
        return false;
    }

    // Blocking Read with an overall deadline (BluetoothSocket streams have no timeout).
    private static int ReadWithTimeout(Stream stream, byte[] buffer, int ms)
    {
        var task = Task.Run(() => { try { return stream.Read(buffer, 0, buffer.Length); } catch { return -1; } });
        return task.Wait(ms) ? Math.Max(0, task.Result) : 0;
    }

    private static BluetoothSocket? Close(BluetoothSocket s) { try { s.Close(); } catch (Exception) { } return null; }

    private void CloseSocket()
    {
        try { socket?.Close(); } catch (Exception) { }
        socket = null; input = null; output = null;
    }

    private void Fail(string reason)
    {
        lock (connectionLock) { isConnecting = false; running = false; }
        Debug(reason);
        onDisconnected?.Invoke(reason);
    }

    private static BluetoothAdapter? GetAdapter()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var mgr = (BluetoothManager?)ctx.GetSystemService(Context.BluetoothService);
            return mgr?.Adapter ?? BluetoothAdapter.DefaultAdapter;
        }
        catch (Exception) { return BluetoothAdapter.DefaultAdapter; }
    }

    private static int ForcedChannel()
    {
        string? env = Environment.GetEnvironmentVariable("HTCOMMANDER_RFCOMM_CHANNEL");
        return !string.IsNullOrEmpty(env) && int.TryParse(env, out int c) && c > 0 && c <= 30 ? c : 0;
    }

    private static int[] Enumerable1To30()
    {
        var a = new int[30];
        for (int i = 0; i < 30; i++) a[i] = i + 1;
        return a;
    }

    // Canonicalize to "AA:BB:CC:DD:EE:FF" (Android's GetRemoteDevice requires colons).
    private static string NormalizeMac(string mac)
    {
        string hex = (mac ?? string.Empty).Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
        if (hex.Length != 12) return (mac ?? string.Empty).Trim().ToUpperInvariant();
        return string.Join(":", System.Linq.Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }
}
