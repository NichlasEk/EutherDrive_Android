using System;
using System.IO;

namespace EutherDrive.Core.SegaCd;

// NOTE: This is the initial scaffold for Sega CD core porting.
// Emulation core translation from jgenesis is in progress; this adapter
// currently loads BIOS + disc info and exposes a blank framebuffer/audio.
public sealed class SegaCdAdapter : IEmulatorCore
{
    private const int DefaultW = 320;
    private const int DefaultH = 224;
    private const int DefaultStride = DefaultW * 4;

    private byte[] _frameBuffer = new byte[DefaultStride * DefaultH];
    private short[] _audioBuffer = Array.Empty<short>();
    private string? _romPath;
    private SegaCdDiscInfo? _discInfo;
    private byte[]? _bios;
    private SegaCdMemory? _memory;
    private readonly MdTracerM68kContextRunner _cpuRunner = new();
    private EutherDrive.Core.MdTracerCore.md_m68k.MdM68kContext? _mainContext;
    private EutherDrive.Core.MdTracerCore.md_m68k.MdM68kContext? _subContext;
    private EutherDrive.Core.MdTracerCore.md_bus? _mainBus;
    private EutherDrive.Core.MdTracerCore.md_bus? _subBus;

    public SegaCdDiscInfo? DiscInfo => _discInfo;
    public ConsoleRegion RegionHint { get; private set; } = ConsoleRegion.Auto;

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Sega CD image not found.", path);

        _romPath = path;
        _discInfo = SegaCdDiscInfo.Read(path);
        RegionHint = DiscInfoToRegion(_discInfo);
        _bios = SegaCdBios.Load(RegionHint == ConsoleRegion.Auto ? ConsoleRegion.US : RegionHint);
        _memory = new SegaCdMemory(_bios);
        _memory.Cdd.SetDiscPresent(true);
        EutherDrive.Core.MdTracerCore.md_main.initialize();
        _mainBus = EutherDrive.Core.MdTracerCore.md_main.g_md_bus;
        if (_mainBus != null)
            _mainBus.OverrideBus = new SegaCdMainBusOverride(_memory);
        _subBus = new EutherDrive.Core.MdTracerCore.md_bus
        {
            OverrideBus = new SegaCdSubBusOverride(_memory)
        };

        if (EutherDrive.Core.MdTracerCore.md_main.g_md_vdp != null)
            EutherDrive.Core.MdTracerCore.md_main.g_md_vdp.reset();

        uint initialSp = ReadMainVector(_memory, 0);
        uint initialPc = ReadMainVector(_memory, 4);
        _mainContext = CreateResetContext(initialPc, initialSp);
        _subContext = CreateResetContext(0, 0);

        // TODO: Instantiate Sega CD emulator core once ported.
        // For now, just clear framebuffer.
        Array.Clear(_frameBuffer, 0, _frameBuffer.Length);
        _audioBuffer = Array.Empty<short>();
    }

    public void Reset()
    {
        if (!string.IsNullOrWhiteSpace(_romPath))
            LoadRom(_romPath);
    }

    public void RunFrame()
    {
        if (_memory == null || _mainContext == null || _subContext == null || _mainBus == null || _subBus == null)
            return;

        // Rough cycle budgets; refine later.
        const int mainCycles = 127_000;
        const int subCycles = 127_000;

        EutherDrive.Core.MdTracerCore.md_main.g_md_bus = _mainBus;
        _cpuRunner.RunSome(_mainContext, mainCycles);

        if (!_memory.SubCpuHalt && !_memory.SubCpuReset)
        {
            EutherDrive.Core.MdTracerCore.md_main.g_md_bus = _subBus;
            _cpuRunner.RunSome(_subContext, subCycles);
            _memory.FlushBufferedSubWrites();
        }

        EutherDrive.Core.MdTracerCore.md_main.g_md_bus = _mainBus;
        _memory.Tick((uint)mainCycles);

        var vdp = EutherDrive.Core.MdTracerCore.md_main.g_md_vdp;
        if (vdp != null)
        {
            EutherDrive.Core.MdTracerCore.md_m68k.ApplyContext(_mainContext);
            int lines = vdp.g_vertical_line_max > 0 ? vdp.g_vertical_line_max : 262;
            for (int line = 0; line < lines; line++)
                vdp.run(line);
            EutherDrive.Core.MdTracerCore.md_m68k.CaptureContext(_mainContext);

            int w = vdp.FrameWidth > 0 ? vdp.FrameWidth : DefaultW;
            int h = vdp.FrameHeight > 0 ? vdp.FrameHeight : DefaultH;
            int needed = w * h * 4;
            if (_frameBuffer.Length != needed)
                _frameBuffer = new byte[needed];

            var src = vdp.RgbaFrame;
            int pixels = Math.Min(w * h, src.Length / 4);
            int si = 0;
            int di = 0;
            for (int i = 0; i < pixels; i++)
            {
                byte r = src[si + 0];
                byte g = src[si + 1];
                byte b = src[si + 2];
                byte a = src[si + 3];
                _frameBuffer[di + 0] = b;
                _frameBuffer[di + 1] = g;
                _frameBuffer[di + 2] = r;
                _frameBuffer[di + 3] = a;
                si += 4;
                di += 4;
            }
        }
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        var vdp = EutherDrive.Core.MdTracerCore.md_main.g_md_vdp;
        width = vdp?.FrameWidth ?? DefaultW;
        height = vdp?.FrameHeight ?? DefaultH;
        stride = width * 4;
        return _frameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = 44100;
        channels = 2;
        return _audioBuffer;
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
        _ = up; _ = down; _ = left; _ = right;
        _ = a; _ = b; _ = c; _ = start;
        _ = x; _ = y; _ = z; _ = mode;
        _ = padType;
        // TODO: wire inputs to Genesis controller once Sega CD core is ported
    }

    private static ConsoleRegion DiscInfoToRegion(SegaCdDiscInfo? info)
    {
        if (info == null || string.IsNullOrWhiteSpace(info.Region))
            return ConsoleRegion.Auto;

        return info.Region switch
        {
            "NTSC-J" => ConsoleRegion.JP,
            "NTSC-U" => ConsoleRegion.US,
            "PAL" => ConsoleRegion.EU,
            _ => ConsoleRegion.Auto
        };
    }

    private static uint ReadMainVector(SegaCdMemory memory, uint address)
    {
        ushort msw = memory.ReadMainWord(address);
        ushort lsw = memory.ReadMainWord(address + 2);
        return (uint)((msw << 16) | lsw);
    }

    private static EutherDrive.Core.MdTracerCore.md_m68k.MdM68kContext CreateResetContext(uint initialPc, uint initialSp)
    {
        var ctx = new EutherDrive.Core.MdTracerCore.md_m68k.MdM68kContext
        {
            RegPc = initialPc,
            InitialPc = initialPc,
            StackTop = initialSp,
            RegAddrUsp = 0,
            Stop = false,
            StatusS = true,
            StatusInterruptMask = 7
        };

        ctx.RegAddr[7] = initialSp;
        return ctx;
    }
}
