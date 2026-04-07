namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XSh2Cpu
{
    private const byte ResetInterruptMask = 15;
    private const int MaxUnsupportedLogs = 8;
    private static readonly bool TraceBootLoop =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_BOOT_LOOP"),
            "1",
            StringComparison.Ordinal);
    private int _unsupportedLogCount;

    public Sega32XSh2Cpu(string name)
    {
        Name = name;
        Registers = new Sega32XSh2Registers();
        ResetPending = true;
    }

    public string Name { get; }
    public Sega32XSh2Registers Registers { get; }
    public bool ResetPending { get; private set; }
    public ulong CycleCounter { get; private set; }

    public void RequestReset() => ResetPending = true;

    public void Execute(ulong ticks, ISega32XSh2Bus bus)
    {
        if (ticks == 0)
            return;

        if (bus.ResetAsserted)
        {
            RequestReset();
            bus.IncrementCycleCounter(5);
            CycleCounter += 5;
            return;
        }

        if (ResetPending)
        {
            ResetPending = false;
            Registers.ProgramCounter = bus.ReadLongword(0x00000000, Sega32XSh2AccessContext.InterruptVector);
            Registers.NextProgramCounter = Registers.ProgramCounter + 2;
            Registers.StackPointer = bus.ReadLongword(0x00000004, Sega32XSh2AccessContext.InterruptVector);
            Registers.VectorBaseRegister = 0;
            Registers.StatusRegister = new Sega32XSh2StatusRegister
            {
                InterruptMask = ResetInterruptMask,
            };

            bus.IncrementCycleCounter(5);
            CycleCounter += 5;
            return;
        }

        if (Registers.NextInstructionInDelaySlot)
        {
            ExecuteSingleInstruction(bus);
            ticks--;
            if (ticks == 0)
                return;
        }

        byte externalInterruptLevel = bus.InterruptLevel;
        if (externalInterruptLevel > Registers.StatusRegister.InterruptMask)
        {
            uint vectorNumber = 64u + (uint)(externalInterruptLevel >> 1);
            HandleException(externalInterruptLevel, vectorNumber, bus);
            return;
        }

        for (ulong i = 0; i < ticks; i++)
        {
            ExecuteSingleInstruction(bus);
        }
    }

    private void ExecuteSingleInstruction(ISega32XSh2Bus bus)
    {
        uint pc = Registers.ProgramCounter;
        ushort opcode = bus.ReadWord(pc, Sega32XSh2AccessContext.Fetch);

        if (TraceBootLoop && pc >= 0x00000180 && pc <= 0x00000220)
        {
            Console.WriteLine(
                $"[S32X-SH2-{Name}] pc=0x{pc:X8} op=0x{opcode:X4} " +
                $"r0=0x{Registers.GeneralPurposeRegisters[0]:X8} " +
                $"r1=0x{Registers.GeneralPurposeRegisters[1]:X8} " +
                $"r8=0x{Registers.GeneralPurposeRegisters[8]:X8} " +
                $"r9=0x{Registers.GeneralPurposeRegisters[9]:X8} " +
                $"gbr=0x{Registers.GlobalBaseRegister:X8} " +
                $"pr=0x{Registers.ProcedureRegister:X8} " +
                $"t={(Registers.StatusRegister.T ? 1 : 0)}");
        }

        Registers.ProgramCounter = Registers.NextProgramCounter;
        Registers.NextProgramCounter = Registers.ProgramCounter + 2;
        Registers.NextInstructionInDelaySlot = false;

        if (TryExecute(opcode, bus))
        {
            bus.IncrementCycleCounter(1);
            CycleCounter += 1;
            return;
        }

        if (_unsupportedLogCount < MaxUnsupportedLogs)
        {
            _unsupportedLogCount++;
            Console.WriteLine($"[S32X-SH2-{Name}] unsupported opcode 0x{opcode:X4} at PC=0x{pc:X8}");
        }
        bus.IncrementCycleCounter(1);
        CycleCounter += 1;
    }

    private void HandleException(byte interruptLevel, uint vectorNumber, ISega32XSh2Bus bus)
    {
        uint sp = Registers.StackPointer - 4;
        bus.WriteLongword(sp, Registers.StatusRegister.ToUInt32(), Sega32XSh2AccessContext.Data);

        sp -= 4;
        bus.WriteLongword(sp, Registers.ProgramCounter, Sega32XSh2AccessContext.Data);

        Registers.StackPointer = sp;
        Sega32XSh2StatusRegister statusRegister = Registers.StatusRegister;
        statusRegister.InterruptMask = interruptLevel;
        Registers.StatusRegister = statusRegister;

        uint vectorAddress = Registers.VectorBaseRegister + (vectorNumber << 2);
        Registers.ProgramCounter = bus.ReadLongword(vectorAddress, Sega32XSh2AccessContext.InterruptVector);
        Registers.NextProgramCounter = Registers.ProgramCounter + 2;
        Registers.NextInstructionInDelaySlot = false;

        bus.IncrementCycleCounter(5);
        CycleCounter += 5;
    }

    private bool TryExecute(ushort opcode, ISega32XSh2Bus bus)
    {
        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;

        if ((opcode & 0xF000) == 0xE000)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked((uint)(sbyte)(opcode & 0xFF));
            return true;
        }

        if ((opcode & 0xF000) == 0xD000)
        {
            uint disp = (uint)((opcode & 0xFF) << 2);
            uint address = (Registers.NextProgramCounter & ~3u) + disp;
            Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF000) == 0x9000)
        {
            uint disp = (uint)((opcode & 0xFF) << 1);
            uint address = Registers.NextProgramCounter + disp;
            Registers.GeneralPurposeRegisters[n] = unchecked((uint)(short)bus.ReadWord(address, Sega32XSh2AccessContext.Data));
            return true;
        }

        if ((opcode & 0xF000) == 0x5000)
        {
            uint disp = (uint)((opcode & 0xF) << 2);
            uint address = Registers.GeneralPurposeRegisters[m] + disp;
            Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF00F) == 0x2000)
        {
            uint address = Registers.GeneralPurposeRegisters[n];
            bus.WriteByte(address, (byte)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF00F) == 0x2001)
        {
            uint address = Registers.GeneralPurposeRegisters[n];
            bus.WriteWord(address, (ushort)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF00F) == 0x2002)
        {
            uint address = Registers.GeneralPurposeRegisters[n];
            bus.WriteLongword(address, Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF00F) == 0x2004)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 1);
            bus.WriteByte(
                Registers.GeneralPurposeRegisters[n],
                (byte)Registers.GeneralPurposeRegisters[m],
                Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF00F) == 0x2005)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 2);
            bus.WriteWord(
                Registers.GeneralPurposeRegisters[n],
                (ushort)Registers.GeneralPurposeRegisters[m],
                Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF00F) == 0x2006)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 4);
            bus.WriteLongword(
                Registers.GeneralPurposeRegisters[n],
                Registers.GeneralPurposeRegisters[m],
                Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF00F) == 0x2008)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (Registers.GeneralPurposeRegisters[m] & Registers.GeneralPurposeRegisters[n]) == 0;
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF00F) == 0x2009)
        {
            Registers.GeneralPurposeRegisters[n] &= Registers.GeneralPurposeRegisters[m];
            return true;
        }

        if ((opcode & 0xF00F) == 0x200A)
        {
            Registers.GeneralPurposeRegisters[n] ^= Registers.GeneralPurposeRegisters[m];
            return true;
        }

        if ((opcode & 0xF00F) == 0x200B)
        {
            Registers.GeneralPurposeRegisters[n] |= Registers.GeneralPurposeRegisters[m];
            return true;
        }

        if ((opcode & 0xF00F) == 0x200C)
        {
            uint xor = Registers.GeneralPurposeRegisters[m] ^ Registers.GeneralPurposeRegisters[n];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (xor & 0xFF) == 0
                || ((xor >> 8) & 0xFF) == 0
                || ((xor >> 16) & 0xFF) == 0
                || ((xor >> 24) & 0xFF) == 0;
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF00F) == 0x200E)
        {
            uint product = (Registers.GeneralPurposeRegisters[m] & 0xFFFF)
                * (Registers.GeneralPurposeRegisters[n] & 0xFFFF);
            Registers.MacLow = product;
            return true;
        }

        if ((opcode & 0xF00F) == 0x200F)
        {
            int lhs = (short)Registers.GeneralPurposeRegisters[m];
            int rhs = (short)Registers.GeneralPurposeRegisters[n];
            Registers.MacLow = unchecked((uint)(lhs * rhs));
            return true;
        }

        if ((opcode & 0xF00F) == 0x2007)
        {
            uint divisor = Registers.GeneralPurposeRegisters[m];
            uint dividend = Registers.GeneralPurposeRegisters[n];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.M = (divisor & 0x80000000) != 0;
            sr.Q = (dividend & 0x80000000) != 0;
            sr.T = sr.M != sr.Q;
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF00F) == 0x6006)
        {
            uint address = Registers.GeneralPurposeRegisters[m];
            Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
            Registers.GeneralPurposeRegisters[m] = address + 4;
            return true;
        }

        if ((opcode & 0xF00F) == 0x6005)
        {
            uint address = Registers.GeneralPurposeRegisters[m];
            Registers.GeneralPurposeRegisters[n] = unchecked((uint)(short)bus.ReadWord(address, Sega32XSh2AccessContext.Data));
            Registers.GeneralPurposeRegisters[m] = address + 2;
            return true;
        }

        if ((opcode & 0xF00F) == 0x6000)
        {
            uint address = Registers.GeneralPurposeRegisters[m];
            Registers.GeneralPurposeRegisters[n] = unchecked((uint)(sbyte)bus.ReadByte(address, Sega32XSh2AccessContext.Data));
            return true;
        }

        if ((opcode & 0xF00F) == 0x6001)
        {
            uint address = Registers.GeneralPurposeRegisters[m];
            Registers.GeneralPurposeRegisters[n] = unchecked((uint)(short)bus.ReadWord(address, Sega32XSh2AccessContext.Data));
            return true;
        }

        if ((opcode & 0xF00F) == 0x6002)
        {
            uint address = Registers.GeneralPurposeRegisters[m];
            Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF00F) == 0x600D)
        {
            Registers.GeneralPurposeRegisters[n] = Registers.GeneralPurposeRegisters[m] & 0x0000FFFF;
            return true;
        }

        if ((opcode & 0xF00F) == 0x6007)
        {
            Registers.GeneralPurposeRegisters[n] = ~Registers.GeneralPurposeRegisters[m];
            return true;
        }

        if ((opcode & 0xF00F) == 0x6008)
        {
            uint value = Registers.GeneralPurposeRegisters[m];
            Registers.GeneralPurposeRegisters[n] = (value << 16) | (value >> 16);
            return true;
        }

        if ((opcode & 0xF00F) == 0x6009)
        {
            uint value = Registers.GeneralPurposeRegisters[m];
            Registers.GeneralPurposeRegisters[n] =
                ((value & 0x00FF00FF) << 8) | ((value & 0xFF00FF00) >> 8);
            return true;
        }

        if ((opcode & 0xF00F) == 0x600A)
        {
            uint source = Registers.GeneralPurposeRegisters[m];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            uint partial = unchecked(0u - source);
            bool borrow1 = partial > 0;
            uint result = unchecked(partial - (sr.T ? 1u : 0u));
            bool borrow2 = result > partial;
            sr.T = borrow1 || borrow2;
            Registers.StatusRegister = sr;
            Registers.GeneralPurposeRegisters[n] = result;
            return true;
        }

        if ((opcode & 0xF00F) == 0x600B)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(0u - Registers.GeneralPurposeRegisters[m]);
            return true;
        }

        if ((opcode & 0xF000) == 0x1000)
        {
            uint disp = (uint)((opcode & 0xF) << 2);
            uint address = Registers.GeneralPurposeRegisters[n] + disp;
            bus.WriteLongword(address, Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF0FF) == 0x400E)
        {
            Registers.StatusRegister = Sega32XSh2StatusRegister.FromUInt32(Registers.GeneralPurposeRegisters[n]);
            return true;
        }

        if ((opcode & 0xF0FF) == 0x401E)
        {
            Registers.GlobalBaseRegister = Registers.GeneralPurposeRegisters[n];
            return true;
        }

        if ((opcode & 0xF0FF) == 0x402E)
        {
            Registers.VectorBaseRegister = Registers.GeneralPurposeRegisters[n];
            return true;
        }

        if ((opcode & 0xF00F) == 0x3000)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = Registers.GeneralPurposeRegisters[n] == Registers.GeneralPurposeRegisters[m];
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xFF00) == 0x8800)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = unchecked((uint)(sbyte)(opcode & 0xFF)) == Registers.GeneralPurposeRegisters[0];
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xFF00) == 0xC800)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (((uint)(opcode & 0xFF)) & Registers.GeneralPurposeRegisters[0]) == 0;
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xFF00) == 0xC400)
        {
            uint address = Registers.GlobalBaseRegister + (uint)(opcode & 0xFF);
            Registers.GeneralPurposeRegisters[0] = unchecked((uint)(sbyte)bus.ReadByte(address, Sega32XSh2AccessContext.Data));
            return true;
        }

        if ((opcode & 0xFF00) == 0xC500)
        {
            uint address = Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 1);
            Registers.GeneralPurposeRegisters[0] = unchecked((uint)(short)bus.ReadWord(address, Sega32XSh2AccessContext.Data));
            return true;
        }

        if ((opcode & 0xFF00) == 0xC600)
        {
            uint address = Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 2);
            Registers.GeneralPurposeRegisters[0] = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xFF00) == 0xC000)
        {
            uint address = Registers.GlobalBaseRegister + (uint)(opcode & 0xFF);
            bus.WriteByte(address, (byte)Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xFF00) == 0xC100)
        {
            uint address = Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 1);
            bus.WriteWord(address, (ushort)Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xFF00) == 0xC200)
        {
            uint address = Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 2);
            bus.WriteLongword(address, Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xFF00) == 0xC800)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (((uint)opcode & 0xFF) & Registers.GeneralPurposeRegisters[0]) == 0;
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xFF00) == 0xC900)
        {
            Registers.GeneralPurposeRegisters[0] &= (uint)opcode & 0xFF;
            return true;
        }

        if ((opcode & 0xFF00) == 0xCA00)
        {
            Registers.GeneralPurposeRegisters[0] ^= (uint)opcode & 0xFF;
            return true;
        }

        if ((opcode & 0xFF00) == 0xCB00)
        {
            Registers.GeneralPurposeRegisters[0] |= (uint)opcode & 0xFF;
            return true;
        }

        if ((opcode & 0xFF00) == 0xCC00)
        {
            uint address = Registers.GlobalBaseRegister + Registers.GeneralPurposeRegisters[0];
            byte value = bus.ReadByte(address, Sega32XSh2AccessContext.Data);
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (((uint)opcode & value) == 0);
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xFF00) == 0xCD00)
        {
            uint address = Registers.GlobalBaseRegister + Registers.GeneralPurposeRegisters[0];
            byte value = bus.ReadByte(address, Sega32XSh2AccessContext.Data);
            bus.WriteByte(address, (byte)(value & (opcode & 0xFF)), Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xFF00) == 0xCE00)
        {
            uint address = Registers.GlobalBaseRegister + Registers.GeneralPurposeRegisters[0];
            byte value = bus.ReadByte(address, Sega32XSh2AccessContext.Data);
            bus.WriteByte(address, (byte)(value ^ (opcode & 0xFF)), Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xFF00) == 0xCF00)
        {
            uint address = Registers.GlobalBaseRegister + Registers.GeneralPurposeRegisters[0];
            byte value = bus.ReadByte(address, Sega32XSh2AccessContext.Data);
            bus.WriteByte(address, (byte)(value | (opcode & 0xFF)), Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF000) == 0x7000)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(
                Registers.GeneralPurposeRegisters[n] + (uint)(sbyte)(opcode & 0xFF));
            return true;
        }

        if ((opcode & 0xF00F) == 0x6003)
        {
            Registers.GeneralPurposeRegisters[n] = Registers.GeneralPurposeRegisters[m];
            return true;
        }

        if ((opcode & 0xF00F) == 0x3008)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(
                Registers.GeneralPurposeRegisters[n] - Registers.GeneralPurposeRegisters[m]);
            return true;
        }

        if ((opcode & 0xF00F) == 0x3002)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = Registers.GeneralPurposeRegisters[n] >= Registers.GeneralPurposeRegisters[m];
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF00F) == 0x3003)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (int)Registers.GeneralPurposeRegisters[n] >= (int)Registers.GeneralPurposeRegisters[m];
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF00F) == 0x3006)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = Registers.GeneralPurposeRegisters[n] > Registers.GeneralPurposeRegisters[m];
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF00F) == 0x3007)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (int)Registers.GeneralPurposeRegisters[n] > (int)Registers.GeneralPurposeRegisters[m];
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF00F) == 0x300C)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(
                Registers.GeneralPurposeRegisters[n] + Registers.GeneralPurposeRegisters[m]);
            return true;
        }

        if ((opcode & 0xF00F) == 0x300A)
        {
            uint lhs = Registers.GeneralPurposeRegisters[n];
            uint rhs = Registers.GeneralPurposeRegisters[m];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            uint partial = unchecked(lhs - rhs);
            bool borrow1 = partial > lhs;
            uint result = unchecked(partial - (sr.T ? 1u : 0u));
            bool borrow2 = result > partial;
            sr.T = borrow1 || borrow2;
            Registers.StatusRegister = sr;
            Registers.GeneralPurposeRegisters[n] = result;
            return true;
        }

        if ((opcode & 0xF00F) == 0x300B)
        {
            uint lhs = Registers.GeneralPurposeRegisters[n];
            uint rhs = Registers.GeneralPurposeRegisters[m];
            bool sourceSign = (rhs & 0x80000000) != 0;
            bool destSign = (lhs & 0x80000000) != 0;
            uint result = unchecked(lhs - rhs);
            bool resultSign = (result & 0x80000000) != 0;
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = sourceSign != destSign && resultSign != destSign;
            Registers.StatusRegister = sr;
            Registers.GeneralPurposeRegisters[n] = result;
            return true;
        }

        if ((opcode & 0xF00F) == 0x300D)
        {
            long lhs = (int)Registers.GeneralPurposeRegisters[m];
            long rhs = (int)Registers.GeneralPurposeRegisters[n];
            long product = lhs * rhs;
            Registers.MacLow = unchecked((uint)product);
            Registers.MacHigh = unchecked((uint)(product >> 32));
            return true;
        }

        if ((opcode & 0xF00F) == 0x3005)
        {
            ulong product = (ulong)Registers.GeneralPurposeRegisters[m] * Registers.GeneralPurposeRegisters[n];
            Registers.MacLow = (uint)product;
            Registers.MacHigh = (uint)(product >> 32);
            return true;
        }

        if ((opcode & 0xF00F) == 0x300E)
        {
            uint lhs = Registers.GeneralPurposeRegisters[n];
            uint rhs = Registers.GeneralPurposeRegisters[m];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            uint partial = unchecked(lhs + rhs);
            bool carry1 = partial < lhs;
            uint result = unchecked(partial + (sr.T ? 1u : 0u));
            bool carry2 = result < partial;
            sr.T = carry1 || carry2;
            Registers.StatusRegister = sr;
            Registers.GeneralPurposeRegisters[n] = result;
            return true;
        }

        if ((opcode & 0xF00F) == 0x300F)
        {
            uint lhs = Registers.GeneralPurposeRegisters[n];
            uint rhs = Registers.GeneralPurposeRegisters[m];
            bool lhsSign = (lhs & 0x80000000) != 0;
            bool rhsSign = (rhs & 0x80000000) != 0;
            uint result = unchecked(lhs + rhs);
            bool resultSign = (result & 0x80000000) != 0;
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = lhsSign == rhsSign && lhsSign != resultSign;
            Registers.StatusRegister = sr;
            Registers.GeneralPurposeRegisters[n] = result;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4015)
        {
            uint value = Registers.GeneralPurposeRegisters[n];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (int)value > 0;
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4010)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 1);
            uint value = Registers.GeneralPurposeRegisters[n];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = value == 0;
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4011)
        {
            uint value = Registers.GeneralPurposeRegisters[n];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (int)value >= 0;
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4001)
        {
            uint value = Registers.GeneralPurposeRegisters[n];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (value & 0x1) != 0;
            Registers.StatusRegister = sr;
            Registers.GeneralPurposeRegisters[n] = value >> 1;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4000 || (opcode & 0xF0FF) == 0x4020)
        {
            uint value = Registers.GeneralPurposeRegisters[n];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (value & 0x80000000) != 0;
            Registers.StatusRegister = sr;
            Registers.GeneralPurposeRegisters[n] = value << 1;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4021)
        {
            uint value = Registers.GeneralPurposeRegisters[n];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (value & 0x1) != 0;
            Registers.StatusRegister = sr;
            Registers.GeneralPurposeRegisters[n] = (value >> 1) | (value & 0x80000000);
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4004)
        {
            uint value = Registers.GeneralPurposeRegisters[n];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (value & 0x80000000) != 0;
            Registers.StatusRegister = sr;
            Registers.GeneralPurposeRegisters[n] = (value << 1) | (value >> 31);
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4005)
        {
            uint value = Registers.GeneralPurposeRegisters[n];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = (value & 0x1) != 0;
            Registers.StatusRegister = sr;
            Registers.GeneralPurposeRegisters[n] = (value >> 1) | (value << 31);
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4024)
        {
            uint value = Registers.GeneralPurposeRegisters[n];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            bool carryOut = (value & 0x80000000) != 0;
            Registers.GeneralPurposeRegisters[n] = (value << 1) | (sr.T ? 1u : 0u);
            sr.T = carryOut;
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4025)
        {
            uint value = Registers.GeneralPurposeRegisters[n];
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            bool carryOut = (value & 0x1) != 0;
            Registers.GeneralPurposeRegisters[n] = (value >> 1) | ((sr.T ? 1u : 0u) << 31);
            sr.T = carryOut;
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4008)
        {
            Registers.GeneralPurposeRegisters[n] <<= 2;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4009)
        {
            Registers.GeneralPurposeRegisters[n] >>= 2;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4018)
        {
            Registers.GeneralPurposeRegisters[n] <<= 8;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4019)
        {
            Registers.GeneralPurposeRegisters[n] >>= 8;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4028)
        {
            Registers.GeneralPurposeRegisters[n] <<= 16;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4029)
        {
            Registers.GeneralPurposeRegisters[n] >>= 16;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x400B)
        {
            Registers.ProcedureRegister = Registers.NextProgramCounter;
            Registers.NextProgramCounter = Registers.GeneralPurposeRegisters[n];
            Registers.NextInstructionInDelaySlot = true;
            bus.IncrementCycleCounter(1);
            CycleCounter += 1;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x402B)
        {
            Registers.NextProgramCounter = Registers.GeneralPurposeRegisters[n];
            Registers.NextInstructionInDelaySlot = true;
            bus.IncrementCycleCounter(1);
            CycleCounter += 1;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x0003)
        {
            Registers.ProcedureRegister = Registers.NextProgramCounter;
            Registers.NextProgramCounter = Registers.NextProgramCounter + Registers.GeneralPurposeRegisters[n];
            Registers.NextInstructionInDelaySlot = true;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x0023)
        {
            Registers.NextProgramCounter = Registers.NextProgramCounter + Registers.GeneralPurposeRegisters[n];
            Registers.NextInstructionInDelaySlot = true;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x0029)
        {
            Registers.GeneralPurposeRegisters[n] = Registers.StatusRegister.T ? 1u : 0u;
            return true;
        }

        if ((opcode & 0xFF00) == 0x8B00)
        {
            if (!Registers.StatusRegister.T)
            {
                int disp = (sbyte)(opcode & 0xFF);
                Registers.ProgramCounter = Registers.NextProgramCounter + (uint)(disp << 1);
                Registers.NextProgramCounter = Registers.ProgramCounter + 2;
            }
            return true;
        }

        if ((opcode & 0xFF00) == 0x8900)
        {
            if (Registers.StatusRegister.T)
            {
                int disp = (sbyte)(opcode & 0xFF);
                Registers.ProgramCounter = Registers.NextProgramCounter + (uint)(disp << 1);
                Registers.NextProgramCounter = Registers.ProgramCounter + 2;
            }
            return true;
        }

        if ((opcode & 0xF000) == 0x8000)
        {
            switch ((opcode >> 8) & 0xF)
            {
                case 0x0:
                {
                    int reg = m;
                    uint address = Registers.GeneralPurposeRegisters[reg] + (uint)(opcode & 0xF);
                    bus.WriteByte(address, (byte)Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data);
                    return true;
                }
                case 0x1:
                {
                    int reg = m;
                    uint address = Registers.GeneralPurposeRegisters[reg] + (uint)((opcode & 0xF) << 1);
                    bus.WriteWord(address, (ushort)Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data);
                    return true;
                }
                case 0x4:
                {
                    int reg = m;
                    uint address = Registers.GeneralPurposeRegisters[reg] + (uint)(opcode & 0xF);
                    Registers.GeneralPurposeRegisters[0] = unchecked((uint)(sbyte)bus.ReadByte(address, Sega32XSh2AccessContext.Data));
                    return true;
                }
                case 0x5:
                {
                    int reg = m;
                    uint address = Registers.GeneralPurposeRegisters[reg] + (uint)((opcode & 0xF) << 1);
                    Registers.GeneralPurposeRegisters[0] = unchecked((uint)(short)bus.ReadWord(address, Sega32XSh2AccessContext.Data));
                    return true;
                }
                case 0x8:
                {
                    Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                    sr.T = Registers.GeneralPurposeRegisters[0] == unchecked((uint)(sbyte)(opcode & 0xFF));
                    Registers.StatusRegister = sr;
                    return true;
                }
                case 0x9:
                    if (Registers.StatusRegister.T)
                    {
                        int disp = (sbyte)(opcode & 0xFF);
                        Registers.ProgramCounter = Registers.NextProgramCounter + (uint)(disp << 1);
                        Registers.NextProgramCounter = Registers.ProgramCounter + 2;
                    }
                    return true;
                case 0xB:
                    if (!Registers.StatusRegister.T)
                    {
                        int disp = (sbyte)(opcode & 0xFF);
                        Registers.ProgramCounter = Registers.NextProgramCounter + (uint)(disp << 1);
                        Registers.NextProgramCounter = Registers.ProgramCounter + 2;
                    }
                    return true;
                case 0xD:
                    if (Registers.StatusRegister.T)
                    {
                        int disp = (sbyte)(opcode & 0xFF);
                        Registers.NextProgramCounter = Registers.NextProgramCounter + (uint)(disp << 1);
                        Registers.NextInstructionInDelaySlot = true;
                    }
                    return true;
                case 0xF:
                    if (!Registers.StatusRegister.T)
                    {
                        int disp = (sbyte)(opcode & 0xFF);
                        Registers.NextProgramCounter = Registers.NextProgramCounter + (uint)(disp << 1);
                        Registers.NextInstructionInDelaySlot = true;
                    }
                    return true;
            }
        }

        if ((opcode & 0xF000) == 0xA000)
        {
            int disp = ((short)(opcode << 4)) >> 4;
            Registers.NextProgramCounter = Registers.NextProgramCounter + (uint)(disp << 1);
            Registers.NextInstructionInDelaySlot = true;
            return true;
        }

        if ((opcode & 0xF000) == 0xB000)
        {
            int disp = ((short)(opcode << 4)) >> 4;
            Registers.ProcedureRegister = Registers.NextProgramCounter;
            Registers.NextProgramCounter = Registers.NextProgramCounter + (uint)(disp << 1);
            Registers.NextInstructionInDelaySlot = true;
            return true;
        }

        if (opcode == 0x000B)
        {
            Registers.NextProgramCounter = Registers.ProcedureRegister;
            Registers.NextInstructionInDelaySlot = true;
            return true;
        }

        if (opcode == 0x0009)
        {
            return true;
        }

        if (opcode == 0x001B)
        {
            Registers.NextProgramCounter = Registers.ProgramCounter;
            Registers.ProgramCounter = unchecked(Registers.ProgramCounter - 2);
            return true;
        }

        return false;
    }
}

