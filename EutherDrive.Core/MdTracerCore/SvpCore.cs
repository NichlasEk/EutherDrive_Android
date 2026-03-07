using System;

namespace EutherDrive.Core.MdTracerCore;

internal sealed class SvpCore
{
    private const ushort SvpEntryPoint = 0x0400;

    private const int DramLenWords = 128 * 1024 / 2;
    private const int IramLenWords = 1024;
    private const int InternalRamLenWords = 256;

    private const int StackLen = 6;

    private const uint ExternalMemoryMask = (1u << 21) - 1;
    private static readonly int SvpInstructionsPerM68kCycle = ParsePositiveIntEnv("EUTHERDRIVE_SVP_INSNS_PER_M68K", 2);

    internal enum PmcWaitingFor
    {
        Address,
        Mode,
    }

    internal sealed class StatusRegister
    {
        public byte LoopSize;
        public bool St5;
        public bool St6;
        public bool Zero;
        public bool Negative;

        public byte LoopModulo => LoopSize != 0 ? (byte)(1 << LoopSize) : (byte)0;

        public bool StBitsSet => St5 || St6;

        public void Write(ushort value)
        {
            LoopSize = (byte)(value & 0x07);
            St5 = Bit(value, 5);
            St6 = Bit(value, 6);
            Zero = Bit(value, 13);
            Negative = Bit(value, 15);
        }

        public ushort ToWord()
        {
            return (ushort)((Negative ? 1 : 0) << 15 |
                            (Zero ? 1 : 0) << 13 |
                            (St6 ? 1 : 0) << 6 |
                            (St5 ? 1 : 0) << 5 |
                            LoopSize);
        }
    }

    internal sealed class StackRegister
    {
        private readonly ushort[] _stack = new ushort[StackLen];
        private byte _pointer;

        public void Push(ushort value)
        {
            _stack[_pointer] = value;
            _pointer = (byte)((_pointer + 1) % StackLen);
        }

        public ushort Pop()
        {
            _pointer = _pointer == 0 ? (byte)(StackLen - 1) : (byte)(_pointer - 1);
            return _stack[_pointer];
        }
    }

    internal sealed class ProgrammableMemoryRegister
    {
        public uint Address;
        public uint AutoIncrement;
        public bool AutoIncrementNegative;
        public ushort AutoIncrementBits;
        public bool SpecialIncrementMode;
        public bool OverwriteMode;

        public void Initialize(ushort address, ushort mode)
        {
            Address = (uint)address | (uint)(mode & 0x001F) << 16;
            OverwriteMode = Bit(mode, 10);

            AutoIncrementBits = (ushort)((mode >> 11) & 0x07);
            AutoIncrement = AutoIncrementBits switch
            {
                0 => 0,
                7 => 128,
                _ => (uint)(1 << (AutoIncrementBits - 1)),
            };

            SpecialIncrementMode = Bit(mode, 14);
            AutoIncrementNegative = Bit(mode, 15);
        }

        public uint GetAndIncrementAddress()
        {
            uint address = Address;

            if (SpecialIncrementMode)
            {
                Address = !Bit(address, 0)
                    ? (Address + 1) & ExternalMemoryMask
                    : (Address + 31) & ExternalMemoryMask;
            }
            else if (AutoIncrement != 0)
            {
                Address = AutoIncrementNegative
                    ? unchecked(Address - AutoIncrement) & ExternalMemoryMask
                    : unchecked(Address + AutoIncrement) & ExternalMemoryMask;
            }

            return address;
        }
    }

    internal sealed class ProgrammableMemoryControlRegister
    {
        public PmcWaitingFor WaitingFor = PmcWaitingFor.Address;
        public ushort Address;
        public ushort Mode;

        public ushort Read()
        {
            ushort value = WaitingFor == PmcWaitingFor.Address
                ? Address
                : (ushort)((Address << 4) | (Address >> 12));

            WaitingFor = Toggle(WaitingFor);
            return value;
        }

        public void Write(ushort value)
        {
            if (WaitingFor == PmcWaitingFor.Address)
            {
                Address = value;
            }
            else
            {
                Mode = value;
            }

            WaitingFor = Toggle(WaitingFor);
        }

        public void UpdateFrom(ProgrammableMemoryRegister pmRegister)
        {
            Address = (ushort)pmRegister.Address;
            Mode = (ushort)(((pmRegister.AutoIncrementNegative ? 1 : 0) << 15) |
                            ((pmRegister.SpecialIncrementMode ? 1 : 0) << 14) |
                            (pmRegister.AutoIncrementBits << 11) |
                            ((pmRegister.OverwriteMode ? 1 : 0) << 10) |
                            (pmRegister.Address >> 16));
        }
    }

