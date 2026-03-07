namespace EutherDrive.Core.MdTracerCore;

internal sealed class SvpBusOverride : IM68kBusOverride
{
    private readonly SvpCore _svp = new();
    private byte[] _romBytes;

    public SvpBusOverride(byte[] romBytes)
    {
        _romBytes = romBytes ?? System.Array.Empty<byte>();
    }

    public void UpdateRom(byte[] romBytes)
    {
        _romBytes = romBytes ?? System.Array.Empty<byte>();
    }

    internal void Tick(uint m68kCycles)
    {
        _svp.Tick(_romBytes, m68kCycles);
    }

    private static bool Handles(uint address)
    {
        return (address >= 0x0030_0000 && address <= 0x0037_FFFF)
            || (address >= 0x00A1_5000 && address <= 0x00A1_5007);
    }

    public bool TryRead8(uint address, out byte value)
    {
        if (!Handles(address))
        {
            value = 0;
            return false;
        }

        ushort word = _svp.M68kReadWord(address & ~1U, _romBytes);
        value = (address & 1) == 0 ? (byte)(word >> 8) : (byte)(word & 0xFF);
        return true;
    }

    public bool TryRead16(uint address, out ushort value)
    {
        if (!Handles(address))
        {
            value = 0;
            return false;
        }

        value = _svp.M68kReadWord(address & ~1U, _romBytes);
        return true;
    }

    public bool TryRead32(uint address, out uint value)
    {
        if (!Handles(address))
        {
            value = 0;
            return false;
        }

        ushort msw = _svp.M68kReadWord(address & ~1U, _romBytes);
        ushort lsw = _svp.M68kReadWord((address & ~1U) + 2, _romBytes);
        value = ((uint)msw << 16) | lsw;
        return true;
    }

    public bool TryWrite8(uint address, byte value)
    {
        if (!Handles(address))
            return false;

        _svp.M68kWriteByte(address, value);
        return true;
    }

    public bool TryWrite16(uint address, ushort value)
    {
        if (!Handles(address))
            return false;

        _svp.M68kWriteWord(address & ~1U, value);
        return true;
    }

    public bool TryWrite32(uint address, uint value)
    {
        if (!Handles(address))
            return false;

        uint evenAddress = address & ~1U;
        _svp.M68kWriteWord(evenAddress, (ushort)(value >> 16));
        _svp.M68kWriteWord(evenAddress + 2, (ushort)value);
        return true;
    }
}
