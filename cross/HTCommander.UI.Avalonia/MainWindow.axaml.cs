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
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        InitMap();

        // The VM is assigned as DataContext after construction; sync to it then.
        DataContextChanged += (_, _) => HookViewModel();
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // PTT is press-and-hold (fail-safe): transmit only while held; release or any
    // loss of pointer capture un-keys the radio.
    private void WirePtt(Button button)
    {
        button.AddHandler(PointerPressedEvent, (_, _) => Vm?.StartTransmit(), RoutingStrategies.Tunnel);
        button.AddHandler(PointerReleasedEvent, (_, _) => Vm?.StopTransmit(), RoutingStrategies.Tunnel);
        button.PointerCaptureLost += (_, _) => Vm?.StopTransmit();
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
