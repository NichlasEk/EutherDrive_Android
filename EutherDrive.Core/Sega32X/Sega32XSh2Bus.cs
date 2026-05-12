using EutherDrive.Core.Savestates;
using System.Runtime.CompilerServices;

namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XSh2Bus
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
    private const uint Sh2ExternalAddressMask = 0x1FFF_FFFF;
    private static readonly ulong CommPortSyncChunkSize = ParseCommPortSyncChunkSize();
    private static readonly bool AggressiveCommReadSyncEnabled = ParseAggressiveCommReadSyncEnabled();
    private static readonly uint? TracePcWatchStart = ParseOptionalHex("EUTHERDRIVE_S32X_TRACE_SH2_PCWATCH_START");
    private static readonly uint? TracePcWatchEnd = ParseOptionalHex("EUTHERDRIVE_S32X_TRACE_SH2_PCWATCH_END");
    private static readonly uint? TraceAddressWatchStart = ParseOptionalHex("EUTHERDRIVE_S32X_TRACE_SH2_ADDRWATCH_START");
    private static readonly uint? TraceAddressWatchEnd = ParseOptionalHex("EUTHERDRIVE_S32X_TRACE_SH2_ADDRWATCH_END");
    private static readonly bool TraceCacheControl =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_SH2_CACHE_CTRL"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceCramWrites =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_CRAM_WRITES"),
            "1",
            StringComparison.Ordinal);
    private static readonly int TraceCramWriteLimit = ParseTraceCramWriteLimit();
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
    private static readonly bool TraceVdpBusWrites =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_SH2_VDP_WRITES"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool WideFrameBufferBus =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_WIDE_FB_BUS"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool BusProfilerEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_BUS_PROF"), "1", StringComparison.Ordinal)
        || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PERF"), "1", StringComparison.Ordinal);
    private static readonly bool PreciseSh2DataCache =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_PRECISE_SH2_DATA_CACHE"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceFrameBufferBusWrites =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_SH2_FB_WRITES"),
            "1",
            StringComparison.Ordinal);
    private static readonly int TraceVdpBusWriteLimit = ParseTraceVdpBusWriteLimit();
    [NonSerialized] private readonly Sega32XScaffoldCore _core;
    [NonSerialized] private readonly Sega32XCpu _whichCpu;
    private readonly ushort[] _cacheDataArray = new ushort[CacheDataArrayLengthWords];
    private readonly uint[] _cacheAddressTags = new uint[CacheEntries * 4];
    private readonly ulong[] _cacheAddressValidBits = new ulong[4];
    private readonly byte[] _cacheAddressLruBits = new byte[CacheEntries];
    private ulong _executableVersion;
    private readonly ulong[] _sdramExecutablePageVersions = new ulong[64];
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
    [NonSerialized] private int _cramWriteTraceCount;
    [NonSerialized] private int _vdpBusWriteTraceCount;
    [NonSerialized] private BusProfileCounters _profileCounters;

    public Sega32XSh2Bus(Sega32XScaffoldCore core, Sega32XCpu whichCpu)
    {
        _core = core;
        _whichCpu = whichCpu;
    }

    public ulong CycleCounter { get; private set; }
    public ulong SchedulerCycleCounter => _schedulerCycleCounter;
    public ulong ExecutableVersion => _executableVersion;
    public ulong GetExecutableVersion(uint address)
    {
        uint masked = address & Sh2ExternalAddressMask;
        ulong version = _executableVersion;
        if (masked >= 0x06000000 && masked < 0x06040000)
            version ^= _sdramExecutablePageVersions[(masked - 0x06000000) >> 12];
        return version;
    }
    [field: NonSerialized]
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
    }

    public void ResetTimingState()
    {
        CycleCounter = 0;
        _schedulerCycleCounter = 0;
        CycleLimit = ulong.MaxValue;
        _profileCounters = default;
    }

    public string? BuildAndResetBusProfileSummary()
    {
        if (!BusProfilerEnabled)
            return null;

        BusProfileCounters counters = _profileCounters;
        _profileCounters = default;

        ulong total = counters.OpcodeFetches + counters.SdramReads + counters.SdramWrites +
            counters.FrameBufferReads + counters.FrameBufferWrites + counters.RegisterReads +
            counters.RegisterWrites + counters.CartridgeReads + counters.OtherReads + counters.OtherWrites;
        if (total == 0)
            return null;

        return $"{_whichCpu}: op={counters.OpcodeFetches} cartR={counters.CartridgeReads} " +
            $"sdramR/W={counters.SdramReads}/{counters.SdramWrites} fbR/W={counters.FrameBufferReads}/{counters.FrameBufferWrites} " +
            $"regR/W={counters.RegisterReads}/{counters.RegisterWrites} otherR/W={counters.OtherReads}/{counters.OtherWrites}";
    }

    public void ResyncTimingFromCpu()
    {
        CycleCounter = CurrentCpu.CycleCounter;
        _schedulerCycleCounter = CurrentCpu.CycleCounter;
        CycleLimit = ulong.MaxValue;
    }

    public void ResetState()
    {
        InvalidateExecutableBlocks();
        Array.Clear(_cacheDataArray, 0, _cacheDataArray.Length);
        Array.Clear(_cacheAddressTags, 0, _cacheAddressTags.Length);
        Array.Clear(_cacheAddressValidBits, 0, _cacheAddressValidBits.Length);
        Array.Clear(_cacheAddressLruBits, 0, _cacheAddressLruBits.Length);
        Array.Clear(_sdramExecutablePageVersions, 0, _sdramExecutablePageVersions.Length);
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
        _cramWriteTraceCount = 0;
        _vdpBusWriteTraceCount = 0;
        ResetTimingState();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InvalidateExecutableBlocks()
    {
        _executableVersion++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void InvalidateExecutableSdramPage(uint masked)
    {
        // The current decoded/block fast paths deliberately avoid compiling SDRAM-resident SH-2
        // code, and the exact interpreter fetches directly from SDRAM. Do not spend time tracking
        // per-page executable versions for ordinary work RAM writes.
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
            }

            TickDmaChannel(channel);
            if (!autoRequest && channel == 1)
                _core.Bus.Pwm.AcknowledgeDreq1();
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
            
            // Match jgenesis by only catching the peer SH-2 up to this CPU's current scheduler
            // time. Syncing reads all the way to CycleLimit is useful for debugging a few tight
            // boot loops, but it is very expensive and lets one CPU run too far ahead of the
            // hardware timebase.
            ulong limit = AggressiveCommReadSyncEnabled && isRead
                ? CycleLimit
                : Math.Min(CycleLimit, _schedulerCycleCounter);
            
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

        // Use coarse enough catch-up chunks to avoid spending most 32X time repeatedly entering
        // the peer SH-2 on mailbox polls, while still keeping comm visibility bounded.
        return 200;
    }

    private static bool ParseAggressiveCommReadSyncEnabled()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_AGGRESSIVE_COMM_READ_SYNC");
        return raw == "1" || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseTraceCramWriteLimit()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_CRAM_WRITES_MAX");
        return int.TryParse(raw, out int value) && value > 0 ? value : 512;
    }

    private static int ParseTraceVdpBusWriteLimit()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_SH2_VDP_WRITES_MAX");
        return int.TryParse(raw, out int value) && value > 0 ? value : 512;
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
    private byte DmacPriority => (byte)((_ipra >> 8) & 0x0F);
    private byte WdtPriority => (byte)((_ipra >> 4) & 0x0F);
    private byte SciRxOkVector => (byte)(_vcra & 0x7F);
    private byte WdtVector => (byte)((_vcrwdt >> 8) & 0x7F);

    private (byte Level, byte VectorNumber) GetInternalInterrupt()
    {
        byte level = 0;
        byte vectorNumber = 0;

        byte dmacPriority = DmacPriority;
        if (dmacPriority != 0)
        {
            if (DmaInterruptPending(0))
            {
                level = dmacPriority;
                vectorNumber = (byte)_dmaVector0;
            }
            else if (DmaInterruptPending(1))
            {
                level = dmacPriority;
                vectorNumber = (byte)_dmaVector1;
            }
        }

        if (_serial.RxInterruptPending && SciPriority > level)
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

    private bool DmaInterruptPending(int channel) =>
        (_dmaChannelControl[channel] & 0x0007) == 0x0007;

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

    public byte ReadByte(uint address, Sega32XSh2AccessContext context)
    {
        uint addressSpace = address >> 29;
        switch (address >> 29)
        {
            case 2:
                CycleCounter += 1;
                AssociativePurge(address);
                InvalidateExecutableBlocks();
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

        if (addressSpace == 0 && TryReplaceCache(address, context, out uint cacheLineLongword))
        {
            byte cacheLineValue = (byte)(cacheLineLongword >> (24 - (int)((address & 3) * 8)));
            TracePcWatch("read8", address & 0x1FFFFFFF, cacheLineValue, context);
            TraceAddressWatch("read8", address & 0x1FFFFFFF, cacheLineValue, context);
            return cacheLineValue;
        }

        uint masked = address & 0x1FFFFFFF;
        byte value = ReadBackingByte(masked, context);
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
                InvalidateExecutableBlocks();
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

        if (!PreciseSh2DataCache && context == Sega32XSh2AccessContext.Data && addressSpace is 0 or 1)
        {
            uint fastMasked = address & Sh2ExternalAddressMask;
            if (fastMasked >= 0x06000000 && fastMasked < 0x06040000)
            {
                CycleCounter += 1 + Sh2SdramReadCycles;
                int wordIndex = (int)((fastMasked - 0x06000000) >> 1);
                return (uint)wordIndex < _core.Bus.Sdram.Length ? _core.Bus.Sdram[wordIndex] : (ushort)0;
            }

            if (fastMasked >= 0x02000000 && fastMasked < 0x02400000)
            {
                CycleCounter += 1 + Sh2CartridgeCycles;
                return _core.Bus.ReadSh2CartridgeWord(fastMasked & 0x003FFFFE);
            }
        }

        if (addressSpace == 0 && TryReadCachedWord(address, out ushort cachedWord))
        {
            CycleCounter += 1;
            return cachedWord;
        }

        if (addressSpace == 0 && TryReplaceCache(address, context, out uint cacheLineLongword))
        {
            ushort cacheLineValue = ((address >> 1) & 1) == 0
                ? (ushort)(cacheLineLongword >> 16)
                : (ushort)cacheLineLongword;
            TracePcWatch("read16", address & 0x1FFFFFFF, cacheLineValue, context);
            TraceAddressWatch("read16", address & 0x1FFFFFFF, cacheLineValue, context);
            return cacheLineValue;
        }

        uint masked = address & 0x1FFFFFFF;
        ushort value = ReadBackingWord(masked, context);
        TracePcWatch("read16", masked, value, context);
        TraceAddressWatch("read16", masked, value, context);
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadOpcode(uint address)
    {
        CountOpcodeFetch();
        if (TracePcWatchStart.HasValue || TraceAddressWatchStart.HasValue)
            return ReadWord(address, Sega32XSh2AccessContext.Fetch);

        uint addressSpace = address >> 29;
        switch (addressSpace)
        {
            case 2:
                CycleCounter += 1;
                AssociativePurge(address);
                InvalidateExecutableBlocks();
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

        if (addressSpace == 0 && TryReplaceCache(address, Sega32XSh2AccessContext.Fetch, out uint cacheLineLongword))
        {
            return ((address >> 1) & 1) == 0
                ? (ushort)(cacheLineLongword >> 16)
                : (ushort)cacheLineLongword;
        }

        return ReadBackingWord(address & 0x1FFFFFFF, Sega32XSh2AccessContext.Fetch);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ushort ReadOpcodeFast(uint address)
    {
        CountOpcodeFetch();
        if (TracePcWatchStart.HasValue || TraceAddressWatchStart.HasValue)
            return ReadOpcodeTraced(address);

        uint addressSpace = address >> 29;
        if (addressSpace is not 0 and not 1)
            return ReadOpcodeTraced(address);

        uint masked = address & Sh2ExternalAddressMask;
        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            CycleCounter += 1 + Sh2SdramReadCycles;
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            return (uint)wordIndex < _core.Bus.Sdram.Length ? _core.Bus.Sdram[wordIndex] : (ushort)0;
        }

        if (masked >= 0x02000000 && masked < 0x02400000)
        {
            CycleCounter += 1 + Sh2CartridgeCycles;
            return _core.Bus.ReadSh2CartridgeWord(masked & 0x003FFFFE);
        }

        if (masked <= 0x00003FFF)
        {
            CycleCounter += 1;
            ReadOnlySpan<byte> bootRom = _whichCpu == Sega32XCpu.Master ? _core.MasterBootRom : _core.SlaveBootRom;
            if (masked + 1 < bootRom.Length)
                return (ushort)((bootRom[(int)masked] << 8) | bootRom[(int)masked + 1]);
            return 0;
        }

        return ReadOpcodeTraced(address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ushort ReadOpcodeTraced(uint address)
    {
        return ReadOpcode(address);
    }

    public uint ReadLongword(uint address, Sega32XSh2AccessContext context)
    {
        uint addressSpace = address >> 29;
        switch (address >> 29)
        {
            case 2:
                CycleCounter += 1;
                AssociativePurge(address);
                InvalidateExecutableBlocks();
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

        if (!PreciseSh2DataCache && context == Sega32XSh2AccessContext.Data && addressSpace is 0 or 1)
        {
            uint fastMasked = address & Sh2ExternalAddressMask;
            if (fastMasked >= 0x06000000 && fastMasked < 0x06040000)
            {
                CycleCounter += 1 + Sh2SdramReadCycles;
                int wordIndex = (int)(((fastMasked - 0x06000000) >> 1) & ~1u);
                if ((uint)(wordIndex + 1) < _core.Bus.Sdram.Length)
                    return ((uint)_core.Bus.Sdram[wordIndex] << 16) | _core.Bus.Sdram[wordIndex + 1];
                return 0;
            }

            if (fastMasked >= 0x02000000 && fastMasked < 0x02400000)
            {
                CycleCounter += 2 * (1 + Sh2CartridgeCycles);
                return _core.Bus.ReadSh2CartridgeLongword(fastMasked & 0x003FFFFC);
            }
        }

        if (addressSpace == 0 && TryReadCachedLongword(address, out uint cachedLong))
        {
            CycleCounter += 1;
            return cachedLong;
        }

        if (addressSpace == 0 && TryReplaceCache(address, context, out uint cacheLineLongword))
        {
            TracePcWatch("read32", address & 0x1FFFFFFF, cacheLineLongword, context);
            TraceAddressWatch("read32", address & 0x1FFFFFFF, cacheLineLongword, context);
            return cacheLineLongword;
        }

        uint masked = address & 0x1FFFFFFF;
        uint value = ReadBackingLongword(masked, context);
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
                InvalidateExecutableBlocks();
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
                InvalidateExecutableBlocks();
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
            SyncIfCommPortAccessed(masked, isRead: false);
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
                {
                    TraceVdpBusWrite("write8-reg-denied", masked & ~1u, word, context);
                    return;
                }
                _core.Bus.Vdp.WriteRegister(masked & ~1u, word);
                TraceVdpBusWrite("write8-reg", masked & ~1u, word, context);
            }
            else
                _core.Registers.Sh2Write(masked & ~1u, word, _whichCpu, _core.Bus.Vdp);
            return;
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            // Byte writes are read/modify/write on the 16-bit SDRAM bus; match jgenesis' timing.
            CycleCounter += 1 + Sh2SdramReadCycles;
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            if ((uint)wordIndex < _core.Bus.Sdram.Length)
            {
                ushort current = _core.Bus.Sdram[wordIndex];
                ushort next = (masked & 1) == 0
                    ? (ushort)((current & 0x00FF) | (value << 8))
                    : (ushort)((current & 0xFF00) | value);
                _core.Bus.Sdram[wordIndex] = next;
                InvalidateExecutableSdramPage(masked);
                TracePcWatch("write8", masked, value, context);
                TraceAddressWatch("write8", masked, value, context);
            }
            return;
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            CycleCounter += WideFrameBufferBus ? 1 : _core.Bus.Vdp.FrameBufferWriteLatency(CycleCounter);
            uint frameBufferAddress = masked - 0x04000000;
            _core.Bus.Vdp.WriteFrameBufferByte(frameBufferAddress, value, IsFrameBufferOverwrite(masked));
            TraceVdpBusWrite("write8-fb", masked, value, context);
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
            {
                TraceCramWrite("write8-denied", masked, value, 0, context);
                return;
            }
            ushort current = _core.Bus.Vdp.ReadCramWord(masked - 0x00004200);
            ushort merged = (masked & 1) == 0
                ? (ushort)((current & 0x00FF) | (value << 8))
                : (ushort)((current & 0xFF00) | value);
            _core.Bus.Vdp.WriteCramWord(masked - 0x00004200, merged);
            TraceCramWrite("write8", masked, value, merged, context);
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
                InvalidateExecutableBlocks();
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
                InvalidateExecutableBlocks();
                return;
            case 7:
                CycleCounter += 1;
                WriteInternalRegisterWord(address, value);
                return;
        }

        uint masked = address & 0x1FFFFFFF;
        if (addressSpace == 0)
            WriteThroughCacheWord(address, value);

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            CountSdramWrite();
            CycleCounter += 1 + Sh2SdramWriteCycles;
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            if ((uint)wordIndex < _core.Bus.Sdram.Length)
            {
                _core.Bus.Sdram[wordIndex] = value;
                InvalidateExecutableSdramPage(masked);
                TracePcWatch("write16", masked, value, context);
                TraceAddressWatch("write16", masked, value, context);
            }
            return;
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            CountFrameBufferWrite();
            CycleCounter += WideFrameBufferBus ? 1 : _core.Bus.Vdp.FrameBufferWriteLatency(CycleCounter);
            uint frameBufferAddress = masked - 0x04000000;
            if (IsFrameBufferOverwrite(masked))
                _core.Bus.Vdp.OverwriteFrameBufferWord(frameBufferAddress, value);
            else
                _core.Bus.Vdp.WriteFrameBufferWord(frameBufferAddress, value);
            TraceVdpBusWrite(IsFrameBufferOverwrite(masked) ? "write16-fb-ovr" : "write16-fb", masked, value, context);
            TracePcWatch("write16", masked, value, context);
            TraceAddressWatch("write16", masked, value, context);
            return;
        }

        if (IsSh2SystemRegister(masked) || IsSh2VdpRegister(masked))
        {
            CountRegisterWrite();
            CycleCounter += 1;
            SyncIfCommPortAccessed(masked, isRead: false);
            if (IsSh2VdpRegister(masked))
            {
                CycleCounter += Sh2VdpCycles;
                if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                {
                    TraceVdpBusWrite("write16-reg-denied", masked & ~1u, value, context);
                    return;
                }
                _core.Bus.Vdp.WriteRegister(masked & ~1u, value);
                TraceVdpBusWrite("write16-reg", masked & ~1u, value, context);
            }
            else
                _core.Registers.Sh2Write(masked & ~1u, value, _whichCpu, _core.Bus.Vdp);
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
            {
                TraceCramWrite("write16-denied", masked, value, 0, context);
                return;
            }
            _core.Bus.Vdp.WriteCramWord(masked - 0x00004200, value);
            TraceCramWrite("write16", masked, value, value, context);
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
                InvalidateExecutableBlocks();
                return;
            case 3:
                CycleCounter += 1;
                WriteAddressArrayLongword(address, value);
                InvalidateExecutableBlocks();
                return;
            case 4:
            case 5:
                CycleCounter += 1;
                return;
            case 6:
                CycleCounter += 1;
                WriteCacheDataArrayLongword(address, value);
                InvalidateExecutableBlocks();
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
            if (masked >= 0x06000000 && masked < 0x06040000)
            {
                CountSdramWrite();
                CycleCounter += 1 + Sh2SdramWriteCycles;
                int wordIndex = (int)(((masked - 0x06000000) >> 1) & ~1u);
                if ((uint)(wordIndex + 1) < _core.Bus.Sdram.Length)
                {
                    _core.Bus.Sdram[wordIndex] = (ushort)(value >> 16);
                    _core.Bus.Sdram[wordIndex + 1] = (ushort)value;
                    InvalidateExecutableSdramPage(masked);
                }
                TracePcWatch("write32", masked, value, context);
                TraceAddressWatch("write32", masked, value, context);
                return;
            }

            if (masked >= 0x04000000 && masked < 0x06000000)
            {
                if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                    return;

                CountFrameBufferWrite();
                uint frameBufferAddress = masked - 0x04000000;
                bool overwrite = IsFrameBufferOverwrite(masked);
                if (WideFrameBufferBus)
                {
                    CycleCounter += 1;
                    if (overwrite)
                        _core.Bus.Vdp.OverwriteFrameBufferLongword(frameBufferAddress, value);
                    else
                        _core.Bus.Vdp.WriteFrameBufferLongword(frameBufferAddress, value);
                    TraceVdpBusWrite(overwrite ? "write32-fb-ovr-wide" : "write32-fb-wide", masked, value, context);
                }
                else
                {
                    CycleCounter += _core.Bus.Vdp.FrameBufferWriteLatency(CycleCounter);
                    if (overwrite)
                        _core.Bus.Vdp.OverwriteFrameBufferWord(frameBufferAddress, (ushort)(value >> 16));
                    else
                        _core.Bus.Vdp.WriteFrameBufferWord(frameBufferAddress, (ushort)(value >> 16));
                    TraceVdpBusWrite(overwrite ? "write32-fb-hi-ovr" : "write32-fb-hi", masked, value >> 16, context);

                    CycleCounter += _core.Bus.Vdp.FrameBufferWriteLatency(CycleCounter);
                    if (overwrite)
                        _core.Bus.Vdp.OverwriteFrameBufferWord(frameBufferAddress | 2, (ushort)value);
                    else
                        _core.Bus.Vdp.WriteFrameBufferWord(frameBufferAddress | 2, (ushort)value);
                    TraceVdpBusWrite(overwrite ? "write32-fb-lo-ovr" : "write32-fb-lo", masked | 2, value, context);
                }
                TracePcWatch("write32", masked, value, context);
                TraceAddressWatch("write32", masked, value, context);
                return;
            }

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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsFastPollingRegister(uint address)
    {
        uint masked = (address & Sh2ExternalAddressMask) & ~1u;

        // Keep this intentionally narrow. DREQ FIFO reads have side effects, while the comm
        // ports and frame buffer control register are pure status/mailbox reads and dominate
        // common SH-2 wait loops.
        return masked == 0x0000410A
            || (masked >= 0x00004020 && masked <= 0x0000402F);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSimpleSdramWordAddress(uint address)
    {
        return IsSimpleSdramAddress(address, 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSimpleSdramAddress(uint address, int sizeBytes)
    {
        uint addressSpace = address >> 29;
        if (addressSpace is not 0 and not 1)
            return false;

        uint masked = address & Sh2ExternalAddressMask;
        if (masked < 0x06000000 || masked >= 0x06040000)
            return false;

        return sizeBytes switch
        {
            1 => true,
            2 => (masked & 1) == 0,
            4 => (masked & 3) == 0 && masked <= 0x0603FFFC,
            _ => false,
        };
    }

    public bool TryBulkFillSdram(uint address, uint value, bool isLongword, ulong iterations)
    {
        if (iterations == 0)
            return false;

        uint addressSpace = address >> 29;
        if (addressSpace is not 0 and not 1)
            return false;

        uint masked = address & Sh2ExternalAddressMask;
        if ((masked & 1) != 0 || masked < 0x06000000 || masked >= 0x06040000)
            return false;

        int wordsPerIteration = isLongword ? 2 : 1;
        ulong totalWords = iterations * (ulong)wordsPerIteration;
        int wordIndex = (int)((masked - 0x06000000) >> 1);
        if ((ulong)wordIndex + totalWords > (ulong)_core.Bus.Sdram.Length)
            return false;

        ushort[] sdram = _core.Bus.Sdram;
        if (isLongword)
        {
            ushort high = (ushort)(value >> 16);
            ushort low = (ushort)value;
            for (ulong i = 0; i < iterations; i++)
            {
                int index = wordIndex + (int)(i << 1);
                sdram[index] = high;
                sdram[index + 1] = low;
            }
        }
        else
        {
            sdram.AsSpan(wordIndex, (int)iterations).Fill((ushort)value);
        }

        CountSdramWrite(iterations);
        CycleCounter += iterations * (1 + Sh2SdramWriteCycles);
        InvalidateExecutableSdramPage(masked);
        return true;
    }

    public bool TryBulkCopySdram(uint sourceAddress, uint destinationAddress, bool isLongword, ulong iterations)
    {
        if (iterations == 0)
            return false;

        uint sourceSpace = sourceAddress >> 29;
        uint destinationSpace = destinationAddress >> 29;
        if (sourceSpace is not 0 and not 1 || destinationSpace is not 0 and not 1)
            return false;

        uint sourceMasked = sourceAddress & Sh2ExternalAddressMask;
        uint destinationMasked = destinationAddress & Sh2ExternalAddressMask;
        if ((sourceMasked & 1) != 0 ||
            (destinationMasked & 1) != 0 ||
            sourceMasked < 0x06000000 ||
            sourceMasked >= 0x06040000 ||
            destinationMasked < 0x06000000 ||
            destinationMasked >= 0x06040000)
        {
            return false;
        }

        int wordsPerIteration = isLongword ? 2 : 1;
        ulong totalWords = iterations * (ulong)wordsPerIteration;
        int sourceIndex = (int)((sourceMasked - 0x06000000) >> 1);
        int destinationIndex = (int)((destinationMasked - 0x06000000) >> 1);
        if ((ulong)sourceIndex + totalWords > (ulong)_core.Bus.Sdram.Length ||
            (ulong)destinationIndex + totalWords > (ulong)_core.Bus.Sdram.Length)
        {
            return false;
        }

        Array.Copy(_core.Bus.Sdram, sourceIndex, _core.Bus.Sdram, destinationIndex, (int)totalWords);
        CountSdramRead(iterations);
        CountSdramWrite(iterations);
        CycleCounter += iterations * (1 + Sh2SdramReadCycles + 1 + Sh2SdramWriteCycles);
        InvalidateExecutableSdramPage(destinationMasked);
        return true;
    }

    public bool TryPeekSdramValueNoTiming(uint address, bool isLongword, out uint value)
    {
        value = 0;

        uint addressSpace = address >> 29;
        if (addressSpace is not 0 and not 1)
            return false;

        uint masked = address & Sh2ExternalAddressMask;
        if ((masked & 1) != 0 || masked < 0x06000000 || masked >= 0x06040000)
            return false;

        int wordIndex = (int)((masked - 0x06000000) >> 1);
        ushort[] sdram = _core.Bus.Sdram;
        if (isLongword)
        {
            if ((uint)(wordIndex + 1) >= sdram.Length)
                return false;

            value = ((uint)sdram[wordIndex] << 16) | sdram[wordIndex + 1];
            return true;
        }

        if ((uint)wordIndex >= sdram.Length)
            return false;

        value = unchecked((uint)(short)sdram[wordIndex]);
        return true;
    }

    public byte ReadExternalByteUncached(uint address, Sega32XSh2AccessContext context)
    {
        uint masked = address & Sh2ExternalAddressMask;
        byte value = ReadBackingByte(masked, context);
        TracePcWatch("read8-uncached", masked, value, context);
        TraceAddressWatch("read8-uncached", masked, value, context);
        return value;
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
            0xFFFFFF08 => (ushort)(_divuControl >> 16),
            0xFFFFFF0A => (ushort)_divuControl,
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
            0xFFFFFF00 => _divuDivisor,
            0xFFFFFF04 or 0xFFFFFF14 or 0xFFFFFF1C => _divuDividendLow,
            0xFFFFFF08 => _divuControl,
            0xFFFFFF10 or 0xFFFFFF18 => _divuDividendHigh,
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
                InvalidateExecutableBlocks();
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
                InvalidateExecutableBlocks();
                break;
            case 0xFFFFFEE2:
                _ipra = value;
                break;
            case 0xFFFFFEE4:
                _vcrwdt = value;
                break;
            case 0xFFFFFF08:
                _divuControl = value;
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
            case 0xFFFFFF00:
                _divuDivisor = value;
                break;
            case 0xFFFFFF04:
                ExecuteDivu32(value);
                break;
            case 0xFFFFFF08:
                _divuControl = value;
                break;
            case 0xFFFFFF10:
            case 0xFFFFFF18:
                _divuDividendHigh = value;
                break;
            case 0xFFFFFF14:
            case 0xFFFFFF1C:
                ExecuteDivu64(value);
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
                InvalidateExecutableBlocks();
                break;
        }
    }

    private void ExecuteDivu32(uint dividendValue)
    {
        int dividend = unchecked((int)dividendValue);
        int divisor = unchecked((int)_divuDivisor);
        if (divisor == 0)
        {
            _divuDividendLow = DivuOverflowResult(dividend);
            _divuControl |= 0x00000001u;
            return;
        }

        long quotient = (long)dividend / divisor;
        long remainder = (long)dividend % divisor;
        _divuDividendLow = unchecked((uint)(int)quotient);
        _divuDividendHigh = unchecked((uint)(int)remainder);
    }

    private void ExecuteDivu64(uint lowDividend)
    {
        long dividend = unchecked((long)(((ulong)_divuDividendHigh << 32) | lowDividend));
        int divisor = unchecked((int)_divuDivisor);
        if (divisor == 0)
        {
            _divuDividendLow = DivuOverflowResult(dividend);
            _divuControl |= 0x00000001u;
            return;
        }

        long quotient = dividend / divisor;
        long remainder = dividend % divisor;
        long clampedQuotient = Math.Clamp(quotient, int.MinValue, int.MaxValue);
        if (clampedQuotient != quotient)
            _divuControl |= 0x00000001u;

        _divuDividendLow = unchecked((uint)(int)clampedQuotient);
        _divuDividendHigh = unchecked((uint)(int)remainder);
    }

    private static uint DivuOverflowResult(long dividend) =>
        dividend >= 0 ? 0x7FFFFFFFu : 0x80000000u;

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
        DmaTransferUnit transferUnit = GetDmaTransferUnit(_dmaChannelControl[channel]);
        switch (transferUnit)
        {
            case DmaTransferUnit.Byte:
            {
                uint source = _dmaSourceAddress[channel] & Sh2ExternalAddressMask;
                byte data = ReadByte(source, Sega32XSh2AccessContext.Data);
                ApplyDmaSourceAddressMode(channel, 1);
                uint destination = _dmaDestinationAddress[channel] & Sh2ExternalAddressMask;
                WriteByte(destination, data, Sega32XSh2AccessContext.Data);
                ApplyDmaDestinationAddressMode(channel, 1);
                _dmaTransferCount[channel] = (count - 1) & 0x00FF_FFFFu;
                break;
            }
            case DmaTransferUnit.Word:
            {
                uint source = _dmaSourceAddress[channel] & Sh2ExternalAddressMask;
                ushort data = ReadWord(source, Sega32XSh2AccessContext.Data);
                ApplyDmaSourceAddressMode(channel, 2);
                uint destination = _dmaDestinationAddress[channel] & Sh2ExternalAddressMask;
                WriteWord(destination, data, Sega32XSh2AccessContext.Data);
                ApplyDmaDestinationAddressMode(channel, 2);
                _dmaTransferCount[channel] = (count - 1) & 0x00FF_FFFFu;
                break;
            }
            case DmaTransferUnit.Longword:
            {
                uint source = _dmaSourceAddress[channel] & Sh2ExternalAddressMask;
                uint data = ReadLongword(source, Sega32XSh2AccessContext.Data);
                ApplyDmaSourceAddressMode(channel, 4);
                uint destination = _dmaDestinationAddress[channel] & Sh2ExternalAddressMask;
                WriteLongword(destination, data, Sega32XSh2AccessContext.Data);
                ApplyDmaDestinationAddressMode(channel, 4);
                _dmaTransferCount[channel] = (count - 1) & 0x00FF_FFFFu;
                break;
            }
            case DmaTransferUnit.SixteenByte:
            {
                int transfers = count == 0 ? 4 : (int)Math.Min(count, 4);
                for (int i = 0; i < transfers; i++)
                {
                    uint source = _dmaSourceAddress[channel] & Sh2ExternalAddressMask;
                    uint data = ReadLongword(source, Sega32XSh2AccessContext.Data);
                    _dmaSourceAddress[channel] += 4;
                    uint destination = _dmaDestinationAddress[channel] & Sh2ExternalAddressMask;
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CacheEntryIndex(uint address) => (int)((address >> 4) & 0x3F);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint TagAddress(uint address) => (address & 0x1FFFFFFF) >> 10;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryReplaceCache(uint address, Sega32XSh2AccessContext context, out uint requestedLongword)
    {
        requestedLongword = 0;
        if ((context == Sega32XSh2AccessContext.Fetch && DisableInstructionReplacement)
            || (context != Sega32XSh2AccessContext.Fetch && DisableDataReplacement)
            || !CacheEnabled
            || !IsCacheableAddress(address))
        {
            return false;
        }

        int entryIndex = CacheEntryIndex(address);
        int way = SelectReplacementWay(entryIndex);
        _cacheAddressTags[(way * CacheEntries) + entryIndex] = TagAddress(address);
        _cacheAddressValidBits[way] |= 1UL << entryIndex;
        UpdateLruBits(way, entryIndex);

        uint lineBase = address & 0x1FFFFFF0;
        int ramIndex = ((way << 10) | (entryIndex << 4)) >> 1;
        int requestedLongwordIndex = (int)((address >> 2) & 3);
        if (lineBase >= 0x06000000 && lineBase < 0x06040000)
        {
            CycleCounter += 1 + Sh2SdramReadCycles;
            int wordIndex = (int)((lineBase - 0x06000000) >> 1);
            for (int i = 0; i < 8; i++)
            {
                int sourceIndex = wordIndex + i;
                ushort word = (uint)sourceIndex < _core.Bus.Sdram.Length
                    ? _core.Bus.Sdram[sourceIndex]
                    : (ushort)0;
                _cacheDataArray[ramIndex++] = word;
                if ((i >> 1) == requestedLongwordIndex)
                    requestedLongword = (i & 1) == 0
                        ? (uint)word << 16
                        : requestedLongword | word;
            }
            return true;
        }

        for (int i = 0; i < 4; i++)
        {
            uint longword = ReadBackingLongword(lineBase + (uint)(i * 4), context);
            _cacheDataArray[ramIndex++] = (ushort)(longword >> 16);
            _cacheDataArray[ramIndex++] = (ushort)longword;
            if (i == requestedLongwordIndex)
                requestedLongword = longword;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsCacheableAddress(uint address)
    {
        // The SH-2 cache is controlled by A29-A31 internally. Area 0 accesses can be cached even
        // when the external address does not map to ROM/SDRAM; some 32X games use those cached
        // aliases as scratch storage and expect write-through hits to be readable later.
        if ((address >> 29) != 0)
            return false;

        uint masked = address & Sh2ExternalAddressMask;
        return !IsVolatileSh2ExternalAddress(masked);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsVolatileSh2ExternalAddress(uint masked)
    {
        return IsSh2SystemRegister(masked)
            || IsSh2VdpRegister(masked)
            || (masked >= 0x00004030 && masked <= 0x0000403F)
            || (masked >= 0x00004200 && masked <= 0x000043FF)
            || (masked >= 0x04000000 && masked < 0x06000000);
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
            uint value = _core.Bus.Vdp.ReadFrameBufferLongword(masked - 0x04000000);
            TracePcWatch("read32", masked, value, context);
            TraceAddressWatch("read32", masked, value, context);
            return value;
        }

        if (masked >= 0x02000000 && masked < 0x02400000)
        {
            CycleCounter += 2 * (1 + Sh2CartridgeCycles);
            return _core.Bus.ReadSh2CartridgeLongword(masked & 0x003FFFFC);
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
            {
                TraceVdpBusWrite("write32-reg-denied", masked & ~1u, value, context);
                return;
            }
            if (IsSh2VdpRegister(masked))
                CycleCounter += 2 * Sh2VdpCycles;

            if (IsSh2VdpRegister(masked))
            {
                _core.Bus.Vdp.WriteRegister(masked & ~1u, (ushort)(value >> 16));
                _core.Bus.Vdp.WriteRegister((masked + 2) & ~1u, (ushort)value);
                TraceVdpBusWrite("write32-reg-hi", masked & ~1u, value >> 16, context);
                TraceVdpBusWrite("write32-reg-lo", (masked + 2) & ~1u, value, context);
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
                InvalidateExecutableSdramPage(masked);
            }
            return;
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            uint frameBufferAddress = masked - 0x04000000;

            bool overwrite = IsFrameBufferOverwrite(masked);
            if (WideFrameBufferBus)
            {
                CycleCounter += 1;
                if (overwrite)
                    _core.Bus.Vdp.OverwriteFrameBufferLongword(frameBufferAddress, value);
                else
                    _core.Bus.Vdp.WriteFrameBufferLongword(frameBufferAddress, value);
                TraceVdpBusWrite(overwrite ? "write32-fb-ovr-wide" : "write32-fb-wide", masked, value, context);
            }
            else
            {
                CycleCounter += _core.Bus.Vdp.FrameBufferWriteLatency(CycleCounter);
                if (overwrite)
                    _core.Bus.Vdp.OverwriteFrameBufferWord(frameBufferAddress, (ushort)(value >> 16));
                else
                    _core.Bus.Vdp.WriteFrameBufferWord(frameBufferAddress, (ushort)(value >> 16));
                TraceVdpBusWrite(overwrite ? "write32-fb-hi-ovr" : "write32-fb-hi", masked, value >> 16, context);

                CycleCounter += _core.Bus.Vdp.FrameBufferWriteLatency(CycleCounter);
                if (overwrite)
                    _core.Bus.Vdp.OverwriteFrameBufferWord(frameBufferAddress | 2, (ushort)value);
                else
                    _core.Bus.Vdp.WriteFrameBufferWord(frameBufferAddress | 2, (ushort)value);
                TraceVdpBusWrite(overwrite ? "write32-fb-lo-ovr" : "write32-fb-lo", masked | 2, value, context);
            }
            TracePcWatch("write32", masked, value, context);
            TraceAddressWatch("write32", masked, value, context);
            return;
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
            {
                TraceCramWrite("write32-denied", masked, value, 0, context);
                return;
            }
            CycleCounter += 2 * (1 + Sh2VdpCycles);
            _core.Bus.Vdp.WriteCramWord(masked - 0x00004200, (ushort)(value >> 16));
            _core.Bus.Vdp.WriteCramWord((masked - 0x00004200) | 2, (ushort)value);
            TraceCramWrite("write32", masked, value, (ushort)value, context);
            return;
        }

        WriteBackingWord(masked & ~1u, (ushort)(value >> 16), context);
        WriteBackingWord((masked & ~1u) + 2, (ushort)value, context);
    }

    private void WriteBackingWord(uint masked, ushort value, Sega32XSh2AccessContext context)
    {
        if (IsSh2SystemRegister(masked) || IsSh2VdpRegister(masked))
        {
            SyncIfCommPortAccessed(masked, isRead: false);
            if (IsSh2VdpRegister(masked))
            {
                if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                {
                    TraceVdpBusWrite("write16-reg-denied", masked & ~1u, value, context);
                    return;
                }
                CycleCounter += 1 + Sh2VdpCycles;
                _core.Bus.Vdp.WriteRegister(masked & ~1u, value);
                TraceVdpBusWrite("write16-reg", masked & ~1u, value, context);
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
            {
                _core.Bus.Sdram[wordIndex] = value;
                InvalidateExecutableSdramPage(masked);
            }
            return;
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            CycleCounter += WideFrameBufferBus ? 1 : _core.Bus.Vdp.FrameBufferWriteLatency(CycleCounter);
            uint frameBufferAddress = masked - 0x04000000;
            if (IsFrameBufferOverwrite(masked))
                _core.Bus.Vdp.OverwriteFrameBufferWord(frameBufferAddress, value);
            else
                _core.Bus.Vdp.WriteFrameBufferWord(frameBufferAddress, value);
            TraceVdpBusWrite(IsFrameBufferOverwrite(masked) ? "write16-fb-ovr" : "write16-fb", masked, value, context);
            TracePcWatch("write16", masked, value, context);
            TraceAddressWatch("write16", masked, value, context);
            return;
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
            {
                TraceCramWrite("write16-denied", masked, value, 0, context);
                return;
            }
            CycleCounter += 1 + Sh2VdpCycles;
            _core.Bus.Vdp.WriteCramWord(masked - 0x00004200, value);
            TraceCramWrite("write16", masked, value, value, context);
        }
    }

    private void TraceCramWrite(string op, uint address, uint value, ushort stored, Sega32XSh2AccessContext context)
    {
        if (!TraceCramWrites || _cramWriteTraceCount >= TraceCramWriteLimit)
            return;

        _cramWriteTraceCount++;
        Console.WriteLine(
            $"[S32X-CRAM-{_whichCpu}] pc=0x{CurrentCpu.CurrentInstructionPc:X8} op={op} " +
            $"addr=0x{address:X8} offset=0x{address - 0x00004200:X3} value=0x{value:X8} " +
            $"stored=0x{stored:X4} fm={(_core.Registers.VdpAccess == Sega32XAccess.Sh2 ? 1 : 0)} " +
            $"ctx={context} cyc={CycleCounter}");
    }

    private void TraceVdpBusWrite(string op, uint address, uint value, Sega32XSh2AccessContext context)
    {
        if (!TraceFrameBufferBusWrites && op.Contains("-fb", StringComparison.Ordinal))
            return;
        if (!TraceVdpBusWrites || _vdpBusWriteTraceCount >= TraceVdpBusWriteLimit)
            return;

        _vdpBusWriteTraceCount++;
        Console.WriteLine(
            $"[S32X-VDPBUS-{_whichCpu}] pc=0x{CurrentCpu.CurrentInstructionPc:X8} op={op} " +
            $"addr=0x{address:X8} value=0x{value:X8} fm={(_core.Registers.VdpAccess == Sega32XAccess.Sh2 ? 1 : 0)} " +
            $"ctx={context} cyc={CycleCounter}");
    }

    private static bool IsSh2SystemRegister(uint masked) => masked >= 0x00004000 && masked <= 0x0000402F;

    private static bool IsCommPortRegister(uint masked) => masked >= 0x00004020 && masked <= 0x0000402F;

    private static bool IsSh2VdpRegister(uint masked) => masked >= 0x00004100 && masked <= 0x000041FF;

    private static bool IsFrameBufferOverwrite(uint masked) => (masked & 0x00020000) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountOpcodeFetch()
    {
        if (BusProfilerEnabled)
            _profileCounters.OpcodeFetches++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountSdramRead(ulong count = 1)
    {
        if (BusProfilerEnabled)
            _profileCounters.SdramReads += count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountSdramWrite(ulong count = 1)
    {
        if (BusProfilerEnabled)
            _profileCounters.SdramWrites += count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountFrameBufferRead()
    {
        if (BusProfilerEnabled)
            _profileCounters.FrameBufferReads++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountFrameBufferWrite()
    {
        if (BusProfilerEnabled)
            _profileCounters.FrameBufferWrites++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountRegisterRead()
    {
        if (BusProfilerEnabled)
            _profileCounters.RegisterReads++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountRegisterWrite()
    {
        if (BusProfilerEnabled)
            _profileCounters.RegisterWrites++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountCartridgeRead()
    {
        if (BusProfilerEnabled)
            _profileCounters.CartridgeReads++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountOtherRead()
    {
        if (BusProfilerEnabled)
            _profileCounters.OtherReads++;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountOtherWrite()
    {
        if (BusProfilerEnabled)
            _profileCounters.OtherWrites++;
    }

    private struct BusProfileCounters
    {
        public ulong OpcodeFetches;
        public ulong SdramReads;
        public ulong SdramWrites;
        public ulong FrameBufferReads;
        public ulong FrameBufferWrites;
        public ulong RegisterReads;
        public ulong RegisterWrites;
        public ulong CartridgeReads;
        public ulong OtherReads;
        public ulong OtherWrites;
    }
}
