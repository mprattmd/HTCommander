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

namespace HTCommander.UI.Avalonia;

/// <summary>
/// Desktop window host. All UI and behavior live in <see cref="MainView"/> so the
/// same control can be hosted by the Android single-view lifetime (which has no
/// Window). This window only provides the desktop frame (title, icon, size).
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
