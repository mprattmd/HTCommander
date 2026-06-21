using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

/// <summary>
/// Mobile review list of the channel builder (<see cref="MainViewModel.BuilderChannels"/>) —
/// the phone-side equivalent of the desktop "Imported channels" window. Tap a row to choose
/// the memory slot it should go into; ✕ drops it; "Place all in free slots" fills the radio's
/// empty slots without overwriting existing channels.
/// </summary>
public partial class MobileImportedChannelsView : UserControl
{
    private readonly MainViewModel _main;

    // Parameterless ctor for the XAML previewer / designer only.
    public MobileImportedChannelsView() : this(null!) { }

    public MobileImportedChannelsView(MainViewModel main)
    {
        InitializeComponent();
        _main = main;
        DataContext = main;

        PlaceAllButton.Click += (_, _) => _main?.PlaceAllInFreeSlots();
        ImportedList.AddHandler(Button.ClickEvent, OnRowButtonClick);
    }

    private void OnRowButtonClick(object? sender, RoutedEventArgs e)
    {
        // Did the click land on the per-row ✕ (remove) vs the row body (choose a slot)?
        bool isRemove = false;
        for (var v = e.Source as Visual; v != null; v = v.GetVisualParent())
            if (v is Button b && b.Classes.Contains("delBtn")) { isRemove = true; break; }

        for (var v = e.Source as Visual; v != null; v = v.GetVisualParent())
            if (v is StyledElement se && se.DataContext is EditableChannel ch)
            {
                if (isRemove)
                {
                    _main?.RemoveBuilderChannel(ch);
                }
                else if (_main != null)
                {
                    this.FindAncestorOfType<MobileView>()?.Push(new MobileSlotPickerView(_main, ch), "Place in slot");
                }
                return;
            }
    }
}
