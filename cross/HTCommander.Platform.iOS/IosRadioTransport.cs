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
using CoreBluetooth;
using Foundation;
using HTCommander.Core.Abstractions;

namespace HTCommander.Platform.iOS;

/// <summary>
/// BLE/GATT transport to the radio via Core Bluetooth. The radio carries the same
/// GAIA command protocol over BLE as it does over Bluetooth-Classic RFCOMM — but
/// with one big simplification: over BLE there is NO GAIA wire framing. Each write to
/// the command characteristic IS one command, and each notification on the indicate
/// characteristic IS one complete response. So unlike the RFCOMM transports (which
/// wrap/unwrap 0xFF 0x01 … frames and run a reassembly accumulator), this transport
/// writes <see cref="EnqueueWrite"/>'s bytes verbatim and raises
/// <see cref="ReceivedData"/> with each notification's bytes verbatim. The payload on
/// both sides is exactly the group+command+body that <c>RadioController</c> already
/// produces and parses, so the Core stays unchanged.
///
/// GATT layout (reverse-engineered; matches the cross-platform benlink reference):
///   service   00001100-d102-11e1-9b23-00025b00a5a5
///   write     00001101-…  (Write With Response)
///   indicate  00001102-…  (Notify/Indicate)
/// </summary>
public sealed class IosRadioTransport : IRadioTransport
{
    internal static readonly CBUUID ServiceUuid  = CBUUID.FromString("00001100-d102-11e1-9b23-00025b00a5a5");
    internal static readonly CBUUID WriteUuid    = CBUUID.FromString("00001101-d102-11e1-9b23-00025b00a5a5");
    internal static readonly CBUUID IndicateUuid = CBUUID.FromString("00001102-d102-11e1-9b23-00025b00a5a5");

    public event Action? OnConnected;
    public event Action<IRadioTransport, Exception, byte[]>? ReceivedData;

    private readonly string address;          // CBPeripheral.Identifier (NSUUID) string
    private readonly ILogger? logger;
    private readonly Action<string>? onDisconnected;

    private CBCentralManager? central;
    private CBPeripheral? peripheral;
    private CBCharacteristic? writeChar;
    private CBCharacteristic? indicateChar;

    private bool running;
    private bool connectedRaised;
    // Effective max bytes a single GATT notification can carry (ATT_MTU - 3). Each whole
    // GAIA message must fit within this; larger TNC/packet payloads are split by the radio
    // into numbered fragments (reassembled in Core), so the transport never reassembles.
    private nuint notifyCap;

    public IosRadioTransport(string address, ILogger? logger, Action<string>? onDisconnected)
    {
        this.address = address;
        this.logger = logger;
        this.onDisconnected = onDisconnected;
    }

    private void Debug(string msg) => logger?.Debug("Transport(iOS): " + msg);

    public bool Connect()
    {
        if (central != null) return false;     // already connecting/connected
        running = true;
        central = new CBCentralManager();
        central.UpdatedState += OnCentralState;
        central.ConnectedPeripheral += OnPeripheralConnected;
        central.DisconnectedPeripheral += OnPeripheralDisconnected;
        central.FailedToConnectPeripheral += OnPeripheralFailed;
        // Connection actually starts once the manager reaches PoweredOn.
        return true;
    }

    // ---- central manager callbacks --------------------------------------------------

    private void OnCentralState(object? sender, EventArgs e)
    {
        if (central!.State != CBManagerState.PoweredOn)
        {
            Debug($"Bluetooth not ready (state={central.State}).");
            return;
        }
        // Resolve the stored identifier back to a CBPeripheral. The system can hand it
        // straight back if it was seen recently (e.g. just discovered in the picker);
        // otherwise scan for the service UUID and match by identifier.
        var nsuuid = new NSUuid(address);
        var known = central.RetrievePeripheralsWithIdentifiers(nsuuid);
        if (known.Length > 0)
        {
            BeginConnect(known[0]);
        }
        else
        {
            Debug("Peripheral not cached; scanning to match by identifier.");
            central.DiscoveredPeripheral += OnScanHit;
            // No service filter — the radio doesn't advertise the GAIA service UUID; we
            // match the target by its CBPeripheral identifier in OnScanHit instead.
            central.ScanForPeripherals((CBUUID[]?)null);
        }
    }

    private void OnScanHit(object? sender, CBDiscoveredPeripheralEventArgs e)
    {
        if (e.Peripheral.Identifier.AsString() != address) return;
        central!.StopScan();
        central.DiscoveredPeripheral -= OnScanHit;
        BeginConnect(e.Peripheral);
    }

    private void BeginConnect(CBPeripheral p)
    {
        peripheral = p;
        // Hold a strong ref via the field; wire peripheral-level events for GATT discovery.
        peripheral.DiscoveredService += OnDiscoveredService;
        peripheral.DiscoveredCharacteristics += OnDiscoveredCharacteristic;
        peripheral.UpdatedCharacterteristicValue += OnCharacteristicValue;
        peripheral.UpdatedNotificationState += OnNotificationState;
        Debug($"Connecting to {address}.");
        central!.ConnectPeripheral(peripheral);
    }

