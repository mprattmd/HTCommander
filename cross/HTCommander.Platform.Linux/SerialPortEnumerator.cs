/*
Copyright 2026 Ylian Saint-Hilaire
Licensed under the Apache License, Version 2.0 (the "License");
http://www.apache.org/licenses/LICENSE-2.0
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;

namespace HTCommander.Platform.Linux
{
    /// <summary>
    /// Lists candidate serial ports for the GPS source picker. On Linux,
    /// <see cref="SerialPort.GetPortNames"/> often reports only /dev/ttyS*, so this also
    /// scans /dev for the USB/ACM adapters most GPS pucks present as.
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
}
