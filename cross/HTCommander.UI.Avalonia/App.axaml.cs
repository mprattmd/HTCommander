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
using HTCommander.Platform.Linux;
using HTCommander.Platform.Linux.Audio;
using HTCommander.UI.Avalonia.Platform;
using HTCommander.UI.Avalonia.ViewModels;

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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root: pick the platform backends and wire the shared
            // DataBroker, then hand the view model to the main window.
            IUiDispatcher dispatcher = new AvaloniaUiDispatcher();
            IConfigStore configStore = new JsonConfigStore("HTCommander");
            DataBroker.Initialize(configStore, dispatcher);
            IAudioDeviceEnumerator audioDevices = new PortAudioDeviceEnumerator();

            // Data services keyed in the DataBroker, shared with the WinForms app's
            // contracts: the Winlink mail store (SQLite) and the connected-mode BBS
            // manager (listens for the "CreateBbs"/"RemoveBbs" events from the UI).
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            string dataDir = Path.Combine(baseDir, "HTCommander");

            DataBroker.AddDataHandler("MailStore", new SqliteMailStore(dataDir));
            DataBroker.AddDataHandler("BbsHandler", new BbsHandler());

            // Winlink B2F client: listens for "WinlinkSync"/"WinlinkDisconnect" and
            // drives CMS sessions (over the internet or via the radio). Held alive by
            // its DataBroker subscriptions; kept here too to be explicit.
            winlinkClient = new WinlinkClient();

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(dispatcher, audioDevices)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
