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
using System.Runtime.InteropServices;

namespace HTCommander.Platform.Mac;

/// <summary>
/// P/Invoke surface for <c>libhtbt.dylib</c> — the Swift IOBluetooth RFCOMM bridge
/// (see <c>mac/htbt/htbt.swift</c>). The dylib is shipped next to the executable;
/// these entry points are only ever called on macOS.
/// </summary>
internal static class NativeHtbt
{
    private const string Lib = "htbt";   // resolves libhtbt.dylib next to the app

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void DataCallback(IntPtr data, int len);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void EventCallback(int kind);   // 0=connected, 1=closed, 2=error

    /// <summary>Connect + GAIA-channel discovery. Returns a handle (>=1) or -1.</summary>
    [DllImport(Lib, EntryPoint = "htbt_connect", CharSet = CharSet.Ansi)]
    public static extern int Connect(string addr, byte preferred, DataCallback onData, EventCallback onEvent);

    /// <summary>Write raw (already GAIA-framed) bytes to the radio.</summary>
    [DllImport(Lib, EntryPoint = "htbt_write")]
    public static extern void Write(int handle, byte[] data, int len);

    /// <summary>Close + release a connection.</summary>
    [DllImport(Lib, EntryPoint = "htbt_close")]
    public static extern void Close(int handle);

    /// <summary>1 if a powered Bluetooth controller is present.</summary>
    [DllImport(Lib, EntryPoint = "htbt_bluetooth_available")]
    public static extern int BluetoothAvailable();

    /// <summary>Fills <paramref name="outBuf"/> with "name\taddr\n" lines for paired devices.</summary>
    [DllImport(Lib, EntryPoint = "htbt_list_radios")]
    public static extern int ListRadios([Out] byte[] outBuf, int cap);
}
