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
using HTCommander.Core.Abstractions;
using global::Android.Bluetooth;
using global::Android.Content;

namespace HTCommander.Platform.Android;

/// <summary>
/// Android discovery of compatible radios. Android can only open RFCOMM to
/// already-bonded (paired) devices, so this enumerates <c>BondedDevices</c> and
/// filters by model name. Pairing happens in the system Bluetooth settings, not
/// in-app. Requires the BLUETOOTH_CONNECT runtime permission (Android 12+).
/// </summary>
public sealed class AndroidRadioDiscovery : IRadioTransportDiscovery
{
    private static readonly string[] TargetDeviceNames =
        { "UV-PRO", "UV-50PRO", "GA-5WB", "VR-N75", "VR-N76", "VR-N7500", "VR-N7600", "DB50-B" };

    public bool CheckBluetooth()
    {
        try { var a = GetAdapter(); return a != null && a.IsEnabled; }
        catch (Exception) { return false; }
    }

    public IReadOnlyList<string> GetDeviceNames()
    {
        try
        {
            return Bonded().Select(d => d.Name ?? "")
                           .Where(n => n.Length > 0).Distinct().OrderBy(n => n).ToList();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    public IReadOnlyList<string> FindCompatibleDevices()
    {
        try
        {
            return Bonded().Select(d => d.Name ?? "")
                           .Where(IsCompatible).Distinct().OrderBy(n => n).ToList();
        }
        catch (Exception) { return Array.Empty<string>(); }
    }

    public IReadOnlyList<RadioDeviceInfo> FindCompatibleRadios()
    {
        try
        {
            return Bonded()
                .Where(d => IsCompatible(d.Name ?? "") && !string.IsNullOrEmpty(d.Address))
                .Select(d => new RadioDeviceInfo(d.Name!, d.Address!))
                .OrderBy(d => d.Name)
                .ToList();
        }
        catch (Exception) { return Array.Empty<RadioDeviceInfo>(); }
    }

    private static bool IsCompatible(string name) =>
        TargetDeviceNames.Any(t => name.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);

    private static IEnumerable<BluetoothDevice> Bonded()
    {
        var adapter = GetAdapter();
        var set = adapter?.BondedDevices;
        return set == null ? Enumerable.Empty<BluetoothDevice>() : set.Where(d => d != null)!;
    }

    private static BluetoothAdapter? GetAdapter()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var mgr = (BluetoothManager?)ctx.GetSystemService(Context.BluetoothService);
            return mgr?.Adapter ?? BluetoothAdapter.DefaultAdapter;
        }
        catch (Exception) { return BluetoothAdapter.DefaultAdapter; }
    }
}
