using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using EutherDrive.Core.Savestates;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core;

public sealed class MdTracerAdapter : IEmulatorCore, ISavestateCapable
{
    private const int DefaultW = 320;
    private const int DefaultH = 224;
    private readonly md_vdp _vdp = new md_vdp();

    private byte[] _frameBufferFront = Array.Empty<byte>(); // BGRA till UI (read)
    private byte[] _frameBufferBack = Array.Empty<byte>(); // BGRA till UI (write)
    private int _fbW, _fbH, _fbStride;
    private int _fbLogW = -1;
    private int _fbLogH = -1;
    private int _fbLogStride = -1;
    private int _fbIdentityLogCount;
    private long _fbPresentCount;
    private const int PresentSampleEveryFrames = 60;
    private long _lastVdpLogTicks;
    private long _lastPresentLogTicks;
    private long _lastSampleLogTicks;
    private long _lastAudioLogTicks;
    private long _lastAudioLevelTicks;
    private long _lastAudioCoreLogTicks;

    private int _tick;
    private int _bootRecoverStallCount;
    private int _bootRecoverFrameCount;
    private bool _bootRecoverCompleted;
    private ushort _bootRecoverLastPc;
    private const int VLINES_NTSC = 262;
    private const int VLINES_PAL = 312;
    private const double FPS_NTSC = 60.0;
    private const double FPS_PAL = 50.0;
    private uint _lastPc;
    private int _pcStallFrames;
    private FrameRateMode _frameRateMode = FrameRateMode.Auto;
    private FrameRateMode _lastAppliedFrameRateMode = FrameRateMode.Auto;
    private int _cpuCyclesPerLine;
    private ConsoleRegion _regionOverride = ConsoleRegion.Auto;

    private int _bootRecoverStablePcFrames;
    private long _bootRecoverLastBusReqToggles;
    private long _bootRecoverLastResetToggles;
    private long _bootRecoverToggleAccum;
    private bool _bootRecoverSigInit;
    private bool _forceFff600Applied;
    private bool _forceFff600EnvLogged;
    private int _forceFff600Frames;

    private static readonly bool DumpVectorsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DUMP_VECTORS"), "1", StringComparison.Ordinal);
    private static readonly bool FrameBufferTraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FB_TRACE"), "1", StringComparison.Ordinal);

    private readonly SavestateService _savestateService = new SavestateService();
    private static readonly bool TraceAudioEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO"), "1", StringComparison.Ordinal);
    private static readonly bool TraceAudioLevel =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDLVL"), "1", StringComparison.Ordinal);
    private static readonly bool TracePerf =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_PERF"), "1", StringComparison.Ordinal);
    private static readonly bool SkipVdpRenderEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SKIP_VDP_RENDER"), "1", StringComparison.Ordinal);
    private static readonly int ForceFff600AfterFrames = ParseForceFff600AfterFrames();
    private static readonly byte ForceFff600Value = ParseHexByteEnv("EUTHERDRIVE_FORCE_FFF600_VALUE", 0x08);
    private static readonly byte ForceFff600When = ParseHexByteEnv("EUTHERDRIVE_FORCE_FFF600_WHEN", 0x10);
    private static bool ForceFff600Enabled => ForceFff600AfterFrames > 0;
    private static readonly double Z80CycleMultiplier = ParseZ80CycleMultiplier();
    private static readonly int BootRecoverStallFrames = ParseBootRecoverStallFrames();
    private static readonly int BootRecoverWindowFrames = ParseBootRecoverWindowFrames();
    private static readonly int BootRecoverEdgeToggleThreshold = ParseBootRecoverEdgeToggleThreshold();
    private static readonly int BootRecoverEdgeStableFrames = ParseBootRecoverEdgeStableFrames();
    private static readonly bool BootRecoverLog =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_BOOT_RECOVER_LOG"), "1", StringComparison.Ordinal);

    // ROM + BUS
    private byte[]? _rom;
    private MegaDriveBus? _bus;
    private readonly object _loadLock = new();
    private readonly object _stateLock = new();
    private RomIdentity? _romIdentity;

    // CPU runner (MDTracer m68k via reflection)
    private MdTracerM68kRunner? _cpu;
    private bool _cpuReady;

    // Simple perf tracking
    private long _accCpuTicks;
    private long _accVdpTicks;
    private long _accFrameTicks;
    private int _perfFrameCount;
    private int _lastGc0;
    private int _lastGc1;
    private int _lastGc2;
    private readonly long[] _hotspotTicks = new long[(int)PerfHotspot.Count];

    private const int PsgSampleRate = 44100;
    private const int PsgChannels = 2;
    private bool _ymEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM"), "1", StringComparison.Ordinal);
    private readonly bool _psgDisabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DISABLE_PSG"), "1", StringComparison.Ordinal);
    private double _psgFrameAccumulator;
    private short[] _psgFrameBuffer = Array.Empty<short>();
    private short[] _ymFrameBuffer = Array.Empty<short>();
    private int _psgFrameSamples;
    private long _psgLastFrame = -1;
    private volatile int _masterVolumePercent = 50;

    public void SetMasterVolumePercent(int percent)
    {
        if (percent < 0) percent = 0;
        else if (percent > 100) percent = 100;
        _masterVolumePercent = percent;
    }

    public void SetZ80BypassEnabled(bool enabled)
    {
        md_bus.SetZ80Bypass(enabled, onlyWhenInactive: true, readValue: 0x01, readAddrs: null);
        md_main.SetZ80Disabled(enabled);
    }

    public void SetBootWaitHackEnabled(bool enabled)
    {
        md_bus.SetBootWaitHack(enabled);
    }

    public void SetForceVdpDisplayEnabled(bool enabled)
    {
        md_vdp.SetForceDisplayEnabled(enabled);
    }

    private void ApplyMasterVolume(short[] buffer, int samples)
    {
        if (samples <= 0)
            return;

        int percent = Volatile.Read(ref _masterVolumePercent);
        if (percent >= 100)
            return;

        for (int i = 0; i < samples; i++)
        {
            int scaled = buffer[i] * percent / 100;
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            else if (scaled < short.MinValue) scaled = short.MinValue;
            buffer[i] = (short)scaled;
        }
    }

    public RomInfo RomInfo { get; private set; } = new RomInfo();

    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => md_main.g_md_vdp?.FrameCounter;

    public void SetYmEnabled(bool enabled)
    {
        _ymEnabled = enabled;
        md_bus.SetYmEnabled(enabled);
    }

    public void SetRegionOverride(ConsoleRegion region)
    {
        _regionOverride = region;
        if (md_main.g_md_io != null)
            md_main.g_md_io.SetRegionOverride(region);
    }

    public void RunInterlaceMode2Test()
    {
        MdVdpInterlaceMode2PatternTest.Run();
    }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("ROM path is empty.", nameof(path));

        Console.WriteLine($"[MdTracerAdapter] LoadRom request: path='{path}' ext='{Path.GetExtension(path)}' thread={Thread.CurrentThread.ManagedThreadId}");

        bool lockTaken = Monitor.TryEnter(_loadLock);
        const int maxAttempts = 50;
        int attempts = 0;
        while (!lockTaken && attempts++ < maxAttempts)
        {
            Thread.Sleep(10);
            lockTaken = Monitor.TryEnter(_loadLock);
        }

        if (!lockTaken)
        {
            Console.WriteLine("[MdTracerAdapter] LoadRom: lock timeout (500ms) – aborting load");
            return;
        }

