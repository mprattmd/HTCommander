using Avalonia.Controls;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileTerminalView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public MobileTerminalView()
    {
        InitializeComponent();
        SessionConnectButton.Click    += (_, _) => Vm?.ConnectSession();
        SessionDisconnectButton.Click += (_, _) => Vm?.DisconnectSession();
        SendTerminalButton.Click      += (_, _) => Vm?.SendTerminal();
    }
}
