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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HTCommander.Core.Abstractions;

namespace HTCommander;

/// <summary>
/// Portable radio command/control over an <see cref="IRadioTransport"/>. Owns the
/// GAIA BASIC command/response loop and publishes parsed results to the
/// <c>DataBroker</c> (so any UI — Avalonia now, the WinForms MainForm later — is a
/// pure DataBroker consumer). This is the platform-neutral seed of the eventual
/// Radio.cs move into Core; it currently covers connect orchestration, HT status,
/// battery percentage and device info. The transport handles GAIA framing; this
/// builds the group/cmd/data payloads.
/// </summary>
public sealed class RadioController : IDisposable
{
    // GAIA BASIC protocol constants (mirror Radio.cs private enums).
    private const int GroupBasic = 2;
    private const int CmdGetDevInfo = 4;
    private const int CmdReadStatus = 5;
    private const int CmdRegisterNotification = 6;
    private const int CmdEventNotification = 9;
    private const int CmdReadSettings = 10;
    private const int CmdReadRfChannel = 13;
    private const int CmdWriteRfChannel = 14;     // WRITE_RF_CH
    private const int CmdSetRegion = 60;          // SET_REGION (selects the channel bank/zone)
    private const int CmdReadBssSettings = 33;
    private const int CmdWriteBssSettings = 34;   // WRITE_BSS_SETTINGS (beacon/ident)
    private const int CmdGetHtStatus = 20;
    private const int CmdSendData = 31;          // HT_SEND_DATA
    private const int MaxMtu = 50;               // TNC fragment payload size
    private const int StateIncorrect = 6;        // RadioCommandState.INCORRECT_STATE (channel busy)
    private const int NotifyHtStatusChanged = 1;
    private const int NotifyDataRxd = 2;
    private const int PowerStatusBatteryPercent = 4;

    private readonly IRadioTransport transport;
    private readonly int deviceId;
    private readonly ILogger? logger;
    private readonly DataBrokerClient broker = new DataBrokerClient();

    // Transmit (HT_SEND_DATA) fragment queue.
    private readonly object txLock = new object();
    private readonly System.Collections.Generic.List<TxFragment> txQueue = new();
    private bool txInFlight;
    private RadioHtStatus? lastStatus;           // cached for channel-busy gating + default ch/region
    private int lastDeviceChannelCount;          // channels-per-region (from dev info), for RefreshChannels
    private RadioChannelInfo[] channelArray;      // indexed by channel_id; published as "Channels" for APRS/Winlink/BBS lookups

    private sealed class TxFragment
    {
        public byte[] Frame; public bool IsLast; public int FragId;
        public TxFragment(byte[] frame, bool isLast, int fragId) { Frame = frame; IsLast = isLast; FragId = fragId; }
    }
    private readonly object gate = new object();
    private CancellationTokenSource? pollCts;
    private bool started;

    public RadioController(IRadioTransport transport, int deviceId = 0, ILogger? logger = null)
    {
        this.transport = transport;
        this.deviceId = deviceId;
        this.logger = logger;
    }

    /// <summary>Subscribes to the transport and begins connecting.</summary>
    public void Start()
    {
        lock (gate)
        {
            if (started) return;
            started = true;
        }
        transport.OnConnected += OnConnected;
        transport.ReceivedData += OnReceivedData;
        // Transmit AX.25 frames dispatched by higher layers (APRS handler, etc.).
        broker.Subscribe(deviceId, "TransmitDataFrame", OnTransmitDataFrame);
        // Beacon/ident settings writes dispatched by the UI.
        broker.Subscribe(deviceId, "SetBssSettings", OnSetBssSettings);
        transport.Connect();
    }

    private void OnTransmitDataFrame(int dev, string name, object data)
    {
        if (data is TransmitDataFrameData txData && txData.Packet != null)
            SendPacket(txData.Packet, txData.ChannelId, txData.RegionId);
    }

    private void OnSetBssSettings(int dev, string name, object data)
    {
        if (data is RadioBssSettings bss) WriteBssSettings(bss);
    }

    /// <summary>Disconnects and unsubscribes.</summary>
    public void Stop()
    {
        lock (gate)
        {
            if (!started) return;
            started = false;
        }
        StopPolling();
        transport.OnConnected -= OnConnected;
        transport.ReceivedData -= OnReceivedData;
        transport.Disconnect();
    }

    public void Dispose() { Stop(); broker.Dispose(); }

