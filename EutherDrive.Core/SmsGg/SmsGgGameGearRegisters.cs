namespace EutherDrive.Core.SmsGg;

public sealed class SmsGgGameGearRegisters
{
    public byte ExtPort { get; set; } = 0x7F;
    public byte ParallelPort { get; set; } = 0xFF;

    public void Reset()
    {
        ExtPort = 0x7F;
        ParallelPort = 0xFF;
    }
}
