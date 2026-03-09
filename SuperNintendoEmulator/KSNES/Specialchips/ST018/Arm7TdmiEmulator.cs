using System;
using System.Numerics;

namespace KSNES.Specialchips.ST018;

internal enum MemoryCycle
{
    N,
    S,
}

internal enum LoadIndexing
{
    Post = 0,
    Pre = 1,
}

internal enum IndexOp
{
    Subtract = 0,
    Add = 1,
}

internal enum WriteBack
{
    No = 0,
    Yes = 1,
}

internal enum LoadSize
{
    Word = 0,
    Byte = 1,
}

internal enum HalfwordLoadType
{
    UnsignedHalfword,
    SignedByte,
    SignedHalfword,
}

internal enum CpuState
{
    Arm = 0,
    Thumb = 1,
}

internal enum CpuMode
{
    User = 0x10,
    Fiq = 0x11,
    Irq = 0x12,
    Supervisor = 0x13,
    Abort = 0x17,
    Undefined = 0x1B,
    Illegal = 0x1E,
    System = 0x1F,
}

internal enum CpuException
{
    Reset,
    UndefinedInstruction,
    SoftwareInterrupt,
    Irq,
}

internal struct StatusRegister
{
    public bool Sign;
    public bool Zero;
    public bool Carry;
    public bool Overflow;
    public bool IrqDisabled;
    public bool FiqDisabled;
    public CpuState State;
    public CpuMode Mode;

    public static StatusRegister FromUInt32(uint value)
    {
        return new StatusRegister
        {
            Sign = (value & 0x80000000) != 0,
            Zero = (value & 0x40000000) != 0,
            Carry = (value & 0x20000000) != 0,
            Overflow = (value & 0x10000000) != 0,
            IrqDisabled = (value & 0x80) != 0,
            FiqDisabled = (value & 0x40) != 0,
            State = (value & 0x20) != 0 ? CpuState.Thumb : CpuState.Arm,
            Mode = FromModeBits(value & 0x1F),
        };
    }

    public readonly uint ToUInt32()
    {
        return (Sign ? 0x80000000u : 0u)
            | (Zero ? 0x40000000u : 0u)
            | (Carry ? 0x20000000u : 0u)
            | (Overflow ? 0x10000000u : 0u)
            | (IrqDisabled ? 0x80u : 0u)
            | (FiqDisabled ? 0x40u : 0u)
            | (State == CpuState.Thumb ? 0x20u : 0u)
            | (uint)Mode;
    }

    private static CpuMode FromModeBits(uint bits)
    {
        return (bits & 0xF) switch
        {
            0x0 => CpuMode.User,
            0x1 => CpuMode.Fiq,
            0x2 => CpuMode.Irq,
            0x3 => CpuMode.Supervisor,
            0x7 => CpuMode.Abort,
            0xB => CpuMode.Undefined,
            0xF => CpuMode.System,
            _ => CpuMode.Illegal,
        };
    }
}

internal sealed class Arm7TdmiEmulator
{
    private const uint Address60Reads = 0x40404001;
    private const int ProgramRomSizeWords = 128 * 1024 / 4;
    private const int DataRomSize = 32 * 1024;
    private const int TotalRomLen = (4 * ProgramRomSizeWords) + DataRomSize;

    private readonly uint[] _programRom;
    private readonly byte[] _dataRom;
    private readonly uint[] _ram;
    private readonly Registers _registers;
    private uint _openBus;

    private readonly uint[] _regs = new uint[16];
    private readonly uint[] _fiqRegs = new uint[5];
    private readonly uint[] _otherRegs = new uint[5];

    private uint _r13Usr;
    private uint _r14Usr;
    private uint _r13Svc;
    private uint _r14Svc;
    private uint _r13Irq;
    private uint _r14Irq;
    private uint _r13Und;
    private uint _r14Und;
    private uint _r13Abt;
    private uint _r14Abt;
    private uint _r13Fiq;
    private uint _r14Fiq;

    private StatusRegister _cpsr;
    private StatusRegister _spsrSvc;
    private StatusRegister _spsrIrq;
    private StatusRegister _spsrUnd;
    private StatusRegister _spsrAbt;
    private StatusRegister _spsrFiq;

    private readonly uint[] _prefetch = new uint[2];
    private uint _prevR15;
    private MemoryCycle _fetchCycle;

    public ulong ExecutedCycles { get; private set; }
    public ulong BusCycles { get; private set; }

    public Arm7TdmiEmulator(byte[] rom, Registers registers)
    {
        (_programRom, _dataRom) = ParseRom(rom);
        _ram = new uint[16 * 1024 / 4];
        _registers = registers;
        Reset();
    }

