/*
Copyright 2026 Ylian Saint-Hilaire
Licensed under the Apache License, Version 2.0 (the "License");
http://www.apache.org/licenses/LICENSE-2.0
*/

using System;
using System.Globalization;
using System.Linq;
using HTCommander;

namespace HTCommander.UI.Avalonia.ViewModels;

/// <summary>One APRS message row (sent or received) for the conversation list.</summary>
public sealed class AprsMessageRow
{
    public DateTime Time { get; init; }
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Text { get; init; } = "";
    public bool Outgoing { get; init; }
    public string Header => $"{Time:HH:mm}  {(Outgoing ? "▶ to " + To : "◀ " + From)}";
}

/// <summary>
/// A global APRS route (named digipeater path), mirroring the Windows routes manager.
/// Persisted form is <c>Name,Dest,Path1,Path2,…</c>; routes are joined with <c>|</c> in
/// the <c>AprsRoutes</c> DataBroker key. The same comma-array is the <c>Route</c> the
/// APRS handler consumes: <c>[RouteName, Dest, Path1, …]</c>.
/// </summary>
public sealed class AprsRoute : ViewModelBase
{
    private string name = "";
    public string Name { get => name; set { if (SetField(ref name, value)) OnPropertyChanged(nameof(Display)); } }
    private string destination = "APN000-0";
    public string Destination { get => destination; set { if (SetField(ref destination, value)) OnPropertyChanged(nameof(Display)); } }
    private string path = "";   // comma-separated digipeaters, e.g. WIDE1-1,WIDE2-2
    public string Path { get => path; set { if (SetField(ref path, value)) OnPropertyChanged(nameof(Display)); } }

    public string Display => string.IsNullOrEmpty(Path)
        ? $"{Name}  →  {Destination}"
        : $"{Name}  →  {Destination} via {Path}";

    /// <summary>Persisted/route-array form: Name,Dest,Path1,Path2,…</summary>
    public string ToStorage()
    {
        var parts = new System.Collections.Generic.List<string> { Name, string.IsNullOrWhiteSpace(Destination) ? "APN000-0" : Destination.Trim() };
        if (!string.IsNullOrWhiteSpace(Path))
            foreach (var p in Path.Split(',')) { var t = p.Trim(); if (t.Length > 0) parts.Add(t); }
        return string.Join(",", parts);
    }

    public string[] ToRouteArray() => ToStorage().Split(',');

    public static AprsRoute FromStorage(string s)
    {
        var p = s.Split(',');
        return new AprsRoute
        {
            Name = p.Length > 0 ? p[0] : "",
            Destination = p.Length > 1 ? p[1] : "APN000-0",
            Path = p.Length > 2 ? string.Join(",", p.Skip(2)) : "",
        };
    }
}

/// <summary>A recorded audio clip (WAV on disk) for the Clips tab.</summary>
public sealed class AudioClipInfo : ViewModelBase
{
    public string FileName { get; init; } = "";        // leaf name, e.g. clip-2026....wav
    public string FullPath { get; init; } = "";
    private string displayName = "";
    public string DisplayName { get => displayName; set { if (SetField(ref displayName, value)) OnPropertyChanged(nameof(Label)); } }
    public DateTime Recorded { get; init; }
    public double Seconds { get; init; }
    public string Duration => TimeSpan.FromSeconds(Seconds).ToString(@"m\:ss");
    public string Label => $"{DisplayName}   ({Duration})";
}

/// <summary>One timestamped point in a station's track history (for map polylines).</summary>
public readonly struct TrackPoint
{
    public TrackPoint(double lat, double lon, DateTime time) { Latitude = lat; Longitude = lon; Time = time; }
    public double Latitude { get; }
    public double Longitude { get; }
    public DateTime Time { get; }
}

/// <summary>A mailbox folder row with live total/unread counts for the Mail folder list.</summary>
public sealed class MailFolder : ViewModelBase
{
    public string Name { get; }
    public MailFolder(string name) { Name = name; }   // was assigning a shadow field, leaving Name null (blank folders + zero counts)
    private int total, unread;
    public int Total { get => total; set { if (SetField(ref total, value)) OnPropertyChanged(nameof(Display)); } }
    public int Unread { get => unread; set { if (SetField(ref unread, value)) OnPropertyChanged(nameof(Display)); } }
    public string Display => unread > 0 ? $"{Name}  {total} ({unread})" : (total > 0 ? $"{Name}  {total}" : Name);
}

/// <summary>One memory-slot tile in the channel grid (mirrors the Windows channel cards).</summary>
public sealed class ChannelSlot : ViewModelBase
{
    public int SlotId { get; }
    private string name = "";
    private double rxMHz;
    private bool isActive;

    public ChannelSlot(int slotId) { SlotId = slotId; }

    public string Name { get => name; set { if (SetField(ref name, value)) { OnPropertyChanged(nameof(Display)); OnPropertyChanged(nameof(IsEmpty)); } } }
    public double RxMHz { get => rxMHz; set { if (SetField(ref rxMHz, value)) OnPropertyChanged(nameof(FreqText)); } }
    public bool IsActive { get => isActive; set => SetField(ref isActive, value); }

