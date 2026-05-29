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
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using HTCommander.Core.Abstractions;
using HTCommander.Platform.Linux;
using HTCommander.UI.Avalonia.Platform;

namespace HTCommander.UI.Avalonia.ViewModels;

/// <summary>
/// Shell view model. Drives the connection panel using the validated Linux
/// stack (<see cref="BlueZRadioDiscovery"/> + <see cref="RadioBluetoothLinux"/>):
/// lists compatible radios, opens/closes the transport, and surfaces connection
/// state plus a live frame log. All cross-thread updates are marshalled through
/// the injected <see cref="IUiDispatcher"/>.
///
/// NOTE: connecting here drives the raw transport directly and sends a hardcoded
/// GET_DEV_INFO probe — full radio control arrives when Radio.cs is portable
/// (porting-plan step 3). The shell, dispatcher and DI wiring are the deliverable.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    // BASIC(2) / GET_DEV_INFO(4) / data=3 — same probe Radio sends on connect.
    private static readonly byte[] GetDevInfo = { 0x00, 0x02, 0x00, 0x04, 0x03 };

    private readonly IUiDispatcher dispatcher;
    private readonly BlueZRadioDiscovery discovery = new();
    private readonly ILogger logger;
    private RadioBluetoothLinux? transport;

    public ObservableCollection<RadioDeviceInfo> Radios { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    public MainViewModel(IUiDispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        logger = new CallbackLogger(AppendLog);
        Refresh();
    }

    private RadioDeviceInfo? selectedRadio;
    public RadioDeviceInfo? SelectedRadio
    {
        get => selectedRadio;
        set { if (SetField(ref selectedRadio, value)) OnPropertyChanged(nameof(CanConnect)); }
    }

    private string status = "Disconnected";
    public string Status { get => status; private set => SetField(ref status, value); }

    private bool bluetoothAvailable;
    public bool BluetoothAvailable { get => bluetoothAvailable; private set => SetField(ref bluetoothAvailable, value); }

    private bool connected;
    public bool Connected
    {
        get => connected;
        private set
        {
            if (SetField(ref connected, value))
            {
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanDisconnect));
            }
        }
    }

    private int frameCount;
    public int FrameCount { get => frameCount; private set => SetField(ref frameCount, value); }

    public bool CanConnect => !Connected && SelectedRadio != null;
    public bool CanDisconnect => Connected || transport != null;

    /// <summary>Re-scans BlueZ for adapter availability and compatible radios.</summary>
    public void Refresh()
    {
        Task.Run(() =>
        {
            bool bt = discovery.CheckBluetooth();
            var radios = discovery.FindCompatibleRadios().ToList();
            dispatcher.Post(() =>
            {
                BluetoothAvailable = bt;
                var previous = SelectedRadio?.Address;
                Radios.Clear();
                foreach (var r in radios) Radios.Add(r);
                SelectedRadio = Radios.FirstOrDefault(r => r.Address == previous) ?? Radios.FirstOrDefault();
                AppendLog(bt
                    ? $"Bluetooth ready — {radios.Count} compatible radio(s) found."
                    : "No Bluetooth adapter available.");
            });
        });
    }

    public void Connect()
    {
        if (!CanConnect) return;
        var radio = SelectedRadio!;
        FrameCount = 0;
        Status = $"Connecting to {radio.Name} ({radio.Address})...";
        AppendLog(Status);

        transport = new RadioBluetoothLinux(radio.Address, logger,
            reason => dispatcher.Post(() =>
            {
                Connected = false;
                transport = null;
                Status = "Disconnected: " + reason;
                OnPropertyChanged(nameof(CanConnect));
                OnPropertyChanged(nameof(CanDisconnect));
            }));

        transport.OnConnected += () => dispatcher.Post(() =>
        {
            Connected = true;
            Status = $"Connected to {radio.Name}";
            AppendLog("Connected — sending GET_DEV_INFO probe.");
            transport?.EnqueueWrite(0, GetDevInfo);
        });

        transport.ReceivedData += (_, _, value) => dispatcher.Post(() =>
        {
            FrameCount++;
            AppendLog($"<<< [{value.Length}B] {BytesToHex(value)}");
        });

        transport.Connect();
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
    }

    public void Disconnect()
    {
        var t = transport;
        if (t == null) return;
        AppendLog("Disconnecting...");
        Task.Run(() => t.Disconnect());
    }

    private void AppendLog(string line)
    {
        void Add()
        {
            Log.Add(line);
            while (Log.Count > 500) Log.RemoveAt(0);   // bound growth
        }
        if (dispatcher.IsDispatchRequired) dispatcher.Post(Add);
        else Add();
    }

    private static string BytesToHex(byte[] data)
    {
        var sb = new System.Text.StringBuilder(data.Length * 3);
        foreach (byte b in data) { sb.Append(b.ToString("X2")); sb.Append(' '); }
        return sb.ToString().TrimEnd();
    }
}
