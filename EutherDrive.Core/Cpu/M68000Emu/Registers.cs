using System;

namespace EutherDrive.Core.Cpu.M68000Emu;

internal struct ConditionCodes
{
    public bool Carry;
    public bool Overflow;
    public bool Zero;
    public bool Negative;
    public bool Extend;

    public ConditionCodes(bool carry, bool overflow, bool zero, bool negative, bool extend)
    {
        Carry = carry;
        Overflow = overflow;
        Zero = zero;
        Negative = negative;
        Extend = extend;
    }

    public static ConditionCodes FromByte(byte value)
    {
        return new ConditionCodes(
            (value & 0x01) != 0,
            (value & 0x02) != 0,
            (value & 0x04) != 0,
            (value & 0x08) != 0,
            (value & 0x10) != 0);
    }

    public byte ToByte()
    {
        return (byte)(
            (Extend ? 1 : 0) << 4 |
            (Negative ? 1 : 0) << 3 |
            (Zero ? 1 : 0) << 2 |
            (Overflow ? 1 : 0) << 1 |
            (Carry ? 1 : 0));
    }
}

internal sealed class Registers
{
    public readonly uint[] Data = new uint[8];
    public readonly uint[] Address = new uint[7];
    public uint Usp;
    public uint Ssp;
    public uint Pc;
    public ushort Prefetch;
    public ConditionCodes Ccr = ConditionCodes.FromByte(0);
    public byte InterruptPriorityMask = DefaultInterruptMask;
    public byte? PendingInterruptLevel;
    public bool SupervisorMode = true;
    public bool TraceEnabled;
    public bool AddressError;
    public bool LastInstructionWasMulDiv;
    public bool Stopped;
    public bool Frozen;

    public const byte DefaultInterruptMask = 7;

    public ushort StatusRegister()
    {
        byte lsb = Ccr.ToByte();
        byte msb = (byte)(
            (InterruptPriorityMask & 0x07)
            | (SupervisorMode ? 0x20 : 0x00)
            | (TraceEnabled ? 0x80 : 0x00));
        return (ushort)((msb << 8) | lsb);
    }

    public void SetStatusRegister(ushort value)
    {
        byte msb = (byte)(value >> 8);
        byte lsb = (byte)(value & 0xFF);
        InterruptPriorityMask = (byte)(msb & 0x07);
        SupervisorMode = (msb & 0x20) != 0;
        TraceEnabled = (msb & 0x80) != 0;
        Ccr = ConditionCodes.FromByte(lsb);
    }

    public uint StackPointer()
    {
        return SupervisorMode ? Ssp : Usp;
    }

    public void SetStackPointer(uint value)
    {
        if (SupervisorMode)
            Ssp = value;
        else
            Usp = value;
    }
}

internal readonly struct DataRegister
{
    private readonly byte _index;

    public DataRegister(byte index)
    {
        _index = index;
    }

    public byte Index => _index;

    public static readonly DataRegister[] All =
    {
        new DataRegister(0),
        new DataRegister(1),
        new DataRegister(2),
        new DataRegister(3),
        new DataRegister(4),
        new DataRegister(5),
        new DataRegister(6),
        new DataRegister(7),
    };

    public uint Read(Registers regs) => regs.Data[_index];

    public void WriteByte(Registers regs, byte value)
    {
        uint existing = regs.Data[_index];
        regs.Data[_index] = (existing & 0xFFFF_FF00) | value;
    }

    public void WriteWord(Registers regs, ushort value)
    {
        uint existing = regs.Data[_index];
        regs.Data[_index] = (existing & 0xFFFF_0000) | value;
    }

    public void WriteLong(Registers regs, uint value)
    {
        regs.Data[_index] = value;
    }
}

internal readonly struct AddressRegister
{
    private readonly byte _index;
    private static readonly bool TraceA0 =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_M68K_TRACE_A0"), "1", StringComparison.Ordinal);

    public AddressRegister(byte index)
    {
        _index = index;
    }

    public byte Index => _index;

    public static readonly AddressRegister[] All =
    {
        new AddressRegister(0),
        new AddressRegister(1),
        new AddressRegister(2),
        new AddressRegister(3),
        new AddressRegister(4),
        new AddressRegister(5),
        new AddressRegister(6),
        new AddressRegister(7),
    };

    public bool IsStackPointer => _index == 7;

    public uint Read(Registers regs)
    {
        if (_index == 7)
            return regs.SupervisorMode ? regs.Ssp : regs.Usp;
        return regs.Address[_index];
    }

    public void WriteWord(Registers regs, ushort value)
    {
        WriteLong(regs, (uint)(short)value);
    }

    public void WriteLong(Registers regs, uint value)
    {
        if (_index == 7)
        {
            if (regs.SupervisorMode)
                regs.Ssp = value;
            else
                regs.Usp = value;
            return;
        }
        if (_index == 0 && TraceA0 && regs.Address[_index] != value)
            Console.WriteLine($"[M68K-A0] pc=0x{regs.Pc:X8} op=0x{regs.Prefetch:X4} value=0x{value:X8}");
        regs.Address[_index] = value;
    }
}
