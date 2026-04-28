namespace EutherDrive.Core.Cpu.V60Emu;

// NEC V60 behavior is translated from MAME's BSD-3-Clause V60 core by
// Farfetch'd and R. Belmont.
public interface IV60Bus
{
    byte Read8(uint address);
    void Write8(uint address, byte value);

    ushort Read16(uint address)
    {
        byte lo = Read8(address);
        byte hi = Read8(address + 1);
        return (ushort)(lo | (hi << 8));
    }

    uint Read32(uint address)
    {
        ushort lo = Read16(address);
        ushort hi = Read16(address + 2);
        return (uint)(lo | (hi << 16));
    }

    void Write16(uint address, ushort value)
    {
        Write8(address, (byte)value);
        Write8(address + 1, (byte)(value >> 8));
    }

    void Write32(uint address, uint value)
    {
        Write16(address, (ushort)value);
        Write16(address + 2, (ushort)(value >> 16));
    }
}
