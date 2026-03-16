using System;
using System.IO;
using System.Threading;

namespace EutherDrive.Core.SegaCd;

public sealed class SegaCdMemory
{
    private static readonly bool LogSubRegs = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_SUBREG"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogSubBus = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_SUBBUS"),
        "1",
        StringComparison.Ordinal);
    private static readonly int SubBusLogLimit = ReadInt("EUTHERDRIVE_SCD_LOG_SUBBUS_LIMIT", 2000);
    private static readonly bool LogSubInt = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_SUBINT"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool TraceSubIntWriterPc = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_SUBINT_PC"),
        "1",
        StringComparison.Ordinal);
    private int _subRegProbeRemaining = 0;
    private int _prgProbeRemaining = 32;
    private int _prgWatchRemaining = 64;
    private int _mainRegProbeRemaining = 64;
    private int _mainPrgReadProbeRemaining = 0;
    private int _subPrgWriteProbeRemaining = 64;
    private int _prgLowWriteRemaining = 512;
    private int _prgBootWriteRemaining = 512;
    private int _prgFlagMainRemaining = 64;
    private int _prgFlagSubRemaining = 64;
    private int _prg2eLogRemaining = 32;
    private static readonly bool LogSubComm = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_SUBCOMM"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogSubCdc = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_SUBCDC"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogCommFlags = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_COMMFLAGS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogSubReads = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_SUBREAD"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogCddCmd = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_CDDCMD"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogCddStatus = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_CDDSTATUS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool TraceCddRegAccess = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_CDD_REGS"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogCddReset = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_CDDRESET"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogCddResetEdge = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_CDDRESET_EDGE"),
        "1",
        StringComparison.Ordinal);
    private int _cddStatusLogRemaining = 64;
    private int _cddResetLogRemaining = 32;
    private bool _subCdcDestLogged;
    private bool _subCdcRegAddrLogged;
    private bool _subCdcRegDataLogged;
    private bool _subAckCddLogged;
    private bool _cddResetLineHigh = true;
    private static readonly bool LogPrgRamWatch = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_PRG_WATCH"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogPrgLowWrites = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_PRG_LOW"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogPrgBootWrites = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_PRG_BOOT"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogPrg2e = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PRG_2E"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogPrgFlag = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_PRG_FLAG"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogPrgWaitWindow = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_PRG_WAIT_WINDOW"),
        "1",
        StringComparison.Ordinal);
    private const uint PrgFlagAddr = 0x005EA4;
    private const uint PrgWaitWindowStart = 0x0005E0;
    private const uint PrgWaitWindowEnd = 0x0005EF;
    private static readonly bool LogMainRegs = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_MAINREG"),
        "1",
        StringComparison.Ordinal);
    private static readonly string? TraceIrqFile =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_IRQ_FILE");
    private readonly bool _logA12001Pc;
    private static readonly bool LogMainRegReads = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_MAINREG_READ"),
        "1",
        StringComparison.Ordinal);
    private static readonly bool LogMainRegProbe = string.Equals(
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_MAINREG_PROBE"),
        "1",
        StringComparison.Ordinal);
    private bool _subRegAccessLogged;
    private int _subBusLogCount;
    private bool _subBusLogSuppressedNotified;
    private int _lastLoggedSubInterruptLevel = -1;
    public const int BiosLen = 128 * 1024;
    public const int PrgRamLen = 512 * 1024;
    public const int BackupRamLen = 8 * 1024;
    public const int RamCartLen = 128 * 1024;
    private const int BackupRamFooterLen = 64;
    private const byte RamCartSizeByte = 0x04;
    private static readonly uint TimerDivider = ReadDivider("EUTHERDRIVE_SCD_TIMER_DIVIDER", 1536);

    private readonly byte[] _bios;
    private readonly byte[] _prgRam = new byte[PrgRamLen];
    private readonly byte[] _backupRam = new byte[BackupRamLen];
    private readonly byte[] _ramCart = new byte[RamCartLen];
    private bool _ramCartWritesEnabled = true;
    private bool _backupRamDirty;
    private uint _timerDivider = TimerDivider;
    private readonly long[] _subAckCounts = new long[7];
    public const uint SubBusAddressMask = 0x0FFFFF;
    private const uint SubRegisterAddressMask = 0x01FF;

    private readonly BufferedWrite[] _bufferedSubWrites = new BufferedWrite[128];
    private readonly BufferedWrite[] _bufferedMainWrites = new BufferedWrite[256];
    private int _bufferedSubWriteCount;
    private int _bufferedMainWriteCount;
    public Func<uint>? MainPcProvider { get; set; }
    public Func<uint>? SubPcProvider { get; set; }

    private enum BufferedWriteKind
    {
        Byte,
        Word
    }

    private readonly struct BufferedWrite
    {
        public readonly BufferedWriteKind Kind;
        public readonly uint Address;
        public readonly ushort Value;

        public BufferedWrite(BufferedWriteKind kind, uint address, ushort value)
        {
            Kind = kind;
            Address = address;
            Value = value;
        }
    }

    public SegaCdRegisters Registers { get; } = new();
    public WordRam WordRam { get; } = new();
    public SegaCdFontRegisters FontRegisters { get; } = new();
    public SegaCdCdcStub Cdc { get; }
    public SegaCdCddStub Cdd { get; }
    public SegaCdPcmStub Pcm { get; }
    public SegaCdGraphicsCoprocessor Graphics { get; } = new();
    private readonly SegaCdCdController _cdController;

    public bool EnableRamCartridge { get; set; } = true;

    private static readonly byte[] BackupRamFooter =
    {
        // $1FC0-$1FCF
        0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x00, 0x00, 0x00, 0x00, 0x40,
        // $1FD0-$1FDF
        0x00, 0x7D, 0x00, 0x7D, 0x00, 0x7D, 0x00, 0x7D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        // $1FE0-$1FEF
        0x53, 0x45, 0x47, 0x41, 0x5F, 0x43, 0x44, 0x5F, 0x52, 0x4F, 0x4D, 0x00, 0x01, 0x00, 0x00, 0x00,
        // $1FF0-$1FFF
        0x52, 0x41, 0x4D, 0x5F, 0x43, 0x41, 0x52, 0x54, 0x52, 0x49, 0x44, 0x47, 0x45, 0x5F, 0x5F, 0x5F
    };

    private static readonly byte[] RamCartFooter =
    {
        // $1FFC0-$1FFCF
        0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x5F, 0x00, 0x00, 0x00, 0x00, 0x40,
        // $1FFD0-$1FFDF
        0x07, 0xFD, 0x07, 0xFD, 0x07, 0xFD, 0x07, 0xFD, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        // $1FFE0-$1FFEF
        0x53, 0x45, 0x47, 0x41, 0x5F, 0x43, 0x44, 0x5F, 0x52, 0x4F, 0x4D, 0x00, 0x01, 0x00, 0x00, 0x00,
        // $1FFF0-$1FFFF
        0x52, 0x41, 0x4D, 0x5F, 0x43, 0x41, 0x52, 0x54, 0x52, 0x49, 0x44, 0x47, 0x45, 0x5F, 0x5F, 0x5F
    };

    public SegaCdMemory(byte[] bios)
    {
        if (bios.Length != BiosLen)
            throw new InvalidOperationException($"BIOS must be {BiosLen} bytes.");
        _bios = bios;
        InitializeBackupRam(_backupRam, BackupRamFooter);
        InitializeBackupRam(_ramCart, RamCartFooter);
        _logA12001Pc = string.Equals(
            Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_A12001_PC"),
            "1",
            StringComparison.Ordinal);
        Cdc = new SegaCdCdcStub();
        Cdc.SubPcProvider = () => SubPcProvider?.Invoke() ?? 0;
        Cdd = new SegaCdCddStub();
        Cdd.SetModel(ParseCdModel(bios));
        Pcm = new SegaCdPcmStub();
        _cdController = new SegaCdCdController(Cdd, Cdc);
    }

    private static void InitializeBackupRam(byte[] buffer, byte[] footer)
    {
        Array.Clear(buffer, 0, buffer.Length);
        if (footer.Length != BackupRamFooterLen || buffer.Length < BackupRamFooterLen)
            return;
        Buffer.BlockCopy(footer, 0, buffer, buffer.Length - BackupRamFooterLen, BackupRamFooterLen);
    }

    private static SegaCdCddStub.CdModel ParseCdModel(byte[] bios)
    {
        string? modelEnv = Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_MODEL");
        if (string.Equals(modelEnv, "2", StringComparison.OrdinalIgnoreCase)
            || string.Equals(modelEnv, "two", StringComparison.OrdinalIgnoreCase))
            return SegaCdCddStub.CdModel.Two;
        if (string.Equals(modelEnv, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(modelEnv, "one", StringComparison.OrdinalIgnoreCase))
            return SegaCdCddStub.CdModel.One;

        // Match jgenesis: detect from BIOS version string at 0x18A..0x18C ("1.")
        if (bios.Length >= 0x18C && bios[0x18A] == (byte)'1' && bios[0x18B] == (byte)'.')
            return SegaCdCddStub.CdModel.One;
        return SegaCdCddStub.CdModel.Two;
    }

    public byte[] GetPrgRamSnapshot()
    {
        byte[] copy = new byte[_prgRam.Length];
        Buffer.BlockCopy(_prgRam, 0, copy, 0, _prgRam.Length);
        return copy;
    }

    internal void SetDisc(CdRom? disc)
    {
        _cdController.SetDisc(disc);
        if (string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_LOG_DISC"), "1", StringComparison.Ordinal))
        {
            if (disc == null)
            {
                Console.Error.WriteLine("[SCD-DISC] SetDisc: null");
            }
            else
            {
                Console.Error.WriteLine($"[SCD-DISC] SetDisc: tracks={disc.Cue.Tracks.Count} end={disc.Cue.LastTrack.EndTime}");
            }
        }
    }

    internal short[] ConsumeAudioBuffer()
    {
        return _cdController.ConsumeAudioBuffer();
    }

    internal short[] ConsumeCdAudioBuffer()
    {
        return _cdController.ConsumeAudioBuffer();
    }

    internal short[] ConsumePcmAudioBuffer()
    {
        return Pcm.ConsumeAudioBuffer();
    }

    public void Reset()
    {
        Registers.Reset();
        Array.Clear(_prgRam, 0, _prgRam.Length);
        _lastLoggedSubInterruptLevel = -1;
        _cdController.Reset();
        WordRam.Reset();
    }

    public void Tick(uint masterClockCycles)
    {
        bool prgRamAccessible = !(Registers.SubCpuBusReq || Registers.SubCpuReset);
        _cdController.Tick(masterClockCycles, WordRam, _prgRam, prgRamAccessible, Pcm);

        uint cycles = masterClockCycles;
        while (cycles >= _timerDivider)
        {
            ClockTimers();
            cycles -= _timerDivider;
            _timerDivider = TimerDivider;
        }
        _timerDivider -= cycles;

        if (!WordRam.IsSubAccessBlocked())
            Graphics.Tick(masterClockCycles, WordRam, Registers.GraphicsInterruptEnabled);
    }

    public void EmulateSubCpuHandshake()
    {
        if (Registers.SubCpuReset || Registers.SubCpuBusReq)
            return;

        // Keep only the ordering side effect of deferred sub-register writes.
        // Unlike the previous stub behavior, do not synthesize command/status
        // mailbox progress when the sub CPU is not actually executing.
        FlushBufferedSubWrites();
    }

    public void FlushBufferedSubWrites()
    {
        if (_bufferedSubWriteCount == 0)
            return;

        for (int i = 0; i < _bufferedSubWriteCount; i++)
        {
            var write = _bufferedSubWrites[i];
            if (write.Kind == BufferedWriteKind.Byte)
                WriteSubRegisterByte(write.Address, (byte)write.Value);
            else
                WriteSubRegisterWord(write.Address, write.Value);
        }
        _bufferedSubWriteCount = 0;
    }

    public void FlushBufferedMainWrites()
    {
        if (_bufferedMainWriteCount == 0)
            return;

        for (int i = 0; i < _bufferedMainWriteCount; i++)
        {
            var write = _bufferedMainWrites[i];
            if (write.Kind == BufferedWriteKind.Byte)
                ApplyMainWriteByte(write.Address, (byte)write.Value);
            else
                ApplyMainWriteWord(write.Address, write.Value);
        }
        _bufferedMainWriteCount = 0;
    }

    private void ClockTimers()
    {
        if (Registers.TimerCounter == 1)
        {
            Registers.TimerInterruptPending = true;
            Registers.TimerCounter = 0;
        }
        else if (Registers.TimerCounter == 0)
        {
            Registers.TimerCounter = Registers.TimerInterval;
        }
        else
        {
            Registers.TimerCounter--;
        }

        Registers.StopwatchCounter = (ushort)((Registers.StopwatchCounter + 1) & 0x0FFF);
    }

    private static uint ReadDivider(string key, uint fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(raw)
            && uint.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out uint value)
            && value > 0)
        {
            return value;
        }
        return fallback;
    }

    private static int ReadInt(string key, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
            && value >= 0)
        {
            return value;
        }

        return fallback;
    }

    private void LogSubBusLine(string line)
    {
        if (!LogSubBus || SubBusLogLimit == 0)
            return;

        if (_subBusLogCount < SubBusLogLimit)
        {
            _subBusLogCount++;
            Console.WriteLine(line);
            return;
        }

        if (!_subBusLogSuppressedNotified)
        {
            _subBusLogSuppressedNotified = true;
            Console.WriteLine($"[SCD-SUBBUS] log suppressed after {SubBusLogLimit} lines (set EUTHERDRIVE_SCD_LOG_SUBBUS_LIMIT)");
        }
    }

    public byte ReadMainByte(uint address)
    {
        if (address <= 0x1FFFFF)
        {
            if ((address & 0x20000) == 0)
            {
                switch (address)
                {
                    case 0x000070:
                    case 0x000071:
                        return 0xFF;
                    case 0x000072:
                        return (byte)(Registers.HInterruptVector >> 8);
                    case 0x000073:
                        return (byte)(Registers.HInterruptVector & 0xFF);
                    default:
                        return _bios[address & 0x1FFFF];
                }
            }

            uint prgAddr = Registers.PrgRamAddr(address);
            if (LogPrgFlag && prgAddr == PrgFlagAddr)
            {
                byte val = _prgRam[prgAddr];
                Console.WriteLine($"[SCD-PRG-FLAG] R8 MAIN addr=0x{prgAddr:X6} val=0x{val:X2}");
            }
            if (_mainPrgReadProbeRemaining > 0 && prgAddr >= 0x0180 && prgAddr <= 0x0190)
            {
                _mainPrgReadProbeRemaining--;
                Console.WriteLine($"[SCD-PRG-RPROBE] R8 MAIN prg=0x{prgAddr:X6}");
            }
            return _prgRam[prgAddr];
        }

        if (address <= 0x3FFFFF)
            return WordRam.MainCpuReadRam(address);

        if (address <= 0x7FFFFF)
            return EnableRamCartridge ? ReadRamCartByte(address) : (byte)0xFF;

        if (address >= 0xA12000 && address <= 0xA1202F)
            return ReadMainRegisterByte(address);

        return 0xFF;
    }

    public ushort ReadMainWord(uint address)
    {
        if (address <= 0x1FFFFF)
        {
            if ((address & 0x20000) == 0)
            {
                if (address == 0x000070) return 0xFFFF;
                if (address == 0x000072) return Registers.HInterruptVector;
                int addr = (int)(address & 0x1FFFF);
                return (ushort)((_bios[addr] << 8) | _bios[addr + 1]);
            }

            uint prgAddr = Registers.PrgRamAddr(address);
            if (LogPrgFlag && prgAddr == PrgFlagAddr)
            {
                byte msb = _prgRam[prgAddr];
                byte lsb = _prgRam[prgAddr + 1];
                ushort val = (ushort)((msb << 8) | lsb);
                Console.WriteLine($"[SCD-PRG-FLAG] R16 MAIN addr=0x{prgAddr:X6} val=0x{val:X4}");
            }
            if (_mainPrgReadProbeRemaining > 0 && prgAddr >= 0x0180 && prgAddr <= 0x0190)
            {
                _mainPrgReadProbeRemaining--;
                Console.WriteLine($"[SCD-PRG-RPROBE] R16 MAIN prg=0x{prgAddr:X6}");
            }
            return (ushort)((_prgRam[prgAddr] << 8) | _prgRam[prgAddr + 1]);
        }

        if (address <= 0x3FFFFF)
        {
            byte msb = WordRam.MainCpuReadRam(address);
            byte lsb = WordRam.MainCpuReadRam(address | 1);
            return (ushort)((msb << 8) | lsb);
        }

        if (address <= 0x7FFFFF)
        {
            // RAM cartridge is mapped to odd addresses only; return the byte value as a word.
            return ReadRamCartByte(address | 1);
        }

        if (address >= 0xA12000 && address <= 0xA1202F)
            return ReadMainRegisterWord(address);

        return 0xFFFF;
    }

    internal ushort ReadMainWordForDma(uint address)
    {
        if (address >= 0x200000 && address <= 0x3FFFFF)
            return WordRam.MainCpuDmaReadWord(address);
        return ReadMainWord(address);
    }

    public void WriteMainByte(uint address, byte value)
    {
        BufferMainWrite(BufferedWriteKind.Byte, address, value);
    }

    public void WriteMainWord(uint address, ushort value)
    {
        BufferMainWrite(BufferedWriteKind.Word, address, value);
    }

    private void ApplyMainWriteByte(uint address, byte value)
    {
        if (address <= 0x1FFFFF)
        {
            if ((address & 0x20000) != 0)
            {
                uint prgAddr = Registers.PrgRamAddr(address);
                WritePrgRam(prgAddr, value, ScdCpu.Main);
            }
            return;
        }

        if (address <= 0x3FFFFF)
        {
            WordRam.MainCpuWriteRam(address, value);
            return;
        }

        if (address <= 0x7FFFFF)
        {
            if (EnableRamCartridge)
                WriteRamCartByte(address, value);
            return;
        }

        if (address >= 0xA12000 && address <= 0xA1202F)
        {
            WriteMainRegisterByte(address, value);
        }
    }

    private void ApplyMainWriteWord(uint address, ushort value)
    {
        if (address <= 0x1FFFFF)
        {
            if ((address & 0x20000) != 0)
            {
                uint prgAddr = Registers.PrgRamAddr(address);
                WritePrgRam(prgAddr, (byte)(value >> 8), ScdCpu.Main);
                WritePrgRam(prgAddr + 1, (byte)value, ScdCpu.Main);
            }
            return;
        }

        if (address <= 0x3FFFFF)
        {
            WordRam.MainCpuWriteRam(address, (byte)(value >> 8));
            WordRam.MainCpuWriteRam(address | 1, (byte)value);
            return;
        }

        if (address <= 0x7FFFFF)
        {
            if (EnableRamCartridge)
                WriteRamCartByte(address | 1, (byte)value);
            return;
        }

        if (address >= 0xA12000 && address <= 0xA1202F)
        {
            WriteMainRegisterWord(address, value);
        }
    }

    private byte ReadRamCartByte(uint address)
    {
        if (!EnableRamCartridge)
            return 0xFF;
        if ((address & 1) == 0)
            return 0x00;
        if (address <= 0x4FFFFF)
            return RamCartSizeByte;
        if (address <= 0x5FFFFF)
            return 0x00;
        if (address <= 0x6FFFFF)
            return _ramCart[(address & 0x3FFFF) >> 1];
        if (address <= 0x7FFFFF)
            return _ramCartWritesEnabled ? (byte)1 : (byte)0;
        return 0x00;
    }

    private void WriteRamCartByte(uint address, byte value)
    {
        if (!EnableRamCartridge)
            return;
        if ((address & 1) == 0)
            return;
        if (address <= 0x5FFFFF)
            return;
        if (address <= 0x6FFFFF)
        {
            if (_ramCartWritesEnabled)
            {
                _ramCart[(address & 0x3FFFF) >> 1] = value;
                _backupRamDirty = true;
            }
            return;
        }
        if (address <= 0x7FFFFF)
        {
            _ramCartWritesEnabled = (value & 1) != 0;
        }
    }

    private void WritePrgRam(uint address, byte value, ScdCpu cpu)
    {
        if (LogPrg2e && _prg2eLogRemaining > 0 && address >= 0x0002D0 && address <= 0x0002F0)
        {
            _prg2eLogRemaining--;
            Console.WriteLine($"[SCD-PRG-2E] W8 {cpu} addr=0x{address:X6} val=0x{value:X2} BUSREQ={(Registers.SubCpuBusReq ? 1 : 0)} RESET={(Registers.SubCpuReset ? 1 : 0)}");
        }

        if (cpu == ScdCpu.Main && !(Registers.SubCpuBusReq || Registers.SubCpuReset))
        {
            if (LogMainRegs)
                Console.WriteLine($"[SCD-PRG] Main write blocked (sub owns bus) addr=0x{address:X6} val=0x{value:X2}");
            // Match hardware and jgenesis: main CPU cannot write PRG RAM while sub CPU has the bus.
            return;
        }

        uint boundary = (uint)Registers.PrgRamWriteProtect * 0x200;
        if (cpu == ScdCpu.Main || address >= boundary)
        {
            if (LogPrgLowWrites && _prgLowWriteRemaining > 0 && address < 0x0200 && value != 0)
            {
                _prgLowWriteRemaining--;
                uint pc = cpu == ScdCpu.Main ? (MainPcProvider?.Invoke() ?? 0) : (SubPcProvider?.Invoke() ?? 0);
                Console.WriteLine(
                    $"[SCD-PRG-LOW] cpu={cpu} addr=0x{address:X6} val=0x{value:X2} " +
                    $"pc=0x{pc:X6} bank=0x{Registers.PrgRamBank:X2} wp=0x{Registers.PrgRamWriteProtect:X2}");
            }
            if (LogPrgBootWrites && _prgBootWriteRemaining > 0 && address >= 0x0180 && address < 0x0240)
            {
                _prgBootWriteRemaining--;
                uint pc = cpu == ScdCpu.Main ? (MainPcProvider?.Invoke() ?? 0) : (SubPcProvider?.Invoke() ?? 0);
                Console.WriteLine(
                    $"[SCD-PRG-BOOT] cpu={cpu} addr=0x{address:X6} val=0x{value:X2} " +
                    $"pc=0x{pc:X6} bank=0x{Registers.PrgRamBank:X2} wp=0x{Registers.PrgRamWriteProtect:X2}");
            }
            _prgRam[address] = value;
            if (LogPrgFlag && address == PrgFlagAddr)
            {
                uint pc = cpu == ScdCpu.Main ? (MainPcProvider?.Invoke() ?? 0) : (SubPcProvider?.Invoke() ?? 0);
                Console.WriteLine($"[SCD-PRG-FLAG] W8 {cpu} addr=0x{address:X6} val=0x{value:X2} pc=0x{pc:X6}");
            }
            if (address == PrgFlagAddr)
            {
                uint pc = cpu == ScdCpu.Main ? (MainPcProvider?.Invoke() ?? 0) : (SubPcProvider?.Invoke() ?? 0);
                AppendTraceLine($"[SCD-IRQ-FILE] PRG flag cpu={cpu} val=0x{value:X2} pc=0x{pc:X6}");
            }
            if (LogPrgWaitWindow && (address == PrgFlagAddr || (address >= PrgWaitWindowStart && address <= PrgWaitWindowEnd)))
            {
                uint pc = cpu == ScdCpu.Main ? (MainPcProvider?.Invoke() ?? 0) : (SubPcProvider?.Invoke() ?? 0);
                Console.WriteLine(
                    $"[SCD-PRG-WAIT] W8 {cpu} addr=0x{address:X6} val=0x{value:X2} pc=0x{pc:X6} " +
                    $"flag=0x{_prgRam[PrgFlagAddr]:X2}");
            }
            if (cpu == ScdCpu.Main && LogPrgRamWatch && _prgProbeRemaining > 0 && address < 0x0400)
            {
                _prgProbeRemaining--;
                Console.WriteLine($"[SCD-PRG-PROBE] W8 MAIN addr=0x{address:X6} val=0x{value:X2} bank={Registers.PrgRamBank}");
            }
            if (cpu == ScdCpu.Main && LogPrgRamWatch && _prgWatchRemaining > 0 && (value != 0 || (address >= 0x0180 && address <= 0x0190)))
            {
                _prgWatchRemaining--;
                Console.WriteLine($"[SCD-PRG-WATCH] W8 MAIN addr=0x{address:X6} val=0x{value:X2} bank={Registers.PrgRamBank}");
            }
            if (address >= 0x0180 && address <= 0x0190)
            {
                if (cpu == ScdCpu.Main && LogPrgFlag && _prgFlagMainRemaining > 0)
                {
                    _prgFlagMainRemaining--;
                    Console.WriteLine($"[SCD-PRG-FLAG] W8 MAIN addr=0x{address:X6} val=0x{value:X2}");
                }
                if (cpu == ScdCpu.Sub && LogPrgFlag && _prgFlagSubRemaining > 0)
                {
                    _prgFlagSubRemaining--;
                    Console.WriteLine($"[SCD-PRG-FLAG] W8 SUB addr=0x{address:X6} val=0x{value:X2}");
                }
            }
            if (cpu == ScdCpu.Sub && _subPrgWriteProbeRemaining > 0 && address >= 0x0180 && address <= 0x0190)
            {
                _subPrgWriteProbeRemaining--;
                Console.WriteLine($"[SCD-PRG-SUB] W8 SUB addr=0x{address:X6} val=0x{value:X2}");
            }
            if (LogPrgRamWatch && address >= 0x0180 && address <= 0x0190)
                Console.WriteLine($"[SCD-PRG] W8 {cpu} addr=0x{address:X6} val=0x{value:X2} bank={Registers.PrgRamBank}");
        }
    }

    private byte ReadMainRegisterByte(uint address)
    {
        if (LogMainRegProbe && _mainRegProbeRemaining > 0)
        {
            _mainRegProbeRemaining--;
            Console.WriteLine($"[SCD-MAINREG-PROBE] R8 0x{address:X6}");
        }
        byte value = address switch
        {
            0xA12000 => (byte)(((Registers.SoftwareInterruptEnabled ? 1 : 0) << 7) | (Registers.SubSoftwareInterruptPending ? 1 : 0)),
            // Bit1: BUSREQ (1=request), Bit0: RESET (1=run)
            0xA12001 => (byte)(((Registers.SubCpuBusReq ? 1 : 0) << 1) | (Registers.SubCpuReset ? 0 : 1)),
            0xA12002 => Registers.PrgRamWriteProtect,
            0xA12003 => (byte)((Registers.PrgRamBank << 6) | WordRam.ReadControl()),
            0xA12004 => (byte)(((Cdc.EndOfDataTransfer ? 1 : 0) << 7) | ((Cdc.DataReady ? 1 : 0) << 6) | Cdc.DeviceDestinationBits),
            0xA12006 => (byte)(Registers.HInterruptVector >> 8),
            0xA12007 => (byte)(Registers.HInterruptVector & 0xFF),
            0xA12008 => (byte)(Cdc.ReadHostData(ScdCpu.Main) >> 8),
            0xA12009 => (byte)(Cdc.ReadHostData(ScdCpu.Main) & 0xFF),
            0xA1200C => (byte)(Registers.StopwatchCounter >> 8),
            0xA1200D => (byte)(Registers.StopwatchCounter & 0xFF),
            0xA1200E => Registers.MainCpuCommunicationFlags,
            0xA1200F => Registers.SubCpuCommunicationFlags,
            >= 0xA12010 and <= 0xA1201F => ReadCommBuffer(Registers.CommunicationCommands, address),
            >= 0xA12020 and <= 0xA1202F => ReadCommBuffer(Registers.CommunicationStatuses, address),
            _ => 0x00
        };
        if (LogMainRegReads && address is >= 0xA12000 and <= 0xA1202F)
            Console.WriteLine($"[SCD-MAINREG] R8 0x{address:X6} -> 0x{value:X2}");
        if (LogCommFlags && (address == 0xA1200E || address == 0xA1200F))
            Console.WriteLine($"[SCD-COMM] MAIN R8 0x{address:X6} -> 0x{value:X2}");
        return value;
    }

    private ushort ReadMainRegisterWord(uint address)
    {
        if (LogMainRegProbe && _mainRegProbeRemaining > 0)
        {
            _mainRegProbeRemaining--;
            Console.WriteLine($"[SCD-MAINREG-PROBE] R16 0x{address:X6}");
        }
        ushort value = address switch
        {
            0xA12000 or 0xA12002 => (ushort)((ReadMainRegisterByte(address) << 8) | ReadMainRegisterByte(address | 1)),
            0xA12004 => (ushort)(ReadMainRegisterByte(address) << 8),
            0xA12006 => Registers.HInterruptVector,
            0xA12008 => Cdc.ReadHostData(ScdCpu.Main),
            0xA1200C => Registers.StopwatchCounter,
            0xA1200E => (ushort)((Registers.MainCpuCommunicationFlags << 8) | Registers.SubCpuCommunicationFlags),
            >= 0xA12010 and <= 0xA1201F => ReadCommWord(Registers.CommunicationCommands, address),
            >= 0xA12020 and <= 0xA1202F => ReadCommWord(Registers.CommunicationStatuses, address),
            _ => 0x0000
        };
        if (LogMainRegReads && address is >= 0xA12000 and <= 0xA1202F)
            Console.WriteLine($"[SCD-MAINREG] R16 0x{address:X6} -> 0x{value:X4}");
        return value;
    }

    private void WriteMainRegisterByte(uint address, byte value)
    {
        if (LogMainRegProbe && _mainRegProbeRemaining > 0)
        {
            _mainRegProbeRemaining--;
            Console.WriteLine($"[SCD-MAINREG-PROBE] W8 0x{address:X6} = 0x{value:X2}");
        }
        if (LogMainRegs && address is >= 0xA12000 and <= 0xA1202F)
            Console.WriteLine($"[SCD-MAINREG] W8 0x{address:X6} = 0x{value:X2}");
        switch (address)
        {
            case 0xA12000:
                bool nextPend = (value & 0x01) != 0;
                if (LogCommFlags && nextPend != Registers.SubSoftwareInterruptPending)
                    Console.WriteLine(
                        $"[COMM-DEBUG] MAIN W 0xA12000 = 0x{value:X2} (pend={nextPend}) " +
                        $"PC=0x{MainPcProvider?.Invoke():X6} SUBPC=0x{SubPcProvider?.Invoke():X6}");
                AppendTraceLine(
                    $"[SCD-IRQ-FILE] MAIN A12000=0x{value:X2} pend={nextPend} " +
                    $"main_pc=0x{MainPcProvider?.Invoke():X6} sub_pc=0x{SubPcProvider?.Invoke():X6}");
                Registers.SubSoftwareInterruptPending = nextPend;
                break;
            case 0xA12001:
                Registers.SubCpuBusReq = (value & 0x02) != 0;
                Registers.SubCpuReset = (value & 0x01) == 0;
                if (LogMainRegs)
                {
                    Console.WriteLine($"[SCD-MAINREG] 0xA12001 <= 0x{value:X2} BUSREQ={Registers.SubCpuBusReq} RESET={Registers.SubCpuReset}");
                }
                if (_logA12001Pc)
                {
                    uint pc = MainPcProvider?.Invoke() ?? 0;
                    Console.WriteLine($"[SCD-A12001-PC] pc=0x{pc:X6} val=0x{value:X2} BUSREQ={(Registers.SubCpuBusReq ? 1 : 0)} RESET={(Registers.SubCpuReset ? 1 : 0)}");
                }
                break;
            case 0xA12002:
                Registers.PrgRamWriteProtect = value;
                break;
            case 0xA12003:
                Registers.PrgRamBank = (byte)(value >> 6);
                WordRam.MainCpuWriteControl(value);
                if (LogMainRegs)
                    Console.WriteLine($"[SCD-WORDRAM] main ctl=0x{value:X2} {WordRam.GetDebugState()}");
                break;
            case 0xA12006:
                Registers.HInterruptVector = (ushort)((Registers.HInterruptVector & 0x00FF) | (value << 8));
                break;
            case 0xA12007:
                Registers.HInterruptVector = (ushort)((Registers.HInterruptVector & 0xFF00) | value);
                break;
            case 0xA12008:
            case 0xA12009:
                Cdc.WriteHostData(ScdCpu.Main);
                break;
            case 0xA1200E:
                Registers.MainCpuCommunicationFlags = value;
                if (LogCommFlags)
                    Console.WriteLine($"[SCD-COMM] MAIN W8 0x{address:X6} = 0x{value:X2}");
                break;
            case 0xA1200F:
                // Hardware-compatible behavior: both byte addresses update main CPU flags.
                Registers.MainCpuCommunicationFlags = value;
                if (LogCommFlags)
                    Console.WriteLine($"[SCD-COMM] MAIN W8 0x{address:X6} = 0x{value:X2}");
                break;
            case >= 0xA12010 and <= 0xA1201F:
                WriteCommBuffer(Registers.CommunicationCommands, address, value);
                break;
        }
    }

    private void WriteMainRegisterWord(uint address, ushort value)
    {
        if (LogMainRegProbe && _mainRegProbeRemaining > 0)
        {
            _mainRegProbeRemaining--;
            Console.WriteLine($"[SCD-MAINREG-PROBE] W16 0x{address:X6} = 0x{value:X4}");
        }
        if (LogMainRegs && address is >= 0xA12000 and <= 0xA1202F)
            Console.WriteLine($"[SCD-MAINREG] W16 0x{address:X6} = 0x{value:X4}");
        switch (address)
        {
            case 0xA12000:
            case 0xA12002:
                WriteMainRegisterByte(address, (byte)(value >> 8));
                WriteMainRegisterByte(address | 1, (byte)value);
                break;
            case 0xA12004:
                // CDC mode (high byte) + CDC register address (low byte)
                // Mirrors hardware/jgenesis register pairing.
                WriteMainRegisterByte(address, (byte)(value >> 8));
                WriteMainRegisterByte(address | 1, (byte)value);
                break;
            case 0xA12006:
                Registers.HInterruptVector = value;
                break;
            case 0xA12008:
                Cdc.WriteHostData(ScdCpu.Main);
                break;
            case 0xA1200E:
                // Only main CPU flags are writable by main CPU word access.
                Registers.MainCpuCommunicationFlags = (byte)(value >> 8);
                break;
            case >= 0xA12010 and <= 0xA1201F:
                WriteCommWord(Registers.CommunicationCommands, address, value);
                break;
        }
    }

    private static byte ReadCommBuffer(ushort[] buffer, uint address)
    {
        int idx = (int)((address & 0xF) >> 1);
        ushort word = buffer[idx];
        return (address & 1) != 0 ? (byte)(word & 0xFF) : (byte)(word >> 8);
    }

    private static ushort ReadCommWord(ushort[] buffer, uint address)
    {
        int idx = (int)((address & 0xF) >> 1);
        return buffer[idx];
    }

    private static void WriteCommBuffer(ushort[] buffer, uint address, byte value)
    {
        int idx = (int)((address & 0xF) >> 1);
        ushort current = buffer[idx];
        if ((address & 1) != 0)
            buffer[idx] = (ushort)((current & 0xFF00) | value);
        else
            buffer[idx] = (ushort)((current & 0x00FF) | (value << 8));
    }

    private static void WriteCommWord(ushort[] buffer, uint address, ushort value)
    {
        int idx = (int)((address & 0xF) >> 1);
        buffer[idx] = value;
    }

    private byte ReadSubRegisterByte(uint address)
    {
        uint reg = address & SubRegisterAddressMask;
        LogFirstSubRegAccess(reg, isWrite: false);
        if (TraceCddRegAccess && (reg >= 0x0038 && reg <= 0x004B))
        {
            uint pc = SubPcProvider?.Invoke() ?? 0;
            Console.WriteLine($"[SCD-CDD-REG] R8 reg=0x{reg:X4} pc=0x{pc:X6}");
        }
        if (LogSubReads && reg is 0x0001 or 0x0002 or 0x0003 or 0x0004 or 0x0005 or 0x0007 or 0x0008 or 0x0009
            or 0x000E or 0x000F or (>= 0x0010 and <= 0x002F)
            or 0x0036 or 0x0037 or (>= 0x0038 and <= 0x004B))
        {
            byte val = reg switch
            {
                0x0001 => 0x01,
                0x0002 => Registers.PrgRamWriteProtect,
                0x0003 => (byte)(WordRam.ReadControl() | ((byte)WordRam.PriorityMode << 3)),
                0x0004 => (byte)(((Cdc.EndOfDataTransfer ? 1 : 0) << 7) | ((Cdc.DataReady ? 1 : 0) << 6) | Cdc.DeviceDestinationBits),
                0x0005 => Cdc.RegisterAddress,
                0x0007 => Cdc.ReadRegister(),
                0x0008 => (byte)(Cdc.ReadHostData(ScdCpu.Sub) >> 8),
                0x0009 => (byte)(Cdc.ReadHostData(ScdCpu.Sub) & 0xFF),
                0x000E => Registers.MainCpuCommunicationFlags,
                0x000F => Registers.SubCpuCommunicationFlags,
                >= 0x0010 and <= 0x001F => ReadCommBuffer(Registers.CommunicationCommands, reg),
                >= 0x0020 and <= 0x002F => ReadCommBuffer(Registers.CommunicationStatuses, reg),
                0x0036 => (byte)(Cdd.PlayingAudio ? 0 : 1),
                0x0037 => (byte)((Registers.CddHostClockOn ? 1 : 0) << 2),
                >= 0x0038 and <= 0x0041 => Cdd.Status[(reg - 8) & 0x0F],
                >= 0x0042 and <= 0x004B => Registers.CddCommand[(reg - 2) & 0x0F],
                _ => 0x00
            };
            Console.WriteLine($"[SCD-SUBREG] R8 0x{reg:X4} -> 0x{val:X2}");
        }
        switch (reg)
        {
            case 0x0000:
                return (byte)(((Registers.LedGreen ? 1 : 0) << 1) | (Registers.LedRed ? 1 : 0));
            case 0x0001:
                byte res = 0x01;
                if (_subRegProbeRemaining > 0)
                {
                    _subRegProbeRemaining--;
                    Console.WriteLine($"[COMM-LOG] SUB R 0x8001 = 0x{res:X2}");
                }
                return res;
            case 0x0002:
                return Registers.PrgRamWriteProtect;
            case 0x0003:
                return (byte)(WordRam.ReadControl() | ((byte)WordRam.PriorityMode << 3));
            case 0x0004:
                return (byte)(((Cdc.EndOfDataTransfer ? 1 : 0) << 7) | ((Cdc.DataReady ? 1 : 0) << 6) | Cdc.DeviceDestinationBits);
            case 0x0005:
                return Cdc.RegisterAddress;
            case 0x0007:
                return Cdc.ReadRegister();
            case 0x0008:
                return (byte)(Cdc.ReadHostData(ScdCpu.Sub) >> 8);
            case 0x0009:
                return (byte)(Cdc.ReadHostData(ScdCpu.Sub) & 0xFF);
            case 0x000C:
                return (byte)(Registers.StopwatchCounter >> 8);
            case 0x000D:
                return (byte)(Registers.StopwatchCounter & 0xFF);
            case 0x000E:
                {
                    byte value = Registers.MainCpuCommunicationFlags;
                    if (LogCommFlags)
                        Console.WriteLine($"[SCD-COMM] SUB R8 0x{reg:X4} -> 0x{value:X2}");
                    return value;
                }
            case 0x000F:
                {
                    byte value = Registers.SubCpuCommunicationFlags;
                    if (LogCommFlags)
                        Console.WriteLine($"[SCD-COMM] SUB R8 0x{reg:X4} -> 0x{value:X2}");
                    return value;
                }
            case >= 0x0010 and <= 0x001F:
                return ReadCommBuffer(Registers.CommunicationCommands, reg);
            case >= 0x0020 and <= 0x002F:
                return ReadCommBuffer(Registers.CommunicationStatuses, reg);
            case 0x0031:
                return Registers.TimerInterval;
            case 0x0033:
                return (byte)(
                    ((Registers.SubcodeInterruptEnabled ? 1 : 0) << 6)
                    | ((Registers.CdcInterruptEnabled ? 1 : 0) << 5)
                    | ((Registers.CddInterruptEnabled ? 1 : 0) << 4)
                    | ((Registers.TimerInterruptEnabled ? 1 : 0) << 3)
                    | ((Registers.SoftwareInterruptEnabled ? 1 : 0) << 2)
                    | ((Registers.GraphicsInterruptEnabled ? 1 : 0) << 1));
            case 0x0036:
                {
                    byte value = (byte)(Cdd.PlayingAudio ? 0 : 1);
                    if (LogCddStatus && _cddStatusLogRemaining-- > 0)
                        Console.WriteLine($"[SCD-CDD-STATUS] R8 0x0036 -> 0x{value:X2}");
                    return value;
                }
            case 0x0037:
                {
                    byte value = (byte)((Registers.CddHostClockOn ? 1 : 0) << 2);
                    if (LogCddStatus && _cddStatusLogRemaining-- > 0)
                        Console.WriteLine($"[SCD-CDD-STATUS] R8 0x0037 -> 0x{value:X2}");
                    return value;
                }
            case >= 0x0038 and <= 0x0041:
                {
                    byte value = Cdd.Status[(reg - 8) & 0x0F];
                    if (LogCddStatus && _cddStatusLogRemaining-- > 0)
                        Console.WriteLine($"[SCD-CDD-STATUS] R8 0x{reg:X4} -> 0x{value:X2}");
                    return value;
                }
            case >= 0x0042 and <= 0x004B:
                return Registers.CddCommand[(reg - 2) & 0x0F];
            case 0x004C:
                return FontRegisters.ReadColor();
            case 0x004E:
                return (byte)(FontRegisters.FontBits >> 8);
            case 0x004F:
                return (byte)(FontRegisters.FontBits & 0xFF);
            case >= 0x0050 and <= 0x0057:
                {
                    ushort word = FontRegisters.ReadFontData(reg);
                    return (reg & 1) != 0 ? (byte)(word & 0xFF) : (byte)(word >> 8);
                }
            case >= 0x0058 and <= 0x0067:
                return Graphics.ReadRegisterByte(reg);
            default:
                return 0x00;
        }
    }

    private ushort ReadSubRegisterWord(uint address)
    {
        uint reg = address & SubRegisterAddressMask;
        LogFirstSubRegAccess(reg, isWrite: false);
        if (LogSubReads && reg is 0x0001 or 0x0008 or 0x000A or 0x000C or 0x0036 or 0x0038 or 0x0042)
        {
            ushort val = reg switch
            {
                0x0008 => Cdc.ReadHostData(ScdCpu.Sub),
                0x000A => (ushort)Cdc.DmaAddress,
                0x000C => (ushort)(Registers.StopwatchCounter & 0x0FFF),
                0x0036 => (ushort)((ReadSubRegisterByte(reg) << 8) | ReadSubRegisterByte(reg | 1)),
                >= 0x0038 and <= 0x0041 => (ushort)((Cdd.Status[(reg - 8) & 0x0F] << 8) | Cdd.Status[(reg - 7) & 0x0F]),
                >= 0x0042 and <= 0x004B => (ushort)((Registers.CddCommand[(reg - 2) & 0x0F] << 8) | Registers.CddCommand[(reg - 1) & 0x0F]),
                _ => 0x0000
            };
            Console.WriteLine($"[SCD-SUBREG] R16 0x{reg:X4} -> 0x{val:X4}");
        }
        switch (reg)
        {
            case 0x0000:
            case 0x0002:
            case 0x0004:
            case 0x0036:
                return (ushort)((ReadSubRegisterByte(reg) << 8) | ReadSubRegisterByte(reg | 1));
            case 0x0006:
                return ReadSubRegisterByte(reg | 1);
            case 0x0008:
                return Cdc.ReadHostData(ScdCpu.Sub);
            case 0x000A:
                return (ushort)(Cdc.DmaAddress >> 3);
            case 0x000C:
                return Registers.StopwatchCounter;
            case 0x000E:
                return (ushort)((Registers.MainCpuCommunicationFlags << 8) | Registers.SubCpuCommunicationFlags);
            case >= 0x0010 and <= 0x001F:
                return ReadCommWord(Registers.CommunicationCommands, reg);
            case >= 0x0020 and <= 0x002F:
                return ReadCommWord(Registers.CommunicationStatuses, reg);
            case 0x0030:
                return Registers.TimerInterval;
            case 0x0032:
                return ReadSubRegisterByte(reg | 1);
            case >= 0x0038 and <= 0x0041:
                {
                    int rel = (int)((reg - 8) & 0x0F);
                    ushort value = (ushort)((Cdd.Status[rel] << 8) | Cdd.Status[(rel + 1) & 0x0F]);
                    if (LogCddStatus && _cddStatusLogRemaining-- > 0)
                        Console.WriteLine($"[SCD-CDD-STATUS] R16 0x{reg:X4} -> 0x{value:X4}");
                    return value;
                }
            case >= 0x0042 and <= 0x004B:
                {
                    int rel = (int)((reg - 2) & 0x0F);
                    return (ushort)((Registers.CddCommand[rel] << 8) | Registers.CddCommand[(rel + 1) & 0x0F]);
                }
            case 0x004C:
                return FontRegisters.ReadColor();
            case 0x004E:
                return FontRegisters.FontBits;
            case >= 0x0050 and <= 0x0057:
                return FontRegisters.ReadFontData(reg);
            case >= 0x0058 and <= 0x0067:
                return Graphics.ReadRegisterWord(reg);
            default:
                return 0x0000;
        }
    }

    private void WriteSubRegisterByte(uint address, byte value)
    {
        uint reg = address & SubRegisterAddressMask;
        LogFirstSubRegAccess(reg, isWrite: true);
        if (LogSubCdc && reg == 0x0004 && !_subCdcDestLogged)
        {
            _subCdcDestLogged = true;
            Console.Error.WriteLine($"[SCD-SUB-CDC] W 0x0004 = 0x{value:X2}");
        }
        if (LogSubCdc && reg == 0x0005 && !_subCdcRegAddrLogged)
        {
            _subCdcRegAddrLogged = true;
            Console.Error.WriteLine($"[SCD-SUB-CDC] W 0x0005 = 0x{value:X2}");
        }
        if (LogSubCdc && reg == 0x0007 && !_subCdcRegDataLogged)
        {
            _subCdcRegDataLogged = true;
            Console.Error.WriteLine($"[SCD-SUB-CDC] W 0x0007 = 0x{value:X2}");
        }
        if (LogSubCdc && reg is 0x0004 or 0x0005 or 0x0007 or 0x0008 or 0x0009 or 0x000A or 0x000B)
        {
            Console.WriteLine($"[SCD-SUB-CDC] W 0x{reg:X4} = 0x{value:X2}");
        }
        if (LogSubRegs
            && reg is 0x0001 or 0x0004 or 0x0005 or 0x0007 or 0x0030 or 0x0031 or 0x0033 or 0x0034 or 0x0035 or 0x0037
                or >= 0x0042 and <= 0x004B)
        {
            Console.WriteLine($"[SCD-SUBREG-W] 0x{reg:X4} = 0x{value:X2}");
        }
        if (LogSubComm && reg is 0x000E or 0x000F)
            Console.WriteLine($"[SCD-SUBREG] W8 0x{reg:X4} = 0x{value:X2}");
        switch (reg)
        {
            case 0x0000:
                Registers.LedGreen = (value & 0x02) != 0;
                Registers.LedRed = (value & 0x01) != 0;
                break;
            case 0x0001:
                if (LogCddReset && _cddResetLogRemaining-- > 0)
                    Console.WriteLine($"[SCD-CDD-RESET] W8 0x0001 = 0x{value:X2}");
                {
                    bool lineHigh = (value & 0x01) != 0;
                    if (_cddResetLineHigh && !lineHigh)
                    {
                        if (LogCddResetEdge)
                            Console.WriteLine("[SCD-CDD-RESET-EDGE] 1->0");
                        Cdd.Reset();
                    }
                    _cddResetLineHigh = lineHigh;
                }
                break;
            case 0x0002:
            case 0x0003:
                WordRam.SubCpuWriteControl(value);
                break;
            case 0x0004:
                Cdc.SetDeviceDestination((byte)(value & 0x07));
                break;
            case 0x0005:
                Cdc.SetRegisterAddress((byte)(value & 0x1F));
                break;
            case 0x0007:
                Cdc.WriteRegister(value);
                break;
            case 0x0008:
            case 0x0009:
                Cdc.WriteHostData(ScdCpu.Sub);
                break;
            case 0x000A:
            case 0x000B:
                Cdc.SetDmaAddress((uint)((value << 8) | value) << 3);
                break;
            case 0x000C:
            case 0x000D:
                Registers.StopwatchCounter = (ushort)(((value << 8) | value) & 0x0FFF);
                break;
            case 0x000E:
            case 0x000F:
                // Hardware-compatible behavior: both byte addresses update sub CPU flags.
                if (LogCommFlags && Registers.SubCpuCommunicationFlags != value)
                    Console.WriteLine(
                        $"[COMM-DEBUG] SUB  W 0x800F = 0x{value:X2} prev=0x{Registers.SubCpuCommunicationFlags:X2} " +
                        $"PC=0x{SubPcProvider?.Invoke():X6}");
                AppendTraceLine(
                    $"[SCD-IRQ-FILE] SUB COMM8 reg=0x{reg:X4} val=0x{value:X2} prev=0x{Registers.SubCpuCommunicationFlags:X2} " +
                    $"pc=0x{SubPcProvider?.Invoke():X6}");
                Registers.SubCpuCommunicationFlags = value;
                if (LogCommFlags)
                    Console.WriteLine($"[SCD-COMM] SUB W8 0x{reg:X4} = 0x{value:X2}");
                break;
            case >= 0x0020 and <= 0x002F:
                if (LogSubComm)
                    Console.WriteLine($"[SCD-SUBREG] W8 STATUS 0x{reg:X4} = 0x{value:X2} PC=0x{SubPcProvider?.Invoke():X6}");
                AppendTraceLine(
                    $"[SCD-IRQ-FILE] SUB STATUS8 reg=0x{reg:X4} val=0x{value:X2} pc=0x{SubPcProvider?.Invoke():X6}");
                WriteCommBuffer(Registers.CommunicationStatuses, reg, value);
                break;
            case 0x0030:
            case 0x0031:
                Registers.TimerInterval = value;
                Registers.TimerCounter = value;
                break;
            case 0x0033:
                Registers.SubcodeInterruptEnabled = (value & 0x40) != 0;
                Registers.CdcInterruptEnabled = (value & 0x20) != 0;
                Registers.CddInterruptEnabled = (value & 0x10) != 0;
                Registers.TimerInterruptEnabled = (value & 0x08) != 0;
                Registers.SoftwareInterruptEnabled = (value & 0x04) != 0;
                Registers.GraphicsInterruptEnabled = (value & 0x02) != 0;
                AppendTraceLine(
                    $"[SCD-IRQ-FILE] SUB INTMASK=0x{value:X2} cdc={(Registers.CdcInterruptEnabled ? 1 : 0)} " +
                    $"cdd={(Registers.CddInterruptEnabled ? 1 : 0)} timer={(Registers.TimerInterruptEnabled ? 1 : 0)} " +
                    $"sw={(Registers.SoftwareInterruptEnabled ? 1 : 0)} gfx={(Registers.GraphicsInterruptEnabled ? 1 : 0)} " +
                    $"pc=0x{SubPcProvider?.Invoke():X6}");
                if (!Registers.GraphicsInterruptEnabled)
                    Graphics.AcknowledgeInterrupt();
                if (LogSubInt)
                {
                    Console.WriteLine(
                        $"[SCD-SUBREG] INTMASK cdd={(Registers.CddInterruptEnabled ? 1 : 0)} " +
                        $"cdc={(Registers.CdcInterruptEnabled ? 1 : 0)} sw={(Registers.SoftwareInterruptEnabled ? 1 : 0)}");
                }
                if (TraceSubIntWriterPc)
                {
                    uint pc = SubPcProvider?.Invoke() ?? 0;
                    Console.WriteLine($"[SCD-SUBREG-PC] W8 0x0033 pc=0x{pc:X6} val=0x{value:X2}");
                }
                break;
            case 0x0034:
            case 0x0035:
                Cdd.SetFaderVolume((ushort)((value << 8) | value));
                break;
            case 0x0037:
                Registers.CddHostClockOn = (value & 0x04) != 0;
                if (LogSubInt)
                    Console.WriteLine($"[SCD-SUBREG] CDD host clock={(Registers.CddHostClockOn ? 1 : 0)}");
                if (TraceSubIntWriterPc)
                {
                    uint pc = SubPcProvider?.Invoke() ?? 0;
                    Console.WriteLine($"[SCD-SUBREG-PC] W8 0x0037 pc=0x{pc:X6} val=0x{value:X2}");
                }
                break;
            case >= 0x0042 and <= 0x004B:
                {
                    int rel = (int)((reg - 2) & 0x0F);
                    Registers.CddCommand[rel] = (byte)(value & 0x0F);
                    if (LogCddCmd)
                        Console.WriteLine($"[SCD-CDD-CMDW] W8 0x{reg:X4} = 0x{value:X2} rel={rel}");
                    if (reg == 0x004B)
                    {
                        if (LogCddCmd)
                            Console.WriteLine($"[SCD-CDD-CMDSEND] CMD={string.Join(" ", Registers.CddCommand)}");
                        Cdd.SendCommand(Registers.CddCommand);
                    }
                    break;
                }
            case 0x004C:
            case 0x004D:
                FontRegisters.WriteColor(value);
                break;
            case 0x004E:
                FontRegisters.WriteFontBitsMsb(value);
                break;
            case 0x004F:
                FontRegisters.WriteFontBitsLsb(value);
                break;
            case >= 0x0058 and <= 0x0067:
                if (LogSubRegs)
                    Console.WriteLine($"[SCD-SUBREG] W8 0x{reg:X4} = 0x{value:X2}");
                Graphics.WriteRegisterByte(reg, value);
                break;
        }
    }

    private void WriteSubRegisterWord(uint address, ushort value)
    {
        uint reg = address & SubRegisterAddressMask;
        LogFirstSubRegAccess(reg, isWrite: true);
        if (LogSubRegs
            && reg is 0x0004 or 0x0008 or 0x000A or 0x0030 or 0x0032 or 0x0034 or 0x0036
                or >= 0x0042 and <= 0x004B)
        {
            Console.WriteLine($"[SCD-SUBREG-W] 0x{reg:X4} = 0x{value:X4}");
        }
        if (LogSubComm && reg == 0x000E)
            Console.WriteLine($"[SCD-SUBREG] W16 0x{reg:X4} = 0x{value:X4}");
        switch (reg)
        {
            case 0x0000:
                WriteSubRegisterByte(reg, (byte)(value >> 8));
                WriteSubRegisterByte(reg | 1, (byte)value);
                break;
            case 0x0002:
                WriteSubRegisterByte(reg | 1, (byte)value);
                break;
            case 0x0004:
                WriteSubRegisterByte(reg, (byte)(value >> 8));
                WriteSubRegisterByte(reg | 1, (byte)value);
                break;
            case 0x0006:
                WriteSubRegisterByte(reg | 1, (byte)value);
                break;
            case 0x0008:
                Cdc.WriteHostData(ScdCpu.Sub);
                break;
            case 0x000A:
                Cdc.SetDmaAddress((uint)value << 3);
                break;
            case 0x000C:
                Registers.StopwatchCounter = (ushort)(value & 0x0FFF);
                break;
            case 0x000E:
                // Only sub CPU flags are writable by sub CPU word access.
                AppendTraceLine(
                    $"[SCD-IRQ-FILE] SUB COMM16 reg=0x{reg:X4} val=0x{value:X4} prev=0x{Registers.SubCpuCommunicationFlags:X2} " +
                    $"pc=0x{SubPcProvider?.Invoke():X6}");
                Registers.SubCpuCommunicationFlags = (byte)value;
                break;
            case >= 0x0020 and <= 0x002F:
                if (LogSubComm)
                    Console.WriteLine($"[SCD-SUBREG] W16 STATUS 0x{reg:X4} = 0x{value:X4} PC=0x{SubPcProvider?.Invoke():X6}");
                AppendTraceLine(
                    $"[SCD-IRQ-FILE] SUB STATUS16 reg=0x{reg:X4} val=0x{value:X4} pc=0x{SubPcProvider?.Invoke():X6}");
                WriteCommWord(Registers.CommunicationStatuses, reg, value);
                break;
            case 0x0030:
                Registers.TimerInterval = (byte)value;
                Registers.TimerCounter = (byte)value;
                break;
            case 0x0032:
                WriteSubRegisterByte(reg | 1, (byte)value);
                break;
            case 0x0034:
                Cdd.SetFaderVolume((ushort)((value >> 4) & 0x07FF));
                break;
            case 0x0036:
                WriteSubRegisterByte(reg | 1, (byte)value);
                break;
            case >= 0x0042 and <= 0x004B:
                {
                    int rel = (int)((reg - 2) & 0x0F);
                    Registers.CddCommand[rel] = (byte)((value >> 8) & 0x0F);
                    Registers.CddCommand[(rel + 1) & 0x0F] = (byte)(value & 0x0F);
                    if (LogCddCmd)
                        Console.WriteLine($"[SCD-CDD-CMDW] W16 0x{reg:X4} = 0x{value:X4} rel={rel}");
                    if (reg == 0x004A)
                    {
                        if (LogCddCmd)
                            Console.WriteLine($"[SCD-CDD-CMDSEND] CMD={string.Join(" ", Registers.CddCommand)}");
                        Cdd.SendCommand(Registers.CddCommand);
                    }
                    break;
                }
            case 0x004C:
                FontRegisters.WriteColor((byte)value);
                break;
            case 0x004E:
                FontRegisters.WriteFontBits(value);
                break;
            case >= 0x0050 and <= 0x0057:
                // Read-only font data registers
                break;
            case >= 0x0058 and <= 0x0067:
                if (LogSubRegs)
                    Console.WriteLine($"[SCD-SUBREG] W16 0x{reg:X4} = 0x{value:X4}");
                Graphics.WriteRegisterWord(reg, value);
                break;
        }
    }

    public byte ReadSubByte(uint address)
    {
        uint addr = address & SubBusAddressMask;
        switch (addr)
        {
            case <= 0x07FFFF:
                {
                    byte val = _prgRam[addr];
                    if (LogPrgFlag && addr == PrgFlagAddr)
                        Console.WriteLine($"[SCD-PRG-FLAG] R8 SUB addr=0x{addr:X6} val=0x{val:X2}");
                    return val;
                }
            case <= 0x0DFFFF:
                return WordRam.SubCpuReadRam(addr);
            case <= 0x0EFFFF:
                if ((addr & 1) != 0)
                {
                    int backupAddr = (int)((addr & 0x3FFF) >> 1);
                    return _backupRam[backupAddr];
                }
                return 0x00;
            case <= 0x0F7FFF:
                return (addr & 1) != 0 ? Pcm.Read((addr & 0x3FFF) >> 1) : (byte)0x00;
            case <= 0x0FFFFF:
                {
                    byte val = ReadSubRegisterByte(addr);
                    if (_subRegProbeRemaining > 0)
                    {
                        _subRegProbeRemaining--;
                        Console.WriteLine($"[SCD-SUBREG-PROBE] R8 0x{address:X6} -> 0x{val:X2}");
                    }
                    LogSubBusLine($"[SCD-SUBBUS] R8 0x{addr:X6} -> 0x{val:X2}");
                    return val;
                }
            default:
                return 0x00;
        }
    }

    public ushort ReadSubWord(uint address)
    {
        uint addr = address & SubBusAddressMask;
        switch (addr)
        {
            case <= 0x07FFFF:
                {
                    ushort val = (ushort)((_prgRam[addr] << 8) | _prgRam[addr + 1]);
                    if (LogPrgRamWatch && addr >= 0x0180 && addr <= 0x0190)
                        Console.WriteLine($"[SCD-PRG] R16 SUB addr=0x{addr:X6} val=0x{val:X4}");
                    if (LogPrgFlag && addr == PrgFlagAddr)
                        Console.WriteLine($"[SCD-PRG-FLAG] R16 SUB addr=0x{addr:X6} val=0x{val:X4}");
                    return val;
                }
            case <= 0x0DFFFF:
                {
                    byte msb = WordRam.SubCpuReadRam(addr);
                    byte lsb = WordRam.SubCpuReadRam(addr | 1);
                    return (ushort)((msb << 8) | lsb);
                }
            case <= 0x0EFFFF:
                {
                    // Backup RAM: $FE0000-$FEFFFF (masked to 0x0E0000-0x0EFFFF)
                    int backupAddr = (int)((addr & 0x3FFF) >> 1);
                    return _backupRam[backupAddr];
                }
            case <= 0x0F7FFF:
                // PCM: $FF0000-$FF7FFF (masked to 0x0F0000-0x0F7FFF)
                return Pcm.Read((addr & 0x3FFF) >> 1);
            case <= 0x0FFFFF:
                {
                    ushort val = ReadSubRegisterWord(addr);
                    if (_subRegProbeRemaining > 0)
                    {
                        _subRegProbeRemaining--;
                        Console.WriteLine($"[SCD-SUBREG-PROBE] R16 0x{address:X6} -> 0x{val:X4}");
                    }
                    LogSubBusLine($"[SCD-SUBBUS] R16 0x{addr:X6} -> 0x{val:X4}");
                    return val;
                }
            default:
                return 0x0000;
        }
    }

    public void WriteSubByte(uint address, byte value)
    {
        uint addr = address & SubBusAddressMask;
        switch (addr)
        {
            case <= 0x07FFFF:
                WritePrgRam(addr, value, ScdCpu.Sub);
                break;
            case <= 0x0DFFFF:
                WordRam.SubCpuWriteRam(addr, value);
                break;
            case <= 0x0EFFFF:
                if ((addr & 1) != 0)
                {
                    int backupAddr = (int)((addr & 0x3FFF) >> 1);
                    _backupRam[backupAddr] = value;
                    _backupRamDirty = true;
                }
                break;
            case <= 0x0F7FFF:
                if ((addr & 1) != 0)
                    Pcm.Write((addr & 0x3FFF) >> 1, value);
                break;
            case <= 0x0FFFFF:
                if (_subRegProbeRemaining > 0)
                {
                    _subRegProbeRemaining--;
                    Console.WriteLine($"[SCD-SUBREG-PROBE] W8 0x{address:X6} = 0x{value:X2}");
                }
                LogSubBusLine($"[SCD-SUBBUS] W8 0x{addr:X6} = 0x{value:X2}");
                if ((addr & SubRegisterAddressMask) == 0x0003)
                    BufferSubWrite(BufferedWriteKind.Byte, addr, value);
                else
                    WriteSubRegisterByte(addr, value);
                break;
        }
    }

    public void WriteSubWord(uint address, ushort value)
    {
        uint addr = address & SubBusAddressMask;
        switch (addr)
        {
            case <= 0x07FFFF:
                WritePrgRam(addr, (byte)(value >> 8), ScdCpu.Sub);
                WritePrgRam(addr + 1, (byte)value, ScdCpu.Sub);
                break;
            case <= 0x0DFFFF:
                WordRam.SubCpuWriteRam(addr, (byte)(value >> 8));
                WordRam.SubCpuWriteRam(addr | 1, (byte)value);
                break;
            case <= 0x0EFFFF:
                {
                    int backupAddr = (int)((addr & 0x3FFF) >> 1);
                    _backupRam[backupAddr] = (byte)value;
                    _backupRamDirty = true;
                    break;
                }
            case <= 0x0F7FFF:
                Pcm.Write((addr & 0x3FFF) >> 1, (byte)value);
                break;
            case <= 0x0FFFFF:
                if (_subRegProbeRemaining > 0)
                {
                    _subRegProbeRemaining--;
                    Console.WriteLine($"[SCD-SUBREG-PROBE] W16 0x{address:X6} = 0x{value:X4}");
                }
                LogSubBusLine($"[SCD-SUBBUS] W16 0x{addr:X6} = 0x{value:X4}");
                if ((addr & SubRegisterAddressMask) == 0x0002)
                    BufferSubWrite(BufferedWriteKind.Word, addr, value);
                else
                    WriteSubRegisterWord(addr, value);
                break;
        }
    }

    private void BufferSubWrite(BufferedWriteKind kind, uint address, ushort value)
    {
        if (_bufferedSubWriteCount >= _bufferedSubWrites.Length)
            return;
        _bufferedSubWrites[_bufferedSubWriteCount++] = new BufferedWrite(kind, address, value);
    }

    private void BufferMainWrite(BufferedWriteKind kind, uint address, ushort value)
    {
        if (_bufferedMainWriteCount >= _bufferedMainWrites.Length)
        {
            FlushBufferedMainWrites();
            if (_bufferedMainWriteCount >= _bufferedMainWrites.Length)
                return;
        }

        _bufferedMainWrites[_bufferedMainWriteCount++] = new BufferedWrite(kind, address, value);
    }

    private void LogFirstSubRegAccess(uint reg, bool isWrite)
    {
        if (_subRegAccessLogged || !LogSubRegs)
            return;
        _subRegAccessLogged = true;
        Console.WriteLine($"[SCD-SUBREG-FIRST] {(isWrite ? "W" : "R")} 0x{reg:X4}");
    }

    public byte GetSubInterruptLevel()
    {
        byte level;
        if (Registers.CdcInterruptEnabled && Cdc.InterruptPending)
        {
            level = 5;
        }
        else if (Registers.CddInterruptEnabled && Registers.CddHostClockOn && Cdd.InterruptPending)
        {
            level = 4;
        }
        else if (Registers.TimerInterruptEnabled && Registers.TimerInterruptPending)
        {
            level = 3;
        }
        else if (Registers.SoftwareInterruptEnabled && Registers.SubSoftwareInterruptPending)
        {
            level = 2;
        }
        else if (Registers.GraphicsInterruptEnabled && Graphics.InterruptPending)
        {
            level = 1;
        }
        else
        {
            level = 0;
        }

        if (TraceIrqFile != null && _lastLoggedSubInterruptLevel != level)
        {
            _lastLoggedSubInterruptLevel = level;
            AppendTraceLine(
                $"[SCD-IRQ-FILE] SUB IRQ level={level} swPend={(Registers.SubSoftwareInterruptPending ? 1 : 0)} " +
                $"swEn={(Registers.SoftwareInterruptEnabled ? 1 : 0)} timerPend={(Registers.TimerInterruptPending ? 1 : 0)} " +
                $"timerEn={(Registers.TimerInterruptEnabled ? 1 : 0)} cddPend={(Cdd.InterruptPending ? 1 : 0)} " +
                $"cddEn={(Registers.CddInterruptEnabled ? 1 : 0)} hostClk={(Registers.CddHostClockOn ? 1 : 0)} " +
                $"cdcPend={(Cdc.InterruptPending ? 1 : 0)} cdcEn={(Registers.CdcInterruptEnabled ? 1 : 0)} " +
                $"gfxPend={(Graphics.InterruptPending ? 1 : 0)} gfxEn={(Registers.GraphicsInterruptEnabled ? 1 : 0)} " +
                $"sub_pc=0x{SubPcProvider?.Invoke():X6}");
        }

        return level;
    }

    public void AcknowledgeSubInterrupt(byte level)
    {
        if (LogSubInt)
            Console.WriteLine($"[SCD-SUBINT] ack level={level} pc=0x{SubPcProvider?.Invoke():X6}");
        AppendTraceLine($"[SCD-IRQ-FILE] SUB ACK level={level} pc=0x{SubPcProvider?.Invoke():X6}");
        if (LogSubInt && level == 4 && !_subAckCddLogged)
        {
            _subAckCddLogged = true;
            Console.Error.WriteLine("[SCD-SUBINT-ACK4] first CDD ack");
        }
        if ((uint)level < (uint)_subAckCounts.Length)
            _subAckCounts[level]++;
        switch (level)
        {
            case 1:
                Graphics.AcknowledgeInterrupt();
                break;
            case 2:
                if (LogSubInt)
                    Console.WriteLine($"[SCD-SUBINT-ACK2] pc=0x{SubPcProvider?.Invoke():X6}");
                Registers.SubSoftwareInterruptPending = false;
                break;
            case 3:
                Registers.TimerInterruptPending = false;
                break;
            case 4:
                Cdd.AcknowledgeInterrupt();
                break;
            case 5:
                Cdc.AcknowledgeInterrupt();
                break;
            case 6:
                Cdd.AcknowledgeSubcodeInterrupt();
                break;
        }
    }

    private static void AppendTraceLine(string line)
    {
        if (string.IsNullOrWhiteSpace(TraceIrqFile))
            return;

        try
        {
            File.AppendAllText(TraceIrqFile, line + Environment.NewLine);
        }
        catch
        {
            // Ignore trace write failures to avoid altering emulation behavior.
        }
    }

    public void ConsumeSubAckCounts(out long level1, out long level2, out long level3, out long level4, out long level5, out long level6)
    {
        level1 = Interlocked.Exchange(ref _subAckCounts[1], 0);
        level2 = Interlocked.Exchange(ref _subAckCounts[2], 0);
        level3 = Interlocked.Exchange(ref _subAckCounts[3], 0);
        level4 = Interlocked.Exchange(ref _subAckCounts[4], 0);
        level5 = Interlocked.Exchange(ref _subAckCounts[5], 0);
        level6 = Interlocked.Exchange(ref _subAckCounts[6], 0);
    }

    public bool SubCpuHalt => Registers.SubCpuBusReq;
    public bool SubCpuReset => Registers.SubCpuReset;
}
