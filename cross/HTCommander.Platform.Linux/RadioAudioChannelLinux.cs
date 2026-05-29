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
/// CHANNEL DISCOVERY GAP: unlike the GAIA channel (positively identified by its
/// 0xFF 0x01 reply), the audio service is silent until audio flows, so it cannot
/// be probe-validated, and this radio's SDP is unreliable over BlueZ/sdptool. So
/// the channel must be supplied explicitly: env <c>HTCOMMANDER_AUDIO_CHANNEL=N</c>,
/// or via <paramref name="channel"/>. A proper fix is a programmatic SDP query
/// (L2CAP PSM 1, ServiceSearchAttribute for UUID 0x1203 → RFCOMM channel) — TODO.
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
    /// Connects the audio RFCOMM socket. <paramref name="channel"/> &gt; 0 forces a
    /// channel; otherwise the env override <c>HTCOMMANDER_AUDIO_CHANNEL</c> is used.
    /// Returns false if no channel is known or the socket could not open.
    /// </summary>
    public bool Connect(int channel = 0)
    {
        if (channel <= 0)
        {
            string? env = Environment.GetEnvironmentVariable("HTCOMMANDER_AUDIO_CHANNEL");
            if (!string.IsNullOrEmpty(env) && int.TryParse(env, out int c) && c > 0) channel = c;
        }
        if (channel <= 0)
        {
            Debug("No audio RFCOMM channel known (set HTCOMMANDER_AUDIO_CHANNEL); not connecting.");
            return false;
        }
        if (!NativeRfcomm.TryParseBdAddr(macAddress, out byte[] bdaddr))
        {
            Debug("Invalid MAC: " + macAddress);
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
