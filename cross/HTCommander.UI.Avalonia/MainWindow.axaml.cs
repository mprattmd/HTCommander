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
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using HTCommander;
using HTCommander.UI.Avalonia.ViewModels;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;

namespace HTCommander.UI.Avalonia;

public partial class MainWindow : Window
{
    private MemoryLayer? stationLayer;
    private bool mapCentered;
    private MainViewModel? subscribedVm;

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

        // PTT is press-and-hold (fail-safe): transmit only while held; any release
        // or loss of pointer capture un-keys the radio.
        WirePtt(PttButton);
        WirePtt(PttButton2);   // PTT on the Voice tab too

        AddContactButton.Click += (_, _) => Vm?.AddOrUpdateContact();
        RemoveContactButton.Click += (_, _) => Vm?.RemoveSelectedContact();
        SendTerminalButton.Click += (_, _) => Vm?.SendTerminalMessage();

        WireChannelBuilder();

        // Mail (Winlink)
        MailSyncButton.Click += (_, _) => Vm?.SyncWinlinkInternet();
        MailComposeButton.Click += (_, _) => Vm?.ComposeSaveToOutbox();
        MailDeleteButton.Click += (_, _) => Vm?.DeleteSelectedMail();
        MailDisconnectButton.Click += (_, _) => Vm?.DisconnectWinlink();

        // BBS
        BbsToggleButton.Click += (_, _) => Vm?.ToggleBbs();
        BbsClearStatsButton.Click += (_, _) => Vm?.ClearBbsStats();

        // Screenshot (button + F12)
        ScreenshotButton.Click += (_, _) => SaveScreenshot();

        // F12 saves a PNG of the whole window (compositor-independent) — handy for
        // docs/screenshots. Written to ~/htcommander-screenshot.png.
        AddHandler(KeyDownEvent, OnGlobalKeyDown, RoutingStrategies.Tunnel);

        InitMap();

