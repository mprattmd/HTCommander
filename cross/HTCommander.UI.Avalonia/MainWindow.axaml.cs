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

using Avalonia.Controls;
using Avalonia.Interactivity;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Actions are wired in code-behind (the shell uses no command framework);
        // display state is data-bound to the MainViewModel.
        RefreshButton.Click += (_, _) => Vm?.Refresh();
        ConnectButton.Click += (_, _) => Vm?.Connect();
        DisconnectButton.Click += (_, _) => Vm?.Disconnect();
        TestToneButton.Click += (_, _) => Vm?.Settings.TestOutput();
        RefreshDevicesButton.Click += (_, _) => Vm?.Settings.RefreshDevices();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;
}
