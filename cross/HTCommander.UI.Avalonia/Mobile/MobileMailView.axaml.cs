using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HTCommander;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileMailView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public MobileMailView()
    {
        InitializeComponent();
        SyncButton.Click += (_, _) => Vm?.SyncWinlinkInternet();
        ComposeButton.Click += (_, _) =>
        {
            Vm?.NewMail();
            this.FindAncestorOfType<MobileView>()?.Push(new MobileMailComposeView(), "Compose");
        };
        MailList.AddHandler(Button.ClickEvent, OnRowClick);
    }

    private void OnRowClick(object? sender, RoutedEventArgs e)
    {
        for (var v = e.Source as Visual; v != null; v = v.GetVisualParent())
            if (v is StyledElement se && se.DataContext is WinLinkMail mail)
            {
                if (Vm != null) Vm.SelectedMail = mail;
                this.FindAncestorOfType<MobileView>()?.Push(new MobileMailReaderView(), mail.Subject ?? "Message");
                return;
            }
    }
}