        try
        {
            string ext = Path.GetExtension(path)?.ToLowerInvariant() ?? string.Empty;
            bool isSms = ext == ".sms" || ext == ".sg" || ext == ".gg";

            if (!File.Exists(path))
            {
                Console.WriteLine($"[MdTracerAdapter] LoadRom: file '{path}' not found.");
                return;
            }

            byte[] rawData = File.ReadAllBytes(path);
            if (rawData.Length >= 6 && Is7zHeader(rawData))
            {
                Console.WriteLine("[MdTracerAdapter] LoadRom: 7z archives are unsupported; please extract first.");
                return;
            }
            _romIdentity = new RomIdentity(Path.GetFileNameWithoutExtension(path), RomIdentity.ComputeSha256(rawData));

            md_main.PowerCycleReset();
            md_main.initialize();
            MdTracerCore.MdLog.MaybeLogTraceBuildStamp();
            md_main.g_md_vdp = _vdp;

            // Set up interrupt acknowledge callback for Panorama Cotton debugging
            _vdp.OnInterruptAck = level =>
            {
                long frame = _vdp.FrameCounter;
                Console.WriteLine($"[VDP-INT-ACK] frame={frame} level={level} pc=0x{MdTracerCore.md_m68k.g_reg_PC:X6}");
            };

            if (isSms)
            {
                byte[] smsRom = NormalizeSmsRom(rawData);
                _rom = smsRom;
                md_main.g_masterSystemMode = true;
                md_main.g_masterSystemRom = smsRom;
                md_main.g_masterSystemRomSize = smsRom.Length;
                if (md_main.g_md_io != null)
                    md_main.g_md_io.SetRomRegionHint(null);
                if (md_main.g_md_cartridge != null)
                {
                    md_main.g_md_cartridge.g_file = Array.Empty<byte>();
                    md_main.g_md_cartridge.g_file_size = 0;
                }
                _bus = null;
                md_bus.Current = null;
                EutherDrive.Core.MdTracerCore.md_bus.Current = null;
                _cpuReady = false;
                _cpu = null;

                RomInfo.Summary = $"SMS ROM bytes: {smsRom.Length}";
                RomInfo.RegionHint = null;
                RomInfo.RegionHeaderRaw = string.Empty;
                RomInfo.SerialNumber = string.Empty;
            }
            else
            {
                md_main.g_masterSystemMode = false;
                md_main.g_masterSystemRom = Array.Empty<byte>();
                md_main.g_masterSystemRomSize = 0;

                md_main.g_md_cartridge ??= new md_cartridge();
                bool loaded = md_main.g_md_cartridge.load(path);
                if (!loaded)
                {
                    Console.WriteLine("[MdTracerAdapter] LoadRom: md_cartridge.load failed, using fallback ROM data.");
                    byte[] normalized = md_rom_utils.NormalizeMegaDriveRom(rawData).Data;
                    md_main.g_md_cartridge.g_file = normalized;
                    md_main.g_md_cartridge.g_file_size = normalized.Length;
                    md_main.g_md_cartridge.g_file_path = path;
                }

                bool useNormalizedForBus = md_main.g_md_cartridge.g_smd_header_size > 0 || md_main.g_md_cartridge.g_smd_deinterleaved;
                _rom = useNormalizedForBus ? md_main.g_md_cartridge.g_file : rawData;
                _bus = new MegaDriveBus(_rom);
                md_bus.Current = _bus;
                EutherDrive.Core.MdTracerCore.md_bus.Current = _bus;

                DumpVectors();

                byte[] vecRom = md_main.g_md_cartridge?.g_file ?? _rom ?? rawData;
                string header = TryReadSegaString(vecRom);
                string title = TryReadRomTitle(vecRom);
                
                if (title.Contains("PANORAMA COTTON", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("panoramacotton", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"[BOOT-HACK] Panorama Cotton detected: hacks disabled by default. title='{title}'");
                }
                _bus.Write32(0xFF0000, 0x1234ABCD);
                uint wramProbe = _bus.Read32(0xFF0000);
                ConsoleRegion? regionHint = md_rom_utils.DetectRegionFromHeader(vecRom, out string regionRaw);
                string serial = md_main.g_md_cartridge?.g_serial_number ?? string.Empty;
                regionHint = AdjustRegionHint(regionHint, regionRaw, serial);
                RomInfo.RegionHint = regionHint;
                RomInfo.RegionHeaderRaw = regionRaw;
                RomInfo.SerialNumber = serial;
                string regionLabel = regionHint?.ToString() ?? ConsoleRegion.Auto.ToString();
                Console.WriteLine($"[MdTracerAdapter] Detected ROM region: {regionLabel} (raw='{regionRaw}')");
                if (md_main.g_md_io != null)
                    md_main.g_md_io.SetRomRegionHint(regionHint);

                uint sp = ReadBe32(vecRom, 0x000000);
                uint pc = ReadBe32(vecRom, 0x000004);
                if (!ValidateVectors(sp, pc, vecRom.Length))
                {
                    Console.WriteLine("[MdTracerAdapter] LoadRom: invalid vector table detected; aborting.");
                    return;
                }

                ushort op = ReadBe16(vecRom, (int)pc);

                try
                {
                    _cpu = new MdTracerM68kRunner();
                    _cpuReady = true;
                }
                catch (Exception ex)
                {
                    _cpuReady = false;
                    _cpu = null;
                    RomInfo.Summary = $"ROM bytes: {_rom.Length} | {header} | WRAM@FF0000: 0x{wramProbe:X8} | VEC SP=0x{sp:X8} PC=0x{pc:X8} OP@PC=0x{op:X4} | CPU: {ex.Message}";
                    Reset();
                    return;
                }

                RomInfo.Summary =
                $"ROM bytes: {_rom.Length} | {header} | WRAM@FF0000: 0x{wramProbe:X8} | " +
                $"VEC SP=0x{sp:X8} PC=0x{pc:X8} OP@PC=0x{op:X4} | CPU API ok";
            }

            Console.WriteLine($"[ROMMODE] type={(isSms ? "SMS" : "MD")} masterSystemMode={(md_main.g_masterSystemMode ? 1 : 0)}");
            ArmBootRecover();
            Reset();
            LogFrameBufferIdentity("LoadRom");
        }
        finally
        {
            Monitor.Exit(_loadLock);
        }
    }

    private static string TryReadSegaString(MegaDriveBus bus)
    {
        Span<byte> s = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
            s[i] = bus.Read8((uint)(0x100 + i));

        var text = Encoding.ASCII.GetString(s);
        return $"Header@0x100: '{text}'";
    }

    private static string TryReadSegaString(byte[] rom)
    {
        if (rom.Length < 0x104)
            return "Header@0x100: '(too small)'";

        Span<byte> s = stackalloc byte[4];
        s[0] = rom[0x100];
        s[1] = rom[0x101];
        s[2] = rom[0x102];
        s[3] = rom[0x103];
        var text = Encoding.ASCII.GetString(s);
        return $"Header@0x100: '{text}'";
    }

    private static string TryReadRomTitle(byte[] rom)
    {
        const int titleStart = 0x120;
        const int titleLen = 0x30;
        if (rom.Length < titleStart + titleLen)
            return string.Empty;
        Span<byte> s = stackalloc byte[titleLen];
        for (int i = 0; i < titleLen; i++)
            s[i] = rom[titleStart + i];
        string text = Encoding.ASCII.GetString(s).Trim();
        return text;
    }

