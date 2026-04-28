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

    private static readonly byte[] GoldenAxe2OpcodeTable =
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
    private ushort _ip;
    private uint _prefixBase;
    private bool _segmentPrefix;
    private byte _carry;
    private byte _zero = 1;
    private byte _sign;
    private byte _overflow;

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
        _prefixBase = 0;
        _segmentPrefix = false;
        Halted = false;
        LastStopReason = string.Empty;
    }

    public int ExecuteInstruction()
    {
        IV25Bus bus = _bus ?? throw new InvalidOperationException("V25 has not been reset with a bus.");
        if (Halted)
            return 0;

        PreviousPc = Pc;
        byte encrypted = Fetch(bus);
        byte opcode = GoldenAxe2OpcodeTable[encrypted];
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
            case 0x3a:
                ExecuteCmpR8Rm8(bus);
                return;
            case 0x43:
                _regs[Bx]++;
                SetIncDecFlags16(_regs[Bx]);
                return;
            case 0x49:
                _regs[Cx]--;
                SetIncDecFlags16(_regs[Cx]);
                return;
            case 0x75:
                Branch8(bus, _zero == 0);
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
        byte opcode = GoldenAxe2OpcodeTable[encrypted];
        if (opcode == 0)
            opcode = encrypted;
        LastOpcode = opcode;
        ExecuteOpcode(bus, opcode);
        _prefixBase = oldBase;
        _segmentPrefix = oldPrefix;
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
            case 7:
                SetSubFlags8(dest, immediate, (byte)(dest - immediate));
                break;
            default:
                StopUnsupportedGroup(0x80, op);
                break;
        }
    }

    private void ExecuteGroup81(IV25Bus bus)
    {
        byte modrm = Fetch(bus);
        int op = (modrm >> 3) & 7;
        ushort dest = ReadRm16(bus, modrm);
        ushort immediate = FetchWord(bus);
        if (op == 7)
            SetSubFlags16(dest, immediate, (ushort)(dest - immediate));
        else
            StopUnsupportedGroup(0x81, op);
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
        uint segmentBase = _segmentPrefix && defaultSegment is Ds0 or Ss
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
