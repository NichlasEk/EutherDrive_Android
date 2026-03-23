using System;

namespace KSNES.Specialchips.SuperFX;

internal enum MemoryType
{
    CodeCache,
    Rom,
    Ram
}

internal enum WaitKind
{
    None,
    Rom,
    Ram
}

internal static class MemoryTypeExtensions
{
    public static byte AccessCycles(this MemoryType memoryType, ClockSpeed clockSpeed)
    {
        return memoryType switch
        {
            MemoryType.CodeCache => 1,
            MemoryType.Rom or MemoryType.Ram => clockSpeed.MemoryAccessCycles(),
            _ => 1
        };
    }
}

internal static class Instructions
{
    public const byte NopOpcode = 0x01;
    private static readonly bool TraceCache =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_SUPERFX_CACHE"), "1", StringComparison.Ordinal);
    private static readonly int TraceCacheLimit =
        ParseTraceLimit("EUTHERDRIVE_TRACE_SNES_SUPERFX_CACHE_LIMIT", 2048);
    private static readonly int TraceCacheStart =
        ParseTraceHex("EUTHERDRIVE_TRACE_SNES_SUPERFX_CACHE_START", 0x8C30);
    private static readonly int TraceCacheEnd =
        ParseTraceHex("EUTHERDRIVE_TRACE_SNES_SUPERFX_CACHE_END", 0x8C3F);
    private static readonly bool TraceExec =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_SUPERFX_EXEC"), "1", StringComparison.Ordinal);
    private static readonly int TraceExecLimit =
        ParseTraceLimit("EUTHERDRIVE_TRACE_SNES_SUPERFX_EXEC_LIMIT", 2048);
    private static readonly int TraceExecStart =
        ParseTraceHex("EUTHERDRIVE_TRACE_SNES_SUPERFX_EXEC_START", 0xD0B0);
    private static readonly int TraceExecEnd =
        ParseTraceHex("EUTHERDRIVE_TRACE_SNES_SUPERFX_EXEC_END", 0xC860);
    private static int _traceCacheCount;
    private static int _traceExecCount;

    public static byte Execute(GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        MemoryType memoryType = NextOpcodeMemoryType(gsu);
        byte opcode = gsu.State.OpcodeBuffer;
        
        if (TraceCache)
            TraceCacheEvent(gsu, "EXEC", gsu.R[15], $"nextType={memoryType} opcode=0x{opcode:X2}");
            
        if (TraceExec)
            TraceExecEvent(gsu, memoryType, opcode);
            
        WaitKind waitKind = GetWaitKind(gsu, memoryType, opcode);
        if (waitKind != WaitKind.None)
        {
            // GSU is waiting for ROM/RAM access
            if (TraceCache)
            {
                TraceCacheEvent(
                    gsu,
                    waitKind == WaitKind.Ram ? "STALL-RAM" : "STALL-ROM",
                    gsu.R[15],
                    $"opcode=0x{opcode:X2} rom={gsu.RomAccess} ram={gsu.RamAccess}");
            }
            return 1;
        }

        byte cycles = 0;

        if (gsu.State.JustJumped)
        {
            TraceCacheEvent(gsu, "JUST-JUMPED", gsu.R[15], "refill=to-pc");
            gsu.State.JustJumped = false;
            cycles = (byte)(cycles + FillCacheToPc(gsu, gsu.R[15], rom, ram));
        }

        if (memoryType == MemoryType.Rom && gsu.State.RomBufferWaitCycles != 0)
        {
            cycles = (byte)(cycles + gsu.State.RomBufferWaitCycles);
            gsu.State.RomBufferWaitCycles = 0;
        }

        if (memoryType == MemoryType.Ram && gsu.State.RamBufferWaitCycles != 0)
        {
            cycles = (byte)(cycles + gsu.State.RamBufferWaitCycles);
            gsu.State.RamBufferWaitCycles = 0;
        }

        FetchOpcode(gsu, rom, ram);
        cycles = (byte)(cycles + ExecuteOpcode(opcode, memoryType, gsu, rom, ram));

        if (gsu.State.RomPointerChanged)
        {
            gsu.State.RomPointerChanged = false;
        }
        else
        {
            gsu.State.RomBufferWaitCycles = SaturatingSub(gsu.State.RomBufferWaitCycles, cycles);
        }

        if (gsu.State.RamBufferWritten)
        {
            gsu.State.RamBufferWritten = false;
        }
        else
        {
            gsu.State.RamBufferWaitCycles = SaturatingSub(gsu.State.RamBufferWaitCycles, cycles);
        }

        gsu.PlotState.Tick(cycles);

        if (gsu.StopState == StopState.StopPending)
        {
            gsu.StopState = StopState.None;
            gsu.Go = false;
            gsu.Irq = true;

            gsu.State.OpcodeBuffer = NopOpcode;
            gsu.R[15] = unchecked((ushort)(gsu.R[15] - 1));
        }
        else
        {
            gsu.StopState = gsu.StopState.Next();
        }

        return cycles;
    }

