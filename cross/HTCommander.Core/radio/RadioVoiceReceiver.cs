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
/// Portable receive-side radio voice decoder. Radio voice rides a SEPARATE
/// RFCOMM stream from the GAIA command channel (service UUID 00001203-…), framed
/// SLIP-style (<c>0x7e</c> delimiters, <c>0x7d</c> escapes) with a one-byte command
/// tag, carrying SBC-coded 32 kHz/16-bit/mono audio. Feed raw bytes from that
/// stream to <see cref="OnAudioBytes"/>; decoded PCM is pushed to the injected
/// <see cref="IAudioPlayback"/>.
///
/// This is the platform-neutral RX core (ported from the WinForms RadioAudio
/// receive path) — wiring it to a real second RFCOMM socket is the platform's job
/// (Linux: see HTCommander.Platform.Linux). Transmit (which keys the radio on the
/// air) is intentionally NOT part of this type.
/// </summary>
public sealed class RadioVoiceReceiver
{
    private readonly IAudioPlayback playback;
    private readonly SbcDecoder decoder = new SbcDecoder();
    private readonly MemoryStream accumulator = new MemoryStream();
    private byte[] pcm = new byte[16384];
    private bool started;

    public RadioVoiceReceiver(IAudioPlayback playback) => this.playback = playback;

    /// <summary>Raised with each decoded PCM buffer (32k/16/mono): (buffer, byteCount).
    /// The buffer is reused — copy if retaining. Used to feed the soft-modem + waterfall.</summary>
    public event Action<byte[], int>? PcmDecoded;

    public void Start()
    {
        if (started) return;
        started = true;
        playback.Format = AudioFormat.RadioPcm;   // 32 kHz / 16-bit / mono
        playback.Start();
    }

    public void Stop()
    {
        started = false;
        try { playback.Stop(); } catch (Exception) { }
        accumulator.SetLength(0);
    }

    /// <summary>Feeds raw bytes read from the audio RFCOMM stream.</summary>
    public void OnAudioBytes(byte[] buffer, int count)
    {
        if (count <= 0) return;
        accumulator.Position = accumulator.Length;     // append
        accumulator.Write(buffer, 0, count);

        byte[]? frame;
        while ((frame = ExtractData()) != null)
        {
            int len = UnescapeInPlace(frame);
            if (len < 1) continue;
            byte cmd = frame[0];
            if (cmd == 0x00 || cmd == 0x03) DecodeSbc(frame, 1, len - 1);   // received audio
            else if (cmd == 0x01) playback.ClearBuffer();                    // end of audio run
        }
    }

    // Pulls one complete 0x7e…0x7e frame out of the accumulator (ported from the
    // WinForms ExtractData), discarding garbage/leading delimiters. Returns null
    // when no complete frame is buffered yet.
    private byte[]? ExtractData()
    {
        while (true)
        {
            int bufLen = (int)accumulator.Length;
            if (bufLen < 2) return null;
            byte[] buf = accumulator.GetBuffer();

            int scanFrom = (buf[0] == 0x7e && buf[1] == 0x7e) ? 1 : 0;
            int start = -1, end = -1;
            for (int i = scanFrom; i < bufLen; i++)
            {
                if (buf[i] != 0x7e) continue;
                if (start == -1) start = i;
                else { end = i; break; }
            }

            if (start != -1 && end != -1 && end > start + 1)
            {
                int dataLen = end - start - 1;
                byte[] data = new byte[dataLen];
                Buffer.BlockCopy(buf, start + 1, data, 0, dataLen);
                Compact(buf, end + 1, bufLen - (end + 1));
                return data;
            }
            if (start != -1 && end != -1 && end == start + 1) { Compact(buf, end, bufLen - end); continue; }
            if (start > 0) { Compact(buf, start, bufLen - start); continue; }
            if (start == -1) { accumulator.SetLength(0); accumulator.Position = 0; return null; }
            return null;   // start == 0, no end yet — wait for more
        }
    }

    private void Compact(byte[] buf, int from, int remaining)
    {
        if (remaining > 0) Buffer.BlockCopy(buf, from, buf, 0, remaining);
        accumulator.SetLength(remaining);
        accumulator.Position = remaining;
    }

    // Reverses 0x7d escaping in place; returns the unescaped length.
    private static int UnescapeInPlace(byte[] b)
    {
        int src = 0, dst = 0;
        while (src < b.Length)
        {
            if (b[src] == 0x7d)
            {
                src++;
                if (src < b.Length) { b[dst++] = (byte)(b[src] ^ 0x20); src++; }
                else break;
            }
            else { b[dst++] = b[src++]; }
        }
        return dst;
    }

    // Decodes the concatenated SBC frames in a payload and pushes PCM to playback
    // (ported from the WinForms DecodeSbcFrame receive path).
    private void DecodeSbc(byte[] f, int start, int length)
    {
        int off = start, rem = length, written = 0;
        while (rem > 0)
        {
            byte sync = f[off];
            if (sync != 0x9C && sync != 0xAD) break;          // SBC / mSBC sync
            if (rem < SbcFrame.HeaderSize) break;

            byte[] header = new byte[SbcFrame.HeaderSize];
            Buffer.BlockCopy(f, off, header, 0, SbcFrame.HeaderSize);
            SbcFrame? probed = decoder.Probe(header);
            if (probed == null) break;
            int size = probed.GetFrameSize();
            if (size <= 0 || size > rem) break;

            byte[] sbc = new byte[size];
            Buffer.BlockCopy(f, off, sbc, 0, size);
            if (!decoder.Decode(sbc, out short[] left, out _, out SbcFrame frame)) break;
            if (frame.GetFrameSize() != size) break;

            int bytes = left.Length * 2;
            if (written + bytes > pcm.Length) Array.Resize(ref pcm, written + bytes);
            Buffer.BlockCopy(left, 0, pcm, written, bytes);
            written += bytes;
            off += size; rem -= size;
        }
        if (written > 0)
        {
            playback.AddSamples(pcm, 0, written);
            try { PcmDecoded?.Invoke(pcm, written); } catch (Exception) { }
        }
    }
}
