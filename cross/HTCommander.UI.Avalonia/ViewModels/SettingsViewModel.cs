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
using HTCommander;                       // DataBroker
using HTCommander.Core.Abstractions;
using HTCommander.Core.Abstractions.Audio;
using HTCommander.Platform.Linux;
using HTCommander.Platform.Linux.Audio;

namespace HTCommander.UI.Avalonia.ViewModels;

/// <summary>
/// Settings: audio output/input device selection + output volume, persisted
/// through <c>DataBroker</c> (device 0, the global settings device) → JSON config,
/// using the same keys as the WinForms app (OutputAudioDevice / InputAudioDevice /
/// OutputVolume). Devices come from <see cref="IAudioDeviceEnumerator"/>. A
/// "test tone" plays through the chosen output via <see cref="PortAudioPlayback"/>.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private const int SettingsDevice = 0;
    private static readonly AudioDevice DefaultEntry = new("", "Default");

    private readonly IUiDispatcher dispatcher;
    private readonly IAudioDeviceEnumerator enumerator;
    private bool loading;

    public ObservableCollection<AudioDevice> OutputDevices { get; } = new();
    public ObservableCollection<AudioDevice> InputDevices { get; } = new();

    public SettingsViewModel(IUiDispatcher dispatcher, IAudioDeviceEnumerator enumerator)
    {
        this.dispatcher = dispatcher;
        this.enumerator = enumerator;
        gpsBroker = new DataBrokerClient();
        gpsBroker.Subscribe(1, "GpsStatus", (_, _, data) => dispatcher.Post(() => { if (data is string s) GpsStatus = s; }));
        RefreshDevices();
        RefreshSerialPorts();
    }

    // ---- GPS source (serial NMEA) -----------------------------------------
    private readonly DataBrokerClient gpsBroker;
    private const string NoPort = "None";

    public ObservableCollection<string> SerialPorts { get; } = new();
    public int[] BaudRates { get; } = { 4800, 9600, 19200, 38400, 57600, 115200 };

    private string selectedGpsPort = NoPort;
    public string SelectedGpsPort
    {
        get => selectedGpsPort;
        set
        {
            if (!SetField(ref selectedGpsPort, value) || loading || value == null) return;
            DataBroker.Dispatch(SettingsDevice, "GpsSerialPort", value, store: true);   // GpsSerialHandler reacts to this
        }
    }

    private int selectedGpsBaud = 4800;
    public int SelectedGpsBaud
    {
        get => selectedGpsBaud;
        set
        {
            if (!SetField(ref selectedGpsBaud, value) || loading) return;
            DataBroker.Dispatch(SettingsDevice, "GpsBaudRate", value, store: true);
        }
    }

    private string gpsStatus = "Disabled";
    public string GpsStatus { get => gpsStatus; private set => SetField(ref gpsStatus, value); }

    /// <summary>Re-scans available serial ports and restores the persisted GPS selection.</summary>
    public void RefreshSerialPorts()
    {
        Task.Run(() =>
        {
            var ports = SerialPortEnumerator.ListPorts();
            string savedPort = DataBroker.GetValue<string>(SettingsDevice, "GpsSerialPort", NoPort) ?? NoPort;
            int savedBaud = DataBroker.GetValue<int>(SettingsDevice, "GpsBaudRate", 4800);
            dispatcher.Post(() =>
            {
                loading = true;
                SerialPorts.Clear();
                SerialPorts.Add(NoPort);
                foreach (var p in ports) SerialPorts.Add(p);
                SelectedGpsPort = SerialPorts.Contains(savedPort) ? savedPort : NoPort;
                SelectedGpsBaud = BaudRates.Contains(savedBaud) ? savedBaud : 4800;
                loading = false;
            });
        });
    }

    private AudioDevice? selectedOutput;
    public AudioDevice? SelectedOutput
    {
        get => selectedOutput;
        set
        {
            if (!SetField(ref selectedOutput, value) || loading || value == null) return;
            DataBroker.Dispatch(SettingsDevice, "OutputAudioDevice", value.Id, store: true);
            DataBroker.Dispatch(SettingsDevice, "SetOutputAudioDevice", value.Id, store: false);
        }
    }

    private AudioDevice? selectedInput;
    public AudioDevice? SelectedInput
    {
        get => selectedInput;
        set
        {
            if (!SetField(ref selectedInput, value) || loading || value == null) return;
            DataBroker.Dispatch(SettingsDevice, "InputAudioDevice", value.Id, store: true);
            DataBroker.Dispatch(SettingsDevice, "SetInputAudioDevice", value.Id, store: false);
        }
    }

    private int outputVolumePercent = 100;
    public int OutputVolumePercent
    {
        get => outputVolumePercent;
        set
        {
            if (!SetField(ref outputVolumePercent, value) || loading) return;
            DataBroker.Dispatch(SettingsDevice, "OutputVolume", value / 100.0f, store: true);
        }
    }

    private int micGainPercent = 400;   // 4x default boost (mic was quiet at unity)
    public int MicGainPercent
    {
        get => micGainPercent;
        set
        {
            if (!SetField(ref micGainPercent, value) || loading) return;
            DataBroker.Dispatch(SettingsDevice, "MicGain", value / 100.0f, store: true);
        }
    }

    /// <summary>Linear mic gain (e.g. 4.0) for transmit, from the persisted percent.</summary>
    public float MicGain => micGainPercent / 100.0f;

    /// <summary>Linear output volume for RX playback.</summary>
    public float OutputVolume => outputVolumePercent / 100.0f;

    /// <summary>Selected output/input device ids ("" = system default) for the voice path.</summary>
    public string? OutputDeviceId => SelectedOutput?.Id;
    public string? InputDeviceId => SelectedInput?.Id;

    private string testStatus = "";
    public string TestStatus { get => testStatus; private set => SetField(ref testStatus, value); }

    /// <summary>Re-enumerates devices and restores the persisted selections.</summary>
    public void RefreshDevices()
    {
        Task.Run(() =>
        {
            var outs = enumerator.GetRenderDevices().ToList();
            var ins = enumerator.GetCaptureDevices().ToList();
            string outId = DataBroker.GetValue<string>(SettingsDevice, "OutputAudioDevice", "") ?? "";
            string inId = DataBroker.GetValue<string>(SettingsDevice, "InputAudioDevice", "") ?? "";
            float vol = DataBroker.GetValue<float>(SettingsDevice, "OutputVolume", 1.0f);
            float micGain = DataBroker.GetValue<float>(SettingsDevice, "MicGain", 4.0f);   // 4x default (matches MicGainPercent)

            dispatcher.Post(() =>
            {
                loading = true;
                Repopulate(OutputDevices, outs);
                Repopulate(InputDevices, ins);
                SelectedOutput = OutputDevices.FirstOrDefault(d => d.Id == outId) ?? OutputDevices.FirstOrDefault();
                SelectedInput = InputDevices.FirstOrDefault(d => d.Id == inId) ?? InputDevices.FirstOrDefault();
                OutputVolumePercent = (int)Math.Round(Math.Clamp(vol, 0f, 1.5f) * 100);
                MicGainPercent = (int)Math.Round(Math.Clamp(micGain, 1f, 40f) * 100);
                loading = false;
            });
        });
    }

    private static void Repopulate(ObservableCollection<AudioDevice> target, System.Collections.Generic.List<AudioDevice> devices)
    {
        target.Clear();
        target.Add(DefaultEntry);
        foreach (var d in devices) target.Add(d);
    }

    /// <summary>Plays a short 440 Hz tone through the selected output device.</summary>
    public void TestOutput()
    {
        string id = SelectedOutput?.Id ?? "";
        float vol = OutputVolumePercent / 100.0f;
        TestStatus = "Playing test tone...";
        Task.Run(() =>
        {
            var fmt = AudioFormat.RadioPcm;
            byte[] tone = new byte[fmt.SampleRate * 2 / 2];   // 0.5s mono 16-bit
            for (int i = 0; i < tone.Length / 2; i++)
            {
                short s = (short)(Math.Sin(2 * Math.PI * 440 * i / fmt.SampleRate) * 8000);
                tone[i * 2] = (byte)(s & 0xFF);
                tone[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }

            var play = new PortAudioPlayback { Format = fmt, Volume = vol };
            play.SetDevice(string.IsNullOrEmpty(id) ? null : id);
            bool ok = play.Start();
            string result;
            if (ok)
            {
                play.AddSamples(tone, 0, tone.Length);
                System.Threading.Thread.Sleep(700);
                play.Stop();
                result = "Test tone played.";
            }
            else { result = "Could not open the output device."; }
            play.Dispose();
            dispatcher.Post(() => TestStatus = result);
        });
    }
}
