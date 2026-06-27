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
using HTCommander.Core.Abstractions;

namespace HTCommander.Platform.iOS;

/// <summary>
/// iOS implementation of the <see cref="IRadioPlatform"/> factory seam. Unlike the
/// desktop/Android backends (Bluetooth-Classic RFCOMM), iOS reaches the radio over
/// BLE GATT via Core Bluetooth — the radio exposes the same GAIA command protocol on
/// a custom BLE service. Voice (the second RFCOMM/SBC stream) has no BLE equivalent,
/// so audio is a no-op stub: iOS is a control + data (APRS/packet/Winlink) client.
/// Selected by the composition root when <see cref="OperatingSystem.IsIOS"/>.
/// </summary>
public sealed class IosRadioPlatform : IRadioPlatform
{
    public IRadioTransportDiscovery CreateDiscovery() => new IosRadioDiscovery();

    public IRadioTransport CreateTransport(string address, ILogger? logger = null, Action<string>? onDisconnected = null)
        => new IosRadioTransport(address, logger, onDisconnected);

    // No BLE voice path on this radio — audio rides Bluetooth-Classic RFCOMM, which
    // iOS does not expose to third-party apps. Stub mirrors AndroidRadioAudioChannel.
    public IRadioAudioChannel CreateAudioChannel(string address, ILogger? logger = null)
        => new IosRadioAudioChannel();
}
