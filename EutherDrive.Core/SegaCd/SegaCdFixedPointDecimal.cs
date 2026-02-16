namespace EutherDrive.Core.SegaCd;

internal readonly struct SegaCdFixedPointDecimal
{
    // 13 integer bits + 11 fractional bits
    private const uint Mask = (1u << 24) - 1u;
    private readonly uint _value;

    private SegaCdFixedPointDecimal(uint value)
    {
        _value = value & Mask;
    }

    public static SegaCdFixedPointDecimal FromPosition(ushort positionWord)
    {
        // Positions have 13 integer bits and 3 fractional bits; shift left 8 to get 11 fractional bits.
        return new SegaCdFixedPointDecimal((uint)positionWord << 8);
    }

    public static SegaCdFixedPointDecimal FromDelta(ushort deltaWord)
    {
        // Deltas have a sign bit, 4 integer bits, and 11 fractional bits; sign-extend to 32 bits.
        return new SegaCdFixedPointDecimal(unchecked((uint)(short)deltaWord));
    }

    public uint IntegerPart => (_value & Mask) >> 11;

    public static SegaCdFixedPointDecimal operator +(SegaCdFixedPointDecimal left, SegaCdFixedPointDecimal right)
    {
        return new SegaCdFixedPointDecimal((left._value + right._value) & Mask);
    }
}
