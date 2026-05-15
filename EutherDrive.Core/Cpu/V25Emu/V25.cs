using System;
using System.Globalization;

namespace EutherDrive.Core.Cpu.V25Emu;

// Minimal NEC V25 interpreter for Sega System 32 protection MCUs. Instruction
// behavior follows MAME's BSD-3-Clause NEC V25 core by Bryan McPhail and
// Alex W. Jackson.
public sealed class V25
{
    private const int Ax = 0;
    private const int Cx = 1;
    private const int Dx = 2;
    private const int Bx = 3;
    private const int Sp = 4;
    private const int Bp = 5;
    private const int Si = 6;
    private const int Di = 7;

    private const int Ds1 = 0;
    private const int Ps = 1;
    private const int Ss = 2;
    private const int Ds0 = 3;

    private const int VectorPcOffset = 0x02;
    private const int PswSaveOffset = 0x04;
    private const int PcSaveOffset = 0x06;
    private const int InternalIrqsOffset = 0x1ef;
    private const int InternalIsprOffset = 0x1fc;

    private static readonly byte[] RegisterBankOffsets =
    {
        0x1e, // AX
        0x1c, // CX
        0x1a, // DX
        0x18, // BX
        0x16, // SP
        0x14, // BP
        0x12, // SI
        0x10  // DI
    };

    private static readonly byte[] SegmentBankOffsets =
    {
        0x0e, // DS1
        0x0c, // PS
        0x0a, // SS
        0x08  // DS0
    };

    public static readonly byte[] GoldenAxe2OpcodeTable =
    {
        0x00,0x00,0xea,0x00,0x00,0x8b,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xfa,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x3b,0x00,0x49,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0xe8,0x00,0x00,0x75,0x00,0x00,0x00,0x00,0x3a,0x00,0x00,
        0x00,0x00,0x00,0x00,0x8d,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xbf,0x00,0x88,0x00,
        0x00,0x00,0x00,0x81,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x02,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xbc,
        0x00,0x00,0x00,0x8a,0x00,0x00,0x00,0x00,0x00,0x00,0x83,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xb8,0x26,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xb5,0x00,0xeb,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xb2,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0xc3,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xb9,0xbb,0x00,0x43,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x8e,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xbe,0x00,0x80,0x00,0x00
    };

    private readonly ushort[] _regs = new ushort[8];
    private readonly ushort[] _sregs = new ushort[4];
    private IV25Bus? _bus;
    private byte[] _opcodeTable = GoldenAxe2OpcodeTable;
    private ushort _ip;
    private uint _prefixBase;
    private bool _segmentPrefix;
    private byte _carry;
    private byte _zero = 1;
    private byte _sign;
    private byte _parity;
    private byte _overflow;
    private bool _direction;
    private bool _rep;
    private int _registerBank = 7;

    public uint Pc => ((uint)_sregs[Ps] << 4) + _ip;
    public uint PreviousPc { get; private set; }
    public byte LastOpcode { get; private set; }
    public bool Halted { get; private set; }
    public string LastStopReason { get; private set; } = string.Empty;
    public int CurrentRegisterBank => _registerBank;
    public ushort DebugSp => _regs[Sp];
    public ushort DebugSs => _sregs[Ss];

    public void Reset(IV25Bus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        Array.Clear(_regs);
        Array.Clear(_sregs);
        _ip = 0;
        _sregs[Ps] = 0xffff;
        _registerBank = 7;
        _zero = 1;
        _carry = _sign = _parity = _overflow = 0;
        _direction = false;
        _rep = false;
        _prefixBase = 0;
        _segmentPrefix = false;
        Halted = false;
        LastStopReason = string.Empty;
        StoreCurrentBankState(bus);
    }

    public void SetOpcodeTable(byte[] opcodeTable)
    {
        if (opcodeTable.Length != 256)
            throw new ArgumentException("V25 opcode decryption table must contain 256 entries.", nameof(opcodeTable));

        _opcodeTable = opcodeTable;
    }

    public int ExecuteInstruction()
    {
        IV25Bus bus = _bus ?? throw new InvalidOperationException("V25 has not been reset with a bus.");
        if (Halted)
            return 0;

        PreviousPc = Pc;
        byte encrypted = Fetch(bus);
        byte opcode = _opcodeTable[encrypted];
        if (opcode == 0)
            opcode = encrypted;
        LastOpcode = opcode;
        ExecuteOpcode(bus, opcode);
        return 8;
    }

    public void TriggerRegisterBankInterrupt(int bank, byte vector)
    {
        IV25Bus bus = _bus ?? throw new InvalidOperationException("V25 has not been reset with a bus.");
        if (Halted)
            return;

        bank &= 7;
        bus.V25WriteInternal8(InternalIrqsOffset, vector);
        byte ispr = bus.V25ReadInternal8(InternalIsprOffset);
        ispr |= (byte)(1 << bank);
        bus.V25WriteInternal8(InternalIsprOffset, ispr);
        ExecuteRegisterBankSwitch(bus, bank);
    }