    private static bool IsRomAccessOpcode(byte opcode)
    {
        // GETB/GETBH/GETBL/GETBS ($EF)
        // GETC/ROMB ($DF)
        return opcode == 0xDF || opcode == 0xEF;
    }

    private static bool IsRamAccessOpcode(byte opcode, bool alt1, bool alt2)
    {
        return (opcode >= 0x30 && opcode <= 0x3B)
            || (opcode >= 0x40 && opcode <= 0x4B)
            || opcode == 0x90
            || ((alt1 || alt2) && ((opcode >= 0xA0 && opcode <= 0xAF) || (opcode >= 0xF0 && opcode <= 0xFF)));
    }

    internal static (byte Value, MemoryType Type) ReadMemory(byte bank, ushort address, byte[] rom, byte[] ram)
    {
        int bankMasked = bank & 0x7F;
        if (bankMasked <= 0x3F)
        {
            uint romAddr = SuperFx.MapLoRomAddress(((uint)bank << 16) | address, (uint)rom.Length);
            return (rom[romAddr % (uint)rom.Length], MemoryType.Rom);
        }
        if (bankMasked <= 0x5F)
        {
            uint romAddr = SuperFx.MapHiRomAddress(((uint)bank << 16) | address, (uint)rom.Length);
            return (rom[romAddr % (uint)rom.Length], MemoryType.Rom);
        }
        if (bankMasked == 0x70 || bankMasked == 0x71)
        {
            return (ram[address & (ram.Length - 1)], MemoryType.Ram);
        }

        return (0, MemoryType.CodeCache);
    }

    internal static void FetchOpcode(GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort fetchAddr = gsu.R[15];
        bool isCacheable = gsu.CodeCache.PcIsCacheable(fetchAddr);
        byte? cached = gsu.CodeCache.Get(fetchAddr);
        if (isCacheable && cached.HasValue)
        {
            gsu.State.OpcodeBuffer = cached.Value;
            TraceCacheEvent(gsu, "FETCH-HIT", fetchAddr, $"opcode=0x{cached.Value:X2}");
            gsu.R[15] = unchecked((ushort)(gsu.R[15] + 1));
            return;
        }

        (byte opcode, MemoryType sourceType) = ReadMemory(gsu.Pbr, fetchAddr, rom, ram);
        gsu.State.OpcodeBuffer = opcode;
        TraceCacheEvent(
            gsu,
            isCacheable ? "FETCH-MISS" : "FETCH-UNCACHED",
            fetchAddr,
            $"opcode=0x{opcode:X2} src={sourceType}");

        if (isCacheable)
        {
            gsu.CodeCache.Set(fetchAddr, opcode);
            TraceCacheEvent(gsu, "FETCH-SET", fetchAddr, $"opcode=0x{opcode:X2}");
        }

        gsu.R[15] = unchecked((ushort)(gsu.R[15] + 1));
    }