    public void Reset()
    {
        Array.Fill(_regs, 0u);
        Array.Fill(_fiqRegs, 0u);
        Array.Fill(_otherRegs, 0u);

        _r13Usr = _r14Usr = _r13Svc = _r14Svc = _r13Irq = _r14Irq = 0;
        _r13Und = _r14Und = _r13Abt = _r14Abt = _r13Fiq = _r14Fiq = 0;

        _spsrSvc = default;
        _spsrIrq = default;
        _spsrUnd = default;
        _spsrAbt = default;
        _spsrFiq = default;

        _cpsr = StatusRegister.FromUInt32(0xD3);
        _regs[15] = 0;
        _prefetch[0] = 0;
        _prefetch[1] = 0;
        _prevR15 = 0;
        _fetchCycle = MemoryCycle.N;
        ExecutedCycles = 0;
        BusCycles = 0;
        _openBus = 0;

        HandleException(CpuException.Reset);
    }

    public void ExecuteInstruction()
    {
        uint opcode = _prefetch[0];
        FetchOpcode();
        ExecuteArmInstruction(opcode);
        ExecutedCycles++;
    }

    private void ExecuteArmInstruction(uint instruction)
    {
        uint cond = (instruction >> 28) & 0xF;
        if (!ConditionPassed(cond))
            return;

        if ((instruction & 0x0F000000) == 0x0F000000)
        {
            HandleException(CpuException.SoftwareInterrupt);
            return;
        }

        if ((instruction & 0x0FFFFFF0) == 0x012FFF10)
        {
            ExecuteBranchExchange(instruction & 0xF);
            return;
        }

        if ((instruction & 0x0FBF0FFF) == 0x010F0000)
        {
            ExecuteMrs(instruction);
            return;
        }

        if ((instruction & 0x0DB0F000) == 0x0120F000)
        {
            ExecuteMsr(instruction, immediate: false);
            return;
        }

        if ((instruction & 0x0DB0F000) == 0x0320F000)
        {
            ExecuteMsr(instruction, immediate: true);
            return;
        }

        if ((instruction & 0x0FBF0FFF) == 0x01000090)
        {
            ExecuteSwap(instruction);
            return;
        }

        if ((instruction & 0x0FC000F0) == 0x00000090)
        {
            ExecuteMultiply(instruction);
            return;
        }

        if ((instruction & 0x0F8000F0) == 0x00800090)
        {
            ExecuteMultiplyLong(instruction);
            return;
        }

        if ((instruction & 0x0E4000F0) == 0x00000090)
        {
            ExecuteLoadHalfword(instruction);
            return;
        }

        if ((instruction & 0x0E000000) == 0x08000000)
        {
            ExecuteLoadMultiple(instruction);
            return;
        }

        if ((instruction & 0x0C000000) == 0x00000000)
        {
            ExecuteDataProcessing(instruction);
        }
        else if ((instruction & 0x0C000000) == 0x04000000)
        {
            ExecuteLoadStore(instruction);
        }
        else if ((instruction & 0x0E000000) == 0x0A000000)
        {
            ExecuteBranch(instruction, link: (instruction & 0x01000000) != 0);
        }
        else
        {
            HandleException(CpuException.UndefinedInstruction);
        }
    }

    private bool ConditionPassed(uint cond)
    {
        return cond switch
        {
            0x0 => _cpsr.Zero,
            0x1 => !_cpsr.Zero,
            0x2 => _cpsr.Carry,
            0x3 => !_cpsr.Carry,
            0x4 => _cpsr.Sign,
            0x5 => !_cpsr.Sign,
            0x6 => _cpsr.Overflow,
            0x7 => !_cpsr.Overflow,
            0x8 => _cpsr.Carry && !_cpsr.Zero,
            0x9 => !_cpsr.Carry || _cpsr.Zero,
            0xA => _cpsr.Sign == _cpsr.Overflow,
            0xB => _cpsr.Sign != _cpsr.Overflow,
            0xC => !_cpsr.Zero && _cpsr.Sign == _cpsr.Overflow,
            0xD => _cpsr.Zero || _cpsr.Sign != _cpsr.Overflow,
            0xE => true,
            _ => false,
        };
    }

