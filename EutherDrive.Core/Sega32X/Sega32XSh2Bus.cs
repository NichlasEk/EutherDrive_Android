namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XSh2Bus : ISega32XSh2Bus
{
    private static readonly bool TraceBootRegisterReads =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_BOOT_LOOP"),
            "1",
            StringComparison.Ordinal);
    private readonly Sega32XScaffoldCore _core;
    private readonly Sega32XCpu _whichCpu;

    public Sega32XSh2Bus(Sega32XScaffoldCore core, Sega32XCpu whichCpu)
    {
        _core = core;
        _whichCpu = whichCpu;
    }

    public ulong CycleCounter { get; private set; }

    public bool ResetAsserted => _core.Registers.ResetSh2;
    public byte InterruptLevel => _whichCpu == Sega32XCpu.Master
        ? _core.Registers.MasterInterrupts.CurrentInterruptLevel
        : _core.Registers.SlaveInterrupts.CurrentInterruptLevel;

    private void SyncIfCommPortAccessed(uint maskedAddress)
    {
        if (maskedAddress < 0x00004020 || maskedAddress > 0x0000402F)
            return;

        if (!_core.BeginCommPortSync())
            return;

        try
        {
            Sega32XSh2Cpu otherCpu = _core.GetOtherCpu(_whichCpu);
            Sega32XSh2Bus otherBus = _core.GetOtherBus(_whichCpu);
            ulong limit = CycleCounter;
            while (otherBus.CycleCounter < limit)
            {
                otherCpu.Execute(_core.Sh2ExecutionSliceLength, otherBus);
            }
        }
        finally
        {
            _core.EndCommPortSync();
        }
    }

    public byte ReadByte(uint address, Sega32XSh2AccessContext context)
    {
        uint masked = address & 0x1FFFFFFF;
        _ = context;

        if (masked <= 0x00003FFF)
        {
            ReadOnlySpan<byte> bootRom = _whichCpu == Sega32XCpu.Master ? _core.MasterBootRom : _core.SlaveBootRom;
            return masked < bootRom.Length ? bootRom[(int)masked] : (byte)0;
        }

        if ((masked >= 0x00004000 && masked <= 0x0000402F) || (masked >= 0x00004100 && masked <= 0x0000410A))
        {
            SyncIfCommPortAccessed(masked);
            ushort word = masked >= 0x00004100
                ? _core.Bus.Vdp.ReadRegister(masked & ~1u)
                : _core.Registers.Sh2Read(masked & ~1u, _whichCpu);
            if (TraceBootRegisterReads && (masked & ~1u) == 0x00004000)
            {
                Console.WriteLine(
                    $"[S32X-SH2BUS-{_whichCpu}] read8 addr=0x{masked:X8} word=0x{word:X4} aden={(_core.Registers.AdapterEnabled ? 1 : 0)} reset={(_core.Registers.ResetSh2 ? 1 : 0)}");
            }
            return (masked & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            if ((uint)wordIndex < _core.Bus.Sdram.Length)
            {
                ushort value = _core.Bus.Sdram[wordIndex];
                return (masked & 1) == 0 ? (byte)(value >> 8) : (byte)value;
            }
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            ushort value = _core.Bus.Vdp.ReadFrameBufferWord(masked - 0x04000000);
            return (masked & 1) == 0 ? (byte)(value >> 8) : (byte)value;
        }

        if (masked >= 0x02000000 && masked < 0x02400000)
        {
            return _core.Bus.ReadSh2CartridgeByte(masked & 0x003FFFFF);
        }

        if (masked >= 0x00004030 && masked <= 0x0000403F)
        {
            int wordIndex = (int)((masked - 0x00004030) >> 1);
            if ((uint)wordIndex < _core.Bus.Sh2PwmRegisters.Length)
            {
                ushort value = _core.Bus.Sh2PwmRegisters[wordIndex];
                return (masked & 1) == 0 ? (byte)(value >> 8) : (byte)value;
            }
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            ushort value = _core.Bus.Vdp.ReadCramWord(masked - 0x00004200);
            return (masked & 1) == 0 ? (byte)(value >> 8) : (byte)value;
        }

        return 0;
    }

    public ushort ReadWord(uint address, Sega32XSh2AccessContext context)
    {
        uint masked = address & 0x1FFFFFFF;
        _ = context;

        if (masked <= 0x00003FFF)
        {
            ReadOnlySpan<byte> bootRom = _whichCpu == Sega32XCpu.Master ? _core.MasterBootRom : _core.SlaveBootRom;
            if (masked + 1 < bootRom.Length)
                return (ushort)((bootRom[(int)masked] << 8) | bootRom[(int)masked + 1]);
            return 0;
        }

        if ((masked >= 0x00004000 && masked <= 0x0000402F) || (masked >= 0x00004100 && masked <= 0x0000410A))
        {
            SyncIfCommPortAccessed(masked);
            ushort word = masked >= 0x00004100
                ? _core.Bus.Vdp.ReadRegister(masked & ~1u)
                : _core.Registers.Sh2Read(masked & ~1u, _whichCpu);
            if (TraceBootRegisterReads && (masked & ~1u) == 0x00004000)
            {
                Console.WriteLine(
                    $"[S32X-SH2BUS-{_whichCpu}] read16 addr=0x{masked:X8} word=0x{word:X4} aden={(_core.Registers.AdapterEnabled ? 1 : 0)} reset={(_core.Registers.ResetSh2 ? 1 : 0)}");
            }
            return word;
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            if ((uint)wordIndex < _core.Bus.Sdram.Length)
                return _core.Bus.Sdram[wordIndex];
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
            return _core.Bus.Vdp.ReadFrameBufferWord(masked - 0x04000000);

        if (masked >= 0x02000000 && masked < 0x02400000)
            return _core.Bus.ReadSh2CartridgeWord(masked & 0x003FFFFE);

        if (masked >= 0x00004030 && masked <= 0x0000403F)
        {
            int wordIndex = (int)((masked - 0x00004030) >> 1);
            if ((uint)wordIndex < _core.Bus.Sh2PwmRegisters.Length)
                return _core.Bus.Sh2PwmRegisters[wordIndex];
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
            return _core.Bus.Vdp.ReadCramWord(masked - 0x00004200);

        return 0;
    }

    public uint ReadLongword(uint address, Sega32XSh2AccessContext context)
    {
        ushort high = ReadWord(address & ~1u, context);
        ushort low = ReadWord((address & ~1u) + 2, context);
        return ((uint)high << 16) | low;
    }

    public void WriteByte(uint address, byte value, Sega32XSh2AccessContext context)
    {
        uint masked = address & 0x1FFFFFFF;
        _ = context;

        if ((masked >= 0x00004000 && masked <= 0x0000402F) || (masked >= 0x00004100 && masked <= 0x0000410A))
        {
            SyncIfCommPortAccessed(masked);
            ushort word = masked >= 0x00004100
                ? _core.Bus.Vdp.ReadRegister(masked & ~1u)
                : _core.Registers.Sh2Read(masked & ~1u, _whichCpu);
            word = (masked & 1) == 0
                ? (ushort)((word & 0x00FF) | (value << 8))
                : (ushort)((word & 0xFF00) | value);
            if (masked >= 0x00004100)
                _core.Bus.Vdp.WriteRegister(masked & ~1u, word);
            else
                _core.Registers.Sh2Write(masked & ~1u, word, _whichCpu);
            return;
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            if ((uint)wordIndex < _core.Bus.Sdram.Length)
            {
                ushort current = _core.Bus.Sdram[wordIndex];
                _core.Bus.Sdram[wordIndex] = (masked & 1) == 0
                    ? (ushort)((current & 0x00FF) | (value << 8))
                    : (ushort)((current & 0xFF00) | value);
            }
            return;
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            ushort current = _core.Bus.Vdp.ReadFrameBufferWord(masked - 0x04000000);
            ushort merged = (masked & 1) == 0
                ? (ushort)((current & 0x00FF) | (value << 8))
                : (ushort)((current & 0xFF00) | value);
            _core.Bus.Vdp.WriteFrameBufferWord(masked - 0x04000000, merged);
            return;
        }

        if (masked >= 0x02000000 && masked < 0x02400000)
        {
            _core.Bus.WriteSh2CartridgeByte(masked & 0x003FFFFF, value);
            return;
        }

        if (masked >= 0x00004030 && masked <= 0x0000403F)
        {
            int wordIndex = (int)((masked - 0x00004030) >> 1);
            if ((uint)wordIndex < _core.Bus.Sh2PwmRegisters.Length)
            {
                ushort current = _core.Bus.Sh2PwmRegisters[wordIndex];
                _core.Bus.Sh2PwmRegisters[wordIndex] = (masked & 1) == 0
                    ? (ushort)((current & 0x00FF) | (value << 8))
                    : (ushort)((current & 0xFF00) | value);
            }
            return;
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            ushort current = _core.Bus.Vdp.ReadCramWord(masked - 0x00004200);
            ushort merged = (masked & 1) == 0
                ? (ushort)((current & 0x00FF) | (value << 8))
                : (ushort)((current & 0xFF00) | value);
            _core.Bus.Vdp.WriteCramWord(masked - 0x00004200, merged);
        }
    }

    public void WriteWord(uint address, ushort value, Sega32XSh2AccessContext context)
    {
        uint masked = address & 0x1FFFFFFF;
        _ = context;

        if ((masked >= 0x00004000 && masked <= 0x0000402F) || (masked >= 0x00004100 && masked <= 0x0000410A))
        {
            SyncIfCommPortAccessed(masked);
            if (masked >= 0x00004100)
                _core.Bus.Vdp.WriteRegister(masked & ~1u, value);
            else
                _core.Registers.Sh2Write(masked & ~1u, value, _whichCpu);
            return;
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            if ((uint)wordIndex < _core.Bus.Sdram.Length)
                _core.Bus.Sdram[wordIndex] = value;
            return;
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            _core.Bus.Vdp.WriteFrameBufferWord(masked - 0x04000000, value);
            return;
        }

        if (masked >= 0x02000000 && masked < 0x02400000)
        {
            _core.Bus.WriteSh2CartridgeWord(masked & 0x003FFFFE, value);
            return;
        }

        if (masked >= 0x00004030 && masked <= 0x0000403F)
        {
            int wordIndex = (int)((masked - 0x00004030) >> 1);
            if ((uint)wordIndex < _core.Bus.Sh2PwmRegisters.Length)
                _core.Bus.Sh2PwmRegisters[wordIndex] = value;
            return;
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            _core.Bus.Vdp.WriteCramWord(masked - 0x00004200, value);
        }
    }

    public void WriteLongword(uint address, uint value, Sega32XSh2AccessContext context)
    {
        WriteWord(address, (ushort)(value >> 16), context);
        WriteWord(address + 2, (ushort)value, context);
    }

    public void IncrementCycleCounter(ulong cycles)
    {
        CycleCounter += cycles;
    }
}
