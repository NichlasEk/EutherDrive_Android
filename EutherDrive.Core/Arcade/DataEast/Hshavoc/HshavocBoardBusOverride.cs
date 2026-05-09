using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core.Arcade.DataEast.Hshavoc;

internal sealed class HshavocBoardBusOverride : IM68kBusOverride
{
    private const uint AckWordAddress = 0x00FFF906;
    private const uint VdpStartAddress = 0x00C00000;
    private const uint VdpEndAddress = 0x00C0001F;
    private const uint IoStartAddress = 0x00A10000;
    private const uint IoEndAddress = 0x00A10FFF;
    private const uint DefaultTraceRamStart = 0x00FFE800;
    private const uint DefaultTraceRamEnd = 0x00FFEAC0;
    private static readonly bool TraceAck =
        string.Equals(System.Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_TRACE_ACK"), "1", System.StringComparison.Ordinal);
    private static readonly bool TraceVdp =
        string.Equals(System.Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_TRACE_VDP"), "1", System.StringComparison.Ordinal);
    private static readonly bool TraceIo =
        string.Equals(System.Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_TRACE_IO"), "1", System.StringComparison.Ordinal);
    private static readonly bool TraceRam =
        string.Equals(System.Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_TRACE_RAM"), "1", System.StringComparison.Ordinal);
    private static readonly bool RepairVdpRegisterPending =
        string.Equals(System.Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_REPAIR_VDP_REG_PENDING"), "1", System.StringComparison.Ordinal);
    private static readonly uint TraceRamStart = ParseHex("EUTHERDRIVE_HSHAVOC_TRACE_RAM_START", DefaultTraceRamStart);
    private static readonly uint TraceRamEnd = ParseHex("EUTHERDRIVE_HSHAVOC_TRACE_RAM_END", DefaultTraceRamEnd);

    private readonly IM68kBusOverride? _inner;
    private int _ackLogRemaining = 16;
    private int _vdpLogRemaining = ParseLimit("EUTHERDRIVE_HSHAVOC_TRACE_VDP_MAX", 128);
    private int _ioLogRemaining = ParseLimit("EUTHERDRIVE_HSHAVOC_TRACE_IO_MAX", 128);
    private int _ramLogRemaining = ParseLimit("EUTHERDRIVE_HSHAVOC_TRACE_RAM_MAX", 160);
    private int _vdpRepairLogRemaining = ParseLimit("EUTHERDRIVE_HSHAVOC_REPAIR_VDP_REG_PENDING_MAX", 32);

    public HshavocBoardBusOverride(IM68kBusOverride? inner)
    {
        _inner = inner;
    }

    public bool TryRead8(uint address, out byte value)
    {
        if (_inner?.TryRead8(address, out value) == true)
            return true;

        TraceIoRead(address, 1);
        TraceRamAccess("RAM-R", address, 1, 0);
        return NoByte(out value);
    }

    public bool TryRead16(uint address, out ushort value)
    {
        if (_inner?.TryRead16(address, out value) == true)
            return true;

        TraceIoRead(address, 2);
        TraceRamAccess("RAM-R", address, 2, 0);
        return NoWord(out value);
    }

    public bool TryRead32(uint address, out uint value)
    {
        if (_inner?.TryRead32(address, out value) == true)
            return true;

        TraceIoRead(address, 4);
        TraceRamAccess("RAM-R", address, 4, 0);
        return NoLong(out value);
    }

    public bool TryWrite8(uint address, byte value)
    {
        if (_inner?.TryWrite8(address, value) == true)
            return true;

        if (!TouchesAck(address, 1))
        {
            TraceVdpWrite(address, 1, value);
            TraceRamAccess("RAM-W", address, 1, value);
            return false;
        }

        ClearAckWord(value);
        return true;
    }

    public bool TryWrite16(uint address, ushort value)
    {
        if (_inner?.TryWrite16(address, value) == true)
            return true;

        if (TryRepairVdpRegisterPending(address, value))
            return true;

        if (!TouchesAck(address, 2))
        {
            TraceVdpWrite(address, 2, value);
            TraceRamAccess("RAM-W", address, 2, value);
            return false;
        }

        ClearAckWord(value);
        return true;
    }

    public bool TryWrite32(uint address, uint value)
    {
        if (_inner?.TryWrite32(address, value) == true)
            return true;

        if (TryRepairVdpRegisterPending(address, value))
            return true;

        if (!TouchesAck(address, 4))
        {
            TraceVdpWrite(address, 4, value);
            TraceRamAccess("RAM-W", address, 4, value);
            return false;
        }

        ClearAckWord(value);
        return true;
    }

    private static bool TouchesAck(uint address, uint size)
    {
        uint start = address & 0x00FFFFFF;
        uint end = start + size - 1;
        return start <= AckWordAddress + 1 && end >= AckWordAddress;
    }

    private void TraceVdpWrite(uint address, int size, uint value)
    {
        if (!TraceVdp || _vdpLogRemaining <= 0)
            return;

        uint masked = address & 0x00FFFFFF;
        if (masked < VdpStartAddress || masked > VdpEndAddress)
            return;

        _vdpLogRemaining--;
        System.Console.WriteLine(
            $"[HSHAVOC-VDP] pc=0x{md_m68k.g_reg_PC:X6} frame={FrameCounter()} size={size} addr=0x{masked:X6} value=0x{value:X8}");
    }

    private bool TryRepairVdpRegisterPending(uint address, ushort value)
    {
        if (!RepairVdpRegisterPending || !IsVdpControlPort(address) || !IsVdpRegisterWrite(value))
            return false;

        md_main.g_md_vdp?.read16(0x00C00004);
        md_main.g_md_vdp?.write16(0x00C00004, value);
        TraceVdpRegisterRepair(address, value);
        return true;
    }

    private bool TryRepairVdpRegisterPending(uint address, uint value)
    {
        if (!RepairVdpRegisterPending || !IsVdpControlPort(address))
            return false;

        ushort hi = (ushort)(value >> 16);
        ushort lo = (ushort)value;
        if (!IsVdpRegisterWrite(hi) || !IsVdpRegisterWrite(lo))
            return false;

        md_main.g_md_vdp?.read16(0x00C00004);
        md_main.g_md_vdp?.write16(0x00C00004, hi);
        md_main.g_md_vdp?.read16(0x00C00004);
        md_main.g_md_vdp?.write16(0x00C00004, lo);
        TraceVdpRegisterRepair(address, value);
        return true;
    }

    private void TraceVdpRegisterRepair(uint address, uint value)
    {
        if (_vdpRepairLogRemaining <= 0)
            return;

        _vdpRepairLogRemaining--;
        System.Console.WriteLine(
            $"[HSHAVOC-VDP-REPAIR] pc=0x{md_m68k.g_reg_PC:X6} frame={FrameCounter()} addr=0x{(address & 0x00FFFFFF):X6} value=0x{value:X8}");
    }

    private void TraceIoRead(uint address, int size)
    {
        if (!TraceIo || _ioLogRemaining <= 0)
            return;

        uint masked = address & 0x00FFFFFF;
        if (masked < IoStartAddress || masked > IoEndAddress)
            return;

        _ioLogRemaining--;
        System.Console.WriteLine(
            $"[HSHAVOC-IO-R] pc=0x{md_m68k.g_reg_PC:X6} frame={FrameCounter()} size={size} addr=0x{masked:X6}");
    }

    private void TraceRamAccess(string tag, uint address, int size, uint value)
    {
        if (!TraceRam || _ramLogRemaining <= 0)
            return;

        uint masked = address & 0x00FFFFFF;
        uint end = masked + (uint)size - 1;
        if (masked > TraceRamEnd || end < TraceRamStart)
            return;

        _ramLogRemaining--;
        System.Console.WriteLine(
            $"[HSHAVOC-{tag}] pc=0x{md_m68k.g_reg_PC:X6} frame={FrameCounter()} size={size} addr=0x{masked:X6} value=0x{value:X8}");
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

    private static long FrameCounter()
        => md_main.g_md_vdp?.FrameCounter ?? -1;

    private static bool IsVdpControlPort(uint address)
    {
        uint masked = address & 0x00FFFFFF;
        return masked >= 0x00C00004 && masked <= 0x00C00007;
    }

    private static bool IsVdpRegisterWrite(ushort value)
        => (value & 0xC000) == 0x8000;

    private static int ParseLimit(string name, int fallback)
    {
        string? raw = System.Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int value) && value >= 0 ? value : fallback;
    }

    private static uint ParseHex(string name, uint fallback)
    {
        string? raw = System.Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        raw = raw.Trim();
        if (raw.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];
        return uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint value)
            ? value & 0x00FFFFFF
            : fallback;
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
