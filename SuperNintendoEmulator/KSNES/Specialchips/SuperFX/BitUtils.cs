namespace KSNES.Specialchips.SuperFX;

internal static class BitUtils
{
    public static bool Bit(this int value, int bit) => ((value >> bit) & 1) != 0;
    public static bool Bit(this uint value, int bit) => ((value >> bit) & 1) != 0;
    public static bool Bit(this ushort value, int bit) => ((value >> bit) & 1) != 0;
    public static bool Bit(this byte value, int bit) => ((value >> bit) & 1) != 0;

    public static bool SignBit(this short value) => (value & 0x8000) != 0;
    public static bool SignBit(this int value) => (value & 0x8000) != 0;
    public static bool SignBit(this ushort value) => (value & 0x8000) != 0;
    public static bool SignBit(this byte value) => (value & 0x80) != 0;

    public static byte Lsb(this ushort value) => (byte)(value & 0xFF);
    public static byte Msb(this ushort value) => (byte)((value >> 8) & 0xFF);

    public static void SetLsb(ref ushort value, byte lsb)
    {
        value = (ushort)((value & 0xFF00) | lsb);
    }

    public static void SetMsb(ref ushort value, byte msb)
    {
        value = (ushort)((value & 0x00FF) | (msb << 8));
    }
}
