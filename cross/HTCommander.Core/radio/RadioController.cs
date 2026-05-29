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
    private const int CmdEventNotification = 9;
    private const int CmdGetHtStatus = 20;
    private const int NotifyHtStatusChanged = 1;
    private const int PowerStatusBatteryPercent = 4;

    private readonly IRadioTransport transport;
    private readonly int deviceId;
    private readonly ILogger? logger;
    private readonly DataBrokerClient broker = new DataBrokerClient();
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
        transport.Connect();
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
        SendBasic(CmdGetDevInfo, new byte[] { 3 });
        RequestBatteryPercent();
        SendBasic(CmdGetHtStatus, null);
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
            case CmdEventNotification:
                if (v.Length > 4 && v[4] == NotifyHtStatusChanged) PublishHtStatus(v);
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

    // --- response handlers -------------------------------------------------

    private void PublishHtStatus(byte[] v)
    {
        if (v.Length < 7) return;
        try
        {
            var status = new RadioHtStatus(v);
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
        var info = new RadioDeviceSummary(
            VendorId: v[5],
            ProductId: (v[6] << 8) | v[7],
            HardwareVersion: v[8],
            SoftwareVersion: (v[9] << 8) | v[10],
            ChannelCount: v[13]);
        broker.Dispatch(deviceId, "DeviceInfo", info, store: false);
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
    int VendorId, int ProductId, int HardwareVersion, int SoftwareVersion, int ChannelCount);
