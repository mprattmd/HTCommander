namespace HTCommander.Core.Abstractions;

/// <summary>
/// Plays back audio to an output device.
/// </summary>
public interface IAudioPlayback : IDisposable
{
    /// <summary>Initializes playback with the given wave format.</summary>
    void Init(IWaveFormat format);

    /// <summary>Starts playback.</summary>
    void Play();

    /// <summary>Stops playback.</summary>
    void Stop();

    /// <summary>Queues samples for playback.</summary>
    void AddSamples(byte[] buffer, int offset, int count);

    /// <summary>Gets or sets the playback volume (typically 0.0 to 1.0).</summary>
    float Volume { get; set; }
}