    private void ExecuteDataProcessing(uint instruction)
    {
        uint opcode = (instruction >> 21) & 0xF;
        uint rn = (instruction >> 16) & 0xF;
        uint rd = (instruction >> 12) & 0xF;
        bool setFlags = (instruction & 0x00100000) != 0;

        uint operand2 = GetOperand2(instruction);
        uint rnValue = ReadRegister(rn);

        uint result = 0;
        bool carry = false;

        switch (opcode)
        {
            case 0x0:
                result = rnValue & operand2;
                break;
            case 0x1:
                result = rnValue ^ operand2;
                break;
            case 0x2:
                result = rnValue - operand2;
                carry = operand2 <= rnValue;
                break;
            case 0x3:
                result = operand2 - rnValue;
                carry = rnValue <= operand2;
                break;
            case 0x4:
                result = rnValue + operand2;
                carry = result < rnValue;
                break;
            case 0x5:
            {
                uint c = _cpsr.Carry ? 1u : 0u;
                result = rnValue + operand2 + c;
                carry = result < rnValue || (c == 1 && result == rnValue);
                break;
            }
            case 0x6:
            {
                uint c = _cpsr.Carry ? 0u : 1u;
                result = rnValue - operand2 - c;
                carry = rnValue >= operand2 + c;
                break;
            }
            case 0x7:
            {
                uint c = _cpsr.Carry ? 0u : 1u;
                result = operand2 - rnValue - c;
                carry = operand2 >= rnValue + c;
                break;
            }
            case 0x8:
                result = rnValue & operand2;
                UpdateFlags(result, carry);
                return;
            case 0x9:
                result = rnValue ^ operand2;
                UpdateFlags(result, carry);
                return;
            case 0xA:
                result = rnValue - operand2;
                carry = rnValue >= operand2;
                UpdateFlags(result, carry);
                return;
            case 0xB:
                result = rnValue + operand2;
                carry = result < rnValue;
                UpdateFlags(result, carry);
                return;
            case 0xC:
                result = rnValue | operand2;
                break;
            case 0xD:
                result = operand2;
                break;
            case 0xE:
                result = rnValue & ~operand2;
                break;
            case 0xF:
                result = ~operand2;
                break;
        }

        if (rd == 15)
        {
            _regs[15] = result & ~3u;
            RefillPrefetch();
        }
        else
        {
            _regs[rd] = result;
        }

        if (setFlags && rd != 15)
        {
            UpdateFlags(result, carry);
        }
    }

    private void ExecuteLoadStore(uint instruction)
    {
        bool load = (instruction & 0x00100000) != 0;
        bool byteAccess = (instruction & 0x00400000) != 0;
        bool writeBack = (instruction & 0x00200000) != 0;
        bool preIndex = (instruction & 0x01000000) != 0;
        bool addOffset = (instruction & 0x00800000) != 0;

        uint rn = (instruction >> 16) & 0xF;
        uint rd = (instruction >> 12) & 0xF;

        uint baseAddress = ReadRegister(rn);
        uint offset = GetLoadStoreOffset(instruction);
        if (!addOffset)
            offset = unchecked(0u - offset);

        uint address = preIndex ? baseAddress + offset : baseAddress;

        if (load)
        {
            uint value = ReadMemory(address, byteAccess ? 0u : 2u);
            if (rd == 15)
            {
                _regs[15] = value & ~3u;
                RefillPrefetch();
            }
            else
            {
                _regs[rd] = value;
            }
        }
        else
        {
            uint value = rd == 15 ? _prevR15 : _regs[rd];
            WriteMemory(address, value, byteAccess ? 0u : 2u);
        }

        if (writeBack)
        {
            _regs[rn] = preIndex ? address : baseAddress + offset;
        }
    }

    private void ExecuteBranch(uint instruction, bool link)
    {
        uint offset = instruction & 0x00FFFFFF;
        if ((offset & 0x00800000) != 0)
            offset |= 0xFF000000;

        offset <<= 2;
        if (link)
            _regs[14] = unchecked(_prevR15 - 4u);
        _regs[15] = _prevR15 + offset;
        RefillPrefetch();
    }

    private void ExecuteBranchExchange(uint rn)
    {
        uint newPc = ReadRegister(rn);
        _cpsr.State = (newPc & 1) != 0 ? CpuState.Thumb : CpuState.Arm;
        _regs[15] = newPc;
        RefillPrefetch();
    }

    private void ExecuteMrs(uint instruction)
    {
        uint rd = (instruction >> 12) & 0xF;
        bool spsr = (instruction & (1u << 22)) != 0;
        _regs[rd] = spsr ? ReadSpsrOrCpsr() : _cpsr.ToUInt32();
    }

    private void ExecuteMsr(uint instruction, bool immediate)
    {
        uint operand = immediate ? ParseRotatedImmediate(instruction) : ReadRegister(instruction & 0xF);
        bool spsr = (instruction & (1u << 22)) != 0;
        bool control = (instruction & (1u << 16)) != 0;
        bool flags = (instruction & (1u << 19)) != 0;

        const uint controlMask = 0xFF;
        const uint flagsMask = 0xFu << 28;

        uint current = spsr ? ReadSpsrOrCpsr() : _cpsr.ToUInt32();
        if (!control)
            operand = (operand & ~controlMask) | (current & controlMask);
        if (!flags)
            operand = (operand & ~flagsMask) | (current & flagsMask);

        if (spsr)
            WriteSpsr(operand);
        else
            WriteCpsr(operand);
    }

