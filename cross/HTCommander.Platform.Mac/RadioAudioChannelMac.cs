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
using HTCommander.Core.Abstractions;

namespace HTCommander.Platform.Mac;

/// <summary>
/// macOS voice-audio channel (the radio's SECOND RFCOMM stream, SBC voice). Mirrors
/// <c>RadioAudioChannelLinux</c> but opens the channel through the libhtbt IOBluetooth
/// bridge, discovered by the SDP service name "AOC" (the "BS AOC" service — NOT the
/// silent 0x1203 HFP gateway). Raw bytes flow both ways: RX → <see cref="DataReceived"/>,
/// TX → <see cref="Send"/> (which keys the radio on the air).
/// </summary>
public sealed class RadioAudioChannelMac : IRadioAudioChannel
{
    // The radio's SBC voice stream is the RFCOMM service named "BS AOC"; match on "AOC".
    private const string AudioServiceName = "AOC";

    private readonly string macAddress;
    private readonly ILogger? logger;
    private readonly object gate = new object();

    private int handle = -1;
    // Native callbacks held in fields so the GC can't collect them while native code
    // still holds the function pointers.
    private NativeHtbt.DataCallback? dataCb;
    private NativeHtbt.EventCallback? eventCb;

    public event Action<byte[], int>? DataReceived;

    public RadioAudioChannelMac(string macAddress, ILogger? logger = null)
    {
        this.macAddress = NormalizeAddress(macAddress);
        this.logger = logger;
    }

    private static string NormalizeAddress(string addr)
    {
        string a = (addr ?? string.Empty).Trim().Replace(":", "-").ToLowerInvariant();
        if (!a.Contains('-') && a.Length == 12)
        {
            var sb = new System.Text.StringBuilder(17);
            for (int i = 0; i < 12; i += 2) { if (i > 0) sb.Append('-'); sb.Append(a, i, 2); }
            a = sb.ToString();
        }
        return a;
    }

    private void Debug(string m) => logger?.Debug("AudioChannel: " + m);

    /// <summary>
    /// Opens the audio channel. The macOS bridge discovers it by SDP service name, so
    /// <paramref name="channel"/> is ignored (kept for interface parity). Must run off
    /// the main thread (the bridge dispatches its IOBluetooth work to the main run loop).
    /// </summary>
    public bool Connect(int channel = 0)
    {
        dataCb = OnNativeData;
        eventCb = OnNativeEvent;
        int h;
        try { h = NativeHtbt.OpenAudio(macAddress, AudioServiceName, dataCb, eventCb); }
        catch (DllNotFoundException) { Debug("libhtbt.dylib not found."); return false; }
        catch (Exception ex) { Debug("OpenAudio failed: " + ex.Message); return false; }

        if (h < 0) { Debug($"Audio service \"{AudioServiceName}\" not found / channel did not open."); return false; }
        lock (gate) { handle = h; }
        Debug($"Audio channel open (handle {h}).");
        return true;
    }

    public bool Send(byte[] data)
    {
        int h;
        lock (gate) { h = handle; }
        if (h < 0 || data == null || data.Length == 0) return false;
        try { NativeHtbt.AudioWrite(h, data, data.Length); return true; }
        catch (Exception ex) { Debug("Send failed: " + ex.Message); return false; }
    }

    public void Disconnect()
    {
        int h;
        lock (gate) { h = handle; handle = -1; }
        if (h >= 0) { try { NativeHtbt.AudioClose(h); } catch (Exception) { } }
    }

    private void OnNativeData(IntPtr data, int len)
    {
        if (len <= 0 || data == IntPtr.Zero) return;
        byte[] chunk = new byte[len];
        Marshal.Copy(data, chunk, 0, len);
        try { DataReceived?.Invoke(chunk, len); } catch (Exception) { }
    }

    private void OnNativeEvent(int kind)
    {
        if (kind == 0) return;                 // 1=closed, 2=error
        lock (gate) { handle = -1; }
        Debug(kind == 2 ? "Audio channel error." : "Audio channel closed.");
    }
}
