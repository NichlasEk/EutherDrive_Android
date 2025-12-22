using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core;

public sealed class MdTracerAdapter : IEmulatorCore
{
    private const int DefaultW = 320;
    private const int DefaultH = 224;
    private readonly md_vdp _vdp = new md_vdp();

    private byte[] _frameBuffer = Array.Empty<byte>(); // RGBA till UI
    private int _fbW, _fbH, _fbStride;
    private int _fbLogW = -1;
    private int _fbLogH = -1;
    private int _fbLogStride = -1;

    private int _tick;
    private const int VLINES_NTSC = 262;
    private uint _lastPc;
    private int _pcStallFrames;

    // ROM + BUS
    private byte[]? _rom;
    private MegaDriveBus? _bus;

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

    public string RomInfo { get; private set; } = "(no rom)";

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("ROM path is empty.", nameof(path));

        Console.WriteLine($"[MdTracerAdapter] LoadRom: {path}");

        _rom = File.ReadAllBytes(path);
        _bus = new MegaDriveBus(_rom);

        // Koppla vår bus-bridge så MDTracer CPU-kod kan läsa/skriva
        md_bus.Current = _bus;
        EutherDrive.Core.MdTracerCore.md_bus.Current = _bus;

        // Initiera MDTracer-kärnan så att g_md_* inte är null.
        md_main.initialize();
        md_main.g_md_vdp = _vdp;
        md_main.g_md_cartridge?.load(path);

        // Steg A/B proof
        string header = TryReadSegaString(_bus);
        _bus.Write32(0xFF0000, 0x1234ABCD);
        uint wramProbe = _bus.Read32(0xFF0000);

        // Vector proof
        uint sp = _bus.Read32(0x000000);
        uint pc = _bus.Read32(0x000004);
        ushort op = _bus.Read16(pc);

        // CPU runner init (tål om md_m68k inte finns än)
        try
        {
            _cpu = new MdTracerM68kRunner();
            _cpuReady = true;
        }
        catch (Exception ex)
        {
            _cpuReady = false;
            _cpu = null;
            RomInfo = $"ROM bytes: {_rom.Length} | {header} | WRAM@FF0000: 0x{wramProbe:X8} | VEC SP=0x{sp:X8} PC=0x{pc:X8} OP@PC=0x{op:X4} | CPU: {ex.Message}";
            Reset();
            return;
        }

        RomInfo =
        $"ROM bytes: {_rom.Length} | {header} | WRAM@FF0000: 0x{wramProbe:X8} | " +
        $"VEC SP=0x{sp:X8} PC=0x{pc:X8} OP@PC=0x{op:X4} | CPU API ok";

        Reset();
    }

    private static string TryReadSegaString(MegaDriveBus bus)
    {
        Span<byte> s = stackalloc byte[4];
        for (int i = 0; i < 4; i++)
            s[i] = bus.Read8((uint)(0x100 + i));

        var text = Encoding.ASCII.GetString(s);
        return $"Header@0x100: '{text}'";
    }

    public void Reset()
    {
        Console.WriteLine("[MdTracerAdapter] Reset begin");
        _tick = 0;

        // Nollställ RAM
        _bus?.Reset();
        md_main.g_md_bus?.Reset();

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
        Console.WriteLine($"[MdTracerAdapter] Reset framebuffer { _fbW }x{ _fbH } stride={ _fbStride }");
    }

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
        if (_fbW != w || _fbH != h || _frameBuffer != _vdp.RgbaFrame)
        {
            _fbW = w;
            _fbH = h;
            _fbStride = stride;
            _frameBuffer = _vdp.RgbaFrame;
            MdTracerCore.MdLog.WriteLine($"[MdTracerAdapter] EnsureFramebufferInitialized({reason}) -> {_fbW}x{_fbH} stride={_fbStride}");
        }
    }

    public void RunFrame()
    {
        if (_tick == 0)
        MdTracerCore.MdLog.WriteLine("[MdTracerAdapter] RunFrame start");

        _tick++;
        long frameStart = Stopwatch.GetTimestamp();

        uint pcAfter = md_m68k.g_reg_PC;
        var directCpu = md_main.g_md_m68k;
        var cpuDirect = md_main.g_md_m68k;
        if (_cpuReady && _cpu != null)
        {
            long vdpTicks = 0;
            long cpuTicks = 0;

            for (int v = 0; v < VLINES_NTSC; v++)
            {
                long start = Stopwatch.GetTimestamp();
                _vdp.run(v);
                vdpTicks += Stopwatch.GetTimestamp() - start;

                start = Stopwatch.GetTimestamp();
                if (cpuDirect != null)
                    cpuDirect.run(200);
                else
                    _cpu.RunSome(budget: 200);
                cpuTicks += Stopwatch.GetTimestamp() - start;
            }

            _accVdpTicks += vdpTicks;
            _accCpuTicks += cpuTicks;

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
            for (int v = 0; v < VLINES_NTSC; v++)
                _vdp.run(v);
        }

        if (_cpuReady && _cpu != null)
        {
            _accFrameTicks += Stopwatch.GetTimestamp() - frameStart;
            _perfFrameCount++;
            MaybeLogPerformance();
        }

        // RGBA direkt till UI (ingen konvertering)
        EnsureFramebufferInitialized("RunFrame");
        _frameBuffer = _vdp.RgbaFrame;
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
            MdTracerCore.MdLog.WriteLine($"[MdTracerAdapter] GetFrameBuffer size={width}x{height} stride={stride} bytes={_frameBuffer.Length}");
        }
        return _frameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = 44100;
        channels = 1;
        return ReadOnlySpan<short>.Empty;
    }

    public void SetInputState(bool up, bool down, bool left, bool right, bool a, bool b, bool c, bool start)
    {
        var io = md_main.g_md_io;
        if (io == null)
            return;

        io._pad1.Up = up;
        io._pad1.Down = down;
        io._pad1.Left = left;
        io._pad1.Right = right;
        io._pad1.A = a;
        io._pad1.B = b;
        io._pad1.C = c;
        io._pad1.Start = start;
    }
}
