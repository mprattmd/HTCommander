namespace HTCommander.Core.Abstractions;

/// <summary>
/// Persists application settings. Replaces the Windows Registry on Windows
/// (RegistryHelper implements this directly); implemented via a JSON file on
/// Linux/macOS.
///
/// Signatures intentionally mirror the original RegistryHelper so that the
/// existing DataBroker logic (which relies on ReadInt returning a nullable to
/// detect presence, and ReadString taking an explicit default) is preserved
/// unchanged.
/// </summary>
public interface IConfigStore
{
    /// <summary>Reads a string value, or <paramref name="defaultValue"/> if not present.</summary>
    string ReadString(string keyName, string defaultValue);

    /// <summary>Writes a string value.</summary>
    void WriteString(string keyName, string value);

    /// <summary>Reads an integer value, or <paramref name="defaultValue"/> (may be null) if not present.</summary>
    int? ReadInt(string keyName, int? defaultValue);

    /// <summary>Writes an integer value.</summary>
    void WriteInt(string keyName, int value);

    /// <summary>Reads a boolean value, or <paramref name="defaultValue"/> if not present.</summary>
    bool ReadBool(string keyName, bool defaultValue);

    /// <summary>Writes a boolean value.</summary>
    void WriteBool(string keyName, bool value);

    /// <summary>Removes a value if it exists.</summary>
    void DeleteValue(string keyName);
}
