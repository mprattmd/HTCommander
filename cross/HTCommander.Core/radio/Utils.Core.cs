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
