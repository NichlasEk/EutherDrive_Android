using EutherDrive.Core.Cpu.M68000Emu;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core.SegaCd;

internal sealed class SegaCdMainM68kBus : IBusInterface
{
    private readonly md_bus _bus;
    private readonly SegaCdMemory? _memory;

    public SegaCdMainM68kBus(md_bus bus, SegaCdMemory? memory = null)
    {
        _bus = bus;
        _memory = memory;
    }

    public byte ReadByte(uint address) => _bus.read8(address);
    public ushort ReadWord(uint address) => _bus.read16(address);
    public uint ReadLong(uint address) => _bus.read32(address);
    public void WriteByte(uint address, byte value) => _bus.write8(address, value);
    public void WriteWord(uint address, ushort value) => _bus.write16(address, value);
    public void WriteLong(uint address, uint value) => _bus.write32(address, value);

    public byte InterruptLevel()
    {
        byte level = 0;
        md_vdp? vdp = md_main.g_md_vdp;
        bool hintEnabled = vdp != null && vdp.g_vdp_reg_0_4_hinterrupt == 1;
        bool vintEnabled = vdp != null && vdp.g_vdp_reg_1_5_vinterrupt == 1;

        // Return highest eligible pending IRQ level.
        // Keep VDP enable gating so we don't deliver spurious H/V IRQs.
        if (md_m68k.g_interrupt_H_req && hintEnabled && level < 4)
            level = 4;
        if (md_m68k.g_interrupt_V_req && vintEnabled && level < 6)
            level = 6;
        if (md_m68k.g_interrupt_EXT_req && level < md_m68k.g_interrupt_EXT_level)
            level = md_m68k.g_interrupt_EXT_level;

        return level;
    }

    public void AcknowledgeInterrupt(byte level)
    {
        if (level != 0)
            md_main.CountMainIrqAcknowledge(level);

        if (level == 4)
        {
            md_m68k.g_interrupt_H_req = false;
            md_m68k.g_interrupt_H_act = false;
            return;
        }

        if (level == 6)
        {
            md_m68k.g_interrupt_V_req = false;
            md_m68k.g_interrupt_V_act = false;
            return;
        }

        if (level == md_m68k.g_interrupt_EXT_level)
        {
            md_m68k.g_interrupt_EXT_req = false;
            md_m68k.g_interrupt_EXT_act = false;
            md_m68k.g_interrupt_EXT_ack?.Invoke(level);
        }
    }

    public bool Reset() => false;
    public bool Halt() => false;

    public BusSignals Signals => new(false);
    public ushort CurrentOpcode => 0;
}

internal sealed class SegaCdSubM68kBus : IBusInterface
{
    private readonly SegaCdMemory _memory;

    public SegaCdSubM68kBus(SegaCdMemory memory)
    {
        _memory = memory;
    }

    public byte ReadByte(uint address) => _memory.ReadSubByte(address);
    public ushort ReadWord(uint address) => _memory.ReadSubWord(address);
    public uint ReadLong(uint address) => (uint)((_memory.ReadSubWord(address) << 16) | _memory.ReadSubWord(address + 2));
    public void WriteByte(uint address, byte value) => _memory.WriteSubByte(address, value);
    public void WriteWord(uint address, ushort value) => _memory.WriteSubWord(address, value);
    public void WriteLong(uint address, uint value)
    {
        _memory.WriteSubWord(address, (ushort)(value >> 16));
        _memory.WriteSubWord(address + 2, (ushort)value);
    }

    public byte InterruptLevel() => _memory.GetSubInterruptLevel();

    public void AcknowledgeInterrupt(byte level)
    {
        if (level != 0)
            _memory.AcknowledgeSubInterrupt(level);
    }

    public bool Reset() => _memory.SubCpuReset;
    public bool Halt() => _memory.SubCpuHalt;

    public BusSignals Signals => new(false);
    public ushort CurrentOpcode => 0;
}
