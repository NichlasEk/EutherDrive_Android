using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using EutherDrive.Core.Cpu.M68000Emu;
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
    private readonly M68000 _mainCpu = M68000.CreateBuilder().Name("SCD-MAIN").Build();
    private readonly M68000 _subCpu = M68000.CreateBuilder().Name("SCD-SUB").Build();
    private IBusInterface? _mainCpuBus;
    private IBusInterface? _subCpuBus;
    private bool _useM68kEmu = UseM68kEmu;
    private bool _subCpuNeedsReset;
    private int _subInitLogRemaining = 8;
    private int _subVectorLogRemaining = 16;
    private int _subPcLogRemaining = 500000;
    private int _subDumpRemaining = 0;
    private int _subChecksumLogRemaining = 16;
    private int _subInitBranchLogRemaining = 64;
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
    private static readonly bool TraceMainWaitLoop =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_MAINWAIT"),
            "1",
            StringComparison.Ordinal);
    private static readonly string? MainWaitTraceFilePath =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_MAINWAIT_FILE");
    private static readonly bool TraceMainDebug =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_MAIN_DEBUG"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceMainRamJump =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_MAIN_RAM_JUMP"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool TraceReset =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_RESET"),
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
    private static readonly bool TraceSubInitBranch =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_SUBINIT_BRANCH"),
            "1",
            StringComparison.Ordinal);
    private static readonly bool ForcePrgChecksum = ReadEnvFlag("EUTHERDRIVE_SCD_FORCE_PRG_CHECKSUM", true);
    private static readonly bool TraceFrameBuffer =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_FRAMEBUFFER"),
            "1",
            StringComparison.Ordinal);
    // Debug overlay of graphics-coprocessor image buffer. Enabled by default to
    // help diagnose if Sub CPU is rendering while Main CPU is stuck.
    private static readonly bool EnableGfxOverlay = ReadEnvFlag("EUTHERDRIVE_SCD_GFX_OVERLAY", true);
    private static readonly bool ProfileScd =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_PROFILE"),
            "1",
            StringComparison.Ordinal);
    private int _mainPcLogRemaining = 32;
    private long _lastMainPcFrame = -1;
    private long _lastMainWaitLogFrame = -1;
    private bool _mainRamJumpLogged;
    private uint _lastMainPc;
    private ushort _lastMainOp;
    private int _mainPcB3bStreak;
    private int _mainPcB3bLogStreakMark;
    private int _mainDecompLogRemaining = 64;
    private long _lastSubWaitFrame = -1;
    private int _lastFbLogW = -1;
    private int _lastFbLogH = -1;
    private int _lastFbLogStride = -1;
    private int _lastFbLogSrcLen = -1;
    private int _frameStatsRemaining = 4;
    private bool _vdpFrameLooksBlank;
    private int _gfxFallbackLogRemaining = 16;
    private long _profileFrames;
    private long _profileTotalTicks;
    private long _profileCpuTicks;
    private long _profileVdpTicks;
    private long _profileGfxTicks;
    private long _profileAudioTicks;
    private static readonly int MainCyclesPerFrame = ReadCyclesPerFrame("EUTHERDRIVE_SCD_MAIN_CYCLES");
    private static readonly int SubCyclesPerFrame = ReadCyclesPerFrame("EUTHERDRIVE_SCD_SUB_CYCLES");
    private static readonly double? TargetFpsOverride = ReadDouble("EUTHERDRIVE_SCD_TARGET_FPS");
    private const double MainClockHz = 7_670_453.0;
    private const double SubClockHz = 12_500_000.0;
    private const double Z80ClockHz = 3_579_545.0;
    private const ulong SegaCdMclkHz = 50_000_000;
    private static readonly ulong GenesisMasterClockHz = (ulong)Math.Round(MainClockHz * 7.0);
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
    private static readonly double ScdCpuCycleScale = ReadDoubleOr("EUTHERDRIVE_SCD_CPU_SCALE", 1.0);
    private static readonly double PcmMixGain = ReadDoubleOr("EUTHERDRIVE_SCD_PCM_GAIN", PcmCoefficient);
    private static readonly double CdMixGain = ReadDoubleOr("EUTHERDRIVE_SCD_CD_GAIN", CdCoefficient);
    private static readonly double PsgMixGain = ReadDoubleOr("EUTHERDRIVE_SCD_PSG_GAIN", PsgCoefficient);
    private static readonly double YmMixGain = ReadDoubleOr("EUTHERDRIVE_SCD_YM_GAIN", 1.0);
    private static readonly bool UseM68kEmu = ReadEnvFlag("EUTHERDRIVE_SCD_USE_M68KEMU", true);
    private readonly bool _psgDisabled =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DISABLE_PSG"), "1", StringComparison.Ordinal);
    private bool _ymEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_YM"), "0", StringComparison.Ordinal);
    private double _mainCycleRemainder;
    private double _subCycleRemainder;
    private double _z80CycleRemainder;
    private ulong _scdMclkCycleProduct;
    private ulong _scdMclkCycles;
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
    private static readonly bool TraceScdTimer =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_TIMER"), "1", StringComparison.Ordinal);
    private long _audioTraceLastTicks;
    private long _audioSplitLastTicks;
    private long _timerTraceLastTicks;
    private static readonly bool TraceCycleBudget =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_TRACE_CYCLES"), "1", StringComparison.Ordinal);
    private static readonly bool DumpBootSector =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_DUMP_BOOT_SECTOR"), "1", StringComparison.Ordinal);
    private static readonly string BootSectorDumpPath =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_DUMP_BOOT_SECTOR_PATH") ?? "/tmp/ed_scd_boot_sector.bin";
    private static readonly string? BootSectorComparePath =
        Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_BOOT_SECTOR_COMPARE");
    private bool _cycleBudgetLogged;
    private BiquadLowPass? _pcmLowPass;
    private BiquadLowPass? _cdLowPass;
    private SincResampler _pcmResampler = new(PcmSampleRate, 44100.0);

    public SegaCdDiscInfo? DiscInfo => _discInfo;
    public ConsoleRegion RegionHint { get; private set; } = ConsoleRegion.Auto;
    public bool EnableRamCartridge { get; set; } = true;
    public bool LoadCdIntoRam { get; set; }
    public bool ForceNoDisc { get; set; }

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
        var disc = CdRom.Open(path, LoadCdIntoRam);
        _memory.SetDisc(disc);
        TryDumpBootSector(disc, path);
        _ymResamplePhase = 0;
        _ymResampleHasCarry = false;
        bool forceNoDisc = ForceNoDisc
            || string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SCD_FORCE_NO_DISC"), "1", StringComparison.Ordinal);
        if (forceNoDisc)
            _memory.SetDisc(null);
        if (_useM68kEmu)
        {
            // Initialized builder above
        }

        if (TraceCycleBudget)
            Console.Error.WriteLine($"[SCD-CYCLES] main={MainCyclesPerFrame} sub={SubCyclesPerFrame} (load)");
        Console.WriteLine($"[SCD-CONFIG] m68kemu={(_useM68kEmu ? 1 : 0)} gfxOverlay={(EnableGfxOverlay ? 1 : 0)}");
        EutherDrive.Core.MdTracerCore.md_main.initialize();
        if (EutherDrive.Core.MdTracerCore.md_main.g_md_io != null)
        {
            EutherDrive.Core.MdTracerCore.md_io.Current = EutherDrive.Core.MdTracerCore.md_main.g_md_io;
            EutherDrive.Core.MdTracerCore.md_main.g_md_io.SetRomRegionHint(RegionHint);
        }
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
        if (EutherDrive.Core.MdTracerCore.md_main.g_md_music != null)
            EutherDrive.Core.MdTracerCore.md_main.g_md_music.reset();

        uint initialSp = ReadMainVector(_memory, 0);
        uint initialPc = ReadMainVector(_memory, 4);
        if (TraceReset)
        {
            uint busError = ReadMainVector(_memory, 8);
            uint addrError = ReadMainVector(_memory, 12);
            uint illegal = ReadMainVector(_memory, 16);
            Console.WriteLine(
                $"[SCD-RESET] vectors: SSP=0x{initialSp:X8} PC=0x{initialPc:X8} " +
                $"BUSERR=0x{busError:X8} ADDRERR=0x{addrError:X8} ILL=0x{illegal:X8}");
        }
        if (_useM68kEmu && _mainBus != null)
        {
            _mainCpuBus = new SegaCdMainM68kBus(_mainBus, _memory);
            _subCpuBus = new SegaCdSubM68kBus(_memory);
            _mainCpu.Reset(_mainCpuBus);
            _subCpuNeedsReset = true;
            if (TraceReset)
                Console.WriteLine($"[SCD-RESET] main CPU: SSP=0x{_mainCpu.Ssp:X8} PC=0x{_mainCpu.Pc:X8} op=0x{_mainCpu.NextOpcode:X4}");
            _mainContext = null;
            _subContext = null;
            _memory.MainPcProvider = () => _mainCpu.Pc;
            _memory.SubPcProvider = () => _subCpu.Pc;
        }
        else
        {
            _mainContext = CreateResetContext(initialPc, initialSp);
            _subContext = CreateResetContext(0, 0);
            _memory.MainPcProvider = () => _mainContext.RegPc;
            _memory.SubPcProvider = () => _subContext.RegPc;
        }

        // TODO: Instantiate Sega CD emulator core once ported.
        // For now, just clear framebuffer.
        Array.Clear(_frameBuffer, 0, _frameBuffer.Length);
        _audioBuffer = Array.Empty<short>();
        _pcmLowPass = CreateLowPass("EUTHERDRIVE_SCD_PCM_LP_HZ", 44100, 7973);
        _cdLowPass = CreateLowPass("EUTHERDRIVE_SCD_CD_LP_HZ", 44100, 0);
        _pcmResampler = new SincResampler(PcmSampleRate, 44100.0);
        _scdMclkCycleProduct = 0;
        _scdMclkCycles = 0;
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

    public void DumpCdcRam(string path)
    {
        if (_memory == null)
            return;
        byte[] snapshot = _memory.Cdc.GetBufferRamSnapshot();
        File.WriteAllBytes(path, snapshot);
    }

    public void DumpVdpRegisters(string path)
    {
        var vdp = EutherDrive.Core.MdTracerCore.md_main.g_md_vdp;
        if (vdp == null)
            return;
        byte[] regs = vdp.GetRegisterSnapshot();
        if (regs.Length == 0)
            return;

        var sb = new StringBuilder();

        sb.AppendLine("Register #0");
        sb.AppendLine($"  Horizontal interrupt enabled: {BoolStr(vdp.g_vdp_reg_0_4_hinterrupt != 0)}");
        sb.AppendLine($"  HV counter latched: {BoolStr(vdp.g_vdp_reg_0_1_hvcounter != 0)}");

        byte reg1 = regs.Length > 1 ? regs[1] : (byte)0;
        bool mode4 = (reg1 & 0x04) == 0;
        string vsize = vdp.g_vdp_reg_1_3_cellmode != 0 ? "V30 (240px)" : "V28 (224px)";
        string vramSize = vdp.g_vdp_reg_1_7_vram128 != 0 ? "128 KB" : "64 KB";
        sb.AppendLine("Register #1");
        sb.AppendLine($"  Display enabled: {BoolStr(vdp.g_vdp_reg_1_6_display != 0)}");
        sb.AppendLine($"  Vertical interrupt enabled: {BoolStr(vdp.g_vdp_reg_1_5_vinterrupt != 0)}");
        sb.AppendLine($"  DMA enabled: {BoolStr(vdp.g_vdp_reg_1_4_dma != 0)}");
        sb.AppendLine($"  Vertical resolution: {vsize}");
        sb.AppendLine($"  Mode: {(mode4 ? "4" : "5")}");
        sb.AppendLine($"  VRAM size: {vramSize}");

        sb.AppendLine("Register #2");
        sb.AppendLine($"  Plane A nametable address: ${vdp.g_vdp_reg_2_scrolla:X4}");

        sb.AppendLine("Register #3");
        sb.AppendLine($"  Window nametable address: ${vdp.g_vdp_reg_3_windows:X4}");

        sb.AppendLine("Register #4");
        sb.AppendLine($"  Plane B nametable address: ${vdp.g_vdp_reg_4_scrollb:X4}");

        sb.AppendLine("Register #5");
        sb.AppendLine($"  Sprite attribute table address: ${vdp.g_vdp_reg_5_sprite:X4}");

        byte reg7 = regs.Length > 7 ? regs[7] : vdp.g_vdp_reg_7_backcolor;
        sb.AppendLine("Register #7");
        sb.AppendLine($"  Backdrop palette: {(reg7 >> 4) & 0x03}");
        sb.AppendLine($"  Backdrop color index: {reg7 & 0x0F}");

        sb.AppendLine("Register #10");
        sb.AppendLine($"  Horizontal interrupt interval: {vdp.g_vdp_reg_10_hint}");

        string vscrollMode = vdp.g_vdp_reg_11_2_vscroll != 0 ? "Per 2 cell" : "Full screen";
        string hscrollMode = vdp.g_vdp_reg_11_1_hscroll switch
        {
            0 => "Full screen",
            1 => "Per cell",
            2 => "Per line",
            _ => "Prohibited"
        };
        sb.AppendLine("Register #11");
        sb.AppendLine($"  Vertical scroll mode: {vscrollMode}");
        sb.AppendLine($"  Horizontal scroll mode: {hscrollMode}");

        string hsize = (vdp.g_vdp_reg_12_7_cellmode1 != 0 || vdp.g_vdp_reg_12_0_cellmode2 != 0)
            ? "H40 (320px)"
            : "H32 (256px)";
        string interlace = vdp.g_vdp_interlace_mode switch
        {
            0 => "Progressive",
            1 => "Single-screen interlaced",
            2 => "Double-screen interlaced",
            _ => "Progressive"
        };
        sb.AppendLine("Register #12");
        sb.AppendLine($"  Horizontal resolution: {hsize}");
        sb.AppendLine($"  Shadow/highlight enabled: {BoolStr(vdp.g_vdp_reg_12_3_shadow != 0)}");
        sb.AppendLine($"  Screen mode: {interlace}");

        sb.AppendLine("Register #13");
        sb.AppendLine($"  H scroll table address: ${vdp.g_vdp_reg_13_hscroll:X4}");

        sb.AppendLine("Register #15");
        sb.AppendLine($"  Data port auto-increment: 0x{vdp.g_vdp_reg_15_autoinc:X}");

        string vplane = ScrollSizeToString(vdp.g_vdp_reg_16_5_scrollV);
        string hplane = ScrollSizeToString(vdp.g_vdp_reg_16_1_scrollH);
        sb.AppendLine("Register #16");
        sb.AppendLine($"  Vertical plane size: {vplane}");
        sb.AppendLine($"  Horizontal plane size: {hplane}");

        byte reg17 = regs.Length > 17 ? regs[17] : (byte)0;
        string winHMode = (reg17 & 0x80) != 0 ? "Center to right" : "Left to center";
        sb.AppendLine("Register #17");
        sb.AppendLine($"  Window horizontal mode: {winHMode}");
        sb.AppendLine($"  Window X: {reg17 & 0x1F}");

        byte reg18 = regs.Length > 18 ? regs[18] : (byte)0;
        string winVMode = (reg18 & 0x80) != 0 ? "Center to bottom" : "Top to center";
        sb.AppendLine("Register #18");
        sb.AppendLine($"  Window vertical mode: {winVMode}");
        sb.AppendLine($"  Window Y: {reg18 & 0x1F}");

        ushort dmaLen = (ushort)((vdp.g_vdp_reg_20_dma_counter_high << 8) | vdp.g_vdp_reg_19_dma_counter_low);
        sb.AppendLine("Registers #19-20");
        sb.AppendLine($"  DMA length: {dmaLen}");

        uint dmaSource = (uint)(vdp.g_vdp_reg_21_dma_source_low
            | (vdp.g_vdp_reg_22_dma_source_mid << 8)
            | ((vdp.g_vdp_reg_23_5_dma_high & 0x7F) << 16));
        string dmaMode = vdp.g_vdp_reg_23_dma_mode switch
        {
            2 => "VRAM fill",
            3 => "VRAM-to-VRAM copy",
            _ => "Memory to VRAM"
        };
        sb.AppendLine("Registers #21-23");
        sb.AppendLine($"  DMA source address: ${dmaSource:X6}");
        sb.AppendLine($"  DMA mode: {dmaMode}");

        File.WriteAllText(path, sb.ToString());
    }

    public void DumpScdRegisters(string path)
    {
        if (_memory == null)
            return;
        var r = _memory.Registers;
        var sb = new StringBuilder();
        sb.AppendLine($"SubCpuBusReq: {r.SubCpuBusReq}");
        sb.AppendLine($"SubCpuReset: {r.SubCpuReset}");
        sb.AppendLine($"MainSoftwareInterruptPending: {r.MainSoftwareInterruptPending}");
        sb.AppendLine($"SubSoftwareInterruptPending: {r.SubSoftwareInterruptPending}");
        sb.AppendLine($"SoftwareInterruptEnabled: {r.SoftwareInterruptEnabled}");
        sb.AppendLine($"LedGreen: {r.LedGreen}");
        sb.AppendLine($"LedRed: {r.LedRed}");
        sb.AppendLine($"PrgRamWriteProtect: 0x{r.PrgRamWriteProtect:X2}");
        sb.AppendLine($"PrgRamBank: 0x{r.PrgRamBank:X2}");
        sb.AppendLine($"HInterruptVector: 0x{r.HInterruptVector:X4}");
        sb.AppendLine($"StopwatchCounter: 0x{r.StopwatchCounter:X3}");
        sb.AppendLine($"SubCpuCommunicationFlags: 0x{r.SubCpuCommunicationFlags:X2}");
        sb.AppendLine($"MainCpuCommunicationFlags: 0x{r.MainCpuCommunicationFlags:X2}");
        for (int i = 0; i < r.CommunicationCommands.Length; i++)
            sb.AppendLine($"CommunicationCommand[{i}]: 0x{r.CommunicationCommands[i]:X4}");
        for (int i = 0; i < r.CommunicationStatuses.Length; i++)
            sb.AppendLine($"CommunicationStatus[{i}]: 0x{r.CommunicationStatuses[i]:X4}");
        sb.AppendLine($"TimerCounter: 0x{r.TimerCounter:X2}");
        sb.AppendLine($"TimerInterval: 0x{r.TimerInterval:X2}");
        sb.AppendLine($"TimerInterruptPending: {r.TimerInterruptPending}");
        sb.AppendLine($"SubcodeInterruptEnabled: {r.SubcodeInterruptEnabled}");
        sb.AppendLine($"CdcInterruptEnabled: {r.CdcInterruptEnabled}");
        sb.AppendLine($"CddInterruptEnabled: {r.CddInterruptEnabled}");
        sb.AppendLine($"TimerInterruptEnabled: {r.TimerInterruptEnabled}");
        sb.AppendLine($"GraphicsInterruptEnabled: {r.GraphicsInterruptEnabled}");
        sb.AppendLine($"CddHostClockOn: {r.CddHostClockOn}");
        for (int i = 0; i < r.CddCommand.Length; i++)
            sb.AppendLine($"CddCommand[{i}]: 0x{r.CddCommand[i]:X2}");
        File.WriteAllText(path, sb.ToString());
    }

    private static string BoolStr(bool value) => value ? "true" : "false";

    private static void SyncLegacyMain68kView(M68000 cpu)
    {
        var state = cpu.GetState();
        ushort sr = state.Sr;
        ushort op = state.Prefetch;

        EutherDrive.Core.MdTracerCore.md_m68k.g_reg_PC = state.Pc & 0x00FF_FFFF;
        EutherDrive.Core.MdTracerCore.md_m68k.g_opcode = op;
        EutherDrive.Core.MdTracerCore.md_m68k.g_op = (byte)(op >> 12);
        EutherDrive.Core.MdTracerCore.md_m68k.g_op1 = (byte)((op >> 9) & 0x07);
        EutherDrive.Core.MdTracerCore.md_m68k.g_op2 = (byte)((op >> 6) & 0x07);
        EutherDrive.Core.MdTracerCore.md_m68k.g_op3 = (byte)((op >> 3) & 0x07);
        EutherDrive.Core.MdTracerCore.md_m68k.g_op4 = (byte)(op & 0x07);

        for (int i = 0; i < 8; i++)
            EutherDrive.Core.MdTracerCore.md_m68k.g_reg_data[i].l = state.Data[i];
        for (int i = 0; i < 7; i++)
            EutherDrive.Core.MdTracerCore.md_m68k.g_reg_addr[i].l = state.Address[i];

        bool supervisor = (sr & 0x2000) != 0;
        uint a7 = supervisor ? state.Ssp : state.Usp;
        EutherDrive.Core.MdTracerCore.md_m68k.g_reg_addr[7].l = a7;
        EutherDrive.Core.MdTracerCore.md_m68k.g_reg_addr_usp.l = state.Usp;

        EutherDrive.Core.MdTracerCore.md_m68k.g_status_T = (sr & 0x8000) != 0;
        EutherDrive.Core.MdTracerCore.md_m68k.g_status_S = supervisor;
        EutherDrive.Core.MdTracerCore.md_m68k.g_status_interrupt_mask = (byte)((sr >> 8) & 0x07);
        EutherDrive.Core.MdTracerCore.md_m68k.g_status_X = (sr & 0x0010) != 0;
        EutherDrive.Core.MdTracerCore.md_m68k.g_status_N = (sr & 0x0008) != 0;
        EutherDrive.Core.MdTracerCore.md_m68k.g_status_Z = (sr & 0x0004) != 0;
        EutherDrive.Core.MdTracerCore.md_m68k.g_status_V = (sr & 0x0002) != 0;
        EutherDrive.Core.MdTracerCore.md_m68k.g_status_C = (sr & 0x0001) != 0;
    }

    private static string ScrollSizeToString(int bits)
    {
        return (bits & 0x03) switch
        {
            0x00 => "32 tiles",
            0x01 => "64 tiles",
            0x02 => "Prohibited",
            0x03 => "128 tiles",
            _ => "32 tiles"
        };
    }

    public void RunFrame()
    {
        if (_memory == null || _mainBus == null)
            return;

        long frameCount = EutherDrive.Core.MdTracerCore.md_main.g_md_vdp?.FrameCounter ?? -1;
        if (_useM68kEmu && frameCount > 180 && frameCount < 200)
        {
            if (_mainCpu.Pc == 0x00132C && _subCpu.Pc == 0x0003DA)
            {
                if (!_memory.Registers.SubSoftwareInterruptPending)
                {
                    Console.WriteLine("[COMM-DEADLOCK] Breaking deadlock by forcing Sub INT2");
                    _memory.Registers.SubSoftwareInterruptPending = true;
                }
            }
        }

        if (_useM68kEmu)
        {
            if (_mainCpuBus == null || _subCpuBus == null)
                return;
        }
        else
        {
            if (_mainContext == null || _subContext == null || _subBus == null)
                return;
        }

        long frameStart = ProfileScd ? Stopwatch.GetTimestamp() : 0;

        // Derive cycle budgets from clock rates unless overridden via env.
        int mainCycles = MainCyclesPerFrame;
        int subCycles = SubCyclesPerFrame;
        int z80Cycles = 0;
        int baseMainCycles = mainCycles;
        int baseSubCycles = subCycles;
        int baseZ80Cycles = z80Cycles;

        var vdp = EutherDrive.Core.MdTracerCore.md_main.g_md_vdp;
        if (vdp != null)
        {
            if (!_useM68kEmu)
                EutherDrive.Core.MdTracerCore.md_m68k.ApplyContext(_mainContext!);
            int lines = vdp.g_vertical_line_max > 0 ? vdp.g_vertical_line_max : 262;
            double targetFps = TargetFpsOverride
                ?? (lines >= 312 ? 50.0 : 59.922);
            _lastTargetFps = targetFps;
            long frameCounter = vdp?.FrameCounter ?? -1;
            EutherDrive.Core.MdTracerCore.md_main.g_md_bus?.TickZ80SafeBoot(frameCounter);
            bool allowZ80 = EutherDrive.Core.MdTracerCore.md_main.ShouldRunZ80(frameCounter);
            if (mainCycles <= 0)
                mainCycles = ComputeCyclesPerFrame(MainClockHz, targetFps, ref _mainCycleRemainder);
            if (subCycles <= 0)
                subCycles = ComputeCyclesPerFrame(SubClockHz, targetFps, ref _subCycleRemainder);
            z80Cycles = ComputeCyclesPerFrame(Z80ClockHz, targetFps, ref _z80CycleRemainder);

            baseMainCycles = mainCycles;
            baseSubCycles = subCycles;
            baseZ80Cycles = z80Cycles;

            if (ScdCpuCycleScale != 1.0)
            {
                mainCycles = ScaleCycleCount(mainCycles, ScdCpuCycleScale);
                subCycles = ScaleCycleCount(subCycles, ScdCpuCycleScale);
                z80Cycles = ScaleCycleCount(z80Cycles, ScdCpuCycleScale);
            }

            if (TraceCycleBudget && !_cycleBudgetLogged)
            {
                _cycleBudgetLogged = true;
                Console.Error.WriteLine(
                    $"[SCD-CYCLES] main={mainCycles} sub={subCycles} z80={z80Cycles} " +
                    $"baseMain={baseMainCycles} baseSub={baseSubCycles} scale={ScdCpuCycleScale:0.###} " +
                    $"lines={lines} fps={targetFps:0.###}");
            }
            int mainPerLine = mainCycles / lines;
            int mainRemainder = mainCycles % lines;
            int baseMainPerLine = baseMainCycles / lines;
            int baseMainRemainder = baseMainCycles % lines;
            int z80PerLine = z80Cycles / lines;
            int z80Remainder = z80Cycles % lines;

            for (int line = 0; line < lines; line++)
            {
                // Reset legacy 68k view's slice info for VDP timing calculations.
                // The VDP uses clock_now as cycles into the current scanline.
                EutherDrive.Core.MdTracerCore.md_m68k.g_clock_now = 0;
                EutherDrive.Core.MdTracerCore.md_m68k.g_slice_start_clock_total = 0;

                int mainSlice = mainPerLine + (line < mainRemainder ? 1 : 0);
                EutherDrive.Core.MdTracerCore.md_m68k.g_slice_clock_len = mainSlice;
                int z80Slice = z80PerLine + (line < z80Remainder ? 1 : 0);
                int baseMainSlice = baseMainPerLine + (line < baseMainRemainder ? 1 : 0);

                ulong genesisMclkElapsed = (ulong)baseMainSlice * 7u;
                _scdMclkCycleProduct += genesisMclkElapsed * SegaCdMclkHz;
                ulong scdMclkElapsed = _scdMclkCycleProduct / GenesisMasterClockHz;
                _scdMclkCycleProduct -= scdMclkElapsed * GenesisMasterClockHz;

                ulong prevScdMclkCycles = _scdMclkCycles;
                _scdMclkCycles += scdMclkElapsed;
                uint baseSubSlice = (uint)(_scdMclkCycles / 4u - prevScdMclkCycles / 4u);

                if (scdMclkElapsed > 0)
                    _memory.Tick((uint)scdMclkElapsed);
                if (baseSubSlice > 0)
                    _memory.Pcm.Tick(baseSubSlice);

                int subSlice = (int)baseSubSlice;
                if (ScdCpuCycleScale != 1.0)
                    subSlice = ScaleCycleCount(subSlice, ScdCpuCycleScale);

                EutherDrive.Core.MdTracerCore.md_main.g_md_bus = _mainBus;
                if (_useM68kEmu)
                    SyncLegacyMain68kView(_mainCpu);

                if (ProfileScd) _profileVdpTicks -= Stopwatch.GetTimestamp();
                vdp.run(line);
                if (ProfileScd) _profileVdpTicks += Stopwatch.GetTimestamp();

                if (!_useM68kEmu)
                {
                    // Capture VDP-driven interrupt requests into the main CPU context before executing.
                    _mainContext!.InterruptVReq = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_V_req;
                    _mainContext.InterruptHReq = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_H_req;
                    _mainContext.InterruptExtReq = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_req;
                    _mainContext.InterruptVAct = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_V_act;
                    _mainContext.InterruptHAct = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_H_act;
                    _mainContext.InterruptExtAct = EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_act;
                }

                if (TraceMainPcFrame)
                {
                    if (frameCounter != _lastMainPcFrame)
                    {
                        _lastMainPcFrame = frameCounter;
                        if (_useM68kEmu)
                        {
                            Console.WriteLine($"[SCD-MAIN-FRAME] frame={frameCounter} pc=0x{_mainCpu.Pc:X6} op=0x{_mainCpu.NextOpcode:X4}");
                        }
                        else
                        {
                            Console.WriteLine($"[SCD-MAIN-FRAME] frame={frameCounter} pc=0x{_mainContext!.RegPc:X6} sp=0x{_mainContext.RegAddr[7]:X8} op=0x{_mainContext.Opcode:X4}");
                        }
                    }
                }
                if (TraceMainWaitLoop && frameCounter != _lastMainWaitLogFrame && (frameCounter % 60) == 0)
                {
                    _lastMainWaitLogFrame = frameCounter;
                    uint pc = _useM68kEmu ? _mainCpu.Pc : _mainContext!.RegPc;
                    ushort op = _useM68kEmu ? _mainCpu.NextOpcode : _mainContext!.Opcode;
                    ushort sr = _useM68kEmu ? _mainCpu.GetState().Sr : _mainContext!.RegSr;
                    int imask = (sr >> 8) & 0x07;
                    ushort ext = _mainCpuBus?.ReadWord(0x000A1C) ?? 0;
                    uint probeAddrRaw = ext;
                    uint probeAddrSe = unchecked((uint)(int)(short)ext);
                    byte probeRaw8 = _mainCpuBus?.ReadByte(probeAddrRaw) ?? 0;
                    ushort probeRaw16 = _mainCpuBus?.ReadWord(probeAddrRaw) ?? 0;
                    byte probeSe8 = _mainCpuBus?.ReadByte(probeAddrSe) ?? 0;
                    ushort probeSe16 = _mainCpuBus?.ReadWord(probeAddrSe) ?? 0;
                    byte gate00 = _mainCpuBus?.ReadByte(0x00A12000) ?? 0;
                    byte gate01 = _mainCpuBus?.ReadByte(0x00A12001) ?? 0;
                    byte gate02 = _mainCpuBus?.ReadByte(0x00A12002) ?? 0;
                    byte gate03 = _mainCpuBus?.ReadByte(0x00A12003) ?? 0;
                    byte gate0E = _mainCpuBus?.ReadByte(0x00A1200E) ?? 0;
                    uint vec2 = _mainCpuBus?.ReadLong(0x00000068) ?? 0;
                    uint vec4 = _mainCpuBus?.ReadLong(0x00000070) ?? 0;
                    uint vec6 = _mainCpuBus?.ReadLong(0x00000078) ?? 0;
                    ushort vec2Op = _mainCpuBus?.ReadWord(vec2) ?? 0;
                    ushort vec4Op = _mainCpuBus?.ReadWord(vec4) ?? 0;
                    ushort vec6Op = _mainCpuBus?.ReadWord(vec6) ?? 0;
                    byte mainIrq = _mainCpuBus?.InterruptLevel() ?? 0;
                    byte subIrq = _memory.GetSubInterruptLevel();
                    bool mainSwPending = _memory.Registers.MainSoftwareInterruptPending;
                    bool subSwPending = _memory.Registers.SubSoftwareInterruptPending;
                    bool cddPending = _memory.Cdd.InterruptPending;
                    bool cdcPending = _memory.Cdc.InterruptPending;
                    bool hintEnabled = EutherDrive.Core.MdTracerCore.md_main.g_md_vdp?.g_vdp_reg_0_4_hinterrupt == 1;
                    bool vintEnabled = EutherDrive.Core.MdTracerCore.md_main.g_md_vdp?.g_vdp_reg_1_5_vinterrupt == 1;
                    Console.WriteLine(
                        $"[SCD-MAIN-WAIT] frame={frameCounter} pc=0x{pc:X6} op=0x{op:X4} " +
                        $"sr=0x{sr:X4} imask={imask} " +
                        $"tstExt=0x{ext:X4} raw=0x{probeAddrRaw:X6} b=0x{probeRaw8:X2} w=0x{probeRaw16:X4} " +
                        $"se=0x{probeAddrSe:X8} b=0x{probeSe8:X2} w=0x{probeSe16:X4} " +
                        $"A12000=0x{gate00:X2} A12001=0x{gate01:X2} A12002=0x{gate02:X2} A12003=0x{gate03:X2} A1200E=0x{gate0E:X2} " +
                        $"V2=0x{vec2:X8}/0x{vec2Op:X4} V4=0x{vec4:X8}/0x{vec4Op:X4} V6=0x{vec6:X8}/0x{vec6Op:X4} " +
                        $"IRQ[m={mainIrq},sub={subIrq},swM={(mainSwPending ? 1 : 0)},swS={(subSwPending ? 1 : 0)},h={(md_m68k.g_interrupt_H_req ? 1 : 0)},v={(md_m68k.g_interrupt_V_req ? 1 : 0)},ext={(md_m68k.g_interrupt_EXT_req ? 1 : 0)},he={(hintEnabled ? 1 : 0)},ve={(vintEnabled ? 1 : 0)},cdd={(cddPending ? 1 : 0)},cdc={(cdcPending ? 1 : 0)}]");
                    if (!string.IsNullOrWhiteSpace(MainWaitTraceFilePath))
                    {
                        string lineOut =
                            $"[SCD-MAIN-WAIT] frame={frameCounter} pc=0x{pc:X6} op=0x{op:X4} " +
                            $"sr=0x{sr:X4} imask={imask} " +
                            $"tstExt=0x{ext:X4} raw=0x{probeAddrRaw:X6} b=0x{probeRaw8:X2} w=0x{probeRaw16:X4} " +
                            $"se=0x{probeAddrSe:X8} b=0x{probeSe8:X2} w=0x{probeSe16:X4} " +
                            $"A12000=0x{gate00:X2} A12001=0x{gate01:X2} A12002=0x{gate02:X2} A12003=0x{gate03:X2} A1200E=0x{gate0E:X2} " +
                            $"V2=0x{vec2:X8}/0x{vec2Op:X4} V4=0x{vec4:X8}/0x{vec4Op:X4} V6=0x{vec6:X8}/0x{vec6Op:X4} " +
                            $"IRQ[m={mainIrq},sub={subIrq},swM={(mainSwPending ? 1 : 0)},swS={(subSwPending ? 1 : 0)},h={(md_m68k.g_interrupt_H_req ? 1 : 0)},v={(md_m68k.g_interrupt_V_req ? 1 : 0)},ext={(md_m68k.g_interrupt_EXT_req ? 1 : 0)},he={(hintEnabled ? 1 : 0)},ve={(vintEnabled ? 1 : 0)},cdd={(cddPending ? 1 : 0)},cdc={(cdcPending ? 1 : 0)}]";
                        try
                        {
                            File.AppendAllText(MainWaitTraceFilePath!, lineOut + Environment.NewLine);
                        }
                        catch
                        {
                            // Ignore trace write issues to avoid impacting emulation.
                        }
                    }
                }

                // Tight interleave for handshakes
                int mRem = mainSlice;
                int sRem = subSlice;
                while (mRem > 0 || sRem > 0)
                {
                    // Run a small chunk of Main CPU
                    if (mRem > 0 && _useM68kEmu)
                    {
                        if (_memory.Registers.MainSoftwareInterruptPending)
                        {
                            EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_req = true;
                            EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_level = 2;
                            EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_vector = 0x68;
                            EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_ack = (l) => { EutherDrive.Core.MdTracerCore.md_m68k.g_interrupt_EXT_req = false; };
                        }
                        int chunk = Math.Min(mRem, 100);
                        while (chunk > 0)
                        {
                            SyncLegacyMain68kView(_mainCpu);
                            uint c = _mainCpu.ExecuteInstruction(_mainCpuBus!);
                            chunk -= (int)c;
                            mRem -= (int)c;
                            mainCyclesConsumed += c;
                        }
                    }
                    // Run a small chunk of Sub CPU
                    if (sRem > 0 && _useM68kEmu && !_memory.SubCpuReset && !_memory.SubCpuHalt && EnsureSubVectorsReady(_memory))
                    {
                        if (_subCpuNeedsReset) { _subCpu.Reset(_subCpuBus!); _subCpuNeedsReset = false; }
                        int chunk = Math.Min(sRem, 100);
                        while (chunk > 0)
                        {
                            _memory.FlushBufferedSubWrites();
                            uint c = _subCpu.ExecuteInstruction(_subCpuBus!);
                            chunk -= (int)c;
                            sRem -= (int)c;
                        }
                    }
                    if (!_useM68kEmu) break; // Fallback to original logic if not in emu mode (though not implemented here)
                    if (mRem <= 0 && sRem <= 0) break;
                }
                if (mainCyclesConsumed > 0) EutherDrive.Core.MdTracerCore.md_main.AdvanceSystemCycles(mainCyclesConsumed);
