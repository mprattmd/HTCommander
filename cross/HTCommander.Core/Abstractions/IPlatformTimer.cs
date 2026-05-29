namespace HTCommander.Core.Abstractions;

/// <summary>
/// An injectable timer abstraction. Replaces System.Timers.Timer.
/// </summary>
public interface IPlatformTimer : IDisposable
{
    /// <summary>The interval, in milliseconds, between <see cref="Elapsed"/> events.</summary>
    double IntervalMs { get; set; }

    /// <summary>Gets or sets whether the timer is running.</summary>
    bool Enabled { get; set; }

    /// <summary>Raised each time the interval elapses.</summary>
    event EventHandler? Elapsed;

    /// <summary>Starts the timer.</summary>
    void Start();

    /// <summary>Stops the timer.</summary>
    void Stop();
}
