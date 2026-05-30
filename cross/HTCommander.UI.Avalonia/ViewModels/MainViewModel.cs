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
    // Channel builder: editable rows imported from CSV / read from the radio.
    public ObservableCollection<EditableChannel> BuilderChannels { get; } = new();
    // Full channel records as last read from the radio (slot -> channel), for "Load from radio".
    private readonly System.Collections.Generic.SortedDictionary<int, RadioChannelInfo> radioChannels = new();
    public ObservableCollection<ReceivedPacketSummary> Packets { get; } = new();
    public ObservableCollection<AprsStationSummary> Stations { get; } = new();

    public ObservableCollection<string> TerminalLog { get; } = new();

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
        broker.Subscribe(0, "ChannelInfo", (_, _, data) => { if (data is RadioChannelInfo c) { radioChannels[c.channel_id] = c; UpdateSlotFromChannel(c); } });
        broker.Subscribe(0, "PacketReceived", (_, _, data) => { if (data is ReceivedPacketSummary p) AddPacket(p); });
        broker.Subscribe(0, "AprsStation", (_, _, data) => { if (data is AprsStationSummary st) AddStation(st); });
        broker.Subscribe(0, "Settings", (_, _, data) => { if (data is RadioSettingsSummary s) RadioSettings = s; });
        broker.Subscribe(0, "BssSettings", (_, _, data) =>
        {
            if (data is not RadioBssSettings b) return;
            Bss = b;
            if (string.IsNullOrEmpty(TerminalMyCall) && !string.IsNullOrEmpty(b.AprsCallsign))
                TerminalMyCall = $"{b.AprsCallsign}-{b.AprsSsid}";   // prefill source callsign
        });
        broker.Subscribe(0, "Stations", (_, _, data) => { if (data is System.Collections.Generic.List<StationInfoClass> list) ApplyContacts(list); });

        // Winlink mail: reflect store changes (e.g. mail received during a sync) and
        // surface the client's state messages. WinlinkStateMessage/Busy ride device 1.
        broker.Subscribe(1, "WinlinkStateMessage", (_, _, data) => { if (data is string s) dispatcher.Post(() => WinlinkStatus = s); });
        var store = DataBroker.GetDataHandler<IMailStore>("MailStore");
        if (store != null) store.MailsChanged += (_, _) => dispatcher.Post(RefreshMails);

        // BBS: live traffic + control messages + station stats (device-agnostic).
        broker.Subscribe(DataBroker.AllDevices, "BbsTraffic", (_, _, data) => dispatcher.Post(() => AddBbsTraffic(data, false)));
        broker.Subscribe(DataBroker.AllDevices, "BbsControlMessage", (_, _, data) => dispatcher.Post(() => AddBbsControl(data)));
        broker.Subscribe(DataBroker.AllDevices, "BbsError", (_, _, data) => dispatcher.Post(() => AddBbsControl(data)));
        broker.Subscribe(1, "BbsMergedStats", (_, _, data) => dispatcher.Post(() => ApplyBbsStats(data)));
        // BbsHandler reports start/stop failures on these — reflect them and revert state.
        broker.Subscribe(1, "BbsCreateFailed", (_, _, data) => dispatcher.Post(() => { BbsActive = false; AppendBbs("BBS failed to start: " + (data as BbsErrorData)?.Error); }));
        broker.Subscribe(1, "BbsRemoveFailed", (_, _, data) => dispatcher.Post(() => AppendBbs("BBS stop failed: " + (data as BbsErrorData)?.Error)));

        Refresh();
        LoadContacts();
        RefreshMails();
        LoadIdentity();
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
                OnPropertyChanged(nameof(CanSendData));
                OnPropertyChanged(nameof(CanWriteChannels));
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

    /// <summary>True once a lawful callsign is set (identity required to transmit).</summary>
    public bool HasValidCallsign => (MyCallsign ?? "").Trim().Length >= 3;

    /// <summary>Operator transmit gate: a callsign and the Allow-Transmit switch (your
    /// license) are required before any on-air transmission. Mirrors the Windows gate.</summary>
    public bool TxAuthorized => AllowTransmit && HasValidCallsign;

    /// <summary>PTT is allowed only while connected, audio channel open, and TX authorized.</summary>
    public bool CanTransmit => Connected && VoiceRxActive && TxAuthorized;

    /// <summary>Data/packet TX uses the command channel — allowed when connected + authorized.</summary>
    public bool CanSendData => Connected && TxAuthorized;

    // ---- Station identity & settings (DataBroker device 0; shared with Core: APRS,
    // Winlink, BBS, AX25Session all read CallSign/StationId/WinlinkPassword/AllowTransmit) ----
    private bool loadingIdentity;
    private string myCallsign = "";
    public string MyCallsign
    {
        get => myCallsign;
        set
        {
            value = (value ?? "").ToUpperInvariant();
            if (!SetField(ref myCallsign, value)) return;
            if (!loadingIdentity) DataBroker.Dispatch(0, "CallSign", value, store: true);
            OnPropertyChanged(nameof(HasValidCallsign));
            OnPropertyChanged(nameof(TxAuthorized));
            OnPropertyChanged(nameof(CanTransmit));
            OnPropertyChanged(nameof(CanSendData));
        }
    }

    public Array StationIdOptions { get; } = new[] { 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15 };
    private int myStationId;
    public int MyStationId
    {
        get => myStationId;
        set { if (SetField(ref myStationId, value) && !loadingIdentity) DataBroker.Dispatch(0, "StationId", value, store: true); }
    }

    private bool allowTransmit;
    public bool AllowTransmit
    {
        get => allowTransmit;
        set
        {
            if (!SetField(ref allowTransmit, value)) return;
            if (!loadingIdentity) DataBroker.Dispatch(0, "AllowTransmit", value, store: true);
            OnPropertyChanged(nameof(TxAuthorized));
            OnPropertyChanged(nameof(CanTransmit));
            OnPropertyChanged(nameof(CanSendData));
        }
    }

    private string winlinkPassword = "";
    public string WinlinkPassword
    {
        get => winlinkPassword;
        set { if (SetField(ref winlinkPassword, value) && !loadingIdentity) DataBroker.Dispatch(0, "WinlinkPassword", value, store: true); }
    }

    private void LoadIdentity()
    {
        loadingIdentity = true;
        MyCallsign = DataBroker.GetValue<string>(0, "CallSign", "") ?? "";
        MyStationId = DataBroker.GetValue<int>(0, "StationId", 0);
        AllowTransmit = DataBroker.GetValue<bool>(0, "AllowTransmit", false);
        WinlinkPassword = DataBroker.GetValue<string>(0, "WinlinkPassword", "") ?? "";
        loadingIdentity = false;
    }

    private string terminalTo = "";
    public string TerminalTo { get => terminalTo; set => SetField(ref terminalTo, value); }
    private string terminalMyCall = "";
    public string TerminalMyCall { get => terminalMyCall; set => SetField(ref terminalMyCall, value); }
    private string terminalText = "";
    public string TerminalText { get => terminalText; set => SetField(ref terminalText, value); }

    /// <summary>Sends a text AX.25 UI frame (MYCALL &gt; TO : text) over the radio's TNC. ON-AIR.</summary>
    public void SendTerminalMessage()
    {
        if (!Connected || controller == null) return;
        string to = (TerminalTo ?? "").Trim();
        string mine = (TerminalMyCall ?? "").Trim();
        string text = TerminalText ?? "";
        if (to.Length == 0 || mine.Length == 0 || text.Length == 0) { AppendLog("Terminal: need To, My callsign and text."); return; }

        var dest = AX25Address.GetAddress(to);
        var src = AX25Address.GetAddress(mine);
        if (dest == null || src == null) { AppendLog("Terminal: invalid callsign."); return; }

        var pkt = new AX25Packet(new System.Collections.Generic.List<AX25Address> { dest, src }, text, DateTime.Now);
        controller.SendPacket(pkt);
        AddTerminalLine($"> {mine} > {to}: {text}");
        TerminalText = "";
    }

    private void AddTerminalLine(string line)
    {
        TerminalLog.Add(line);
        while (TerminalLog.Count > 500) TerminalLog.RemoveAt(0);
    }

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
            EditChannel = value.Channel ?? "";
            EditAprsRoute = value.APRSRoute ?? "";
            EditAx25Destination = value.AX25Destination ?? "";
            EditWaitForConnection = value.WaitForConnection;
            EditAuthPassword = value.AuthPassword ?? "";
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

    // Connection setup for the station (channel/frequency to reach it on, APRS path, etc.)
    private string editChannel = "";
    public string EditChannel { get => editChannel; set => SetField(ref editChannel, value); }
    private string editAprsRoute = "";
    public string EditAprsRoute { get => editAprsRoute; set => SetField(ref editAprsRoute, value); }
    private string editAx25Destination = "";
    public string EditAx25Destination { get => editAx25Destination; set => SetField(ref editAx25Destination, value); }
    private bool editWaitForConnection;
    public bool EditWaitForConnection { get => editWaitForConnection; set => SetField(ref editWaitForConnection, value); }
    private string editAuthPassword = "";
    public string EditAuthPassword { get => editAuthPassword; set => SetField(ref editAuthPassword, value); }

    /// <summary>Channel names from the radio, for the contact's "Channel" dropdown.</summary>
    public ObservableCollection<string> ChannelNames { get; } = new();

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
        station.Channel = EditChannel ?? "";
        station.APRSRoute = EditAprsRoute ?? "";
        station.AX25Destination = EditAx25Destination ?? "";
        station.WaitForConnection = EditWaitForConnection;
        station.AuthPassword = EditAuthPassword ?? "";
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
        TerminalLog.Clear();
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
        currentChannelId = s.curr_ch_id;
        RefreshActiveSlots();
    }

    private void ApplyDeviceInfo(RadioDeviceSummary d)
    {
        DeviceInfoText = $"Vendor {d.VendorId} · Product {d.ProductId} · HW v{d.HardwareVersion} · FW v{d.SoftwareVersion} · {d.ChannelCount} ch · {d.RegionCount} bank(s)";
        channelCount = Math.Max(1, d.ChannelCount);
        EnsureSlots();
        RegionCount = Math.Max(1, d.RegionCount);
        loadingBanks = true;
        Banks.Clear();
        for (int i = 0; i < RegionCount; i++) Banks.Add(i);
        if (selectedBank >= RegionCount) selectedBank = 0;
        else selectedBank = Math.Max(0, Region);   // default to the radio's current bank
        loadingBanks = false;
        OnPropertyChanged(nameof(SelectedBank));
    }

    private void ApplyChannel(RadioChannelSummary c)
    {
        // Insert/replace keeping the list ordered by channel id.
        for (int i = 0; i < Channels.Count; i++)
        {
            if (Channels[i].ChannelId == c.ChannelId) { Channels[i] = c; UpdateChannelNames(); return; }
            if (Channels[i].ChannelId > c.ChannelId) { Channels.Insert(i, c); UpdateChannelNames(); return; }
        }
        Channels.Add(c);
        UpdateChannelNames();
    }

    private void UpdateChannelNames()
    {
        foreach (var ch in Channels)
            if (!string.IsNullOrWhiteSpace(ch.Name) && !ChannelNames.Contains(ch.Name))
                ChannelNames.Add(ch.Name);
    }

    // ---- Channel builder ---------------------------------------------------

    private string builderStatus = "";
    public string BuilderStatus { get => builderStatus; set => SetField(ref builderStatus, value); }

    public bool CanWriteChannels => Connected && controller != null;

    // Channel banks (radio "regions"/zones). Imported channels are written into the
    // selected bank; switching banks re-reads that bank's channels from the radio.
    public ObservableCollection<int> Banks { get; } = new();
    private int regionCount = 1;
    public int RegionCount { get => regionCount; private set { if (SetField(ref regionCount, value)) OnPropertyChanged(nameof(HasBanks)); } }
    public bool HasBanks => regionCount > 1;

    private bool loadingBanks;
    private int selectedBank;
    public int SelectedBank
    {
        get => selectedBank;
        set
        {
            if (!SetField(ref selectedBank, value) || loadingBanks) return;
            if (controller != null && Connected && value >= 0)
            {
                controller.SetRegion(value);          // switch the radio to this bank
                radioChannels.Clear();
                foreach (var s in Slots) { s.Name = ""; s.RxMHz = 0; }   // clear while the new bank loads
                controller.RefreshChannels();         // re-read this bank's channels
                BuilderStatus = $"Switched to bank {value}; reading its channels…";
            }
        }
    }

    // ---- Channel slot grid (radio memory tiles, drag-to-program) ----
    public ObservableCollection<ChannelSlot> Slots { get; } = new();
    private int channelCount = 32;
    private int currentChannelId = -1;

    private void EnsureSlots()
    {
        while (Slots.Count < channelCount) Slots.Add(new ChannelSlot(Slots.Count));
        while (Slots.Count > channelCount && Slots.Count > 0) Slots.RemoveAt(Slots.Count - 1);
    }

    private void UpdateSlotFromChannel(RadioChannelInfo c)
    {
        EnsureSlots();
        if (c.channel_id < 0 || c.channel_id >= Slots.Count) return;
        var s = Slots[c.channel_id];
        bool programmed = c.rx_freq != 0 || !string.IsNullOrEmpty(c.name_str);
        s.Name = programmed ? (c.name_str ?? "") : "";
        s.RxMHz = c.rx_freq / 1_000_000.0;
    }

    private void RefreshActiveSlots()
    {
        for (int i = 0; i < Slots.Count; i++) Slots[i].IsActive = (i == currentChannelId);
    }

    /// <summary>Program a single memory slot from an imported channel card (drag-and-drop target).</summary>
    public void ProgramSlot(int slotId, EditableChannel? ec)
    {
        if (ec == null || slotId < 0) return;
        if (controller == null || !Connected) { BuilderStatus = "Connect to a radio first."; return; }
        var info = ec.ToRadioChannelInfo(slotId);
        if (!FreqInRange(info.rx_freq) || !FreqInRange(info.tx_freq)) { BuilderStatus = $"Slot {slotId}: frequency out of range."; return; }
        if (HasBanks) controller.SetRegion(SelectedBank);
        controller.WriteChannel(info);
        EnsureSlots();
        if (slotId < Slots.Count) { Slots[slotId].Name = info.name_str; Slots[slotId].RxMHz = info.rx_freq / 1_000_000.0; }
        string where = HasBanks ? $" in bank {SelectedBank}" : "";
        BuilderStatus = $"Programmed slot {slotId}{where}: {info.name_str}";
        AppendLog($"Channel builder: programmed slot {slotId}{where} -> {info.name_str}");
    }

    /// <summary>Copy the channels last read from the radio into the editable builder.</summary>
    public void LoadChannelsFromRadio()
    {
        if (radioChannels.Count == 0) { BuilderStatus = "No channels read from the radio yet (connect first)."; return; }
        BuilderChannels.Clear();
        foreach (var kv in radioChannels)
            if (kv.Value.rx_freq != 0 || kv.Value.tx_freq != 0)
                BuilderChannels.Add(new EditableChannel(kv.Value));
        BuilderStatus = $"Loaded {BuilderChannels.Count} channel(s) from the radio.";
    }

    /// <summary>Import channels from a CSV file (CHIRP / native / RepeaterBook formats).</summary>
    public void ImportChannelsFromCsv(string path)
    {
        RadioChannelInfo[]? parsed = null;
        try { parsed = ImportUtils.ParseChannelsFromFile(path); }
        catch (Exception ex) { BuilderStatus = "Import failed: " + ex.Message; return; }
        if (parsed == null || parsed.Length == 0) { BuilderStatus = "No channels found (unrecognized CSV format)."; return; }

        int startSlot = BuilderChannels.Count;
        foreach (var c in parsed)
        {
            var ec = new EditableChannel(c) { ChannelId = startSlot++ };
            BuilderChannels.Add(ec);
        }
        BuilderStatus = $"Imported {parsed.Length} channel(s) from {System.IO.Path.GetFileName(path)}.";
    }

    /// <summary>Export the builder rows to a CSV file (native or CHIRP format).</summary>
    public void ExportChannelsToCsv(string path, bool chirp)
    {
        var arr = new RadioChannelInfo[BuilderChannels.Count];
        for (int i = 0; i < BuilderChannels.Count; i++) arr[i] = BuilderChannels[i].ToRadioChannelInfo(i);
        string csv = chirp ? ImportUtils.ExportToChirpFormat(arr) : ImportUtils.ExportToNativeFormat(arr);
        try { System.IO.File.WriteAllText(path, csv); BuilderStatus = $"Exported {arr.Length} channel(s) to {System.IO.Path.GetFileName(path)}."; }
        catch (Exception ex) { BuilderStatus = "Export failed: " + ex.Message; }
    }

    public void AddBuilderChannel()
    {
        BuilderChannels.Add(new EditableChannel { ChannelId = BuilderChannels.Count, Name = "NEW", Mode = "FM", Power = "H" });
    }

    public void RemoveBuilderChannel(EditableChannel? ch)
    {
        if (ch != null) BuilderChannels.Remove(ch);
    }

    /// <summary>
    /// Write all builder rows to the radio's memory (WRITE_RF_CH), one per slot in row
    /// order. This reconfigures the radio; it is an explicit operator action.
    /// </summary>
    public void WriteChannelsToRadio()
    {
        if (controller == null || !Connected) { BuilderStatus = "Connect to a radio first."; return; }
        if (HasBanks) controller.SetRegion(SelectedBank);   // ensure writes land in the chosen bank
        int written = 0, skipped = 0;
        for (int i = 0; i < BuilderChannels.Count; i++)
        {
            var info = BuilderChannels[i].ToRadioChannelInfo(i);
            if (info.rx_freq == 0 && info.tx_freq == 0) continue;   // skip empty rows
            // Guard the radio against mis-typed / mis-parsed rows: only write plausible
            // frequencies (1 MHz–1300 MHz covers the HT's bands with margin).
            if (!FreqInRange(info.rx_freq) || !FreqInRange(info.tx_freq)) { skipped++; continue; }
            controller.WriteChannel(info);
            written++;
        }
        string where = HasBanks ? $" to bank {SelectedBank}" : "";
        BuilderStatus = skipped > 0
            ? $"Wrote {written} channel(s){where}; skipped {skipped} with out-of-range frequency."
            : $"Wrote {written} channel(s){where}.";
        AppendLog($"Channel builder: wrote {written} channel(s){where}" + (skipped > 0 ? $", skipped {skipped} invalid." : "."));
    }

    private static bool FreqInRange(int hz) => hz >= 1_000_000 && hz <= 1_300_000_000;

    public void NoteScreenshot(string path) => AppendLog($"Screenshot saved: {path}");

    // ---- Winlink mail -------------------------------------------------------

    public string[] Mailboxes { get; } = { "Inbox", "Outbox", "Draft", "Sent", "Archive", "Trash" };

    private string selectedMailbox = "Inbox";
    public string SelectedMailbox
    {
        get => selectedMailbox;
        set { if (SetField(ref selectedMailbox, value)) RefreshMails(); }
    }

    public ObservableCollection<WinLinkMail> Mails { get; } = new();

    private WinLinkMail? selectedMail;
    public WinLinkMail? SelectedMail
    {
        get => selectedMail;
        set { if (SetField(ref selectedMail, value)) { OnPropertyChanged(nameof(MailPreview)); OnPropertyChanged(nameof(HasSelectedMail)); } }
    }
    public bool HasSelectedMail => selectedMail != null;

    public string MailPreview => selectedMail == null ? "" :
        $"From: {selectedMail.From}\nTo: {selectedMail.To}\n" +
        (string.IsNullOrEmpty(selectedMail.Cc) ? "" : $"Cc: {selectedMail.Cc}\n") +
        $"Date: {selectedMail.DateTime:yyyy-MM-dd HH:mm}\nSubject: {selectedMail.Subject}\n" +
        new string('-', 40) + "\n" + selectedMail.Body;

    private string composeTo = "", composeSubject = "", composeBody = "";
    public string ComposeTo { get => composeTo; set => SetField(ref composeTo, value); }
    public string ComposeSubject { get => composeSubject; set => SetField(ref composeSubject, value); }
    public string ComposeBody { get => composeBody; set => SetField(ref composeBody, value); }

    private string winlinkStatus = "Idle.";
    public string WinlinkStatus { get => winlinkStatus; private set => SetField(ref winlinkStatus, value); }

    private IMailStore? MailStore => DataBroker.GetDataHandler<IMailStore>("MailStore");

    private void RefreshMails()
    {
        var store = MailStore;
        if (store == null) return;
        Mails.Clear();
        foreach (var m in store.GetAllMails())
            if (string.Equals(m.Mailbox, SelectedMailbox, StringComparison.OrdinalIgnoreCase))
                Mails.Add(m);
        OnPropertyChanged(nameof(MailCountText));
    }

    public string MailCountText => $"{SelectedMailbox} ({Mails.Count})";

    /// <summary>Compose a new message and queue it in the Outbox for the next sync.</summary>
    public void ComposeSaveToOutbox()
    {
        var store = MailStore;
        if (store == null) { WinlinkStatus = "Mail store unavailable."; return; }
        string to = (ComposeTo ?? "").Trim();
        if (to.Length == 0) { WinlinkStatus = "Compose: 'To' is required."; return; }

        string mine = TerminalMyCall ?? DataBroker.GetValue<string>(0, "CallSign", "") ?? "";
        var mail = new WinLinkMail
        {
            MID = WinLinkMail.GenerateMID(),
            DateTime = DateTime.Now,
            From = mine,
            To = to,
            Subject = ComposeSubject ?? "",
            Body = ComposeBody ?? "",
            Mailbox = "Outbox",
            Flags = 0,
        };
        store.AddMail(mail);
        ComposeTo = ComposeSubject = ComposeBody = "";
        WinlinkStatus = $"Saved to Outbox: {mail.Subject}";
        SelectedMailbox = "Outbox";
        RefreshMails();
    }

    public void DeleteSelectedMail()
    {
        var store = MailStore;
        if (store == null || selectedMail == null) return;
        if (string.Equals(selectedMail.Mailbox, "Trash", StringComparison.OrdinalIgnoreCase))
        {
            store.DeleteMail(selectedMail.MID);                 // permanent from Trash
            WinlinkStatus = "Deleted.";
        }
        else
        {
            selectedMail.Mailbox = "Trash";                     // soft-delete elsewhere
            store.UpdateMail(selectedMail);
            WinlinkStatus = "Moved to Trash.";
        }
        RefreshMails();
    }

    /// <summary>Start a Winlink session over the internet (Telnet CMS) to send Outbox
    /// mail and receive new mail. Needs network + a reachable CMS.</summary>
    public void SyncWinlinkInternet()
    {
        WinlinkStatus = "Connecting to Winlink CMS (internet)…";
        DataBroker.Dispatch(1, "WinlinkSync", new { Server = "server.winlink.org", Port = 8772, UseTls = false }, store: false);
    }

    public void DisconnectWinlink()
    {
        DataBroker.Dispatch(1, "WinlinkDisconnect", null, store: false);
        WinlinkStatus = "Disconnect requested.";
    }

    // ---- BBS (connected-mode mail drop / bulletin board) --------------------

    private const int BbsRadioDeviceId = 0;        // matches the Avalonia RadioController id

    public ObservableCollection<string> BbsLog { get; } = new();
    public ObservableCollection<MergedStationStats> BbsStations { get; } = new();

    private bool bbsActive;
    public bool BbsActive { get => bbsActive; private set { if (SetField(ref bbsActive, value)) OnPropertyChanged(nameof(BbsToggleText)); } }
    public string BbsToggleText => bbsActive ? "Stop BBS" : "Start BBS";

    private static string PropStr(object data, string name) => data?.GetType().GetProperty(name)?.GetValue(data)?.ToString() ?? "";

    private void AddBbsTraffic(object data, bool _)
    {
        bool outgoing = string.Equals(PropStr(data, "Outgoing"), "True", StringComparison.OrdinalIgnoreCase);
        string call = PropStr(data, "Callsign");
        string msg = PropStr(data, "Message");
        AppendBbs($"{call} {(outgoing ? "<" : ">")} {msg}");
    }

    private void AddBbsControl(object data) => AppendBbs("• " + PropStr(data, "Message") + PropStr(data, "Error"));

    private void AppendBbs(string line)
    {
        BbsLog.Add(line);
        while (BbsLog.Count > 500) BbsLog.RemoveAt(0);
    }

    private void ApplyBbsStats(object? data)
    {
        if (data is not System.Collections.Generic.List<MergedStationStats> list) return;
        BbsStations.Clear();
        foreach (var s in list) BbsStations.Add(s);
    }

    public void ToggleBbs()
    {
        if (bbsActive) { StopBbs(); return; }
        if (!Connected) { AppendBbs("Connect to a radio before starting the BBS."); return; }

        int region = 0, channel = 0;
        var ht = DataBroker.GetValue<RadioHtStatus>(0, "HtStatus", null!);
        if (ht != null) { region = ht.curr_region; channel = ht.curr_ch_id; }

        DataBroker.Dispatch(1, "CreateBbs",
            new CreateBbsData { RadioDeviceId = BbsRadioDeviceId, ChannelId = channel, RegionId = region },
            store: false);
        BbsActive = true;
        AppendBbs($"BBS started on channel {channel}, region {region}. Waiting for stations to connect…");
    }

    public void StopBbs()
    {
        DataBroker.Dispatch(1, "RemoveBbs", new RemoveBbsData { RadioDeviceId = BbsRadioDeviceId }, store: false);
        BbsActive = false;
        AppendBbs("BBS stopped.");
    }

    public void ClearBbsStats()
    {
        DataBroker.Dispatch(1, "BbsClearAllStats", null, store: false);
        BbsStations.Clear();
    }

    private void AddPacket(ReceivedPacketSummary p)
    {
        Packets.Insert(0, p);                       // newest first
        while (Packets.Count > 500) Packets.RemoveAt(Packets.Count - 1);
        AddTerminalLine($"< {p.Source}: {p.Info}"); // mirror into the terminal log
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
