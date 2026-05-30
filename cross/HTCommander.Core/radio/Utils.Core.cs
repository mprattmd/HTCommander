/*
Copyright 2025 Ylian Saint-Hilaire

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
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;

namespace HTCommander
{
    // Cross-platform Core subset of the platform-neutral helpers from the
    // original src/radio/Utils.cs (which is WinForms-coupled and stays in the
    // WinForms project). Named CoreUtils rather than Utils so that when the
    // WinForms project references Core there is no duplicate-type conflict with
    // its own HTCommander.Utils class.
    public static class CoreUtils
    {
        public static string BytesToHex(byte[] Bytes)
        {
            if (Bytes == null) return "";
            StringBuilder Result = new StringBuilder(Bytes.Length * 2);
            string HexAlphabet = "0123456789ABCDEF";
            foreach (byte B in Bytes)
            {
                Result.Append(HexAlphabet[(int)(B >> 4)]);
                Result.Append(HexAlphabet[(int)(B & 0xF)]);
            }
            return Result.ToString();
        }

        public static string BytesToHex(byte[] Bytes, int offset, int length)
        {
            if (Bytes == null) return "";
            StringBuilder Result = new StringBuilder(length * 2);
            string HexAlphabet = "0123456789ABCDEF";
            for (int i = offset; i < length + offset; i++)
            {
                Result.Append(HexAlphabet[(int)(Bytes[i] >> 4)]);
                Result.Append(HexAlphabet[(int)(Bytes[i] & 0xF)]);
            }
            return Result.ToString();
        }

        public static byte[] ComputeShortSha256Hash(byte[] rawData)
        {
            using (SHA256 sha256Hash = SHA256.Create())
            {
                byte[] r = sha256Hash.ComputeHash(rawData);
                byte[] r2 = new byte[12];
                Array.Copy(r, 0, r2, 0, 12);
                return r2;
            }
        }

        public static byte[] ComputeSha256Hash(byte[] rawData)
        {
            using (SHA256 sha256Hash = SHA256.Create()) { return sha256Hash.ComputeHash(rawData); }
        }

        public static byte[] ComputeHmacSha256Hash(byte[] authkey, byte[] data)
        {
            using (HMACSHA256 hmac = new HMACSHA256(authkey)) { return hmac.ComputeHash(data); }
        }

        public static byte[] HexStringToByteArray(string Hex)
        {
            try
            {
                if (Hex.Length % 2 != 0) return null;
                byte[] Bytes = new byte[Hex.Length / 2];
                int[] HexValue = new int[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F };
                for (int x = 0, i = 0; i < Hex.Length; i += 2, x += 1)
                {
                    Bytes[x] = (byte)(HexValue[Char.ToUpper(Hex[i + 0]) - '0'] << 4 | HexValue[Char.ToUpper(Hex[i + 1]) - '0']);
                }
                return Bytes;
            }
            catch (Exception) { return null; }
        }

        // --- Big-endian byte helpers (ported verbatim from src/radio/Utils.cs)
        // so on-wire frame parsing/serialization matches the WinForms build byte-for-byte.

        public static int GetShort(byte[] d, int p) { return ((int)d[p] << 8) + (int)d[p + 1]; }
        public static int GetInt(byte[] d, int p) { return ((int)d[p] << 24) + (int)(d[p + 1] << 16) + (int)(d[p + 2] << 8) + (int)d[p + 3]; }
        public static void SetShort(byte[] d, int p, int v) { d[p] = (byte)((v >> 8) & 0xFF); d[p + 1] = (byte)(v & 0xFF); }
        public static void SetInt(byte[] d, int p, int v) { d[p] = (byte)(v >> 24); d[p + 1] = (byte)((v >> 16) & 0xFF); d[p + 2] = (byte)((v >> 8) & 0xFF); d[p + 3] = (byte)(v & 0xFF); }

        // Parse "CALLSIGN-SSID" into its parts; returns false if malformed.
        // Ported verbatim from src/radio/Utils.cs.
        public static bool ParseCallsignWithId(string callsignWithId, out string xcallsign, out int xstationId)
        {
            xcallsign = null;
            xstationId = -1;
            if (callsignWithId == null) return false;
            string[] destSplit = callsignWithId.Split('-');
            if (destSplit.Length != 2) return false;
            int destStationId = -1;
            if (destSplit[0].Length < 3) return false;
            if (destSplit[0].Length > 6) return false;
            if (destSplit[1].Length < 1) return false;
            if (destSplit[1].Length > 2) return false;
            if (int.TryParse(destSplit[1], out destStationId) == false) return false;
            if ((destStationId < 0) || (destStationId > 15)) return false;
            xcallsign = destSplit[0];
            xstationId = destStationId;
            return true;
        }

        // --- Compression (Brotli/Deflate) — used by BBS + Winlink mail.
        // Ported verbatim from the WinForms Utils so on-air framing matches byte-for-byte.

        public static byte[] CompressBrotli(byte[] data)
        {
            using var output = new MemoryStream();
            using (var brotli = new BrotliStream(output, CompressionMode.Compress, leaveOpen: true))
                brotli.Write(data, 0, data.Length);
            return output.ToArray();
        }

        public static byte[] DecompressBrotli(byte[] compressedData) =>
            DecompressBrotli(compressedData, 0, compressedData.Length);

        public static byte[] DecompressBrotli(byte[] compressedData, int index, int length)
        {
            using var input = new MemoryStream(compressedData, index, length);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            brotli.CopyTo(output);
            return output.ToArray();
        }

        public static byte[] CompressDeflate(byte[] data)
        {
            using var output = new MemoryStream();
            using (var dstream = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
                dstream.Write(data, 0, data.Length);
            return output.ToArray();
        }

        public static byte[] DecompressDeflate(byte[] compressedData) =>
            DecompressDeflate(compressedData, 0, compressedData.Length);

        public static byte[] DecompressDeflate(byte[] compressedData, int index, int length)
        {
            using var input = new MemoryStream(compressedData, index, length);
            using var output = new MemoryStream();
            using var dstream = new DeflateStream(input, CompressionMode.Decompress);
            dstream.CopyTo(output);
            return output.ToArray();
        }
    }
}
