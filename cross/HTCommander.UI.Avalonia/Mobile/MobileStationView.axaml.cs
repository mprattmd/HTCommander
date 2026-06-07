using Avalonia.Controls;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileStationView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    public MobileStationView()
    {
        InitializeComponent();
        SetFixedButton.Click += (_, _) => Vm?.SetManualPosition();
    }
}
