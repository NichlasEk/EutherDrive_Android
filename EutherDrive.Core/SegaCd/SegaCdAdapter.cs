using System;
using System.IO;
using EutherDrive.Core.MdTracerCore;

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
    private int _frameWidth;
    private int _frameHeight;
    private byte[] _gfxBuffer = Array.Empty<byte>();
    private int _gfxWidth;
    private int _gfxHeight;
    private string? _romPath;
    private SegaCdDiscInfo? _discInfo;
    private byte[]? _bios;
    private SegaCdMemory? _memory;
    private readonly MdTracerM68kContextRunner _cpuRunner = new();
    private EutherDrive.Core.MdTracerCore.md_m68k.MdM68kContext? _mainContext;
    private EutherDrive.Core.MdTracerCore.md_m68k.MdM68kContext? _subContext;
    private EutherDrive.Core.MdTracerCore.md_bus? _mainBus;
    private EutherDrive.Core.MdTracerCore.md_bus? _subBus;
    private int _subInitLogRemaining = 8;
    private int _subVectorLogRemaining = 16;
    private int _subPcLogRemaining = 32;
    private int _subDumpRemaining = 4;
    private int _subChecksumLogRemaining = 16;
    private bool _subChecksumComputed;
    private bool _lastSubReset;
    private bool _subPcSawChecksumRegion;
    private bool _subPcLoggedExit;
    private int _subPcAfterExitRemaining;
    private bool _subIrqHandlerLogged;
    private byte _subLastIntMask = 0xFF;
    private static readonly bool TraceSubPcAfterExit =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_SUBPC_AFTER_EXIT"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceMainPc =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_MAINPC"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceMainPcFrame =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_MAINPC_FRAME"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceSubWait =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_SUBWAIT"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceFrameBuffer =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_FRAMEBUFFER"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool EnableGfxOverlay =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_GFX_OVERLAY"),
            "1",
            StringComparison.Ordinal);
    private int _mainPcLogRemaining = 32;
    private long _lastMainPcFrame = -1;
    private int _mainDecompLogRemaining = 64;
    private long _lastSubWaitFrame = -1;
    private int _lastFbLogW = -1;
    private int _lastFbLogH = -1;
    private int _lastFbLogStride = -1;
    private int _lastFbLogSrcLen = -1;

    public SegaCdDiscInfo? DiscInfo => _discInfo;
    public ConsoleRegion RegionHint { get; private set; } = ConsoleRegion.Auto;
    public bool EnableRamCartridge { get; set; }
    public bool LoadCdIntoRam { get; set; }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Sega CD image not found.", path);

        _romPath = path;
        _discInfo = SegaCdDiscInfo.Read(path);
        RegionHint = DiscInfoToRegion(_discInfo);
        _bios = SegaCdBios.Load(RegionHint == ConsoleRegion.Auto ? ConsoleRegion.US : RegionHint);
        _memory = new SegaCdMemory(_bios);
        _memory.EnableRamCartridge = EnableRamCartridge;
        _memory.SetDisc(CdRom.Open(path, LoadCdIntoRam));
        bool forceNoDisc = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_FORCE_NO_DISC"), "1", StringComparison.Ordinal);
        if (forceNoDisc)
            _memory.SetDisc(null);
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

    public void DumpPrgRam(string path)
    {
        if (_memory == null)
            return;
        byte[] snapshot = _memory.GetPrgRamSnapshot();
        File.WriteAllBytes(path, snapshot);
    }

    public void RunFrame()
    {
        if (_memory == null || _mainContext == null || _subContext == null || _mainBus == null || _subBus == null)
            return;

        // Rough cycle budgets; refine later.
        const int mainCycles = 127_000;
        const int subCycles = 127_000;

        var vdp = EutherDrive.Core.MdTracerCore.md_main.g_md_vdp;
        if (vdp != null)
        {
            EutherDrive.Core.MdTracerCore.md_m68k.ApplyContext(_mainContext);
            int lines = vdp.g_vertical_line_max > 0 ? vdp.g_vertical_line_max : 262;
            int mainPerLine = mainCycles / lines;
            int mainRemainder = mainCycles % lines;
            int subPerLine = subCycles / lines;
            int subRemainder = subCycles % lines;

            for (int line = 0; line < lines; line++)
            {
                int mainSlice = mainPerLine + (line < mainRemainder ? 1 : 0);
                int subSlice = subPerLine + (line < subRemainder ? 1 : 0);

            EutherDrive.Core.MdTracerCore.md_main.g_md_bus = _mainBus;
            if (TraceMainPcFrame)
            {
                long frame = vdp?.FrameCounter ?? -1;
                if (frame != _lastMainPcFrame)
                {
                    _lastMainPcFrame = frame;
                    Console.WriteLine($"[SCD-MAIN-FRAME] frame={frame} pc=0x{_mainContext.RegPc:X6} sp=0x{_mainContext.RegAddr[7]:X8} op=0x{_mainContext.Opcode:X4}");
                }
            }
            if (mainSlice > 0)
                _cpuRunner.RunSome(_mainContext, mainSlice);
            if (TraceMainPc && _mainPcLogRemaining > 0)
            {
                _mainPcLogRemaining--;
                Console.WriteLine($"[SCD-MAIN] pc=0x{_mainContext.RegPc:X6} sp=0x{_mainContext.RegAddr[7]:X8} op=0x{_mainContext.Opcode:X4}");
            }
            if (_mainDecompLogRemaining > 0 && _mainContext.RegPc >= 0x00090E && _mainContext.RegPc <= 0x00098C)
            {
                _mainDecompLogRemaining--;
                Console.WriteLine(
                    $"[SCD-MAIN-DECOMP] pc=0x{_mainContext.RegPc:X6} op=0x{_mainContext.Opcode:X4} " +
                    $"A0=0x{_mainContext.RegAddr[0]:X8} A1=0x{_mainContext.RegAddr[1]:X8} " +
                    $"D0=0x{_mainContext.RegData[0]:X8} D1=0x{_mainContext.RegData[1]:X8} D2=0x{_mainContext.RegData[2]:X8} D3=0x{_mainContext.RegData[3]:X8}");
            }

            bool subReset = _memory.SubCpuReset;
            if (subReset && !_lastSubReset)
                _subContext = CreateResetContext(0, 0);
            _lastSubReset = subReset;

            if (!_memory.SubCpuHalt && !_memory.SubCpuReset)
            {
                    // Preserve main CPU interrupt state while running the sub CPU.
                    bool mainVReq = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_V_req;
                    bool mainHReq = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_H_req;
                    bool mainExtReq = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_req;
                    bool mainVAct = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_V_act;
                    bool mainHAct = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_H_act;
                    bool mainExtAct = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_act;

                    if (!EnsureSubContextInitialized(_memory))
                    {
                        if (_subInitLogRemaining > 0)
                        {
                            _subInitLogRemaining--;
                            Console.WriteLine("[SCD-SUB] Skipping sub CPU: reset vectors not initialized yet.");
                        }
                    }
                    else
                    {
                        byte subLevel = _memory.GetSubInterruptLevel();
                        if (subLevel != 0)
                        {
                            // Route sub CPU interrupts through EXT vector with dynamic level/vector.
                            EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_level = subLevel;
                            EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_vector = (uint)(0x0060 + (subLevel * 4));
                            EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_ack = _memory.AcknowledgeSubInterrupt;
                        }
                        _subContext.InterruptExtReq = subLevel != 0;
                        EutherDrive.Core.MdTracerCore.md_main.g_md_bus = _subBus;
                    if (subSlice > 0)
                        _cpuRunner.RunSome(_subContext, subSlice);
                    uint subPc = _subContext.RegPc;
                    if (subPc >= 0x0002C0 && subPc <= 0x000320)
                    {
                        _subPcSawChecksumRegion = true;
                    }
                    else if (_subPcSawChecksumRegion && !_subPcLoggedExit)
                    {
                        _subPcLoggedExit = true;
                        Console.WriteLine($"[SCD-SUB] left checksum region pc=0x{subPc:X6}");
                        if (TraceSubPcAfterExit)
                            _subPcAfterExitRemaining = 16;
                    }
                    if (!_subIrqHandlerLogged && subPc >= 0x0005F2 && subPc <= 0x000610)
                    {
                        _subIrqHandlerLogged = true;
                        Console.WriteLine($"[SCD-SUB] entered IRQ handler pc=0x{subPc:X6}");
                    }
                    if (_subPcAfterExitRemaining > 0)
                    {
                        _subPcAfterExitRemaining--;
                        Console.WriteLine($"[SCD-SUB] post-exit pc=0x{subPc:X6} op=0x{_subContext.Opcode:X4}");
                    }
                    if (TraceSubWait && subPc >= 0x0005E0 && subPc <= 0x0005F0)
                    {
                        long frame = vdp?.FrameCounter ?? -1;
                        if (frame != _lastSubWaitFrame)
                        {
                            _lastSubWaitFrame = frame;
                            byte prgFlag = _memory.ReadSubByte(0x0005EA4);
                            byte subFlag = _memory.ReadSubByte(0xFF800F);
                            byte mainFlag = _memory.ReadSubByte(0xFF800E);
                            Console.WriteLine(
                                $"[SCD-SUBWAIT] frame={frame} pc=0x{subPc:X6} prg[0x005EA4]=0x{prgFlag:X2} " +
                                $"mainFlag=0x{mainFlag:X2} subFlag=0x{subFlag:X2}");
                        }
                    }
                    if (_subContext.StatusInterruptMask != _subLastIntMask)
                    {
                        _subLastIntMask = _subContext.StatusInterruptMask;
                        Console.WriteLine($"[SCD-SUB] int-mask={_subLastIntMask}");
                    }
                    if (_subPcLogRemaining > 0)
                    {
                        _subPcLogRemaining--;
                        Console.WriteLine($"[SCD-SUB] pc=0x{_subContext.RegPc:X6} sp=0x{_subContext.RegAddr[7]:X8} op=0x{_subContext.Opcode:X4}");
                    }
                        if (_subDumpRemaining > 0 && _subContext.RegPc >= 0x0002E0 && _subContext.RegPc <= 0x0002E2)
                        {
                            _subDumpRemaining--;
                            uint baseAddr = 0x0002D0;
                            Span<byte> bytes = stackalloc byte[64];
                            for (int i = 0; i < bytes.Length; i++)
                                bytes[i] = _memory.ReadSubByte(baseAddr + (uint)i);
                            ushort w18e = _memory.ReadSubWord(0x00018E);
                            ushort w18c = _memory.ReadSubWord(0x00018C);
                            string hex = BitConverter.ToString(bytes.ToArray()).Replace("-", " ");
                            Console.WriteLine(
                                $"[SCD-SUB] dump @0x{baseAddr:X6}: {hex} " +
                                $"W[0x018C]=0x{w18c:X4} W[0x018E]=0x{w18e:X4} " +
                                $"D0=0x{_subContext.RegData[0]:X8} D1=0x{_subContext.RegData[1]:X8} D2=0x{_subContext.RegData[2]:X8} " +
                                $"A0=0x{_subContext.RegAddr[0]:X8} A1=0x{_subContext.RegAddr[1]:X8}");
                            if (!_subChecksumComputed)
                            {
                                _subChecksumComputed = true;
                                uint start = _subContext.RegAddr[0] & 0xFFFFF;
                                uint count = (uint)((_subContext.RegData[2] & 0xFFFF) + 1);
                                uint end = start + (count * 2);
                                ushort sumBe = 0;
                                ushort sumLe = 0;
                                uint zeroWords = 0;
                                for (uint addr = start; addr < end; addr += 2)
                                {
                                    ushort subWord = _memory.ReadSubWord(addr);
                                    sumBe = (ushort)(sumBe + subWord);
                                    ushort swapped = (ushort)((subWord << 8) | (subWord >> 8));
                                    sumLe = (ushort)(sumLe + swapped);
                                    if (subWord == 0)
                                        zeroWords++;
                                }
                                ushort expected = _memory.ReadSubWord(0x00018E);
                                Console.WriteLine(
                                    $"[SCD-CHK] start=0x{start:X6} count={count} end=0x{(end - 1):X6} sumBE=0x{sumBe:X4} sumLE=0x{sumLe:X4} expected=0x{expected:X4} zeroWords={zeroWords}");
                            }
                        }
                        if (_subChecksumLogRemaining > 0 && _subContext.RegPc >= 0x0002EA && _subContext.RegPc <= 0x0002F2)
                        {
                            _subChecksumLogRemaining--;
                            ushort expected = _memory.ReadSubWord(0x00018E);
                            Console.WriteLine(
                                $"[SCD-SUB] chk pc=0x{_subContext.RegPc:X6} D0=0x{_subContext.RegData[0] & 0xFFFF:X4} D1=0x{_subContext.RegData[1] & 0xFFFF:X4} exp=0x{expected:X4}");
                        }
                        _memory.FlushBufferedSubWrites();
                    }

                    // Restore main CPU interrupt state after sub CPU run.
                    EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_V_req = mainVReq;
                    EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_H_req = mainHReq;
                    EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_req = mainExtReq;
                    EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_V_act = mainVAct;
                    EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_H_act = mainHAct;
                    EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_act = mainExtAct;
                }
                else
                {
                    _memory.EmulateSubCpuHandshake();
                }

                EutherDrive.Core.MdTracerCore.md_main.g_md_bus = _mainBus;
                if (mainSlice > 0)
                    _memory.Tick((uint)mainSlice);

                vdp.run(line);

                // Capture VDP-driven interrupt requests into the main CPU context.
                _mainContext.InterruptVReq = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_V_req;
                _mainContext.InterruptHReq = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_H_req;
                _mainContext.InterruptExtReq = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_req;
                _mainContext.InterruptVAct = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_V_act;
                _mainContext.InterruptHAct = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_H_act;
                _mainContext.InterruptExtAct = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_act;
            }
            // Ensure main CPU context is active before capturing.
            EutherDrive.Core.MdTracerCore.md_m68k.ApplyContext(_mainContext);
            EutherDrive.Core.MdTracerCore.md_m68k.CaptureContext(_mainContext);

            int frameW = vdp.FrameWidth > 0 ? vdp.FrameWidth : DefaultW;
            int frameH = vdp.FrameHeight > 0 ? vdp.FrameHeight : DefaultH;
            _frameWidth = frameW;
            _frameHeight = frameH;
            int needed = frameW * frameH * 4;
            if (_frameBuffer.Length != needed)
                _frameBuffer = new byte[needed];

            var src = vdp.RgbaFrame;
            if (TraceFrameBuffer)
            {
                int stride = frameW * 4;
                if (frameW != _lastFbLogW || frameH != _lastFbLogH || stride != _lastFbLogStride || src.Length != _lastFbLogSrcLen)
                {
                    _lastFbLogW = frameW;
                    _lastFbLogH = frameH;
                    _lastFbLogStride = stride;
                    _lastFbLogSrcLen = src.Length;
                    Console.WriteLine($"[SCD-FRAME] vdp w={frameW} h={frameH} stride={stride} srcLen={src.Length}");
                }
            }
            int pixels = Math.Min(frameW * frameH, src.Length / 4);
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

        if (_memory != null && _memory.Graphics.TryGetImageBufferDimensions(out int gw, out int gh))
        {
            int needed = gw * gh * 4;
            if (_gfxBuffer.Length != needed)
                _gfxBuffer = new byte[needed];

            if (_memory.Graphics.TryRenderImageBuffer(_memory.WordRam, _gfxBuffer, out int rw, out int rh))
            {
                _gfxWidth = rw;
                _gfxHeight = rh;
                if (TraceFrameBuffer)
                    Console.WriteLine($"[SCD-FRAME] gfx w={rw} h={rh} stride={rw * 4}");

                // Composite gfx buffer over the VDP output without changing the frame size.
                if (EnableGfxOverlay)
                {
                    int copyW = Math.Min(_frameWidth, _gfxWidth);
                    int copyH = Math.Min(_frameHeight, _gfxHeight);
                    int dstStride = _frameWidth * 4;
                    int srcStride = _gfxWidth * 4;
                    for (int y = 0; y < copyH; y++)
                    {
                        int srcRow = y * srcStride;
                        int dstRow = y * dstStride;
                        Array.Copy(_gfxBuffer, srcRow, _frameBuffer, dstRow, copyW * 4);
                    }
                }
            }
        }

        _audioBuffer = _memory != null ? _memory.ConsumeAudioBuffer() : Array.Empty<short>();
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = _frameWidth > 0 ? _frameWidth : DefaultW;
        height = _frameHeight > 0 ? _frameHeight : DefaultH;
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
        md_sms_io.SetPad1Input(state);
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

    private static uint ReadSubVector(SegaCdMemory memory, uint address)
    {
        ushort msw = memory.ReadSubWord(address);
        ushort lsw = memory.ReadSubWord(address + 2);
        return (uint)((msw << 16) | lsw);
    }

    private static EutherDrive.Core.MdTracerCore.md_m68k.MdM68kContext CreateResetContext(uint initialPc, uint initialSp)
    {
        var ctx = new EutherDrive.Core.MdTracerCore.md_m68k.MdM68kContext
        {
            RegPc = initialPc,
            InitialPc = initialPc,
            StackTop = initialSp,
            RegAddrUsp = initialSp,
            Stop = false,
            StatusS = true,
            StatusInterruptMask = 7
        };

        ctx.RegAddr[7] = initialSp;
        return ctx;
    }

    private bool EnsureSubContextInitialized(SegaCdMemory memory)
    {
        if (_subContext == null)
            return false;

        if (_subContext.RegAddr[7] != 0 && _subContext.RegPc != 0)
            return true;

        uint subSp = ReadSubVector(memory, 0);
        uint subPc = ReadSubVector(memory, 4);
        if (_subVectorLogRemaining > 0)
        {
            _subVectorLogRemaining--;
            Console.WriteLine($"[SCD-SUB] vectors sp=0x{subSp:X8} pc=0x{subPc:X8}");
        }
        if (subSp == 0 || subPc == 0)
            return false;

        _subContext.RegPc = subPc;
        _subContext.InitialPc = subPc;
        _subContext.StackTop = subSp;
        _subContext.RegAddrUsp = subSp;
        _subContext.StatusS = true;
        _subContext.StatusInterruptMask = 7;
        _subContext.RegAddr[7] = subSp;
        Console.WriteLine($"[SCD-SUB] Initialized sub CPU sp=0x{subSp:X8} pc=0x{subPc:X8}");
        return true;
    }
}
