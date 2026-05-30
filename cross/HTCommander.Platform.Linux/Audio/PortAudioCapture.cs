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

/// <summary>PortAudio-backed <see cref="IAudioCapture"/> (replaces WasapiCapture).</summary>
public sealed class PortAudioCapture : IAudioCapture
{
    private readonly object gate = new object();
    private PaStream? stream;
    private PaStream.Callback? callback;   // held to keep the delegate alive for native code
    private int deviceIndex = -1;          // -1 => system default
    private byte[] scratch = Array.Empty<byte>();

    public event Action<byte[], int>? DataAvailable;
    public AudioFormat Format { get; set; } = AudioFormat.RadioPcm;
    public bool IsCapturing { get; private set; }

    public void SetDevice(string? deviceId)
    {
        int idx = PortAudioLifecycle.ResolveDeviceIndex(deviceId, output: false);
        lock (gate)
        {
            if (idx == deviceIndex) return;
            deviceIndex = idx;
            if (IsCapturing) { StopInternal(); StartInternal(); }
        }
    }

    public bool Start() { lock (gate) { return StartInternal(); } }
    public void Stop() { lock (gate) { StopInternal(); } }
    public void Dispose() { lock (gate) { StopInternal(); } }

    private bool StartInternal()
    {
        StopInternal();
        PortAudioLifecycle.EnsureInitialized();
        int dev = deviceIndex >= 0 ? deviceIndex : PortAudio.DefaultInputDevice;
        if (dev < 0 || dev == PortAudio.NoDevice) return false;

        var info = PortAudio.GetDeviceInfo(dev);
        var inParams = new StreamParameters
        {
            device = dev,
            channelCount = Format.Channels,
            sampleFormat = SampleFormat.Int16,
            suggestedLatency = info.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        callback = OnCallback;
        try
        {
            // Request a fixed buffer (2 SBC frames = 8 ms) so capture delivers regular
            // chunks. With FramesPerBufferUnspecified, PipeWire fragments the stream
            // into tiny/irregular buffers, which makes transmit audio stutter/garble.
            stream = new PaStream((StreamParameters?)inParams, null, Format.SampleRate,
                256u, StreamFlags.ClipOff, callback, IntPtr.Zero);
            stream.Start();
            IsCapturing = true;
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
        int bytes = (int)frameCount * Format.BlockAlign;
        if (input != IntPtr.Zero && bytes > 0)
        {
            if (scratch.Length < bytes) scratch = new byte[bytes];
            Marshal.Copy(input, scratch, 0, bytes);
            DataAvailable?.Invoke(scratch, bytes);
        }
        return StreamCallbackResult.Continue;
    }

    private void StopInternal()
    {
        IsCapturing = false;
        if (stream != null)
        {
            try { stream.Stop(); } catch (Exception) { }
            try { stream.Close(); } catch (Exception) { }
            try { stream.Dispose(); } catch (Exception) { }
            stream = null;
        }
        callback = null;
    }
}