    private void ExecuteMultiply(uint instruction)
    {
        bool accumulate = (instruction & (1u << 21)) != 0;
        bool setFlags = (instruction & (1u << 20)) != 0;
        uint rd = (instruction >> 16) & 0xF;
        uint rn = (instruction >> 12) & 0xF;
        uint rs = (instruction >> 8) & 0xF;
        uint rm = instruction & 0xF;

        uint result = ReadRegister(rm) * ReadRegister(rs);
        if (accumulate)
            result += ReadRegister(rn);

        if (rd == 15)
        {
            _regs[15] = result & ~3u;
            RefillPrefetch();
        }
        else
        {
            _regs[rd] = result;
        }

        if (setFlags)
        {
            _cpsr.Sign = (result & 0x80000000) != 0;
            _cpsr.Zero = result == 0;
        }

        uint operand = ReadRegister(rs);
        uint internalCycles = (operand & 0xFFFFFF00) == 0 || (operand & 0xFFFFFF00) == 0xFFFFFF00 ? 1u
            : (operand & 0xFFFF0000) == 0 || (operand & 0xFFFF0000) == 0xFFFF0000 ? 2u
            : (operand & 0xFF000000) == 0 || (operand & 0xFF000000) == 0xFF000000 ? 3u
            : 4u;
        if (accumulate)
            internalCycles++;
        InternalCycles(internalCycles);
    }

    private void ExecuteMultiplyLong(uint instruction)
    {
        bool signed = (instruction & (1u << 22)) != 0;
        bool accumulate = (instruction & (1u << 21)) != 0;
        bool setFlags = (instruction & (1u << 20)) != 0;
        uint rdHi = (instruction >> 16) & 0xF;
        uint rdLo = (instruction >> 12) & 0xF;
        uint rs = (instruction >> 8) & 0xF;
        uint rm = instruction & 0xF;

        ulong product = signed
            ? unchecked((ulong)((long)(int)ReadRegister(rm) * (long)(int)ReadRegister(rs)))
            : (ulong)ReadRegister(rm) * ReadRegister(rs);

        if (accumulate)
        {
            ulong existing = ((ulong)_regs[rdHi] << 32) | _regs[rdLo];
            product = unchecked(product + existing);
        }

        _regs[rdLo] = (uint)product;
        _regs[rdHi] = (uint)(product >> 32);

        if (setFlags)
        {
            _cpsr.Sign = (product & 0x8000000000000000UL) != 0;
            _cpsr.Zero = product == 0;
        }

        uint operand = ReadRegister(rs);
        uint internalCycles = (operand & 0xFFFFFF00) == 0 || (signed && (operand & 0xFFFFFF00) == 0xFFFFFF00) ? 1u
            : (operand & 0xFFFF0000) == 0 || (signed && (operand & 0xFFFF0000) == 0xFFFF0000) ? 2u
            : (operand & 0xFF000000) == 0 || (signed && (operand & 0xFF000000) == 0xFF000000) ? 3u
            : 4u;
        InternalCycles(1u + internalCycles + (accumulate ? 1u : 0u));
    }

    private void ExecuteSwap(uint instruction)
    {
        uint rm = instruction & 0xF;
        uint rd = (instruction >> 12) & 0xF;
        uint rn = (instruction >> 16) & 0xF;
        bool byteSwap = (instruction & (1u << 22)) != 0;
        uint address = _regs[rn];

        if (byteSwap)
        {
            uint oldValue = ReadMemory(address, 0u);
            WriteMemory(address, ReadRegister(rm) & 0xFF, 0u);
            _regs[rd] = oldValue;
        }
        else
        {
            uint oldValue = ReadMemory(address, 2u);
            oldValue = RotateRight(oldValue, (address & 3) * 8);
            WriteMemory(address, ReadRegister(rm), 2u);
            _regs[rd] = oldValue;
        }
    }

    private void ExecuteLoadHalfword(uint instruction)
    {
        bool load = (instruction & (1u << 20)) != 0;
        LoadIndexing indexing = ((instruction & (1u << 24)) != 0) ? LoadIndexing.Pre : LoadIndexing.Post;
        IndexOp indexOp = ((instruction & (1u << 23)) != 0) ? IndexOp.Add : IndexOp.Subtract;
        WriteBack writeBack = ((instruction & (1u << 21)) != 0) ? WriteBack.Yes : WriteBack.No;
        bool immediateOffset = (instruction & (1u << 22)) != 0;
        uint rn = (instruction >> 16) & 0xF;
        uint rd = (instruction >> 12) & 0xF;

        uint offset = immediateOffset
            ? (((instruction >> 4) & 0xF0) | (instruction & 0xF))
            : ReadRegister(instruction & 0xF);

        HalfwordLoadType loadType = ((instruction >> 5) & 0x3) switch
        {
            0x1 => HalfwordLoadType.UnsignedHalfword,
            0x2 => HalfwordLoadType.SignedByte,
            0x3 => HalfwordLoadType.SignedHalfword,
            _ => HalfwordLoadType.UnsignedHalfword,
        };

        LoadHalfword(load, rn, rd, offset, loadType, indexing, indexOp, writeBack);
    }

