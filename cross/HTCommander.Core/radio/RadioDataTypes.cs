/*
Copyright 2026 Ylian Saint-Hilaire
Licensed under the Apache License, Version 2.0 (the "License");
http://www.apache.org/licenses/LICENSE-2.0
*/

using System;

namespace HTCommander
{
    // Platform-neutral data types extracted from the WinForms-coupled Radio.cs
    // so that portable Core code (AX25Session, SoftwareModem, RadioHtStatus, ...)
    // can compile without depending on the full Radio class. During the later
    // WinForms->Core consolidation, the originals in src/radio/Radio.cs should be
    // removed in favour of these.

    /// <summary>Radio channel type (was Radio.RadioChannelType).</summary>
    public enum RadioChannelType : int { OFF = 0, A = 1, B = 2 }

    /// <summary>Channel modulation (was Radio.RadioModulationType).</summary>
    public enum RadioModulationType : int { FM = 0, AM = 1, DMR = 2 }

    /// <summary>Channel bandwidth (was Radio.RadioBandwidthType).</summary>
    public enum RadioBandwidthType : int { NARROW = 0, WIDE = 1 }

    /// <summary>Current lock state of the radio.</summary>
    public class RadioLockState
    {
        public bool IsLocked { get; set; }
        public string Usage { get; set; }
        public int RegionId { get; set; }
        public int ChannelId { get; set; }
    }

    /// <summary>Data class for setting a lock on the radio.</summary>
    public class SetLockData
    {
        public string Usage { get; set; }
        public int RegionId { get; set; }
        public int ChannelId { get; set; }
    }

    /// <summary>Data class for unlocking the radio.</summary>
    public class SetUnlockData
    {
        public string Usage { get; set; }
    }

    /// <summary>Data class for transmitting AX.25 / BSS packets via the Data Broker.</summary>
    public class TransmitDataFrameData
    {
        public AX25Packet Packet { get; set; }
        public BSSPacket BSSPacket { get; set; }
        public int ChannelId { get; set; } = -1;
        public int RegionId { get; set; } = -1;
    }
}
