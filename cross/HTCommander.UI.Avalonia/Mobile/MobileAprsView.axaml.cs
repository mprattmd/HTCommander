using Avalonia.Controls;
using Avalonia.VisualTree;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileAprsView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    public MobileAprsView()
    {
        InitializeComponent();
        SendButton.Click += (_, _) => Vm?.SendAprsMessage();
        StationsButton.Click += (_, _) =>
            this.FindAncestorOfType<MobileView>()?.Push(new MobileAprsStationsView(), "Stations heard");
    }
}
