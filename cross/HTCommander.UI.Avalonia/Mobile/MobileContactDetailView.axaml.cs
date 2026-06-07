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
using Avalonia.VisualTree;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileContactDetailView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public MobileContactDetailView()
    {
        InitializeComponent();
        SaveButton.Click += (_, _) => { Vm?.AddOrUpdateContact(); Close(); };
        RemoveButton.Click += (_, _) => { Vm?.RemoveSelectedContact(); Close(); };
    }

    private void Close() => this.FindAncestorOfType<MobileView>()?.Back();
}
