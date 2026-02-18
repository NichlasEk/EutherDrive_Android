using System;
using System.Diagnostics;
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
    private static readonly bool TraceSubMilestones =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_SUB_MILESTONES"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceSubDebug =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_SUB_DEBUG"),
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
    private static readonly bool TraceMainDebug =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_MAIN_DEBUG"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceSubWait =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_SUBWAIT"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceSubIntMask =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_SUBINT_MASK"),
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
    private static readonly bool ProfileScd =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_PROFILE"),
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
    private long _profileFrames;
    private long _profileTotalTicks;
    private long _profileCpuTicks;
    private long _profileVdpTicks;
    private long _profileGfxTicks;
    private long _profileAudioTicks;
    private static readonly int MainCyclesPerFrame = ReadCyclesPerFrame("EUTHERDRIVE_SCD_MAIN_CYCLES");
    private static readonly int SubCyclesPerFrame = ReadCyclesPerFrame("EUTHERDRIVE_SCD_SUB_CYCLES");
    private static readonly int MclkPerSubCycle = ReadCyclesPerFrame("EUTHERDRIVE_SCD_MCLK_PER_SUB", 4);
    private static readonly double? TargetFpsOverride = ReadDouble("EUTHERDRIVE_SCD_TARGET_FPS");
    private const double MainClockHz = 7_670_453.0;
    private const double SubClockHz = 12_500_000.0;
    private const double Z80ClockHz = 3_579_545.0;
    private const int PcmDivider = 384;
    private const double PcmSampleRate = SubClockHz / PcmDivider;
    private const int PsgSampleRate = 44100;
    private const int PsgChannels = 2;
    private const int YmInternalSampleRate = 53267;
    private const double PcmCoefficient = 0.5011872336272722; // -6 dB
    private const double CdCoefficient = 0.44668359215096315; // -7 dB
    private const double PsgCoefficient = 0.44668359215096315; // jgenesis PSG coefficient
    private static readonly double YmResampleScale = GetYmResampleScale();
    private static readonly double FmMixGain = GetFmMixGain();
    private static readonly double PcmMixGain = ReadDoubleOr("EUTHERDRIVE_SCD_PCM_GAIN", PcmCoefficient);
    private static readonly double CdMixGain = ReadDoubleOr("EUTHERDRIVE_SCD_CD_GAIN", CdCoefficient);
    private static readonly double PsgMixGain = ReadDoubleOr("EUTHERDRIVE_SCD_PSG_GAIN", PsgCoefficient);
    private static readonly double YmMixGain = ReadDoubleOr("EUTHERDRIVE_SCD_YM_GAIN", 1.0);
    private readonly bool _psgDisabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DISABLE_PSG"), "1", StringComparison.Ordinal);
    private bool _ymEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM"), "0", StringComparison.Ordinal);
    private double _mainCycleRemainder;
    private double _subCycleRemainder;
    private double _z80CycleRemainder;
    private double _lastTargetFps = 60.0;
    private double _ymResamplePhase;
    private bool _ymResampleHasCarry;
    private short _ymResampleCarryL;
    private short _ymResampleCarryR;
    private short[] _psgFrameBuffer = Array.Empty<short>();
    private short[] _ymFrameBuffer = Array.Empty<short>();
    private short[] _ymInternalBuffer = Array.Empty<short>();
    private static readonly bool TraceScdAudio =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_AUDIO"), "1", StringComparison.Ordinal);
    private static readonly bool TraceScdAudioEmpty =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_AUDIO_EMPTY"), "1", StringComparison.Ordinal);
    private static readonly bool TraceScdAudioStats =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_AUDIO_STATS"), "1", StringComparison.Ordinal);
    private static readonly bool TraceScdAudioSplit =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_AUDIO_SPLIT"), "1", StringComparison.Ordinal);
    private static readonly bool TraceScdAudioCore =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_AUDIO_CORE"), "1", StringComparison.Ordinal);
    private long _audioTraceLastTicks;
    private long _audioSplitLastTicks;
    private static readonly bool TraceCycleBudget =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_CYCLES"), "1", StringComparison.Ordinal);
    private bool _cycleBudgetLogged;
    private BiquadLowPass? _pcmLowPass;
    private BiquadLowPass? _cdLowPass;
    private SincResampler _pcmResampler = new(PcmSampleRate, 44100.0);

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
        _ymResamplePhase = 0;
        _ymResampleHasCarry = false;
        bool forceNoDisc = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_FORCE_NO_DISC"), "1", StringComparison.Ordinal);
        if (forceNoDisc)
            _memory.SetDisc(null);
        if (TraceCycleBudget)
            Console.Error.WriteLine($"[SCD-CYCLES] main={MainCyclesPerFrame} sub={SubCyclesPerFrame} (load)");
        EutherDrive.Core.MdTracerCore.md_main.initialize();
        EutherDrive.Core.MdTracerCore.md_main.ResetZ80WaitState();
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
        _pcmLowPass = CreateLowPass("EUTHERDRIVE_SCD_PCM_LP_HZ", 44100, 7973);
        _cdLowPass = CreateLowPass("EUTHERDRIVE_SCD_CD_LP_HZ", 44100, 0);
        _pcmResampler = new SincResampler(PcmSampleRate, 44100.0);
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

        long frameStart = ProfileScd ? Stopwatch.GetTimestamp() : 0;

        // Derive cycle budgets from clock rates unless overridden via env.
        int mainCycles = MainCyclesPerFrame;
        int subCycles = SubCyclesPerFrame;
        int z80Cycles = 0;

        var vdp = EutherDrive.Core.MdTracerCore.md_main.g_md_vdp;
        if (vdp != null)
        {
            EutherDrive.Core.MdTracerCore.md_m68k.ApplyContext(_mainContext);
            int lines = vdp.g_vertical_line_max > 0 ? vdp.g_vertical_line_max : 262;
            double targetFps = TargetFpsOverride
                ?? (lines >= 312 ? 50.0 : 59.922);
            _lastTargetFps = targetFps;
            long frame = vdp?.FrameCounter ?? -1;
            EutherDrive.Core.MdTracerCore.md_main.g_md_bus?.TickZ80SafeBoot(frame);
            bool allowZ80 = EutherDrive.Core.MdTracerCore.md_main.ShouldRunZ80(frame);
            if (mainCycles <= 0)
                mainCycles = ComputeCyclesPerFrame(MainClockHz, targetFps, ref _mainCycleRemainder);
            if (subCycles <= 0)
                subCycles = ComputeCyclesPerFrame(SubClockHz, targetFps, ref _subCycleRemainder);
            z80Cycles = ComputeCyclesPerFrame(Z80ClockHz, targetFps, ref _z80CycleRemainder);
            if (TraceCycleBudget && !_cycleBudgetLogged)
            {
                _cycleBudgetLogged = true;
                Console.Error.WriteLine($"[SCD-CYCLES] main={mainCycles} sub={subCycles} lines={lines} fps={targetFps:0.###}");
            }
            int mainPerLine = mainCycles / lines;
            int mainRemainder = mainCycles % lines;
            int subPerLine = subCycles / lines;
            int subRemainder = subCycles % lines;
            int z80PerLine = z80Cycles / lines;
            int z80Remainder = z80Cycles % lines;

            for (int line = 0; line < lines; line++)
            {
                int mainSlice = mainPerLine + (line < mainRemainder ? 1 : 0);
                int subSlice = subPerLine + (line < subRemainder ? 1 : 0);
                int z80Slice = z80PerLine + (line < z80Remainder ? 1 : 0);

            EutherDrive.Core.MdTracerCore.md_main.g_md_bus = _mainBus;
            if (TraceMainPcFrame)
            {
                if (frame != _lastMainPcFrame)
                {
                    _lastMainPcFrame = frame;
                    Console.WriteLine($"[SCD-MAIN-FRAME] frame={frame} pc=0x{_mainContext.RegPc:X6} sp=0x{_mainContext.RegAddr[7]:X8} op=0x{_mainContext.Opcode:X4}");
                }
            }
            if (mainSlice > 0)
            {
                if (ProfileScd) _profileCpuTicks -= Stopwatch.GetTimestamp();
                _cpuRunner.RunSome(_mainContext, mainSlice);
                if (ProfileScd) _profileCpuTicks += Stopwatch.GetTimestamp();
            }

            if (z80Slice > 0 && allowZ80 && _mainBus != null)
            {
                bool busReq = _mainBus.Z80BusGranted;
                bool reset = _mainBus.Z80Reset;
                if (!busReq && !reset)
                    EutherDrive.Core.MdTracerCore.md_main.g_md_z80?.run(z80Slice);
            }
            if (TraceMainPc && _mainPcLogRemaining > 0)
            {
                _mainPcLogRemaining--;
                Console.WriteLine($"[SCD-MAIN] pc=0x{_mainContext.RegPc:X6} sp=0x{_mainContext.RegAddr[7]:X8} op=0x{_mainContext.Opcode:X4}");
            }
            if (TraceMainDebug && _mainDecompLogRemaining > 0 && _mainContext.RegPc >= 0x00090E && _mainContext.RegPc <= 0x00098C)
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
                        if (TraceSubDebug && _subInitLogRemaining > 0)
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
                    {
                        if (ProfileScd) _profileCpuTicks -= Stopwatch.GetTimestamp();
                        _cpuRunner.RunSome(_subContext, subSlice);
                        if (ProfileScd) _profileCpuTicks += Stopwatch.GetTimestamp();
                    }
                    uint subPc = _subContext.RegPc;
                    if (subPc >= 0x0002C0 && subPc <= 0x000320)
                    {
                        _subPcSawChecksumRegion = true;
                    }
                    else if (_subPcSawChecksumRegion && !_subPcLoggedExit)
                    {
                        _subPcLoggedExit = true;
                        if (TraceSubMilestones)
                            Console.WriteLine($"[SCD-SUB] left checksum region pc=0x{subPc:X6}");
                        if (TraceSubPcAfterExit)
                            _subPcAfterExitRemaining = 16;
                    }
                    if (!_subIrqHandlerLogged && subPc >= 0x0005F2 && subPc <= 0x000610)
                    {
                        _subIrqHandlerLogged = true;
                        if (TraceSubMilestones)
                            Console.WriteLine($"[SCD-SUB] entered IRQ handler pc=0x{subPc:X6}");
                    }
                    if (TraceSubPcAfterExit && _subPcAfterExitRemaining > 0)
                    {
                        _subPcAfterExitRemaining--;
                        Console.WriteLine($"[SCD-SUB] post-exit pc=0x{subPc:X6} op=0x{_subContext.Opcode:X4}");
                    }
                    if (TraceSubWait && subPc >= 0x0005E0 && subPc <= 0x0005F0)
                    {
                        long subWaitFrame = vdp?.FrameCounter ?? -1;
                        if (subWaitFrame != _lastSubWaitFrame)
                        {
                            _lastSubWaitFrame = subWaitFrame;
                            byte prgFlag = _memory.ReadSubByte(0x0005EA4);
                            byte subFlag = _memory.ReadSubByte(0xFF800F);
                            byte mainFlag = _memory.ReadSubByte(0xFF800E);
                            Console.WriteLine(
                                $"[SCD-SUBWAIT] frame={subWaitFrame} pc=0x{subPc:X6} prg[0x005EA4]=0x{prgFlag:X2} " +
                                $"mainFlag=0x{mainFlag:X2} subFlag=0x{subFlag:X2}");
                        }
                    }
                    if (_subContext.StatusInterruptMask != _subLastIntMask)
                    {
                        _subLastIntMask = _subContext.StatusInterruptMask;
                        if (TraceSubIntMask)
                            Console.WriteLine($"[SCD-SUB] int-mask={_subLastIntMask}");
                    }
                    if (TraceSubDebug && _subPcLogRemaining > 0)
                    {
                        _subPcLogRemaining--;
                        Console.WriteLine($"[SCD-SUB] pc=0x{_subContext.RegPc:X6} sp=0x{_subContext.RegAddr[7]:X8} op=0x{_subContext.Opcode:X4}");
                    }
                        if (TraceSubDebug && _subDumpRemaining > 0 && _subContext.RegPc >= 0x0002E0 && _subContext.RegPc <= 0x0002E2)
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
                        if (TraceSubDebug && _subChecksumLogRemaining > 0 && _subContext.RegPc >= 0x0002EA && _subContext.RegPc <= 0x0002F2)
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

                if (ProfileScd) _profileVdpTicks -= Stopwatch.GetTimestamp();
                vdp.run(line);
                if (ProfileScd) _profileVdpTicks += Stopwatch.GetTimestamp();

                // Capture VDP-driven interrupt requests into the main CPU context.
                _mainContext.InterruptVReq = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_V_req;
                _mainContext.InterruptHReq = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_H_req;
                _mainContext.InterruptExtReq = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_req;
                _mainContext.InterruptVAct = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_V_act;
                _mainContext.InterruptHAct = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_H_act;
                _mainContext.InterruptExtAct = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_act;
            }
            if (subCycles > 0)
            {
                uint mclkCycles = (uint)Math.Max(1, subCycles * Math.Max(1, MclkPerSubCycle));
                _memory.Tick(mclkCycles);
                _memory.Pcm.Tick((uint)subCycles);
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
            if (ProfileScd) _profileGfxTicks -= Stopwatch.GetTimestamp();
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
            if (ProfileScd) _profileGfxTicks += Stopwatch.GetTimestamp();
        }

        if (ProfileScd) _profileAudioTicks -= Stopwatch.GetTimestamp();
        if (_memory != null)
        {
            short[] cdAudio = _memory.ConsumeCdAudioBuffer();
            short[] pcmAudio = _memory.ConsumePcmAudioBuffer();
            _audioBuffer = MixAudio(cdAudio, pcmAudio, _lastTargetFps);
            MixWithPsgYm(_audioBuffer, _lastTargetFps);
            if (TraceScdAudioSplit)
                TraceSplitAudio(cdAudio, pcmAudio);
        }
        else
        {
            _audioBuffer = Array.Empty<short>();
        }
        if (TraceScdAudio)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _audioTraceLastTicks >= Stopwatch.Frequency)
            {
                _audioTraceLastTicks = now;
                Console.Error.WriteLine($"[SCD-AUDIO] rate=44100 ch=2 samples={_audioBuffer.Length}");
            }
        }
        else if (TraceScdAudioStats)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _audioTraceLastTicks >= Stopwatch.Frequency)
            {
                _audioTraceLastTicks = now;
                if (_audioBuffer.Length == 0)
                {
                    Console.Error.WriteLine("[SCD-AUDIO] stats empty");
                }
                else
                {
                    short min = short.MaxValue;
                    short max = short.MinValue;
                    int nonZero = 0;
                    for (int i = 0; i < _audioBuffer.Length; i++)
                    {
                        short sample = _audioBuffer[i];
                        if (sample != 0)
                            nonZero++;
                        if (sample < min)
                            min = sample;
                        if (sample > max)
                            max = sample;
                    }
                    Console.Error.WriteLine(
                        $"[SCD-AUDIO] stats samples={_audioBuffer.Length} nonzero={nonZero} min={min} max={max}");
                }
            }
        }
        else if (TraceScdAudioEmpty && _audioBuffer.Length == 0)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _audioTraceLastTicks >= Stopwatch.Frequency)
            {
                _audioTraceLastTicks = now;
                Console.Error.WriteLine("[SCD-AUDIO] empty");
            }
        }
        if (ProfileScd) _profileAudioTicks += Stopwatch.GetTimestamp();

        if (ProfileScd)
        {
            _profileFrames++;
            _profileTotalTicks += Stopwatch.GetTimestamp() - frameStart;
            if ((_profileFrames % 60) == 0)
            {
                double ticksPerSec = Stopwatch.Frequency;
                double fps = _profileFrames / (_profileTotalTicks / ticksPerSec);
                double cpuMs = (_profileCpuTicks / ticksPerSec) * 1000.0;
                double vdpMs = (_profileVdpTicks / ticksPerSec) * 1000.0;
                double gfxMs = (_profileGfxTicks / ticksPerSec) * 1000.0;
                double audMs = (_profileAudioTicks / ticksPerSec) * 1000.0;
                Console.Error.WriteLine(
                    $"[SCD-PROFILE] frames={_profileFrames} fps={fps:0.0} " +
                    $"cpu_ms={cpuMs:0.0} vdp_ms={vdpMs:0.0} gfx_ms={gfxMs:0.0} audio_ms={audMs:0.0}");
            }
        }
    }

    private static int ReadCyclesPerFrame(string key, int fallback = 0)
    {
        string? raw = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value)
            && value > 0)
        {
            return value;
        }
        return fallback;
    }

    private static double? ReadDouble(string key)
    {
        string? raw = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            && value > 0)
        {
            return value;
        }
        return null;
    }

    private static int ComputeCyclesPerFrame(double clockHz, double fps, ref double remainder)
    {
        if (fps <= 0)
            fps = 60.0;
        double exact = clockHz / fps;
        double withRemainder = exact + remainder;
        int whole = (int)Math.Floor(withRemainder);
        remainder = withRemainder - whole;
        if (whole <= 0)
            return 1;
        return whole;
    }

    private sealed class BiquadLowPass
    {
        private readonly Biquad _left;
        private readonly Biquad _right;

        public BiquadLowPass(int sampleRate, double cutoffHz)
        {
            _left = Biquad.CreateLowPass(sampleRate, cutoffHz);
            _right = Biquad.CreateLowPass(sampleRate, cutoffHz);
        }

        public void Apply(short[] buffer, int samples)
        {
            for (int i = 0; i < samples; i += 2)
            {
                double l = _left.Process(buffer[i]);
                double r = _right.Process(buffer[i + 1]);
                buffer[i] = (short)Math.Clamp((int)Math.Round(l), short.MinValue, short.MaxValue);
                buffer[i + 1] = (short)Math.Clamp((int)Math.Round(r), short.MinValue, short.MaxValue);
            }
        }

        private sealed class Biquad
        {
            private readonly double _b0;
            private readonly double _b1;
            private readonly double _b2;
            private readonly double _a1;
            private readonly double _a2;
            private double _z1;
            private double _z2;

            private Biquad(double b0, double b1, double b2, double a1, double a2)
            {
                _b0 = b0;
                _b1 = b1;
                _b2 = b2;
                _a1 = a1;
                _a2 = a2;
            }

            public static Biquad CreateLowPass(int sampleRate, double cutoffHz)
            {
                if (cutoffHz <= 0)
                    cutoffHz = 8000;
                double omega = 2.0 * Math.PI * cutoffHz / sampleRate;
                double cos = Math.Cos(omega);
                double sin = Math.Sin(omega);
                double q = 1.0 / Math.Sqrt(2.0);
                double alpha = sin / (2.0 * q);
                double b0 = (1.0 - cos) * 0.5;
                double b1 = 1.0 - cos;
                double b2 = (1.0 - cos) * 0.5;
                double a0 = 1.0 + alpha;
                double a1 = -2.0 * cos;
                double a2 = 1.0 - alpha;
                return new Biquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
            }

            public double Process(double input)
            {
                double output = _b0 * input + _z1;
                _z1 = _b1 * input - _a1 * output + _z2;
                _z2 = _b2 * input - _a2 * output;
                return output;
            }
        }
    }

    private static BiquadLowPass? CreateLowPass(string envKey, int sampleRate, double defaultCutoff)
    {
        string? raw = Environment.GetEnvironmentVariable(envKey);
        double cutoff;
        if (string.IsNullOrWhiteSpace(raw))
        {
            cutoff = defaultCutoff;
        }
        else if (!double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out cutoff))
        {
            return null;
        }
        if (cutoff <= 0)
            return null;
        return new BiquadLowPass(sampleRate, cutoff);
    }

    private static void ApplyGainInPlace(short[] buffer, int samples, double gain)
    {
        if (samples <= 0 || gain == 1.0)
            return;
        for (int i = 0; i < samples; i++)
        {
            int scaled = ScaleSample(buffer[i], gain);
            buffer[i] = (short)scaled;
        }
    }

    private sealed class SincResampler
    {
        private const int DefaultTaps = 32;
        private readonly int _taps;
        private readonly int _half;
        private readonly double _inRate;
        private readonly double _outRate;
        private readonly double _step;
        private readonly double _cutoff;
        private readonly double[] _window;
        private double _phase;

        public SincResampler(double inRate, double outRate, int taps = DefaultTaps)
        {
            _taps = taps < 8 ? 8 : (taps % 2 == 0 ? taps : taps + 1);
            _half = _taps / 2;
            _inRate = inRate;
            _outRate = outRate;
            _step = _inRate / _outRate;
            _cutoff = 0.5 * Math.Min(1.0, _outRate / _inRate);
            _window = BuildBlackmanWindow(_taps);
        }

        public short[] Resample(short[] input, int outFrames)
        {
            if (outFrames <= 0)
                return Array.Empty<short>();
            if (input.Length == 0)
                return new short[outFrames * 2];

            int inFrames = input.Length / 2;
            if (inFrames <= 0)
                return new short[outFrames * 2];

            short[] output = new short[outFrames * 2];
            double phase = _phase;

            for (int i = 0; i < outFrames; i++)
            {
                int center = (int)Math.Floor(phase);
                double frac = phase - center;

                double sumL = 0.0;
                double sumR = 0.0;
                double norm = 0.0;

                for (int t = 0; t < _taps; t++)
                {
                    int k = t - _half + 1;
                    int src = center + k;
                    if (src < 0) src = 0;
                    else if (src >= inFrames) src = inFrames - 1;

                    double x = k - frac;
                    double coeff = Sinc(2.0 * _cutoff * x) * _window[t] * (2.0 * _cutoff);
                    int idx = src * 2;
                    sumL += input[idx] * coeff;
                    sumR += input[idx + 1] * coeff;
                    norm += coeff;
                }

                if (norm != 0.0)
                {
                    sumL /= norm;
                    sumR /= norm;
                }

                int outIndex = i * 2;
                output[outIndex] = (short)Math.Clamp((int)Math.Round(sumL), short.MinValue, short.MaxValue);
                output[outIndex + 1] = (short)Math.Clamp((int)Math.Round(sumR), short.MinValue, short.MaxValue);

                phase += _step;
                if (phase >= inFrames)
                    phase -= inFrames;
            }

            if (phase < 0)
                phase = 0;
            _phase = phase;
            return output;
        }

        private static double Sinc(double x)
        {
            if (Math.Abs(x) < 1e-8)
                return 1.0;
            double pix = Math.PI * x;
            return Math.Sin(pix) / pix;
        }

        private static double[] BuildBlackmanWindow(int taps)
        {
            double[] w = new double[taps];
            if (taps == 1)
            {
                w[0] = 1.0;
                return w;
            }
            double denom = taps - 1;
            for (int n = 0; n < taps; n++)
            {
                double a = 2.0 * Math.PI * n / denom;
                w[n] = 0.42 + 0.5 * Math.Cos(a) + 0.08 * Math.Cos(2.0 * a);
            }
            return w;
        }
    }

    private void MixWithPsgYm(short[] mixed, double targetFps)
    {
        var music = EutherDrive.Core.MdTracerCore.md_main.g_md_music;
        if (music == null || mixed.Length == 0)
            return;

        int frames = mixed.Length / 2;
        if (frames <= 0)
            return;

        bool wantPsg = !_psgDisabled;
        bool wantYm = _ymEnabled;
        if (!wantPsg && !wantYm)
            return;

        if (wantPsg)
        {
            int samples = frames * 2;
            if (_psgFrameBuffer.Length < samples)
                _psgFrameBuffer = new short[samples];
            for (int i = 0; i < frames; i++)
            {
                int s = music.PsgUpdateSample();
                if (s > short.MaxValue) s = short.MaxValue;
                else if (s < short.MinValue) s = short.MinValue;
                int idx = i * 2;
                _psgFrameBuffer[idx] = (short)s;
                _psgFrameBuffer[idx + 1] = (short)s;
            }
            if (TraceScdAudioCore)
                TraceCoreAudio("[SCD-AUDIO-PSG]", _psgFrameBuffer, samples);
            ApplyGainInPlace(_psgFrameBuffer, samples, PsgMixGain);
            AddInto(mixed, _psgFrameBuffer, samples);
        }

        if (wantYm)
        {
            short[] ym = GenerateYmBuffer(frames);
            if (TraceScdAudioCore)
                TraceCoreAudio("[SCD-AUDIO-YM]", ym, ym.Length);
            ApplyGainInPlace(ym, ym.Length, YmMixGain);
            AddInto(mixed, ym, ym.Length);
        }
    }

    private short[] GenerateYmBuffer(int frames)
    {
        if (frames <= 0)
            return Array.Empty<short>();
        var music = EutherDrive.Core.MdTracerCore.md_main.g_md_music;
        if (music == null)
            return Array.Empty<short>();

        int outSamples = frames * 2;
        if (_ymFrameBuffer.Length < outSamples)
            _ymFrameBuffer = new short[outSamples];

        double ratio = (YmInternalSampleRate * YmResampleScale) / PsgSampleRate;
        double phase = _ymResamplePhase;
        int neededInternal = (int)Math.Floor(phase + ((frames - 1) * ratio)) + 2;
        if (neededInternal < 2)
            neededInternal = 2;
        int internalSamples = neededInternal * 2;
        if (_ymInternalBuffer.Length < internalSamples)
            _ymInternalBuffer = new short[internalSamples];

        int writeOffsetFrames = 0;
        if (_ymResampleHasCarry)
        {
            _ymInternalBuffer[0] = _ymResampleCarryL;
            _ymInternalBuffer[1] = _ymResampleCarryR;
            writeOffsetFrames = 1;
        }

        int genFrames = neededInternal - writeOffsetFrames;
        if (genFrames > 0)
        {
            var dst = _ymInternalBuffer.AsSpan(writeOffsetFrames * 2, genFrames * 2);
            music.YmUpdateBatch(dst, genFrames);
        }

        for (int i = 0; i < frames; i++)
        {
            double pos = phase + i * ratio;
            int idx0 = (int)Math.Floor(pos);
            if (idx0 < 0) idx0 = 0;
            if (idx0 >= neededInternal) idx0 = neededInternal - 1;
            int idx1 = idx0 + 1;
            if (idx1 >= neededInternal) idx1 = neededInternal - 1;
            double t = pos - idx0;

            int base0 = idx0 * 2;
            int base1 = idx1 * 2;
            int l0 = _ymInternalBuffer[base0];
            int r0 = _ymInternalBuffer[base0 + 1];
            int l1 = _ymInternalBuffer[base1];
            int r1 = _ymInternalBuffer[base1 + 1];

            int l = LinearInterpolate(l0, l1, t);
            int r = LinearInterpolate(r0, r1, t);

            if (FmMixGain != 1.0)
            {
                l = ScaleSample(l, FmMixGain);
                r = ScaleSample(r, FmMixGain);
            }

            int outIndex = i * 2;
            _ymFrameBuffer[outIndex] = (short)l;
            _ymFrameBuffer[outIndex + 1] = (short)r;
        }

        int lastIndex = (neededInternal - 1) * 2;
        _ymResampleCarryL = _ymInternalBuffer[lastIndex];
        _ymResampleCarryR = _ymInternalBuffer[lastIndex + 1];
        _ymResampleHasCarry = true;

        double endPos = phase + frames * ratio;
        _ymResamplePhase = endPos - (neededInternal - 1);
        if (_ymResamplePhase < 0)
            _ymResamplePhase = 0;

        return _ymFrameBuffer;
    }

    private static void AddInto(short[] dst, short[] src, int count)
    {
        int max = Math.Min(dst.Length, count);
        for (int i = 0; i < max; i++)
        {
            int mixed = dst[i] + src[i];
            if (mixed > short.MaxValue) mixed = short.MaxValue;
            else if (mixed < short.MinValue) mixed = short.MinValue;
            dst[i] = (short)mixed;
        }
    }

    private static int LinearInterpolate(int s0, int s1, double t)
    {
        double v = s0 + (s1 - s0) * t;
        if (v > short.MaxValue) return short.MaxValue;
        if (v < short.MinValue) return short.MinValue;
        return (int)Math.Round(v);
    }

    private static int ScaleSample(int sample, double scale)
    {
        double v = sample * scale;
        if (v > short.MaxValue) return short.MaxValue;
        if (v < short.MinValue) return short.MinValue;
        return (int)Math.Round(v);
    }

    private static double GetYmResampleScale()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_YM_RESAMPLE_SCALE");
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            && value > 0)
        {
            return value;
        }
        return 1.0;
    }

    private static double GetFmMixGain()
    {
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_FM_MIX_GAIN");
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            && value > 0)
        {
            return value;
        }
        return 1.0;
    }

    private static double ReadDoubleOr(string key, double fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            && value > 0)
        {
            return value;
        }
        return fallback;
    }

    private short[] MixAudio(short[] cdAudio, short[] pcmAudio, double targetFps)
    {
        int outFrames;
        if (cdAudio.Length >= 2)
        {
            outFrames = cdAudio.Length / 2;
        }
        else
        {
            if (targetFps <= 0)
                targetFps = 60.0;
            outFrames = (int)Math.Round(44100.0 / targetFps);
        }

        short[] pcmResampled = ResamplePcmToOutput(pcmAudio, outFrames);
        int outSamples = outFrames * 2;
        short[] mixed = new short[outSamples];
        if (cdAudio.Length > 0)
        {
            ApplyGainInPlace(cdAudio, cdAudio.Length, CdMixGain);
            _cdLowPass?.Apply(cdAudio, cdAudio.Length);
        }
        if (pcmResampled.Length > 0)
        {
            ApplyGainInPlace(pcmResampled, pcmResampled.Length, PcmMixGain);
            _pcmLowPass?.Apply(pcmResampled, pcmResampled.Length);
        }

        for (int i = 0; i < outSamples; i++)
        {
            int cd = i < cdAudio.Length ? cdAudio[i] : 0;
            int pcm = i < pcmResampled.Length ? pcmResampled[i] : 0;
            int sum = cd + pcm;
            if (sum > short.MaxValue) sum = short.MaxValue;
            if (sum < short.MinValue) sum = short.MinValue;
            mixed[i] = (short)sum;
        }

        return mixed;
    }

    private short[] ResamplePcmToOutput(short[] pcmAudio, int outFrames)
    {
        if (outFrames <= 0)
            return Array.Empty<short>();
        return _pcmResampler.Resample(pcmAudio, outFrames);
    }

    private void TraceSplitAudio(short[] cdAudio, short[] pcmAudio)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - _audioSplitLastTicks < Stopwatch.Frequency)
            return;
        _audioSplitLastTicks = now;

        Console.Error.WriteLine($"[SCD-AUDIO-CD] {DescribeBuffer(cdAudio)}");
        Console.Error.WriteLine($"[SCD-AUDIO-PCM] {DescribeBuffer(pcmAudio)}");
    }

    private static string DescribeBuffer(short[] buffer)
    {
        if (buffer.Length == 0)
            return "samples=0";
        short min = short.MaxValue;
        short max = short.MinValue;
        int nonZero = 0;
        for (int i = 0; i < buffer.Length; i++)
        {
            short s = buffer[i];
            if (s != 0)
                nonZero++;
            if (s < min) min = s;
            if (s > max) max = s;
        }
        return $"samples={buffer.Length} nonzero={nonZero} min={min} max={max}";
    }

    private void TraceCoreAudio(string label, short[] buffer, int length)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - _audioSplitLastTicks < Stopwatch.Frequency)
            return;
        int count = Math.Min(length, buffer.Length);
        if (count <= 0)
        {
            Console.Error.WriteLine($"{label} samples=0");
            return;
        }
        short min = short.MaxValue;
        short max = short.MinValue;
        int nonZero = 0;
        for (int i = 0; i < count; i++)
        {
            short s = buffer[i];
            if (s != 0)
                nonZero++;
            if (s < min) min = s;
            if (s > max) max = s;
        }
        Console.Error.WriteLine($"{label} samples={count} nonzero={nonZero} min={min} max={max}");
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
        if (TraceSubDebug && _subVectorLogRemaining > 0)
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
        if (TraceSubDebug)
            Console.WriteLine($"[SCD-SUB] Initialized sub CPU sp=0x{subSp:X8} pc=0x{subPc:X8}");
        return true;
    }
}
