namespace EutherDrive.Core.Cpu.M68000Emu;

public readonly struct BusSignals
{
    public readonly bool Reset;

    public BusSignals(bool reset)
    {
        Reset = reset;
    }
}

public interface IBusInterface
{
    byte ReadByte(uint address);
    ushort ReadWord(uint address);
    uint ReadLong(uint address);
    void WriteByte(uint address, byte value);
    void WriteWord(uint address, ushort value);
    void WriteLong(uint address, uint value);

    byte InterruptLevel();
    void AcknowledgeInterrupt(byte level);

    bool Reset();
    bool Halt();

    BusSignals Signals { get; }
    ushort CurrentOpcode { get; }
}