    private void OnConnected()
    {
        broker.Dispatch(deviceId, "State", "Connected", store: false);
        // Real-time pushes: HT status changes and received packet data.
        SendBasic(CmdRegisterNotification, new byte[] { NotifyHtStatusChanged });
        SendBasic(CmdRegisterNotification, new byte[] { NotifyDataRxd });
        SendBasic(CmdGetDevInfo, new byte[] { 3 });
        RequestBatteryPercent();
        SendBasic(CmdGetHtStatus, null);
        SendBasic(CmdReadSettings, null);
        SendBasic(CmdReadBssSettings, null);
        StartPolling();
    }

    private void OnReceivedData(IRadioTransport sender, Exception? error, byte[] v)
    {
        if (error != null || v == null || v.Length < 4) return;
        int group = (v[0] << 8) | v[1];
        int cmd = ((v[2] << 8) | v[3]) & 0x7FFF;
        if (group != GroupBasic) return;

        switch (cmd)
        {
            case CmdGetHtStatus: PublishHtStatus(v); break;
            case CmdReadStatus: HandleReadStatus(v); break;
            case CmdGetDevInfo: HandleDevInfo(v); break;
            case CmdReadRfChannel: HandleChannel(v); break;
            case CmdReadSettings: HandleSettings(v); break;
            case CmdReadBssSettings: HandleBssSettings(v); break;
            case CmdWriteBssSettings: if (v.Length > 4 && v[4] != 0) logger?.Debug($"WRITE_BSS_SETTINGS error: {v[4]}"); break;
            case CmdSendData: HandleSendDataResponse(v); break;
            case CmdEventNotification:
                if (v.Length > 4)
                {
                    if (v[4] == NotifyHtStatusChanged) PublishHtStatus(v);
                    else if (v[4] == NotifyDataRxd) HandleDataReceived(v);
                }
                break;
        }
    }

    // --- command builders --------------------------------------------------

    private void SendBasic(int cmd, byte[]? data)
    {
        int len = 4 + (data?.Length ?? 0);
        byte[] frame = new byte[len];
        frame[0] = (byte)(GroupBasic >> 8);
        frame[1] = (byte)(GroupBasic & 0xFF);
        frame[2] = (byte)(cmd >> 8);
        frame[3] = (byte)(cmd & 0xFF);
        if (data != null) Array.Copy(data, 0, frame, 4, data.Length);
        try { transport.EnqueueWrite(0, frame); } catch (Exception ex) { logger?.Debug("SendBasic: " + ex.Message); }
    }

    private void RequestBatteryPercent() => SendBasic(CmdReadStatus, new byte[] { 0x00, PowerStatusBatteryPercent });

    // --- transmit data (HT_SEND_DATA) ---

    /// <summary>
    /// Transmits an AX.25 packet over the radio's hardware TNC (1200 AFSK). The frame
    /// is fragmented to the Bluetooth MTU and queued; fragments are sent as the radio
    /// acks each one. ⚠ ON-AIR EMISSION — call only from an explicit operator action.
    /// Returns the number of payload bytes queued.
    /// </summary>
    public int SendPacket(AX25Packet packet, int channelId = -1, int regionId = -1)
    {
        if (packet == null) return 0;
        byte[] data;
        try { data = packet.ToByteArray(); } catch (Exception) { return 0; }
        if (data == null || data.Length == 0) return 0;

        if (channelId < 0) channelId = lastStatus?.curr_ch_id ?? 0;
        if (regionId < 0) regionId = lastStatus?.curr_region ?? 0;

        lock (txLock)
        {
            int fragid = 0;
            for (int i = 0; i < data.Length; i += MaxMtu)
            {
                int n = Math.Min(MaxMtu, data.Length - i);
                byte[] chunk = new byte[n];
                Array.Copy(data, i, chunk, 0, n);
                bool isLast = (i + n) == data.Length;
                byte[] frame = new TncDataFragment(isLast, fragid, chunk, channelId, regionId).toByteArray();
                txQueue.Add(new TxFragment(frame, isLast, fragid));
                fragid++;
            }
            KickTxLocked();
        }
        return data.Length;
    }

    private bool ChannelFree() => lastStatus == null || (!lastStatus.is_in_tx && lastStatus.rssi == 0);

    private void KickTxLocked()
    {
        if (txInFlight || txQueue.Count == 0) return;
        if (txQueue[0].FragId != 0 || ChannelFree())
        {
            txInFlight = true;
            SendBasic(CmdSendData, txQueue[0].Frame);
        }
    }