internal sealed class Sega32XSh2Registers
{
    public uint[] GeneralPurposeRegisters { get; } = new uint[16];
    public Sega32XSh2StatusRegister StatusRegister { get; set; } = new();
    public uint GlobalBaseRegister { get; set; }
    public uint VectorBaseRegister { get; set; }
    public uint MacLow { get; set; }
    public uint MacHigh { get; set; }
    public uint ProcedureRegister { get; set; }
    public uint ProgramCounter { get; set; }
    public uint NextProgramCounter { get; set; }
    public bool NextInstructionInDelaySlot { get; set; }

    public uint StackPointer
    {
        get => GeneralPurposeRegisters[15];
        set => GeneralPurposeRegisters[15] = value;
    }
}

internal struct Sega32XSh2StatusRegister
{
    public byte InterruptMask { get; set; }
    public bool T { get; set; }
    public bool S { get; set; }
    public bool Q { get; set; }
    public bool M { get; set; }

    public static Sega32XSh2StatusRegister FromUInt32(uint value)
    {
        return new Sega32XSh2StatusRegister
        {
            InterruptMask = (byte)((value >> 4) & 0xF),
            T = (value & 0x0001) != 0,
            S = (value & 0x0002) != 0,
            Q = (value & 0x0100) != 0,
            M = (value & 0x0200) != 0,
        };
    }

    public uint ToUInt32()
    {
        return ((M ? 1u : 0u) << 9)
            | ((Q ? 1u : 0u) << 8)
            | ((uint)InterruptMask << 4)
            | ((S ? 1u : 0u) << 1)
            | (T ? 1u : 0u);
    }
}

internal enum Sega32XSh2AccessContext
{
    Fetch,
    Data,
    InterruptVector,
}

internal interface ISega32XSh2Bus
{
    bool ResetAsserted { get; }
    byte InterruptLevel { get; }
    byte ReadByte(uint address, Sega32XSh2AccessContext context);
    ushort ReadWord(uint address, Sega32XSh2AccessContext context);
    uint ReadLongword(uint address, Sega32XSh2AccessContext context);
    void WriteByte(uint address, byte value, Sega32XSh2AccessContext context);
    void WriteWord(uint address, ushort value, Sega32XSh2AccessContext context);
    void WriteLongword(uint address, uint value, Sega32XSh2AccessContext context);
    void IncrementCycleCounter(ulong cycles);
}
