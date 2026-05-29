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

using PortAudioSharp;

namespace HTCommander.Platform.Linux.Audio;

/// <summary>
/// Process-wide PortAudio initialization guard. PortAudio must be initialized
/// once before any device/stream call; this initializes lazily and thread-safely.
/// PortAudio is intentionally never Terminate()d — it is torn down at process exit,
/// and terminating while a UI may still create streams is error-prone.
/// </summary>
internal static class PortAudioLifecycle
{
    private static readonly object gate = new object();
    private static bool initialized;

    public static void EnsureInitialized()
    {
        if (initialized) return;
        lock (gate)
        {
            if (initialized) return;
            PortAudio.LoadNativeLibrary();
            PortAudio.Initialize();
            initialized = true;
        }
    }

    /// <summary>
    /// Resolves an <see cref="HTCommander.Core.Abstractions.Audio.AudioDevice.Id"/>
    /// (a PortAudio device index as a string) to a device index in the requested
    /// direction, or -1 to mean "use the system default".
    /// </summary>
    public static int ResolveDeviceIndex(string? id, bool output)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(id)) return -1;
        if (int.TryParse(id, out int idx) && idx >= 0 && idx < PortAudio.DeviceCount)
        {
            var info = PortAudio.GetDeviceInfo(idx);
            int ch = output ? info.maxOutputChannels : info.maxInputChannels;
            if (ch > 0) return idx;
        }
        return -1;
    }
}