    public bool IsEmpty => string.IsNullOrEmpty(name);
    /// <summary>1-based slot number for display (the wire/channel_id stays 0-based via SlotId).</summary>
    public int Number => SlotId + 1;
    public string Display => IsEmpty ? Number.ToString() : name;
    public string FreqText => rxMHz > 0 ? rxMHz.ToString("0.0000", CultureInfo.InvariantCulture) : "";
}

/// <summary>
/// A bindable, human-friendly view of a <see cref="RadioChannelInfo"/> for the
/// channel builder grid: frequencies in MHz, mode/power as short strings. Converts
/// to/from the wire model (<see cref="RadioChannelInfo"/>, freqs in Hz) so imported,
/// hand-edited, and radio-read channels share one editable representation.
/// </summary>
public sealed class EditableChannel : ViewModelBase
{
    private int channelId;
    private string name = "";
    private double rxMHz;
    private double txMHz;
    private string mode = "FM";        // FM | NFM | AM | DMR
    private string power = "H";         // H | M | L
    private double rxToneHz;            // CTCSS in Hz (0 = none), display value
    private double txToneHz;
    private bool scan;

    public int ChannelId { get => channelId; set => SetField(ref channelId, value); }
    public string Name { get => name; set => SetField(ref name, value); }
    public double RxMHz { get => rxMHz; set => SetField(ref rxMHz, value); }
    public double TxMHz { get => txMHz; set => SetField(ref txMHz, value); }
    public string Mode { get => mode; set => SetField(ref mode, value); }
    public string Power { get => power; set => SetField(ref power, value); }
    public double RxToneHz { get => rxToneHz; set => SetField(ref rxToneHz, value); }
    public double TxToneHz { get => txToneHz; set => SetField(ref txToneHz, value); }
    public bool Scan { get => scan; set => SetField(ref scan, value); }

    // The DataGrid edits these string views so parsing/formatting is always
    // InvariantCulture (a '.' decimal point), independent of the OS locale — otherwise
    // a comma-decimal locale could mis-parse a typed frequency.
    public string RxMHzText
    {
        get => rxMHz.ToString("0.0000", CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) RxMHz = d; }
    }
    public string TxMHzText
    {
        get => txMHz.ToString("0.0000", CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) TxMHz = d; }
    }
    public string RxToneText
    {
        get => rxToneHz.ToString("0.0", CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) RxToneHz = d; }
    }
    public string TxToneText
    {
        get => txToneHz.ToString("0.0", CultureInfo.InvariantCulture);
        set { if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) TxToneHz = d; }
    }

    public EditableChannel() { }

    public EditableChannel(RadioChannelInfo c)
    {
        channelId = c.channel_id;
        name = c.name_str ?? "";
        rxMHz = c.rx_freq / 1_000_000.0;
        txMHz = c.tx_freq / 1_000_000.0;
        rxToneHz = c.rx_sub_audio / 100.0;
        txToneHz = c.tx_sub_audio / 100.0;
        scan = c.scan;
        power = c.tx_at_max_power ? "H" : (c.tx_at_med_power ? "M" : "L");
        mode = c.rx_mod switch
        {
            RadioModulationType.AM => "AM",
            RadioModulationType.DMR => "DMR",
            _ => c.bandwidth == RadioBandwidthType.NARROW ? "NFM" : "FM",
        };
    }

    /// <summary>Builds the wire model for WRITE_RF_CH. <paramref name="id"/> overrides
    /// the channel slot (the builder assigns slots by row order on write).</summary>
    public RadioChannelInfo ToRadioChannelInfo(int id)
    {
        var c = new RadioChannelInfo
        {
            channel_id = id,
            name_str = (name ?? "").Length > 10 ? name.Substring(0, 10) : (name ?? ""),
            rx_freq = (int)Math.Round(rxMHz * 1_000_000),
            tx_freq = (int)Math.Round(txMHz * 1_000_000),
            rx_sub_audio = (int)Math.Round(rxToneHz * 100),
            tx_sub_audio = (int)Math.Round(txToneHz * 100),
            scan = scan,
            tx_at_max_power = power == "H",
            tx_at_med_power = power == "M",
        };
        switch ((mode ?? "FM").ToUpperInvariant())
        {
            case "AM": c.rx_mod = c.tx_mod = RadioModulationType.AM; c.bandwidth = RadioBandwidthType.WIDE; break;
            case "DMR": c.rx_mod = c.tx_mod = RadioModulationType.DMR; c.bandwidth = RadioBandwidthType.NARROW; break;
            case "NFM": c.rx_mod = c.tx_mod = RadioModulationType.FM; c.bandwidth = RadioBandwidthType.NARROW; break;
            default: c.rx_mod = c.tx_mod = RadioModulationType.FM; c.bandwidth = RadioBandwidthType.WIDE; break;
        }
        return c;
    }
}
