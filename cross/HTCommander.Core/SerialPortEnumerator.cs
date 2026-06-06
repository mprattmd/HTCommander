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
using System.IO;
using System.IO.Ports;
using System.Linq;

namespace HTCommander;

/// <summary>
/// Lists candidate serial ports for the GPS source picker. Portable (lives in Core):
/// uses <see cref="SerialPort.GetPortNames"/> plus a /dev scan for the USB/ACM adapters
/// most GPS pucks present as on Linux. On platforms without serial ports (Android) both
/// sources come back empty, which is correct (GPS-over-serial isn't used there).
/// </summary>
public static class SerialPortEnumerator
{
    public static IReadOnlyList<string> ListPorts()
    {
        var ports = new SortedSet<string>(StringComparer.Ordinal);

        try { foreach (var p in SerialPort.GetPortNames()) if (!string.IsNullOrEmpty(p)) ports.Add(p); }
        catch (Exception) { }

        // Common Linux serial device nodes (USB/ACM adapters + hardware UARTs).
        try
        {
            foreach (var f in Directory.EnumerateFiles("/dev"))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith("ttyUSB", StringComparison.Ordinal) ||
                    name.StartsWith("ttyACM", StringComparison.Ordinal) ||
                    name.StartsWith("ttyAMA", StringComparison.Ordinal) ||
                    name.StartsWith("ttyS", StringComparison.Ordinal))
                    ports.Add(f);
            }
        }
        catch (Exception) { }

        return ports.ToList();
    }
}
