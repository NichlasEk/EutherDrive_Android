using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    private bool _subPhase1f6eLogged;
    private bool _subPhase70b2Logged;
    private bool _subPhase7130Logged;
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
        bool loggedSubWaitOpcodeWindow = false;

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

                if (mainSlice > 0)
                {
                    if (ProfileScd) _profileCpuTicks -= Stopwatch.GetTimestamp();
                    long mainCyclesConsumed = 0;
                    if (_useM68kEmu)
                    {
                        if (_memory.Registers.MainSoftwareInterruptPending)
                        {
                            md_m68k.g_interrupt_EXT_req = true;
                            md_m68k.g_interrupt_EXT_level = 2;
                            md_m68k.g_interrupt_EXT_vector = 0x68;
                            md_m68k.g_interrupt_EXT_ack = (level) => { /* no-op or handled via register write */ };
                        }

                        int remaining = mainSlice;
                        while (remaining > 0)
                        {
                            SyncLegacyMain68kView(_mainCpu);

                            int dmaWait = vdp.dma_status_update();
                            if (dmaWait > 0)
                            {
                                int waitStep = Math.Min(remaining, dmaWait);
                                remaining -= waitStep;
                                EutherDrive.Core.MdTracerCore.md_m68k.g_clock_now += (ushort)waitStep;
                                mainCyclesConsumed += waitStep;
                                continue;
                            }

                            if (TraceMainRamJump && !_mainRamJumpLogged)
                            {
                                uint pc = _mainCpu.Pc;
                                if (pc >= 0xFF0000)
                                {
                                    _mainRamJumpLogged = true;
                                    Console.WriteLine(
                                        $"[SCD-MAIN-RAMJUMP] pc=0x{pc:X6} op=0x{_mainCpu.NextOpcode:X4} " +
                                        $"prev_pc=0x{_lastMainPc:X6} prev_op=0x{_lastMainOp:X4}");
                                }
                                _lastMainPc = pc;
                                _lastMainOp = _mainCpu.NextOpcode;
                            }
                            if (_mainCpu.Pc == 0x000B3B)
                            {
                                _mainPcB3bStreak++;
                                if (_mainPcB3bStreak >= 1024 && (_mainPcB3bStreak - _mainPcB3bLogStreakMark) >= 1024)
                                {
                                    _mainPcB3bLogStreakMark = _mainPcB3bStreak;
                                    long frame = vdp?.FrameCounter ?? -1;
                                    byte mainIrq = _mainCpuBus!.InterruptLevel();
                                    byte subIrq = _memory.GetSubInterruptLevel();
                                    byte commMain = _memory.Registers.MainCpuCommunicationFlags;
                                    byte commSub = _memory.Registers.SubCpuCommunicationFlags;
                                    Console.Error.WriteLine(
                                        $"[SCD-STALL-B3B] frame={frame} line={line} streak={_mainPcB3bStreak} " +
                                        $"main_pc=0x{_mainCpu.Pc:X6} main_op=0x{_mainCpu.NextOpcode:X4} " +
                                        $"sub_pc=0x{_subCpu.Pc:X6} sub_op=0x{_subCpu.NextOpcode:X4} " +
                                        $"busreq={(_memory.Registers.SubCpuBusReq ? 1 : 0)} reset={(_memory.Registers.SubCpuReset ? 1 : 0)} halt={(_memory.SubCpuHalt ? 1 : 0)} " +
                                        $"main_irq={mainIrq} sub_irq={subIrq} " +
                                        $"cdc_int={( _memory.Cdc.InterruptPending ? 1 : 0)} cdd_int={(_memory.Cdd.InterruptPending ? 1 : 0)} " +
                                        $"cdc_en={(_memory.Registers.CdcInterruptEnabled ? 1 : 0)} cdd_en={(_memory.Registers.CddInterruptEnabled ? 1 : 0)} " +
                                        $"comm[m=0x{commMain:X2},s=0x{commSub:X2}]");
                                }
                            }
                            else
                            {
                                _mainPcB3bStreak = 0;
                                _mainPcB3bLogStreakMark = 0;
                            }

                            if (_mainCpu.Pc == 0x00132C && frameCounter % 60 == 0)
                            {
                                var state = _mainCpu.GetState();
                                uint w1 = _mainCpuBus!.ReadWord(_mainCpu.Pc + 2);
                                uint w2 = _mainCpuBus!.ReadWord(_mainCpu.Pc + 4);
                                uint w3 = _mainCpuBus!.ReadWord(_mainCpu.Pc + 6);
                                Console.WriteLine($"[MAIN-STUCK] PC=132C OP={_mainCpu.NextOpcode:X4} w1={w1:X4} w2={w2:X4} w3={w3:X4} D0={state.Data[0]:X8} SR={state.Sr:X4}");
                            }

                            uint cycles = _mainCpu.ExecuteInstruction(_mainCpuBus!);
                            remaining -= (int)cycles;
                            EutherDrive.Core.MdTracerCore.md_m68k.g_clock_now += (ushort)cycles;
                            mainCyclesConsumed += cycles;

                        }

                        SyncLegacyMain68kView(_mainCpu);
                    }
                    else
                    {
                        _cpuRunner.RunSome(_mainContext!, mainSlice);
                        mainCyclesConsumed += mainSlice;
                    }

                    if (mainCyclesConsumed > 0)
                    {
                        // Keep MD audio/VDP scheduling timebase aligned with executed main-CPU cycles.
                        EutherDrive.Core.MdTracerCore.md_main.AdvanceSystemCycles(mainCyclesConsumed);
                    }
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
                    if (_useM68kEmu)
                    {
                        Console.WriteLine($"[SCD-MAIN] pc=0x{_mainCpu.Pc:X6} op=0x{_mainCpu.NextOpcode:X4}");
                    }
                    else
                    {
                        Console.WriteLine($"[SCD-MAIN] pc=0x{_mainContext!.RegPc:X6} sp=0x{_mainContext.RegAddr[7]:X8} op=0x{_mainContext.Opcode:X4}");
                    }
                }

                if (!_useM68kEmu && TraceMainDebug && _mainDecompLogRemaining > 0 && _mainContext!.RegPc >= 0x00090E && _mainContext.RegPc <= 0x00098C)
                {
                    _mainDecompLogRemaining--;
                    Console.WriteLine(
                        $"[SCD-MAIN-DECOMP] pc=0x{_mainContext.RegPc:X6} op=0x{_mainContext.Opcode:X4} " +
                        $"A0=0x{_mainContext.RegAddr[0]:X8} A1=0x{_mainContext.RegAddr[1]:X8} " +
                        $"D0=0x{_mainContext.RegData[0]:X8} D1=0x{_mainContext.RegData[1]:X8} D2=0x{_mainContext.RegData[2]:X8} D3=0x{_mainContext.RegData[3]:X8}");
                }

                bool subReset = _memory.SubCpuReset;
                if (_useM68kEmu)
                {
                    if (subReset && !_lastSubReset)
                        _subCpuNeedsReset = true;
                    _lastSubReset = subReset;

                    if (!_memory.SubCpuHalt && !_memory.SubCpuReset)
                    {
                        if (!EnsureSubVectorsReady(_memory))
                        {
                            if (TraceSubDebug && _subInitLogRemaining > 0)
                            {
                                _subInitLogRemaining--;
                                Console.WriteLine("[SCD-SUB] Skipping sub CPU: reset vectors not initialized yet.");
                            }
                        }
                        else
                        {
                            if (_subCpuNeedsReset)
                            {
                                _subCpu.Reset(_subCpuBus!);
                                _subCpuNeedsReset = false;
                            }

                            if (subSlice > 0)
                            {
                                if (ProfileScd) _profileCpuTicks -= Stopwatch.GetTimestamp();
                                int remaining = subSlice;
                                while (remaining > 0)
                                {
                                    if (_subCpu.Pc == 0x0003D4 && frameCounter % 60 == 0)
                                    {
                                        Console.WriteLine($"[SUB-STUCK] PC=3D4 OP={_subCpu.NextOpcode:X4}");
                                    }
                                    if (_subCpu.Pc == 0x0003DA && frameCounter % 60 == 0)
                                    {
                                        var state = _subCpu.GetState();
                                        uint w1 = _subCpuBus!.ReadWord(0x03D4);
                                        uint w2 = _subCpuBus!.ReadWord(0x03D6);
                                        uint w3 = _subCpuBus!.ReadWord(0x03D8);
                                        Console.WriteLine($"[SUB-STUCK] PC=3DA OP={_subCpu.NextOpcode:X4} w1={w1:X4} w2={w2:X4} w3={w3:X4} D0={state.Data[0]:X8} SR={state.Sr:X4}");
                                    }
                                    if (_subCpu.Pc == 0x0005EE && frameCounter % 60 == 0)
                                    {
                                        var state = _subCpu.GetState();
                                        byte waitFlag = _memory.ReadSubByte(0x0005EA4);
                                        byte subLevel = _memory.GetSubInterruptLevel();
                                        Console.WriteLine(
                                            $"[SUB-WAIT-5EE] pc=0x{_subCpu.Pc:X6} op=0x{_subCpu.NextOpcode:X4} sr=0x{state.Sr:X4} " +
                                            $"mask={(state.Sr >> 8) & 7} irq={subLevel} swPend={(_memory.Registers.SubSoftwareInterruptPending ? 1 : 0)} " +
                                            $"swEn={(_memory.Registers.SoftwareInterruptEnabled ? 1 : 0)} timerPend={(_memory.Registers.TimerInterruptPending ? 1 : 0)} " +
                                            $"timerEn={(_memory.Registers.TimerInterruptEnabled ? 1 : 0)} cddPend={(_memory.Cdd.InterruptPending ? 1 : 0)} " +
                                            $"cddEn={(_memory.Registers.CddInterruptEnabled ? 1 : 0)} hostClk={(_memory.Registers.CddHostClockOn ? 1 : 0)} " +
                                            $"cdcPend={(_memory.Cdc.InterruptPending ? 1 : 0)} cdcEn={(_memory.Registers.CdcInterruptEnabled ? 1 : 0)} " +
                                            $"flag=0x{waitFlag:X2}");
                                    }
                                    if (!loggedSubWaitOpcodeWindow && _subCpu.Pc >= 0x0005E8 && _subCpu.Pc <= 0x0005F0)
                                    {
                                        loggedSubWaitOpcodeWindow = true;
                                        uint baseAddr = 0x0005E8;
                                        Span<byte> bytes = stackalloc byte[16];
                                        for (int i = 0; i < bytes.Length; i++)
                                            bytes[i] = _memory.ReadSubByte(baseAddr + (uint)i);
                                        string hex = BitConverter.ToString(bytes.ToArray()).Replace("-", " ");
                                        Console.WriteLine($"[SCD-SUB-WAIT-BYTES] pc=0x{_subCpu.Pc:X6} op=0x{_subCpu.NextOpcode:X4} mem@0x{baseAddr:X6}={hex}");
                                    }

                                    // Match jgenesis: buffered sub register writes are visible before each instruction.
                                    _memory.FlushBufferedSubWrites();
                                    uint cycles = _subCpu.ExecuteInstruction(_subCpuBus!);
                                    remaining -= (int)cycles;
                                }
                                if (ProfileScd) _profileCpuTicks += Stopwatch.GetTimestamp();
                            }

                            uint subPc = _subCpu.Pc;
                            if (TraceSubInitBranch && _subInitBranchLogRemaining > 0 && subPc >= 0x000240 && subPc <= 0x000278)
                            {
                                _subInitBranchLogRemaining--;
                                var subState = _subCpu.GetState();
                                uint baseAddr = subPc >= 16 ? subPc - 16 : 0;
                                Span<byte> bytes = stackalloc byte[64];
                                for (int i = 0; i < bytes.Length; i++)
                                    bytes[i] = _memory.ReadSubByte(baseAddr + (uint)i);
                                byte reg8001 = _memory.ReadSubByte(0xFFFF8001);
                                byte reg8003 = _memory.ReadSubByte(0xFFFF8003);
                                string hex = BitConverter.ToString(bytes.ToArray()).Replace("-", " ");
                                Console.WriteLine(
                                    $"[SCD-SUB-BRANCH] pc=0x{subPc:X6} op=0x{_subCpu.NextOpcode:X4} sr=0x{subState.Sr:X4} " +
                                    $"D0=0x{subState.Data[0]:X8} D1=0x{subState.Data[1]:X8} D2=0x{subState.Data[2]:X8} " +
                                    $"A0=0x{subState.Address[0]:X8} A1=0x{subState.Address[1]:X8} A7=0x{subState.Ssp:X8} " +
                                    $"R8001=0x{reg8001:X2} R8003=0x{reg8003:X2} bit2={(reg8003 & 0x04) >> 2} " +
                                    $"mem@0x{baseAddr:X6}={hex}");
                            }
                            if (TraceSubDebug && _subPcLogRemaining > 0)
                            {
                                _subPcLogRemaining--;
                                Console.WriteLine($"[SCD-SUB] pc=0x{subPc:X6} op=0x{_subCpu.NextOpcode:X4}");
                            }
                            if (TraceSubDebug && subPc >= 0x0005F2 && subPc <= 0x00060E)
                            {
                                var subState = _subCpu.GetState();
                                byte waitFlag = _memory.ReadSubByte(0x0005EA4);
                                Console.WriteLine(
                                    $"[SCD-SUB-IRQ2] pc=0x{subPc:X6} op=0x{_subCpu.NextOpcode:X4} sr=0x{subState.Sr:X4} " +
                                    $"ssp=0x{subState.Ssp:X8} usp=0x{subState.Usp:X8} a7=0x{subState.Ssp:X8} flag=0x{waitFlag:X2} " +
                                    $"d0=0x{subState.Data[0]:X8} d1=0x{subState.Data[1]:X8}");
                            }
                            if (!_subPhase1f6eLogged && subPc >= 0x001F6E && subPc <= 0x001F72)
                            {
                                _subPhase1f6eLogged = true;
                                var subState = _subCpu.GetState();
                                uint baseAddr = 0x001F60;
                                Span<byte> bytes = stackalloc byte[32];
                                Span<byte> srcBytes = stackalloc byte[32];
                                Span<byte> dstBytes = stackalloc byte[32];
                                Span<ushort> statuses = stackalloc ushort[8];
                                Span<ushort> commands = stackalloc ushort[8];
                                for (int i = 0; i < bytes.Length; i++)
                                    bytes[i] = _memory.ReadSubByte(baseAddr + (uint)i);
                                for (int i = 0; i < srcBytes.Length; i++)
                                {
                                    srcBytes[i] = _memory.ReadSubByte(subState.Address[0] + (uint)i);
                                    dstBytes[i] = _memory.ReadSubByte(subState.Address[1] + (uint)i);
                                }
                                for (int i = 0; i < 8; i++)
                                {
                                    commands[i] = _memory.ReadSubWord(0x00FF8010u + (uint)(i * 2));
                                    statuses[i] = _memory.ReadSubWord(0x00FF8020u + (uint)(i * 2));
                                }
                                string codeHex = BitConverter.ToString(bytes.ToArray()).Replace("-", " ");
                                string srcHex = BitConverter.ToString(srcBytes.ToArray()).Replace("-", " ");
                                string dstHex = BitConverter.ToString(dstBytes.ToArray()).Replace("-", " ");
                                string cmdHex = string.Join(" ", commands.ToArray().Select(static w => $"{w:X4}"));
                                string stsHex = string.Join(" ", statuses.ToArray().Select(static w => $"{w:X4}"));
                                Console.WriteLine(
                                    $"[SCD-SUB-PHASE] pc=0x{subPc:X6} op=0x{_subCpu.NextOpcode:X4} sr=0x{subState.Sr:X4} " +
                                    $"d0=0x{subState.Data[0]:X8} d1=0x{subState.Data[1]:X8} d2=0x{subState.Data[2]:X8} " +
                                    $"a0=0x{subState.Address[0]:X8} a1=0x{subState.Address[1]:X8} a6=0x{subState.Address[6]:X8} " +
                                    $"flags=0x{_memory.Registers.MainCpuCommunicationFlags:X2}/0x{_memory.Registers.SubCpuCommunicationFlags:X2} " +
                                    $"swPend={(_memory.Registers.SubSoftwareInterruptPending ? 1 : 0)} swEn={(_memory.Registers.SoftwareInterruptEnabled ? 1 : 0)} " +
                                    $"cddPend={(_memory.Cdd.InterruptPending ? 1 : 0)} cddEn={(_memory.Registers.CddInterruptEnabled ? 1 : 0)} hostClk={(_memory.Registers.CddHostClockOn ? 1 : 0)} " +
                                    $"commands=[{cmdHex}] statuses=[{stsHex}] cdd=[{string.Join(" ", _memory.Cdd.Status.Select(static b => b.ToString("X2")))}] " +
                                    $"mem@0x{baseAddr:X6}={codeHex} src@0x{subState.Address[0]:X6}={srcHex} dst@0x{subState.Address[1]:X6}={dstHex}");
                            }
                            if (!_subPhase70b2Logged && subPc >= 0x0070B2 && subPc <= 0x00712E)
                            {
                                _subPhase70b2Logged = true;
                                var subState = _subCpu.GetState();
                                Console.WriteLine(
                                    $"[SCD-SUB-70B2] pc=0x{subPc:X6} op=0x{_subCpu.NextOpcode:X4} sr=0x{subState.Sr:X4} " +
                                    $"d0=0x{subState.Data[0]:X8} d1=0x{subState.Data[1]:X8} d2=0x{subState.Data[2]:X8} " +
                                    $"a0=0x{subState.Address[0]:X8} a1=0x{subState.Address[1]:X8} fp=0x{subState.Address[6]:X8} " +
                                    $"flags=0x{_memory.Registers.MainCpuCommunicationFlags:X2}/0x{_memory.Registers.SubCpuCommunicationFlags:X2} " +
                                    $"swPend={(_memory.Registers.SubSoftwareInterruptPending ? 1 : 0)} swEn={(_memory.Registers.SoftwareInterruptEnabled ? 1 : 0)} " +
                                    $"cddPend={(_memory.Cdd.InterruptPending ? 1 : 0)} cddEn={(_memory.Registers.CddInterruptEnabled ? 1 : 0)} hostClk={(_memory.Registers.CddHostClockOn ? 1 : 0)}");
                            }
                            if (!_subPhase7130Logged && subPc >= 0x007130 && subPc <= 0x007134)
                            {
                                _subPhase7130Logged = true;
                                var subState = _subCpu.GetState();
                                Console.WriteLine(
                                    $"[SCD-SUB-7130] pc=0x{subPc:X6} op=0x{_subCpu.NextOpcode:X4} sr=0x{subState.Sr:X4} " +
                                    $"d0=0x{subState.Data[0]:X8} d1=0x{subState.Data[1]:X8} d2=0x{subState.Data[2]:X8} " +
                                    $"flags=0x{_memory.Registers.MainCpuCommunicationFlags:X2}/0x{_memory.Registers.SubCpuCommunicationFlags:X2} " +
                                    $"swPend={(_memory.Registers.SubSoftwareInterruptPending ? 1 : 0)} swEn={(_memory.Registers.SoftwareInterruptEnabled ? 1 : 0)}");
                            }
                            if (ForcePrgChecksum && !_subChecksumComputed && subPc >= 0x0002E0 && subPc <= 0x0002E2)
                            {
                                var subState = _subCpu.GetState();
                                TryApplyPrgChecksumFix(subPc, subState.Address[0], subState.Data[2]);
                            }

                            if (_subDumpRemaining > 0 && subPc >= 0x0002E0 && subPc <= 0x0002E2)
                            {
                                _subDumpRemaining--;
                                var subState = _subCpu.GetState();
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
                                    $"D0=0x{subState.Data[0]:X8} D1=0x{subState.Data[1]:X8} D2=0x{subState.Data[2]:X8} " +
                                    $"A0=0x{subState.Address[0]:X8} A1=0x{subState.Address[1]:X8}");
                                if (!_subChecksumComputed)
                                {
                                    _subChecksumComputed = true;
                                    uint start = subState.Address[0] & 0xFFFFF;
                                    uint count = (uint)((subState.Data[2] & 0xFFFF) + 1);
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
                                    if (ForcePrgChecksum && sumBe != expected)
                                    {
                                        _memory.WriteSubWord(0x00018E, sumBe);
                                        Console.WriteLine(
                                            $"[SCD-CHK] Forcing PRG checksum from 0x{expected:X4} to 0x{sumBe:X4}");
                                    }
                                }
                            }
                            _memory.FlushBufferedSubWrites();
                        }
                    }
                }
                else
                {
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
                            if (ForcePrgChecksum && !_subChecksumComputed && _subContext.RegPc >= 0x0002E0 && _subContext.RegPc <= 0x0002E2)
                            {
                                TryApplyPrgChecksumFix(_subContext.RegPc, _subContext.RegAddr[0], _subContext.RegData[2]);
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
                                    if (ForcePrgChecksum && sumBe != expected)
                                    {
                                        _memory.WriteSubWord(0x00018E, sumBe);
                                        Console.WriteLine(
                                            $"[SCD-CHK] Forcing PRG checksum from 0x{expected:X4} to 0x{sumBe:X4}");
                                    }
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
                }

                EutherDrive.Core.MdTracerCore.md_main.g_md_bus = _mainBus;
            }

            if (!_useM68kEmu)
            {
                // Ensure main CPU context is active before capturing.
                EutherDrive.Core.MdTracerCore.md_m68k.ApplyContext(_mainContext!);
                EutherDrive.Core.MdTracerCore.md_m68k.CaptureContext(_mainContext!);
            }

            int frameW = vdp.FrameWidth > 0 ? vdp.FrameWidth : DefaultW;
            int frameH = vdp.FrameHeight > 0 ? vdp.FrameHeight : DefaultH;
            _frameWidth = frameW;
            _frameHeight = frameH;
            int needed = frameW * frameH * 4;
            if (_frameBuffer.Length != needed)
                _frameBuffer = new byte[needed];

            var srcArgb = vdp.GetFrameBuffer();
            if (TraceFrameBuffer)
            {
                int stride = frameW * 4;
                int srcLenBytes = srcArgb.Length * 4;
                if (frameW != _lastFbLogW || frameH != _lastFbLogH || stride != _lastFbLogStride || srcLenBytes != _lastFbLogSrcLen)
                {
                    _lastFbLogW = frameW;
                    _lastFbLogH = frameH;
                    _lastFbLogStride = stride;
                    _lastFbLogSrcLen = srcLenBytes;
                    Console.WriteLine($"[SCD-FRAME] vdp w={frameW} h={frameH} stride={stride} srcLen={srcLenBytes}");
                }
                if (_frameStatsRemaining > 0 && srcArgb.Length > 0)
                {
                    _frameStatsRemaining--;
                    int sampleCount = Math.Min(srcArgb.Length, 1024);
                    int nonZero = 0;
                    int rgbNonZero = 0;
                    for (int i = 0; i < sampleCount; i++)
                    {
                        uint argb = srcArgb[i];
                        byte a0 = (byte)(argb >> 24);
                        byte r0 = (byte)(argb >> 16);
                        byte g0 = (byte)(argb >> 8);
                        byte b0 = (byte)argb;
                        if (r0 != 0 || g0 != 0 || b0 != 0)
                            rgbNonZero++;
                        if (r0 != 0 || g0 != 0 || b0 != 0 || a0 != 0)
                            nonZero++;
                    }
                    uint p0 = srcArgb[0];
                    byte p0a = (byte)(p0 >> 24);
                    byte p0r = (byte)(p0 >> 16);
                    byte p0g = (byte)(p0 >> 8);
                    byte p0b = (byte)p0;
                    Console.WriteLine(
                        $"[SCD-FRAME] sample0={p0r:X2} {p0g:X2} {p0b:X2} {p0a:X2} " +
                        $"sampleNonZero={nonZero}/{sampleCount} sampleRgbNonZero={rgbNonZero}/{sampleCount}");
                }
            }
            int pixels = Math.Min(frameW * frameH, srcArgb.Length);
            int di = 0;
            int nonBlackPixels = 0;
            for (int i = 0; i < pixels; i++)
            {
                uint argb = srcArgb[i];
                byte a = (byte)(argb >> 24);
                byte r = (byte)(argb >> 16);
                byte g = (byte)(argb >> 8);
                byte b = (byte)argb;
                if ((r | g | b) != 0)
                    nonBlackPixels++;
                _frameBuffer[di + 0] = b;
                _frameBuffer[di + 1] = g;
                _frameBuffer[di + 2] = r;
                _frameBuffer[di + 3] = a != 0 ? a : (byte)0xFF;
                di += 4;
            }
            _vdpFrameLooksBlank = nonBlackPixels == 0;
            if (TraceFrameBuffer && _frameStatsRemaining > 0)
            {
                Console.WriteLine($"[SCD-FRAME] vdpNonBlack={nonBlackPixels}/{Math.Max(1, pixels)} blank={(_vdpFrameLooksBlank ? 1 : 0)}");
            }
            if (TraceFrameBuffer && frameCounter >= 0 && (frameCounter % 120) == 0 && srcArgb.Length > 0)
            {
                uint p0 = srcArgb[0];
                byte p0a = (byte)(p0 >> 24);
                byte p0r = (byte)(p0 >> 16);
                byte p0g = (byte)(p0 >> 8);
                byte p0b = (byte)p0;
                Console.WriteLine(
                    $"[SCD-FRAME-PERIODIC] frame={frameCounter} p0={p0r:X2} {p0g:X2} {p0b:X2} {p0a:X2} " +
                    $"vdpNonBlack={nonBlackPixels}/{Math.Max(1, pixels)}");
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
                // Keep disabled by default; this is debug-only overlay only.
                bool applyGfxToFrame = EnableGfxOverlay;
                if (applyGfxToFrame)
                {
                    int dstX0 = (_frameWidth - _gfxWidth) / 2;
                    int dstY0 = (_frameHeight - _gfxHeight) / 2;
                    int srcX0 = 0;
                    int srcY0 = 0;
                    if (dstX0 < 0)
                    {
                        srcX0 = -dstX0;
                        dstX0 = 0;
                    }
                    if (dstY0 < 0)
                    {
                        srcY0 = -dstY0;
                        dstY0 = 0;
                    }
                    int copyW = Math.Min(_frameWidth - dstX0, _gfxWidth - srcX0);
                    int copyH = Math.Min(_frameHeight - dstY0, _gfxHeight - srcY0);
                    int dstStride = _frameWidth * 4;
                    int srcStride = _gfxWidth * 4;
                    int copiedPixels = 0;
                    for (int y = 0; y < copyH; y++)
                    {
                        int srcRow = (srcY0 + y) * srcStride + srcX0 * 4;
                        int dstRow = (dstY0 + y) * dstStride + dstX0 * 4;
                        for (int x = 0; x < copyW; x++)
                        {
                            int si = srcRow + x * 4;
                            byte r = _gfxBuffer[si + 0];
                            byte g = _gfxBuffer[si + 1];
                            byte b = _gfxBuffer[si + 2];
                            byte a = _gfxBuffer[si + 3];
                            if (a == 0)
                            {
                                continue;
                            }

                            int di = dstRow + x * 4;
                            _frameBuffer[di + 0] = b;
                            _frameBuffer[di + 1] = g;
                            _frameBuffer[di + 2] = r;
                            _frameBuffer[di + 3] = 0xFF;
                            copiedPixels++;
                        }
                    }
                    if (TraceFrameBuffer && _gfxFallbackLogRemaining > 0)
                    {
                        _gfxFallbackLogRemaining--;
                        Console.WriteLine($"[SCD-GFX-OVERLAY] copied={copiedPixels} area={copyW}x{copyH}");
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

        if (TraceScdTimer && _memory != null)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _timerTraceLastTicks >= Stopwatch.Frequency)
            {
                _timerTraceLastTicks = now;
                byte tCounter = _memory.Registers.TimerCounter;
                byte tInterval = _memory.Registers.TimerInterval;
                bool tPend = _memory.Registers.TimerInterruptPending;
                bool tEn = _memory.Registers.TimerInterruptEnabled;
                bool cddEn = _memory.Registers.CddInterruptEnabled;
                bool cdcEn = _memory.Registers.CdcInterruptEnabled;
                bool hostClk = _memory.Registers.CddHostClockOn;
                byte subLevel = _memory.GetSubInterruptLevel();
                bool cddPend = _memory.Cdd.InterruptPending;
                ushort sw = _memory.Registers.StopwatchCounter;
                _memory.ConsumeSubAckCounts(out long ack1, out long ack2, out long ack3, out long ack4, out long ack5, out long ack6);
                byte cdd0 = _memory.Cdd.Status.Length > 0 ? _memory.Cdd.Status[0] : (byte)0;
                byte cdd1 = _memory.Cdd.Status.Length > 1 ? _memory.Cdd.Status[1] : (byte)0;
                byte cdd2 = _memory.Cdd.Status.Length > 2 ? _memory.Cdd.Status[2] : (byte)0;
                byte cdd3 = _memory.Cdd.Status.Length > 3 ? _memory.Cdd.Status[3] : (byte)0;
                Console.Error.WriteLine(
                    $"[SCD-TIMER] t={tCounter}/{tInterval} sw=0x{sw:X3} pend={(tPend ? 1 : 0)} en={(tEn ? 1 : 0)} " +
                    $"cddS={cdd0:X2} {cdd1:X2} {cdd2:X2} {cdd3:X2} " +
                    $"cddEn={(cddEn ? 1 : 0)} cdcEn={(cdcEn ? 1 : 0)} cddPend={(cddPend ? 1 : 0)} " +
                    $"ack1={ack1} ack2={ack2} ack3={ack3} ack4={ack4} ack5={ack5} ack6={ack6} " +
                    $"hostclk={(hostClk ? 1 : 0)} subInt={subLevel}");
            }
        }

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

    private void TryApplyPrgChecksumFix(uint subPc, uint a0, uint d2)
    {
        if (_memory == null)
            return;

        _subChecksumComputed = true;
        uint start = a0 & 0xFFFFF;
        uint count = (d2 & 0xFFFF) + 1;
        // Guard against pathological values so this remains bounded.
        if (count == 0 || count > 0x20000)
            return;

        uint end = start + (count * 2);
        ushort sumBe = 0;
        for (uint addr = start; addr < end; addr += 2)
        {
            ushort subWord = _memory.ReadSubWord(addr);
            sumBe = (ushort)(sumBe + subWord);
        }

        ushort expected = _memory.ReadSubWord(0x00018E);
        if (sumBe != expected)
        {
            _memory.WriteSubWord(0x00018E, sumBe);
            Console.WriteLine(
                $"[SCD-CHK] auto-fix pc=0x{subPc:X6} start=0x{start:X6} count={count} expected=0x{expected:X4} fixed=0x{sumBe:X4}");
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

    private static bool ReadEnvFlag(string key, bool fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(key);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (string.Equals(raw, "1", StringComparison.Ordinal))
            return true;
        if (string.Equals(raw, "0", StringComparison.Ordinal))
            return false;
        if (bool.TryParse(raw, out bool parsed))
            return parsed;
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
            }

            if (inFrames > 0)
            {
                while (phase >= inFrames)
                    phase -= inFrames;
                while (phase < 0)
                    phase += inFrames;
            }
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
                w[n] = 0.42 - 0.5 * Math.Cos(a) + 0.08 * Math.Cos(2.0 * a);
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

    private static int ScaleCycleCount(int cycles, double scale)
    {
        if (scale == 1.0)
            return cycles;
        double scaled = cycles * scale;
        if (scaled <= 0)
            return 1;
        if (scaled > int.MaxValue)
            return int.MaxValue;
        return (int)Math.Round(scaled);
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

    private static void TryDumpBootSector(CdRom? disc, string romPath)
    {
        if (disc == null)
            return;
        if (!DumpBootSector && string.IsNullOrWhiteSpace(BootSectorComparePath))
            return;

        CdTrack? dataTrack = null;
        foreach (var track in disc.Cue.Tracks)
        {
            if (track.TrackType == CdTrackType.Data)
            {
                dataTrack = track;
                break;
            }
        }

        if (dataTrack == null)
        {
            Console.Error.WriteLine($"[SCD-BOOT] No data track found for '{romPath}'.");
            return;
        }

        CdTime relative = dataTrack.PregapLen.Add(dataTrack.PauseLen);
        byte[] sector = new byte[CdRom.BytesPerSector];
        bool ok = disc.ReadSector(dataTrack.Number, relative, sector);
        if (!ok)
        {
            Console.Error.WriteLine($"[SCD-BOOT] Failed to read boot sector (track {dataTrack.Number}) for '{romPath}'.");
            return;
        }

        string fullHash = Convert.ToHexString(SHA1.HashData(sector));
        string userHash = Convert.ToHexString(SHA1.HashData(sector.AsSpan(16, 2048)));
        int sigWithSpaces = FindAscii(sector.AsSpan(16, 2048), "SEGA DISC SYSTEM");
        int sigNoSpaces = FindAscii(sector.AsSpan(16, 2048), "SEGADISCSYSTEM");
        string preview = AsciiPreview(sector.AsSpan(16, 64));

        Console.Error.WriteLine(
            $"[SCD-BOOT] track={dataTrack.Number} start={dataTrack.StartTime} pregap={dataTrack.PregapLen} pause={dataTrack.PauseLen} " +
            $"hash2352={fullHash} hash2048={userHash} sig_space={sigWithSpaces} sig_nospace={sigNoSpaces} preview='{preview}'");

        if (DumpBootSector)
        {
            try
            {
                File.WriteAllBytes(BootSectorDumpPath, sector);
                Console.Error.WriteLine($"[SCD-BOOT] Wrote boot sector to '{BootSectorDumpPath}'.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SCD-BOOT] Failed to write boot sector to '{BootSectorDumpPath}': {ex.Message}");
            }
        }

        if (!string.IsNullOrWhiteSpace(BootSectorComparePath) && File.Exists(BootSectorComparePath))
        {
            try
            {
                byte[] other = File.ReadAllBytes(BootSectorComparePath);
                string otherHash = Convert.ToHexString(SHA1.HashData(other));
                int diff = FindFirstDiff(sector, other);
                Console.Error.WriteLine(
                    $"[SCD-BOOT] Compare '{BootSectorComparePath}': len={other.Length} hash={otherHash} first_diff={diff}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SCD-BOOT] Failed to compare boot sector '{BootSectorComparePath}': {ex.Message}");
            }
        }
    }

    private static int FindFirstDiff(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        int min = Math.Min(a.Length, b.Length);
        for (int i = 0; i < min; i++)
        {
            if (a[i] != b[i])
                return i;
        }
        return a.Length == b.Length ? -1 : min;
    }

    private static int FindAscii(ReadOnlySpan<byte> data, string needle)
    {
        if (string.IsNullOrEmpty(needle))
            return -1;
        byte[] pattern = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i <= data.Length - pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < pattern.Length; j++)
            {
                if (data[i + j] != pattern[j])
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return i;
        }
        return -1;
    }

    private static string AsciiPreview(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            byte b = data[i];
            sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
        }
        return sb.ToString();
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

    private bool EnsureSubVectorsReady(SegaCdMemory memory)
    {
        uint subSp = ReadSubVector(memory, 0);
        uint subPc = ReadSubVector(memory, 4);
        if (TraceSubDebug && _subVectorLogRemaining > 0)
        {
            _subVectorLogRemaining--;
            Console.WriteLine($"[SCD-SUB] vectors sp=0x{subSp:X8} pc=0x{subPc:X8}");
        }
        return subSp != 0 && subPc != 0;
    }
}