    private void ExecuteOpcode(IV25Bus bus, byte opcode)
    {
        switch (opcode)
        {
            case 0xfa:
                return; // CLI
            case 0xfb:
                return; // STI
            case 0xfc:
                _direction = false;
                return;
            case 0xfd:
                _direction = true;
                return;
            case 0xf3:
                ExecuteWithRepPrefix(bus);
                return;
            case 0x06:
                Push(bus, _sregs[Ds1]);
                return;
            case 0x07:
                _sregs[Ds1] = Pop(bus);
                return;
            case 0x0e:
                Push(bus, _sregs[Ps]);
                return;
            case 0x16:
                Push(bus, _sregs[Ss]);
                return;
            case 0x17:
                _sregs[Ss] = Pop(bus);
                return;
            case 0x1e:
                Push(bus, _sregs[Ds0]);
                return;
            case 0x1f:
                _sregs[Ds0] = Pop(bus);
                return;
            case 0x26:
                ExecuteWithSegmentPrefix(bus, Ds1);
                return;
            case 0x2e:
                ExecuteWithSegmentPrefix(bus, Ps);
                return;
            case 0x36:
                ExecuteWithSegmentPrefix(bus, Ss);
                return;
            case 0x3e:
                ExecuteWithSegmentPrefix(bus, Ds0);
                return;
            case 0x02:
                ExecuteAluReg8Rm8(bus, 0);
                return;
            case 0x03:
                ExecuteAluReg16Rm16(bus, 0);
                return;
            case 0x00:
                ExecuteAluRm8Reg8(bus, 0);
                return;
            case 0x01:
                ExecuteAluRm16Reg16(bus, 0);
                return;
            case 0x04:
                ExecuteAluAlImm8(bus, 0);
                return;
            case 0x05:
                ExecuteAluAxImm16(bus, 0);
                return;
            case 0x0a:
                ExecuteAluReg8Rm8(bus, 1);
                return;
            case 0x08:
                ExecuteAluRm8Reg8(bus, 1);
                return;
            case 0x09:
                ExecuteAluRm16Reg16(bus, 1);
                return;
            case 0x0b:
                ExecuteAluReg16Rm16(bus, 1);
                return;
            case 0x0c:
                ExecuteAluAlImm8(bus, 1);
                return;
            case 0x0d:
                ExecuteAluAxImm16(bus, 1);
                return;
            case 0x0f:
                ExecuteNecExtended(bus);
                return;
            case 0x10:
                ExecuteAluRm8Reg8(bus, 2);
                return;
            case 0x11:
                ExecuteAluRm16Reg16(bus, 2);
                return;
            case 0x12:
                ExecuteAluReg8Rm8(bus, 2);
                return;
            case 0x13:
                ExecuteAluReg16Rm16(bus, 2);
                return;
            case 0x14:
                ExecuteAluAlImm8(bus, 2);
                return;
            case 0x15:
                ExecuteAluAxImm16(bus, 2);
                return;
            case 0x18:
                ExecuteAluRm8Reg8(bus, 3);
                return;
            case 0x19:
                ExecuteAluRm16Reg16(bus, 3);
                return;
            case 0x1a:
                ExecuteAluReg8Rm8(bus, 3);
                return;
            case 0x1b:
                ExecuteAluReg16Rm16(bus, 3);
                return;
            case 0x1c:
                ExecuteAluAlImm8(bus, 3);
                return;
            case 0x1d:
                ExecuteAluAxImm16(bus, 3);
                return;
            case 0x20:
                ExecuteAluRm8Reg8(bus, 4);
                return;
            case 0x21:
                ExecuteAluRm16Reg16(bus, 4);
                return;
            case 0x22:
                ExecuteAluReg8Rm8(bus, 4);
                return;
            case 0x23:
                ExecuteAluReg16Rm16(bus, 4);
                return;
            case 0x24:
                ExecuteAluAlImm8(bus, 4);
                return;
            case 0x25:
                ExecuteAluAxImm16(bus, 4);
                return;
            case 0x28:
                ExecuteAluRm8Reg8(bus, 5);
                return;
            case 0x29:
                ExecuteAluRm16Reg16(bus, 5);
                return;
            case 0x2a:
                ExecuteAluReg8Rm8(bus, 5);
                return;
            case 0x2b:
                ExecuteAluReg16Rm16(bus, 5);
                return;
            case 0x2c:
                ExecuteAluAlImm8(bus, 5);
                return;
            case 0x2d:
                ExecuteAluAxImm16(bus, 5);
                return;
            case 0x30:
                ExecuteAluRm8Reg8(bus, 6);
                return;
            case 0x31:
                ExecuteAluRm16Reg16(bus, 6);
                return;
            case 0x32:
                ExecuteAluReg8Rm8(bus, 6);
                return;
            case 0x33:
                ExecuteAluReg16Rm16(bus, 6);
                return;
            case 0x34:
                ExecuteAluAlImm8(bus, 6);
                return;
            case 0x35:
                ExecuteAluAxImm16(bus, 6);
                return;
            case 0x38:
                ExecuteAluRm8Reg8(bus, 7);
                return;
            case 0x39:
                ExecuteAluRm16Reg16(bus, 7);
                return;
            case 0x3a:
                ExecuteAluReg8Rm8(bus, 7);
                return;
            case 0x3b:
                ExecuteAluReg16Rm16(bus, 7);
                return;
            case 0x3c:
                ExecuteAluAlImm8(bus, 7);
                return;
            case 0x3d:
                ExecuteAluAxImm16(bus, 7);
                return;
            case >= 0x40 and <= 0x47:
                {
                    int reg = opcode - 0x40;
                    ushort old = _regs[reg];
                    _regs[reg] = (ushort)(old + 1);
                    SetIncFlags16(old, _regs[reg]);
                }
                return;
            case >= 0x48 and <= 0x4f:
                {
                    int reg = opcode - 0x48;
                    ushort old = _regs[reg];
                    _regs[reg] = (ushort)(old - 1);
                    SetDecFlags16(old, _regs[reg]);
                }
                return;
            case >= 0x50 and <= 0x57:
                Push(bus, _regs[opcode - 0x50]);
                return;
            case >= 0x58 and <= 0x5f:
                _regs[opcode - 0x58] = Pop(bus);
                return;
            case 0x68:
                Push(bus, FetchWord(bus));
                return;
            case >= 0x70 and <= 0x7f:
                Branch8(bus, EvaluateCondition(opcode & 0x0f));
                return;
            case 0x80:
                ExecuteGroup80(bus);
                return;
            case 0x81:
                ExecuteGroup81(bus);
                return;
            case 0x83:
                ExecuteGroup83(bus);
                return;
            case 0x88:
                ExecuteMovRm8R8(bus);
                return;
            case 0x89:
                ExecuteMovRm16R16(bus);
                return;
            case 0x86:
                ExecuteXchgRm8R8(bus);
                return;
            case 0x87:
                ExecuteXchgRm16R16(bus);
                return;
            case 0x8c:
                ExecuteMovRm16Sreg(bus);
                return;
            case 0x8a:
                ExecuteMovR8Rm8(bus);
                return;
            case 0x8b:
                ExecuteMovR16Rm16(bus);
                return;
            case 0x8d:
                ExecuteLea(bus);
                return;
            case 0x8e:
                ExecuteMovSregRm16(bus);
                return;
            case 0x90:
                return;
            case >= 0xb0 and <= 0xb7:
                SetReg8(opcode - 0xb0, Fetch(bus));
                return;
            case >= 0xb8 and <= 0xbf:
                _regs[opcode - 0xb8] = FetchWord(bus);
                return;
            case 0xa0:
                SetReg8(0, Read8(bus, GetDirectAddress(Ds0, FetchWord(bus))));
                return;
            case 0xa1:
                _regs[Ax] = Read16(bus, GetDirectAddress(Ds0, FetchWord(bus)));
                return;
            case 0xa2:
                Write8(bus, GetDirectAddress(Ds0, FetchWord(bus)), GetReg8(0));
                return;
            case 0xa3:
                Write16(bus, GetDirectAddress(Ds0, FetchWord(bus)), _regs[Ax]);
                return;
            case 0xa4:
                ExecuteMovsb(bus);
                return;
            case 0xa5:
                ExecuteMovsw(bus);
                return;
            case 0xa8:
                SetLogicalFlags8((byte)(GetReg8(0) & Fetch(bus)));
                return;
            case 0xa9:
                SetLogicalFlags16((ushort)(_regs[Ax] & FetchWord(bus)));
                return;
            case 0xaa:
                ExecuteStosb(bus);
                return;
            case 0xab:
                ExecuteStosw(bus);
                return;
            case 0xc0:
                ExecuteGroupC0(bus, -1);
                return;
            case 0xc1:
                ExecuteGroupC1(bus, -1);
                return;
            case 0xc6:
                ExecuteMovRm8Imm8(bus);
                return;
            case 0xc7:
                ExecuteMovRm16Imm16(bus);
                return;
            case 0xd0:
                ExecuteGroupC0(bus, 1);
                return;
            case 0xd1:
                ExecuteGroupC1(bus, 1);
                return;
            case 0xd2:
                ExecuteGroupC0(bus, GetReg8(Cx));
                return;
            case 0xd3:
                ExecuteGroupC1(bus, GetReg8(Cx));
                return;
            case 0xc3:
                _ip = Pop(bus);
                return;
            case 0xe8:
                {
                    short displacement = (short)FetchWord(bus);
                    Push(bus, _ip);
                    _ip = unchecked((ushort)(_ip + displacement));
                    return;
                }
            case 0xe2:
                {
                    sbyte displacement = (sbyte)Fetch(bus);
                    _regs[Cx]--;
                    if (_regs[Cx] != 0)
                        _ip = unchecked((ushort)(_ip + displacement));
                    return;
                }
            case 0xe9:
                {
                    short displacement = (short)FetchWord(bus);
                    _ip = unchecked((ushort)(_ip + displacement));
                    return;
                }
            case 0xea:
                {
                    ushort ip = FetchWord(bus);
                    ushort ps = FetchWord(bus);
                    _ip = ip;
                    _sregs[Ps] = ps;
                    return;
                }
            case 0xeb:
                Branch8(bus, true);
                return;
            case 0xf8:
                _carry = 0;
                return;
            case 0xf9:
                _carry = 1;
                return;
            case 0xf6:
                ExecuteGroupF6(bus);
                return;
            case 0xf7:
                ExecuteGroupF7(bus);
                return;
            case 0xfe:
                ExecuteGroupFE(bus);
                return;
            case 0xff:
                ExecuteGroupFF(bus);
                return;
            default:
                Halted = true;
                LastStopReason = string.Create(
                    CultureInfo.InvariantCulture,
                    $"unimplemented opcode 0x{opcode:X2} encrypted=0x{Read8(bus, PreviousPc):X2} pc=0x{PreviousPc:X5}");
                return;
        }
    }

    private void ExecuteWithSegmentPrefix(IV25Bus bus, int segment)
    {
        uint oldBase = _prefixBase;
        bool oldPrefix = _segmentPrefix;
        _prefixBase = (uint)_sregs[segment] << 4;
        _segmentPrefix = true;
        byte encrypted = Fetch(bus);
        byte opcode = _opcodeTable[encrypted];
        if (opcode == 0)
            opcode = encrypted;
        LastOpcode = opcode;
        ExecuteOpcode(bus, opcode);
        _prefixBase = oldBase;
        _segmentPrefix = oldPrefix;
    }

    private void ExecuteWithRepPrefix(IV25Bus bus)
    {
        bool oldRep = _rep;
        _rep = true;
        byte encrypted = Fetch(bus);
        byte opcode = _opcodeTable[encrypted];
        if (opcode == 0)
            opcode = encrypted;
        LastOpcode = opcode;
        ExecuteOpcode(bus, opcode);
        _rep = oldRep;
    }

