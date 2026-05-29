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
/// Writes a PCM WAV (RIFF) file. Replaces NAudio's <c>WaveFileWriter</c> used for
/// recording and saved audio clips. A portable implementation lives in
/// <c>HTCommander.Core.Audio.WavFileWriter</c> (RIFF is platform-neutral).
/// </summary>
public interface IWaveFileWriter : IDisposable
{
    AudioFormat Format { get; }

    /// <summary>Appends little-endian PCM sample data.</summary>
    void Write(byte[] buffer, int offset, int count);

    /// <summary>Flushes buffered bytes to disk.</summary>
    void Flush();
}

/// <summary>
/// Reads a PCM WAV (RIFF) file. Replaces NAudio's <c>WaveFileReader</c> used for
/// playing saved audio clips.
/// </summary>
public interface IWaveFileReader : IDisposable
{
    AudioFormat Format { get; }

    /// <summary>Total number of PCM data bytes in the file.</summary>
    long TotalBytes { get; }

    /// <summary>Reads up to <paramref name="count"/> PCM bytes; returns bytes read (0 at end).</summary>
    int Read(byte[] buffer, int offset, int count);
}
