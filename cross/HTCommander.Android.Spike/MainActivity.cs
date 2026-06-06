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
using System.Linq;
using System.Text;
using HTCommander.Core.Abstractions;
using HTCommander.Platform.Android;
using global::Android.App;
using global::Android.Content.PM;
using global::Android.OS;
using global::Android.Views;
using global::Android.Widget;

namespace HtcSpike;

/// <summary>
/// Phase-0 spike screen: tap "Connect &amp; probe", and it drives the real
/// AndroidRadioTransport against the first bonded compatible radio, sends
/// GET_DEV_INFO, and prints whether a GAIA frame (0xFF 0x01) comes back.
/// A green "SPIKE PASS" line means Android RFCOMM ↔ the radio works and the
/// full port is worth building.
/// </summary>
[Activity(Label = "HTC RFCOMM Spike", MainLauncher = true)]
public sealed class MainActivity : Activity, ILogger
{
    private TextView? _log;
    private IRadioTransport? _transport;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Android 15 (target SDK 35) enforces edge-to-edge: the content frame fills the
        // whole window and the action bar overlays the top, so our views render behind
        // the status bar + action bar. Hide the action bar and pad the top by the real
        // status-bar height so content is fully visible. Deterministic (no reliance on
        // inset-fitting behavior, which varies by theme on API 35).
        ActionBar?.Hide();
        int statusBar = 0;
        int rid = Resources?.GetIdentifier("status_bar_height", "dimen", "android") ?? 0;
        if (rid > 0) statusBar = Resources!.GetDimensionPixelSize(rid);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetPadding(24, 24 + statusBar, 24, 24);
        root.LayoutParameters = new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent);

        var button = new Button(this) { Text = "Connect & probe radio" };
        button.Click += (_, _) => RunProbe();
        root.AddView(button);

        _log = new TextView(this) { Text = "Ready. Pair the radio in Android Bluetooth settings first.\n" };
        _log.SetTextIsSelectable(true);
        var scroll = new ScrollView(this);
        scroll.AddView(_log);
        root.AddView(scroll);

        SetContentView(root);

        RequestPermissionsIfNeeded();
    }

    private void RequestPermissionsIfNeeded()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S)   // Android 12+
        {
            var needed = new[] { "android.permission.BLUETOOTH_CONNECT", "android.permission.BLUETOOTH_SCAN" }
                .Where(p => CheckSelfPermission(p) != Permission.Granted).ToArray();
            if (needed.Length > 0) RequestPermissions(needed, 1);
        }
    }

    private void RunProbe()
    {
        Append("\n--- Probe start ---");
        try
        {
            var platform = new AndroidRadioPlatform();
            var discovery = platform.CreateDiscovery();

            if (!discovery.CheckBluetooth()) { Append("❌ Bluetooth adapter off or unavailable."); return; }

            var radios = discovery.FindCompatibleRadios();
            if (radios.Count == 0)
            {
                Append("❌ No bonded compatible radio found.");
                Append("Bonded devices: " + string.Join(", ", discovery.GetDeviceNames()));
                Append("→ Pair your radio (UV-PRO etc.) in Android Settings, then retry.");
                return;
            }

            var radio = radios[0];
            Append($"Found {radio.Name} [{radio.Address}] — connecting...");

            _transport = platform.CreateTransport(radio.Address, this,
                reason => Post(() => Append("Link dropped: " + reason)));

            _transport.OnConnected += () => Post(() =>
                Append("✅ SPIKE PASS — RFCOMM connected and GAIA channel validated."));

            _transport.ReceivedData += (_, _, data) => Post(() =>
                Append("GAIA frame received (" + data.Length + " bytes): " + Hex(data)));

            _transport.Connect();   // sends GET_DEV_INFO and validates GAIA during connect
        }
        catch (Exception ex) { Append("❌ Exception: " + ex.Message); }
    }

    private static string Hex(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 3);
        foreach (byte b in data) { sb.Append(b.ToString("X2")); sb.Append(' '); }
        return sb.ToString().TrimEnd();
    }

    private void Post(Action a) => RunOnUiThread(a);

    private void Append(string line)
    {
        RunOnUiThread(() => { if (_log != null) _log.Text += line + "\n"; });
    }

    protected override void OnDestroy()
    {
        try { _transport?.Disconnect(); } catch (Exception) { }
        base.OnDestroy();
    }

    // ILogger — route transport debug output into the on-screen log.
    public void Debug(string message) => Append("· " + message);
    public void Info(string message) => Append("i " + message);
    public void Warn(string message) => Append("⚠ " + message);
    public void Error(string message, Exception? ex = null) => Append("✖ " + message + (ex != null ? " — " + ex.Message : ""));
}
