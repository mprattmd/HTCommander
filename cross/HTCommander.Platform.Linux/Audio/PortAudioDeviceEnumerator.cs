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

using System.Collections.Generic;
using HTCommander.Core.Abstractions.Audio;
using PortAudioSharp;

namespace HTCommander.Platform.Linux.Audio;

/// <summary>PortAudio-backed <see cref="IAudioDeviceEnumerator"/>.</summary>
public sealed class PortAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    public IReadOnlyList<AudioDevice> GetRenderDevices() => GetDevices(output: true);
    public IReadOnlyList<AudioDevice> GetCaptureDevices() => GetDevices(output: false);

    public AudioDevice? GetDefaultRenderDevice() => GetDefault(output: true);
    public AudioDevice? GetDefaultCaptureDevice() => GetDefault(output: false);

    public IAudioPlayback CreatePlayback() => new PortAudioPlayback();
    public IAudioCapture CreateCapture() => new PortAudioCapture();

    private static List<AudioDevice> GetDevices(bool output)
    {
        PortAudioLifecycle.EnsureInitialized();
        var list = new List<AudioDevice>();
        int def = output ? PortAudio.DefaultOutputDevice : PortAudio.DefaultInputDevice;
        for (int i = 0; i < PortAudio.DeviceCount; i++)
        {
            var info = PortAudio.GetDeviceInfo(i);
            int ch = output ? info.maxOutputChannels : info.maxInputChannels;
            if (ch <= 0) continue;
            list.Add(new AudioDevice(i.ToString(), info.name) { IsDefault = i == def });
        }
        return list;
    }

    private static AudioDevice? GetDefault(bool output)
    {
        PortAudioLifecycle.EnsureInitialized();
        int def = output ? PortAudio.DefaultOutputDevice : PortAudio.DefaultInputDevice;
        if (def < 0 || def == PortAudio.NoDevice || def >= PortAudio.DeviceCount) return null;
        var info = PortAudio.GetDeviceInfo(def);
        return new AudioDevice(def.ToString(), info.name) { IsDefault = true };
    }
}
