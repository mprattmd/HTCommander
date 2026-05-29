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
using System.Text;
using System.Security.Cryptography;

namespace HTCommander
{
    // Cross-platform Core subset of the original src/radio/Utils.cs.
    // Contains ONLY the platform-neutral helpers required by portable Core code
    // (e.g. BSSPacket, TncDataFragment). The WinForms-coupled members of the
    // original Utils (Control/TextBox/RichTextBox helpers, user32.dll P/Invoke,
    // RtfBuilder) remain in the WinForms project. When the WinForms project is
    // later wired to reference Core, the pure helpers below should be removed
    // from src/radio/Utils.cs so this becomes the single canonical definition.
    public static partial class Utils
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
    }
}