    private void LoadHalfword(
        bool load,
        uint rn,
        uint rd,
        uint offset,
        HalfwordLoadType loadType,
        LoadIndexing indexing,
        IndexOp indexOp,
        WriteBack writeBack)
    {
        uint signedOffset = indexOp == IndexOp.Add ? offset : unchecked(0u - offset);
        uint address = ReadRegister(rn);
        if (indexing == LoadIndexing.Pre)
            address = unchecked(address + signedOffset);

        if (load)
        {
            uint value = loadType switch
            {
                HalfwordLoadType.UnsignedHalfword => RotateRight(ReadMemory(address, 1u), (address & 1u) * 8u),
                HalfwordLoadType.SignedByte => unchecked((uint)(int)(sbyte)ReadMemory(address, 0u)),
                HalfwordLoadType.SignedHalfword => (address & 1u) == 0
                    ? unchecked((uint)(int)(short)ReadMemory(address, 1u))
                    : unchecked((uint)(int)(sbyte)ReadMemory(address, 0u)),
                _ => 0u,
            };

            if (rd == 15)
            {
                _regs[15] = value;
                RefillPrefetch();
            }
            else
            {
                _regs[rd] = value;
            }

            InternalCycles(1);
        }
        else
        {
            WriteMemory(address, ReadRegister(rd) & 0xFFFF, 1u);
            _fetchCycle = MemoryCycle.N;
        }

        if (!(load && rn == rd))
        {
            if (indexing == LoadIndexing.Post)
                _regs[rn] = unchecked(address + signedOffset);
            else if (writeBack == WriteBack.Yes)
                _regs[rn] = address;
        }
    }

    private void ExecuteLoadMultiple(uint instruction)
    {
        bool load = (instruction & (1u << 20)) != 0;
        bool increment = (instruction & (1u << 23)) != 0;
        bool after = (instruction & (1u << 24)) == 0;
        WriteBack writeBack = ((instruction & (1u << 21)) != 0) ? WriteBack.Yes : WriteBack.No;
        bool sBit = (instruction & (1u << 22)) != 0;
        uint rn = (instruction >> 16) & 0xF;
        uint registerBits = instruction & 0xFFFF;

        LoadMultiple(load, increment, after, registerBits, rn, writeBack, sBit);
    }

    private void LoadMultiple(
        bool load,
        bool increment,
        bool after,
        uint registerBits,
        uint rn,
        WriteBack writeBack,
        bool sBit)
    {
        bool emptyList = registerBits == 0;
        if (emptyList)
            registerBits = 1u << 15;

        uint count = emptyList ? 16u : (uint)BitOperations.PopCount(registerBits);
        bool r15Loaded = (registerBits & (1u << 15)) != 0;
        uint baseAddress = ReadRegister(rn);
        uint finalAddress = increment
            ? unchecked(baseAddress + 4u * count)
            : unchecked(baseAddress - 4u * count);

        uint address = increment ? baseAddress : finalAddress;
        bool needWriteBack = writeBack == WriteBack.Yes;

        for (int r = 0; r < 16; r++)
        {
            if ((registerBits & (1u << r)) == 0)
                continue;

            if (load && needWriteBack)
            {
                _regs[rn] = finalAddress;
                needWriteBack = false;
            }

            if (!(after ^ !increment))
                address = unchecked(address + 4u);

            if (load)
            {
                uint value = ReadMemory(address, 2u);
                if (r == 15)
                {
                    _regs[15] = value;
                    if (sBit)
                    {
                        uint spsr = ReadSpsrOrCpsr();
                        WriteCpsr(spsr);
                    }
                }
                else
                {
                    _regs[r] = value;
                }
            }
            else
            {
                uint value = sBit ? GetUserModeRegister((uint)r) : _regs[r];
                WriteMemory(address, value, 2u);
            }

            if (after ^ !increment)
                address = unchecked(address + 4u);

            if (!load && needWriteBack)
            {
                _regs[rn] = finalAddress;
                needWriteBack = false;
            }
        }

        if (load && r15Loaded)
            RefillPrefetch();
        if (load)
            InternalCycles(1);
        else
            _fetchCycle = MemoryCycle.N;
    }

