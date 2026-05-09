using EutherDrive.Core.MdTracerCore;
using System.Collections.Generic;
using System.IO;

namespace EutherDrive.Core.Arcade.DataEast.Hshavoc;

internal sealed class HshavocBoardBusOverride : IM68kBusOverride
{
    private const uint AckWordAddress = 0x00FFF906;
    private const uint BoardRamStartAddress = 0x00200000;
    private const uint BoardRamEndAddress = 0x002023FF;
    public const int BoardRamLength = (int)(BoardRamEndAddress - BoardRamStartAddress + 1);
    private const int BoardRamStateVersion = 1;
    private const uint LatchedVdpQueueBlock = 0x00FFE91A;
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
    private static readonly bool TraceRamSkipZero =
        string.Equals(System.Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_TRACE_RAM_SKIP_ZERO"), "1", System.StringComparison.Ordinal);
    private static readonly bool TraceRamRegs =
        string.Equals(System.Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_TRACE_RAM_REGS"), "1", System.StringComparison.Ordinal);
    private static readonly bool UiProofMode = IsUiProofMode();
    private static readonly bool FlushVdpCommandBlocksOnAck =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_FLUSH_VDP_COMMAND_BLOCKS") || UiProofMode;
    private static readonly bool TraceVdpCommandBlocks =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_TRACE_VDP_COMMAND_BLOCKS");
    private static readonly bool SkipRomVdpDma =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_SKIP_ROM_VDP_DMA");
    private static readonly bool RepairVdpRegisterPending =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_REPAIR_VDP_REG_PENDING") || UiProofMode;
    private static readonly bool ForceVBlankGateRead =
        !IsEnvDisabled("EUTHERDRIVE_HSHAVOC_FORCE_VBLANK_GATE_READ");
    private static readonly bool TraceVBlankGateRead =
        IsEnvEnabled("EUTHERDRIVE_HSHAVOC_TRACE_VBLANK_GATE_READ");
    private static readonly uint TraceRamStart = ParseHex("EUTHERDRIVE_HSHAVOC_TRACE_RAM_START", DefaultTraceRamStart);
    private static readonly uint TraceRamEnd = ParseHex("EUTHERDRIVE_HSHAVOC_TRACE_RAM_END", DefaultTraceRamEnd);
    private static readonly long TraceVdpFrameStart = ParseLong("EUTHERDRIVE_HSHAVOC_TRACE_VDP_FRAME_START", long.MinValue);
    private static readonly long TraceVdpFrameEnd = ParseLong("EUTHERDRIVE_HSHAVOC_TRACE_VDP_FRAME_END", long.MaxValue);
    private static readonly long TraceRamFrameStart = ParseLong("EUTHERDRIVE_HSHAVOC_TRACE_RAM_FRAME_START", long.MinValue);
    private static readonly long TraceRamFrameEnd = ParseLong("EUTHERDRIVE_HSHAVOC_TRACE_RAM_FRAME_END", long.MaxValue);
    private static readonly uint VdpCommandBlockStart = ParseHex("EUTHERDRIVE_HSHAVOC_VDP_COMMAND_BLOCK_START", 0x00FFE900);
    private static readonly uint VdpCommandBlockEnd = ParseHex("EUTHERDRIVE_HSHAVOC_VDP_COMMAND_BLOCK_END", 0x00FFEA80);

    private readonly IM68kBusOverride? _inner;
    private readonly byte[] _boardRam = new byte[BoardRamLength];
    private readonly HashSet<ulong> _flushedAckCommandBlocks = new();
    private readonly HashSet<ulong> _flushedLatchedQueueEntries = new();
    private uint _latchedSlot0Source;
    private ushort _latchedSlot0Destination;
    private ushort _latchedSlot0Length;
    private bool _latchedSlot0Active;
    private int _ackLogRemaining = 16;
    private int _vdpLogRemaining = ParseLimit("EUTHERDRIVE_HSHAVOC_TRACE_VDP_MAX", 128);
    private int _ioLogRemaining = ParseLimit("EUTHERDRIVE_HSHAVOC_TRACE_IO_MAX", 128);
    private int _ramLogRemaining = ParseLimit("EUTHERDRIVE_HSHAVOC_TRACE_RAM_MAX", 160);
    private int _vdpRepairLogRemaining = ParseLimit("EUTHERDRIVE_HSHAVOC_REPAIR_VDP_REG_PENDING_MAX", 32);
    private int _vblankGateReadLogRemaining = ParseLimit("EUTHERDRIVE_HSHAVOC_TRACE_VBLANK_GATE_READ_MAX", 32);
    private int _ackCommandBlockLogRemaining = ParseLimit("EUTHERDRIVE_HSHAVOC_TRACE_VDP_COMMAND_BLOCKS_MAX", 128);

    public HshavocBoardBusOverride(IM68kBusOverride? inner)
    {
        _inner = inner;
    }

    public void SaveBoardRamState(BinaryWriter writer)
    {
        writer.Write(BoardRamStateVersion);
        writer.Write(_boardRam.Length);
        writer.Write(_boardRam);
    }

    public void LoadBoardRamState(BinaryReader reader)
    {
        int version = reader.ReadInt32();
        if (version != BoardRamStateVersion)
            throw new InvalidDataException($"Unsupported HSHavoc board RAM state version: {version}.");

        int length = reader.ReadInt32();
        if (length != _boardRam.Length)
            throw new InvalidDataException($"Unexpected HSHavoc board RAM length: {length}.");

        byte[] data = reader.ReadBytes(length);
        if (data.Length != length)
            throw new EndOfStreamException("HSHavoc board RAM state truncated.");

        data.CopyTo(_boardRam, 0);
    }

    public byte[] GetBoardRamCopy()
    {
        return (byte[])_boardRam.Clone();
    }

    public bool TryRead8(uint address, out byte value)
    {
        if (_inner?.TryRead8(address, out value) == true)
            return true;

        if (TryReadBoardRam8(address, out value))
            return true;

        TraceIoRead(address, 1);
        TraceRamAccess("RAM-R", address, 1, 0);
        return NoByte(out value);
    }

    public bool TryRead16(uint address, out ushort value)
    {
        if (_inner?.TryRead16(address, out value) == true)
            return true;

        if (TryReadVBlankGate(address, out value))
            return true;

        if (TryReadBoardRam16(address, out value))
            return true;

        TraceIoRead(address, 2);
        TraceRamAccess("RAM-R", address, 2, 0);
        return NoWord(out value);
    }

    public bool TryRead32(uint address, out uint value)
    {
        if (_inner?.TryRead32(address, out value) == true)
            return true;

        if (TryReadBoardRam32(address, out value))
            return true;

        TraceIoRead(address, 4);
        TraceRamAccess("RAM-R", address, 4, 0);
        return NoLong(out value);
    }

    public bool TryWrite8(uint address, byte value)
    {
        if (_inner?.TryWrite8(address, value) == true)
            return true;

        if (TryWriteBoardRam8(address, value))
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

        if (TryWriteBoardRam16(address, value))
            return true;

        if (TryRepairVdpRegisterPending(address, value))
            return true;

        if (!TouchesAck(address, 2))
        {
            LatchVdpQueueParameter(address, value, 2);
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

        if (TryWriteBoardRam32(address, value))
            return true;

        if (TryRepairVdpRegisterPending(address, value))
            return true;

        if (!TouchesAck(address, 4))
        {
            LatchVdpQueueParameter(address, value, 4);
            TraceVdpWrite(address, 4, value);
            TraceRamAccess("RAM-W", address, 4, value);
            return false;
        }

        ClearAckWord(value);
        return true;
    }

    private bool TryReadBoardRam8(uint address, out byte value)
    {
        uint masked = address & 0x00FFFFFF;
        if (!IsBoardRamAddress(masked))
        {
            value = 0;
            return false;
        }

        value = _boardRam[masked - BoardRamStartAddress];
        return true;
    }

    private bool TryReadBoardRam16(uint address, out ushort value)
    {
        if (!TryReadBoardRam8(address, out byte hi) ||
            !TryReadBoardRam8(address + 1, out byte lo))
        {
            value = 0;
            return false;
        }

        value = (ushort)((hi << 8) | lo);
        return true;
    }

    private bool TryReadBoardRam32(uint address, out uint value)
    {
        if (!TryReadBoardRam16(address, out ushort hi) ||
            !TryReadBoardRam16(address + 2, out ushort lo))
        {
            value = 0;
            return false;
        }

        value = ((uint)hi << 16) | lo;
        return true;
    }

    private bool TryWriteBoardRam8(uint address, byte value)
    {
        uint masked = address & 0x00FFFFFF;
        if (!IsBoardRamAddress(masked))
            return false;

        _boardRam[masked - BoardRamStartAddress] = value;
        return true;
    }

    private bool TryWriteBoardRam16(uint address, ushort value)
    {
        uint masked = address & 0x00FFFFFF;
        if (!IsBoardRamAddress(masked) || !IsBoardRamAddress((masked + 1) & 0x00FFFFFF))
            return false;

        _boardRam[masked - BoardRamStartAddress] = (byte)(value >> 8);
        _boardRam[masked + 1 - BoardRamStartAddress] = (byte)value;
        return true;
    }

    private bool TryWriteBoardRam32(uint address, uint value)
    {
        uint masked = address & 0x00FFFFFF;
        if (!IsBoardRamAddress(masked) || !IsBoardRamAddress((masked + 3) & 0x00FFFFFF))
            return false;

        _boardRam[masked - BoardRamStartAddress] = (byte)(value >> 24);
        _boardRam[masked + 1 - BoardRamStartAddress] = (byte)(value >> 16);
        _boardRam[masked + 2 - BoardRamStartAddress] = (byte)(value >> 8);
        _boardRam[masked + 3 - BoardRamStartAddress] = (byte)value;
        return true;
    }

    private static bool IsBoardRamAddress(uint maskedAddress)
        => maskedAddress >= BoardRamStartAddress && maskedAddress <= BoardRamEndAddress;

    private static bool TouchesAck(uint address, uint size)
    {
        uint start = address & 0x00FFFFFF;
        uint end = start + size - 1;
        return start <= AckWordAddress + 1 && end >= AckWordAddress;
    }

    private bool TryReadVBlankGate(uint address, out ushort value)
    {
        value = 0;
        if (!ForceVBlankGateRead || (address & 0x00FFFFFF) != AckWordAddress)
            return false;

        // The VBlank handler probes this PIC/board gate before calling the
        // shared VDP dispatcher. Other $fff906 polls remain backed by RAM so
        // startup/acknowledgement waits still exercise the board model.
        if (md_m68k.g_reg_PC != 0x000AC2)
            return false;

        value = 0x0001;
        if (TraceVBlankGateRead && _vblankGateReadLogRemaining > 0)
        {
            _vblankGateReadLogRemaining--;
            System.Console.WriteLine(
                $"[HSHAVOC-VBLANK-GATE-R] pc=0x{md_m68k.g_reg_PC:X6} frame={FrameCounter()} addr=0x{AckWordAddress:X6} value=0x{value:X4}");
        }

        return true;
    }

    private void TraceVdpWrite(uint address, int size, uint value)
    {
        if (!TraceVdp || _vdpLogRemaining <= 0)
            return;

        uint masked = address & 0x00FFFFFF;
        if (masked < VdpStartAddress || masked > VdpEndAddress)
            return;
        long frame = FrameCounter();
        if (frame < TraceVdpFrameStart || frame > TraceVdpFrameEnd)
            return;

        _vdpLogRemaining--;
        System.Console.WriteLine(
            $"[HSHAVOC-VDP] pc=0x{md_m68k.g_reg_PC:X6} frame={frame} size={size} addr=0x{masked:X6} value=0x{value:X8}");
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
        if (TraceRamSkipZero && value == 0)
            return;

        uint masked = address & 0x00FFFFFF;
        uint end = masked + (uint)size - 1;
        if (masked > TraceRamEnd || end < TraceRamStart)
            return;
        long frame = FrameCounter();
        if (frame < TraceRamFrameStart || frame > TraceRamFrameEnd)
            return;

        _ramLogRemaining--;
        string regs = TraceRamRegs
            ? $" d2=0x{md_m68k.g_reg_data[2].l:X8} d3=0x{md_m68k.g_reg_data[3].l:X8} d4=0x{md_m68k.g_reg_data[4].l:X8} a0=0x{md_m68k.g_reg_addr[0].l:X8} a1=0x{md_m68k.g_reg_addr[1].l:X8}"
            : string.Empty;
        System.Console.WriteLine(
            $"[HSHAVOC-{tag}] pc=0x{md_m68k.g_reg_PC:X6} frame={frame} size={size} addr=0x{masked:X6} value=0x{value:X8}{regs}");
    }

    private void ClearAckWord(uint attemptedValue)
    {
        FlushLatchedVdpQueueOnAckIfRequested();
        FlushVdpCommandBlocksOnAckIfRequested();

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

    private void LatchVdpQueueParameter(uint address, uint value, int size)
    {
        uint masked = address & 0x00FFFFFF;

        if (size == 4 && masked == 0x00FFE802)
        {
            _latchedSlot0Source = value & 0x00FFFFFF;
            return;
        }

        if (size != 2)
            return;

        ushort word = (ushort)value;
        switch (masked)
        {
            case 0x00FFE806:
                _latchedSlot0Destination = word;
                break;
            case 0x00FFE808:
                _latchedSlot0Length = word;
                break;
            case 0x00FFE80A:
                if (word != 0)
                    _latchedSlot0Active = true;
                break;
        }
    }

    private void FlushLatchedVdpQueueOnAckIfRequested()
    {
        if (!FlushVdpCommandBlocksOnAck || !_latchedSlot0Active || !IsQueueAckWritePc(md_m68k.g_reg_PC))
            return;

        md_m68k.InitMemoryIfNeeded();
        byte[]? memory = md_m68k.g_memory;
        if (memory == null)
            return;

        int length = _latchedSlot0Length;
        if (length == 0 || length > 0x4000)
            return;

        uint byteLength = (uint)length * 2;
        bool romSource = _latchedSlot0Source < 0x00100000 && _latchedSlot0Source + byteLength <= 0x00100000;
        bool ramSource = _latchedSlot0Source >= 0x00FF0000 && _latchedSlot0Source + byteLength - 1 < memory.Length;
        if (!romSource && !ramSource)
            return;

        uint sourceWord = _latchedSlot0Source >> 1;
        ushort reg19 = (ushort)(0x9300 | (length & 0x00FF));
        ushort reg20 = (ushort)(0x9400 | ((length >> 8) & 0x00FF));
        ushort reg21 = (ushort)(0x9500 | (sourceWord & 0x00FF));
        ushort reg22 = (ushort)(0x9600 | ((sourceWord >> 8) & 0x00FF));
        ushort reg23 = (ushort)(0x9700 | ((sourceWord >> 16) & 0x007F));
        ushort control1 = (ushort)(0x4000 | (_latchedSlot0Destination & 0x3FFF));
        ushort control2 = (ushort)(0x0080 | ((_latchedSlot0Destination >> 14) & 0x0007));

        ulong signature =
            ((ulong)_latchedSlot0Source << 32) ^
            ((ulong)_latchedSlot0Destination << 16) ^
            _latchedSlot0Length;
        if (ramSource)
            signature ^= HashMemoryWords(memory, _latchedSlot0Source, length);
        if (!_flushedLatchedQueueEntries.Add(signature))
            return;

        ExecuteVdpCommandBlock(0x00FFE91A, reg19, reg20, reg21, reg22, reg23, control1, control2, "HSHAVOC-VDPQ-LATCH-FLUSH");
        _latchedSlot0Active = false;
    }

    private void FlushVdpCommandBlocksOnAckIfRequested()
    {
        if (!FlushVdpCommandBlocksOnAck || md_main.g_md_vdp == null || !IsQueueAckWritePc(md_m68k.g_reg_PC))
            return;

        md_m68k.InitMemoryIfNeeded();
        byte[]? memory = md_m68k.g_memory;
        if (memory == null)
            return;

        uint start = ClampRamScanStart(VdpCommandBlockStart);
        uint end = ClampRamScanEnd(VdpCommandBlockEnd);
        if (end < start)
            return;

        for (uint block = start; block <= end; block += 2)
        {
            if (block == LatchedVdpQueueBlock)
                continue;

            ushort reg19 = ReadMemoryWord(memory, block);
            ushort reg20 = ReadMemoryWord(memory, block + 2);
            ushort reg21 = ReadMemoryWord(memory, block + 4);
            ushort reg22 = ReadMemoryWord(memory, block + 6);
            ushort reg23 = ReadMemoryWord(memory, block + 8);
            ushort control1 = ReadMemoryWord(memory, block + 10);
            ushort control2 = ReadMemoryWord(memory, block + 12);

            if (!LooksLikeVdpCommandBlock(memory, reg19, reg20, reg21, reg22, reg23, control1, control2))
                continue;

            ulong signature = BuildVdpCommandBlockSignature(memory, block, reg19, reg20, reg21, reg22, reg23, control1, control2);
            if (!_flushedAckCommandBlocks.Add(signature))
                continue;

            ExecuteVdpCommandBlock(block, reg19, reg20, reg21, reg22, reg23, control1, control2, "HSHAVOC-VDPBLK-ACK-FLUSH");
        }
    }

    private void ExecuteVdpCommandBlock(
        uint block,
        ushort reg19,
        ushort reg20,
        ushort reg21,
        ushort reg22,
        ushort reg23,
        ushort control1,
        ushort control2,
        string traceTag)
    {
        md_vdp? vdp = md_main.g_md_vdp;
        if (vdp == null)
            return;

        foreach (ushort word in new[] { reg19, reg20, reg21, reg22, reg23 })
        {
            vdp.read16(0x00C00004);
            vdp.write16(0x00C00004, word);
        }

        vdp.read16(0x00C00004);
        vdp.write16(0x00C00004, control1);
        vdp.write16(0x00C00004, control2);

        if (TraceVdpCommandBlocks && _ackCommandBlockLogRemaining > 0)
        {
            _ackCommandBlockLogRemaining--;
            LogVdpCommandBlock(traceTag, block, reg19, reg20, reg21, reg22, reg23, control1, control2);
        }
    }

    private static bool IsQueueAckWritePc(uint pc)
        => pc is 0x002A0E or 0x019338 or 0x019386 or 0x0193D4 or 0x019436 or 0x01948C;

    private static bool LooksLikeVdpCommandBlock(
        byte[] memory,
        ushort reg19,
        ushort reg20,
        ushort reg21,
        ushort reg22,
        ushort reg23,
        ushort control1,
        ushort control2)
    {
        if ((reg19 & 0xFF00) != 0x9300 || (reg20 & 0xFF00) != 0x9400 ||
            (reg21 & 0xFF00) != 0x9500 || (reg22 & 0xFF00) != 0x9600 ||
            (reg23 & 0xFF00) != 0x9700)
            return false;

        if ((control1 & 0xC000) == 0x8000)
            return false;

        int codeLow = DecodeVdpCodeLow(control1, control2);
        if (codeLow != 0x01 && codeLow != 0x03 && codeLow != 0x05)
            return false;

        if ((control2 & 0x0080) == 0)
            return false;

        int length = (reg19 & 0x00FF) | ((reg20 & 0x00FF) << 8);
        if (length == 0 || length > 0x4000)
            return false;

        uint sourceByte = DecodeVdpDmaSourceByte(reg21, reg22, reg23);
        uint byteLength = (uint)length * 2;
        bool romSource = !SkipRomVdpDma && sourceByte < 0x00100000 && sourceByte + byteLength <= 0x00100000;
        bool ramSource = sourceByte >= 0x00FF0000 && sourceByte + byteLength - 1 < memory.Length;
        return romSource || ramSource;
    }

    private static ulong BuildVdpCommandBlockSignature(
        byte[] memory,
        uint block,
        ushort reg19,
        ushort reg20,
        ushort reg21,
        ushort reg22,
        ushort reg23,
        ushort control1,
        ushort control2)
    {
        ulong signature =
            ((ulong)block << 40) ^
            ((ulong)reg19 << 48) ^
            ((ulong)reg20 << 32) ^
            ((ulong)reg21 << 16) ^
            reg22 ^
            ((ulong)reg23 << 8) ^
            ((ulong)control1 << 24) ^
            ((ulong)control2 << 4);

        uint sourceByte = DecodeVdpDmaSourceByte(reg21, reg22, reg23);
        if (sourceByte < 0x00FF0000 || sourceByte >= memory.Length)
            return signature;

        int length = (reg19 & 0x00FF) | ((reg20 & 0x00FF) << 8);
        return signature ^ HashMemoryWords(memory, sourceByte, length);
    }

    private static ulong HashMemoryWords(byte[] memory, uint source, int words)
    {
        ulong hash = 1469598103934665603UL;
        for (int i = 0; i < words; i++)
        {
            uint address = source + (uint)(i * 2);
            ushort value = address + 1 < memory.Length ? ReadMemoryWord(memory, address) : (ushort)0;
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private static uint DecodeVdpDmaSourceByte(ushort reg21, ushort reg22, ushort reg23)
    {
        uint sourceWord = (uint)((reg21 & 0x00FF) | ((reg22 & 0x00FF) << 8) | ((reg23 & 0x007F) << 16));
        return sourceWord << 1;
    }

    private static int DecodeVdpCodeLow(ushort control1, ushort control2)
        => ((control1 >> 14) & 0x03) | ((control2 >> 2) & 0x0C);

    private static int DecodeVdpDestination(ushort control1, ushort control2)
        => (control1 & 0x3FFF) | ((control2 & 0x0007) << 14);

    private static void LogVdpCommandBlock(
        string tag,
        uint block,
        ushort reg19,
        ushort reg20,
        ushort reg21,
        ushort reg22,
        ushort reg23,
        ushort control1,
        ushort control2)
    {
        int codeLow = DecodeVdpCodeLow(control1, control2);
        int dest = DecodeVdpDestination(control1, control2);
        int length = (reg19 & 0x00FF) | ((reg20 & 0x00FF) << 8);
        int sourceWord = (reg21 & 0x00FF) | ((reg22 & 0x00FF) << 8) | ((reg23 & 0x007F) << 16);
        System.Console.WriteLine(
            $"[{tag}] pc=0x{md_m68k.g_reg_PC:X6} frame={FrameCounter()} " +
            $"block=0x{block:X6} len=0x{length:X4} sourceWord=0x{sourceWord:X6} " +
            $"sourceByte=0x{(sourceWord << 1):X6} dest=0x{dest:X4} code=0x{codeLow:X2} " +
            $"regs={reg19:X4},{reg20:X4},{reg21:X4},{reg22:X4},{reg23:X4} cmd={control1:X4},{control2:X4}");
    }

    private static ushort ReadMemoryWord(byte[] memory, uint address)
        => address + 1 < memory.Length
            ? (ushort)((memory[address] << 8) | memory[address + 1])
            : (ushort)0;

    private static uint ClampRamScanStart(uint value)
        => System.Math.Max(0x00FF0000, value & 0x00FFFFFE);

    private static uint ClampRamScanEnd(uint value)
        => System.Math.Min(0x00FFFFF2, value & 0x00FFFFFE);

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

    private static long ParseLong(string name, long fallback)
    {
        string? raw = System.Environment.GetEnvironmentVariable(name);
        return long.TryParse(raw, out long value) ? value : fallback;
    }

    private static bool IsUiProofMode()
    {
        string? raw = System.Environment.GetEnvironmentVariable("EUTHERDRIVE_HSHAVOC_UI_PROOF_MODE");
        if (string.Equals(raw, "0", System.StringComparison.Ordinal))
            return false;
        if (string.Equals(raw, "1", System.StringComparison.Ordinal))
            return true;

        return string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("EUTHERDRIVE_HEADLESS_CORE"));
    }

    private static bool IsEnvEnabled(string name)
        => string.Equals(System.Environment.GetEnvironmentVariable(name), "1", System.StringComparison.Ordinal);

    private static bool IsEnvDisabled(string name)
        => string.Equals(System.Environment.GetEnvironmentVariable(name), "0", System.StringComparison.Ordinal);

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
