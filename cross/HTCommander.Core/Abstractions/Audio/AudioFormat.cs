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

namespace HTCommander.Core.Abstractions.Audio;

/// <summary>
/// Linear PCM audio format. Replaces NAudio's <c>WaveFormat</c> in the portable
/// layer. The HTCommander radio audio path is 32 kHz / 16-bit / mono throughout.
/// </summary>
public readonly record struct AudioFormat(int SampleRate, int BitsPerSample, int Channels)
{
    /// <summary>The radio audio format used across capture, playback and recording.</summary>
    public static AudioFormat RadioPcm => new(32000, 16, 1);

    /// <summary>Bytes per sample frame (all channels).</summary>
    public int BlockAlign => Channels * (BitsPerSample / 8);

    /// <summary>Bytes per second of audio.</summary>
    public int AverageBytesPerSecond => SampleRate * BlockAlign;
}
