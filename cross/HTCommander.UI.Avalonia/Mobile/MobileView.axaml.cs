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

using System.Collections.Generic;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

/// <summary>
/// Phone shell: a bottom-nav app (Radio · APRS · Mail · Map · More) hosting one page
/// at a time, with a simple drill-down stack for detail pages (list → detail → back).
/// All pages reuse the shared <see cref="MainViewModel"/> (inherited DataContext).
/// </summary>
public partial class MobileView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    private MainViewModel? hooked;

    // Drill-down stack: each entry is the page + its title; the live page is in PageHost.
    private readonly Stack<(Control page, string title)> stack = new();
    private string rootKey = "Radio";

    private static readonly IBrush DotConnected = new SolidColorBrush(Color.Parse("#3a8f63"));
    private static readonly IBrush DotIdle = new SolidColorBrush(Color.Parse("#a99e84"));

    public MobileView()
    {
        InitializeComponent();

        NavRadio.Click += (_, _) => NavigateRoot("Radio");
        NavAprs.Click  += (_, _) => NavigateRoot("APRS");
        NavMail.Click  += (_, _) => NavigateRoot("Mail");
        NavMap.Click   += (_, _) => NavigateRoot("Map");
        NavChannels.Click += (_, _) => NavigateRoot("Channels");
        NavMore.Click  += (_, _) => MoreSheet.IsVisible = true;

        BackButton.Click += (_, _) => Back();

        // "More" tiles → navigate (and close the sheet)
        MoreStation.Click  += (_, _) => NavigateRoot("Station");
        MoreContacts.Click += (_, _) => NavigateRoot("Contacts");
        MoreSettings.Click += (_, _) => NavigateRoot("Settings");
        MorePackets.Click  += (_, _) => NavigateRoot("Packets");
        MoreAbout.Click    += (_, _) => NavigateRoot("About");
        // Tap the scrim (outside the sheet) to dismiss.
        MoreSheet.PointerPressed += (s, e) => { if (e.Source == MoreSheet) MoreSheet.IsVisible = false; };

        DataContextChanged += (_, _) => { Hook(); UpdateChip(); };
        NavigateRoot("Radio");
    }

    // ---- Navigation -------------------------------------------------------
    private void NavigateRoot(string key)
    {
        MoreSheet.IsVisible = false;
        rootKey = key;
        stack.Clear();
        SetContent(PageFor(key), TitleFor(key));
        UpdateNavHighlight(key);
    }

    /// <summary>Push a detail page (drill-down) with a Back button.</summary>
    public void Push(Control page, string title)
    {
        if (PageHost.Content is Control cur) stack.Push((cur, AppTitle.Text ?? ""));
        SetContent(page, title);
    }

    public void Back()
    {
        if (stack.Count == 0) return;
        var (page, title) = stack.Pop();
        SetContent(page, title);
    }

    private void SetContent(Control page, string title)
    {
        PageHost.Content = page;
        AppTitle.Text = title;
        BackButton.IsVisible = stack.Count > 0;
    }

    private Control PageFor(string key) => key switch
    {
        "Radio" => new MobileRadioView(),
        "APRS" => new MobileAprsView(),
        "Mail" => new MobileMailView(),
        "Contacts" => new MobileContactsView(),
        "Channels" => new MobileChannelsView(),
        "Station" => new MobileStationView(),
        "Settings" => new MobileSettingsView(),
        "Packets" => new MobilePacketsView(),
        "Map" => new MobileMapView(),
        "About" => new MobileAboutView(),
        _ => Placeholder(key),
    };

    private static string TitleFor(string key) => key;

    // Temporary stand-in for pages not built yet (APRS/Mail/Map/Station/…); replaced
    // as each screen lands.
    private Control Placeholder(string key) => new Border
    {
        Child = new TextBlock
        {
            Text = key + "\n\ncoming next",
            TextAlignment = global::Avalonia.Media.TextAlignment.Center,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            FontSize = 16,
            Foreground = DotIdle,
        }
    };

    private void UpdateNavHighlight(string key)
    {
        SetOn(NavRadio, key == "Radio");
        SetOn(NavAprs, key == "APRS");
        SetOn(NavMail, key == "Mail");
        SetOn(NavMap, key == "Map");
        SetOn(NavChannels, key == "Channels");
        // "More" destinations keep More highlighted.
        SetOn(NavMore, key is "Station" or "Contacts" or "Settings" or "Packets" or "About");
    }

    private static void SetOn(Control c, bool on)
    {
        if (on) { if (!c.Classes.Contains("on")) c.Classes.Add("on"); }
        else c.Classes.Remove("on");
    }

    // ---- Connection chip --------------------------------------------------
    private void Hook()
    {
        if (hooked != null) hooked.PropertyChanged -= OnVmChanged;
        hooked = Vm;
        if (hooked != null) hooked.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Connected) or nameof(MainViewModel.Status) or null)
            UpdateChip();
    }

    private void UpdateChip()
    {
        bool connected = Vm?.Connected ?? false;
        ConnDot.Fill = connected ? DotConnected : DotIdle;
        ConnText.Text = connected ? "UV-PRO" : "Disconnected";
    }
}
