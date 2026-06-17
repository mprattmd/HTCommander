/*
Copyright 2026 Ylian Saint-Hilaire
Licensed under the Apache License, Version 2.0 (the "License");
http://www.apache.org/licenses/LICENSE-2.0
*/

using Avalonia.Controls;

namespace HTCommander.UI.Avalonia;

/// <summary>
/// Modal "Add / edit contact" dialog. The owner binds the MainViewModel as the DataContext
/// (the form fields bind to the same Edit* / Show* properties the inline pane used), shows it
/// with ShowDialog&lt;bool&gt;, and — when it returns true (Save) — calls AddOrUpdateContact().
/// Replaces the old inline right-pane form so desktop (Mac/Linux/Windows) edits in a dialog box.
/// </summary>
public partial class ContactEditWindow : Window
{
    public ContactEditWindow()
    {
        InitializeComponent();

        ContactSaveButton.Click += (_, _) => Close(true);
        ContactCancelButton.Click += (_, _) => Close(false);
    }
}
