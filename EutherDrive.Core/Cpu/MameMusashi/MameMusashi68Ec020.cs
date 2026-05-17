namespace EutherDrive.Core.Cpu.MameMusashi;

using EutherDrive.Core.Cpu.M68000Emu;

public sealed class MameMusashi68Ec020
{
    private const uint ResetCycles = 518;
    private const ushort SrMask = 0xf71f;
    private const ushort SupervisorMask = 0x2000;
    private const ushort MasterMask = 0x1000;
    private const ushort TraceMask = 0xc000;
    private const int ExceptionUninitializedInterrupt = 15;
    private const int ExceptionInterruptAutovector = 24;
    private const int ExceptionTrapBase = 32;

    private readonly uint[] _d = new uint[8];
    private readonly uint[] _a = new uint[8];
    private readonly Action[] _handlers = new Action[ushort.MaxValue + 1];
    private readonly byte[] _cycles = new byte[ushort.MaxValue + 1];
    private IBusInterface? _bus;
    private uint _pc;
    private uint _ppc;
    private uint _vbr;
    private uint _usp;
    private uint _isp;
    private uint _msp;
    private ushort _sr = 0x2700;
    private ushort _ir;
    private bool _stopped;
    private bool _halted;
    private uint _lastCycles;

    public MameMusashi68Ec020()
    {
        for (int i = 0; i < _handlers.Length; i++)
        {
            _handlers[i] = Illegal;
            _cycles[i] = 4;
        }

        LoadMameEc020OpcodeSpecs();
        BuildEc020OpcodeBlock0();
    }

    public uint Pc => _pc & 0x00ff_ffffu;
    public uint Ssp => CurrentSupervisorStackPointer();
    public ushort NextOpcode => ReadOpcodeWord(Pc);
    public ushort StatusRegister => _sr;
    public byte InterruptPriorityMask => (byte)((_sr >> 8) & 7);
    public bool IsStopped => _stopped;
    public bool IsFrozen => _halted;
    public bool AddressError => false;
    public bool LastInstructionWasMulOrDiv => false;
    public int ImplementedOpcodeCount { get; private set; }
    public int MameEc020OpcodeCount { get; private set; }

    public M68000.M68000State GetState()
    {
        uint[] data = new uint[8];
        uint[] address = new uint[7];
        Array.Copy(_d, data, data.Length);
        Array.Copy(_a, address, address.Length);
        return new M68000.M68000State(data, address, _usp, CurrentSupervisorStackPointer(), _sr, Pc, NextOpcode);
    }

    public void SetState(M68000.M68000State state)
    {
        if (state.Data.Length >= 8)
            Array.Copy(state.Data, _d, 8);
        if (state.Address.Length >= 7)
            Array.Copy(state.Address, _a, 7);
        _usp = state.Usp;
        _isp = state.Ssp;
        _msp = state.Ssp;
        _sr = (ushort)(state.Sr & SrMask);
        _pc = state.Pc & 0x00ff_ffffu;
        _stopped = false;
        _halted = false;
        LoadActiveStackPointer();
    }

    public void ForceInterruptMask(byte mask)
    {
        _sr = (ushort)((_sr & ~0x0700) | ((mask & 7) << 8));
    }

    public void Reset(IBusInterface bus)
    {
        _bus = bus;
        Array.Clear(_d);
        Array.Clear(_a);
        _sr = 0x2700;
        _vbr = 0;
        _usp = 0;
        _msp = 0;
        _stopped = false;
        _halted = false;
        _isp = ReadLong(0);
        _a[7] = _isp;
        _pc = ReadLong(4) & 0x00ff_ffffu;
        _ppc = _pc;
        _ir = ReadOpcodeWord(_pc);
    }

    public uint ExecuteInstruction(IBusInterface bus)
    {
        _bus = bus;
        if (bus.Reset())
        {
            Reset(bus);
            return ResetCycles;
        }
        if (bus.Halt() || _halted)
            return 1;

        CheckInterrupts();
        if (_stopped)
            return 4;

        _ppc = _pc;
        _ir = ReadOpcodeWord(_pc);
        _pc = (_pc + 2u) & 0x00ff_ffffu;
        _lastCycles = Math.Max(1u, _cycles[_ir]);
        _handlers[_ir]();
        return _lastCycles;
    }

