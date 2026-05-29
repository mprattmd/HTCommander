namespace HTCommander.Core.Abstractions;

/// <summary>
/// Transmits APRS packets. Replaces the AprsStack dependency on MainForm/Radio
/// for outbound transmission.
/// </summary>
public interface IAprsTransport
{
    /// <summary>Transmits an APRS packet.</summary>
    void TransmitAprs(byte[] packet);
}
