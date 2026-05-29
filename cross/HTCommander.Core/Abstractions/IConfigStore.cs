namespace HTCommander.Core.Abstractions;

/// <summary>
/// Persists application settings. Replaces the Windows Registry on Windows;
/// implemented via a JSON file on Linux/macOS.
/// </summary>
public interface IConfigStore
{
    /// <summary>Reads a string value, or null if not present.</summary>
    string? ReadString(string name);

    /// <summary>Writes a string value.</summary>
    void WriteString(string name, string value);

    /// <summary>Reads an integer value, or <paramref name="defaultValue"/> if not present.</summary>
    int ReadInt(string name, int defaultValue = 0);

    /// <summary>Writes an integer value.</summary>
    void WriteInt(string name, int value);

    /// <summary>Reads a boolean value, or <paramref name="defaultValue"/> if not present.</summary>
    bool ReadBool(string name, bool defaultValue = false);

    /// <summary>Writes a boolean value.</summary>
    void WriteBool(string name, bool value);

    /// <summary>Removes a value if it exists.</summary>
    void DeleteValue(string name);
}
