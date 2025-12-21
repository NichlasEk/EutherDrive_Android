using System;

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

    public MegaDriveBus(byte[] rom)
    {
        _rom = rom ?? throw new ArgumentNullException(nameof(rom));
        if (_rom.Length == 0) throw new ArgumentException("ROM is empty.", nameof(rom));
    }

    public void Reset()
    {
        Array.Clear(_wram, 0, _wram.Length);
    }

    // 68k ROM space: 0x000000..0x3FFFFF
    // 68k WRAM:      0xFF0000..0xFFFFFF
    public byte Read8(uint addr)
    {
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
        int hi = Read8(addr);
        int lo = Read8(addr + 1);
        return (ushort)((hi << 8) | lo);
    }

    public uint Read32(uint addr)
    {
        uint b0 = Read8(addr);
        uint b1 = Read8(addr + 1);
        uint b2 = Read8(addr + 2);
        uint b3 = Read8(addr + 3);
        return (b0 << 24) | (b1 << 16) | (b2 << 8) | b3;
    }

    public void Write8(uint addr, byte value)
    {
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
        // 68k big-endian writes
        Write8(addr, (byte)(value >> 8));
        Write8(addr + 1, (byte)(value & 0xFF));
    }

    public void Write32(uint addr, uint value)
    {
        Write8(addr,     (byte)(value >> 24));
        Write8(addr + 1, (byte)((value >> 16) & 0xFF));
        Write8(addr + 2, (byte)((value >> 8)  & 0xFF));
        Write8(addr + 3, (byte)(value & 0xFF));
    }

    public ReadOnlySpan<byte> GetRomSpan() => _rom;
    public ReadOnlySpan<byte> GetWramSpan() => _wram;
}
