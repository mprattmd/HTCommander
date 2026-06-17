/*
Copyright 2026 Ylian Saint-Hilaire
Licensed under the Apache License, Version 2.0 (the "License");
http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Collections.Generic;
using Avalonia.Controls;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia;

/// <summary>What the user did in the channel-edit dialog (the ShowDialog result).</summary>
public enum ChannelEditResult { Cancel, Save, MakeLive }

/// <summary>
/// Modal single-channel editor. The owner binds the live <see cref="EditableChannel"/>
/// (the same instance the MainViewModel is holding in EditingChannel) as the DataContext,
/// shows it with ShowDialog&lt;ChannelEditResult&gt;, and acts on the result by calling the
/// existing SaveEditingChannel / MakeEditingChannelLive / CancelEditingChannel methods.
/// Replaces the old inline overlay so desktop (Mac/Linux/Windows) edits in a real dialog box.
/// </summary>
public partial class ChannelEditWindow : Window
{
    public ChannelEditWindow()
    {
        InitializeComponent();

        // Closing via the window chrome (X) leaves the channel unchanged.
        SaveButton.Click += (_, _) => Close(ChannelEditResult.Save);
        CancelButton.Click += (_, _) => Close(ChannelEditResult.Cancel);
        MakeLiveButton.Click += (_, _) => Close(ChannelEditResult.MakeLive);
    }

    /// <summary>Populate the Mode/Power pickers from the owner's option lists (set before showing).</summary>
    public void SetOptions(IEnumerable<string> modes, IEnumerable<string> powers)
    {
        ModeCombo.ItemsSource = modes;
        PowerCombo.ItemsSource = powers;
    }
}
