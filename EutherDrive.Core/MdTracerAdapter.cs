using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core;

public sealed class MdTracerAdapter : IEmulatorCore
{
    private const int DefaultW = 320;
    private const int DefaultH = 224;
    private readonly md_vdp _vdp = new md_vdp();

    private byte[] _frameBuffer = Array.Empty<byte>(); // BGRA till UI
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

    private int _tick;
    private const int VLINES_NTSC = 262;
    private uint _lastPc;
    private int _pcStallFrames;
    private FrameRateMode _frameRateMode = FrameRateMode.Auto;
    private int _cpuCyclesPerLine = 200;

    private static readonly bool DumpVectorsEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DUMP_VECTORS"), "1", StringComparison.Ordinal);
    private static readonly bool FrameBufferTraceEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_FB_TRACE"), "1", StringComparison.Ordinal);
    private static readonly bool TraceAudioEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_TRACE_AUDIO"), "1", StringComparison.Ordinal);
    private static readonly bool SkipVdpRenderEnabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SKIP_VDP_RENDER"), "1", StringComparison.Ordinal);

    // ROM + BUS
    private byte[]? _rom;
    private MegaDriveBus? _bus;
    private readonly object _loadLock = new();

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

    public RomInfo RomInfo { get; private set; } = new RomInfo();

    public void SetYmEnabled(bool enabled)
    {
        _ymEnabled = enabled;
        md_bus.SetYmEnabled(enabled);
    }

    public void SetRegionOverride(ConsoleRegion region)
    {
        if (md_main.g_md_io != null)
            md_main.g_md_io.SetRegionOverride(region);
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

            md_main.PowerCycleReset();
            md_main.initialize();
            md_main.g_md_vdp = _vdp;

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
                }

                bool useNormalizedForBus = md_main.g_md_cartridge.g_smd_header_size > 0 || md_main.g_md_cartridge.g_smd_deinterleaved;
                _rom = useNormalizedForBus ? md_main.g_md_cartridge.g_file : rawData;
                _bus = new MegaDriveBus(_rom);
                md_bus.Current = _bus;
                EutherDrive.Core.MdTracerCore.md_bus.Current = _bus;

                DumpVectors();

                byte[] vecRom = md_main.g_md_cartridge?.g_file ?? _rom ?? rawData;
                string header = TryReadSegaString(vecRom);
                _bus.Write32(0xFF0000, 0x1234ABCD);
                uint wramProbe = _bus.Read32(0xFF0000);
                ConsoleRegion? regionHint = md_rom_utils.DetectRegionFromHeader(vecRom, out string regionRaw);
                RomInfo.RegionHint = regionHint;
                RomInfo.RegionHeaderRaw = regionRaw;
                RomInfo.SerialNumber = md_main.g_md_cartridge?.g_serial_number ?? string.Empty;
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

    public void Reset()
    {
        Console.WriteLine("[MdTracerAdapter] Reset begin");
        _tick = 0;

        // Nollställ RAM
        _bus?.Reset();
        md_main.g_md_bus?.Reset();
        _vdp.reset();
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
        Array.Clear(_frameBuffer, 0, _frameBuffer.Length);
        Console.WriteLine($"[MdTracerAdapter] Reset framebuffer { _fbW }x{ _fbH } stride={ _fbStride }");
        LogFrameBufferIdentity("Reset");
    }

    public void SetFrameRateMode(FrameRateMode mode) => _frameRateMode = mode;

    public void SetCpuCyclesPerLine(int cycles)
    {
        if (cycles <= 0)
            throw new ArgumentOutOfRangeException(nameof(cycles), "Cycles must be positive.");
        _cpuCyclesPerLine = cycles;
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
        if (_frameBuffer.Length != needed)
            _frameBuffer = new byte[needed];

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
        if (_tick == 0)
        MdTracerCore.MdLog.WriteLine("[MdTracerAdapter] RunFrame start");

        _tick++;
        long frameStart = Stopwatch.GetTimestamp();

        if (md_main.g_masterSystemMode)
        {
            md_main.RunFrame();
        }
        else
        {
        uint pcAfter = md_m68k.g_reg_PC;
        if (_cpuReady && _cpu != null)
        {
            long cpuTicks = 0;
            long vdpTicks = 0;
            int z80Budget = md_main.VDL_LINE_RENDER_Z80_CLOCK;

            for (int v = 0; v < VLINES_NTSC; v++)
            {
                if (!SkipVdpRenderEnabled)
                {
                    long start = Stopwatch.GetTimestamp();
                    _vdp.run(v);
                    vdpTicks += Stopwatch.GetTimestamp() - start;
                }

                long cpuStart = Stopwatch.GetTimestamp();
                _cpu.RunSome(budget: md_main.VDL_LINE_RENDER_MC68_CLOCK);
                cpuTicks += Stopwatch.GetTimestamp() - cpuStart;

                md_main.g_md_z80?.run(z80Budget);
            }

            _accCpuTicks += cpuTicks;
            _accVdpTicks += vdpTicks;
            PerfHotspots.Add(PerfHotspot.CpuStep, cpuTicks);
            PerfHotspots.Add(PerfHotspot.VdpRender, vdpTicks);

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
                for (int v = 0; v < VLINES_NTSC; v++)
                    _vdp.run(v);
            }
        }
        }