    private void HandleSendDataResponse(byte[] v)
    {
        lock (txLock)
        {
            if (txQueue.Count == 0) { txInFlight = false; return; }
            int err = v.Length > 4 ? v[4] : 0;

            if (err == StateIncorrect)
            {
                if (txQueue[0].FragId == 0)
                {
                    // First fragment rejected (channel busy) — retry when clear, else hold.
                    if (ChannelFree()) { txInFlight = true; SendBasic(CmdSendData, txQueue[0].Frame); }
                    else txInFlight = false;
                    return;
                }
                // Mid-message failure — drop the rest of this message.
                while (txQueue.Count > 0 && !txQueue[0].IsLast) txQueue.RemoveAt(0);
                if (txQueue.Count > 0) txQueue.RemoveAt(0);
            }
            else
            {
                txQueue.RemoveAt(0);   // fragment accepted
            }

            if (txQueue.Count > 0 && (txQueue[0].FragId != 0 || ChannelFree()))
            {
                txInFlight = true;
                SendBasic(CmdSendData, txQueue[0].Frame);
            }
            else txInFlight = false;
        }
    }

    // --- response handlers -------------------------------------------------

    private void PublishHtStatus(byte[] v)
    {
        if (v.Length < 7) return;
        try
        {
            var status = new RadioHtStatus(v);
            lastStatus = status;
            broker.Dispatch(deviceId, "HtStatus", status, store: false);
        }
        catch (Exception) { /* malformed frame */ }
    }

    private void HandleReadStatus(byte[] v)
    {
        if (v.Length < 8) return;
        int powerStatus = (v[5] << 8) | v[6];
        if (powerStatus == PowerStatusBatteryPercent)
            broker.Dispatch(deviceId, "BatteryAsPercentage", (int)v[7], store: false);
    }

    private void HandleDevInfo(byte[] v)
    {
        if (v.Length < 15) return;
        int channelCount = v[13];
        lastDeviceChannelCount = channelCount;
        if (channelArray == null || channelArray.Length != Math.Max(1, channelCount))
            channelArray = new RadioChannelInfo[Math.Max(1, channelCount)];
        int regionCount = ((v[11] & 0x03) << 4) + ((v[12] & 0xF0) >> 4);   // banks/zones
        var info = new RadioDeviceSummary(
            VendorId: v[5],
            ProductId: (v[6] << 8) | v[7],
            HardwareVersion: v[8],
            SoftwareVersion: (v[9] << 8) | v[10],
            ChannelCount: channelCount,
            RegionCount: regionCount);
        broker.Dispatch(deviceId, "DeviceInfo", info, store: false);

        // Read each channel now that we know how many there are.
        for (int ch = 0; ch < channelCount && ch < 256; ch++)
            SendBasic(CmdReadRfChannel, new byte[] { (byte)ch });
    }

    private void HandleChannel(byte[] v)
    {
        if (v.Length < 30) return;
        if (v[4] != 0) return;                       // command status: 0 = OK
        int rxRaw = GetInt(v, 10);
        int txRaw = GetInt(v, 6);
        string name = System.Text.Encoding.UTF8.GetString(v, 20, 10);
        int nul = name.IndexOf('\0');
        if (nul >= 0) name = name.Substring(0, nul);
        name = name.Trim();

        var ch = new RadioChannelSummary(
            ChannelId: v[5],
            Name: name,
            RxHz: rxRaw & 0x3FFFFFFF,
            TxHz: txRaw & 0x3FFFFFFF,
            Modulation: ModulationName((rxRaw >> 30) & 0x3));
        broker.Dispatch(deviceId, "Channel", ch, store: false);

        // Also publish the full editable channel record (the READ_RF_CH reply layout
        // matches RadioChannelInfo's byte ctor exactly) so the channel builder can
        // load existing channels for editing / re-writing, and maintain an array
        // indexed by channel_id stored as "Channels" (APRS/Winlink/BBS look it up).
        try
        {
            var full = new RadioChannelInfo(v);
            broker.Dispatch(deviceId, "ChannelInfo", full, store: false);
            if (channelArray != null && full.channel_id >= 0 && full.channel_id < channelArray.Length)
            {
                channelArray[full.channel_id] = full;
                broker.Dispatch(deviceId, "Channels", channelArray, store: true);
            }
        }
        catch { /* malformed channel row — skip */ }
    }

    /// <summary>
    /// Writes a memory channel to the radio (WRITE_RF_CH) and re-reads it so the UI
    /// reflects what the radio actually stored. This reconfigures the radio's memory;
    /// it is an operator-initiated action, never automatic.
    /// </summary>
    public void WriteChannel(RadioChannelInfo channel)
    {
        if (channel == null) return;
        SendBasic(CmdWriteRfChannel, channel.ToByteArray());
        SendBasic(CmdReadRfChannel, new byte[] { (byte)channel.channel_id });
    }

