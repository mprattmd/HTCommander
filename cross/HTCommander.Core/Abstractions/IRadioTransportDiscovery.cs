namespace HTCommander.Core.Abstractions;

/// <summary>
/// Discovers Bluetooth radios and reports adapter availability.
/// Replaces the static discovery helpers on RadioBluetoothWin.
/// </summary>
public interface IRadioTransportDiscovery
{
    /// <summary>Returns true if a usable Bluetooth adapter is present.</summary>
    bool CheckBluetooth();

    /// <summary>Returns the names of all paired/known Bluetooth devices.</summary>
    IReadOnlyList<string> GetDeviceNames();

    /// <summary>Returns the names of devices that are compatible radios.</summary>
    IReadOnlyList<string> FindCompatibleDevices();

    /// <summary>
    /// Returns compatible radios with their BD_ADDR alongside the name — what a
    /// caller needs to actually open a transport (<see cref="FindCompatibleDevices"/>
    /// only exposes names).
    /// </summary>
    IReadOnlyList<RadioDeviceInfo> FindCompatibleRadios();
}
