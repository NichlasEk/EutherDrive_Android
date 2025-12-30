using System;
using System.Diagnostics;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core;

/// <summary>
/// Minimal Mega Drive address bus:
/// - ROM:  0x000000..0x3FFFFF (mirrored)
/// - WRAM: 0xFF0000..0xFFFFFF (64KB)
/// Steg B: läs/skriv så CPU kan leva senare.
/// </summary>
public sealed class MegaDriveBus
{
    private readonly byte[] _rom;

    // 68k Work RAM (64KB)
    private readonly byte[] _wram = new byte[64 * 1024];
    private bool _z80BusRequested;
    private bool _z80Reset;
    private readonly bool[] _ioReadLogged = new bool[0x20];
    private readonly bool[] _ioWriteLogged = new bool[0x20];
    private int _z80RegReadLogRemaining = 8;
    private int _z80RegWriteLogRemaining = 8;
    private int _z80WindowReadLogRemaining = 8;
    private int _z80WindowWriteLogRemaining = 8;
    private int _vdpWriteLogRemaining = 10;
    private int _vdpWriteTotal;
    private int _vdpWriteRouted;
    private int _vdpWriteElsewhere;
    private int _z80WinLogRemaining = 64;
    private bool _z80WinWarned;
    private readonly long[] _vdpPortWriteCounts = new long[8 * 3];
    private long _vdpLastSummaryTicks;
    private bool _vdpNormalizeLogged;
    private bool _vdpNotRoutedDataLogged;
    private bool _vdpNotRoutedCtrlLogged;
    private static readonly bool TraceBusAccess =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_BUS"), "1", StringComparison.Ordinal);
    private static readonly bool TraceZ80Win =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_Z80WIN"), "1", StringComparison.Ordinal);
    private static bool TraceZ80Sig => MdTracerCore.MdLog.TraceZ80Sig;
    private static bool MapZ80OddReadToNext => ReadEnvDefaultOn("EUTHERDRIVE_Z80_ODD_READ_TO_NEXT");

    public MegaDriveBus(byte[] rom)
    {
        _rom = rom ?? throw new ArgumentNullException(nameof(rom));
        if (_rom.Length == 0) throw new ArgumentException("ROM is empty.", nameof(rom));
    }

    public void Reset()
    {
        Array.Clear(_wram, 0, _wram.Length);
        _z80BusRequested = false;
        _z80Reset = false;
        _z80RegReadLogRemaining = 8;
        _z80RegWriteLogRemaining = 8;
        _z80WindowReadLogRemaining = 8;
        _z80WindowWriteLogRemaining = 8;
        _vdpWriteLogRemaining = 10;
        _vdpWriteTotal = 0;
        _vdpWriteRouted = 0;
        _vdpWriteElsewhere = 0;
        _z80WinLogRemaining = 64;
        _z80WinWarned = false;
        Array.Clear(_vdpPortWriteCounts, 0, _vdpPortWriteCounts.Length);
        _vdpLastSummaryTicks = 0;
        _vdpNormalizeLogged = false;
        _vdpNotRoutedDataLogged = false;
        _vdpNotRoutedCtrlLogged = false;
    }

    private static bool IsVdpPort(uint addr) => (addr & 0xFFFFE0) == 0xC00000;
    private static bool IsIoPort(uint addr) => (addr & 0xFFFFE0) == 0xA10000;
    private static bool IsZ80Window(uint addr) => (addr & 0xFFFF00) == 0xA00000;
    private static bool IsZ80BusReq(uint addr) => (addr & 0xFFFFFE) == 0xA11100;
    private static bool IsZ80Reset(uint addr) => (addr & 0xFFFFFE) == 0xA11200;
    private static bool IsZ80Mailbox(uint addr) => addr >= 0xA01B80 && addr <= 0xA01B8F;
    private static bool ReadEnvDefaultOn(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(raw))
            return true;
        return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    // 68k ROM space: 0x000000..0x3FFFFF
    // 68k WRAM:      0xFF0000..0xFFFFFF
    public byte Read8(uint addr)
    {
        if (MegaDriveBusProfiler.Enabled)
        {
            long start = Stopwatch.GetTimestamp();
            byte result = Read8Core(addr);
            MegaDriveBusProfiler.AddReadTicks(Stopwatch.GetTimestamp() - start);
            md_m68k.RecordBusAccess(addr, 1, false, result);
            return result;
        }

        byte value = Read8Core(addr);
        md_m68k.RecordBusAccess(addr, 1, false, value);
        return value;
    }

