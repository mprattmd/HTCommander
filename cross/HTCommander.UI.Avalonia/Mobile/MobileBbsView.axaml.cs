using Avalonia.Controls;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileBbsView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public MobileBbsView()
    {
        InitializeComponent();
        BbsToggleButton.Click     += (_, _) => Vm?.ToggleBbs();
        BbsClearStatsButton.Click += (_, _) => Vm?.ClearBbsStats();
    }
}
