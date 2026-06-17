/*
Copyright 2026 Ylian Saint-Hilaire
Licensed under the Apache License, Version 2.0 (the "License");
http://www.apache.org/licenses/LICENSE-2.0
*/

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using HTCommander.UI.Avalonia.ViewModels;

namespace HTCommander.UI.Avalonia;

/// <summary>
/// Floating (non-modal) list of imported channels that acts as a drag source. Because the
/// memory slots live in the main window, the drag must cross window boundaries — so this uses
/// native OS drag-and-drop (DragDrop.DoDragDropAsync) carrying the EditableChannel in an
/// in-process data format. The main window's slot grid accepts the drop (see MainView's
/// OnChannelDragOver / OnChannelDrop) and programs the slot.
/// </summary>
public partial class ImportedChannelsWindow : Window
{
    /// <summary>
    /// Drag payload: the index of the dragged channel in BuilderChannels, carried as an
    /// application string. It must be a platform-serializable format — macOS native drag
    /// (NSDraggingSession) throws if the pasteboard has no items, so an in-process-only object
    /// format cannot be used. The drop side (same process, shared VM) maps the index back to
    /// the actual EditableChannel.
    /// </summary>
    public static readonly DataFormat<string> ChannelDragFormat =
        DataFormat.CreateStringApplicationFormat("htcommander.channelindex");

    public ImportedChannelsWindow()
    {
        InitializeComponent();
        ImportedCards.AddHandler(PointerPressedEvent, OnCardPointerPressed, RoutingStrategies.Tunnel);
    }

    private async void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ImportedCards).Properties.IsLeftButtonPressed) return;
        var ch = AncestorDataContext<EditableChannel>(e.Source);
        if (ch == null || DataContext is not MainViewModel vm) return;
        int idx = vm.BuilderChannels.IndexOf(ch);
        if (idx < 0) return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ChannelDragFormat, idx.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        try { await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy); }
        catch { /* drag cancelled / platform refused — nothing to program */ }
    }

    private static T? AncestorDataContext<T>(object? source) where T : class
    {
        for (var v = source as Visual; v != null; v = v.GetVisualParent())
            if (v is StyledElement se && se.DataContext is T t) return t;
        return null;
    }
}
