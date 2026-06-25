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
using Foundation;
using HTCommander.Core.Abstractions;
using Security;

namespace HTCommander.Platform.iOS;

/// <summary>
/// iOS <see cref="ISecretStore"/> backed by the iOS Keychain (GenericPassword items),
/// namespaced under a service name — the direct counterpart of the desktop
/// <c>MacKeychainSecretStore</c>. Secrets are encrypted at rest by the OS and bound to
/// the app via its keychain-access entitlement, so other apps cannot read them.
/// </summary>
public sealed class IosSecretStore : ISecretStore
{
    private readonly string _service;

    public IosSecretStore(string service = "HTCommander") => _service = service;

    public bool IsEncrypted => true;

    public string? Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        using var query = new SecRecord(SecKind.GenericPassword) { Service = _service, Account = key };
        var match = SecKeyChain.QueryAsRecord(query, out SecStatusCode code);
        if (code != SecStatusCode.Success || match?.ValueData == null) return null;
        return NSString.FromData(match.ValueData, NSStringEncoding.UTF8)?.ToString();
    }

    public void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (string.IsNullOrEmpty(value)) { Delete(key); return; }

        // Replace-by-delete-then-add keeps the path simple and avoids Update attribute quirks.
        Delete(key);
        using var record = new SecRecord(SecKind.GenericPassword)
        {
            Service = _service,
            Account = key,
            ValueData = NSData.FromString(value, NSStringEncoding.UTF8),
            Accessible = SecAccessible.AfterFirstUnlock,   // readable while backgrounded, not before first unlock
        };
        var code = SecKeyChain.Add(record);
        if (code != SecStatusCode.Success)
            System.Diagnostics.Debug.WriteLine("IosSecretStore add failed: " + code);
    }

    public void Delete(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        using var query = new SecRecord(SecKind.GenericPassword) { Service = _service, Account = key };
        SecKeyChain.Remove(query);
    }
}
