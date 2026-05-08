using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core.Arcade.DataEast.Hshavoc;

internal sealed class HshavocBoardBusOverride : IM68kBusOverride
{
    private const uint AckWordAddress = 0x00FFF906;
    private static readonly bool TraceAck =
        string.Equals(System.Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_TRACE_ACK"), "1", System.StringComparison.Ordinal);

    private readonly IM68kBusOverride? _inner;
    private int _ackLogRemaining = 16;

    public HshavocBoardBusOverride(IM68kBusOverride? inner)
    {
        _inner = inner;
    }

    public bool TryRead8(uint address, out byte value)
        => _inner?.TryRead8(address, out value) ?? NoByte(out value);

    public bool TryRead16(uint address, out ushort value)
        => _inner?.TryRead16(address, out value) ?? NoWord(out value);

    public bool TryRead32(uint address, out uint value)
        => _inner?.TryRead32(address, out value) ?? NoLong(out value);

    public bool TryWrite8(uint address, byte value)
    {
        if (_inner?.TryWrite8(address, value) == true)
            return true;

        if (!TouchesAck(address, 1))
            return false;

        ClearAckWord(value);
        return true;
    }

    public bool TryWrite16(uint address, ushort value)
    {
        if (_inner?.TryWrite16(address, value) == true)
            return true;

        if (!TouchesAck(address, 2))
            return false;

        ClearAckWord(value);
        return true;
    }

    public bool TryWrite32(uint address, uint value)
    {
        if (_inner?.TryWrite32(address, value) == true)
            return true;

        if (!TouchesAck(address, 4))
            return false;

        ClearAckWord(value);
        return true;
    }

    private static bool TouchesAck(uint address, uint size)
    {
        uint start = address & 0x00FFFFFF;
        uint end = start + size - 1;
        return start <= AckWordAddress + 1 && end >= AckWordAddress;
    }

    private void ClearAckWord(uint attemptedValue)
    {
        md_m68k.InitMemoryIfNeeded();
        byte[] memory = md_m68k.g_memory!;
        memory[AckWordAddress] = 0;
        memory[AckWordAddress + 1] = 0;

        if (!TraceAck || _ackLogRemaining <= 0)
            return;

        _ackLogRemaining--;
        System.Console.WriteLine(
            $"[HSHAVOC-ACK] pc=0x{md_m68k.g_reg_PC:X6} addr=0x{AckWordAddress:X6} attempted=0x{attemptedValue:X8} forced=0");
    }

    private static bool NoByte(out byte value)
    {
        value = 0;
        return false;
    }

    private static bool NoWord(out ushort value)
    {
        value = 0;
        return false;
    }

    private static bool NoLong(out uint value)
    {
        value = 0;
        return false;
    }
}
