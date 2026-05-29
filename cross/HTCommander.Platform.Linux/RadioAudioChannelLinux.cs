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
using System.Threading;
using System.Threading.Tasks;
using HTCommander.Core.Abstractions;
using HTCommander.Platform.Linux.Bluetooth;

namespace HTCommander.Platform.Linux;

/// <summary>
/// Opens the radio's SECOND RFCOMM stream — the voice-audio channel (Windows
/// connects it by service UUID 00001203-…) — as a raw kernel RFCOMM socket and
/// pumps received bytes to a callback (wire it to <c>RadioVoiceReceiver</c>). This
/// runs independently of, and concurrently with, the GAIA command transport.
///
/// CHANNEL DISCOVERY: the audio service is silent until audio flows (so it can't
/// be probe-validated like the GAIA channel) and BlueZ/sdptool were unreliable
/// here, so the channel is discovered via a direct SDP query for UUID 0x1203
/// (<see cref="SdpClient"/>). Priority: explicit arg → env <c>HTCOMMANDER_AUDIO_CHANNEL</c>
/// → SDP. The radio assigns this channel dynamically, so SDP (queried fresh per
/// connect) is the reliable source.
///
/// Receive only. Transmit keys the radio on the air and is intentionally omitted.
/// </summary>
public sealed class RadioAudioChannelLinux
{
    private readonly string macAddress;
    private readonly ILogger? logger;
    private readonly object gate = new object();
    private NativeRfcomm.RfcommStream? stream;
    private CancellationTokenSource? cts;
    private Task? readTask;

    /// <summary>Raised with (buffer, count) for each chunk read from the audio stream.</summary>
    public event Action<byte[], int>? DataReceived;

    public RadioAudioChannelLinux(string macAddress, ILogger? logger = null)
    {
        this.macAddress = (macAddress ?? string.Empty).Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
        this.logger = logger;
    }

    private void Debug(string m) => logger?.Debug("AudioChannel: " + m);

    /// <summary>
    /// The radio's SBC voice stream is the RFCOMM service named "BS AOC" (verified:
    /// it streams ~64 kB/s of decodable SBC during RX). The 0x1203 "GenericAudio"
    /// service the Windows app targets is, on this radio, the Handsfree gateway and
    /// streams nothing — so we discover by name, with 0x1203 as a fallback.
    /// </summary>
    private const string AudioServiceName = "AOC";
    private const ushort AudioServiceUuidFallback = 0x1203;

    /// <summary>
    /// Connects the audio RFCOMM socket. Channel priority: <paramref name="channel"/>
    /// &gt; 0 → env <c>HTCOMMANDER_AUDIO_CHANNEL</c> → SDP discovery (UUID 0x1203).
    /// Returns false if no channel could be determined or the socket could not open.
    /// </summary>
    public bool Connect(int channel = 0)
    {
        if (!NativeRfcomm.TryParseBdAddr(macAddress, out byte[] bdaddr))
        {
            Debug("Invalid MAC: " + macAddress);
            return false;
        }

        if (channel <= 0)
        {
            string? env = Environment.GetEnvironmentVariable("HTCOMMANDER_AUDIO_CHANNEL");
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out int c) && c > 0) channel = c;
        }
        if (channel <= 0)
        {
            int? byName = SdpClient.FindRfcommChannelByName(bdaddr, AudioServiceName);
            if (byName.HasValue) { channel = byName.Value; Debug($"SDP: audio service \"{AudioServiceName}\" on RFCOMM channel {channel}."); }
        }
        if (channel <= 0)
        {
            int? byUuid = SdpClient.FindRfcommChannel(bdaddr, AudioServiceUuidFallback);
            if (byUuid.HasValue) { channel = byUuid.Value; Debug($"SDP fallback: 0x{AudioServiceUuidFallback:X4} on RFCOMM channel {channel}."); }
        }
        if (channel <= 0)
        {
            Debug("Could not determine the audio RFCOMM channel (SDP failed; set HTCOMMANDER_AUDIO_CHANNEL).");
            return false;
        }

        int fd = NativeRfcomm.TryConnect(bdaddr, channel, 8000);
        if (fd < 0) { Debug($"Could not open audio RFCOMM channel {channel}."); return false; }

        lock (gate)
        {
            stream = new NativeRfcomm.RfcommStream(fd);
            cts = new CancellationTokenSource();
            readTask = Task.Run(() => ReadLoop(stream, cts.Token));
        }
        Debug($"Audio channel {channel} open.");
        return true;
    }

    private void ReadLoop(NativeRfcomm.RfcommStream s, CancellationToken ct)
    {
        byte[] buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = s.Read(buf, 0, buf.Length);
                if (n <= 0) break;                 // peer closed
                DataReceived?.Invoke(buf, n);
            }
        }
        catch (Exception) { /* closed during teardown */ }
    }

    public void Disconnect()
    {
        lock (gate)
        {
            try { cts?.Cancel(); } catch (Exception) { }
            try { stream?.Dispose(); } catch (Exception) { }   // unblocks ReadLoop
            stream = null;
        }
        try { readTask?.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
        lock (gate) { try { cts?.Dispose(); } catch (Exception) { } cts = null; readTask = null; }
    }
}