    private void OnPeripheralConnected(object? sender, CBPeripheralEventArgs e)
    {
        Debug("Link up; discovering GATT service.");
        peripheral!.DiscoverServices(new[] { ServiceUuid });
    }

    private void OnPeripheralFailed(object? sender, CBPeripheralErrorEventArgs e)
    {
        Fail("connect failed: " + (e.Error?.LocalizedDescription ?? "unknown"));
    }

    private void OnPeripheralDisconnected(object? sender, CBPeripheralErrorEventArgs e)
    {
        if (!running) return;
        Fail("disconnected: " + (e.Error?.LocalizedDescription ?? "link dropped"));
    }

    // ---- GATT discovery -------------------------------------------------------------

    private void OnDiscoveredService(object? sender, NSErrorEventArgs e)
    {
        if (peripheral?.Services == null) return;
        foreach (var svc in peripheral.Services)
        {
            Debug($"service {svc.UUID}");   // smoke log: every advertised service
            if (svc.UUID.Equals(ServiceUuid))
                peripheral.DiscoverCharacteristics(new[] { WriteUuid, IndicateUuid }, svc);
        }
    }

    private void OnDiscoveredCharacteristic(object? sender, CBServiceEventArgs e)
    {
        if (e.Service.Characteristics == null) return;
        foreach (var c in e.Service.Characteristics)
        {
            // Smoke log: characteristic UUID + properties, so a first-connect mismatch
            // (wrong write type, missing notify, etc.) is obvious in the transport log.
            Debug($"  char {c.UUID} props={c.Properties}");
            if (c.UUID.Equals(WriteUuid)) writeChar = c;
            else if (c.UUID.Equals(IndicateUuid)) indicateChar = c;
        }
        if (indicateChar != null)
            peripheral!.SetNotifyValue(true, indicateChar);   // subscribe before declaring ready
        else
            Fail("indicate characteristic not found.");
    }

    private void OnNotificationState(object? sender, CBCharacteristicEventArgs e)
    {
        if (!e.Characteristic.UUID.Equals(IndicateUuid)) return;
        if (e.Error != null) { Fail("subscribe failed: " + e.Error.LocalizedDescription); return; }
        if (writeChar == null) { Fail("write characteristic not found."); return; }
        if (connectedRaised) return;
        connectedRaised = true;
        // ATT_MTU-3: the cap for both a Without-Response write and a single notification.
        // Each whole GAIA message must fit here; if it ever doesn't, the radio truncated a
        // message rather than fragmenting it (fragmentation is per-message in Core, not bytes).
        notifyCap = peripheral!.GetMaximumWriteValueLength(CBCharacteristicWriteType.WithoutResponse);
        Debug($"Subscribed; transport ready. notify/write cap = {notifyCap} bytes.");
        OnConnected?.Invoke();
    }

    // ---- data ----------------------------------------------------------------------

    private void OnCharacteristicValue(object? sender, CBCharacteristicEventArgs e)
    {
        if (!e.Characteristic.UUID.Equals(IndicateUuid)) return;
        if (e.Error != null) { Debug("notify error: " + e.Error.LocalizedDescription); return; }
        var data = e.Characteristic.Value;
        if (data == null || data.Length == 0) return;
        // Truncation guard: a notification filling the whole ATT_MTU is a red flag that a
        // single GAIA message was clipped to fit (it should never reach the cap, since the
        // radio fragments large payloads itself). Log it so an MTU problem is diagnosable.
        if (notifyCap > 0 && data.Length >= notifyCap)
            Debug($"WARNING: notification hit MTU cap ({data.Length}B) — possible truncated GAIA message.");
        // One notification = one complete GAIA message body (group+cmd+payload). Deliver
        // verbatim — RadioController parses byte 0..1 = group, 2..3 = command.
        ReceivedData?.Invoke(this, null!, data.ToArray());
    }

    public void EnqueueWrite(int expectedResponse, byte[] cmdData)
    {
        var p = peripheral;
        var w = writeChar;
        if (!running || p == null || w == null) return;
        // No GAIA framing over BLE: write the group+cmd+payload bytes as-is.
        using var payload = NSData.FromArray(cmdData);
        p.WriteValue(payload, w, CBCharacteristicWriteType.WithResponse);
    }

    // ---- teardown -------------------------------------------------------------------

    private void Fail(string reason)
    {
        Debug(reason);
        var cb = onDisconnected;
        Disconnect();
        cb?.Invoke(reason);
    }

    public void Disconnect()
    {
        running = false;
        try
        {
            if (peripheral != null)
            {
                peripheral.DiscoveredService -= OnDiscoveredService;
                peripheral.DiscoveredCharacteristics -= OnDiscoveredCharacteristic;
                peripheral.UpdatedCharacterteristicValue -= OnCharacteristicValue;
                peripheral.UpdatedNotificationState -= OnNotificationState;
                if (central != null && peripheral.State != CBPeripheralState.Disconnected)
                    central.CancelPeripheralConnection(peripheral);
            }
        }
        catch (Exception ex) { Debug("teardown: " + ex.Message); }
        peripheral = null;
        writeChar = null;
        indicateChar = null;
        connectedRaised = false;
        central = null;
    }
}