    internal static MemoryType NextOpcodeMemoryType(GraphicsSupportUnit gsu)
    {
        if (gsu.CodeCache.PcIsCacheable(gsu.R[15]) && gsu.CodeCache.Get(gsu.R[15]).HasValue)
        {
            return MemoryType.CodeCache;
        }

        int bankMasked = gsu.Pbr & 0x7F;
        if (bankMasked <= 0x5F)
        {
            return MemoryType.Rom;
        }
        if (bankMasked == 0x70 || bankMasked == 0x71)
        {
            return MemoryType.Ram;
        }

        return MemoryType.Rom;
    }

    internal static byte FillCacheToPc(GraphicsSupportUnit gsu, ushort pc, byte[] rom, byte[] ram)
    {
        if (!gsu.CodeCache.PcIsCacheable(pc) || gsu.CodeCache.Get(pc).HasValue)
        {
            TraceCacheEvent(gsu, "FILL-TO-SKIP", pc, "reason=not-needed");
            return 0;
        }

        int count = pc & 0xF;
        for (int i = 0; i < count; i++)
        {
            ushort cacheAddr = (ushort)((pc & 0xFFF0) | i);
            (byte opcode, _) = ReadMemory(gsu.Pbr, cacheAddr, rom, ram);
            gsu.CodeCache.Set(cacheAddr, opcode);
            TraceCacheEvent(gsu, "FILL-TO", cacheAddr, $"opcode=0x{opcode:X2}");
        }

        return (byte)(gsu.ClockSpeed.MemoryAccessCycles() * count);
    }

    internal static byte CacheAtPc(GraphicsSupportUnit gsu, ushort pc, byte[] rom, byte[] ram)
    {
        if (!gsu.CodeCache.PcIsCacheable(pc) || gsu.CodeCache.Get(pc).HasValue)
        {
            TraceCacheEvent(gsu, "CACHE-PC-SKIP", pc, "reason=not-needed");
            return 0;
        }

        (byte opcode, _) = ReadMemory(gsu.Pbr, pc, rom, ram);
        gsu.CodeCache.Set(pc, opcode);
        TraceCacheEvent(gsu, "CACHE-PC", pc, $"opcode=0x{opcode:X2}");

        return gsu.ClockSpeed.MemoryAccessCycles();
    }

    internal static byte FillCacheFromPc(GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        if ((gsu.R[15] & 0xF) == 0)
        {
            TraceCacheEvent(gsu, "FILL-FROM-SKIP", gsu.R[15], "reason=line-start");
            return 0;
        }

        if (!gsu.CodeCache.PcIsCacheable(gsu.R[15]) || gsu.CodeCache.Get(gsu.R[15]).HasValue)
        {
            TraceCacheEvent(gsu, "FILL-FROM-SKIP", gsu.R[15], "reason=not-needed");
            return 0;
        }

        int start = gsu.R[15] & 0xF;
        for (int i = start; i < 0x10; i++)
        {
            ushort cacheAddr = (ushort)((gsu.R[15] & 0xFFF0) | i);
            (byte opcode, _) = ReadMemory(gsu.Pbr, cacheAddr, rom, ram);
            gsu.CodeCache.Set(cacheAddr, opcode);
            TraceCacheEvent(gsu, "FILL-FROM", cacheAddr, $"opcode=0x{opcode:X2}");
        }

        return (byte)(gsu.ClockSpeed.MemoryAccessCycles() * (0x10 - start));
    }

    internal static ushort ReadRegister(GraphicsSupportUnit gsu, byte register)
    {
        return register == 15
            ? unchecked((ushort)(gsu.R[15] - 1))
            : gsu.R[register];
    }

    internal static byte WriteRegister(GraphicsSupportUnit gsu, byte register, ushort value, byte[] rom, byte[] ram)
    {
        byte cycles = 0;
        if (register == 14)
        {
            (byte romByte, _) = ReadMemory(gsu.Rombr, value, rom, ram);
            gsu.State.RomBuffer = romByte;
            gsu.State.RomBufferWaitCycles = gsu.ClockSpeed.RomBufferWaitCycles();
            gsu.State.RomPointerChanged = true;
        }
        else if (register == 15)
        {
            gsu.State.JustJumped = true;
            cycles = (byte)(cycles + FillCacheFromPc(gsu, rom, ram));
        }

        gsu.R[register] = value;
        return cycles;
    }

