using System;

namespace EutherDrive.Core.Cpu.M68000Emu;

public sealed class M68000
{
    private const uint ResetCycles = 132;

    private readonly Registers _regs = new();
    private readonly bool _allowTasWrites;
    private readonly string _name;

    private M68000(bool allowTasWrites, string name)
    {
        _allowTasWrites = allowTasWrites;
        _name = name;
    }

    public static Builder CreateBuilder() => new();

    public sealed class Builder
    {
        private bool _allowTasWrites = true;
        private string _name = string.Empty;

        public Builder AllowTasWrites(bool allow)
        {
            _allowTasWrites = allow;
            return this;
        }

        public Builder Name(string name)
        {
            _name = name ?? string.Empty;
            return this;
        }

        public M68000 Build()
        {
            return new M68000(_allowTasWrites, _name);
        }
    }

    public uint Pc => _regs.Pc;
    public uint Ssp => _regs.Ssp;
    public ushort NextOpcode => _regs.Prefetch;
    public bool AddressError => _regs.AddressError;
    public bool LastInstructionWasMulOrDiv => _regs.LastInstructionWasMulDiv;

    public void Reset(IBusInterface bus)
    {
        _regs.SupervisorMode = true;
        _regs.TraceEnabled = false;
        _regs.InterruptPriorityMask = Registers.DefaultInterruptMask;
        _regs.Stopped = false;
        _regs.Frozen = false;
        _regs.AddressError = false;

        _regs.Ssp = bus.ReadLong(0x000000) & 0x00FF_FFFF;
        _regs.Pc = bus.ReadLong(0x000004) & 0x00FF_FFFF;
        _regs.Prefetch = bus.ReadWord(_regs.Pc);
    }

    public uint ExecuteInstruction(IBusInterface bus)
    {
        if (bus.Reset())
        {
            Reset(bus);
            return ResetCycles;
        }
        if (bus.Halt() || _regs.Frozen)
            return 1;

        return new InstructionExecutor(_regs, bus, _allowTasWrites, _name).Execute();
    }
}
