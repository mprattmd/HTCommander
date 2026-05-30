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
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using NetTopologySuite.Geometries;

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
        RefreshSerialButton.Click += (_, _) => Vm?.Settings.RefreshSerialPorts();

        // PTT is press-and-hold (fail-safe): transmit only while held; any release
        // or loss of pointer capture un-keys the radio.
        WirePtt(PttButton);
        WirePtt(PttButton2);   // PTT on the Voice tab too
        VoiceRxToggleButton.Click += (_, _) => Vm?.ToggleVoiceRx();   // voice audio is on-demand (off by default)

        AddContactButton.Click += (_, _) => Vm?.AddOrUpdateContact();
        RemoveContactButton.Click += (_, _) => Vm?.RemoveSelectedContact();
        SendTerminalButton.Click += (_, _) => Vm?.SendTerminal();
        SessionConnectButton.Click += (_, _) => Vm?.ConnectSession();
        SessionDisconnectButton.Click += (_, _) => Vm?.DisconnectSession();
        PacketExportButton.Click += async (_, _) => await ExportPacketsAsync();
        PacketLoadButton.Click += async (_, _) => await LoadPacketsAsync();

        WireChannelBuilder();

        // Mail (Winlink)
        MailSyncButton.Click += (_, _) => Vm?.SyncWinlinkInternet();
        MailSyncRadioButton.Click += (_, _) => Vm?.SyncWinlinkRadio();
        MailComposeButton.Click += (_, _) => Vm?.ComposeSaveToOutbox();
        MailDraftButton.Click += (_, _) => Vm?.SaveAsDraft();
        MailDeleteButton.Click += (_, _) => Vm?.DeleteSelectedMail();
        MailDisconnectButton.Click += (_, _) => Vm?.DisconnectWinlink();
        MailNewButton.Click += (_, _) => Vm?.NewMail();
        MailReplyButton.Click += (_, _) => Vm?.ReplyMail();
        MailReplyAllButton.Click += (_, _) => Vm?.ReplyAllMail();
        MailForwardButton.Click += (_, _) => Vm?.ForwardMail();
        MailMoveButton.Click += (_, _) => { if (Vm != null) Vm.MoveSelectedMailTo(Vm.MoveTarget); };
        AttachAddButton.Click += async (_, _) => await AddAttachmentAsync();
        AttachRemoveButton.Click += (_, _) => Vm?.RemoveComposeAttachment();
        AttachmentOpenButton.Click += (_, _) => Vm?.OpenSelectedAttachment();
        AttachmentSaveButton.Click += async (_, _) => await SaveAttachmentAsync();
        MailBackupButton.Click += async (_, _) => await BackupMailAsync();
        MailRestoreButton.Click += async (_, _) => await RestoreMailAsync();

        // BBS
        BbsToggleButton.Click += (_, _) => Vm?.ToggleBbs();
        BbsClearStatsButton.Click += (_, _) => Vm?.ClearBbsStats();

        // APRS message send + routes + beacon/ident + create-channel
        AprsSendButton.Click += (_, _) => Vm?.SendAprsMessage();
        CreateAprsChannelButton.Click += (_, _) => Vm?.CreateAprsChannel();
        RequestPositionButton.Click += (_, _) => Vm?.RequestPosition();
        SetFixedPositionButton.Click += (_, _) => Vm?.SetManualPosition();
        CenterGpsButton.Click += (_, _) => CenterOnGps();
        AprsFiFetchButton.Click += (_, _) => Vm?.FetchAprsFi();
        WriteBssButton.Click += (_, _) => Vm?.WriteBssSettings();
        BeaconNowButton.Click += (_, _) => Vm?.BeaconNow();
        AddRouteButton.Click += (_, _) => Vm?.AddOrUpdateRoute();
        RemoveRouteButton.Click += (_, _) => Vm?.RemoveSelectedRoute();

        // Audio clips
        ClipRecordButton.Click += (_, _) => Vm?.ToggleRecordClip();
        ClipPlayButton.Click += (_, _) => Vm?.PlaySelectedClip();
        ClipStopButton.Click += (_, _) => Vm?.StopClipPlayback();
        ClipRenameButton.Click += (_, _) => Vm?.RenameSelectedClip();
        ClipDeleteButton.Click += (_, _) => Vm?.DeleteSelectedClip();
        VoiceModePlayButton.Click += (_, _) => Vm?.PlayVoiceMode();

        // About dialog
        AboutButton.Click += (_, _) => new AboutWindow().ShowDialog(this);

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
        ChLoadAllBanksButton.Click += (_, _) => Vm?.LoadAllBanks();
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

    // ---- Packet capture: CSV export / load ----
    private async Task ExportPacketsAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export packet capture (CSV)",
            DefaultExtension = "csv",
            SuggestedFileName = "packets.csv",
            FileTypeChoices = new[] { new FilePickerFileType("CSV files") { Patterns = new[] { "*.csv" } } },
        });
        var path = file?.TryGetLocalPath();
        if (path != null) Vm?.ExportPacketsCsv(path);
    }

    private async Task LoadPacketsAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load packet capture (CSV)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("CSV files") { Patterns = new[] { "*.csv" } } },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path != null) Vm?.LoadPacketsCsv(path);
    }

    // ---- Mail (Winlink): attachment + backup/restore file pickers ----
    private async Task AddAttachmentAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach a file",
            AllowMultiple = true,
        });
        foreach (var f in files)
        {
            var p = f.TryGetLocalPath();
            if (p != null) Vm?.AddComposeAttachment(p);
        }
    }

    private async Task SaveAttachmentAsync()
    {
        var att = Vm?.SelectedAttachment;
        if (att == null) return;
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save attachment",
            SuggestedFileName = att.Name,
        });
        var path = file?.TryGetLocalPath();
        if (path != null) Vm?.SaveAttachmentTo(path);
    }

    private async Task BackupMailAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Back up mail",
            DefaultExtension = "gz",
            SuggestedFileName = "htcommander-mail-backup.txt.gz",
            FileTypeChoices = new[] { new FilePickerFileType("Gzip backup") { Patterns = new[] { "*.gz" } } },
        });
        var path = file?.TryGetLocalPath();
        if (path != null) Vm?.BackupMail(path);
    }

    private async Task RestoreMailAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore mail from backup",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Gzip backup") { Patterns = new[] { "*.gz" } } },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path != null) Vm?.RestoreMail(path);
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
        if (subscribedVm != null)
        {
            subscribedVm.Stations.CollectionChanged -= OnStationsChanged;
            subscribedVm.InternetStations.CollectionChanged -= OnStationsChanged;
            subscribedVm.PropertyChanged -= OnVmPropertyChanged;
            subscribedVm.WaterfallPcm -= OnWaterfallPcm;
        }
        subscribedVm = Vm;
        if (subscribedVm != null)
        {
            subscribedVm.Stations.CollectionChanged += OnStationsChanged;
            subscribedVm.InternetStations.CollectionChanged += OnStationsChanged;
            subscribedVm.PropertyChanged += OnVmPropertyChanged;
            subscribedVm.WaterfallPcm += OnWaterfallPcm;
        }
        RebuildStations();
    }

    private void OnStationsChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildStations();

    // RX audio (off the decode thread) → waterfall control (it marshals its own render).
    private void OnWaterfallPcm(byte[] pcm, int count) => Waterfall.PushPcm(pcm, count);

    // Map appearance toggles (tracks/markers/time filter) and the radio's own
    // position fix all trigger a map rebuild.
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.ShowTracks):
            case nameof(MainViewModel.LargeMarkers):
            case nameof(MainViewModel.TrackMinutes):
            case nameof(MainViewModel.MyPosition):
            case nameof(MainViewModel.SerialPosition):
                RebuildStations();
                break;
        }
    }

    private static MPoint Merc(double lon, double lat) { var (x, y) = SphericalMercator.FromLonLat(lon, lat); return new MPoint(x, y); }

    // Rebuild the map features from the VM: per-callsign track polylines (time-filtered),
    // station markers, and the radio's own GPS position. Runs on the UI thread.
    private void RebuildStations()
    {
        if (stationLayer == null || Vm == null) return;

        var features = new List<IFeature>();
        MPoint? first = null;
        double markerScale = Vm.LargeMarkers ? 1.3 : 0.8;

        foreach (var s in Vm.Stations)
        {
            // Time-filtered track for this callsign.
            List<TrackPoint>? track = Vm.Tracks.TryGetValue(s.Callsign, out var t) ? t : null;
            var pts = track == null ? new List<TrackPoint>() : track.Where(p => Vm.WithinTimeFilter(p.Time)).ToList();

            // Skip the station entirely if a time filter is active and it has no recent fix.
            if (Vm.TrackMinutes > 0 && pts.Count == 0) continue;

            if (Vm.ShowTracks && pts.Count >= 2)
            {
                var coords = pts.Select(p => { var m = Merc(p.Longitude, p.Latitude); return new Coordinate(m.X, m.Y); }).ToArray();
                var line = new GeometryFeature(new LineString(coords));
                line.Styles.Add(new VectorStyle { Line = new Pen(new Color(40, 120, 220, 200), 2) });
                features.Add(line);
            }

            var point = Merc(s.Longitude, s.Latitude);
            first ??= point;
            var f = new PointFeature(point);
            f.Styles.Add(new SymbolStyle { SymbolScale = markerScale, Fill = new Brush(new Color(220, 40, 40, 255)) });
            f.Styles.Add(new LabelStyle
            {
                Text = s.Callsign,
                ForeColor = new Color(20, 20, 20, 255),
                BackColor = new Brush(new Color(255, 255, 255, 200)),
                Offset = new Offset(0, -18)
            });
            features.Add(f);
        }

        // The radio's own GPS position (distinct blue marker).
        if (Vm.MyPosition is { Locked: true } mp)
        {
            var me = new PointFeature(Merc(mp.Longitude, mp.Latitude));
            me.Styles.Add(new SymbolStyle { SymbolScale = markerScale * 1.2, Fill = new Brush(new Color(40, 120, 220, 255)) });
            me.Styles.Add(new LabelStyle
            {
                Text = "GPS",
                ForeColor = new Color(255, 255, 255, 255),
                BackColor = new Brush(new Color(40, 120, 220, 220)),
                Offset = new Offset(0, -18)
            });
            features.Add(me);
        }

        // Serial-GPS fix (distinct green marker).
        if (Vm.SerialPosition is { IsFixed: true } sp)
        {
            var f = new PointFeature(Merc(sp.Longitude, sp.Latitude));
            f.Styles.Add(new SymbolStyle { SymbolScale = markerScale * 1.2, Fill = new Brush(new Color(40, 170, 80, 255)) });
            f.Styles.Add(new LabelStyle
            {
                Text = "GPS(ser)",
                ForeColor = new Color(255, 255, 255, 255),
                BackColor = new Brush(new Color(40, 170, 80, 220)),
                Offset = new Offset(0, -18)
            });
            features.Add(f);
        }

        // Internet (aprs.fi) stations — distinct orange markers, no tracks.
        foreach (var s in Vm.InternetStations)
        {
            var f = new PointFeature(Merc(s.Longitude, s.Latitude));
            f.Styles.Add(new SymbolStyle { SymbolScale = markerScale, Fill = new Brush(new Color(240, 150, 30, 255)) });
            f.Styles.Add(new LabelStyle
            {
                Text = s.Callsign,
                ForeColor = new Color(20, 20, 20, 255),
                BackColor = new Brush(new Color(250, 210, 150, 220)),
                Offset = new Offset(0, -18)
            });
            features.Add(f);
            first ??= Merc(s.Longitude, s.Latitude);
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

    // Center the map on the radio's GPS position (or the serial-GPS fix if there's no radio fix).
    private void CenterOnGps()
    {
        MPoint? p = null;
        if (Vm?.MyPosition is { Locked: true } mp) p = Merc(mp.Longitude, mp.Latitude);
        else if (Vm?.SerialPosition is { IsFixed: true } sp) p = Merc(sp.Longitude, sp.Latitude);
        if (p == null) return;
        try { MapControl.Map.Navigator.CenterOnAndZoomTo(p, 50); mapCentered = true; }
        catch (Exception) { }
    }
}
