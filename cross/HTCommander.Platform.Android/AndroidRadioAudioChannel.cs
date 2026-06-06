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
/// Round-one no-op audio channel. Voice TX/RX is deferred on Android (no PortAudio
/// runtime; the second-RFCOMM audio-channel discovery is the riskiest unknown — see
/// ANDROID-PORT-PLAN.md §6). This stub satisfies the <see cref="IRadioAudioChannel"/>
/// seam so the platform composes; the UI gates voice features off on Android v1.
/// Round two replaces this with AudioRecord/AudioTrack + the SBC codec.
/// </summary>
public sealed class AndroidRadioAudioChannel : IRadioAudioChannel
{
    // Never raised in round one; present to satisfy the interface.
#pragma warning disable CS0067
    public event Action<byte[], int>? DataReceived;
#pragma warning restore CS0067

    public bool Connect(int channel = 0) => false;   // audio unavailable on Android v1
    public bool Send(byte[] data) => false;
    public void Disconnect() { }
}
