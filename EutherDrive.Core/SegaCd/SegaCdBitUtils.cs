namespace EutherDrive.Core.SegaCd;

internal static class SegaCdBitUtils
{
    public static bool Bit(this byte value, int bit) => (value & (1 << bit)) != 0;

    public static byte Msb(this ushort value) => (byte)(value >> 8);

    public static byte Lsb(this ushort value) => (byte)(value & 0xFF);

    public static void SetMsb(ref ushort value, byte msb)
    {
        value = (ushort)((value & 0x00FF) | (msb << 8));
    }

    public static void SetLsb(ref ushort value, byte lsb)
    {
        value = (ushort)((value & 0xFF00) | lsb);
    }
}
