namespace EutherDrive.Core.SegaCd;

public sealed class SegaCdFontRegisters
{
    private byte _color0;
    private byte _color1;
    private ushort _fontBits;

    public byte ReadColor()
    {
        return (byte)((_color1 << 4) | _color0);
    }

    public void WriteColor(byte value)
    {
        _color0 = (byte)(value & 0x0F);
        _color1 = (byte)(value >> 4);
    }

    public ushort FontBits => _fontBits;

    public void WriteFontBits(ushort value)
    {
        _fontBits = value;
    }

    public void WriteFontBitsMsb(byte value)
    {
        _fontBits = (ushort)((value << 8) | (_fontBits & 0x00FF));
    }

    public void WriteFontBitsLsb(byte value)
    {
        _fontBits = (ushort)((_fontBits & 0xFF00) | value);
    }

    public ushort ReadFontData(uint address)
    {
        uint wordIndex = (address & 0x07) >> 1;
        byte baseFontBit = (byte)((3 - wordIndex) << 2);

        ushort result = 0;
        for (int i = 0; i < 16; i++)
        {
            byte fontBitIndex = (byte)(baseFontBit + (i >> 2));
            int fontColorIndex = i & 0x03;
            bool fontBit = ((_fontBits >> fontBitIndex) & 1) != 0;
            byte colorNibble = fontBit ? _color1 : _color0;
            bool colorBit = ((colorNibble >> fontColorIndex) & 1) != 0;
            if (colorBit)
                result |= (ushort)(1 << i);
        }

        return result;
    }
}
