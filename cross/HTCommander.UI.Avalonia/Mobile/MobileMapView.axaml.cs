using Avalonia.Controls;
using Mapsui;
using Mapsui.Tiling;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileMapView : UserControl
{
    public MobileMapView()
    {
        InitializeComponent();
        var map = new Map();
        map.Layers.Add(OpenStreetMap.CreateTileLayer());
        MapControl.Map = map;
    }
}
