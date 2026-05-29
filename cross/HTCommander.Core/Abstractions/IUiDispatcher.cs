namespace HTCommander.Core.Abstractions;

/// <summary>
/// Marshals callbacks onto the UI thread. Replaces WinForms Control.Invoke /
/// Avalonia Dispatcher.UIThread.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>True when the caller is not on the UI thread and marshaling is required.</summary>
    bool IsDispatchRequired { get; }

    /// <summary>Posts an action to the UI thread asynchronously (does not wait for completion).</summary>
    void Post(Action action);

    /// <summary>Invokes an action on the UI thread synchronously (waits for completion).</summary>
    void Invoke(Action action);
}