    private void ExecuteMovSregRm16(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        ushort value = ReadRm16(bus, modrm);
        switch (modrm & 0x38)
        {
            case 0x00:
                _sregs[Ds1] = value;
                break;
            case 0x08:
                _sregs[Ps] = value;
                break;
            case 0x10:
                _sregs[Ss] = value;
                break;
            case 0x18:
                _sregs[Ds0] = value;
                break;
        }
    }

    private void ExecuteMovRm16Sreg(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        ushort value = (modrm & 0x38) switch
        {
            0x00 => _sregs[Ds1],
            0x08 => _sregs[Ps],
            0x10 => _sregs[Ss],
            _ => _sregs[Ds0]
        };
        WriteRm16(bus, modrm, value);
    }

    private void ExecuteLea(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int reg = (modrm >> 3) & 7;
        _ = GetEffectiveAddress(bus, modrm, out ushort offset, out _);
        _regs[reg] = offset;
    }

    private void ExecuteMovR16Rm16(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        _regs[(modrm >> 3) & 7] = ReadRm16(bus, modrm);
    }

    private void ExecuteMovR8Rm8(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        SetReg8((modrm >> 3) & 7, ReadRm8(bus, modrm));
    }

    private void ExecuteMovRm8R8(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        WriteRm8(bus, modrm, GetReg8((modrm >> 3) & 7));
    }

    private void ExecuteMovRm16R16(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        WriteRm16(bus, modrm, _regs[(modrm >> 3) & 7]);
    }

    private void ExecuteXchgRm8R8(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int reg = (modrm >> 3) & 7;
        byte left = ReadRm8Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        byte right = GetReg8(reg);
        WriteRm8Resolved(bus, isRegister, register, address, right);
        SetReg8(reg, left);
    }

    private void ExecuteXchgRm16R16(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int reg = (modrm >> 3) & 7;
        ushort left = ReadRm16Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        ushort right = _regs[reg];
        WriteRm16Resolved(bus, isRegister, register, address, right);
        _regs[reg] = left;
    }

    private void ExecuteMovRm8Imm8(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        if (op != 0)
        {
            StopUnsupportedGroup(0xc6, op);
            return;
        }

        if (modrm >= 0xc0)
        {
            SetReg8(modrm & 7, Fetch(bus));
            return;
        }

        uint address = GetEffectiveAddress(bus, modrm, out _, out _);
        Write8(bus, address, Fetch(bus));
    }

    private void ExecuteMovRm16Imm16(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        if (op != 0)
        {
            StopUnsupportedGroup(0xc7, op);
            return;
        }

        if (modrm >= 0xc0)
        {
            _regs[modrm & 7] = FetchWord(bus);
            return;
        }

        uint address = GetEffectiveAddress(bus, modrm, out _, out _);
        Write16(bus, address, FetchWord(bus));
    }

    private void ExecuteAluRm8Reg8(IV25Bus bus, int op)
    {
        byte modrm = Fetch(bus);
        byte dest = ReadRm8Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        byte source = GetReg8((modrm >> 3) & 7);
        byte result = ApplyAlu8(op, dest, source);
        if (op != 7)
            WriteRm8Resolved(bus, isRegister, register, address, result);
    }

    private void ExecuteAluRm16Reg16(IV25Bus bus, int op)
    {
        byte modrm = Fetch(bus);
        ushort dest = ReadRm16Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        ushort source = _regs[(modrm >> 3) & 7];
        ushort result = ApplyAlu16(op, dest, source);
        if (op != 7)
            WriteRm16Resolved(bus, isRegister, register, address, result);
    }

    private void ExecuteAluReg8Rm8(IV25Bus bus, int op)
    {
        byte modrm = Fetch(bus);
        int reg = (modrm >> 3) & 7;
        byte dest = GetReg8(reg);
        byte source = ReadRm8(bus, modrm);
        byte result = ApplyAlu8(op, dest, source);
        if (op != 7)
            SetReg8(reg, result);
    }

    private void ExecuteAluReg16Rm16(IV25Bus bus, int op)
    {
        byte modrm = Fetch(bus);
        int reg = (modrm >> 3) & 7;
        ushort dest = _regs[reg];
        ushort source = ReadRm16(bus, modrm);
        ushort result = ApplyAlu16(op, dest, source);
        if (op != 7)
            _regs[reg] = result;
    }

    private void ExecuteAluAlImm8(IV25Bus bus, int op)
    {
        byte dest = GetReg8(0);
        byte source = Fetch(bus);
        byte result = ApplyAlu8(op, dest, source);
        if (op != 7)
            SetReg8(0, result);
    }

    private void ExecuteAluAxImm16(IV25Bus bus, int op)
    {
        ushort dest = _regs[Ax];
        ushort source = FetchWord(bus);
        ushort result = ApplyAlu16(op, dest, source);
        if (op != 7)
            _regs[Ax] = result;
    }

    private byte ApplyAlu8(int op, byte dest, byte source)
    {
        return op switch
        {
            0 => Add8(dest, source),
            1 => Or8(dest, source),
            2 => Adc8(dest, source),
            3 => Sbb8(dest, source),
            4 => And8(dest, source),
            5 => Sub8(dest, source),
            6 => Xor8(dest, source),
            _ => Cmp8(dest, source)
        };
    }

    private ushort ApplyAlu16(int op, ushort dest, ushort source)
    {
        return op switch
        {
            0 => Add16(dest, source),
            1 => Or16(dest, source),
            2 => Adc16(dest, source),
            3 => Sbb16(dest, source),
            4 => And16(dest, source),
            5 => Sub16(dest, source),
            6 => Xor16(dest, source),
            _ => Cmp16(dest, source)
        };
    }

    private byte Add8(byte left, byte right)
    {
        byte result = (byte)(left + right);
        SetAddFlags8(left, right, result);
        return result;
    }

    private ushort Add16(ushort left, ushort right)
    {
        ushort result = (ushort)(left + right);
        SetAddFlags16(left, right, result);
        return result;
    }

    private byte Adc8(byte left, byte right)
    {
        int carry = _carry;
        byte result = (byte)(left + right + carry);
        SetAdcFlags8(left, right, carry, result);
        return result;
    }

    private ushort Adc16(ushort left, ushort right)
    {
        int carry = _carry;
        ushort result = (ushort)(left + right + carry);
        SetAdcFlags16(left, right, carry, result);
        return result;
    }

    private byte Sbb8(byte left, byte right)
    {
        int carry = _carry;
        byte result = (byte)(left - right - carry);
        SetSbbFlags8(left, right, carry, result);
        return result;
    }

    private ushort Sbb16(ushort left, ushort right)
    {
        int carry = _carry;
        ushort result = (ushort)(left - right - carry);
        SetSbbFlags16(left, right, carry, result);
        return result;
    }

    private byte Sub8(byte left, byte right)
    {
        byte result = (byte)(left - right);
        SetSubFlags8(left, right, result);
        return result;
    }

    private ushort Sub16(ushort left, ushort right)
    {
        ushort result = (ushort)(left - right);
        SetSubFlags16(left, right, result);
        return result;
    }

    private byte Cmp8(byte left, byte right) => Sub8(left, right);

    private ushort Cmp16(ushort left, ushort right) => Sub16(left, right);

    private byte Or8(byte left, byte right)
    {
        byte result = (byte)(left | right);
        SetLogicalFlags8(result);
        return result;
    }

    private ushort Or16(ushort left, ushort right)
    {
        ushort result = (ushort)(left | right);
        SetLogicalFlags16(result);
        return result;
    }

    private byte And8(byte left, byte right)
    {
        byte result = (byte)(left & right);
        SetLogicalFlags8(result);
        return result;
    }

    private ushort And16(ushort left, ushort right)
    {
        ushort result = (ushort)(left & right);
        SetLogicalFlags16(result);
        return result;
    }

    private byte Xor8(byte left, byte right)
    {
        byte result = (byte)(left ^ right);
        SetLogicalFlags8(result);
        return result;
    }

    private ushort Xor16(ushort left, ushort right)
    {
        ushort result = (ushort)(left ^ right);
        SetLogicalFlags16(result);
        return result;
    }

    private void ExecuteGroup80(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        byte dest = ReadRm8Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        byte immediate = Fetch(bus);
        byte result = ApplyAlu8(op, dest, immediate);
        if (op != 7)
            WriteRm8Resolved(bus, isRegister, register, address, result);
    }