    internal static void ClearPrefixFlags(GraphicsSupportUnit gsu)
    {
        gsu.Alt1 = false;
        gsu.Alt2 = false;
        gsu.B = false;
        gsu.SReg = 0;
        gsu.DReg = 0;
    }

    private static byte ExecuteOpcode(byte opcode, MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        return opcode switch
        {
            0x00 => Stop(memoryType, gsu),
            0x01 => Nop(memoryType, gsu),
            0x02 => Cache(memoryType, gsu, rom, ram),
            0x03 => Alu.Lsr(memoryType, gsu, rom, ram),
            0x04 => Alu.Rol(memoryType, gsu, rom, ram),
            0x05 => Flow.Bra(memoryType, gsu, rom, ram),
            0x06 => Flow.Bge(memoryType, gsu, rom, ram),
            0x07 => Flow.Blt(memoryType, gsu, rom, ram),
            0x08 => Flow.Bne(memoryType, gsu, rom, ram),
            0x09 => Flow.Beq(memoryType, gsu, rom, ram),
            0x0A => Flow.Bpl(memoryType, gsu, rom, ram),
            0x0B => Flow.Bmi(memoryType, gsu, rom, ram),
            0x0C => Flow.Bcc(memoryType, gsu, rom, ram),
            0x0D => Flow.Bcs(memoryType, gsu, rom, ram),
            0x0E => Flow.Bvc(memoryType, gsu, rom, ram),
            0x0F => Flow.Bvs(memoryType, gsu, rom, ram),
            >= 0x10 and <= 0x1F => Flags.To(opcode, memoryType, gsu, rom, ram),
            >= 0x20 and <= 0x2F => Flags.With(opcode, memoryType, gsu),
            >= 0x30 and <= 0x3B => gsu.Alt1
                ? Load.Stb(opcode, memoryType, gsu, ram)
                : Load.Stw(opcode, memoryType, gsu, ram),
            0x3C => Flow.Loop(memoryType, gsu, rom, ram),
            0x3D => Flags.Alt1(memoryType, gsu),
            0x3E => Flags.Alt2(memoryType, gsu),
            0x3F => Flags.Alt3(memoryType, gsu),
            >= 0x40 and <= 0x4B => gsu.Alt1
                ? Load.Ldb(opcode, memoryType, gsu, rom, ram)
                : Load.Ldw(opcode, memoryType, gsu, rom, ram),
            0x4C => gsu.Alt1
                ? Plot.Rpix(memoryType, gsu, rom, ram)
                : Plot.PlotPixel(memoryType, gsu, ram),
            0x4D => Load.Swap(memoryType, gsu, rom, ram),
            0x4E => gsu.Alt1
                ? Plot.Cmode(memoryType, gsu)
                : Plot.Color(memoryType, gsu),
            0x4F => Alu.Not(memoryType, gsu, rom, ram),
            >= 0x50 and <= 0x5F => Alu.Add(opcode, memoryType, gsu, rom, ram),
            >= 0x60 and <= 0x6F => Alu.Sub(opcode, memoryType, gsu, rom, ram),
            0x70 => Load.Merge(memoryType, gsu, rom, ram),
            >= 0x71 and <= 0x7F => Alu.And(opcode, memoryType, gsu, rom, ram),
            >= 0x80 and <= 0x8F => Alu.Mult(opcode, memoryType, gsu, rom, ram),
            0x90 => Load.Sbk(memoryType, gsu, ram),
            >= 0x91 and <= 0x94 => Flow.Link(opcode, memoryType, gsu),
            0x95 => Alu.Sex(memoryType, gsu, rom, ram),
            0x96 => Alu.Asr(memoryType, gsu, rom, ram),
            0x97 => Alu.Ror(memoryType, gsu, rom, ram),
            >= 0x98 and <= 0x9D => gsu.Alt1
                ? Flow.Ljmp(opcode, memoryType, gsu, rom, ram)
                : Flow.Jmp(opcode, memoryType, gsu, rom, ram),
            0x9E => Load.Lob(memoryType, gsu, rom, ram),
            0x9F => Alu.Fmult(memoryType, gsu, rom, ram),
            >= 0xA0 and <= 0xAF => (gsu.Alt2, gsu.Alt1) switch
            {
                (false, false) => Load.Ibt(opcode, memoryType, gsu, rom, ram),
                (true, false) => Load.Sms(opcode, memoryType, gsu, rom, ram),
                (_, true) => Load.Lms(opcode, memoryType, gsu, rom, ram)
            },
            >= 0xB0 and <= 0xBF => Flags.From(opcode, memoryType, gsu, rom, ram),
            0xC0 => Load.Hib(memoryType, gsu, rom, ram),
            >= 0xC1 and <= 0xCF => Alu.Or(opcode, memoryType, gsu, rom, ram),
            >= 0xD0 and <= 0xDE => Alu.Inc(opcode, memoryType, gsu, rom, ram),
            0xDF => (gsu.Alt2, gsu.Alt1) switch
            {
                (false, _) => Plot.Getc(memoryType, gsu),
                (true, false) => Nop(memoryType, gsu),
                (true, true) => Load.Romb(memoryType, gsu)
            },
            >= 0xE0 and <= 0xEE => Alu.Dec(opcode, memoryType, gsu, rom, ram),
            0xEF => Load.Getb(memoryType, gsu, rom, ram),
            >= 0xF0 and <= 0xFF => (gsu.Alt2, gsu.Alt1) switch
            {
                (false, false) => Load.Iwt(opcode, memoryType, gsu, rom, ram),
                (true, false) => Load.Sm(opcode, memoryType, gsu, rom, ram),
                (_, true) => Load.Lm(opcode, memoryType, gsu, rom, ram)
            }
        };
    }