    private uint GetOperand2(uint instruction)
    {
        bool immediate = (instruction & 0x02000000) != 0;
        if (immediate)
        {
            return ParseRotatedImmediate(instruction);
        }

        uint rm = instruction & 0xF;
        uint shiftType = (instruction >> 5) & 0x3;
        uint shiftAmount = (instruction >> 7) & 0x1F;
        uint value = ReadRegister(rm);
        return Shift(value, shiftType, shiftAmount);
    }

    private uint GetLoadStoreOffset(uint instruction)
    {
        bool immediateOffset = (instruction & 0x02000000) == 0;
        if (immediateOffset)
            return instruction & 0xFFF;

        uint rm = instruction & 0xF;
        uint shiftType = (instruction >> 5) & 0x3;
        uint shiftAmount = (instruction >> 7) & 0x1F;
        uint value = ReadRegister(rm);
        return Shift(value, shiftType, shiftAmount);
    }

    private static uint Shift(uint value, uint type, uint amount)
    {
        if (amount == 0)
            return value;

        return type switch
        {
            0 => value << (int)amount,
            1 => value >> (int)amount,
            2 => (uint)((int)value >> (int)amount),
            3 => RotateRight(value, amount),
            _ => value,
        };
    }

    private static uint RotateRight(uint value, uint amount)
    {
        amount &= 31;
        if (amount == 0)
            return value;

        return (value >> (int)amount) | (value << (int)(32 - amount));
    }

    private static uint ParseRotatedImmediate(uint instruction)
    {
        uint imm = instruction & 0xFF;
        uint rotate = ((instruction >> 8) & 0xF) * 2;
        return RotateRight(imm, rotate);
    }

    private void UpdateFlags(uint result, bool carry)
    {
        _cpsr.Sign = (result & 0x80000000) != 0;
        _cpsr.Zero = result == 0;
        _cpsr.Carry = carry;
    }

    private void RefillPrefetch()
    {
        _fetchCycle = MemoryCycle.N;
        _regs[15] &= ~3u;
        FetchOpcode();
        FetchOpcode();
    }

    private void FetchOpcode()
    {
        _prevR15 = _regs[15];
        MemoryCycle cycle = _fetchCycle;
        _fetchCycle = MemoryCycle.S;
        _prefetch[0] = _prefetch[1];
        _prefetch[1] = ReadMemory(_regs[15], 2u, cycle);
        _regs[15] += 4u;
    }

    private uint ReadRegister(uint register)
    {
        return register == 15 ? _prevR15 : _regs[register];
    }

    private void HandleException(CpuException exception)
    {
        CpuMode mode = exception switch
        {
            CpuException.Reset or CpuException.SoftwareInterrupt => CpuMode.Supervisor,
            CpuException.UndefinedInstruction => CpuMode.Undefined,
            CpuException.Irq => CpuMode.Irq,
            _ => CpuMode.Supervisor,
        };

        if (exception != CpuException.Reset)
        {
            StatusRegister old = _cpsr;
            ChangeMode(mode);
            SetSpsr(mode, old);
            _regs[14] = ReturnAddress(exception, old.State, _prevR15);
        }
        else
        {
            _cpsr.Mode = mode;
        }

        _cpsr.State = CpuState.Arm;
        _cpsr.IrqDisabled = true;
        if (exception == CpuException.Reset)
            _cpsr.FiqDisabled = true;

        _regs[15] = exception switch
        {
            CpuException.Reset => 0x00,
            CpuException.UndefinedInstruction => 0x04,
            CpuException.SoftwareInterrupt => 0x08,
            CpuException.Irq => 0x18,
            _ => 0x00,
        };

        RefillPrefetch();
    }

    private static uint ReturnAddress(CpuException exception, CpuState state, uint r15)
    {
        return (state, exception) switch
        {
            (CpuState.Arm, _) => unchecked(r15 - 4u),
            (CpuState.Thumb, CpuException.UndefinedInstruction or CpuException.SoftwareInterrupt) => unchecked(r15 - 2u),
            (CpuState.Thumb, CpuException.Irq) => r15,
            (CpuState.Thumb, CpuException.Reset) => r15,
            _ => r15,
        };
    }

