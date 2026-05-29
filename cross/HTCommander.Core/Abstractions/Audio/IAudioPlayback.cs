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

namespace HTCommander.Core.Abstractions.Audio;

/// <summary>
/// Speaker / output playback with an internal jitter buffer. Replaces the NAudio
/// <c>WasapiOut</c> + <c>BufferedWaveProvider</c> + <c>VolumeSampleProvider</c>
/// combination in <c>RadioAudio.cs</c>: callers push decoded PCM with
/// <see cref="AddSamples"/> and the implementation paces it to the device.
/// </summary>
public interface IAudioPlayback : IDisposable
{
    /// <summary>Playback format. Set before <see cref="Start"/>.</summary>
    AudioFormat Format { get; set; }

    /// <summary>Linear output gain (1.0 = unity). Applied to enqueued samples.</summary>
    float Volume { get; set; }

    /// <summary>True while the output stream is running.</summary>
    bool IsPlaying { get; }

    /// <summary>Selects the output device by <see cref="AudioDevice.Id"/>; null/empty = system default.</summary>
    void SetDevice(string? deviceId);

    /// <summary>Opens the output stream and begins draining the buffer.</summary>
    bool Start();

    /// <summary>Stops playback and releases the stream.</summary>
    void Stop();

    /// <summary>
    /// Enqueues little-endian PCM (in <see cref="Format"/>) for playback. Excess
    /// audio beyond the buffer capacity is discarded (mirrors NAudio's
    /// DiscardOnBufferOverflow) to bound latency.
    /// </summary>
    void AddSamples(byte[] buffer, int offset, int count);

    /// <summary>Drops any buffered-but-unplayed audio.</summary>
    void ClearBuffer();
}