    private static byte Stop(MemoryType memoryType, GraphicsSupportUnit gsu)
    {
        gsu.StopState = StopState.StopExecuted;
        ClearPrefixFlags(gsu);
        return memoryType.AccessCycles(gsu.ClockSpeed);
    }

    private static byte Nop(MemoryType memoryType, GraphicsSupportUnit gsu)
    {
        ClearPrefixFlags(gsu);
        return memoryType.AccessCycles(gsu.ClockSpeed);
    }

    private static byte Cache(MemoryType memoryType, GraphicsSupportUnit gsu, byte[] rom, byte[] ram)
    {
        ushort cbr = (ushort)((gsu.R[15] - 1) & 0xFFF0);
        bool updated = false;
        byte cycles = 0;
        if (cbr != gsu.CodeCache.Cbr)
        {
            ushort prevCbr = gsu.CodeCache.Cbr;
            gsu.CodeCache.UpdateCbr(cbr);
            TraceCacheEvent(gsu, "CBR-UPDATE", cbr, $"from=0x{prevCbr:X4}");
            cycles = (byte)(cycles + FillCacheToPc(gsu, unchecked((ushort)(gsu.R[15] - 1)), rom, ram));
            cycles = (byte)(cycles + CacheAtPc(gsu, unchecked((ushort)(gsu.R[15] - 1)), rom, ram));
            updated = true;
        }

        if (memoryType == MemoryType.CodeCache)
        {
            return (byte)(cycles + 1);
        }

        return (byte)(cycles + memoryType.AccessCycles(gsu.ClockSpeed) + (updated ? 1 : 0));
    }

    private static byte SaturatingSub(byte value, byte sub)
    {
        int result = value - sub;
        return (byte)(result < 0 ? 0 : result);
    }

    internal static WaitKind GetWaitKind(GraphicsSupportUnit gsu)
        => GetWaitKind(gsu, NextOpcodeMemoryType(gsu), gsu.State.OpcodeBuffer);

