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

namespace HTCommander.Platform.Android;

/// <summary>
/// Android implementation of the <see cref="IRadioPlatform"/> factory seam: hands
/// out bonded-device discovery + RFCOMM transport. Audio is a no-op stub in round
/// one. Selected by the composition root when <see cref="OperatingSystem.IsAndroid"/>.
/// </summary>
public sealed class AndroidRadioPlatform : IRadioPlatform
{
    public IRadioTransportDiscovery CreateDiscovery() => new AndroidRadioDiscovery();

    public IRadioTransport CreateTransport(string address, ILogger? logger = null, Action<string>? onDisconnected = null)
        => new AndroidRadioTransport(address, logger, onDisconnected);

    public IRadioAudioChannel CreateAudioChannel(string address, ILogger? logger = null)
        => new AndroidRadioAudioChannel();
}
