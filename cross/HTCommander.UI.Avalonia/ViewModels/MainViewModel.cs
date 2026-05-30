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
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using HTCommander;                       // RadioController, RadioHtStatus, RadioDeviceSummary, DataBrokerClient (Core)
using HTCommander.Core.Abstractions;
using HTCommander.Core.Abstractions.Audio;
using HTCommander.Core.Audio;          // WavFileReader / WavFileWriter
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
    private string? connectedMac;   // set on connect; used to open voice audio on demand
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
        broker.Subscribe(0, "Position", (_, _, data) => { if (data is RadioPositionInfo p) MyPosition = p; });
        broker.Subscribe(1, "GpsData", (_, _, data) => SerialPosition = data as HTCommander.Gps.GpsData);
        broker.Subscribe(0, "Settings", (_, _, data) => { if (data is RadioSettingsSummary s) RadioSettings = s; });
        broker.Subscribe(0, "BssSettings", (_, _, data) =>
        {
            if (data is not RadioBssSettings b) return;
            Bss = b;
            SeedBssEditor(b);                                        // refresh the editable beacon/ident fields
            if (string.IsNullOrEmpty(TerminalMyCall) && !string.IsNullOrEmpty(b.AprsCallsign))
                TerminalMyCall = $"{b.AprsCallsign}-{b.AprsSsid}";   // prefill source callsign
            // Enforce the chosen beacon method: if the radio's built-in beacon is on but
            // we're not in Radio mode, turn it off (stops a stale radio beacon firing on
            // the tuned channel before the operator picks a method).
            if (Connected && !IsRadioBeacon && b.ShouldShareLocation)
            {
                AppendLog("Beacon method is not 'Radio' — disabling the radio's built-in beacon.");
                WriteBssSettings();
            }
        });
        broker.Subscribe(0, "Stations", (_, _, data) => { if (data is System.Collections.Generic.List<StationInfoClass> list) ApplyContacts(list); });

        // Winlink mail: reflect store changes (e.g. mail received during a sync) and
        // surface the client's state messages. WinlinkStateMessage/Busy ride device 1.
        broker.Subscribe(1, "WinlinkStateMessage", (_, _, data) => { if (data is string s) dispatcher.Post(() => { WinlinkStatus = s; AppendWinlinkLog(s); }); });
        broker.Subscribe(1, "AprsFrame", (_, _, data) => dispatcher.Post(() => OnAprsFrame(data)));
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

        ComposeAttachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasComposeAttachments));

        Refresh();
        LoadContacts();
        SelectedFolder = Folders.FirstOrDefault();   // Inbox; triggers the first RefreshMails
        LoadIdentity();
        LoadAprsRoutes();
        LoadClips();
        LoadSoftModemMode();
        LoadFixedPosition();
        autoLoadAllBanks = DataBroker.GetValue<bool>(0, "AutoLoadAllBanks", true);
        LoadAprsFiSettings();
        LoadAprsChannelName();
        LoadBeaconMode();
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
                OnPropertyChanged(nameof(CanSendAprs));
            OnPropertyChanged(nameof(CanBeacon));
                OnPropertyChanged(nameof(CanWriteChannels));
                OnPropertyChanged(nameof(CanWriteBss));
                OnPropertyChanged(nameof(CanCreateAprsChannel));
                OnPropertyChanged(nameof(CanSyncRadio));
                OnPropertyChanged(nameof(CanConnectSession));
                OnPropertyChanged(nameof(CanDisconnectSession));
                OnPropertyChanged(nameof(CanSendSession));
                OnPropertyChanged(nameof(CanRequestPosition));
                OnPropertyChanged(nameof(CanCenterGps));
                OnPropertyChanged(nameof(CanLoadAllBanks));
                OnPropertyChanged(nameof(CanBeacon));
                UpdateAutoBeaconTimer();   // start/stop the app auto-beacon with the connection
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
            OnPropertyChanged(nameof(CanSyncRadio));
            OnPropertyChanged(nameof(CanConnectSession));
            OnPropertyChanged(nameof(CanTransmit));
            OnPropertyChanged(nameof(CanSendData));
                OnPropertyChanged(nameof(CanSendAprs));
            OnPropertyChanged(nameof(CanBeacon));
            OnPropertyChanged(nameof(WindowTitle));
        }
    }

    /// <summary>Window title — shows the operator callsign once set (dynamic title bar).</summary>
    public string WindowTitle => HasValidCallsign
        ? $"HTCommander — {MyCallsign}{(MyStationId > 0 ? $"-{MyStationId}" : "")}"
        : "HTCommander";

    public Array StationIdOptions { get; } = new[] { 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15 };
    private int myStationId;
    public int MyStationId
    {
        get => myStationId;
        set { if (SetField(ref myStationId, value)) { OnPropertyChanged(nameof(WindowTitle)); if (!loadingIdentity) DataBroker.Dispatch(0, "StationId", value, store: true); } }
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
            OnPropertyChanged(nameof(CanSyncRadio));
            OnPropertyChanged(nameof(CanConnectSession));
            OnPropertyChanged(nameof(CanTransmit));
            OnPropertyChanged(nameof(CanSendData));
                OnPropertyChanged(nameof(CanSendAprs));
            OnPropertyChanged(nameof(CanBeacon));
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

    // ---- Terminal connected-mode session (Core AX25Session) ----------------
    private AX25Session? terminalSession;

    private string terminalConnectTo = "";
    public string TerminalConnectTo
    {
        get => terminalConnectTo;
        set { if (SetField(ref terminalConnectTo, value)) OnPropertyChanged(nameof(CanConnectSession)); }
    }

    public string[] TerminalProtocols { get; } = { "AX.25" };
    private string terminalProtocol = "AX.25";
    public string TerminalProtocol { get => terminalProtocol; set => SetField(ref terminalProtocol, value); }

    /// <summary>Optional channel to lock the session to (blank = the radio's current channel).</summary>
    private string terminalChannel = "";
    public string TerminalChannel { get => terminalChannel; set => SetField(ref terminalChannel, value); }

    private bool sessionConnected;
    public bool SessionConnected
    {
        get => sessionConnected;
        private set { if (SetField(ref sessionConnected, value)) OnPropertyChanged(nameof(CanSendSession)); }
    }

    private string sessionStateText = "Disconnected";
    public string SessionState { get => sessionStateText; private set => SetField(ref sessionStateText, value); }
    public string SessionStateText => $"Session: {sessionStateText}";

    public bool CanConnectSession => Connected && TxAuthorized && terminalSession == null && !string.IsNullOrWhiteSpace(TerminalConnectTo);
    public bool CanDisconnectSession => terminalSession != null;
    public bool CanSendSession => sessionConnected;

    private void RaiseSessionGates()
    {
        OnPropertyChanged(nameof(CanConnectSession));
        OnPropertyChanged(nameof(CanDisconnectSession));
        OnPropertyChanged(nameof(CanSendSession));
    }

    /// <summary>Opens a connected-mode AX.25 session to a station. ON-AIR (keys the TNC).</summary>
    public void ConnectSession()
    {
        if (terminalSession != null) { AddTerminalLine("* Already in a session."); return; }
        if (!Connected) { AddTerminalLine("* Connect a radio first."); return; }
        if (!TxAuthorized) { AddTerminalLine("* Set callsign + Allow-Transmit first."); return; }
        string to = (TerminalConnectTo ?? "").Trim().ToUpperInvariant();
        string mine = (string.IsNullOrEmpty(TerminalMyCall) ? MyCallsign : TerminalMyCall) ?? "";
        if (to.Length == 0) { AddTerminalLine("* Enter a station to connect to."); return; }

        var dest = AX25Address.GetAddress(to);
        var src = AX25Address.GetAddress(mine);
        if (dest == null || src == null) { AddTerminalLine("* Invalid callsign."); return; }
        if (!CoreUtils.ParseCallsignWithId(mine, out string mc, out int mssid)) { mc = mine; mssid = 0; }

        ApplyTerminalChannelLock();   // honor an explicit channel pick (else current channel)

        var s = new AX25Session(BbsRadioDeviceId) { CallSignOverride = mc, StationIdOverride = mssid };
        s.StateChanged += OnSessionStateChanged;
        s.DataReceivedEvent += OnSessionDataReceived;
        s.UiDataReceivedEvent += OnSessionDataReceived;
        s.ErrorEvent += OnSessionError;
        terminalSession = s;
        SessionState = "Connecting";
        RaiseSessionGates();
        AddTerminalLine($"* Connecting to {to}…");
        s.Connect(new System.Collections.Generic.List<AX25Address> { dest, src });
    }

    public void DisconnectSession()
    {
        var s = terminalSession;
        if (s == null) return;
        AddTerminalLine("* Disconnecting…");
        try { s.Disconnect(); } catch (Exception) { }
    }

    // Lock the session's TX to a chosen channel. AX25Session.EmitPacket reads this
    // from the store via GetValue, so it must be stored (store:true). A blank
    // selection stores a cleared lock (ChannelId -1 → EmitPacket falls back to the
    // current channel).
    private void ApplyTerminalChannelLock()
    {
        var ch = string.IsNullOrWhiteSpace(TerminalChannel) ? null : Channels.FirstOrDefault(c => c.Name == TerminalChannel);
        var lockState = ch == null
            ? new RadioLockState { IsLocked = false, ChannelId = -1, RegionId = -1 }
            : new RadioLockState { IsLocked = true, ChannelId = ch.ChannelId, RegionId = Region };
        DataBroker.Dispatch(BbsRadioDeviceId, "LockState", lockState, store: true);
    }

    private void OnSessionStateChanged(AX25Session sender, AX25Session.ConnectionState state)
    {
        dispatcher.Post(() =>
        {
            SessionState = state.ToString();
            SessionConnected = state == AX25Session.ConnectionState.CONNECTED;
            AddTerminalLine($"* Session {state}");
            if (state == AX25Session.ConnectionState.DISCONNECTED) CleanupSession();
            RaiseSessionGates();
        });
    }

    private void OnSessionDataReceived(AX25Session sender, byte[] data)
    {
        string text = System.Text.Encoding.UTF8.GetString(data ?? Array.Empty<byte>());
        dispatcher.Post(() => { foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')) if (line.Length > 0) AddTerminalLine($"< {line}"); });
    }

    private void OnSessionError(AX25Session sender, string error) =>
        dispatcher.Post(() => AddTerminalLine("* Session error: " + error));

    private void CleanupSession()
    {
        var s = terminalSession;
        terminalSession = null;
        SessionConnected = false;
        if (s != null)
        {
            s.StateChanged -= OnSessionStateChanged;
            s.DataReceivedEvent -= OnSessionDataReceived;
            s.UiDataReceivedEvent -= OnSessionDataReceived;
            s.ErrorEvent -= OnSessionError;
            try { s.Dispose(); } catch (Exception) { }
        }
        RaiseSessionGates();
    }

    /// <summary>Send from the Terminal box: over the session if connected, else a UI frame.</summary>
    public void SendTerminal()
    {
        if (sessionConnected && terminalSession != null)
        {
            string text = TerminalText ?? "";
            if (text.Length == 0) return;
            try { terminalSession.Send(text + "\r"); } catch (Exception ex) { AddTerminalLine("* Send failed: " + ex.Message); return; }
            AddTerminalLine($"> {text}");
            TerminalText = "";
        }
        else SendTerminalMessage();
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
        private set { if (SetField(ref bss, value)) { OnPropertyChanged(nameof(HasBss)); OnPropertyChanged(nameof(CanWriteBss)); } }
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

    // Picking one of the saved APRS routes (Config tab) fills the contact's path.
    private AprsRoute? selectedContactRoute;
    public AprsRoute? SelectedContactRoute
    {
        get => selectedContactRoute;
        set { if (SetField(ref selectedContactRoute, value) && value != null) EditAprsRoute = value.Path; }
    }
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

        // Winlink-type contacts populate the over-radio sync picker.
        var keep = SelectedSyncStation?.Callsign;
        WinlinkStations.Clear();
        foreach (var s in list.Where(s => s.StationType == StationInfoClass.StationTypes.Winlink))
            WinlinkStations.Add(s);
        SelectedSyncStation = WinlinkStations.FirstOrDefault(s => s.Callsign == keep) ?? WinlinkStations.FirstOrDefault();
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
        autoSwept = false;
        Channels.Clear();
        Packets.Clear();
        Stations.Clear();
        Tracks.Clear();
        MyPosition = null;
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
            connectedMac = radio.Address;  // voice audio is opened on demand: holding the BT audio
                                           // (AOC) link open re-routes the radio's TX audio and stops
                                           // the hardware TNC's AFSK reaching the air (packet goes silent).
            // If a fixed position is configured, re-apply it once the connect settles.
            _ = Task.Run(async () => { await Task.Delay(1500); dispatcher.Post(PushFixedPositionIfSet); });
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
    /// <summary>
    /// Toggles the radio's voice-audio (BT AOC) channel on demand. It's OFF by default
    /// because holding it open disrupts the hardware TNC's transmit audio — packet
    /// (APRS/Winlink/BBS) won't reach the air while voice audio is active.
    /// </summary>
    public void ToggleVoiceRx()
    {
        if (VoiceRxActive) { StopVoiceRx(); AppendLog("Voice RX stopped (audio channel closed)."); }
        else if (Connected && connectedMac != null) StartVoiceRx(connectedMac);
        else AppendLog("Connect a radio before enabling Voice RX.");
    }

    private void StartVoiceRx(string mac)
    {
        Task.Run(() =>
        {
            try
            {
                var playback = new PortAudioPlayback { Volume = Settings.OutputVolume };
                playback.SetDevice(Settings.OutputDeviceId);     // honor the Settings output pick
                var rx = new RadioVoiceReceiver(playback);
                rx.PcmDecoded += OnRxPcm;          // feed the soft-modem + waterfall
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

    // --- Software modem (demodulate RX audio) + waterfall feed ---------------
    public string[] SoftModemModes { get; } = { "None", "AFSK1200", "PSK2400", "PSK4800", "G3RUH9600" };
    private string softModemMode = "None";
    public string SoftModemMode
    {
        get => softModemMode;
        set
        {
            if (!SetField(ref softModemMode, value) || value == null) return;
            DataBroker.Dispatch(0, "SetSoftwareModemMode", value, store: true);   // SoftwareModem reacts + persists
        }
    }

    /// <summary>Raised with a copy of each decoded RX-audio PCM block (32k/16/mono) for the waterfall.</summary>
    public event Action<byte[], int>? WaterfallPcm;

    // Decoded RX audio: feed the soft-modem (as "AudioDataAvailable") and the waterfall.
    // Runs on the audio decode thread; we copy the (reused) buffer before handing it on.
    private void OnRxPcm(byte[] buffer, int count)
    {
        if (count <= 0) return;
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, 0, copy, 0, count);
        try { WaterfallPcm?.Invoke(copy, count); } catch (Exception) { }
        if (softModemMode != "None")
        {
            DataBroker.Dispatch(0, "AudioDataAvailable",
                new { Data = copy, Offset = 0, Length = count, ChannelName = "", Transmit = false }, store: false);
        }
    }

    private void LoadSoftModemMode()
    {
        string m = DataBroker.GetValue<string>(0, "SoftwareModemMode", "None") ?? "None";
        if (Array.IndexOf(SoftModemModes, m) >= 0) SetField(ref softModemMode, m, nameof(SoftModemMode));
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
        try { autoBeaconTimer?.Stop(); autoBeaconTimer?.Dispose(); autoBeaconTimer = null; } catch (Exception) { }
        CleanupSession();
        SessionState = "Disconnected";
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
        Tracks.Clear();
        MyPosition = null;
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
        if (ChannelSlotIds.Count != channelCount)
        {
            ChannelSlotIds.Clear();
            for (int i = 0; i < channelCount; i++) ChannelSlotIds.Add(i);
        }
        RegionCount = Math.Max(1, d.RegionCount);
        loadingBanks = true;
        Banks.Clear();
        for (int i = 0; i < RegionCount; i++) Banks.Add(i);
        if (selectedBank >= RegionCount) selectedBank = 0;
        else selectedBank = Math.Max(0, Region);   // default to the radio's current bank
        loadingBanks = false;
        OnPropertyChanged(nameof(SelectedBank));

        // Optionally sweep every bank shortly after connect so the APRS channel (and
        // all channels) are available in the pickers without a manual "Load all banks".
        if (AutoLoadAllBanks && RegionCount > 1 && !autoSwept && Connected)
        {
            autoSwept = true;
            _ = Task.Run(async () => { await Task.Delay(2000); dispatcher.Post(LoadAllBanks); });
        }
    }

    private bool autoSwept;   // guard: auto-sweep at most once per connection
    private bool autoLoadAllBanks = true;
    /// <summary>Read every bank's channels automatically on connect (default on).</summary>
    public bool AutoLoadAllBanks
    {
        get => autoLoadAllBanks;
        set { if (SetField(ref autoLoadAllBanks, value)) DataBroker.Dispatch(0, "AutoLoadAllBanks", value, store: true); }
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
    public int RegionCount { get => regionCount; private set { if (SetField(ref regionCount, value)) { OnPropertyChanged(nameof(HasBanks)); OnPropertyChanged(nameof(CanLoadAllBanks)); } } }
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

    private bool sweepingBanks;
    /// <summary>True while the radio has more than one bank (enables "Load all banks").</summary>
    public bool CanLoadAllBanks => HasBanks && Connected && !sweepingBanks;

    /// <summary>
    /// Reads every bank's channels so the channel pickers (Contacts, etc.) list all
    /// banks — the radio only exposes one bank at a time, so this sweeps each bank,
    /// accumulating channel names, then returns the radio to the bank it started on.
    /// </summary>
    public void LoadAllBanks()
    {
        if (controller == null || !Connected) { BuilderStatus = "Connect to a radio first."; return; }
        if (RegionCount <= 1) { LoadChannelsFromRadio(); return; }
        if (sweepingBanks) return;
        sweepingBanks = true;
        OnPropertyChanged(nameof(CanLoadAllBanks));
        int start = SelectedBank;
        int banks = RegionCount;
        Task.Run(async () =>
        {
            try
            {
                for (int b = 0; b < banks; b++)
                {
                    controller.SetRegion(b);
                    controller.RefreshChannels();
                    int shown = b;
                    dispatcher.Post(() => BuilderStatus = $"Reading bank {shown} of {banks - 1}…");
                    await Task.Delay(1200);            // let this bank's channel replies arrive
                }
                // Restore the bank the operator was on and re-read it cleanly.
                dispatcher.Post(() => { radioChannels.Clear(); foreach (var s in Slots) { s.Name = ""; s.RxMHz = 0; } });
                controller.SetRegion(start);
                controller.RefreshChannels();
                dispatcher.Post(() => BuilderStatus = $"Loaded all {banks} banks — the channel picker now lists every bank.");
            }
            catch (Exception ex) { dispatcher.Post(() => BuilderStatus = "Load all banks failed: " + ex.Message); }
            finally { dispatcher.Post(() => { sweepingBanks = false; OnPropertyChanged(nameof(CanLoadAllBanks)); }); }
        });
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

    // ---- Click-a-tile single-channel editor -------------------------------
    public string[] ChannelModes { get; } = { "FM", "NFM", "AM", "DMR" };
    public string[] ChannelPowers { get; } = { "H", "M", "L" };
    private int editingSlotId = -1;
    private EditableChannel? editingChannel;
    public EditableChannel? EditingChannel
    {
        get => editingChannel;
        private set { if (SetField(ref editingChannel, value)) OnPropertyChanged(nameof(IsEditingChannel)); }
    }
    public bool IsEditingChannel => editingChannel != null;

    /// <summary>Opens the inline editor for a radio memory slot (existing channel or empty slot).</summary>
    public void BeginEditSlot(int slotId)
    {
        editingSlotId = slotId;
        EditingChannel = radioChannels.TryGetValue(slotId, out var info) && (info.rx_freq != 0 || info.tx_freq != 0)
            ? new EditableChannel(info)
            : new EditableChannel { ChannelId = slotId, Name = "" };
    }

    /// <summary>Writes the single edited channel to the radio at its slot.</summary>
    public void SaveEditingChannel()
    {
        if (EditingChannel == null || controller == null) return;
        if (!CanWriteChannels) { BuilderStatus = "Connect to a radio first to write channels."; return; }
        int id = editingSlotId >= 0 ? editingSlotId : EditingChannel.ChannelId;
        controller.WriteChannel(EditingChannel.ToRadioChannelInfo(id));
        BuilderStatus = $"Wrote channel {id} ({EditingChannel.Name}) to the radio.";
        EditingChannel = null; editingSlotId = -1;
    }

    public void CancelEditingChannel() { EditingChannel = null; editingSlotId = -1; }

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

    public static readonly string[] MailboxNames = { "Inbox", "Outbox", "Draft", "Sent", "Archive", "Trash" };

    /// <summary>Mailbox folders with live total/unread counts.</summary>
    public ObservableCollection<MailFolder> Folders { get; } = new(MailboxNames.Select(n => new MailFolder(n)));

    /// <summary>Folders a message can be filed into (everything except Trash, which is Delete).</summary>
    public string[] MoveTargets { get; } = MailboxNames.Where(n => n != "Trash").ToArray();
    private string moveTarget = "Archive";
    public string MoveTarget { get => moveTarget; set => SetField(ref moveTarget, value); }

    private MailFolder? selectedFolder;
    public MailFolder? SelectedFolder
    {
        get => selectedFolder;
        set { if (SetField(ref selectedFolder, value) && value != null) { selectedMailbox = value.Name; RefreshMails(); } }
    }

    private string selectedMailbox = "Inbox";
    public string SelectedMailbox => selectedMailbox;

    public ObservableCollection<WinLinkMail> Mails { get; } = new();

    private WinLinkMail? selectedMail;
    public WinLinkMail? SelectedMail
    {
        get => selectedMail;
        set
        {
            if (!SetField(ref selectedMail, value)) return;
            OnPropertyChanged(nameof(MailPreview));
            OnPropertyChanged(nameof(HasSelectedMail));
            OnPropertyChanged(nameof(MailAttachments));
            OnPropertyChanged(nameof(HasMailAttachments));
            OnPropertyChanged(nameof(CanDeleteMail));
            MarkSelectedRead();
        }
    }
    public bool HasSelectedMail => selectedMail != null;
    public bool CanDeleteMail => selectedMail != null;

    /// <summary>Attachments on the currently-open mail (for the viewer's list).</summary>
    public System.Collections.Generic.List<WinLinkMailAttachement> MailAttachments =>
        selectedMail?.Attachments ?? new System.Collections.Generic.List<WinLinkMailAttachement>();
    public bool HasMailAttachments => selectedMail?.Attachments is { Count: > 0 };

    private WinLinkMailAttachement? selectedAttachment;
    public WinLinkMailAttachement? SelectedAttachment { get => selectedAttachment; set => SetField(ref selectedAttachment, value); }

    public string MailPreview => selectedMail == null ? "" :
        $"From: {selectedMail.From}\nTo: {selectedMail.To}\n" +
        (string.IsNullOrEmpty(selectedMail.Cc) ? "" : $"Cc: {selectedMail.Cc}\n") +
        $"Date: {selectedMail.DateTime:yyyy-MM-dd HH:mm}\nSubject: {selectedMail.Subject}\n" +
        new string('-', 40) + "\n" + selectedMail.Body;

    private string composeTo = "", composeCc = "", composeSubject = "", composeBody = "";
    public string ComposeTo { get => composeTo; set => SetField(ref composeTo, value); }
    public string ComposeCc { get => composeCc; set => SetField(ref composeCc, value); }
    public string ComposeSubject { get => composeSubject; set => SetField(ref composeSubject, value); }
    public string ComposeBody { get => composeBody; set => SetField(ref composeBody, value); }

    /// <summary>Attachments staged on the message being composed.</summary>
    public ObservableCollection<WinLinkMailAttachement> ComposeAttachments { get; } = new();
    public bool HasComposeAttachments => ComposeAttachments.Count > 0;
    private WinLinkMailAttachement? selectedComposeAttachment;
    public WinLinkMailAttachement? SelectedComposeAttachment { get => selectedComposeAttachment; set => SetField(ref selectedComposeAttachment, value); }

    private string winlinkStatus = "Idle.";
    public string WinlinkStatus { get => winlinkStatus; private set => SetField(ref winlinkStatus, value); }

    /// <summary>Winlink session/traffic log (state messages from the client).</summary>
    public ObservableCollection<string> WinlinkLog { get; } = new();
    private void AppendWinlinkLog(string line)
    {
        WinlinkLog.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (WinlinkLog.Count > 500) WinlinkLog.RemoveAt(0);
    }

    /// <summary>Contacts that are Winlink gateways/stations, for the over-radio sync picker.</summary>
    public ObservableCollection<StationInfoClass> WinlinkStations { get; } = new();
    private StationInfoClass? selectedSyncStation;
    public StationInfoClass? SelectedSyncStation { get => selectedSyncStation; set { if (SetField(ref selectedSyncStation, value)) OnPropertyChanged(nameof(CanSyncRadio)); } }
    public bool CanSyncRadio => Connected && SelectedSyncStation != null && TxAuthorized;

    private IMailStore? MailStore => DataBroker.GetDataHandler<IMailStore>("MailStore");

    private void RefreshMails()
    {
        var store = MailStore;
        if (store == null) return;
        string? keepMid = selectedMail?.MID;        // preserve selection across the rebuild
        var all = store.GetAllMails();
        Mails.Clear();
        foreach (var m in all)
            if (string.Equals(m.Mailbox, selectedMailbox, StringComparison.OrdinalIgnoreCase))
                Mails.Add(m);
        UpdateFolderCounts(all);
        OnPropertyChanged(nameof(MailCountText));
        if (keepMid != null)
        {
            var again = Mails.FirstOrDefault(m => m.MID == keepMid);
            if (again != null) SelectedMail = again;   // re-open the same message (no flicker/loss)
        }
    }

    private void UpdateFolderCounts(System.Collections.Generic.List<WinLinkMail> all)
    {
        foreach (var f in Folders)
        {
            f.Total = all.Count(m => string.Equals(m.Mailbox, f.Name, StringComparison.OrdinalIgnoreCase));
            f.Unread = all.Count(m => string.Equals(m.Mailbox, f.Name, StringComparison.OrdinalIgnoreCase) && (m.Flags & (int)WinLinkMail.MailFlags.Unread) != 0);
        }
    }

    public string MailCountText => $"{selectedMailbox} ({Mails.Count})";

    // Reading a message clears its Unread flag and refreshes the folder badges.
    private void MarkSelectedRead()
    {
        var store = MailStore;
        if (store == null || selectedMail == null) return;
        if ((selectedMail.Flags & (int)WinLinkMail.MailFlags.Unread) == 0) return;
        selectedMail.Flags &= ~(int)WinLinkMail.MailFlags.Unread;
        try { store.UpdateMail(selectedMail); } catch (Exception) { }
        UpdateFolderCounts(store.GetAllMails());
    }

    private string MyCall() => string.IsNullOrEmpty(TerminalMyCall)
        ? (DataBroker.GetValue<string>(0, "CallSign", "") ?? "")
        : TerminalMyCall;

    private WinLinkMail BuildOutgoing(string mailbox)
    {
        return new WinLinkMail
        {
            MID = WinLinkMail.GenerateMID(),
            DateTime = DateTime.Now,
            From = MyCall(),
            To = (ComposeTo ?? "").Trim(),
            Cc = (ComposeCc ?? "").Trim(),
            Subject = ComposeSubject ?? "",
            Body = ComposeBody ?? "",
            Mailbox = mailbox,
            Flags = 0,
            Attachments = ComposeAttachments.Count > 0
                ? new System.Collections.Generic.List<WinLinkMailAttachement>(ComposeAttachments)
                : null,
        };
    }

    private void ClearCompose()
    {
        ComposeTo = ComposeCc = ComposeSubject = ComposeBody = "";
        ComposeAttachments.Clear();
    }

    /// <summary>Clears the compose form and switches to the compose view.</summary>
    public void NewMail()
    {
        SelectedMail = null;
        ClearCompose();
        WinlinkStatus = "New message.";
    }

    /// <summary>Compose a new message and queue it in the Outbox for the next sync.</summary>
    public void ComposeSaveToOutbox()
    {
        var store = MailStore;
        if (store == null) { WinlinkStatus = "Mail store unavailable."; return; }
        if ((ComposeTo ?? "").Trim().Length == 0) { WinlinkStatus = "Compose: 'To' is required."; return; }
        var mail = BuildOutgoing("Outbox");
        store.AddMail(mail);
        ClearCompose();
        WinlinkStatus = $"Saved to Outbox: {mail.Subject}";
        SelectFolder("Outbox");
    }

    /// <summary>Save the message being composed to the Draft folder (not sent on sync).</summary>
    public void SaveAsDraft()
    {
        var store = MailStore;
        if (store == null) { WinlinkStatus = "Mail store unavailable."; return; }
        var mail = BuildOutgoing("Draft");
        store.AddMail(mail);
        ClearCompose();
        WinlinkStatus = $"Saved to Draft: {mail.Subject}";
        SelectFolder("Draft");
    }

    private void SelectFolder(string name)
    {
        var f = Folders.FirstOrDefault(x => x.Name == name);
        if (f != null) SelectedFolder = f; else { selectedMailbox = name; RefreshMails(); }
    }

    // ---- Reply / reply-all / forward (populate the compose form) ----
    private void ComposeFrom(WinLinkMail original, string to, string cc, string subjectPrefix,
                             string header, bool keepAttachments)
    {
        SelectedMail = null;
        ComposeTo = to;
        ComposeCc = cc;
        ComposeSubject = original.Subject.StartsWith(subjectPrefix, StringComparison.OrdinalIgnoreCase)
            ? original.Subject : subjectPrefix + original.Subject;
        ComposeBody = $"\r\n\r\n{header}\r\n{original.Body}";
        ComposeAttachments.Clear();
        if (keepAttachments && original.Attachments != null)
            foreach (var a in original.Attachments) ComposeAttachments.Add(a);
        WinlinkStatus = "Editing reply/forward.";
    }

    public void ReplyMail()
    {
        if (selectedMail is not { } m) return;
        ComposeFrom(m, m.From, "", "Re: ",
            $"--- Original Message ---\r\nFrom: {m.From}\r\nDate: {m.DateTime.ToLocalTime()}\r\n", false);
    }

    public void ReplyAllMail()
    {
        if (selectedMail is not { } m) return;
        ComposeFrom(m, m.From, m.Cc ?? "", "Re: ",
            $"--- Original Message ---\r\nFrom: {m.From}\r\nDate: {m.DateTime.ToLocalTime()}\r\n", false);
    }

    public void ForwardMail()
    {
        if (selectedMail is not { } m) return;
        ComposeFrom(m, "", "", "Fwd: ",
            $"--- Forwarded Message ---\r\nFrom: {m.From}\r\nTo: {m.To}\r\nDate: {m.DateTime.ToLocalTime()}\r\nSubject: {m.Subject}\r\n", true);
    }

    // ---- Attachments ----
    public void AddComposeAttachment(string path)
    {
        try
        {
            var data = System.IO.File.ReadAllBytes(path);
            ComposeAttachments.Add(new WinLinkMailAttachement { Name = System.IO.Path.GetFileName(path), Data = data });
            WinlinkStatus = $"Attached {System.IO.Path.GetFileName(path)} ({data.Length} bytes).";
        }
        catch (Exception ex) { WinlinkStatus = "Attach failed: " + ex.Message; }
    }

    public void RemoveComposeAttachment()
    {
        if (SelectedComposeAttachment != null) ComposeAttachments.Remove(SelectedComposeAttachment);
    }

    /// <summary>Saves the selected viewer attachment to a chosen path.</summary>
    public void SaveAttachmentTo(string path)
    {
        if (SelectedAttachment?.Data == null) return;
        try { System.IO.File.WriteAllBytes(path, SelectedAttachment.Data); WinlinkStatus = $"Saved {SelectedAttachment.Name}."; }
        catch (Exception ex) { WinlinkStatus = "Save failed: " + ex.Message; }
    }

    /// <summary>Writes the selected attachment to a temp file and opens it with the OS handler.</summary>
    public void OpenSelectedAttachment()
    {
        if (SelectedAttachment?.Data == null) return;
        try
        {
            // Sanitize the remote-supplied name to its leaf only — never let an
            // attachment write outside the temp dir (e.g. "../" or an absolute path).
            string safeName = System.IO.Path.GetFileName(SelectedAttachment.Name);
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "attachment";
            string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), safeName);
            System.IO.File.WriteAllBytes(tmp, SelectedAttachment.Data);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", $"\"{tmp}\"") { UseShellExecute = false });
            WinlinkStatus = $"Opened {safeName}.";
        }
        catch (Exception ex) { WinlinkStatus = "Open failed: " + ex.Message; }
    }

    // ---- Move between folders ----
    public void MoveSelectedMailTo(string folder)
    {
        var store = MailStore;
        if (store == null || selectedMail == null || string.IsNullOrEmpty(folder)) return;
        selectedMail.Mailbox = folder;
        store.UpdateMail(selectedMail);
        WinlinkStatus = $"Moved to {folder}.";
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

    // ---- Backup / restore (gzip of the WinLinkMail text serialization) ----
    public void BackupMail(string path)
    {
        var store = MailStore;
        if (store == null) { WinlinkStatus = "Mail store unavailable."; return; }
        try
        {
            string text = WinLinkMail.Serialize(store.GetAllMails());
            byte[] raw = System.Text.Encoding.UTF8.GetBytes(text);
            using var fs = System.IO.File.Create(path);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal);
            gz.Write(raw, 0, raw.Length);
            WinlinkStatus = $"Backed up {store.Count} message(s) to {System.IO.Path.GetFileName(path)}.";
        }
        catch (Exception ex) { WinlinkStatus = "Backup failed: " + ex.Message; }
    }

    public void RestoreMail(string path)
    {
        var store = MailStore;
        if (store == null) { WinlinkStatus = "Mail store unavailable."; return; }
        try
        {
            using var fs = System.IO.File.OpenRead(path);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
            using var ms = new System.IO.MemoryStream();
            gz.CopyTo(ms);
            string text = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            var mails = WinLinkMail.Deserialize(text);
            int added = 0;
            foreach (var m in mails)
                if (!store.MailExists(m.MID)) { store.AddMail(m); added++; }
            WinlinkStatus = $"Restored {added} new message(s) from {System.IO.Path.GetFileName(path)}.";
            RefreshMails();
        }
        catch (Exception ex) { WinlinkStatus = "Restore failed: " + ex.Message; }
    }

    /// <summary>Start a Winlink session over the internet (Telnet CMS) to send Outbox
    /// mail and receive new mail. Needs network + a reachable CMS.</summary>
    public void SyncWinlinkInternet()
    {
        WinlinkStatus = "Connecting to Winlink CMS (internet)…";
        AppendWinlinkLog("Sync (internet) requested.");
        DataBroker.Dispatch(1, "WinlinkSync", new { Server = "server.winlink.org", Port = 8772, UseTls = false }, store: false);
    }

    /// <summary>Start a Winlink B2F session over the radio to a selected gateway station.
    /// ON-AIR — needs a connected, TX-authorized radio and a chosen Winlink contact.</summary>
    public void SyncWinlinkRadio()
    {
        if (!Connected) { WinlinkStatus = "Connect a radio first."; return; }
        if (SelectedSyncStation == null) { WinlinkStatus = "Pick a Winlink station (a Winlink-type contact)."; return; }
        if (!TxAuthorized) { WinlinkStatus = "Set callsign + Allow-Transmit before a radio sync."; return; }
        WinlinkStatus = $"Connecting to {SelectedSyncStation.Callsign} over the radio…";
        AppendWinlinkLog($"Sync (radio) → {SelectedSyncStation.Callsign} requested.");
        DataBroker.Dispatch(1, "WinlinkSync",
            new { RadioId = BbsRadioDeviceId, Station = SelectedSyncStation }, store: false);
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
        RecordTrackPoint(s);
        // One entry per callsign; update in place when a station's position changes.
        for (int i = 0; i < Stations.Count; i++)
            if (Stations[i].Callsign == s.Callsign) { Stations[i] = s; return; }
        Stations.Add(s);
    }

    // ---- GPS position (radio) + map tracks/markers -------------------------
    private RadioPositionInfo? myPosition;
    public RadioPositionInfo? MyPosition
    {
        get => myPosition;
        private set { if (SetField(ref myPosition, value)) { OnPropertyChanged(nameof(HasMyPosition)); OnPropertyChanged(nameof(CanCenterGps)); } }
    }
    public bool HasMyPosition => myPosition is { Locked: true };
    public bool CanRequestPosition => Connected;
    public bool CanCenterGps => HasMyPosition || HasSerialPosition;

    // Serial-GPS fix (device 1, from GpsSerialHandler) — shown as a distinct map marker.
    private HTCommander.Gps.GpsData? serialPosition;
    public HTCommander.Gps.GpsData? SerialPosition
    {
        get => serialPosition;
        private set { if (SetField(ref serialPosition, value)) { OnPropertyChanged(nameof(HasSerialPosition)); OnPropertyChanged(nameof(CanCenterGps)); } }
    }
    public bool HasSerialPosition => serialPosition is { IsFixed: true };

    /// <summary>Requests a fresh GPS position from the radio (GET_POSITION).</summary>
    public void RequestPosition()
    {
        if (controller == null || !Connected) { AppendLog("Connect a radio to request position."); return; }
        controller.RequestPosition();
        AppendLog("Requested fresh GPS position.");
    }

    // Fixed/manual position (for a stationary station with no GPS). Persisted and
    // re-pushed on connect so the radio keeps beaconing it across restarts.
    private string manualLatitude = "", manualLongitude = "";
    public string ManualLatitude { get => manualLatitude; set => SetField(ref manualLatitude, value); }
    public string ManualLongitude { get => manualLongitude; set => SetField(ref manualLongitude, value); }

    private bool TryParseManualPosition(out double lat, out double lon)
    {
        lat = lon = 0;
        return double.TryParse(ManualLatitude, NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
            && double.TryParse(ManualLongitude, NumberStyles.Float, CultureInfo.InvariantCulture, out lon)
            && lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180;
    }

    /// <summary>Pushes the entered lat/lon to the radio as a fixed position (no GPS needed).</summary>
    public void SetManualPosition()
    {
        if (controller == null || !Connected) { AppendLog("Connect a radio first."); return; }
        if (!TryParseManualPosition(out double lat, out double lon))
        { AppendLog("Enter a valid latitude (-90..90) and longitude (-180..180), decimal degrees."); return; }
        controller.SetManualPosition(lat, lon);
        DataBroker.Dispatch(0, "FixedLat", ManualLatitude, store: true);
        DataBroker.Dispatch(0, "FixedLon", ManualLongitude, store: true);
        AppendLog($"Set fixed position {lat.ToString("0.0000", CultureInfo.InvariantCulture)}, {lon.ToString("0.0000", CultureInfo.InvariantCulture)} on the radio.");
    }

    private void LoadFixedPosition()
    {
        ManualLatitude = DataBroker.GetValue<string>(0, "FixedLat", "") ?? "";
        ManualLongitude = DataBroker.GetValue<string>(0, "FixedLon", "") ?? "";
    }

    // Re-push a stored fixed position shortly after connect (radio has no GPS).
    private void PushFixedPositionIfSet()
    {
        if (controller == null || !Connected) return;
        if (TryParseManualPosition(out double lat, out double lon))
        {
            controller.SetManualPosition(lat, lon);
            AppendLog($"Re-applied fixed position {lat.ToString("0.0000", CultureInfo.InvariantCulture)}, {lon.ToString("0.0000", CultureInfo.InvariantCulture)}.");
        }
    }

    // Per-callsign track history (timestamped), for map polylines.
    public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<TrackPoint>> Tracks { get; } = new();
    private const int MaxTrackPoints = 300;

    private void RecordTrackPoint(AprsStationSummary s)
    {
        if (!Tracks.TryGetValue(s.Callsign, out var list)) { list = new(); Tracks[s.Callsign] = list; }
        // Skip duplicate consecutive fixes.
        if (list.Count > 0)
        {
            var last = list[list.Count - 1];
            if (Math.Abs(last.Latitude - s.Latitude) < 1e-7 && Math.Abs(last.Longitude - s.Longitude) < 1e-7) return;
        }
        list.Add(new TrackPoint(s.Latitude, s.Longitude, DateTime.Now));
        while (list.Count > MaxTrackPoints) list.RemoveAt(0);
    }

    private bool showTracks = true;
    public bool ShowTracks { get => showTracks; set => SetField(ref showTracks, value); }

    private bool largeMarkers;
    public bool LargeMarkers { get => largeMarkers; set => SetField(ref largeMarkers, value); }

    // Time filter (minutes); 0 = show all. The map honors this when rebuilding.
    public int[] TrackMinuteOptions { get; } = { 0, 5, 15, 30, 60, 240 };
    private int trackMinutes;
    public int TrackMinutes { get => trackMinutes; set => SetField(ref trackMinutes, value); }

    /// <summary>True if a point/track timestamp passes the current time filter.</summary>
    public bool WithinTimeFilter(DateTime t) => trackMinutes <= 0 || t >= DateTime.Now.AddMinutes(-trackMinutes);

    // ---- APRS messaging (send + conversation) -------------------------------
    public ObservableCollection<AprsMessageRow> AprsMessages { get; } = new();

    private string aprsDestination = "";
    public string AprsDestination { get => aprsDestination; set => SetField(ref aprsDestination, value); }
    private string aprsMessageText = "";
    public string AprsMessageText { get => aprsMessageText; set => SetField(ref aprsMessageText, value); }

    /// <summary>APRS message send needs a connected, TX-authorized radio.</summary>
    public bool CanSendAprs => CanSendData;

    public void SendAprsMessage()
    {
        string dest = (AprsDestination ?? "").Trim().ToUpperInvariant();
        string msg = AprsMessageText ?? "";
        if (dest.Length == 0 || msg.Length == 0) { AppendLog("APRS: need a destination and message text."); return; }
        if (!CanSendData) { AppendLog("APRS: set callsign + Allow-Transmit and connect first."); return; }
        string[]? route = SelectedSendRoute?.ToRouteArray();   // optional named digipeater path
        DataBroker.Dispatch(1, "SendAprsMessage",
            new AprsSendMessageData { Destination = dest, Message = msg, RadioDeviceId = BbsRadioDeviceId, Route = route },
            store: false);
        AppendLog(route != null
            ? $"APRS → {dest} via {SelectedSendRoute!.Name}: {msg}"
            : $"APRS → {dest}: {msg}");
        AprsMessageText = "";
    }

    private void OnAprsFrame(object? data)
    {
        if (data is not AprsFrameEventArgs e || e.AprsPacket?.MessageData?.MsgText == null) return;
        var md = e.AprsPacket.MessageData;
        if (string.IsNullOrEmpty(md.MsgText)) return;                 // messages only (positions go to the map)
        bool outgoing = e.AX25Packet != null && !e.AX25Packet.incoming;
        string from = e.AX25Packet != null && e.AX25Packet.addresses.Count > 1 ? e.AX25Packet.addresses[1].ToString() : "";
        AprsMessages.Add(new AprsMessageRow
        {
            Time = DateTime.Now,
            From = from,
            To = md.Addressee ?? "",
            Text = md.MsgText,
            Outgoing = outgoing,
        });
        while (AprsMessages.Count > 500) AprsMessages.RemoveAt(0);
    }

    // ---- Per-packet detail (Packets tab selection) -------------------------
    private ReceivedPacketSummary? selectedPacket;
    public ReceivedPacketSummary? SelectedPacket
    {
        get => selectedPacket;
        set { if (SetField(ref selectedPacket, value)) OnPropertyChanged(nameof(HasSelectedPacket)); }
    }
    public bool HasSelectedPacket => selectedPacket != null;

    private static string CsvField(string? s)
    {
        s ??= "";
        return (s.Contains(',') || s.Contains('"') || s.Contains('\n'))
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }

    private static System.Collections.Generic.List<string> ParseCsvLine(string line)
    {
        var fields = new System.Collections.Generic.List<string>();
        var sb = new System.Text.StringBuilder();
        bool inQ = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQ)
            {
                if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else inQ = false; }
                else sb.Append(c);
            }
            else if (c == '"') inQ = true;
            else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>Exports the captured packets to a CSV file (decode columns + raw info).</summary>
    public void ExportPacketsCsv(string path)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Time,Source,Destination,Path,APRS,Type,Symbol,Latitude,Longitude,Comment,Info");
            foreach (var p in Packets)
                sb.AppendLine(string.Join(",",
                    CsvField(p.TimeLocal == default ? "" : p.TimeLocal.ToString("o", CultureInfo.InvariantCulture)),
                    CsvField(p.Source), CsvField(p.Destination), CsvField(p.Path),
                    p.IsAprs ? "1" : "0", CsvField(p.AprsType), CsvField(p.Symbol),
                    CsvField(p.Latitude?.ToString(CultureInfo.InvariantCulture)),
                    CsvField(p.Longitude?.ToString(CultureInfo.InvariantCulture)),
                    CsvField(p.Comment), CsvField(p.Info)));
            System.IO.File.WriteAllText(path, sb.ToString());
            AppendLog($"Exported {Packets.Count} packet(s) to {System.IO.Path.GetFileName(path)}.");
        }
        catch (Exception ex) { AppendLog("Packet export failed: " + ex.Message); }
    }

    /// <summary>Loads a previously-exported packet capture CSV into the Packets list for viewing.</summary>
    public void LoadPacketsCsv(string path)
    {
        try
        {
            var lines = System.IO.File.ReadAllLines(path);
            if (lines.Length < 2) { AppendLog("Capture file has no rows."); return; }
            Packets.Clear();
            SelectedPacket = null;
            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var f = ParseCsvLine(lines[i]);
                string Get(int n) => n < f.Count ? f[n] : "";
                double? Dbl(int n) => double.TryParse(Get(n), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
                DateTime t = DateTime.TryParse(Get(0), CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : default;
                Packets.Add(new ReceivedPacketSummary(
                    Get(1), Get(2), Get(10), Get(4) == "1",
                    TimeLocal: t, Path: Get(3), AprsType: Get(5), Symbol: Get(6),
                    Latitude: Dbl(7), Longitude: Dbl(8), Comment: Get(9)));
            }
            AppendLog($"Loaded {Packets.Count} packet(s) from {System.IO.Path.GetFileName(path)}.");
        }
        catch (Exception ex) { AppendLog("Capture load failed: " + ex.Message); }
    }

    // ---- Beacon / Ident (BSS) editable settings + write --------------------
    // The Config tab edits these; "Write to radio" sends the whole BSS object
    // (preserving fields we don't expose) via the SetBssSettings broker event.
    private bool loadingBss;
    private string beaconCallsign = "";
    public string BeaconCallsign { get => beaconCallsign; set { if (!loadingBss) value = (value ?? "").ToUpperInvariant(); SetField(ref beaconCallsign, value); } }
    private int beaconSsid;
    public int BeaconSsid { get => beaconSsid; set => SetField(ref beaconSsid, value); }
    private string beaconSymbol = "";
    public string BeaconSymbol { get => beaconSymbol; set => SetField(ref beaconSymbol, value); }
    private string beaconMessageText = "";
    public string BeaconMessageText { get => beaconMessageText; set => SetField(ref beaconMessageText, value); }

    // The memory channel the app uses for ALL APRS TX (messages + app beacons).
    private bool loadingAprsChannel;
    private string aprsChannelName = "APRS";
    public string AprsChannelName
    {
        get => aprsChannelName;
        set
        {
            if (!SetField(ref aprsChannelName, value) || value == null) return;
            if (loadingAprsChannel) return;
            DataBroker.Dispatch(0, "AprsChannelName", value, store: true);
            // Re-read channels so RadioController records the chosen channel's bank+id.
            if (Connected && controller != null) { if (HasBanks) LoadAllBanks(); else controller.RefreshChannels(); }
        }
    }
    private void LoadAprsChannelName()
    {
        loadingAprsChannel = true;
        aprsChannelName = DataBroker.GetValue<string>(0, "AprsChannelName", "APRS") ?? "APRS";
        // Seed the channel list with the saved name so the picker shows it before connecting
        // (the live channel list fills in once a radio connects).
        if (!string.IsNullOrWhiteSpace(aprsChannelName) && !ChannelNames.Contains(aprsChannelName))
            ChannelNames.Add(aprsChannelName);
        OnPropertyChanged(nameof(AprsChannelName));
        loadingAprsChannel = false;
    }
    private int beaconInterval;
    public int BeaconInterval { get => beaconInterval; set => SetField(ref beaconInterval, value); }
    private bool beaconShareLocation;
    public bool BeaconShareLocation { get => beaconShareLocation; set => SetField(ref beaconShareLocation, value); }

    // Single beacon method (mutually exclusive): "Off" | "Radio" | "App".
    public string[] BeaconModes { get; } = { "Off", "Radio (built-in)", "App (TNC)" };
    private bool loadingBeaconMode;
    private string beaconMode = "Off";
    public string BeaconMode
    {
        get => beaconMode;
        set
        {
            if (!SetField(ref beaconMode, value) || value == null) return;
            OnPropertyChanged(nameof(IsRadioBeacon));
            OnPropertyChanged(nameof(IsAppBeacon));
            if (loadingBeaconMode) return;
            DataBroker.Dispatch(0, "BeaconMode", value, store: true);
            // Apply to the radio's own beacon: ON only in Radio mode (prevents double-TX).
            if (Connected && HasBss) WriteBssSettings();
            UpdateAutoBeaconTimer();
        }
    }
    public bool IsRadioBeacon => beaconMode != null && beaconMode.StartsWith("Radio");
    public bool IsAppBeacon => beaconMode != null && beaconMode.StartsWith("App");
    private void LoadBeaconMode()
    {
        loadingBeaconMode = true;
        string m = DataBroker.GetValue<string>(0, "BeaconMode", "Off") ?? "Off";
        beaconMode = Array.IndexOf(BeaconModes, m) >= 0 ? m : "Off";
        OnPropertyChanged(nameof(BeaconMode));
        OnPropertyChanged(nameof(IsRadioBeacon));
        OnPropertyChanged(nameof(IsAppBeacon));
        loadingBeaconMode = false;
    }

    private bool beaconOnPttRelease;
    public bool BeaconOnPttRelease { get => beaconOnPttRelease; set => SetField(ref beaconOnPttRelease, value); }
    private bool identOnPttRelease;
    public bool IdentOnPttRelease { get => identOnPttRelease; set => SetField(ref identOnPttRelease, value); }
    private string identText = "";
    public string IdentText { get => identText; set => SetField(ref identText, value); }

    /// <summary>BSS write needs a connected radio with settings already read (so we
    /// preserve the fields the editor doesn't expose).</summary>
    public bool CanWriteBss => Connected && HasBss;

    private void SeedBssEditor(RadioBssSettings b)
    {
        loadingBss = true;
        BeaconCallsign = b.AprsCallsign ?? "";
        BeaconSsid = b.AprsSsid;
        BeaconSymbol = b.AprsSymbol ?? "";
        BeaconMessageText = b.BeaconMessage ?? "";
        BeaconInterval = b.LocationShareInterval;
        BeaconShareLocation = b.ShouldShareLocation;
        BeaconOnPttRelease = b.PttReleaseSendLocation;
        IdentOnPttRelease = b.PttReleaseSendIdInfo;
        IdentText = b.PttReleaseIdInfo ?? "";
        loadingBss = false;
    }

    private static string Clamp(string? s, int max)
    {
        s ??= "";
        return s.Length > max ? s.Substring(0, max) : s;
    }

    /// <summary>Writes the edited beacon/ident settings to the radio (WRITE_BSS_SETTINGS).
    /// This configures PTT-release beacon/ident transmissions, so it is operator-initiated.</summary>
    public void WriteBssSettings()
    {
        if (!CanWriteBss) { AppendLog("Connect to a radio (and let settings load) before writing beacon/ident."); return; }
        var b = Bss!.Clone();
        b.AprsCallsign = Clamp((MyCallsign ?? "").Trim().ToUpperInvariant(), 6);   // from station identity (no duplicate field)
        b.AprsSsid = Math.Clamp(MyStationId, 0, 15);
        b.AprsSymbol = Clamp(string.IsNullOrEmpty(BeaconSymbol) ? "/-" : BeaconSymbol, 2);
        b.BeaconMessage = Clamp(BeaconMessageText, 18);
        b.LocationShareInterval = Math.Max(0, BeaconInterval);
        b.ShouldShareLocation = IsRadioBeacon;   // radio beacons only in Radio mode (avoids double-TX with App mode)
        b.PttReleaseSendLocation = BeaconOnPttRelease;
        b.PttReleaseSendIdInfo = IdentOnPttRelease;
        b.PttReleaseIdInfo = Clamp(IdentText, 12);
        DataBroker.Dispatch(0, "SetBssSettings", b, store: false);

        if (IsRadioBeacon)
        {
            // Point the radio's built-in beacon at the resolved APRS channel (auto_share_loc_ch),
            // so it transmits there instead of "Current" (whatever you're tuned to). Mirrors the
            // WinForms beacon editor. Needs a memory channel named to match the APRS picker.
            var loc = DataBroker.GetValue<AprsChannelLocation>(0, "AprsChannel", null);
            if (loc != null && controller != null && controller.WriteAutoShareLocChannel(loc.ChannelId))
                AppendLog($"Beacon/ident written. Radio beacon set to the '{AprsChannelName}' channel — make sure Digital mode is ON in the radio's menu (the app can't toggle it).");
            else
                AppendLog($"Beacon/ident written, but couldn't find a channel named '{AprsChannelName}' to target — the radio will beacon on its currently-tuned channel. Add a memory channel named '{AprsChannelName}', and enable Digital mode in the radio's menu.");
        }
        else
        {
            AppendLog("Beacon/ident settings written to the radio (radio's built-in beacon is off).");
        }
    }

    // ---- App-driven beacon (sends a position report on the APRS channel) ----
    public bool CanBeacon => CanSendData;   // connected + callsign + Allow-Transmit

    private bool TryGetBeaconPosition(out double lat, out double lon)
    {
        if (TryParseManualPosition(out lat, out lon)) return true;       // fixed position wins
        if (MyPosition is { Locked: true } p) { lat = p.Latitude; lon = p.Longitude; return true; }
        lat = lon = 0; return false;
    }

    /// <summary>Sends one APRS position report on the APRS channel via the TNC (app-driven beacon).</summary>
    public void BeaconNow()
    {
        if (!Connected) { AppendLog("Connect a radio first."); return; }
        if (!TxAuthorized) { AppendLog("Set callsign + Allow-Transmit to beacon."); return; }
        if (!TryGetBeaconPosition(out double lat, out double lon))
        { AppendLog("No position to beacon — set a fixed position or get a GPS fix first."); return; }
        DataBroker.Dispatch(1, "SendAprsBeacon", new AprsSendBeaconData
        {
            Latitude = lat, Longitude = lon,
            Symbol = string.IsNullOrEmpty(BeaconSymbol) ? "/-" : BeaconSymbol,
            Comment = BeaconMessageText ?? "",
            RadioDeviceId = BbsRadioDeviceId,
            Route = SelectedSendRoute?.ToRouteArray(),
        }, store: false);
        AppendLog($"Beaconed {lat.ToString("0.0000", CultureInfo.InvariantCulture)}, {lon.ToString("0.0000", CultureInfo.InvariantCulture)} on the APRS channel.");
    }

    private System.Timers.Timer? autoBeaconTimer;
    private bool autoBeacon;
    public bool AutoBeacon
    {
        get => autoBeacon;
        set { if (SetField(ref autoBeacon, value)) UpdateAutoBeaconTimer(); }
    }

    private void UpdateAutoBeaconTimer()
    {
        try { autoBeaconTimer?.Stop(); autoBeaconTimer?.Dispose(); } catch (Exception) { }
        autoBeaconTimer = null;
        if (autoBeacon && Connected && IsAppBeacon)   // app auto-beacon only in App mode
        {
            int secs = Math.Max(10, BeaconInterval);
            autoBeaconTimer = new System.Timers.Timer(secs * 1000.0) { AutoReset = true };
            autoBeaconTimer.Elapsed += (_, _) => dispatcher.Post(BeaconNow);
            autoBeaconTimer.Start();
            AppendLog($"Auto-beacon on: every {secs}s on the APRS channel.");
        }
    }

    // ---- Global APRS routes (named digipeater paths) -----------------------
    public ObservableCollection<AprsRoute> AprsRoutes { get; } = new();

    private AprsRoute? selectedSendRoute;
    /// <summary>Route chosen on the compose bar; null = the radio's default path.</summary>
    public AprsRoute? SelectedSendRoute { get => selectedSendRoute; set => SetField(ref selectedSendRoute, value); }

    private AprsRoute? selectedRoute;
    public AprsRoute? SelectedRoute
    {
        get => selectedRoute;
        set
        {
            if (!SetField(ref selectedRoute, value) || value == null) return;
            EditRouteName = value.Name;
            EditRouteDest = value.Destination;
            EditRoutePath = value.Path;
        }
    }

    private string editRouteName = "";
    public string EditRouteName { get => editRouteName; set => SetField(ref editRouteName, value); }
    private string editRouteDest = "APN000-0";
    public string EditRouteDest { get => editRouteDest; set => SetField(ref editRouteDest, value); }
    private string editRoutePath = "";
    public string EditRoutePath { get => editRoutePath; set => SetField(ref editRoutePath, value); }

    private void LoadAprsRoutes()
    {
        string s = DataBroker.GetValue<string>(0, "AprsRoutes", "") ?? "";
        var keepName = SelectedSendRoute?.Name;
        AprsRoutes.Clear();
        if (string.IsNullOrWhiteSpace(s))
            AprsRoutes.Add(new AprsRoute { Name = "Standard", Destination = "APN000-0", Path = "WIDE1-1,WIDE2-2" });
        else
            foreach (var r in s.Split('|', StringSplitOptions.RemoveEmptyEntries))
                AprsRoutes.Add(AprsRoute.FromStorage(r));
        SelectedSendRoute = AprsRoutes.FirstOrDefault(r => r.Name == keepName) ?? AprsRoutes.FirstOrDefault();
    }

    private void SaveAprsRoutes()
    {
        string s = string.Join("|", AprsRoutes.Select(r => r.ToStorage()));
        DataBroker.Dispatch(0, "AprsRoutes", s, store: true);
    }

    public void AddOrUpdateRoute()
    {
        string name = (EditRouteName ?? "").Trim();
        if (name.Length == 0) { AppendLog("APRS route needs a name."); return; }
        // Comma is the field delimiter in the stored "Name,Dest,Path…" route form, so a
        // comma in the name or destination would shift the route array and mis-address TX.
        // (The path may contain commas — they separate digipeaters.)
        if (name.Contains(',') || (EditRouteDest ?? "").Contains(','))
        { AppendLog("APRS route name/destination can't contain a comma."); return; }
        var existing = AprsRoutes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        var route = existing ?? new AprsRoute();
        route.Name = name;
        route.Destination = string.IsNullOrWhiteSpace(EditRouteDest) ? "APN000-0" : EditRouteDest.Trim();
        route.Path = (EditRoutePath ?? "").Trim();
        if (existing == null) AprsRoutes.Add(route);
        SaveAprsRoutes();
        SelectedSendRoute ??= route;
        AppendLog($"Saved APRS route '{name}'.");
    }

    public void RemoveSelectedRoute()
    {
        if (SelectedRoute == null) return;
        bool wasSend = ReferenceEquals(SelectedRoute, SelectedSendRoute);
        AprsRoutes.Remove(SelectedRoute);
        SelectedRoute = null;
        if (wasSend) SelectedSendRoute = AprsRoutes.FirstOrDefault();
        SaveAprsRoutes();
    }

    // ---- Create an "APRS" memory channel (144.39 FM, wide) -----------------
    public ObservableCollection<int> ChannelSlotIds { get; } = new();

    private int aprsChannelSlot;
    public int AprsChannelSlot { get => aprsChannelSlot; set => SetField(ref aprsChannelSlot, value); }

    private string aprsChannelFreq = "144.3900";
    public string AprsChannelFreq { get => aprsChannelFreq; set => SetField(ref aprsChannelFreq, value); }

    public bool CanCreateAprsChannel => Connected && controller != null;

    /// <summary>Programs an APRS memory channel (FM, wide, muted) into the chosen slot —
    /// the standard 144.39 MHz packet channel, so APRS TX/RX has a channel to use.</summary>
    public void CreateAprsChannel()
    {
        if (controller == null || !Connected) { AppendLog("Connect to a radio to create an APRS channel."); return; }
        if (!float.TryParse(AprsChannelFreq, NumberStyles.Float, CultureInfo.InvariantCulture, out float mhz) || mhz < 144 || mhz > 148)
        { AppendLog("APRS channel: frequency must be between 144 and 148 MHz."); return; }

        int slot = AprsChannelSlot;
        int hz = (int)(mhz * 1_000_000);
        var ch = new RadioChannelInfo
        {
            channel_id = slot,
            name_str = "APRS",
            rx_freq = hz,
            tx_freq = hz,
            rx_mod = RadioModulationType.FM,
            tx_mod = RadioModulationType.FM,
            bandwidth = RadioBandwidthType.WIDE,
            mute = true,
            pre_de_emph_bypass = true,
            scan = false,
            talk_around = false,
            tx_at_max_power = true,
            tx_at_med_power = false,
            tx_sub_audio = 0,
            rx_sub_audio = 0,
            tx_disable = false,
        };
        if (HasBanks) controller.SetRegion(SelectedBank);
        controller.WriteChannel(ch);
        if (slot < Slots.Count) { Slots[slot].Name = "APRS"; Slots[slot].RxMHz = mhz; }
        AppendLog($"Created APRS channel at {mhz.ToString("0.0000", CultureInfo.InvariantCulture)} MHz in slot {slot}.");
    }

    // ---- Audio clips (record / play / list) --------------------------------
    public ObservableCollection<AudioClipInfo> Clips { get; } = new();

    private AudioClipInfo? selectedClip;
    public AudioClipInfo? SelectedClip
    {
        get => selectedClip;
        set { if (SetField(ref selectedClip, value)) { OnPropertyChanged(nameof(HasSelectedClip)); ClipRenameText = value?.DisplayName ?? ""; } }
    }
    public bool HasSelectedClip => selectedClip != null;

    private string clipRenameText = "";
    public string ClipRenameText { get => clipRenameText; set => SetField(ref clipRenameText, value); }

    private bool recordingClip;
    public bool RecordingClip { get => recordingClip; private set { if (SetField(ref recordingClip, value)) OnPropertyChanged(nameof(RecordButtonText)); } }
    public string RecordButtonText => recordingClip ? "■ Stop" : "● Record";

    private IAudioCapture? clipMic;
    private System.IO.MemoryStream? clipRecording;
    private readonly object clipLock = new object();
    private IAudioPlayback? clipPlayback;

    private static string ClipsDir
    {
        get
        {
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir))
                baseDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return System.IO.Path.Combine(baseDir, "HTCommander", "AudioClips");
        }
    }

    private void LoadClips()
    {
        Clips.Clear();
        try
        {
            if (!System.IO.Directory.Exists(ClipsDir)) return;
            foreach (var path in System.IO.Directory.GetFiles(ClipsDir, "*.wav").OrderBy(p => p))
            {
                double secs = 0;
                try { using var r = new WavFileReader(path); secs = r.TotalBytes / (double)Math.Max(1, r.Format.SampleRate * r.Format.BlockAlign); }
                catch (Exception) { }
                Clips.Add(new AudioClipInfo
                {
                    FileName = System.IO.Path.GetFileName(path),
                    FullPath = path,
                    DisplayName = System.IO.Path.GetFileNameWithoutExtension(path),
                    Recorded = System.IO.File.GetLastWriteTime(path),
                    Seconds = secs,
                });
            }
        }
        catch (Exception ex) { AppendLog("Load clips failed: " + ex.Message); }
    }

    /// <summary>Toggles clip recording from the microphone (writes a WAV on stop).</summary>
    public void ToggleRecordClip()
    {
        if (recordingClip) { StopRecordClip(); return; }
        try
        {
            var mic = new PortAudioCapture { Format = AudioFormat.RadioPcm };
            mic.SetDevice(Settings.InputDeviceId);
            var buf = new System.IO.MemoryStream();
            mic.DataAvailable += (b, n) => { lock (clipLock) { buf.Write(b, 0, n); } };
            if (!mic.Start()) { mic.Dispose(); AppendLog("Clip record failed (no microphone)."); return; }
            clipMic = mic; clipRecording = buf; RecordingClip = true;
            AppendLog("Recording audio clip…");
        }
        catch (Exception ex) { AppendLog("Clip record failed: " + ex.Message); }
    }

    private void StopRecordClip()
    {
        var mic = clipMic; var buf = clipRecording;
        clipMic = null; clipRecording = null; RecordingClip = false;
        if (mic == null || buf == null) return;
        byte[] pcm;
        try { mic.Stop(); } catch (Exception) { }
        try { mic.Dispose(); } catch (Exception) { }
        lock (clipLock) { pcm = buf.ToArray(); }
        if (pcm.Length == 0) { AppendLog("Clip was empty (nothing recorded)."); return; }
        try
        {
            System.IO.Directory.CreateDirectory(ClipsDir);
            string name = $"clip-{DateTime.Now:yyyyMMdd-HHmmss}";
            string path = System.IO.Path.Combine(ClipsDir, name + ".wav");
            using (var w = new WavFileWriter(path, AudioFormat.RadioPcm)) w.Write(pcm, 0, pcm.Length);
            LoadClips();
            SelectedClip = Clips.FirstOrDefault(c => c.FileName == name + ".wav");
            AppendLog($"Saved clip {name} ({pcm.Length} bytes).");
        }
        catch (Exception ex) { AppendLog("Save clip failed: " + ex.Message); }
    }

    /// <summary>Plays the selected clip through the configured output device.</summary>
    public void PlaySelectedClip()
    {
        var clip = SelectedClip;
        if (clip == null) return;
        StopClipPlayback();
        Task.Run(() =>
        {
            try
            {
                using var r = new WavFileReader(clip.FullPath);
                var data = new byte[r.TotalBytes];
                int read = r.Read(data, 0, data.Length);
                var play = new PortAudioPlayback { Format = r.Format, Volume = Settings.OutputVolume };
                play.SetDevice(Settings.OutputDeviceId);
                if (!play.Start()) { play.Dispose(); dispatcher.Post(() => AppendLog("Clip playback failed (no output device).")); return; }
                clipPlayback = play;
                play.AddSamples(data, 0, read);
            }
            catch (Exception ex) { dispatcher.Post(() => AppendLog("Clip playback failed: " + ex.Message)); }
        });
    }

    public void StopClipPlayback()
    {
        var p = clipPlayback; clipPlayback = null;
        if (p == null) return;
        try { p.Stop(); } catch (Exception) { }
        try { p.Dispose(); } catch (Exception) { }
    }

    // Play a 16-bit PCM buffer (32k/mono) through the configured output device.
    private void PlayPcm16(byte[] pcm16)
    {
        StopClipPlayback();
        Task.Run(() =>
        {
            try
            {
                var play = new PortAudioPlayback { Format = AudioFormat.RadioPcm, Volume = Settings.OutputVolume };
                play.SetDevice(Settings.OutputDeviceId);
                if (!play.Start()) { play.Dispose(); dispatcher.Post(() => AppendLog("Playback failed (no output device).")); return; }
                clipPlayback = play;
                play.AddSamples(pcm16, 0, pcm16.Length);
            }
            catch (Exception ex) { dispatcher.Post(() => AppendLog("Playback failed: " + ex.Message)); }
        });
    }

    // 8-bit unsigned PCM (centre 128, what the Morse/DTMF engines emit) → 16-bit signed.
    private static byte[] Pcm8ToPcm16(byte[] pcm8)
    {
        var pcm16 = new byte[pcm8.Length * 2];
        for (int i = 0; i < pcm8.Length; i++)
        {
            short s = (short)((pcm8[i] - 128) * 256);
            pcm16[i * 2] = (byte)(s & 0xFF);
            pcm16[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return pcm16;
    }

    // ---- Voice transmit modes: Morse / DTMF (local generation + preview) ----
    public string[] VoiceModes { get; } = { "Morse", "DTMF" };
    private string voiceMode = "Morse";
    public string VoiceMode { get => voiceMode; set => SetField(ref voiceMode, value); }

    private string voiceModeText = "";
    public string VoiceModeText { get => voiceModeText; set => SetField(ref voiceModeText, value); }

    /// <summary>Generates Morse or DTMF audio from the text and plays it to the speaker.
    /// (On-air transmit of these tone modes is a follow-up — it needs the SBC buffer path.)</summary>
    public void PlayVoiceMode()
    {
        string text = (VoiceModeText ?? "").Trim();
        if (text.Length == 0) { AppendLog("Enter text for " + VoiceMode + "."); return; }
        try
        {
            byte[] pcm8 = string.Equals(VoiceMode, "DTMF", StringComparison.OrdinalIgnoreCase)
                ? HTCommander.radio.DmtfEngine.GenerateDmtfPcm(text)
                : HTCommander.radio.MorseCodeEngine.GenerateMorsePcm(text);
            if (pcm8.Length == 0) { AppendLog($"{VoiceMode}: nothing to play (no encodable characters)."); return; }
            PlayPcm16(Pcm8ToPcm16(pcm8));
            AppendLog($"Playing {VoiceMode}: {text}");
        }
        catch (Exception ex) { AppendLog($"{VoiceMode} failed: " + ex.Message); }
    }

    public void DeleteSelectedClip()
    {
        var clip = SelectedClip;
        if (clip == null) return;
        try { System.IO.File.Delete(clip.FullPath); } catch (Exception ex) { AppendLog("Delete clip failed: " + ex.Message); return; }
        Clips.Remove(clip);
        SelectedClip = null;
        AppendLog("Deleted clip.");
    }

    /// <summary>Renames the selected clip (renames the underlying WAV file).</summary>
    public void RenameSelectedClip()
    {
        var clip = SelectedClip;
        if (clip == null) return;
        string newName = System.IO.Path.GetFileNameWithoutExtension((ClipRenameText ?? "").Trim());
        if (newName.Length == 0 || newName.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        { AppendLog("Invalid clip name."); return; }
        try
        {
            string dest = System.IO.Path.Combine(ClipsDir, newName + ".wav");
            if (!string.Equals(dest, clip.FullPath, StringComparison.Ordinal))
            {
                if (System.IO.File.Exists(dest)) { AppendLog("A clip with that name already exists."); return; }
                System.IO.File.Move(clip.FullPath, dest);
            }
            LoadClips();
            SelectedClip = Clips.FirstOrDefault(c => c.FileName == newName + ".wav");
            AppendLog($"Renamed clip to {newName}.");
        }
        catch (Exception ex) { AppendLog("Rename clip failed: " + ex.Message); }
    }

    // ---- aprs.fi internet station lookup (plots stations beyond RF range) ----
    private static readonly HttpClient aprsFiHttp = CreateAprsFiHttp();
    private static HttpClient CreateAprsFiHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("HTCommander/0.2 (+https://github.com/mprattmd/HTCommander)");
        return c;
    }

    /// <summary>Stations fetched from aprs.fi (internet), plotted as a separate map layer.</summary>
    public ObservableCollection<AprsStationSummary> InternetStations { get; } = new();

    // The API key lives in Settings (SettingsViewModel), persisted to "AprsFiApiKey".
    private string aprsFiCallsigns = "";
    public string AprsFiCallsigns { get => aprsFiCallsigns; set { if (SetField(ref aprsFiCallsigns, value) && !loadingAprsFi) DataBroker.Dispatch(0, "AprsFiCallsigns", value, store: true); } }
    private bool aprsFiIncludeMe = true;
    public bool AprsFiIncludeMe { get => aprsFiIncludeMe; set { if (SetField(ref aprsFiIncludeMe, value) && !loadingAprsFi) DataBroker.Dispatch(0, "AprsFiIncludeMe", value, store: true); } }
    private string aprsFiStatus = "";
    public string AprsFiStatus { get => aprsFiStatus; private set => SetField(ref aprsFiStatus, value); }
    private bool loadingAprsFi;

    private void LoadAprsFiSettings()
    {
        loadingAprsFi = true;
        AprsFiCallsigns = DataBroker.GetValue<string>(0, "AprsFiCallsigns", "") ?? "";
        AprsFiIncludeMe = DataBroker.GetValue<bool>(0, "AprsFiIncludeMe", true);
        loadingAprsFi = false;
    }

    /// <summary>Looks up the configured callsigns on aprs.fi and plots them on the map.</summary>
    public async void FetchAprsFi()
    {
        string key = (DataBroker.GetValue<string>(0, "AprsFiApiKey", "") ?? "").Trim();
        if (key.Length == 0) { AprsFiStatus = "Enter your aprs.fi API key in Settings."; return; }

        var calls = new System.Collections.Generic.List<string>();
        if (AprsFiIncludeMe && HasValidCallsign)
            calls.Add(MyStationId > 0 ? $"{MyCallsign}-{MyStationId}" : MyCallsign);
        foreach (var c in (AprsFiCallsigns ?? "").Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            calls.Add(c.Trim().ToUpperInvariant());
        string names = string.Join(",", calls.Distinct().Take(20));
        if (names.Length == 0) { AprsFiStatus = "Add a callsign (or enable 'include my callsign')."; return; }

        AprsFiStatus = "Querying aprs.fi…";
        try
        {
            string url = $"https://api.aprs.fi/api/get?name={Uri.EscapeDataString(names)}&what=loc&apikey={Uri.EscapeDataString(key)}&format=json";
            string json = await aprsFiHttp.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string result = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
            if (result != "ok")
            {
                string desc = root.TryGetProperty("description", out var d) ? (d.GetString() ?? "query failed") : "query failed";
                dispatcher.Post(() => AprsFiStatus = "aprs.fi: " + desc);
                return;
            }
            var found = new System.Collections.Generic.List<AprsStationSummary>();
            if (root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in entries.EnumerateArray())
                {
                    string nm = e.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                    if (!TryGetJsonDouble(e, "lat", out double lat) || !TryGetJsonDouble(e, "lng", out double lon)) continue;
                    string sym = e.TryGetProperty("symbol", out var s) ? (s.GetString() ?? "") : "";
                    string com = e.TryGetProperty("comment", out var cm) ? (cm.GetString() ?? "") : "";
                    found.Add(new AprsStationSummary(nm, lat, lon, sym, com));
                }
            }
            dispatcher.Post(() =>
            {
                InternetStations.Clear();
                foreach (var st in found) InternetStations.Add(st);
                AprsFiStatus = found.Count > 0 ? $"aprs.fi: {found.Count} station(s) plotted." : "aprs.fi: no positions found for those calls.";
            });
        }
        catch (Exception ex) { dispatcher.Post(() => AprsFiStatus = "aprs.fi error: " + ex.Message); }
    }

    private static bool TryGetJsonDouble(JsonElement e, string prop, out double val)
    {
        val = 0;
        if (!e.TryGetProperty(prop, out var p)) return false;
        if (p.ValueKind == JsonValueKind.String) return double.TryParse(p.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out val);
        if (p.ValueKind == JsonValueKind.Number) { val = p.GetDouble(); return true; }
        return false;
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
