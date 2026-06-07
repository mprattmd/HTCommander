using Avalonia.Controls;
using Avalonia.VisualTree;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileChannelEditView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;
    public MobileChannelEditView()
    {
        InitializeComponent();
        SaveButton.Click += (_, _) => { Vm?.SaveEditingChannel(); Close(); };
        CancelButton.Click += (_, _) => { Vm?.CancelEditingChannel(); Close(); };
    }
    private void Close() => this.FindAncestorOfType<MobileView>()?.Back();
}
