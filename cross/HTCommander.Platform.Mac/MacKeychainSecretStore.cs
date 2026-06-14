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
using System.Runtime.InteropServices;
using System.Text;
using HTCommander.Core.Abstractions;

namespace HTCommander.Platform.Mac;

/// <summary>
/// macOS <see cref="ISecretStore"/> backed by the login Keychain via the
/// Security framework (generic-password items). The secret value is passed as a
/// CFData through the native API — it never appears in process arguments the way
/// shelling out to <c>security(1)</c> would. Each secret is a generic-password
/// item keyed by (service = application name, account = the secret key).
/// </summary>
public sealed class MacKeychainSecretStore : ISecretStore
{
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string Sec = "/System/Library/Frameworks/Security.framework/Security";

    private const int errSecSuccess = 0;
    private const int errSecItemNotFound = -25300;

    private readonly string _service;

    // Cached CFStringRef constants (exported data symbols, dereferenced once).
    private static readonly IntPtr kSecClass;
    private static readonly IntPtr kSecClassGenericPassword;
    private static readonly IntPtr kSecAttrService;
    private static readonly IntPtr kSecAttrAccount;
    private static readonly IntPtr kSecValueData;
    private static readonly IntPtr kSecReturnData;
    private static readonly IntPtr kSecMatchLimit;
    private static readonly IntPtr kSecMatchLimitOne;
    private static readonly IntPtr kSecAttrAccessible;
    private static readonly IntPtr kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly;
    private static readonly IntPtr kCFBooleanTrue;
    private static readonly IntPtr kCFTypeDictionaryKeyCallBacks;
    private static readonly IntPtr kCFTypeDictionaryValueCallBacks;

    static MacKeychainSecretStore()
    {
        IntPtr sec = NativeLibrary.Load(Sec);
        IntPtr cf = NativeLibrary.Load(CF);
        kSecClass = Deref(sec, "kSecClass");
        kSecClassGenericPassword = Deref(sec, "kSecClassGenericPassword");
        kSecAttrService = Deref(sec, "kSecAttrService");
        kSecAttrAccount = Deref(sec, "kSecAttrAccount");
        kSecValueData = Deref(sec, "kSecValueData");
        kSecReturnData = Deref(sec, "kSecReturnData");
        kSecMatchLimit = Deref(sec, "kSecMatchLimit");
        kSecMatchLimitOne = Deref(sec, "kSecMatchLimitOne");
        kSecAttrAccessible = Deref(sec, "kSecAttrAccessible");
        kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly = Deref(sec, "kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly");
        kCFBooleanTrue = Deref(cf, "kCFBooleanTrue");
        // Dictionary callbacks are structs; the symbol address *is* the value.
        kCFTypeDictionaryKeyCallBacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks");
        kCFTypeDictionaryValueCallBacks = NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks");
    }

    public MacKeychainSecretStore(string applicationName = "HTCommander")
    {
        _service = string.IsNullOrEmpty(applicationName) ? "HTCommander" : applicationName;
    }

    public bool IsEncrypted => true;

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        IntPtr query = BuildQuery(key, returnData: true);
        try
        {
            int status = SecItemCopyMatching(query, out IntPtr result);
            if (status != errSecSuccess || result == IntPtr.Zero) return null;
            try
            {
                nint len = CFDataGetLength(result);
                IntPtr ptr = CFDataGetBytePtr(result);
                if (len <= 0 || ptr == IntPtr.Zero) return string.Empty;
                byte[] buf = new byte[(int)len];
                Marshal.Copy(ptr, buf, 0, (int)len);
                return Encoding.UTF8.GetString(buf);
            }
            finally { CFRelease(result); }
        }
        finally { CFRelease(query); }
    }

    public void Set(string key, string value)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (string.IsNullOrEmpty(value)) { Delete(key); return; }

        byte[] bytes = Encoding.UTF8.GetBytes(value);
        IntPtr dataRef = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
        IntPtr query = BuildQuery(key, returnData: false);
        IntPtr updateAttrs = CFDictionary(new[] { kSecValueData }, new[] { dataRef });
        try
        {
            int status = SecItemUpdate(query, updateAttrs);
            if (status == errSecItemNotFound)
            {
                IntPtr addDict = CFDictionary(
                    new[] { kSecClass, kSecAttrService, kSecAttrAccount, kSecValueData, kSecAttrAccessible },
                    new[] { kSecClassGenericPassword, CFStr(_service), CFStr(key), dataRef, kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly },
                    releaseValuesFrom: 1, releaseValuesTo: 2);   // release the two CFStrings we created
                try { SecItemAdd(addDict, IntPtr.Zero); }
                finally { CFRelease(addDict); }
            }
        }
        finally
        {
            CFRelease(updateAttrs);
            CFRelease(query);
            CFRelease(dataRef);
        }
    }

    public void Delete(string key)
    {
        if (string.IsNullOrEmpty(key)) return;
        IntPtr query = BuildQuery(key, returnData: false);
        try { SecItemDelete(query); }
        finally { CFRelease(query); }
    }

    // ---- helpers -------------------------------------------------------------

    private IntPtr BuildQuery(string key, bool returnData)
    {
        IntPtr svc = CFStr(_service);
        IntPtr acct = CFStr(key);
        try
        {
            if (returnData)
            {
                return CFDictionary(
                    new[] { kSecClass, kSecAttrService, kSecAttrAccount, kSecReturnData, kSecMatchLimit },
                    new[] { kSecClassGenericPassword, svc, acct, kCFBooleanTrue, kSecMatchLimitOne });
            }
            return CFDictionary(
                new[] { kSecClass, kSecAttrService, kSecAttrAccount },
                new[] { kSecClassGenericPassword, svc, acct });
        }
        finally
        {
            CFRelease(svc);
            CFRelease(acct);
        }
    }

    /// <summary>Builds a CFDictionary; the type callbacks retain keys/values so callers free their own refs.</summary>
    private static IntPtr CFDictionary(IntPtr[] keys, IntPtr[] values, int releaseValuesFrom = -1, int releaseValuesTo = -1)
    {
        IntPtr dict = CFDictionaryCreate(IntPtr.Zero, keys, values, keys.Length,
            kCFTypeDictionaryKeyCallBacks, kCFTypeDictionaryValueCallBacks);
        if (releaseValuesFrom >= 0)
            for (int i = releaseValuesFrom; i <= releaseValuesTo; i++) CFRelease(values[i]);
        return dict;
    }

    private static IntPtr CFStr(string s)
        => CFStringCreateWithCharacters(IntPtr.Zero, s.ToCharArray(), s.Length);

    private static IntPtr Deref(IntPtr handle, string symbol)
        => Marshal.ReadIntPtr(NativeLibrary.GetExport(handle, symbol));

    // ---- P/Invoke ------------------------------------------------------------

    [DllImport(CF)] private static extern void CFRelease(IntPtr cf);
    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCharacters(IntPtr alloc, char[] chars, nint numChars);
    [DllImport(CF)] private static extern IntPtr CFDataCreate(IntPtr alloc, byte[] bytes, nint length);
    [DllImport(CF)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CF)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CF)] private static extern IntPtr CFDictionaryCreate(IntPtr alloc, IntPtr[] keys, IntPtr[] values, nint numValues, IntPtr keyCallBacks, IntPtr valueCallBacks);

    [DllImport(Sec)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Sec)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(Sec)] private static extern int SecItemUpdate(IntPtr query, IntPtr attributesToUpdate);
    [DllImport(Sec)] private static extern int SecItemDelete(IntPtr query);
}
