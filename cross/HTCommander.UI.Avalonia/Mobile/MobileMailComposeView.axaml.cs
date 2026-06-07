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
        // Only leave the compose screen if the message actually saved (e.g. 'To' filled,
        // mail store available) — otherwise stay so the status line explains why.
        OutboxButton.Click += (_, _) => { if (Vm?.ComposeSaveToOutbox() == true) Close(); };
        DraftButton.Click  += (_, _) => { if (Vm?.SaveAsDraft() == true) Close(); };
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