    private byte Read8Core(uint addr)
    {
        if (IsZ80BusReq(addr))
        {
            byte val = _z80BusRequested ? (byte)0x00 : (byte)0x01;
            LogZ80RegRead(addr, val);
            LogBusAccess("busreq read8", addr, val);
            return val;
        }

        if (IsZ80Reset(addr))
        {
            byte val = _z80Reset ? (byte)0x00 : (byte)0x01;
            LogZ80RegRead(addr, val);
            LogBusAccess("reset read8", addr, val);
            return val;
        }

        if (IsVdpPort(addr) && md_main.g_md_vdp != null)
            return md_main.g_md_vdp.read8(addr);

        if (IsIoPort(addr) && md_main.g_md_io != null)
        {
            byte val = md_main.g_md_io.read8(addr);
            LogIoRead(addr, val);
            return val;
        }

        if (IsZ80Window(addr) && md_main.g_md_z80 != null)
        {
            uint z80Addr = addr & 0xFFFF;
            if (MapZ80OddReadToNext && (z80Addr & 1) != 0)
                z80Addr = (z80Addr + 1) & 0xFFFF;
            byte val = md_main.g_md_z80.read8(z80Addr);
            LogZ80WindowRead(addr, val);
            return val;
        }

        // ROM
        if (addr < 0x400000)
        {
            uint i = addr % (uint)_rom.Length;
            return _rom[i];
        }

        // Work RAM (mirror inside 64KB)
        if ((addr & 0xFF0000) == 0xFF0000)
        {
            int i = (int)(addr & 0xFFFF);
            return _wram[i];
        }

        return 0xFF; // open bus
    }

    public ushort Read16(uint addr)
    {
        if (IsZ80BusReq(addr))
        {
            ushort val = _z80BusRequested ? (ushort)0x0000 : (ushort)0x0101;
            LogZ80RegRead(addr, val);
            LogBusAccess("busreq read16", addr, val);
            md_m68k.RecordBusAccess(addr, 2, false, val);
            return val;
        }

        if (IsZ80Reset(addr))
        {
            ushort val = _z80Reset ? (ushort)0x0000 : (ushort)0x0001;
            LogZ80RegRead(addr, val);
            LogBusAccess("reset read16", addr, val);
            md_m68k.RecordBusAccess(addr, 2, false, val);
            return val;
        }

        if (IsVdpPort(addr) && md_main.g_md_vdp != null)
        {
            if (MegaDriveBusProfiler.Enabled)
            {
                long start = Stopwatch.GetTimestamp();
                ushort val = md_main.g_md_vdp.read16(addr);
                MegaDriveBusProfiler.AddReadTicks(Stopwatch.GetTimestamp() - start);
                md_m68k.RecordBusAccess(addr, 2, false, val);
                return val;
            }
            ushort vdpVal = md_main.g_md_vdp.read16(addr);
            md_m68k.RecordBusAccess(addr, 2, false, vdpVal);
            return vdpVal;
        }

        if (IsIoPort(addr) && md_main.g_md_io != null)
        {
            ushort ioVal;
            if (MegaDriveBusProfiler.Enabled)
            {
                long start = Stopwatch.GetTimestamp();
                ioVal = md_main.g_md_io.read16(addr);
                MegaDriveBusProfiler.AddReadTicks(Stopwatch.GetTimestamp() - start);
                LogIoRead(addr, ioVal);
                md_m68k.RecordBusAccess(addr, 2, false, ioVal);
                return ioVal;
            }
            ioVal = md_main.g_md_io.read16(addr);
            LogIoRead(addr, ioVal);
            md_m68k.RecordBusAccess(addr, 2, false, ioVal);
            return ioVal;
        }

        if (IsZ80Window(addr) && md_main.g_md_z80 != null)
        {
            if (IsZ80Mailbox(addr) || IsZ80Mailbox(addr + 1))
            {
                byte mbxHi = md_main.g_md_z80.read8(addr & 0xFFFF);
                byte mbxLo = md_main.g_md_z80.read8((addr + 1) & 0xFFFF);
                ushort mbxVal = (ushort)((mbxHi << 8) | mbxLo);
                LogZ80WindowRead(addr, mbxVal);
                md_m68k.RecordBusAccess(addr, 2, false, mbxVal);
                return mbxVal;
            }
            ushort windowVal;
            if (MegaDriveBusProfiler.Enabled)
            {
                long start = Stopwatch.GetTimestamp();
                windowVal = md_main.g_md_z80.read16(addr & 0xFFFF);
                MegaDriveBusProfiler.AddReadTicks(Stopwatch.GetTimestamp() - start);
                LogZ80WindowRead(addr, windowVal);
                md_m68k.RecordBusAccess(addr, 2, false, windowVal);
                return windowVal;
            }
            windowVal = md_main.g_md_z80.read16(addr & 0xFFFF);
            LogZ80WindowRead(addr, windowVal);
            md_m68k.RecordBusAccess(addr, 2, false, windowVal);
            return windowVal;
        }

        int hi = Read8Core(addr);
        int lo = Read8Core(addr + 1);
        ushort value = (ushort)((hi << 8) | lo);
        md_m68k.RecordBusAccess(addr, 2, false, value);
        return value;
    }

