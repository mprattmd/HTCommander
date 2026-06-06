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

namespace HTCommander.Platform.Android;

/// <summary>
/// GAIA frame encode/decode — byte-for-byte identical to the Windows/Linux/macOS
/// transports. The radio speaks GAIA over RFCOMM regardless of host OS, so only
/// the socket layer (Android.Bluetooth here) differs; this framing is shared.
///
/// A GAIA frame is: 0xFF 0x01 [flags] [payloadLen] [payload...] (+1 checksum byte
/// when flags bit0 is set). The decoded "cmd" handed up is the 4-byte header tail
/// + payload, matching what the Core Radio logic expects.
/// </summary>
internal static class GaiaFraming
{
    /// <summary>The GET_DEV_INFO probe (BASIC vendor/cmd). A radio replies with a GAIA
    /// frame (0xFF 0x01 ...), which is exactly the signal the de-risk spike checks for.</summary>
    public static readonly byte[] GetDevInfoCmd = { 0x00, 0x02, 0x00, 0x04, 0x03 };

    /// <summary>Wraps a command in a GAIA frame ready to write to the socket.</summary>
    public static byte[] Encode(byte[] cmd)
    {
        byte[] bytes = new byte[cmd.Length + 4];
        bytes[0] = 0xFF;
        bytes[1] = 0x01;
        bytes[3] = (byte)(cmd.Length - 4);
        Array.Copy(cmd, 0, bytes, 4, cmd.Length);
        return bytes;
    }

    /// <summary>
    /// Decodes one GAIA frame from <paramref name="data"/> at <paramref name="index"/>.
    /// Returns bytes consumed, 0 if incomplete (need more), or -1 on a bad header.
    /// </summary>
    public static int Decode(byte[] data, int index, int len, out byte[]? cmd)
    {
        cmd = null;
        if (len < 8) return 0;
        if (data[index] != 0xFF || data[index + 1] != 0x01) return -1;

        byte payloadLen = data[index + 3];
        int hasChecksum = data[index + 2] & 1;
        int totalLen = payloadLen + 8 + hasChecksum;
        if (totalLen > len) return 0;

        cmd = new byte[4 + payloadLen];
        Array.Copy(data, index + 4, cmd, 0, cmd.Length);
        return totalLen;
    }
}
