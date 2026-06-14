/*
Copyright 2026 Ylian Saint-Hilaire
Licensed under the Apache License, Version 2.0 (the "License");
http://www.apache.org/licenses/LICENSE-2.0
*/

using Avalonia.Controls;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia;

/// <summary>
/// Modal "Search RepeaterBook…" dialog. The owner constructs it with a
/// <see cref="RepeaterBookSearchViewModel"/>, shows it with ShowDialog&lt;bool&gt;,
/// and — when it returns true (the user clicked Add) — reads
/// <see cref="RepeaterBookSearchViewModel.GetSelectedChannels"/>.
/// </summary>
public partial class RepeaterBookSearchWindow : Window
{
    private RepeaterBookSearchViewModel Vm => DataContext as RepeaterBookSearchViewModel;

    public RepeaterBookSearchWindow()
    {
        InitializeComponent();

        SearchButton.Click += async (_, _) => { if (Vm != null) await Vm.SearchAsync(); };
        SelectAllButton.Click += (_, _) => Vm?.SelectAll(true);
        SelectNoneButton.Click += (_, _) => Vm?.SelectAll(false);
        CancelButton.Click += (_, _) => Close(false);
        AddButton.Click += (_, _) => Close(true);
    }
}
