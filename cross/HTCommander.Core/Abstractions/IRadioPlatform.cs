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

namespace HTCommander.Core.Abstractions;

/// <summary>
/// Factory seam that lets the UI obtain a radio transport + discovery without
/// naming a platform-specific type. The composition root (App.axaml.cs) picks the
/// concrete platform once — <c>LinuxRadioPlatform</c> (BlueZ) or
/// <c>MacRadioPlatform</c> (IOBluetooth) — based on <see cref="OperatingSystem"/>,
/// and hands this interface to the view model.
/// </summary>
public interface IRadioPlatform
{
    /// <summary>Creates the platform's radio discovery service.</summary>
    IRadioTransportDiscovery CreateDiscovery();

    /// <summary>Creates a transport bound to one radio (by BD_ADDR).</summary>
    /// <param name="address">Target radio BD_ADDR, e.g. "AA:BB:CC:DD:EE:FF".</param>
    /// <param name="logger">Optional transport debug logger.</param>
    /// <param name="onDisconnected">Optional callback invoked with a reason when the link drops.</param>
    IRadioTransport CreateTransport(string address, ILogger? logger = null, Action<string>? onDisconnected = null);
}
