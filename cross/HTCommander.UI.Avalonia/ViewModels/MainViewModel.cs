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
using System.Threading.Tasks;
using HTCommander;                       // RadioController, RadioHtStatus, RadioDeviceSummary, DataBrokerClient (Core)
using HTCommander.Core.Abstractions;
using HTCommander.Core.Abstractions.Audio;
using HTCommander.Platform.Linux;
using HTCommander.Platform.Linux.Audio;
using HTCommander.UI.Avalonia.Platform;

namespace HTCommander.UI.Avalonia.ViewModels;

/// <summary>
/// Shell view model. Lists compatible radios (<see cref="BlueZRadioDiscovery"/>),
/// opens the <see cref="RadioBluetoothLinux"/> transport, and drives it through a
/// portable <see cref="RadioController"/> (Core). The controller publishes parsed
/// results — HT status, battery, device info — to the <c>DataBroker</c>; this VM
/// subscribes (device 0) and binds them to the UI, i.e. the shell is a pure
/// DataBroker consumer. Cross-thread updates are marshalled via <see cref="IUiDispatcher"/>
/// (DataBroker marshals subscriber callbacks through the same dispatcher).
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly IUiDispatcher dispatcher;
    private readonly BlueZRadioDiscovery discovery = new();
    private readonly ILogger logger;
    private readonly DataBrokerClient broker = new DataBrokerClient();
    private RadioBluetoothLinux? transport;
    private RadioController? controller;
    private RadioAudioChannelLinux? audioChannel;
    private RadioVoiceReceiver? voiceReceiver;
    private IAudioPlayback? voicePlayback;
    private RadioVoiceTransmitter? voiceTransmitter;
    private IAudioCapture? mic;

    public ObservableCollection<RadioDeviceInfo> Radios { get; } = new();
    public ObservableCollection<string> Log { get; } = new();
    public ObservableCollection<RadioChannelSummary> Channels { get; } = new();
    public ObservableCollection<ReceivedPacketSummary> Packets { get; } = new();
    public ObservableCollection<AprsStationSummary> Stations { get; } = new();

    // Contacts (address book) — Core StationInfoClass, persisted via DataBroker "Stations".
    public ObservableCollection<StationInfoClass> Contacts { get; } = new();
    public Array StationTypeOptions { get; } = Enum.GetValues(typeof(StationInfoClass.StationTypes));
    private bool loadingContacts;

    /// <summary>Settings sub-view-model (bound by the Settings tab).</summary>
    public SettingsViewModel Settings { get; }

    public MainViewModel(IUiDispatcher dispatcher, IAudioDeviceEnumerator audioDevices)
    {
        this.dispatcher = dispatcher;
        logger = new CallbackLogger(AppendLog);
        Settings = new SettingsViewModel(dispatcher, audioDevices);

        // Consume the controller's published telemetry (device 0). DataBroker
        // marshals these callbacks onto the UI thread for us.
        broker.Subscribe(0, "HtStatus", (_, _, data) => { if (data is RadioHtStatus s) ApplyHtStatus(s); });
        broker.Subscribe(0, "BatteryAsPercentage", (_, _, data) => { if (data is int p) BatteryPercent = p; });
        broker.Subscribe(0, "DeviceInfo", (_, _, data) => { if (data is RadioDeviceSummary d) ApplyDeviceInfo(d); });
        broker.Subscribe(0, "Channel", (_, _, data) => { if (data is RadioChannelSummary c) ApplyChannel(c); });
        broker.Subscribe(0, "PacketReceived", (_, _, data) => { if (data is ReceivedPacketSummary p) AddPacket(p); });
        broker.Subscribe(0, "AprsStation", (_, _, data) => { if (data is AprsStationSummary st) AddStation(st); });
        broker.Subscribe(0, "Settings", (_, _, data) => { if (data is RadioSettingsSummary s) RadioSettings = s; });
        broker.Subscribe(0, "BssSettings", (_, _, data) => { if (data is RadioBssSettings b) Bss = b; });
        broker.Subscribe(0, "Stations", (_, _, data) => { if (data is System.Collections.Generic.List<StationInfoClass> list) ApplyContacts(list); });

        Refresh();
        LoadContacts();
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
                OnPropertyChanged(nameof(CanTransmit));
            }
        }
    }

    private int frameCount;
    public int FrameCount { get => frameCount; private set => SetField(ref frameCount, value); }

    private bool voiceRxActive;
    public bool VoiceRxActive
    {
        get => voiceRxActive;
        private set { if (SetField(ref voiceRxActive, value)) OnPropertyChanged(nameof(CanTransmit)); }
    }

    private bool transmitting;
    public bool Transmitting { get => transmitting; private set => SetField(ref transmitting, value); }

    /// <summary>PTT is allowed only while connected with the audio channel open.</summary>
    public bool CanTransmit => Connected && VoiceRxActive;

    public bool CanConnect => !Connected && SelectedRadio != null;
    public bool CanDisconnect => Connected || controller != null;

    // --- Telemetry published by RadioController (parsed via Core types) ---

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

    private int batteryPercent;
    public int BatteryPercent { get => batteryPercent; private set => SetField(ref batteryPercent, value); }

    private string deviceInfoText = "—";
    public string DeviceInfoText { get => deviceInfoText; private set => SetField(ref deviceInfoText, value); }

    private RadioSettingsSummary? radioSettings;
    public RadioSettingsSummary? RadioSettings
    {
        get => radioSettings;
        private set { if (SetField(ref radioSettings, value)) OnPropertyChanged(nameof(HasRadioSettings)); }
    }
    public bool HasRadioSettings => RadioSettings != null;

    private RadioBssSettings? bss;
    public RadioBssSettings? Bss
    {
        get => bss;
        private set { if (SetField(ref bss, value)) OnPropertyChanged(nameof(HasBss)); }
    }
    public bool HasBss => Bss != null;

    // --- Contacts editor fields ---
    private StationInfoClass? selectedContact;
    public StationInfoClass? SelectedContact
    {
        get => selectedContact;
        set
        {
            if (!SetField(ref selectedContact, value) || value == null) return;
            EditCallsign = value.Callsign ?? "";
            EditName = value.Name ?? "";
            EditDescription = value.Description ?? "";
            EditType = value.StationType;
        }
    }

    private string editCallsign = "";
    public string EditCallsign { get => editCallsign; set => SetField(ref editCallsign, value); }
    private string editName = "";
    public string EditName { get => editName; set => SetField(ref editName, value); }
    private string editDescription = "";
    public string EditDescription { get => editDescription; set => SetField(ref editDescription, value); }
    private StationInfoClass.StationTypes editType = StationInfoClass.StationTypes.Generic;
    public StationInfoClass.StationTypes EditType { get => editType; set => SetField(ref editType, value); }

    private void LoadContacts()
    {
        Task.Run(() =>
        {
            var list = DataBroker.GetValue<System.Collections.Generic.List<StationInfoClass>?>(0, "Stations");
            if (list != null) dispatcher.Post(() => ApplyContacts(list));
        });
    }

    private void ApplyContacts(System.Collections.Generic.List<StationInfoClass> list)
    {
        loadingContacts = true;
        Contacts.Clear();
        foreach (var s in list) Contacts.Add(s);
        loadingContacts = false;
    }

    private void SaveContacts()
    {
        if (loadingContacts) return;
        DataBroker.Dispatch(0, "Stations", Contacts.ToList(), store: true);   // persists + re-publishes
    }

    /// <summary>Adds a new contact, or updates the existing one with the same callsign.</summary>
    public void AddOrUpdateContact()
    {
        string call = (EditCallsign ?? "").Trim().ToUpperInvariant();
        if (call.Length == 0) { AppendLog("Contact needs a callsign."); return; }

        var existing = Contacts.FirstOrDefault(c => string.Equals(c.Callsign, call, StringComparison.OrdinalIgnoreCase));
        var station = existing ?? new StationInfoClass();
        station.Callsign = call;
        station.Name = EditName ?? "";
        station.Description = EditDescription ?? "";
        station.StationType = EditType;
        if (existing == null) Contacts.Add(station);
        SaveContacts();
        AppendLog($"Saved contact {call}.");
    }

    public void RemoveSelectedContact()
    {
        if (SelectedContact == null) return;
        Contacts.Remove(SelectedContact);
        SelectedContact = null;
        SaveContacts();
    }

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
        Channels.Clear();
        Packets.Clear();
        Stations.Clear();
        Status = $"Connecting to {radio.Name} ({radio.Address})...";
        AppendLog(Status);

        transport = new RadioBluetoothLinux(radio.Address, logger,
            reason => dispatcher.Post(() => OnDisconnected(reason)));

        transport.OnConnected += () => dispatcher.Post(() =>
        {
            Connected = true;
            Status = $"Connected to {radio.Name}";
            AppendLog("Connected — querying device info, status and battery.");
            StartVoiceRx(radio.Address);   // best-effort: hear the radio on the PC speaker
        });

        transport.ReceivedData += (_, _, value) => dispatcher.Post(() =>
        {
            FrameCount++;
            AppendLog($"<<< [{value.Length}B] {BytesToHex(value)}");
        });

        // The controller owns the command/response protocol and connects the transport.
        controller = new RadioController(transport, 0, logger);
        controller.Start();
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
    }

    public void Disconnect()
    {
        var c = controller;
        if (c == null) return;
        AppendLog("Disconnecting...");
        Task.Run(() => c.Stop());   // triggers the transport's onDisconnected -> OnDisconnected()
    }

    // Open the radio's voice-audio RFCOMM channel and decode it to the speaker.
    // Runs off the UI thread (SDP + a second RFCOMM connect take a moment) and is
    // best-effort — failure never affects radio command/control.
    private void StartVoiceRx(string mac)
    {
        Task.Run(() =>
        {
            try
            {
                var playback = new PortAudioPlayback { Volume = Settings.OutputVolume };
                playback.SetDevice(Settings.OutputDeviceId);     // honor the Settings output pick
                var rx = new RadioVoiceReceiver(playback);
                rx.Start();
                var ch = new RadioAudioChannelLinux(mac, logger);
                ch.DataReceived += (b, n) => rx.OnAudioBytes(b, n);
                if (ch.Connect())
                {
                    audioChannel = ch; voiceReceiver = rx; voicePlayback = playback;
                    dispatcher.Post(() => { VoiceRxActive = true; AppendLog("Voice RX active (audio channel open)."); });
                }
                else
                {
                    rx.Stop(); playback.Dispose();
                    dispatcher.Post(() => AppendLog("Voice RX unavailable (audio channel not found)."));
                }
            }
            catch (Exception ex) { logger.Debug("Voice RX start failed: " + ex.Message); }
        });
    }

    private void StopVoiceRx()
    {
        StopTransmit();
        try { audioChannel?.Disconnect(); } catch (Exception) { }
        try { voiceReceiver?.Stop(); } catch (Exception) { }
        try { voicePlayback?.Dispose(); } catch (Exception) { }
        audioChannel = null; voiceReceiver = null; voicePlayback = null;
        VoiceRxActive = false;
    }

    // --- PTT / transmit (ON-AIR). Operator-triggered only; never automatic. ---

    /// <summary>Keys the radio and transmits mic audio. Call on PTT press.</summary>
    public void StartTransmit()
    {
        if (!CanTransmit || Transmitting || audioChannel == null) return;
        try
        {
            mic = new PortAudioCapture();
            mic.SetDevice(Settings.InputDeviceId);              // honor the Settings input pick
            voiceTransmitter = new RadioVoiceTransmitter(mic, data => audioChannel?.Send(data)) { Gain = Settings.MicGain };
            if (voiceTransmitter.Start())
            {
                Transmitting = true;
                AppendLog("PTT down — TRANSMITTING (on the air).");
            }
            else { CleanupTx(); AppendLog("PTT failed (could not open microphone)."); }
        }
        catch (Exception ex) { CleanupTx(); logger.Debug("StartTransmit failed: " + ex.Message); }
    }

    /// <summary>Un-keys the radio. Call on PTT release.</summary>
    public void StopTransmit()
    {
        if (voiceTransmitter == null) { Transmitting = false; return; }
        try { voiceTransmitter.Stop(); } catch (Exception) { }
        CleanupTx();
        AppendLog("PTT up — stopped transmitting.");
    }

    private void CleanupTx()
    {
        try { mic?.Dispose(); } catch (Exception) { }
        voiceTransmitter = null; mic = null;
        Transmitting = false;
    }

    private void OnDisconnected(string reason)
    {
        StopVoiceRx();
        try { controller?.Dispose(); } catch (Exception) { }
        controller = null;
        transport = null;
        Connected = false;
        HasStatus = false;
        BatteryPercent = 0;
        DeviceInfoText = "—";
        Channels.Clear();
        Packets.Clear();
        Stations.Clear();
        RadioSettings = null;
        Bss = null;
        Status = "Disconnected: " + reason;
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
    }

    private void ApplyHtStatus(RadioHtStatus s)
    {
        HasStatus = true;
        PowerText = s.is_power_on ? "On" : "Off";
        TxRxState = s.is_in_tx ? "Transmitting" : s.is_in_rx ? "Receiving" : "Idle";
        SquelchText = s.is_sq ? "Open" : "Closed";
        ChannelId = s.channel_id;
        Rssi = s.rssi;
        Region = s.curr_region;
        GpsText = s.is_gps_locked ? "Locked" : "No lock";
    }

    private void ApplyDeviceInfo(RadioDeviceSummary d) =>
        DeviceInfoText = $"Vendor {d.VendorId} · Product {d.ProductId} · HW v{d.HardwareVersion} · FW v{d.SoftwareVersion} · {d.ChannelCount} ch";

    private void ApplyChannel(RadioChannelSummary c)
    {
        // Insert/replace keeping the list ordered by channel id.
        for (int i = 0; i < Channels.Count; i++)
        {
            if (Channels[i].ChannelId == c.ChannelId) { Channels[i] = c; return; }
            if (Channels[i].ChannelId > c.ChannelId) { Channels.Insert(i, c); return; }
        }
        Channels.Add(c);
    }

    private void AddPacket(ReceivedPacketSummary p)
    {
        Packets.Insert(0, p);                       // newest first
        while (Packets.Count > 500) Packets.RemoveAt(Packets.Count - 1);
    }

    private void AddStation(AprsStationSummary s)
    {
        // One entry per callsign; update in place when a station's position changes.
        for (int i = 0; i < Stations.Count; i++)
            if (Stations[i].Callsign == s.Callsign) { Stations[i] = s; return; }
        Stations.Add(s);
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
