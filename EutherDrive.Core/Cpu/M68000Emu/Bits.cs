namespace EutherDrive.Core.Cpu.M68000Emu;

internal static class Bits
{
    public static bool Test(this ushort value, int bit)
    {
        return (value & (1 << bit)) != 0;
    }
}
