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
/// The radio's SECOND RFCOMM stream — the SBC voice-audio channel ("BS AOC"),
/// separate from the GAIA command transport. Raw bytes flow both ways: received
/// audio is raised via <see cref="DataReceived"/> (wire it to a
/// <c>RadioVoiceReceiver</c>), and <see cref="Send"/> writes outgoing audio
/// (transmit keys the radio on the air). Implemented per platform (Linux raw
/// kernel RFCOMM socket; macOS via the libhtbt IOBluetooth bridge) and created
/// through <see cref="IRadioPlatform.CreateAudioChannel"/>.
/// </summary>
public interface IRadioAudioChannel
{
    /// <summary>Raised with (buffer, count) for each chunk read from the audio stream.</summary>
    event Action<byte[], int> DataReceived;

    /// <summary>
    /// Connects the audio RFCOMM channel. <paramref name="channel"/> &gt; 0 forces a
    /// specific RFCOMM channel; 0 discovers it (per-platform: SDP service name / UUID).
    /// Returns false if the channel could not be opened.
    /// </summary>
    bool Connect(int channel = 0);

    /// <summary>Writes framed audio bytes to the channel (transmit). ⚠ keys the radio on the air.</summary>
    bool Send(byte[] data);

    /// <summary>Closes the audio channel and stops the read loop.</summary>
    void Disconnect();
}
