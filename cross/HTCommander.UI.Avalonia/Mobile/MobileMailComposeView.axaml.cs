using Avalonia.Controls;
using Avalonia.VisualTree;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileMailComposeView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    public MobileMailComposeView()
    {
        InitializeComponent();
        OutboxButton.Click += (_, _) =>
        {
            Vm?.ComposeSaveToOutbox();
            this.FindAncestorOfType<MobileView>()?.Back();
        };
    }
}