    private static WaitKind GetWaitKind(GraphicsSupportUnit gsu, MemoryType memoryType, byte opcode)
    {
        if (gsu.RomAccess == BusAccess.Snes
            && (memoryType == MemoryType.Rom || IsRomAccessOpcode(opcode)))
        {
            return WaitKind.Rom;
        }

        if (gsu.RamAccess == BusAccess.Snes
            && (memoryType == MemoryType.Ram || IsRamAccessOpcode(opcode, gsu.Alt1, gsu.Alt2)))
        {
            return WaitKind.Ram;
        }

        return WaitKind.None;
    }

    private static void TraceCacheEvent(GraphicsSupportUnit gsu, string evt, ushort address, string extra)
    {
        if (!TraceCache || _traceCacheCount >= TraceCacheLimit)
            return;

        if (address < TraceCacheStart || address > TraceCacheEnd)
            return;

        _traceCacheCount++;
        bool cacheable = gsu.CodeCache.PcIsCacheable(address);
        byte? cached = gsu.CodeCache.Get(address);
        string cachedValue = cached.HasValue ? $"0x{cached.Value:X2}" : "--";
        string suffix = string.IsNullOrWhiteSpace(extra) ? string.Empty : $" {extra}";
        Console.WriteLine(
            $"[SFX-CACHE] evt={evt} pbr=0x{gsu.Pbr:X2} addr=0x{address:X4} cbr=0x{gsu.CodeCache.Cbr:X4} " +
            $"lines=0x{gsu.CodeCache.CachedLines:X8} cacheable={(cacheable ? 1 : 0)} cached={(cached.HasValue ? 1 : 0)} " +
            $"val={cachedValue} r15=0x{gsu.R[15]:X4} go={(gsu.Go ? 1 : 0)} justJumped={(gsu.State.JustJumped ? 1 : 0)}{suffix}");
    }

    private static void TraceExecEvent(GraphicsSupportUnit gsu, MemoryType memoryType, byte opcode)
    {
        if (!TraceExec || _traceExecCount >= TraceExecLimit)
            return;

        ushort pc = gsu.R[15];
        if (!PcInTraceExecRange(pc))
            return;

        _traceExecCount++;
        Console.WriteLine(
            $"[SFX-EXEC] pc=0x{pc:X4} op=0x{opcode:X2} next={memoryType} " +
            $"r0=0x{gsu.R[0]:X4} r1=0x{gsu.R[1]:X4} r2=0x{gsu.R[2]:X4} r3=0x{gsu.R[3]:X4} " +
            $"r4=0x{gsu.R[4]:X4} r5=0x{gsu.R[5]:X4} r6=0x{gsu.R[6]:X4} r7=0x{gsu.R[7]:X4} " +
            $"r8=0x{gsu.R[8]:X4} r9=0x{gsu.R[9]:X4} r10=0x{gsu.R[10]:X4} r11=0x{gsu.R[11]:X4} " +
            $"r12=0x{gsu.R[12]:X4} r13=0x{gsu.R[13]:X4} r14=0x{gsu.R[14]:X4} pbr=0x{gsu.Pbr:X2} rombr=0x{gsu.Rombr:X2} " +
            $"cbr=0x{gsu.CodeCache.Cbr:X4} z={(gsu.ZeroFlag ? 1 : 0)} c={(gsu.CarryFlag ? 1 : 0)} s={(gsu.SignFlag ? 1 : 0)} ov={(gsu.OverflowFlag ? 1 : 0)} " +
            $"rom={gsu.RomAccess} ram={gsu.RamAccess} justJumped={(gsu.State.JustJumped ? 1 : 0)}");
    }

    private static bool PcInTraceExecRange(ushort pc)
    {
        if (TraceExecStart <= TraceExecEnd)
            return pc >= TraceExecStart && pc <= TraceExecEnd;

        return pc >= TraceExecStart || pc <= TraceExecEnd;
    }

    private static int ParseTraceLimit(string envName, int defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        return int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private static int ParseTraceHex(string envName, int defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        string normalized = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw;
        return int.TryParse(normalized, System.Globalization.NumberStyles.HexNumber, null, out int parsed)
            ? parsed
            : defaultValue;
    }
}
