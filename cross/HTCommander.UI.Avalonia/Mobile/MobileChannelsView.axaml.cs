using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

public partial class MobileChannelsView : UserControl
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public MobileChannelsView()
    {
        InitializeComponent();
        ImportButton.Click += async (_, _) => await ImportCsvAsync();
        LoadButton.Click += (_, _) => Vm?.LoadChannelsFromRadio();
        LoadAllButton.Click += (_, _) => Vm?.LoadAllBanks();
        WriteAllButton.Click += (_, _) => Vm?.WriteChannelsToRadio();
        SlotList.AddHandler(Button.ClickEvent, OnSlotClick);
    }

    private async Task ImportCsvAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import channels (CSV: CHIRP / RepeaterBook / native)",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("CSV files") { Patterns = new[] { "*.csv" } } },
        });
        var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path != null) Vm?.ImportChannelsFromCsv(path);
    }

    private void OnSlotClick(object? sender, RoutedEventArgs e)
    {
        for (var v = e.Source as Visual; v != null; v = v.GetVisualParent())
            if (v is StyledElement se && se.DataContext is ChannelSlot slot)
            {
                Vm?.BeginEditSlot(slot.SlotId);
                this.FindAncestorOfType<MobileView>()?.Push(new MobileChannelEditView(), "CH " + slot.SlotId);
                return;
            }
    }
}