    private void BuildEc020OpcodeBlock0()
    {
        int before = CountImplementedHandlers();
        for (int op = 0x7000; op <= 0x70ff; op++)
            _handlers[op] = Moveq;
        for (int op = 0x6000; op <= 0x6fff; op += 0x0100)
        {
            for (int low = 0; low <= 0xff; low++)
                _handlers[op | low] = Bcc;
        }

        _handlers[0x4e71] = Nop;
        _handlers[0x4e72] = Stop;
        _handlers[0x4e73] = Rte;
        _handlers[0x4e75] = Rts;
        _handlers[0x4e76] = Trapv;
        _handlers[0x4e77] = Rtr;

        for (int op = 0x4e40; op <= 0x4e4f; op++)
            _handlers[op] = Trap;
        for (int op = 0x4e80; op <= 0x4ebf; op++)
            _handlers[op] = Jsr;
        for (int op = 0x4ec0; op <= 0x4eff; op++)
            _handlers[op] = Jmp;
        ImplementedOpcodeCount = CountImplementedHandlers();
        MameEc020OpcodeCount = CountMameEc020Opcodes();
        _ = before;
    }

    private void LoadMameEc020OpcodeSpecs()
    {
        foreach (var spec in MameMusashi68kOpcodeSpecs.Ec020)
        {
            for (uint opcode = 0; opcode <= ushort.MaxValue; opcode++)
            {
                if ((opcode & spec.Mask) == spec.Match)
                    _cycles[opcode] = spec.Ec020Cycles;
            }
        }
    }

    private int CountImplementedHandlers()
    {
        int count = 0;
        for (int i = 0; i < _handlers.Length; i++)
        {
            if (_handlers[i] != Illegal)
                count++;
        }

        return count;
    }

    private static int CountMameEc020Opcodes()
    {
        bool[] covered = new bool[ushort.MaxValue + 1];
        foreach (var spec in MameMusashi68kOpcodeSpecs.Ec020)
        {
            for (uint opcode = 0; opcode <= ushort.MaxValue; opcode++)
            {
                if ((opcode & spec.Mask) == spec.Match)
                    covered[opcode] = true;
            }
        }

        int count = 0;
        foreach (bool value in covered)
        {
            if (value)
                count++;
        }

        return count;
    }

    private void Moveq()
    {
        int register = (_ir >> 9) & 7;
        _d[register] = unchecked((uint)(sbyte)(_ir & 0xff));
        SetN((int)_d[register] < 0);
        SetZ(_d[register] == 0);
        SetV(false);
        SetC(false);
        _lastCycles = 2;
    }

    private void Bcc()
    {
        int condition = (_ir >> 8) & 0x0f;
        int displacement = unchecked((sbyte)(_ir & 0xff));
        if ((_ir & 0xff) == 0)
        {
            displacement = unchecked((short)ReadOpcodeWord(_pc));
            _pc = (_pc + 2u) & 0x00ff_ffffu;
        }
        else if ((_ir & 0xff) == 0xff)
        {
            displacement = unchecked((int)ReadOpcodeLong(_pc));
            _pc = (_pc + 4u) & 0x00ff_ffffu;
        }

        if (condition == 1)
        {
            PushLong(_pc);
            _pc = unchecked((uint)((int)_pc + displacement)) & 0x00ff_ffffu;
            _lastCycles = 7;
            return;
        }

        if (CheckCondition(condition))
        {
            _pc = unchecked((uint)((int)_pc + displacement)) & 0x00ff_ffffu;
            _lastCycles = 10;
        }
        else
        {
            _lastCycles = 8;
        }
    }

    private void Nop()
    {
        _lastCycles = 4;
    }

    private void Stop()
    {
        if (!Supervisor)
        {
            ExceptionVector(8, _ppc);
            return;
        }

        SetSr(ReadOpcodeWord(_pc));
        _pc = (_pc + 2u) & 0x00ff_ffffu;
        _stopped = true;
        _lastCycles = 4;
    }

    private void Rte()
    {
        if (!Supervisor)
        {
            ExceptionVector(8, _ppc);
            return;
        }

        for (int i = 0; i < 8; i++)
        {
            uint sp = _a[7] & 0x00ff_ffffu;
            ushort sr = ReadWord(sp);
            uint pc = ReadLong(sp + 2u) & 0x00ff_ffffu;
            ushort formatWord = ReadWord(sp + 6u);
            uint format = (uint)(formatWord >> 12);

            if (format == 1)
            {
                _a[7] = (sp + 8u) & 0x00ff_ffffu;
                SetSrNoInterrupt(sr);
                continue;
            }

            uint frameSize = format switch
            {
                0 => 8u,
                2 => 12u,
                _ => 8u
            };
            _a[7] = (sp + frameSize) & 0x00ff_ffffu;
            _pc = pc;
            SetSr(sr);
            _lastCycles = 20;
            return;
        }
    }

