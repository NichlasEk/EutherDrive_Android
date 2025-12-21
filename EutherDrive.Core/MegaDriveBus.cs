using System;
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
    }

    private static bool IsVdpPort(uint addr) => (addr & 0xFFFFE0) == 0xC00000;
    private static bool IsIoPort(uint addr) => (addr & 0xFFFFE0) == 0xA10000;
    private static bool IsZ80Window(uint addr) => (addr & 0xFFFF00) == 0xA00000;
    private static bool IsZ80BusReq(uint addr) => (addr & 0xFFFFFE) == 0xA11100;
    private static bool IsZ80Reset(uint addr) => (addr & 0xFFFFFE) == 0xA11200;

    // 68k ROM space: 0x000000..0x3FFFFF
    // 68k WRAM:      0xFF0000..0xFFFFFF
    public byte Read8(uint addr)
    {
        if (IsZ80BusReq(addr))
        {
            byte val = _z80BusRequested ? (byte)0x01 : (byte)0x00;
            LogZ80RegRead(addr, val);
            return val;
        }

        if (IsZ80Reset(addr))
        {
            byte val = _z80Reset ? (byte)0x00 : (byte)0x01;
            LogZ80RegRead(addr, val);
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
            byte val = md_main.g_md_z80.read8(addr & 0xFFFF);
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
            ushort val = _z80BusRequested ? (ushort)0x0001 : (ushort)0x0000;
            LogZ80RegRead(addr, val);
            return val;
        }

        if (IsZ80Reset(addr))
        {
            ushort val = _z80Reset ? (ushort)0x0000 : (ushort)0x0001;
            LogZ80RegRead(addr, val);
            return val;
        }

        if (IsVdpPort(addr) && md_main.g_md_vdp != null)
            return md_main.g_md_vdp.read16(addr);

        if (IsIoPort(addr) && md_main.g_md_io != null)
        {
            ushort val = md_main.g_md_io.read16(addr);
            LogIoRead(addr, val);
            return val;
        }

        if (IsZ80Window(addr) && md_main.g_md_z80 != null)
        {
            ushort val = md_main.g_md_z80.read16(addr & 0xFFFF);
            LogZ80WindowRead(addr, val);
            return val;
        }

        int hi = Read8(addr);
        int lo = Read8(addr + 1);
        return (ushort)((hi << 8) | lo);
    }

    public uint Read32(uint addr)
    {
        if (IsZ80BusReq(addr))
        {
            uint val = _z80BusRequested ? 0x0000_0001u : 0x0000_0000u;
            LogZ80RegRead(addr, val);
            return val;
        }

        if (IsZ80Reset(addr))
        {
            uint val = _z80Reset ? 0x0000_0000u : 0x0000_0001u;
            LogZ80RegRead(addr, val);
            return val;
        }

        if (IsVdpPort(addr) && md_main.g_md_vdp != null)
            return md_main.g_md_vdp.read32(addr);

        if (IsIoPort(addr) && md_main.g_md_io != null)
        {
            uint val = md_main.g_md_io.read32(addr);
            LogIoRead(addr, val);
            return val;
        }

        if (IsZ80Window(addr) && md_main.g_md_z80 != null)
        {
            uint val = md_main.g_md_z80.read32(addr & 0xFFFF);
            LogZ80WindowRead(addr, val);
            return val;
        }

        uint b0 = Read8(addr);
        uint b1 = Read8(addr + 1);
        uint b2 = Read8(addr + 2);
        uint b3 = Read8(addr + 3);
        return (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
    }

    public void Write8(uint addr, byte value)
    {
        if (IsZ80BusReq(addr))
        {
            _z80BusRequested = (value & 0x01) != 0;
            LogZ80RegWrite(addr, value);
            return;
        }

        if (IsZ80Reset(addr))
        {
            _z80Reset = (value & 0x01) == 0;
            LogZ80RegWrite(addr, value);
            return;
        }

        if (IsVdpPort(addr) && md_main.g_md_vdp != null)
        {
            md_main.g_md_vdp.write8(addr, value);
            return;
        }

        if (IsIoPort(addr) && md_main.g_md_io != null)
        {
            md_main.g_md_io.write8(addr, value);
            LogIoWrite(addr, value);
            return;
        }

        if (IsZ80Window(addr) && md_main.g_md_z80 != null)
        {
            md_main.g_md_z80.write8(addr & 0xFFFF, value);
            LogZ80WindowWrite(addr, value);
            return;
        }

        // Work RAM only (for now)
        if ((addr & 0xFF0000) == 0xFF0000)
        {
            int i = (int)(addr & 0xFFFF);
            _wram[i] = value;
            return;
        }

        // ignore writes elsewhere in Steg B
    }

    public void Write16(uint addr, ushort value)
    {
        if (IsZ80BusReq(addr))
        {
            _z80BusRequested = (value & 0x0100) != 0;
            LogZ80RegWrite(addr, value);
            return;
        }

        if (IsZ80Reset(addr))
        {
            _z80Reset = (value & 0x0100) == 0;
            LogZ80RegWrite(addr, value);
            return;
        }

        if (IsVdpPort(addr) && md_main.g_md_vdp != null)
        {
            md_main.g_md_vdp.write16(addr, value);
            return;
        }

        if (IsIoPort(addr) && md_main.g_md_io != null)
        {
            md_main.g_md_io.write16(addr, value);
            LogIoWrite(addr, value);
            return;
        }

        if (IsZ80Window(addr) && md_main.g_md_z80 != null)
        {
            md_main.g_md_z80.write16(addr & 0xFFFF, value);
            LogZ80WindowWrite(addr, value);
            return;
        }

        // 68k big-endian writes
        Write8(addr, (byte)(value >> 8));
        Write8(addr + 1, (byte)(value & 0xFF));
    }

    public void Write32(uint addr, uint value)
    {
        if (IsZ80BusReq(addr))
        {
            _z80BusRequested = (value & 0x0100_0000u) != 0;
            LogZ80RegWrite(addr, value);
            return;
        }

        if (IsZ80Reset(addr))
        {
            _z80Reset = (value & 0x0100_0000u) == 0;
            LogZ80RegWrite(addr, value);
            return;
        }

        if (IsVdpPort(addr) && md_main.g_md_vdp != null)
        {
            md_main.g_md_vdp.write32(addr, value);
            return;
        }

        if (IsIoPort(addr) && md_main.g_md_io != null)
        {
            md_main.g_md_io.write32(addr, value);
            LogIoWrite(addr, value);
            return;
        }

        if (IsZ80Window(addr) && md_main.g_md_z80 != null)
        {
            md_main.g_md_z80.write32(addr & 0xFFFF, value);
            LogZ80WindowWrite(addr, value);
            return;
        }

        Write8(addr,     (byte)(value >> 24));
        Write8(addr + 1, (byte)((value >> 16) & 0xFF));
        Write8(addr + 2, (byte)((value >> 8)  & 0xFF));
        Write8(addr + 3, (byte)(value & 0xFF));
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
        Console.WriteLine($"[MegaDriveBus] IO read 0x{addr:X6} -> 0x{val:X}");
    }

    private void LogIoWrite(uint addr, uint val)
    {
        int off = (int)(addr & 0x1F);
        if ((uint)off >= (uint)_ioWriteLogged.Length)
            return;
        if (_ioWriteLogged[off])
            return;
        _ioWriteLogged[off] = true;
        Console.WriteLine($"[MegaDriveBus] IO write 0x{addr:X6} <- 0x{val:X}");
    }

    private void LogZ80RegRead(uint addr, uint val)
    {
        if (_z80RegReadLogRemaining <= 0)
            return;
        _z80RegReadLogRemaining--;
        Console.WriteLine($"[MegaDriveBus] Z80 reg read 0x{addr:X6} -> 0x{val:X}");
    }

    private void LogZ80RegWrite(uint addr, uint val)
    {
        if (_z80RegWriteLogRemaining <= 0)
            return;
        _z80RegWriteLogRemaining--;
        Console.WriteLine($"[MegaDriveBus] Z80 reg write 0x{addr:X6} <- 0x{val:X}");
    }

    private void LogZ80WindowRead(uint addr, uint val)
    {
        if (_z80WindowReadLogRemaining <= 0)
            return;
        _z80WindowReadLogRemaining--;
        Console.WriteLine($"[MegaDriveBus] Z80 win read 0x{addr:X6} -> 0x{val:X}");
    }

    private void LogZ80WindowWrite(uint addr, uint val)
    {
        if (_z80WindowWriteLogRemaining <= 0)
            return;
        _z80WindowWriteLogRemaining--;
        Console.WriteLine($"[MegaDriveBus] Z80 win write 0x{addr:X6} <- 0x{val:X}");
    }
}
