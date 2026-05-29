using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using HTCommander.Core.Abstractions;

namespace HTCommander.Platform.Linux
{
    /// <summary>
    /// Cross-platform (Linux/macOS) implementation of <see cref="IConfigStore"/> that
    /// persists settings to a JSON file, replacing the Windows-Registry-backed
    /// RegistryHelper. Values are stored as strings keyed by name, mirroring
    /// RegistryHelper's semantics (ReadInt returns null when a key is absent).
    /// </summary>
    public sealed class JsonConfigStore : IConfigStore
    {
        private readonly object _lock = new object();
        private readonly string _filePath;
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>();

        /// <summary>
        /// Creates a store backed by a JSON file under the user's config directory,
        /// e.g. ~/.config/HTCommander/settings.json on Linux,
        /// ~/Library/Application Support/HTCommander/settings.json on macOS.
        /// </summary>
        public JsonConfigStore(string applicationName = "HTCommander", string filePath = null)
        {
            if (filePath != null)
            {
                _filePath = filePath;
            }
            else
            {
                string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(baseDir))
                    baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                _filePath = Path.Combine(baseDir, applicationName, "settings.json");
            }
            Load();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                string json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (loaded != null)
                {
                    _values.Clear();
                    foreach (var kv in loaded) _values[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"JsonConfigStore: failed to load '{_filePath}': {ex.Message}");
            }
        }

        private void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(_values, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"JsonConfigStore: failed to save '{_filePath}': {ex.Message}");
            }
        }

        public string ReadString(string keyName, string defaultValue)
        {
            lock (_lock)
            {
                return _values.TryGetValue(keyName, out string value) ? value : defaultValue;
            }
        }

        public void WriteString(string keyName, string value)
        {
            lock (_lock)
            {
                _values[keyName] = value;
                Save();
            }
        }

        public int? ReadInt(string keyName, int? defaultValue)
        {
            lock (_lock)
            {
                if (_values.TryGetValue(keyName, out string value) &&
                    int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    return parsed;
                }
                return defaultValue;
            }
        }

        public void WriteInt(string keyName, int value)
        {
            lock (_lock)
            {
                _values[keyName] = value.ToString(CultureInfo.InvariantCulture);
                Save();
            }
        }

        public bool ReadBool(string keyName, bool defaultValue)
        {
            lock (_lock)
            {
                if (_values.TryGetValue(keyName, out string value))
                {
                    if (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return true;
                    if (value == "0" || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return false;
                }
                return defaultValue;
            }
        }

        public void WriteBool(string keyName, bool value)
        {
            lock (_lock)
            {
                _values[keyName] = value ? "1" : "0";
                Save();
            }
        }

        public void DeleteValue(string keyName)
        {
            lock (_lock)
            {
                if (_values.Remove(keyName)) Save();
            }
        }
    }
}
