namespace HTCommander.UI.Avalonia;

/// <summary>
/// Compile-time feature toggles.
///
/// <para><b>RepeaterBook:</b> its UI entry points — the channel-builder
/// "Search RepeaterBook…" button and the Settings token section, on both the desktop
/// and mobile heads — are hidden in <b>public release builds</b> until the live token
/// flow is finished. The feature code stays fully compiled in; only the entry points
/// are gated.</para>
///
/// <para>Hide it by building with <c>-p:RepeaterBookHidden=true</c> (which defines the
/// <c>RB_HIDDEN</c> symbol). Default/dev builds leave it visible so you can develop and
/// test the feature.</para>
/// </summary>
public static class FeatureFlags
{
#if RB_HIDDEN
    public const bool RepeaterBookEnabled = false;
#else
    public const bool RepeaterBookEnabled = true;
#endif
}
