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
using HTCommander.Core.Abstractions.Audio;

namespace HTCommander;

/// <summary>
/// Portable transmit-side radio voice encoder. Captures mic PCM, SBC-encodes it
/// (32 kHz/16-bit/mono), SLIP-frames each batch with the <c>0x00</c> "audio" tag,
/// and hands the framed bytes to a send callback (wire to the audio RFCOMM
/// channel). Keying is implicit in the protocol: sending audio frames keys the
/// radio; <see cref="Stop"/> sends the end frame to un-key.
///
/// ⚠ TRANSMIT = ON-AIR RF EMISSION. Construction/encoding is inert; nothing is
/// sent until <see cref="Start"/> is called and the send callback writes to the
/// radio. The caller (UI) must gate this behind an explicit, operator-controlled
/// PTT and ensure a lawful frequency/power. Live mic input is inherently
/// real-time-paced, so no extra pacing is needed here.
/// </summary>
public sealed class RadioVoiceTransmitter
{
    // End-of-audio frame: tells the radio to stop transmitting (un-key).
    private static readonly byte[] EndFrame = { 0x7e, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x7e };

    private readonly IAudioCapture mic;
    private readonly Action<byte[]> send;
    private readonly SbcEncoder encoder = new SbcEncoder();
    private readonly SbcFrame frame = new SbcFrame
    {
        Frequency = SbcFrequency.Freq32K,
        Blocks = 16,
        Mode = SbcMode.Mono,
        AllocationMethod = SbcBitAllocationMethod.Loudness,
        Subbands = 8,
        Bitpool = 18
    };
    private readonly int pcmFrameBytes;            // bytes of PCM per SBC frame (128 samples * 2)
    private readonly object gate = new object();
    private byte[] remainder = Array.Empty<byte>();
    private volatile bool transmitting;

    public bool IsTransmitting => transmitting;

    /// <summary>Linear mic gain applied to PCM before encoding (1.0 = unity). Mirrors the WinForms mic Boost.</summary>
    public float Gain { get; set; } = 1.0f;

    public RadioVoiceTransmitter(IAudioCapture mic, Action<byte[]> send)
    {
        this.mic = mic;
        this.send = send;
        pcmFrameBytes = frame.Blocks * frame.Subbands * 2;
    }

    /// <summary>Begins capturing + transmitting (keys the radio). On-air emission starts here.</summary>
    public bool Start()
    {
        lock (gate)
        {
            if (transmitting) return true;
            transmitting = true;
            remainder = Array.Empty<byte>();
        }
        mic.Format = AudioFormat.RadioPcm;
        mic.DataAvailable += OnMic;
        if (!mic.Start())
        {
            mic.DataAvailable -= OnMic;
            lock (gate) { transmitting = false; }
            return false;
        }
        return true;
    }

    /// <summary>Stops transmitting and sends the end frame to un-key the radio.</summary>
    public void Stop()
    {
        bool wasTx;
        lock (gate) { wasTx = transmitting; transmitting = false; }
        if (!wasTx) return;
        try { mic.DataAvailable -= OnMic; } catch (Exception) { }
        try { mic.Stop(); } catch (Exception) { }
        try { send(EndFrame); } catch (Exception) { }
    }

    private void OnMic(byte[] pcm, int count)
    {
        if (!transmitting || count <= 0) return;

        byte[] buf;
        lock (gate)
        {
            buf = new byte[remainder.Length + count];
            Buffer.BlockCopy(remainder, 0, buf, 0, remainder.Length);
            Buffer.BlockCopy(pcm, 0, buf, remainder.Length, count);
            remainder = Array.Empty<byte>();
        }

        int off = 0;
        float gain = Gain;
        using var sbcAll = new MemoryStream();
        int samplesPerFrame = frame.Blocks * frame.Subbands;
        while (buf.Length - off >= pcmFrameBytes)
        {
            short[] samples = new short[samplesPerFrame];
            for (int i = 0; i < samplesPerFrame; i++)
            {
                short s = (short)(buf[off + i * 2] | (buf[off + i * 2 + 1] << 8));
                if (gain != 1.0f)
                {
                    int v = (int)(s * gain);
                    if (v > short.MaxValue) v = short.MaxValue;
                    else if (v < short.MinValue) v = short.MinValue;
                    s = (short)v;
                }
                samples[i] = s;
            }
            byte[] sbc = encoder.Encode(samples, null, frame);
            if (sbc == null || sbc.Length == 0) break;
            sbcAll.Write(sbc, 0, sbc.Length);
            off += pcmFrameBytes;
        }

        int leftover = buf.Length - off;
        if (leftover > 0)
        {
            byte[] rem = new byte[leftover];
            Buffer.BlockCopy(buf, off, rem, 0, leftover);
            lock (gate) { remainder = rem; }
        }

        if (sbcAll.Length > 0 && transmitting)
        {
            try { send(Escape(0x00, sbcAll.ToArray())); } catch (Exception) { }
        }
    }

    // SLIP-frames a payload: 0x7e <cmd> <0x7d/0x7e-escaped payload> 0x7e.
    private static byte[] Escape(byte cmd, byte[] data)
    {
        using var o = new MemoryStream(data.Length + 4);
        o.WriteByte(0x7e);
        o.WriteByte(cmd);
        foreach (byte b in data)
        {
            if (b == 0x7d || b == 0x7e) { o.WriteByte(0x7d); o.WriteByte((byte)(b ^ 0x20)); }
            else o.WriteByte(b);
        }
        o.WriteByte(0x7e);
        return o.ToArray();
    }
}
