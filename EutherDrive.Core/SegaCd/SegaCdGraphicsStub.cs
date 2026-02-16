namespace EutherDrive.Core.SegaCd;

public sealed class SegaCdGraphicsStub
{
    private bool _interruptPending;

    public byte ReadRegisterByte(uint address) => 0x00;
    public ushort ReadRegisterWord(uint address) => 0x0000;

    public void WriteRegisterByte(uint address, byte value)
    {
    }

    public void WriteRegisterWord(uint address, ushort value)
    {
    }

    public bool InterruptPending => _interruptPending;

    public void AcknowledgeInterrupt()
    {
        _interruptPending = false;
    }
}