    private void ChangeMode(CpuMode newMode)
    {
        if (newMode == _cpsr.Mode)
            return;

        switch (_cpsr.Mode)
        {
            case CpuMode.User:
            case CpuMode.System:
                _r13Usr = _regs[13];
                _r14Usr = _regs[14];
                break;
            case CpuMode.Supervisor:
                _r13Svc = _regs[13];
                _r14Svc = _regs[14];
                break;
            case CpuMode.Irq:
                _r13Irq = _regs[13];
                _r14Irq = _regs[14];
                break;
            case CpuMode.Undefined:
                _r13Und = _regs[13];
                _r14Und = _regs[14];
                break;
            case CpuMode.Abort:
                _r13Abt = _regs[13];
                _r14Abt = _regs[14];
                break;
            case CpuMode.Fiq:
                _r13Fiq = _regs[13];
                _r14Fiq = _regs[14];
                Array.Copy(_regs, 8, _fiqRegs, 0, 5);
                Array.Copy(_otherRegs, 0, _regs, 8, 5);
                break;
        }

        switch (newMode)
        {
            case CpuMode.User:
            case CpuMode.System:
                _regs[13] = _r13Usr;
                _regs[14] = _r14Usr;
                break;
            case CpuMode.Supervisor:
                _regs[13] = _r13Svc;
                _regs[14] = _r14Svc;
                break;
            case CpuMode.Irq:
                _regs[13] = _r13Irq;
                _regs[14] = _r14Irq;
                break;
            case CpuMode.Undefined:
                _regs[13] = _r13Und;
                _regs[14] = _r14Und;
                break;
            case CpuMode.Abort:
                _regs[13] = _r13Abt;
                _regs[14] = _r14Abt;
                break;
            case CpuMode.Fiq:
                Array.Copy(_regs, 8, _otherRegs, 0, 5);
                Array.Copy(_fiqRegs, 0, _regs, 8, 5);
                _regs[13] = _r13Fiq;
                _regs[14] = _r14Fiq;
                break;
        }

        _cpsr.Mode = newMode;
    }

    private void SetSpsr(CpuMode mode, StatusRegister value)
    {
        switch (mode)
        {
            case CpuMode.Supervisor:
                _spsrSvc = value;
                break;
            case CpuMode.Irq:
                _spsrIrq = value;
                break;
            case CpuMode.Undefined:
                _spsrUnd = value;
                break;
            case CpuMode.Abort:
                _spsrAbt = value;
                break;
            case CpuMode.Fiq:
                _spsrFiq = value;
                break;
        }
    }

    private uint ReadSpsrOrCpsr()
    {
        return _cpsr.Mode switch
        {
            CpuMode.Supervisor => _spsrSvc.ToUInt32(),
            CpuMode.Irq => _spsrIrq.ToUInt32(),
            CpuMode.Undefined => _spsrUnd.ToUInt32(),
            CpuMode.Abort => _spsrAbt.ToUInt32(),
            CpuMode.Fiq => _spsrFiq.ToUInt32(),
            _ => _cpsr.ToUInt32(),
        };
    }

    private void WriteSpsr(uint value)
    {
        StatusRegister registerValue = StatusRegister.FromUInt32(value);
        switch (_cpsr.Mode)
        {
            case CpuMode.Supervisor:
                _spsrSvc = registerValue;
                break;
            case CpuMode.Irq:
                _spsrIrq = registerValue;
                break;
            case CpuMode.Undefined:
                _spsrUnd = registerValue;
                break;
            case CpuMode.Abort:
                _spsrAbt = registerValue;
                break;
            case CpuMode.Fiq:
                _spsrFiq = registerValue;
                break;
        }
    }

    private void WriteCpsr(uint value)
    {
        StatusRegister newCpsr = StatusRegister.FromUInt32(value);
        if (_cpsr.Mode != CpuMode.User)
            ChangeMode(newCpsr.Mode);

        _cpsr.Sign = newCpsr.Sign;
        _cpsr.Zero = newCpsr.Zero;
        _cpsr.Carry = newCpsr.Carry;
        _cpsr.Overflow = newCpsr.Overflow;

        if (_cpsr.Mode != CpuMode.User)
        {
            _cpsr.IrqDisabled = newCpsr.IrqDisabled;
            _cpsr.FiqDisabled = newCpsr.FiqDisabled;
            _cpsr.State = newCpsr.State;
            _cpsr.Mode = newCpsr.Mode;
        }
    }

