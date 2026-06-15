namespace HTCommander.Core.Abstractions;

/// <summary>
/// Stores per-user secrets (API tokens, passwords) encrypted at rest via the
/// platform's native secret store — macOS Keychain, Windows DPAPI, Linux
/// Secret Service (libsecret). This is deliberately separate from
/// <see cref="IConfigStore"/>, which persists ordinary settings in plaintext
/// (JSON file / Registry).
///
/// Keys are short stable identifiers (e.g. "WinlinkPassword"); the
/// implementation namespaces them under an application service name so they do
/// not collide with other apps in a shared keychain.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// True when secrets are protected by an OS-native encrypted store. False
    /// for a degraded fallback (e.g. an obfuscated file on a headless box with
    /// no keyring) — the UI can warn the user in that case.
    /// </summary>
    bool IsEncrypted { get; }

    /// <summary>Reads a secret, or null if it is not present.</summary>
    string? Get(string key);

    /// <summary>
    /// Stores (or replaces) a secret. A null or empty value deletes the entry
    /// rather than storing a blank.
    /// </summary>
    void Set(string key, string value);

    /// <summary>Removes a secret if it exists. No-op when absent.</summary>
    void Delete(string key);
}
