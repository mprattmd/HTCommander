namespace HTCommander.Core.Abstractions;

/// <summary>
/// Carries a buffer of captured audio samples.
/// </summary>
public class AudioDataEventArgs : EventArgs
{
    /// <summary>The buffer containing audio samples.</summary>
    public byte[] Buffer { get; }

    /// <summary>The number of valid bytes recorded in <see cref="Buffer"/>.</summary>
    public int BytesRecorded { get; }

    public AudioDataEventArgs(byte[] buffer, int bytesRecorded)
    {
        Buffer = buffer;
        BytesRecorded = bytesRecorded;
    }
}
