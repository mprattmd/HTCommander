using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileMailComposeView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public MobileMailComposeView()
    {
        InitializeComponent();
        OutboxButton.Click += (_, _) => { Vm?.ComposeSaveToOutbox(); Close(); };
        DraftButton.Click  += (_, _) => { Vm?.SaveAsDraft(); Close(); };
        AttachButton.Click += async (_, _) => await AddAttachmentAsync();
        AttachRemoveButton.Click += (_, _) => Vm?.RemoveComposeAttachment();
    }

    private void Close() => this.FindAncestorOfType<MobileView>()?.Back();

    private async Task AddAttachmentAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Attach a file", AllowMultiple = true });
        foreach (var f in files)
        {
            var p = f.TryGetLocalPath();
            if (p != null) Vm?.AddComposeAttachment(p);
        }
    }
}
