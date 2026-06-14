using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using HTCommander.Core.Abstractions;

namespace HTCommander.Platform.Linux
{
    /// <summary>
    /// Linux <see cref="ISecretStore"/> backed by the Secret Service (GNOME
    /// Keyring / KWallet) via the <c>secret-tool</c> CLI from libsecret. The
    /// secret is written to the tool's <em>stdin</em>, so it never appears in
    /// process arguments.
    ///
    /// When <c>secret-tool</c> (or a running Secret Service) is unavailable —
    /// e.g. a headless box — this degrades to a 0600 file under the user's
    /// config dir and reports <see cref="IsEncrypted"/> = false so the UI can
    /// warn that the secret is not encrypted at rest.
    /// </summary>
    public sealed class LinuxSecretStore : ISecretStore
    {
        private const string Service = "HTCommander";
        private readonly bool _useSecretTool;
        private readonly string _fallbackDir;

        public LinuxSecretStore(string applicationName = "HTCommander")
        {
            _useSecretTool = ProbeSecretTool();
            string baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(baseDir))
                baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            _fallbackDir = Path.Combine(baseDir, applicationName, "secrets");
        }

        public bool IsEncrypted => _useSecretTool;

        public string Get(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_useSecretTool)
            {
                var (code, stdout) = Run("lookup", new[] { "service", Service, "account", key }, stdin: null);
                if (code != 0) return null;                 // exit 1 = not found
                return stdout.Length == 0 ? null : stdout.TrimEnd('\n');
            }
            string path = FallbackPath(key);
            if (!File.Exists(path)) return null;
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(File.ReadAllText(path))); }
            catch { return null; }
        }

        public void Set(string key, string value)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (string.IsNullOrEmpty(value)) { Delete(key); return; }
            if (_useSecretTool)
            {
                Run("store", new[] { "--label", $"{Service}: {key}", "service", Service, "account", key }, stdin: value);
                return;
            }
            Directory.CreateDirectory(_fallbackDir);
            string path = FallbackPath(key);
            File.WriteAllText(path, Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
            TrySetUserOnly(path);
        }

        public void Delete(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (_useSecretTool)
            {
                Run("clear", new[] { "service", Service, "account", key }, stdin: null);
                return;
            }
            string path = FallbackPath(key);
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        private string FallbackPath(string key)
        {
            // Keep the filename filesystem-safe regardless of the key.
            var sb = new StringBuilder(key.Length);
            foreach (char c in key) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return Path.Combine(_fallbackDir, sb.ToString() + ".secret");
        }

        private static bool ProbeSecretTool()
        {
            try
            {
                var (code, _) = Run("--version", Array.Empty<string>(), stdin: null);
                return code == 0;
            }
            catch { return false; }
        }

        private static (int code, string stdout) Run(string verb, string[] args, string stdin)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "secret-tool",
                RedirectStandardInput = stdin != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(verb);
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (stdin != null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            string outText = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, outText);
        }

        private static void TrySetUserOnly(string path)
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
            catch { /* not fatal */ }
        }
    }
}
