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
using HTCommander.Core.Abstractions.Audio;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;   // disambiguate from System.IO.Stream

namespace HTCommander.Platform.Linux.Audio;

/// <summary>
/// PortAudio-backed <see cref="IAudioPlayback"/>. Replaces WasapiOut +
/// BufferedWaveProvider + VolumeSampleProvider: callers push PCM with
/// <see cref="AddSamples"/> into an internal ring buffer that the PortAudio
/// output callback drains, applying <see cref="Volume"/> and zero-filling on
/// underrun. Incoming audio beyond capacity is discarded to bound latency.
///
/// Two locks: <c>lifeLock</c> serializes Start/Stop/SetDevice and is NEVER taken
/// by the audio callback, so the blocking native Stop()/Close() cannot deadlock
/// against the callback; <c>bufLock</c> guards only the ring buffer and is taken
/// briefly by the callback.
/// </summary>
public sealed class PortAudioPlayback : IAudioPlayback
{
    private readonly object lifeLock = new object();
    private readonly object bufLock = new object();
    private PaStream? stream;
    private PaStream.Callback? callback;   // held to keep the delegate alive for native code
    private int deviceIndex = -1;          // -1 => system default

    // Ring buffer (bytes of interleaved little-endian PCM).
    private byte[] ring = Array.Empty<byte>();
    private int head, count, capacity;
    private byte[] outScratch = Array.Empty<byte>();

    public AudioFormat Format { get; set; } = AudioFormat.RadioPcm;
    public float Volume { get; set; } = 1.0f;
    public bool IsPlaying { get; private set; }

    public void SetDevice(string? deviceId)
    {
        int idx = PortAudioLifecycle.ResolveDeviceIndex(deviceId, output: true);
        lock (lifeLock)
        {
            if (idx == deviceIndex) return;
            deviceIndex = idx;
            if (IsPlaying) { StopInternal(); StartInternal(); }
        }
    }

    public bool Start() { lock (lifeLock) { return StartInternal(); } }
    public void Stop() { lock (lifeLock) { StopInternal(); } }
    public void Dispose() { lock (lifeLock) { StopInternal(); } }

    public void ClearBuffer() { lock (bufLock) { head = 0; count = 0; } }

    public void AddSamples(byte[] buffer, int offset, int count)
    {
        lock (bufLock)
        {
            if (capacity == 0) return;
            int free = capacity - this.count;
            int n = Math.Min(count, free);              // discard overflow (bounded latency)
            int tail = (head + this.count) % capacity;
            for (int i = 0; i < n; i++)
            {
                ring[tail] = buffer[offset + i];
                tail = (tail + 1) % capacity;
            }
            this.count += n;
        }
    }

    private bool StartInternal()
    {
        StopInternal();
        PortAudioLifecycle.EnsureInitialized();
        int dev = deviceIndex >= 0 ? deviceIndex : PortAudio.DefaultOutputDevice;
        if (dev < 0 || dev == PortAudio.NoDevice) return false;

        lock (bufLock)
        {
            capacity = Math.Max(Format.AverageBytesPerSecond * 2, 1 << 16);   // ~2s jitter buffer
            ring = new byte[capacity];
            head = 0; count = 0;
        }

        var info = PortAudio.GetDeviceInfo(dev);
        var outParams = new StreamParameters
        {
            device = dev,
            channelCount = Format.Channels,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = info.defaultLowOutputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        callback = OnCallback;
        try
        {
            stream = new PaStream(null, (StreamParameters?)outParams, Format.SampleRate,
                PortAudio.FramesPerBufferUnspecified, StreamFlags.ClipOff, callback, IntPtr.Zero);
            stream.Start();
            IsPlaying = true;
            return true;
        }
        catch (Exception)
        {
            StopInternal();
            return false;
        }
    }

    private StreamCallbackResult OnCallback(IntPtr input, IntPtr output, uint frameCount,
        ref StreamCallbackTimeInfo timeInfo, StreamCallbackFlags statusFlags, IntPtr userData)
    {
        int needed = (int)frameCount * Format.BlockAlign;
        if (needed <= 0 || output == IntPtr.Zero) return StreamCallbackResult.Continue;
        if (outScratch.Length < needed) outScratch = new byte[needed];

        int filled;
        lock (bufLock)
        {
            filled = Math.Min(needed, count);
            for (int i = 0; i < filled; i++)
            {
                outScratch[i] = ring[head];
                head = (head + 1) % capacity;
            }
            count -= filled;
        }

        if (filled < needed) Array.Clear(outScratch, filled, needed - filled);   // underrun -> silence
        ApplyVolume(outScratch, filled, Volume);
        Marshal.Copy(outScratch, 0, output, needed);
        return StreamCallbackResult.Continue;
    }

    private static void ApplyVolume(byte[] pcm16, int byteLen, float volume)
    {
        if (volume == 1.0f) return;
        for (int i = 0; i + 1 < byteLen; i += 2)
        {
            short s = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            int v = (int)(s * volume);
            if (v > short.MaxValue) v = short.MaxValue;
            if (v < short.MinValue) v = short.MinValue;
            pcm16[i] = (byte)(v & 0xFF);
            pcm16[i + 1] = (byte)((v >> 8) & 0xFF);
        }
    }

    // Called only under lifeLock. The native Stop()/Close() block until the audio
    // callback returns; that is safe because the callback never takes lifeLock.
    private void StopInternal()
    {
        IsPlaying = false;
        if (stream != null)
        {
            try { stream.Stop(); } catch (Exception) { }
            try { stream.Close(); } catch (Exception) { }
            try { stream.Dispose(); } catch (Exception) { }
            stream = null;
        }
        callback = null;
        lock (bufLock) { capacity = 0; head = 0; count = 0; }
    }
}
