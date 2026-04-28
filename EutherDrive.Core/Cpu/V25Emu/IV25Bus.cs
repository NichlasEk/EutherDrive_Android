namespace EutherDrive.Core.Cpu.V25Emu;

public interface IV25Bus
{
    byte V25Read8(uint address);
    void V25Write8(uint address, byte value);
}
