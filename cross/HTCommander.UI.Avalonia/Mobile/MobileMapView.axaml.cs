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
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using HTCommander.UI.Avalonia.ViewModels;
using Mapsui;
using Mapsui.Layers;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileMapView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    private MainViewModel? hooked;
    private MemoryLayer? stationLayer;
    private bool centered;

    public MobileMapView()
    {
        InitializeComponent();
        var map = new Map();
        map.Layers.Add(OpenStreetMap.CreateTileLayer());
        stationLayer = new MemoryLayer("APRS Stations") { Features = System.Array.Empty<IFeature>() };
        map.Layers.Add(stationLayer);
        MapControl.Map = map;

        CenterGpsButton.Click += (_, _) => CenterOnGps();
        LookUpButton.Click += (_, _) => Vm?.FetchAprsFi();
        DataContextChanged += (_, _) => { Hook(); Rebuild(); };
    }

    private void Hook()
    {
        if (hooked != null)
        {
            hooked.Stations.CollectionChanged -= OnChanged;
            hooked.InternetStations.CollectionChanged -= OnChanged;
            hooked.PropertyChanged -= OnVmChanged;
        }
        hooked = Vm;
        if (hooked != null)
        {
            hooked.Stations.CollectionChanged += OnChanged;
            hooked.InternetStations.CollectionChanged += OnChanged;
            hooked.PropertyChanged += OnVmChanged;
        }
    }

    private void OnChanged(object? s, NotifyCollectionChangedEventArgs e) => Rebuild();
    private void OnVmChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.MyPosition)) Rebuild();
    }

    private static MPoint Merc(double lon, double lat) { var (x, y) = SphericalMercator.FromLonLat(lon, lat); return new MPoint(x, y); }

    private static IFeature Marker(MPoint p, string label, Color fill, Color labelBg)
    {
        var f = new PointFeature(p);
        f.Styles.Add(new SymbolStyle { SymbolScale = 0.9, Fill = new Brush(fill) });
        f.Styles.Add(new LabelStyle { Text = label, ForeColor = new Color(20, 20, 20), BackColor = new Brush(labelBg), Offset = new Offset(0, -18) });
        return f;
    }

    private void Rebuild()
    {
        if (stationLayer == null || Vm == null) return;
        var features = new List<IFeature>();
        MPoint? first = null;

        foreach (var s in Vm.Stations)
        {
            var p = Merc(s.Longitude, s.Latitude);
            first ??= p;
            features.Add(Marker(p, s.Callsign, new Color(220, 40, 40), new Color(255, 255, 255, 200)));
        }
        foreach (var s in Vm.InternetStations)
        {
            var p = Merc(s.Longitude, s.Latitude);
            first ??= p;
            features.Add(Marker(p, s.Callsign, new Color(240, 150, 30), new Color(250, 210, 150, 220)));
        }
        if (Vm.MyPosition is { Locked: true } mp)
        {
            var p = Merc(mp.Longitude, mp.Latitude);
            first ??= p;
            var me = new PointFeature(p);
            me.Styles.Add(new SymbolStyle { SymbolScale = 1.1, Fill = new Brush(new Color(40, 120, 220)) });
            me.Styles.Add(new LabelStyle { Text = "GPS", ForeColor = new Color(255, 255, 255), BackColor = new Brush(new Color(40, 120, 220, 220)), Offset = new Offset(0, -18) });
            features.Add(me);
        }

        stationLayer.Features = features;
        stationLayer.DataHasChanged();
        MapControl.RefreshGraphics();

        if (!centered && first != null)
            try { MapControl.Map.Navigator.CenterOnAndZoomTo(first, 150); centered = true; } catch { }
    }

    private void CenterOnGps()
    {
        MPoint? p = null;
        if (Vm?.MyPosition is { Locked: true } mp) p = Merc(mp.Longitude, mp.Latitude);
        if (p == null) return;
        try { MapControl.Map.Navigator.CenterOnAndZoomTo(p, 50); centered = true; } catch { }
    }
}
