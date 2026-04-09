using EutherDrive.Core.Savestates;

namespace EutherDrive.Core.Sega32X;

internal sealed class Sega32XSh2Bus : ISega32XSh2Bus
{
    private const int CacheDataArrayLengthWords = 4 * 1024 / 2;
    private const ulong Sh2CartridgeCycles = 7;
    private const ulong Sh2FrameBufferReadCycles = 4;
    private const ulong Sh2VdpCycles = 4;
    private const ulong Sh2SdramReadCycles = 9;
    private const ulong Sh2SdramWriteCycles = 0;
    private const int CacheEntries = 64;
    private static readonly bool TraceBootRegisterReads =
        string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_S32X_TRACE_BOOT_LOOP"),
            "1",
            StringComparison.Ordinal);
    [NonSerialized] private readonly Sega32XScaffoldCore _core;
    [NonSerialized] private readonly Sega32XCpu _whichCpu;
    private readonly ushort[] _cacheDataArray = new ushort[CacheDataArrayLengthWords];
    private readonly byte[] _serialRegisters = new byte[6];
    private readonly byte[] _freeRunTimerRegisters = new byte[10];
    private readonly uint[] _cacheAddressTags = new uint[CacheEntries * 4];
    private readonly ulong[] _cacheAddressValidBits = new ulong[4];
    private readonly byte[] _cacheAddressLruBits = new byte[CacheEntries];
    private byte _cacheControl;
    private byte _watchdogControl;
    private byte _watchdogCounter;
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
    private uint _dmaVector0;
    private uint _dmaVector1;
    private ushort _freeRunCounterBase;
    private ulong _freeRunCounterCycleBase;

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

    public void SaveState(BinaryWriter writer) => StateBinarySerializer.WriteInto(writer, this);

    public void LoadState(BinaryReader reader) => StateBinarySerializer.ReadInto(reader, this);

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
            
            // Execute in small chunks to maintain handshake precision while keeping performance.
            // 1-by-1 is too slow (12 FPS), whole-budget is too coarse (no boot).
            const ulong ChunkSize = 10;
            while (otherBus.CycleCounter < limit)
            {
                ulong toRun = Math.Min(ChunkSize, limit - otherBus.CycleCounter);
                otherCpu.Execute(toRun, otherBus);
            }
        }
        finally
        {
            _core.EndCommPortSync();
        }
    }

    public byte ReadByte(uint address, Sega32XSh2AccessContext context)
    {
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

        uint masked = address & 0x1FFFFFFF;
        if (TryReadCachedByte(address, out byte cachedByte))
        {
            CycleCounter += 1;
            return cachedByte;
        }

        if (masked <= 0x00003FFF)
        {
            CycleCounter += 1;
            byte value = ReadBackingByte(masked, context);
            MaybeReplaceCache(address, context);
            return value;
        }

        if (IsSh2SystemRegister(masked) || IsSh2VdpRegister(masked))
        {
            CycleCounter += 1;
            SyncIfCommPortAccessed(masked);
            if (IsSh2VdpRegister(masked) && _core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return 0xFF;
            if (IsSh2VdpRegister(masked))
                CycleCounter += Sh2VdpCycles;
            ushort word = IsSh2VdpRegister(masked)
                ? _core.Bus.Vdp.ReadRegister(masked & ~1u)
                : _core.Registers.Sh2Read(masked & ~1u, _whichCpu, _core.Bus.Vdp);
            if (TraceBootRegisterReads && (masked & ~1u) == 0x00004000)
            {
                Console.WriteLine(
                    $"[S32X-SH2BUS-{_whichCpu}] read8 addr=0x{masked:X8} word=0x{word:X4} aden={(_core.Registers.AdapterEnabled ? 1 : 0)} reset={(_core.Registers.ResetSh2 ? 1 : 0)}");
            }
            return (masked & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            CycleCounter += 1 + Sh2SdramReadCycles;
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            if ((uint)wordIndex < _core.Bus.Sdram.Length)
            {
                ushort value = _core.Bus.Sdram[wordIndex];
                return (masked & 1) == 0 ? (byte)(value >> 8) : (byte)value;
            }
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            CycleCounter += 1 + Sh2FrameBufferReadCycles;
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return 0xFF;
            ushort value = _core.Bus.Vdp.ReadFrameBufferWord(masked - 0x04000000);
            return (masked & 1) == 0 ? (byte)(value >> 8) : (byte)value;
        }

        if (masked >= 0x02000000 && masked < 0x02400000)
        {
            CycleCounter += 1 + Sh2CartridgeCycles;
            return _core.Bus.ReadSh2CartridgeByte(masked & 0x003FFFFF);
        }

        if (masked >= 0x00004030 && masked <= 0x0000403F)
        {
            CycleCounter += 1;
            int wordIndex = (int)((masked - 0x00004030) >> 1);
            if ((uint)wordIndex < _core.Bus.Sh2PwmRegisters.Length)
            {
                ushort value = _core.Bus.Sh2PwmRegisters[wordIndex];
                return (masked & 1) == 0 ? (byte)(value >> 8) : (byte)value;
            }
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            CycleCounter += 1 + Sh2VdpCycles;
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return 0xFF;
            ushort value = _core.Bus.Vdp.ReadCramWord(masked - 0x00004200);
            return (masked & 1) == 0 ? (byte)(value >> 8) : (byte)value;
        }

        return 0;
    }

    public ushort ReadWord(uint address, Sega32XSh2AccessContext context)
    {
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

        uint masked = address & 0x1FFFFFFF;
        if (TryReadCachedWord(address, out ushort cachedWord))
        {
            CycleCounter += 1;
            return cachedWord;
        }

        if (masked <= 0x00003FFF)
        {
            CycleCounter += 1;
            ushort value = ReadBackingWord(masked, context);
            MaybeReplaceCache(address, context);
            return value;
        }

        if (IsSh2SystemRegister(masked) || IsSh2VdpRegister(masked))
        {
            CycleCounter += 1;
            SyncIfCommPortAccessed(masked);
            if (IsSh2VdpRegister(masked) && _core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return 0xFFFF;
            if (IsSh2VdpRegister(masked))
                CycleCounter += Sh2VdpCycles;
            ushort word = IsSh2VdpRegister(masked)
                ? _core.Bus.Vdp.ReadRegister(masked & ~1u)
                : _core.Registers.Sh2Read(masked & ~1u, _whichCpu, _core.Bus.Vdp);
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
            if ((uint)wordIndex < _core.Bus.Sdram.Length)
                return _core.Bus.Sdram[wordIndex];
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            CycleCounter += 1 + Sh2FrameBufferReadCycles;
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return 0xFFFF;
            return _core.Bus.Vdp.ReadFrameBufferWord(masked - 0x04000000);
        }

        if (masked >= 0x02000000 && masked < 0x02400000)
        {
            CycleCounter += 1 + Sh2CartridgeCycles;
            return _core.Bus.ReadSh2CartridgeWord(masked & 0x003FFFFE);
        }

        if (masked >= 0x00004030 && masked <= 0x0000403F)
        {
            CycleCounter += 1;
            int wordIndex = (int)((masked - 0x00004030) >> 1);
            if ((uint)wordIndex < _core.Bus.Sh2PwmRegisters.Length)
                return _core.Bus.Sh2PwmRegisters[wordIndex];
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

    public uint ReadLongword(uint address, Sega32XSh2AccessContext context)
    {
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

        if (TryReadCachedLongword(address, out uint cachedLong))
        {
            CycleCounter += 1;
            return cachedLong;
        }

        if ((address >> 29) == 0)
        {
            uint masked = address & 0x1FFFFFFF;
            uint value = ReadBackingLongword(masked, context);
            MaybeReplaceCache(address, context);
            return value;
        }

        ushort high = ReadWord(address & ~1u, context);
        ushort low = ReadWord((address & ~1u) + 2, context);
        return ((uint)high << 16) | low;
    }

    public void WriteByte(uint address, byte value, Sega32XSh2AccessContext context)
    {
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

        uint masked = address & 0x1FFFFFFF;
        WriteThroughCacheByte(address, value);

        if (IsSh2SystemRegister(masked) || IsSh2VdpRegister(masked))
        {
            CycleCounter += 1;
            SyncIfCommPortAccessed(masked);
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
                _core.Bus.Sdram[wordIndex] = (masked & 1) == 0
                    ? (ushort)((current & 0x00FF) | (value << 8))
                    : (ushort)((current & 0xFF00) | value);
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
            CycleCounter += 1;
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            ushort current = _core.Bus.Vdp.ReadCramWord(masked - 0x00004200);
            ushort merged = (masked & 1) == 0
                ? (ushort)((current & 0x00FF) | (value << 8))
                : (ushort)((current & 0xFF00) | value);
            _core.Bus.Vdp.WriteCramWord(masked - 0x00004200, merged);
        }
    }

    public void WriteWord(uint address, ushort value, Sega32XSh2AccessContext context)
    {
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
        WriteThroughCacheWord(address, value);

        if (IsSh2SystemRegister(masked) || IsSh2VdpRegister(masked))
        {
            CycleCounter += 1;
            SyncIfCommPortAccessed(masked);
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
            int wordIndex = (int)((masked - 0x00004030) >> 1);
            if ((uint)wordIndex < _core.Bus.Sh2PwmRegisters.Length)
                _core.Bus.Sh2PwmRegisters[wordIndex] = value;
            return;
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            CycleCounter += 1;
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            _core.Bus.Vdp.WriteCramWord(masked - 0x00004200, value);
        }
    }

    public void WriteLongword(uint address, uint value, Sega32XSh2AccessContext context)
    {
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

        WriteThroughCacheLongword(address, value);
        if ((address >> 29) == 0)
        {
            WriteBackingLongword(address & 0x1FFFFFFF, value, context);
            return;
        }
        WriteWord(address, (ushort)(value >> 16), context);
        WriteWord(address + 2, (ushort)value, context);
    }

    public void IncrementCycleCounter(ulong cycles)
    {
        CycleCounter += cycles;
    }

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
        return address switch
        {
            0xFFFFFC17 => 0,
            >= 0xFFFFFE00 and <= 0xFFFFFE05 => _serialRegisters[address - 0xFFFFFE00],
            >= 0xFFFFFE10 and <= 0xFFFFFE19 => ReadFreeRunTimerRegister(address),
            0xFFFFFE60 => (byte)(_iprb >> 8),
            0xFFFFFE61 => (byte)_iprb,
            0xFFFFFE62 => (byte)(_vcra >> 8),
            0xFFFFFE63 => (byte)_vcra,
            0xFFFFFE64 => (byte)(_vcrb >> 8),
            0xFFFFFE65 => (byte)_vcrb,
            0xFFFFFE80 => _watchdogControl,
            0xFFFFFE81 => _watchdogCounter,
            0xFFFFFE92 => _cacheControl,
            0xFFFFFE93 or >= 0xFFFFFE94 and <= 0xFFFFFE9F => 0,
            0xFFFFFEE2 => (byte)(_ipra >> 8),
            0xFFFFFEE3 => (byte)_ipra,
            0xFFFFFEE4 => (byte)(_vcrwdt >> 8),
            0xFFFFFEE5 => (byte)_vcrwdt,
            _ => 0,
        };
    }

    private ushort ReadInternalRegisterWord(uint address)
    {
        return address switch
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
    }

    private uint ReadInternalRegisterLongword(uint address)
    {
        return address switch
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
    }

    private void WriteInternalRegisterByte(uint address, byte value)
    {
        switch (address)
        {
            case >= 0xFFFFFE00 and <= 0xFFFFFE05:
                _serialRegisters[address - 0xFFFFFE00] = value;
                break;
            case >= 0xFFFFFE10 and <= 0xFFFFFE19:
                WriteFreeRunTimerRegister(address, value);
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
                _watchdogControl = (byte)(value >> 8);
                _watchdogCounter = (byte)value;
                break;
            case 0xFFFFFE92:
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
                _cacheControl = (byte)value;
                if ((value & 0x10) != 0)
                    PurgeAllCache();
                break;
        }
    }

    private byte ReadFreeRunTimerRegister(uint address)
    {
        if (address is 0xFFFFFE12 or 0xFFFFFE13)
        {
            ushort frc = CurrentFreeRunCounter;
            return address == 0xFFFFFE12 ? (byte)(frc >> 8) : (byte)frc;
        }

        return _freeRunTimerRegisters[address - 0xFFFFFE10];
    }

    private void WriteFreeRunTimerRegister(uint address, byte value)
    {
        _freeRunTimerRegisters[address - 0xFFFFFE10] = value;

        if (address is 0xFFFFFE12 or 0xFFFFFE13)
        {
            ushort frc = CurrentFreeRunCounter;
            frc = address == 0xFFFFFE12
                ? (ushort)((frc & 0x00FF) | (value << 8))
                : (ushort)((frc & 0xFF00) | value);
            _freeRunCounterBase = frc;
            _freeRunCounterCycleBase = CycleCounter;
        }
    }

    private ushort CurrentFreeRunCounter =>
        (ushort)(_freeRunCounterBase + ((CycleCounter - _freeRunCounterCycleBase) & 0xFFFF));

    private bool CacheEnabled => (_cacheControl & 0x01) != 0;
    private bool DisableInstructionReplacement => (_cacheControl & 0x02) != 0;
    private bool DisableDataReplacement => (_cacheControl & 0x04) != 0;
    private bool TwoWayMode => (_cacheControl & 0x08) != 0;

    private void AssociativePurge(uint address)
    {
        int entryIndex = CacheEntryIndex(address);
        uint tag = address & 0x1FFFFC00;
        ulong mask = 1UL << entryIndex;
        
        for (int way = 0; way < 4; way++)
        {
            if ((_cacheAddressValidBits[way] & mask) != 0 && _cacheAddressTags[entryIndex * 4 + way] == tag)
            {
                _cacheAddressValidBits[way] &= ~mask;
                // LRU update not strictly required for purge but good for consistency
                return;
            }
        }
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
            ReadOnlySpan<byte> bootRom = _whichCpu == Sega32XCpu.Master ? _core.MasterBootRom : _core.SlaveBootRom;
            if (masked + 1 < bootRom.Length)
                return (ushort)((bootRom[(int)masked] << 8) | bootRom[(int)masked + 1]);
            return 0;
        }

        if (IsSh2SystemRegister(masked) || IsSh2VdpRegister(masked))
        {
            SyncIfCommPortAccessed(masked);
            if (IsSh2VdpRegister(masked) && _core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return 0xFFFF;
            return IsSh2VdpRegister(masked)
                ? _core.Bus.Vdp.ReadRegister(masked & ~1u)
                : _core.Registers.Sh2Read(masked & ~1u, _whichCpu, _core.Bus.Vdp);
        }

        if (masked >= 0x06000000 && masked < 0x06040000)
        {
            int wordIndex = (int)((masked - 0x06000000) >> 1);
            return (uint)wordIndex < _core.Bus.Sdram.Length ? _core.Bus.Sdram[wordIndex] : (ushort)0;
        }

        if (masked >= 0x04000000 && masked < 0x06000000)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return 0xFFFF;
            return _core.Bus.Vdp.ReadFrameBufferWord(masked - 0x04000000);
        }

        if (masked >= 0x02000000 && masked < 0x02400000)
            return _core.Bus.ReadSh2CartridgeWord(masked & 0x003FFFFE);

        if (masked >= 0x00004030 && masked <= 0x0000403F)
        {
            int wordIndex = (int)((masked - 0x00004030) >> 1);
            if ((uint)wordIndex < _core.Bus.Sh2PwmRegisters.Length)
                return _core.Bus.Sh2PwmRegisters[wordIndex];
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return 0xFFFF;
            return _core.Bus.Vdp.ReadCramWord(masked - 0x00004200);
        }

        return 0;
    }

    private uint ReadBackingLongword(uint masked, Sega32XSh2AccessContext context)
    {
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
            return ((uint)highWord << 16) | lowWord;
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
            CycleCounter += 2 * (1 + 4); // FB Write cycles
            _core.Bus.Vdp.WriteFrameBufferWord(masked - 0x04000000, (ushort)(value >> 16));
            _core.Bus.Vdp.WriteFrameBufferWord((masked - 0x04000000) | 2, (ushort)value);
            return;
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            CycleCounter += 2;
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
            SyncIfCommPortAccessed(masked);
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
            CycleCounter += 1 + 4; // FB Write
            _core.Bus.Vdp.WriteFrameBufferWord(masked - 0x04000000, value);
            return;
        }

        if (masked >= 0x00004200 && masked <= 0x000043FF)
        {
            if (_core.Registers.VdpAccess != Sega32XAccess.Sh2)
                return;
            CycleCounter += 1;
            _core.Bus.Vdp.WriteCramWord(masked - 0x00004200, value);
        }
    }

    private static bool IsSh2SystemRegister(uint masked) => masked >= 0x00004000 && masked <= 0x0000402F;

    private static bool IsSh2VdpRegister(uint masked) => masked >= 0x00004100 && masked <= 0x000041FF;

    private static bool IsFrameBufferOverwrite(uint masked) => (masked & 0x00020000) != 0;
}
