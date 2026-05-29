using System;

namespace HTCommander.Core.Abstractions;

/// <summary>
/// Platform-neutral transport to a radio (Windows WinRT Bluetooth, Linux BlueZ,
/// macOS CoreBluetooth, ...). Abstracts the connection so the platform-neutral
/// Radio logic does not depend on a concrete Bluetooth implementation.
///
/// Signatures mirror the original RadioBluetoothWin so existing call sites in
/// Radio.cs need only swap the concrete type for this interface.
/// </summary>
public interface IRadioTransport
{
    /// <summary>Raised once the transport has connected to the radio.</summary>
    event Action OnConnected;

    /// <summary>Raised when a decoded payload arrives (sender, error, value).</summary>
    event Action<IRadioTransport, Exception, byte[]> ReceivedData;

    /// <summary>Begins connecting. Returns false if already connecting/connected.</summary>
    bool Connect();

    /// <summary>Disconnects and releases the transport.</summary>
    void Disconnect();

    /// <summary>Queues a command for transmission, tagged with its expected response code.</summary>
    void EnqueueWrite(int expectedResponse, byte[] cmdData);
}
