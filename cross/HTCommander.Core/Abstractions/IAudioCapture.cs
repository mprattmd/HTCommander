namespace HTCommander.Core.Abstractions;

/// <summary>
/// Captures audio from an input device.
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>The wave format the capture produces.</summary>
    IWaveFormat Format { get; }

    /// <summary>Raised when a buffer of captured audio is available.</summary>
    event EventHandler<AudioDataEventArgs>? DataAvailable;

    /// <summary>Starts capturing audio.</summary>
    void Start();

    /// <summary>Stops capturing audio.</summary>
    void Stop();
}
