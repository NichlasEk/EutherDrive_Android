namespace EutherDrive.Core.Cpu.MameMusashi;

using EutherDrive.Core.Cpu.M68000Emu;

public sealed class MameMusashi68Ec020
{
    private const uint ResetCycles = 518;
    private const ushort SrMask = 0xf71f;
    private const ushort SupervisorMask = 0x2000;
    private const ushort MasterMask = 0x1000;
    private const ushort TraceMask = 0xc000;
    private const int ExceptionFormatError = 14;
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
    private uint _sfc;
    private uint _dfc;
    private uint _cacr;
    private uint _caar;
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

    public uint Pc => _pc;
    public uint ActiveStackPointer => _a[7];
    public uint UserStackPointer => _usp;
    public uint InterruptStackPointer => _isp;
    public uint MasterStackPointer => _msp;
    public uint Ssp => CurrentSupervisorStackPointer();
    public ushort NextOpcode => ReadOpcodeWord(Pc);
    public ushort StatusRegister => _sr;
    public uint VectorBase
    {
        get => _vbr;
        set => _vbr = value;
    }
    public byte InterruptPriorityMask => (byte)((_sr >> 8) & 7);
    public bool IsStopped => _stopped;
    public bool IsFrozen => _halted;
    public bool AddressError => false;
    public bool LastInstructionWasMulOrDiv => false;
    public ulong RteCount { get; private set; }
    public ulong SuspiciousRteCount { get; private set; }
    public uint LastRteStackPointer { get; private set; }
    public ushort LastRteStatusRegister { get; private set; }
    public uint LastRteProgramCounter { get; private set; }
    public ushort LastRteFormatWord { get; private set; }
    public uint FirstSuspiciousRteStackPointer { get; private set; }
    public uint FirstSuspiciousRteInstructionPc { get; private set; }
    public ushort FirstSuspiciousRteOpcode { get; private set; }
    public ushort FirstSuspiciousRteStatusRegister { get; private set; }
    public uint FirstSuspiciousRteProgramCounter { get; private set; }
    public ushort FirstSuspiciousRteFormatWord { get; private set; }
    public ulong LowStackSwitchCount { get; private set; }
    public uint LastLowStackSwitchPc { get; private set; }
    public ushort LastLowStackSwitchOpcode { get; private set; }
    public ushort LastLowStackSwitchOldSr { get; private set; }
    public ushort LastLowStackSwitchNewSr { get; private set; }
    public uint LastLowStackSwitchOldStackPointer { get; private set; }
    public uint LastLowStackSwitchNewStackPointer { get; private set; }
    public uint LastLowStackSwitchUserStackPointer { get; private set; }
    public uint LastLowStackSwitchInterruptStackPointer { get; private set; }
    public uint LastLowStackSwitchMasterStackPointer { get; private set; }
    public ulong SuspiciousSupervisorStackCount { get; private set; }
    public uint FirstSuspiciousSupervisorStackPc { get; private set; }
    public ushort FirstSuspiciousSupervisorStackOpcode { get; private set; }
    public ushort FirstSuspiciousSupervisorStackStatusRegister { get; private set; }
    public uint FirstSuspiciousSupervisorStackPointer { get; private set; }
    public uint LastRestoreIndexedPc { get; private set; }
    public ushort LastRestoreIndexedExtension { get; private set; }
    public uint LastRestoreIndexedBase { get; private set; }
    public uint LastRestoreIndexedRawIndex { get; private set; }
    public int LastRestoreIndexedIndex { get; private set; }
    public uint LastRestoreIndexedAddress { get; private set; }
    public uint LastRestoreIndexedValue { get; private set; }
    public ulong IllegalInstructionCount { get; private set; }
    public uint FirstIllegalInstructionPc { get; private set; }
    public ushort FirstIllegalInstructionOpcode { get; private set; }
    public ushort FirstIllegalInstructionStatusRegister { get; private set; }
    public ulong FormatErrorCount { get; private set; }
    public uint FirstFormatErrorPc { get; private set; }
    public ushort FirstFormatErrorOpcode { get; private set; }
    public ushort FirstFormatErrorStatusRegister { get; private set; }
    public ushort FirstFormatErrorFrameWord { get; private set; }
    public int ImplementedOpcodeCount { get; private set; }
    public int MameEc020OpcodeCount { get; private set; }

    public M68000.M68000State GetState()
    {
        SaveActiveStackPointer();
        uint[] data = new uint[8];
        uint[] address = new uint[7];
        Array.Copy(_d, data, data.Length);
        Array.Copy(_a, address, address.Length);
        return new M68000.M68000State(data, address, _usp, CurrentSupervisorStackPointer(), _sr, Pc, NextOpcode);
    }

    public void SetState(M68000.M68000State state)
    {
        SaveActiveStackPointer();
        if (state.Data.Length >= 8)
            Array.Copy(state.Data, _d, 8);
        if (state.Address.Length >= 7)
            Array.Copy(state.Address, _a, 7);
        _usp = state.Usp;
        _sr = (ushort)(state.Sr & SrMask);
        if (Supervisor)
        {
            if (Master)
                _msp = state.Ssp;
            else
                _isp = state.Ssp;
        }
        else
        {
            _usp = state.Usp;
        }
        _pc = state.Pc;
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
        _sfc = 0;
        _dfc = 0;
        _cacr = 0;
        _caar = 0;
        _usp = 0;
        _msp = 0;
        RteCount = 0;
        SuspiciousRteCount = 0;
        LastRteStackPointer = 0;
        LastRteStatusRegister = 0;
        LastRteProgramCounter = 0;
        LastRteFormatWord = 0;
        FirstSuspiciousRteStackPointer = 0;
        FirstSuspiciousRteInstructionPc = 0;
        FirstSuspiciousRteOpcode = 0;
        FirstSuspiciousRteStatusRegister = 0;
        FirstSuspiciousRteProgramCounter = 0;
        FirstSuspiciousRteFormatWord = 0;
        LowStackSwitchCount = 0;
        LastLowStackSwitchPc = 0;
        LastLowStackSwitchOpcode = 0;
        LastLowStackSwitchOldSr = 0;
        LastLowStackSwitchNewSr = 0;
        LastLowStackSwitchOldStackPointer = 0;
        LastLowStackSwitchNewStackPointer = 0;
        LastLowStackSwitchUserStackPointer = 0;
        LastLowStackSwitchInterruptStackPointer = 0;
        LastLowStackSwitchMasterStackPointer = 0;
        SuspiciousSupervisorStackCount = 0;
        FirstSuspiciousSupervisorStackPc = 0;
        FirstSuspiciousSupervisorStackOpcode = 0;
        FirstSuspiciousSupervisorStackStatusRegister = 0;
        FirstSuspiciousSupervisorStackPointer = 0;
        LastRestoreIndexedPc = 0;
        LastRestoreIndexedExtension = 0;
        LastRestoreIndexedBase = 0;
        LastRestoreIndexedRawIndex = 0;
        LastRestoreIndexedIndex = 0;
        LastRestoreIndexedAddress = 0;
        LastRestoreIndexedValue = 0;
        IllegalInstructionCount = 0;
        FirstIllegalInstructionPc = 0;
        FirstIllegalInstructionOpcode = 0;
        FirstIllegalInstructionStatusRegister = 0;
        FormatErrorCount = 0;
        FirstFormatErrorPc = 0;
        FirstFormatErrorOpcode = 0;
        FirstFormatErrorStatusRegister = 0;
        FirstFormatErrorFrameWord = 0;
        _stopped = false;
        _halted = false;
        _isp = ReadLong(0);
        _a[7] = _isp;
        _pc = ReadLong(4);
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

        RecordSuspiciousSupervisorStack();
        if (CheckInterrupts())
        {
            RecordSuspiciousSupervisorStack();
            return _lastCycles;
        }
        if (_stopped)
            return 4;

        _ppc = _pc;
        _ir = ReadOpcodeWord(_pc);
        _pc = (_pc + 2u);
        _lastCycles = Math.Max(1u, _cycles[_ir]);
        _handlers[_ir]();
        RecordSuspiciousSupervisorStack();
        return _lastCycles;
    }

