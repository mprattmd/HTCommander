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
            var picked = _search.GetSelectedChannels();
            _main?.AddRepeaterBookChannels(picked);
            var host = this.FindAncestorOfType<MobileView>();
            host?.Back();   // pop the search page…
            // …and, when channels were added, land on the imported list so they're visible
            // and writable to the radio (otherwise the builder is invisible on mobile).
            if (picked.Count > 0 && _main != null)
                host?.Push(new MobileImportedChannelsView(_main), "Imported");
        };
        AttributionButton.Click += (_, _) =>
            TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(new System.Uri("https://www.repeaterbook.com/"));
    }
}
