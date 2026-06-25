/*
Copyright 2026 Ylian Saint-Hilaire

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

#if IOS
using Avalonia;
using Avalonia.iOS;
using Foundation;
using UIKit;

namespace HTCommander.UI.Avalonia;

/// <summary>
/// iOS head. Avalonia drives the single-view lifetime through this AppDelegate (the
/// counterpart of the Android <c>HtcAndroidApplication</c>); the shared <see cref="App"/>
/// composition root then selects the iOS backends (Core Bluetooth BLE transport, etc.).
/// </summary>
[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder).WithInterFont();
}

/// <summary>UIKit entry point — hands control to the Avalonia AppDelegate above.
/// Named IosProgram (not "Application") so it doesn't shadow Avalonia.Application,
/// which the shared <see cref="App"/> derives from.</summary>
public static class IosProgram
{
    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
}
#endif
