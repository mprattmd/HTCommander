/*
Copyright 2026 Ylian Saint-Hilaire
Licensed under the Apache License, Version 2.0 (the "License");
http://www.apache.org/licenses/LICENSE-2.0
*/

using System;
using HTCommander;

namespace HTCommander.UI.Avalonia.ViewModels;

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
