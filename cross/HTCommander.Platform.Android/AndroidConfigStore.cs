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
using System.Text.Json;
using HTCommander.Core.Abstractions;

namespace HTCommander.Platform.Android;

/// <summary>
/// Android <see cref="IConfigStore"/>: the same string-keyed JSON file the desktop
/// uses, stored in app-private storage (<c>Context.FilesDir</c>). Mirrors the Linux
/// JsonConfigStore semantics exactly (ReadInt returns null when a key is absent) so
/// the shared DataBroker logic is unchanged.
/// </summary>
public sealed class AndroidConfigStore : IConfigStore
{
    private readonly object _lock = new();
    private readonly string _filePath;
    private readonly Dictionary<string, string> _values = new();

    /// <param name="filePath">
    /// Optional explicit path. When null, resolves to
    /// <c>Application.Context.FilesDir/HTCommander/settings.json</c>.
    /// </param>
    public AndroidConfigStore(string? filePath = null)
    {
        if (filePath != null) { _filePath = filePath; }
        else
        {
            string baseDir = global::Android.App.Application.Context.FilesDir?.AbsolutePath
                             ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _filePath = Path.Combine(baseDir, "HTCommander", "settings.json");
        }
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_filePath));
            if (loaded != null) { _values.Clear(); foreach (var kv in loaded) _values[kv.Key] = kv.Value; }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("AndroidConfigStore load failed: " + ex.Message); }
    }

    private void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("AndroidConfigStore save failed: " + ex.Message); }
    }

    public string ReadString(string keyName, string defaultValue)
    {
        lock (_lock) { return _values.TryGetValue(keyName, out var v) ? v : defaultValue; }
    }

    public void WriteString(string keyName, string value)
    {
        lock (_lock) { _values[keyName] = value; Save(); }
    }

    public int? ReadInt(string keyName, int? defaultValue)
    {
        lock (_lock)
        {
            return _values.TryGetValue(keyName, out var v) && int.TryParse(v, out int i) ? i : defaultValue;
        }
    }

    public void WriteInt(string keyName, int value)
    {
        lock (_lock) { _values[keyName] = value.ToString(System.Globalization.CultureInfo.InvariantCulture); Save(); }
    }

    public bool ReadBool(string keyName, bool defaultValue)
    {
        lock (_lock)
        {
            return _values.TryGetValue(keyName, out var v) && bool.TryParse(v, out bool b) ? b : defaultValue;
        }
    }

    public void WriteBool(string keyName, bool value)
    {
        lock (_lock) { _values[keyName] = value.ToString(); Save(); }
    }

    public void DeleteValue(string keyName)
    {
        lock (_lock) { if (_values.Remove(keyName)) Save(); }
    }
}
