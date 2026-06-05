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
    private const int CmdWriteSettings = 11;      // WRITE_SETTINGS (used to set the active channel)
    private const int CmdReadRfChannel = 13;
    private const int CmdWriteRfChannel = 14;     // WRITE_RF_CH
    private const int CmdSetRegion = 60;          // SET_REGION (selects the channel bank/zone)
    private const int CmdReadBssSettings = 33;
    private const int CmdWriteBssSettings = 34;   // WRITE_BSS_SETTINGS (beacon/ident)
    private const int CmdGetHtStatus = 20;
    private const int CmdGetPosition = 76;        // GET_POSITION
    private const int CmdSetPosition = 32;        // SET_POSITION (push a serial-GPS fix to the radio)
    private const int CmdSendData = 31;          // HT_SEND_DATA
    private const int MaxMtu = 50;               // TNC fragment payload size
    private const int StateIncorrect = 6;        // RadioCommandState.INCORRECT_STATE (channel busy)
    private const int NotifyHtStatusChanged = 1;
    private const int NotifyDataRxd = 2;
    private const int NotifyPositionChange = 13;
    private const int PowerStatusBatteryPercent = 4;

    private readonly IRadioTransport transport;
    private readonly int deviceId;
    private readonly ILogger? logger;
    private readonly DataBrokerClient broker = new DataBrokerClient();

    // Transmit (HT_SEND_DATA) fragment queue.
    private readonly object txLock = new object();
    private readonly System.Collections.Generic.List<TxFragment> txQueue = new();
    private bool txInFlight;
    private int restoreRegionAfterTx = -1;       // bank to return to after a switched APRS send
    private int restoreChannelAfterTx = -1;      // active channel to return to after a switched APRS send
    private RadioHtStatus? lastStatus;           // cached for channel-busy gating + default ch/region
    private bool notificationsRegistered;        // registered (after GET_DEV_INFO); reset on stop
    private int lastDeviceChannelCount;          // channels-per-region (from dev info), for RefreshChannels
    private RadioChannelInfo[] channelArray;      // indexed by channel_id; published as "Channels" for APRS/Winlink/BBS lookups
    private int regionBeingRead;                  // the bank whose channels are currently being read (for APRS-channel region)
    // name -> (region, channel_id) across ALL banks. "Channels" is overwritten per bank
    // (channel ids repeat across banks), so name lookups there only see the last-loaded
    // bank. This map survives the whole sweep so Winlink/BBS can resolve a channel that
    // lives in a different bank than the one currently loaded. Published as "ChannelLocations".
    private readonly Dictionary<string, AprsChannelLocation> channelLocations = new(StringComparer.OrdinalIgnoreCase);

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
        // Channel lock/unlock for exclusive use (Winlink/BBS): switch region+channel,
        // disable scan/dual-watch for the session, then restore on unlock.
        broker.Subscribe(deviceId, "SetLock", OnSetLock);
        broker.Subscribe(deviceId, "SetUnlock", OnSetUnlock);
        // Serial-GPS fixes (device 1) are pushed to the radio as SET_POSITION.
        broker.Subscribe(1, "GpsData", OnGpsData);
        // Frames decoded by the software modem (DataFrame) flow through the same
        // decode/display/UniqueDataFrame path as hardware-TNC packets.
        broker.Subscribe(deviceId, "DataFrame", OnSoftModemFrame);
        transport.Connect();
    }

    private void OnSoftModemFrame(int dev, string name, object data)
    {
        if (data is TncDataFragment frag && frag.incoming) PublishPacket(frag);
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

    /// <summary>
    /// Pushes a fixed/manual position to the radio (SET_POSITION) — for a stationary
    /// station with no GPS. The radio then beacons this position. Operator-initiated.
    /// </summary>
    public void SetManualPosition(double lat, double lon, double altMetres = 0)
    {
        var gps = new HTCommander.Gps.GpsData
        {
            Latitude = lat, Longitude = lon, Altitude = altMetres,
            Speed = 0, Heading = 0, IsFixed = true, GpsTime = DateTime.UtcNow,
        };
        lastGpsLat = lat; lastGpsLon = lon;   // keep the serial-GPS throttle consistent
        try { SendBasic(CmdSetPosition, EncodeSetPosition(gps)); } catch (Exception) { }
        SendBasic(CmdGetPosition, null);       // read it back so the UI/map reflect it
    }

    // Last serial-GPS fix pushed to the radio (distance-throttled, like the WinForms app).
    private double lastGpsLat = double.NaN, lastGpsLon;

    private void OnGpsData(int dev, string name, object data)
    {
        if (!Connected || data is not HTCommander.Gps.GpsData gps || !gps.IsFixed) return;
        // Throttle: only push when the fix has moved more than ~10 m.
        if (!double.IsNaN(lastGpsLat) && HaversineMetres(lastGpsLat, lastGpsLon, gps.Latitude, gps.Longitude) < 10.0) return;
        lastGpsLat = gps.Latitude; lastGpsLon = gps.Longitude;
        try { SendBasic(CmdSetPosition, EncodeSetPosition(gps)); } catch (Exception) { }
    }

    private volatile bool connected;     // true between the transport's OnConnected and Stop()
    public bool Connected => connected;

    private static byte[] EncodeSetPosition(HTCommander.Gps.GpsData g)
    {
        int latRaw = (int)Math.Round(g.Latitude * 60.0 * 500.0);
        int lonRaw = (int)Math.Round(g.Longitude * 60.0 * 500.0);
        int alt = (int)Math.Round(g.Altitude);
        int speed = (int)Math.Round(g.Speed);
        int heading = (int)Math.Round(g.Heading);
        int timeRaw = g.GpsTime > DateTime.MinValue ? (int)new DateTimeOffset(g.GpsTime.ToUniversalTime()).ToUnixTimeSeconds() : 0;
        return new byte[]
        {
            (byte)((latRaw >> 16) & 0xFF), (byte)((latRaw >> 8) & 0xFF), (byte)(latRaw & 0xFF),
            (byte)((lonRaw >> 16) & 0xFF), (byte)((lonRaw >> 8) & 0xFF), (byte)(lonRaw & 0xFF),
            (byte)((alt >> 8) & 0xFF), (byte)(alt & 0xFF),
            (byte)((speed >> 8) & 0xFF), (byte)(speed & 0xFF),
            (byte)((heading >> 8) & 0xFF), (byte)(heading & 0xFF),
            (byte)((timeRaw >> 24) & 0xFF), (byte)((timeRaw >> 16) & 0xFF), (byte)((timeRaw >> 8) & 0xFF), (byte)(timeRaw & 0xFF),
            0, 0,
        };
    }

    private static double HaversineMetres(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Disconnects and unsubscribes.</summary>
    public void Stop()
    {
        lock (gate)
        {
            if (!started) return;
            started = false;
        }
        connected = false;
        lastGpsLat = double.NaN;            // re-send an initial SET_POSITION after reconnect
        restoreRegionAfterTx = -1;
        restoreChannelAfterTx = -1;
        notificationsRegistered = false;
        lock (txLock) { txQueue.Clear(); txInFlight = false; }   // drop any stale fragments
        StopPolling();
        transport.OnConnected -= OnConnected;
        transport.ReceivedData -= OnReceivedData;
        transport.Disconnect();
    }

    public void Dispose() { Stop(); broker.Dispose(); }

    private void OnConnected()
    {
        connected = true;
        broker.Dispatch(deviceId, "State", "Connected", store: false);
        // Notifications (incl. DATA_RXD for inbound packets) are registered after the
        // GET_DEV_INFO reply — see HandleDevInfo. (An older comment here claimed DATA_RXD
        // must NOT be registered; that was WinForms-era and is false on this firmware/BLE
        // path — without it we get zero RX while the official app on the same radio works.)
        SendBasic(CmdGetDevInfo, new byte[] { 3 });
        RequestBatteryPercent();
        SendBasic(CmdGetHtStatus, null);
        SendBasic(CmdReadSettings, null);
        SendBasic(CmdReadBssSettings, null);
        SendBasic(CmdGetPosition, null);
        StartPolling();
    }

    private void OnReceivedData(IRadioTransport sender, Exception? error, byte[] v)
    {
        if (error != null || v == null || v.Length < 4) return;
        int group = (v[0] << 8) | v[1];
        int cmd = ((v[2] << 8) | v[3]) & 0x7FFF;
        // DIAG: log every raw inbound frame so we can prove what the radio actually pushes
        // (esp. whether EVENT_NOTIFICATION/DATA_RXD ever arrive, or get dropped at the group filter).
        logger?.Debug($"RX FRAME: group={group} cmd={cmd} len={v.Length}{(v.Length > 4 ? $" b4={v[4]}" : "")}");
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
            case CmdGetPosition: HandlePosition(v); break;
            case CmdRegisterNotification:
                // Reply to our REGISTER_NOTIFICATION. Log the status so we can tell whether
                // the radio actually ACCEPTED the subscription (benlink #6: only some event
                // values get a success reply). v[4] is typically the status/echoed type.
                logger?.Debug($"REGISTER_NOTIFICATION reply: {v.Length}B{(v.Length > 4 ? $", byte4={v[4]}" : "")}");
                break;
            case CmdEventNotification:
                if (v.Length > 4)
                {
                    // Unsolicited event channel. Logged so we can PROVE it is alive: HtStatus
                    // also arrives via polling (GET_HT_STATUS), so only an EVENT_NOTIFICATION
                    // line here confirms the radio pushes events. DATA_RXD (inbound packets)
                    // arrives ONLY here — if we never see type=2, RX is dead regardless of TX.
                    logger?.Debug($"EVENT_NOTIFICATION: type={v[4]}, {v.Length}B");
                    if (v[4] == NotifyHtStatusChanged) PublishHtStatus(v);
                    else if (v[4] == NotifyDataRxd) HandleDataReceived(v);
                    else if (v[4] == NotifyPositionChange) RequestPosition();   // re-fetch the authoritative GET_POSITION reply
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

    /// <summary>Requests a fresh GPS position from the radio (GET_POSITION); the reply
    /// is published to DataBroker "Position". Operator-/UI-initiated.</summary>
    public void RequestPosition() => SendBasic(CmdGetPosition, null);

    // 24-bit two's-complement → decimal degrees (protocol scale 60*500).
    private static double DecodeDegrees(int raw24)
    {
        if ((raw24 & 0x800000) != 0) raw24 |= unchecked((int)0xFF000000);
        else raw24 &= 0x00FFFFFF;
        return raw24 / 60.0 / 500.0;
    }

    private void HandlePosition(byte[] v)
    {
        if (v.Length < 5) return;                       // need the command-status byte at v[4]
        int status = v[4];                              // RadioCommandState; 0 = SUCCESS
        if (status != 0 || v.Length < 11)
        {
            broker.Dispatch(deviceId, "Position",
                new RadioPositionInfo(false, 0, 0, 0, 0, 0, default, 0), store: false);
            return;
        }
        try
        {
            double lat = DecodeDegrees((v[5] << 16) | (v[6] << 8) | v[7]);
            double lon = DecodeDegrees((v[8] << 16) | (v[9] << 8) | v[10]);
            int alt = 0, speed = 0, heading = 0, accuracy = 0;
            DateTime t = default;
            if (v.Length > 22)
            {
                alt = (v[11] << 8) | v[12];
                speed = (v[13] << 8) | v[14];
                heading = (v[15] << 8) | v[16];
                long timeRaw = ((long)v[17] << 24) | ((long)v[18] << 16) | ((long)v[19] << 8) | v[20];
                if (timeRaw > 0) t = DateTimeOffset.FromUnixTimeSeconds(timeRaw).UtcDateTime;
                accuracy = (v[21] << 8) | v[22];
            }
            broker.Dispatch(deviceId, "Position",
                new RadioPositionInfo(true, lat, lon, alt, speed, heading, t, accuracy), store: false);
        }
        catch (Exception) { }
    }

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
        // Regulatory gate: never key the transmitter unless the operator has enabled
        // transmit (callsign + Allow-Transmit). Matches the WinForms guard.
        if (!broker.GetValue<bool>(0, "AllowTransmit", false))
        {
            logger?.Debug("SendPacket blocked: transmit not allowed (enable Allow-Transmit).");
            return 0;
        }
        byte[] data;
        try { data = packet.ToByteArray(); } catch (Exception) { return 0; }
        if (data == null || data.Length == 0) return 0;

        if (channelId < 0) channelId = lastStatus?.curr_ch_id ?? 0;
        int currentRegion = lastStatus?.curr_region ?? 0;
        if (regionId < 0) regionId = currentRegion;

        // The radio transmits HT_SEND_DATA on its ACTIVE region+channel (the channel id
        // in the payload alone doesn't select it). So to send on the APRS channel we set
        // the active region AND active channel to it first, then restore afterwards.
        // Without this, the burst keys whatever channel is currently active (e.g. Winlink).
        int currentChannel = lastStatus?.curr_ch_id ?? ActiveChannelA;
        bool switched = false;
        // Under a session lock (Winlink/BBS), the radio is already held on the locked
        // channel and MUST stay there for the whole connected-mode session — restoring
        // after each burst would move it off-channel before the peer's reply arrives, so
        // the handshake would never complete. Only the one-shot APRS path switches+restores.
        bool isLocked = lockState != null && lockState.IsLocked;
        // Diagnose no-reply connects: show the TARGET region/channel vs. what the radio
        // actually reports as its current region/channel. If they disagree, the lock's
        // region switch didn't take and we're keying the wrong memory (e.g. the APRS
        // channel). channelArray only holds the last-loaded bank (regionBeingRead) and
        // channel ids repeat across banks, so its freq is only meaningful when that bank
        // matches the radio's current region — label it honestly.
        int curRegion = lastStatus?.curr_region ?? -1;
        int curCh = lastStatus?.curr_ch_id ?? -1;
        string chDetail = $", radio-now region={curRegion} ch={curCh}, loaded-bank={regionBeingRead}";
        // channelArray holds only the last-loaded bank, and channel ids repeat across banks,
        // so its freq is ONLY meaningful when that bank == the radio's current region.
        if (regionBeingRead == curRegion && channelArray != null && curCh >= 0 && curCh < channelArray.Length && channelArray[curCh] is RadioChannelInfo ci)
        {
            string power = ci.tx_at_max_power ? "HIGH" : (ci.tx_at_med_power ? "MED" : "LOW");
            chDetail += $", ch[{curCh}]='{ci.name_str}' tx={ci.tx_freq / 1e6:0.0000} rx={ci.rx_freq / 1e6:0.0000} mod={ci.tx_mod}/{ci.rx_mod} tone tx={ci.tx_sub_audio} rx={ci.rx_sub_audio} bw={ci.bandwidth} pwr={power}{(ci.tx_disable ? " TX-DISABLED" : "")}";
        }
        else
        {
            chDetail += $", (ch freq unknown: bank {regionBeingRead} loaded, radio on region {curRegion} — open the Channels tab on region {curRegion} to load it)";
        }
        logger?.Debug($"SendPacket: {data.Length}B, target region {regionId} ch {channelId}, locked={isLocked}, free={ChannelFree()}{chDetail}");
        // Dump the exact AX.25 frame bytes (pre-FCS; the radio appends HDLC flags + FCS)
        // so a no-reply connect can be verified byte-for-byte against the standard.
        logger?.Debug($"  AX25 frame ({data.Length}B): {CoreUtils.BytesToHex(data)}");
        lock (txLock)
        {
            bool needSwitch = !isLocked && rawSettings != null && channelId >= 0 &&
                              (regionId != currentRegion || channelId != currentChannel);
            if (needSwitch)
            {
                if (restoreRegionAfterTx < 0) { restoreRegionAfterTx = currentRegion; restoreChannelAfterTx = currentChannel; }
                if (regionId != currentRegion) SetRegion(regionId);
                WriteActiveChannel(channelId);
                switched = true;
                logger?.Debug($"APRS TX: locked to bank {regionId} channel {channelId} (restore {restoreRegionAfterTx}/{restoreChannelAfterTx}).");
            }

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
            if (!switched) KickTxLocked();
        }

        // Let the radio settle on the new channel before the first burst.
        if (switched)
            Task.Run(async () => { try { await Task.Delay(800); } catch (Exception) { } lock (txLock) { KickTxLocked(); } });

        return data.Length;
    }

    // Restore the operator's region+channel once a switched send has fully drained.
    private void MaybeRestoreRegionLocked()
    {
        if (txQueue.Count == 0 && restoreRegionAfterTx >= 0)
        {
            int r = restoreRegionAfterTx, c = restoreChannelAfterTx;
            restoreRegionAfterTx = -1; restoreChannelAfterTx = -1;
            SetRegion(r);
            if (c >= 0) WriteActiveChannel(c);
            logger?.Debug($"APRS TX: restored bank {r} channel {c}.");
        }
    }

    // Channel is clear to transmit when the radio is neither transmitting nor receiving.
    // (Matches WinForms IsTncFree. An earlier port used rssi==0, which never went true on
    // a channel with any noise, so the first fragment was gated off forever — no TX.)
    private bool ChannelFree() => lastStatus == null || (!lastStatus.is_in_tx && !lastStatus.is_in_rx);

    private void KickTxLocked()
    {
        if (txInFlight || txQueue.Count == 0) return;
        if (txQueue[0].FragId != 0 || ChannelFree())
        {
            txInFlight = true;
            logger?.Debug($"TX HT_SEND_DATA: frag {txQueue[0].FragId}, {txQueue[0].Frame.Length}B (KickTxLocked)");
            SendBasic(CmdSendData, txQueue[0].Frame);
        }
    }

    private void HandleSendDataResponse(byte[] v)
    {
        lock (txLock)
        {
            if (txQueue.Count == 0) { txInFlight = false; return; }
            int err = v.Length > 4 ? v[4] : 0;
            logger?.Debug($"TX HT_SEND_DATA response: err={err}, queue={txQueue.Count}, free={ChannelFree()}");

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
            else { txInFlight = false; MaybeRestoreRegionLocked(); }
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
            broker.Dispatch(deviceId, "HtStatus", status, store: true);   // store so GetValue("HtStatus") works (Winlink/SoftwareModem region selection)

            // Mirror WinForms ProcessTncQueue: every status update is our signal that the
            // channel may have cleared, so resume any held TX. Without this, a single busy
            // moment (e.g. right after a lock's channel switch) stalls the queue forever —
            // nothing else re-kicks it, so no packets ever go out.
            lock (txLock)
            {
                bool channelFree = !status.is_in_tx && !status.is_in_rx;
                if (channelFree && !txInFlight && txQueue.Count > 0)
                {
                    txInFlight = true;
                    logger?.Debug($"TX HT_SEND_DATA: frag {txQueue[0].FragId}, {txQueue[0].Frame.Length}B (status re-kick)");
                    SendBasic(CmdSendData, txQueue[0].Frame);
                }
                else if (txInFlight && status.is_in_rx)
                {
                    txInFlight = false;   // radio went to RX: clear a stuck in-flight flag so the queue resumes
                }
            }
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

        // Register real-time notifications now (after GET_DEV_INFO), matching WinForms.
        // NOT DATA_RXD — see OnConnected. Guard so a re-read of dev info doesn't re-register.
        if (!notificationsRegistered)
        {
            notificationsRegistered = true;
            logger?.Debug("HandleDevInfo: registering notifications (HtStatus, Position)");
            SendBasic(CmdRegisterNotification, new byte[] { NotifyHtStatusChanged });
            SendBasic(CmdRegisterNotification, new byte[] { NotifyPositionChange });
            // Do NOT register DATA_RXD. The WinForms app never does, yet still receives
            // unsolicited DATA_RXD events. On-air testing (2026-06-05) confirmed that
            // explicitly subscribing DATA_RXD makes THIS firmware go silent on the entire
            // event channel — no DATA_RXD AND no HT_STATUS_CHANGED (knob/volume changes
            // produced zero cmd=9 events). Inbound packets flow automatically without it.
        }

        // The initial read is for the radio's current bank.
        regionBeingRead = lastStatus?.curr_region ?? 0;

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
            // Record this channel's bank+id by name across all banks (see field comment).
            if (!string.IsNullOrEmpty(name))
            {
                channelLocations[name] = new AprsChannelLocation(regionBeingRead, full.channel_id);
                broker.Dispatch(deviceId, "ChannelLocations", channelLocations, store: true);  // store so GetValue can read it (like "Channels")
            }
            // Remember which bank the user's designated APRS channel lives in, so APRS TX
            // targets the right (region, channel) pair — channel ids repeat across banks.
            // The APRS channel is chosen in the UI ("AprsChannelName"); default "APRS".
            string aprsName = broker.GetValue<string>(0, "AprsChannelName", "APRS");
            if (!string.IsNullOrWhiteSpace(aprsName) && string.Equals(name, aprsName.Trim(), StringComparison.OrdinalIgnoreCase))
                broker.Dispatch(deviceId, "AprsChannel",
                    new AprsChannelLocation(regionBeingRead, full.channel_id), store: true);
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
        regionBeingRead = regionId;     // channels read after this belong to this bank
        SendBasic(CmdSetRegion, new byte[] { (byte)regionId });
    }

    /// <summary>Currently-configured active channel (VFO A), from the last READ_SETTINGS.</summary>
    private int ActiveChannelA => rawSettings != null && rawSettings.Length > 14
        ? ((rawSettings[5] & 0xF0) >> 4) + (rawSettings[14] & 0xF0) : -1;

    /// <summary>
    /// Sets the radio's active channel (VFO A) via WRITE_SETTINGS, changing ONLY the
    /// channel_a bits and preserving every other setting byte-for-byte. The radio
    /// transmits HT_SEND_DATA on its active channel, so this is how APRS/Winlink target
    /// a specific channel. Returns false if settings haven't been read yet.
    /// </summary>
    public bool WriteActiveChannel(int channelId)
    {
        if (rawSettings == null || rawSettings.Length <= 14 || channelId < 0) return false;
        int n = rawSettings.Length - 5;
        byte[] buf = new byte[n];
        Array.Copy(rawSettings, 5, buf, 0, n);          // settings payload (skip 5-byte header)
        // channel_a: low nibble in buf[0] high nibble, high nibble in buf[9] high nibble.
        buf[0] = (byte)((buf[0] & 0x0F) | ((channelId & 0x0F) << 4));
        buf[9] = (byte)((buf[9] & 0x0F) | (channelId & 0xF0));
        SendBasic(CmdWriteSettings, buf);
        SendBasic(CmdReadSettings, null);               // re-read so our cached settings stay accurate
        return true;
    }

    /// <summary>
    /// Sets the channel the radio's built-in beacon transmits on (auto_share_loc_ch),
    /// preserving every other setting byte-for-byte. Mirrors the WinForms beacon editor:
    /// the stored value is channel_id + 1 (0 = "Current"/tuned channel), in buf[5] low 5 bits
    /// (read-frame byte 10). So selecting the radio beacon can target the APRS channel
    /// instead of whatever channel the operator happens to be tuned to. Returns false if
    /// settings haven't been read yet.
    /// </summary>
    public bool WriteAutoShareLocChannel(int channelId)
    {
        if (rawSettings == null || rawSettings.Length <= 10 || channelId < 0) return false;
        int value = (channelId + 1) & 0x1F;             // 1-based; 0 means "Current" (tuned)
        int n = rawSettings.Length - 5;
        byte[] buf = new byte[n];
        Array.Copy(rawSettings, 5, buf, 0, n);          // settings payload (skip 5-byte header)
        buf[5] = (byte)((buf[5] & 0xE0) | value);       // auto_share_loc_ch = low 5 bits of buf[5]
        SendBasic(CmdWriteSettings, buf);
        SendBasic(CmdReadSettings, null);               // re-read so our cached settings stay accurate
        logger?.Debug($"Radio beacon channel set to auto_share_loc_ch={value} (channel_id {channelId}).");
        return true;
    }

    // ---- Channel lock (Winlink/BBS exclusive use) -------------------------
    // Ported from the WinForms Radio.OnSetLockEvent/OnSetUnlockEvent: a higher layer
    // (Winlink, BBS) locks the radio to a specific region+channel for the session, with
    // scan and dual-watch disabled, and we restore the prior state on unlock. Without
    // this, those features transmit on whatever channel the operator is tuned to.
    private RadioLockState? lockState;
    private int savedLockRegionId, savedLockChannelId, savedLockDualWatch;
    private bool savedLockScan;

    private void OnSetLock(int dev, string name, object data)
    {
        if (data is not SetLockData lockData) return;
        if (lockState != null) return;                       // already locked
        if (rawSettings == null || rawSettings.Length <= 14) return;

        savedLockRegionId = lastStatus?.curr_region ?? 0;
        savedLockChannelId = ActiveChannelA;
        savedLockScan = (rawSettings[6] & 0x80) != 0;
        savedLockDualWatch = (rawSettings[6] & 0x30) >> 4;

        int targetRegion = lockData.RegionId >= 0 ? lockData.RegionId : savedLockRegionId;
        int targetChannel = lockData.ChannelId >= 0 ? lockData.ChannelId : savedLockChannelId;

        lock (txLock) { txQueue.Clear(); txInFlight = false; }   // start the locked session with a clean TX queue
        lockState = new RadioLockState { IsLocked = true, Usage = lockData.Usage, RegionId = targetRegion, ChannelId = targetChannel };
        broker.Dispatch(deviceId, "LockState", lockState, store: true);
        logger?.Debug($"Radio locked for '{lockData.Usage}': region {targetRegion}, channel {targetChannel} (was region {savedLockRegionId}, channel {savedLockChannelId}).");

        if (targetRegion != savedLockRegionId) SetRegion(targetRegion);
        // scan + dual-watch off while locked; also force a real TNC preamble (~500ms delay,
        // ~50ms tail) since the radio defaults kiss_tx_delay to 0 — without it the peer
        // never decodes our SABM. Left set after unlock (harmless, benefits all packet TX).
        WriteLockSettings(targetChannel, scan: false, doubleChannel: 0, tncTxDelay: 50, tncTxTail: 5);
    }

    private void OnSetUnlock(int dev, string name, object data)
    {
        if (data is not SetUnlockData unlockData) return;
        if (lockState == null) return;
        if (lockState.Usage != unlockData.Usage) return;     // only the holder may unlock
        if (rawSettings == null || rawSettings.Length <= 14) return;

        lock (txLock) { txQueue.Clear(); txInFlight = false; }   // drop any unsent fragments from the session
        int curRegion = lastStatus?.curr_region ?? savedLockRegionId;
        if (savedLockRegionId != curRegion && savedLockRegionId >= 0) SetRegion(savedLockRegionId);
        WriteLockSettings(savedLockChannelId, savedLockScan, savedLockDualWatch);   // restore prior state

        string usage = lockState.Usage;
        lockState = null;
        broker.Dispatch(deviceId, "LockState", new RadioLockState { IsLocked = false, Usage = usage }, store: true);
        logger?.Debug($"Radio unlocked from '{usage}': restored region {savedLockRegionId}, channel {savedLockChannelId}.");
    }

    /// <summary>
    /// Writes channel_a + scan + dual-watch via WRITE_SETTINGS, preserving every other
    /// setting byte-for-byte. Used by the channel lock (mirrors WinForms ToByteArray):
    /// scan is bit 7 and double_channel is bits 5-4 of settings byte 1 (read-frame byte 6).
    /// </summary>
    private bool WriteLockSettings(int channelId, bool scan, int doubleChannel, int tncTxDelay = -1, int tncTxTail = -1)
    {
        if (rawSettings == null || rawSettings.Length <= 14 || channelId < 0) return false;
        int n = rawSettings.Length - 5;
        byte[] buf = new byte[n];
        Array.Copy(rawSettings, 5, buf, 0, n);
        buf[0] = (byte)((buf[0] & 0x0F) | ((channelId & 0x0F) << 4));    // channel_a low nibble
        buf[9] = (byte)((buf[9] & 0x0F) | (channelId & 0xF0));           // channel_a high nibble
        buf[1] = (byte)((buf[1] & ~0xB0) | (scan ? 0x80 : 0) | ((doubleChannel & 0x03) << 4));  // scan + double_channel, keep aghfp/squelch
        // Optional TNC preamble: kiss_tx_delay / kiss_tx_tail are settings bytes 12/13
        // (10ms per count). The radio ships these at 0, leaving the hardware TNC with no
        // preamble, so the far end's modem can't lock before our frame data → no decode →
        // no UA. Connected-mode sessions force a sane value so the SABM is decodable.
        if (tncTxDelay >= 0 && n > 12) buf[12] = (byte)Math.Min(255, tncTxDelay);
        if (tncTxTail  >= 0 && n > 13) buf[13] = (byte)Math.Min(255, tncTxTail);
        SendBasic(CmdWriteSettings, buf);
        SendBasic(CmdReadSettings, null);
        return true;
    }

    /// <summary>Re-reads all channels for the current region (after a region switch).</summary>
    public void RefreshChannels()
    {
        int count = lastDeviceChannelCount > 0 ? lastDeviceChannelCount : 32;
        for (int ch = 0; ch < count && ch < 256; ch++)
            SendBasic(CmdReadRfChannel, new byte[] { (byte)ch });
    }

    private static string ModulationName(int mod) => mod switch { 0 => "FM", 1 => "AM", 2 => "DMR", _ => "?" };

    private byte[] rawSettings;             // last READ_SETTINGS reply (for WRITE_SETTINGS round-trip)

    private void HandleSettings(byte[] v)
    {
        // Capture rawSettings whenever the frame is long enough for the active-channel /
        // lock writes (they touch index 14), even if it's too short for the full summary
        // parse — otherwise a short frame would silently leave the channel lock disabled.
        if (v.Length > 14) rawSettings = (byte[])v.Clone();
        if (v.Length < 25) return;          // need bit fields through msg[16] for the summary below
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
            // TNC preamble diagnostics: kiss_tx_delay / kiss_tx_tail live at settings
            // bytes 12/13 (read-frame v[17]/v[18]); benlink's layout is confirmed here by
            // vfo tx_power at v[15]/v[16] matching. Units are the KISS standard 10ms/count.
            // If tx_delay is very low, the far end's modem can't lock before our SABM data
            // arrives → frame undecodable → no UA, even though TX is accepted (err=0).
            if (v.Length > 18)
                logger?.Debug($"Radio TNC preamble: kiss_tx_delay={v[17]} (~{v[17] * 10}ms), kiss_tx_tail={v[18]} (~{v[18] * 10}ms) [settings bytes 12/13]");
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
        catch (Exception) { logger?.Debug($"RX data frame: undecodable ({v.Length}B raw)"); return; }
        fragment.encoding = TncDataFragment.FragmentEncodingType.HardwareAfsk1200;
        logger?.Debug($"RX data frame: frag {fragment.fragment_id}, {v.Length}B, final={fragment.final_fragment}");
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

    // 3-second dedup of incoming frames before re-publishing as UniqueDataFrame
    // (the radio can deliver the same frame more than once). Mirrors the Windows
    // FrameDeduplicator, which src/ runs before UniqueDataFrame is dispatched.
    private readonly System.Collections.Generic.Dictionary<string, DateTime> recentFrames = new();
    private const double DedupWindowSeconds = 3.0;

    private void PublishPacket(TncDataFragment frame)
    {
        // Stamp the receiving radio so connected-mode sessions/BBS (which match on
        // RadioDeviceId) accept it; then re-publish the raw frame as "UniqueDataFrame".
        // The AX.25 link layer needs every frame (incl. retransmissions), so this is
        // NOT deduplicated — dedup is applied only to the display dispatches below.
        frame.RadioDeviceId = deviceId;
        // Incoming frames carry no channel id — backfill from the current channel, look up
        // its name, and tag the session usage so connected-mode consumers (BBS) and
        // channel-scoped handlers attribute the frame correctly. Mirrors WinForms.
        if (frame.channel_id < 0 && lastStatus != null) frame.channel_id = lastStatus.curr_ch_id;
        if (string.IsNullOrEmpty(frame.channel_name) && frame.channel_id >= 0 &&
            channelArray != null && frame.channel_id < channelArray.Length && channelArray[frame.channel_id] != null)
            frame.channel_name = channelArray[frame.channel_id].name_str;
        if (lockState != null && lockState.IsLocked && frame.channel_id == lockState.ChannelId)
            frame.usage = lockState.Usage;
        try { broker.Dispatch(deviceId, "UniqueDataFrame", frame, store: false); }
        catch (Exception) { /* never let routing break packet display */ }

        // De-duplicate only the UI/display path (a frame heard twice within a few
        // seconds shouldn't double-list or re-plot).
        bool freshForDisplay = true;
        try
        {
            string key = frame.ToHex();
            if (!string.IsNullOrEmpty(key))
            {
                var now = DateTime.UtcNow;
                lock (recentFrames)
                {
                    var cutoff = now.AddSeconds(-DedupWindowSeconds);
                    foreach (var k in recentFrames.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
                        recentFrames.Remove(k);
                    freshForDisplay = !recentFrames.ContainsKey(key);
                    if (freshForDisplay) recentFrames[key] = now;
                }
            }
        }
        catch (Exception) { }
        if (!freshForDisplay) return;   // skip display for a duplicate; the session/BBS already got the raw frame

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

/// <summary>Which bank (region) + channel id the APRS channel lives in, so APRS TX
/// targets the right channel even when channel ids repeat across banks.</summary>
public sealed record AprsChannelLocation(int RegionId, int ChannelId);

/// <summary>The radio's own GPS position decoded from a GET_POSITION reply.</summary>
public sealed record RadioPositionInfo(
    bool Locked, double Latitude, double Longitude,
    int AltitudeM, int SpeedKnots, int HeadingDeg, DateTime TimeUtc, int Accuracy)
{
    public string PositionText => $"{Latitude:0.00000}, {Longitude:0.00000}";
    public string DetailText => $"Alt {AltitudeM} m · {SpeedKnots} kn · hdg {HeadingDeg}° · ±{Accuracy} m";
    public string TimeText => TimeUtc == default ? "" : TimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>Read-only subset of READ_SETTINGS worth displaying.</summary>
public sealed record RadioSettingsSummary(
    int ChannelA, int ChannelB, int DoubleChannel, bool Scan, int SquelchLevel,
    int MicGain, int TxTimeLimit, int Vfo1TxPower, int Vfo2TxPower,
    bool PowerSavingMode, bool ImperialUnit)
{
    public string DualWatch => DoubleChannel != 0 ? "On" : "Off";
    public string Vfo1Power => Vfo1TxPower switch { 0 => "Low", 1 => "Med", _ => "High" };
}