    private static double ParseZ80CycleMultiplier()
    {
        const double fallback = 1.0;
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_Z80_CYCLES_MULT");
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) && parsed > 0.0)
            return parsed;
        return fallback;
    }

    private static int ParseBootRecoverStallFrames()
    {
        const int fallback = 0;
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_BOOT_RECOVER_STALL_FRAMES");
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
            return parsed;
        return fallback;
    }

    private static int ParseBootRecoverWindowFrames()
    {
        const int fallback = 0;
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_BOOT_RECOVER_WINDOW_FRAMES");
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
            return parsed;
        return fallback;
    }

    private static int ParseBootRecoverEdgeToggleThreshold()
    {
        const int fallback = 0;
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_BOOT_RECOVER_EDGE_TOGGLES");
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
            return parsed;
        return fallback;
    }

    private static int ParseBootRecoverEdgeStableFrames()
    {
        const int fallback = 0;
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_BOOT_RECOVER_EDGE_STABLE_FRAMES");
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
            return parsed;
        return fallback;
    }

    private static int ParseForceFff600AfterFrames()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_FORCE_FFF600_AFTER_FRAMES");
        if (string.IsNullOrWhiteSpace(raw))
            return 0;
        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0)
            return value;
        return 0;
    }

    private static byte ParseHexByteEnv(string name, byte fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw.Substring(2);
        if (byte.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
            return value;
        return fallback;
    }

    private void MaybeForceFff600(long frame)
    {
        if (!ForceFff600Enabled || _forceFff600Applied)
            return;

        var bus = md_main.g_md_bus;
        if (bus == null)
            return;

        if (!_forceFff600EnvLogged)
        {
            Console.WriteLine($"[FFF600-FORCE-ENV] after={ForceFff600AfterFrames} when=0x{ForceFff600When:X2} value=0x{ForceFff600Value:X2}");
            _forceFff600EnvLogged = true;
        }

        byte state = bus.read8(0x00FFF600);
        if (state == ForceFff600When)
        {
            _forceFff600Frames++;
        }
        else
        {
            _forceFff600Frames = 0;
        }

        if (_forceFff600Frames >= ForceFff600AfterFrames)
        {
            bus.write8(0x00FFF600, ForceFff600Value);
            _forceFff600Applied = true;
            Console.WriteLine($"[FFF600-FORCE] frame={frame} old=0x{state:X2} new=0x{ForceFff600Value:X2} after={_forceFff600Frames}");
        }
    }

    private static ConsoleRegion? ParseRegionOverrideEnv()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_REGION");
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "jp":
            case "japan":
                return ConsoleRegion.JP;
            case "us":
            case "usa":
                return ConsoleRegion.US;
            case "eu":
            case "europe":
                return ConsoleRegion.EU;
            case "auto":
                return null;
            default:
                return null;
        }
    }

    private static ConsoleRegion? AdjustRegionHint(ConsoleRegion? hint, string regionRaw, string serial)
    {
        string upper = regionRaw?.Trim().ToUpperInvariant() ?? string.Empty;
        string serialUpper = serial?.Trim().ToUpperInvariant() ?? string.Empty;
        bool hasJ = upper.Contains('J');
        bool hasU = upper.Contains('U');
        bool hasE = upper.Contains('E');
        bool serialPal = serialUpper.EndsWith("-50", StringComparison.Ordinal);

        if (serialPal && hasE)
            return ConsoleRegion.EU;
        if (hint.HasValue)
            return hint;
        if (hasE && !hasJ && !hasU)
            return ConsoleRegion.EU;
        if (hasU && !hasJ && !hasE)
            return ConsoleRegion.US;
        if (hasJ && !hasU && !hasE)
            return ConsoleRegion.JP;
        if (serialPal)
            return ConsoleRegion.EU;
        return hint;
    }

    private static ushort ReadBe16(byte[] rom, int offset)
    {
        if ((uint)(offset + 1) >= (uint)rom.Length)
            return 0;
        return (ushort)((rom[offset] << 8) | rom[offset + 1]);
    }

    private static uint ReadBe32(byte[] rom, int offset)
    {
        if ((uint)(offset + 3) >= (uint)rom.Length)
            return 0;
        return (uint)((rom[offset] << 24) | (rom[offset + 1] << 16) | (rom[offset + 2] << 8) | rom[offset + 3]);
    }
    private static byte[] NormalizeSmsRom(byte[] rawData)
    {
        if (rawData.Length >= 0x200 && (rawData.Length % 0x4000) == 0x200)
        {
            byte[] trimmed = new byte[rawData.Length - 0x200];
            Buffer.BlockCopy(rawData, 0x200, trimmed, 0, trimmed.Length);
            return trimmed;
        }

        return rawData;
    }

    private void ArmBootRecover()
    {
        _bootRecoverStallCount = 0;
        _bootRecoverFrameCount = 0;
        _bootRecoverCompleted = false;
        _bootRecoverLastPc = 0;
        _bootRecoverStablePcFrames = 0;
        _bootRecoverLastBusReqToggles = 0;
        _bootRecoverLastResetToggles = 0;
        _bootRecoverToggleAccum = 0;
        _bootRecoverSigInit = false;
    }

    private void ResetZ80Only()
    {
        if (md_main.g_md_z80 == null)
            return;
        md_main.BeginZ80ResetCycle();
        md_main.g_md_z80.reset();
        md_main.g_md_z80.ArmPostResetHold();
        bool busReq = md_main.g_md_bus?.Z80BusGranted ?? false;
        bool reset = md_main.g_md_bus?.Z80Reset ?? false;
        md_main.g_md_z80.g_active = !busReq && !reset;
    }

    public void Reset()
    {
        Console.WriteLine("[MdTracerAdapter] Reset begin");
        Console.WriteLine($"[MdTracerAdapter] Reset _cpuReady={_cpuReady} _cpu={(_cpu != null ? "ok" : "null")}");
        _tick = 0;

        // Nollställ RAM
        _bus?.Reset();
        md_main.g_md_bus?.Reset();
        md_main.ResetZ80WaitState();
        _vdp.reset();
        _lastAppliedFrameRateMode = GetEffectiveFrameRateMode();
        ApplyFrameRateMode(_lastAppliedFrameRateMode);
        ResetAudioFrameState();
        md_main.g_md_music?.reset();

        // Stub (så VDP-testet fortsätter)
        md_main.EnsureCpuStubs();

        // Init/reset CPU om vi har den
        if (_cpuReady && _cpu != null)
        {
            // md_bus.Current är redan satt i LoadRom
            _cpu.EnsureInitAndReset();

            // Bra att skriva en gång i terminalen (kan tas bort sen)
            MdTracerCore.MdLog.WriteLine("m68k runner ok. Runner: " + _cpu.SelectedRunApi);
            MdTracerCore.MdLog.WriteLine("Methods:\n" + _cpu.DebugApi);
        }

        _vdp.SetFrameSize(DefaultW, DefaultH);
        EnsureFramebufferInitialized("Reset");
        Array.Clear(_frameBufferFront, 0, _frameBufferFront.Length);
        Array.Clear(_frameBufferBack, 0, _frameBufferBack.Length);
        Console.WriteLine($"[MdTracerAdapter] Reset framebuffer { _fbW }x{ _fbH } stride={ _fbStride }");
        LogFrameBufferIdentity("Reset");
    }

    public void SetFrameRateMode(FrameRateMode mode)
    {
        _frameRateMode = mode;
    }

    public double GetTargetFps()
    {
        return GetEffectiveFrameRateMode() == FrameRateMode.Hz50 ? FPS_PAL : FPS_NTSC;
    }

    public void SetCpuCyclesPerLine(int cycles)
    {
        if (cycles <= 0)
            throw new ArgumentOutOfRangeException(nameof(cycles), "Cycles must be positive.");
        _cpuCyclesPerLine = cycles;
    }

    private FrameRateMode GetEffectiveFrameRateMode()
    {
        if (_frameRateMode != FrameRateMode.Auto)
            return _frameRateMode;

        if (RomInfo.RegionHint == ConsoleRegion.EU)
            return FrameRateMode.Hz50;
        if (RomInfo.RegionHint == ConsoleRegion.JP || RomInfo.RegionHint == ConsoleRegion.US)
            return FrameRateMode.Hz60;

        ConsoleRegion region = GetEffectiveRegion();
        return region == ConsoleRegion.EU ? FrameRateMode.Hz50 : FrameRateMode.Hz60;
    }

    private ConsoleRegion GetEffectiveRegion()
    {
        ConsoleRegion? envOverride = ParseRegionOverrideEnv();
        if (envOverride.HasValue)
            return envOverride.Value;
        if (_regionOverride != ConsoleRegion.Auto)
            return _regionOverride;
        if (RomInfo.RegionHint.HasValue)
            return RomInfo.RegionHint.Value;
        return ConsoleRegion.US;
    }

    private int ApplyFrameRateMode(FrameRateMode mode)
    {
        int lines = mode == FrameRateMode.Hz50 ? VLINES_PAL : VLINES_NTSC;
        if (_vdp.g_vertical_line_max != lines)
        {
            _vdp.g_vertical_line_max = lines;
            _vdp.g_vdp_reg_1_3_cellmode = (byte)(lines == VLINES_PAL ? 1 : 0);
            _vdp.g_vdp_status_0_tvmode = (byte)(lines == VLINES_PAL ? 1 : 0);
        }
        return lines;
    }

    private void ResetAudioFrameState()
    {
        _psgFrameAccumulator = 0;
        _psgFrameSamples = 0;
        _psgLastFrame = -1;
    }

    public void PowerCycleAndLoadRom(string path) => LoadRom(path);

    private void EnsureFramebufferInitialized(string reason)
    {
        int w = _vdp.FrameWidth;
        int h = _vdp.FrameHeight;
        if (w <= 0 || h <= 0)
        {
            w = DefaultW;
            h = DefaultH;
            _vdp.SetFrameSize(w, h);
        }

        int stride = Math.Max(4, w * 4);
        if (_fbW != w || _fbH != h || _fbStride != stride)
        {
            _fbW = w;
            _fbH = h;
            _fbStride = stride;
            MdTracerCore.MdLog.WriteLine($"[MdTracerAdapter] EnsureFramebufferInitialized({reason}) -> {_fbW}x{_fbH} stride={_fbStride}");
        }

        int needed = _fbW * _fbH * 4;
        if (_frameBufferFront.Length != needed)
            _frameBufferFront = new byte[needed];
        if (_frameBufferBack.Length != needed)
            _frameBufferBack = new byte[needed];

        if (FrameBufferTraceEnabled && _fbIdentityLogCount++ < 10)
        {
            var vdpBuffer = _vdp.GetFrameBuffer();
            int vdpId = vdpBuffer.Length == 0 ? 0 : RuntimeHelpers.GetHashCode(vdpBuffer);
            Console.WriteLine($"[MdTracerAdapter] Framebuffer source at {reason}: vdp=0x{vdpId:X8}");
        }
    }

    private void DumpVectors()
    {
        if (!DumpVectorsEnabled || _rom == null || _bus == null)
            return;

        int count = Math.Min(_rom.Length, 32);
        var sb = new StringBuilder(count * 3);
        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                sb.Append(' ');
            sb.Append(_rom[i].ToString("X2"));
        }

        Console.WriteLine($"[ROM0] {sb}");
        uint sp = _bus.Read32(0x000000);
        uint pc = _bus.Read32(0x000004);
        Console.WriteLine($"[VECTORS] SP=0x{sp:X8} PC=0x{pc:X8}");
    }

    private static bool Is7zHeader(ReadOnlySpan<byte> header)
        => header.Length >= 6 &&
           header[0] == 0x37 && header[1] == 0x7A && header[2] == 0xBC &&
           header[3] == 0xAF && header[4] == 0x27 && header[5] == 0x1C;

    private void ValidateVectors(uint sp, uint pc)
    {
        if (_rom == null)
            return;

        bool spReasonable = sp != 0 && (sp >= 0x00F00000 || sp >= 0xFFF00000);
        bool pcReasonable = pc < Math.Min(_rom.Length, 0x400000);
        Console.WriteLine($"[MdTracerAdapter] Vector check SP=0x{sp:X8} ({(spReasonable ? "ok" : "sus")}) PC=0x{pc:X8} ({(pcReasonable ? "ok" : "sus")})");
    }

    private bool ValidateVectors(uint sp, uint pc, int romLength)
    {
        Console.WriteLine($"[MdTracerAdapter] Vector check SP=0x{sp:X8} PC=0x{pc:X8}");
        return pc > 0 && pc < romLength && pc < 0x400000;
    }

    public void RunFrame()
    {
        lock (_stateLock)
        {
            if (_tick == 0)
                MdTracerCore.MdLog.WriteLine("[MdTracerAdapter] RunFrame start");
            if (_tick == 0)
            {
                _forceFff600Applied = false;
                _forceFff600EnvLogged = false;
                _forceFff600Frames = 0;
            }

        _tick++;
        if (!_bootRecoverCompleted && (BootRecoverStallFrames > 0 || BootRecoverEdgeToggleThreshold > 0))
        {
            _bootRecoverFrameCount++;
            if (BootRecoverWindowFrames > 0 && _bootRecoverFrameCount > BootRecoverWindowFrames)
            {
                _bootRecoverCompleted = true;
            }
            else
            {
                var z80 = md_main.g_md_z80;
                var bus = md_main.g_md_bus;
                bool busReq = bus?.Z80BusGranted ?? false;
                bool reset = bus?.Z80Reset ?? false;
                bool active = z80?.g_active ?? false;
                ushort pc = z80?.CpuPc ?? (ushort)0;

                bool pcStable = pc == _bootRecoverLastPc;
                if (pcStable)
                {
                    _bootRecoverStablePcFrames++;
                }
                else
                {
                    _bootRecoverStablePcFrames = 0;
                    _bootRecoverToggleAccum = 0;
                    _bootRecoverLastPc = pc;
                }

                if (BootRecoverEdgeToggleThreshold > 0 && bus != null)
                {
                    if (!_bootRecoverSigInit)
                    {
                        bus.PeekZ80SignalStats(out _, out long busReqToggles, out _, out long resetToggles);
                        _bootRecoverLastBusReqToggles = busReqToggles;
                        _bootRecoverLastResetToggles = resetToggles;
                        _bootRecoverSigInit = true;
                    }
                    else
                    {
                        bus.PeekZ80SignalStats(out _, out long busReqToggles, out _, out long resetToggles);
                        long deltaBusReqToggles = busReqToggles - _bootRecoverLastBusReqToggles;
                        long deltaResetToggles = resetToggles - _bootRecoverLastResetToggles;
                        if (deltaBusReqToggles < 0)
                            deltaBusReqToggles = 0;
                        if (deltaResetToggles < 0)
                            deltaResetToggles = 0;
                        _bootRecoverLastBusReqToggles = busReqToggles;
                        _bootRecoverLastResetToggles = resetToggles;

                        if (pcStable)
                            _bootRecoverToggleAccum += deltaBusReqToggles + deltaResetToggles;

                        int stableFramesTarget = BootRecoverEdgeStableFrames > 0
                            ? BootRecoverEdgeStableFrames
                            : (BootRecoverStallFrames > 0 ? BootRecoverStallFrames : 1);
                        if (stableFramesTarget > 0 &&
                            _bootRecoverStablePcFrames >= stableFramesTarget &&
                            _bootRecoverToggleAccum >= BootRecoverEdgeToggleThreshold &&
                            !active)
                        {
                            _bootRecoverCompleted = true;
                            if (BootRecoverLog)
                            {
                                Console.WriteLine(
                                    $"[BOOTRECOVER] reset after edge toggles={_bootRecoverToggleAccum} " +
                                    $"stable={_bootRecoverStablePcFrames} pc=0x{pc:X4} " +
                                    $"busReqT={deltaBusReqToggles} resetT={deltaResetToggles} " +
                                    $"busReq={(busReq ? 1 : 0)} reset={(reset ? 1 : 0)}");
                            }
                            ResetZ80Only();
                            return;
                        }
                    }
                }

                bool stalled = busReq && !reset && !active && pcStable;
                if (stalled)
                {
                    _bootRecoverStallCount++;
                }
                else
                {
                    _bootRecoverStallCount = 0;
                }
                if (BootRecoverStallFrames > 0 && _bootRecoverStallCount >= BootRecoverStallFrames)
                {
                    _bootRecoverCompleted = true;
                    if (BootRecoverLog)
                    {
                        Console.WriteLine(
                            $"[BOOTRECOVER] reset after stall frames={_bootRecoverStallCount} " +
                            $"pc=0x{pc:X4} busReq={(busReq ? 1 : 0)} reset={(reset ? 1 : 0)}");
                    }
                    ResetZ80Only();
                    return;
                }
            }
        }
        long frameStart = TracePerf ? Stopwatch.GetTimestamp() : 0;

        if (md_main.g_masterSystemMode)
        {
            md_main.RunFrame();
        }
        else
        {
            var effectiveFrameRateMode = GetEffectiveFrameRateMode();
            if (effectiveFrameRateMode != _lastAppliedFrameRateMode)
            {
                _lastAppliedFrameRateMode = effectiveFrameRateMode;
                ResetAudioFrameState();
            }
            int vlines = ApplyFrameRateMode(effectiveFrameRateMode);
            long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
            bool allowZ80 = md_main.ShouldRunZ80(frame);
            uint pcAfter = md_m68k.g_reg_PC;
            if (_cpuReady && _cpu != null)
            {
                long cpuTicks = 0;
                long vdpTicks = 0;
                int z80Budget = Math.Max(1, (int)(md_main.VDL_LINE_RENDER_Z80_CLOCK * Z80CycleMultiplier));
                int cpuBudget = _cpuCyclesPerLine > 0 ? _cpuCyclesPerLine : md_main.VDL_LINE_RENDER_MC68_CLOCK;

                for (int v = 0; v < vlines; v++)
                {
                    if (!SkipVdpRenderEnabled)
                    {
                        if (TracePerf)
                        {
                            long start = Stopwatch.GetTimestamp();
                            _vdp.run(v);
                            vdpTicks += Stopwatch.GetTimestamp() - start;
                        }
                        else
                        {
                            _vdp.run(v);
                        }
                    }

                    if (TracePerf)
                    {
                        long cpuStart = Stopwatch.GetTimestamp();
                        _cpu.RunSome(budget: cpuBudget);
                        cpuTicks += Stopwatch.GetTimestamp() - cpuStart;
                    }
                    else
                    {
                        _cpu.RunSome(budget: cpuBudget);
                    }

                    if (allowZ80)
                        md_main.g_md_z80?.run(z80Budget);
                }

                if (TracePerf)
                {
                    _accCpuTicks += cpuTicks;
                    _accVdpTicks += vdpTicks;
                    PerfHotspots.Add(PerfHotspot.CpuStep, cpuTicks);
                    PerfHotspots.Add(PerfHotspot.VdpRender, vdpTicks);
                }

                pcAfter = md_m68k.g_reg_PC;
                if (pcAfter == _lastPc)
                    _pcStallFrames++;
                else
                    _pcStallFrames = 0;
                _lastPc = pcAfter;

                if ((_tick % 60) == 0)
                {
                    ushort vdpStatus = md_main.g_md_vdp.read16(0xC00004);
                    ushort hv = md_main.g_md_vdp.read16(0xC00008);
                    ushort op0 = md_m68k.read16(pcAfter);
                    ushort op1 = md_m68k.read16(pcAfter + 2);
                    uint d1 = md_m68k.g_reg_data[1].l;
                    MdTracerCore.MdLog.WriteLine($"m68k PC=0x{pcAfter:X6} stall={_pcStallFrames} VDP=0x{vdpStatus:X4} HV=0x{hv:X4} D1=0x{d1:X8} OP=0x{op0:X4} NXT=0x{op1:X4}");
                }
            }
            else
            {
                // Fortfarande VDP-test tills vi kopplar VDP-register/IO
                if (!SkipVdpRenderEnabled)
                {
                    for (int v = 0; v < vlines; v++)
                        _vdp.run(v);
                }
            }

            MaybeForceFff600(frame);
            md_main.MaybeInjectMbx(frame);
            md_main.g_md_music?.g_md_ym2612.FlushDacRateFrame(frame);
            md_main.g_md_music?.FlushAudioStats(frame);
            md_main.g_md_bus?.FlushZ80WinHist(frame);
            md_main.g_md_bus?.FlushZ80WinStat(frame);
            md_main.g_md_bus?.FlushMbx68kStat(frame);
            md_main.g_md_bus?.TickZ80SafeBoot(frame);
            md_main.g_md_z80?.FlushZ80MbxPoll(frame);
            md_main.g_md_z80?.FlushPcHist(frame);
        }

            if (TracePerf && _cpuReady && _cpu != null)
            {
                _accFrameTicks += Stopwatch.GetTimestamp() - frameStart;
                _perfFrameCount++;
                MaybeLogPerformance();
            }

        // Blitta VDP RGB555 -> UI BGRA staging buffer
        EnsureFramebufferInitialized("RunFrame");
            var vdpBuffer = _vdp.GetFrameBuffer();
            if (vdpBuffer.Length > 0)
            {
            int vdpWidth = _vdp.FrameWidth;
            int vdpHeight = _vdp.FrameHeight;
            if (vdpWidth <= 0)
                vdpWidth = 320;
            if (vdpHeight <= 0)
                vdpHeight = 224;

                if (FrameBufferTraceEnabled && ShouldLogPerSecond(ref _lastVdpLogTicks))
                {
                int id = RuntimeHelpers.GetHashCode(vdpBuffer);
                uint p0 = vdpBuffer.Length > 0 ? vdpBuffer[0] : 0;
                uint p1 = vdpBuffer.Length > 1 ? vdpBuffer[1] : 0;
                uint p2 = vdpBuffer.Length > 2 ? vdpBuffer[2] : 0;
                uint p3 = vdpBuffer.Length > 3 ? vdpBuffer[3] : 0;
                Console.WriteLine($"[MdTracerAdapter] VDP output fbId=0x{id:X8} words={vdpBuffer.Length} p0=0x{p0:X8} p1=0x{p1:X8} p2=0x{p2:X8} p3=0x{p3:X8}");

                int renderPixels = Math.Min(vdpBuffer.Length, vdpWidth * vdpHeight);
                uint baseColor = vdpBuffer[0];
                int diffCount = 0;
                int firstDiff = -1;
                uint firstDiffValue = 0;
                for (int i = 0; i < renderPixels; i++)
                {
                    uint val = vdpBuffer[i];
                    if (val == baseColor)
                        continue;
                    diffCount++;
                    if (firstDiff < 0)
                    {
                        firstDiff = i;
                        firstDiffValue = val;
                    }
                }

                if (diffCount == 0)
                {
                    Console.WriteLine($"[MdTracerAdapter] VDP summary base=0x{baseColor:X8} diff=0 size={vdpWidth}x{vdpHeight}");
                }
                else
                {
                    int fx = firstDiff % vdpWidth;
                    int fy = firstDiff / vdpWidth;
                    Console.WriteLine($"[MdTracerAdapter] VDP summary base=0x{baseColor:X8} diff={diffCount} first=({fx},{fy}) val=0x{firstDiffValue:X8} size={vdpWidth}x{vdpHeight}");
                }
            }

                ReadOnlySpan<uint> vdpSpan = vdpBuffer;
                long blitStart = TracePerf ? Stopwatch.GetTimestamp() : 0;
                BlitArgbToBgra8888(vdpSpan, _frameBufferBack, srcStridePixels: vdpWidth, srcWidth: vdpWidth, srcHeight: vdpHeight);
                _frameBufferBack = Interlocked.Exchange(ref _frameBufferFront, _frameBufferBack);
                if (TracePerf)
                    PerfHotspots.Add(PerfHotspot.VdpBlit, Stopwatch.GetTimestamp() - blitStart);
            }
        }
    }

    /// <summary>
    /// Step one frame - for headless testing
    /// </summary>
    public void StepFrame()
    {
        RunFrame();
    }

    /// <summary>
    /// Get current Z80 PC - for debugging
    /// </summary>
    public ushort GetZ80Pc()
    {
        return md_main.g_md_z80?.CpuPc ?? (ushort)0;
    }

    /// <summary>
    /// Get current M68K PC - for debugging
    /// </summary>
    public uint GetM68kPc()
    {
        return MdTracerCore.md_m68k.g_reg_PC;
    }

    public void SaveState(BinaryWriter writer)
    {
        if (writer == null)
            throw new ArgumentNullException(nameof(writer));
        lock (_stateLock)
        {
            var serializer = new MdTracerStateSerializer();
            serializer.Save(writer);
        }
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader == null)
            throw new ArgumentNullException(nameof(reader));
        lock (_stateLock)
        {
            var serializer = new MdTracerStateSerializer();
            serializer.Load(reader);
        }
    }

    private void MaybeLogPerformance()
    {
        if (_perfFrameCount < 60)
            return;

        double invFreq = 1000.0 / Stopwatch.Frequency;
        double avgCpuMs = (_accCpuTicks / (double)_perfFrameCount) * invFreq;
        double avgVdpMs = (_accVdpTicks / (double)_perfFrameCount) * invFreq;
        double avgFrameMs = (_accFrameTicks / (double)_perfFrameCount) * invFreq;

        int gc0 = GC.CollectionCount(0);
        int gc1 = GC.CollectionCount(1);
        int gc2 = GC.CollectionCount(2);

        Console.WriteLine($"[MdTracerAdapter] perf avg/frame={avgFrameMs:0.00}ms CPU={avgCpuMs:0.00}ms VDP={avgVdpMs:0.00}ms GC0={gc0 - _lastGc0} GC1={gc1 - _lastGc1} GC2={gc2 - _lastGc2}");
        PerfHotspots.SnapshotAndReset(_hotspotTicks);
        const int maxTop = 5;
        Span<int> topIdx = stackalloc int[maxTop];
        Span<long> topTicks = stackalloc long[maxTop];
        for (int i = 0; i < maxTop; i++)
        {
            topIdx[i] = -1;
            topTicks[i] = 0;
        }

        for (int i = 0; i < _hotspotTicks.Length; i++)
        {
            long ticks = _hotspotTicks[i];
            if (ticks <= 0)
                continue;

            for (int slot = 0; slot < maxTop; slot++)
            {
                if (ticks <= topTicks[slot])
                    continue;

                for (int shift = maxTop - 1; shift > slot; shift--)
                {
                    topTicks[shift] = topTicks[shift - 1];
                    topIdx[shift] = topIdx[shift - 1];
                }

                topTicks[slot] = ticks;
                topIdx[slot] = i;
                break;
            }
        }

        var sb = new StringBuilder(96);
        bool anyHotspot = false;
        for (int i = 0; i < maxTop; i++)
        {
            int idx = topIdx[i];
            if (idx < 0)
                continue;

            double ms = (topTicks[i] / (double)_perfFrameCount) * invFreq;
            if (!anyHotspot)
            {
                sb.Append("[MdTracerAdapter] hotspots");
                anyHotspot = true;
            }

            sb.Append(' ');
            sb.Append(PerfHotspots.GetName((PerfHotspot)idx));
            sb.Append('=');
            sb.Append(ms.ToString("0.00"));
            sb.Append("ms");
        }

        if (anyHotspot)
            Console.WriteLine(sb.ToString());

        _accCpuTicks = 0;
        _accVdpTicks = 0;
        _accFrameTicks = 0;
        _perfFrameCount = 0;
        _lastGc0 = gc0;
        _lastGc1 = gc1;
        _lastGc2 = gc2;
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        EnsureFramebufferInitialized("GetFrameBuffer");
        width = _fbW;
        height = _fbH;
        stride = _fbStride;
        if (_fbLogW != width || _fbLogH != height || _fbLogStride != stride)
        {
            _fbLogW = width;
            _fbLogH = height;
            _fbLogStride = stride;
            MdTracerCore.MdLog.WriteLine($"[MdTracerAdapter] GetFrameBuffer size={width}x{height} stride={stride}");
        }

        if (FrameBufferTraceEnabled)
        {
            _fbPresentCount++;
            int id = RuntimeHelpers.GetHashCode(_frameBufferFront);
            if (ShouldLogPerSecond(ref _lastPresentLogTicks))
                Console.WriteLine($"[MdTracerAdapter] Present fbId=0x{id:X8} size={width}x{height} stride={stride} bytes={_frameBufferFront.Length}");
            if ((_fbPresentCount % PresentSampleEveryFrames) == 0 && _frameBufferFront.Length >= 4)
            {
                Console.WriteLine($"[MdTracerAdapter] Present sample frame={_fbPresentCount} bytes={_frameBufferFront[0]:X2} {_frameBufferFront[1]:X2} {_frameBufferFront[2]:X2} {_frameBufferFront[3]:X2}");
            }
        }
        return _frameBufferFront;
    }

    // Check if framebuffer has non-black pixels (for debugging)
    public bool FrameBufferHasContent()
    {
        EnsureFramebufferInitialized("FrameBufferHasContent");
        // Check the internal game screen (before conversion to RGBA)
        if (_vdp is Core.MdTracerCore.md_vdp vdp)
        {
            var (width, height, hasNonBlack) = vdp.GetFrameBufferInfo();
            if (hasNonBlack)
            {
                Console.WriteLine($"[MdTracerAdapter] Framebuffer HAS content: {width}x{height}");
                return true;
            }
        }
        Console.WriteLine("[MdTracerAdapter] Framebuffer is black");
        return false;
    }

    // Dump framebuffer to PPM file (for debugging)
    public void DumpFrameBufferToPpm(string filePath)
    {
        EnsureFramebufferInitialized("DumpFrameBufferToPpm");
        if (_vdp is Core.MdTracerCore.md_vdp vdp)
        {
            vdp.DumpFrameBufferToPpm(filePath);
            Console.WriteLine($"[MdTracerAdapter] Dumped framebuffer to {filePath}");
        }
    }

    private void LogFrameBufferIdentity(string reason)
    {
        if (!FrameBufferTraceEnabled)
            return;

        if (_frameBufferFront.Length == 0)
        {
            Console.WriteLine($"[MdTracerAdapter] {reason} framebuffer empty");
            return;
        }

        int id = RuntimeHelpers.GetHashCode(_frameBufferFront);
        Console.WriteLine($"[MdTracerAdapter] {reason} framebuffer id=0x{id:X8} size={_fbW}x{_fbH} stride={_fbStride} bytes={_frameBufferFront.Length}");
    }

    private void BlitRgb555ToBgra8888(Span<ushort> vdpSrc, Span<byte> dst, int srcStridePixels)
    {
        if (vdpSrc.Length == 0 || dst.Length == 0)
            return;

        int srcWidth = 320;
        int copyHeight = Math.Min(_fbH, 224);
        int copyWidth = Math.Min(_fbW, srcWidth);
        int dstStride = 1280;

        int requiredDst = (copyHeight - 1) * dstStride + (copyWidth * 4);
        if (dst.Length < requiredDst)
            return;

        bool interleaved = vdpSrc.Length >= srcStridePixels * copyHeight * 2;
        int requiredSrc = interleaved ? (srcStridePixels * copyHeight * 2) : (srcStridePixels * copyHeight);
        if (vdpSrc.Length < requiredSrc)
            return;

        bool logSamples = FrameBufferTraceEnabled && ShouldLogPerSecond(ref _lastSampleLogTicks);
        if (logSamples)
        {
            ushort p0 = ReadVdpPixel(vdpSrc, srcStridePixels, interleaved, 0, 0);
            ushort p1 = ReadVdpPixel(vdpSrc, srcStridePixels, interleaved, 1, 0);
            ushort p2 = ReadVdpPixel(vdpSrc, srcStridePixels, interleaved, 2, 0);
            ushort p3 = ReadVdpPixel(vdpSrc, srcStridePixels, interleaved, 3, 0);
            Console.WriteLine($"[MdTracerAdapter] VDP src sample p0=0x{p0:X4} p1=0x{p1:X4} p2=0x{p2:X4} p3=0x{p3:X4}");
        }

        for (int y = 0; y < copyHeight; y++)
        {
            int srcRow = interleaved ? (y * srcStridePixels * 2) : (y * srcStridePixels);
            int dstRow = y * dstStride;
            for (int x = 0; x < copyWidth; x++)
            {
                int srcIndex = interleaved ? (srcRow + (x * 2)) : (srcRow + x);
                ushort rgb555 = (ushort)(vdpSrc[srcIndex] & 0x7FFF);
                int r5 = (rgb555 >> 10) & 0x1F;
                int g5 = (rgb555 >> 5) & 0x1F;
                int b5 = rgb555 & 0x1F;

                byte r8 = (byte)((r5 << 3) | (r5 >> 2));
                byte g8 = (byte)((g5 << 3) | (g5 >> 2));
                byte b8 = (byte)((b5 << 3) | (b5 >> 2));

                int di = dstRow + (x * 4);
                dst[di + 0] = b8;
                dst[di + 1] = g8;
                dst[di + 2] = r8;
                dst[di + 3] = 255;
            }
        }

        if (logSamples && dst.Length >= 16)
        {
            Console.WriteLine($"[MdTracerAdapter] DST sample b0={dst[0]:X2} g0={dst[1]:X2} r0={dst[2]:X2} a0={dst[3]:X2} " +
                              $"b1={dst[4]:X2} g1={dst[5]:X2} r1={dst[6]:X2} a1={dst[7]:X2}");
        }
    }

    private void BlitArgbToBgra8888(ReadOnlySpan<uint> vdpSrc, Span<byte> dst, int srcStridePixels, int srcWidth, int srcHeight)
    {
        if (vdpSrc.Length == 0 || dst.Length == 0)
            return;

        int copyHeight = Math.Min(_fbH, srcHeight);
        int copyWidth = Math.Min(_fbW, srcWidth);
        int dstStride = _fbStride;
        int srcStrideBytes = srcStridePixels * 4;
        int copyBytesPerRow = copyWidth * 4;

        int requiredSrc = srcStridePixels * copyHeight;
        int requiredDst = (copyHeight - 1) * dstStride + copyBytesPerRow;
        if (vdpSrc.Length < requiredSrc || dst.Length < requiredDst)
            return;

        bool logSamples = FrameBufferTraceEnabled && ShouldLogPerSecond(ref _lastSampleLogTicks);
        ReadOnlySpan<byte> srcBytes = MemoryMarshal.AsBytes(vdpSrc);
        int requiredSrcBytes = srcStrideBytes * copyHeight;
        if (srcBytes.Length < requiredSrcBytes)
            return;

        if (srcStrideBytes == copyBytesPerRow && dstStride == copyBytesPerRow)
        {
            int totalBytes = copyBytesPerRow * copyHeight;
            srcBytes.Slice(0, totalBytes).CopyTo(dst);
        }
        else
        {
            for (int y = 0; y < copyHeight; y++)
            {
                int srcRow = y * srcStrideBytes;
                int dstRow = y * dstStride;
                srcBytes.Slice(srcRow, copyBytesPerRow).CopyTo(dst.Slice(dstRow, copyBytesPerRow));
            }
        }

        if (logSamples && dst.Length >= 16)
        {
            Console.WriteLine($"[MdTracerAdapter] DST sample b0={dst[0]:X2} g0={dst[1]:X2} r0={dst[2]:X2} a0={dst[3]:X2} " +
                              $"b1={dst[4]:X2} g1={dst[5]:X2} r1={dst[6]:X2} a1={dst[7]:X2}");
        }
    }

    private static bool ShouldLogPerSecond(ref long lastTicks)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - lastTicks < Stopwatch.Frequency)
            return false;
        lastTicks = now;
        return true;
    }

    private static ushort ReadVdpPixel(Span<ushort> vdpSrc, int srcStridePixels, bool interleaved, int x, int y)
    {
        int srcRow = interleaved ? (y * srcStridePixels * 2) : (y * srcStridePixels);
        int srcIndex = interleaved ? (srcRow + (x * 2)) : (srcRow + x);
        if ((uint)srcIndex >= (uint)vdpSrc.Length)
            return 0;
        return (ushort)(vdpSrc[srcIndex] & 0x7FFF);
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = PsgSampleRate;
        channels = PsgChannels;

        var music = md_main.g_md_music;
        if (music == null)
            return ReadOnlySpan<short>.Empty;

        bool wantPsg = !_psgDisabled;
        bool wantYm = _ymEnabled;
        if (!wantPsg && !wantYm)
            return ReadOnlySpan<short>.Empty;

        long frame = md_main.g_md_vdp?.FrameCounter ?? -1;
        if (frame == _psgLastFrame)
            return _psgFrameSamples > 0 ? _psgFrameBuffer.AsSpan(0, _psgFrameSamples) : ReadOnlySpan<short>.Empty;

        _psgLastFrame = frame;
        _psgFrameAccumulator += (double)PsgSampleRate / GetTargetFps();
        int frames = (int)_psgFrameAccumulator;
        if (frames <= 0)
        {
            _psgFrameSamples = 0;
            return ReadOnlySpan<short>.Empty;
        }

        _psgFrameAccumulator -= frames;
        int samples = frames * PsgChannels;
        bool trackAudioLevel = TraceAudioLevel;
        long mixSumSq = 0;
        if (_psgFrameBuffer.Length < samples)
            _psgFrameBuffer = new short[samples];

        int psgMin = 0;
        int psgMax = 0;
        bool psgMinMaxInit = false;
        int psgPeak = 0;
        int psgNonZero = 0;
        if (wantPsg)
        {
            var psg = music.g_md_sn76489;
            for (int i = 0; i < frames; i++)
            {
                int s = psg.SN76489_Update();
                if (s > short.MaxValue) s = short.MaxValue;
                else if (s < short.MinValue) s = short.MinValue;
                short sample = (short)s;
                if (!psgMinMaxInit)
                {
                    psgMinMaxInit = true;
                    psgMin = sample;
                    psgMax = sample;
                }
                else
                {
                    if (sample < psgMin) psgMin = sample;
                    if (sample > psgMax) psgMax = sample;
                }
                int abs = sample < 0 ? -sample : sample;
                if (abs > psgPeak) psgPeak = abs;
                if (sample != 0) psgNonZero += PsgChannels;
                int idx = i * PsgChannels;
                _psgFrameBuffer[idx] = sample;
                _psgFrameBuffer[idx + 1] = sample;
                if (trackAudioLevel && !wantYm)
                    mixSumSq += (long)sample * sample * PsgChannels;
            }
        }
        else
        {
            Array.Clear(_psgFrameBuffer, 0, samples);
        }

        int ymMin = 0;
        int ymMax = 0;
        bool ymMinMaxInit = false;
        if (wantYm)
        {
            if (_ymFrameBuffer.Length < samples)
                _ymFrameBuffer = new short[samples];

            music.g_md_ym2612.YM2612_UpdateBatch(_ymFrameBuffer, frames);

            int mixMin = 0;
            int mixMax = 0;
            bool mixMinMaxInit = false;
            int ymPeak = 0;
            int ymNonZero = 0;
            int mixPeak = 0;
            int mixNonZero = 0;
            for (int i = 0; i < samples; i++)
            {
                int ymSample = _ymFrameBuffer[i];
                if (!ymMinMaxInit)
                {
                    ymMinMaxInit = true;
                    ymMin = ymSample;
                    ymMax = ymSample;
                }
                else
                {
                    if (ymSample < ymMin) ymMin = ymSample;
                    if (ymSample > ymMax) ymMax = ymSample;
                }
                int ymAbs = ymSample < 0 ? -ymSample : ymSample;
                if (ymAbs > ymPeak) ymPeak = ymAbs;
                if (ymSample != 0) ymNonZero++;

                int mixed = _psgFrameBuffer[i] + _ymFrameBuffer[i];
                if (mixed > short.MaxValue) mixed = short.MaxValue;
                else if (mixed < short.MinValue) mixed = short.MinValue;
                _psgFrameBuffer[i] = (short)mixed;

                if (!mixMinMaxInit)
                {
                    mixMinMaxInit = true;
                    mixMin = mixed;
                    mixMax = mixed;
                }
                else
                {
                    if (mixed < mixMin) mixMin = mixed;
                    if (mixed > mixMax) mixMax = mixed;
                }
                int mixAbs = mixed < 0 ? -mixed : mixed;
                if (mixAbs > mixPeak) mixPeak = mixAbs;
                if (mixed != 0) mixNonZero++;
                if (trackAudioLevel)
                    mixSumSq += (long)mixed * mixed;
            }

            if (TraceAudioEnabled && ShouldLogPerSecond(ref _lastAudioLogTicks))
            {
                Console.WriteLine($"[Audio] psgMin={psgMin} psgMax={psgMax} ymMin={ymMin} ymMax={ymMax} mixMin={mixMin} mixMax={mixMax} samples={samples}");
            }

            if (trackAudioLevel && ShouldLogPerSecond(ref _lastAudioLevelTicks))
            {
                double rms = samples > 0 ? Math.Sqrt(mixSumSq / (double)samples) : 0;
                Console.WriteLine($"[AUDLVL] min={mixMin} max={mixMax} rms={rms:F1} samples={samples}");
            }

            if (ShouldLogPerSecond(ref _lastAudioCoreLogTicks))
            {
                if (trackAudioLevel)
                {
                    if (wantPsg)
                        Console.WriteLine($"[PSGLVL] peak={psgPeak} samples={samples}");
                    Console.WriteLine($"[YMLVL] peak={ymPeak} samples={samples}");
                }
                int outVolMin = 0;
                int outVolMax = 0;
                if (music.g_out_vol != null && music.g_out_vol.Length > 0)
                {
                    outVolMin = music.g_out_vol[0];
                    outVolMax = music.g_out_vol[0];
                    for (int i = 1; i < music.g_out_vol.Length; i++)
                    {
                        int v = music.g_out_vol[i];
                        if (v < outVolMin) outVolMin = v;
                        if (v > outVolMax) outVolMax = v;
                    }
                }
                Console.WriteLine(
                    $"[AUDIOCORE] frames={frames} psgPeak={psgPeak} ymPeak={ymPeak} mixPeak={mixPeak} " +
                    $"psgNZ={psgNonZero} ymNZ={ymNonZero} mixNZ={mixNonZero} outVolMin={outVolMin} outVolMax={outVolMax}");
            }
        }
        else if (TraceAudioEnabled && ShouldLogPerSecond(ref _lastAudioLogTicks))
        {
            Console.WriteLine($"[Audio] psgMin={psgMin} psgMax={psgMax} ymMin=NA ymMax=NA mixMin=NA mixMax=NA samples={samples}");
        }

        if (trackAudioLevel && !wantYm && ShouldLogPerSecond(ref _lastAudioLevelTicks))
        {
            double rms = samples > 0 ? Math.Sqrt(mixSumSq / (double)samples) : 0;
            Console.WriteLine($"[AUDLVL] min={psgMin} max={psgMax} rms={rms:F1} samples={samples}");
        }

        if (!wantYm && ShouldLogPerSecond(ref _lastAudioCoreLogTicks))
        {
            if (trackAudioLevel && wantPsg)
                Console.WriteLine($"[PSGLVL] peak={psgPeak} samples={samples}");
            int outVolMin = 0;
            int outVolMax = 0;
            if (music.g_out_vol != null && music.g_out_vol.Length > 0)
            {
                outVolMin = music.g_out_vol[0];
                outVolMax = music.g_out_vol[0];
                for (int i = 1; i < music.g_out_vol.Length; i++)
                {
                    int v = music.g_out_vol[i];
                    if (v < outVolMin) outVolMin = v;
                    if (v > outVolMax) outVolMax = v;
                }
            }
            Console.WriteLine(
                $"[AUDIOCORE] frames={frames} psgPeak={psgPeak} ymPeak=0 mixPeak={psgPeak} " +
                $"psgNZ={psgNonZero} ymNZ=0 mixNZ={psgNonZero} outVolMin={outVolMin} outVolMax={outVolMax}");
        }

        ApplyMasterVolume(_psgFrameBuffer, samples);
        _psgFrameSamples = samples;
        return _psgFrameBuffer.AsSpan(0, _psgFrameSamples);
    }

    public ReadOnlySpan<short> GetAudioBufferForFrames(int frames, out int sampleRate, out int channels)
    {
        sampleRate = PsgSampleRate;
        channels = PsgChannels;

        if (frames <= 0)
            return ReadOnlySpan<short>.Empty;

        var music = md_main.g_md_music;
        if (music == null)
            return ReadOnlySpan<short>.Empty;

        bool wantPsg = !_psgDisabled;
        bool wantYm = _ymEnabled;
        if (!wantPsg && !wantYm)
            return ReadOnlySpan<short>.Empty;

        int samples = frames * PsgChannels;
        bool trackAudioLevel = TraceAudioLevel;
        long mixSumSq = 0;
        if (_psgFrameBuffer.Length < samples)
            _psgFrameBuffer = new short[samples];

        int psgMin = 0;
        int psgMax = 0;
        bool psgMinMaxInit = false;
        int psgPeak = 0;
        int psgNonZero = 0;
        if (wantPsg)
        {
            var psg = music.g_md_sn76489;
            for (int i = 0; i < frames; i++)
            {
                int s = psg.SN76489_Update();
                if (s > short.MaxValue) s = short.MaxValue;
                else if (s < short.MinValue) s = short.MinValue;
                short sample = (short)s;
                if (!psgMinMaxInit)
                {
                    psgMinMaxInit = true;
                    psgMin = sample;
                    psgMax = sample;
                }
                else
                {
                    if (sample < psgMin) psgMin = sample;
                    if (sample > psgMax) psgMax = sample;
                }
                int abs = sample < 0 ? -sample : sample;
                if (abs > psgPeak) psgPeak = abs;
                if (sample != 0) psgNonZero += PsgChannels;
                int idx = i * PsgChannels;
                _psgFrameBuffer[idx] = sample;
                _psgFrameBuffer[idx + 1] = sample;
                if (trackAudioLevel && !wantYm)
                    mixSumSq += (long)sample * sample * PsgChannels;
            }
        }
        else
        {
            Array.Clear(_psgFrameBuffer, 0, samples);
        }

        int ymMin = 0;
        int ymMax = 0;
        bool ymMinMaxInit = false;
        int mixMin = 0;
        int mixMax = 0;
        bool mixMinMaxInit = false;
        int ymPeak = 0;
        int ymNonZero = 0;
        int mixPeak = 0;
        int mixNonZero = 0;
        if (wantYm)
        {
            if (_ymFrameBuffer.Length < samples)
                _ymFrameBuffer = new short[samples];

            music.g_md_ym2612.YM2612_UpdateBatch(_ymFrameBuffer, frames);

            for (int i = 0; i < samples; i++)
            {
                int ymSample = _ymFrameBuffer[i];
                if (!ymMinMaxInit)
                {
                    ymMinMaxInit = true;
                    ymMin = ymSample;
                    ymMax = ymSample;
                }
                else
                {
                    if (ymSample < ymMin) ymMin = ymSample;
                    if (ymSample > ymMax) ymMax = ymSample;
                }
                int ymAbs = ymSample < 0 ? -ymSample : ymSample;
                if (ymAbs > ymPeak) ymPeak = ymAbs;
                if (ymSample != 0) ymNonZero++;

                int mixed = _psgFrameBuffer[i] + _ymFrameBuffer[i];
                if (mixed > short.MaxValue) mixed = short.MaxValue;
                else if (mixed < short.MinValue) mixed = short.MinValue;
                _psgFrameBuffer[i] = (short)mixed;

                if (!mixMinMaxInit)
                {
                    mixMinMaxInit = true;
                    mixMin = mixed;
                    mixMax = mixed;
                }
                else
                {
                    if (mixed < mixMin) mixMin = mixed;
                    if (mixed > mixMax) mixMax = mixed;
                }
                int mixAbs = mixed < 0 ? -mixed : mixed;
                if (mixAbs > mixPeak) mixPeak = mixAbs;
                if (mixed != 0) mixNonZero++;
                if (trackAudioLevel)
                    mixSumSq += (long)mixed * mixed;
            }

            if (TraceAudioEnabled && ShouldLogPerSecond(ref _lastAudioLogTicks))
            {
                Console.WriteLine($"[Audio] psgMin={psgMin} psgMax={psgMax} ymMin={ymMin} ymMax={ymMax} mixMin={mixMin} mixMax={mixMax} samples={samples}");
            }

            if (trackAudioLevel && ShouldLogPerSecond(ref _lastAudioLevelTicks))
            {
                double rms = samples > 0 ? Math.Sqrt(mixSumSq / (double)samples) : 0;
                Console.WriteLine($"[AUDLVL] min={mixMin} max={mixMax} rms={rms:F1} samples={samples}");
            }

            if (ShouldLogPerSecond(ref _lastAudioCoreLogTicks))
            {
                if (trackAudioLevel)
                {
                    if (wantPsg)
                        Console.WriteLine($"[PSGLVL] peak={psgPeak} samples={samples}");
                    Console.WriteLine($"[YMLVL] peak={ymPeak} samples={samples}");
                }
                int outVolMin = 0;
                int outVolMax = 0;
                if (music.g_out_vol != null && music.g_out_vol.Length > 0)
                {
                    outVolMin = music.g_out_vol[0];
                    outVolMax = music.g_out_vol[0];
                    for (int i = 1; i < music.g_out_vol.Length; i++)
                    {
                        int v = music.g_out_vol[i];
                        if (v < outVolMin) outVolMin = v;
                        if (v > outVolMax) outVolMax = v;
                    }
                }
                Console.WriteLine(
                    $"[AUDIOCORE] frames={frames} psgPeak={psgPeak} ymPeak={ymPeak} mixPeak={mixPeak} " +
                    $"psgNZ={psgNonZero} ymNZ={ymNonZero} mixNZ={mixNonZero} outVolMin={outVolMin} outVolMax={outVolMax}");
            }
        }
        else if (TraceAudioEnabled && ShouldLogPerSecond(ref _lastAudioLogTicks))
        {
            Console.WriteLine($"[Audio] psgMin={psgMin} psgMax={psgMax} ymMin=NA ymMax=NA mixMin=NA mixMax=NA samples={samples}");
        }

        if (trackAudioLevel && !wantYm && ShouldLogPerSecond(ref _lastAudioLevelTicks))
        {
            double rms = samples > 0 ? Math.Sqrt(mixSumSq / (double)samples) : 0;
            Console.WriteLine($"[AUDLVL] min={psgMin} max={psgMax} rms={rms:F1} samples={samples}");
        }

        if (!wantYm && ShouldLogPerSecond(ref _lastAudioCoreLogTicks))
        {
            if (trackAudioLevel && wantPsg)
                Console.WriteLine($"[PSGLVL] peak={psgPeak} samples={samples}");
            int outVolMin = 0;
            int outVolMax = 0;
            if (music.g_out_vol != null && music.g_out_vol.Length > 0)
            {
                outVolMin = music.g_out_vol[0];
                outVolMax = music.g_out_vol[0];
                for (int i = 1; i < music.g_out_vol.Length; i++)
                {
                    int v = music.g_out_vol[i];
                    if (v < outVolMin) outVolMin = v;
                    if (v > outVolMax) outVolMax = v;
                }
            }
            Console.WriteLine(
                $"[AUDIOCORE] frames={frames} psgPeak={psgPeak} ymPeak=0 mixPeak={psgPeak} " +
                $"psgNZ={psgNonZero} ymNZ=0 mixNZ={psgNonZero} outVolMin={outVolMin} outVolMax={outVolMax}");
        }

        ApplyMasterVolume(_psgFrameBuffer, samples);
        _psgFrameSamples = samples;
        return _psgFrameBuffer.AsSpan(0, _psgFrameSamples);
    }

    public bool WritePsg(byte value)
    {
        var bus = md_main.g_md_bus;
        if (bus == null)
            return false;

        bus.write8(0xC00011, value);
        return true;
    }

    public void SetInputState(
        bool up,
        bool down,
        bool left,
        bool right,
        bool a,
        bool b,
        bool c,
        bool start,
        bool x,
        bool y,
        bool z,
        bool mode,
        PadType padType)
    {
        var io = md_main.g_md_io;
        if (io == null)
            return;

        var state = new MdPadState
        {
            Up = up,
            Down = down,
            Left = left,
            Right = right,
            A = a,
            B = b,
            C = c,
            Start = start,
            X = x,
            Y = y,
            Z = z,
            Mode = mode
        };

        io.SetPad1Input(state, padType);
    }

    public void LoadStateSlot(int slotIndex)
    {
        _savestateService.Load(this, slotIndex);
    }

    public void SaveStateSlot(int slotIndex)
    {
        _savestateService.Save(this, slotIndex);
    }

}
