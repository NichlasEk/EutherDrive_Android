using EutherDrive.Core.Savestates;

namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XSh2Bus : ISega32XSh2Bus
{
    private enum DmaAddressMode : uint
    {
        Fixed = 0,
        AutoIncrement = 1,
        AutoDecrement = 2,
        Invalid = 3,
    }

    private enum DmaTransferUnit : uint
    {
        Byte = 0,
        Word = 1,
        Longword = 2,
        SixteenByte = 3,
    }

    private const int CacheDataArrayLengthWords = 4 * 1024 / 2;
    private const ulong NativeSh2CyclesPerM68kCycle = 3;
    private const ulong Sh2CartridgeCycles = 7;
    private const ulong Sh2FrameBufferReadCycles = 4;
    private const ulong Sh2VdpCycles = 4;
    private const ulong Sh2SdramReadCycles = 9;
    private const ulong Sh2SdramWriteCycles = 0;
    private static readonly ulong CommPortSyncChunkSize = ParseCommPortSyncChunkSize();
    private static readonly int StableRegisterPollThreshold = ParseStableCommPollThreshold();
    private static readonly uint? TracePcWatchStart = ParseOptionalHex("EUTHERDRIVE_S32X_TRACE_SH2_PCWATCH_START");
    private static readonly uint? TracePcWatchEnd = ParseOptionalHex("EUTHERDRIVE_S32X_TRACE_SH2_PCWATCH_END");
    private static readonly uint? TraceAddressWatchStart = ParseOptionalHex("EUTHERDRIVE_S32X_TRACE_SH2_ADDRWATCH_START");
    private static readonly uint? TraceAddressWatchEnd = ParseOptionalHex("EUTHERDRIVE_S32X_TRACE_SH2_ADDRWATCH_END");
    private static readonly bool TraceCacheControl =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_SH2_CACHE_CTRL"),
            "1",
            StringComparison.Ordinal);
    private const int CacheEntries = 64;
    private static readonly bool TraceBootRegisterReads =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_BOOT_LOOP"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceDmaRegisters =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_SH2_DMA_REGS"),
            "1",
            StringComparison.Ordinal);
    [NonSerialized] private readonly Sega32XScaffoldCore _core;
    [NonSerialized] private readonly Sega32XCpu _whichCpu;
    private readonly ushort[] _cacheDataArray = new ushort[CacheDataArrayLengthWords];
    private readonly uint[] _cacheAddressTags = new uint[CacheEntries * 4];
    private readonly ulong[] _cacheAddressValidBits = new ulong[4];
    private readonly byte[] _cacheAddressLruBits = new byte[CacheEntries];
    private readonly Sega32XSh2SerialInterface _serial = new();
    private readonly Sega32XSh2WatchdogTimer _watchdog = new();
    private readonly Sega32XSh2FreeRunTimer _freeRunTimer = new();
    private byte _cacheControl;
    private ushort _ipra;
    private ushort _iprb;
    private ushort _vcra;
    private ushort _vcrb;
    private ushort _vcrwdt;
    private uint _divuControl;
    private uint _divuDividendHigh;
    private uint _divuDividendLow;
    private uint _divuDivisor;
    private uint _breakAddressA;
    private uint _breakAddressB;
    private uint _dmaRegister;
    private readonly uint[] _dmaSourceAddress = new uint[2];
    private readonly uint[] _dmaDestinationAddress = new uint[2];
    private readonly uint[] _dmaTransferCount = new uint[2];
    private readonly ushort[] _dmaChannelControl = new ushort[2];
    private ushort _dmaOperation;
    private uint _dmaVector0;
    private uint _dmaVector1;
    [NonSerialized] private ulong _schedulerCycleCounter;
    [NonSerialized] private uint _lastStablePollPc;
    [NonSerialized] private uint _lastStablePollAddress;
    [NonSerialized] private ushort _lastStablePollValue;
    [NonSerialized] private int _stablePollCount;

    public Sega32XSh2Bus(Sega32XScaffoldCore core, Sega32XCpu whichCpu)
    {
        _core = core;
        _whichCpu = whichCpu;
    }

    public ulong CycleCounter { get; private set; }
    public ulong SchedulerCycleCounter => _schedulerCycleCounter;
    public ulong CycleLimit { get; set; } = ulong.MaxValue;
    public bool ShouldStopExecution => _schedulerCycleCounter >= CycleLimit;
    public ulong M68kReferenceCyclesDone => (_schedulerCycleCounter + (NativeSh2CyclesPerM68kCycle - 1)) / NativeSh2CyclesPerM68kCycle;

    public bool ResetAsserted => _core.Registers.ResetSh2;
    public byte InterruptLevel => _whichCpu == Sega32XCpu.Master
        ? _core.Registers.MasterInterrupts.CurrentInterruptLevel
        : _core.Registers.SlaveInterrupts.CurrentInterruptLevel;
    public byte InternalInterruptLevel => GetInternalInterrupt().Level;
    public byte InternalInterruptVectorNumber => GetInternalInterrupt().VectorNumber;

    public void SaveState(BinaryWriter writer) => StateBinarySerializer.WriteInto(writer, this);

    public void LoadState(BinaryReader reader)
    {
        StateBinarySerializer.ReadInto(reader, this);
        _schedulerCycleCounter = CurrentCpu.CycleCounter;
        ResetStablePollTracking();
    }

    public void ResetTimingState()
    {
        CycleCounter = 0;
        _schedulerCycleCounter = 0;
        CycleLimit = ulong.MaxValue;
        ResetStablePollTracking();
    }

    public void ResetState()
    {
        Array.Clear(_cacheDataArray, 0, _cacheDataArray.Length);
        Array.Clear(_cacheAddressTags, 0, _cacheAddressTags.Length);
        Array.Clear(_cacheAddressValidBits, 0, _cacheAddressValidBits.Length);
        Array.Clear(_cacheAddressLruBits, 0, _cacheAddressLruBits.Length);
        Array.Clear(_dmaSourceAddress, 0, _dmaSourceAddress.Length);
        Array.Clear(_dmaDestinationAddress, 0, _dmaDestinationAddress.Length);
        Array.Clear(_dmaTransferCount, 0, _dmaTransferCount.Length);
        Array.Clear(_dmaChannelControl, 0, _dmaChannelControl.Length);
        _serial.Reset();
        _watchdog.Reset();
        _freeRunTimer.Reset();
        _cacheControl = 0;
        _ipra = 0;
        _iprb = 0;
        _vcra = 0;
        _vcrb = 0;
        _vcrwdt = 0;
        _divuControl = 0;
        _divuDividendHigh = 0;
        _divuDividendLow = 0;
        _divuDivisor = 0;
        _breakAddressA = 0;
        _breakAddressB = 0;
        _dmaRegister = 0;
        _dmaOperation = 0;
        _dmaVector0 = 0;
        _dmaVector1 = 0;
        ResetTimingState();
    }

    public void TickPeripherals(ulong cycles)
    {
        if (cycles == 0)
            return;

        _watchdog.Tick(cycles);
        _serial.Tick(cycles, value => _core.GetOtherBus(_whichCpu).QueueSerialReceive(value));
    }

    public bool TryTickDma()
    {
        if ((_dmaOperation & 0x0001) == 0 || (_dmaOperation & 0x0004) != 0)
            return false;

        for (int channel = 0; channel < 2; channel++)
        {
            ushort control = _dmaChannelControl[channel];
            if ((control & 0x0001) == 0 || (control & 0x0002) != 0)
                continue;

            bool autoRequest = (control & 0x0200) != 0;
            if (!autoRequest)
            {
                bool dmaRequest = channel switch
                {
                    0 => !_core.Registers.Dma.Fifo.Sh2IsEmpty,
                    1 => _core.Bus.Pwm.Dreq1,
                    _ => false,
                };

                if (!dmaRequest)
                    continue;
                    
                if (channel == 1)
                    _core.Bus.Pwm.AcknowledgeDreq1();
            }

            TickDmaChannel(channel);
            return true;
        }

        return false;
    }

    private void SyncIfCommPortAccessed(uint maskedAddress, bool isRead)
    {
        if (maskedAddress < 0x00004020 || maskedAddress > 0x0000402F)
            return;

        if (!_core.BeginCommPortSync())
            return;

        try
        {
            Sega32XSh2Cpu otherCpu = _core.GetOtherCpu(_whichCpu);
            Sega32XSh2Bus otherBus = _core.GetOtherBus(_whichCpu);
            
            // För läsningar från comm-portar: Synka den andra CPUn hela vägen till CycleLimit.
            // Detta är kritiskt för boot-sekvenser där en CPU väntar på att den andra ska skriva
            // till kommunikationsportarna. Genom att synka hela vägen till CycleLimit säkerställer
            // vi att den andra CPUn har fått chansen att köra och eventuellt skriva sina värden.
            // För skrivningar: Använd gamla beteendet (synka bara till nuvarande cykel).
            ulong limit = isRead ? CycleLimit : Math.Min(CycleLimit, _schedulerCycleCounter);
            
            if (otherBus.SchedulerCycleCounter >= limit)
                return;

            // Växla temporärt andra CPU:ns CycleLimit för att tillåta den att köra fram till vår limit
            ulong originalOtherLimit = otherBus.CycleLimit;
            otherBus.CycleLimit = limit;
            
            try
            {
                while (otherBus.SchedulerCycleCounter < limit)
                {
                    ulong toRun = Math.Min(CommPortSyncChunkSize, limit - otherBus.SchedulerCycleCounter);
                    otherCpu.Execute(toRun, otherBus);
                }
            }
            finally
            {
                otherBus.CycleLimit = originalOtherLimit;
            }
        }
        finally
        {
            _core.EndCommPortSync();
        }
    }

    private static ulong ParseCommPortSyncChunkSize()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_COMM_SYNC_CHUNK");
        if (ulong.TryParse(raw, out ulong parsed) && parsed > 0)
            return parsed;

        // jgenesis uses 50-cycle catch-up chunks for communication port accesses.
        // Matching that keeps the close mailbox timing that games like Brutal need
        // while avoiding the 10-cycle over-sync overhead that shows up in Chaotix.
        return 50;
    }

    private static int ParseStableCommPollThreshold()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_STABLE_COMM_POLL_THRESHOLD");
        if (int.TryParse(raw, out int parsed) && parsed > 0)
            return parsed;

        return 32;
    }

    private static uint? ParseOptionalHex(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];

        return uint.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out uint parsed)
            ? parsed
            : null;
    }

    private Sega32XSh2Cpu CurrentCpu => _whichCpu == Sega32XCpu.Master ? _core.MasterSh2 : _core.SlaveSh2;

    private byte SciPriority => (byte)(_iprb >> 12);
    private byte WdtPriority => (byte)((_ipra >> 4) & 0x0F);
    private byte SciRxOkVector => (byte)(_vcra & 0x7F);
    private byte WdtVector => (byte)((_vcrwdt >> 8) & 0x7F);

    private (byte Level, byte VectorNumber) GetInternalInterrupt()
    {
        byte level = 0;
        byte vectorNumber = 0;

        if (_serial.RxInterruptPending)
        {
            level = SciPriority;
            vectorNumber = SciRxOkVector;
        }

        if (_watchdog.IntervalOverflowPending && WdtPriority > level)
        {
            level = WdtPriority;
            vectorNumber = WdtVector;
        }

        return (level, vectorNumber);
    }

    private void QueueSerialReceive(byte value)
    {
        _serial.QueueReceive(value);
    }

    private bool ShouldTracePcWatch(Sega32XSh2AccessContext context)
    {
        if (!TracePcWatchStart.HasValue || !TracePcWatchEnd.HasValue)
            return false;

        uint pc = CurrentCpu.CurrentInstructionPc;
        return pc >= TracePcWatchStart.Value && pc <= TracePcWatchEnd.Value;
    }

    private void TracePcWatch(string op, uint address, uint value, Sega32XSh2AccessContext context)
    {
        if (!ShouldTracePcWatch(context))
            return;

        Console.WriteLine(
            $"[S32X-PCWATCH-{_whichCpu}] pc=0x{CurrentCpu.CurrentInstructionPc:X8} op={op} " +
            $"addr=0x{address:X8} value=0x{value:X8} ctx={context} cyc={CycleCounter}");
    }

    private void TraceCacheControlWrite(string width, uint address, uint value)
    {
        if (!TraceCacheControl || address is not 0xFFFFFE92 and not 0xFFFFFE93)
            return;

        Console.WriteLine(
            $"[S32X-CACHECTRL-{_whichCpu}] width={width} addr=0x{address:X8} value=0x{value:X8} " +
            $"pc=0x{CurrentCpu.CurrentInstructionPc:X8} cyc={CycleCounter}");
    }

    private void TraceAddressWatch(string op, uint address, uint value, Sega32XSh2AccessContext context)
    {
        if (!TraceAddressWatchStart.HasValue || !TraceAddressWatchEnd.HasValue)
            return;
        if (address < TraceAddressWatchStart.Value || address > TraceAddressWatchEnd.Value)
            return;

        Console.WriteLine(
            $"[S32X-ADDRWATCH-{_whichCpu}] pc=0x{CurrentCpu.CurrentInstructionPc:X8} op={op} " +
            $"addr=0x{address:X8} value=0x{value:X8} ctx={context} cyc={CycleCounter}");
    }

    private void MaybeShortCircuitStableRegisterPoll(uint maskedAddress, ushort value, Sega32XSh2AccessContext context)
    {
        if (context != Sega32XSh2AccessContext.Data)
            return;

        if (StableRegisterPollThreshold <= 0 || CycleLimit == ulong.MaxValue || _schedulerCycleCounter >= CycleLimit)
            return;

        uint pc = CurrentCpu.CurrentInstructionPc;
        if (pc < 0x06000000 || pc >= 0x06040000)
        {
            ResetStablePollTracking();
            return;
        }

        if (!IsStablePollCandidate(maskedAddress, value))
        {
            ResetStablePollTracking();
            return;
        }

        if (pc == _lastStablePollPc && maskedAddress == _lastStablePollAddress && value == _lastStablePollValue)
            _stablePollCount++;
        else
            _stablePollCount = 1;

        _lastStablePollPc = pc;
        _lastStablePollAddress = maskedAddress;
        _lastStablePollValue = value;

        if (_stablePollCount < StableRegisterPollThreshold)
            return;

        if (_core.UseExperimentalCommPollModel && IsCommPortRegister(maskedAddress))
        {
            CurrentCpu.EnterCommPoll(maskedAddress, value);
            _schedulerCycleCounter = CycleLimit;
            return;
        }

        SkipToCycleLimit();
    }

    private void SkipToCycleLimit()
    {
        if (_schedulerCycleCounter >= CycleLimit)
            return;

        ulong skippedCycles = CycleLimit - _schedulerCycleCounter;
        _schedulerCycleCounter = CycleLimit;
        CycleCounter += skippedCycles;
    }

    private void ResetStablePollTracking()
    {
        _lastStablePollPc = 0;
        _lastStablePollAddress = 0;
        _lastStablePollValue = 0;
        _stablePollCount = 0;
    }

    private static bool IsStablePollCandidate(uint maskedAddress, ushort value)
    {
        if (IsCommPortRegister(maskedAddress))
            return true;

        return (maskedAddress & ~1u) == 0x0000410A && (value & 0x0002) != 0;
    }

    public byte ReadByte(uint address, Sega32XSh2AccessContext context)
    {
        uint addressSpace = address >> 29;
        switch (address >> 29)
        {
            case 2:
                CycleCounter += 1;
                AssociativePurge(address);
                return 0;
            case 3:
                CycleCounter += 1;
                return 0;
            case 4:
            case 5:
                CycleCounter += 1;
                return 0;
            case 6:
                CycleCounter += 1;
                return ReadCacheDataArrayByte(address);
            case 7:
                CycleCounter += 1;
                return ReadInternalRegisterByte(address);
        }

        if (addressSpace == 0 && TryReadCachedByte(address, out byte cachedByte))
        {
            CycleCounter += 1;
            return cachedByte;
        }

        uint masked = address & 0x1FFFFFFF;
        byte value = ReadBackingByte(masked, context);
        if (addressSpace == 0)
            MaybeReplaceCache(address, context);
        TracePcWatch("read8", masked, value, context);
        TraceAddressWatch("read8", masked, value, context);
        return value;
    }

    public ushort ReadWord(uint address, Sega32XSh2AccessContext context)
    {
        uint addressSpace = address >> 29;
        switch (address >> 29)
        {
            case 2:
                CycleCounter += 1;
                AssociativePurge(address);
                return 0;
            case 3:
                CycleCounter += 1;
                return 0;
            case 4:
            case 5:
                CycleCounter += 1;
                return 0;
            case 6:
                CycleCounter += 1;
                return ReadCacheDataArrayWord(address);
            case 7:
                CycleCounter += 1;
                return ReadInternalRegisterWord(address);
        }

        if (addressSpace == 0 && TryReadCachedWord(address, out ushort cachedWord))
        {
            CycleCounter += 1;
            return cachedWord;
        }

        uint masked = address & 0x1FFFFFFF;
        ushort value = ReadBackingWord(masked, context);
        if (addressSpace == 0)
            MaybeReplaceCache(address, context);
        TracePcWatch("read16", masked, value, context);
        TraceAddressWatch("read16", masked, value, context);
        return value;
    }

    public uint ReadLongword(uint address, Sega32XSh2AccessContext context)
    {
        uint addressSpace = address >> 29;
        switch (address >> 29)
        {
            case 2:
                CycleCounter += 1;
                AssociativePurge(address);
                return 0;
            case 3:
                CycleCounter += 1;
                return ReadAddressArrayLongword(address);
            case 4:
            case 5:
                CycleCounter += 1;
                return 0;
            case 6:
                CycleCounter += 1;
                return ReadCacheDataArrayLongword(address);
            case 7:
                CycleCounter += 1;
                return ReadInternalRegisterLongword(address);
        }

        if (addressSpace == 0 && TryReadCachedLongword(address, out uint cachedLong))
        {
            CycleCounter += 1;
            return cachedLong;
        }

        uint masked = address & 0x1FFFFFFF;
        uint value = ReadBackingLongword(masked, context);
        if (addressSpace == 0)
            MaybeReplaceCache(address, context);
        TracePcWatch("read32", masked, value, context);
        TraceAddressWatch("read32", masked, value, context);
        return value;
    }

    public void WriteByte(uint address, byte value, Sega32XSh2AccessContext context)
    {
        uint addressSpace = address >> 29;
        uint masked = address & 0x1FFFFFFF;
        switch (address >> 29)
        {
            case 2:
                CycleCounter += 1;
                AssociativePurge(address);
                return;
            case 3:
                CycleCounter += 1;
                return;
            case 4:
            case 5:
                CycleCounter += 1;
                return;
            case 6:
                CycleCounter += 1;
                WriteCacheDataArrayByte(address, value);
                return;
            case 7:
                CycleCounter += 1;
                WriteInternalRegisterByte(address, value);
                return;
        }

        if (addressSpace == 0)
            WriteThroughCacheByte(address, value);

        if (IsSh2SystemRegister(masked) || IsSh2VdpRegister(masked))
        {
            CycleCounter += 1;
            SyncIfCommPortAccessed(masked, isRead: true);
            ushort word = IsSh2VdpRegister(masked)
                ? _core.Bus.Vdp.ReadRegister(masked & ~1u)
                : _core.Registers.Sh2Read(masked & ~1u, _whichCpu, _core.Bus.Vdp);
            word = (masked & 1) == 0
                ? (ushort)((word & 0x00FF) | (value << 8))
                : (ushort)((word & 0xFF00) | value);
            if (IsSh2VdpRegister(masked))
            {
                CycleCounter += Sh2VdpCycles;
                if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                    return;
                _core.Bus.Vdp.WriteRegister(masked & ~1u, word);
            }
            else
                _core.Registers.Sh2Write(masked & ~1u, word, _whichCpu, _core.Bus.Vdp);
            return;
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            CycleCounter += 1 + Sh2SdramWriteCycles;
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            if ((uint)wordIndex < _core.Bus.Sdram.Length)
            {
                ushort current = _core.Bus.Sdram[wordIndex];
                ushort next = (masked & 1) == 0
                    ? (ushort)((current & 0x00FF) | (value << 8))
                    : (ushort)((current & 0xFF00) | value);
                _core.Bus.Sdram[wordIndex] = next;
                TracePcWatch("write8", masked, value, context);
                TraceAddressWatch("write8", masked, value, context);
            }
            return;
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            CycleCounter += _core.Bus.Vdp.FrameBufferWriteLatency(CycleCounter);
            uint frameBufferAddress = masked - 0x04000000;
            _core.Bus.Vdp.WriteFrameBufferByte(frameBufferAddress, value, IsFrameBufferOverwrite(masked));
            TracePcWatch("write8", masked, value, context);
            TraceAddressWatch("write8", masked, value, context);
            return;
        }

        if (masked >= 0x02000000 && masked < 0x02400000)
        {
            CycleCounter += 1 + Sh2CartridgeCycles;
            _core.Bus.WriteSh2CartridgeByte(masked & 0x003FFFFF, value);
            return;
        }

        if (masked >= 0x00004030 && masked <= 0x0000403F)
        {
            CycleCounter += 1;
            ushort current = _core.Bus.Pwm.ReadRegister(masked & ~1u);
            ushort merged = (masked & 1) == 0
                ? (ushort)((current & 0x00FF) | (value << 8))
                : (ushort)((current & 0xFF00) | value);
            _core.Bus.Pwm.Sh2WriteRegister(masked & ~1u, merged);
            return;
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            CycleCounter += 1 + Sh2VdpCycles;
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            ushort current = _core.Bus.Vdp.ReadCramWord(masked - 0x00004200);
            ushort merged = (masked & 1) == 0
                ? (ushort)((current & 0x00FF) | (value << 8))
                : (ushort)((current & 0xFF00) | value);
            _core.Bus.Vdp.WriteCramWord(masked - 0x00004200, merged);
        }

        TracePcWatch("write8", masked, value, context);
        TraceAddressWatch("write8", masked, value, context);
    }

    public void WriteWord(uint address, ushort value, Sega32XSh2AccessContext context)
    {
        uint addressSpace = address >> 29;
        switch (address >> 29)
        {
            case 2:
                CycleCounter += 1;
                AssociativePurge(address);
                return;
            case 3:
                CycleCounter += 1;
                return;
            case 4:
            case 5:
                CycleCounter += 1;
                return;
            case 6:
                CycleCounter += 1;
                WriteCacheDataArrayWord(address, value);
                return;
            case 7:
                CycleCounter += 1;
                WriteInternalRegisterWord(address, value);
                return;
        }

        uint masked = address & 0x1FFFFFFF;
        if (addressSpace == 0)
            WriteThroughCacheWord(address, value);

        if (IsSh2SystemRegister(masked) || IsSh2VdpRegister(masked))
        {
            CycleCounter += 1;
            SyncIfCommPortAccessed(masked, isRead: true);
            if (IsSh2VdpRegister(masked))
            {
                CycleCounter += Sh2VdpCycles;
                if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                    return;
                _core.Bus.Vdp.WriteRegister(masked & ~1u, value);
            }
            else
                _core.Registers.Sh2Write(masked & ~1u, value, _whichCpu, _core.Bus.Vdp);
            return;
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            CycleCounter += 1 + Sh2SdramWriteCycles;
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            if ((uint)wordIndex < _core.Bus.Sdram.Length)
            {
                _core.Bus.Sdram[wordIndex] = value;
                TracePcWatch("write16", masked, value, context);
                TraceAddressWatch("write16", masked, value, context);
            }
            return;
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            CycleCounter += _core.Bus.Vdp.FrameBufferWriteLatency(CycleCounter);
            uint frameBufferAddress = masked - 0x04000000;
            if (IsFrameBufferOverwrite(masked))
                _core.Bus.Vdp.OverwriteFrameBufferWord(frameBufferAddress, value);
            else
                _core.Bus.Vdp.WriteFrameBufferWord(frameBufferAddress, value);
            TracePcWatch("write16", masked, value, context);
            TraceAddressWatch("write16", masked, value, context);
            return;
        }

        if (masked >= 0x02000000 && masked < 0x02400000)
        {
            CycleCounter += 1 + Sh2CartridgeCycles;
            _core.Bus.WriteSh2CartridgeWord(masked & 0x003FFFFE, value);
            return;
        }

        if (masked >= 0x00004030 && masked <= 0x0000403F)
        {
            CycleCounter += 1;
            _core.Bus.Pwm.Sh2WriteRegister(masked, value);
            return;
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            CycleCounter += 1 + Sh2VdpCycles;
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            _core.Bus.Vdp.WriteCramWord(masked - 0x00004200, value);
        }

        TracePcWatch("write16", masked, value, context);
        TraceAddressWatch("write16", masked, value, context);
    }

    public void WriteLongword(uint address, uint value, Sega32XSh2AccessContext context)
    {
        uint addressSpace = address >> 29;
        switch (address >> 29)
        {
            case 2:
                CycleCounter += 1;
                AssociativePurge(address);
                return;
            case 3:
                CycleCounter += 1;
                WriteAddressArrayLongword(address, value);
                return;
            case 4:
            case 5:
                CycleCounter += 1;
                return;
            case 6:
                CycleCounter += 1;
                WriteCacheDataArrayLongword(address, value);
                return;
            case 7:
                CycleCounter += 1;
                WriteInternalRegisterLongword(address, value);
                return;
        }

        if (addressSpace == 0)
            WriteThroughCacheLongword(address, value);

        if (addressSpace == 0 || addressSpace == 1)
        {
            uint masked = address & 0x1FFFFFFF;
            WriteBackingLongword(masked, value, context);
            TracePcWatch("write32", masked, value, context);
            TraceAddressWatch("write32", masked, value, context);
            return;
        }
        WriteWord(address, (ushort)(value >> 16), context);
        WriteWord(address + 2, (ushort)value, context);
    }

    public void IncrementCycleCounter(ulong cycles)
    {
        CycleCounter += cycles;
        _schedulerCycleCounter += cycles;
    }

    public void IncrementDetailCycleCounter(ulong cycles)
    {
        CycleCounter += cycles;
    }

    public bool TryPeekInstructionWord(uint address, out ushort value)
    {
        uint addressSpace = address >> 29;
        uint masked = address & 0x1FFFFFFF;

        if (addressSpace is not 0 and not 1)
        {
            value = 0;
            return false;
        }

        if (masked <= 0x00003FFF)
        {
            ReadOnlySpan<byte> bootRom = _whichCpu == Sega32XCpu.Master ? _core.MasterBootRom : _core.SlaveBootRom;
            if (masked + 1 >= bootRom.Length)
            {
                value = 0;
                return false;
            }

            value = (ushort)((bootRom[(int)masked] << 8) | bootRom[(int)masked + 1]);
            return true;
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            if ((uint)wordIndex >= _core.Bus.Sdram.Length)
            {
                value = 0;
                return false;
            }

            value = _core.Bus.Sdram[wordIndex];
            return true;
        }

        if (masked >= 0x02000000 && masked < 0x02400000)
        {
            value = _core.Bus.ReadSh2CartridgeWord(masked & 0x003FFFFE);
            return true;
        }

        value = 0;
        return false;
    }

    public ushort DebugReadCacheDataArrayWord(uint address) => ReadCacheDataArrayWord(address);

    private byte ReadCacheDataArrayByte(uint address)
    {
        ushort word = _cacheDataArray[((int)(address >> 1)) & (CacheDataArrayLengthWords - 1)];
        return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
    }

    private ushort ReadCacheDataArrayWord(uint address)
    {
        return _cacheDataArray[((int)(address >> 1)) & (CacheDataArrayLengthWords - 1)];
    }

    private uint ReadCacheDataArrayLongword(uint address)
    {
        int wordAddress = (((int)(address >> 1)) & (CacheDataArrayLengthWords - 1)) & ~1;
        return ((uint)_cacheDataArray[wordAddress] << 16) | _cacheDataArray[wordAddress + 1];
    }

    private void WriteCacheDataArrayByte(uint address, byte value)
    {
        int wordAddress = ((int)(address >> 1)) & (CacheDataArrayLengthWords - 1);
        ushort word = _cacheDataArray[wordAddress];
        _cacheDataArray[wordAddress] = (address & 1) == 0
            ? (ushort)((word & 0x00FF) | (value << 8))
            : (ushort)((word & 0xFF00) | value);
    }

    private void WriteCacheDataArrayWord(uint address, ushort value)
    {
        _cacheDataArray[((int)(address >> 1)) & (CacheDataArrayLengthWords - 1)] = value;
    }

    private void WriteCacheDataArrayLongword(uint address, uint value)
    {
        int wordAddress = (((int)(address >> 1)) & (CacheDataArrayLengthWords - 1)) & ~1;
        _cacheDataArray[wordAddress] = (ushort)(value >> 16);
        _cacheDataArray[wordAddress + 1] = (ushort)value;
    }

    private byte ReadInternalRegisterByte(uint address)
    {
        if (TryReadDmaByte(address, out byte dmaByte))
            return dmaByte;

        byte value = address switch
        {
            0xFFFFFC17 => 0,
            >= 0xFFFFFE00 and <= 0xFFFFFE05 => _serial.ReadRegister(address),
            >= 0xFFFFFE10 and <= 0xFFFFFE19 => _freeRunTimer.ReadRegister(address, CycleCounter),
            0xFFFFFE60 => (byte)(_iprb >> 8),
            0xFFFFFE61 => (byte)_iprb,
            0xFFFFFE62 => (byte)(_vcra >> 8),
            0xFFFFFE63 => (byte)_vcra,
            0xFFFFFE64 => (byte)(_vcrb >> 8),
            0xFFFFFE65 => (byte)_vcrb,
            0xFFFFFE80 => _watchdog.ReadControl(),
            0xFFFFFE81 => _watchdog.Counter,
            0xFFFFFE92 => _cacheControl,
            0xFFFFFE93 or >= 0xFFFFFE94 and <= 0xFFFFFE9F => 0,
            0xFFFFFEE2 => (byte)(_ipra >> 8),
            0xFFFFFEE3 => (byte)_ipra,
            0xFFFFFEE4 => (byte)(_vcrwdt >> 8),
            0xFFFFFEE5 => (byte)_vcrwdt,
            _ => 0,
        };

        MaybeTraceDmaRegisterAccess("READ8", address, value);
        return value;
    }

    private ushort ReadInternalRegisterWord(uint address)
    {
        if (TryReadDmaWord(address, out ushort dmaWord))
            return dmaWord;

        ushort value = address switch
        {
            0xFFFFFE60 => _iprb,
            0xFFFFFE62 => _vcra,
            0xFFFFFE64 => _vcrb,
            0xFFFFFE92 => _cacheControl,
            0xFFFFFEE2 => _ipra,
            0xFFFFFEE4 => _vcrwdt,
            0xFFFFFF08 => (ushort)_divuControl,
            0xFFFFFF40 => (ushort)(_breakAddressA >> 16),
            0xFFFFFF42 => (ushort)_breakAddressA,
            0xFFFFFF60 => (ushort)(_breakAddressB >> 16),
            0xFFFFFF62 => (ushort)_breakAddressB,
            _ => 0,
        };

        MaybeTraceDmaRegisterAccess("READ16", address, value);
        return value;
    }

    private uint ReadInternalRegisterLongword(uint address)
    {
        if (TryReadDmaLongword(address, out uint dmaValue))
            return dmaValue;

        uint value = address switch
        {
            >= 0xFFFFFF00 and <= 0xFFFFFF03 => _divuDividendHigh,
            >= 0xFFFFFF04 and <= 0xFFFFFF07 => _divuDividendLow,
            >= 0xFFFFFF08 and <= 0xFFFFFF0B => _divuControl,
            >= 0xFFFFFF10 and <= 0xFFFFFF13 => _divuDivisor,
            0xFFFFFF40 => _breakAddressA,
            0xFFFFFF60 => _breakAddressB,
            >= 0xFFFFFF80 and <= 0xFFFFFF9F or 0xFFFFFFB0 => _dmaRegister,
            0xFFFFFFA0 => _dmaVector0,
            0xFFFFFFA8 => _dmaVector1,
            0xFFFFFFE0 => 0xA55A0001,
            _ => 0,
        };

        MaybeTraceDmaRegisterAccess("READ32", address, value);
        return value;
    }

    private void WriteInternalRegisterByte(uint address, byte value)
    {
        if (TryWriteDmaByte(address, value))
            return;

        MaybeTraceDmaRegisterAccess("WRITE8", address, value);

        switch (address)
        {
            case >= 0xFFFFFE00 and <= 0xFFFFFE05:
                _serial.WriteRegister(address, value);
                break;
            case >= 0xFFFFFE10 and <= 0xFFFFFE19:
                _freeRunTimer.WriteRegister(address, value, CycleCounter);
                break;
            case 0xFFFFFE60:
                _iprb = (ushort)(value << 8);
                break;
            case 0xFFFFFE61:
                break;
            case 0xFFFFFE62:
                _vcra = (ushort)((_vcra & 0x00FF) | (value << 8));
                break;
            case 0xFFFFFE63:
                _vcra = (ushort)((_vcra & 0xFF00) | value);
                break;
            case 0xFFFFFE64:
                _vcrb = (ushort)((_vcrb & 0x00FF) | (value << 8));
                break;
            case 0xFFFFFE65:
                _vcrb = (ushort)((_vcrb & 0xFF00) | value);
                break;
            case 0xFFFFFE71:
            case 0xFFFFFE72:
            case 0xFFFFFE91:
                break;
            case 0xFFFFFE92:
                TraceCacheControlWrite("8", address, value);
                _cacheControl = value;
                if ((value & 0x10) != 0)
                    PurgeAllCache();
                break;
            case >= 0xFFFFFE93 and <= 0xFFFFFE9F:
                break;
            case 0xFFFFFEE2:
                _ipra = (ushort)((_ipra & 0x00FF) | (value << 8));
                break;
            case 0xFFFFFEE3:
                _ipra = (ushort)((_ipra & 0xFF00) | value);
                break;
            case 0xFFFFFEE4:
                _vcrwdt = (ushort)((_vcrwdt & 0x00FF) | (value << 8));
                break;
            case 0xFFFFFEE5:
                _vcrwdt = (ushort)((_vcrwdt & 0xFF00) | value);
                break;
        }
    }

    private void WriteInternalRegisterWord(uint address, ushort value)
    {
        if (TryWriteDmaWord(address, value))
            return;

        MaybeTraceDmaRegisterAccess("WRITE16", address, value);

        switch (address)
        {
            case 0xFFFF8446:
                break;
            case 0xFFFFFE60:
                _iprb = value;
                break;
            case 0xFFFFFE62:
                _vcra = value;
                break;
            case 0xFFFFFE64:
                _vcrb = value;
                break;
            case 0xFFFFFE80:
                _watchdog.WriteControl(value);
                break;
            case 0xFFFFFE92:
                TraceCacheControlWrite("16", address, value);
                _cacheControl = (byte)value;
                if ((value & 0x10) != 0)
                    PurgeAllCache();
                break;
            case 0xFFFFFEE2:
                _ipra = value;
                break;
            case 0xFFFFFEE4:
                _vcrwdt = value;
                break;
            case 0xFFFFFF08:
                _divuControl = (_divuControl & 0xFFFF0000u) | value;
                break;
            case 0xFFFFFF40:
                _breakAddressA = (_breakAddressA & 0x0000FFFFu) | ((uint)value << 16);
                break;
            case 0xFFFFFF42:
                _breakAddressA = (_breakAddressA & 0xFFFF0000u) | value;
                break;
            case 0xFFFFFF60:
                _breakAddressB = (_breakAddressB & 0x0000FFFFu) | ((uint)value << 16);
                break;
            case 0xFFFFFF62:
                _breakAddressB = (_breakAddressB & 0xFFFF0000u) | value;
                break;
        }
    }

    private void WriteInternalRegisterLongword(uint address, uint value)
    {
        if (TryWriteDmaLongword(address, value))
            return;

        MaybeTraceDmaRegisterAccess("WRITE32", address, value);

        switch (address)
        {
            case >= 0xFFFFFF00 and <= 0xFFFFFF03:
                _divuDividendHigh = value;
                break;
            case >= 0xFFFFFF04 and <= 0xFFFFFF07:
                _divuDividendLow = value;
                break;
            case >= 0xFFFFFF08 and <= 0xFFFFFF0B:
                _divuControl = value;
                break;
            case >= 0xFFFFFF10 and <= 0xFFFFFF13:
                _divuDivisor = value;
                if (_divuDivisor == 0)
                {
                    _divuControl |= 0x00000001u; // Set overflow bit on divide by zero
                }
                else
                {
                    long dividend = ((long)(int)_divuDividendHigh << 32) | _divuDividendLow;
                    long quotient = dividend / (int)_divuDivisor;
                    long remainder = dividend % (int)_divuDivisor;
                    
                    _divuDividendHigh = (uint)(remainder >> 32); // SH-2 docs say high bits are updated
                    _divuDividendLow = (uint)quotient;
                    
                    // Actually SH-2 DIVU works like this:
                    // DVDNTL / DVSR -> DVDNTL (quotient), DVDNTH (remainder)
                    // Let's match typical emu logic:
                    _divuDividendLow = (uint)quotient;
                    _divuDividendHigh = (uint)remainder;
                }
                break;
            case 0xFFFFFF40:
                _breakAddressA = value;
                break;
            case 0xFFFFFF48:
                break;
            case 0xFFFFFF60:
                _breakAddressB = value;
                break;
            case 0xFFFFFF68:
                break;
            case >= 0xFFFFFF80 and <= 0xFFFFFF9F or 0xFFFFFFB0:
                _dmaRegister = value;
                break;
            case 0xFFFFFFA0:
                _dmaVector0 = value;
                break;
            case 0xFFFFFFA8:
                _dmaVector1 = value;
                break;
            case >= 0xFFFFFFE0 and <= 0xFFFFFFFF:
                break;
            case 0xFFFFFE92:
                TraceCacheControlWrite("32", address, value);
                _cacheControl = (byte)value;
                if ((value & 0x10) != 0)
                    PurgeAllCache();
                break;
        }
    }

    private void MaybeTraceDmaRegisterAccess(string op, uint address, uint value)
    {
        if (!TraceDmaRegisters || !IsDmaInternalRegister(address))
            return;

        Console.WriteLine(
            $"[S32X-SH2-DMAREG] cpu={_whichCpu} op={op} addr=0x{address:X8} value=0x{value:X8} cyc={CycleCounter}");
    }

    private static bool IsDmaInternalRegister(uint address)
    {
        return (address >= 0xFFFFFF80 && address <= 0xFFFFFFBF)
            || address == 0xFFFFFFA0
            || address == 0xFFFFFFA8;
    }

    private bool TryReadDmaByte(uint address, out byte value)
    {
        if (!TryReadDmaWord(address & ~1u, out ushort word))
        {
            value = 0;
            return false;
        }

        value = (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        return true;
    }

    private bool TryReadDmaWord(uint address, out ushort value)
    {
        if (!TryReadDmaLongword(address & ~3u, out uint longword))
        {
            value = 0;
            return false;
        }

        value = (address & 2) == 0 ? (ushort)(longword >> 16) : (ushort)longword;
        return true;
    }

    private bool TryReadDmaLongword(uint address, out uint value)
    {
        switch (address)
        {
            case 0xFFFFFF80:
                value = _dmaSourceAddress[0];
                break;
            case 0xFFFFFF84:
                value = _dmaDestinationAddress[0];
                break;
            case 0xFFFFFF88:
                value = _dmaTransferCount[0];
                break;
            case 0xFFFFFF8C:
                value = _dmaChannelControl[0];
                break;
            case 0xFFFFFF90:
                value = _dmaSourceAddress[1];
                break;
            case 0xFFFFFF94:
                value = _dmaDestinationAddress[1];
                break;
            case 0xFFFFFF98:
                value = _dmaTransferCount[1];
                break;
            case 0xFFFFFF9C:
                value = _dmaChannelControl[1];
                break;
            case 0xFFFFFFA0:
                value = _dmaVector0;
                break;
            case 0xFFFFFFA8:
                value = _dmaVector1;
                break;
            case 0xFFFFFFB0:
                value = _dmaOperation;
                break;
            default:
                value = 0;
                return false;
        }

        MaybeTraceDmaRegisterAccess("READ32", address, value);
        return true;
    }

    private bool TryWriteDmaByte(uint address, byte value)
    {
        if (!TryReadDmaWord(address & ~1u, out ushort current))
            return false;

        ushort merged = (address & 1) == 0
            ? (ushort)((current & 0x00FF) | (value << 8))
            : (ushort)((current & 0xFF00) | value);
        return TryWriteDmaWord(address & ~1u, merged);
    }

    private bool TryWriteDmaWord(uint address, ushort value)
    {
        if (!TryReadDmaLongword(address & ~3u, out uint current))
            return false;

        uint merged = (address & 2) == 0
            ? (current & 0x0000FFFFu) | ((uint)value << 16)
            : (current & 0xFFFF0000u) | value;
        return TryWriteDmaLongword(address & ~3u, merged);
    }

    private bool TryWriteDmaLongword(uint address, uint value)
    {
        MaybeTraceDmaRegisterAccess("WRITE32", address, value);

        switch (address)
        {
            case 0xFFFFFF80:
                _dmaSourceAddress[0] = value;
                return true;
            case 0xFFFFFF84:
                _dmaDestinationAddress[0] = value;
                return true;
            case 0xFFFFFF88:
                _dmaTransferCount[0] = value & 0x00FF_FFFFu;
                return true;
            case 0xFFFFFF8C:
                _dmaChannelControl[0] = WriteDmaChannelControl(_dmaChannelControl[0], value);
                return true;
            case 0xFFFFFF90:
                _dmaSourceAddress[1] = value;
                return true;
            case 0xFFFFFF94:
                _dmaDestinationAddress[1] = value;
                return true;
            case 0xFFFFFF98:
                _dmaTransferCount[1] = value & 0x00FF_FFFFu;
                return true;
            case 0xFFFFFF9C:
                _dmaChannelControl[1] = WriteDmaChannelControl(_dmaChannelControl[1], value);
                return true;
            case 0xFFFFFFA0:
                _dmaVector0 = value;
                return true;
            case 0xFFFFFFA8:
                _dmaVector1 = value;
                return true;
            case 0xFFFFFFB0:
                _dmaOperation = WriteDmaOperation(_dmaOperation, value);
                return true;
            default:
                return false;
        }
    }

    private static ushort WriteDmaChannelControl(ushort current, uint value)
    {
        ushort next = (ushort)value;
        // TE can be cleared by writes but not set directly.
        if ((next & 0x0002) == 0)
            current &= unchecked((ushort)~0x0002);
        else
            next = (ushort)(next & ~0x0002);

        return (ushort)((next & ~0x0002) | (current & 0x0002));
    }

    private static ushort WriteDmaOperation(ushort current, uint value)
    {
        ushort next = (ushort)value;
        // Address error flag is clear-only.
        if ((next & 0x0004) == 0)
            current &= unchecked((ushort)~0x0004);
        else
            next = (ushort)(next & ~0x0004);

        return (ushort)((next & ~0x0004) | (current & 0x0004));
    }

    private void TickDmaChannel(int channel)
    {
        uint count = _dmaTransferCount[channel] & 0x00FF_FFFFu;
        if (count == 0)
        {
            _dmaChannelControl[channel] |= 0x0002;
            return;
        }

        DmaTransferUnit transferUnit = GetDmaTransferUnit(_dmaChannelControl[channel]);
        switch (transferUnit)
        {
            case DmaTransferUnit.Byte:
            {
                uint source = _dmaSourceAddress[channel];
                byte data = ReadByte(source, Sega32XSh2AccessContext.Data);
                ApplyDmaSourceAddressMode(channel, 1);
                uint destination = _dmaDestinationAddress[channel];
                WriteByte(destination, data, Sega32XSh2AccessContext.Data);
                ApplyDmaDestinationAddressMode(channel, 1);
                _dmaTransferCount[channel] = (count - 1) & 0x00FF_FFFFu;
                break;
            }
            case DmaTransferUnit.Word:
            {
                uint source = _dmaSourceAddress[channel];
                ushort data = ReadWord(source, Sega32XSh2AccessContext.Data);
                ApplyDmaSourceAddressMode(channel, 2);
                uint destination = _dmaDestinationAddress[channel];
                WriteWord(destination, data, Sega32XSh2AccessContext.Data);
                ApplyDmaDestinationAddressMode(channel, 2);
                _dmaTransferCount[channel] = (count - 1) & 0x00FF_FFFFu;
                break;
            }
            case DmaTransferUnit.Longword:
            {
                uint source = _dmaSourceAddress[channel];
                uint data = ReadLongword(source, Sega32XSh2AccessContext.Data);
                ApplyDmaSourceAddressMode(channel, 4);
                uint destination = _dmaDestinationAddress[channel];
                WriteLongword(destination, data, Sega32XSh2AccessContext.Data);
                ApplyDmaDestinationAddressMode(channel, 4);
                _dmaTransferCount[channel] = (count - 1) & 0x00FF_FFFFu;
                break;
            }
            case DmaTransferUnit.SixteenByte:
            {
                int transfers = (int)Math.Min(count, 4);
                for (int i = 0; i < transfers; i++)
                {
                    uint source = _dmaSourceAddress[channel];
                    uint data = ReadLongword(source, Sega32XSh2AccessContext.Data);
                    _dmaSourceAddress[channel] += 4;
                    uint destination = _dmaDestinationAddress[channel];
                    WriteLongword(destination, data, Sega32XSh2AccessContext.Data);
                    ApplyDmaDestinationAddressMode(channel, 4);
                    count--;
                    if (count == 0)
                        break;
                }

                _dmaTransferCount[channel] = count & 0x00FF_FFFFu;
                break;
            }
        }

        if ((_dmaTransferCount[channel] & 0x00FF_FFFFu) == 0)
            _dmaChannelControl[channel] |= 0x0002;
    }

    private static DmaTransferUnit GetDmaTransferUnit(ushort control)
    {
        return (DmaTransferUnit)((control >> 10) & 0x3);
    }

    private void ApplyDmaSourceAddressMode(int channel, uint size)
    {
        switch ((DmaAddressMode)((_dmaChannelControl[channel] >> 12) & 0x3))
        {
            case DmaAddressMode.AutoIncrement:
                _dmaSourceAddress[channel] += size;
                break;
            case DmaAddressMode.AutoDecrement:
                _dmaSourceAddress[channel] -= size;
                break;
        }
    }

    private void ApplyDmaDestinationAddressMode(int channel, uint size)
    {
        switch ((DmaAddressMode)((_dmaChannelControl[channel] >> 14) & 0x3))
        {
            case DmaAddressMode.AutoIncrement:
                _dmaDestinationAddress[channel] += size;
                break;
            case DmaAddressMode.AutoDecrement:
                _dmaDestinationAddress[channel] -= size;
                break;
        }
    }

    private bool CacheEnabled => (_cacheControl & 0x01) != 0;
    private bool DisableInstructionReplacement => (_cacheControl & 0x02) != 0;
    private bool DisableDataReplacement => (_cacheControl & 0x04) != 0;
    private bool TwoWayMode => (_cacheControl & 0x08) != 0;

    private void AssociativePurge(uint address)
    {
        int entryIndex = CacheEntryIndex(address);
        ulong mask = ~(1UL << entryIndex);

        // Associative purge invalidates the whole cache line for this set, not just a matching tag.
        for (int way = 0; way < 4; way++)
            _cacheAddressValidBits[way] &= mask;

        _cacheAddressLruBits[entryIndex] = 0;
    }

    private void PurgeAllCache()
    {
        for (int i = 0; i < _cacheAddressValidBits.Length; i++)
            _cacheAddressValidBits[i] = 0;
        Array.Clear(_cacheAddressLruBits);
    }

    private uint ReadAddressArrayLongword(uint address)
    {
        int entryIndex = CacheEntryIndex(address);
        int way = CacheSelectedWay;
        bool valid = ((_cacheAddressValidBits[way] >> entryIndex) & 1UL) != 0;
        uint tag = _cacheAddressTags[(way * CacheEntries) + entryIndex];
        return (valid ? 1u : 0u) << 1
            | ((uint)_cacheAddressLruBits[entryIndex] << 3)
            | (tag << 10);
    }

    private void WriteAddressArrayLongword(uint address, uint value)
    {
        int entryIndex = CacheEntryIndex(address);
        int way = CacheSelectedWay;
        ulong mask = 1UL << entryIndex;
        bool valid = (address & 0x2) != 0;
        if (valid)
            _cacheAddressValidBits[way] |= mask;
        else
            _cacheAddressValidBits[way] &= ~mask;

        _cacheAddressTags[(way * CacheEntries) + entryIndex] = TagAddress(address);
        _cacheAddressLruBits[entryIndex] = (byte)((value >> 3) & 0x3F);
    }

    private int CacheSelectedWay => (_cacheControl >> 6) & 0x3;

    private static int CacheEntryIndex(uint address) => (int)((address >> 4) & 0x3F);

    private static uint TagAddress(uint address) => (address & 0x1FFFFFFF) >> 10;

    private int CacheRamWordIndex(int way, int entryIndex, uint address) =>
        (((way << 10) | (entryIndex << 4)) | ((int)address & 0xE)) >> 1;

    private bool TryReadCachedByte(uint address, out byte value)
    {
        value = 0;
        if (!TryResolveCacheHit(address, out int way, out int entryIndex))
            return false;
        ushort word = _cacheDataArray[CacheRamWordIndex(way, entryIndex, address)];
        value = (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        return true;
    }

    private bool TryReadCachedWord(uint address, out ushort value)
    {
        value = 0;
        if (!TryResolveCacheHit(address, out int way, out int entryIndex))
            return false;
        value = _cacheDataArray[CacheRamWordIndex(way, entryIndex, address)];
        return true;
    }

    private bool TryReadCachedLongword(uint address, out uint value)
    {
        value = 0;
        if (!TryResolveCacheHit(address, out int way, out int entryIndex))
            return false;
        int idx = CacheRamWordIndex(way, entryIndex, address & ~2u);
        value = ((uint)_cacheDataArray[idx] << 16) | _cacheDataArray[idx + 1];
        return true;
    }

    private bool TryResolveCacheHit(uint address, out int way, out int entryIndex)
    {
        way = 0;
        entryIndex = 0;
        if (!CacheEnabled || !IsCacheableAddress(address))
            return false;

        entryIndex = CacheEntryIndex(address);
        uint tag = TagAddress(address);
        for (int i = 3; i >= 0; i--)
        {
            if (((_cacheAddressValidBits[i] >> entryIndex) & 1UL) != 0
                && _cacheAddressTags[(i * CacheEntries) + entryIndex] == tag)
            {
                way = i;
                UpdateLruBits(i, entryIndex);
                return true;
            }
        }

        return false;
    }

    private void MaybeReplaceCache(uint address, Sega32XSh2AccessContext context)
    {
        if ((context == Sega32XSh2AccessContext.Fetch && DisableInstructionReplacement)
            || (context != Sega32XSh2AccessContext.Fetch && DisableDataReplacement)
            || !CacheEnabled
            || !IsCacheableAddress(address))
        {
            return;
        }

        int entryIndex = CacheEntryIndex(address);
        int way = SelectReplacementWay(entryIndex);
        _cacheAddressTags[(way * CacheEntries) + entryIndex] = TagAddress(address);
        _cacheAddressValidBits[way] |= 1UL << entryIndex;
        UpdateLruBits(way, entryIndex);

        uint lineBase = address & 0x1FFFFFF0;
        int ramIndex = ((way << 10) | (entryIndex << 4)) >> 1;
        if (lineBase >= 0x06000000 && lineBase < 0x06040000)
        {
            int wordIndex = (int)((lineBase - 0x06000000) >> 1);
            for (int i = 0; i < 8; i++)
            {
                int sourceIndex = wordIndex + i;
                _cacheDataArray[ramIndex++] = (uint)sourceIndex < _core.Bus.Sdram.Length
                    ? _core.Bus.Sdram[sourceIndex]
                    : (ushort)0;
            }
            return;
        }

        for (int i = 0; i < 4; i++)
        {
            uint longword = ReadBackingLongword(lineBase + (uint)(i * 4), context);
            _cacheDataArray[ramIndex++] = (ushort)(longword >> 16);
            _cacheDataArray[ramIndex++] = (ushort)longword;
        }
    }

    private int SelectReplacementWay(int entryIndex)
    {
        byte lru = _cacheAddressLruBits[entryIndex];
        if (!TwoWayMode)
        {
            return (lru & 0b100110) == 0b000110 ? 1
                : (lru & 0b010101) == 0b000001 ? 2
                : (lru & 0b001011) == 0 ? 3
                : 0;
        }

        return (lru & 1) != 0 ? 2 : 3;
    }

    private void UpdateLruBits(int way, int entryIndex)
    {
        byte andMask;
        byte orMask;
        switch (way)
        {
            case 0:
                andMask = 0b000111;
                orMask = 0;
                break;
            case 1:
                andMask = 0b111001;
                orMask = 0b100000;
                break;
            case 2:
                andMask = 0b111110;
                orMask = 0b010100;
                break;
            default:
                andMask = 0b111111;
                orMask = 0b001011;
                break;
        }

        _cacheAddressLruBits[entryIndex] = (byte)((_cacheAddressLruBits[entryIndex] & andMask) | orMask);
    }

    private void WriteThroughCacheByte(uint address, byte value)
    {
        if (!TryResolveCacheHit(address, out int way, out int entryIndex))
            return;
        int idx = CacheRamWordIndex(way, entryIndex, address);
        ushort word = _cacheDataArray[idx];
        _cacheDataArray[idx] = (address & 1) == 0
            ? (ushort)((word & 0x00FF) | (value << 8))
            : (ushort)((word & 0xFF00) | value);
    }

    private void WriteThroughCacheWord(uint address, ushort value)
    {
        if (!TryResolveCacheHit(address, out int way, out int entryIndex))
            return;
        _cacheDataArray[CacheRamWordIndex(way, entryIndex, address)] = value;
    }

    private void WriteThroughCacheLongword(uint address, uint value)
    {
        if (!TryResolveCacheHit(address, out int way, out int entryIndex))
            return;
        int idx = CacheRamWordIndex(way, entryIndex, address & ~2u);
        _cacheDataArray[idx] = (ushort)(value >> 16);
        _cacheDataArray[idx + 1] = (ushort)value;
    }

    private static bool IsCacheableAddress(uint address)
    {
        if ((address >> 29) != 0)
            return false;

        uint masked = address & 0x1FFFFFFF;
        return masked <= 0x00003FFF
            || (masked >= 0x02000000 && masked < 0x02400000)
            || (masked >= 0x06000000 && masked < 0x06040000);
    }

    private byte ReadBackingByte(uint masked, Sega32XSh2AccessContext context)
    {
        ushort word = ReadBackingWord(masked & ~1u, context);
        return (masked & 1) == 0 ? (byte)(word >> 8) : (byte)word;
    }

    private ushort ReadBackingWord(uint masked, Sega32XSh2AccessContext context)
    {
        if (masked <= 0x00003FFF)
        {
            CycleCounter += 1;
            ReadOnlySpan<byte> bootRom = _whichCpu == Sega32XCpu.Master ? _core.MasterBootRom : _core.SlaveBootRom;
            if (masked + 1 < bootRom.Length)
                return (ushort)((bootRom[(int)masked] << 8) | bootRom[(int)masked + 1]);
            return 0;
        }

        if (IsSh2SystemRegister(masked) || IsSh2VdpRegister(masked))
        {
            CycleCounter += 1;
            SyncIfCommPortAccessed(masked, isRead: true);
            if (IsSh2VdpRegister(masked) && _core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return 0xFFFF;
            if (IsSh2VdpRegister(masked))
                CycleCounter += Sh2VdpCycles;

            ushort word = IsSh2VdpRegister(masked)
                ? _core.Bus.Vdp.ReadRegister(masked & ~1u)
                : _core.Registers.Sh2Read(masked & ~1u, _whichCpu, _core.Bus.Vdp);

            if (masked >= 0x00004020 && masked <= 0x0000402F)
            {
                if (_core.TryConsumeRecentCommWrite(masked & ~1u, _whichCpu, M68kReferenceCyclesDone, out ushort queuedWord))
                    word = queuedWord;
            }

            MaybeShortCircuitStableRegisterPoll(masked & ~1u, word, context);

            if (TraceBootRegisterReads && (masked & ~1u) == 0x00004000)
            {
                Console.WriteLine(
                    $"[S32X-SH2BUS-{_whichCpu}] read16 addr=0x{masked:X8} word=0x{word:X4} aden={(_core.Registers.AdapterEnabled ? 1 : 0)} reset={(_core.Registers.ResetSh2 ? 1 : 0)}");
            }

            return word;
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            CycleCounter += 1 + Sh2SdramReadCycles;
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            return (uint)wordIndex < _core.Bus.Sdram.Length ? _core.Bus.Sdram[wordIndex] : (ushort)0;
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            CycleCounter += 1 + Sh2FrameBufferReadCycles;
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return 0xFFFF;
            ushort value = _core.Bus.Vdp.ReadFrameBufferWord(masked - 0x04000000);
            TracePcWatch("read16", masked, value, context);
            TraceAddressWatch("read16", masked, value, context);
            return value;
        }

        if (masked >= 0x02000000 && masked < 0x02400000)
        {
            CycleCounter += 1 + Sh2CartridgeCycles;
            return _core.Bus.ReadSh2CartridgeWord(masked & 0x003FFFFE);
        }

        if (masked >= 0x00004030 && masked <= 0x0000403F)
        {
            CycleCounter += 1;
            return _core.Bus.Pwm.ReadRegister(masked);
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            CycleCounter += 1 + Sh2VdpCycles;
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return 0xFFFF;
            return _core.Bus.Vdp.ReadCramWord(masked - 0x00004200);
        }

        return 0;
    }

    private uint ReadBackingLongword(uint masked, Sega32XSh2AccessContext context)
    {
        if (IsSh2SystemRegister(masked) || IsSh2VdpRegister(masked))
        {
            CycleCounter += 2;
            SyncIfCommPortAccessed(masked, isRead: true);
            if (IsSh2VdpRegister(masked) && _core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return 0xFFFFFFFF;
            if (IsSh2VdpRegister(masked))
                CycleCounter += 2 * Sh2VdpCycles;

            ushort highWord = IsSh2VdpRegister(masked)
                ? _core.Bus.Vdp.ReadRegister(masked & ~1u)
                : _core.Registers.Sh2Read(masked & ~1u, _whichCpu, _core.Bus.Vdp);
            ushort lowWord = IsSh2VdpRegister(masked + 2)
                ? _core.Bus.Vdp.ReadRegister((masked + 2) & ~1u)
                : _core.Registers.Sh2Read((masked + 2) & ~1u, _whichCpu, _core.Bus.Vdp);

            if (masked >= 0x00004020 && masked <= 0x0000402F)
            {
                if (_core.TryConsumeRecentCommWrite(masked & ~1u, _whichCpu, M68kReferenceCyclesDone, out ushort queuedHighWord))
                    highWord = queuedHighWord;
                if (_core.TryConsumeRecentCommWrite((masked + 2) & ~1u, _whichCpu, M68kReferenceCyclesDone, out ushort queuedLowWord))
                    lowWord = queuedLowWord;
            }

            return ((uint)highWord << 16) | lowWord;
        }

        if (masked >= 0x00004030 && masked <= 0x0000403F)
        {
            CycleCounter += 2;
            ushort highWord = _core.Bus.Pwm.ReadRegister(masked);
            ushort lowWord = _core.Bus.Pwm.ReadRegister(masked + 2);
            return ((uint)highWord << 16) | lowWord;
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            CycleCounter += 1 + Sh2SdramReadCycles;
            int wordIndex = (int)(((masked - 0x06000000) >> 1) & ~1u);
            if ((uint)(wordIndex + 1) < _core.Bus.Sdram.Length)
                return ((uint)_core.Bus.Sdram[wordIndex] << 16) | _core.Bus.Sdram[wordIndex + 1];
            return 0;
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            CycleCounter += 2 * (1 + Sh2FrameBufferReadCycles);
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return 0xFFFFFFFF;
            ushort highWord = _core.Bus.Vdp.ReadFrameBufferWord(masked - 0x04000000);
            ushort lowWord = _core.Bus.Vdp.ReadFrameBufferWord((masked - 0x04000000) | 2);
            uint value = ((uint)highWord << 16) | lowWord;
            TracePcWatch("read32", masked, value, context);
            TraceAddressWatch("read32", masked, value, context);
            return value;
        }

        if (masked >= 0x02000000 && masked < 0x02400000)
        {
            CycleCounter += 2 * (1 + Sh2CartridgeCycles);
            uint romAddress = masked & 0x003FFFFC;
            ushort highWord = _core.Bus.ReadSh2CartridgeWord(romAddress);
            ushort lowWord = _core.Bus.ReadSh2CartridgeWord(romAddress | 2);
            return ((uint)highWord << 16) | lowWord;
        }

        ushort high = ReadBackingWord(masked & ~1u, context);
        ushort low = ReadBackingWord((masked & ~1u) + 2, context);
        return ((uint)high << 16) | low;
    }

    private void WriteBackingLongword(uint masked, uint value, Sega32XSh2AccessContext context)
    {
        if (IsSh2SystemRegister(masked) || IsSh2VdpRegister(masked))
        {
            CycleCounter += 2;
            SyncIfCommPortAccessed(masked, isRead: false);
            if (IsSh2VdpRegister(masked) && _core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            if (IsSh2VdpRegister(masked))
                CycleCounter += 2 * Sh2VdpCycles;

            if (IsSh2VdpRegister(masked))
            {
                _core.Bus.Vdp.WriteRegister(masked & ~1u, (ushort)(value >> 16));
                _core.Bus.Vdp.WriteRegister((masked + 2) & ~1u, (ushort)value);
            }
            else
            {
                _core.Registers.Sh2Write(masked & ~1u, (ushort)(value >> 16), _whichCpu, _core.Bus.Vdp);
                _core.Registers.Sh2Write((masked + 2) & ~1u, (ushort)value, _whichCpu, _core.Bus.Vdp);
            }
            return;
        }

        if (masked >= 0x00004030 && masked <= 0x0000403F)
        {
            CycleCounter += 2;
            _core.Bus.Pwm.Sh2WriteRegister(masked, (ushort)(value >> 16));
            _core.Bus.Pwm.Sh2WriteRegister(masked + 2, (ushort)value);
            return;
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            CycleCounter += 1 + Sh2SdramWriteCycles;
            int wordIndex = (int)(((masked - 0x06000000) >> 1) & ~1u);
            if ((uint)(wordIndex + 1) < _core.Bus.Sdram.Length)
            {
                _core.Bus.Sdram[wordIndex] = (ushort)(value >> 16);
                _core.Bus.Sdram[wordIndex + 1] = (ushort)value;
            }
            return;
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            uint frameBufferAddress = masked - 0x04000000;

            CycleCounter += _core.Bus.Vdp.FrameBufferWriteLatency(CycleCounter);
            if (IsFrameBufferOverwrite(masked))
                _core.Bus.Vdp.OverwriteFrameBufferWord(frameBufferAddress, (ushort)(value >> 16));
            else
                _core.Bus.Vdp.WriteFrameBufferWord(frameBufferAddress, (ushort)(value >> 16));

            CycleCounter += _core.Bus.Vdp.FrameBufferWriteLatency(CycleCounter);
            if (IsFrameBufferOverwrite(masked))
                _core.Bus.Vdp.OverwriteFrameBufferWord(frameBufferAddress | 2, (ushort)value);
            else
                _core.Bus.Vdp.WriteFrameBufferWord(frameBufferAddress | 2, (ushort)value);
            TracePcWatch("write32", masked, value, context);
            TraceAddressWatch("write32", masked, value, context);
            return;
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            CycleCounter += 2 * (1 + Sh2VdpCycles);
            _core.Bus.Vdp.WriteCramWord(masked - 0x00004200, (ushort)(value >> 16));
            _core.Bus.Vdp.WriteCramWord((masked - 0x00004200) | 2, (ushort)value);
            return;
        }

        WriteBackingWord(masked & ~1u, (ushort)(value >> 16), context);
        WriteBackingWord((masked & ~1u) + 2, (ushort)value, context);
    }

    private void WriteBackingWord(uint masked, ushort value, Sega32XSh2AccessContext context)
    {
        if (IsSh2SystemRegister(masked) || IsSh2VdpRegister(masked))
        {
            SyncIfCommPortAccessed(masked, isRead: true);
            if (IsSh2VdpRegister(masked))
            {
                if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                    return;
                CycleCounter += 1 + Sh2VdpCycles;
                _core.Bus.Vdp.WriteRegister(masked & ~1u, value);
            }
            else
            {
                _core.Registers.Sh2Write(masked & ~1u, value, _whichCpu, _core.Bus.Vdp);
            }
            return;
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            CycleCounter += 1 + Sh2SdramWriteCycles;
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            if ((uint)wordIndex < _core.Bus.Sdram.Length)
                _core.Bus.Sdram[wordIndex] = value;
            return;
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            CycleCounter += _core.Bus.Vdp.FrameBufferWriteLatency(CycleCounter);
            uint frameBufferAddress = masked - 0x04000000;
            if (IsFrameBufferOverwrite(masked))
                _core.Bus.Vdp.OverwriteFrameBufferWord(frameBufferAddress, value);
            else
                _core.Bus.Vdp.WriteFrameBufferWord(frameBufferAddress, value);
            TracePcWatch("write16", masked, value, context);
            TraceAddressWatch("write16", masked, value, context);
            return;
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            CycleCounter += 1 + Sh2VdpCycles;
            _core.Bus.Vdp.WriteCramWord(masked - 0x00004200, value);
        }
    }

    private static bool IsSh2SystemRegister(uint masked) => masked >= 0x00004000 && masked <= 0x0000402F;

    private static bool IsCommPortRegister(uint masked) => masked >= 0x00004020 && masked <= 0x0000402F;

    private static bool IsSh2VdpRegister(uint masked) => masked >= 0x00004100 && masked <= 0x000041FF;

    private static bool IsFrameBufferOverwrite(uint masked) => (masked & 0x00020000) != 0;
}