    private void ExecuteGroupC0(IV25Bus bus, int count)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        byte dest = ReadRm8Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        if (count < 0)
            count = Fetch(bus);
        byte result = ShiftRotate8(op, dest, count & 0x1f, out bool supported);
        if (!supported)
        {
            StopUnsupportedGroup(0xc0, op);
            return;
        }

        WriteRm8Resolved(bus, isRegister, register, address, result);
    }

    private void ExecuteGroupC1(IV25Bus bus, int count)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        ushort dest = ReadRm16Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        if (count < 0)
            count = Fetch(bus);
        ushort result = ShiftRotate16(op, dest, count & 0x1f, out bool supported);
        if (!supported)
        {
            StopUnsupportedGroup(0xc1, op);
            return;
        }

        WriteRm16Resolved(bus, isRegister, register, address, result);
    }

    private void ExecuteGroupF6(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        byte dest = ReadRm8Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        switch (op)
        {
            case 0:
                {
                    byte immediate = Fetch(bus);
                    SetLogicalFlags8((byte)(dest & immediate));
                    break;
                }
            case 2:
                WriteRm8Resolved(bus, isRegister, register, address, (byte)~dest);
                break;
            case 3:
                {
                    byte result = (byte)-dest;
                    _carry = (byte)(dest != 0 ? 1 : 0);
                    SetResultFlags8(result);
                    _overflow = (byte)(dest == 0x80 ? 1 : 0);
                    WriteRm8Resolved(bus, isRegister, register, address, result);
                    break;
                }
            case 4:
                {
                    ushort result = (ushort)(GetReg8(0) * dest);
                    _regs[Ax] = result;
                    _carry = _overflow = (byte)((result & 0xff00) != 0 ? 1 : 0);
                    SetResultFlags8((byte)result);
                    break;
                }
            case 5:
                {
                    short result = (short)((sbyte)GetReg8(0) * (sbyte)dest);
                    _regs[Ax] = (ushort)result;
                    _carry = _overflow = (byte)((_regs[Ax] & 0xff00) != 0 ? 1 : 0);
                    SetResultFlags8((byte)result);
                    break;
                }
            case 6:
                ExecuteDiv8(dest, signed: false);
                break;
            case 7:
                ExecuteDiv8(dest, signed: true);
                break;
            default:
                StopUnsupportedGroup(0xf6, op);
                break;
        }
    }

    private void ExecuteGroupF7(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        ushort dest = ReadRm16Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        switch (op)
        {
            case 0:
                {
                    ushort immediate = FetchWord(bus);
                    SetLogicalFlags16((ushort)(dest & immediate));
                    break;
                }
            case 2:
                WriteRm16Resolved(bus, isRegister, register, address, (ushort)~dest);
                break;
            case 3:
                {
                    ushort result = (ushort)-dest;
                    _carry = (byte)(dest != 0 ? 1 : 0);
                    SetResultFlags16(result);
                    _overflow = (byte)(dest == 0x8000 ? 1 : 0);
                    WriteRm16Resolved(bus, isRegister, register, address, result);
                    break;
                }
            case 4:
                {
                    uint result = (uint)(_regs[Ax] * dest);
                    _regs[Ax] = (ushort)result;
                    _regs[Dx] = (ushort)(result >> 16);
                    _carry = _overflow = (byte)(_regs[Dx] != 0 ? 1 : 0);
                    SetResultFlags16(_regs[Ax]);
                    break;
                }
            case 5:
                {
                    int result = (short)_regs[Ax] * (short)dest;
                    _regs[Ax] = (ushort)result;
                    _regs[Dx] = (ushort)(result >> 16);
                    _carry = _overflow = (byte)(_regs[Dx] != 0 ? 1 : 0);
                    SetResultFlags16(_regs[Ax]);
                    break;
                }
            case 6:
                ExecuteDiv16(dest, signed: false);
                break;
            case 7:
                ExecuteDiv16(dest, signed: true);
                break;
            default:
                StopUnsupportedGroup(0xf7, op);
                break;
        }
    }

    private void ExecuteDiv8(byte divisor, bool signed)
    {
        if (divisor == 0)
        {
            Halted = true;
            LastStopReason = string.Create(CultureInfo.InvariantCulture, $"V25 divide by zero pc=0x{PreviousPc:X5}");
            return;
        }

        if (signed)
        {
            int dividend = (short)_regs[Ax];
            int quotient = dividend / (sbyte)divisor;
            int remainder = dividend % (sbyte)divisor;
            if (quotient < sbyte.MinValue || quotient > sbyte.MaxValue)
            {
                Halted = true;
                LastStopReason = string.Create(CultureInfo.InvariantCulture, $"V25 signed byte divide overflow pc=0x{PreviousPc:X5}");
                return;
            }

            SetReg8(0, (byte)(sbyte)quotient);
            SetReg8(4, (byte)(sbyte)remainder);
            return;
        }

        int unsignedQuotient = _regs[Ax] / divisor;
        int unsignedRemainder = _regs[Ax] % divisor;
        if (unsignedQuotient > byte.MaxValue)
        {
            Halted = true;
            LastStopReason = string.Create(CultureInfo.InvariantCulture, $"V25 byte divide overflow pc=0x{PreviousPc:X5}");
            return;
        }

        SetReg8(0, (byte)unsignedQuotient);
        SetReg8(4, (byte)unsignedRemainder);
    }

    private void ExecuteDiv16(ushort divisor, bool signed)
    {
        if (divisor == 0)
        {
            Halted = true;
            LastStopReason = string.Create(CultureInfo.InvariantCulture, $"V25 divide by zero pc=0x{PreviousPc:X5}");
            return;
        }

        if (signed)
        {
            int dividend = (_regs[Dx] << 16) | _regs[Ax];
            int quotient = dividend / (short)divisor;
            int remainder = dividend % (short)divisor;
            if (quotient < short.MinValue || quotient > short.MaxValue)
            {
                Halted = true;
                LastStopReason = string.Create(CultureInfo.InvariantCulture, $"V25 signed word divide overflow pc=0x{PreviousPc:X5}");
                return;
            }

            _regs[Ax] = (ushort)(short)quotient;
            _regs[Dx] = (ushort)(short)remainder;
            return;
        }

        uint unsignedDividend = ((uint)_regs[Dx] << 16) | _regs[Ax];
        uint unsignedQuotient = unsignedDividend / divisor;
        uint unsignedRemainder = unsignedDividend % divisor;
        if (unsignedQuotient > ushort.MaxValue)
        {
            Halted = true;
            LastStopReason = string.Create(CultureInfo.InvariantCulture, $"V25 word divide overflow pc=0x{PreviousPc:X5}");
            return;
        }

        _regs[Ax] = (ushort)unsignedQuotient;
        _regs[Dx] = (ushort)unsignedRemainder;
    }

    private void ExecuteGroupFE(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        byte dest = ReadRm8Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        switch (op)
        {
            case 0:
                {
                    byte result = (byte)(dest + 1);
                    SetIncFlags8(dest, result);
                    WriteRm8Resolved(bus, isRegister, register, address, result);
                    break;
                }
            case 1:
                {
                    byte result = (byte)(dest - 1);
                    SetDecFlags8(dest, result);
                    WriteRm8Resolved(bus, isRegister, register, address, result);
                    break;
                }
            default:
                StopUnsupportedGroup(0xfe, op);
                break;
        }
    }

    private void ExecuteNecExtended(IV25Bus bus)
    {
        byte subOpcode = Fetch(bus);
        switch (subOpcode)
        {
            case >= 0x10 and <= 0x17:
                ExecuteBitOpRm(bus, subOpcode, false);
                break;
            case >= 0x18 and <= 0x1f:
                ExecuteBitOpRm(bus, subOpcode, true);
                break;
            case 0x20:
                ExecuteAdd4s(bus);
                break;
            case 0x22:
                ExecuteSub4s(bus, compareOnly: false);
                break;
            case 0x25:
                ExecuteMoveStackPointerFromSavedBank(bus);
                break;
            case 0x26:
                ExecuteSub4s(bus, compareOnly: true);
                break;
            case 0x28:
                ExecuteRol4(bus);
                break;
            case 0x2a:
                ExecuteRor4(bus);
                break;
            case 0x2d:
                ExecuteRegisterBankSwitch(bus, FetchRegisterBankOperand(bus));
                break;
            case 0x91:
                ExecuteReturnFromRegisterBankInterrupt(bus);
                break;
            case 0x92:
                ExecuteFinishInterrupt(bus);
                break;
            case 0x94:
                ExecuteTaskSwitch(bus, FetchRegisterBankOperand(bus));
                break;
            case 0x95:
                ExecuteMoveStackPointerToBank(bus, FetchRegisterBankOperand(bus));
                break;
            case 0x9e:
                Halted = true;
                LastStopReason = string.Create(CultureInfo.InvariantCulture, $"V25 STOP pc=0x{PreviousPc:X5}");
                break;
            default:
                Halted = true;
                LastStopReason = string.Create(CultureInfo.InvariantCulture, $"unimplemented NEC extension 0x0F 0x{subOpcode:X2} pc=0x{PreviousPc:X5}");
                break;
        }
    }

    private int FetchRegisterBankOperand(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        if (modrm < 0xc0)
        {
            Halted = true;
            LastStopReason = string.Create(CultureInfo.InvariantCulture, $"invalid V25 register-bank ModRM 0x{modrm:X2} pc=0x{PreviousPc:X5}");
            return 0;
        }

        return _regs[modrm & 7] & 7;
    }

    private void ExecuteRegisterBankSwitch(IV25Bus bus, int bank)
    {
        ushort flags = CompressFlags();
        StoreCurrentBankState(bus);
        _registerBank = bank & 7;
        LoadCurrentBankState(bus);
        WriteBankWord(bus, _registerBank, PswSaveOffset, flags);
        WriteBankWord(bus, _registerBank, PcSaveOffset, _ip);
        _ip = ReadBankWord(bus, _registerBank, VectorPcOffset);
    }

    private void ExecuteReturnFromRegisterBankInterrupt(IV25Bus bus)
    {
        ushort flags = ReadBankWord(bus, _registerBank, PswSaveOffset);
        ushort returnIp = ReadBankWord(bus, _registerBank, PcSaveOffset);
        int bank = (flags >> 12) & 7;
        ExpandFlags(flags);
        StoreCurrentBankState(bus);
        _registerBank = bank;
        LoadCurrentBankState(bus);
        _ip = returnIp;
    }

    private void ExecuteTaskSwitch(IV25Bus bus, int bank)
    {
        ushort flags = CompressFlags();
        StoreCurrentBankState(bus);
        WriteBankWord(bus, _registerBank, PswSaveOffset, flags);
        WriteBankWord(bus, _registerBank, PcSaveOffset, _ip);
        _registerBank = bank & 7;
        LoadCurrentBankState(bus);
        _ip = ReadBankWord(bus, _registerBank, PcSaveOffset);
        ExpandFlags(ReadBankWord(bus, _registerBank, PswSaveOffset));
    }

    private void ExecuteMoveStackPointerFromSavedBank(IV25Bus bus)
    {
        int bank = (ReadBankWord(bus, _registerBank, PswSaveOffset) >> 12) & 7;
        _sregs[Ss] = ReadBankWord(bus, bank, SegmentBankOffsets[Ss]);
        _regs[Sp] = ReadBankWord(bus, bank, RegisterBankOffsets[Sp]);
    }

    private void ExecuteMoveStackPointerToBank(IV25Bus bus, int bank)
    {
        bank &= 7;
        WriteBankWord(bus, bank, SegmentBankOffsets[Ss], _sregs[Ss]);
        WriteBankWord(bus, bank, RegisterBankOffsets[Sp], _regs[Sp]);
    }

    private static void ExecuteFinishInterrupt(IV25Bus bus)
    {
        byte lo = bus.V25ReadInternal8(InternalIsprOffset);
        byte hi = bus.V25ReadInternal8(InternalIsprOffset + 1);
        ushort ispr = (ushort)(lo | (hi << 8));
        for (ushort bit = 1; bit != 0; bit <<= 1)
        {
            if ((ispr & bit) == 0)
                continue;

            ispr &= (ushort)~bit;
            break;
        }

        bus.V25WriteInternal8(InternalIsprOffset, (byte)ispr);
        bus.V25WriteInternal8(InternalIsprOffset + 1, (byte)(ispr >> 8));
    }

    private void ExecuteBitOpRm(IV25Bus bus, byte subOpcode, bool immediateBit)
    {
        int op = (subOpcode >> 1) & 3;
        bool word = (subOpcode & 1) != 0;
        if (word)
        {
            ushort value = ReadRm16Resolved(bus, Fetch(bus), out bool isRegister, out int register, out uint address);
            int bit = immediateBit ? Fetch(bus) & 0x0f : GetReg8(Cx) & 0x0f;
            ushort result = ApplyBitOp16(op, value, bit);
            if (op != 0)
                WriteRm16Resolved(bus, isRegister, register, address, result);
        }
        else
        {
            byte value = ReadRm8Resolved(bus, Fetch(bus), out bool isRegister, out int register, out uint address);
            int bit = immediateBit ? Fetch(bus) & 7 : GetReg8(Cx) & 7;
            byte result = ApplyBitOp8(op, value, bit);
            if (op != 0)
                WriteRm8Resolved(bus, isRegister, register, address, result);
        }
    }

    private byte ApplyBitOp8(int op, byte value, int bit)
    {
        byte mask = (byte)(1 << bit);
        if (op == 0)
        {
            SetBitTestFlags((value & mask) != 0);
            return value;
        }

        return op switch
        {
            1 => (byte)(value & ~mask),
            2 => (byte)(value | mask),
            _ => (byte)(value ^ mask)
        };
    }

    private ushort ApplyBitOp16(int op, ushort value, int bit)
    {
        ushort mask = (ushort)(1 << bit);
        if (op == 0)
        {
            SetBitTestFlags((value & mask) != 0);
            return value;
        }

        return op switch
        {
            1 => (ushort)(value & ~mask),
            2 => (ushort)(value | mask),
            _ => (ushort)(value ^ mask)
        };
    }

    private void SetBitTestFlags(bool bitSet)
    {
        _zero = (byte)(bitSet ? 0 : 1);
        _sign = 0;
        _parity = _zero;
        _carry = 0;
        _overflow = 0;
    }

    private void ExecuteAdd4s(IV25Bus bus)
    {
        int count = (GetReg8(Cx) + 1) / 2;
        ushort si = _regs[Si];
        ushort di = _regs[Di];
        _zero = 1;
        _carry = 0;

        for (int i = 0; i < count; i++)
        {
            byte source = Read8(bus, ((uint)_sregs[Ds0] << 4) + si);
            byte dest = Read8(bus, ((uint)_sregs[Ds1] << 4) + di);
            int left = ((source >> 4) * 10) + (source & 0x0f);
            int right = ((dest >> 4) * 10) + (dest & 0x0f);
            int result = left + right + _carry;
            _carry = (byte)(result > 99 ? 1 : 0);
            result %= 100;
            byte packed = (byte)(((result / 10) << 4) | (result % 10));
            Write8(bus, ((uint)_sregs[Ds1] << 4) + di, packed);
            if (packed != 0)
                _zero = 0;
            si++;
            di++;
        }
    }

    private void ExecuteSub4s(IV25Bus bus, bool compareOnly)
    {
        int count = (GetReg8(Cx) + 1) / 2;
        ushort si = _regs[Si];
        ushort di = _regs[Di];
        _zero = 1;
        _carry = 0;

        for (int i = 0; i < count; i++)
        {
            byte dest = Read8(bus, ((uint)_sregs[Ds1] << 4) + di);
            byte source = Read8(bus, ((uint)_sregs[Ds0] << 4) + si);
            int left = ((dest >> 4) * 10) + (dest & 0x0f);
            int right = ((source >> 4) * 10) + (source & 0x0f) + _carry;
            int result;
            if (left < right)
            {
                result = left + 100 - right;
                _carry = 1;
            }
            else
            {
                result = left - right;
                _carry = 0;
            }

            byte packed = (byte)(((result / 10) << 4) | (result % 10));
            if (!compareOnly)
                Write8(bus, ((uint)_sregs[Ds1] << 4) + di, packed);
            if (packed != 0)
                _zero = 0;
            si++;
            di++;
        }
    }

    private void ExecuteRol4(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        byte value = ReadRm8Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        int rotated = (value << 4) | (GetReg8(0) & 0x0f);
        SetReg8(0, (byte)((GetReg8(0) & 0xf0) | ((rotated >> 8) & 0x0f)));
        WriteRm8Resolved(bus, isRegister, register, address, (byte)rotated);
    }

    private void ExecuteRor4(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        byte value = ReadRm8Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        int alLow = (GetReg8(0) & 0x0f) << 4;
        SetReg8(0, (byte)((GetReg8(0) & 0xf0) | (value & 0x0f)));
        WriteRm8Resolved(bus, isRegister, register, address, (byte)(alLow | (value >> 4)));
    }

    private void ExecuteStosw(IV25Bus bus)
    {
        int count = _rep ? _regs[Cx] : 1;
        while (count-- > 0)
        {
            Write16(bus, ((uint)_sregs[Ds1] << 4) + _regs[Di], _regs[Ax]);
            _regs[Di] = unchecked((ushort)(_regs[Di] + (_direction ? -2 : 2)));
        }

        if (_rep)
            _regs[Cx] = 0;
    }

    private void ExecuteStosb(IV25Bus bus)
    {
        int count = _rep ? _regs[Cx] : 1;
        while (count-- > 0)
        {
            Write8(bus, ((uint)_sregs[Ds1] << 4) + _regs[Di], GetReg8(0));
            _regs[Di] = unchecked((ushort)(_regs[Di] + (_direction ? -1 : 1)));
        }

        if (_rep)
            _regs[Cx] = 0;
    }

    private void ExecuteMovsb(IV25Bus bus)
    {
        int count = _rep ? _regs[Cx] : 1;
        while (count-- > 0)
        {
            byte value = Read8(bus, GetDirectAddress(Ds0, _regs[Si]));
            Write8(bus, ((uint)_sregs[Ds1] << 4) + _regs[Di], value);
            int step = _direction ? -1 : 1;
            _regs[Si] = unchecked((ushort)(_regs[Si] + step));
            _regs[Di] = unchecked((ushort)(_regs[Di] + step));
        }

        if (_rep)
            _regs[Cx] = 0;
    }

    private void ExecuteMovsw(IV25Bus bus)
    {
        int count = _rep ? _regs[Cx] : 1;
        while (count-- > 0)
        {
            ushort value = Read16(bus, GetDirectAddress(Ds0, _regs[Si]));
            Write16(bus, ((uint)_sregs[Ds1] << 4) + _regs[Di], value);
            int step = _direction ? -2 : 2;
            _regs[Si] = unchecked((ushort)(_regs[Si] + step));
            _regs[Di] = unchecked((ushort)(_regs[Di] + step));
        }

        if (_rep)
            _regs[Cx] = 0;
    }

    private void ExecuteGroup81(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        ushort dest = ReadRm16Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        ushort immediate = FetchWord(bus);
        ushort result = ApplyAlu16(op, dest, immediate);
        if (op != 7)
            WriteRm16Resolved(bus, isRegister, register, address, result);
    }

    private void ExecuteGroupFF(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        switch (op)
        {
            case 0:
                {
                    ushort dest = ReadRm16Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
                    ushort result = (ushort)(dest + 1);
                    SetIncFlags16(dest, result);
                    WriteRm16Resolved(bus, isRegister, register, address, result);
                    return;
                }
            case 1:
                {
                    ushort dest = ReadRm16Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
                    ushort result = (ushort)(dest - 1);
                    SetDecFlags16(dest, result);
                    WriteRm16Resolved(bus, isRegister, register, address, result);
                    return;
                }
            case 2:
                {
                    ushort target = ReadRm16Resolved(bus, modrm, out _, out _, out _);
                    Push(bus, _ip);
                    _ip = target;
                }
                return;
            case 4:
                _ip = ReadRm16Resolved(bus, modrm, out _, out _, out _);
                return;
            case 6:
                Push(bus, ReadRm16Resolved(bus, modrm, out _, out _, out _));
                return;
            default:
                // MAME only logs unsupported FF group variants and continues.
                _ = ReadRm16Resolved(bus, modrm, out _, out _, out _);
                return;
        }
    }

    private void ExecuteGroup83(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        ushort dest = ReadRm16Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        ushort immediate = (ushort)(short)(sbyte)Fetch(bus);
        ushort result = ApplyAlu16(op, dest, immediate);
        if (op != 7)
            WriteRm16Resolved(bus, isRegister, register, address, result);
    }

    private byte ShiftRotate8(int op, byte value, int count, out bool supported)
    {
        supported = true;
        if (count == 0)
            return value;

        byte result = value;
        for (int i = 0; i < count; i++)
        {
            switch (op)
            {
                case 0:
                    result = (byte)((result << 1) | (result >> 7));
                    _carry = (byte)(result & 1);
                    break;
                case 1:
                    _carry = (byte)(result & 1);
                    result = (byte)((result >> 1) | (_carry << 7));
                    break;
                case 2:
                    {
                        byte oldCarry = _carry;
                        _carry = (byte)(result >> 7);
                        result = (byte)((result << 1) | oldCarry);
                        break;
                    }
                case 3:
                    {
                        byte oldCarry = _carry;
                        _carry = (byte)(result & 1);
                        result = (byte)((result >> 1) | (oldCarry << 7));
                        break;
                    }
                case 4:
                case 6:
                    _carry = (byte)(result >> 7);
                    result = (byte)(result << 1);
                    break;
                case 5:
                    _carry = (byte)(result & 1);
                    result = (byte)(result >> 1);
                    break;
                case 7:
                    _carry = (byte)(result & 1);
                    result = (byte)((sbyte)result >> 1);
                    break;
                default:
                    supported = false;
                    return value;
            }
        }

        if (op > 3)
            SetShiftResultFlags8(result);
        _overflow = op switch
        {
            0 or 2 or 4 or 6 => (byte)(((_carry ^ (result >> 7)) & 1) != 0 ? 1 : 0),
            1 or 3 => (byte)(((result ^ (result << 1)) & 0x80) != 0 ? 1 : 0),
            5 => (byte)((value & 0x80) != 0 ? 1 : 0),
            _ => 0
        };
        return result;
    }

    private ushort ShiftRotate16(int op, ushort value, int count, out bool supported)
    {
        supported = true;
        if (count == 0)
            return value;

        ushort result = value;
        for (int i = 0; i < count; i++)
        {
            switch (op)
            {
                case 0:
                    result = (ushort)((result << 1) | (result >> 15));
                    _carry = (byte)(result & 1);
                    break;
                case 1:
                    _carry = (byte)(result & 1);
                    result = (ushort)((result >> 1) | (_carry << 15));
                    break;
                case 2:
                    {
                        byte oldCarry = _carry;
                        _carry = (byte)(result >> 15);
                        result = (ushort)((result << 1) | oldCarry);
                        break;
                    }
                case 3:
                    {
                        byte oldCarry = _carry;
                        _carry = (byte)(result & 1);
                        result = (ushort)((result >> 1) | (oldCarry << 15));
                        break;
                    }
                case 4:
                case 6:
                    _carry = (byte)(result >> 15);
                    result = (ushort)(result << 1);
                    break;
                case 5:
                    _carry = (byte)(result & 1);
                    result = (ushort)(result >> 1);
                    break;
                case 7:
                    _carry = (byte)(result & 1);
                    result = (ushort)((short)result >> 1);
                    break;
                default:
                    supported = false;
                    return value;
            }
        }

        if (op > 3)
            SetShiftResultFlags16(result);
        _overflow = op switch
        {
            0 or 2 or 4 or 6 => (byte)(((_carry ^ (result >> 15)) & 1) != 0 ? 1 : 0),
            1 or 3 => (byte)(((result ^ (result << 1)) & 0x8000) != 0 ? 1 : 0),
            5 => (byte)((value & 0x8000) != 0 ? 1 : 0),
            _ => 0
        };
        return result;
    }

    private void StopUnsupportedGroup(byte opcode, int op)
    {
        Halted = true;
        LastStopReason = string.Create(CultureInfo.InvariantCulture, $"unimplemented group opcode 0x{opcode:X2}/{op} pc=0x{PreviousPc:X5}");
    }

    private bool EvaluateCondition(int condition)
    {
        return condition switch
        {
            0x0 => _overflow != 0,
            0x1 => _overflow == 0,
            0x2 => _carry != 0,
            0x3 => _carry == 0,
            0x4 => _zero != 0,
            0x5 => _zero == 0,
            0x6 => _carry != 0 || _zero != 0,
            0x7 => _carry == 0 && _zero == 0,
            0x8 => _sign != 0,
            0x9 => _sign == 0,
            0xa => _parity != 0,
            0xb => _parity == 0,
            0xc => _sign != _overflow,
            0xd => _sign == _overflow,
            0xe => _zero != 0 || _sign != _overflow,
            _ => _zero == 0 && _sign == _overflow
        };
    }

    private void Branch8(IV25Bus bus, bool taken)
    {
        sbyte displacement = (sbyte)Fetch(bus);
        if (taken)
            _ip = unchecked((ushort)(_ip + displacement));
    }

    private byte Fetch(IV25Bus bus)
    {
        return bus.V25Read8(PcBeforeFetch() & 0x0f_ffff);
    }

    private uint PcBeforeFetch()
    {
        uint pc = Pc;
        _ip++;
        return pc;
    }

    private ushort FetchWord(IV25Bus bus)
    {
        byte lo = Fetch(bus);
        byte hi = Fetch(bus);
        return (ushort)(lo | (hi << 8));
    }

    private void Push(IV25Bus bus, ushort value)
    {
        _regs[Sp] -= 2;
        Write16(bus, ((uint)_sregs[Ss] << 4) + _regs[Sp], value);
    }

    private ushort Pop(IV25Bus bus)
    {
        ushort value = Read16(bus, ((uint)_sregs[Ss] << 4) + _regs[Sp]);
        _regs[Sp] += 2;
        return value;
    }

    private byte ReadRm8(IV25Bus bus, byte modrm)
    {
        if (modrm >= 0xc0)
            return GetReg8(modrm & 7);
        uint address = GetEffectiveAddress(bus, modrm, out _, out _);
        return Read8(bus, address);
    }

    private ushort ReadRm16(IV25Bus bus, byte modrm)
    {
        if (modrm >= 0xc0)
            return _regs[modrm & 7];
        uint address = GetEffectiveAddress(bus, modrm, out _, out _);
        return Read16(bus, address);
    }

    private byte ReadRm8Resolved(IV25Bus bus, byte modrm, out bool isRegister, out int register, out uint address)
    {
        register = modrm & 7;
        if (modrm >= 0xc0)
        {
            isRegister = true;
            address = 0;
            return GetReg8(register);
        }

        isRegister = false;
        address = GetEffectiveAddress(bus, modrm, out _, out _);
        return Read8(bus, address);
    }

    private ushort ReadRm16Resolved(IV25Bus bus, byte modrm, out bool isRegister, out int register, out uint address)
    {
        register = modrm & 7;
        if (modrm >= 0xc0)
        {
            isRegister = true;
            address = 0;
            return _regs[register];
        }

        isRegister = false;
        address = GetEffectiveAddress(bus, modrm, out _, out _);
        return Read16(bus, address);
    }

    private void WriteRm8(IV25Bus bus, byte modrm, byte value)
    {
        if (modrm >= 0xc0)
        {
            SetReg8(modrm & 7, value);
            return;
        }

        uint address = GetEffectiveAddress(bus, modrm, out _, out _);
        Write8(bus, address, value);
    }

    private void WriteRm16(IV25Bus bus, byte modrm, ushort value)
    {
        if (modrm >= 0xc0)
        {
            _regs[modrm & 7] = value;
            return;
        }

        uint address = GetEffectiveAddress(bus, modrm, out _, out _);
        Write16(bus, address, value);
    }

    private void WriteRm8Resolved(IV25Bus bus, bool isRegister, int register, uint address, byte value)
    {
        if (isRegister)
            SetReg8(register, value);
        else
            Write8(bus, address, value);
    }

    private void WriteRm16Resolved(IV25Bus bus, bool isRegister, int register, uint address, ushort value)
    {
        if (isRegister)
            _regs[register] = value;
        else
            Write16(bus, address, value);
    }

    private uint GetEffectiveAddress(IV25Bus bus, byte modrm, out ushort offset, out int defaultSegment)
    {
        int mod = modrm >> 6;
        int rm = modrm & 7;
        int baseValue = rm switch
        {
            0 => _regs[Bx] + _regs[Si],
            1 => _regs[Bx] + _regs[Di],
            2 => _regs[Bp] + _regs[Si],
            3 => _regs[Bp] + _regs[Di],
            4 => _regs[Si],
            5 => _regs[Di],
            6 => mod == 0 ? 0 : _regs[Bp],
            _ => _regs[Bx]
        };

        defaultSegment = rm is 2 or 3 || (rm == 6 && mod != 0) ? Ss : Ds0;
        int displacement = 0;
        if (mod == 0 && rm == 6)
            displacement = FetchWord(bus);
        else if (mod == 1)
            displacement = (sbyte)Fetch(bus);
        else if (mod == 2)
            displacement = (short)FetchWord(bus);

        offset = (ushort)(baseValue + displacement);
        return GetSegmentBase(defaultSegment) + offset;
    }

    private uint GetDirectAddress(int defaultSegment, ushort offset)
    {
        return GetSegmentBase(defaultSegment) + offset;
    }

    private uint GetSegmentBase(int defaultSegment)
    {
        return _segmentPrefix && (defaultSegment == Ds0 || defaultSegment == Ss)
            ? _prefixBase
            : (uint)_sregs[defaultSegment] << 4;
    }

    private byte GetReg8(int index)
    {
        return index switch
        {
            0 => (byte)_regs[Ax],
            1 => (byte)_regs[Cx],
            2 => (byte)_regs[Dx],
            3 => (byte)_regs[Bx],
            4 => (byte)(_regs[Ax] >> 8),
            5 => (byte)(_regs[Cx] >> 8),
            6 => (byte)(_regs[Dx] >> 8),
            _ => (byte)(_regs[Bx] >> 8)
        };
    }

    private void SetReg8(int index, byte value)
    {
        switch (index)
        {
            case 0:
                _regs[Ax] = (ushort)((_regs[Ax] & 0xff00) | value);
                break;
            case 1:
                _regs[Cx] = (ushort)((_regs[Cx] & 0xff00) | value);
                break;
            case 2:
                _regs[Dx] = (ushort)((_regs[Dx] & 0xff00) | value);
                break;
            case 3:
                _regs[Bx] = (ushort)((_regs[Bx] & 0xff00) | value);
                break;
            case 4:
                _regs[Ax] = (ushort)((_regs[Ax] & 0x00ff) | (value << 8));
                break;
            case 5:
                _regs[Cx] = (ushort)((_regs[Cx] & 0x00ff) | (value << 8));
                break;
            case 6:
                _regs[Dx] = (ushort)((_regs[Dx] & 0x00ff) | (value << 8));
                break;
            default:
                _regs[Bx] = (ushort)((_regs[Bx] & 0x00ff) | (value << 8));
                break;
        }
    }

    private static ushort BankOffset(int bank, int byteOffset)
    {
        return (ushort)(((bank & 7) << 5) + (byteOffset & 0x1f));
    }

    private static ushort ReadBankWord(IV25Bus bus, int bank, int byteOffset)
    {
        ushort offset = BankOffset(bank, byteOffset);
        byte lo = bus.V25ReadInternal8(offset);
        byte hi = bus.V25ReadInternal8((ushort)(offset + 1));
        return (ushort)(lo | (hi << 8));
    }

    private static void WriteBankWord(IV25Bus bus, int bank, int byteOffset, ushort value)
    {
        ushort offset = BankOffset(bank, byteOffset);
        bus.V25WriteInternal8(offset, (byte)value);
        bus.V25WriteInternal8((ushort)(offset + 1), (byte)(value >> 8));
    }

    private void StoreCurrentBankState(IV25Bus bus)
    {
        for (int i = 0; i < _regs.Length; i++)
            WriteBankWord(bus, _registerBank, RegisterBankOffsets[i], _regs[i]);

        for (int i = 0; i < _sregs.Length; i++)
            WriteBankWord(bus, _registerBank, SegmentBankOffsets[i], _sregs[i]);
    }

    private void LoadCurrentBankState(IV25Bus bus)
    {
        for (int i = 0; i < _regs.Length; i++)
            _regs[i] = ReadBankWord(bus, _registerBank, RegisterBankOffsets[i]);

        for (int i = 0; i < _sregs.Length; i++)
            _sregs[i] = ReadBankWord(bus, _registerBank, SegmentBankOffsets[i]);
    }

    private bool TryReadCurrentBankByte(ushort offset, out byte value)
    {
        int bankBase = (_registerBank & 7) << 5;
        int relative = (offset - bankBase) & 0x01ff;
        if ((uint)relative >= 0x20)
        {
            value = 0;
            return false;
        }

        for (int i = 0; i < RegisterBankOffsets.Length; i++)
        {
            int registerOffset = RegisterBankOffsets[i];
            if (relative == registerOffset)
            {
                value = (byte)_regs[i];
                return true;
            }

            if (relative == registerOffset + 1)
            {
                value = (byte)(_regs[i] >> 8);
                return true;
            }
        }

        for (int i = 0; i < SegmentBankOffsets.Length; i++)
        {
            int segmentOffset = SegmentBankOffsets[i];
            if (relative == segmentOffset)
            {
                value = (byte)_sregs[i];
                return true;
            }

            if (relative == segmentOffset + 1)
            {
                value = (byte)(_sregs[i] >> 8);
                return true;
            }
        }

        value = 0;
        return false;
    }

    private bool TryWriteCurrentBankByte(ushort offset, byte value)
    {
        int bankBase = (_registerBank & 7) << 5;
        int relative = (offset - bankBase) & 0x01ff;
        if ((uint)relative >= 0x20)
            return false;

        for (int i = 0; i < RegisterBankOffsets.Length; i++)
        {
            int registerOffset = RegisterBankOffsets[i];
            if (relative == registerOffset)
            {
                _regs[i] = (ushort)((_regs[i] & 0xff00) | value);
                return true;
            }

            if (relative == registerOffset + 1)
            {
                _regs[i] = (ushort)((_regs[i] & 0x00ff) | (value << 8));
                return true;
            }
        }

        for (int i = 0; i < SegmentBankOffsets.Length; i++)
        {
            int segmentOffset = SegmentBankOffsets[i];
            if (relative == segmentOffset)
            {
                _sregs[i] = (ushort)((_sregs[i] & 0xff00) | value);
                return true;
            }

            if (relative == segmentOffset + 1)
            {
                _sregs[i] = (ushort)((_sregs[i] & 0x00ff) | (value << 8));
                return true;
            }
        }

        return false;
    }

    private ushort CompressFlags()
    {
        ushort flags = 0;
        if (_carry != 0)
            flags |= 0x0001;
        if (_parity != 0)
            flags |= 0x0004;
        if (_zero != 0)
            flags |= 0x0040;
        if (_sign != 0)
            flags |= 0x0080;
        if (_direction)
            flags |= 0x0400;
        if (_overflow != 0)
            flags |= 0x0800;

        flags |= (ushort)((_registerBank & 7) << 12);
        return flags;
    }

    private void ExpandFlags(ushort flags)
    {
        _carry = (byte)((flags & 0x0001) != 0 ? 1 : 0);
        _parity = (byte)((flags & 0x0004) != 0 ? 1 : 0);
        _zero = (byte)((flags & 0x0040) != 0 ? 1 : 0);
        _sign = (byte)((flags & 0x0080) != 0 ? 1 : 0);
        _direction = (flags & 0x0400) != 0;
        _overflow = (byte)((flags & 0x0800) != 0 ? 1 : 0);
    }

    private byte Read8(IV25Bus bus, uint address)
    {
        address &= 0x0f_ffff;
        if (bus.V25TryGetInternalOffset(address, out ushort offset))
        {
            if (TryReadCurrentBankByte(offset, out byte value))
                return value;

            return bus.V25ReadInternal8(offset);
        }

        return bus.V25Read8(address);
    }

    private ushort Read16(IV25Bus bus, uint address)
    {
        byte lo = Read8(bus, address);
        byte hi = Read8(bus, address + 1);
        return (ushort)(lo | (hi << 8));
    }

    private void Write8(IV25Bus bus, uint address, byte value)
    {
        address &= 0x0f_ffff;
        if (bus.V25TryGetInternalOffset(address, out ushort offset))
        {
            TryWriteCurrentBankByte(offset, value);
            bus.V25WriteInternal8(offset, value);
            return;
        }

        bus.V25Write8(address, value);
    }

    private void Write16(IV25Bus bus, uint address, ushort value)
    {
        Write8(bus, address, (byte)value);
        Write8(bus, address + 1, (byte)(value >> 8));
    }

    private void SetIncFlags16(ushort oldValue, ushort result)
    {
        SetResultFlags16(result);
        _overflow = (byte)(oldValue == 0x7fff ? 1 : 0);
    }

    private void SetDecFlags16(ushort oldValue, ushort result)
    {
        SetResultFlags16(result);
        _overflow = (byte)(oldValue == 0x8000 ? 1 : 0);
    }

    private void SetIncFlags8(byte oldValue, byte result)
    {
        SetResultFlags8(result);
        _overflow = (byte)(oldValue == 0x7f ? 1 : 0);
    }

    private void SetDecFlags8(byte oldValue, byte result)
    {
        SetResultFlags8(result);
        _overflow = (byte)(oldValue == 0x80 ? 1 : 0);
    }

    private void SetAddFlags8(byte left, byte right, byte result)
    {
        _carry = (byte)(left + right > 0xff ? 1 : 0);
        SetResultFlags8(result);
        _overflow = (byte)((~(left ^ right) & (left ^ result) & 0x80) != 0 ? 1 : 0);
    }

    private void SetAddFlags16(ushort left, ushort right, ushort result)
    {
        _carry = (byte)(left + right > 0xffff ? 1 : 0);
        SetResultFlags16(result);
        _overflow = (byte)((~(left ^ right) & (left ^ result) & 0x8000) != 0 ? 1 : 0);
    }

    private void SetAdcFlags8(byte left, byte right, int carry, byte result)
    {
        _carry = (byte)(left + right + carry > 0xff ? 1 : 0);
        SetResultFlags8(result);
        _overflow = (byte)(((left ^ result) & (right ^ result) & 0x80) != 0 ? 1 : 0);
    }

    private void SetAdcFlags16(ushort left, ushort right, int carry, ushort result)
    {
        _carry = (byte)(left + right + carry > 0xffff ? 1 : 0);
        SetResultFlags16(result);
        _overflow = (byte)(((left ^ result) & (right ^ result) & 0x8000) != 0 ? 1 : 0);
    }

    private void SetLogicalFlags16(ushort result)
    {
        _carry = 0;
        _overflow = 0;
        SetResultFlags16(result);
    }

    private void SetLogicalFlags8(byte result)
    {
        _carry = 0;
        _overflow = 0;
        SetResultFlags8(result);
    }

    private void SetShiftResultFlags8(byte result)
    {
        SetResultFlags8(result);
    }

    private void SetRotateResultFlags8(byte result)
    {
        SetResultFlags8(result);
    }

    private void SetShiftResultFlags16(ushort result)
    {
        SetResultFlags16(result);
    }

    private void SetRotateResultFlags16(ushort result)
    {
        SetResultFlags16(result);
    }

    private void SetSubFlags8(byte left, byte right, byte result)
    {
        _carry = (byte)(left < right ? 1 : 0);
        SetResultFlags8(result);
        _overflow = (byte)(((left ^ right) & (left ^ result) & 0x80) != 0 ? 1 : 0);
    }

    private void SetSubFlags16(ushort left, ushort right, ushort result)
    {
        _carry = (byte)(left < right ? 1 : 0);
        SetResultFlags16(result);
        _overflow = (byte)(((left ^ right) & (left ^ result) & 0x8000) != 0 ? 1 : 0);
    }

    private void SetSbbFlags8(byte left, byte right, int carry, byte result)
    {
        int subtrahend = right + carry;
        byte adjustedRight = (byte)subtrahend;
        _carry = (byte)(left < subtrahend ? 1 : 0);
        SetResultFlags8(result);
        _overflow = (byte)(((left ^ adjustedRight) & (left ^ result) & 0x80) != 0 ? 1 : 0);
    }

    private void SetSbbFlags16(ushort left, ushort right, int carry, ushort result)
    {
        int subtrahend = right + carry;
        ushort adjustedRight = (ushort)subtrahend;
        _carry = (byte)(left < subtrahend ? 1 : 0);
        SetResultFlags16(result);
        _overflow = (byte)(((left ^ adjustedRight) & (left ^ result) & 0x8000) != 0 ? 1 : 0);
    }

    private void SetResultFlags8(byte result)
    {
        _zero = (byte)(result == 0 ? 1 : 0);
        _sign = (byte)((result & 0x80) != 0 ? 1 : 0);
        _parity = EvenParity(result);
    }

    private void SetResultFlags16(ushort result)
    {
        _zero = (byte)(result == 0 ? 1 : 0);
        _sign = (byte)((result & 0x8000) != 0 ? 1 : 0);
        _parity = EvenParity((byte)result);
    }

    private static byte EvenParity(byte value)
    {
        value ^= (byte)(value >> 4);
        value &= 0x0f;
        return (byte)(((0x6996 >> value) & 1) == 0 ? 1 : 0);
    }
}
