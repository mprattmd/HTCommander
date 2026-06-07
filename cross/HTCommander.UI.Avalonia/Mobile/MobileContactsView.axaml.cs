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
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HTCommander;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileContactsView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public MobileContactsView()
    {
        InitializeComponent();
        NewButton.Click += (_, _) => NewContact();
        ContactsList.AddHandler(Button.ClickEvent, OnRowClick);
    }

    private void OnRowClick(object? sender, RoutedEventArgs e)
    {
        // Find the StationInfoClass behind the tapped row.
        for (var v = e.Source as Visual; v != null; v = v.GetVisualParent())
            if (v is StyledElement se && se.DataContext is StationInfoClass sc)
            {
                if (Vm != null) Vm.SelectedContact = sc;   // populates the Edit* fields
                this.FindAncestorOfType<MobileView>()?.Push(new MobileContactDetailView(), sc.Callsign ?? "Contact");
                return;
            }
    }

    private void NewContact()
    {
        if (Vm == null) return;
        Vm.SelectedContact = null;
        Vm.EditCallsign = ""; Vm.EditName = ""; Vm.EditDescription = "";
        Vm.EditChannel = ""; Vm.EditAprsRoute = ""; Vm.EditAx25Destination = "";
        Vm.EditAuthPassword = ""; Vm.EditWaitForConnection = false;
        this.FindAncestorOfType<MobileView>()?.Push(new MobileContactDetailView(), "New contact");
    }
}