    private void BuildEc020OpcodeBlock0()
    {
        int before = CountImplementedHandlers();
        for (int op = 0x1000; op <= 0x3fff; op++)
            _handlers[op] = Move;
        RegisterLongMultiplyDivideOpcodes();
        for (int op = 0x4000; op <= 0x40bf; op++)
        {
            if (IsUnaryDataAlterableEffectiveAddress(op))
                _handlers[op] = Negx;
        }
        for (int op = 0x4200; op <= 0x42bf; op++)
        {
            if (IsUnaryDataAlterableEffectiveAddress(op))
                _handlers[op] = Clear;
        }
        for (int op = 0x4400; op <= 0x44bf; op++)
        {
            if (IsUnaryDataAlterableEffectiveAddress(op))
                _handlers[op] = Neg;
        }
        for (int op = 0x4600; op <= 0x46bf; op++)
        {
            if (IsUnaryDataAlterableEffectiveAddress(op))
                _handlers[op] = Not;
        }
        for (int op = 0x40c0; op <= 0x40ff; op++)
        {
            int ea = op & 0x3f;
            int mode = (ea >> 3) & 7;
            int reg = ea & 7;
            if (mode != 1 && (mode < 7 || reg <= 1))
                _handlers[op] = MoveSrToEa;
        }
        for (int op = 0x44c0; op <= 0x44ff; op++)
        {
            int ea = op & 0x3f;
            int mode = (ea >> 3) & 7;
            int reg = ea & 7;
            if (mode != 1 && (mode < 7 || reg <= 4))
                _handlers[op] = MoveEaToCcr;
        }
        for (int op = 0x46c0; op <= 0x46ff; op++)
        {
            int ea = op & 0x3f;
            int mode = (ea >> 3) & 7;
            int reg = ea & 7;
            if (mode != 1 && (mode < 7 || reg <= 4))
                _handlers[op] = MoveEaToSr;
        }
        for (int op = 0x4880; op <= 0x4cff; op++)
        {
            if ((op & 0xfb80) == 0x4880 && IsMovemEffectiveAddress(op))
                _handlers[op] = Movem;
        }
        for (int op = 0x41c0; op <= 0x4fff; op++)
        {
            if ((op & 0xf1c0) == 0x41c0)
                _handlers[op] = Lea;
        }
        for (int op = 0x4100; op <= 0x41ff; op++)
        {
            if (((op & 0xf1c0) == 0x4100 || (op & 0xf1c0) == 0x4180) && IsChkEffectiveAddress(op))
                _handlers[op] = Chk;
        }
        for (int op = 0x5000; op <= 0x5fff; op++)
        {
            if ((op & 0x00c0) != 0x00c0)
                _handlers[op] = AddSubQuick;
        }
        for (int op = 0x50c0; op <= 0x5fff; op++)
        {
            int ea = op & 0x3f;
            int mode = (ea >> 3) & 7;
            int reg = ea & 7;
            if ((op & 0x00c0) == 0x00c0
                && mode != 1
                && (mode < 7 || reg <= 1))
            {
                _handlers[op] = SetOnCondition;
            }
        }
        for (int op = 0x50c8; op <= 0x5fcf; op++)
        {
            if ((op & 0xf0f8) == 0x50c8)
                _handlers[op] = Dbcc;
        }
        for (int op = 0x0000; op <= 0x0fff; op++)
        {
            if (IsChk2Cmp2Opcode(op))
            {
                _handlers[op] = Chk2Cmp2;
                continue;
            }

            if ((op & 0x0100) != 0 && IsDynamicBitEffectiveAddress(op))
            {
                _handlers[op] = DynamicBitOperation;
                continue;
            }

            if ((op & 0xff00) == 0x0800 && IsImmediateBitEffectiveAddress(op))
            {
                _handlers[op] = ImmediateBitOperation;
                continue;
            }

            int group = op & 0xff00;
            int sizeCode = (op >> 6) & 3;
            int mode = (op >> 3) & 7;
            if (sizeCode <= 2
                && mode != 1
                && (group == 0x0000 || group == 0x0200 || group == 0x0400 || group == 0x0600 || group == 0x0a00 || group == 0x0c00))
            {
                _handlers[op] = ImmediateOperation;
            }
        }
        for (int op = 0x7000; op <= 0x7fff; op++)
        {
            if ((op & 0xf100) == 0x7000)
                _handlers[op] = Moveq;
        }
        for (int op = 0x8000; op <= 0x8fff; op++)
        {
            int opmode = (op >> 6) & 7;
            if (opmode <= 2 && IsDataAluSourceEffectiveAddress(op))
                _handlers[op] = DataRegisterAlu;
            else if (opmode is 3 or 7 && IsDataAluSourceEffectiveAddress(op))
                _handlers[op] = DivideWord;
            else if (opmode is >= 4 and <= 6 && IsMemoryAlterableEffectiveAddress(op))
                _handlers[op] = RegisterToEffectiveAddressAlu;
        }
        for (int op = 0x9000; op <= 0x9fff; op++)
        {
            int opmode = (op >> 6) & 7;
            if (opmode <= 2 && IsDataAluSourceEffectiveAddress(op))
                _handlers[op] = DataRegisterAlu;
            else if (opmode is >= 4 and <= 6 && IsMemoryAlterableEffectiveAddress(op))
                _handlers[op] = RegisterToEffectiveAddressAlu;
        }
        for (int op = 0x90c0; op <= 0x9fff; op++)
        {
            if ((op & 0xf1c0) is 0x90c0 or 0x91c0 && IsAddressAluSourceEffectiveAddress(op))
                _handlers[op] = AddSubAddress;
        }
        for (int op = 0xb000; op <= 0xbfff; op++)
        {
            int opmode = (op >> 6) & 7;
            if (opmode <= 3 || opmode == 7)
                _handlers[op] = Compare;
            else if (opmode is >= 4 and <= 6 && IsDataAlterableEffectiveAddress(op))
                _handlers[op] = RegisterToEffectiveAddressAlu;
        }
        for (int op = 0xc000; op <= 0xcfff; op++)
        {
            int opmode = (op >> 6) & 7;
            if (opmode <= 2 && IsDataAluSourceEffectiveAddress(op))
                _handlers[op] = DataRegisterAlu;
            else if (opmode is 3 or 7 && IsDataAluSourceEffectiveAddress(op))
                _handlers[op] = MultiplyWord;
            else if (opmode is >= 4 and <= 6 && IsMemoryAlterableEffectiveAddress(op))
                _handlers[op] = RegisterToEffectiveAddressAlu;
        }
        for (int op = 0xc0c0; op <= 0xcfff; op++)
        {
            if ((op & 0xf1c0) is 0xc0c0 or 0xc1c0 && IsDataAluSourceEffectiveAddress(op))
                _handlers[op] = MultiplyWord;
        }
        for (int op = 0xd000; op <= 0xdfff; op++)
        {
            int opmode = (op >> 6) & 7;
            if (opmode <= 2 && IsDataAluSourceEffectiveAddress(op))
                _handlers[op] = DataRegisterAlu;
            else if (opmode is >= 4 and <= 6 && IsMemoryAlterableEffectiveAddress(op))
                _handlers[op] = RegisterToEffectiveAddressAlu;
        }
        for (int op = 0xd0c0; op <= 0xdfff; op++)
        {
            if ((op & 0xf1c0) is 0xd0c0 or 0xd1c0 && IsAddressAluSourceEffectiveAddress(op))
                _handlers[op] = AddSubAddress;
        }
        for (int op = 0xe000; op <= 0xefff; op++)
        {
            if ((op & 0x00c0) != 0x00c0)
                _handlers[op] = ShiftRotateRegister;
        }
        for (int op = 0xa000; op <= 0xafff; op++)
            _handlers[op] = Line1010;
        for (int op = 0xf000; op <= 0xffff; op++)
            _handlers[op] = Line1111;
        for (int op = 0x6000; op <= 0x6fff; op += 0x0100)
        {
            for (int low = 0; low <= 0xff; low++)
                _handlers[op | low] = Bcc;
        }

        _handlers[0x003c] = OriToCcr;
        _handlers[0x007c] = OriToSr;
        _handlers[0x023c] = AndiToCcr;
        _handlers[0x027c] = AndiToSr;
        _handlers[0x0a3c] = EoriToCcr;
        _handlers[0x0a7c] = EoriToSr;

        _handlers[0x4e71] = Nop;
        _handlers[0x4e72] = Stop;
        _handlers[0x4e73] = Rte;
        _handlers[0x4e7a] = Movec;
        _handlers[0x4e7b] = Movec;
        _handlers[0x4e75] = Rts;
        _handlers[0x4e76] = Trapv;
        _handlers[0x4e77] = Rtr;

        for (int op = 0x4a00; op <= 0x4abf; op++)
        {
            if (IsTstEffectiveAddress(op))
                _handlers[op] = Test;
        }
        for (int op = 0x4ac0; op <= 0x4aff; op++)
        {
            if (IsDataAlterableEffectiveAddress(op))
                _handlers[op] = Tas;
        }

        for (int op = 0x4840; op <= 0x4847; op++)
            _handlers[op] = Swap;
        for (int op = 0x4840; op <= 0x487f; op++)
        {
            if (IsControlEffectiveAddress(op))
                _handlers[op] = Pea;
        }
        RegisterBitfieldOpcodes();
        for (int op = 0x4808; op <= 0x480f; op++)
            _handlers[op] = LinkLong;
        for (int op = 0x4880; op <= 0x4887; op++)
            _handlers[op] = ExtWord;
        for (int op = 0x48c0; op <= 0x48c7; op++)
            _handlers[op] = ExtLong;
        for (int op = 0x49c0; op <= 0x49c7; op++)
            _handlers[op] = ExtByteLong;
        for (int op = 0x4880; op <= 0x4cff; op++)
        {
            if ((op & 0xfb80) == 0x4880 && IsMovemEffectiveAddress(op))
                _handlers[op] = Movem;
        }

        for (int op = 0x4e40; op <= 0x4e4f; op++)
            _handlers[op] = Trap;
        for (int op = 0x4e50; op <= 0x4e57; op++)
            _handlers[op] = LinkWord;
        for (int op = 0x4e58; op <= 0x4e5f; op++)
            _handlers[op] = Unlink;
        for (int op = 0x4e80; op <= 0x4ebf; op++)
            _handlers[op] = Jsr;
        for (int op = 0x4ec0; op <= 0x4eff; op++)
            _handlers[op] = Jmp;
        RegisterLongMultiplyDivideOpcodes();
        ImplementedOpcodeCount = CountImplementedHandlers();
        MameEc020OpcodeCount = CountMameEc020Opcodes();
        _ = before;
    }

    private void RegisterLongMultiplyDivideOpcodes()
    {
        for (int op = 0x4c00; op <= 0x4c3f; op++)
        {
            if (IsDataAluSourceEffectiveAddress(op))
                _handlers[op] = MultiplyLong;
        }

        for (int op = 0x4c40; op <= 0x4c7f; op++)
        {
            if (IsDataAluSourceEffectiveAddress(op))
                _handlers[op] = DivideLong;
        }
    }

