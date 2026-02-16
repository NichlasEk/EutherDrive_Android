namespace EutherDrive.Core.SegaCd;

public sealed class SegaCdPcmStub
{
    public byte Read(uint address) => 0x00;

    public void Write(uint address, byte value)
    {
        // TODO: RF5C164 implementation
    }
}
