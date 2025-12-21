using System;

namespace EutherDrive.Core.MdTracerCore;

/// <summary>
/// Minimal bus-bridge som MDTracer-opcodes förväntar sig (instans via md_main.g_md_bus).
/// Den forwardar till EutherDrive.Core.MegaDriveBus via static Current.
/// </summary>
public sealed class md_bus
{
    public static EutherDrive.Core.MegaDriveBus? Current { get; set; }

    // uint-varianter (vanligast i MDTracer)
    public byte read8(uint addr) => Current?.Read8(addr) ?? (byte)0xFF;
    public ushort read16(uint addr) => Current?.Read16(addr) ?? 0xFFFF;
    public uint read32(uint addr) => Current?.Read32(addr) ?? 0xFFFFFFFF;

    public void write8(uint addr, byte value) => Current?.Write8(addr, value);
    public void write16(uint addr, ushort value) => Current?.Write16(addr, value);
    public void write32(uint addr, uint value) => Current?.Write32(addr, value);

    // int-overloads (ibland förekommer i äldre kod)
    public byte read8(int addr) => read8((uint)addr);
    public ushort read16(int addr) => read16((uint)addr);
    public uint read32(int addr) => read32((uint)addr);

    public void write8(int addr, byte value) => write8((uint)addr, value);
    public void write16(int addr, ushort value) => write16((uint)addr, value);
    public void write32(int addr, uint value) => write32((uint)addr, value);
}