    private void RegisterBitfieldOpcodes()
    {
        for (int op = 0xe8c0; op <= 0xefff; op++)
        {
            if (!IsBitfieldEffectiveAddress(op))
                continue;

            switch (op & 0xffc0)
            {
                case 0xe8c0:
                    _handlers[op] = BitfieldTest;
                    break;
                case 0xe9c0:
                    _handlers[op] = BitfieldExtractUnsigned;
                    break;
                case 0xeac0:
                    _handlers[op] = BitfieldChange;
                    break;
                case 0xebc0:
                    _handlers[op] = BitfieldExtractSigned;
                    break;
                case 0xecc0:
                    _handlers[op] = BitfieldClear;
                    break;
                case 0xedc0:
                    _handlers[op] = BitfieldFindFirstOne;
                    break;
                case 0xeec0:
                    _handlers[op] = BitfieldSet;
                    break;
                case 0xefc0:
                    _handlers[op] = BitfieldInsert;
                    break;
            }
        }
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

    private void OriToCcr()
    {
        _sr = (ushort)((_sr & 0xff00) | ((_sr | ReadImmediateWord()) & 0x1f));
        _lastCycles = _cycles[_ir];
    }

    private void OriToSr()
    {
        if (!Supervisor)
        {
            PrivilegeViolation();
            return;
        }

        SetSr((ushort)(_sr | ReadImmediateWord()));
        _lastCycles = _cycles[_ir];
    }

    private void AndiToCcr()
    {
        _sr = (ushort)((_sr & 0xff00) | ((_sr & ReadImmediateWord()) & 0x1f));
        _lastCycles = _cycles[_ir];
    }

    private void AndiToSr()
    {
        if (!Supervisor)
        {
            PrivilegeViolation();
            return;
        }

        SetSr((ushort)(_sr & ReadImmediateWord()));
        _lastCycles = _cycles[_ir];
    }

    private void EoriToCcr()
    {
        _sr = (ushort)((_sr & 0xff00) | ((_sr ^ ReadImmediateWord()) & 0x1f));
        _lastCycles = _cycles[_ir];
    }

    private void EoriToSr()
    {
        if (!Supervisor)
        {
            PrivilegeViolation();
            return;
        }

        SetSr((ushort)(_sr ^ ReadImmediateWord()));
        _lastCycles = _cycles[_ir];
    }

    private void AddSubQuick()
    {
        int quick = ((_ir >> 9) & 7) == 0 ? 8 : ((_ir >> 9) & 7);
        bool subtract = (_ir & 0x0100) != 0;
        OpSize size = ((_ir >> 6) & 3) switch
        {
            0 => OpSize.Byte,
            1 => OpSize.Word,
            2 => OpSize.Long,
            _ => throw new InvalidOperationException($"Invalid ADDQ/SUBQ size for opcode 0x{_ir:X4}.")
        };
        int ea = _ir & 0x3f;
        int mode = (ea >> 3) & 7;

        if (mode == 1)
        {
            if (size == OpSize.Byte)
            {
                Illegal();
                return;
            }

            int reg = ea & 7;
            _a[reg] = subtract
                ? unchecked(_a[reg] - (uint)quick)
                : unchecked(_a[reg] + (uint)quick);
            _lastCycles = _cycles[_ir];
            return;
        }

        uint dst = ReadAlterableEaForModify(ea, size, out uint address, out bool writeRegister);
        uint mask = MaskForSize(size);
        uint src = (uint)quick & mask;
        uint result = subtract
            ? unchecked(dst - src)
            : unchecked(dst + src);
        result &= mask;
        WriteAlterableEaForModify(ea, size, result, address, writeRegister);
        SetAddSubFlags(size, src, dst, result, subtract);
        _lastCycles = _cycles[_ir];
    }

    private void Dbcc()
    {
        int condition = (_ir >> 8) & 0x0f;
        int reg = _ir & 7;
        if (CheckCondition(condition))
        {
            _pc = (_pc + 2u);
            _lastCycles = _cycles[_ir];
            return;
        }

        uint extensionPc = _pc;
        ushort counter = (ushort)(_d[reg] - 1u);
        _d[reg] = (_d[reg] & 0xffff_0000u) | counter;
        short displacement = (short)ReadImmediateWord();
        if (counter != 0xffff)
            _pc = unchecked(extensionPc + (uint)displacement);
        _lastCycles = _cycles[_ir];
    }

    private void SetOnCondition()
    {
        int condition = (_ir >> 8) & 0x0f;
        WriteEa(_ir & 0x3f, OpSize.Byte, CheckCondition(condition) ? 0xffu : 0u);
        _lastCycles = _cycles[_ir];
    }

    private void MoveSrToEa()
    {
        if (!Supervisor)
        {
            PrivilegeViolation();
            return;
        }

        WriteEa(_ir & 0x3f, OpSize.Word, _sr);
        _lastCycles = _cycles[_ir];
    }

    private void MoveEaToCcr()
    {
        ushort value = (ushort)ReadEa(_ir & 0x3f, OpSize.Word);
        _sr = (ushort)((_sr & 0xff00) | (value & 0x1f));
        _lastCycles = _cycles[_ir];
    }

    private void MoveEaToSr()
    {
        if (!Supervisor)
        {
            PrivilegeViolation();
            return;
        }

        SetSr((ushort)ReadEa(_ir & 0x3f, OpSize.Word));
        _lastCycles = _cycles[_ir];
    }

    private void Neg()
    {
        OpSize size = ((_ir >> 6) & 3) switch
        {
            0 => OpSize.Byte,
            1 => OpSize.Word,
            2 => OpSize.Long,
            _ => throw new InvalidOperationException($"Invalid NEG size for opcode 0x{_ir:X4}.")
        };

        int ea = _ir & 0x3f;
        uint dst = ReadAlterableEaForModify(ea, size, out uint address, out bool writeRegister);
        uint mask = MaskForSize(size);
        uint result = unchecked(0u - dst) & mask;
        WriteAlterableEaForModify(ea, size, result, address, writeRegister);

        uint sign = SignBitForSize(size);
        dst &= mask;
        N = (result & sign) != 0;
        Z = result == 0;
        V = dst == sign;
        C = dst != 0;
        X = C;
        _lastCycles = _cycles[_ir];
    }

    private void Negx()
    {
        OpSize size = ((_ir >> 6) & 3) switch
        {
            0 => OpSize.Byte,
            1 => OpSize.Word,
            2 => OpSize.Long,
            _ => throw new InvalidOperationException($"Invalid NEGX size for opcode 0x{_ir:X4}.")
        };

        int ea = _ir & 0x3f;
        uint dst = ReadAlterableEaForModify(ea, size, out uint address, out bool writeRegister);
        uint mask = MaskForSize(size);
        uint extend = X ? 1u : 0u;
        uint result = unchecked(0u - dst - extend) & mask;
        WriteAlterableEaForModify(ea, size, result, address, writeRegister);

        uint sign = SignBitForSize(size);
        dst &= mask;
        N = (result & sign) != 0;
        if (result != 0)
            Z = false;
        V = ((dst ^ result) & sign) != 0 && ((0u ^ dst) & sign) == 0;
        C = (dst | result) != 0;
        X = C;
        _lastCycles = _cycles[_ir];
    }

    private void Clear()
    {
        OpSize size = ((_ir >> 6) & 3) switch
        {
            0 => OpSize.Byte,
            1 => OpSize.Word,
            2 => OpSize.Long,
            _ => throw new InvalidOperationException($"Invalid CLR size for opcode 0x{_ir:X4}.")
        };

        int ea = _ir & 0x3f;
        ReadAlterableEaForModify(ea, size, out uint address, out bool writeRegister);
        WriteAlterableEaForModify(ea, size, 0, address, writeRegister);
        N = false;
        Z = true;
        V = false;
        C = false;
        _lastCycles = _cycles[_ir];
    }

    private void Not()
    {
        OpSize size = ((_ir >> 6) & 3) switch
        {
            0 => OpSize.Byte,
            1 => OpSize.Word,
            2 => OpSize.Long,
            _ => throw new InvalidOperationException($"Invalid NOT size for opcode 0x{_ir:X4}.")
        };

        int ea = _ir & 0x3f;
        uint value = ReadAlterableEaForModify(ea, size, out uint address, out bool writeRegister);
        uint result = (~value) & MaskForSize(size);
        WriteAlterableEaForModify(ea, size, result, address, writeRegister);
        SetLogicFlags(size, result);
        _lastCycles = _cycles[_ir];
    }

    private void Test()
    {
        OpSize size = ((_ir >> 6) & 3) switch
        {
            0 => OpSize.Byte,
            1 => OpSize.Word,
            2 => OpSize.Long,
            _ => throw new InvalidOperationException($"Invalid TST size for opcode 0x{_ir:X4}.")
        };

        SetLogicFlags(size, ReadEa(_ir & 0x3f, size));
        _lastCycles = _cycles[_ir];
    }

    private void Tas()
    {
        int ea = _ir & 0x3f;
        uint value = ReadAlterableEaForModify(ea, OpSize.Byte, out uint address, out bool writeRegister);
        SetLogicFlags(OpSize.Byte, value);
        WriteAlterableEaForModify(ea, OpSize.Byte, value | 0x80u, address, writeRegister);
        _lastCycles = _cycles[_ir];
    }

    private void ExtWord()
    {
        int register = _ir & 7;
        ushort value = unchecked((ushort)(short)(sbyte)(_d[register] & 0xff));
        _d[register] = (_d[register] & 0xffff_0000u) | value;
        SetLogicFlags(OpSize.Word, value);
        _lastCycles = _cycles[_ir];
    }

    private void ExtLong()
    {
        int register = _ir & 7;
        uint value = unchecked((uint)(int)(short)(_d[register] & 0xffff));
        _d[register] = value;
        SetLogicFlags(OpSize.Long, value);
        _lastCycles = _cycles[_ir];
    }

    private void ExtByteLong()
    {
        int register = _ir & 7;
        uint value = unchecked((uint)(int)(sbyte)(_d[register] & 0xff));
        _d[register] = value;
        SetLogicFlags(OpSize.Long, value);
        _lastCycles = _cycles[_ir];
    }

    private void DynamicBitOperation()
    {
        ExecuteBitOperation(_d[(_ir >> 9) & 7]);
    }

    private void ImmediateBitOperation()
    {
        ExecuteBitOperation(ReadImmediateWord() & 0xffu);
    }

    private void ExecuteBitOperation(uint bit)
    {
        int operation = (_ir >> 6) & 3;
        int ea = _ir & 0x3f;
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;

        if (mode == 0)
        {
            uint mask = 1u << (int)(bit & 0x1f);
            uint value = _d[reg];
            Z = (value & mask) == 0;

            if (operation == 1)
                _d[reg] = value ^ mask;
            else if (operation == 2)
                _d[reg] = value & ~mask;
            else if (operation == 3)
                _d[reg] = value | mask;
        }
        else
        {
            uint mask = 1u << (int)(bit & 7);
            if (operation == 0)
            {
                uint value = ReadEa(ea, OpSize.Byte);
                Z = (value & mask) == 0;
            }
            else
            {
                uint address = ResolveBitMemoryAddress(ea);
                uint value = ReadByte(address);
                Z = (value & mask) == 0;
                uint result = operation switch
                {
                    1 => value ^ mask,
                    2 => value & ~mask,
                    _ => value | mask
                };
                WriteByte(address, (byte)result);
            }
        }

        _lastCycles = _cycles[_ir];
    }

    private void Movem()
    {
        bool memoryToRegisters = (_ir & 0x0400) != 0;
        OpSize size = (_ir & 0x0040) != 0 ? OpSize.Long : OpSize.Word;
        ushort registerMask = ReadImmediateWord();
        int ea = _ir & 0x3f;
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        uint address = ResolveMovemEa(ea);
        uint step = size == OpSize.Long ? 4u : 2u;
        uint count = 0;

        if (memoryToRegisters)
        {
            for (int i = 0; i < 16; i++)
            {
                if ((registerMask & (1 << i)) == 0)
                    continue;

                uint value = ReadMemory(address, size);
                WriteMovemRegister(i, size == OpSize.Word ? (uint)(int)(short)value : value);
                address = (address + step);
                count++;
            }

            if (mode == 3)
                _a[reg] = address;
        }
        else if (mode == 4)
        {
            for (int i = 0; i < 16; i++)
            {
                if ((registerMask & (1 << i)) == 0)
                    continue;

                address = (address - step);
                WriteMemory(address, size, ReadMovemRegister(15 - i));
                count++;
            }

            _a[reg] = address;
        }
        else
        {
            uint current = address;
            for (int i = 0; i < 16; i++)
            {
                if ((registerMask & (1 << i)) == 0)
                    continue;

                WriteMemory(current, size, ReadMovemRegister(i));
                current = (current + step);
                count++;
            }
        }

        _lastCycles = _cycles[_ir] + count * (size == OpSize.Long ? 8u : 4u);
    }

    private void BitfieldTest()
    {
        ushort extension = ReadImmediateWord();
        if (!TryReadBitfield(extension, out uint aligned, out uint extracted, out _, out int width, out _, out _))
        {
            Illegal();
            return;
        }

        SetBitfieldFlags(aligned, extracted, width);
        _lastCycles = _cycles[_ir];
    }

    private void BitfieldExtractUnsigned()
    {
        ushort extension = ReadImmediateWord();
        if (!TryReadBitfield(extension, out uint aligned, out uint extracted, out _, out int width, out _, out _))
        {
            Illegal();
            return;
        }

        _d[(extension >> 12) & 7] = extracted;
        SetBitfieldFlags(aligned, extracted, width);
        _lastCycles = _cycles[_ir];
    }

    private void BitfieldExtractSigned()
    {
        ushort extension = ReadImmediateWord();
        if (!TryReadBitfield(extension, out uint aligned, out uint extracted, out _, out int width, out _, out _))
        {
            Illegal();
            return;
        }

        _d[(extension >> 12) & 7] = width == 32
            ? extracted
            : unchecked((uint)(((int)aligned) >> (32 - width)));
        SetBitfieldFlags(aligned, extracted, width);
        _lastCycles = _cycles[_ir];
    }

    private void BitfieldFindFirstOne()
    {
        ushort extension = ReadImmediateWord();
        if (!TryReadBitfield(extension, out uint aligned, out uint extracted, out int offset, out int width, out _, out _))
        {
            Illegal();
            return;
        }

        int result = offset;
        for (uint bit = 1u << (width - 1); bit != 0 && (extracted & bit) == 0; bit >>= 1)
            result++;

        _d[(extension >> 12) & 7] = (uint)result;
        SetBitfieldFlags(aligned, extracted, width);
        _lastCycles = _cycles[_ir];
    }

    private void BitfieldChange() => BitfieldModify(setBits: false, insert: false, changeBits: true);

    private void BitfieldClear() => BitfieldModify(setBits: false, insert: false);

    private void BitfieldSet() => BitfieldModify(setBits: true, insert: false);

    private void BitfieldInsert() => BitfieldModify(setBits: false, insert: true);

    private void BitfieldModify(bool setBits, bool insert, bool changeBits = false)
    {
        ushort extension = ReadImmediateWord();
        if (!TryReadBitfield(extension, out uint aligned, out uint extracted, out int offset, out int width, out uint ea, out bool dataRegister))
        {
            Illegal();
            return;
        }

        uint source = insert
            ? width == 32 ? _d[(extension >> 12) & 7] : _d[(extension >> 12) & 7] & ((1u << width) - 1u)
            : changeBits ? (width == 32 ? ~extracted : (~extracted) & ((1u << width) - 1u))
            : setBits ? (width == 32 ? uint.MaxValue : (1u << width) - 1u) : 0u;

        if (dataRegister)
        {
            int reg = _ir & 7;
            uint value = _d[reg];
            for (int bit = 0; bit < width; bit++)
            {
                int destinationBit = 31 - ((offset + bit) & 31);
                uint mask = 1u << destinationBit;
                uint sourceBit = (source >> (width - 1 - bit)) & 1u;
                value = sourceBit == 0 ? value & ~mask : value | mask;
            }

            _d[reg] = value;
        }
        else
        {
            WriteBitfield(ea, offset, width, source);
        }

        SetBitfieldFlags(aligned, insert ? source : extracted, width);
        _lastCycles = _cycles[_ir];
    }

    private void ImmediateOperation()
    {
        OpSize size = ((_ir >> 6) & 3) switch
        {
            0 => OpSize.Byte,
            1 => OpSize.Word,
            2 => OpSize.Long,
            _ => throw new InvalidOperationException($"Invalid immediate operation size for opcode 0x{_ir:X4}.")
        };
        uint source = size switch
        {
            OpSize.Byte => ReadImmediateWord() & 0xffu,
            OpSize.Word => ReadImmediateWord(),
            _ => ReadImmediateLong()
        };
        int ea = _ir & 0x3f;
        uint mask = MaskForSize(size);

        if ((_ir & 0xff00) == 0x0c00)
        {
            uint compareDest = ReadEa(ea, size);
            uint compareResult = unchecked(compareDest - source) & mask;
            SetCompareFlags(size, source, compareDest, compareResult);
            _lastCycles = _cycles[_ir];
            return;
        }

        uint dest = ReadAlterableEaForModify(ea, size, out uint address, out bool writeRegister);
        uint result;

        switch (_ir & 0xff00)
        {
            case 0x0000:
                result = (dest | source) & mask;
                WriteAlterableEaForModify(ea, size, result, address, writeRegister);
                SetLogicFlags(size, result);
                break;
            case 0x0200:
                result = (dest & source) & mask;
                WriteAlterableEaForModify(ea, size, result, address, writeRegister);
                SetLogicFlags(size, result);
                break;
            case 0x0400:
                result = unchecked(dest - source) & mask;
                WriteAlterableEaForModify(ea, size, result, address, writeRegister);
                SetAddSubFlags(size, source, dest, result, subtract: true);
                break;
            case 0x0600:
                result = unchecked(dest + source) & mask;
                WriteAlterableEaForModify(ea, size, result, address, writeRegister);
                SetAddSubFlags(size, source, dest, result, subtract: false);
                break;
            case 0x0a00:
                result = (dest ^ source) & mask;
                WriteAlterableEaForModify(ea, size, result, address, writeRegister);
                SetLogicFlags(size, result);
                break;
            default:
                throw new InvalidOperationException($"Invalid immediate operation opcode 0x{_ir:X4}.");
        }

        _lastCycles = _cycles[_ir];
    }

    private void DataRegisterAlu()
    {
        OpSize size = ((_ir >> 6) & 3) switch
        {
            0 => OpSize.Byte,
            1 => OpSize.Word,
            _ => OpSize.Long
        };
        int register = (_ir >> 9) & 7;
        uint source = ReadEa(_ir & 0x3f, size);
        uint dest = ReadDataRegister(register, size);
        uint mask = MaskForSize(size);
        uint result;

        switch (_ir & 0xf000)
        {
            case 0x8000:
                result = (dest | source) & mask;
                WriteDataRegister(register, size, result);
                SetLogicFlags(size, result);
                break;
            case 0x9000:
                result = unchecked(dest - source) & mask;
                WriteDataRegister(register, size, result);
                SetAddSubFlags(size, source, dest, result, subtract: true);
                break;
            case 0xc000:
                result = (dest & source) & mask;
                WriteDataRegister(register, size, result);
                SetLogicFlags(size, result);
                break;
            case 0xd000:
                result = unchecked(dest + source) & mask;
                WriteDataRegister(register, size, result);
                SetAddSubFlags(size, source, dest, result, subtract: false);
                break;
            default:
                throw new InvalidOperationException($"Invalid data ALU opcode 0x{_ir:X4}.");
        }

        _lastCycles = _cycles[_ir];
    }

    private void DivideWord()
    {
        int register = (_ir >> 9) & 7;
        bool signed = ((_ir >> 6) & 7) == 7;
        uint sourceRaw = ReadEa(_ir & 0x3f, OpSize.Word) & 0xffffu;
        if (sourceRaw == 0)
        {
            ExceptionTrapFormat2(5);
            _lastCycles = 38;
            return;
        }

        if (signed)
        {
            int dividend = unchecked((int)_d[register]);
            int divisor = (short)sourceRaw;
            int quotient = dividend / divisor;
            int remainder = dividend % divisor;
            SetC(false);
            if (quotient < short.MinValue || quotient > short.MaxValue)
            {
                SetV(true);
                _lastCycles = 158;
                return;
            }

            uint quotientWord = (uint)(ushort)quotient;
            _d[register] = ((uint)(ushort)remainder << 16) | quotientWord;
            SetN((quotientWord & 0x8000u) != 0);
            SetZ((quotientWord & 0xffffu) == 0);
            SetV(false);
        }
        else
        {
            uint dividend = _d[register];
            uint quotient = dividend / sourceRaw;
            uint remainder = dividend % sourceRaw;
            SetC(false);
            if (quotient > 0xffffu)
            {
                SetV(true);
                _lastCycles = 140;
                return;
            }

            _d[register] = (remainder << 16) | (quotient & 0xffffu);
            SetN((quotient & 0x8000u) != 0);
            SetZ((quotient & 0xffffu) == 0);
            SetV(false);
        }

        _lastCycles = 140;
    }

    private void RegisterToEffectiveAddressAlu()
    {
        OpSize size = ((_ir >> 6) & 3) switch
        {
            0 => OpSize.Byte,
            1 => OpSize.Word,
            _ => OpSize.Long
        };
        int register = (_ir >> 9) & 7;
        int ea = _ir & 0x3f;
        uint source = ReadDataRegister(register, size);
        uint dest = ReadAlterableEaForModify(ea, size, out uint address, out bool writeRegister);
        uint mask = MaskForSize(size);
        uint result;

        switch (_ir & 0xf000)
        {
            case 0x8000:
                result = (dest | source) & mask;
                WriteAlterableEaForModify(ea, size, result, address, writeRegister);
                SetLogicFlags(size, result);
                break;
            case 0x9000:
                result = unchecked(dest - source) & mask;
                WriteAlterableEaForModify(ea, size, result, address, writeRegister);
                SetAddSubFlags(size, source, dest, result, subtract: true);
                break;
            case 0xb000:
                result = (dest ^ source) & mask;
                WriteAlterableEaForModify(ea, size, result, address, writeRegister);
                SetLogicFlags(size, result);
                break;
            case 0xc000:
                result = (dest & source) & mask;
                WriteAlterableEaForModify(ea, size, result, address, writeRegister);
                SetLogicFlags(size, result);
                break;
            case 0xd000:
                result = unchecked(dest + source) & mask;
                WriteAlterableEaForModify(ea, size, result, address, writeRegister);
                SetAddSubFlags(size, source, dest, result, subtract: false);
                break;
            default:
                throw new InvalidOperationException($"Invalid register-to-EA ALU opcode 0x{_ir:X4}.");
        }

        _lastCycles = _cycles[_ir];
    }

    private void MultiplyLong()
    {
        ushort extension = ReadImmediateWord();
        uint src = ReadEa(_ir & 0x3f, OpSize.Long);
        uint dst = _d[(extension >> 12) & 7];
        bool signed = (extension & 0x0800) != 0;
        bool fullResult = (extension & 0x0400) != 0;

        ulong result = signed
            ? unchecked((ulong)((long)(int)src * (long)(int)dst))
            : (ulong)src * dst;

        C = false;
        if (fullResult)
        {
            _d[extension & 7] = (uint)(result >> 32);
            _d[(extension >> 12) & 7] = (uint)result;
            N = (result & 0x8000_0000_0000_0000UL) != 0;
            Z = result == 0;
            V = false;
            _lastCycles = _cycles[_ir];
            return;
        }

        uint low = (uint)result;
        _d[(extension >> 12) & 7] = low;
        N = (low & 0x8000_0000u) != 0;
        Z = low == 0;
        V = signed
            ? (long)result != (int)low
            : result > uint.MaxValue;
        _lastCycles = _cycles[_ir];
    }

    private void DivideLong()
    {
        ushort extension = ReadImmediateWord();
        uint divisor = ReadEa(_ir & 0x3f, OpSize.Long);
        if (divisor == 0)
        {
            ExceptionTrapFormat2(5);
            _lastCycles = _cycles[_ir];
            return;
        }

        int quotientRegister = (extension >> 12) & 7;
        int remainderRegister = extension & 7;
        bool signed = (extension & 0x0800) != 0;
        bool fullDividend = (extension & 0x0400) != 0;
        ulong quotient;
        ulong remainder;

        if (fullDividend)
        {
            ulong dividend = ((ulong)_d[remainderRegister] << 32) | _d[quotientRegister];
            if (signed)
            {
                long signedDividend = unchecked((long)dividend);
                long signedDivisor = unchecked((int)divisor);
                if (signedDividend == long.MinValue && signedDivisor == -1)
                {
                    V = true;
                    _lastCycles = _cycles[_ir];
                    return;
                }

                long signedQuotient = signedDividend / signedDivisor;
                long signedRemainder = signedDividend % signedDivisor;
                if (signedQuotient != (int)signedQuotient)
                {
                    V = true;
                    _lastCycles = _cycles[_ir];
                    return;
                }

                quotient = unchecked((ulong)signedQuotient);
                remainder = unchecked((ulong)signedRemainder);
            }
            else
            {
                quotient = dividend / divisor;
                if (quotient > uint.MaxValue)
                {
                    V = true;
                    _lastCycles = _cycles[_ir];
                    return;
                }

                remainder = dividend % divisor;
            }
        }
        else
        {
            uint dividend = _d[quotientRegister];
            if (signed)
            {
                long signedDividend = unchecked((int)dividend);
                long signedDivisor = unchecked((int)divisor);
                long signedQuotient = signedDividend / signedDivisor;
                long signedRemainder = signedDividend % signedDivisor;
                if (signedQuotient != (int)signedQuotient)
                {
                    V = true;
                    _lastCycles = _cycles[_ir];
                    return;
                }

                quotient = unchecked((ulong)signedQuotient);
                remainder = unchecked((ulong)signedRemainder);
            }
            else
            {
                quotient = dividend / divisor;
                remainder = dividend % divisor;
            }
        }

        _d[remainderRegister] = (uint)remainder;
        _d[quotientRegister] = (uint)quotient;
        N = ((uint)quotient & 0x8000_0000u) != 0;
        Z = (uint)quotient == 0;
        V = false;
        C = false;
        _lastCycles = _cycles[_ir];
    }

    private void Chk()
    {
        int register = (_ir >> 9) & 7;
        bool longSize = (_ir & 0x0080) == 0;
        if (longSize)
        {
            int source = unchecked((int)_d[register]);
            int bound = unchecked((int)ReadEa(_ir & 0x3f, OpSize.Long));
            Z = source == 0;
            V = false;
            C = false;
            if (source < 0 || source > bound)
            {
                N = source < 0;
                ExceptionTrapFormat2(6);
            }
        }
        else
        {
            int source = unchecked((short)(_d[register] & 0xffff));
            int bound = unchecked((short)ReadEa(_ir & 0x3f, OpSize.Word));
            Z = source == 0;
            V = false;
            C = false;
            if (source < 0 || source > bound)
            {
                N = source < 0;
                ExceptionTrapFormat2(6);
            }
        }

        _lastCycles = _cycles[_ir];
    }

    private void Chk2Cmp2()
    {
        OpSize size = (_ir & 0x0600) switch
        {
            0x0000 => OpSize.Byte,
            0x0200 => OpSize.Word,
            0x0400 => OpSize.Long,
            _ => throw new InvalidOperationException($"Invalid CHK2/CMP2 opcode 0x{_ir:X4}.")
        };

        ushort extension = ReadImmediateWord();
        int compareRegister = (extension >> 12) & 0x0f;
        long compare = unchecked((int)(compareRegister < 8 ? _d[compareRegister] : _a[compareRegister - 8]));
        long lower;
        long upper;

        if (size == OpSize.Long)
        {
            uint ea = ResolveControlEa(_ir & 0x3f);
            lower = unchecked((int)ReadLong(ea));
            upper = unchecked((int)ReadLong(ea + 4u));
        }
        else if (size == OpSize.Word)
        {
            if ((extension & 0x8000) == 0)
                compare &= 0xffff;

            uint ea = ResolveControlEa(_ir & 0x3f);
            ushort lowerRaw = ReadWord(ea);
            ushort upperRaw = ReadWord(ea + 2u);
            lower = lowerRaw;
            upper = upperRaw;
            if ((lowerRaw & 0x8000) != 0)
            {
                lower = unchecked((short)lowerRaw);
                upper = unchecked((short)upperRaw);
                if ((extension & 0x8000) == 0)
                    compare = unchecked((short)compare);
            }
        }
        else
        {
            if ((extension & 0x8000) == 0)
                compare &= 0xff;

            uint ea = ResolveControlEa(_ir & 0x3f);
            byte lowerRaw = ReadByte(ea);
            byte upperRaw = ReadByte(ea + 1u);
            lower = lowerRaw;
            upper = upperRaw;
            if ((lowerRaw & 0x80) != 0)
            {
                lower = unchecked((sbyte)lowerRaw);
                upper = unchecked((sbyte)upperRaw);
                if ((extension & 0x8000) == 0)
                    compare = unchecked((sbyte)compare);
            }
        }

        C = compare < lower || compare > upper;
        Z = compare == lower || compare == upper;
        if (C && (extension & 0x0800) != 0)
            ExceptionTrapFormat2(6);

        _lastCycles = _cycles[_ir];
    }

    private void MultiplyWord()
    {
        int register = (_ir >> 9) & 7;
        bool signed = (_ir & 0x0100) != 0;
        ushort source = (ushort)ReadEa(_ir & 0x3f, OpSize.Word);
        uint result = signed
            ? unchecked((uint)((int)(short)source * (int)(short)(_d[register] & 0xffff)))
            : (uint)(source * (ushort)(_d[register] & 0xffff));

        _d[register] = result;
        SetLogicFlags(OpSize.Long, result);
        _lastCycles = _cycles[_ir];
    }

    private void AddSubAddress()
    {
        int register = (_ir >> 9) & 7;
        bool subtract = (_ir & 0xf000) == 0x9000;
        OpSize size = (_ir & 0x0100) == 0 ? OpSize.Word : OpSize.Long;
        uint source = ReadEa(_ir & 0x3f, size);
        if (size == OpSize.Word)
            source = unchecked((uint)(int)(short)source);

        _a[register] = subtract
            ? unchecked(_a[register] - source)
            : unchecked(_a[register] + source);
        _lastCycles = _cycles[_ir];
    }

    private void Move()
    {
        int sizeCode = (_ir >> 12) & 3;
        OpSize size = sizeCode switch
        {
            1 => OpSize.Byte,
            2 => OpSize.Long,
            3 => OpSize.Word,
            _ => throw new InvalidOperationException($"Invalid MOVE size for opcode 0x{_ir:X4}.")
        };
        int sourceEa = _ir & 0x3f;
        int destMode = (_ir >> 6) & 7;
        int destReg = (_ir >> 9) & 7;
        int destEa = (destMode << 3) | destReg;

        uint value = ReadEa(sourceEa, size);
        if (destMode == 1)
        {
            if (size == OpSize.Byte)
            {
                Illegal();
                return;
            }

            _a[destReg] = size == OpSize.Word ? unchecked((uint)(int)(short)value) : value;
            _lastCycles = _cycles[_ir];
            return;
        }

        WriteEa(destEa, size, value);
        SetN(size switch
        {
            OpSize.Byte => (value & 0x80) != 0,
            OpSize.Word => (value & 0x8000) != 0,
            _ => (value & 0x8000_0000u) != 0
        });
        SetZ((value & MaskForSize(size)) == 0);
        SetV(false);
        SetC(false);
        _lastCycles = _cycles[_ir];
    }

    private void Compare()
    {
        int opmode = (_ir >> 6) & 7;
        int destReg = (_ir >> 9) & 7;
        int sourceEa = _ir & 0x3f;

        if (opmode == 3 || opmode == 7)
        {
            OpSize size = opmode == 3 ? OpSize.Word : OpSize.Long;
            uint rawSource = ReadEa(sourceEa, size);
            uint source = size == OpSize.Word ? unchecked((uint)(int)(short)rawSource) : rawSource;
            uint dest = _a[destReg];
            SetCompareFlags(OpSize.Long, source, dest, unchecked(dest - source));
            _lastCycles = _cycles[_ir];
            return;
        }

        OpSize compareSize = opmode switch
        {
            0 => OpSize.Byte,
            1 => OpSize.Word,
            2 => OpSize.Long,
            _ => throw new InvalidOperationException($"Invalid CMP size for opcode 0x{_ir:X4}.")
        };
        uint src = ReadEa(sourceEa, compareSize);
        uint dst = ReadDataRegister(destReg, compareSize);
        SetCompareFlags(compareSize, src, dst, unchecked(dst - src));
        _lastCycles = _cycles[_ir];
    }

    private void ShiftRotateRegister()
    {
        int reg = _ir & 7;
        int operation = (_ir >> 3) & 3;
        bool registerCount = (_ir & 0x0020) != 0;
        OpSize size = ((_ir >> 6) & 3) switch
        {
            0 => OpSize.Byte,
            1 => OpSize.Word,
            2 => OpSize.Long,
            _ => throw new InvalidOperationException($"Invalid shift size for opcode 0x{_ir:X4}.")
        };
        bool left = (_ir & 0x0100) != 0;
        int count = registerCount
            ? (int)(_d[(_ir >> 9) & 7] & 0x3f)
            : (((_ir >> 9) & 7) == 0 ? 8 : ((_ir >> 9) & 7));

        uint mask = MaskForSize(size);
        int bits = size switch
        {
            OpSize.Byte => 8,
            OpSize.Word => 16,
            _ => 32
        };
        uint value = _d[reg] & mask;
        uint result = value;
        bool carry = false;
        bool overflow = false;

        if (count != 0)
        {
            switch (operation)
            {
                case 0:
                    result = left
                        ? ArithmeticShiftLeft(value, count, bits, out carry, out overflow)
                        : ArithmeticShiftRight(value, count, bits, out carry);
                    X = carry;
                    break;
                case 1:
                    result = left
                        ? LogicalShiftLeft(value, count, bits, out carry)
                        : LogicalShiftRight(value, count, bits, out carry);
                    X = carry;
                    break;
                case 2:
                    result = left
                        ? RotateWithExtendLeft(value, count, bits, X, out carry)
                        : RotateWithExtendRight(value, count, bits, X, out carry);
                    X = carry;
                    break;
                case 3:
                    result = left
                        ? RotateLeft(value, count, bits, out carry)
                        : RotateRight(value, count, bits, out carry);
                    break;
            }
        }
        else if (operation == 2)
        {
            carry = X;
        }

        result &= mask;
        WriteDataRegister(reg, size, result);
        N = (result & SignBitForSize(size)) != 0;
        Z = result == 0;
        V = operation == 0 && left && overflow;
        C = carry;
        _lastCycles = _cycles[_ir] + (uint)(count * 2);
    }

    private void Swap()
    {
        int register = _ir & 7;
        uint value = _d[register];
        value = (value >> 16) | (value << 16);
        _d[register] = value;
        SetN((value & 0x8000_0000u) != 0);
        SetZ(value == 0);
        SetV(false);
        SetC(false);
        _lastCycles = _cycles[_ir];
    }

    private void Lea()
    {
        int register = (_ir >> 9) & 7;
        _a[register] = ResolveControlEa(_ir & 0x3f);
        _lastCycles = _cycles[_ir];
    }

    private void Bcc()
    {
        int condition = (_ir >> 8) & 0x0f;
        int displacement = unchecked((sbyte)(_ir & 0xff));
        uint branchBase = _pc;
        if ((_ir & 0xff) == 0)
        {
            displacement = unchecked((short)ReadOpcodeWord(_pc));
            _pc = (_pc + 2u);
            branchBase = (_pc - 2u);
        }
        else if ((_ir & 0xff) == 0xff)
        {
            displacement = unchecked((int)ReadOpcodeLong(_pc));
            _pc = (_pc + 4u);
            branchBase = (_pc - 4u);
        }

        if (condition == 1)
        {
            PushLong(_pc);
            _pc = unchecked(branchBase + (uint)displacement);
            _lastCycles = 7;
            return;
        }

        if (CheckCondition(condition))
        {
            _pc = unchecked(branchBase + (uint)displacement);
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
            PrivilegeViolation();
            return;
        }

        SetSr(ReadOpcodeWord(_pc));
        _pc = (_pc + 2u);
        _stopped = true;
        _lastCycles = 4;
    }

    private void Rte()
    {
        if (!Supervisor)
        {
            PrivilegeViolation();
            return;
        }

        for (int i = 0; i < 8; i++)
        {
            uint sp = _a[7];
            ushort sr = ReadWord(sp);
            uint pc = ReadLong(sp + 2u);
            ushort formatWord = ReadWord(sp + 6u);
            uint format = (uint)(formatWord >> 12);
            RteCount++;
            LastRteStackPointer = sp;
            LastRteStatusRegister = sr;
            LastRteProgramCounter = pc;
            LastRteFormatWord = formatWord;
            if (pc < 0x100 || pc >= 0x0020_0000u || pc == uint.MaxValue)
            {
                if (SuspiciousRteCount == 0)
                {
                    FirstSuspiciousRteStackPointer = sp;
                    FirstSuspiciousRteInstructionPc = _ppc;
                    FirstSuspiciousRteOpcode = _ir;
                    FirstSuspiciousRteStatusRegister = sr;
                    FirstSuspiciousRteProgramCounter = pc;
                    FirstSuspiciousRteFormatWord = formatWord;
                }

                SuspiciousRteCount++;
            }

            if (format == 1)
            {
                _a[7] = (sp + 8u);
                SetSrNoInterrupt(sr);
                continue;
            }

            uint frameSize = format switch
            {
                0 => 8u,
                2 => 12u,
                7 => 60u,
                0x0a => 32u,
                0x0b => 92u,
                _ => 0u
            };
            if (frameSize == 0)
            {
                if (FormatErrorCount == 0)
                {
                    FirstFormatErrorPc = _ppc;
                    FirstFormatErrorOpcode = _ir;
                    FirstFormatErrorStatusRegister = sr;
                    FirstFormatErrorFrameWord = formatWord;
                }

                FormatErrorCount++;
                ExceptionVector(ExceptionFormatError, _pc);
                _lastCycles = 44;
                return;
            }

            _a[7] = (sp + frameSize);
            _pc = pc;
            SetSr(sr);
            _lastCycles = 20;
            return;
        }
    }

    private void Movec()
    {
        if (!Supervisor)
        {
            PrivilegeViolation();
            _lastCycles = 34;
            return;
        }

        ushort extension = ReadImmediateWord();
        int register = (extension >> 12) & 15;
        ushort controlRegister = (ushort)(extension & 0x0fff);

        if (_ir == 0x4e7a)
        {
            if (!TryReadControlRegister(controlRegister, out uint value))
            {
                Illegal();
                return;
            }

            SetMovecRegister(register, value);
        }
        else
        {
            uint value = GetMovecRegister(register);
            if (!TryWriteControlRegister(controlRegister, value))
            {
                Illegal();
                return;
            }
        }

        _lastCycles = 12;
    }

    private void Rts()
    {
        _pc = PopLong();
        _lastCycles = 16;
    }

    private void Pea()
    {
        PushLong(ResolveControlEa(_ir & 0x3f));
        _lastCycles = _cycles[_ir];
    }

    private void LinkWord()
    {
        int register = _ir & 7;
        PushLong(_a[register]);
        _a[register] = _a[7];
        _a[7] = unchecked(_a[7] + (uint)(int)(short)ReadImmediateWord());
        _lastCycles = _cycles[_ir];
    }

    private void LinkLong()
    {
        int register = _ir & 7;
        PushLong(_a[register]);
        _a[register] = _a[7];
        _a[7] = unchecked(_a[7] + ReadImmediateLong());
        _lastCycles = _cycles[_ir];
    }

    private void Unlink()
    {
        int register = _ir & 7;
        _a[7] = _a[register];
        _a[register] = PopLong();
        _lastCycles = _cycles[_ir];
    }

    private void Trapv()
    {
        if (V)
            ExceptionTrapFormat2(7);
        _lastCycles = V ? 34u : 4u;
    }

    private void Rtr()
    {
        ushort ccr = PopWord();
        _pc = PopLong();
        _sr = (ushort)((_sr & 0xff00) | (ccr & 0x1f));
        _lastCycles = 20;
    }

    private void Trap()
    {
        uint vector = ExceptionTrapBase + (uint)(_ir & 0x0f);
        ExceptionVector(vector, _pc);
        _lastCycles = 38;
    }

    private void PrivilegeViolation()
    {
        ExceptionVector(8, _ppc);
        _lastCycles = 34;
    }

    private void Jsr()
    {
        uint target = ResolveControlEa(_ir & 0x3f);
        PushLong(_pc);
        _pc = target;
        _lastCycles = 16;
    }

    private void Jmp()
    {
        _pc = ResolveControlEa(_ir & 0x3f);
        _lastCycles = 10;
    }

    private void Illegal()
    {
        if (IllegalInstructionCount == 0)
        {
            FirstIllegalInstructionPc = _ppc;
            FirstIllegalInstructionOpcode = _ir;
            FirstIllegalInstructionStatusRegister = _sr;
        }

        IllegalInstructionCount++;
        ExceptionVector(4, _ppc);
        _lastCycles = 34;
    }

    private void Line1010()
    {
        ExceptionVector(10, _ppc);
        _lastCycles = 20;
    }

    private void Line1111()
    {
        ExceptionVector(11, _ppc);
        _lastCycles = 20;
    }

    private uint ResolveControlEa(int ea)
    {
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode switch
        {
            2 => _a[reg],
            5 => unchecked(_a[reg] + (uint)(int)(short)ReadImmediateWord()),
            6 => GetIndexedAddress(_a[reg]),
            7 when reg == 0 => unchecked((uint)(int)(short)ReadImmediateWord()),
            7 when reg == 1 => ReadImmediateLong(),
            7 when reg == 2 => GetPcDisplacementAddress(),
            7 when reg == 3 => GetPcIndexedAddress(),
            _ => throw new InvalidOperationException($"Unsupported EC020 control EA mode {mode}/{reg} for opcode 0x{_ir:X4}.")
        };
    }

    private static bool IsMovemEffectiveAddress(int opcode)
    {
        bool memoryToRegisters = (opcode & 0x0400) != 0;
        int ea = opcode & 0x3f;
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        if (memoryToRegisters)
            return mode == 2 || mode == 3 || mode == 5 || mode == 6 || (mode == 7 && reg <= 3);
        return mode == 2 || mode == 4 || mode == 5 || mode == 6 || (mode == 7 && reg <= 1);
    }

    private static bool IsDynamicBitEffectiveAddress(int opcode)
    {
        int operation = (opcode >> 6) & 3;
        int ea = opcode & 0x3f;
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode != 1 && (mode < 7 || (operation == 0 ? reg <= 4 : reg <= 1));
    }

    private static bool IsImmediateBitEffectiveAddress(int opcode)
    {
        int operation = (opcode >> 6) & 3;
        int ea = opcode & 0x3f;
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode != 1 && (mode < 7 || (operation == 0 ? reg <= 3 : reg <= 1));
    }

    private static bool IsDataAluSourceEffectiveAddress(int opcode)
    {
        int ea = opcode & 0x3f;
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode != 1 && (mode < 7 || reg <= 4);
    }

    private static bool IsMemoryAlterableEffectiveAddress(int opcode)
    {
        int ea = opcode & 0x3f;
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode is >= 2 and <= 6 || (mode == 7 && reg <= 1);
    }

    private static bool IsControlEffectiveAddress(int opcode)
    {
        int ea = opcode & 0x3f;
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode is 2 or 5 or 6 || (mode == 7 && reg <= 3);
    }

    private static bool IsChk2Cmp2Opcode(int opcode)
    {
        if ((opcode & 0xf9c0) != 0x00c0)
            return false;

        int sizeCode = (opcode >> 9) & 3;
        return sizeCode <= 2 && IsControlEffectiveAddress(opcode);
    }

    private static bool IsDataAlterableEffectiveAddress(int opcode)
    {
        int ea = opcode & 0x3f;
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode == 0 || mode is >= 2 and <= 6 || (mode == 7 && reg <= 1);
    }

    private static bool IsAddressAluSourceEffectiveAddress(int opcode)
    {
        int ea = opcode & 0x3f;
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode < 7 || reg <= 4;
    }

    private static bool IsUnaryDataAlterableEffectiveAddress(int opcode)
    {
        if (((opcode >> 6) & 3) == 3)
            return false;

        int ea = opcode & 0x3f;
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode != 1 && (mode < 7 || reg <= 1);
    }

    private static bool IsTstEffectiveAddress(int opcode)
    {
        if (((opcode >> 6) & 3) == 3)
            return false;

        int ea = opcode & 0x3f;
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode != 1 && (mode < 7 || reg <= 4);
    }

    private static bool IsChkEffectiveAddress(int opcode)
    {
        int ea = opcode & 0x3f;
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode != 1 && (mode < 7 || reg <= 4);
    }

    private static bool IsBitfieldEffectiveAddress(int opcode)
    {
        int mode = (opcode >> 3) & 7;
        int reg = opcode & 7;
        return mode == 0 || mode == 2 || mode == 5 || mode == 6 || (mode == 7 && reg <= 1);
    }

    private bool TryReadBitfield(
        ushort extension,
        out uint aligned,
        out uint extracted,
        out int offset,
        out int width,
        out uint ea,
        out bool dataRegister)
    {
        aligned = 0;
        extracted = 0;
        ea = 0;
        dataRegister = false;
        offset = (extension & 0x0800) != 0
            ? unchecked((int)_d[(extension >> 6) & 7])
            : (extension >> 6) & 31;
        uint widthRaw = (extension & 0x0020) != 0
            ? _d[extension & 7]
            : (uint)(extension & 31);
        width = (int)(((widthRaw - 1) & 31) + 1);

        int mode = (_ir >> 3) & 7;
        int reg = _ir & 7;
        if (mode == 0)
        {
            dataRegister = true;
            offset &= 31;
            aligned = RotateLeft32(_d[reg], offset);
            extracted = width == 32 ? aligned : aligned >> (32 - width);
            return true;
        }

        if (!TryResolveBitfieldEa(mode, reg, out ea))
            return false;

        ea = unchecked(ea + (uint)Math.DivRem(offset, 8, out int bitOffset));
        if (bitOffset < 0)
        {
            bitOffset += 8;
            ea--;
        }

        offset = bitOffset;
        aligned = ReadBitfieldWindow(ea, offset, width);
        extracted = width == 32 ? aligned : aligned >> (32 - width);
        return true;
    }

    private bool TryResolveBitfieldEa(int mode, int reg, out uint ea)
    {
        ea = 0;
        switch (mode)
        {
            case 2:
                ea = _a[reg];
                return true;
            case 5:
                ea = unchecked(_a[reg] + (uint)(int)(short)ReadImmediateWord());
                return true;
            case 6:
                ea = GetIndexedAddress(_a[reg]);
                return true;
            case 7 when reg == 0:
                ea = unchecked((uint)(int)(short)ReadImmediateWord());
                return true;
            case 7 when reg == 1:
                ea = ReadImmediateLong();
                return true;
            default:
                return false;
        }
    }

    private uint ResolveMovemEa(int ea)
    {
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode switch
        {
            2 or 3 or 4 => _a[reg],
            5 => unchecked(_a[reg] + (uint)(int)(short)ReadImmediateWord()),
            6 => GetIndexedAddress(_a[reg]),
            7 when reg == 0 => unchecked((uint)(int)(short)ReadImmediateWord()),
            7 when reg == 1 => ReadImmediateLong(),
            7 when reg == 2 => GetPcDisplacementAddress(),
            7 when reg == 3 => GetPcIndexedAddress(),
            _ => throw new InvalidOperationException($"Unsupported MOVEM EA mode {mode}/{reg} for opcode 0x{_ir:X4}.")
        };
    }

    private uint ResolveBitMemoryAddress(int ea)
    {
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        switch (mode)
        {
            case 2:
                return _a[reg];
            case 3:
            {
                uint address = _a[reg];
                _a[reg] = (_a[reg] + EaStep(reg, OpSize.Byte));
                return address;
            }
            case 4:
                _a[reg] = (_a[reg] - EaStep(reg, OpSize.Byte));
                return _a[reg];
            case 5:
                return unchecked(_a[reg] + (uint)(int)(short)ReadImmediateWord());
            case 6:
                return GetIndexedAddress(_a[reg]);
            case 7 when reg == 0:
                return unchecked((uint)(int)(short)ReadImmediateWord());
            case 7 when reg == 1:
                return ReadImmediateLong();
            default:
                throw new InvalidOperationException($"Unsupported bit write EA mode {mode}/{reg} for opcode 0x{_ir:X4}.");
        }
    }

    private uint ReadEa(int ea, OpSize size)
    {
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        return mode switch
        {
            0 => ReadDataRegister(reg, size),
            1 => _a[reg],
            2 => ReadMemory(_a[reg], size),
            3 => ReadPostincrement(reg, size),
            4 => ReadPredecrement(reg, size),
            5 => ReadMemory(unchecked(_a[reg] + (uint)(int)(short)ReadImmediateWord()), size),
            6 => ReadMemory(GetIndexedAddress(_a[reg]), size),
            7 when reg == 0 => ReadMemory(unchecked((uint)(int)(short)ReadImmediateWord()), size),
            7 when reg == 1 => ReadMemory(ReadImmediateLong(), size),
            7 when reg == 2 => ReadMemory(GetPcDisplacementAddress(), size),
            7 when reg == 3 => ReadMemory(GetPcIndexedAddress(), size),
            7 when reg == 4 => size switch
            {
                OpSize.Byte => ReadImmediateWord() & 0xffu,
                OpSize.Word => ReadImmediateWord(),
                _ => ReadImmediateLong()
            },
            _ => throw new InvalidOperationException($"Unsupported read EA mode {mode}/{reg} for opcode 0x{_ir:X4}.")
        };
    }

    private void WriteEa(int ea, OpSize size, uint value)
    {
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        switch (mode)
        {
            case 0:
                WriteDataRegister(reg, size, value);
                break;
            case 2:
                WriteMemory(_a[reg], size, value);
                break;
            case 3:
                WriteMemory(_a[reg], size, value);
                _a[reg] = (_a[reg] + EaStep(reg, size));
                break;
            case 4:
                _a[reg] = (_a[reg] - EaStep(reg, size));
                WriteMemory(_a[reg], size, value);
                break;
            case 5:
                WriteMemory(unchecked(_a[reg] + (uint)(int)(short)ReadImmediateWord()), size, value);
                break;
            case 6:
                WriteMemory(GetIndexedAddress(_a[reg]), size, value);
                break;
            case 7 when reg == 0:
                WriteMemory(unchecked((uint)(int)(short)ReadImmediateWord()), size, value);
                break;
            case 7 when reg == 1:
                WriteMemory(ReadImmediateLong(), size, value);
                break;
            default:
                throw new InvalidOperationException($"Unsupported write EA mode {mode}/{reg} for opcode 0x{_ir:X4}.");
        }
    }

    private uint ReadAlterableEaForModify(int ea, OpSize size, out uint address, out bool writeRegister)
    {
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        writeRegister = mode == 0;
        address = 0;

        if (writeRegister)
            return ReadDataRegister(reg, size);

        address = mode switch
        {
            2 => _a[reg],
            3 => _a[reg],
            4 => (_a[reg] - EaStep(reg, size)),
            5 => unchecked(_a[reg] + (uint)(int)(short)ReadImmediateWord()),
            6 => GetIndexedAddress(_a[reg]),
            7 when reg == 0 => unchecked((uint)(int)(short)ReadImmediateWord()),
            7 when reg == 1 => ReadImmediateLong(),
            _ => throw new InvalidOperationException($"Unsupported modify EA mode {mode}/{reg} for opcode 0x{_ir:X4}.")
        };

        if (mode == 4)
            _a[reg] = address;

        return ReadMemory(address, size);
    }

    private void WriteAlterableEaForModify(int ea, OpSize size, uint value, uint address, bool writeRegister)
    {
        int mode = (ea >> 3) & 7;
        int reg = ea & 7;
        if (writeRegister)
        {
            WriteDataRegister(reg, size, value);
            return;
        }

        WriteMemory(address, size, value);
        if (mode == 3)
            _a[reg] = (_a[reg] + EaStep(reg, size));
    }

    private uint ReadDataRegister(int reg, OpSize size)
        => _d[reg] & MaskForSize(size);

    private void WriteDataRegister(int reg, OpSize size, uint value)
    {
        _d[reg] = size switch
        {
            OpSize.Byte => (_d[reg] & 0xffff_ff00u) | (value & 0xffu),
            OpSize.Word => (_d[reg] & 0xffff_0000u) | (value & 0xffffu),
            _ => value
        };
    }

    private uint ReadMovemRegister(int index)
        => index < 8 ? _d[index] : _a[index - 8];

    private void WriteMovemRegister(int index, uint value)
    {
        if (index < 8)
            _d[index] = value;
        else
            _a[index - 8] = value;
    }

    private uint ReadPostincrement(int reg, OpSize size)
    {
        uint address = _a[reg];
        uint value = ReadMemory(address, size);
        _a[reg] = (address + EaStep(reg, size));
        return value;
    }

    private uint ReadPredecrement(int reg, OpSize size)
    {
        _a[reg] = (_a[reg] - EaStep(reg, size));
        return ReadMemory(_a[reg], size);
    }

    private uint ReadMemory(uint address, OpSize size) => size switch
    {
        OpSize.Byte => ReadByte(address),
        OpSize.Word => ReadWord(address),
        _ => ReadLong(address)
    };

    private void WriteMemory(uint address, OpSize size, uint value)
    {
        switch (size)
        {
            case OpSize.Byte:
                WriteByte(address, (byte)value);
                break;
            case OpSize.Word:
                WriteWord(address, (ushort)value);
                break;
            default:
                WriteLong(address, value);
                break;
        }
    }

    private static uint EaStep(int reg, OpSize size)
        => size == OpSize.Byte && reg == 7 ? 2u : size switch
        {
            OpSize.Byte => 1u,
            OpSize.Word => 2u,
            _ => 4u
        };

    private static uint MaskForSize(OpSize size) => size switch
    {
        OpSize.Byte => 0xffu,
        OpSize.Word => 0xffffu,
        _ => 0xffff_ffffu
    };

    private static uint SignBitForSize(OpSize size) => size switch
    {
        OpSize.Byte => 0x80u,
        OpSize.Word => 0x8000u,
        _ => 0x8000_0000u
    };

    private static uint LogicalShiftRight(uint value, int count, int bits, out bool carry)
    {
        if (count >= bits)
        {
            carry = count == bits && (value & (1u << (bits - 1))) != 0;
            return 0;
        }

        carry = ((value >> (count - 1)) & 1u) != 0;
        return value >> count;
    }

    private static uint LogicalShiftLeft(uint value, int count, int bits, out bool carry)
    {
        uint mask = bits == 32 ? 0xffff_ffffu : (1u << bits) - 1u;
        if (count >= bits)
        {
            carry = count == bits && (value & 1u) != 0;
            return 0;
        }

        carry = ((value >> (bits - count)) & 1u) != 0;
        return (value << count) & mask;
    }

    private static uint ArithmeticShiftRight(uint value, int count, int bits, out bool carry)
    {
        uint mask = bits == 32 ? 0xffff_ffffu : (1u << bits) - 1u;
        uint sign = 1u << (bits - 1);
        if (count >= bits)
        {
            carry = (value & sign) != 0;
            return carry ? mask : 0;
        }

        carry = ((value >> (count - 1)) & 1u) != 0;
        if ((value & sign) == 0)
            return value >> count;

        uint fill = mask << (bits - count);
        return ((value >> count) | fill) & mask;
    }

    private static uint ArithmeticShiftLeft(uint value, int count, int bits, out bool carry, out bool overflow)
    {
        uint mask = bits == 32 ? 0xffff_ffffu : (1u << bits) - 1u;
        uint sign = 1u << (bits - 1);
        uint result = value & mask;
        carry = false;
        overflow = false;
        for (int i = 0; i < count; i++)
        {
            bool before = (result & sign) != 0;
            carry = before;
            result = (result << 1) & mask;
            overflow |= before != ((result & sign) != 0);
        }

        return result;
    }

    private static uint RotateRight(uint value, int count, int bits, out bool carry)
    {
        uint mask = bits == 32 ? 0xffff_ffffu : (1u << bits) - 1u;
        count %= bits;
        value &= mask;
        if (count == 0)
        {
            carry = (value & (1u << (bits - 1))) != 0;
            return value;
        }

        uint result = ((value >> count) | (value << (bits - count))) & mask;
        carry = (result & (1u << (bits - 1))) != 0;
        return result;
    }

    private static uint RotateLeft(uint value, int count, int bits, out bool carry)
    {
        uint mask = bits == 32 ? 0xffff_ffffu : (1u << bits) - 1u;
        count %= bits;
        value &= mask;
        if (count == 0)
        {
            carry = (value & 1u) != 0;
            return value;
        }

        uint result = ((value << count) | (value >> (bits - count))) & mask;
        carry = (result & 1u) != 0;
        return result;
    }

    private uint ReadBitfieldWindow(uint ea, int offset, int width)
    {
        uint data = offset + width < 8
            ? (uint)ReadByte(ea) << 24
            : offset + width < 16
                ? (uint)ReadWord(ea) << 16
                : ReadLong(ea);
        data = unchecked(data << offset);
        if (offset + width > 32)
            data |= (uint)(ReadByte(ea + 4u) << offset) >> 8;
        return data;
    }

    private void WriteBitfield(uint ea, int offset, int width, uint source)
    {
        uint insertBase = width == 32 ? source : source << (32 - width);
        uint maskBase = width == 32 ? uint.MaxValue : uint.MaxValue << (32 - width);
        uint maskLong = maskBase >> offset;
        uint insertLong = insertBase >> offset;
        uint dataLong = offset + width <= 8
            ? (uint)ReadByte(ea) << 24
            : offset + width <= 16
                ? (uint)ReadWord(ea) << 16
                : ReadLong(ea);
        uint mergedLong = (dataLong & ~maskLong) | insertLong;

        if (offset + width <= 8)
            WriteByte(ea, (byte)(mergedLong >> 24));
        else if (offset + width <= 16)
            WriteWord(ea, (ushort)(mergedLong >> 16));
        else
            WriteLong(ea, mergedLong);

        if (offset + width > 32)
        {
            byte maskByte = (byte)((byte)maskBase << (8 - offset));
            byte insertByte = (byte)((byte)insertBase << (8 - offset));
            byte dataByte = ReadByte(ea + 4u);
            WriteByte(ea + 4u, (byte)((dataByte & ~maskByte) | insertByte));
        }
    }

    private void SetBitfieldFlags(uint aligned, uint extracted, int width)
    {
        N = (aligned & 0x8000_0000u) != 0;
        Z = (width == 32 ? extracted : extracted & ((1u << width) - 1u)) == 0;
        V = false;
        C = false;
    }

    private static uint RotateLeft32(uint value, int count)
    {
        count &= 31;
        return count == 0 ? value : (value << count) | (value >> (32 - count));
    }

    private static uint RotateWithExtendRight(uint value, int count, int bits, bool extend, out bool carry)
    {
        uint mask = bits == 32 ? 0xffff_ffffu : (1u << bits) - 1u;
        uint result = value & mask;
        carry = extend;
        int period = bits + 1;
        count %= period;
        for (int i = 0; i < count; i++)
        {
            bool nextExtend = (result & 1u) != 0;
            result >>= 1;
            if (carry)
                result |= 1u << (bits - 1);
            carry = nextExtend;
        }

        return result & mask;
    }

    private static uint RotateWithExtendLeft(uint value, int count, int bits, bool extend, out bool carry)
    {
        uint mask = bits == 32 ? 0xffff_ffffu : (1u << bits) - 1u;
        uint result = value & mask;
        carry = extend;
        int period = bits + 1;
        count %= period;
        for (int i = 0; i < count; i++)
        {
            bool nextExtend = (result & (1u << (bits - 1))) != 0;
            result = (result << 1) & mask;
            if (carry)
                result |= 1u;
            carry = nextExtend;
        }

        return result & mask;
    }

    private void SetAddSubFlags(OpSize size, uint src, uint dst, uint result, bool subtract)
    {
        uint sign = size switch
        {
            OpSize.Byte => 0x80u,
            OpSize.Word => 0x8000u,
            _ => 0x8000_0000u
        };
        uint mask = MaskForSize(size);
        src &= mask;
        dst &= mask;
        result &= mask;

        N = (result & sign) != 0;
        Z = result == 0;
        if (subtract)
        {
            V = ((dst ^ src) & (dst ^ result) & sign) != 0;
            X = C = src > dst;
        }
        else
        {
            V = (~(dst ^ src) & (result ^ dst) & sign) != 0;
            X = C = result < dst;
        }
    }

    private void SetLogicFlags(OpSize size, uint result)
    {
        uint sign = size switch
        {
            OpSize.Byte => 0x80u,
            OpSize.Word => 0x8000u,
            _ => 0x8000_0000u
        };
        result &= MaskForSize(size);
        N = (result & sign) != 0;
        Z = result == 0;
        V = false;
        C = false;
    }

    private void SetCompareFlags(OpSize size, uint src, uint dst, uint result)
    {
        uint sign = size switch
        {
            OpSize.Byte => 0x80u,
            OpSize.Word => 0x8000u,
            _ => 0x8000_0000u
        };
        uint mask = MaskForSize(size);
        src &= mask;
        dst &= mask;
        result &= mask;

        N = (result & sign) != 0;
        Z = result == 0;
        V = ((dst ^ src) & (dst ^ result) & sign) != 0;
        C = src > dst;
    }

    private uint GetIndexedAddress(uint baseAddress)
    {
        ushort extension = ReadImmediateWord();
        if ((extension & 0x0100) != 0)
            return GetFullIndexedAddress(baseAddress, extension);

        int indexReg = (extension >> 12) & 7;
        bool addressIndex = (extension & 0x8000) != 0;
        bool longIndex = (extension & 0x0800) != 0;
        int scale = 1 << ((extension >> 9) & 3);
        int displacement = unchecked((sbyte)(extension & 0xff));
        uint raw = addressIndex ? _a[indexReg] : _d[indexReg];
        int index = longIndex ? unchecked((int)raw) : unchecked((short)raw);
        uint address = unchecked(baseAddress + (uint)(index * scale + displacement));
        if (_ppc == 0x0022d4)
        {
            LastRestoreIndexedPc = _ppc;
            LastRestoreIndexedExtension = extension;
            LastRestoreIndexedBase = baseAddress;
            LastRestoreIndexedRawIndex = raw;
            LastRestoreIndexedIndex = index;
            LastRestoreIndexedAddress = address;
            LastRestoreIndexedValue = ReadLong(address);
        }

        return address;
    }

    private uint GetFullIndexedAddress(uint baseAddress, ushort extension)
    {
        uint baseValue = (extension & 0x0080) != 0 ? 0 : baseAddress;
        uint indexValue = 0;
        if ((extension & 0x0040) == 0)
        {
            int indexReg = (extension >> 12) & 7;
            bool addressIndex = (extension & 0x8000) != 0;
            bool longIndex = (extension & 0x0800) != 0;
            int scaleShift = (extension >> 9) & 3;
            uint raw = addressIndex ? _a[indexReg] : _d[indexReg];
            indexValue = longIndex
                ? unchecked(raw << scaleShift)
                : unchecked((uint)((int)(short)raw << scaleShift));
        }

        uint baseDisplacement = 0;
        if ((extension & 0x0020) != 0)
        {
            baseDisplacement = (extension & 0x0010) != 0
                ? ReadImmediateLong()
                : unchecked((uint)(int)(short)ReadImmediateWord());
        }

        int indirectSelection = extension & 0x0007;
        uint baseAndDisplacement = unchecked(baseValue + baseDisplacement);
        if (indirectSelection == 0)
            return unchecked(baseAndDisplacement + indexValue);

        uint outerDisplacement = 0;
        if ((extension & 0x0002) != 0)
        {
            outerDisplacement = (extension & 0x0001) != 0
                ? ReadImmediateLong()
                : unchecked((uint)(int)(short)ReadImmediateWord());
        }

        return (extension & 0x0004) != 0
            ? unchecked(ReadLong(baseAndDisplacement) + indexValue + outerDisplacement)
            : unchecked(ReadLong(baseAndDisplacement + indexValue) + outerDisplacement);
    }

    private uint GetPcDisplacementAddress()
    {
        uint baseAddress = _pc;
        return unchecked(baseAddress + (uint)(int)(short)ReadImmediateWord());
    }

    private uint GetPcIndexedAddress()
    {
        uint baseAddress = _pc;
        return GetIndexedAddress(baseAddress);
    }

    private bool CheckInterrupts()
    {
        byte level = (byte)(_bus!.InterruptLevel() & 7);
        if (level == 0 || level <= InterruptPriorityMask)
            return false;

        _stopped = false;
        _bus.AcknowledgeInterrupt(level);
        ushort stackedSr = _sr;
        SetSrForException(level);
        bool createThrowawayFrame = Master;
        uint vector = (uint)(ExceptionInterruptAutovector + level);
        uint target = ReadVector(vector);
        if (target == 0)
            target = ReadVector(ExceptionUninitializedInterrupt);
        StackFrameFormat0(_pc, stackedSr, vector);
        if (createThrowawayFrame)
        {
            SetSrNoInterrupt((ushort)(_sr & ~MasterMask));
            StackFrameFormat1(_pc, (ushort)(stackedSr | SupervisorMask), vector);
        }
        _pc = target;
        _lastCycles = 44;
        return true;
    }

    private void ExceptionVector(uint vector, uint pc)
    {
        ushort stackedSr = _sr;
        SetSrForException(null);
        StackFrameFormat0(pc, stackedSr, vector);
        _pc = ReadVector(vector);
    }

    private void ExceptionTrapFormat2(uint vector)
    {
        ExceptionTrapFrame(vector, _pc, _ppc);
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

    private void StackFrameFormat1(uint pc, ushort sr, uint vector)
    {
        PushWord((ushort)(0x1000 | ((vector << 2) & 0x0fffu)));
        PushLong(pc);
        PushWord(sr);
    }

    private uint ReadVector(uint vector) => ReadLong((_vbr + (vector << 2)));

    private bool TryReadControlRegister(ushort controlRegister, out uint value)
    {
        value = controlRegister switch
        {
            0x000 => _sfc,
            0x001 => _dfc,
            0x002 => _cacr,
            0x800 => _usp,
            0x801 => _vbr,
            0x802 => _caar,
            0x803 => Master ? _a[7] : _msp,
            0x804 => Master ? _isp : _a[7],
            _ => 0
        };
        return IsSupportedMovecControlRegister(controlRegister);
    }

    private bool TryWriteControlRegister(ushort controlRegister, uint value)
    {
        switch (controlRegister)
        {
            case 0x000:
                _sfc = value & 7;
                return true;
            case 0x001:
                _dfc = value & 7;
                return true;
            case 0x002:
                _cacr = value & 0x0f;
                _cacr &= ~0x0cu;
                return true;
            case 0x800:
                _usp = value;
                return true;
            case 0x801:
                _vbr = value;
                return true;
            case 0x802:
                _caar = value;
                return true;
            case 0x803:
                if (Master)
                    _a[7] = value;
                else
                    _msp = value;
                return true;
            case 0x804:
                if (Master)
                    _isp = value;
                else
                    _a[7] = value;
                return true;
            default:
                return false;
        }
    }

    private static bool IsSupportedMovecControlRegister(ushort controlRegister)
        => controlRegister is 0x000 or 0x001 or 0x002 or 0x800 or 0x801 or 0x802 or 0x803 or 0x804;

    private uint GetMovecRegister(int register)
        => register < 8 ? _d[register] : _a[register - 8];

    private void SetMovecRegister(int register, uint value)
    {
        if (register < 8)
            _d[register] = value;
        else
            _a[register - 8] = value;
    }

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
        _ = CheckInterrupts();
    }

    private void SetSrNoInterrupt(ushort value)
    {
        uint oldStackPointer = _a[7];
        ushort oldSr = _sr;
        SaveActiveStackPointer();
        _sr = (ushort)(value & SrMask);
        LoadActiveStackPointer();
        RecordLowStackSwitch(oldSr, _sr, oldStackPointer, _a[7]);
    }

    private void RecordLowStackSwitch(ushort oldSr, ushort newSr, uint oldStackPointer, uint newStackPointer)
    {
        if (newStackPointer >= 0x0040_0000u && newStackPointer < 0x0042_0000u)
            return;

        LowStackSwitchCount++;
        LastLowStackSwitchPc = _ppc;
        LastLowStackSwitchOpcode = _ir;
        LastLowStackSwitchOldSr = oldSr;
        LastLowStackSwitchNewSr = newSr;
        LastLowStackSwitchOldStackPointer = oldStackPointer;
        LastLowStackSwitchNewStackPointer = newStackPointer;
        LastLowStackSwitchUserStackPointer = _usp;
        LastLowStackSwitchInterruptStackPointer = _isp;
        LastLowStackSwitchMasterStackPointer = _msp;
    }

    private void RecordSuspiciousSupervisorStack()
    {
        if (!Supervisor || (_a[7] >= 0x0040_0000u && _a[7] < 0x0042_0000u))
            return;

        if (SuspiciousSupervisorStackCount == 0)
        {
            FirstSuspiciousSupervisorStackPc = _ppc;
            FirstSuspiciousSupervisorStackOpcode = _ir;
            FirstSuspiciousSupervisorStackStatusRegister = _sr;
            FirstSuspiciousSupervisorStackPointer = _a[7];
        }

        SuspiciousSupervisorStackCount++;
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

    private uint CurrentSupervisorStackPointer()
    {
        if (Supervisor)
            return _a[7];
        return ActiveSupervisorStackPointer();
    }

    private uint ActiveSupervisorStackPointer()
        => (Master ? _msp : _isp);

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
        _a[7] = Supervisor ? ActiveSupervisorStackPointer() : (_usp);
    }

    private ushort ReadImmediateWord()
    {
        ushort value = ReadOpcodeWord(_pc);
        _pc = (_pc + 2u);
        return value;
    }

    private uint ReadImmediateLong()
    {
        uint value = ReadOpcodeLong(_pc);
        _pc = (_pc + 4u);
        return value;
    }

    private ushort PopWord()
    {
        ushort value = ReadWord(_a[7]);
        _a[7] = (_a[7] + 2u);
        return value;
    }

    private uint PopLong()
    {
        uint value = ReadLong(_a[7]);
        _a[7] = (_a[7] + 4u);
        return value;
    }

    private void PushWord(ushort value)
    {
        _a[7] = (_a[7] - 2u);
        WriteWord(_a[7], value);
    }

    private void PushLong(uint value)
    {
        _a[7] = (_a[7] - 4u);
        WriteLong(_a[7], value);
    }

    private byte ReadByte(uint address) => _bus!.ReadByte(address);
    private ushort ReadWord(uint address) => _bus!.ReadWord(address);
    private uint ReadLong(uint address) => _bus!.ReadLong(address);
    private void WriteWord(uint address, ushort value) => _bus!.WriteWord(address, value);
    private void WriteByte(uint address, byte value) => _bus!.WriteByte(address, value);
    private void WriteLong(uint address, uint value) => _bus!.WriteLong(address, value);
    private ushort ReadOpcodeWord(uint address) => _bus is IOpcodeBusInterface op ? op.ReadOpcodeWord(address) : ReadWord(address);
    private uint ReadOpcodeLong(uint address) => ((uint)ReadOpcodeWord(address) << 16) | ReadOpcodeWord(address + 2u);

    private enum OpSize
    {
        Byte,
        Word,
        Long
    }
}
