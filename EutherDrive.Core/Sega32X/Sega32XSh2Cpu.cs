namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XSh2Cpu
{
    private const int MaxUnsupportedLogs = 100_000;
    public string Name { get; }
    public Sega32XSh2Registers Registers { get; } = new();
    public ulong CycleCounter { get; private set; }
    public bool ResetPending { get; set; } = true;
    private int _unsupportedLogCount;

    private static readonly byte ResetInterruptMask = 0x0F;
    private static readonly bool TraceBootLoop =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_BOOT_LOOP"), "1", StringComparison.Ordinal);
    private static readonly bool TraceExceptions =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_EXCEPTIONS"), "1", StringComparison.Ordinal);

    public Sega32XSh2Cpu(string name)
    {
        Name = name;
        RequestReset();
    }

    public void RequestReset()
    {
        ResetPending = true;
        Registers.StatusRegister = new Sega32XSh2StatusRegister { InterruptMask = ResetInterruptMask };
    }

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
            if (!Registers.NextInstructionInDelaySlot)
            {
                byte externalInterruptLevel = bus.InterruptLevel;
                if (externalInterruptLevel > Registers.StatusRegister.InterruptMask)
                {
                    uint vectorNumber = 64u + (uint)(externalInterruptLevel >> 1);
                    HandleException(externalInterruptLevel, vectorNumber, bus);
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
                $"r15=0x{Registers.StackPointer:X8} " +
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
            EmitTraceLine($"[S32X-SH2-{Name}] illegal opcode 0x{opcode:X4} at PC=0x{pc:X8}");
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
        uint sp = Registers.StackPointer - 4;
        bus.WriteLongword(sp, Registers.StatusRegister.ToUInt32(), Sega32XSh2AccessContext.Data);
        sp -= 4;
        bus.WriteLongword(sp, Registers.ProgramCounter, Sega32XSh2AccessContext.Data);
        Registers.StackPointer = sp;

        if (interruptLevel.HasValue)
        {
            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
            sr.InterruptMask = interruptLevel.Value;
            Registers.StatusRegister = sr;
        }

        uint vectorAddress = Registers.VectorBaseRegister + (vectorNumber << 2);
        Registers.ProgramCounter = bus.ReadLongword(vectorAddress, Sega32XSh2AccessContext.InterruptVector);
        Registers.NextProgramCounter = Registers.ProgramCounter + 2;
        Registers.NextInstructionInDelaySlot = false;

        if (TraceExceptions)
        {
            Console.WriteLine($"[S32X-SH2-{Name}-EXC] pc=0x{faultPc:X8} vector=0x{vectorNumber:X2} target=0x{Registers.ProgramCounter:X8}");
        }

        bus.IncrementCycleCounter(5);
        CycleCounter += 5;
    }

    private static void EmitTraceLine(string line) => Console.WriteLine(line);

    private long GetMac() => ((long)(int)Registers.MacHigh << 32) | Registers.MacLow;

    private void SetMac(long value)
    {
        Registers.MacLow = unchecked((uint)value);
        Registers.MacHigh = unchecked((uint)(value >> 32));
    }

    private bool TryExecute(ushort opcode, ISega32XSh2Bus bus)
    {
        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;

        switch (opcode & 0xF000)
        {
            case 0xE000: // MOV #imm, Rn
                Registers.GeneralPurposeRegisters[n] = unchecked((uint)(sbyte)(opcode & 0xFF));
                return true;
            case 0xD000: // MOV.L @(disp, PC), Rn
                {
                    uint disp = (uint)((opcode & 0xFF) << 2);
                    uint address = (Registers.NextProgramCounter & ~3u) + disp;
                    Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
                    return true;
                }
            case 0x9000: // MOV.W @(disp, PC), Rn
                {
                    uint disp = (uint)((opcode & 0xFF) << 1);
                    uint address = Registers.NextProgramCounter + disp;
                    Registers.GeneralPurposeRegisters[n] = unchecked((uint)(short)bus.ReadWord(address, Sega32XSh2AccessContext.Data));
                    return true;
                }
            case 0x5000: // MOV.L @(disp, Rm), Rn
                {
                    uint disp = (uint)((opcode & 0xF) << 2);
                    uint address = Registers.GeneralPurposeRegisters[m] + disp;
                    Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(address, Sega32XSh2AccessContext.Data);
                    return true;
                }
            case 0x1000: // MOV.L Rm, @(disp, Rn)
                {
                    uint disp = (uint)((opcode & 0xF) << 2);
                    uint address = Registers.GeneralPurposeRegisters[n] + disp;
                    bus.WriteLongword(address, Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                    return true;
                }
            case 0x7000: // ADD #imm, Rn
                Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] + (uint)(sbyte)(opcode & 0xFF));
                return true;
            case 0xA000: // BRA label
                {
                    int disp = ((short)(opcode << 4)) >> 4;
                    Registers.NextProgramCounter = Registers.NextProgramCounter + (uint)(disp << 1);
                    Registers.NextInstructionInDelaySlot = true;
                    return true;
                }
            case 0xB000: // BSR label
                {
                    int disp = ((short)(opcode << 4)) >> 4;
                    Registers.ProcedureRegister = Registers.NextProgramCounter;
                    Registers.NextProgramCounter = Registers.NextProgramCounter + (uint)(disp << 1);
                    Registers.NextInstructionInDelaySlot = true;
                    return true;
                }
            case 0x6000:
                switch (opcode & 0xF00F)
                {
                    case 0x6000: // MOV.B @Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(sbyte)bus.ReadByte(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data));
                        return true;
                    case 0x6001: // MOV.W @Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(short)bus.ReadWord(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data));
                        return true;
                    case 0x6002: // MOV.L @Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x6003: // MOV Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.GeneralPurposeRegisters[m];
                        return true;
                    case 0x6004: // MOV.B @Rm+, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(sbyte)bus.ReadByte(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data));
                        if (n != m) Registers.GeneralPurposeRegisters[m]++;
                        return true;
                    case 0x6005: // MOV.W @Rm+, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(short)bus.ReadWord(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data));
                        if (n != m) Registers.GeneralPurposeRegisters[m] += 2;
                        return true;
                    case 0x6006: // MOV.L @Rm+, Rn
                        Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        if (n != m) Registers.GeneralPurposeRegisters[m] += 4;
                        return true;
                    case 0x6007: // NOT Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = ~Registers.GeneralPurposeRegisters[m];
                        return true;
                    case 0x6008: // SWAP.B Rm, Rn
                        {
                            uint val = Registers.GeneralPurposeRegisters[m];
                            Registers.GeneralPurposeRegisters[n] = (val & 0xFFFF0000) | ((val & 0x000000FF) << 8) | ((val & 0x0000FF00) >> 8);
                            return true;
                        }
                    case 0x6009: // SWAP.W Rm, Rn
                        {
                            uint val = Registers.GeneralPurposeRegisters[m];
                            Registers.GeneralPurposeRegisters[n] = (val << 16) | (val >> 16);
                            return true;
                        }
                    case 0x600A: // NEGC Rm, Rn
                        {
                            uint src = Registers.GeneralPurposeRegisters[m];
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            uint partial = unchecked(0u - src);
                            bool b1 = partial > 0;
                            uint res = unchecked(partial - (sr.T ? 1u : 0u));
                            bool b2 = res > partial;
                            sr.T = b1 || b2;
                            Registers.StatusRegister = sr;
                            Registers.GeneralPurposeRegisters[n] = res;
                            return true;
                        }
                    case 0x600B: // NEG Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked(0u - Registers.GeneralPurposeRegisters[m]);
                        return true;
                    case 0x600C: // EXTU.B Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.GeneralPurposeRegisters[m] & 0xFF;
                        return true;
                    case 0x600D: // EXTU.W Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.GeneralPurposeRegisters[m] & 0xFFFF;
                        return true;
                    case 0x600E: // EXTS.B Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(sbyte)Registers.GeneralPurposeRegisters[m]);
                        return true;
                    case 0x600F: // EXTS.W Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(short)Registers.GeneralPurposeRegisters[m]);
                        return true;
                }
                break;
            case 0x2000:
                switch (opcode & 0xF00F)
                {
                    case 0x2000: // MOV.B Rm, @Rn
                        bus.WriteByte(Registers.GeneralPurposeRegisters[n], (byte)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x2001: // MOV.W Rm, @Rn
                        bus.WriteWord(Registers.GeneralPurposeRegisters[n], (ushort)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x2002: // MOV.L Rm, @Rn
                        bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x2004: // MOV.B Rm, @-Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 1);
                        bus.WriteByte(Registers.GeneralPurposeRegisters[n], (byte)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x2005: // MOV.W Rm, @-Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 2);
                        bus.WriteWord(Registers.GeneralPurposeRegisters[n], (ushort)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x2006: // MOV.L Rm, @-Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 4);
                        bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                        return true;
                    case 0x2008: // TST Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = (Registers.GeneralPurposeRegisters[m] & Registers.GeneralPurposeRegisters[n]) == 0;
                            Registers.StatusRegister = sr;
                            return true;
                        }
                    case 0x2009: // AND Rm, Rn
                        Registers.GeneralPurposeRegisters[n] &= Registers.GeneralPurposeRegisters[m];
                        return true;
                    case 0x200A: // XOR Rm, Rn
                        Registers.GeneralPurposeRegisters[n] ^= Registers.GeneralPurposeRegisters[m];
                        return true;
                    case 0x200B: // OR Rm, Rn
                        Registers.GeneralPurposeRegisters[n] |= Registers.GeneralPurposeRegisters[m];
                        return true;
                    case 0x200D: // XTRACT Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = (Registers.GeneralPurposeRegisters[m] << 16) | (Registers.GeneralPurposeRegisters[n] >> 16);
                        return true;
                    case 0x200E: // MULU.W Rm, Rn
                        Registers.MacLow = (Registers.GeneralPurposeRegisters[m] & 0xFFFF) * (Registers.GeneralPurposeRegisters[n] & 0xFFFF);
                        return true;
                    case 0x200F: // MULS.W Rm, Rn
                        Registers.MacLow = unchecked((uint)((short)Registers.GeneralPurposeRegisters[m] * (short)Registers.GeneralPurposeRegisters[n]));
                        return true;
                    case 0x2007: // DIV0S Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.M = (Registers.GeneralPurposeRegisters[m] & 0x80000000) != 0;
                            sr.Q = (Registers.GeneralPurposeRegisters[n] & 0x80000000) != 0;
                            sr.T = sr.M != sr.Q;
                            Registers.StatusRegister = sr;
                            return true;
                        }
                }
                break;
            case 0x3000:
                switch (opcode & 0xF00F)
                {
                    case 0x3000: // CMP/EQ Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = Registers.GeneralPurposeRegisters[n] == Registers.GeneralPurposeRegisters[m];
                            Registers.StatusRegister = sr;
                            return true;
                        }
                    case 0x3002: // CMP/HS Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = Registers.GeneralPurposeRegisters[n] >= Registers.GeneralPurposeRegisters[m];
                            Registers.StatusRegister = sr;
                            return true;
                        }
                    case 0x3003: // CMP/GE Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = (int)Registers.GeneralPurposeRegisters[n] >= (int)Registers.GeneralPurposeRegisters[m];
                            Registers.StatusRegister = sr;
                            return true;
                        }
                    case 0x3006: // CMP/HI Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = Registers.GeneralPurposeRegisters[n] > Registers.GeneralPurposeRegisters[m];
                            Registers.StatusRegister = sr;
                            return true;
                        }
                    case 0x3007: // CMP/GT Rm, Rn
                        {
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = (int)Registers.GeneralPurposeRegisters[n] > (int)Registers.GeneralPurposeRegisters[m];
                            Registers.StatusRegister = sr;
                            return true;
                        }
                    case 0x3008: // SUB Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - Registers.GeneralPurposeRegisters[m]);
                        return true;
                    case 0x300A: // SUBC Rm, Rn
                        {
                            uint lhs = Registers.GeneralPurposeRegisters[n];
                            uint rhs = Registers.GeneralPurposeRegisters[m];
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            uint p = unchecked(lhs - rhs);
                            bool b1 = p > lhs;
                            uint res = unchecked(p - (sr.T ? 1u : 0u));
                            bool b2 = res > p;
                            sr.T = b1 || b2;
                            Registers.StatusRegister = sr;
                            Registers.GeneralPurposeRegisters[n] = res;
                            return true;
                        }
                    case 0x300B: // SUBV Rm, Rn
                        {
                            uint lhs = Registers.GeneralPurposeRegisters[n];
                            uint rhs = Registers.GeneralPurposeRegisters[m];
                            uint res = unchecked(lhs - rhs);
                            bool s1 = (rhs & 0x80000000) != 0;
                            bool s2 = (lhs & 0x80000000) != 0;
                            bool s3 = (res & 0x80000000) != 0;
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = (s1 != s2) && (s3 != s2);
                            Registers.StatusRegister = sr;
                            Registers.GeneralPurposeRegisters[n] = res;
                            return true;
                        }
                    case 0x300C: // ADD Rm, Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] + Registers.GeneralPurposeRegisters[m]);
                        return true;
                    case 0x300E: // ADDC Rm, Rn
                        {
                            uint lhs = Registers.GeneralPurposeRegisters[n];
                            uint rhs = Registers.GeneralPurposeRegisters[m];
                            uint p = unchecked(lhs + rhs);
                            bool c1 = p < lhs;
                            uint res = unchecked(p + (Registers.StatusRegister.T ? 1u : 0u));
                            bool c2 = res < p;
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = c1 || c2;
                            Registers.StatusRegister = sr;
                            Registers.GeneralPurposeRegisters[n] = res;
                            return true;
                        }
                    case 0x300F: // ADDV Rm, Rn
                        {
                            uint lhs = Registers.GeneralPurposeRegisters[n];
                            uint rhs = Registers.GeneralPurposeRegisters[m];
                            uint res = unchecked(lhs + rhs);
                            bool s1 = (lhs & 0x80000000) != 0;
                            bool s2 = (rhs & 0x80000000) != 0;
                            bool s3 = (res & 0x80000000) != 0;
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            sr.T = (s1 == s2) && (s1 != s3);
                            Registers.StatusRegister = sr;
                            Registers.GeneralPurposeRegisters[n] = res;
                            return true;
                        }
                    case 0x300D: // DMULS.L Rm, Rn
                        {
                            long res = (long)(int)Registers.GeneralPurposeRegisters[m] * (int)Registers.GeneralPurposeRegisters[n];
                            Registers.MacLow = unchecked((uint)res);
                            Registers.MacHigh = unchecked((uint)(res >> 32));
                            return true;
                        }
                    case 0x3005: // DMULU.L Rm, Rn
                        {
                            ulong res = (ulong)Registers.GeneralPurposeRegisters[m] * Registers.GeneralPurposeRegisters[n];
                            Registers.MacLow = (uint)res;
                            Registers.MacHigh = (uint)(res >> 32);
                            return true;
                        }
                    case 0x3004: // DIV1 Rm, Rn
                        {
                            uint divisor = Registers.GeneralPurposeRegisters[m];
                            uint dividend = Registers.GeneralPurposeRegisters[n];
                            
                            Sega32XSh2StatusRegister sr = Registers.StatusRegister;
                            bool oldQ = sr.Q;
                            sr.Q = (dividend & 0x80000000) != 0;
                            
                            dividend <<= 1;
                            if (sr.T) dividend |= 1;
                            
                            if (oldQ == sr.M)
                            {
                                uint prev = dividend;
                                dividend = unchecked(dividend - divisor);
                                sr.T = dividend > prev;
                            }
                            else
                            {
                                uint prev = dividend;
                                dividend = unchecked(dividend + divisor);
                                sr.T = dividend < prev;
                            }
                            
                            sr.Q = (sr.Q ^ sr.M ^ sr.T);
                            sr.T = (sr.Q == sr.M);
                            
                            Registers.StatusRegister = sr;
                            Registers.GeneralPurposeRegisters[n] = dividend;
                            return true;
                        }
                }
                break;
            case 0x4000:
                if ((opcode & 0xF00F) == 0x400F) // MAC.W @Rm+, @Rn+
                {
                    short valM = (short)bus.ReadWord(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data);
                    Registers.GeneralPurposeRegisters[m] += 2;
                    short valN = (short)bus.ReadWord(Registers.GeneralPurposeRegisters[n], Sega32XSh2AccessContext.Data);
                    Registers.GeneralPurposeRegisters[n] += 2;
                    int product = valM * valN;
                    if (Registers.StatusRegister.S)
                    {
                        long currentMacL = (int)Registers.MacLow;
                        long result = currentMacL + product;
                        if (result > int.MaxValue) { Registers.MacLow = int.MaxValue; Registers.MacHigh |= 1; }
                        else if (result < int.MinValue) { Registers.MacLow = unchecked((uint)int.MinValue); Registers.MacHigh |= 1; }
                        else Registers.MacLow = (uint)result;
                    }
                    else SetMac(GetMac() + product);
                    return true;
                }

                switch (opcode & 0xF0FF)
                {
                    case 0x4015: // CMP/PL Rn
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (int)Registers.GeneralPurposeRegisters[n] > 0; Registers.StatusRegister = sr; return true; }
                    case 0x4011: // CMP/PZ Rn
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (int)Registers.GeneralPurposeRegisters[n] >= 0; Registers.StatusRegister = sr; return true; }
                    case 0x4010: // DT Rn
                        { Registers.GeneralPurposeRegisters[n] = unchecked(Registers.GeneralPurposeRegisters[n] - 1); Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = Registers.GeneralPurposeRegisters[n] == 0; Registers.StatusRegister = sr; return true; }
                    case 0x4001: // SHLR Rn
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (Registers.GeneralPurposeRegisters[n] & 1) != 0; Registers.StatusRegister = sr; Registers.GeneralPurposeRegisters[n] >>= 1; return true; }
                    case 0x4000: // SHLL Rn
                    case 0x4020: // SHAL Rn
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (Registers.GeneralPurposeRegisters[n] & 0x80000000) != 0; Registers.StatusRegister = sr; Registers.GeneralPurposeRegisters[n] <<= 1; return true; }
                    case 0x4021: // SHAR Rn
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (Registers.GeneralPurposeRegisters[n] & 1) != 0; Registers.StatusRegister = sr; Registers.GeneralPurposeRegisters[n] = (uint)((int)Registers.GeneralPurposeRegisters[n] >> 1); return true; }
                    case 0x4004: // ROTL Rn
                        { uint val = Registers.GeneralPurposeRegisters[n]; Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (val & 0x80000000) != 0; Registers.StatusRegister = sr; Registers.GeneralPurposeRegisters[n] = (val << 1) | (val >> 31); return true; }
                    case 0x4005: // ROTR Rn
                        { uint val = Registers.GeneralPurposeRegisters[n]; Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (val & 1) != 0; Registers.StatusRegister = sr; Registers.GeneralPurposeRegisters[n] = (val >> 1) | (val << 31); return true; }
                    case 0x4024: // ROTCL Rn
                        { uint val = Registers.GeneralPurposeRegisters[n]; Sega32XSh2StatusRegister sr = Registers.StatusRegister; bool cO = (val & 0x80000000) != 0; Registers.GeneralPurposeRegisters[n] = (val << 1) | (sr.T ? 1u : 0u); sr.T = cO; Registers.StatusRegister = sr; return true; }
                    case 0x4025: // ROTCR Rn
                        { uint val = Registers.GeneralPurposeRegisters[n]; Sega32XSh2StatusRegister sr = Registers.StatusRegister; bool cO = (val & 1) != 0; Registers.GeneralPurposeRegisters[n] = (val >> 1) | ((sr.T ? 1u : 0u) << 31); sr.T = cO; Registers.StatusRegister = sr; return true; }
                    case 0x4008: // SHLL2 Rn
                        Registers.GeneralPurposeRegisters[n] <<= 2; return true;
                    case 0x4009: // SHLR2 Rn
                        Registers.GeneralPurposeRegisters[n] >>= 2; return true;
                    case 0x4018: // SHLL8 Rn
                        Registers.GeneralPurposeRegisters[n] <<= 8; return true;
                    case 0x4019: // SHLR8 Rn
                        Registers.GeneralPurposeRegisters[n] >>= 8; return true;
                    case 0x4028: // SHLL16 Rn
                        Registers.GeneralPurposeRegisters[n] <<= 16; return true;
                    case 0x4029: // SHLR16 Rn
                        Registers.GeneralPurposeRegisters[n] >>= 16; return true;
                    case 0x400B: // JSR @Rn
                        Registers.ProcedureRegister = Registers.NextProgramCounter; Registers.NextProgramCounter = Registers.GeneralPurposeRegisters[n]; Registers.NextInstructionInDelaySlot = true; bus.IncrementCycleCounter(1); CycleCounter += 1; return true;
                    case 0x402B: // JMP @Rn
                        Registers.NextProgramCounter = Registers.GeneralPurposeRegisters[n]; Registers.NextInstructionInDelaySlot = true; bus.IncrementCycleCounter(1); CycleCounter += 1; return true;
                    case 0x4002: // STS.L MACH, @-Rn
                        Registers.GeneralPurposeRegisters[n] -= 4; bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.MacHigh, Sega32XSh2AccessContext.Data); return true;
                    case 0x4012: // STS.L MACL, @-Rn
                        Registers.GeneralPurposeRegisters[n] -= 4; bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.MacLow, Sega32XSh2AccessContext.Data); return true;
                    case 0x4022: // STS.L PR, @-Rn
                        Registers.GeneralPurposeRegisters[n] -= 4; bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.ProcedureRegister, Sega32XSh2AccessContext.Data); return true;
                    case 0x4003: // STC.L SR, @-Rn
                        Registers.GeneralPurposeRegisters[n] -= 4; bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.StatusRegister.ToUInt32(), Sega32XSh2AccessContext.Data); return true;
                    case 0x4013: // STC.L GBR, @-Rn
                        Registers.GeneralPurposeRegisters[n] -= 4; bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.GlobalBaseRegister, Sega32XSh2AccessContext.Data); return true;
                    case 0x4023: // STC.L VBR, @-Rn
                        Registers.GeneralPurposeRegisters[n] -= 4; bus.WriteLongword(Registers.GeneralPurposeRegisters[n], Registers.VectorBaseRegister, Sega32XSh2AccessContext.Data); return true;
                    case 0x400A: // STS MACH, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.MacHigh; return true;
                    case 0x401A: // STS MACL, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.MacLow; return true;
                    case 0x400E: // LDC Rn, SR
                        Registers.StatusRegister = Sega32XSh2StatusRegister.FromUInt32(Registers.GeneralPurposeRegisters[n]); return true;
                    case 0x401E: // LDC Rn, GBR
                        Registers.GlobalBaseRegister = Registers.GeneralPurposeRegisters[n]; return true;
                    case 0x402E: // LDC Rn, VBR
                        Registers.VectorBaseRegister = Registers.GeneralPurposeRegisters[n]; return true;
                    case 0x4006: // LDS.L @Rn+, MACH
                        { uint addr = Registers.GeneralPurposeRegisters[n]; Registers.MacHigh = bus.ReadLongword(addr, Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[n] += 4; return true; }
                    case 0x4016: // LDS.L @Rn+, MACL
                        { uint addr = Registers.GeneralPurposeRegisters[n]; Registers.MacLow = bus.ReadLongword(addr, Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[n] += 4; return true; }
                    case 0x4026: // LDS.L @Rn+, PR
                        { uint addr = Registers.GeneralPurposeRegisters[n]; Registers.ProcedureRegister = bus.ReadLongword(addr, Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[n] += 4; return true; }
                    case 0x4007: // LDC.L @Rn+, SR
                        { uint addr = Registers.GeneralPurposeRegisters[n]; Registers.StatusRegister = Sega32XSh2StatusRegister.FromUInt32(bus.ReadLongword(addr, Sega32XSh2AccessContext.Data)); Registers.GeneralPurposeRegisters[n] += 4; return true; }
                    case 0x4017: // LDC.L @Rn+, GBR
                        { uint addr = Registers.GeneralPurposeRegisters[n]; Registers.GlobalBaseRegister = bus.ReadLongword(addr, Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[n] += 4; return true; }
                    case 0x4027: // LDC.L @Rn+, VBR
                        { uint addr = Registers.GeneralPurposeRegisters[n]; Registers.VectorBaseRegister = bus.ReadLongword(addr, Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[n] += 4; return true; }
                    case 0x401B: // TAS.B @Rn
                        { uint addr = Registers.GeneralPurposeRegisters[n]; byte val = bus.ReadByte(addr, Sega32XSh2AccessContext.Data); bus.WriteByte(addr, (byte)(val | 0x80), Sega32XSh2AccessContext.Data); Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = val == 0; Registers.StatusRegister = sr; bus.IncrementCycleCounter(3); CycleCounter += 3; return true; }
                }
                break;
            case 0x0000:
                switch (opcode & 0xF00F)
                {
                    case 0x0004: // MOV.B Rm, @(R0, Rn)
                        bus.WriteByte(Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[n], (byte)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data); return true;
                    case 0x0005: // MOV.W Rm, @(R0, Rn)
                        bus.WriteWord(Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[n], (ushort)Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data); return true;
                    case 0x0006: // MOV.L Rm, @(R0, Rn)
                        bus.WriteLongword(Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[n], Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data); return true;
                    case 0x000C: // MOV.B @(R0, Rm), Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(sbyte)bus.ReadByte(Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data)); return true;
                    case 0x000D: // MOV.W @(R0, Rm), Rn
                        Registers.GeneralPurposeRegisters[n] = unchecked((uint)(short)bus.ReadWord(Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data)); return true;
                    case 0x000E: // MOV.L @(R0, Rm), Rn
                        Registers.GeneralPurposeRegisters[n] = bus.ReadLongword(Registers.GeneralPurposeRegisters[0] + Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data); return true;
                    case 0x0007: // MUL.L Rm, Rn
                        Registers.MacLow = unchecked(Registers.GeneralPurposeRegisters[n] * Registers.GeneralPurposeRegisters[m]); return true;
                    case 0x000F: // MAC.L @Rm+, @Rn+
                        {
                            uint valM = bus.ReadLongword(Registers.GeneralPurposeRegisters[m], Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[m] += 4;
                            uint valN = bus.ReadLongword(Registers.GeneralPurposeRegisters[n], Sega32XSh2AccessContext.Data); Registers.GeneralPurposeRegisters[n] += 4;
                            long pS = unchecked(((long)(int)valM * (int)valN) + GetMac());
                            if (Registers.StatusRegister.S) { const long Min48 = -(1L << 47); const long Max48 = (1L << 47) - 1; pS = Math.Clamp(pS, Min48, Max48); }
                            SetMac(pS); return true;
                        }
                }
                switch (opcode & 0xF0FF)
                {
                    case 0x0002: // STC SR, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.StatusRegister.ToUInt32(); return true;
                    case 0x0012: // STC GBR, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.GlobalBaseRegister; return true;
                    case 0x0022: // STC VBR, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.VectorBaseRegister; return true;
                    case 0x000A: // STS MACH, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.MacHigh; return true;
                    case 0x001A: // STS MACL, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.MacLow; return true;
                    case 0x002A: // STS PR, Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.ProcedureRegister; return true;
                    case 0x0003: // BSRF Rn
                        Registers.ProcedureRegister = Registers.NextProgramCounter; Registers.NextProgramCounter = Registers.NextProgramCounter + Registers.GeneralPurposeRegisters[n]; Registers.NextInstructionInDelaySlot = true; return true;
                    case 0x0023: // BRAF Rn
                        Registers.NextProgramCounter = Registers.NextProgramCounter + Registers.GeneralPurposeRegisters[n]; Registers.NextInstructionInDelaySlot = true; return true;
                    case 0x0008: // CLRT
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = false; Registers.StatusRegister = sr; return true; }
                    case 0x0018: // SETT
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = true; Registers.StatusRegister = sr; return true; }
                    case 0x001B: // SLEEP
                        return true;
                    case 0x0028: // CLRMAC
                        Registers.MacLow = 0; Registers.MacHigh = 0; return true;
                    case 0x0019: // DIV0U
                        { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.M = false; sr.Q = false; sr.T = false; Registers.StatusRegister = sr; return true; }
                    case 0x0029: // MOVT Rn
                        Registers.GeneralPurposeRegisters[n] = Registers.StatusRegister.T ? 1u : 0u; return true;
                }
                if (opcode == 0x000B) { Registers.NextProgramCounter = Registers.ProcedureRegister; Registers.NextInstructionInDelaySlot = true; return true; }
                if (opcode == 0x002B) {
                    uint sp = Registers.StackPointer;
                    Registers.NextProgramCounter = bus.ReadLongword(sp, Sega32XSh2AccessContext.Data);
                    Registers.StackPointer = sp + 4;
                    Registers.StatusRegister = Sega32XSh2StatusRegister.FromUInt32(bus.ReadLongword(Registers.StackPointer, Sega32XSh2AccessContext.Data));
                    Registers.StackPointer += 4;
                    Registers.NextInstructionInDelaySlot = true;
                    bus.IncrementCycleCounter(3); CycleCounter += 3;
                    return true;
                }
                if (opcode == 0x0009) return true;
                break;
            case 0x8000:
                switch ((opcode >> 8) & 0xF)
                {
                    case 0x8: { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = Registers.GeneralPurposeRegisters[0] == unchecked((uint)(sbyte)(opcode & 0xFF)); Registers.StatusRegister = sr; return true; }
                    case 0x9: if (Registers.StatusRegister.T) { Registers.ProgramCounter = Registers.NextProgramCounter + (uint)((sbyte)(opcode & 0xFF) << 1); Registers.NextProgramCounter = Registers.ProgramCounter + 2; } return true;
                    case 0xB: if (!Registers.StatusRegister.T) { Registers.ProgramCounter = Registers.NextProgramCounter + (uint)((sbyte)(opcode & 0xFF) << 1); Registers.NextProgramCounter = Registers.ProgramCounter + 2; } return true;
                    case 0xD: if (Registers.StatusRegister.T) { Registers.NextProgramCounter = Registers.NextProgramCounter + (uint)((sbyte)(opcode & 0xFF) << 1); Registers.NextInstructionInDelaySlot = true; } return true;
                    case 0xF: if (!Registers.StatusRegister.T) { Registers.NextProgramCounter = Registers.NextProgramCounter + (uint)((sbyte)(opcode & 0xFF) << 1); Registers.NextInstructionInDelaySlot = true; } return true;
                    case 0x0: bus.WriteByte(Registers.GeneralPurposeRegisters[m] + (uint)(opcode & 0xF), (byte)Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data); return true;
                    case 0x1: bus.WriteWord(Registers.GeneralPurposeRegisters[m] + (uint)((opcode & 0xF) << 1), (ushort)Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data); return true;
                    case 0x4: Registers.GeneralPurposeRegisters[0] = unchecked((uint)(sbyte)bus.ReadByte(Registers.GeneralPurposeRegisters[m] + (uint)(opcode & 0xF), Sega32XSh2AccessContext.Data)); return true;
                    case 0x5: Registers.GeneralPurposeRegisters[0] = unchecked((uint)(short)bus.ReadWord(Registers.GeneralPurposeRegisters[m] + (uint)((opcode & 0xF) << 1), Sega32XSh2AccessContext.Data)); return true;
                }
                break;
            case 0xC000:
                switch ((opcode >> 8) & 0xF)
                {
                    case 0x0: bus.WriteByte(Registers.GlobalBaseRegister + (uint)(opcode & 0xFF), (byte)Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data); return true;
                    case 0x1: bus.WriteWord(Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 1), (ushort)Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data); return true;
                    case 0x2: bus.WriteLongword(Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 2), Registers.GeneralPurposeRegisters[0], Sega32XSh2AccessContext.Data); return true;
                    case 0x4: Registers.GeneralPurposeRegisters[0] = unchecked((uint)(sbyte)bus.ReadByte(Registers.GlobalBaseRegister + (uint)(opcode & 0xFF), Sega32XSh2AccessContext.Data)); return true;
                    case 0x5: Registers.GeneralPurposeRegisters[0] = unchecked((uint)(short)bus.ReadWord(Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 1), Sega32XSh2AccessContext.Data)); return true;
                    case 0x6: Registers.GeneralPurposeRegisters[0] = bus.ReadLongword(Registers.GlobalBaseRegister + (uint)((opcode & 0xFF) << 2), Sega32XSh2AccessContext.Data); return true;
                    case 0x8: { Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (Registers.GeneralPurposeRegisters[0] & (uint)(opcode & 0xFF)) == 0; Registers.StatusRegister = sr; return true; }
                    case 0x9: Registers.GeneralPurposeRegisters[0] &= (uint)(opcode & 0xFF); return true;
                    case 0xA: Registers.GeneralPurposeRegisters[0] ^= (uint)(opcode & 0xFF); return true;
                    case 0xB: Registers.GeneralPurposeRegisters[0] |= (uint)(opcode & 0xFF); return true;
                    case 0xC: { uint address = Registers.GlobalBaseRegister + Registers.GeneralPurposeRegisters[0]; byte val = bus.ReadByte(address, Sega32XSh2AccessContext.Data); Sega32XSh2StatusRegister sr = Registers.StatusRegister; sr.T = (val & (uint)(opcode & 0xFF)) == 0; Registers.StatusRegister = sr; bus.IncrementCycleCounter(3); CycleCounter += 3; return true; }
                    case 0xD: { uint address = Registers.GlobalBaseRegister + Registers.GeneralPurposeRegisters[0]; byte val = (byte)(bus.ReadByte(address, Sega32XSh2AccessContext.Data) & (opcode & 0xFF)); bus.WriteByte(address, val, Sega32XSh2AccessContext.Data); bus.IncrementCycleCounter(3); CycleCounter += 3; return true; }
                    case 0xE: { uint address = Registers.GlobalBaseRegister + Registers.GeneralPurposeRegisters[0]; byte val = (byte)(bus.ReadByte(address, Sega32XSh2AccessContext.Data) ^ (opcode & 0xFF)); bus.WriteByte(address, val, Sega32XSh2AccessContext.Data); bus.IncrementCycleCounter(3); CycleCounter += 3; return true; }
                    case 0xF: { uint address = Registers.GlobalBaseRegister + Registers.GeneralPurposeRegisters[0]; byte val = (byte)(bus.ReadByte(address, Sega32XSh2AccessContext.Data) | (opcode & 0xFF)); bus.WriteByte(address, val, Sega32XSh2AccessContext.Data); bus.IncrementCycleCounter(3); CycleCounter += 3; return true; }
                    case 0x7: Registers.GeneralPurposeRegisters[0] = (Registers.NextProgramCounter & ~3u) + (uint)((opcode & 0xFF) << 2); return true;
                    case 0x3:
                        {
                            uint sp = unchecked(Registers.StackPointer - 4);
                            bus.WriteLongword(sp, Registers.StatusRegister.ToUInt32(), Sega32XSh2AccessContext.Data);
                            sp -= 4; bus.WriteLongword(sp, Registers.NextProgramCounter, Sega32XSh2AccessContext.Data);
                            Registers.StackPointer = sp;
                            uint vA = Registers.VectorBaseRegister + (uint)((opcode & 0xFF) << 2);
                            Registers.ProgramCounter = bus.ReadLongword(vA, Sega32XSh2AccessContext.InterruptVector);
                            Registers.NextProgramCounter = Registers.ProgramCounter + 2;
                            bus.IncrementCycleCounter(6); CycleCounter += 6;
                            return true;
                        }
                }
                break;
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