    private void Rts()
    {
        _pc = PopLong() & 0x00ff_ffffu;
        _lastCycles = 16;
    }

    private void Trapv()
    {
        if (V)
            ExceptionVector(7, _pc);
        _lastCycles = V ? 34u : 4u;
    }

    private void Rtr()
    {
        ushort ccr = PopWord();
        _pc = PopLong() & 0x00ff_ffffu;
        _sr = (ushort)((_sr & 0xff00) | (ccr & 0x1f));
        _lastCycles = 20;
    }

    private void Trap()
    {
        uint vector = ExceptionTrapBase + (uint)(_ir & 0x0f);
        ExceptionTrapFrame(vector, _pc, _ppc);
        _lastCycles = 38;
    }

    private void Jsr()
    {
        uint target = ResolveControlEa(_ir & 0x3f);
        PushLong(_pc);
        _pc = target & 0x00ff_ffffu;
        _lastCycles = 16;
    }

    private void Jmp()
    {
        _pc = ResolveControlEa(_ir & 0x3f) & 0x00ff_ffffu;
        _lastCycles = 10;
    }

    private void Illegal()
    {
        ExceptionVector(4, _ppc);
        _lastCycles = 34;
    }

    private uint ResolveControlEa(int ea)
    {
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode switch
        {
            2 => _a[reg],
            5 => unchecked(_a[reg] + (uint)(short)ReadImmediateWord()),
            6 => GetBriefIndexedAddress(_a[reg]),
            7 when reg == 0 => unchecked((uint)(short)ReadImmediateWord()),
            7 when reg == 1 => ReadImmediateLong(),
            7 when reg == 2 => unchecked(_pc + (uint)(short)ReadImmediateWord()),
            7 when reg == 3 => GetBriefIndexedAddress(_pc),
            _ => throw new InvalidOperationException($"Unsupported EC020 control EA mode {mode}/{reg} for opcode 0x{_ir:X4}.")
        };
    }

    private uint GetBriefIndexedAddress(uint baseAddress)
    {
        ushort extension = ReadImmediateWord();
        int indexReg = (extension >> 12) & 7;
        bool addressIndex = (extension & 0x8000) != 0;
        bool longIndex = (extension & 0x0800) != 0;
        int scale = 1 << ((extension >> 9) & 3);
        int displacement = unchecked((sbyte)(extension & 0xff));
        uint raw = addressIndex ? _a[indexReg] : _d[indexReg];
        int index = longIndex ? unchecked((int)raw) : unchecked((short)raw);
        return unchecked(baseAddress + (uint)(index * scale + displacement));
    }

    private void CheckInterrupts()
    {
        byte level = (byte)(_bus!.InterruptLevel() & 7);
        if (level == 0 || level <= InterruptPriorityMask)
            return;

        _stopped = false;
        _bus.AcknowledgeInterrupt(level);
        ushort stackedSr = _sr;
        SetSrForException(level);
        uint vector = (uint)(ExceptionInterruptAutovector + level);
        uint target = ReadVector(vector);
        if (target == 0)
            target = ReadVector(ExceptionUninitializedInterrupt);
        StackFrameFormat0(_pc, stackedSr, vector);
        _pc = target & 0x00ff_ffffu;
        _lastCycles = 44;
    }

    private void ExceptionVector(uint vector, uint pc)
    {
        ushort stackedSr = _sr;
        SetSrForException(null);
        StackFrameFormat0(pc, stackedSr, vector);
        _pc = ReadVector(vector);
    }

    private void ExceptionTrapFrame(uint vector, uint pc, uint faultPc)
    {
        ushort stackedSr = _sr;
        SetSrForException(null);
        PushLong(faultPc);
        PushWord((ushort)(0x2000 | ((vector << 2) & 0x0fffu)));
        PushLong(pc);
        PushWord(stackedSr);
        _pc = ReadVector(vector);
    }

    private void StackFrameFormat0(uint pc, ushort sr, uint vector)
    {
        PushWord((ushort)((vector << 2) & 0x0fffu));
        PushLong(pc);
        PushWord(sr);
    }

    private uint ReadVector(uint vector) => ReadLong((_vbr + (vector << 2)) & 0x00ff_ffffu) & 0x00ff_ffffu;