        if (_cpuReady && _cpu != null)
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
            if (FrameBufferTraceEnabled && ShouldLogPerSecond(ref _lastVdpLogTicks))
            {
                int id = RuntimeHelpers.GetHashCode(vdpBuffer);
                uint p0 = vdpBuffer.Length > 0 ? vdpBuffer[0] : 0;
                uint p1 = vdpBuffer.Length > 1 ? vdpBuffer[1] : 0;
                uint p2 = vdpBuffer.Length > 2 ? vdpBuffer[2] : 0;
                uint p3 = vdpBuffer.Length > 3 ? vdpBuffer[3] : 0;
                Console.WriteLine($"[MdTracerAdapter] VDP output fbId=0x{id:X8} words={vdpBuffer.Length} p0=0x{p0:X8} p1=0x{p1:X8} p2=0x{p2:X8} p3=0x{p3:X8}");
            }

            int vdpWidth = _vdp.g_display_xsize;
            int vdpHeight = _vdp.g_display_ysize;
            if (vdpWidth <= 0)
                vdpWidth = 320;
            if (vdpHeight <= 0)
                vdpHeight = 224;

            ReadOnlySpan<uint> vdpSpan = vdpBuffer;
            long blitStart = Stopwatch.GetTimestamp();
            BlitArgbToBgra8888(vdpSpan, _frameBuffer, srcStridePixels: vdpWidth, srcWidth: vdpWidth, srcHeight: vdpHeight);
            PerfHotspots.Add(PerfHotspot.VdpBlit, Stopwatch.GetTimestamp() - blitStart);
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
            int id = RuntimeHelpers.GetHashCode(_frameBuffer);
            if (ShouldLogPerSecond(ref _lastPresentLogTicks))
                Console.WriteLine($"[MdTracerAdapter] Present fbId=0x{id:X8} size={width}x{height} stride={stride} bytes={_frameBuffer.Length}");
            if ((_fbPresentCount % PresentSampleEveryFrames) == 0 && _frameBuffer.Length >= 4)
            {
                Console.WriteLine($"[MdTracerAdapter] Present sample frame={_fbPresentCount} bytes={_frameBuffer[0]:X2} {_frameBuffer[1]:X2} {_frameBuffer[2]:X2} {_frameBuffer[3]:X2}");
            }
        }
        return _frameBuffer;
    }

    private void LogFrameBufferIdentity(string reason)
    {
        if (!FrameBufferTraceEnabled)
            return;

        if (_frameBuffer.Length == 0)
        {
            Console.WriteLine($"[MdTracerAdapter] {reason} framebuffer empty");
            return;
        }

        int id = RuntimeHelpers.GetHashCode(_frameBuffer);
        Console.WriteLine($"[MdTracerAdapter] {reason} framebuffer id=0x{id:X8} size={_fbW}x{_fbH} stride={_fbStride} bytes={_frameBuffer.Length}");
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
        _psgFrameAccumulator += (double)PsgSampleRate / 60.0;
        int frames = (int)_psgFrameAccumulator;
        if (frames <= 0)
        {
            _psgFrameSamples = 0;
            return ReadOnlySpan<short>.Empty;
        }

        _psgFrameAccumulator -= frames;
        int samples = frames * PsgChannels;
        if (_psgFrameBuffer.Length < samples)
            _psgFrameBuffer = new short[samples];

        int psgMin = 0;
        int psgMax = 0;
        bool psgMinMaxInit = false;
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
                int idx = i * PsgChannels;
                _psgFrameBuffer[idx] = sample;
                _psgFrameBuffer[idx + 1] = sample;
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
            }

            if (TraceAudioEnabled && ShouldLogPerSecond(ref _lastAudioLogTicks))
            {
                Console.WriteLine($"[Audio] psgMin={psgMin} psgMax={psgMax} ymMin={ymMin} ymMax={ymMax} mixMin={mixMin} mixMax={mixMax} samples={samples}");
            }
        }
        else if (TraceAudioEnabled && ShouldLogPerSecond(ref _lastAudioLogTicks))
        {
            Console.WriteLine($"[Audio] psgMin={psgMin} psgMax={psgMax} ymMin=NA ymMax=NA mixMin=NA mixMax=NA samples={samples}");
        }

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
        if (_psgFrameBuffer.Length < samples)
            _psgFrameBuffer = new short[samples];

        int psgMin = 0;
        int psgMax = 0;
        bool psgMinMaxInit = false;
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
                int idx = i * PsgChannels;
                _psgFrameBuffer[idx] = sample;
                _psgFrameBuffer[idx + 1] = sample;
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
            }

            if (TraceAudioEnabled && ShouldLogPerSecond(ref _lastAudioLogTicks))
            {
                Console.WriteLine($"[Audio] psgMin={psgMin} psgMax={psgMax} ymMin={ymMin} ymMax={ymMax} mixMin={mixMin} mixMax={mixMax} samples={samples}");
            }
        }
        else if (TraceAudioEnabled && ShouldLogPerSecond(ref _lastAudioLogTicks))
        {
            Console.WriteLine($"[Audio] psgMin={psgMin} psgMax={psgMax} ymMin=NA ymMax=NA mixMin=NA mixMax=NA samples={samples}");
        }

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
}
