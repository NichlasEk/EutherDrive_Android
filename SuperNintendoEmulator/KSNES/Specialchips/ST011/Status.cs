namespace KSNES.Specialchips.ST011;

internal struct Status
{
    public bool RequestForMaster;
    public bool UserFlag0;
    public bool UserFlag1;
    public bool DrBusy;
    public DataRegisterBits DrControl;

    public void Write(ushort value)
    {
        UserFlag1 = ((value >> 14) & 1) != 0;
        UserFlag0 = ((value >> 13) & 1) != 0;
        DrControl = ((value >> 10) & 1) != 0 ? DataRegisterBits.Eight : DataRegisterBits.Sixteen;
    }

    public byte ToByte()
    {
        return (byte)(
            (RequestForMaster ? 0x80 : 0) |
            (UserFlag1 ? 0x40 : 0) |
            (UserFlag0 ? 0x20 : 0) |
            (DrBusy ? 0x10 : 0) |
            (DrControl == DataRegisterBits.Eight ? 0x04 : 0)
        );
    }
}