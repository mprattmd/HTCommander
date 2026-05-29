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

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using HTCommander;
using HTCommander.Core.Abstractions;
using HTCommander.Platform.Linux;
using HTCommander.UI.Avalonia.Platform;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia;

public partial class App : Application
{
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

            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(dispatcher)
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
