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
using System.Collections.Generic;
using HTCommander.Core.Abstractions.Audio;

namespace HTCommander.Platform.Android;

/// <summary>
/// Round-one Android audio enumerator: reports no devices and hands out no-op
/// playback/capture. Audio (voice TX/RX) is deferred on Android (no PortAudio
/// runtime); the UI gates voice features off, but shared view-model code can still
/// call the factory without a platform-specific type. Round two replaces these with
/// AudioRecord/AudioTrack-backed implementations.
/// </summary>
public sealed class AndroidAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDevice> GetRenderDevices() => Array.Empty<AudioDevice>();
    public IReadOnlyList<AudioDevice> GetCaptureDevices() => Array.Empty<AudioDevice>();
    public AudioDevice? GetDefaultRenderDevice() => null;
    public AudioDevice? GetDefaultCaptureDevice() => null;
    public IAudioPlayback CreatePlayback() => new NoOpPlayback();
    public IAudioCapture CreateCapture() => new NoOpCapture();

    private sealed class NoOpPlayback : IAudioPlayback
    {
        public AudioFormat Format { get; set; } = AudioFormat.RadioPcm;
        public float Volume { get; set; } = 1f;
        public bool IsPlaying => false;
        public void SetDevice(string? deviceId) { }
        public bool Start() => false;        // no audio on Android round one
        public void Stop() { }
        public void AddSamples(byte[] buffer, int offset, int count) { }
        public void ClearBuffer() { }
        public void Dispose() { }
    }

    private sealed class NoOpCapture : IAudioCapture
    {
#pragma warning disable CS0067
        public event Action<byte[], int>? DataAvailable;
#pragma warning restore CS0067
        public AudioFormat Format { get; set; } = AudioFormat.RadioPcm;
        public bool IsCapturing => false;
        public void SetDevice(string? deviceId) { }
        public bool Start() => false;
        public void Stop() { }
        public void Dispose() { }
    }
}
