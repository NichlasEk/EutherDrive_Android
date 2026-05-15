namespace EutherDrive.Core.Cpu.V25Emu;

public interface IV25Bus
{
    byte V25Read8(uint address);
    void V25Write8(uint address, byte value);
    bool V25TryGetInternalOffset(uint address, out ushort offset)
    {
        address &= 0x0f_ffff;
        if ((address & 0xffe00) == 0xffe00 || address == 0xfffff)
        {
            offset = (ushort)(address & 0x01ff);
            return true;
        }

        offset = 0;
        return false;
    }

    byte V25ReadInternal8(ushort address) => 0xff;
    void V25WriteInternal8(ushort address, byte value) { }
}
