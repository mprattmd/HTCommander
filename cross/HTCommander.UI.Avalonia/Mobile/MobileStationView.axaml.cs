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
        RequestPosButton.Click += (_, _) => Vm?.RequestPosition();
        BeaconNowButton.Click += (_, _) => Vm?.BeaconNow();          // App (TNC) beacon
        WriteBssButton.Click += (_, _) => Vm?.WriteBssSettings();    // Radio built-in beacon
    }
}
