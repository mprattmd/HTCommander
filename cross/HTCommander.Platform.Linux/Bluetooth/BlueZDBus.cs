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

using System.Collections.Generic;
using System.Threading.Tasks;
using Tmds.DBus;

namespace HTCommander.Platform.Linux.Bluetooth;

// Minimal Tmds.DBus proxy surface for BlueZ. We use D-Bus only for adapter
// power-on and device discovery/enumeration; the actual RFCOMM data stream is
// opened with a raw kernel socket (see NativeRfcomm) because BlueZ does not
// expose RFCOMM streams over D-Bus (only Profile1 + fd-passing, which the
// high-level Tmds.DBus API does not support).
//
// BlueZ bus name: "org.bluez". Objects live under "/org/bluez/hciN" with
// devices at "/org/bluez/hciN/dev_XX_XX_XX_XX_XX_XX".

// NOTE: these interfaces must be public — Tmds.DBus generates proxy types via
// Reflection.Emit, and an emitted proxy cannot implement a non-public interface
// (throws TypeLoadException "attempting to implement an inaccessible interface").

/// <summary>org.freedesktop.DBus.ObjectManager — enumerates all BlueZ objects.</summary>
[DBusInterface("org.freedesktop.DBus.ObjectManager")]
public interface IObjectManager : IDBusObject
{
    Task<IDictionary<ObjectPath, IDictionary<string, IDictionary<string, object>>>> GetManagedObjectsAsync();
}

/// <summary>org.bluez.Adapter1 — a local Bluetooth controller (hciN).</summary>
[DBusInterface("org.bluez.Adapter1")]
public interface IAdapter1 : IDBusObject
{
    Task<object> GetAsync(string prop);
    Task SetAsync(string prop, object val);
}

/// <summary>org.bluez.Device1 — a remote Bluetooth device.</summary>
[DBusInterface("org.bluez.Device1")]
public interface IDevice1 : IDBusObject
{
    Task ConnectAsync();
    Task DisconnectAsync();
    Task PairAsync();
    Task<object> GetAsync(string prop);
    Task SetAsync(string prop, object val);
}