    private void SetSrForException(byte? interruptLevel)
    {
        ushort next = (ushort)((_sr & ~TraceMask) | SupervisorMask);
        if (interruptLevel.HasValue)
            next = (ushort)((next & ~0x0700) | ((interruptLevel.Value & 7) << 8));
        SetSrNoInterrupt(next);
    }

    private void SetSr(ushort value)
    {
        SetSrNoInterrupt(value);
        CheckInterrupts();
    }

    private void SetSrNoInterrupt(ushort value)
    {
        SaveActiveStackPointer();
        _sr = (ushort)(value & SrMask);
        LoadActiveStackPointer();
    }

    private bool CheckCondition(int condition) => condition switch
    {
        0 => true,
        1 => false,
        2 => !C && !Z,
        3 => C || Z,
        4 => !C,
        5 => C,
        6 => !Z,
        7 => Z,
        8 => !V,
        9 => V,
        10 => !N,
        11 => N,
        12 => N == V,
        13 => N != V,
        14 => !Z && N == V,
        15 => Z || N != V,
        _ => false
    };

    private bool Supervisor => (_sr & SupervisorMask) != 0;
    private bool Master => (_sr & MasterMask) != 0;
    private bool X { get => (_sr & 0x0010) != 0; set => SetBit(0x0010, value); }
    private bool N { get => (_sr & 0x0008) != 0; set => SetBit(0x0008, value); }
    private bool Z { get => (_sr & 0x0004) != 0; set => SetBit(0x0004, value); }
    private bool V { get => (_sr & 0x0002) != 0; set => SetBit(0x0002, value); }
    private bool C { get => (_sr & 0x0001) != 0; set => SetBit(0x0001, value); }

    private void SetN(bool value) => N = value;
    private void SetZ(bool value) => Z = value;
    private void SetV(bool value) => V = value;
    private void SetC(bool value) => C = value;

    private void SetBit(ushort mask, bool value)
    {
        _sr = value ? (ushort)(_sr | mask) : (ushort)(_sr & ~mask);
    }

    private uint CurrentSupervisorStackPointer() => Master ? _msp : _isp;

    private void SaveActiveStackPointer()
    {
        if (Supervisor)
        {
            if (Master)
                _msp = _a[7];
            else
                _isp = _a[7];
        }
        else
        {
            _usp = _a[7];
        }
    }

    private void LoadActiveStackPointer()
    {
        _a[7] = Supervisor ? CurrentSupervisorStackPointer() : _usp;
    }

    private ushort ReadImmediateWord()
    {
        ushort value = ReadOpcodeWord(_pc);
        _pc = (_pc + 2u) & 0x00ff_ffffu;
        return value;
    }

    private uint ReadImmediateLong()
    {
        uint value = ReadOpcodeLong(_pc);
        _pc = (_pc + 4u) & 0x00ff_ffffu;
        return value;
    }

    private ushort PopWord()
    {
        ushort value = ReadWord(_a[7]);
        _a[7] = (_a[7] + 2u) & 0x00ff_ffffu;
        return value;
    }

    private uint PopLong()
    {
        uint value = ReadLong(_a[7]);
        _a[7] = (_a[7] + 4u) & 0x00ff_ffffu;
        return value;
    }

    private void PushWord(ushort value)
    {
        _a[7] = (_a[7] - 2u) & 0x00ff_ffffu;
        WriteWord(_a[7], value);
    }

    private void PushLong(uint value)
    {
        _a[7] = (_a[7] - 4u) & 0x00ff_ffffu;
        WriteLong(_a[7], value);
    }

    private byte ReadByte(uint address) => _bus!.ReadByte(address & 0x00ff_ffffu);
    private ushort ReadWord(uint address) => _bus!.ReadWord(address & 0x00ff_ffffu);
    private uint ReadLong(uint address) => _bus!.ReadLong(address & 0x00ff_ffffu);
    private void WriteWord(uint address, ushort value) => _bus!.WriteWord(address & 0x00ff_ffffu, value);
    private void WriteLong(uint address, uint value) => _bus!.WriteLong(address & 0x00ff_ffffu, value);
    private ushort ReadOpcodeWord(uint address) => _bus is IOpcodeBusInterface op ? op.ReadOpcodeWord(address & 0x00ff_ffffu) : ReadWord(address);
    private uint ReadOpcodeLong(uint address) => ((uint)ReadOpcodeWord(address) << 16) | ReadOpcodeWord(address + 2u);
}
