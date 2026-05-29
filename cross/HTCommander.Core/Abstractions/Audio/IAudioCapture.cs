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
/// Microphone / input capture. Replaces NAudio's <c>WasapiCapture</c> in
/// <c>Microphone.cs</c>. Delivers raw little-endian PCM in <see cref="Format"/>.
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>
    /// Raised when a buffer of captured audio is available: (buffer, bytesRecorded).
    /// The buffer is owned by the capturer and only valid for the duration of the
    /// call — copy it if retaining. Mirrors NAudio's DataAvailable contract.
    /// </summary>
    event Action<byte[], int> DataAvailable;

    /// <summary>Desired capture format. Set before <see cref="Start"/>.</summary>
    AudioFormat Format { get; set; }

    /// <summary>True while capturing.</summary>
    bool IsCapturing { get; }

    /// <summary>Selects the input device by <see cref="AudioDevice.Id"/>; null/empty = system default.</summary>
    void SetDevice(string? deviceId);

    /// <summary>Begins capture. Returns false if no device or the stream could not open.</summary>
    bool Start();

    /// <summary>Stops capture and releases the stream.</summary>
    void Stop();
}
