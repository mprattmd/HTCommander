/*
Copyright 2026 Ylian Saint-Hilaire
Licensed under the Apache License, Version 2.0 (the "License");
http://www.apache.org/licenses/LICENSE-2.0
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HTCommander.Core.Radio;

namespace HTCommander.UI.Avalonia.ViewModels;

/// <summary>One search-result row: the mapped channel plus a checkbox + display fields.</summary>
public sealed class RepeaterBookRow : ViewModelBase
{
    private readonly RepeaterBookResult _src;
    public RepeaterBookRow(RepeaterBookResult src) { _src = src; }

    private bool isSelected;
    public bool IsSelected { get => isSelected; set => SetField(ref isSelected, value); }

    public string Callsign => _src.Callsign ?? "";
    public string Location => string.Join(", ", new[] { _src.NearestCity, _src.State }.Where(s => !string.IsNullOrWhiteSpace(s)));
    public string Output => _src.Frequency ?? "";
    public string Input => _src.InputFreq ?? "";
    public string Tone => _src.Pl ?? "";
    public string Mode => string.Equals(_src.Dmr, "Yes", StringComparison.OrdinalIgnoreCase) ? "DMR" : "FM";
    public string Status => _src.OperationalStatus ?? "";

    public double? Latitude => TryD(_src.Lat);
    public double? Longitude => TryD(_src.Long);
    public double RxFreqMHz => TryD(_src.Frequency) ?? 0;

    public RadioChannelInfo ToChannel() => RepeaterBookClient.ToRadioChannelInfo(_src);

    private static double? TryD(string s)
        => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
}

/// <summary>
/// Backs the "Search RepeaterBook…" dialog. Builds a <see cref="RepeaterBookQuery"/>
/// from the inputs, calls the Core client, then applies the band and proximity
/// post-filters before presenting rows the user can check and add to the builder.
/// </summary>
public sealed class RepeaterBookSearchViewModel : ViewModelBase
{
    // Reuse one HttpClient for the dialog's lifetime (cheap, avoids socket churn).
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

    private readonly string _token;
    private readonly double? _fixLat;
    private readonly double? _fixLon;
    private CancellationTokenSource _cts;

    public RepeaterBookSearchViewModel(string token, double? fixLat = null, double? fixLon = null)
    {
        _token = token ?? "";
        _fixLat = fixLat;
        _fixLon = fixLon;
        ProximityAvailable = fixLat.HasValue && fixLon.HasValue;
        if (!ProximityAvailable) useProximity = false;
        UpdateStatusForToken();
    }

    public ObservableCollection<string> ServiceOptions { get; } = new() { "Amateur", "GMRS" };
    public ObservableCollection<string> BandOptions { get; } = new() { "VHF + UHF", "VHF (2 m)", "UHF (70 cm)", "All bands" };
    public ObservableCollection<string> ModeOptions { get; } = new() { "FM analog", "DMR", "Any" };

    private string selectedService = "Amateur";
    public string SelectedService
    {
        get => selectedService;
        set { if (SetField(ref selectedService, value)) { OnPropertyChanged(nameof(IsAmateur)); OnPropertyChanged(nameof(BandEnabled)); } }
    }

    /// <summary>GMRS has no mode param and is US-only single-band; gate the amateur-only inputs.</summary>
    public bool IsAmateur => SelectedService == "Amateur";
    public bool BandEnabled => IsAmateur;

    private string country = "United States";
    public string Country { get => country; set => SetField(ref country, value); }

    private string state = "";
    public string State { get => state; set => SetField(ref state, value); }

    private string county = "";
    public string County { get => county; set => SetField(ref county, value); }

    private string city = "";
    public string City { get => city; set => SetField(ref city, value); }

    private string selectedBand = "VHF + UHF";
    public string SelectedBand { get => selectedBand; set => SetField(ref selectedBand, value); }

    private string selectedMode = "FM analog";
    public string SelectedMode { get => selectedMode; set => SetField(ref selectedMode, value); }

    public bool ProximityAvailable { get; }

    private bool useProximity;
    public bool UseProximity { get => useProximity; set => SetField(ref useProximity, value); }

    private int proximityMiles = 50;
    public int ProximityMiles { get => proximityMiles; set => SetField(ref proximityMiles, value); }

    public ObservableCollection<RepeaterBookRow> Results { get; } = new();

    private bool isSearching;
    public bool IsSearching
    {
        get => isSearching;
        set { if (SetField(ref isSearching, value)) OnPropertyChanged(nameof(CanSearch)); }
    }
    public bool CanSearch => !IsSearching;

    private string statusText = "";
    public string StatusText { get => statusText; set => SetField(ref statusText, value); }

    public bool HasToken => !string.IsNullOrWhiteSpace(_token);

    public async Task SearchAsync()
    {
        if (IsSearching) return;
        if (!HasToken) { StatusText = "No RepeaterBook token — set one in Settings."; return; }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsSearching = true;
        StatusText = "Searching RepeaterBook…";
        Results.Clear();
        try
        {
            var query = new RepeaterBookQuery
            {
                Service = IsAmateur ? RepeaterBookService.Amateur : RepeaterBookService.Gmrs,
                Country = string.IsNullOrWhiteSpace(Country) ? null : Country,
                State = string.IsNullOrWhiteSpace(State) ? null : State,
                County = string.IsNullOrWhiteSpace(County) ? null : County,
                City = string.IsNullOrWhiteSpace(City) ? null : City,
                Mode = IsAmateur ? ModeParam(SelectedMode) : null,
            };

            var client = new RepeaterBookClient(Http);
            RepeaterBookResult[] raw = await client.SearchAsync(query, _token, _cts.Token);

            int total = raw.Length;
            var rows = raw
                .Where(InBand)
                .Where(WithinProximity)
                .Select(r => new RepeaterBookRow(r))
                .ToList();

            foreach (var row in rows) Results.Add(row);

            if (total == 0) StatusText = "No repeaters matched that search.";
            else if (rows.Count == 0) StatusText = $"{total} found, but none within the band/proximity filters.";
            else StatusText = $"{rows.Count} repeater(s)" + (rows.Count < total ? $" (filtered from {total})." : ".");
        }
        catch (OperationCanceledException) { /* superseded by a newer search */ }
        catch (RepeaterBookException ex) { StatusText = ex.Message; }
        catch (Exception ex) { StatusText = "Search failed: " + ex.Message; }
        finally { IsSearching = false; }
    }

    public void SelectAll(bool selected)
    {
        foreach (var r in Results) r.IsSelected = selected;
    }

    /// <summary>The channels for the checked rows, ready to append to the builder.</summary>
    public List<RadioChannelInfo> GetSelectedChannels()
        => Results.Where(r => r.IsSelected).Select(r => r.ToChannel()).Where(c => c != null).ToList();

    private static string ModeParam(string display) => display switch
    {
        "FM analog" => "analog",
        "DMR" => "DMR",
        _ => null,   // "Any"
    };

    private bool InBand(RepeaterBookResult r)
    {
        if (!IsAmateur || SelectedBand == "All bands") return true;
        double mhz = double.TryParse(r.Frequency, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : 0;
        bool vhf = mhz >= 144 && mhz <= 148;
        bool uhf = mhz >= 420 && mhz <= 450;
        return SelectedBand switch
        {
            "VHF (2 m)" => vhf,
            "UHF (70 cm)" => uhf,
            _ => vhf || uhf,   // "VHF + UHF"
        };
    }

    private bool WithinProximity(RepeaterBookResult r)
    {
        if (!UseProximity || !ProximityAvailable) return true;
        double? lat = TryD(r.Lat), lon = TryD(r.Long);
        if (!lat.HasValue || !lon.HasValue) return false;   // can't place it — exclude when proximity is on
        return HaversineMiles(_fixLat.Value, _fixLon.Value, lat.Value, lon.Value) <= ProximityMiles;
    }

    private void UpdateStatusForToken()
        => StatusText = HasToken ? "Enter a location and search." : "Set a RepeaterBook API token in Settings to search.";

    private static double? TryD(string s)
        => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v) ? v : (double?)null;

    private static double HaversineMiles(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 3958.7613; // Earth radius, miles
        double dLat = Deg2Rad(lat2 - lat1), dLon = Deg2Rad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(Deg2Rad(lat1)) * Math.Cos(Deg2Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double Deg2Rad(double d) => d * Math.PI / 180.0;
}
