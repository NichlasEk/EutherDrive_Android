using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core.SegaCd;

internal sealed class SegaCdMainBusOverride : IM68kBusOverride
{
    private readonly SegaCdMemory _memory;

    public SegaCdMainBusOverride(SegaCdMemory memory)
    {
        _memory = memory;
    }

    private static bool Handles(uint address)
    {
        return address <= 0x007FFFFF || (address >= 0x00A12000 && address <= 0x00A1202F);
    }

    public bool TryRead8(uint address, out byte value)
    {
        if (!Handles(address))
        {
            value = 0;
            return false;
        }

        value = _memory.ReadMainByte(address);
        return true;
    }

    public bool TryRead16(uint address, out ushort value)
    {
        if (!Handles(address))
        {
            value = 0;
            return false;
        }

        value = _memory.ReadMainWord(address);
        return true;
    }

    public bool TryRead32(uint address, out uint value)
    {
        if (!Handles(address))
        {
            value = 0;
            return false;
        }

        ushort msw = _memory.ReadMainWord(address);
        ushort lsw = _memory.ReadMainWord(address + 2);
        value = (uint)((msw << 16) | lsw);
        return true;
    }

    public bool TryWrite8(uint address, byte value)
    {
        if (!Handles(address))
            return false;

        _memory.WriteMainByte(address, value);
        return true;
    }

    public bool TryWrite16(uint address, ushort value)
    {
        if (!Handles(address))
            return false;

        _memory.WriteMainWord(address, value);
        return true;
    }

    public bool TryWrite32(uint address, uint value)
    {
        if (!Handles(address))
            return false;

        _memory.WriteMainWord(address, (ushort)(value >> 16));
        _memory.WriteMainWord(address + 2, (ushort)value);
        return true;
    }
}

internal sealed class SegaCdSubBusOverride : IM68kBusOverride
{
    private readonly SegaCdMemory _memory;

    public SegaCdSubBusOverride(SegaCdMemory memory)
    {
        _memory = memory;
    }

    public bool TryRead8(uint address, out byte value)
    {
        value = _memory.ReadSubByte(address);
        return true;
    }

    public bool TryRead16(uint address, out ushort value)
    {
        value = _memory.ReadSubWord(address);
        return true;
    }

    public bool TryRead32(uint address, out uint value)
    {
        ushort msw = _memory.ReadSubWord(address);
        ushort lsw = _memory.ReadSubWord(address + 2);
        value = (uint)((msw << 16) | lsw);
        return true;
    }

    public bool TryWrite8(uint address, byte value)
    {
        _memory.WriteSubByte(address, value);
        return true;
    }

    public bool TryWrite16(uint address, ushort value)
    {
        _memory.WriteSubWord(address, value);
        return true;
    }

    public bool TryWrite32(uint address, uint value)
    {
        _memory.WriteSubWord(address, (ushort)(value >> 16));
        _memory.WriteSubWord(address + 2, (ushort)value);
        return true;
    }
}
