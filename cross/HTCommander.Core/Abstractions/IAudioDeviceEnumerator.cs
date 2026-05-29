namespace HTCommander.Core.Abstractions;

/// <summary>
/// Enumerates available audio devices.
/// </summary>
public interface IAudioDeviceEnumerator
{
    /// <summary>Returns the available input (capture) devices.</summary>
    IReadOnlyList<IAudioDevice> GetInputDevices();

    /// <summary>Returns the available output (playback) devices.</summary>
    IReadOnlyList<IAudioDevice> GetOutputDevices();

    /// <summary>The default input device, or null if none is available.</summary>
    IAudioDevice? DefaultInput { get; }

    /// <summary>The default output device, or null if none is available.</summary>
    IAudioDevice? DefaultOutput { get; }
}