    public uint Read32(uint addr)
    {
        if (IsZ80BusReq(addr))
        {
            uint val = _z80BusRequested ? 0x0000_0000u : 0x0101_0101u;
            LogZ80RegRead(addr, val);
            LogBusAccess("busreq read32", addr, val);
            md_m68k.RecordBusAccess(addr, 4, false, val);
            return val;
        }

        if (IsZ80Reset(addr))
        {
            uint val = _z80Reset ? 0x0000_0000u : 0x0000_0001u;
            LogZ80RegRead(addr, val);
            LogBusAccess("reset read32", addr, val);
            md_m68k.RecordBusAccess(addr, 4, false, val);
            return val;
        }

        if (IsVdpPort(addr) && md_main.g_md_vdp != null)
        {
            if (MegaDriveBusProfiler.Enabled)
            {
                long start = Stopwatch.GetTimestamp();
                uint val = md_main.g_md_vdp.read32(addr);
                MegaDriveBusProfiler.AddReadTicks(Stopwatch.GetTimestamp() - start);
                md_m68k.RecordBusAccess(addr, 4, false, val);
                return val;
            }
            uint vdpVal = md_main.g_md_vdp.read32(addr);
            md_m68k.RecordBusAccess(addr, 4, false, vdpVal);
            return vdpVal;
        }

        if (IsIoPort(addr) && md_main.g_md_io != null)
        {
            uint ioVal;
            if (MegaDriveBusProfiler.Enabled)
            {
                long start = Stopwatch.GetTimestamp();
                ioVal = md_main.g_md_io.read32(addr);
                MegaDriveBusProfiler.AddReadTicks(Stopwatch.GetTimestamp() - start);
                LogIoRead(addr, ioVal);
                md_m68k.RecordBusAccess(addr, 4, false, ioVal);
                return ioVal;
            }
            ioVal = md_main.g_md_io.read32(addr);
            LogIoRead(addr, ioVal);
            md_m68k.RecordBusAccess(addr, 4, false, ioVal);
            return ioVal;
        }

        if (IsZ80Window(addr) && md_main.g_md_z80 != null)
        {
            uint windowVal;
            if (MegaDriveBusProfiler.Enabled)
            {
                long start = Stopwatch.GetTimestamp();
                windowVal = md_main.g_md_z80.read32(addr & 0xFFFF);
                MegaDriveBusProfiler.AddReadTicks(Stopwatch.GetTimestamp() - start);
                LogZ80WindowRead(addr, windowVal);
                md_m68k.RecordBusAccess(addr, 4, false, windowVal);
                return windowVal;
            }
            windowVal = md_main.g_md_z80.read32(addr & 0xFFFF);
            LogZ80WindowRead(addr, windowVal);
            md_m68k.RecordBusAccess(addr, 4, false, windowVal);
            return windowVal;
        }

        uint b0 = Read8Core(addr);
        uint b1 = Read8Core(addr + 1);
        uint b2 = Read8Core(addr + 2);
        uint b3 = Read8Core(addr + 3);
        uint value = (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
        md_m68k.RecordBusAccess(addr, 4, false, value);
        return value;
    }

    public void Write8(uint addr, byte value)
    {
        if (MegaDriveBusProfiler.Enabled)
        {
            long start = Stopwatch.GetTimestamp();
            Write8Core(addr, value);
            MegaDriveBusProfiler.AddWriteTicks(Stopwatch.GetTimestamp() - start);
            return;
        }

        Write8Core(addr, value);
    }

    private void Write8Core(uint addr, byte value)
    {
        if (IsZ80BusReq(addr))
        {
            bool prev = _z80BusRequested;
            _z80BusRequested = (value & 0x01) != 0;
            if (md_main.g_md_z80 != null)
                md_main.g_md_z80.g_active = !_z80BusRequested && !_z80Reset;
            LogZ80RegWrite(addr, value);
            LogBusAccess("busreq write8", addr, value);
            if (TraceZ80Win)
                Console.WriteLine($"[Z80BUSREQ] write addr=0x{addr:X6} value=0x{value:X2}");
            if (prev != _z80BusRequested && TraceZ80Sig)
                Console.WriteLine($"[Z80SIG] BUSREQ={(_z80BusRequested ? 1 : 0)} (stopOn={(_z80BusRequested ? 1 : 0)})");
            md_m68k.RecordBusAccess(addr, 1, true, value);
            return;
        }

        if (IsZ80Reset(addr))
        {
            bool next = (value & 0x01) == 0;
            bool prev = _z80Reset;
            _z80Reset = next;
            if (md_main.g_md_z80 != null)
            {
                if (next && !prev)
                {
                    md_main.BeginZ80ResetCycle();
                    md_main.g_md_z80.reset();
                }
                md_main.g_md_z80.g_active = !next && !_z80BusRequested;
            }
            LogZ80RegWrite(addr, value);
            LogBusAccess("reset write8", addr, value);
            if (TraceZ80Win)
                Console.WriteLine($"[Z80RESET]  write addr=0x{addr:X6} value=0x{value:X2}");
            if (prev != next && TraceZ80Sig)
                Console.WriteLine($"[Z80SIG] RESET={(next ? 1 : 0)} ({(next ? "assert" : "deassert")})");
            md_m68k.RecordBusAccess(addr, 1, true, value);
            return;
        }

        if (IsVdpPort(addr) && md_main.g_md_vdp != null)
        {
            LogVdpWrite(addr, addr, 8, value, "VDP");
            md_main.g_md_vdp.write8(addr, value);
            md_m68k.RecordBusAccess(addr, 1, true, value);
            return;
        }

        if (IsIoPort(addr) && md_main.g_md_io != null)
        {
            LogVdpWrite(addr, addr, 8, value, "IO");
            md_main.g_md_io.write8(addr, value);
            LogIoWrite(addr, value);
            md_m68k.RecordBusAccess(addr, 1, true, value);
            return;
        }

        if (IsZ80Window(addr) && md_main.g_md_z80 != null)
        {
            LogVdpWrite(addr, addr, 8, value, "Z80");
            uint z80Index = addr & 0x1FFF;
            md_main.g_md_z80.write8(addr & 0xFFFF, value);
            LogZ80WindowWrite(addr, value);
            if (TraceZ80Win && _z80WinLogRemaining > 0)
            {
                _z80WinLogRemaining--;
                int busReq = _z80BusRequested ? 1 : 0;
                int reset = _z80Reset ? 1 : 0;
                Console.WriteLine($"[Z80WIN] W8 addr=0x{addr:X6} -> z80[{z80Index:X4}]=0x{value:X2} busReq={busReq} reset={reset}");
            }
            if (TraceZ80Win && !_z80BusRequested && !_z80WinWarned)
            {
                _z80WinWarned = true;
                int busReq = _z80BusRequested ? 1 : 0;
                int reset = _z80Reset ? 1 : 0;
                Console.WriteLine($"[Z80WIN][WARN] Write while bus not granted! addr=0x{addr:X6} val=0x{value:X2} busReq={busReq} reset={reset}");
            }
            md_m68k.RecordBusAccess(addr, 1, true, value);
            return;
        }

        LogVdpWrite(addr, addr, 8, value, "WRAM/other");
        // Work RAM only (for now)
        if ((addr & 0xFF0000) == 0xFF0000)
        {
            int i = (int)(addr & 0xFFFF);
            _wram[i] = value;
            md_m68k.RecordBusAccess(addr, 1, true, value);
            return;
        }

        // ignore writes elsewhere in Steg B
        md_m68k.RecordBusAccess(addr, 1, true, value);
    }

    public void Write16(uint addr, ushort value)
    {
        if (IsZ80BusReq(addr))
        {
            bool prev = _z80BusRequested;
            _z80BusRequested = (value & 0x0101) != 0;
            if (md_main.g_md_z80 != null)
                md_main.g_md_z80.g_active = !_z80BusRequested && !_z80Reset;
            LogZ80RegWrite(addr, value);
            LogBusAccess("busreq write16", addr, value);
            if (TraceZ80Win)
                Console.WriteLine($"[Z80BUSREQ] write addr=0x{addr:X6} value=0x{value:X4}");
            if (prev != _z80BusRequested && TraceZ80Sig)
                Console.WriteLine($"[Z80SIG] BUSREQ={(_z80BusRequested ? 1 : 0)} (stopOn={(_z80BusRequested ? 1 : 0)})");
            md_m68k.RecordBusAccess(addr, 2, true, value);
            return;
        }

        if (IsZ80Reset(addr))
        {
            bool next = (value & 0x0101) == 0;
            bool prev = _z80Reset;
            _z80Reset = next;
            if (md_main.g_md_z80 != null)
            {
                if (next && !prev)
                {
                    md_main.BeginZ80ResetCycle();
                    md_main.g_md_z80.reset();
                }
                md_main.g_md_z80.g_active = !next && !_z80BusRequested;
            }
            LogZ80RegWrite(addr, value);
            LogBusAccess("reset write16", addr, value);
            if (TraceZ80Win)
                Console.WriteLine($"[Z80RESET]  write addr=0x{addr:X6} value=0x{value:X4}");
            if (prev != next && TraceZ80Sig)
                Console.WriteLine($"[Z80SIG] RESET={(next ? 1 : 0)} ({(next ? "assert" : "deassert")})");
            md_m68k.RecordBusAccess(addr, 2, true, value);
            return;
        }

        if (IsVdpPort(addr) && md_main.g_md_vdp != null)
        {
            LogVdpWrite(addr, addr, 16, value, "VDP");
            md_main.g_md_vdp.write16(addr, value);
            md_m68k.RecordBusAccess(addr, 2, true, value);
            return;
        }

        if (IsIoPort(addr) && md_main.g_md_io != null)
        {
            LogVdpWrite(addr, addr, 16, value, "IO");
            md_main.g_md_io.write16(addr, value);
            LogIoWrite(addr, value);
            md_m68k.RecordBusAccess(addr, 2, true, value);
            return;
        }

        if (IsZ80Window(addr) && md_main.g_md_z80 != null)
        {
            if (IsZ80Mailbox(addr) || IsZ80Mailbox(addr + 1))
            {
                byte mbxHi = (byte)((value >> 8) & 0xFF);
                byte mbxLo = (byte)(value & 0xFF);
                md_main.g_md_z80.write8(addr & 0xFFFF, mbxHi);
                md_main.g_md_z80.write8((addr + 1) & 0xFFFF, mbxLo);
                LogZ80WindowWrite(addr, value);
                md_m68k.RecordBusAccess(addr, 2, true, value);
                return;
            }
            LogVdpWrite(addr, addr, 16, value, "Z80");
            md_main.g_md_z80.write16(addr & 0xFFFF, value);
            LogZ80WindowWrite(addr, value);
            md_m68k.RecordBusAccess(addr, 2, true, value);
            return;
        }

        LogVdpWrite(addr, addr, 16, value, "WRAM/other");
        // 68k big-endian writes
        Write8(addr, (byte)(value >> 8));
        Write8(addr + 1, (byte)(value & 0xFF));
        md_m68k.RecordBusAccess(addr, 2, true, value);
    }

    public void Write32(uint addr, uint value)
    {
        if (IsZ80BusReq(addr))
        {
            bool prev = _z80BusRequested;
            _z80BusRequested = (value & 0x0101_0101u) != 0;
            if (md_main.g_md_z80 != null)
                md_main.g_md_z80.g_active = !_z80BusRequested && !_z80Reset;
            LogZ80RegWrite(addr, value);
            LogBusAccess("busreq write32", addr, value);
            if (TraceZ80Win)
                Console.WriteLine($"[Z80BUSREQ] write addr=0x{addr:X6} value=0x{value:X8}");
            if (prev != _z80BusRequested && TraceZ80Sig)
                Console.WriteLine($"[Z80SIG] BUSREQ={(_z80BusRequested ? 1 : 0)} (stopOn={(_z80BusRequested ? 1 : 0)})");
            md_m68k.RecordBusAccess(addr, 4, true, value);
            return;
        }

        if (IsZ80Reset(addr))
        {
            bool next = (value & 0x0101_0101u) == 0;
            bool prev = _z80Reset;
            _z80Reset = next;
            if (md_main.g_md_z80 != null)
            {
                if (next && !prev)
                {
                    md_main.BeginZ80ResetCycle();
                    md_main.g_md_z80.reset();
                }
                md_main.g_md_z80.g_active = !next && !_z80BusRequested;
            }
            LogZ80RegWrite(addr, value);
            LogBusAccess("reset write32", addr, value);
            if (TraceZ80Win)
                Console.WriteLine($"[Z80RESET]  write addr=0x{addr:X6} value=0x{value:X8}");
            if (prev != next && TraceZ80Sig)
                Console.WriteLine($"[Z80SIG] RESET={(next ? 1 : 0)} ({(next ? "assert" : "deassert")})");
            md_m68k.RecordBusAccess(addr, 4, true, value);
            return;
        }

        if (IsVdpPort(addr) && md_main.g_md_vdp != null)
        {
            LogVdpWrite(addr, addr, 32, value, "VDP");
            md_main.g_md_vdp.write32(addr, value);
            md_m68k.RecordBusAccess(addr, 4, true, value);
            return;
        }

        if (IsIoPort(addr) && md_main.g_md_io != null)
        {
            LogVdpWrite(addr, addr, 32, value, "IO");
            md_main.g_md_io.write32(addr, value);
            LogIoWrite(addr, value);
            md_m68k.RecordBusAccess(addr, 4, true, value);
            return;
        }

        if (IsZ80Window(addr) && md_main.g_md_z80 != null)
        {
            if (IsZ80Mailbox(addr) || IsZ80Mailbox(addr + 1) ||
                IsZ80Mailbox(addr + 2) || IsZ80Mailbox(addr + 3))
            {
                byte mbxB3 = (byte)((value >> 24) & 0xFF);
                byte mbxB2 = (byte)((value >> 16) & 0xFF);
                byte mbxB1 = (byte)((value >> 8) & 0xFF);
                byte mbxB0 = (byte)(value & 0xFF);
                md_main.g_md_z80.write8(addr & 0xFFFF, mbxB3);
                md_main.g_md_z80.write8((addr + 1) & 0xFFFF, mbxB2);
                md_main.g_md_z80.write8((addr + 2) & 0xFFFF, mbxB1);
                md_main.g_md_z80.write8((addr + 3) & 0xFFFF, mbxB0);
                LogZ80WindowWrite(addr, value);
                md_m68k.RecordBusAccess(addr, 4, true, value);
                return;
            }
            LogVdpWrite(addr, addr, 32, value, "Z80");
            md_main.g_md_z80.write32(addr & 0xFFFF, value);
            LogZ80WindowWrite(addr, value);
            md_m68k.RecordBusAccess(addr, 4, true, value);
            return;
        }

        LogVdpWrite(addr, addr, 32, value, "WRAM/other");
        Write8(addr,     (byte)(value >> 24));
        Write8(addr + 1, (byte)((value >> 16) & 0xFF));
        Write8(addr + 2, (byte)((value >> 8)  & 0xFF));
        Write8(addr + 3, (byte)(value & 0xFF));
        md_m68k.RecordBusAccess(addr, 4, true, value);
    }

    public ReadOnlySpan<byte> GetRomSpan() => _rom;
    public ReadOnlySpan<byte> GetWramSpan() => _wram;

    private void LogIoRead(uint addr, uint val)
    {
        int off = (int)(addr & 0x1F);
        if ((uint)off >= (uint)_ioReadLogged.Length)
            return;
        if (_ioReadLogged[off])
            return;
        _ioReadLogged[off] = true;
        MdTracerCore.MdLog.WriteLine($"[MegaDriveBus] IO read 0x{addr:X6} -> 0x{val:X}");
    }

    private void LogIoWrite(uint addr, uint val)
    {
        int off = (int)(addr & 0x1F);
        if ((uint)off >= (uint)_ioWriteLogged.Length)
            return;
        if (_ioWriteLogged[off])
            return;
        _ioWriteLogged[off] = true;
        MdTracerCore.MdLog.WriteLine($"[MegaDriveBus] IO write 0x{addr:X6} <- 0x{val:X}");
    }

    private void LogZ80RegRead(uint addr, uint val)
    {
        if (_z80RegReadLogRemaining <= 0)
            return;
        _z80RegReadLogRemaining--;
        MdTracerCore.MdLog.WriteLine($"[MegaDriveBus] Z80 reg read 0x{addr:X6} -> 0x{val:X}");
    }

    private void LogZ80RegWrite(uint addr, uint val)
    {
        if (_z80RegWriteLogRemaining <= 0)
            return;
        _z80RegWriteLogRemaining--;
        MdTracerCore.MdLog.WriteLine($"[MegaDriveBus] Z80 reg write 0x{addr:X6} <- 0x{val:X}");
    }

    private void LogZ80WindowRead(uint addr, uint val)
    {
        if (_z80WindowReadLogRemaining <= 0)
            return;
        _z80WindowReadLogRemaining--;
        MdTracerCore.MdLog.WriteLine($"[MegaDriveBus] Z80 win read 0x{addr:X6} -> 0x{val:X}");
    }

    private void LogZ80WindowWrite(uint addr, uint val)
    {
        if (_z80WindowWriteLogRemaining <= 0)
            return;
        _z80WindowWriteLogRemaining--;
        MdTracerCore.MdLog.WriteLine($"[MegaDriveBus] Z80 win write 0x{addr:X6} <- 0x{val:X}");
    }

    private void LogBusAccess(string label, uint addr, uint val)
    {
        if (!TraceBusAccess)
            return;
        string flags = $"grant={(_z80BusRequested ? 1 : 0)} reset={(_z80Reset ? 1 : 0)}";
        Console.WriteLine($"[BUS] {label} 0x{addr:X6} val=0x{val:X8} {flags}");
    }

    private void LogVdpWrite(uint addr, uint normalized, int width, uint value, string routed)
    {
        if (!IsVdpPort(addr))
        {
            MaybeLogVdpSummary();
            return;
        }

        _vdpWriteTotal++;
        if (routed == "VDP")
            _vdpWriteRouted++;
        else
            _vdpWriteElsewhere++;

        if (!_vdpNormalizeLogged && addr != normalized)
        {
            _vdpNormalizeLogged = true;
            MdTracerCore.MdLog.WriteLine($"[MegaDriveBus] VDP write addr normalized 0x{addr:X6} -> 0x{normalized:X6}");
        }

        if (_vdpWriteLogRemaining > 0)
        {
            _vdpWriteLogRemaining--;
            MdTracerCore.MdLog.WriteLine($"[MegaDriveBus] VDP write w{width} addr=0x{addr:X6} norm=0x{normalized:X6} val=0x{value:X} routed={routed}");
        }

        if (routed != "VDP")
        {
            bool isData = (addr & 0x1F) == 0x00;
            bool isCtrl = (addr & 0x1F) == 0x04;
            if (isData && !_vdpNotRoutedDataLogged)
            {
                _vdpNotRoutedDataLogged = true;
                MdTracerCore.MdLog.WriteLine($"[MegaDriveBus] VDP data write NOT routed to VDP addr=0x{addr:X6} val=0x{value:X} routed={routed}");
            }
            else if (isCtrl && !_vdpNotRoutedCtrlLogged)
            {
                _vdpNotRoutedCtrlLogged = true;
                MdTracerCore.MdLog.WriteLine($"[MegaDriveBus] VDP ctrl write NOT routed to VDP addr=0x{addr:X6} val=0x{value:X} routed={routed}");
            }
        }

        int sizeIndex = width == 8 ? 0 : width == 16 ? 1 : 2;
        int portIndex = (int)(addr & 0x7);
        if ((uint)portIndex < 8u)
            _vdpPortWriteCounts[(portIndex * 3) + sizeIndex]++;

        MaybeLogVdpSummary();
    }

    private void MaybeLogVdpSummary()
    {
        if (!MdTracerCore.MdLog.Enabled)
            return;

        long now = Stopwatch.GetTimestamp();
        if (now - _vdpLastSummaryTicks < Stopwatch.Frequency)
            return;
        _vdpLastSummaryTicks = now;

        var sb = new System.Text.StringBuilder();
        sb.Append($"[MegaDriveBus] VDP writes summary total={_vdpWriteTotal} routedVDP={_vdpWriteRouted} routedElsewhere={_vdpWriteElsewhere}");
        for (int port = 0; port < 8; port++)
        {
            int baseIdx = port * 3;
            long c8 = _vdpPortWriteCounts[baseIdx];
            long c16 = _vdpPortWriteCounts[baseIdx + 1];
            long c32 = _vdpPortWriteCounts[baseIdx + 2];
            if (c8 == 0 && c16 == 0 && c32 == 0)
                continue;
            sb.Append($" p{port:X1}[8={c8} 16={c16} 32={c32}]");
        }
        MdTracerCore.MdLog.WriteLine(sb.ToString());
        _vdpWriteTotal = 0;
        _vdpWriteRouted = 0;
        _vdpWriteElsewhere = 0;
        Array.Clear(_vdpPortWriteCounts, 0, _vdpPortWriteCounts.Length);
    }
}

internal static class MegaDriveBusProfiler
{
    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_PERF_STATS") == "1";

    internal static long ReadTicks;
    internal static long WriteTicks;
    internal static int ReadCount;
    internal static int WriteCount;

    internal static void ResetFrame()
    {
        if (!Enabled)
            return;
        ReadTicks = 0;
        WriteTicks = 0;
        ReadCount = 0;
        WriteCount = 0;
    }

    internal static void AddReadTicks(long ticks)
    {
        ReadTicks += ticks;
        ReadCount++;
    }

    internal static void AddWriteTicks(long ticks)
    {
        WriteTicks += ticks;
        WriteCount++;
    }
}
