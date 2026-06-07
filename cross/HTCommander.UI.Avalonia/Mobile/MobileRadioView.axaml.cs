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

using System.ComponentModel;
using Avalonia.Controls;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileRadioView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    private MainViewModel? hooked;

    public MobileRadioView()
    {
        InitializeComponent();
        RefreshButton.Click    += (_, _) => Vm?.Refresh();
        ConnectButton.Click    += (_, _) => Vm?.Connect();
        DisconnectButton.Click += (_, _) => Vm?.Disconnect();
        SegPacket.Click  += (_, _) => { if (Vm != null) Vm.RadioMode = "Packet"; };
        SegDigital.Click += (_, _) => { if (Vm != null) Vm.RadioMode = "Digital"; };
        DataContextChanged += (_, _) => { Hook(); UpdateSeg(); };
    }

    private void Hook()
    {
        if (hooked != null) hooked.PropertyChanged -= OnVmChanged;
        hooked = Vm;
        if (hooked != null) hooked.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.RadioMode) or null) UpdateSeg();
    }

    private void UpdateSeg()
    {
        bool packet = Vm?.IsPacketMode ?? true;
        SetOn(SegPacket, packet);
        SetOn(SegDigital, Vm?.IsDigitalMode ?? false);
    }

    private static void SetOn(Control c, bool on)
    {
        if (on) { if (!c.Classes.Contains("on")) c.Classes.Add("on"); }
        else c.Classes.Remove("on");
    }
}
