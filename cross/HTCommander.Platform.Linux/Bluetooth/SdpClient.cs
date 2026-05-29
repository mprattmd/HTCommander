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
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HTCommander.Platform.Linux.Bluetooth;

/// <summary>
/// Minimal Bluetooth SDP client over a raw L2CAP socket (PSM 1). BlueZ/`sdptool`
/// were unreliable on the target radio, so this issues its own
/// ServiceSearchAttributeRequest for a given 16-bit service UUID and extracts the
/// RFCOMM channel from the ProtocolDescriptorList — used to discover the radio's
/// voice-audio RFCOMM channel (UUID 0x1203), which is assigned dynamically and not
/// exposed over D-Bus. Handles SDP continuation-state fragmentation.
/// </summary>
internal static class SdpClient
{
    private const int AF_BLUETOOTH = 31;
    private const int SOCK_SEQPACKET = 5;
    private const int BTPROTO_L2CAP = 0;
    private const int SOL_SOCKET = 1;
    private const int SO_RCVTIMEO = 20;
    private const int SO_SNDTIMEO = 21;
    private const ushort SdpPsm = 0x0001;

    private const byte SdpServiceSearchAttrRequest = 0x06;

    [DllImport("libc", SetLastError = true)] private static extern int socket(int domain, int type, int protocol);
    [DllImport("libc", SetLastError = true)] private static extern int connect(int fd, byte[] addr, uint len);
    [DllImport("libc", SetLastError = true)] private static extern nint send(int fd, byte[] buf, nuint len, int flags);
    [DllImport("libc", SetLastError = true)] private static extern nint recv(int fd, byte[] buf, nuint len, int flags);
    [DllImport("libc", SetLastError = true)] private static extern int setsockopt(int fd, int level, int opt, byte[] val, uint len);
    [DllImport("libc", SetLastError = true)] private static extern int close(int fd);

    /// <summary>
    /// Returns the RFCOMM channel advertised by the service with the given 16-bit
    /// <paramref name="serviceUuid16"/> on <paramref name="bdaddr"/> (6 bytes, the
    /// kernel's reversed order), or null if not found / SDP unavailable.
    /// </summary>
    public static int? FindRfcommChannel(byte[] bdaddr, ushort serviceUuid16, int timeoutMs = 5000)
    {
        byte[]? attr = Query(bdaddr, serviceUuid16, timeoutMs);
        if (attr == null) return null;
        for (int i = 0; i + 4 < attr.Length; i++)
            if (attr[i] == 0x19 && attr[i + 1] == 0x00 && attr[i + 2] == 0x03 && attr[i + 3] == 0x08)
                return attr[i + 4];
        return null;
    }

    /// <summary>
    /// Browses all RFCOMM services and returns the channel of the one whose
    /// ServiceName contains <paramref name="nameContains"/> (case-insensitive).
    /// Used to find the radio's audio stream ("BS AOC"), whose only useful
    /// identifier is its name (it carries no distinctive 16-bit service-class UUID,
    /// and the 0x1203 GenericAudio service on this radio is NOT the audio stream).
    /// In each SDP record the RFCOMM channel precedes the ServiceName.
    /// </summary>
    public static int? FindRfcommChannelByName(byte[] bdaddr, string nameContains, int timeoutMs = 5000)
    {
        byte[]? attr = Query(bdaddr, 0x0100, timeoutMs);   // search L2CAP -> matches every RFCOMM service
        if (attr == null) return null;
        int pendingChannel = -1;
        for (int i = 0; i < attr.Length; i++)
        {
            if (i + 4 < attr.Length && attr[i] == 0x19 && attr[i + 1] == 0x00 && attr[i + 2] == 0x03 && attr[i + 3] == 0x08)
                pendingChannel = attr[i + 4];
            else if (attr[i] == 0x25 && i + 1 < attr.Length)        // text string (ServiceName)
            {
                int len = attr[i + 1];
                if (i + 2 + len <= attr.Length && pendingChannel >= 0)
                {
                    string name = System.Text.Encoding.ASCII.GetString(attr, i + 2, len);
                    if (name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        return pendingChannel;
                }
            }
        }
        return null;
    }

    // Issues a ServiceSearchAttributeRequest for serviceUuid16 (all attributes) and
    // returns the accumulated AttributeLists bytes, handling continuation state.
    private static byte[]? Query(byte[] bdaddr, ushort serviceUuid16, int timeoutMs)
    {
        int fd = socket(AF_BLUETOOTH, SOCK_SEQPACKET, BTPROTO_L2CAP);
        if (fd < 0) return null;
        try
        {
            byte[] tv = new byte[16];
            BitConverter.GetBytes((long)(timeoutMs / 1000)).CopyTo(tv, 0);
            BitConverter.GetBytes((long)((timeoutMs % 1000) * 1000)).CopyTo(tv, 8);
            setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, tv, 16);
            setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, tv, 16);

            // sockaddr_l2: family(2) psm(2 LE) bdaddr(6) cid(2) bdaddr_type(1) = 14
            byte[] addr = new byte[14];
            addr[0] = AF_BLUETOOTH & 0xFF;
            addr[2] = SdpPsm & 0xFF;
            addr[3] = (SdpPsm >> 8) & 0xFF;
            Array.Copy(bdaddr, 0, addr, 4, 6);
            if (connect(fd, addr, 14) != 0) return null;

            var attr = new List<byte>();
            byte[] cont = Array.Empty<byte>();
            for (int iter = 0; iter < 64; iter++)
            {
                byte[] req = BuildRequest(serviceUuid16, cont);
                if (send(fd, req, (nuint)req.Length, 0) < 0) return null;

                byte[] resp = new byte[8192];
                nint n = recv(fd, resp, (nuint)resp.Length, 0);
                if (n < 9 || resp[0] != 0x07) return null;
                int attrCount = (resp[5] << 8) | resp[6];
                if (7 + attrCount + 1 > n) return null;
                for (int i = 0; i < attrCount; i++) attr.Add(resp[7 + i]);

                int contLen = resp[7 + attrCount];
                if (7 + attrCount + 1 + contLen > n) return null;
                cont = new byte[contLen];
                Array.Copy(resp, 7 + attrCount + 1, cont, 0, contLen);
                if (contLen == 0) break;
            }
            return attr.ToArray();
        }
        catch (Exception) { return null; }
        finally { try { close(fd); } catch (Exception) { } }
    }

    private static byte[] BuildRequest(ushort uuid16, byte[] continuation)
    {
        var p = new List<byte>();
        // ServiceSearchPattern: sequence { UUID16 uuid16 }
        p.AddRange(new byte[] { 0x35, 0x03, 0x19, (byte)(uuid16 >> 8), (byte)(uuid16 & 0xFF) });
        // MaximumAttributeByteCount
        p.AddRange(new byte[] { 0xFF, 0xFF });
        // AttributeIDList: sequence { uint32 range 0x0000_FFFF }  (all attributes)
        p.AddRange(new byte[] { 0x35, 0x05, 0x0A, 0x00, 0x00, 0xFF, 0xFF });
        // ContinuationState
        p.Add((byte)continuation.Length);
        p.AddRange(continuation);

        var msg = new List<byte>
        {
            SdpServiceSearchAttrRequest,
            0x00, 0x01,                                   // transaction id
            (byte)(p.Count >> 8), (byte)(p.Count & 0xFF)  // parameter length
        };
        msg.AddRange(p);
        return msg.ToArray();
    }
}
