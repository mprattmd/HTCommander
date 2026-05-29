namespace HTCommander.Core.Abstractions;

/// <summary>
/// Simple logging / debug output abstraction. Replaces MainForm-based debug calls.
/// </summary>
public interface ILogger
{
    /// <summary>Logs a debug-level message.</summary>
    void Debug(string message);

    /// <summary>Logs an informational message.</summary>
    void Info(string message);

    /// <summary>Logs a warning message.</summary>
    void Warn(string message);

    /// <summary>Logs an error message, optionally with an associated exception.</summary>
    void Error(string message, Exception? ex = null);
}
