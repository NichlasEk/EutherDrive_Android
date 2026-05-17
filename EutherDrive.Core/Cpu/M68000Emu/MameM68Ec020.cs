namespace EutherDrive.Core.Cpu.M68000Emu;

public sealed class MameM68Ec020
{
    private const uint ResetCycles = 132;
    private const uint TrapVectorBase = 32;
    private const uint AutoVectorBase = 24;
    private const ushort SupervisorMask = 0x2000;
    private const ushort TraceMask = 0xc000;

    private readonly mame.eutherdrive_m68000.M68000 _cpu;
    private BusAdapter? _boundBus;
    private byte? _pendingInterruptLevel;

    private MameM68Ec020(string name)
    {
        _cpu = mame.eutherdrive_m68000.M68000.CreateBuilder()
            .AllowUnalignedWordLongAccess(true)
            .Name(name)
            .Build();
    }

    public static MameM68Ec020 Create(string name = "") => new(name);

    public uint Pc => _cpu.Pc;
    public uint Ssp => _cpu.Ssp;
    public ushort NextOpcode => _cpu.NextOpcode;
    public ushort StatusRegister => _cpu.StatusRegister;
    public byte InterruptPriorityMask => _cpu.InterruptPriorityMask;
    public byte? PendingInterruptLevel => _pendingInterruptLevel;
    public bool IsStopped => _cpu.IsStopped;
    public bool IsFrozen => _cpu.IsFrozen;
    public bool AddressError => _cpu.AddressError;
    public bool LastInstructionWasMulOrDiv => _cpu.LastInstructionWasMulOrDiv;

    public void ForceInterruptMask(byte mask) => _cpu.ForceInterruptMask(mask);