    private uint ReadMemory(uint address, uint size, MemoryCycle cycle = MemoryCycle.N)
    {
        BusCycles++;

        if (address >= 0x00000000 && address <= 0x1FFFFFFF)
        {
            uint romAddr = (address >> 2) & (uint)(_programRom.Length - 1);
            uint word = _programRom[romAddr];
            uint value = size switch
            {
                0 => (word >> (int)((address & 3) * 8)) & 0xFF,
                1 => (word >> (int)((address & 2) * 8)) & 0xFFFF,
                _ => word,
            };
            UpdateOpenBus(value, size);
            return value;
        }

        if (address >= 0x40000000 && address <= 0x5FFFFFFF)
        {
            byte? value = _registers.ArmRead(address);
            if (value.HasValue)
            {
                UpdateOpenBus(value.Value, 0u);
                return value.Value;
            }

            return ReadOpenBus(address, size);
        }

        if (address >= 0x60000000 && address <= 0x7FFFFFFF)
        {
            uint value = size == 0 ? ((Address60Reads >> (int)((address & 3) * 8)) & 0xFF) : Address60Reads;
            UpdateOpenBus(value, size == 0 ? 0u : 2u);
            return value;
        }

        if (address >= 0xA0000000 && address <= 0xBFFFFFFF)
        {
            uint romAddr = address & (uint)(_dataRom.Length - 1);
            uint value = size switch
            {
                0 => _dataRom[romAddr],
                1 => (uint)(_dataRom[romAddr] | (_dataRom[(romAddr + 1) & (uint)(_dataRom.Length - 1)] << 8)),
                _ => (uint)(_dataRom[romAddr]
                    | (_dataRom[(romAddr + 1) & (uint)(_dataRom.Length - 1)] << 8)
                    | (_dataRom[(romAddr + 2) & (uint)(_dataRom.Length - 1)] << 16)
                    | (_dataRom[(romAddr + 3) & (uint)(_dataRom.Length - 1)] << 24)),
            };
            UpdateOpenBus(value, size);
            return value;
        }

        if (address >= 0xE0000000 && address <= 0xFFFFFFFF)
        {
            uint ramAddr = (address >> 2) & (uint)(_ram.Length - 1);
            uint word = _ram[ramAddr];
            uint value = size switch
            {
                0 => (word >> (int)((address & 3) * 8)) & 0xFF,
                1 => (word >> (int)((address & 2) * 8)) & 0xFFFF,
                _ => word,
            };
            UpdateOpenBus(value, size);
            return value;
        }

        return ReadOpenBus(address, size);
    }

    private void WriteMemory(uint address, uint value, uint size)
    {
        BusCycles++;
        UpdateOpenBus(value, size);

        if (address >= 0x40000000 && address <= 0x5FFFFFFF)
        {
            _registers.ArmWrite(address, (byte)(value & 0xFF));
            return;
        }

        if (address >= 0xE0000000 && address <= 0xFFFFFFFF)
        {
            uint ramAddr = (address >> 2) & (uint)(_ram.Length - 1);
            if (size == 0)
            {
                uint shift = (address & 3) * 8;
                uint mask = ~(0xFFu << (int)shift);
                _ram[ramAddr] = (_ram[ramAddr] & mask) | ((value & 0xFF) << (int)shift);
            }
            else if (size == 1)
            {
                uint shift = (address & 2) * 8;
                uint mask = ~(0xFFFFu << (int)shift);
                _ram[ramAddr] = (_ram[ramAddr] & mask) | ((value & 0xFFFF) << (int)shift);
            }
            else if (size == 2)
            {
                _ram[ramAddr] = value;
            }
        }
    }

    private void InternalCycles(uint cycles)
    {
        BusCycles += cycles;
    }

    private uint ReadOpenBus(uint address, uint size)
    {
        return size switch
        {
            0 => (_openBus >> (int)((address & 3) * 8)) & 0xFF,
            1 => (_openBus >> (int)((address & 2) * 8)) & 0xFFFF,
            _ => _openBus,
        };
    }

    private void UpdateOpenBus(uint value, uint size)
    {
        _openBus = size switch
        {
            0 => (uint)(byte)value * 0x01010101u,
            1 => (value & 0xFFFF) | ((value & 0xFFFF) << 16),
            _ => value,
        };
    }

    private uint GetUserModeRegister(uint register)
    {
        if (register < 8 || register == 15)
            return _regs[register];

        if (register < 13)
            return _cpsr.Mode == CpuMode.Fiq ? _otherRegs[register - 8] : _regs[register];

        return register switch
        {
            13 => _cpsr.Mode switch
            {
                CpuMode.Fiq => _r13Usr,
                CpuMode.Supervisor => _r13Usr,
                CpuMode.Irq => _r13Usr,
                CpuMode.Undefined => _r13Usr,
                CpuMode.Abort => _r13Usr,
                _ => _regs[13],
            },
            14 => _cpsr.Mode switch
            {
                CpuMode.Fiq => _r14Usr,
                CpuMode.Supervisor => _r14Usr,
                CpuMode.Irq => _r14Usr,
                CpuMode.Undefined => _r14Usr,
                CpuMode.Abort => _r14Usr,
                _ => _regs[14],
            },
            _ => _regs[register],
        };
    }

    private static (uint[] ProgramRom, byte[] DataRom) ParseRom(byte[] rom)
    {
        if (rom.Length < TotalRomLen)
            throw new ArgumentException($"ST018 ROM too small: {rom.Length} bytes");

        uint[] programRom = new uint[ProgramRomSizeWords];
        for (int i = 0; i < ProgramRomSizeWords; i++)
        {
            int offset = i * 4;
            programRom[i] = BitConverter.ToUInt32(rom, offset);
        }

        byte[] dataRom = new byte[DataRomSize];
        Array.Copy(rom, 4 * ProgramRomSizeWords, dataRom, 0, DataRomSize);
        return (programRom, dataRom);
    }
}
