using Avalonia.Controls;
using Avalonia.VisualTree;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia.Mobile;

/// <summary>
/// Mobile RepeaterBook search page. Reuses the shared
/// <see cref="RepeaterBookSearchViewModel"/>; on "Add" it appends the picked
/// repeaters to the builder via the host <see cref="MainViewModel"/> and pops back.
/// </summary>
public partial class MobileRepeaterBookSearchView : UserControl
{
    private readonly MainViewModel _main;
    private readonly RepeaterBookSearchViewModel _search;

    // Parameterless ctor for the XAML previewer / designer only.
    public MobileRepeaterBookSearchView() : this(null!) { }

    public MobileRepeaterBookSearchView(MainViewModel main)
    {
        InitializeComponent();
        _main = main;

        string token = HTCommander.DataBroker.GetValue<string>(0, "RepeaterBookToken", "") ?? "";
        var (lat, lon) = main?.CurrentFix() ?? (null, null);
        _search = new RepeaterBookSearchViewModel(token, lat, lon);
        DataContext = _search;

        SearchButton.Click += async (_, _) => await _search.SearchAsync();
        SelectAllButton.Click += (_, _) => _search.SelectAll(true);
        SelectNoneButton.Click += (_, _) => _search.SelectAll(false);
        AddButton.Click += (_, _) =>
        {
            _main?.AddRepeaterBookChannels(_search.GetSelectedChannels());
            this.FindAncestorOfType<MobileView>()?.Back();
        };
        AttributionButton.Click += (_, _) =>
            TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new System.Uri("https://www.repeaterbook.com/"));
    }
}
