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

using System;
using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HTCommander;
using HTCommander.Core.Abstractions;
using HTCommander.Core.Abstractions.Audio;
using HTCommander.UI.Avalonia.Platform;
using HTCommander.UI.Avalonia.ViewModels;
#if !ANDROID
using HTCommander.Platform.Linux;
using HTCommander.Platform.Linux.Audio;
#endif
#if ANDROID
using HTCommander.Platform.Android;
#endif

namespace HTCommander.UI.Avalonia;

public partial class App : Application
{
    private WinlinkClient? winlinkClient;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Composition root: pick the platform backends via the seams, wire the shared
        // DataBroker + portable handlers, then host the shared MainView — a Window on
        // desktop, the single-view lifetime on Android.
        IUiDispatcher dispatcher = new AvaloniaUiDispatcher();

#if ANDROID
        // Android backends (RFCOMM over Android.Bluetooth; file config; no audio in v1).
        IConfigStore configStore = new AndroidConfigStore();
        IAudioDeviceEnumerator audioDevices = new AndroidAudioDeviceEnumerator();
        IRadioPlatform radioPlatform = new AndroidRadioPlatform();
#else
        // Desktop: JSON config + PortAudio; radio transport is macOS IOBluetooth or Linux BlueZ.
        IConfigStore configStore = new JsonConfigStore("HTCommander");
        IAudioDeviceEnumerator audioDevices = new PortAudioDeviceEnumerator();
        IRadioPlatform radioPlatform = OperatingSystem.IsMacOS()
            ? new HTCommander.Platform.Mac.MacRadioPlatform()
            : new LinuxRadioPlatform();
#endif

        DataBroker.Initialize(configStore, dispatcher);

        // Portable data handlers (Core) — shared by every platform.
        DataBroker.AddDataHandler("BbsHandler", new BbsHandler());
        DataBroker.AddDataHandler("AprsHandler", new AprsHandler());
        DataBroker.AddDataHandler("SoftwareModem", new SoftwareModem());
        // Winlink B2F client (CMS over internet/radio); held alive by its subscriptions.
        winlinkClient = new WinlinkClient();

#if !ANDROID
        // Desktop-only services: SQLite mail store + serial GPS. Both are deferred on
        // Android round one (mail persistence and serial GPS have no Android backend yet).
        string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        DataBroker.AddDataHandler("MailStore", new SqliteMailStore(Path.Combine(baseDir, "HTCommander")));
        DataBroker.AddDataHandler("GpsSerialHandler", new HTCommander.Gps.GpsSerialHandler());
#endif

        var viewModel = new MainViewModel(dispatcher, audioDevices, radioPlatform);

#if ANDROID
        viewModel.RadioMode = "Packet";   // mobile is data-only (Packet); no Voice/Digital switch
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = new Mobile.MobileView { DataContext = viewModel };
#else
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
#endif

        base.OnFrameworkInitializationCompleted();
    }
}
