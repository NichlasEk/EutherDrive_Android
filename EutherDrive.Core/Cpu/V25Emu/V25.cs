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
    private byte _overflow;
    private bool _direction;
    private bool _rep;

    public uint Pc => ((uint)_sregs[Ps] << 4) + _ip;
    public uint PreviousPc { get; private set; }
    public byte LastOpcode { get; private set; }
    public bool Halted { get; private set; }
    public string LastStopReason { get; private set; } = string.Empty;

    public void Reset(IV25Bus bus)
    {
        _bus = bus ?? throw new ArgumentNullException(nameof(bus));
        Array.Clear(_regs);
        Array.Clear(_sregs);
        _ip = 0;
        _sregs[Ps] = 0xffff;
        _zero = 1;
        _carry = _sign = _overflow = 0;
        _direction = false;
        _rep = false;
        _prefixBase = 0;
        _segmentPrefix = false;
        Halted = false;
        LastStopReason = string.Empty;
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
            case 0x1e:
                Push(bus, _sregs[Ds0]);
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
                ExecuteAddR8Rm8(bus);
                return;
            case 0x03:
                ExecuteAddR16Rm16(bus);
                return;
            case 0x0a:
                ExecuteOrR8Rm8(bus);
                return;
            case 0x15:
                {
                    ushort source = FetchWord(bus);
                    ushort dest = _regs[Ax];
                    int carry = _carry;
                    ushort result = (ushort)(dest + source + carry);
                    SetAdcFlags16(dest, source, carry, result);
                    _regs[Ax] = result;
                    return;
                }
            case 0x33:
                ExecuteXorR16Rm16(bus);
                return;
            case 0x3a:
                ExecuteCmpR8Rm8(bus);
                return;
            case >= 0x40 and <= 0x47:
                _regs[opcode - 0x40]++;
                SetIncDecFlags16(_regs[opcode - 0x40]);
                return;
            case >= 0x48 and <= 0x4f:
                _regs[opcode - 0x48]--;
                SetIncDecFlags16(_regs[opcode - 0x48]);
                return;
            case >= 0x50 and <= 0x57:
                Push(bus, _regs[opcode - 0x50]);
                return;
            case >= 0x58 and <= 0x5f:
                _regs[opcode - 0x58] = Pop(bus);
                return;
            case 0x73:
                Branch8(bus, _carry == 0);
                return;
            case 0x74:
                Branch8(bus, _zero != 0);
                return;
            case 0x75:
                Branch8(bus, _zero == 0);
                return;
            case 0x78:
                Branch8(bus, _sign != 0);
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
            case >= 0xb0 and <= 0xb7:
                SetReg8(opcode - 0xb0, Fetch(bus));
                return;
            case >= 0xb8 and <= 0xbf:
                _regs[opcode - 0xb8] = FetchWord(bus);
                return;
            case 0xa0:
                SetReg8(0, Read8(bus, GetDirectAddress(Ds0, FetchWord(bus))));
                return;
            case 0xa2:
                Write8(bus, GetDirectAddress(Ds0, FetchWord(bus)), GetReg8(0));
                return;
            case 0xa4:
                ExecuteMovsb(bus);
                return;
            case 0xab:
                ExecuteStosw(bus);
                return;
            case 0xc0:
                ExecuteGroupC0(bus);
                return;
            case 0xc6:
                ExecuteMovRm8Imm8(bus);
                return;
            case 0xc7:
                ExecuteMovRm16Imm16(bus);
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
            case 0xf6:
                ExecuteGroupF6(bus);
                return;
            case 0xfe:
                ExecuteGroupFE(bus);
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

    private void ExecuteMovRm8Imm8(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        byte value = Fetch(bus);
        if (op != 0)
        {
            StopUnsupportedGroup(0xc6, op);
            return;
        }

        WriteRm8(bus, modrm, value);
    }

    private void ExecuteMovRm16Imm16(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        ushort value = FetchWord(bus);
        if (op != 0)
        {
            StopUnsupportedGroup(0xc7, op);
            return;
        }

        WriteRm16(bus, modrm, value);
    }

    private void ExecuteAddR8Rm8(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        byte source = ReadRm8(bus, modrm);
        int reg = (modrm >> 3) & 7;
        byte dest = GetReg8(reg);
        byte result = (byte)(dest + source);
        SetAddFlags8(dest, source, result);
        SetReg8(reg, result);
    }

    private void ExecuteAddR16Rm16(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        ushort source = ReadRm16(bus, modrm);
        int reg = (modrm >> 3) & 7;
        ushort dest = _regs[reg];
        ushort result = (ushort)(dest + source);
        SetAddFlags16(dest, source, result);
        _regs[reg] = result;
    }

    private void ExecuteOrR8Rm8(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int reg = (modrm >> 3) & 7;
        byte result = (byte)(GetReg8(reg) | ReadRm8(bus, modrm));
        SetLogicalFlags8(result);
        SetReg8(reg, result);
    }

    private void ExecuteXorR16Rm16(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int reg = (modrm >> 3) & 7;
        ushort result = (ushort)(_regs[reg] ^ ReadRm16(bus, modrm));
        SetLogicalFlags16(result);
        _regs[reg] = result;
    }

    private void ExecuteCmpR8Rm8(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        byte source = ReadRm8(bus, modrm);
        byte dest = GetReg8((modrm >> 3) & 7);
        SetSubFlags8(dest, source, (byte)(dest - source));
    }

    private void ExecuteGroup80(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        byte dest = ReadRm8Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        byte immediate = Fetch(bus);
        switch (op)
        {
            case 0:
                {
                    byte result = (byte)(dest + immediate);
                    SetAddFlags8(dest, immediate, result);
                    WriteRm8Resolved(bus, isRegister, register, address, result);
                    break;
                }
            case 4:
                {
                    byte result = (byte)(dest & immediate);
                    SetLogicalFlags8(result);
                    WriteRm8Resolved(bus, isRegister, register, address, result);
                    break;
                }
            case 7:
                SetSubFlags8(dest, immediate, (byte)(dest - immediate));
                break;
            default:
                StopUnsupportedGroup(0x80, op);
                break;
        }
    }

    private void ExecuteGroupC0(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        byte dest = ReadRm8Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        int count = Fetch(bus) & 0x1f;
        byte result = dest;
        switch (op)
        {
            case 1:
                for (int i = 0; i < count; i++)
                    result = (byte)((result >> 1) | (result << 7));
                SetLogicalFlags8(result);
                WriteRm8Resolved(bus, isRegister, register, address, result);
                break;
            default:
                StopUnsupportedGroup(0xc0, op);
                break;
        }
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
            default:
                StopUnsupportedGroup(0xf6, op);
                break;
        }
    }

    private void ExecuteGroupFE(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        byte dest = ReadRm8Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        switch (op)
        {
            case 1:
                {
                    byte result = (byte)(dest - 1);
                    SetIncDecFlags8(result);
                    WriteRm8Resolved(bus, isRegister, register, address, result);
                    break;
                }
            default:
                StopUnsupportedGroup(0xfe, op);
                break;
        }
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

    private void ExecuteGroup81(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        ushort dest = ReadRm16Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        ushort immediate = FetchWord(bus);
        switch (op)
        {
            case 4:
                {
                    ushort result = (ushort)(dest & immediate);
                    SetLogicalFlags16(result);
                    WriteRm16Resolved(bus, isRegister, register, address, result);
                    break;
                }
            case 7:
                SetSubFlags16(dest, immediate, (ushort)(dest - immediate));
                break;
            default:
                StopUnsupportedGroup(0x81, op);
                break;
        }
    }

    private void ExecuteGroup83(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        ushort dest = ReadRm16Resolved(bus, modrm, out bool isRegister, out int register, out uint address);
        ushort immediate = (ushort)(short)(sbyte)Fetch(bus);
        switch (op)
        {
            case 0:
                {
                    ushort result = (ushort)(dest + immediate);
                    SetAddFlags16(dest, immediate, result);
                    WriteRm16Resolved(bus, isRegister, register, address, result);
                    break;
                }
            case 5:
                {
                    ushort result = (ushort)(dest - immediate);
                    SetSubFlags16(dest, immediate, result);
                    WriteRm16Resolved(bus, isRegister, register, address, result);
                    break;
                }
            case 7:
                SetSubFlags16(dest, immediate, (ushort)(dest - immediate));
                break;
            default:
                StopUnsupportedGroup(0x83, op);
                break;
        }
    }

    private void StopUnsupportedGroup(byte opcode, int op)
    {
        Halted = true;
        LastStopReason = string.Create(CultureInfo.InvariantCulture, $"unimplemented group opcode 0x{opcode:X2}/{op} pc=0x{PreviousPc:X5}");
    }

    private void Branch8(IV25Bus bus, bool taken)
    {
        sbyte displacement = (sbyte)Fetch(bus);
        if (taken)
            _ip = unchecked((ushort)(_ip + displacement));
    }

    private byte Fetch(IV25Bus bus)
    {
        return Read8(bus, PcBeforeFetch());
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
        uint segmentBase = _segmentPrefix
            ? _prefixBase
            : (uint)_sregs[defaultSegment] << 4;
        return segmentBase + offset;
    }

    private uint GetDirectAddress(int defaultSegment, ushort offset)
    {
        uint segmentBase = _segmentPrefix
            ? _prefixBase
            : (uint)_sregs[defaultSegment] << 4;
        return segmentBase + offset;
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

    private byte Read8(IV25Bus bus, uint address)
    {
        return bus.V25Read8(address & 0xfffff);
    }

    private ushort Read16(IV25Bus bus, uint address)
    {
        byte lo = Read8(bus, address);
        byte hi = Read8(bus, address + 1);
        return (ushort)(lo | (hi << 8));
    }

    private void Write8(IV25Bus bus, uint address, byte value)
    {
        bus.V25Write8(address & 0xfffff, value);
    }

    private void Write16(IV25Bus bus, uint address, ushort value)
    {
        Write8(bus, address, (byte)value);
        Write8(bus, address + 1, (byte)(value >> 8));
    }

    private void SetIncDecFlags16(ushort result)
    {
        _zero = (byte)(result == 0 ? 1 : 0);
        _sign = (byte)((result & 0x8000) != 0 ? 1 : 0);
    }

    private void SetIncDecFlags8(byte result)
    {
        _zero = (byte)(result == 0 ? 1 : 0);
        _sign = (byte)((result & 0x80) != 0 ? 1 : 0);
    }

    private void SetAddFlags8(byte left, byte right, byte result)
    {
        _carry = (byte)(left + right > 0xff ? 1 : 0);
        _zero = (byte)(result == 0 ? 1 : 0);
        _sign = (byte)((result & 0x80) != 0 ? 1 : 0);
        _overflow = (byte)((~(left ^ right) & (left ^ result) & 0x80) != 0 ? 1 : 0);
    }

    private void SetAddFlags16(ushort left, ushort right, ushort result)
    {
        _carry = (byte)(left + right > 0xffff ? 1 : 0);
        _zero = (byte)(result == 0 ? 1 : 0);
        _sign = (byte)((result & 0x8000) != 0 ? 1 : 0);
        _overflow = (byte)((~(left ^ right) & (left ^ result) & 0x8000) != 0 ? 1 : 0);
    }

    private void SetAdcFlags16(ushort left, ushort right, int carry, ushort result)
    {
        _carry = (byte)(left + right + carry > 0xffff ? 1 : 0);
        _zero = (byte)(result == 0 ? 1 : 0);
        _sign = (byte)((result & 0x8000) != 0 ? 1 : 0);
        _overflow = (byte)(((left ^ result) & (right ^ result) & 0x8000) != 0 ? 1 : 0);
    }

    private void SetLogicalFlags16(ushort result)
    {
        _carry = 0;
        _overflow = 0;
        _zero = (byte)(result == 0 ? 1 : 0);
        _sign = (byte)((result & 0x8000) != 0 ? 1 : 0);
    }

    private void SetLogicalFlags8(byte result)
    {
        _carry = 0;
        _overflow = 0;
        _zero = (byte)(result == 0 ? 1 : 0);
        _sign = (byte)((result & 0x80) != 0 ? 1 : 0);
    }

    private void SetSubFlags8(byte left, byte right, byte result)
    {
        _carry = (byte)(left < right ? 1 : 0);
        _zero = (byte)(result == 0 ? 1 : 0);
        _sign = (byte)((result & 0x80) != 0 ? 1 : 0);
        _overflow = (byte)(((left ^ right) & (left ^ result) & 0x80) != 0 ? 1 : 0);
    }

    private void SetSubFlags16(ushort left, ushort right, ushort result)
    {
        _carry = (byte)(left < right ? 1 : 0);
        _zero = (byte)(result == 0 ? 1 : 0);
        _sign = (byte)((result & 0x8000) != 0 ? 1 : 0);
        _overflow = (byte)(((left ^ right) & (left ^ result) & 0x8000) != 0 ? 1 : 0);
    }
}
