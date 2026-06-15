using System;
using Avalonia.Controls;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileSettingsView : UserControl
{
    public MobileSettingsView()
    {
        InitializeComponent();
        RequestTokenButton.Click += (_, _) =>
        {
            var top = TopLevel.GetTopLevel(this);
            top?.Launcher.LaunchUriAsync(new Uri("https://www.repeaterbook.com/user/api_apps.php"));
        };
    }
}
