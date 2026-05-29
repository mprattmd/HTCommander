namespace HTCommander.Core.Abstractions;

/// <summary>
/// Describes a PCM audio wave format.
/// </summary>
public interface IWaveFormat
{
    /// <summary>Samples per second (Hz).</summary>
    int SampleRate { get; }

    /// <summary>Number of channels (1 = mono, 2 = stereo).</summary>
    int Channels { get; }

    /// <summary>Bits per sample.</summary>
    int BitsPerSample { get; }

    /// <summary>Bytes per sample frame across all channels.</summary>
    int BlockAlign { get; }

    /// <summary>Average bytes per second.</summary>
    int AverageBytesPerSecond { get; }
}
