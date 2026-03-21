using System;
using System.Collections.Generic;
using System.Globalization;
using KSNES.Tracing;

namespace KSNES.ROM;

public class ROM : IROM
{
    private enum TimedCoprocessorDispatch
    {
        None,
        Cx4,
        Dsp1,
        St010,
        St011,
        St018,
        SuperFx,
        Sa1,
        Mixed
    }

    private enum PlainReadDispatch
    {
        None,
        LoRom,
        HiRom,
        ExHiRom
    }

    private enum BasePageKind : byte
    {
        OpenBus,
        RomDirect,
        RomWrapped,
        SramDirect,
        SramWrapped
    }

    private enum FastCartridgeDispatch : byte
    {
        Base,
        Cx4,
        Dsp1,
        SuperFx,
        Sa1,
        Sdd1,
        St010,
        St011,
        St018,
        Obc1,
        Srtc,
        Spc7110,
        Mixed
    }

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    public Header Header { get; private set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
    [NonSerialized]
    private byte[] _data = [];
    private byte[] _sram = [];
    private bool _hasSram;
    private int _banks;
    private int _sramSize;

    [NonSerialized]
    private ISNESSystem? _system;
    [NonSerialized]
    private KSNES.Specialchips.CX4.Cx4? _cx4;
    [NonSerialized]
    private KSNES.Specialchips.DSP1.Dsp1? _dsp1;
    [NonSerialized]
    private KSNES.Specialchips.SuperFX.SuperFx? _superFx;
    [NonSerialized]
    private KSNES.Specialchips.SA1.Sa1? _sa1;
    [NonSerialized]
    private KSNES.Specialchips.SDD1.Sdd1? _sdd1;
    [NonSerialized]
    private KSNES.Specialchips.ST010.St010? _st010;
    [NonSerialized]
    private KSNES.Specialchips.ST011.St011? _st011;
    [NonSerialized]
    private KSNES.Specialchips.ST018.St018? _st018;
    [NonSerialized]
    private KSNES.Specialchips.OBC1.Obc1? _obc1;
    [NonSerialized]
    private KSNES.Specialchips.SRTC.SRtc? _srtc;
    [NonSerialized]
    private KSNES.Specialchips.SPC7110.Spc7110? _spc7110;
    private bool _superFxHasBattery;
    private ulong _superFxOverclock = 1;
    private Dsp1PortMapping _dsp1PortMapping;
    private bool _dsp1IsHiRom;
    private bool _dsp1BroadMap;
    private bool _dsp1SwapPorts;
    private static readonly bool TraceCx4Bus =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_CX4_BUS"), "1", StringComparison.Ordinal);
    private static readonly bool TraceDsp1Bus =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DSP1_BUS"), "1", StringComparison.Ordinal);
    private static readonly bool TraceDsp1NearMiss =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DSP1_NEARMISS"), "1", StringComparison.Ordinal);
    private static readonly int TraceDsp1BusLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_DSP1_BUS_LIMIT", 500);
    private int _traceDsp1BusCount;
    private static readonly bool TraceDsp1ExactReads =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_DSP1_EXACT_READS"), "1", StringComparison.Ordinal);
    private static readonly int TraceDsp1ExactReadLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_DSP1_EXACT_READ_LIMIT", 64);
    private int _traceDsp1ExactReadCount;
    private static readonly bool TraceReadPcWindow =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_READ_PC_WINDOW"), "1", StringComparison.Ordinal);
    private static readonly int TraceReadPcStart = ParseTraceHex("EUTHERDRIVE_TRACE_SNES_READ_PC_START", 0);
    private static readonly int TraceReadPcEnd = ParseTraceHex("EUTHERDRIVE_TRACE_SNES_READ_PC_END", 0);
    private static readonly int TraceReadPcLimit = ParseTraceLimit("EUTHERDRIVE_TRACE_SNES_READ_PC_LIMIT", 200);
    private int _traceReadPcCount;
    private static readonly bool TraceSnesVectors =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_VECTORS"), "1", StringComparison.Ordinal);
    private static readonly bool TraceSuperFxBlockedReads =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SNES_SUPERFX_BLOCKED_READS"), "1", StringComparison.Ordinal);
    private static readonly int TraceSuperFxBlockedReadLimit =
        ParseTraceLimit("EUTHERDRIVE_TRACE_SNES_SUPERFX_BLOCKED_READ_LIMIT", 256);
    private int _traceSuperFxBlockedReadCount;
    private static readonly Dictionary<string, string> s_specialRomOverrides =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_specialRomOverridesLock = new();
    private readonly bool _traceSa1BwramWatch =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_BWRAM_WATCH"), "1", StringComparison.Ordinal);
    [NonSerialized]
    private readonly HashSet<uint> _traceSa1BwramOffsets =
        ParseTraceOffsets(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_SA1_BWRAM_ADDRS"));
    [NonSerialized]
    private TimedCoprocessorDispatch _timedDispatch;
    [NonSerialized]
    private PlainReadDispatch _plainReadDispatch;
    [NonSerialized]
    private bool _romLengthIsPowerOfTwo;
    [NonSerialized]
    private uint _romLengthMask;
    [NonSerialized]
    private byte[] _basePageKind = [];
    [NonSerialized]
    private int[] _basePageBase = [];
    [NonSerialized]
    private FastCartridgeDispatch _fastCartridgeDispatch;
    [NonSerialized]
    private bool _fastCartridgeRequiresSlowPath;

    [NonSerialized]
    private Timer? _sRAMTimer;

    public static void SetSpecialRomOverride(string environmentVariable, string? path)
    {
        if (string.IsNullOrWhiteSpace(environmentVariable))
            return;

        lock (s_specialRomOverridesLock)
        {
            if (string.IsNullOrWhiteSpace(path))
                s_specialRomOverrides.Remove(environmentVariable);
            else
                s_specialRomOverrides[environmentVariable] = path;
        }
    }

    public void LoadROM(byte[] data, Header header)
    {
        _data = data;
        _romLengthIsPowerOfTwo = _data.Length > 0 && (_data.Length & (_data.Length - 1)) == 0;
        _romLengthMask = _data.Length > 0 ? (uint)_data.Length - 1U : 0U;
        Header = header;
        _sram = new byte[header.RamSize];
        _hasSram = header.Chips > 0;
        _banks = header.RomSize / 0x8000;
        _sramSize = header.RamSize;

        bool hasSuperFx = header.MapMode == 0x20 && header.ChipsetByte >= 0x13 && header.ChipsetByte <= 0x1A;
        if (hasSuperFx)
        {
            int ramLen = KSNES.Specialchips.SuperFX.SuperFx.GuessRamLen(_data);
            _sram = new byte[ramLen];
            Array.Fill(_sram, (byte)0xFF);
            _sramSize = _sram.Length;
            _superFxHasBattery = KSNES.Specialchips.SuperFX.SuperFx.HasBattery(_data);
            _hasSram = _superFxHasBattery;
            _superFxOverclock = (ulong)ParseTraceLimit("EUTHERDRIVE_SUPERFX_OVERCLOCK", 1);
            _superFx = new KSNES.Specialchips.SuperFX.SuperFx(_data, _sram, _superFxOverclock);
            Console.WriteLine($"[SuperFX] Initialized successfully. Chipset=0x{header.ChipsetByte:X2} Overclock={_superFxOverclock} SRAM={_sram.Length}.");
        }
        else
        {
            _superFx = null;
            _superFxHasBattery = false;
            _superFxOverclock = 1;
        }

        if (header.ExCoprocessor == 0x10)
        {
            if (_system == null)
                throw new InvalidOperationException("ROM system not set.");
            _cx4 = new KSNES.Specialchips.CX4.Cx4(_system);
            _cx4.Reset();
        }
        else
        {
            _cx4 = null;
        }

        if (IsSa1Chipset(header.ChipsetByte))
        {
            if (_system == null)
                throw new InvalidOperationException("ROM system not set.");
            _sa1 = new KSNES.Specialchips.SA1.Sa1(_data, _sram, _system.IsPal);
            _sram = _sa1.Bwram;
            _sramSize = _sram.Length;
            _hasSram = KSNES.Specialchips.SA1.Sa1.HasBattery(_data, _sramSize);
        }
        else
        {
            _sa1 = null;
        }

        if (IsSdd1Chipset(header.MapMode, header.ChipsetByte))
        {
            _sdd1 = new KSNES.Specialchips.SDD1.Sdd1(_data, _sram);
            _hasSram = _sram.Length > 0;
        }
        else
        {
            _sdd1 = null;
        }

        if (IsSpc7110Chipset(header))
        {
            bool hasRtc = header.ChipsetByte == 0xF9;
            _spc7110 = new KSNES.Specialchips.SPC7110.Spc7110(_data, _sram, hasRtc);
            _sram = _spc7110.Sram;
            _sramSize = _sram.Length;
            _hasSram = _sram.Length > 0;
            Console.WriteLine($"[SPC7110] Initialized successfully. RTC={(hasRtc ? 1 : 0)}");
        }
        else
        {
            _spc7110 = null;
        }

        bool hasDsp =
            _superFx == null &&
            _spc7110 == null &&
            !header.IsExHiRom &&
            header.ExCoprocessor == 0 &&
            IsDspChipset(header.ChipsetByte);
        if (hasDsp)
        {
            if (_system == null)
                throw new InvalidOperationException("ROM system not set.");

            DspVariant dspVariant = GuessDspVariant(data);
            byte[]? dspRom = TryLoadDspRom(dspVariant);
            if (dspRom == null)
            {
                Console.WriteLine($"[{FormatDspVariant(dspVariant)}] ROM not found; {FormatDspVariant(dspVariant)} disabled.");
                _dsp1 = null;
            }
            else
            {
                _dsp1 = new KSNES.Specialchips.DSP1.Dsp1(dspRom, _system);
                _dsp1.Reset();
                Console.WriteLine($"[{FormatDspVariant(dspVariant)}] Initialized successfully.");
            }
            ApplyDspPortConfiguration(dspVariant, header);
        }
        else
        {
            _dsp1 = null;
            _dsp1BroadMap = false;
            _dsp1SwapPorts = false;
        }

        bool isSt01x = IsSt01xChipset(header.ChipsetByte, header.ExCoprocessor);
        bool hasSt010 = false;
        bool hasSt011 = false;
        if (isSt01x)
        {
            if (GuessSt01xVariant(data) == St01xVariant.St011)
                hasSt011 = true;
            else
                hasSt010 = true;
        }

        if (hasSt010)
        {
            if (_system == null)
                throw new InvalidOperationException("ROM system not set.");

            byte[]? st010Rom = TryLoadSt010Rom();
            if (st010Rom == null)
            {
                Console.WriteLine("[ST010] ROM not found; ST-010 disabled.");
                _st010 = null;
            }
            else
            {
                _st010 = new KSNES.Specialchips.ST010.St010(st010Rom, _sram, _system);
                _st010.Reset();
                Console.WriteLine("[ST010] Initialized successfully.");
            }
        }
        else
        {
            _st010 = null;
        }

        if (hasSt011)
        {
            if (_system == null)
                throw new InvalidOperationException("ROM system not set.");

            byte[]? st011Rom = TryLoadSt011Rom();
            if (st011Rom == null)
            {
                Console.WriteLine("[ST011] ROM not found; ST-011 disabled.");
                _st011 = null;
            }
            else
            {
                _st011 = new KSNES.Specialchips.ST011.St011(st011Rom, _sram, _system);
                _st011.Reset();
                Console.WriteLine("[ST011] Initialized successfully.");
            }
        }
        else
        {
            _st011 = null;
        }

        // ST018 support (chip ID 0x05 with specific chipset byte)
        bool isSt018 = header.ChipsetByte == 0xF5 && header.Chips == 5;
        if (isSt018)
        {
            if (_system == null)
                throw new InvalidOperationException("ROM system not set.");

            byte[]? st018Rom = TryLoadSt018Rom();
            if (st018Rom == null)
            {
                Console.WriteLine("[ST018] ROM not found; ST-018 disabled.");
                _st018 = null;
            }
            else
            {
                _st018 = new KSNES.Specialchips.ST018.St018(st018Rom, _system);
                _st018.Reset();
                Console.WriteLine("[ST018] Initialized successfully.");
            }
        }
        else
        {
            _st018 = null;
        }

        // OBC1 support
        bool isObc1 = header.ChipsetByte == 0x25;
        if (isObc1)
        {
            _obc1 = new KSNES.Specialchips.OBC1.Obc1(_data, _sram);
            Console.WriteLine("[OBC1] Initialized successfully.");
        }
        else
        {
            _obc1 = null;
        }

        if (header.IsExHiRom && header.ChipsetByte == 0x55)
        {
            _srtc = new KSNES.Specialchips.SRTC.SRtc();
            _srtc.ResetState();
            Console.WriteLine("[S-RTC] Initialized successfully.");
        }
        else
        {
            _srtc = null;
        }

        ConfigureTimedCoprocessorDispatch();
        ConfigurePlainReadDispatch();
        ConfigureBasePageTable();
        ConfigureFastCartridgeDispatch();
    }

    public int RomLength => _data.Length;
    internal KSNES.Specialchips.CX4.Cx4? Cx4 => _cx4;
        internal KSNES.Specialchips.DSP1.Dsp1? Dsp1 => _dsp1;
    internal KSNES.Specialchips.SuperFX.SuperFx? SuperFx => _superFx;
    internal KSNES.Specialchips.ST010.St010? St010 => _st010;
    internal KSNES.Specialchips.ST011.St011? St011 => _st011;
    internal KSNES.Specialchips.ST018.St018? St018 => _st018;
    internal KSNES.Specialchips.OBC1.Obc1? Obc1 => _obc1;
    public object? Sa1 => _sa1;

    public void LoadSRAM()
    {
        string fileName = GetSRAMFileName();
        if (new FileInfo(fileName).Exists)
        {
            byte[] data = File.ReadAllBytes(fileName);
            if (_superFx != null && data.Length != _sram.Length)
            {
                Console.WriteLine($"[SuperFX] Ignoring SRAM file with unexpected length {data.Length}; expected {_sram.Length}.");
            }
            else
            {
                int copy = Math.Min(data.Length, _sram.Length);
                if (copy > 0)
                {
                    Buffer.BlockCopy(data, 0, _sram, 0, copy);
                }
                if (data.Length != _sram.Length)
                {
                    Console.WriteLine($"[SRAM] Loaded size {data.Length} != expected {_sram.Length}. Truncated to {copy}.");
                }
            }
        }

        LoadSpc7110Rtc();
    }

    public byte Read(int bank, int adr)
    {
        int SramIndex(int idx) => _sramSize <= 0 ? 0 : idx % _sramSize;
        bool traceExactDspRead =
            TraceDsp1ExactReads &&
            _dsp1 != null &&
            (bank & 0x7f) == 0x30 &&
            (adr == 0x8000 || adr == 0xC000) &&
            _traceDsp1ExactReadCount < TraceDsp1ExactReadLimit;
        int snesPc = -1;
        int snesOp = -1;
        if (TraceReadPcWindow)
        {
            TryGetSnesPc(out snesPc, out snesOp);
            if (snesPc >= TraceReadPcStart && snesPc <= TraceReadPcEnd && _traceReadPcCount++ < TraceReadPcLimit)
            {
                Console.WriteLine($"[SNES-RD-PC] pc=0x{snesPc:X6} op=0x{snesOp:X2} addr=0x{bank:X2}{adr:X4}");
            }
        }
        if (TraceSnesVectors && bank == 0x00 && (adr == 0xFFEA || adr == 0xFFEB || adr == 0xFFEE || adr == 0xFFEF))
        {
            Console.WriteLine($"[SNES-VEC] read bank=0x{bank:X2} adr=0x{adr:X4}");
        }
        if (_plainReadDispatch != PlainReadDispatch.None)
        {
            return ReadPlainMapped(bank, adr, SramIndex);
        }
        if (_sa1 != null)
        {
            uint address = (uint)((bank << 16) | (adr & 0xFFFF));
            bool needResolve = Sa1Trace.IsEnabled || _traceSa1BwramWatch;
            if (snesPc < 0 && (needResolve || _sa1.RequiresSnesAccessPc))
                TryGetSnesPc(out snesPc, out snesOp);
            byte? sa1Value = _sa1.SnesRead(address, snesPc);
            if (sa1Value.HasValue)
            {
                if (needResolve && _sa1.TryResolveSnesAccess(address, out string region, out uint? resolved))
                {
                    if (Sa1Trace.IsEnabled && (region.StartsWith("I-RAM", StringComparison.Ordinal) || region.StartsWith("BW-RAM", StringComparison.Ordinal)))
                    {
                        _sa1.TraceSnesReadMirror(address, sa1Value.Value, region, resolved);
                    }
                    if (_traceSa1BwramWatch && region.StartsWith("BW-RAM", StringComparison.Ordinal))
                    {
                        uint adr16 = address & 0xFFFF;
                        uint off = resolved ?? 0;
                        if (adr16 == 0x604E || adr16 == 0x604F || (off & 0xFFFF) == 0x004E || (off & 0xFFFF) == 0x004F)
                        {
                            Console.WriteLine($"[SNES-BWRAM-RD] addr=0x{address:X6} bwram=0x{off:X6} val=0x{sa1Value.Value:X2} pc=0x{snesPc:X6}");
                        }
                    }
                    if (Sa1Trace.IsEnabled && snesPc >= 0)
                        Sa1Trace.Log("SNES", snesPc, snesOp, address, "R", sa1Value.Value, region, resolved);
                }
                return sa1Value.Value;
            }
        }

        if (_superFx != null)
        {
            uint address = (uint)((bank << 16) | (adr & 0xFFFF));
            if (_superFx.Read(address, out byte value))
                return value;
            if (IsSuperFxMappedAddress(bank, adr))
            {
                if (TraceSuperFxBlockedReads && _traceSuperFxBlockedReadCount++ < TraceSuperFxBlockedReadLimit)
                {
                    if (snesPc < 0)
                        TryGetSnesPc(out snesPc, out snesOp);
                    Console.WriteLine(
                        $"[SFX-OPENBUS-RD] pc=0x{snesPc:X6} op=0x{snesOp:X2} addr=0x{address:X6} open=0x{(_system?.OpenBus ?? 0):X2}");
                }
                return (byte)(_system?.OpenBus ?? 0);
            }
        }

        if (_sdd1 != null)
        {
            uint address = (uint)((bank << 16) | (adr & 0xFFFF));
            if (_sdd1.Read(address, out byte value))
                return value;
        }

        if (_spc7110 != null)
        {
            uint address = (uint)((bank << 16) | (adr & 0xFFFF));
            if (_spc7110.Read(address, out byte value))
                return value;
        }

        if (_cx4 != null)
        {
            if ((bank & 0x7f) < 0x40 && adr >= 0x6000 && adr < 0x8000)
            {
                if (TraceCx4Bus)
                    Console.WriteLine($"[CX4-BUS-RD] bank=0x{bank:X2} adr=0x{adr:X4}");
                return _cx4.Read(adr);
            }
            if (adr < 0x8000 && _hasSram && ((bank >= 0x70 && bank < 0x7e) || bank >= 0xf0))
            {
                int idx = ((bank & 0x0f) << 15) | (adr & 0x7fff);
                return _sram[SramIndex(idx)];
            }
            bank &= 0x7f;
            if (adr >= 0x8000 || bank >= 0x40)
            {
                return _data[((bank & (_banks - 1)) << 15) | (adr & 0x7fff)];
            }
            return (byte)(_system?.OpenBus ?? 0);
        }

        if (TryReadDsp1(bank, adr, out byte dsp1Value))
        {
            if (traceExactDspRead)
            {
                _traceDsp1ExactReadCount++;
                Console.WriteLine($"[DSP1-EXACT-RD] addr=0x{bank:X2}{adr:X4} source=DSP val=0x{dsp1Value:X2} hirom={_dsp1IsHiRom} broad={_dsp1BroadMap} swap={_dsp1SwapPorts}");
            }
            return dsp1Value;
        }
        if (TraceDsp1NearMiss && _dsp1 != null && IsNearDspPort(bank, adr) && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
        {
            Console.WriteLine($"[DSP1-BUS-MISS-RD] bank=0x{bank:X2} adr=0x{adr:X4} hirom={_dsp1IsHiRom} broad={_dsp1BroadMap} swap={_dsp1SwapPorts}");
        }

        if (TryReadSt010(bank, adr, out byte st010Value))
        {
            if (traceExactDspRead)
            {
                _traceDsp1ExactReadCount++;
                Console.WriteLine($"[DSP1-EXACT-RD] addr=0x{bank:X2}{adr:X4} source=ST010 val=0x{st010Value:X2}");
            }
            return st010Value;
        }

        if (TryReadSt011(bank, adr, out byte st011Value))
        {
            if (traceExactDspRead)
            {
                _traceDsp1ExactReadCount++;
                Console.WriteLine($"[DSP1-EXACT-RD] addr=0x{bank:X2}{adr:X4} source=ST011 val=0x{st011Value:X2}");
            }
            return st011Value;
        }

        if (TryReadSt018(bank, adr, out byte st018Value))
        {
            if (traceExactDspRead)
            {
                _traceDsp1ExactReadCount++;
                Console.WriteLine($"[DSP1-EXACT-RD] addr=0x{bank:X2}{adr:X4} source=ST018 val=0x{st018Value:X2}");
            }
            return st018Value;
        }

        if (TryReadObc1(bank, adr, out byte obc1Value))
        {
            if (traceExactDspRead)
            {
                _traceDsp1ExactReadCount++;
                Console.WriteLine($"[DSP1-EXACT-RD] addr=0x{bank:X2}{adr:X4} source=OBC1 val=0x{obc1Value:X2}");
            }
            return obc1Value;
        }

        if (TryReadSrtc(bank, adr, out byte srtcValue))
        {
            return srtcValue;
        }
        return ReadBasePage(bank, adr, traceExactDspRead);
    }

    public void Write(int bank, int adr, byte value)
    {
        if (_sa1 != null)
        {
            uint address = (uint)((bank << 16) | (adr & 0xFFFF));
            bool needResolve = Sa1Trace.IsEnabled || _traceSa1BwramWatch;
            bool needSnesPc = needResolve || _sa1.RequiresSnesAccessPc;
            if (needSnesPc)
            {
                int snesPc = -1;
                int snesOp = -1;
                TryGetSnesPc(out snesPc, out snesOp);
                if (_sa1.TrySnesWrite(address, value, out bool touchesBwram, snesPc))
                {
                    if (needResolve && _sa1.TryResolveSnesAccess(address, out string region, out uint? resolved))
                    {
                        if (ShouldTraceBwramContext(address, region, resolved) &&
                            TryGetSnesContext(out int ctxPc, out string regs, out string opBytes))
                        {
                            Console.WriteLine($"[SNES-BWRAM-CTX] pc=0x{ctxPc:X6} op=[{opBytes}] regs=[{regs}] addr=0x{address:X6} bwram=0x{(resolved ?? 0):X6} val=0x{value:X2}");
                        }
                        if (Sa1Trace.IsEnabled && snesPc >= 0)
                            Sa1Trace.Log("SNES", snesPc, snesOp, address, "W", value, region, resolved);
                    }
                    if (_hasSram && touchesBwram)
                        _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                    return;
                }
            }
            else if (_sa1.TrySnesWrite(address, value, out bool touchesBwram))
            {
                if (_hasSram && touchesBwram)
                    _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                return;
            }
        }

        if (_superFx != null)
        {
            uint address = (uint)((bank << 16) | (adr & 0xFFFF));
            if (_superFx.Write(address, value, out bool wroteRam))
            {
                if (wroteRam && _hasSram)
                    _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                return;
            }
        }

        if (_sdd1 != null)
        {
            uint address = (uint)((bank << 16) | (adr & 0xFFFF));
            if (_sdd1.Write(address, value, out bool wroteRam))
            {
                if (wroteRam && _hasSram)
                    _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                return;
            }
        }

        if (_spc7110 != null)
        {
            uint address = (uint)((bank << 16) | (adr & 0xFFFF));
            if (_spc7110.Write(address, value, out bool wroteRam))
            {
                if (wroteRam && _hasSram)
                    _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                else if (_spc7110.Rtc != null && ((bank & 0x7F) <= 0x3F) && (adr == 0x4840 || adr == 0x4841))
                    SaveSpc7110Rtc();
                return;
            }
        }

        if (_cx4 != null)
        {
            if ((bank & 0x7f) < 0x40 && adr >= 0x6000 && adr < 0x8000)
            {
                if (TraceCx4Bus)
                    Console.WriteLine($"[CX4-BUS-WR] bank=0x{bank:X2} adr=0x{adr:X4} val=0x{value:X2}");
                _cx4.Write(adr, value);
                return;
            }
            if (adr < 0x8000 && _hasSram && ((bank >= 0x70 && bank < 0x7e) || bank >= 0xf0))
            {
                int idx = ((bank & 0x0f) << 15) | (adr & 0x7fff);
                _sram[SramIndex(idx)] = value;
                _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            }
            return;
        }

        if (TryWriteDsp1(bank, adr, value))
        {
            return;
        }
        if (TraceDsp1NearMiss && _dsp1 != null && IsNearDspPort(bank, adr) && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
        {
            Console.WriteLine($"[DSP1-BUS-MISS-WR] bank=0x{bank:X2} adr=0x{adr:X4} val=0x{value:X2} hirom={_dsp1IsHiRom} broad={_dsp1BroadMap} swap={_dsp1SwapPorts}");
        }

        if (TryWriteSt010(bank, adr, value))
        {
            return;
        }

        if (TryWriteSt011(bank, adr, value))
        {
            return;
        }

        if (TryWriteSt018(bank, adr, value))
        {
            return;
        }

        if (TryWriteObc1(bank, adr, value))
        {
            return;
        }

        if (TryWriteSrtc(bank, adr, value))
        {
            return;
        }
        WriteBasePage(bank, adr, value);
    }

    internal byte ReadFast(int fullAdr)
    {
        if (_fastCartridgeRequiresSlowPath)
            return Read((fullAdr >> 16) & 0xFF, fullAdr & 0xFFFF);

        int bank = (fullAdr >> 16) & 0xFF;
        int adr = fullAdr & 0xFFFF;
        return _fastCartridgeDispatch switch
        {
            FastCartridgeDispatch.Base => ReadBasePage(bank, adr, traceExactDspRead: false),
            FastCartridgeDispatch.Cx4 => ReadFastCx4(bank, adr),
            FastCartridgeDispatch.Dsp1 => ReadFastDsp1(bank, adr),
            FastCartridgeDispatch.SuperFx => ReadFastSuperFx(bank, adr),
            FastCartridgeDispatch.Sa1 => ReadFastSa1(bank, adr),
            FastCartridgeDispatch.Sdd1 => ReadFastSdd1(bank, adr),
            FastCartridgeDispatch.St010 => ReadFastSt010(bank, adr),
            FastCartridgeDispatch.St011 => ReadFastSt011(bank, adr),
            FastCartridgeDispatch.St018 => ReadFastSt018(bank, adr),
            FastCartridgeDispatch.Obc1 => ReadFastObc1(bank, adr),
            FastCartridgeDispatch.Srtc => ReadFastSrtc(bank, adr),
            FastCartridgeDispatch.Spc7110 => ReadFastSpc7110(bank, adr),
            _ => Read(bank, adr)
        };
    }

    internal void WriteFast(int fullAdr, byte value)
    {
        if (_fastCartridgeRequiresSlowPath)
        {
            Write((fullAdr >> 16) & 0xFF, fullAdr & 0xFFFF, value);
            return;
        }

        int bank = (fullAdr >> 16) & 0xFF;
        int adr = fullAdr & 0xFFFF;
        switch (_fastCartridgeDispatch)
        {
            case FastCartridgeDispatch.Base:
                WriteBasePage(bank, adr, value);
                return;
            case FastCartridgeDispatch.Cx4:
                WriteFastCx4(bank, adr, value);
                return;
            case FastCartridgeDispatch.Dsp1:
                WriteFastDsp1(bank, adr, value);
                return;
            case FastCartridgeDispatch.SuperFx:
                WriteFastSuperFx(bank, adr, value);
                return;
            case FastCartridgeDispatch.Sa1:
                WriteFastSa1(bank, adr, value);
                return;
            case FastCartridgeDispatch.Sdd1:
                WriteFastSdd1(bank, adr, value);
                return;
            case FastCartridgeDispatch.St010:
                WriteFastSt010(bank, adr, value);
                return;
            case FastCartridgeDispatch.St011:
                WriteFastSt011(bank, adr, value);
                return;
            case FastCartridgeDispatch.St018:
                WriteFastSt018(bank, adr, value);
                return;
            case FastCartridgeDispatch.Obc1:
                WriteFastObc1(bank, adr, value);
                return;
            case FastCartridgeDispatch.Srtc:
                WriteFastSrtc(bank, adr, value);
                return;
            case FastCartridgeDispatch.Spc7110:
                WriteFastSpc7110(bank, adr, value);
                return;
            default:
                Write(bank, adr, value);
                return;
        }
    }

    private byte ReadFastSa1(int bank, int adr)
    {
        byte? sa1Value = _sa1!.SnesRead((uint)((bank << 16) | (adr & 0xFFFF)));
        return sa1Value ?? ReadBasePage(bank, adr, traceExactDspRead: false);
    }

    private void WriteFastSa1(int bank, int adr, byte value)
    {
        if (_sa1!.TrySnesWrite((uint)((bank << 16) | (adr & 0xFFFF)), value, out bool touchesBwram))
        {
            if (_hasSram && touchesBwram)
                _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            return;
        }

        WriteBasePage(bank, adr, value);
    }

    private byte ReadFastSuperFx(int bank, int adr)
    {
        uint address = (uint)((bank << 16) | (adr & 0xFFFF));
        if (_superFx!.Read(address, out byte value))
            return value;
        if (IsSuperFxMappedAddress(bank, adr))
            return GetOpenBusByte();
        return ReadBasePage(bank, adr, traceExactDspRead: false);
    }

    private void WriteFastSuperFx(int bank, int adr, byte value)
    {
        uint address = (uint)((bank << 16) | (adr & 0xFFFF));
        if (_superFx!.Write(address, value, out bool wroteRam))
        {
            if (wroteRam && _hasSram)
                _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            return;
        }

        WriteBasePage(bank, adr, value);
    }

    private byte ReadFastSdd1(int bank, int adr)
    {
        return _sdd1!.Read((uint)((bank << 16) | (adr & 0xFFFF)), out byte value)
            ? value
            : ReadBasePage(bank, adr, traceExactDspRead: false);
    }

    private void WriteFastSdd1(int bank, int adr, byte value)
    {
        if (_sdd1!.Write((uint)((bank << 16) | (adr & 0xFFFF)), value, out bool wroteRam))
        {
            if (wroteRam && _hasSram)
                _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            return;
        }

        WriteBasePage(bank, adr, value);
    }

    private byte ReadFastSpc7110(int bank, int adr)
    {
        return _spc7110!.Read((uint)((bank << 16) | (adr & 0xFFFF)), out byte value)
            ? value
            : ReadBasePage(bank, adr, traceExactDspRead: false);
    }

    private void WriteFastSpc7110(int bank, int adr, byte value)
    {
        if (_spc7110!.Write((uint)((bank << 16) | (adr & 0xFFFF)), value, out bool wroteRam))
        {
            if (wroteRam && _hasSram)
                _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            else if (_spc7110.Rtc != null && ((bank & 0x7F) <= 0x3F) && (adr == 0x4840 || adr == 0x4841))
                SaveSpc7110Rtc();
            return;
        }

        WriteBasePage(bank, adr, value);
    }

    private byte ReadFastCx4(int bank, int adr)
    {
        if ((bank & 0x7f) < 0x40 && adr >= 0x6000 && adr < 0x8000)
            return _cx4!.Read(adr);
        if (adr < 0x8000 && _hasSram && ((bank >= 0x70 && bank < 0x7e) || bank >= 0xf0))
            return _sram[SramIndex(((bank & 0x0f) << 15) | (adr & 0x7fff))];

        int maskedBank = bank & 0x7f;
        return adr >= 0x8000 || maskedBank >= 0x40
            ? _data[((maskedBank & (_banks - 1)) << 15) | (adr & 0x7fff)]
            : GetOpenBusByte();
    }

    private void WriteFastCx4(int bank, int adr, byte value)
    {
        if ((bank & 0x7f) < 0x40 && adr >= 0x6000 && adr < 0x8000)
        {
            _cx4!.Write(adr, value);
            return;
        }
        if (adr < 0x8000 && _hasSram && ((bank >= 0x70 && bank < 0x7e) || bank >= 0xf0))
        {
            _sram[SramIndex(((bank & 0x0f) << 15) | (adr & 0x7fff))] = value;
            _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
    }

    private byte ReadFastDsp1(int bank, int adr)
    {
        return TryReadDsp1(bank, adr, out byte value)
            ? value
            : ReadBasePage(bank, adr, traceExactDspRead: false);
    }

    private void WriteFastDsp1(int bank, int adr, byte value)
    {
        if (!TryWriteDsp1(bank, adr, value))
            WriteBasePage(bank, adr, value);
    }

    private byte ReadFastSt010(int bank, int adr)
    {
        return TryReadSt010(bank, adr, out byte value)
            ? value
            : ReadBasePage(bank, adr, traceExactDspRead: false);
    }

    private void WriteFastSt010(int bank, int adr, byte value)
    {
        if (!TryWriteSt010(bank, adr, value))
            WriteBasePage(bank, adr, value);
    }

    private byte ReadFastSt011(int bank, int adr)
    {
        return TryReadSt011(bank, adr, out byte value)
            ? value
            : ReadBasePage(bank, adr, traceExactDspRead: false);
    }

    private void WriteFastSt011(int bank, int adr, byte value)
    {
        if (!TryWriteSt011(bank, adr, value))
            WriteBasePage(bank, adr, value);
    }

    private byte ReadFastSt018(int bank, int adr)
    {
        return TryReadSt018(bank, adr, out byte value)
            ? value
            : ReadBasePage(bank, adr, traceExactDspRead: false);
    }

    private void WriteFastSt018(int bank, int adr, byte value)
    {
        if (!TryWriteSt018(bank, adr, value))
            WriteBasePage(bank, adr, value);
    }

    private byte ReadFastObc1(int bank, int adr)
    {
        return TryReadObc1(bank, adr, out byte value)
            ? value
            : ReadBasePage(bank, adr, traceExactDspRead: false);
    }

    private void WriteFastObc1(int bank, int adr, byte value)
    {
        if (!TryWriteObc1(bank, adr, value))
            WriteBasePage(bank, adr, value);
    }

    private byte ReadFastSrtc(int bank, int adr)
    {
        return TryReadSrtc(bank, adr, out byte value)
            ? value
            : ReadBasePage(bank, adr, traceExactDspRead: false);
    }

    private void WriteFastSrtc(int bank, int adr, byte value)
    {
        if (!TryWriteSrtc(bank, adr, value))
            WriteBasePage(bank, adr, value);
    }

    private int SramIndex(int idx)
    {
        return _sramSize <= 0 ? 0 : idx % _sramSize;
    }

    private byte ReadBasePage(int bank, int adr, bool traceExactDspRead)
    {
        int pageIndex = (bank << 8) | ((adr >> 8) & 0xFF);
        int offset = adr & 0xFF;
        byte value = (BasePageKind)_basePageKind[pageIndex] switch
        {
            BasePageKind.RomDirect => _data[_basePageBase[pageIndex] + offset],
            BasePageKind.RomWrapped => _data[(_basePageBase[pageIndex] + offset) % _data.Length],
            BasePageKind.SramDirect => _sram[_basePageBase[pageIndex] + offset],
            BasePageKind.SramWrapped => _sram[(_basePageBase[pageIndex] + offset) % _sramSize],
            _ => GetOpenBusByte()
        };

        if (traceExactDspRead)
        {
            _traceDsp1ExactReadCount++;
            Console.WriteLine($"[DSP1-EXACT-RD] addr=0x{bank:X2}{adr:X4} source={GetExactBaseReadSource((BasePageKind)_basePageKind[pageIndex])} val=0x{value:X2}");
        }

        return value;
    }

    private void WriteBasePage(int bank, int adr, byte value)
    {
        int pageIndex = (bank << 8) | ((adr >> 8) & 0xFF);
        int offset = adr & 0xFF;
        switch ((BasePageKind)_basePageKind[pageIndex])
        {
            case BasePageKind.SramDirect:
                _sram[_basePageBase[pageIndex] + offset] = value;
                _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                break;
            case BasePageKind.SramWrapped:
                _sram[(_basePageBase[pageIndex] + offset) % _sramSize] = value;
                _sRAMTimer ??= new Timer(SaveSRAM, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
                break;
        }
    }

    private string GetExactBaseReadSource(BasePageKind kind)
    {
        if (kind == BasePageKind.OpenBus)
            return Header.IsHiRom ? "OpenBusHi" : "OpenBus";
        if (kind == BasePageKind.SramDirect || kind == BasePageKind.SramWrapped)
            return "SRAM";
        if (Header.IsExHiRom)
            return "ExHiROM";
        if (Header.IsHiRom)
            return "HiROM";
        return "LoROM";
    }

    private bool IsNearDspPort(int bank, int adr)
    {
        int bankMasked = bank & 0x7f;
        if (_dsp1 == null)
            return false;
        return bankMasked >= 0x20 && bankMasked < 0x70 && (adr & 0x8000) != 0;
    }

    private bool TryReadDsp1(int bank, int adr, out byte value)
    {
        value = 0;
        if (_dsp1 == null)
        {
            return false;
        }
        if (TraceDsp1ExactReads &&
            (bank & 0x7f) == 0x30 &&
            (adr == 0x8000 || adr == 0xC000) &&
            _traceDsp1ExactReadCount < TraceDsp1ExactReadLimit)
        {
            Console.WriteLine($"[DSP1-EXACT-CFG] bank=0x{bank:X2} adr=0x{adr:X4} hirom={_dsp1IsHiRom} broad={_dsp1BroadMap} swap={_dsp1SwapPorts} portHit={_dsp1PortMapping.IsDspPort(bank, adr)}");
        }
        bool IsDataPort(int address) => _dsp1SwapPorts ? (address & 0x4000) != 0 : (address & 0x4000) == 0;

        if (_dsp1BroadMap)
        {
            if ((bank & 0x7f) <= 0x3f && adr >= 0x6000 && adr < 0x8000)
            {
                value = IsDataPort(adr) ? _dsp1.ReadData() : _dsp1.ReadStatus();
                if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                {
                    string port = IsDataPort(adr) ? "DATA" : "STAT";
                    Console.WriteLine($"[DSP1-BUS-RD] bank=0x{bank:X2} adr=0x{adr:X4} {port} (broad) -> 0x{value:X2}");
                }
                return true;
            }
        }

        if (!_dsp1IsHiRom)
        {
            if (_dsp1PortMapping.IsDspPort(bank, adr))
            {
                value = IsDataPort(adr) ? _dsp1.ReadData() : _dsp1.ReadStatus();
                if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                {
                    string port = IsDataPort(adr) ? "DATA" : "STAT";
                    Console.WriteLine($"[DSP1-BUS-RD] bank=0x{bank:X2} adr=0x{adr:X4} {port} -> 0x{value:X2}");
                }
                return true;
            }
            return false;
        }

        if (((bank & 0x7f) <= 0x0f) && adr >= 0x6000 && adr < 0x7000)
        {
            value = _dsp1.ReadData();
            if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                Console.WriteLine($"[DSP1-BUS-RD] bank=0x{bank:X2} adr=0x{adr:X4} DATA -> 0x{value:X2}");
            return true;
        }
        if (((bank & 0x7f) <= 0x0f) && adr >= 0x7000 && adr < 0x8000)
        {
            value = _dsp1.ReadStatus();
            if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                Console.WriteLine($"[DSP1-BUS-RD] bank=0x{bank:X2} adr=0x{adr:X4} STAT -> 0x{value:X2}");
            return true;
        }
        return false;
    }

    private bool TryReadSt010(int bank, int adr, out byte value)
    {
        value = 0;
        if (_st010 == null)
        {
            return false;
        }

        // jgenesis maps ST010/ST011 ports at $60-$67:0000-$0001 and RAM at $68-$6F:0000-$0FFF.
        if (bank is >= 0x60 and <= 0x67 && adr == 0x0000)
        {
            value = _st010.ReadData();
            return true;
        }

        if (bank is >= 0x60 and <= 0x67 && adr == 0x0001)
        {
            value = _st010.ReadStatus();
            return true;
        }

        if (bank is >= 0x68 and <= 0x6F && adr >= 0x0000 && adr <= 0x0FFF)
        {
            uint sramAddr = (uint)(((bank & 0x7) << 12) | (adr & 0x0FFF));
            value = _st010.ReadRam(sramAddr);
            return true;
        }

        return false;
    }

    private bool TryReadSt011(int bank, int adr, out byte value)
    {
        value = 0;
        if (_st011 == null)
        {
            return false;
        }

        // ST011 uses similar mapping to DSP-1 but with different addresses
        // Based on research: ST011 maps to $6000-$7FFF in banks $00-$3F/$80-$BF
        if ((bank & 0x7F) <= 0x3F && adr >= 0x6000 && adr < 0x8000)
        {
            // $6000-$6FFF: Data port, $7000-$7FFF: Status port
            if (adr < 0x7000)
            {
                value = _st011.ReadData();
            }
            else
            {
                value = _st011.ReadStatus();
            }
            return true;
        }

        return false;
    }

    private bool TryReadSt018(int bank, int adr, out byte value)
    {
        value = 0;
        if (_st018 == null)
        {
            return false;
        }

        // ST018 maps to $3800-$3807 in banks $00-$3F/$80-$BF
        if ((bank & 0x7F) <= 0x3F && adr >= 0x3800 && adr < 0x3808)
        {
            uint address = (uint)((bank << 16) | adr);
            value = _st018.Read(address);
            return true;
        }

        return false;
    }

    private bool TryWriteSt018(int bank, int adr, byte value)
    {
        if (_st018 == null)
        {
            return false;
        }

        // ST018 maps to $3800-$3807 in banks $00-$3F/$80-$BF
        if ((bank & 0x7F) <= 0x3F && adr >= 0x3800 && adr < 0x3808)
        {
            uint address = (uint)((bank << 16) | adr);
            _st018.Write(address, value);
            return true;
        }

        return false;
    }

    private bool TryWriteSt010(int bank, int adr, byte value)
    {
        if (_st010 == null)
        {
            return false;
        }

        if (bank is >= 0x60 and <= 0x67 && adr == 0x0000)
        {
            _st010.WriteData(value);
            return true;
        }

        if (bank is >= 0x68 and <= 0x6F && adr >= 0x0000 && adr <= 0x0FFF)
        {
            uint sramAddr = (uint)(((bank & 0x7) << 12) | (adr & 0x0FFF));
            _st010.WriteRam(sramAddr, value);
            return true;
        }

        return false;
    }

    private bool TryWriteSt011(int bank, int adr, byte value)
    {
        if (_st011 == null)
        {
            return false;
        }

        // ST011 uses similar mapping to DSP-1 but with different addresses
        if ((bank & 0x7F) <= 0x3F && adr >= 0x6000 && adr < 0x8000)
        {
            // $6000-$6FFF: Data port, $7000-$7FFF: Status port
            if (adr < 0x7000)
            {
                _st011.WriteData(value);
            }
            // Status port is read-only for writes
            return true;
        }

        return false;
    }

    private bool TryReadObc1(int bank, int adr, out byte value)
    {
        value = 0;
        if (_obc1 == null)
        {
            return false;
        }

        uint address = (uint)((bank << 16) | adr);
        byte? result = _obc1.Read(address);
        
        if (result.HasValue)
        {
            value = result.Value;
            return true;
        }

        return false;
    }

    private bool TryWriteObc1(int bank, int adr, byte value)
    {
        if (_obc1 == null)
        {
            return false;
        }

        uint address = (uint)((bank << 16) | adr);
        _obc1.Write(address, value);
        return true;
    }

    private bool TryWriteDsp1(int bank, int adr, byte value)
    {
        if (_dsp1 == null)
        {
            return false;
        }
        bool IsDataPort(int address) => _dsp1SwapPorts ? (address & 0x4000) != 0 : (address & 0x4000) == 0;

        if (_dsp1BroadMap)
        {
            if ((bank & 0x7f) <= 0x3f && adr >= 0x6000 && adr < 0x8000)
            {
                if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                    Console.WriteLine($"[DSP1-BUS-WR] bank=0x{bank:X2} adr=0x{adr:X4} val=0x{value:X2} (broad)");
                if (IsDataPort(adr))
                    _dsp1.WriteData(value);
                return true;
            }
        }

        if (!_dsp1IsHiRom)
        {
            if (_dsp1PortMapping.IsDspPort(bank, adr))
            {
                if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                    Console.WriteLine($"[DSP1-BUS-WR] bank=0x{bank:X2} adr=0x{adr:X4} val=0x{value:X2}");
                if (IsDataPort(adr))
                    _dsp1.WriteData(value);
                return true;
            }
            return false;
        }

        if (((bank & 0x7f) <= 0x0f) && adr >= 0x6000 && adr < 0x7000)
        {
            if (TraceDsp1Bus && (_traceDsp1BusCount++ < TraceDsp1BusLimit))
                Console.WriteLine($"[DSP1-BUS-WR] bank=0x{bank:X2} adr=0x{adr:X4} val=0x{value:X2}");
            _dsp1.WriteData(value);
            return true;
        }
        return false;
    }

    public void SetSystem(ISNESSystem system)
    {
        _system = system;
    }

    public void ResetCoprocessor()
    {
        _cx4?.Reset();
        _dsp1?.Reset();
        _st010?.Reset();
        _st011?.Reset();
        _st018?.Reset();
        _superFx?.Reset();
        _sa1?.Reset();
        _sdd1?.Reset();
        _srtc?.ResetState();
    }

    public bool TryReadForDma(int fullAddress, out int value)
    {
        value = 0;

        if (_superFx == null)
            return false;

        int bank = (fullAddress >> 16) & 0xFF;
        int adr = fullAddress & 0xFFFF;
        if (!IsSuperFxMappedAddress(bank, adr))
            return false;

        if (_superFx.Read((uint)fullAddress, out byte dmaValue, allowSnesRomReadWhileRunning: true))
        {
            value = dmaValue;
            return true;
        }

        return false;
    }

    public void RunCoprocessor(ulong snesCycles)
    {
        switch (_timedDispatch)
        {
            case TimedCoprocessorDispatch.None:
                return;
            case TimedCoprocessorDispatch.Cx4:
                _cx4!.RunTo(snesCycles);
                return;
            case TimedCoprocessorDispatch.Dsp1:
                _dsp1!.RunTo(snesCycles);
                return;
            case TimedCoprocessorDispatch.St010:
                _st010!.RunTo(snesCycles);
                return;
            case TimedCoprocessorDispatch.St011:
                _st011!.RunTo(snesCycles);
                return;
            case TimedCoprocessorDispatch.St018:
                _st018!.RunTo(snesCycles);
                return;
            case TimedCoprocessorDispatch.SuperFx:
                _superFx!.Tick(snesCycles);
                return;
            case TimedCoprocessorDispatch.Sa1:
                _sa1!.Tick(snesCycles);
                return;
            default:
                _cx4?.RunTo(snesCycles);
                _dsp1?.RunTo(snesCycles);
                _st010?.RunTo(snesCycles);
                _st011?.RunTo(snesCycles);
                _st018?.RunTo(snesCycles);
                _superFx?.Tick(snesCycles);
                _sa1?.Tick(snesCycles);
                return;
        }
    }

    public void ResyncCoprocessors(ulong snesCycles)
    {
        ConfigureTimedCoprocessorDispatch();
        if (_dsp1 != null)
        {
            ApplyDspPortConfiguration(GuessDspVariant(_data), Header);
        }
        switch (_timedDispatch)
        {
            case TimedCoprocessorDispatch.None:
                return;
            case TimedCoprocessorDispatch.Dsp1:
                _dsp1!.ResyncTo(snesCycles);
                return;
            case TimedCoprocessorDispatch.St010:
                _st010!.ResyncTo(snesCycles);
                return;
            case TimedCoprocessorDispatch.St011:
                _st011!.ResyncTo(snesCycles);
                return;
            case TimedCoprocessorDispatch.St018:
                _st018!.ResyncTo(snesCycles);
                return;
            case TimedCoprocessorDispatch.SuperFx:
                _superFx!.ResyncTo(snesCycles);
                return;
            case TimedCoprocessorDispatch.Sa1:
                _sa1!.ResyncTo(snesCycles);
                return;
            default:
                _dsp1?.ResyncTo(snesCycles);
                _st010?.ResyncTo(snesCycles);
                _st011?.ResyncTo(snesCycles);
                _st018?.ResyncTo(snesCycles);
                _superFx?.ResyncTo(snesCycles);
                _sa1?.ResyncTo(snesCycles);
                return;
        }
    }

    private void ConfigureTimedCoprocessorDispatch()
    {
        int count = 0;
        TimedCoprocessorDispatch dispatch = TimedCoprocessorDispatch.None;

        if (_cx4 != null)
        {
            dispatch = TimedCoprocessorDispatch.Cx4;
            count++;
        }
        if (_dsp1 != null)
        {
            dispatch = TimedCoprocessorDispatch.Dsp1;
            count++;
        }
        if (_st010 != null)
        {
            dispatch = TimedCoprocessorDispatch.St010;
            count++;
        }
        if (_st011 != null)
        {
            dispatch = TimedCoprocessorDispatch.St011;
            count++;
        }
        if (_st018 != null)
        {
            dispatch = TimedCoprocessorDispatch.St018;
            count++;
        }
        if (_superFx != null)
        {
            dispatch = TimedCoprocessorDispatch.SuperFx;
            count++;
        }
        if (_sa1 != null)
        {
            dispatch = TimedCoprocessorDispatch.Sa1;
            count++;
        }

        _timedDispatch = count <= 1 ? dispatch : TimedCoprocessorDispatch.Mixed;
    }

    private void ConfigurePlainReadDispatch()
    {
        if (_cx4 != null
            || _dsp1 != null
            || _superFx != null
            || _sa1 != null
            || _sdd1 != null
            || _st010 != null
            || _st011 != null
            || _st018 != null
            || _obc1 != null
            || _srtc != null
            || _spc7110 != null)
        {
            _plainReadDispatch = PlainReadDispatch.None;
            return;
        }

        if (Header.IsExHiRom)
        {
            _plainReadDispatch = PlainReadDispatch.ExHiRom;
        }
        else if (Header.IsHiRom)
        {
            _plainReadDispatch = PlainReadDispatch.HiRom;
        }
        else
        {
            _plainReadDispatch = PlainReadDispatch.LoRom;
        }
    }

    private void ConfigureBasePageTable()
    {
        _basePageKind = new byte[0x10000];
        _basePageBase = new int[0x10000];

        for (int bank = 0; bank < 0x100; bank++)
        {
            int bankBase = bank << 8;
            for (int page = 0; page < 0x100; page++)
            {
                int pageIndex = bankBase | page;
                int adr = page << 8;
                if (Header.IsExHiRom)
                {
                    ConfigureExHiRomBasePage(pageIndex, bank, adr);
                }
                else if (Header.IsHiRom)
                {
                    ConfigureHiRomBasePage(pageIndex, bank, adr);
                }
                else
                {
                    ConfigureLoRomBasePage(pageIndex, bank, adr);
                }
            }
        }
    }

    private void ConfigureLoRomBasePage(int pageIndex, int bank, int adr)
    {
        if (adr < 0x8000 && bank >= 0x70 && bank < 0x7e && _hasSram)
        {
            ConfigureSramPage(pageIndex, ((bank - 0x70) << 15) | (adr & 0x7fff));
            return;
        }

        ConfigureRomPage(pageIndex, (uint)(((bank & (_banks - 1)) << 15) | (adr & 0x7fff)));
    }

    private void ConfigureHiRomBasePage(int pageIndex, int bank, int adr)
    {
        if (adr >= 0x6000 && adr < 0x8000 && _hasSram && IsHiRomSramBank(bank))
        {
            ConfigureSramPage(pageIndex, ((bank & 0x1f) << 13) | (adr & 0x1fff));
            return;
        }

        if (adr >= 0x8000 || (bank >= 0x40 && bank < 0x7e) || bank >= 0xc0)
        {
            ConfigureRomPage(pageIndex, (uint)(((bank & 0x7f) << 16) | (adr & 0xffff)));
        }
    }

    private void ConfigureExHiRomBasePage(int pageIndex, int bank, int adr)
    {
        if (adr >= 0x6000 && adr < 0x8000 && _hasSram && IsExHiRomSramBank(bank))
        {
            ConfigureSramPage(pageIndex, ((bank & 0x1f) << 13) | (adr & 0x1fff));
            return;
        }

        if (adr >= 0x8000 || ((bank >= 0x40 && bank < 0x7e) || bank >= 0xC0))
        {
            uint address = (uint)((bank << 16) | (adr & 0xFFFF));
            uint mapped = (address & 0x3FFFFF) | (((address >> 1) & 0x400000) ^ 0x400000);
            ConfigureRomPage(pageIndex, mapped);
        }
    }

    private void ConfigureRomPage(int pageIndex, uint mappedAddress)
    {
        if (_data.Length == 0)
            return;

        int pageBase = _romLengthIsPowerOfTwo
            ? (int)(mappedAddress & _romLengthMask)
            : (int)(mappedAddress % (uint)_data.Length);
        _basePageBase[pageIndex] = pageBase;
        _basePageKind[pageIndex] = (byte)(pageBase + 0x100 <= _data.Length
            ? BasePageKind.RomDirect
            : BasePageKind.RomWrapped);
    }

    private void ConfigureSramPage(int pageIndex, int mappedAddress)
    {
        if (_sramSize <= 0)
            return;

        int pageBase = mappedAddress % _sramSize;
        _basePageBase[pageIndex] = pageBase;
        _basePageKind[pageIndex] = (byte)(pageBase + 0x100 <= _sramSize
            ? BasePageKind.SramDirect
            : BasePageKind.SramWrapped);
    }

    private void ConfigureFastCartridgeDispatch()
    {
        int count = 0;
        FastCartridgeDispatch dispatch = FastCartridgeDispatch.Base;

        if (_cx4 != null)
        {
            dispatch = FastCartridgeDispatch.Cx4;
            count++;
        }
        if (_dsp1 != null)
        {
            dispatch = FastCartridgeDispatch.Dsp1;
            count++;
        }
        if (_superFx != null)
        {
            dispatch = FastCartridgeDispatch.SuperFx;
            count++;
        }
        if (_sa1 != null)
        {
            dispatch = FastCartridgeDispatch.Sa1;
            count++;
        }
        if (_sdd1 != null)
        {
            dispatch = FastCartridgeDispatch.Sdd1;
            count++;
        }
        if (_st010 != null)
        {
            dispatch = FastCartridgeDispatch.St010;
            count++;
        }
        if (_st011 != null)
        {
            dispatch = FastCartridgeDispatch.St011;
            count++;
        }
        if (_st018 != null)
        {
            dispatch = FastCartridgeDispatch.St018;
            count++;
        }
        if (_obc1 != null)
        {
            dispatch = FastCartridgeDispatch.Obc1;
            count++;
        }
        if (_srtc != null)
        {
            dispatch = FastCartridgeDispatch.Srtc;
            count++;
        }
        if (_spc7110 != null)
        {
            dispatch = FastCartridgeDispatch.Spc7110;
            count++;
        }

        _fastCartridgeDispatch = count <= 1 ? dispatch : FastCartridgeDispatch.Mixed;
        _fastCartridgeRequiresSlowPath =
            TraceCx4Bus ||
            TraceDsp1Bus ||
            TraceDsp1NearMiss ||
            TraceDsp1ExactReads ||
            TraceReadPcWindow ||
            TraceSnesVectors ||
            TraceSuperFxBlockedReads ||
            Sa1Trace.IsEnabled ||
            _traceSa1BwramWatch;
    }

    private byte ReadPlainMapped(int bank, int adr, Func<int, int> sramIndex)
    {
        return ReadBasePage(bank, adr, traceExactDspRead: false);
    }

    private byte ReadPlainLoRom(int bank, int adr, Func<int, int> sramIndex)
    {
        if (adr < 0x8000 && bank >= 0x70 && bank < 0x7e && _hasSram)
        {
            int idx = ((bank - 0x70) << 15) | (adr & 0x7fff);
            return _sram[sramIndex(idx)];
        }

        return ReadLoRom(bank, adr);
    }

    private byte ReadPlainHiRom(int bank, int adr, Func<int, int> sramIndex)
    {
        if (adr >= 0x6000 && adr < 0x8000 && _hasSram && IsHiRomSramBank(bank))
        {
            int idx = ((bank & 0x1f) << 13) | (adr & 0x1fff);
            return _sram[sramIndex(idx)];
        }

        if (adr >= 0x8000 || (bank >= 0x40 && bank < 0x7e) || bank >= 0xc0)
            return ReadHiRom(bank, adr);

        return GetOpenBusByte();
    }

    private byte ReadPlainExHiRom(int bank, int adr, Func<int, int> sramIndex)
    {
        if (adr >= 0x6000 && adr < 0x8000 && _hasSram && IsExHiRomSramBank(bank))
        {
            int idx = ((bank & 0x1f) << 13) | (adr & 0x1fff);
            return _sram[sramIndex(idx)];
        }

        if (adr >= 0x8000 || ((bank >= 0x40 && bank < 0x7e) || bank >= 0xC0))
            return ReadExHiRom(bank, adr);

        return GetOpenBusByte();
    }

    private void ApplyDspPortConfiguration(DspVariant dspVariant, Header header)
    {
        _dsp1IsHiRom = dspVariant == DspVariant.Dsp1 && header.IsHiRom;
        string? forceLo = Environment.GetEnvironmentVariable("EUTHERDRIVE_DSP1_FORCE_LOROM");
        string? forceHi = Environment.GetEnvironmentVariable("EUTHERDRIVE_DSP1_FORCE_HIROM");
        if (string.Equals(forceLo, "1", StringComparison.Ordinal))
        {
            _dsp1IsHiRom = false;
            Console.WriteLine("[DSP1] Forcing LoROM port mapping.");
        }
        else if (string.Equals(forceHi, "1", StringComparison.Ordinal))
        {
            _dsp1IsHiRom = true;
            Console.WriteLine("[DSP1] Forcing HiROM port mapping.");
        }

        _dsp1BroadMap = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DSP1_BROAD_MAP"), "1", StringComparison.Ordinal);
        if (_dsp1BroadMap)
            Console.WriteLine("[DSP1] Using broad port mapping (banks 00-3F/80-BF, $6000-$7FFF).");

        _dsp1SwapPorts = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DSP1_SWAP_PORTS"), "1", StringComparison.Ordinal);
        if (_dsp1SwapPorts)
            Console.WriteLine("[DSP1] Swapping data/status port halves.");

        _dsp1PortMapping = Dsp1PortMapping.FromVariantAndMetadata(dspVariant, header.RomSize, header.RamSize);
    }

    public void NotifyDmaStart(byte channel, uint sourceAddress)
    {
        _sa1?.NotifyDmaStart(sourceAddress);
        _sdd1?.NotifyDmaStart(channel, sourceAddress);
    }

    public void NotifyDmaEnd(byte channel)
    {
        _sa1?.NotifyDmaEnd();
        _sdd1?.NotifyDmaEnd(channel);
    }

    public void DumpRecentDspIo(string reason, int count)
    {
        if (_dsp1 == null)
            return;
        Console.WriteLine($"[DSP1-DUMP-META] tag={reason} snesWords={_dsp1.SnesWordWritesSeen}");
        _dsp1.DumpRecentOpcodes(reason, Math.Min(count, 64));
        _dsp1.DumpRecentIo(reason, count);
    }

    public bool IrqWanted => (_superFx?.Irq ?? false) || (_sa1?.SnesIrq() ?? false);
    public bool NmiWanted => _sa1?.SnesNmi() ?? false;

    public byte ReadRomByteLoRom(uint address)
    {
        if (_data.Length == 0)
            return 0;
        uint mapped = ((address & 0x7F0000) >> 1) | (address & 0x007FFF);
        return ReadWrappedRom(mapped);
    }

    private void SaveSRAM(object? state)
    {
        string fileName = GetSRAMFileName();
        try
        {
            File.WriteAllBytes(fileName, _sram);
            SaveSpc7110Rtc();
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.WriteLine($"[SRAM] Save failed: {ex.Message}");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"[SRAM] Save failed: {ex.Message}");
        }
        _sRAMTimer?.Dispose();
        _sRAMTimer = null;
    }

    private string GetSRAMFileName()
    {
        return Path.ChangeExtension(_system!.FileName, ".srm");
    }

    private string GetSpc7110RtcFileName()
    {
        return Path.ChangeExtension(_system!.FileName, ".rtc");
    }

    private void LoadSpc7110Rtc()
    {
        if (_spc7110?.Rtc == null)
            return;

        string fileName = GetSpc7110RtcFileName();
        if (!File.Exists(fileName))
            return;

        try
        {
            using var stream = File.OpenRead(fileName);
            using var reader = new BinaryReader(stream);
            _spc7110.Rtc.Load(reader);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException)
        {
            Console.WriteLine($"[RTC] Load failed: {ex.Message}");
        }
    }

    private void SaveSpc7110Rtc()
    {
        if (_spc7110?.Rtc == null)
            return;

        string fileName = GetSpc7110RtcFileName();
        try
        {
            using var stream = File.Create(fileName);
            using var writer = new BinaryWriter(stream);
            _spc7110.Rtc.Save(writer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[RTC] Save failed: {ex.Message}");
        }
    }

    private static bool IsSa1Chipset(int chipsetByte)
    {
        return chipsetByte == 0x32 || chipsetByte == 0x34 || chipsetByte == 0x35;
    }

    private static bool IsSdd1Chipset(int mapMode, int chipsetByte)
    {
        return (mapMode == 0x22 || mapMode == 0x32) && chipsetByte >= 0x43 && chipsetByte <= 0x45;
    }

    private static bool IsSt01xChipset(int chipsetByte, int exCoprocessor)
    {
        return chipsetByte == 0xF6 && exCoprocessor == 0x01;
    }

    private static bool IsDspChipset(int chipsetByte)
    {
        return chipsetByte is 0x03 or 0x04 or 0x05;
    }

    private static bool IsSuperFxMappedAddress(int bank, int adr)
    {
        int bankMasked = bank & 0x7F;

        if (bankMasked <= 0x3F)
        {
            if ((adr >= 0x3000 && adr <= 0x30FF) || (adr >= 0x3100 && adr <= 0x32FF) || (adr >= 0x3300 && adr <= 0x34FF))
                return true;
            if (adr >= 0x6000 && adr <= 0x7FFF)
                return true;
            if (adr >= 0x8000)
                return true;
        }

        if (bankMasked <= 0x5F)
            return true;

        return bankMasked is 0x70 or 0x71;
    }


    private static DspVariant GuessDspVariant(byte[] rom)
    {
        uint checksum = ComputeCrc32(rom);
        return checksum switch
        {
            0x0DFD9CEB or 0x7A1BA194 or 0xAA79FA33 or 0x89A67ADF => DspVariant.Dsp2,
            0x4DC3D903 => DspVariant.Dsp3,
            0xA20BE998 or 0x493FDB13 or 0xB9B9DF06 => DspVariant.Dsp4,
            _ => DspVariant.Dsp1
        };
    }

    private static string FormatDspVariant(DspVariant variant)
    {
        return variant switch
        {
            DspVariant.Dsp1 => "DSP1",
            DspVariant.Dsp2 => "DSP2",
            DspVariant.Dsp3 => "DSP3",
            DspVariant.Dsp4 => "DSP4",
            _ => "DSP1"
        };
    }

    private static St01xVariant GuessSt01xVariant(byte[] rom)
    {
        uint checksum = ComputeCrc32(rom);
        // Hayazashi Nidan Morita Shougi (J)
        return checksum == 0x81E822AD ? St01xVariant.St011 : St01xVariant.St010;
    }

    private enum St01xVariant
    {
        St010,
        St011
    }

    private enum DspVariant
    {
        Dsp1,
        Dsp2,
        Dsp3,
        Dsp4
    }

    private static readonly uint[] Crc32Table = BuildCrc32Table();

    private static uint[] BuildCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            uint crc = i;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : (crc >> 1);
            table[i] = crc;
        }

        return table;
    }

    private static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private bool TryGetSnesPc(out int pc, out int op)
    {
        pc = 0;
        op = -1;
        if (_system is not KSNES.SNESSystem.SNESSystem snes)
            return false;
        if (snes.CPU is not KSNES.CPU.CPU cpu)
            return false;
        pc = cpu.ProgramCounter24;
        return true;
    }

    private bool TryGetSnesContext(out int pc, out string regs, out string opBytes)
    {
        pc = 0;
        regs = string.Empty;
        opBytes = string.Empty;
        if (_system is not KSNES.SNESSystem.SNESSystem snes)
            return false;
        if (snes.CPU is not KSNES.CPU.CPU cpu)
            return false;
        pc = cpu.ProgramCounter24;
        regs = cpu.GetTraceState();
        int b0 = snes.Peek(pc);
        int b1 = snes.Peek((pc + 1) & 0xFFFFFF);
        int b2 = snes.Peek((pc + 2) & 0xFFFFFF);
        int b3 = snes.Peek((pc + 3) & 0xFFFFFF);
        opBytes = $"{b0:X2} {b1:X2} {b2:X2} {b3:X2}";
        return true;
    }

    private bool ShouldTraceBwramContext(uint address, string region, uint? resolved)
    {
        if (!_traceSa1BwramWatch || !region.StartsWith("BW-RAM", StringComparison.Ordinal))
            return false;

        if (_traceSa1BwramOffsets.Count == 0)
            return true;

        uint adr16 = address & 0xFFFF;
        uint off16 = (resolved ?? 0) & 0xFFFF;
        return _traceSa1BwramOffsets.Contains(adr16) || _traceSa1BwramOffsets.Contains(off16);
    }

    private static HashSet<uint> ParseTraceOffsets(string? raw)
    {
        var offsets = new HashSet<uint>();
        if (string.IsNullOrWhiteSpace(raw))
            return offsets;

        string[] parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (uint.TryParse(part, System.Globalization.NumberStyles.HexNumber, null, out uint value))
                offsets.Add(value);
        }

        return offsets;
    }

    private byte GetOpenBusByte()
    {
        return (byte)(_system?.OpenBus ?? 0);
    }

    private byte ReadLoRom(int bank, int adr)
    {
        return _data[((bank & (_banks - 1)) << 15) | (adr & 0x7fff)];
    }

    private byte ReadWrappedRom(uint address)
    {
        if (_romLengthIsPowerOfTwo)
            return _data[address & _romLengthMask];

        return _data[address % (uint)_data.Length];
    }

    private byte ReadHiRom(int bank, int adr)
    {
        uint address = (uint)(((bank & 0x7f) << 16) | (adr & 0xffff));
        return ReadWrappedRom(address);
    }

    private byte ReadExHiRom(int bank, int adr)
    {
        uint address = (uint)((bank << 16) | (adr & 0xFFFF));
        uint mapped = (address & 0x3FFFFF) | (((address >> 1) & 0x400000) ^ 0x400000);
        return ReadWrappedRom(mapped);
    }

    private static bool IsHiRomSramBank(int bank)
    {
        int b = bank & 0x7f;
        return b >= 0x20 && b < 0x40;
    }

    private static bool IsSpc7110Chipset(Header header)
    {
        return header.ExCoprocessor == 0 &&
               (header.ChipsetByte == 0xF5 || header.ChipsetByte == 0xF9);
    }

    private static bool IsExHiRomSramBank(int bank)
    {
        return bank >= 0x80 && bank < 0xC0;
    }

    private bool TryReadSrtc(int bank, int adr, out byte value)
    {
        value = 0;
        if (_srtc == null)
            return false;

        int maskedBank = bank & 0x7F;
        if (maskedBank <= 0x3F && adr == 0x2800)
        {
            value = _srtc.Read();
            return true;
        }

        return false;
    }

    private bool TryWriteSrtc(int bank, int adr, byte value)
    {
        if (_srtc == null)
            return false;

        int maskedBank = bank & 0x7F;
        if (maskedBank <= 0x3F && adr == 0x2801)
        {
            _srtc.Write(value);
            return true;
        }

        return false;
    }

    private static byte[]? TryLoadDspRom(DspVariant variant)
    {
        string envName = variant switch
        {
            DspVariant.Dsp1 => "EUTHERDRIVE_DSP1_ROM",
            DspVariant.Dsp2 => "EUTHERDRIVE_DSP2_ROM",
            DspVariant.Dsp3 => "EUTHERDRIVE_DSP3_ROM",
            DspVariant.Dsp4 => "EUTHERDRIVE_DSP4_ROM",
            _ => "EUTHERDRIVE_DSP1_ROM"
        };

        byte[]? configuredRom = TryLoadConfiguredSpecialRom(envName);
        if (configuredRom is not null)
            return configuredRom;

        string[] possiblePaths = variant switch
        {
            DspVariant.Dsp1 => [
                .. EnumerateRepoRelativeBiosPaths("DSP1.bin"),
                .. EnumerateRepoRelativeBiosPaths("dsp1.bin"),
                "/home/nichlas/roms/DSP1 (World) (Enhancement Chip).bin"
            ],
            DspVariant.Dsp2 => [
                .. EnumerateRepoRelativeBiosPaths("DSP2.bin"),
                .. EnumerateRepoRelativeBiosPaths("dsp2.bin"),
                "/home/nichlas/roms/DSP2.bin",
                "/home/nichlas/roms/DSP2 (Enhancement Chip).bin"
            ],
            DspVariant.Dsp3 => [
                .. EnumerateRepoRelativeBiosPaths("DSP3.bin"),
                .. EnumerateRepoRelativeBiosPaths("dsp3.bin"),
                "/home/nichlas/roms/DSP3.bin",
                "/home/nichlas/roms/DSP3 (Enhancement Chip).bin"
            ],
            DspVariant.Dsp4 => [
                .. EnumerateRepoRelativeBiosPaths("DSP4.bin"),
                .. EnumerateRepoRelativeBiosPaths("dsp4.bin"),
                "/home/nichlas/roms/DSP4.bin",
                "/home/nichlas/roms/DSP4 (Enhancement Chip).bin"
            ],
            _ => Array.Empty<string>()
        };

        foreach (string path in possiblePaths)
        {
            if (File.Exists(path))
                return File.ReadAllBytes(path);
        }

        return null;
    }

    private static byte[]? TryLoadSt010Rom()
    {
        byte[]? configuredRom = TryLoadConfiguredSpecialRom("EUTHERDRIVE_ST010_ROM");
        if (configuredRom is not null)
            return configuredRom;

        // Search repo-local BIOS paths first, then compatibility fallbacks.
        string[] possiblePaths = [
            .. EnumerateRepoRelativeBiosPaths("st010.bin"),
            .. EnumerateRepoRelativeBiosPaths("st-010.bin"),
            "/home/nichlas/roms/ST-010.bin",
            "/home/nichlas/roms/ST010.bin",
            "/home/nichlas/roms/ST010 (Enhancement Chip).bin",
            "/home/nichlas/roms/ST-010 (Enhancement Chip).bin"
        ];

        foreach (string path in possiblePaths)
        {
            if (File.Exists(path))
                return File.ReadAllBytes(path);
        }

        return null;
    }

    private static byte[]? TryLoadSt011Rom()
    {
        byte[]? configuredRom = TryLoadConfiguredSpecialRom("EUTHERDRIVE_ST011_ROM");
        if (configuredRom is not null)
            return configuredRom;

        // Search repo-local BIOS paths first, then compatibility fallbacks.
        string[] possiblePaths = [
            .. EnumerateRepoRelativeBiosPaths("st011.bin"),
            .. EnumerateRepoRelativeBiosPaths("ST011.bin"),
            .. EnumerateRepoRelativeBiosPaths("st011.rom"),
            .. EnumerateRepoRelativeBiosPaths("ST011.rom"),
            "/home/nichlas/roms/ST-011.bin",
            "/home/nichlas/roms/ST011.bin",
            "/home/nichlas/roms/ST011 (Enhancement Chip).bin",
            "/home/nichlas/roms/ST-011 (Enhancement Chip).bin"
        ];

        foreach (string path in possiblePaths)
        {
            if (File.Exists(path))
                return File.ReadAllBytes(path);
        }

        return null;
    }

    private static byte[]? TryLoadSt018Rom()
    {
        string? configuredPath = ResolveConfiguredSpecialRomPath("EUTHERDRIVE_ST018_ROM");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            byte[]? configuredRom = TryLoadSt018RomFromPath(configuredPath);
            if (configuredRom is not null)
                return configuredRom;
        }

        byte[]? splitRom = TryLoadSplitSt018Rom();
        if (splitRom is not null)
            return splitRom;

        // Fall back to pre-concatenated blobs and a few compatibility paths.
        string[] possiblePaths = [
            .. EnumerateRepoRelativeBiosPaths("st018.rom"),
            .. EnumerateRepoRelativeBiosPaths("st018.bin"),
            "/home/nichlas/roms/ST-018.bin",
            "/home/nichlas/roms/ST018.bin",
            "/home/nichlas/roms/ST018 (Enhancement Chip).bin",
            "/home/nichlas/roms/ST-018 (Enhancement Chip).bin"
        ];

        foreach (string path in possiblePaths)
        {
            if (File.Exists(path))
                return File.ReadAllBytes(path);
        }

        return null;
    }

    private static byte[]? TryLoadSplitSt018Rom()
    {
        foreach (string programPath in EnumerateRepoRelativeBiosPaths("st018.program.rom"))
        {
            string directory = Path.GetDirectoryName(programPath) ?? string.Empty;
            byte[]? splitRom = TryLoadSplitSt018RomFromDirectory(directory);
            if (splitRom is not null)
                return splitRom;
        }

        return null;
    }

    private static byte[]? TryLoadConfiguredSpecialRom(string environmentVariable)
    {
        string? configuredPath = ResolveConfiguredSpecialRomPath(environmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPath))
            return null;

        return File.ReadAllBytes(configuredPath);
    }

    private static string? ResolveConfiguredSpecialRomPath(string environmentVariable)
    {
        lock (s_specialRomOverridesLock)
        {
            if (s_specialRomOverrides.TryGetValue(environmentVariable, out string? overridePath) &&
                !string.IsNullOrWhiteSpace(overridePath) &&
                File.Exists(overridePath))
            {
                return overridePath;
            }
        }

        string? fromEnv = Environment.GetEnvironmentVariable(environmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
            return fromEnv;

        return null;
    }

    private static byte[]? TryLoadSt018RomFromPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        string fileName = Path.GetFileName(path);
        if (string.Equals(fileName, "st018.program.rom", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "st018.data.rom", StringComparison.OrdinalIgnoreCase))
        {
            return TryLoadSplitSt018RomFromDirectory(Path.GetDirectoryName(path) ?? string.Empty);
        }

        return File.ReadAllBytes(path);
    }

    private static byte[]? TryLoadSplitSt018RomFromDirectory(string directory)
    {
        string? programPath = TryFindSiblingFile(directory, "st018.program.rom");
        string? dataPath = TryFindSiblingFile(directory, "st018.data.rom");
        bool hasProgram = !string.IsNullOrWhiteSpace(programPath);
        bool hasData = !string.IsNullOrWhiteSpace(dataPath);

        if (!hasProgram && !hasData)
            return null;

        if (hasProgram && hasData)
        {
            byte[] programRom = File.ReadAllBytes(programPath!);
            byte[] dataRom = File.ReadAllBytes(dataPath!);
            byte[] combined = new byte[programRom.Length + dataRom.Length];
            Buffer.BlockCopy(programRom, 0, combined, 0, programRom.Length);
            Buffer.BlockCopy(dataRom, 0, combined, programRom.Length, dataRom.Length);
            return combined;
        }

        throw new FileNotFoundException(
            $"ST018 BIOS set incomplete in '{directory}'; expected both st018.program.rom and st018.data.rom.");
    }

    private static string? TryFindSiblingFile(string directory, string fileName)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return null;

        string exactPath = Path.Combine(directory, fileName);
        if (File.Exists(exactPath))
            return exactPath;

        foreach (string candidate in Directory.EnumerateFiles(directory))
        {
            if (string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private static IEnumerable<string> EnumerateRepoRelativeBiosPaths(string fileName)
    {
        HashSet<string> yielded = new(StringComparer.OrdinalIgnoreCase);
        foreach (string root in EnumerateSearchRoots())
        {
            string? current = root;
            while (!string.IsNullOrWhiteSpace(current))
            {
                string candidate = Path.Combine(current, "bios", fileName);
                if (yielded.Add(candidate))
                    yield return candidate;

                DirectoryInfo? parent = Directory.GetParent(current);
                current = parent?.FullName;
            }
        }
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }

    private static int ParseTraceLimit(string envName, int defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int limit) && limit > 0)
            return limit;
        return defaultValue;
    }

    private static int ParseTraceHex(string envName, int defaultValue)
    {
        string? raw = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];

        return int.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value)
            ? value
            : defaultValue;
    }

    private readonly struct Dsp1PortMapping
    {
        private readonly int _bankStart;
        private readonly int _bankEndExclusive;
        private readonly int _offsetMask;

        private Dsp1PortMapping(int bankStart, int bankEndExclusive, int offsetMask)
        {
            _bankStart = bankStart;
            _bankEndExclusive = bankEndExclusive;
            _offsetMask = offsetMask;
        }

        public static Dsp1PortMapping FromVariantAndMetadata(DspVariant variant, int romSize, int sramSize)
        {
            if (variant == DspVariant.Dsp4)
                return new Dsp1PortMapping(0x30, 0x40, 0x8000);
            if (variant == DspVariant.Dsp2 || variant == DspVariant.Dsp3)
                return new Dsp1PortMapping(0x20, 0x40, 0x8000);
            if (romSize > 1024 * 1024)
                return new Dsp1PortMapping(0x60, 0x70, 0x0000);
            if (sramSize != 0)
                return new Dsp1PortMapping(0x20, 0x40, 0x8000);
            return new Dsp1PortMapping(0x30, 0x40, 0x8000);
        }

        public bool IsDspPort(int bank, int adr)
        {
            int bankMasked = bank & 0x7f;
            return bankMasked >= _bankStart && bankMasked < _bankEndExclusive && (adr & 0x8000) == _offsetMask;
        }
    }
}
