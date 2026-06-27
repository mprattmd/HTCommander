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
using System.Collections.Generic;
using System.Linq;
using CoreBluetooth;
using Foundation;
using HTCommander.Core.Abstractions;

namespace HTCommander.Platform.iOS;

/// <summary>
/// iOS discovery of compatible radios over BLE. Unlike Android (which enumerates
/// already-bonded Classic devices), Core Bluetooth has no paired-device list: you
/// SCAN for peripherals advertising the radio's GAIA service UUID. Devices are
/// identified by an opaque per-app <see cref="CBPeripheral.Identifier"/> (NSUUID) —
/// iOS never exposes the hardware MAC — and that identifier string becomes the
/// <see cref="RadioDeviceInfo.Address"/> handed back to <see cref="IosRadioTransport"/>.
///
/// The scan runs continuously while this object is alive, accumulating a snapshot;
/// <see cref="FindCompatibleRadios"/> returns whatever has been seen so far, so the
/// UI should call it again a second or two after opening the picker.
/// </summary>
public sealed class IosRadioDiscovery : IRadioTransportDiscovery, IDisposable
{
    private static readonly string[] TargetDeviceNames =
        { "UV-PRO", "UV-50PRO", "GA-5WB", "VR-N75", "VR-N76", "VR-N7500", "VR-N7600", "DB50-B" };

    private readonly CBCentralManager central;
    private readonly object gate = new();
    // identifier-string -> advertised name
    private readonly Dictionary<string, string> found = new();
    private bool poweredOn;

    public IosRadioDiscovery()
    {
        central = new CBCentralManager();
        central.UpdatedState += OnStateChanged;
        central.DiscoveredPeripheral += OnDiscovered;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        poweredOn = central.State == CBManagerState.PoweredOn;
        if (poweredOn) StartScan();
    }

    private void StartScan()
    {
        // Scan with NO service filter: the radio advertises its NAME but not the GAIA
        // service UUID, so a UUID-filtered scan never sees it. We discover everything and
        // match by model name below (same approach as bleak/benlink). The service is then
        // discovered after connecting (IosRadioTransport).
        central.ScanForPeripherals((CBUUID[]?)null);
    }

    private void OnDiscovered(object? sender, CBDiscoveredPeripheralEventArgs e)
    {
        string? name = e.Peripheral.Name ?? AdvertisedName(e.AdvertisementData);
        if (name == null || !IsCompatible(name)) return;   // ignore unrelated BLE devices
        string id = e.Peripheral.Identifier.AsString();
        lock (gate) { found[id] = name; }
    }

    private static bool IsCompatible(string name) =>
        TargetDeviceNames.Any(t => name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);

    private static string? AdvertisedName(NSDictionary adv)
    {
        if (adv != null && adv.TryGetValue(CBAdvertisement.DataLocalNameKey, out var v) && v is NSString s)
            return s.ToString();
        return null;
    }

    public bool CheckBluetooth() => central.State == CBManagerState.PoweredOn;

    public IReadOnlyList<string> GetDeviceNames()
    {
        lock (gate) { return found.Values.Distinct().OrderBy(n => n).ToList(); }
    }

    // Every peripheral we surface already advertised the radio service UUID, so they
    // are all compatible by construction.
    public IReadOnlyList<string> FindCompatibleDevices() => GetDeviceNames();

    public IReadOnlyList<RadioDeviceInfo> FindCompatibleRadios()
    {
        lock (gate)
        {
            return found.Select(kv => new RadioDeviceInfo(kv.Value, kv.Key))
                        .OrderBy(d => d.Name).ToList();
        }
    }

    public void Dispose()
    {
        try { if (poweredOn) central.StopScan(); } catch { /* tearing down */ }
        central.UpdatedState -= OnStateChanged;
        central.DiscoveredPeripheral -= OnDiscovered;
    }
}
