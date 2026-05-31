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
using System.Text;
using HTCommander.Core.Abstractions;

namespace HTCommander.Platform.Mac;

/// <summary>
/// IOBluetooth-backed discovery of paired radios. Mirrors <c>BlueZRadioDiscovery</c>;
/// reads the paired-device list from <c>libhtbt.dylib</c> and filters to compatible
/// radio model names.
/// </summary>
public sealed class MacRadioDiscovery : IRadioTransportDiscovery
{
    // Same model names the Linux/Windows backends key on.
    private static readonly string[] TargetDeviceNames =
        { "UV-PRO", "UV-50PRO", "GA-5WB", "VR-N75", "VR-N76", "VR-N7500", "VR-N7600", "DB50-B" };

    public bool CheckBluetooth()
    {
        try { return NativeHtbt.BluetoothAvailable() == 1; }
        catch (Exception) { return false; }
    }

    public IReadOnlyList<string> GetDeviceNames() =>
        ListPaired().Select(d => d.Name)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();

    public IReadOnlyList<string> FindCompatibleDevices() =>
        FindCompatibleRadios().Select(d => d.Name).ToList();

    public IReadOnlyList<RadioDeviceInfo> FindCompatibleRadios() =>
        ListPaired().Where(d => IsCompatible(d.Name))
                    .OrderBy(d => d.Name)
                    .ToList();

    private static bool IsCompatible(string name) =>
        !string.IsNullOrEmpty(name) &&
        TargetDeviceNames.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));

    // Reads "name\taddr\n" lines from the native bridge.
    private static List<RadioDeviceInfo> ListPaired()
    {
        var list = new List<RadioDeviceInfo>();
        try
        {
            byte[] buf = new byte[8192];
            int n = NativeHtbt.ListRadios(buf, buf.Length);
            if (n <= 0) return list;
            string text = Encoding.UTF8.GetString(buf, 0, n);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 2) continue;
                string name = parts[0].Trim();
                string addr = parts[1].Trim();
                if (!string.IsNullOrEmpty(addr)) list.Add(new RadioDeviceInfo(name, addr));
            }
        }
        catch (Exception) { /* bridge missing / not on macOS — return empty */ }
        return list;
    }
}