    internal sealed class ExternalStatusRegister
    {
        public ushort Value;
        public bool M68kWritten;
        public bool SspWritten;

        public void M68kWrite(ushort value)
        {
            Value = value;
            M68kWritten = true;
        }

        public void SspWrite(ushort value)
        {
            Value = value;
            SspWritten = true;
        }

        public ushort Status()
        {
            return (ushort)(((M68kWritten ? 1 : 0) << 1) | (SspWritten ? 1 : 0));
        }

        public ushort M68kReadStatus()
        {
            ushort status = Status();
            SspWritten = false;
            return status;
        }

        public ushort SspReadStatus()
        {
            ushort status = Status();
            M68kWritten = false;
            return status;
        }
    }

    internal sealed class Registers
    {
        public ushort X;
        public ushort Y;
        public uint Accumulator;
        public readonly StatusRegister Status = new();
        public readonly StackRegister Stack = new();
        public ushort Pc = SvpEntryPoint;

        public readonly ProgrammableMemoryRegister[] PmRead =
        {
            new(), new(), new(), new(), new(),
        };

        public readonly ProgrammableMemoryRegister[] PmWrite =
        {
            new(), new(), new(), new(), new(),
        };

        public readonly ProgrammableMemoryControlRegister Pmc = new();
        public readonly ExternalStatusRegister Xst = new();

        public readonly byte[] Ram0Pointers = new byte[3];
        public readonly byte[] Ram1Pointers = new byte[3];

        public uint Product()
        {
            return unchecked(2u * (uint)(short)X * (uint)(short)Y);
        }
    }

    internal readonly Registers RegistersState = new();
    internal readonly ushort[] Dram = new ushort[DramLenWords];
    internal readonly ushort[] Iram = new ushort[IramLenWords];
    internal readonly ushort[] Ram0 = new ushort[InternalRamLenWords];
    internal readonly ushort[] Ram1 = new ushort[InternalRamLenWords];

    internal bool Halted;
    internal bool DramDirty;
    private static readonly bool TraceSvp =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SVP"), "1", StringComparison.Ordinal);
    private int _traceBudget = 64;
    private bool _loggedEntryGate;
    private bool _loggedXstWrite;
    private bool _loggedIdleWait;

    public bool Enabled => true;

    public void Tick(ReadOnlySpan<byte> romBytes, uint m68kCycles)
    {
        if (Halted)
        {
            return;
        }

        uint instructionCount = (uint)SvpInstructionsPerM68kCycle * m68kCycles;
        for (uint i = 0; i < instructionCount; i++)
        {
            if (RegistersState.Pc is 0x0425 or 0x2789)
            {
                if (!DramDirty)
                {
                    if (TraceSvp && !_loggedIdleWait && _traceBudget > 0)
                    {
                        _loggedIdleWait = true;
                        _traceBudget--;
                        Console.WriteLine($"[SVP] idle wait loop at PC=0x{RegistersState.Pc:X4}, waiting DRAM mailbox write");
                    }
                    return;
                }

                DramDirty = false;
            }

            if (RegistersState.Pc == SvpEntryPoint && !RegistersState.Xst.M68kWritten)
            {
                if (TraceSvp && !_loggedEntryGate && _traceBudget > 0)
                {
                    _loggedEntryGate = true;
                    _traceBudget--;
                    Console.WriteLine("[SVP] waiting at entry PC=0x0400 for XST write from 68k");
                }
                return;
            }

            Ssp1601.ExecuteInstruction(this, romBytes);
        }
    }

    public ushort M68kReadWord(uint address, ReadOnlySpan<byte> romBytes)
    {
        uint masked = address & 0x00FF_FFFF;

        return masked switch
        {
            <= 0x1F_FFFF => ReadRomWordByByteAddress(romBytes, masked),
            >= 0x30_0000 and <= 0x37_FFFF => Dram[(masked & 0x1_FFFF) >> 1],
            0xA1_5000 or 0xA1_5002 => RegistersState.Xst.Value,
            0xA1_5004 => RegistersState.Xst.M68kReadStatus(),
            _ => 0xFFFF,
        };
    }

