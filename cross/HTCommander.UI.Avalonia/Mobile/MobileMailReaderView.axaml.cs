using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileMailReaderView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public MobileMailReaderView()
    {
        InitializeComponent();
        ReplyButton.Click    += (_, _) => { Vm?.ReplyMail();    OpenCompose(); };
        ReplyAllButton.Click += (_, _) => { Vm?.ReplyAllMail(); OpenCompose(); };
        ForwardButton.Click  += (_, _) => { Vm?.ForwardMail();  OpenCompose(); };
        MoveButton.Click   += (_, _) => { if (Vm != null) Vm.MoveSelectedMailTo(Vm.MoveTarget); Close(); };
        DeleteButton.Click += (_, _) => { Vm?.DeleteSelectedMail(); Close(); };
        AttOpenButton.Click += (_, _) => Vm?.OpenSelectedAttachment();
        AttSaveButton.Click += async (_, _) => await SaveAttachmentAsync();
    }

    private void OpenCompose() => this.FindAncestorOfType<MobileView>()?.Push(new MobileMailComposeView(), "Compose");
    private void Close() => this.FindAncestorOfType<MobileView>()?.Back();

    private async Task SaveAttachmentAsync()
    {
        var att = Vm?.SelectedAttachment;
        var top = TopLevel.GetTopLevel(this);
        if (att == null || top == null) return;
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save attachment",
            SuggestedFileName = att.Name,
        });
        var path = file?.TryGetLocalPath();
        if (path != null) Vm?.SaveAttachmentTo(path);
    }
}
