using System;

namespace mame.eutherdrive_m68000;

public sealed class M68000
{
    private const uint ResetCycles = 132;

    private readonly Registers _regs = new();
    private readonly bool _allowTasWrites;
    private readonly bool _allowUnalignedWordLongAccess;
    private readonly string _name;
    private IBusInterface? _executorBus;
    private InstructionExecutor? _executor;

    private M68000(bool allowTasWrites, bool allowUnalignedWordLongAccess, string name)
    {
        _allowTasWrites = allowTasWrites;
        _allowUnalignedWordLongAccess = allowUnalignedWordLongAccess;
        _name = name;
    }

    public static Builder CreateBuilder() => new();

    public sealed class Builder
    {
        private bool _allowTasWrites = true;
        private bool _allowUnalignedWordLongAccess;
        private string _name = string.Empty;

        public Builder AllowTasWrites(bool allow)
        {
            _allowTasWrites = allow;
            return this;
        }

        public Builder AllowUnalignedWordLongAccess(bool allow)
        {
            _allowUnalignedWordLongAccess = allow;
            return this;
        }

        public Builder Name(string name)
        {
            _name = name ?? string.Empty;
            return this;
        }

        public M68000 Build()
        {
            return new M68000(_allowTasWrites, _allowUnalignedWordLongAccess, _name);
        }
    }

    public uint Pc => _regs.Pc;
    public uint Ssp => _regs.Ssp;
    public ushort NextOpcode => _regs.Prefetch;
    public ushort StatusRegister => _regs.StatusRegister();
    public byte InterruptPriorityMask => _regs.InterruptPriorityMask;
    public byte? PendingInterruptLevel => _regs.PendingInterruptLevel;
    public bool IsStopped => _regs.Stopped;
    public bool IsFrozen => _regs.Frozen;
    public bool AddressError => _regs.AddressError;
    public bool LastInstructionWasMulOrDiv => _regs.LastInstructionWasMulDiv;

    public void ForceInterruptMask(byte mask)
    {
        mask &= 0x07;
        _regs.InterruptPriorityMask = mask;
    }

    public readonly struct M68000State
    {
        public readonly uint[] Data;
        public readonly uint[] Address;
        public readonly uint Usp;
        public readonly uint Ssp;
        public readonly ushort Sr;
        public readonly uint Pc;
        public readonly ushort Prefetch;

        public M68000State(uint[] data, uint[] address, uint usp, uint ssp, ushort sr, uint pc, ushort prefetch)
        {
            Data = data;
            Address = address;
            Usp = usp;
            Ssp = ssp;
            Sr = sr;
            Pc = pc;
            Prefetch = prefetch;
        }
    }

    public M68000State GetState()
    {
        uint[] data = new uint[8];
        uint[] address = new uint[7];
        Array.Copy(_regs.Data, data, data.Length);
        Array.Copy(_regs.Address, address, address.Length);
        return new M68000State(data, address, _regs.Usp, _regs.Ssp, _regs.StatusRegister(), _regs.Pc, _regs.Prefetch);
    }

    public void SetState(M68000State state)
    {
        if (state.Data.Length >= 8)
            Array.Copy(state.Data, _regs.Data, 8);
        if (state.Address.Length >= 7)
            Array.Copy(state.Address, _regs.Address, 7);
        _regs.Usp = state.Usp;
        _regs.Ssp = state.Ssp;
        _regs.SetStatusRegister(state.Sr);
        _regs.Pc = state.Pc;
        _regs.Prefetch = state.Prefetch;
        _regs.AddressError = false;
        _regs.Stopped = false;
        _regs.Frozen = false;
    }

    public void Reset(IBusInterface bus)
    {
        _regs.SupervisorMode = true;
        _regs.TraceEnabled = false;
        _regs.InterruptPriorityMask = Registers.DefaultInterruptMask;
        _regs.PendingInterruptLevel = null;
        _regs.Stopped = false;
        _regs.Frozen = false;
        _regs.AddressError = false;

        _regs.Ssp = bus.ReadLong(0x000000) & 0x00FF_FFFF;
        _regs.Pc = bus.ReadLong(0x000004) & 0x00FF_FFFF;
        _regs.Prefetch = bus.ReadWord(_regs.Pc);
    }

    public void BindBus(IBusInterface bus)
    {
        _executorBus = bus;
        _executor = new InstructionExecutor(_regs, bus, _allowTasWrites, _allowUnalignedWordLongAccess, _name);
    }

    public uint ExecuteBoundInstruction()
    {
        if (_regs.Frozen)
            return 1;

        return _executor!.Execute();
    }

    public bool TryConsumeBoundIdleLoop(int cycleBudget, out uint cycles)
    {
        if (_regs.Frozen || _executor == null)
        {
            cycles = 0;
            return false;
        }

        return _executor.TryConsumeIdleLoop(cycleBudget, out cycles);
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

        if (!ReferenceEquals(bus, _executorBus) || _executor == null)
        {
            _executorBus = bus;
            _executor = new InstructionExecutor(_regs, bus, _allowTasWrites, _allowUnalignedWordLongAccess, _name);
        }

        return _executor.Execute();
    }
}
