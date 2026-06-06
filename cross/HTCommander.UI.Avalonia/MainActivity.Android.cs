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

#if ANDROID
using System;
using Android.App;
using Android.Content.PM;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace HTCommander.UI.Avalonia;

/// <summary>
/// Android Application — Avalonia 12 configures the app here (via the generic
/// <see cref="AvaloniaAndroidApplication{TApp}"/>) rather than on the activity.
/// This is where the shared <see cref="App"/> and AppBuilder customizations live.
/// </summary>
[Application(Label = "HTCommander")]
public sealed class HtcAndroidApplication : AvaloniaAndroidApplication<App>
{
    public HtcAndroidApplication(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer) { }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder).WithInterFont();
}

/// <summary>
/// Launcher activity. A NoActionBar platform theme avoids the action bar overlaying
/// content (the edge-to-edge gotcha hit during the RFCOMM spike). Avalonia drives the
/// single-view lifetime from the Application above.
/// </summary>
[Activity(
    Label = "HTCommander",
    Theme = "@android:style/Theme.Material.Light.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity
{
}
#endif
