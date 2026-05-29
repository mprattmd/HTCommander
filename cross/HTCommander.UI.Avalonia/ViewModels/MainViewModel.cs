/*
Copyright 2026 Ylian Saint-Hilaire

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

   http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HTCommander;                       // RadioHtStatus (Core)
using HTCommander.Core.Abstractions;
using HTCommander.Core.Abstractions.Audio;
using HTCommander.Platform.Linux;
using HTCommander.UI.Avalonia.Platform;

namespace HTCommander.UI.Avalonia.ViewModels;

/// <summary>
/// Shell view model. Drives the connection panel using the validated Linux
/// stack (<see cref="BlueZRadioDiscovery"/> + <see cref="RadioBluetoothLinux"/>):
/// lists compatible radios, opens/closes the transport, and surfaces connection
/// state plus a live frame log. All cross-thread updates are marshalled through
/// the injected <see cref="IUiDispatcher"/>.
///
/// NOTE: connecting here drives the raw transport directly and sends a hardcoded
/// GET_DEV_INFO probe — full radio control arrives when Radio.cs is portable
/// (porting-plan step 3). The shell, dispatcher and DI wiring are the deliverable.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private const int GroupBasic = 2;
    private const int CmdGetHtStatus = 20;
    // BASIC(2) / GET_DEV_INFO(4) / data=3 — same probe Radio sends on connect.
    private static readonly byte[] GetDevInfo = { 0x00, 0x02, 0x00, 0x04, 0x03 };
    // BASIC(2) / GET_HT_STATUS(20), no payload — polled while connected.
    private static readonly byte[] GetHtStatus = { 0x00, 0x02, 0x00, 0x14 };

    private readonly IUiDispatcher dispatcher;
    private readonly BlueZRadioDiscovery discovery = new();
    private readonly ILogger logger;
    private RadioBluetoothLinux? transport;
    private CancellationTokenSource? pollCts;

    public ObservableCollection<RadioDeviceInfo> Radios { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>Settings sub-view-model (bound by the Settings tab).</summary>
    public SettingsViewModel Settings { get; }

    public MainViewModel(IUiDispatcher dispatcher, IAudioDeviceEnumerator audioDevices)
    {
        this.dispatcher = dispatcher;
        logger = new CallbackLogger(AppendLog);
        Settings = new SettingsViewModel(dispatcher, audioDevices);
        Refresh();
    }

    private RadioDeviceInfo? selectedRadio;
    public RadioDeviceInfo? SelectedRadio
    {
        get => selectedRadio;
        set { if (SetField(ref selectedRadio, value)) OnPropertyChanged(nameof(CanConnect)); }
    }

    private string status = "Disconnected";
    public string Status { get => status; private set => SetField(ref status, value); }

    private bool bluetoothAvailable;
    public bool BluetoothAvailable { get => bluetoothAvailable; private set => SetField(ref bluetoothAvailable, value); }

    private bool connected;
    public bool Connected
    {
        get => connected;
        private set
        {
            if (SetField(ref connected, value))
            {
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanDisconnect));
            }
        }
    }

    private int frameCount;
    public int FrameCount { get => frameCount; private set => SetField(ref frameCount, value); }

    public bool CanConnect => !Connected && SelectedRadio != null;
    public bool CanDisconnect => Connected || transport != null;

    // --- Live HT status telemetry (parsed from GET_HT_STATUS via Core RadioHtStatus) ---

    private bool hasStatus;
    public bool HasStatus { get => hasStatus; private set => SetField(ref hasStatus, value); }

    private string powerText = "—";
    public string PowerText { get => powerText; private set => SetField(ref powerText, value); }

    private string txRxState = "—";
    public string TxRxState { get => txRxState; private set => SetField(ref txRxState, value); }

    private string squelchText = "—";
    public string SquelchText { get => squelchText; private set => SetField(ref squelchText, value); }

    private int channelId;
    public int ChannelId { get => channelId; private set => SetField(ref channelId, value); }

    private int rssi;
    public int Rssi { get => rssi; private set => SetField(ref rssi, value); }

    private int region;
    public int Region { get => region; private set => SetField(ref region, value); }

    private string gpsText = "—";
    public string GpsText { get => gpsText; private set => SetField(ref gpsText, value); }

    /// <summary>Re-scans BlueZ for adapter availability and compatible radios.</summary>
    public void Refresh()
    {
        Task.Run(() =>
        {
            bool bt = discovery.CheckBluetooth();
            var radios = discovery.FindCompatibleRadios().ToList();
            dispatcher.Post(() =>
            {
                BluetoothAvailable = bt;
                var previous = SelectedRadio?.Address;
                Radios.Clear();
                foreach (var r in radios) Radios.Add(r);
                SelectedRadio = Radios.FirstOrDefault(r => r.Address == previous) ?? Radios.FirstOrDefault();
                AppendLog(bt
                    ? $"Bluetooth ready — {radios.Count} compatible radio(s) found."
                    : "No Bluetooth adapter available.");
            });
        });
    }

    public void Connect()
    {
        if (!CanConnect) return;
        var radio = SelectedRadio!;
        FrameCount = 0;
        Status = $"Connecting to {radio.Name} ({radio.Address})...";
        AppendLog(Status);

        transport = new RadioBluetoothLinux(radio.Address, logger,
            reason => dispatcher.Post(() =>
            {
                StopPolling();
                Connected = false;
                transport = null;
                HasStatus = false;
                Status = "Disconnected: " + reason;
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanDisconnect));
            }));

        transport.OnConnected += () => dispatcher.Post(() =>
        {
            Connected = true;
            Status = $"Connected to {radio.Name}";
            AppendLog("Connected — sending GET_DEV_INFO and polling GET_HT_STATUS.");
            transport?.EnqueueWrite(0, GetDevInfo);
            StartPolling();
        });

        transport.ReceivedData += (_, _, value) => dispatcher.Post(() =>
        {
            FrameCount++;
            AppendLog($"<<< [{value.Length}B] {BytesToHex(value)}");
            TryApplyHtStatus(value);
        });

        transport.Connect();
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
    }

    public void Disconnect()
    {
        var t = transport;
        if (t == null) return;
        AppendLog("Disconnecting...");
        StopPolling();
        Task.Run(() => t.Disconnect());
    }

    private void StartPolling()
    {
        StopPolling();
        var cts = new CancellationTokenSource();
        pollCts = cts;
        CancellationToken ct = cts.Token;
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { transport?.EnqueueWrite(0, GetHtStatus); } catch (Exception) { }
                try { await Task.Delay(1500, ct); } catch (Exception) { break; }
            }
        });
    }

    private void StopPolling()
    {
        try { pollCts?.Cancel(); } catch (Exception) { }
        pollCts = null;
    }

    // Parse a BASIC/GET_HT_STATUS reply into live telemetry (reuses Core RadioHtStatus).
    private void TryApplyHtStatus(byte[] value)
    {
        if (value.Length < 7) return;
        int group = (value[0] << 8) | value[1];
        int cmd = ((value[2] << 8) | value[3]) & 0x7FFF;
        if (group != GroupBasic || cmd != CmdGetHtStatus) return;

        try
        {
            var s = new RadioHtStatus(value);
            HasStatus = true;
            PowerText = s.is_power_on ? "On" : "Off";
            TxRxState = s.is_in_tx ? "Transmitting" : s.is_in_rx ? "Receiving" : "Idle";
            SquelchText = s.is_sq ? "Open" : "Closed";
            ChannelId = s.channel_id;
            Rssi = s.rssi;
            Region = s.curr_region;
            GpsText = s.is_gps_locked ? "Locked" : "No lock";
        }
        catch (Exception) { /* malformed frame — ignore */ }
    }

    private void AppendLog(string line)
    {
        void Add()
        {
            Log.Add(line);
            while (Log.Count > 500) Log.RemoveAt(0);   // bound growth
        }
        if (dispatcher.IsDispatchRequired) dispatcher.Post(Add);
        else Add();
    }

    private static string BytesToHex(byte[] data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 3);
        foreach (byte b in data) { sb.Append(b.ToString("X2")); sb.Append(' '); }
        return sb.ToString().TrimEnd();
    }
}
