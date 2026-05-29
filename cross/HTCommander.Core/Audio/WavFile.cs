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
using System.Text;
using HTCommander.Core.Abstractions.Audio;

namespace HTCommander.Core.Audio;

/// <summary>
/// Portable PCM WAV (RIFF) writer. Pure managed code — RIFF/PCM is platform
/// neutral, so this serves every platform and needs no native audio library.
/// The canonical 44-byte PCM header is written up front and the size fields are
/// patched on <see cref="Dispose"/>.
/// </summary>
public sealed class WavFileWriter : IWaveFileWriter
{
    private readonly Stream stream;
    private readonly bool ownsStream;
    private long dataBytes;
    private bool disposed;

    public AudioFormat Format { get; }

    public WavFileWriter(string path, AudioFormat format)
        : this(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read), format, ownsStream: true) { }

    public WavFileWriter(Stream output, AudioFormat format, bool ownsStream = false)
    {
        stream = output;
        this.ownsStream = ownsStream;
        Format = format;
        WriteHeader(0);
    }

    private void WriteHeader(long data)
    {
        int byteRate = Format.AverageBytesPerSecond;
        short blockAlign = (short)Format.BlockAlign;
        var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        stream.Position = 0;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write((uint)(36 + data));
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write((uint)16);                          // PCM fmt chunk size
        w.Write((ushort)1);                         // PCM
        w.Write((ushort)Format.Channels);
        w.Write((uint)Format.SampleRate);
        w.Write((uint)byteRate);
        w.Write((ushort)blockAlign);
        w.Write((ushort)Format.BitsPerSample);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write((uint)data);
        w.Flush();
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        if (disposed) throw new ObjectDisposedException(nameof(WavFileWriter));
        stream.Write(buffer, offset, count);
        dataBytes += count;
    }

    public void Flush() => stream.Flush();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (stream.CanSeek) WriteHeader(dataBytes);  // patch RIFF + data sizes
        stream.Flush();
        if (ownsStream) stream.Dispose();
    }
}

/// <summary>
/// Portable PCM WAV (RIFF) reader. Parses the fmt/data chunks and exposes the
/// PCM payload. Handles arbitrary chunk ordering and pad bytes.
/// </summary>
public sealed class WavFileReader : IWaveFileReader
{
    private readonly Stream stream;
    private readonly bool ownsStream;
    private readonly long dataStart;
    private long dataRemaining;
    private bool disposed;

    public AudioFormat Format { get; }
    public long TotalBytes { get; }

    public WavFileReader(string path)
        : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ownsStream: true) { }

    public WavFileReader(Stream input, bool ownsStream = false)
    {
        stream = input;
        this.ownsStream = ownsStream;
        var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        if (new string(r.ReadChars(4)) != "RIFF") throw new InvalidDataException("Not a RIFF file.");
        r.ReadUInt32();                                   // overall size (ignored)
        if (new string(r.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

        int sampleRate = 0, channels = 0, bits = 0;
        long foundDataStart = -1, foundDataLen = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            string id = new string(r.ReadChars(4));
            uint size = r.ReadUInt32();
            if (id == "fmt ")
            {
                r.ReadUInt16();                           // audio format (PCM=1)
                channels = r.ReadUInt16();
                sampleRate = (int)r.ReadUInt32();
                r.ReadUInt32();                           // byte rate
                r.ReadUInt16();                           // block align
                bits = r.ReadUInt16();
                long consumed = 16;
                if (size > consumed) stream.Position += size - consumed;  // skip any extension
            }
            else if (id == "data")
            {
                foundDataStart = stream.Position;
                foundDataLen = size;
                break;                                    // PCM payload starts here
            }
            else
            {
                stream.Position += size + (size & 1);     // skip chunk + pad byte
            }
        }

        if (foundDataStart < 0) throw new InvalidDataException("WAV has no data chunk.");
        Format = new AudioFormat(sampleRate, bits, channels);
        TotalBytes = foundDataLen;
        dataStart = foundDataStart;
        dataRemaining = foundDataLen;
        stream.Position = dataStart;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (disposed) throw new ObjectDisposedException(nameof(WavFileReader));
        if (dataRemaining <= 0) return 0;
        int toRead = (int)Math.Min(count, dataRemaining);
        int read = stream.Read(buffer, offset, toRead);
        dataRemaining -= read;
        return read;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ownsStream) stream.Dispose();
    }
}
