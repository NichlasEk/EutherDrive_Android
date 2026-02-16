namespace EutherDrive.Core.MdTracerCore;

internal interface IM68kBusOverride
{
    bool TryRead8(uint address, out byte value);
    bool TryRead16(uint address, out ushort value);
    bool TryRead32(uint address, out uint value);
    bool TryWrite8(uint address, byte value);
    bool TryWrite16(uint address, ushort value);
    bool TryWrite32(uint address, uint value);
}
