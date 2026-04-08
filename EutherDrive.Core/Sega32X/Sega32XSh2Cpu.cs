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
    private static readonly bool TraceKnownStalls =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_KNOWN_STALLS"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceExceptions =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_EXCEPTIONS"),
            "1",
            StringComparison.Ordinal);
    private static readonly uint? TracePcStart = ParseOptionalHexEnv("EUTHERDRIVE_S32X_TRACE_PC_START");
    private static readonly uint? TracePcEnd = ParseOptionalHexEnv("EUTHERDRIVE_S32X_TRACE_PC_END");
    private static readonly uint? TraceR7Max = ParseOptionalHexEnv("EUTHERDRIVE_S32X_TRACE_R7_MAX");
    private int _unsupportedLogCount;
    private int _knownStallLogCount;

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

        while (ticks > 0)
        {
            // Always check interrupts if we're not in a delay slot
            if (!Registers.NextInstructionInDelaySlot)
            {
                byte externalInterruptLevel = bus.InterruptLevel;
                if (externalInterruptLevel > Registers.StatusRegister.InterruptMask)
                {
                    uint vectorNumber = 64u + (uint)(externalInterruptLevel >> 1);
                    HandleException(externalInterruptLevel, vectorNumber, bus);
                    // Exceptions take cycles and might change ticks context, but for now just count as 5
                    if (ticks > 5) ticks -= 5; else ticks = 0;
                    if (ticks == 0) break;
                }
            }

            ExecuteSingleInstruction(bus);
            ticks--;
        }
    }

    private void ExecuteSingleInstruction(ISega32XSh2Bus bus)
    {
        uint pc = Registers.ProgramCounter;
        ushort opcode = bus.ReadWord(pc, Sega32XSh2AccessContext.Fetch);

        if (TraceBootLoop && pc >= 0x00000180 && pc <= 0x00000220)
        {
            EmitTraceLine(
                $"[S32X-SH2-{Name}] pc=0x{pc:X8} op=0x{opcode:X4} " +
                $"r0=0x{Registers.GeneralPurposeRegisters[0]:X8} " +
                $"r1=0x{Registers.GeneralPurposeRegisters[1]:X8} " +
                $"r8=0x{Registers.GeneralPurposeRegisters[8]:X8} " +
                $"r9=0x{Registers.GeneralPurposeRegisters[9]:X8} " +
                $"gbr=0x{Registers.GlobalBaseRegister:X8} " +
                $"pr=0x{Registers.ProcedureRegister:X8} " +
                $"t={(Registers.StatusRegister.T ? 1 : 0)}");
        }

        if (TracePcStart.HasValue
            && TracePcEnd.HasValue
            && pc >= TracePcStart.Value
            && pc <= TracePcEnd.Value
            && (!TraceR7Max.HasValue || Registers.GeneralPurposeRegisters[7] <= TraceR7Max.Value))
        {
            EmitTraceLine(
                $"[S32X-SH2-{Name}] pc=0x{pc:X8} op=0x{opcode:X4} " +
                $"r0=0x{Registers.GeneralPurposeRegisters[0]:X8} " +
                $"r1=0x{Registers.GeneralPurposeRegisters[1]:X8} " +
                $"r2=0x{Registers.GeneralPurposeRegisters[2]:X8} " +
                $"r3=0x{Registers.GeneralPurposeRegisters[3]:X8} " +
                $"r4=0x{Registers.GeneralPurposeRegisters[4]:X8} " +
                $"r5=0x{Registers.GeneralPurposeRegisters[5]:X8} " +
                $"r6=0x{Registers.GeneralPurposeRegisters[6]:X8} " +
                $"r7=0x{Registers.GeneralPurposeRegisters[7]:X8} " +
                $"r8=0x{Registers.GeneralPurposeRegisters[8]:X8} " +
                $"r9=0x{Registers.GeneralPurposeRegisters[9]:X8} " +
                $"gbr=0x{Registers.GlobalBaseRegister:X8} vbr=0x{Registers.VectorBaseRegister:X8} pr=0x{Registers.ProcedureRegister:X8} " +
                $"mach=0x{Registers.MacHigh:X8} macl=0x{Registers.MacLow:X8} " +
                $"sr=0x{Registers.StatusRegister.ToUInt32():X8}");
        }

        if (TraceKnownStalls && _knownStallLogCount < 64 && (pc == 0x060008C8 || pc == 0x06000276))
        {
            _knownStallLogCount++;
            uint r14 = Registers.GeneralPurposeRegisters[14];
            uint polled20 = bus.ReadLongword(r14 + 0x20, Sega32XSh2AccessContext.Data);
            uint polled24 = bus.ReadLongword(r14 + 0x24, Sega32XSh2AccessContext.Data);
            EmitTraceLine(
                $"[S32X-SH2-{Name}-STALL] pc=0x{pc:X8} op=0x{opcode:X4} " +
                $"r0=0x{Registers.GeneralPurposeRegisters[0]:X8} r1=0x{Registers.GeneralPurposeRegisters[1]:X8} " +
                $"r2=0x{Registers.GeneralPurposeRegisters[2]:X8} r3=0x{Registers.GeneralPurposeRegisters[3]:X8} " +
                $"r4=0x{Registers.GeneralPurposeRegisters[4]:X8} r5=0x{Registers.GeneralPurposeRegisters[5]:X8} " +
                $"r14=0x{r14:X8} pr=0x{Registers.ProcedureRegister:X8} gbr=0x{Registers.GlobalBaseRegister:X8} " +
                $"sr=0x{Registers.StatusRegister.ToUInt32():X8} mem20=0x{polled20:X8} mem24=0x{polled24:X8}");
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
            EmitTraceLine(
                $"[S32X-SH2-{Name}] illegal opcode 0x{opcode:X4} at PC=0x{pc:X8} " +
                $"next=0x{Registers.NextProgramCounter:X8} pr=0x{Registers.ProcedureRegister:X8} " +
                $"r14=0x{Registers.GeneralPurposeRegisters[14]:X8} r15=0x{Registers.StackPointer:X8} " +
                $"sr=0x{Registers.StatusRegister.ToUInt32():X8} space={(pc >> 29)}");
        }

        Registers.ProgramCounter = pc;
        Registers.NextProgramCounter = pc + 2;
        Registers.NextInstructionInDelaySlot = false;
        HandleException(null, 4, bus);
        bus.IncrementCycleCounter(1);
        CycleCounter += 1;
    }

    private void HandleException(byte? interruptLevel, uint vectorNumber, ISega32XSh2Bus bus)
    {
        uint faultPc = Registers.ProgramCounter;
        uint faultSp = Registers.StackPointer;
        uint oldSr = Registers.StatusRegister.ToUInt32();
        uint sp = Registers.StackPointer - 4;
        bus.WriteLongword(sp, Registers.StatusRegister.ToUInt32(), Sega32XSh2AccessContext.Data);

        sp -= 4;
        bus.WriteLongword(sp, Registers.ProgramCounter, Sega32XSh2AccessContext.Data);

        Registers.StackPointer = sp;
        if (interruptLevel.HasValue)
        {
            Sega32XSh2StatusRegister statusRegister = Registers.StatusRegister;
            statusRegister.InterruptMask = interruptLevel.Value;
            Registers.StatusRegister = statusRegister;
        }

        uint vectorAddress = Registers.VectorBaseRegister + (vectorNumber << 2);
        Registers.ProgramCounter = bus.ReadLongword(vectorAddress, Sega32XSh2AccessContext.InterruptVector);
        Registers.NextProgramCounter = Registers.ProgramCounter + 2;
        Registers.NextInstructionInDelaySlot = false;

        if (TraceExceptions)
        {
            string line =
                $"[S32X-SH2-{Name}-EXC] pc=0x{faultPc:X8} sp=0x{faultSp:X8} " +
                $"oldsr=0x{oldSr:X8} level={(interruptLevel.HasValue ? interruptLevel.Value.ToString() : "illegal")} vector=0x{vectorNumber:X2} " +
                $"vbr=0x{Registers.VectorBaseRegister:X8} target=0x{Registers.ProgramCounter:X8}";
            Console.WriteLine(line);
            string? traceFilePath = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_FILE");
            if (!string.IsNullOrWhiteSpace(traceFilePath))
                System.IO.File.AppendAllText(traceFilePath, line + Environment.NewLine);
        }

        bus.IncrementCycleCounter(5);
        CycleCounter += 5;
    }

    private static uint? ParseOptionalHexEnv(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];

        return uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint value)
            ? value
            : null;
    }

    private static void EmitTraceLine(string line)
    {
        Console.WriteLine(line);
        string? traceFilePath = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_FILE");
        if (!string.IsNullOrWhiteSpace(traceFilePath))
            System.IO.File.AppendAllText(traceFilePath, line + Environment.NewLine);
    }

    private long GetMac()
    {
        return ((long)(int)Registers.MacHigh << 32) | Registers.MacLow;
    }

    private void SetMac(long value)
    {
        Registers.MacLow = unchecked((uint)value);
        Registers.MacHigh = unchecked((uint)(value >> 32));
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

        if ((opcode & 0xF00F) == 0x0004)
        {
            uint address = Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[n];
            bus.WriteByte(address, (byte)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF00F) == 0x0005)
        {
            uint address = Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[n];
            bus.WriteWord(address, (ushort)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF00F) == 0x0006)
        {
            uint address = Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[n];
            bus.WriteLongword(address, Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF00F) == 0x0007)
        {
            Registers.MacLow = unchecked(Registers.GeneralPurposeRegisters[n] * Registers.GeneralPurposeRegisters[m]);
            return true;
        }

        if ((opcode & 0xF00F) == 0x000C)
        {
            uint address = Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[m];
            Registers.GeneralPurposeRegisters[n] =
                unchecked((uint)(sbyte)bus.ReadByte(address, Sega32XSh2AccessContext.Data));
            return true;
        }

        if ((opcode & 0xF00F) == 0x000D)
        {
            uint address = Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[m];
            Registers.GeneralPurposeRegisters[n] =
                unchecked((uint)(short)bus.ReadWord(address, Sega32XSh2AccessContext.Data));
            return true;
        }

        if ((opcode & 0xF00F) == 0x000E)
        {
            uint address = Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[m];
            Registers.GeneralPurposeRegisters[n] =
                bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF00F) == 0x000F)
        {
            uint addressM = Registers.GeneralPurposeRegisters[m];
            int lhs = unchecked((int)bus.ReadLongword(addressM, Sega32XSh2AccessContext.Data));
            Registers.GeneralPurposeRegisters[m] = addressM + 4;

            uint addressN = Registers.GeneralPurposeRegisters[n];
            int rhs = unchecked((int)bus.ReadLongword(addressN, Sega32XSh2AccessContext.Data));
            Registers.GeneralPurposeRegisters[n] = addressN + 4;

            long productSum = unchecked(((long)lhs * rhs) + GetMac());
            if (Registers.StatusRegister.S)
            {
                const long MinMac48 = -(1L << 47);
                const long MaxMac48 = (1L << 47) - 1;
                productSum = Math.Clamp(productSum, MinMac48, MaxMac48);
            }

            SetMac(productSum);
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

        if ((opcode & 0xF00F) == 0x6004)
        {
            uint address = Registers.GeneralPurposeRegisters[m];
            Registers.GeneralPurposeRegisters[n] = unchecked((uint)(sbyte)bus.ReadByte(address, Sega32XSh2AccessContext.Data));
            if (n != m)
                Registers.GeneralPurposeRegisters[m] = address + 1;
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

        if ((opcode & 0xF00F) == 0x600C)
        {
            Registers.GeneralPurposeRegisters[n] = Registers.GeneralPurposeRegisters[m] & 0x000000FF;
            return true;
        }

        if ((opcode & 0xF00F) == 0x6008)
        {
            uint value = Registers.GeneralPurposeRegisters[m];
            Registers.GeneralPurposeRegisters[n] =
                (value & 0xFFFF0000) | ((value & 0x000000FF) << 8) | ((value & 0x0000FF00) >> 8);
            return true;
        }

        if ((opcode & 0xF00F) == 0x6007)
        {
            Registers.GeneralPurposeRegisters[n] = ~Registers.GeneralPurposeRegisters[m];
            return true;
        }

        if ((opcode & 0xF00F) == 0x6009)
        {
            uint value = Registers.GeneralPurposeRegisters[m];
            Registers.GeneralPurposeRegisters[n] = (value << 16) | (value >> 16);
            return true;
        }

        if ((opcode & 0xF00F) == 0x200D)
        {
            Registers.GeneralPurposeRegisters[n] =
                (Registers.GeneralPurposeRegisters[m] << 16) | (Registers.GeneralPurposeRegisters[n] >> 16);
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

        if ((opcode & 0xF00F) == 0x600E)
        {
            Registers.GeneralPurposeRegisters[n] =
                unchecked((uint)(sbyte)Registers.GeneralPurposeRegisters[m]);
            return true;
        }

        if ((opcode & 0xF00F) == 0x600F)
        {
            Registers.GeneralPurposeRegisters[n] =
                unchecked((uint)(short)Registers.GeneralPurposeRegisters[m]);
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

        if ((opcode & 0xF0FF) == 0x4007)
        {
            uint address = Registers.GeneralPurposeRegisters[n];
            Registers.StatusRegister =
                Sega32XSh2StatusRegister.FromUInt32(
                    bus.ReadLongword(address, Sega32XSh2AccessContext.Data));
            Registers.GeneralPurposeRegisters[n] = address + 4;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x401E)
        {
            Registers.GlobalBaseRegister = Registers.GeneralPurposeRegisters[n];
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4017)
        {
            uint address = Registers.GeneralPurposeRegisters[n];
            Registers.GlobalBaseRegister = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
            Registers.GeneralPurposeRegisters[n] = address + 4;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x402E)
        {
            Registers.VectorBaseRegister = Registers.GeneralPurposeRegisters[n];
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4027)
        {
            uint address = Registers.GeneralPurposeRegisters[n];
            Registers.VectorBaseRegister = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
            Registers.GeneralPurposeRegisters[n] = address + 4;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x400A)
        {
            Registers.MacHigh = Registers.GeneralPurposeRegisters[n];
            return true;
        }

        if ((opcode & 0xF0FF) == 0x401A)
        {
            Registers.MacLow = Registers.GeneralPurposeRegisters[n];
            return true;
        }

        if ((opcode & 0xF0FF) == 0x402A)
        {
            Registers.ProcedureRegister = Registers.GeneralPurposeRegisters[n];
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4006)
        {
            uint address = Registers.GeneralPurposeRegisters[n];
            Registers.MacHigh = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
            Registers.GeneralPurposeRegisters[n] = address + 4;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4016)
        {
            uint address = Registers.GeneralPurposeRegisters[n];
            Registers.MacLow = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
            Registers.GeneralPurposeRegisters[n] = address + 4;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4026)
        {
            uint address = Registers.GeneralPurposeRegisters[n];
            Registers.ProcedureRegister = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
            Registers.GeneralPurposeRegisters[n] = address + 4;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x0002)
        {
            Registers.GeneralPurposeRegisters[n] = Registers.StatusRegister.ToUInt32();
            return true;
        }

        if ((opcode & 0xF0FF) == 0x0012)
        {
            Registers.GeneralPurposeRegisters[n] = Registers.GlobalBaseRegister;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x0022)
        {
            Registers.GeneralPurposeRegisters[n] = Registers.VectorBaseRegister;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x000A)
        {
            Registers.GeneralPurposeRegisters[n] = Registers.MacHigh;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x001A)
        {
            Registers.GeneralPurposeRegisters[n] = Registers.MacLow;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x002A)
        {
            Registers.GeneralPurposeRegisters[n] = Registers.ProcedureRegister;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4003)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 4);
            bus.WriteLongword(
                Registers.GeneralPurposeRegisters[n],
                Registers.StatusRegister.ToUInt32(),
                Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4013)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 4);
            bus.WriteLongword(
                Registers.GeneralPurposeRegisters[n],
                Registers.GlobalBaseRegister,
                Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4023)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 4);
            bus.WriteLongword(
                Registers.GeneralPurposeRegisters[n],
                Registers.VectorBaseRegister,
                Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4002)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 4);
            bus.WriteLongword(
                Registers.GeneralPurposeRegisters[n],
                Registers.MacHigh,
                Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4012)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 4);
            bus.WriteLongword(
                Registers.GeneralPurposeRegisters[n],
                Registers.MacLow,
                Sega32XSh2AccessContext.Data);
            return true;
        }

        if ((opcode & 0xF0FF) == 0x4022)
        {
            Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 4);
            bus.WriteLongword(
                Registers.GeneralPurposeRegisters[n],
                Registers.ProcedureRegister,
                Sega32XSh2AccessContext.Data);
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

        if ((opcode & 0xF00F) == 0x3004)
        {
            uint divisor = Registers.GeneralPurposeRegisters[m];
            uint dividend = Registers.GeneralPurposeRegisters[n];
            bool previousSign = (dividend & 0x80000000) != 0;
            dividend = (dividend << 1) | (Registers.StatusRegister.T ? 1u : 0u);

            uint previousDividend = dividend;
            bool overflowed;
            if (Registers.StatusRegister.Q == Registers.StatusRegister.M)
            {
                dividend = unchecked(dividend - divisor);
                overflowed = dividend > previousDividend;
            }
            else
            {
                dividend = unchecked(dividend + divisor);
                overflowed = dividend < previousDividend;
            }

            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.Q = overflowed ^ previousSign ^ sr.M;
            sr.T = sr.Q == sr.M;
            Registers.StatusRegister = sr;
            Registers.GeneralPurposeRegisters[n] = dividend;
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

        if ((opcode & 0xF0FF) == 0x0008)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = false;
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x0018)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = true;
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x0028)
        {
            Registers.MacLow = 0;
            Registers.MacHigh = 0;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x0019)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.M = false;
            sr.Q = false;
            sr.T = false;
            Registers.StatusRegister = sr;
            return true;
        }

        if ((opcode & 0xF0FF) == 0x401B)
        {
            uint address = Registers.GeneralPurposeRegisters[n];
            byte value = bus.ReadByte(address, Sega32XSh2AccessContext.Data);
            bus.WriteByte(address, (byte)(value | 0x80), Sega32XSh2AccessContext.Data);
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.T = value == 0;
            Registers.StatusRegister = sr;
            bus.IncrementCycleCounter(3);
            CycleCounter += 3;
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

        if (opcode == 0x002B)
        {
            uint sp = Registers.StackPointer;
            Registers.NextProgramCounter = bus.ReadLongword(sp, Sega32XSh2AccessContext.Data);
            Registers.NextInstructionInDelaySlot = true;
            Registers.StackPointer = sp + 4;
            Registers.StatusRegister =
                Sega32XSh2StatusRegister.FromUInt32(
                    bus.ReadLongword(Registers.StackPointer, Sega32XSh2AccessContext.Data));
            Registers.StackPointer += 4;
            bus.IncrementCycleCounter(3);
            CycleCounter += 3;
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

        if ((opcode & 0xFF00) == 0xC300)
        {
            uint sp = unchecked(Registers.StackPointer - 4);
            bus.WriteLongword(sp, Registers.StatusRegister.ToUInt32(), Sega32XSh2AccessContext.Data);
            sp = unchecked(sp - 4);
            bus.WriteLongword(sp, Registers.NextProgramCounter, Sega32XSh2AccessContext.Data);
            Registers.StackPointer = sp;

            uint vectorNumber = (uint)(opcode & 0xFF);
            uint vectorAddress = Registers.VectorBaseRegister + (vectorNumber << 2);
            Registers.ProgramCounter = bus.ReadLongword(vectorAddress, Sega32XSh2AccessContext.InterruptVector);
            Registers.NextProgramCounter = Registers.ProgramCounter + 2;
            bus.IncrementCycleCounter(6);
            CycleCounter += 6;
            return true;
        }

        if ((opcode & 0xFF00) == 0xC700)
        {
            uint disp = (uint)((opcode & 0xFF) << 2);
            Registers.GeneralPurposeRegisters[0] = (Registers.NextProgramCounter & ~3u) + disp;
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