    public void M68kWriteByte(uint address, byte value)
    {
        uint masked = address & 0x00FF_FFFF;

        if (masked >= 0x30_0000 && masked <= 0x37_FFFF)
        {
            int wordAddress = (int)((masked & 0x1_FFFF) >> 1);
            ushort word = Dram[wordAddress];
            Dram[wordAddress] = Bit(masked, 0)
                ? (ushort)((word & 0xFF00) | value)
                : (ushort)((word & 0x00FF) | (value << 8));

            if (wordAddress is 0x7F03 or 0x7F04)
            {
                DramDirty = true;
            }

            return;
        }

        if (Bit(masked, 0))
        {
            M68kWriteWord(masked & ~1u, value);
        }
        else
        {
            M68kWriteWord(masked, (ushort)(value << 8));
        }
    }

    public void M68kWriteWord(uint address, ushort value)
    {
        uint masked = address & 0x00FF_FFFF;

        if (masked >= 0x30_0000 && masked <= 0x37_FFFF)
        {
            int wordAddress = (int)((masked & 0x1_FFFF) >> 1);
            Dram[wordAddress] = value;

            if (wordAddress is 0x7F03 or 0x7F04)
            {
                DramDirty = true;
            }

            return;
        }

        switch (masked)
        {
            case 0xA1_5000:
            case 0xA1_5002:
                RegistersState.Xst.M68kWrite(value);
                if (TraceSvp && !_loggedXstWrite && _traceBudget > 0)
                {
                    _loggedXstWrite = true;
                    _traceBudget--;
                    Console.WriteLine($"[SVP] XST write by 68k: 0x{value:X4}");
                }
                break;
            case 0xA1_5006:
                Halted = value == 0x000A;
                if (TraceSvp && _traceBudget > 0)
                {
                    _traceBudget--;
                    Console.WriteLine($"[SVP] HALT write by 68k: 0x{value:X4} halted={Halted}");
                }
                break;
        }
    }

    internal ushort ReadProgramMemory(ushort address, ReadOnlySpan<byte> romBytes)
    {
        if (address <= 0x03FF)
        {
            return Iram[address];
        }

        return ReadRomWordByWordAddress(romBytes, address);
    }

    internal ushort ReadExternalMemory(uint address, ReadOnlySpan<byte> romBytes)
    {
        uint masked = address & ExternalMemoryMask;

        if (masked <= 0x0F_FFFF)
        {
            return ReadRomWordByWordAddress(romBytes, (int)masked);
        }

        if (masked >= 0x18_0000 && masked <= 0x18_FFFF)
        {
            return Dram[masked & 0xFFFF];
        }

        if (masked >= 0x1C_8000 && masked <= 0x1C_83FF)
        {
            return Iram[masked & 0x03FF];
        }

        return 0xFFFF;
    }

    internal void WriteExternalMemory(uint address, ushort value)
    {
        uint masked = address & ExternalMemoryMask;

        if (masked >= 0x18_0000 && masked <= 0x18_FFFF)
        {
            Dram[masked & 0xFFFF] = value;
            return;
        }

        if (masked >= 0x1C_8000 && masked <= 0x1C_83FF)
        {
            Iram[masked & 0x03FF] = value;
        }
    }

    private static PmcWaitingFor Toggle(PmcWaitingFor value)
    {
        return value == PmcWaitingFor.Address ? PmcWaitingFor.Mode : PmcWaitingFor.Address;
    }

    private static bool Bit(uint value, int bit)
    {
        return ((value >> bit) & 1) != 0;
    }

    private static bool Bit(ushort value, int bit)
    {
        return ((value >> bit) & 1) != 0;
    }

    private static int ParsePositiveIntEnv(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int parsed) && parsed > 0)
            return parsed;
        return fallback;
    }

    internal static ushort ReadRomWordByWordAddress(ReadOnlySpan<byte> romBytes, int wordAddress)
    {
        int byteAddress = wordAddress << 1;
        return ReadRomWordByByteAddress(romBytes, (uint)byteAddress);
    }

    internal static ushort ReadRomWordByWordAddress(ReadOnlySpan<byte> romBytes, uint wordAddress)
    {
        uint byteAddress = wordAddress << 1;
        return ReadRomWordByByteAddress(romBytes, byteAddress);
    }

    private static ushort ReadRomWordByByteAddress(ReadOnlySpan<byte> romBytes, uint byteAddress)
    {
        if (byteAddress + 1 >= (uint)romBytes.Length)
        {
            return 0xFFFF;
        }

        byte hi = romBytes[(int)byteAddress];
        byte lo = romBytes[(int)(byteAddress + 1)];
        return (ushort)((hi << 8) | lo);
    }
}
