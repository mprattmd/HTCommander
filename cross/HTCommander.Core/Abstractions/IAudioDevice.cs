namespace HTCommander.Core.Abstractions;

/// <summary>
/// Describes an audio input or output device.
/// </summary>
public interface IAudioDevice
{
    /// <summary>The platform-specific device identifier.</summary>
    string Id { get; }

    /// <summary>The human-readable device name.</summary>
    string Name { get; }

    /// <summary>True if this is an input (capture) device; false for output (playback).</summary>
    bool IsInput { get; }

    /// <summary>True if this is the system default device for its direction.</summary>
    bool IsDefault { get; }
}