    /// <summary>
    /// Selects the active region (channel bank/zone). WRITE_RF_CH / READ_RF_CH then
    /// operate on that bank's channels. Mirrors the WinForms SET_REGION behavior.
    /// </summary>
    public void SetRegion(int regionId)
    {
        if (regionId < 0) return;
        SendBasic(CmdSetRegion, new byte[] { (byte)regionId });
    }

    /// <summary>Re-reads all channels for the current region (after a region switch).</summary>
    public void RefreshChannels()
    {
        int count = lastDeviceChannelCount > 0 ? lastDeviceChannelCount : 32;
        for (int ch = 0; ch < count && ch < 256; ch++)
            SendBasic(CmdReadRfChannel, new byte[] { (byte)ch });
    }

    private static string ModulationName(int mod) => mod switch { 0 => "FM", 1 => "AM", 2 => "DMR", _ => "?" };

    private void HandleSettings(byte[] v)
    {
        if (v.Length < 25) return;          // reads bit fields through msg[16]
        try
        {
            int channelA = ((v[5] & 0xF0) >> 4) + (v[14] & 0xF0);
            int channelB = (v[5] & 0x0F) + ((v[14] & 0x0F) << 4);
            var s = new RadioSettingsSummary(
                ChannelA: channelA,
                ChannelB: channelB,
                DoubleChannel: (v[6] & 0x30) >> 4,
                Scan: (v[6] & 0x80) != 0,
                SquelchLevel: v[6] & 0x0F,
                MicGain: (v[7] & 0x0E) >> 1,
                TxTimeLimit: v[8] & 0x1F,
                Vfo1TxPower: v[15] & 0x03,
                Vfo2TxPower: v[16] >> 6,
                PowerSavingMode: (v[9] & 0x01) != 0,
                ImperialUnit: (v[13] & 0x01) != 0);
            broker.Dispatch(deviceId, "Settings", s, store: false);
        }
        catch (Exception) { }
    }

    private void HandleBssSettings(byte[] v)
    {
        try { broker.Dispatch(deviceId, "BssSettings", new RadioBssSettings(v), store: false); }
        catch (Exception) { }              // ctor throws on a short frame
    }

    /// <summary>
    /// Writes beacon/ident (BSS) settings to the radio (WRITE_BSS_SETTINGS) and re-reads
    /// them so the UI reflects what was stored. These settings drive PTT-release beacon
    /// position and ident transmissions, so this is an operator-initiated action.
    /// </summary>
    public void WriteBssSettings(RadioBssSettings bss)
    {
        if (bss == null) return;
        SendBasic(CmdWriteBssSettings, bss.ToByteArray());
        SendBasic(CmdReadBssSettings, null);
    }