    public M68000.M68000State GetState()
    {
        var state = _cpu.GetState();
        return new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, state.Pc, state.Prefetch);
    }

    public void SetState(M68000.M68000State state)
    {
        _cpu.SetState(new mame.eutherdrive_m68000.M68000.M68000State(
            state.Data,
            state.Address,
            state.Usp,
            state.Ssp,
            state.Sr,
            state.Pc,
            state.Prefetch));
    }

    public void Reset(IBusInterface bus)
    {
        var adapter = BindBus(bus);
        _pendingInterruptLevel = null;
        _cpu.Reset(adapter);
    }

    public uint ExecuteInstruction(IBusInterface bus)
    {
        var adapter = BindBus(bus);
        if (bus.Reset())
        {
            _pendingInterruptLevel = null;
            _cpu.Reset(adapter);
            return ResetCycles;
        }
        if (bus.Halt() || _cpu.IsFrozen)
            return 1;

        byte interruptLevel = (byte)(bus.InterruptLevel() & 0x07);
        byte mask = (byte)(_cpu.InterruptPriorityMask & 0x07);
        if (interruptLevel > mask)
        {
            _pendingInterruptLevel = interruptLevel;
            return ServiceInterrupt(bus, interruptLevel);
        }

        _pendingInterruptLevel = null;

        ushort opcode = ReadOpcodeWord(bus, _cpu.Pc);
        if (opcode == 0x4e73)
            return ExecuteRte(bus);
        if ((opcode & 0xfff0) == 0x4e40)
            return ExecuteTrap(bus, (uint)(opcode & 0x0f));

        return _cpu.ExecuteInstruction(adapter);
    }

    private uint ServiceInterrupt(IBusInterface bus, byte level)
    {
        var state = GetState();
        ushort stackedSr = state.Sr;
        ushort sr = EnterExceptionSr(stackedSr, interruptLevel: level);
        uint vector = AutoVectorBase + level;
        uint pc = state.Pc & 0x00ff_ffffu;
        uint ssp = PushFormat0Frame(bus, state.Ssp, stackedSr, pc, vector);
        uint newPc = ReadVector(bus, vector);

        bus.AcknowledgeInterrupt(level);
        state = new M68000.M68000State(
            state.Data,
            state.Address,
            state.Usp,
            ssp,
            sr,
            newPc,
            ReadOpcodeWord(bus, newPc));
        SetState(state);
        return 44;
    }

    private uint ExecuteTrap(IBusInterface bus, uint trap)
    {
        var state = GetState();
        ushort stackedSr = state.Sr;
        ushort sr = EnterExceptionSr(stackedSr);
        uint vector = TrapVectorBase + trap;
        uint instructionPc = state.Pc & 0x00ff_ffffu;
        uint returnPc = (instructionPc + 2u) & 0x00ff_ffffu;
        uint ssp = PushFormat2Frame(bus, state.Ssp, stackedSr, returnPc, vector, instructionPc);
        uint newPc = ReadVector(bus, vector);

        state = new M68000.M68000State(
            state.Data,
            state.Address,
            state.Usp,
            ssp,
            sr,
            newPc,
            ReadOpcodeWord(bus, newPc));
        SetState(state);
        return 38;
    }

    private uint ExecuteRte(IBusInterface bus)
    {
        var state = GetState();
        if ((state.Sr & SupervisorMask) == 0)
            return _cpu.ExecuteInstruction(BindBus(bus));

        uint sp = state.Ssp & 0x00ff_ffffu;
        for (int unwind = 0; unwind < 8; unwind++)
        {
            ushort formatWord = bus.ReadWord((sp + 6u) & 0x00ff_ffffu);
            uint format = (uint)(formatWord >> 12);
            ushort sr = bus.ReadWord(sp);

            if (format == 1)
            {
                sp = (sp + 8u) & 0x00ff_ffffu;
                state = new M68000.M68000State(state.Data, state.Address, state.Usp, sp, sr, state.Pc, state.Prefetch);
                continue;
            }

            uint pc = bus.ReadLong((sp + 2u) & 0x00ff_ffffu) & 0x00ff_ffffu;
            uint frameSize = format switch
            {
                0 => 8u,
                2 => 12u,
                _ => 8u
            };
            sp = (sp + frameSize) & 0x00ff_ffffu;
            state = new M68000.M68000State(state.Data, state.Address, state.Usp, sp, sr, pc, ReadOpcodeWord(bus, pc));
            SetState(state);
            return 20;
        }

        SetState(state);
        return 20;
    }

    private static ushort EnterExceptionSr(ushort sr, byte? interruptLevel = null)
    {
        ushort next = (ushort)((sr & ~TraceMask) | SupervisorMask);
        if (interruptLevel.HasValue)
            next = (ushort)((next & ~0x0700) | ((interruptLevel.Value & 0x07) << 8));
        return next;
    }

    private static uint PushFormat0Frame(IBusInterface bus, uint ssp, ushort sr, uint pc, uint vector)
    {
        ssp = PushWord(bus, ssp, (ushort)((vector << 2) & 0x0fff));
        ssp = PushLong(bus, ssp, pc);
        ssp = PushWord(bus, ssp, sr);
        return ssp;
    }

    private static uint PushFormat2Frame(IBusInterface bus, uint ssp, ushort sr, uint pc, uint vector, uint faultPc)
    {
        ssp = PushLong(bus, ssp, faultPc);
        ssp = PushWord(bus, ssp, (ushort)(0x2000 | ((vector << 2) & 0x0fff)));
        ssp = PushLong(bus, ssp, pc);
        ssp = PushWord(bus, ssp, sr);
        return ssp;
    }

    private static uint PushWord(IBusInterface bus, uint ssp, ushort value)
    {
        ssp = (ssp - 2u) & 0x00ff_ffffu;
        bus.WriteWord(ssp, value);
        return ssp;
    }

    private static uint PushLong(IBusInterface bus, uint ssp, uint value)
    {
        ssp = (ssp - 4u) & 0x00ff_ffffu;
        bus.WriteLong(ssp, value);
        return ssp;
    }

    private static uint ReadVector(IBusInterface bus, uint vector)
        => bus.ReadLong((vector << 2) & 0x00ff_ffffu) & 0x00ff_ffffu;

    private static ushort ReadOpcodeWord(IBusInterface bus, uint address)
        => bus is IOpcodeBusInterface opcodeBus ? opcodeBus.ReadOpcodeWord(address) : bus.ReadWord(address);

    private BusAdapter BindBus(IBusInterface bus)
    {
        if (_boundBus == null || !ReferenceEquals(_boundBus.Inner, bus))
            _boundBus = new BusAdapter(bus);
        return _boundBus;
    }

    private sealed class BusAdapter : mame.eutherdrive_m68000.IBusInterface
    {
        public readonly IBusInterface Inner;

        public BusAdapter(IBusInterface inner)
        {
            Inner = inner;
        }

        public byte ReadByte(uint address) => Inner.ReadByte(address);
        public ushort ReadWord(uint address) => Inner.ReadWord(address);
        public uint ReadLong(uint address) => Inner.ReadLong(address);
        public void WriteByte(uint address, byte value) => Inner.WriteByte(address, value);
        public void WriteWord(uint address, ushort value) => Inner.WriteWord(address, value);
        public void WriteLong(uint address, uint value) => Inner.WriteLong(address, value);
        public byte InterruptLevel() => 0;
        public void AcknowledgeInterrupt(byte level) { }
        public bool Reset() => Inner.Reset();
        public bool Halt() => Inner.Halt();
        public mame.eutherdrive_m68000.BusSignals Signals => new(Inner.Signals.Reset);
        public ushort CurrentOpcode => Inner.CurrentOpcode;
    }
}