        // The VM is assigned as DataContext after construction; sync to it then.
        DataContextChanged += (_, _) => HookViewModel();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // PTT is press-and-hold (fail-safe): transmit only while held; release or any
    // loss of pointer capture un-keys the radio.
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12) SaveScreenshot();
    }

    // Saves a PNG of the whole window to ~/htcommander-screenshot.png. Triggered by the
    // 📷 button in the status bar or F12 (compositor-independent — Wayland/ChromeOS block
    // external screenshot tools).
    private void SaveScreenshot()
    {
        try
        {
            double scale = RenderScaling;
            var size = new global::Avalonia.PixelSize(
                System.Math.Max(1, (int)(Bounds.Width * scale)),
                System.Math.Max(1, (int)(Bounds.Height * scale)));
            using var rtb = new global::Avalonia.Media.Imaging.RenderTargetBitmap(size, new global::Avalonia.Vector(96 * scale, 96 * scale));
            rtb.Render(this);
            string path = System.IO.Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                "htcommander-screenshot.png");
            rtb.Save(path);
            Vm?.NoteScreenshot(path);
        }
        catch (Exception ex) { Vm?.NoteScreenshot("(failed: " + ex.Message + ")"); }
    }

    private void WirePtt(Button button)
    {
        button.AddHandler(PointerPressedEvent, (_, _) => Vm?.StartTransmit(), RoutingStrategies.Tunnel);
        button.AddHandler(PointerReleasedEvent, (_, _) => Vm?.StopTransmit(), RoutingStrategies.Tunnel);
        button.PointerCaptureLost += (_, _) => Vm?.StopTransmit();
    }

    // ---- Channel builder: button actions, file pickers, CSV drag-and-drop ----
    private void WireChannelBuilder()
    {
        ChImportButton.Click += async (_, _) => await ImportChannelsAsync();
        ChExportButton.Click += async (_, _) => await ExportChannelsAsync();
        ChLoadRadioButton.Click += (_, _) => Vm?.LoadChannelsFromRadio();
        ChAddRowButton.Click += (_, _) => Vm?.AddBuilderChannel();
        ChRemoveRowButton.Click += (_, _) => Vm?.RemoveBuilderChannel(ChannelGrid.SelectedItem as EditableChannel);
        ChWriteButton.Click += (_, _) => Vm?.WriteChannelsToRadio();

        // Drag a .csv file onto the builder to import it (matches the Windows builder).
        ChannelBuilderRoot.AddHandler(DragDrop.DragOverEvent, OnChannelDragOver);
        ChannelBuilderRoot.AddHandler(DragDrop.DropEvent, OnChannelDrop);
        DragDrop.SetAllowDrop(ChannelBuilderRoot, true);

        // In-app drag: pick up an imported channel card and drop it on a memory slot to
        // program it (single-window manual drag — version-independent of the OS DnD API).
        ImportedCards.AddHandler(PointerPressedEvent, OnImportedPointerPressed, RoutingStrategies.Tunnel);
        ImportedCards.AddHandler(PointerReleasedEvent, OnImportedPointerReleased, RoutingStrategies.Tunnel);
    }

    private EditableChannel? _dragChannel;

    private static T? AncestorDataContext<T>(object? source) where T : class
    {
        for (var v = source as global::Avalonia.Visual; v != null; v = v.GetVisualParent())
            if (v is global::Avalonia.StyledElement se && se.DataContext is T t) return t;
        return null;
    }

    private void OnImportedPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ImportedCards).Properties.IsLeftButtonPressed) return;
        _dragChannel = AncestorDataContext<EditableChannel>(e.Source);
        if (_dragChannel != null)
        {
            e.Pointer.Capture(ImportedCards);   // so we get the release even over the slot grid
            if (Vm != null) Vm.BuilderStatus = $"Dragging '{_dragChannel.Name}' — drop it on a memory slot.";
        }
    }

    private void OnImportedPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var ch = _dragChannel;
        _dragChannel = null;
        e.Pointer.Capture(null);
        if (ch == null) return;

        // Hit-test the slot grid at the release point.
        var pos = e.GetPosition(SlotCards);
        var hit = SlotCards.InputHitTest(pos);
        var slot = AncestorDataContext<ChannelSlot>(hit);
        if (slot != null) Vm?.ProgramSlot(slot.SlotId, ch);
        else if (Vm != null) Vm.BuilderStatus = "Dropped outside the memory grid — nothing programmed.";
    }

    // Avalonia 12 drag-drop uses the IDataTransfer model (e.DataTransfer + TryGetFiles).
    private static string? DragCsvPath(DragEventArgs e)
    {
        var files = e.DataTransfer?.TryGetFiles();
        if (files == null) return null;
        foreach (var f in files)
        {
            var p = f.TryGetLocalPath();
            if (p != null && p.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    private void OnChannelDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DragCsvPath(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnChannelDrop(object? sender, DragEventArgs e)
    {
        var path = DragCsvPath(e);
        if (path != null) Vm?.ImportChannelsFromCsv(path);
    }

    private async Task ImportChannelsAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import channels (CSV: CHIRP / RepeaterBook / native)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("CSV files") { Patterns = new[] { "*.csv" } } },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path != null) Vm?.ImportChannelsFromCsv(path);
    }

    private async Task ExportChannelsAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export channels (native CSV)",
            DefaultExtension = "csv",
            SuggestedFileName = "channels.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV files") { Patterns = new[] { "*.csv" } } },
        });
        var path = file?.TryGetLocalPath();
        if (path != null) Vm?.ExportChannelsToCsv(path, chirp: false);
    }

    private void InitMap()
    {
        var map = new Map();
        map.Layers.Add(OpenStreetMap.CreateTileLayer());
        stationLayer = new MemoryLayer("APRS Stations")
        {
            Features = Array.Empty<IFeature>()
        };
        map.Layers.Add(stationLayer);
        MapControl.Map = map;
    }

    private void HookViewModel()
    {
        if (subscribedVm != null) subscribedVm.Stations.CollectionChanged -= OnStationsChanged;
        subscribedVm = Vm;
        if (subscribedVm != null) subscribedVm.Stations.CollectionChanged += OnStationsChanged;
        RebuildStations();
    }

    private void OnStationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildStations();

    // Rebuild the marker layer from the VM's stations (runs on the UI thread —
    // DataBroker marshals the station updates that drive the collection).
    private void RebuildStations()
    {
        if (stationLayer == null || Vm == null) return;

        var features = new List<IFeature>(Vm.Stations.Count);
        MPoint? first = null;
        foreach (var s in Vm.Stations)
        {
            var (x, y) = SphericalMercator.FromLonLat(s.Longitude, s.Latitude);
            var point = new MPoint(x, y);
            first ??= point;
            var f = new PointFeature(point);
            f.Styles.Add(new SymbolStyle { SymbolScale = 0.8, Fill = new Brush(new Color(220, 40, 40, 255)) });
            f.Styles.Add(new LabelStyle
            {
                Text = s.Callsign,
                ForeColor = new Color(20, 20, 20, 255),
                BackColor = new Brush(new Color(255, 255, 255, 200)),
                Offset = new Offset(0, -18)
            });
            features.Add(f);
        }

        stationLayer.Features = features;
        stationLayer.DataHasChanged();
        MapControl.RefreshGraphics();

        // Center once on the first station we hear.
        if (!mapCentered && first != null)
        {
            try { MapControl.Map.Navigator.CenterOnAndZoomTo(first, 150); mapCentered = true; }
            catch (Exception) { }
        }
    }
}
