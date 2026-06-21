using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

/// <summary>
/// Pick the memory slot for one imported channel — the mobile stand-in for the desktop
/// "drag the imported card onto a slot" gesture. Lists every slot (a name = already used);
/// tapping one writes the channel there and returns to the import list.
/// </summary>
public partial class MobileSlotPickerView : UserControl
{
    private readonly MainViewModel _main;
    private readonly EditableChannel _channel;

    // Parameterless ctor for the XAML previewer / designer only.
    public MobileSlotPickerView() : this(null!, null!) { }

    public MobileSlotPickerView(MainViewModel main, EditableChannel channel)
    {
        InitializeComponent();
        _main = main;
        _channel = channel;
        DataContext = main;

        if (channel != null)
            Header.Text = $"Place \"{channel.Name}\" — tap a memory slot. Slots showing a name are already in use; FREE ones are empty.";

        SlotList.AddHandler(Button.ClickEvent, OnSlotClick);
    }

    private void OnSlotClick(object? sender, RoutedEventArgs e)
    {
        for (var v = e.Source as Visual; v != null; v = v.GetVisualParent())
            if (v is StyledElement se && se.DataContext is ChannelSlot slot)
            {
                if (_main != null && _channel != null && _main.PlaceImported(slot.SlotId, _channel))
                    this.FindAncestorOfType<MobileView>()?.Back();
                return;
            }
    }
}
