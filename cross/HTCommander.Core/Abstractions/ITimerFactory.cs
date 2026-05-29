namespace HTCommander.Core.Abstractions;

/// <summary>
/// Creates <see cref="IPlatformTimer"/> instances. Replaces direct use of System.Timers.Timer.
/// </summary>
public interface ITimerFactory
{
    /// <summary>Creates a timer with the given interval (in milliseconds) and auto-reset behavior.</summary>
    IPlatformTimer Create(double intervalMs, bool autoReset = true);
}
