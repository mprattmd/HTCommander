namespace HTCommander.Core.Abstractions;

/// <summary>
/// Abstraction over the radio data transport (Windows WinRT Bluetooth /
/// Linux BlueZ / macOS). Replaces direct use of RadioBluetoothWin.
/// </summary>
public interface IRadioTransport
{
    /// <summary>Raised when the transport has established a connection.</summary>
    event EventHandler? OnConnected;

    /// <summary>Raised when a decoded data frame has been received from the radio.</summary>
    event EventHandler<byte[]>? ReceivedData;

    /// <summary>Begins connecting to the radio asynchronously.</summary>
    void Connect();

    /// <summary>Disconnects from the radio and releases transport resources.</summary>
    void Disconnect();

    /// <summary>Encodes and enqueues a command for transmission to the radio.</summary>
    void EnqueueWrite(int expectedResponse, byte[] data);
}
