namespace EutherDrive.Core.Cpu.M68000Emu;

internal static class Bits
{
    public static bool Test(this byte value, int bit)
    {
        return (value & (1 << bit)) != 0;
    }

    public static bool Test(this ushort value, int bit)
    {
        return (value & (1 << bit)) != 0;
    }

    public static bool Test(this uint value, int bit)
    {
        return (value & (1u << bit)) != 0;
    }

    public static bool SignBit(this byte value) => (value & 0x80) != 0;
    public static bool SignBit(this ushort value) => (value & 0x8000) != 0;
    public static bool SignBit(this uint value) => (value & 0x8000_0000) != 0;
    public static bool SignBit(this sbyte value) => value < 0;
    public static bool SignBit(this short value) => value < 0;
    public static bool SignBit(this int value) => value < 0;
}
