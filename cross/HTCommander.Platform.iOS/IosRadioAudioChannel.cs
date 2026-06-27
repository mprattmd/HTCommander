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
/// No-op audio channel for iOS. The radio's voice stream ("BS AOC") is SBC over a
/// SECOND Bluetooth-Classic RFCOMM channel, which iOS gives third-party apps no way
/// to open (no RFCOMM, no SDP, and the radio is not MFi-certified). There is no BLE
/// audio service to fall back to, so voice TX/RX is unsupported on iOS. Connect()
/// returns false; the UI keeps APRS/packet/Winlink/control, which need only the GAIA
/// command transport (<see cref="IosRadioTransport"/>). Mirrors the Android stub.
/// </summary>
public sealed class IosRadioAudioChannel : IRadioAudioChannel
{
#pragma warning disable CS0067 // never raised — no audio stream exists on iOS
    public event Action<byte[], int>? DataReceived;
#pragma warning restore CS0067

    public bool Connect(int channel = 0) => false;

    public bool Send(byte[] data) => false;

    public void Disconnect() { }
}
