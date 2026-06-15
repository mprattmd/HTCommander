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
using System.IO;
using System.Text;
using System.Text.Json;
using global::Android.Security.Keystore;
using global::Java.Security;
using global::Javax.Crypto;
using global::Javax.Crypto.Spec;
using HTCommander.Core.Abstractions;

namespace HTCommander.Platform.Android;

/// <summary>
/// Android <see cref="ISecretStore"/>: each secret is encrypted with an AES-256/GCM
/// key held in the hardware-backed <c>AndroidKeyStore</c> (never exported to the app
/// process), and the ciphertext is persisted to app-private storage as
/// <c>iv:ciphertext</c> base64 pairs. The master key never leaves the Keystore, so
/// even a rooted dump of the file yields only ciphertext.
/// </summary>
public sealed class AndroidSecretStore : ISecretStore
{
    private const string KeyAlias = "HTCommanderSecretKey";
    private const string KeystoreName = "AndroidKeyStore";
    private const string Transformation = "AES/GCM/NoPadding";
    private const int GcmTagBits = 128;

    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly Dictionary<string, string> _values = new();   // key -> base64(iv):base64(ciphertext)

    public AndroidSecretStore(string? filePath = null)
    {
        if (filePath != null) { _filePath = filePath; }
        else
        {
            string baseDir = global::Android.App.Application.Context.FilesDir?.AbsolutePath
                             ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _filePath = Path.Combine(baseDir, "HTCommander", "secrets.json");
        }
        Load();
        EnsureKey();
    }

    public bool IsEncrypted => true;

    public string? Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        string? blob;
        lock (_lock) { if (!_values.TryGetValue(key, out blob) || blob == null) return null; }
        try { return Decrypt(blob); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("AndroidSecretStore decrypt failed: " + ex.Message); return null; }
    }

    public void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (string.IsNullOrEmpty(value)) { Delete(key); return; }
        try
        {
            string blob = Encrypt(value);
            lock (_lock) { _values[key] = blob; Save(); }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("AndroidSecretStore encrypt failed: " + ex.Message); }
    }

    public void Delete(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_lock) { if (_values.Remove(key)) Save(); }
    }

    // ---- Keystore + crypto ---------------------------------------------------

    private static void EnsureKey()
    {
        var ks = KeyStore.GetInstance(KeystoreName);
        ks!.Load(null, null);
        if (ks.ContainsAlias(KeyAlias)) return;

        var gen = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, KeystoreName);
        var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes(KeyProperties.BlockModeGcm)!
            .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
            .SetKeySize(256)!
            .Build();
        gen!.Init(spec);
        gen.GenerateKey();
    }

    private static ISecretKey GetKey()
    {
        var ks = KeyStore.GetInstance(KeystoreName);
        ks!.Load(null, null);
        var entry = (KeyStore.SecretKeyEntry)ks.GetEntry(KeyAlias, null)!;
        return entry.SecretKey!;
    }

    private static string Encrypt(string plaintext)
    {
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.EncryptMode, GetKey());
        byte[] iv = cipher.GetIV()!;
        byte[] ct = cipher.DoFinal(Encoding.UTF8.GetBytes(plaintext))!;
        return Convert.ToBase64String(iv) + ":" + Convert.ToBase64String(ct);
    }

    private static string Decrypt(string blob)
    {
        int sep = blob.IndexOf(':');
        if (sep <= 0) throw new FormatException("malformed secret blob");
        byte[] iv = Convert.FromBase64String(blob.Substring(0, sep));
        byte[] ct = Convert.FromBase64String(blob.Substring(sep + 1));
        var cipher = Cipher.GetInstance(Transformation)!;
        cipher.Init(CipherMode.DecryptMode, GetKey(), new GCMParameterSpec(GcmTagBits, iv));
        byte[] pt = cipher.DoFinal(ct)!;
        return Encoding.UTF8.GetString(pt);
    }

    // ---- ciphertext persistence (app-private storage) ------------------------

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath));
            if (loaded != null) { _values.Clear(); foreach (var kv in loaded) _values[kv.Key] = kv.Value; }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("AndroidSecretStore load failed: " + ex.Message); }
    }

    private void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("AndroidSecretStore save failed: " + ex.Message); }
    }
}
