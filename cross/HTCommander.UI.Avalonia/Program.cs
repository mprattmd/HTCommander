// Desktop entry point only. The Android head launches via MainActivity
// (AvaloniaMainActivity<App>) and the iOS head via AppDelegate
// (AvaloniaAppDelegate<App>), so this classic-desktop bootstrap is excluded on both.
#if !ANDROID && !IOS
using Avalonia;
using System;

namespace HTCommander.UI.Avalonia;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
#endif
