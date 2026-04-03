namespace ePceCD
{
    public enum PceCdBiosMode
    {
        Rom = 0,
        Auto = 1,
        Hle = 2
    }

    internal enum PceCdBiosCallStatus
    {
        Unknown = 0,
        Traced = 1,
        Stubbed = 2,
        PartiallyImplemented = 3,
        Implemented = 4,
        TimingSensitive = 5
    }

    public enum PceCdBiosTransferType
    {
        Reset = 0,
        Jsr = 1,
        Bsr = 2,
        Jmp = 3,
        Rts = 4,
        Rti = 5,
        Irq = 6
    }
}
