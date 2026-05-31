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

namespace HTCommander.Platform.Linux;

/// <summary>
/// Linux implementation of the <see cref="IRadioPlatform"/> factory seam: hands
/// out the BlueZ discovery + RFCOMM transport. Selected by the composition root
/// when <see cref="OperatingSystem.IsLinux"/>.
/// </summary>
public sealed class LinuxRadioPlatform : IRadioPlatform
{
    public IRadioTransportDiscovery CreateDiscovery() => new BlueZRadioDiscovery();

    public IRadioTransport CreateTransport(string address, ILogger? logger = null, Action<string>? onDisconnected = null)
        => new RadioBluetoothLinux(address, logger, onDisconnected);

    public IRadioAudioChannel CreateAudioChannel(string address, ILogger? logger = null)
        => new RadioAudioChannelLinux(address, logger);
}