    // Big-endian 32-bit (network order), matching the radio protocol.
    private static int GetInt(byte[] b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    // --- received packets (DATA_RXD -> TNC reassembly -> AX.25 -> APRS) ---

    private TncDataFragment? frameAccumulator;

    private void HandleDataReceived(byte[] v)
    {
        TncDataFragment fragment;
        try { fragment = new TncDataFragment(v); }
        catch (Exception) { return; }
        fragment.encoding = TncDataFragment.FragmentEncodingType.HardwareAfsk1200;
        AccumulateFragment(fragment);
    }

    // Reassemble multi-fragment frames, then decode the completed packet.
    private void AccumulateFragment(TncDataFragment fragment)
    {
        if (frameAccumulator == null)
        {
            if (fragment.fragment_id == 0) frameAccumulator = fragment;
        }
        else
        {
            frameAccumulator = frameAccumulator.Append(fragment);
        }

        if (frameAccumulator != null && frameAccumulator.final_fragment)
        {
            TncDataFragment packet = frameAccumulator;
            frameAccumulator = null;
            packet.incoming = true;
            PublishPacket(packet);
        }
    }

    private void PublishPacket(TncDataFragment frame)
    {
        AX25Packet? ax;
        try { ax = AX25Packet.DecodeAX25Packet(frame); }
        catch (Exception) { return; }
        if (ax == null || ax.addresses == null || ax.addresses.Count < 2) return;

        string dest = ax.addresses[0].CallSignWithId;
        string src = ax.addresses[1].CallSignWithId;
        string info = ax.dataStr ?? string.Empty;

        // Digipeater path = any addresses past dest(0)/src(1).
        string path = ax.addresses.Count > 2
            ? string.Join(",", ax.addresses.Skip(2).Select(a => a.CallSignWithId))
            : string.Empty;

        aprsparser.AprsPacket? aprs = null;
        try { aprs = aprsparser.AprsPacket.Parse(ax); } catch (Exception) { }
        bool isAprs = aprs != null && !string.IsNullOrEmpty(info);

        double? lat = null, lon = null;
        if (aprs?.Position?.CoordinateSet != null && aprs.Position.IsValid())
        {
            lat = aprs.Position.CoordinateSet.Latitude.Value;
            lon = aprs.Position.CoordinateSet.Longitude.Value;
        }

        broker.Dispatch(deviceId, "PacketReceived",
            new ReceivedPacketSummary(src, dest, info, isAprs,
                TimeLocal: DateTime.Now,
                Path: path,
                AprsType: isAprs ? aprs!.DataType.ToString() : "",
                Symbol: isAprs ? $"{aprs!.SymbolTableIdentifier}{aprs.SymbolCode}" : "",
                Latitude: lat, Longitude: lon,
                Comment: aprs?.Comment ?? "",
                MessageText: aprs?.MessageData?.MsgText ?? "",
                MessageAddressee: aprs?.MessageData?.Addressee ?? ""),
            store: false);

        // When the APRS packet carries a valid position, publish a station fix too.
        if (aprs?.Position?.CoordinateSet != null && aprs.Position.IsValid())
        {
            var cs = aprs.Position.CoordinateSet;
            broker.Dispatch(deviceId, "AprsStation",
                new AprsStationSummary(
                    Callsign: src,
                    Latitude: cs.Latitude.Value,
                    Longitude: cs.Longitude.Value,
                    Symbol: $"{aprs.SymbolTableIdentifier}{aprs.SymbolCode}",
                    Comment: aprs.Comment ?? string.Empty),
                store: false);
        }
    }

    // --- HT-status / battery polling --------------------------------------

    private void StartPolling()
    {
        StopPolling();
        var cts = new CancellationTokenSource();
        lock (gate) { pollCts = cts; }
        CancellationToken ct = cts.Token;
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                SendBasic(CmdGetHtStatus, null);
                try { await Task.Delay(1500, ct); } catch (Exception) { break; }
            }
        });
    }

    private void StopPolling()
    {
        CancellationTokenSource? cts;
        lock (gate) { cts = pollCts; pollCts = null; }
        try { cts?.Cancel(); } catch (Exception) { }
    }
}

/// <summary>Device identity/capabilities decoded from a GET_DEV_INFO reply.</summary>
public sealed record RadioDeviceSummary(
    int VendorId, int ProductId, int HardwareVersion, int SoftwareVersion, int ChannelCount,
    int RegionCount = 1);

/// <summary>A single memory channel decoded from a READ_RF_CH reply.</summary>
public sealed record RadioChannelSummary(
    int ChannelId, string Name, long RxHz, long TxHz, string Modulation)
{
    public double RxMHz => RxHz / 1_000_000.0;
    public double TxMHz => TxHz / 1_000_000.0;
}

/// <summary>
/// A received AX.25/APRS packet, decoded from a DATA_RXD notification. The trailing
/// fields are populated for the per-packet detail view; they stay at their defaults
/// for non-APRS frames or fields the packet doesn't carry.
/// </summary>
public sealed record ReceivedPacketSummary(
    string Source, string Destination, string Info, bool IsAprs,
    DateTime TimeLocal = default, string Path = "",
    string AprsType = "", string Symbol = "",
    double? Latitude = null, double? Longitude = null,
    string Comment = "", string MessageText = "", string MessageAddressee = "")
{
    public bool HasPosition => Latitude.HasValue && Longitude.HasValue;
    public string TimeText => TimeLocal == default ? "" : TimeLocal.ToString("HH:mm:ss");
    public string PositionText => HasPosition ? $"{Latitude:0.0000}, {Longitude:0.0000}" : "";
}

/// <summary>An APRS station position fix decoded from a received packet.</summary>
public sealed record AprsStationSummary(
    string Callsign, double Latitude, double Longitude, string Symbol, string Comment);

/// <summary>Read-only subset of READ_SETTINGS worth displaying.</summary>
public sealed record RadioSettingsSummary(
    int ChannelA, int ChannelB, int DoubleChannel, bool Scan, int SquelchLevel,
    int MicGain, int TxTimeLimit, int Vfo1TxPower, int Vfo2TxPower,
    bool PowerSavingMode, bool ImperialUnit)
{
    public string DualWatch => DoubleChannel != 0 ? "On" : "Off";
    public string Vfo1Power => Vfo1TxPower switch { 0 => "Low", 1 => "Med", _ => "High" };
}
