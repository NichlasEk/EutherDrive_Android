namespace EutherDrive.Core.Arcade.Taito;

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using EutherDrive.Core.Cpu.M68000Emu;
using EutherDrive.Core.Cpu.MameMusashi;
using EutherDrive.Core.Savestates;
using SharpCompress.Archives;

public sealed class DariusGaidenAdapter : IEmulatorCore, ISavestateCapable, IDisposable
{
    private const int FrameWidth = 320;
    private const int FrameHeight = 232;
    private const int VisibleAreaMinX = 46;
    private const int VisibleAreaMinY = 24;
    private const int FrameStride = FrameWidth * 4;
    private const int FrameBytes = FrameHeight * FrameStride;
    private const byte PivotLayerRank = 0;
    private const byte Sprite0LayerRank = 1;
    private const byte Playfield0LayerRank = 2;
    private const byte Sprite3LayerRank = 3;
    private const byte Playfield3LayerRank = 4;
    private const byte Sprite2LayerRank = 5;
    private const byte Playfield2LayerRank = 6;
    private const byte Sprite1LayerRank = 7;
    private const byte Playfield1LayerRank = 8;
    private const byte EmptyLayerRank = 0xff;
    private const double TargetFps = 26_686_000.0 / 4.0 / (432.0 * 262.0);
    private const int MainClockHz = 16_000_000;
    private const int MainInstructionLimitPerFrame = 120_000;
    private const uint F3WorkRamBase = 0x400000;
    private const int F3WorkRamMirrorWindowSize = 0x40000;
    private const int F3WatchdogTimeoutCycles = MainClockHz * 3;
    private const uint F3SchedulerCurrentIndex = 0x4066fa;
    private const uint F3SchedulerMask = 0x4066fc;
    private const uint F3SchedulerCurrentStackSlot = 0x406700;
    private const uint F3SchedulerCurrentSlot = 0x406704;
    private const uint F3SchedulerTaskSlots = 0x406708;
    private const uint F3SchedulerStackSlots = 0x4066b8;
    private const uint F3SchedulerA5 = F3WorkRamBase + 0x8000;

    private static readonly bool Trace = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_TRACE") == "1";
    private static readonly bool UseNativeF3TrapScheduler = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_NATIVE_TRAPS") != "0";
    private static readonly bool TraceBootPc = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_TRACE_BOOT_PC") == "1";
    private static readonly bool TraceSceneCopyPc = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_TRACE_SCENE_COPY_PC") == "1";
    private static readonly bool TraceTaskCreatePc = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_TRACE_TASK_CREATE") == "1";
    private static readonly bool TraceTaskSchedulerPc = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_TRACE_TASK_SCHEDULER") == "1";
    private static readonly bool TraceSummary = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_TRACE_SUMMARY") == "1";
    private static readonly bool TracePhaseTiming = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_PHASE_TIMING") == "1";
    private static readonly bool TraceHotFill = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_TRACE_HOT_FILL") == "1";
    private static readonly bool TracePcProfile = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_PC_PROFILE") == "1";
    private static readonly bool RenderStats = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_RENDER_STATS") == "1";
    private static readonly int TraceInstructionLimit = ParseEnvInt("EUTHERDRIVE_DARIUSG_TRACE_INSTRUCTIONS", 64);
    private static readonly int TraceBootPcLimit = ParseEnvInt("EUTHERDRIVE_DARIUSG_TRACE_BOOT_PC_LIMIT", 256);
    private static readonly int TraceSceneCopyPcLimit = ParseEnvInt("EUTHERDRIVE_DARIUSG_TRACE_SCENE_COPY_PC_LIMIT", 32);
    private static readonly int TraceTaskCreatePcLimit = ParseEnvInt("EUTHERDRIVE_DARIUSG_TRACE_TASK_CREATE_LIMIT", 256);
    private static readonly int TraceTaskCreateFromFrame = ParseEnvInt("EUTHERDRIVE_DARIUSG_TRACE_TASK_CREATE_FROM_FRAME", 0);
    private static readonly int CpuScale = Math.Clamp(ParseEnvInt("EUTHERDRIVE_DARIUSG_CPU_SCALE", 1), 1, 32);
    private static readonly int RenderDivisor = Math.Clamp(ParseEnvInt("EUTHERDRIVE_DARIUSG_RENDER_DIVISOR", 1), 1, 8);
    private static readonly bool AdaptiveRenderPacing = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_ADAPTIVE_RENDER") == "1";
    private static readonly long TargetFrameTicks = Math.Max(1, (long)(Stopwatch.Frequency / TargetFps));

    private static readonly HashSet<string> SupportedDrivers = new(StringComparer.OrdinalIgnoreCase)
    {
        "dariusg",
        "dariusgj",
        "dariusgu",
        "dariusgx"
    };

    private static readonly string[] RequiredDariusGaidenEntries =
    {
        "d87-01.bin",
        "d87-02.bin",
        "d87-03.bin",
        "d87-04.bin",
        "d87-05.bin",
        "d87-06.bin",
        "d87-08.bin",
        "d87-10.bin",
        "d87-11.bin",
        "d87-12.bin",
        "d87-13.bin",
        "d87-14.bin",
        "d87-16.bin",
        "d87-17.bin"
    };

    private readonly byte[] _frameBuffer = new byte[FrameBytes];
    private readonly byte[] _presentFrameBuffer = new byte[FrameBytes];
    private readonly byte[] _framePriority = new byte[FrameWidth * FrameHeight];
    private readonly byte[] _framePriorityRank = new byte[FrameWidth * FrameHeight];
    private readonly byte[] _framePriorityConflict = new byte[FrameWidth * FrameHeight];
    private readonly ushort[] _mixSrcPalette = new ushort[FrameWidth * FrameHeight];
    private readonly ushort[] _mixDstPalette = new ushort[FrameWidth * FrameHeight];
    private readonly byte[] _mixSrcBlend = new byte[FrameWidth * FrameHeight];
    private readonly byte[] _mixDstBlend = new byte[FrameWidth * FrameHeight];
    private readonly byte[] _mixSrcPriority = new byte[FrameWidth * FrameHeight];
    private readonly byte[] _mixDstPriority = new byte[FrameWidth * FrameHeight];
    private readonly byte[] _mixSrcBlendMode = new byte[FrameWidth * FrameHeight];
    private readonly byte[] _mixDstBlendMode = new byte[FrameWidth * FrameHeight];
    private readonly ushort[] _spriteReefPalette = new ushort[FrameWidth * FrameHeight];
    private readonly byte[] _spriteReefGroup = new byte[FrameWidth * FrameHeight];
    private readonly int[] _spriteReefOffsets = new int[FrameWidth * FrameHeight];
    private int _spriteReefOffsetCount;
    private readonly int[] _spriteReefNext = new int[FrameWidth * FrameHeight];
    private readonly int[] _spriteReefRowHead = new int[FrameHeight];
    private readonly bool[] _spriteReefRowActive = new bool[FrameHeight];
    private readonly List<int> _spriteReefTouchedRows = new(FrameHeight);
    private readonly ushort[] _playfieldLineAttrCache = new ushort[32];
    private readonly ushort[] _playfieldLineCodeCache = new ushort[32];
    private readonly uint[] _paletteColorCache = new uint[0x2000];
    private readonly int[] _paletteColorCacheStamp = new int[0x2000];
    private int _paletteColorCacheFrame;
    private readonly F3LineState[] _lineStates = new F3LineState[256];
    private F3LineState _lineBuildState = new();
    private readonly MameMusashi68Ec020 _mainCpu = new();
    private readonly TaitoF3MainBus _bus = new();
    private readonly TaitoF3SoundSystem _sound = new();
    private RomIdentity? _romIdentity;
    private TaitoF3RomSet? _roms;
    private string _driverName = "dariusg";
    private long _frameCounter;
    private bool _loaded;
    private bool _cpuFaulted;
    private int _adaptiveRenderSkipsRemaining;
    private string _lastStopReason = "idle";
    private uint _lastRecoveredInvalidPc;
    private uint _lastPcBeforeRecoveredInvalidPc;
    private ushort _lastOpBeforeRecoveredInvalidPc;
    private ulong _executedInstructions;
    private ulong _executedCycles;
    private ulong _m68ec020ProbeInstructions;
    private int _lastFrameInstructions;
    private int _lastFrameCycles;
    private long _lastFrameTotalTicks;
    private long _lastFrameCpuTicks;
    private long _lastFrameRenderTicks;
    private long _lastFrameControlTicks;
    private int _traceInstructionsRemaining;
    private int _traceSceneCopyPcRemaining;
    private int _traceHotFillRemaining = ParseEnvInt("EUTHERDRIVE_DARIUSG_TRACE_HOT_FILL_LIMIT", 128);
    private readonly Dictionary<uint, int> _framePcProfile = new();
    private readonly Queue<F3TaskState> _f3TaskQueue = new();
    private readonly uint[] _m68ec020ScratchData = new uint[8];
    private readonly uint[] _m68ec020ScratchAddress = new uint[7];
    private ulong _f3TasksEnqueued;
    private ulong _f3TasksDispatched;
    private uint _lastF3TaskEntry;
    private uint _lastF3TaskStack;
    private uint _lastF3TrapPc;
    private int _currentF3TaskPriority;
    private readonly uint[] _recentF3EnqueuedTasks = new uint[8];
    private readonly uint[] _recentF3DispatchedTasks = new uint[8];
    private int _recentF3EnqueuedIndex;
    private int _recentF3DispatchedIndex;
    private uint _nextF3TaskStack = 0x0041_f000;
    private int _traceBootPcRemaining;
    private int _traceTaskCreatePcRemaining;
    private int _masterVolumePercent = 100;
    private bool _spriteBank;
    private bool _spriteTrails;
    private bool _flipScreen;
    private int _spritePenMask = 0x0f;
    private readonly List<F3Sprite> _sprites = new(0x400);
    private readonly List<F3Sprite> _latchedSprites = new(0x400);
    private int _lastSpriteCandidates;
    private int _lastVisibleSprites;
    private int _lastSpritePixels;
    private int _lastPlayfieldCandidates;
    private int _lastPlayfieldPixels;
    private int _lastMixSourcePixels;
    private int _lastMixLitSourcePixels;
    private int _lastMixDestOnlyPixels;
    private int _lastMixPriorityZeroConflicts;
    private long _debugSummaryFrame = long.MinValue;
    private string _debugSummaryCache = string.Empty;
    private readonly int[] _lastPlayfieldLayerCandidates = new int[4];
    private readonly int[] _lastPlayfieldLayerPixels = new int[4];
    private readonly int[] _lastPlayfieldBlendSelect0 = new int[4];
    private readonly int[] _lastPlayfieldBlendSelect1 = new int[4];
    private ushort _lastSpriteControlWord;
    private int _lastSpriteCandidateEntry = -1;
    private int _lastSpriteCandidateX;
    private int _lastSpriteCandidateY;
    private int _lastSpriteCandidateScaleX;
    private int _lastSpriteCandidateScaleY;
    private int _lastSpriteCandidateTile;
    private byte _lastSpriteCandidateControl;
    private int _lastSpriteMinX;
    private int _lastSpriteMaxX;
    private int _lastSpriteMinY;
    private int _lastSpriteMaxY;
    private int _lastSpriteClosestDistance;
    private int _lastSpriteMixSource;
    private int _lastSpriteMixDest;
    private int _lastSpriteMixBehind;
    private int _lastSpriteMixSameBlend;
    private int _lastSpriteMixDisabled;
    private int _lastSpriteMixClipped;
    private bool _hasPresentFrame;
    private ulong _sceneEntryHits;
    private ulong _sceneGateRoutineHits;
    private ulong _sceneGateSetInstructionHits;
    private ulong _sceneGateWaitHits;
    private ulong _mainGateWaitHits;
    private ulong _sceneInitResumeHits;
    private ulong _sceneInitMainHits;
    private ulong _sceneMenuInitHits;
    private ulong _sceneMenuYieldHits;
    private ulong _sceneSpawnerYieldHits;
    private ulong _sceneContinuationEnqueued;
    private ulong _sceneContinuationDispatched;
    private ulong _sceneContinuationRemoved;
    private bool _gameStartAccepted;
    private F3TaskState? _lastPersistentYieldTask;
    private ulong _spriteListBuildHits;
    private ulong _spriteListProducerWrites;
    private ulong _spriteListFinalizeHits;
    private ulong _spriteListLatchedWrites;
    private uint _lastSceneAbsoluteCallTarget;
    private uint _lastF3TaskRemoveMask;

    public RomInfo RomInfo { get; } = new()
    {
        Summary = "Taito F3 Darius Gaiden adapter idle",
        RegionHint = ConsoleRegion.Auto
    };

    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => _frameCounter;

    public string DebugSummary
    {
        get
        {
            if (_debugSummaryFrame == _frameCounter && _debugSummaryCache.Length != 0)
                return _debugSummaryCache;
            _debugSummaryCache = BuildDebugSummary();
            _debugSummaryFrame = _frameCounter;
            return _debugSummaryCache;
        }
    }

    public int LastFrameInstructions => _lastFrameInstructions;

    public int LastFrameCycles => _lastFrameCycles;

    public long LastFrameTotalTicks => _lastFrameTotalTicks;

    public long LastFrameCpuTicks => _lastFrameCpuTicks;

    public long LastFrameRenderTicks => _lastFrameRenderTicks;

    public long LastFrameControlTicks => _lastFrameControlTicks;

    private string BuildDebugSummary()
    {
        var state = GetScratchMainCpuState();
        double tickMs = 1000.0 / Stopwatch.Frequency;
        return $"driver={_driverName} frame={_frameCounter} pc=0x{_mainCpu.Pc:X6} sr=0x{_mainCpu.StatusRegister:X4} " +
        $"op=0x{_mainCpu.NextOpcode:X4} d0=0x{state.Data[0]:X8} d1=0x{state.Data[1]:X8} a0=0x{state.Address[0]:X8} a1=0x{state.Address[1]:X8} a5=0x{state.Address[5]:X8} a6=0x{state.Address[6]:X8} " +
        $"cycles={_executedCycles} instr={_executedInstructions} frameInstr={_lastFrameInstructions} frameCycles={_lastFrameCycles} phaseMs={_lastFrameTotalTicks * tickMs:0.###}/{_lastFrameCpuTicks * tickMs:0.###}/{_lastFrameRenderTicks * tickMs:0.###}/{_lastFrameControlTicks * tickMs:0.###} mame020={_mainCpu.ImplementedOpcodeCount}/{_mainCpu.MameEc020OpcodeCount} " +
        $"020probe={_m68ec020ProbeInstructions} tasks={_f3TaskQueue.Count} q={BuildTaskQueueSample()} taskEnq={_f3TasksEnqueued} taskRun={_f3TasksDispatched} " +
        $"lastTask=0x{_lastF3TaskEntry:X6} enq={BuildRecentTaskSample(_recentF3EnqueuedTasks, _recentF3EnqueuedIndex)} run={BuildRecentTaskSample(_recentF3DispatchedTasks, _recentF3DispatchedIndex)} lastTrap=0x{_lastF3TrapPc:X6} vbr=0x{_mainCpu.VectorBase:X6} " +
        $"rte={_mainCpu.RteCount}/{_mainCpu.SuspiciousRteCount}@0x{_mainCpu.LastRteStackPointer:X6}:sr{_mainCpu.LastRteStatusRegister:X4}/pc0x{_mainCpu.LastRteProgramCounter:X6}/fmt{_mainCpu.LastRteFormatWord:X4} firstBadRte=0x{_mainCpu.FirstSuspiciousRteInstructionPc:X6}/0x{_mainCpu.FirstSuspiciousRteOpcode:X4}@sp0x{_mainCpu.FirstSuspiciousRteStackPointer:X6}:sr{_mainCpu.FirstSuspiciousRteStatusRegister:X4}/pc0x{_mainCpu.FirstSuspiciousRteProgramCounter:X6}/fmt{_mainCpu.FirstSuspiciousRteFormatWord:X4} sp=a7:{_mainCpu.ActiveStackPointer:X6}/u:{_mainCpu.UserStackPointer:X6}/i:{_mainCpu.InterruptStackPointer:X6}/m:{_mainCpu.MasterStackPointer:X6} badSupSp={_mainCpu.SuspiciousSupervisorStackCount}@0x{_mainCpu.FirstSuspiciousSupervisorStackPc:X6}/0x{_mainCpu.FirstSuspiciousSupervisorStackOpcode:X4}:sr{_mainCpu.FirstSuspiciousSupervisorStackStatusRegister:X4}/sp0x{_mainCpu.FirstSuspiciousSupervisorStackPointer:X6} lowSp={_mainCpu.LowStackSwitchCount}@0x{_mainCpu.LastLowStackSwitchPc:X6}/0x{_mainCpu.LastLowStackSwitchOpcode:X4}:sr{_mainCpu.LastLowStackSwitchOldSr:X4}->{_mainCpu.LastLowStackSwitchNewSr:X4}:0x{_mainCpu.LastLowStackSwitchOldStackPointer:X6}->0x{_mainCpu.LastLowStackSwitchNewStackPointer:X6}/u{_mainCpu.LastLowStackSwitchUserStackPointer:X6}/i{_mainCpu.LastLowStackSwitchInterruptStackPointer:X6}/m{_mainCpu.LastLowStackSwitchMasterStackPointer:X6} restIdx=0x{_mainCpu.LastRestoreIndexedPc:X6}/ext{_mainCpu.LastRestoreIndexedExtension:X4}:base0x{_mainCpu.LastRestoreIndexedBase:X8}+idx{_mainCpu.LastRestoreIndexedIndex}/raw0x{_mainCpu.LastRestoreIndexedRawIndex:X8}->ea0x{_mainCpu.LastRestoreIndexedAddress:X8}=0x{_mainCpu.LastRestoreIndexedValue:X8} " +
        $"ill={_mainCpu.IllegalInstructionCount}@0x{_mainCpu.FirstIllegalInstructionPc:X6}/0x{_mainCpu.FirstIllegalInstructionOpcode:X4}:sr{_mainCpu.FirstIllegalInstructionStatusRegister:X4} fmtErr={_mainCpu.FormatErrorCount}@0x{_mainCpu.FirstFormatErrorPc:X6}/0x{_mainCpu.FirstFormatErrorOpcode:X4}:sr{_mainCpu.FirstFormatErrorStatusRegister:X4}/fmt{_mainCpu.FirstFormatErrorFrameWord:X4} " +
        $"ramW={_bus.WorkRamWrites} palW={_bus.PaletteWrites} sprW={_bus.SpriteWrites} pfW={_bus.PlayfieldWrites} pfNZ={_bus.PlayfieldNonZeroWords} pfCand={_lastPlayfieldCandidates} pfPix={_lastPlayfieldPixels} pfL={BuildPlayfieldLayerSample()} mixSrc={_lastMixSourcePixels}/{_lastMixLitSourcePixels} mixDstOnly={_lastMixDestOnlyPixels} mixP0={_lastMixPriorityZeroConflicts} lineMid={BuildLineStateSample()} txtNZ={_bus.TextNonZeroWords} pivNZ={_bus.PivotNonZeroWords} " +
        $"lastSprNZ=0x{_bus.LastNonZeroSpriteWritePc:X6}->0x{_bus.LastNonZeroSpriteWriteAddress:X6}:0x{_bus.LastNonZeroSpriteWriteValue:X2} lastPfNZ=0x{_bus.LastNonZeroPlayfieldWritePc:X6}->0x{_bus.LastNonZeroPlayfieldWriteAddress:X6}:0x{_bus.LastNonZeroPlayfieldWriteValue:X2} lastTxtNZ=0x{_bus.LastNonZeroTextWritePc:X6}->0x{_bus.LastNonZeroTextWriteAddress:X6}:0x{_bus.LastNonZeroTextWriteValue:X2} " +
        $"mode=0x{_bus.PeekByte(0x40221d):X2}/0x{_bus.PeekByte(0x40223a):X2}/0x{_bus.PeekByte(0x40223d):X2}/0x{_bus.PeekByte(0x40223f):X2} coin=0x{_bus.CoinWord0:X4}/0x{_bus.CoinWord1:X4}/in{(_bus.Input.Coin1 ? 1 : 0)} start={(_bus.Input.Start ? 1 : 0)}/lat{_bus.StartLatchFrames}/in0=0x{_bus.InputPort0Snapshot:X8} fio22={BuildFioSoftSample()} wdog={_bus.WatchdogKicks}/{_bus.WatchdogSoftResets}/{_bus.WatchdogCyclesRemaining} coinT={_bus.PeekWord(0x400090):X4},{_bus.PeekWord(0x400092):X4},{_bus.PeekWord(0x4000a2):X4},{_bus.PeekWord(0x4000a4):X4} bkup18=0x{_bus.PeekByte(0x406c6c):X2} cfg2_18=0x{_bus.PeekByte(0x406c8c):X2} gateEbb4=0x{_bus.PeekByte(0x406bb4):X2} gateEbb5=0x{_bus.PeekByte(0x406bb5):X2} gateEbb6=0x{_bus.PeekByte(0x406bb6):X2} gateW=0x{_bus.LastGateWritePc:X6}->0x{_bus.LastGateWriteAddress:X6}:0x{_bus.LastGateWriteValue:X2} gateNZ=0x{_bus.LastNonZeroGateWritePc:X6}->0x{_bus.LastNonZeroGateWriteAddress:X6}:0x{_bus.LastNonZeroGateWriteValue:X2}/{_bus.NonZeroGateWrites} scene=entry:{_sceneEntryHits}/init:{_sceneInitResumeHits},{_sceneInitMainHits},{_sceneMenuInitHits},{_sceneSpawnerYieldHits}/gate:{_sceneGateRoutineHits}/bset:{_sceneGateSetInstructionHits}/wait:{_sceneGateWaitHits}/mainwait:{_mainGateWaitHits}/call=0x{_lastSceneAbsoluteCallTarget:X6}/cont:{_sceneContinuationEnqueued},{_sceneContinuationDispatched},{_sceneContinuationRemoved}/rm=0x{_lastF3TaskRemoveMask:X8} flag224=0x{_bus.PeekByte(0x402224):X2} irqPtr=0x{_bus.PeekLong(0x406704):X8}/W0x{_bus.LastIrqWorkPointerWritePc:X6}->0x{_bus.LastIrqWorkPointerWriteAddress:X6}:0x{_bus.LastIrqWorkPointerValue:X8} sched={BuildNativeSchedulerSample()} maskW=0x{_bus.LastSchedulerMaskWritePc:X6}->0x{_bus.LastSchedulerMaskWriteAddress:X6}:0x{_bus.LastSchedulerMaskValue:X8} taskCtlW=0x{_bus.LastSchedulerTaskControlWritePc:X6}->0x{_bus.LastSchedulerTaskControlWriteAddress:X6}:0x{_bus.LastSchedulerTaskControlValue:X8} schedW=0x{_bus.LastSchedulerStackWritePc:X6}->0x{_bus.LastSchedulerStackWriteAddress:X6}:0x{_bus.LastSchedulerStackPointerValue:X8} schedLow=0x{_bus.LastLowSchedulerStackWritePc:X6}->0x{_bus.LastLowSchedulerStackWriteAddress:X6}:0x{_bus.LastLowSchedulerStackPointerValue:X8}/{_bus.LowSchedulerStackWrites} objMap={_bus.PeekLong(0x410000):X8},{_bus.PeekLong(0x410004):X8},{_bus.PeekLong(0x410030):X8} obj916=0x{_bus.PeekByte(0x408916):X2} obj917=0x{_bus.PeekByte(0x408917):X2} listCnt=0x{_bus.PeekWord(0x402218):X4}/W0x{_bus.LastSpriteListCountWritePc:X6}->0x{_bus.LastSpriteListCountWriteAddress:X6}:0x{_bus.LastSpriteListCountValue:X4} listPtr=0x{_bus.PeekLong(0x407360):X6},0x{_bus.PeekLong(0x407364):X6},0x{_bus.PeekLong(0x407368):X6} listPtrW=0x{_bus.LastSpriteListPointerWritePc:X6}->0x{_bus.LastSpriteListPointerWriteAddress:X6}:0x{_bus.LastSpriteListPointerValue:X8} listFlow={_spriteListBuildHits},{_spriteListProducerWrites},{_spriteListFinalizeHits},{_spriteListLatchedWrites} sprNZ={_bus.SpriteNonZeroWords} sprFirst={_bus.FirstNonZeroSpriteWordOffset:X4} sprRaw={BuildSpriteRamSample()} sprHead0={BuildSpritePointerSample(0x407360)} sprHead1={BuildSpritePointerSample(0x407364)} sprHead2={BuildSpritePointerSample(0x407368)} sprTiles={BuildSpriteTileSample()} " +
        $"taskFrmW=0x{_bus.LastTaskFrameWritePc:X6}->0x{_bus.LastTaskFrameWriteAddress:X6}/s0x{_bus.LastTaskFrameWriteStack:X6}:pc0x{_bus.LastTaskFrameWriteFramePc:X8} firstBadTaskFrm=0x{_bus.FirstBadTaskFrameWritePc:X6}->0x{_bus.FirstBadTaskFrameWriteAddress:X6}/s0x{_bus.FirstBadTaskFrameWriteStack:X6}:pc0x{_bus.FirstBadTaskFrameWriteFramePc:X8} " +
        $"sprCand={_lastSpriteCandidates} sprVis={_lastVisibleSprites} sprPix={_lastSpritePixels} sprMix=s{_lastSpriteMixSource}/d{_lastSpriteMixDest}/b{_lastSpriteMixBehind}/same{_lastSpriteMixSameBlend}/off{_lastSpriteMixDisabled}/clip{_lastSpriteMixClipped} sprCtl=0x{_lastSpriteControlWord:X4} sprBank={(_spriteBank ? 1 : 0)} sprLast={_lastSpriteCandidateEntry:X3}/0x{_lastSpriteCandidateTile:X5}/0x{_lastSpriteCandidateControl:X2}@{_lastSpriteCandidateX},{_lastSpriteCandidateY}+{_lastSpriteCandidateScaleX},{_lastSpriteCandidateScaleY} sprBox={_lastSpriteMinX},{_lastSpriteMinY}..{_lastSpriteMaxX},{_lastSpriteMaxY}/{_lastSpriteClosestDistance} " +
        $"ctrlR={_bus.ControlReads} lastCtrl=0x{_bus.LastControlReadAddress:X6}:0x{_bus.LastControlReadValue:X2} modeW=0x{_bus.LastModeWritePc:X6}->0x{_bus.LastModeWriteAddress:X6}:0x{_bus.LastModeWriteValue:X2} modeBtst=0x{_bus.LastModeBtstPc:X6}@0x{_bus.LastModeBtstAddress:X6}:0x{_bus.LastModeBtstValue:X2}/b{_bus.LastModeBtstBit}/z{(_bus.LastModeBtstZero ? 1 : 0)} btst=0x{_bus.LastBtstAddress:X6}:0x{_bus.LastBtstValue:X2}/b{_bus.LastBtstBit} bkupW=0x{_bus.LastBackupWritePc:X6}:0x{_bus.LastBackupWriteValue:X2} ctrlW={_bus.ControlWrites} dpramR={_bus.DualPortReads}@0x{_bus.LastDualPortReadPc:X6}->0x{_bus.LastDualPortReadAddress:X6}:0x{_bus.LastDualPortReadValue:X2} dpramW={_bus.DualPortWrites}@0x{_bus.LastDualPortWritePc:X6}->0x{_bus.LastDualPortWriteAddress:X6}:0x{_bus.LastDualPortWriteValue:X2} sndRst={(_bus.SoundCpuResetAsserted ? 1 : 0)} sndRstW=0x{_bus.LastSoundResetWritePc:X6}->0x{_bus.LastSoundResetWriteAddress:X6} {_sound.DebugSummary} unmappedR={_bus.UnmappedReads}@0x{_bus.LastUnmappedReadPc:X6}->0x{_bus.LastUnmappedReadAddress:X6} unmappedW={_bus.UnmappedWrites}@0x{_bus.LastUnmappedWritePc:X6}->0x{_bus.LastUnmappedWriteAddress:X6}:0x{_bus.LastUnmappedWriteValue:X2} recover=0x{_lastRecoveredInvalidPc:X6}<-0x{_lastPcBeforeRecoveredInvalidPc:X6}/0x{_lastOpBeforeRecoveredInvalidPc:X4} stop={_lastStopReason}";
    }

    private string BuildSpritePointerSample(uint pointerAddress)
    {
        uint pointer = _bus.PeekLong(pointerAddress) & 0x00ff_ffff;
        if (pointer < 0x600000 || pointer >= 0x610000)
            return "--------";

        return string.Create(CultureInfo.InvariantCulture, $"{_bus.PeekWord(pointer):X4},{_bus.PeekWord(pointer + 2):X4},{_bus.PeekWord(pointer + 4):X4},{_bus.PeekWord(pointer + 6):X4},{_bus.PeekWord(pointer + 8):X4},{_bus.PeekWord(pointer + 10):X4},{_bus.PeekWord(pointer + 12):X4},{_bus.PeekWord(pointer + 14):X4}");
    }

    private string BuildLineStateSample()
    {
        F3LineState line = _lineStates[(VisibleAreaMinY + FrameHeight / 2) & 0xff];
        return string.Create(
            CultureInfo.InvariantCulture,
            $"b={line.Blend[0]},{line.Blend[1]},{line.Blend[2]},{line.Blend[3]} pf={line.PlayfieldMix[0]:X4},{line.PlayfieldMix[1]:X4},{line.PlayfieldMix[2]:X4},{line.PlayfieldMix[3]:X4} piv={line.PivotMix:X4}");
    }

    private string BuildPlayfieldLayerSample()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"0:{_lastPlayfieldLayerCandidates[0]}/{_lastPlayfieldLayerPixels[0]}/{_lastPlayfieldBlendSelect0[0]},{_lastPlayfieldBlendSelect1[0]} " +
            $"1:{_lastPlayfieldLayerCandidates[1]}/{_lastPlayfieldLayerPixels[1]}/{_lastPlayfieldBlendSelect0[1]},{_lastPlayfieldBlendSelect1[1]} " +
            $"2:{_lastPlayfieldLayerCandidates[2]}/{_lastPlayfieldLayerPixels[2]}/{_lastPlayfieldBlendSelect0[2]},{_lastPlayfieldBlendSelect1[2]} " +
            $"3:{_lastPlayfieldLayerCandidates[3]}/{_lastPlayfieldLayerPixels[3]}/{_lastPlayfieldBlendSelect0[3]},{_lastPlayfieldBlendSelect1[3]}");

    private string BuildFioSoftSample()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{_bus.PeekByte(0x402224):X2},{_bus.PeekByte(0x402225):X2},{_bus.PeekByte(0x402226):X2},{_bus.PeekByte(0x402227):X2}," +
            $"{_bus.PeekByte(0x402228):X2},{_bus.PeekByte(0x402229):X2},{_bus.PeekByte(0x40222a):X2},{_bus.PeekByte(0x40222b):X2}");

    private string BuildNativeSchedulerSample()
    {
        uint currentStackSlot = _bus.PeekLong(F3SchedulerCurrentStackSlot);
        uint currentStack = _bus.PeekLong(currentStackSlot);
        uint currentTaskSlot = _bus.PeekLong(F3SchedulerCurrentSlot);
        uint restoreFrame = currentStack + 60u;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"idx={_bus.PeekWord(F3SchedulerCurrentIndex):X4} mask={_bus.PeekLong(F3SchedulerMask):X8} cur=0x{currentStackSlot:X8}/val=0x{currentStack:X8} ptr=0x{currentTaskSlot:X8}/val=0x{_bus.PeekLong(currentTaskSlot):X8} " +
            $"stkFrm={_bus.PeekWord(restoreFrame):X4},{_bus.PeekLong(restoreFrame + 2):X8},{_bus.PeekWord(restoreFrame + 6):X4} " +
            $"{BuildSchedulerSlotSample(0)} {BuildSchedulerSlotSample(1)} {BuildSchedulerSlotSample(4)} {BuildSchedulerSlotSample(15)}");
    }

    private string BuildSchedulerSlotSample(int priority)
    {
        uint control = _bus.PeekLong(F3SchedulerTaskSlots + (uint)priority * 4u);
        uint stack = _bus.PeekLong(F3SchedulerStackSlots + (uint)priority * 4u);
        uint frame = stack + 60u;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"s{priority}={control:X8}@0x{stack:X8}/{_bus.PeekWord(frame):X4},{_bus.PeekLong(frame + 2):X8},{_bus.PeekWord(frame + 6):X4}");
    }

    private bool ShouldTraceF3TaskCreatePc(uint pc)
        => _frameCounter >= TraceTaskCreateFromFrame
            && (pc is 0x001e60 or 0x001e76 or 0x001e84 or 0x001eb8
            or 0x001ed6 or 0x001ede or 0x001f10
            or 0x001f32 or 0x001f3c or 0x001f5a or 0x001f62
            || TraceTaskSchedulerPc && (pc is 0x001a34 or 0x001a4c
            or 0x001a54 or 0x001a5c or 0x001a64 or 0x001a66
            or 0x00222e or 0x002232 or 0x00223a
            or 0x002274 or 0x002278 or 0x00227c or 0x002280
            or 0x002284 or 0x002292 or 0x002298 or 0x00229a
            or 0x00229c or 0x0022a0 or 0x0022a6 or 0x0022ae
            or 0x0022ba or 0x0022c6 or 0x0022d8 or 0x0022dc
            or 0x0022de or 0x0022f8 or 0x0022fa or 0x002302
            or 0x00230a or 0x00230c or 0x002310
            || pc is >= 0x001262 and <= 0x0012c0
            || pc is >= 0x00144a and <= 0x0014d8
            || pc is >= 0x00163c and <= 0x001656
            || pc is >= 0x0038b0 and <= 0x003910
            || pc is >= 0x0c676e and <= 0x0c67e0));

    private void TraceF3TaskCreatePc(int instructions, uint pc, ushort op)
    {
        var state = GetScratchMainCpuState();
        uint sp = (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp;
        uint slot15Stack = _bus.PeekLong(F3SchedulerStackSlots + 15u * 4u);
        uint slot15Frame = slot15Stack + 60u;
        uint slot15Control = F3SchedulerTaskSlots + 15u * 4u;
        uint a1Frame = state.Address[1] + 60u;
        Console.WriteLine(
            $"[DARIUSG-TASK] f={_frameCounter} i={instructions} pc=0x{pc:X6} op=0x{op:X4} sr=0x{state.Sr:X4} " +
            $"ccr=0x{state.Sr & 0x1f:X2} d0=0x{state.Data[0]:X8} d1=0x{state.Data[1]:X8} d2=0x{state.Data[2]:X8} d3=0x{state.Data[3]:X8} d6=0x{state.Data[6]:X8} d7=0x{state.Data[7]:X8} " +
            $"a0=0x{state.Address[0]:X8} a1=0x{state.Address[1]:X8} a2=0x{state.Address[2]:X8} a3=0x{state.Address[3]:X8} a5=0x{state.Address[5]:X8} a6=0x{state.Address[6]:X8} sp=0x{sp:X8} usp=0x{state.Usp:X8} ssp=0x{state.Ssp:X8} " +
            $"stk={_bus.PeekLong(sp):X8},{_bus.PeekLong(sp + 4):X8},{_bus.PeekLong(sp + 8):X8},{_bus.PeekLong(sp + 0x0c):X8},{_bus.PeekLong(sp + 0x10):X8},{_bus.PeekLong(sp + 0x14):X8},{_bus.PeekLong(sp + 0x18):X8},{_bus.PeekLong(sp + 0x1c):X8},{_bus.PeekLong(sp + 0x20):X8},{_bus.PeekLong(sp + 0x24):X8} " +
            $"cntW=0x{_bus.LastSceneCopyCountWritePc:X6}->0x{_bus.LastSceneCopyCountWriteAddress:X6}:0x{_bus.LastSceneCopyCountWriteValue:X4} cnt=0x{_bus.PeekWord(0x402210):X4} " +
            $"mask=0x{_bus.PeekLong(F3SchedulerMask):X8} idx=0x{_bus.PeekWord(F3SchedulerCurrentIndex):X4} " +
            $"slot15=0x{slot15Stack:X8}/ctl=0x{_bus.PeekLong(slot15Control):X8} " +
            $"slot15frm={_bus.PeekWord(slot15Frame):X4},{_bus.PeekLong(slot15Frame + 2):X8},{_bus.PeekWord(slot15Frame + 6):X4} " +
            $"a1frm={_bus.PeekWord(a1Frame):X4},{_bus.PeekLong(a1Frame + 2):X8},{_bus.PeekWord(a1Frame + 6):X4}");
    }

    private string BuildSpriteRamSample()
    {
        int first = _bus.FirstNonZeroSpriteWordOffset;
        if (first < 0)
            return "--------";

        int start = Math.Max(0, first - (first & 7));
        return string.Create(CultureInfo.InvariantCulture, $"{start:X4}:{_bus.ReadSpriteWord(start + 0):X4},{_bus.ReadSpriteWord(start + 1):X4},{_bus.ReadSpriteWord(start + 2):X4},{_bus.ReadSpriteWord(start + 3):X4},{_bus.ReadSpriteWord(start + 4):X4},{_bus.ReadSpriteWord(start + 5):X4},{_bus.ReadSpriteWord(start + 6):X4},{_bus.ReadSpriteWord(start + 7):X4}");
    }

    private string BuildSpriteTileSample()
    {
        List<string> entries = new(4);
        for (int bank = 0; bank <= 0x4000 && entries.Count < 4; bank += 0x4000)
        {
            for (int entry = 0; entry < 0x400 && entries.Count < 4; entry++)
            {
                int wordOffset = bank + entry * 8;
                ushort tile = _bus.ReadSpriteWord(wordOffset);
                if (tile == 0)
                    continue;

                entries.Add(string.Create(CultureInfo.InvariantCulture, $"{wordOffset:X4}:{tile:X4}/{_bus.ReadSpriteWord(wordOffset + 1):X4}/{_bus.ReadSpriteWord(wordOffset + 2):X4}/{_bus.ReadSpriteWord(wordOffset + 3):X4}/{_bus.ReadSpriteWord(wordOffset + 4):X4}/{_bus.ReadSpriteWord(wordOffset + 5):X4}/{_bus.ReadSpriteWord(wordOffset + 6):X4}"));
            }
        }

        return entries.Count == 0 ? "-" : string.Join(";", entries);
    }

    private string BuildTaskQueueSample()
        => _f3TaskQueue.Count == 0
            ? "-"
            : string.Join(",", _f3TaskQueue.Take(5).Select(static task => task.DelayFrames == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{task.Pc:X6}/p{task.Priority}")
                : string.Create(CultureInfo.InvariantCulture, $"{task.Pc:X6}/p{task.Priority}:{task.DelayFrames}")));

    private static string BuildRecentTaskSample(uint[] ring, int nextIndex)
    {
        string[] entries = new string[ring.Length];
        int count = 0;
        for (int i = 0; i < ring.Length; i++)
        {
            uint pc = ring[(nextIndex + i) & (ring.Length - 1)];
            if (pc == 0)
                continue;
            entries[count++] = pc.ToString("X6", CultureInfo.InvariantCulture);
        }

        return count == 0 ? "-" : string.Join(",", entries[..count].ToArray());
    }

    public string MissingDevices =>
        "missing: real M68EC020 core, full F3 trap scheduler, TC0630FDP priorities/blending, full F3 sprite generator, persistent EEPROM/NVRAM, full watchdog reset outside scheduler stub, ES5510 DSP effects.";

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string name = GetDriverName(path);
        if (SupportedDrivers.Contains(name))
            return true;

        return LooksLikeDariusGaidenArchive(path);
    }

    public static bool IsSupportedDriverName(string driverName)
        => SupportedDrivers.Contains(driverName);

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Darius Gaiden ROM path is empty.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Darius Gaiden ROM archive not found.", path);
        if (!IsSupportedArchive(path))
            throw new NotSupportedException($"'{Path.GetFileName(path)}' is not recognized as a Taito F3 Darius Gaiden MAME set.");

        _driverName = GetDriverName(path);
        if (!SupportedDrivers.Contains(_driverName))
            _driverName = "dariusg";

        TaitoF3RomSet roms = TaitoF3RomSet.Load(path);
        _roms = roms;
        _bus.Load(roms);
        _sound.Load(roms.SoundCpu, roms.Ensoniq, _bus);
        _mainCpu.Reset(_bus);
        _loaded = true;
        _cpuFaulted = false;
        _adaptiveRenderSkipsRemaining = 0;
        _hasPresentFrame = false;
        _frameCounter = 0;
        InvalidateDebugSummary();
        _executedInstructions = 0;
        _executedCycles = 0;
        _m68ec020ProbeInstructions = 0;
        _f3TasksEnqueued = 0;
        _f3TasksDispatched = 0;
        _lastF3TaskEntry = 0;
        _lastF3TaskStack = 0;
        _lastF3TrapPc = 0;
        _lastRecoveredInvalidPc = 0;
        _lastPcBeforeRecoveredInvalidPc = 0;
        _lastOpBeforeRecoveredInvalidPc = 0;
        _currentF3TaskPriority = 0;
        ResetSceneDiagnostics();
        _nextF3TaskStack = 0x0041_f000;
        _traceInstructionsRemaining = TraceInstructionLimit;
        _traceBootPcRemaining = TraceBootPcLimit;
        _traceSceneCopyPcRemaining = TraceSceneCopyPcLimit;
        _traceTaskCreatePcRemaining = TraceTaskCreatePcLimit;
        _f3TaskQueue.Clear();
        ResetVideoRuntime();
        _lastStopReason = "reset";
        _romIdentity = new RomIdentity(_driverName, BuildRomHash(roms.MainCpu), Path.GetDirectoryName(Path.GetFullPath(path)));
        UpdateRomInfo(path);
        RenderUglyVideo();

        Console.WriteLine($"[DARIUSG] load driver={_driverName} reset_sp=0x{_bus.ReadLong(0):X8} reset_pc=0x{_bus.ReadLong(4):X8} {MissingDevices}");
    }

    public void Reset()
    {
        if (!_loaded)
            return;

        _bus.ResetRuntime();
        _sound.Reset(asserted: true);
        _mainCpu.Reset(_bus);
        _cpuFaulted = false;
        _hasPresentFrame = false;
        _f3TasksEnqueued = 0;
        _f3TasksDispatched = 0;
        _lastF3TaskEntry = 0;
        _lastF3TaskStack = 0;
        _lastF3TrapPc = 0;
        _lastRecoveredInvalidPc = 0;
        _lastPcBeforeRecoveredInvalidPc = 0;
        _lastOpBeforeRecoveredInvalidPc = 0;
        _currentF3TaskPriority = 0;
        ResetSceneDiagnostics();
        _nextF3TaskStack = 0x0041_f000;
        _lastStopReason = "reset";
        InvalidateDebugSummary();
        _traceBootPcRemaining = TraceBootPcLimit;
        _traceInstructionsRemaining = TraceInstructionLimit;
        _traceSceneCopyPcRemaining = TraceSceneCopyPcLimit;
        _traceTaskCreatePcRemaining = TraceTaskCreatePcLimit;
        _f3TaskQueue.Clear();
        ResetVideoRuntime();
        RenderUglyVideo();
    }

    private void ResetVideoRuntime()
    {
        _spriteBank = false;
        _spriteTrails = false;
        _spritePenMask = 0x0f;
        _sprites.Clear();
        _latchedSprites.Clear();
        Array.Clear(_spriteReefPalette);
        Array.Clear(_spriteReefGroup);
        Array.Clear(_spriteReefRowActive);
        _spriteReefOffsetCount = 0;
        _spriteReefTouchedRows.Clear();
    }

    public void RunFrame()
    {
        if (!_loaded)
            return;

        long profileStartTicks = TracePhaseTiming ? Stopwatch.GetTimestamp() : 0;
        long controlTicks = 0;
        long cpuTicks = 0;
        long renderTicks = 0;
        long frameStartTicks = AdaptiveRenderPacing && RenderDivisor <= 1
            ? Stopwatch.GetTimestamp()
            : 0;
        _frameCounter++;
        long controlStartTicks = profileStartTicks != 0 ? Stopwatch.GetTimestamp() : 0;
        _bus.BeginFrameInterrupt();
        if (!UseNativeF3TrapScheduler && _f3TasksEnqueued == 0)
            _bus.PulseBootSchedulerGate();
        _bus.RefreshInputLatches();
        if (controlStartTicks != 0)
            controlTicks += Stopwatch.GetTimestamp() - controlStartTicks;
        if (_cpuFaulted)
        {
            DrawBringupFrame();
            InvalidateDebugSummary();
            return;
        }

        int cycles = 0;
        int instructions = 0;
        long cpuStartTicks = profileStartTicks != 0 ? Stopwatch.GetTimestamp() : 0;
        if (TracePcProfile)
            _framePcProfile.Clear();
        try
        {
            int cycleBudget = (int)(MainClockHz / TargetFps) * CpuScale;
            int instructionBudget = MainInstructionLimitPerFrame * CpuScale;
            while (cycles < cycleBudget && instructions < instructionBudget)
            {
                if (_bus.ConsumeWatchdogSoftReset())
                {
                    SoftResetF3Machine("watchdog");
                    cycles += 518;
                    _executedCycles += 518;
                    continue;
                }

                uint pc = _mainCpu.Pc;
                _bus.CurrentCpuPc = pc;
                ushort op = _mainCpu.NextOpcode;
                if (TracePcProfile)
                {
                    uint pcKey = pc & 0x00ff_ffff;
                    _framePcProfile[pcKey] = _framePcProfile.TryGetValue(pcKey, out int pcHits) ? pcHits + 1 : 1;
                }
                TrackSceneDiagnosticPc(pc);
                if (UseNativeF3TrapScheduler && IsNativeF3IdleSpin(pc, op))
                {
                    uint idleCycles = _bus.CyclesUntilInterrupt3Ready;
                    if (idleCycles != 0)
                    {
                        cycles += (int)idleCycles;
                        _bus.AdvanceMainCycles(idleCycles);
                        _executedCycles += idleCycles;
                        _lastStopReason = "f3 idle fast-forward irq3";
                        continue;
                    }

                    if (_bus.CanEndFrameAtIdle)
                    {
                        _lastStopReason = "f3 idle";
                        break;
                    }
                }
                if (TryRecoverInvalidF3ProgramCounter(pc, out uint recoverCycles))
                {
                    cycles += (int)recoverCycles;
                    _bus.AdvanceMainCycles(recoverCycles);
                    _executedCycles += recoverCycles;
                    _m68ec020ProbeInstructions++;
                    _lastStopReason = $"recovered pc=0x{pc:X6}";
                    if (recoverCycles < (uint)cycleBudget / 8)
                        continue;
                    break;
                }
                if (TraceBootPc && _traceBootPcRemaining > 0 && ShouldTraceDariusBootPc(pc))
                {
                    _traceBootPcRemaining--;
                    var state = _mainCpu.GetState();
                    uint sp = (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp;
                    Console.WriteLine(
                        $"[DARIUSG-PC] f={_frameCounter} i={instructions} pc=0x{pc:X6} op=0x{op:X4} " +
                        $"d0=0x{state.Data[0]:X8} d1=0x{state.Data[1]:X8} a0=0x{state.Address[0]:X8} a1=0x{state.Address[1]:X8} a2=0x{state.Address[2]:X8} a3=0x{state.Address[3]:X8} " +
                        $"sp=0x{sp:X8} st0=0x{_bus.ReadLong(sp):X8} st4=0x{_bus.ReadLong(sp + 4):X8} " +
                        $"tasks={_f3TaskQueue.Count} enq={_f3TasksEnqueued} run={_f3TasksDispatched}");
                }
                if (TraceSceneCopyPc && _traceSceneCopyPcRemaining > 0 && pc == 0x001364)
                {
                    _traceSceneCopyPcRemaining--;
                    var state = _mainCpu.GetState();
                    Console.WriteLine(
                        $"[DARIUSG-COPY] f={_frameCounter} i={instructions} pc=0x{pc:X6} op=0x{op:X4} " +
                        $"d0=0x{state.Data[0]:X8} d1=0x{state.Data[1]:X8} d2=0x{state.Data[2]:X8} d3=0x{state.Data[3]:X8} d4=0x{state.Data[4]:X8} d5=0x{state.Data[5]:X8} d6=0x{state.Data[6]:X8} " +
                        $"a0=0x{state.Address[0]:X8} a1=0x{state.Address[1]:X8} a2=0x{state.Address[2]:X8} a3=0x{state.Address[3]:X8} a4=0x{state.Address[4]:X8} a5=0x{state.Address[5]:X8} a6=0x{state.Address[6]:X8} " +
                        $"src1e=0x{_bus.PeekWord(state.Address[0] + 0x1e):X4} dst2=0x{((state.Address[1] + 2) & 0x00ff_ffff):X6} a1w=0x{_bus.PeekWord(state.Address[1] & 0x00ff_ffff):X4}");
                }
                if (TraceTaskCreatePc && _traceTaskCreatePcRemaining > 0 && ShouldTraceF3TaskCreatePc(pc))
                {
                    _traceTaskCreatePcRemaining--;
                    TraceF3TaskCreatePc(instructions, pc, op);
                }
                if (Trace && _traceInstructionsRemaining > 0)
                {
                    _traceInstructionsRemaining--;
                    Console.WriteLine($"[DARIUSG-020] f={_frameCounter} i={instructions} pc=0x{pc:X6} op=0x{op:X4} sr=0x{_mainCpu.StatusRegister:X4}");
                }
                if (TraceHotFill && _traceHotFillRemaining > 0 && pc >= 0x005880 && pc <= 0x005896)
                {
                    _traceHotFillRemaining--;
                    var hotState = _mainCpu.GetState();
                    Console.WriteLine(
                        $"[DARIUSG-HOTFILL] f={_frameCounter} i={instructions} c={cycles} pc=0x{pc:X6} op=0x{op:X4} " +
                        $"d0=0x{hotState.Data[0]:X8} d1=0x{hotState.Data[1]:X8} a0=0x{hotState.Address[0]:X8} next=0x{_bus.PeekWord(pc + 2):X4}");
                }

                uint used = TryExecuteM68ec020ProbeInstruction(pc, op, out uint probeCycles)
                    ? probeCycles
                    : _mainCpu.ExecuteInstruction(_bus);
                _bus.VectorBase = _mainCpu.VectorBase;
                cycles += Math.Max(1, (int)used);
                _bus.AdvanceMainCycles(Math.Max(1, used));
                instructions++;
                _executedCycles += used;
                _executedInstructions++;
                _lastPcBeforeRecoveredInvalidPc = pc;
                _lastOpBeforeRecoveredInvalidPc = op;

                if (_mainCpu.IsStopped || _mainCpu.IsFrozen)
                {
                    _lastStopReason = _mainCpu.IsStopped ? "cpu stopped" : "cpu frozen";
                    break;
                }

                if (_mainCpu.Pc == pc && _mainCpu.NextOpcode == op && used <= 1)
                {
                    _lastStopReason = $"stalled pc=0x{pc:X6} op=0x{op:X4}";
                    break;
                }
            }

            if (instructions >= instructionBudget)
                _lastStopReason = "instruction budget";
            else if (cycles >= cycleBudget)
                _lastStopReason = "frame budget";
        }
        catch (Exception ex)
        {
            _cpuFaulted = true;
            _lastStopReason = $"{ex.GetType().Name}: {ex.Message}";
            Console.Error.WriteLine($"[DARIUSG-020] fault frame={_frameCounter} pc=0x{_mainCpu.Pc:X6} op=0x{_mainCpu.NextOpcode:X4} {ex}");
        }
        if (cpuStartTicks != 0)
            cpuTicks = Stopwatch.GetTimestamp() - cpuStartTicks;

        if (Trace || TraceSummary)
            Console.WriteLine($"[DARIUSG] {DebugSummary}");

        controlStartTicks = profileStartTicks != 0 ? Stopwatch.GetTimestamp() : 0;
        if (_f3TasksEnqueued != 0)
            _bus.RefreshInputLatches();
        if (controlStartTicks != 0)
            controlTicks += Stopwatch.GetTimestamp() - controlStartTicks;

        bool renderedFrame = ShouldRenderThisFrame();
        if (renderedFrame)
        {
            long renderStartTicks = profileStartTicks != 0 ? Stopwatch.GetTimestamp() : 0;
            RenderUglyVideo();
            if (renderStartTicks != 0)
                renderTicks = Stopwatch.GetTimestamp() - renderStartTicks;
        }
        _sound.RunFrame(!_bus.SoundCpuResetAsserted, _bus.DualPortWriteSerial);

        if (frameStartTicks != 0)
            UpdateAdaptiveRenderPacing(Stopwatch.GetTimestamp() - frameStartTicks);

        _lastFrameInstructions = instructions;
        _lastFrameCycles = cycles;
        _lastFrameCpuTicks = cpuTicks;
        _lastFrameRenderTicks = renderTicks;
        _lastFrameControlTicks = controlTicks;
        _lastFrameTotalTicks = profileStartTicks != 0 ? Stopwatch.GetTimestamp() - profileStartTicks : 0;
        InvalidateDebugSummary();

        if (TracePhaseTiming && _lastFrameTotalTicks > TargetFrameTicks)
        {
            double tickMs = 1000.0 / Stopwatch.Frequency;
            Console.WriteLine(
                $"[DARIUSG-PHASE] f={_frameCounter} total={_lastFrameTotalTicks * tickMs:0.###}ms " +
                $"cpu={cpuTicks * tickMs:0.###}ms render={renderTicks * tickMs:0.###}ms ctrl={controlTicks * tickMs:0.###}ms " +
                $"instr={instructions} cycles={cycles} rendered={(renderedFrame ? 1 : 0)} stop={_lastStopReason} pc=0x{_mainCpu.Pc:X6} irq={_bus.InterruptLevel()}");
            if (TracePcProfile)
                Console.WriteLine($"[DARIUSG-PCPROFILE] f={_frameCounter} {BuildFramePcProfileSample()}");
        }
    }

    private string BuildFramePcProfileSample()
    {
        if (_framePcProfile.Count == 0)
            return "-";

        return string.Join(",",
            _framePcProfile
                .OrderByDescending(static pair => pair.Value)
                .Take(12)
                .Select(static pair => $"0x{pair.Key:X6}:{pair.Value}"));
    }

    private bool ShouldRenderThisFrame()
    {
        if (RenderDivisor > 1 && _frameCounter > 3 && (_frameCounter % RenderDivisor) != 0)
            return false;

        if (AdaptiveRenderPacing && RenderDivisor <= 1 && _adaptiveRenderSkipsRemaining > 0 && _hasPresentFrame)
        {
            _adaptiveRenderSkipsRemaining--;
            return false;
        }

        return true;
    }

    private void UpdateAdaptiveRenderPacing(long elapsedTicks)
    {
        if (!_hasPresentFrame)
            return;

        long highWaterTicks = TargetFrameTicks + (TargetFrameTicks / 10);
        if (elapsedTicks > highWaterTicks)
            _adaptiveRenderSkipsRemaining = 1;
    }

    private void SoftResetF3Machine(string reason)
    {
        _mainCpu.Reset(_bus);
        _f3TaskQueue.Clear();
        _currentF3TaskPriority = 0;
        _nextF3TaskStack = 0x0041_f000;
        ResetSceneDiagnostics();
        ResetVideoRuntime();
        _lastStopReason = $"{reason} reset";
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = FrameWidth;
        height = FrameHeight;
        stride = FrameStride;
        return _hasPresentFrame ? _presentFrameBuffer : _frameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
        => _sound.GetAudioBuffer(out sampleRate, out channels, _masterVolumePercent);

    public void SetMasterVolumePercent(int percent)
    {
        _masterVolumePercent = Math.Clamp(percent, 0, 200);
    }

    public double GetTargetFps() => TargetFps;

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
        _bus.Input = new TaitoF3InputState(up, down, left, right, a, b, c, start, x, y, z, coin1: mode);
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write("DARIUSG");
        writer.Write(12);
        writer.Write(_frameCounter);
        writer.Write(_executedInstructions);
        writer.Write(_executedCycles);
        writer.Write(_m68ec020ProbeInstructions);
        writer.Write(_cpuFaulted);
        writer.Write(_lastStopReason);
        _bus.SaveState(writer);
        var state = GetScratchMainCpuState();
        writer.Write(state.Pc);
        writer.Write(state.Ssp);
        writer.Write(state.Usp);
        writer.Write(state.Sr);
        writer.Write(state.Prefetch);
        for (int i = 0; i < 8; i++) writer.Write(state.Data[i]);
        for (int i = 0; i < 7; i++) writer.Write(state.Address[i]);
        _sound.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader.ReadString() != "DARIUSG")
            throw new InvalidDataException("Not a Darius Gaiden bringup savestate.");
        int version = reader.ReadInt32();
        if (version < 3 || version > 12)
            throw new InvalidDataException($"Unsupported Darius Gaiden bringup savestate version {version}.");

        _frameCounter = reader.ReadInt64();
        _executedInstructions = reader.ReadUInt64();
        _executedCycles = reader.ReadUInt64();
        _m68ec020ProbeInstructions = reader.ReadUInt64();
        _cpuFaulted = reader.ReadBoolean();
        _lastStopReason = reader.ReadString();
        _bus.LoadState(reader, version);
        uint pc = reader.ReadUInt32();
        uint ssp = reader.ReadUInt32();
        uint usp = reader.ReadUInt32();
        ushort sr = reader.ReadUInt16();
        ushort prefetch = reader.ReadUInt16();
        uint[] data = new uint[8];
        uint[] address = new uint[7];
        for (int i = 0; i < data.Length; i++) data[i] = reader.ReadUInt32();
        for (int i = 0; i < address.Length; i++) address[i] = reader.ReadUInt32();
        _mainCpu.SetState(new M68000.M68000State(data, address, usp, ssp, sr, pc, prefetch));
        _mainCpu.VectorBase = _bus.VectorBase;
        InvalidateDebugSummary();
        if (version >= 12)
            _sound.LoadState(reader);
        else
            _sound.SuspendLegacyState(_bus.SoundCpuResetAsserted);
        RenderUglyVideo();
    }

    public void Dispose()
    {
    }

    private void RenderUglyVideo()
    {
        TaitoF3RomSet? roms = _roms;
        if (roms == null)
        {
            ClearWithPalette(0);
            return;
        }

        ClearWithPalette(0, clearFrameBuffer: false);
        BuildMameLineStates();
        InitializeMameLineBackgrounds();

        bool drewAny = false;
        drewAny |= RenderPlayfields(roms);
        drewAny |= RenderPivotPixelLayer(roms);
        drewAny |= RenderSprites(roms);
        drewAny |= RenderTextLayer(roms);

        if (!drewAny)
            ClearWithPalette(0);
        else
            RenderMameMixBufferToFrame();

        LatchPresentFrameIfUseful(drewAny);
    }

    private void LatchPresentFrameIfUseful(bool drewAny)
    {
        if (!drewAny && _hasPresentFrame)
            return;

        Buffer.BlockCopy(_frameBuffer, 0, _presentFrameBuffer, 0, _frameBuffer.Length);
        _hasPresentFrame = true;
    }

    private bool FrameHasVisibleDetail()
    {
        uint first = ReadFramePixel(0);
        int different = 0;
        int nonTransparent = 0;
        int step = 32 * 4;
        for (int offset = 0; offset < _frameBuffer.Length; offset += step)
        {
            uint pixel = ReadFramePixel(offset);
            if ((pixel >> 24) != 0)
                nonTransparent++;
            if (pixel != first && ++different >= 4)
                return true;
        }

        return nonTransparent > 0 && !_hasPresentFrame;
    }

    private uint ReadFramePixel(int offset)
    {
        return (uint)(_frameBuffer[offset + 0]
            | (_frameBuffer[offset + 1] << 8)
            | (_frameBuffer[offset + 2] << 16)
            | (_frameBuffer[offset + 3] << 24));
    }

    private bool TryExecuteM68ec020ProbeInstruction(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc is 0x004d06 or 0x004d18 or 0x0050dc)
            _bus.EnsureBackupDefaults();

        if (TryBypassKnownFioSelfTestFatal(pc, out cycles))
            return true;
        if (TryBypassBackupSettingsGate(pc, op, out cycles))
            return true;
        if (TryForceBackupCheckResult(pc, op, out cycles))
            return true;
        if (TryForceInitialTaskBackupOk(pc, op, out cycles))
            return true;
        if (TryBypassBackupRamInitReset(pc, op, out cycles))
            return true;
        // Exact loop collapses for Darius' hot RAM/video fill routines. These
        // keep native trap scheduling intact while avoiding thousands of tiny
        // 020 memory operations per frame.
        if (TryExecuteDariusF3IrqTaskScan(pc, op, out cycles))
            return true;
        if (TryExecuteDariusF3TaskMaskScan(pc, op, out cycles))
            return true;
        if (TryExecuteDariusF3ZeroWait(pc, op, out cycles))
            return true;
        if (TryExecuteDariusIndexedLongCopyLoop(pc, op, out cycles))
            return true;
        if (TryExecuteDariusTilemapRleDraw(pc, op, out cycles))
            return true;
        if (TryExecuteDariusBytePairExpand(pc, op, out cycles))
            return true;
        if (TryExecuteDariusObjectPackLoop(pc, op, out cycles))
            return true;
        if (TryExecuteDariusSceneSpriteProlog(pc, op, out cycles))
            return true;
        if (TryExecuteDariusStaticSpriteCopyEntry(pc, op, out cycles))
            return true;
        if (TryExecuteDariusSceneSpriteRows(pc, op, out cycles))
            return true;
        if (TryExecuteDariusSceneSpriteCopy(pc, op, out cycles))
            return true;
        if (TryExecuteDariusStaticSpriteCopy(pc, op, out cycles))
            return true;
        if (TryExecuteDariusEmptySceneSpriteScan(pc, op, out cycles))
            return true;
        if (TryExecuteDariusObjectTrailShift(pc, op, out cycles))
            return true;
        if (TryExecuteDariusObjectAnimPointerLoad(pc, op, out cycles))
            return true;
        if (TryExecuteDariusTimedObjectAnimPointerLoad(pc, op, out cycles))
            return true;
        if (TryExecuteDariusObjectAnimationStep(pc, op, out cycles))
            return true;
        if (TryExecuteDariusPaletteLerpTable(pc, op, out cycles))
            return true;
        if (TryExecuteDariusSpriteReefClear(pc, out cycles))
            return true;
        if (TrySkipEmptySpriteControlSlots(pc, op, out cycles))
            return true;
        if (TryExecuteDariusWorkRamCurrent(pc, op, out cycles))
            return true;
        if (TryExecuteDariusBootRamClear(pc, op, out cycles))
            return true;
        if (TryExecuteDariusF3MemoryTide(pc, op, out cycles))
            return true;
        if (TryExecuteDariusPaletteCurrent(pc, op, out cycles))
            return true;
        if (!UseNativeF3TrapScheduler && TryExecuteBtstImmediateByteDisplacement(pc, op, out cycles))
            return true;
        if (TryExecuteF3SchedulerYieldEntry(pc, op, out cycles))
            return true;
        if (TryExecuteF3TrapSchedulerStub(pc, op, out cycles))
            return true;
        if (TryDispatchF3QueuedTask(pc, op, out cycles))
            return true;

        if (!UseNativeF3TrapScheduler)
        {
            if ((op & 0xfff8) == 0xe9c0)
                return TryExecuteBfextuDataRegister(pc, op, out cycles);
            if ((op & 0xfff8) == 0xe9e8)
                return TryExecuteBfextuDisplacement(pc, op, out cycles);
            if ((op & 0xffc0) == 0xecc0)
                return TryExecuteBfclr(pc, op, out cycles);
            if ((op & 0xffc0) == 0xedc0)
                return TryExecuteBfffo(pc, op, out cycles);
            if ((op & 0xffc0) == 0xeec0)
                return TryExecuteBfset(pc, op, out cycles);
            if ((op & 0xfff8) == 0xefc0)
                return TryExecuteBfinsDataRegister(pc, op, out cycles);
            if ((op & 0xfff8) == 0xefe8)
                return TryExecuteBfinsDisplacement(pc, op, out cycles);
            if ((op & 0xfff8) == 0xeff0)
                return TryExecuteBfinsIndexed(pc, op, out cycles);
        }
        if (!UseNativeF3TrapScheduler)
        {
            if ((op & 0xfff8) == 0x49c0)
                return TryExecuteExtByteToLong(pc, op, out cycles);
            if ((op & 0xf038) == 0x5028 && ((op >> 6) & 3) != 3)
                return TryExecuteAddSubQuickDisplacement(pc, op, out cycles);
            if ((op & 0xf1f8) == 0xd070)
                return TryExecuteAddWordIndexedToData(pc, op, out cycles);
        }
        return false;
    }

    private static bool IsNativeF3IdleSpin(uint pc, ushort op)
        => pc is 0x002320 or 0x002326
            && op is 0x4eb9 or 0x60f8;

    private bool TryExecuteDariusF3IrqTaskScan(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x002284 || op != 0x3880
            || _bus.PeekWord(0x002284) != 0x3880
            || _bus.PeekWord(0x002286) != 0x3200
            || _bus.PeekWord(0x002288) != 0xe541
            || _bus.PeekWord(0x00228a) != 0x47f4
            || _bus.PeekWord(0x00228e) != 0x2b4b
            || _bus.PeekWord(0x002292) != 0x2413
            || _bus.PeekWord(0x002294) != 0x262d
            || _bus.PeekWord(0x002298) != 0x0103
            || _bus.PeekWord(0x00229a) != 0x67d8
            || _bus.PeekWord(0x00229c) != 0x0802
            || _bus.PeekWord(0x0022de) != 0x4a2d)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        uint a4 = state.Address[4] & 0x00ff_ffff;
        uint a5 = state.Address[5] & 0x00ff_ffff;
        if (a4 != ((a5 + unchecked((uint)(short)0xe6fa)) & 0x00ff_ffff))
            return false;

        ushort d0 = (ushort)state.Data[0];
        uint maskAddress = (a5 + unchecked((uint)(short)0xe6fc)) & 0x00ff_ffff;
        uint pointerAddress = (a5 + unchecked((uint)(short)0xe704)) & 0x00ff_ffff;
        uint readyMask = _bus.ReadLong(maskAddress);
        int skipped = 0;

        while (d0 < 0x20)
        {
            ushort d1 = (ushort)(d0 << 2);
            uint a3 = (a4 + 0x0eu + d1) & 0x00ff_ffff;
            _bus.WriteWord(a4, d0);
            _bus.WriteLong(pointerAddress, a3);
            if (((readyMask >> d0) & 1u) != 0)
            {
                state.Data[0] = (state.Data[0] & 0xffff_0000u) | d0;
                state.Data[1] = (state.Data[1] & 0xffff_0000u) | d1;
                state.Data[2] = _bus.ReadLong(a3);
                state.Data[3] = readyMask;
                state.Address[3] = a3;
                state.Address[4] = a4;
                ushort sr = (ushort)(state.Sr & ~0x0004);
                uint nextPc = 0x00229c;
                _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, _bus.ReadOpcodeWord(nextPc)));
                _m68ec020ProbeInstructions += (ulong)(skipped * 9 + 8);
                cycles = (uint)Math.Max(24, skipped * 34 + 20);
                return skipped != 0;
            }

            d0++;
            skipped++;
        }

        if (skipped == 0)
            return false;

        state.Data[0] = (state.Data[0] & 0xffff_0000u) | 0x0020u;
        state.Data[1] = (state.Data[1] & 0xffff_0000u) | 0x007cu;
        state.Data[3] = readyMask;
        state.Address[4] = a4;
        uint noTaskPc = 0x0022de;
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, noTaskPc, _bus.ReadOpcodeWord(noTaskPc)));
        _m68ec020ProbeInstructions += (ulong)(skipped * 9);
        cycles = (uint)Math.Max(34, skipped * 34);
        return true;
    }

    private bool TryExecuteDariusF3TaskMaskScan(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x0104e2 || op != 0x0302
            || _bus.PeekWord(pc + 2) != 0x6600
            || _bus.PeekWord(pc + 4) != 0x000e
            || _bus.PeekWord(0x0104f4) != 0x41e8
            || _bus.PeekWord(0x0104f6) != 0x006c
            || _bus.PeekWord(0x0104f8) != 0x51c9)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        int bit = (short)(ushort)state.Data[1];
        if (bit < 0 || bit > 31)
            return false;

        uint mask = state.Data[2];
        int skipped = 0;
        while (bit - skipped >= 0 && ((mask >> (bit - skipped)) & 1u) != 0)
            skipped++;

        if (skipped == 0)
            return false;

        int nextBit = bit - skipped;
        state.Address[0] = (state.Address[0] + (uint)(skipped * 0x6c)) & 0x00ff_ffff;
        state.Data[1] = (state.Data[1] & 0xffff_0000u) | (ushort)(nextBit < 0 ? 0xffff : nextBit);
        ushort sr = (ushort)(state.Sr & ~0x0004);
        uint nextPc = nextBit < 0 ? 0x0104fcu : 0x0104e2u;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)(skipped * 4);
        cycles = (uint)Math.Max(34, skipped * 12);
        return true;
    }

    private bool TryExecuteDariusF3ZeroWait(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x001a44 || op != 0x4a2d || _bus.PeekWord(pc + 2) != 0xa2b3 || _bus.PeekWord(pc + 4) != 0x56c8)
            return false;

        var state = GetScratchMainCpuState();
        uint waitAddress = (state.Address[5] + unchecked((uint)(short)0xa2b3)) & 0x00ff_ffff;
        if (_bus.ReadByte(waitAddress) != 0)
            return false;

        int remaining = ((ushort)state.Data[0]) + 1;
        if (remaining <= 0 || remaining > 0x0800)
            return false;

        state.Data[0] = (state.Data[0] & 0xffff_0000u) | 0xffffu;
        ushort sr = (ushort)((state.Sr & 0xfff0) | 0x0004);
        uint nextPc = 0x001a4c;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)remaining;
        cycles = (uint)Math.Max(34, remaining * 6);
        return true;
    }

    private bool TryExecuteDariusIndexedLongCopyLoop(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x001648 || op != 0x219b
            || _bus.PeekWord(0x001648) != 0x219b
            || _bus.PeekWord(0x00164a) != 0x1000
            || _bus.PeekWord(0x00164c) != 0x5841
            || _bus.PeekWord(0x00164e) != 0x51ca
            || _bus.PeekWord(0x001650) != 0xfff8)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        int remaining = ((ushort)state.Data[2]) + 1;
        if (remaining <= 0 || remaining > 0x0400)
            return false;

        uint baseAddress = state.Address[0] & 0x00ff_ffff;
        uint source = state.Address[3] & 0x00ff_ffff;
        ushort d1 = (ushort)state.Data[1];
        ushort oldD1 = d1;
        ushort resultD1 = d1;
        for (int i = 0; i < remaining; i++)
        {
            uint destination = (baseAddress + unchecked((uint)(short)d1)) & 0x00ff_ffff;
            uint value = _bus.ReadLong(source);
            _bus.WriteLong(destination, value);
            source = (source + 4) & 0x00ff_ffff;
            oldD1 = d1;
            resultD1 = (ushort)(d1 + 4);
            d1 = resultD1;
        }

        bool negative = (resultD1 & 0x8000) != 0;
        bool zero = resultD1 == 0;
        bool overflow = ((oldD1 ^ resultD1) & 0x8000) != 0 && (oldD1 & 0x8000) == 0;
        bool carry = oldD1 > resultD1;
        state.Address[3] = source;
        state.Data[1] = (state.Data[1] & 0xffff_0000u) | resultD1;
        state.Data[2] = (state.Data[2] & 0xffff_0000u) | 0xffffu;
        ushort sr = UpdateAddSubCcr(state.Sr, negative, zero, overflow, carry);
        uint nextPc = 0x001652;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)(remaining * 3);
        cycles = (uint)Math.Max(34, remaining * 24);
        return true;
    }

    private bool TryExecuteDariusBytePairExpand(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x00567c || op != 0x1018
            || _bus.PeekWord(0x00567c) != 0x1018
            || _bus.PeekWord(0x00567e) != 0x6706
            || _bus.PeekWord(0x005680) != 0x12c7
            || _bus.PeekWord(0x005682) != 0x12c0
            || _bus.PeekWord(0x005684) != 0x60f6
            || _bus.PeekWord(0x005686) != 0x2009)
        {
            return false;
        }

        var state = _mainCpu.GetState();
        uint source = state.Address[0] & 0x00ff_ffff;
        uint destination = state.Address[1] & 0x00ff_ffff;
        byte prefix = (byte)state.Data[7];
        int copied = 0;
        while (copied < 0x4000)
        {
            byte value = _bus.ReadByte(source);
            source = (source + 1) & 0x00ff_ffff;
            state.Data[0] = (state.Data[0] & 0xffff_ff00u) | value;
            if (value == 0)
                break;

            _bus.WriteByte(destination, prefix);
            _bus.WriteByte(destination + 1, value);
            destination = (destination + 2) & 0x00ff_ffff;
            copied++;
        }

        if (copied >= 0x4000)
            return false;

        state.Address[0] = source;
        state.Address[1] = destination;
        ushort sr = UpdateCcr(state.Sr, negative: false, zero: true, overflow: false, carry: false);
        uint nextPc = 0x005686;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)(copied * 5 + 2);
        cycles = (uint)Math.Max(34, copied * 18 + 12);
        return true;
    }

    private bool TryExecuteDariusTilemapRleDraw(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x0053b2 || op != 0x48e7
            || _bus.PeekWord(0x0053b2) != 0x48e7
            || _bus.PeekWord(0x0053b4) != 0xffe0
            || _bus.PeekWord(0x0053b6) != 0x206f
            || _bus.PeekWord(0x0053ba) != 0x7e00
            || _bus.PeekWord(0x0053bc) != 0x1e18
            || _bus.PeekWord(0x0053be) != 0x5347
            || _bus.PeekWord(0x0053c0) != 0x4847
            || _bus.PeekWord(0x0053c2) != 0x1e18
            || _bus.PeekWord(0x0053c4) != 0x5347
            || _bus.PeekWord(0x0053c6) != 0x2c2f
            || _bus.PeekWord(0x0053ca) != 0x2246
            || _bus.PeekWord(0x0053cc) != 0x262f
            || _bus.PeekWord(0x0053d0) != 0xe9c3
            || _bus.PeekWord(0x0053d4) != 0x45fa
            || _bus.PeekWord(0x0053dc) != 0x4e92
            || _bus.PeekWord(0x0053de) != 0x0283
            || _bus.PeekWord(0x0053e4) != 0x45fa
            || _bus.PeekWord(0x00540c) != 0x727c
            || _bus.PeekWord(0x00540e) != 0xc841
            || _bus.PeekWord(0x005410) != 0x4ed2
            || _bus.PeekWord(0x00542a) != 0x3018
            || _bus.PeekWord(0x00542c) != 0x6600
            || _bus.PeekWord(0x005456) != 0x42b1
            || _bus.PeekWord(0x00546c) != 0x2382
            || _bus.PeekWord(0x005482) != 0x2382
            || _bus.PeekWord(0x00549a) != 0x2382)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        uint sp = (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp;
        uint source = _bus.ReadLong(sp + 4) & 0x00ff_ffff;
        uint destination = _bus.ReadLong(sp + 8) & 0x00ff_ffff;
        uint control = _bus.ReadLong(sp + 12);
        if (!IsF3VideoAddress(destination))
            return false;

        int rows = _bus.ReadByte(source);
        int columns = _bus.ReadByte(source + 1);
        source = (source + 2) & 0x00ff_ffff;
        if (rows <= 0 || rows > 0x80 || columns <= 0 || columns > 0x80)
            return false;

        uint d7 = ((uint)(rows - 1) << 16) | (ushort)(columns - 1);
        uint d5;
        uint d6 = destination;
        switch ((control >> 30) & 3)
        {
            case 0:
                d5 = 0x0080_0000u | (ushort)d6;
                d6 = ((uint)(ushort)d6 << 16) | 0x0004u;
                break;
            case 1:
                d5 = 0x0080_0000u | (ushort)d6;
                d6 = (d6 & 0xffff_0000u) | (ushort)(d6 + (uint)((ushort)d7 << 2));
                d6 = ((uint)(ushort)d6 << 16) | 0xfffcu;
                break;
            case 2:
                d5 = 0xff80_0000u | (ushort)d6;
                d5 = (d5 & 0xffff_0000u) | (ushort)(d5 + ((d7 >> 9) & 0x0fffu));
                d6 = ((uint)(ushort)d6 << 16) | 0x0004u;
                break;
            default:
                d5 = 0xff80_0000u | (ushort)d6;
                d5 = (d5 & 0xffff_0000u) | (ushort)(d5 + ((d7 >> 9) & 0x0fffu));
                d6 = (d6 & 0xffff_0000u) | (ushort)(d6 + (uint)((ushort)d7 << 2));
                d6 = ((uint)(ushort)d6 << 16) | 0xfffcu;
                break;
        }

        uint xorMask = control & 0xfe00_0000u;
        uint rowBase = destination;
        int repeatMode = 0;
        int repeatRemaining = 0;
        uint repeatValue = 0;
        int cells = rows * columns;

        for (int row = 0; row < rows; row++)
        {
            ushort rowOffset = (ushort)(d5 & 0x0f80u);
            rowBase = (rowBase & 0xffff_f000u) + rowOffset;
            if (!IsF3VideoAddress(rowBase))
                return false;

            ushort columnOffset = (ushort)(d6 >> 16);
            for (int column = 0; column < columns; column++)
            {
                columnOffset &= 0x007c;
                uint writeAddress = (rowBase + columnOffset) & 0x00ff_ffff;
                if (!IsF3VideoAddress(writeAddress))
                    return false;

                if (repeatMode == 0)
                {
                    ushort command = _bus.ReadWord(source);
                    source = (source + 2) & 0x00ff_ffff;
                    if (command == 0)
                    {
                        uint value = _bus.ReadLong(source) ^ xorMask;
                        source = (source + 4) & 0x00ff_ffff;
                        _bus.WriteLong(writeAddress, value);
                    }
                    else
                    {
                        repeatMode = (command >> 8) & 0xff;
                        repeatRemaining = (command & 0xff) + 1;
                        if (repeatMode > 3)
                            return false;
                        if (repeatMode != 0)
                        {
                            repeatValue = _bus.ReadLong(source) ^ xorMask;
                            source = (source + 4) & 0x00ff_ffff;
                        }

                        WriteDariusTilemapRleRepeat(writeAddress, repeatMode, ref repeatValue);
                        if (--repeatRemaining == 0)
                            repeatMode = 0;
                    }
                }
                else
                {
                    WriteDariusTilemapRleRepeat(writeAddress, repeatMode, ref repeatValue);
                    if (--repeatRemaining == 0)
                        repeatMode = 0;
                }

                columnOffset = (ushort)(columnOffset + (ushort)d6);
            }

            d5 = (d5 & 0xffff_0000u) | (ushort)(d5 + (d5 >> 16));
        }

        SetStateAfterRts(state, state.Sr);
        _m68ec020ProbeInstructions += (ulong)(cells * 5 + 24);
        cycles = (uint)Math.Max(80, cells * 20 + 80);
        return true;
    }

    private void WriteDariusTilemapRleRepeat(uint address, int mode, ref uint value)
    {
        switch (mode)
        {
            case 0:
                _bus.WriteLong(address, 0);
                break;
            case 1:
                _bus.WriteLong(address, value);
                break;
            case 2:
                _bus.WriteLong(address, value);
                value = (value & 0xffff_0000u) | (ushort)(value + 1);
                break;
            case 3:
                _bus.WriteLong(address, value);
                value = (value & 0xffff_0000u) | (ushort)(value - 1);
                break;
        }
    }

    private bool TryExecuteDariusObjectPackLoop(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x00173e || op != 0x7000
            || _bus.PeekWord(0x00173e) != 0x7000
            || _bus.PeekWord(0x001740) != 0x322a
            || _bus.PeekWord(0x001744) != 0xd352
            || _bus.PeekWord(0x001746) != 0x1012
            || _bus.PeekWord(0x001748) != 0xe188
            || _bus.PeekWord(0x00174a) != 0x322a
            || _bus.PeekWord(0x001752) != 0x102a
            || _bus.PeekWord(0x001756) != 0xe188
            || _bus.PeekWord(0x001758) != 0x322a
            || _bus.PeekWord(0x001760) != 0x102a
            || _bus.PeekWord(0x001764) != 0x26c0
            || _bus.PeekWord(0x001766) != 0x45ea
            || _bus.PeekWord(0x00176a) != 0x51ce)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        int count = ((ushort)state.Data[6]) + 1;
        if (count <= 0 || count > 0x0400)
            return false;

        uint source = state.Address[2] & 0x00ff_ffff;
        uint destination = state.Address[3] & 0x00ff_ffff;
        uint d0 = state.Data[0];
        uint d1 = state.Data[1];
        for (int i = 0; i < count; i++)
        {
            d0 = 0;

            ushort add0 = _bus.ReadWord(source + 6);
            d1 = (d1 & 0xffff_0000u) | add0;
            ushort pos0 = (ushort)(_bus.ReadWord(source) + add0);
            _bus.WriteWord(source, pos0);
            d0 = (d0 & 0xffff_ff00u) | _bus.ReadByte(source);
            d0 <<= 8;

            ushort add1 = _bus.ReadWord(source + 8);
            d1 = (d1 & 0xffff_0000u) | add1;
            ushort pos1 = (ushort)(_bus.ReadWord(source + 2) + add1);
            _bus.WriteWord(source + 2, pos1);
            d0 = (d0 & 0xffff_ff00u) | _bus.ReadByte(source + 2);
            d0 <<= 8;

            ushort add2 = _bus.ReadWord(source + 10);
            d1 = (d1 & 0xffff_0000u) | add2;
            ushort pos2 = (ushort)(_bus.ReadWord(source + 4) + add2);
            _bus.WriteWord(source + 4, pos2);
            d0 = (d0 & 0xffff_ff00u) | _bus.ReadByte(source + 4);

            _bus.WriteLong(destination, d0);
            destination = (destination + 4) & 0x00ff_ffff;
            source = (source + 12) & 0x00ff_ffff;
        }

        state.Data[0] = d0;
        state.Data[1] = d1;
        state.Data[6] = (state.Data[6] & 0xffff_0000u) | 0xffffu;
        state.Address[2] = state.Data[4] & 0x00ff_ffff;
        state.Address[3] = state.Data[5] & 0x00ff_ffff;
        ushort sr = UpdateCcr(state.Sr, (d0 & 0x8000_0000u) != 0, d0 == 0, overflow: false, carry: false);
        SetStateAfterRts(state, sr);
        _m68ec020ProbeInstructions += (ulong)(count * 15);
        cycles = (uint)Math.Max(34, count * 52);
        return true;
    }

    private bool TryExecuteDariusEmptySceneSpriteScan(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x001348 || op != 0x3292
            || _bus.PeekWord(pc + 2) != 0x6700
            || _bus.PeekWord(pc + 4) != 0x0046
            || _bus.PeekWord(0x001392) != 0x0884
            || _bus.PeekWord(0x001394) != 0x000c
            || _bus.PeekWord(0x001396) != 0x0884
            || _bus.PeekWord(0x001398) != 0x000d
            || _bus.PeekWord(0x00139a) != 0xd4c6
            || _bus.PeekWord(0x00139c) != 0x9445
            || _bus.PeekWord(0x00139e) != 0x51c8)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        int remaining = ((ushort)state.Data[0]) + 1;
        if (remaining <= 0 || remaining > 0x0400)
            return false;

        int sourceStep = unchecked((short)(ushort)state.Data[6]);
        if (sourceStep <= 0 || sourceStep > 0x0100)
            return false;

        uint source = state.Address[2] & 0x00ff_ffff;
        int skip = 0;
        while (skip < remaining && _bus.ReadWord(source + (uint)(skip * sourceStep)) == 0)
            skip++;

        if (skip == 0)
            return false;

        uint destination = state.Address[1] & 0x00ff_ffff;
        _bus.WriteWord(destination, 0);

        ushort oldD2 = (ushort)state.Data[2];
        ushort subtrahend = (ushort)(unchecked((short)(ushort)state.Data[5]) * skip);
        uint result = (uint)(oldD2 - subtrahend) & 0xffffu;
        state.Data[2] = (state.Data[2] & 0xffff_0000u) | result;
        state.Data[4] &= ~(uint)((1 << 12) | (1 << 13));
        state.Address[2] = (state.Address[2] + (uint)(skip * sourceStep)) & 0x00ff_ffff;
        state.Data[0] = (state.Data[0] & 0xffff_0000u) | (ushort)((remaining - skip - 1) & 0xffff);

        bool negative = (result & 0x8000u) != 0;
        bool zero = result == 0;
        bool overflow = ((oldD2 ^ subtrahend) & (oldD2 ^ result) & 0x8000u) != 0;
        bool carry = oldD2 < subtrahend;
        ushort sr = UpdateAddSubCcr(state.Sr, negative, zero, overflow, carry);
        uint nextPc = skip == remaining ? 0x0013a2u : 0x001348u;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)(skip * 7);
        cycles = (uint)Math.Max(34, skip * 50);
        return true;
    }

    private bool TryExecuteDariusSceneSpriteCopy(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x001348 || op != 0x3292
            || _bus.PeekWord(0x001348) != 0x3292
            || _bus.PeekWord(0x00134a) != 0x6700
            || _bus.PeekWord(0x00134c) != 0x0046
            || _bus.PeekWord(0x00134e) != 0x362a
            || _bus.PeekWord(0x001350) != 0x0002
            || _bus.PeekWord(0x001352) != 0xb943
            || _bus.PeekWord(0x001354) != 0x4a28
            || _bus.PeekWord(0x001356) != 0x001d
            || _bus.PeekWord(0x001364) != 0x3368
            || _bus.PeekWord(0x001366) != 0x001e
            || _bus.PeekWord(0x001368) != 0x0002
            || _bus.PeekWord(0x001382) != 0xe9c2
            || _bus.PeekWord(0x001384) != 0x340c
            || _bus.PeekWord(0x00139a) != 0xd4c6
            || _bus.PeekWord(0x00139c) != 0x9445
            || _bus.PeekWord(0x00139e) != 0x51c8)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        int count = ((ushort)state.Data[0]) + 1;
        if (count <= 0 || count > 0x0400)
            return false;

        int sourceStep = unchecked((short)(ushort)state.Data[6]);
        if (sourceStep <= 0 || sourceStep > 0x0100)
            return false;

        uint a0 = state.Address[0] & 0x00ff_ffff;
        uint a1 = state.Address[1] & 0x00ff_ffff;
        uint a2 = state.Address[2] & 0x00ff_ffff;
        if (!IsSpriteRamAddress(a1))
            return false;

        uint d2 = state.Data[2] & 0xffffu;
        uint d3 = state.Data[3];
        uint d4 = state.Data[4];
        ushort d5 = (ushort)state.Data[5];
        byte overrideByte = _bus.ReadByte(a0 + 0x1d);
        ushort spriteWord2 = _bus.ReadDataWord(a0 + 0x1e);
        ushort lastD2BeforeSub = (ushort)d2;

        for (int i = 0; i < count; i++)
        {
            ushort sourceWord = _bus.ReadDataWord(a2);
            _bus.WriteSpriteWordAddress(a1, sourceWord);
            if (sourceWord == 0)
            {
                d4 &= ~(uint)((1 << 12) | (1 << 13));
            }
            else
            {
                d3 = (d3 & 0xffff_0000u) | (ushort)(_bus.ReadDataWord(a2 + 2) ^ (ushort)d4);
                if (overrideByte != 0)
                    d3 = (d3 & 0xffff_ff00u) | overrideByte;

                _bus.WriteSpriteWordAddress(a1 + 8, (ushort)d3);
                _bus.WriteSpriteWordAddress(a1 + 2, spriteWord2);
                bool bit12WasSet = (d4 & (1u << 12)) != 0;
                d4 |= 1u << 12;
                if (!bit12WasSet)
                {
                    d4 |= (1u << 13) | (1u << 14) | (1u << 31);
                    d4 &= ~(1u << 15);
                    uint repeated = (d2 << 16) | d2;
                    d3 = (d3 & 0xffff_0000u) | ((repeated >> 4) & 0x0fffu);
                    _bus.WriteSpriteWordAddress(a1 + 6, (ushort)d3);
                }

                a1 = (a1 + 0x10) & 0x00ff_ffff;
            }

            a2 = (a2 + (uint)sourceStep) & 0x00ff_ffff;
            lastD2BeforeSub = (ushort)d2;
            d2 = (uint)((ushort)d2 - d5) & 0xffffu;
        }

        state.Data[0] = (state.Data[0] & 0xffff_0000u) | 0xffffu;
        state.Data[2] = (state.Data[2] & 0xffff_0000u) | d2;
        state.Data[3] = d3;
        state.Data[4] = d4;
        state.Address[1] = a1;
        state.Address[2] = a2;

        bool negative = (d2 & 0x8000u) != 0;
        bool zero = d2 == 0;
        bool overflow = ((lastD2BeforeSub ^ d5) & (lastD2BeforeSub ^ d2) & 0x8000u) != 0;
        bool carry = lastD2BeforeSub < d5;
        ushort sr = UpdateAddSubCcr(state.Sr, negative, zero, overflow, carry);
        uint nextPc = 0x0013a2u;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)(count * 14);
        cycles = (uint)Math.Max(34, count * 68);
        return true;
    }

    private bool TryExecuteDariusSceneSpriteRows(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x001330 || op != 0xe9c1
            || _bus.PeekWord(0x001330) != 0xe9c1
            || _bus.PeekWord(0x001332) != 0x0010
            || _bus.PeekWord(0x001334) != 0x264a
            || _bus.PeekWord(0x001336) != 0x3428
            || _bus.PeekWord(0x001338) != 0x0018
            || _bus.PeekWord(0x00133a) != 0xe94a
            || _bus.PeekWord(0x00133c) != 0x0884
            || _bus.PeekWord(0x00133e) != 0x000c
            || _bus.PeekWord(0x001340) != 0x0884
            || _bus.PeekWord(0x001342) != 0x000d
            || _bus.PeekWord(0x001344) != 0x0884
            || _bus.PeekWord(0x001346) != 0x001f
            || _bus.PeekWord(0x001348) != 0x3292
            || _bus.PeekWord(0x00139a) != 0xd4c6
            || _bus.PeekWord(0x00139c) != 0x9445
            || _bus.PeekWord(0x00139e) != 0x51c8
            || _bus.PeekWord(0x0013a2) != 0x45f3
            || _bus.PeekWord(0x0013a6) != 0x0804
            || _bus.PeekWord(0x0013c4) != 0x51c9
            || _bus.PeekWord(0x0013c8) != 0x2b49
            || _bus.PeekWord(0x0013cc) != 0x4e75)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        int rows = ((ushort)state.Data[1]) + 1;
        int columns = (ushort)(state.Data[1] >> 16) + 1;
        if (rows <= 0 || rows > 0x0100 || columns <= 0 || columns > 0x0100)
            return false;

        int sourceStep = unchecked((short)(ushort)state.Data[6]);
        int rowStep = unchecked((short)(ushort)state.Data[7]);
        if (Math.Abs(sourceStep) > 0x0200 || Math.Abs(rowStep) > 0x2000)
            return false;

        uint a0 = state.Address[0] & 0x00ff_ffff;
        uint a1 = state.Address[1] & 0x00ff_ffff;
        uint a2 = state.Address[2] & 0x00ff_ffff;
        uint a5 = state.Address[5] & 0x00ff_ffff;
        if (!IsSpriteRamAddress(a1))
            return false;

        uint d2Start = ((uint)_bus.ReadDataWord(a0 + 0x18) << 4) & 0xffffu;
        ushort d5 = (ushort)state.Data[5];
        uint d3 = state.Data[3];
        uint d4 = state.Data[4];
        byte overrideByte = _bus.ReadByte(a0 + 0x1d);
        ushort spriteWord2 = _bus.ReadDataWord(a0 + 0x1e);
        ushort lastD2BeforeSub = (ushort)d2Start;
        uint d2 = d2Start;
        ulong copiedSlots = 0;

        for (int row = 0; row < rows; row++)
        {
            uint rowStart = a2;
            d2 = d2Start;
            d4 &= ~((1u << 12) | (1u << 13) | (1u << 31));

            for (int column = 0; column < columns; column++)
            {
                ushort sourceWord = _bus.ReadDataWord(a2);
                _bus.WriteSpriteWordAddress(a1, sourceWord);
                copiedSlots++;
                if (sourceWord == 0)
                {
                    d4 &= ~(uint)((1 << 12) | (1 << 13));
                }
                else
                {
                    d3 = (d3 & 0xffff_0000u) | (ushort)(_bus.ReadDataWord(a2 + 2) ^ (ushort)d4);
                    if (overrideByte != 0)
                        d3 = (d3 & 0xffff_ff00u) | overrideByte;

                    _bus.WriteSpriteWordAddress(a1 + 8, (ushort)d3);
                    _bus.WriteSpriteWordAddress(a1 + 2, spriteWord2);
                    bool bit12WasSet = (d4 & (1u << 12)) != 0;
                    d4 |= 1u << 12;
                    if (!bit12WasSet)
                    {
                        d4 |= (1u << 13) | (1u << 14) | (1u << 31);
                        d4 &= ~(1u << 15);
                        uint repeated = (d2 << 16) | d2;
                        d3 = (d3 & 0xffff_0000u) | ((repeated >> 4) & 0x0fffu);
                        _bus.WriteSpriteWordAddress(a1 + 6, (ushort)d3);
                    }

                    a1 = (a1 + 0x10) & 0x00ff_ffff;
                }

                a2 = unchecked(a2 + (uint)sourceStep) & 0x00ff_ffff;
                lastD2BeforeSub = (ushort)d2;
                d2 = (uint)((ushort)d2 - d5) & 0xffffu;
            }

            a2 = unchecked(rowStart + (uint)rowStep) & 0x00ff_ffff;
            if ((d4 & (1u << 31)) == 0)
            {
                _bus.WriteSpriteWordAddress(a1 + 8, (ushort)d4);
                _bus.WriteSpriteWordAddress(a1 + 2, spriteWord2);
                d4 |= 1u << 14;
                a1 = (a1 + 0x10) & 0x00ff_ffff;
            }

            d4 |= 1u << 15;
        }

        state.Data[0] = (state.Data[0] & 0xffff_0000u) | 0xffffu;
        state.Data[1] = (state.Data[1] & 0xffff_0000u) | 0xffffu;
        state.Data[2] = (state.Data[2] & 0xffff_0000u) | d2;
        state.Data[3] = d3;
        state.Data[4] = d4;
        state.Address[1] = a1;
        state.Address[2] = a2;
        _bus.WriteLong((a5 - 0x0ca0u) & 0x00ff_ffff, a1);

        bool negative = (d2 & 0x8000u) != 0;
        bool zero = d2 == 0;
        bool overflow = ((lastD2BeforeSub ^ d5) & (lastD2BeforeSub ^ d2) & 0x8000u) != 0;
        bool carry = lastD2BeforeSub < d5;
        ushort sr = UpdateAddSubCcr(state.Sr, negative, zero, overflow, carry);
        SetStateAfterRts(state, sr);
        _m68ec020ProbeInstructions += copiedSlots * 14u + (ulong)(rows * 10);
        cycles = (uint)Math.Max(48, copiedSlots * 68u + (ulong)(rows * 42));
        return true;
    }

    private bool TryExecuteDariusStaticSpriteCopyEntry(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x001282 || op != 0x226d
            || _bus.PeekWord(0x001282) != 0x226d
            || _bus.PeekWord(0x001284) != 0xf360
            || _bus.PeekWord(0x001286) != 0x2468
            || _bus.PeekWord(0x001288) != 0x0010
            || _bus.PeekWord(0x00128a) != 0x0c92
            || _bus.PeekWord(0x00128c) != 0x0001
            || _bus.PeekWord(0x00128e) != 0x0001
            || _bus.PeekWord(0x001290) != 0x6700
            || _bus.PeekWord(0x001292) != 0x013c)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        uint a0 = state.Address[0] & 0x00ff_ffff;
        uint a5 = state.Address[5] & 0x00ff_ffff;
        uint a1 = _bus.ReadDataLong((a5 - 0x0ca0u) & 0x00ff_ffff) & 0x00ff_ffff;
        uint a2 = _bus.ReadDataLong(a0 + 0x10) & 0x00ff_ffff;
        if (_bus.ReadDataLong(a2) != 0x0001_0001u)
            return false;

        state.Address[1] = a1;
        state.Address[2] = a2;
        return ExecuteDariusStaticSpriteCopyBody(state, a0, a1, a2, 22, 72, out cycles);
    }

    private bool TryExecuteDariusSceneSpriteProlog(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x001282 || op != 0x226d
            || _bus.PeekWord(0x001282) != 0x226d
            || _bus.PeekWord(0x001286) != 0x2468
            || _bus.PeekWord(0x00128a) != 0x0c92
            || _bus.PeekWord(0x001294) != 0x301a
            || _bus.PeekWord(0x001296) != 0x321a
            || _bus.PeekWord(0x001308) != 0xefc1
            || _bus.PeekWord(0x00130c) != 0xe9e8
            || _bus.PeekWord(0x001318) != 0xe9e8
            || _bus.PeekWord(0x001324) != 0x3428
            || _bus.PeekWord(0x001328) != 0xe9c2
            || _bus.PeekWord(0x00132c) != 0x3343)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        uint a0 = state.Address[0] & 0x00ff_ffff;
        uint a5 = state.Address[5] & 0x00ff_ffff;
        uint a1 = _bus.ReadDataLong((a5 - 0x0ca0u) & 0x00ff_ffff) & 0x00ff_ffff;
        uint a2 = _bus.ReadDataLong(a0 + 0x10) & 0x00ff_ffff;
        if (!IsSpriteRamAddress(a1) || _bus.ReadDataLong(a2) == 0x0001_0001u)
            return false;

        ushort columnsWord = _bus.ReadDataWord(a2);
        ushort rowsWord = _bus.ReadDataWord(a2 + 2);
        int columns = columnsWord - 1;
        int rows = rowsWord - 1;
        if (columns < 0 || rows < 0 || columns > 0xff || rows > 0xff)
            return false;

        uint a2Cursor = (a2 + 4) & 0x00ff_ffff;
        int d7 = columnsWord << 2;
        int d6;
        int mode = _bus.ReadByte(a0 + 0x1c) & 3;
        switch (mode)
        {
            case 0:
                d6 = 4;
                break;
            case 1:
                d6 = 4;
                d7 = unchecked((short)-d7);
                a2Cursor = unchecked(a2Cursor - (uint)(short)(rows * d7)) & 0x00ff_ffff;
                break;
            case 2:
                d6 = -4;
                a2Cursor = unchecked(a2Cursor + (uint)(columns * 4)) & 0x00ff_ffff;
                break;
            default:
                d6 = -4;
                d7 = unchecked((short)-d7);
                a2Cursor = unchecked(a2Cursor - (uint)(short)(rows * d7) + (uint)(columns * 4)) & 0x00ff_ffff;
                break;
        }

        uint d1 = ((uint)(ushort)columns << 16) | (ushort)rows;
        ushort xDelta = unchecked((ushort)(_bus.ReadByte(a0 + 0x1f) - 0x0101));
        ushort yDelta = unchecked((ushort)(_bus.ReadByte(a0 + 0x1e) - 0x0100));
        uint d5 = ((uint)xDelta << 16) | yDelta;
        ushort xBase = (ushort)(_bus.ReadDataWord(a0 + 0x14) & 0x0fff);
        _bus.WriteSpriteWordAddress(a1 + 4, xBase);
        uint d2 = (uint)(_bus.ReadDataWord(a0 + 0x18) << 4) & 0xffffu;
        uint d4 = _bus.ReadDataWord(a0 + 0x1c) & 0xff00u;

        state.Data[0] = (state.Data[0] & 0xffff_0000u) | (ushort)columns;
        state.Data[1] = d1;
        state.Data[2] = (state.Data[2] & 0xffff_0000u) | d2;
        state.Data[3] = (state.Data[3] & 0xffff_0000u) | xBase;
        state.Data[4] = d4;
        state.Data[5] = d5;
        state.Data[6] = (state.Data[6] & 0xffff_0000u) | (ushort)d6;
        state.Data[7] = (state.Data[7] & 0xffff_0000u) | (ushort)d7;
        state.Address[1] = a1;
        state.Address[2] = a2Cursor;
        state.Address[3] = _bus.ReadDataLong(0x0012b8u + (uint)(mode * 4)) & 0x00ff_ffff;

        uint nextPc = 0x001330u;
        ushort sr = UpdateCcr(state.Sr, (xBase & 0x0800) != 0, xBase == 0, overflow: false, carry: false);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, _bus.ReadOpcodeWord(nextPc)));
        _m68ec020ProbeInstructions += 28;
        cycles = 96;
        return true;
    }

    private bool TryExecuteDariusStaticSpriteCopy(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x0013ce || op != 0x3368
            || _bus.PeekWord(0x0013ce) != 0x3368
            || _bus.PeekWord(0x0013d4) != 0x2028
            || _bus.PeekWord(0x0013d8) != 0x3028
            || _bus.PeekWord(0x0013dc) != 0x0280
            || _bus.PeekWord(0x0013e2) != 0x2340
            || _bus.PeekWord(0x0013e6) != 0x32aa
            || _bus.PeekWord(0x0013ea) != 0x302a
            || _bus.PeekWord(0x0013ee) != 0x3228
            || _bus.PeekWord(0x0013f2) != 0x51c1
            || _bus.PeekWord(0x0013f4) != 0xb340
            || _bus.PeekWord(0x0013f6) != 0x4a28
            || _bus.PeekWord(0x0013fa) != 0x6700
            || _bus.PeekWord(0x0013fe) != 0x1028
            || _bus.PeekWord(0x001402) != 0x3340
            || _bus.PeekWord(0x001406) != 0x43e9
            || _bus.PeekWord(0x00140a) != 0x2b49
            || _bus.PeekWord(0x00140e) != 0x4e75)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        uint a0 = state.Address[0] & 0x00ff_ffff;
        uint a1 = state.Address[1] & 0x00ff_ffff;
        uint a2 = state.Address[2] & 0x00ff_ffff;
        return ExecuteDariusStaticSpriteCopyBody(state, a0, a1, a2, 18, 54, out cycles);
    }

    private bool ExecuteDariusStaticSpriteCopyBody(
        M68000.M68000State state,
        uint a0,
        uint a1,
        uint a2,
        ulong instructionCount,
        uint cycleCount,
        out uint cycles)
    {
        cycles = 0;
        uint a5 = state.Address[5] & 0x00ff_ffff;
        if (!IsSpriteRamAddress(a1))
            return false;

        _bus.WriteSpriteWordAddress(a1 + 2, _bus.ReadDataWord(a0 + 0x1e));

        uint d0 = _bus.ReadDataLong(a0 + 0x14);
        d0 = (d0 & 0xffff_0000u) | _bus.ReadDataWord(a0 + 0x18);
        d0 &= 0x0fff_0fffu;
        _bus.WriteSpriteLongAddress(a1 + 4, d0);

        _bus.WriteSpriteWordAddress(a1, _bus.ReadDataWord(a2 + 4));

        d0 = (d0 & 0xffff_0000u) | _bus.ReadDataWord(a2 + 6);
        uint d1 = (state.Data[1] & 0xffff_0000u) | (ushort)(_bus.ReadDataWord(a0 + 0x1c) & 0xff00);
        d0 = (d0 & 0xffff_0000u) | (ushort)(((ushort)d0) ^ (ushort)d1);

        byte overrideByte = _bus.ReadByte(a0 + 0x1d);
        if (overrideByte != 0)
            d0 = (d0 & 0xffff_ff00u) | overrideByte;

        _bus.WriteSpriteWordAddress(a1 + 8, (ushort)d0);

        a1 = (a1 + 0x10) & 0x00ff_ffff;
        state.Data[0] = d0;
        state.Data[1] = d1;
        state.Address[1] = a1;
        _bus.WriteLong((a5 - 0x0ca0u) & 0x00ff_ffff, a1);

        ushort sr = UpdateCcr(state.Sr, (overrideByte & 0x80) != 0, overrideByte == 0, overflow: false, carry: false);
        SetStateAfterRts(state, sr);
        _m68ec020ProbeInstructions += instructionCount;
        cycles = cycleCount;
        return true;
    }

    private bool TryExecuteDariusObjectTrailShift(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x0c8514 || op != 0x22e9
            || _bus.PeekWord(0x0c8510) != 0x323c
            || _bus.PeekWord(0x0c8512) != 0x0017
            || _bus.PeekWord(0x0c8514) != 0x22e9
            || _bus.PeekWord(0x0c8516) != 0x0004
            || _bus.PeekWord(0x0c8518) != 0x51c9
            || _bus.PeekWord(0x0c851a) != 0xfffa
            || _bus.PeekWord(0x0c851c) != 0x4a68)
        {
            return false;
        }

        var state = _mainCpu.GetState();
        if ((ushort)state.Data[1] != 0x0017)
            return false;

        uint a1 = state.Address[1] & 0x00ff_ffff;
        if (!IsF3WritableRamRange(a1, 0x64))
            return false;

        uint lastValue = 0;
        for (int i = 0; i < 24; i++)
        {
            uint source = (a1 + 4 + (uint)(i * 4)) & 0x00ff_ffff;
            uint destination = (a1 + (uint)(i * 4)) & 0x00ff_ffff;
            lastValue = _bus.ReadLong(source);
            _bus.WriteLong(destination, lastValue);
        }

        state.Address[1] = (a1 + 0x60) & 0x00ff_ffff;
        state.Data[1] = (state.Data[1] & 0xffff_0000u) | 0xffffu;
        ushort sr = UpdateCcr(state.Sr, (lastValue & 0x8000_0000u) != 0, lastValue == 0, overflow: false, carry: false);
        uint nextPc = 0x0c851c;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += 48;
        cycles = 720;
        return true;
    }

    private bool TryExecuteDariusObjectAnimPointerLoad(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x0cfa86 || op != 0x3019
            || _bus.PeekWord(0x0cfa86) != 0x3019
            || _bus.PeekWord(0x0cfa88) != 0x1140
            || _bus.PeekWord(0x0cfa8c) != 0x2159
            || _bus.PeekWord(0x0cfa90) != 0x2151
            || _bus.PeekWord(0x0cfa94) != 0x4e75)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        uint a0 = state.Address[0] & 0x00ff_ffff;
        uint a1 = state.Address[1] & 0x00ff_ffff;
        if (!IsF3WritableRamRange(a0, 0x6c))
            return false;

        ushort d0 = _bus.ReadWord(a1);
        a1 = (a1 + 2) & 0x00ff_ffff;
        _bus.WriteByte(a0 + 0x1c, (byte)d0);
        uint pointer = _bus.ReadLong(a1);
        _bus.WriteLong(a0 + 0x10, pointer);
        a1 = (a1 + 4) & 0x00ff_ffff;
        uint nextPointer = _bus.ReadLong(a1);
        _bus.WriteLong(a0 + 0x68, nextPointer);

        state.Data[0] = (state.Data[0] & 0xffff_0000u) | d0;
        state.Address[1] = a1;
        ushort sr = UpdateCcr(state.Sr, (nextPointer & 0x8000_0000u) != 0, nextPointer == 0, overflow: false, carry: false);
        SetStateAfterRts(state, sr);
        _m68ec020ProbeInstructions += 5;
        cycles = 58;
        return true;
    }

    private bool TryExecuteDariusTimedObjectAnimPointerLoad(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x0d0690 || op != 0x5368
            || _bus.PeekWord(0x0d0690) != 0x5368
            || _bus.PeekWord(0x0d0692) != 0x004c
            || _bus.PeekWord(0x0d0694) != 0x6700
            || _bus.PeekWord(0x0d0698) != 0x4e75
            || _bus.PeekWord(0x0d069a) != 0x2268
            || _bus.PeekWord(0x0d069e) != 0xd2fc
            || _bus.PeekWord(0x0d06a2) != 0x2149
            || _bus.PeekWord(0x0d06a6) != 0x3019
            || _bus.PeekWord(0x0d06a8) != 0x6b00
            || _bus.PeekWord(0x0d06ac) != 0x3140
            || _bus.PeekWord(0x0d06b0) != 0x3019
            || _bus.PeekWord(0x0d06b2) != 0x1140
            || _bus.PeekWord(0x0d06b6) != 0x2159
            || _bus.PeekWord(0x0d06ba) != 0x2151
            || _bus.PeekWord(0x0d06be) != 0x4e75
            || _bus.PeekWord(0x0d06c0) != 0x4a51
            || _bus.PeekWord(0x0d06c2) != 0x6700
            || _bus.PeekWord(0x0d06c6) != 0x92d1
            || _bus.PeekWord(0x0d06c8) != 0x2149
            || _bus.PeekWord(0x0d06cc) != 0x3159
            || _bus.PeekWord(0x0d06d0) != 0x3019
            || _bus.PeekWord(0x0d06d2) != 0x1140
            || _bus.PeekWord(0x0d06d6) != 0x2159
            || _bus.PeekWord(0x0d06da) != 0x2151
            || _bus.PeekWord(0x0d06de) != 0x4e75)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        uint a0 = state.Address[0] & 0x00ff_ffff;
        if (!IsF3WritableRamRange(a0, 0x6c))
            return false;

        ushort oldTimer = _bus.ReadWord(a0 + 0x4c);
        ushort timer = (ushort)(oldTimer - 1);
        _bus.WriteWord(a0 + 0x4c, timer);
        ushort sr = UpdateSubWordCcr(state.Sr, 1, oldTimer, timer);
        if (timer != 0)
        {
            SetStateAfterRts(state, sr);
            _m68ec020ProbeInstructions += 3;
            cycles = 30;
            return true;
        }

        uint a1 = (_bus.ReadLong(a0 + 0x48) + 0x0cu) & 0x00ff_ffff;
        _bus.WriteLong(a0 + 0x48, a1);
        ushort d0 = _bus.ReadWord(a1);
        a1 = (a1 + 2) & 0x00ff_ffff;
        if ((d0 & 0x8000) != 0)
        {
            ushort jump = _bus.ReadWord(a1);
            sr = UpdateCcr(state.Sr, (jump & 0x8000) != 0, jump == 0, overflow: false, carry: false);
            if (jump == 0)
            {
                state.Data[0] = (state.Data[0] & 0xffff_0000u) | d0;
                state.Address[1] = a1;
                SetStateAfterRts(state, sr);
                _m68ec020ProbeInstructions += 8;
                cycles = 76;
                return true;
            }

            a1 = unchecked(a1 - (uint)(short)jump) & 0x00ff_ffff;
            _bus.WriteLong(a0 + 0x48, a1);
            timer = _bus.ReadWord(a1);
            _bus.WriteWord(a0 + 0x4c, timer);
            a1 = (a1 + 2) & 0x00ff_ffff;
        }
        else
        {
            _bus.WriteWord(a0 + 0x4c, d0);
        }

        d0 = _bus.ReadWord(a1);
        a1 = (a1 + 2) & 0x00ff_ffff;
        _bus.WriteByte(a0 + 0x1c, (byte)d0);
        uint pointer = _bus.ReadLong(a1);
        _bus.WriteLong(a0 + 0x10, pointer);
        a1 = (a1 + 4) & 0x00ff_ffff;
        uint nextPointer = _bus.ReadLong(a1);
        _bus.WriteLong(a0 + 0x68, nextPointer);

        state.Data[0] = (state.Data[0] & 0xffff_0000u) | d0;
        state.Address[1] = a1;
        sr = UpdateCcr(state.Sr, (nextPointer & 0x8000_0000u) != 0, nextPointer == 0, overflow: false, carry: false);
        SetStateAfterRts(state, sr);
        _m68ec020ProbeInstructions += 13;
        cycles = 118;
        return true;
    }

    private bool TryExecuteDariusObjectAnimationStep(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x0cbd52 || op != 0x3028
            || _bus.PeekWord(0x0cbd52) != 0x3028
            || _bus.PeekWord(0x0cbd54) != 0x003a
            || _bus.PeekWord(0x0cbd56) != 0x4a71
            || _bus.PeekWord(0x0cbd58) != 0x0000
            || _bus.PeekWord(0x0cbd5a) != 0x6c00
            || _bus.PeekWord(0x0cbd5e) != 0x4268
            || _bus.PeekWord(0x0cbd62) != 0x60ee
            || _bus.PeekWord(0x0cbd64) != 0x5268
            || _bus.PeekWord(0x0cbd68) != 0x3228
            || _bus.PeekWord(0x0cbd6c) != 0xb271
            || _bus.PeekWord(0x0cbd70) != 0x6f00
            || _bus.PeekWord(0x0cbd74) != 0x4268
            || _bus.PeekWord(0x0cbd78) != 0x5c68
            || _bus.PeekWord(0x0cbd7c) != 0x60d4
            || _bus.PeekWord(0x0cbd7e) != 0x2171
            || _bus.PeekWord(0x0cbd84) != 0x4e75)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        uint a0 = state.Address[0] & 0x00ff_ffff;
        uint a1 = state.Address[1] & 0x00ff_ffff;
        if (!IsF3WritableRamRange(a0, 0x42))
            return false;

        ushort d0 = _bus.ReadWord(a0 + 0x3a);
        ushort frameCounter = _bus.ReadWord(a0 + 0x3e);
        ushort d1 = frameCounter;
        int loops = 0;
        while (true)
        {
            short entryFrames = unchecked((short)_bus.ReadWord(a1 + unchecked((uint)(short)d0)));
            if (entryFrames < 0)
            {
                d0 = 0;
                if (++loops > 64)
                    return false;
                continue;
            }

            d1 = (ushort)(frameCounter + 1);
            if (unchecked((short)d1) > entryFrames)
            {
                frameCounter = 0;
                d1 = 0;
                d0 = (ushort)(d0 + 6);
                if (++loops > 64)
                    return false;
                continue;
            }

            uint animationValue = _bus.ReadLong(a1 + unchecked((uint)(short)d0) + 2);
            _bus.WriteWord(a0 + 0x3a, d0);
            _bus.WriteWord(a0 + 0x3e, d1);
            _bus.WriteLong(a0 + 0x10, animationValue);

            state.Data[0] = (state.Data[0] & 0xffff_0000u) | d0;
            state.Data[1] = (state.Data[1] & 0xffff_0000u) | d1;
            ushort sr = UpdateCcr(state.Sr, (animationValue & 0x8000_0000u) != 0, animationValue == 0, overflow: false, carry: false);
            SetStateAfterRts(state, sr);
            _m68ec020ProbeInstructions += (ulong)(loops * 8 + 10);
            cycles = (uint)(loops * 48 + 64);
            return true;
        }
    }

    private bool TryExecuteDariusPaletteLerpTable(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x11c3d2 || op != 0x2419
            || _bus.PeekWord(0x11c3d2) != 0x2419
            || _bus.PeekWord(0x11c3d4) != 0x261a
            || _bus.PeekWord(0x11c3d6) != 0x7800
            || _bus.PeekWord(0x11c3d8) != 0xe9c2
            || _bus.PeekWord(0x11c3dc) != 0xe9c3
            || _bus.PeekWord(0x11c3e0) != 0x6100
            || _bus.PeekWord(0x11c3e4) != 0xefc4
            || _bus.PeekWord(0x11c3e8) != 0xe9c2
            || _bus.PeekWord(0x11c3ec) != 0xe9c3
            || _bus.PeekWord(0x11c3f0) != 0x6100
            || _bus.PeekWord(0x11c3f4) != 0xefc4
            || _bus.PeekWord(0x11c3f8) != 0xe9c2
            || _bus.PeekWord(0x11c3fc) != 0xe9c3
            || _bus.PeekWord(0x11c400) != 0x6100
            || _bus.PeekWord(0x11c404) != 0xefc4
            || _bus.PeekWord(0x11c408) != 0x26c4
            || _bus.PeekWord(0x11c40a) != 0x51c9
            || _bus.PeekWord(0x11c40e) != 0x2f3c)
        {
            return false;
        }

        var state = GetScratchMainCpuState();
        int count = ((ushort)state.Data[1]) + 1;
        if (count <= 0 || count > 0x20)
            return false;

        uint sourceA = state.Address[1] & 0x00ff_ffff;
        uint sourceB = state.Address[2] & 0x00ff_ffff;
        uint destination = state.Address[3] & 0x00ff_ffff;
        if (!IsF3WritableRamRange(destination, (uint)count * 4u))
            return false;

        short lerp = unchecked((short)(ushort)state.Data[0]);
        uint d2 = state.Data[2];
        uint d3 = state.Data[3];
        uint d4 = state.Data[4];
        uint d5 = state.Data[5];
        uint d6 = state.Data[6];

        for (int i = 0; i < count; i++)
        {
            d2 = _bus.ReadLong(sourceA);
            d3 = _bus.ReadLong(sourceB);
            sourceA = (sourceA + 4) & 0x00ff_ffff;
            sourceB = (sourceB + 4) & 0x00ff_ffff;
            d4 = 0;

            d5 = ExtractRegisterBitfieldByte(d2, 8);
            d6 = ExtractRegisterBitfieldByte(d3, 8);
            d6 = DariusPaletteLerpByte(d5, d6, lerp);
            d4 = InsertRegisterBitfieldByte(d4, d6, 8);

            d5 = ExtractRegisterBitfieldByte(d2, 16);
            d6 = ExtractRegisterBitfieldByte(d3, 16);
            d6 = DariusPaletteLerpByte(d5, d6, lerp);
            d4 = InsertRegisterBitfieldByte(d4, d6, 16);

            d5 = ExtractRegisterBitfieldByte(d2, 24);
            d6 = ExtractRegisterBitfieldByte(d3, 24);
            d6 = DariusPaletteLerpByte(d5, d6, lerp);
            d4 = InsertRegisterBitfieldByte(d4, d6, 24);

            _bus.WriteLong(destination, d4);
            destination = (destination + 4) & 0x00ff_ffff;
        }

        state.Address[1] = sourceA;
        state.Address[2] = sourceB;
        state.Address[3] = destination;
        state.Data[1] = (state.Data[1] & 0xffff_0000u) | 0xffffu;
        state.Data[2] = d2;
        state.Data[3] = d3;
        state.Data[4] = d4;
        state.Data[5] = d5;
        state.Data[6] = d6;

        ushort sr = UpdateCcr(state.Sr, (d6 & 0x80u) != 0, (d6 & 0xffu) == 0, overflow: false, carry: false);
        uint nextPc = 0x11c40e;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)(count * 19);
        cycles = (uint)Math.Max(34, count * 84);
        return true;
    }

    private static uint ExtractRegisterBitfieldByte(uint value, int offset)
        => (value >> (32 - offset - 8)) & 0xffu;

    private static uint InsertRegisterBitfieldByte(uint destination, uint value, int offset)
    {
        int shift = 32 - offset - 8;
        uint mask = 0xffu << shift;
        return (destination & ~mask) | ((value & 0xffu) << shift);
    }

    private static uint DariusPaletteLerpByte(uint source, uint target, short lerp)
    {
        short delta = unchecked((short)(ushort)(source - target));
        int scaled = (delta * lerp) >> 8;
        return (uint)(ushort)(unchecked((short)(ushort)target) + scaled);
    }

    private bool TrySkipEmptySpriteControlSlots(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x0016b8 || op != 0x3c11)
            return false;

        var state = _mainCpu.GetState();
        int d7 = (ushort)state.Data[7];
        if (d7 == 0xffff)
            return false;

        int remaining = d7 + 1;
        int skip = 0;
        uint a1 = state.Address[1];
        while (skip < remaining && _bus.ReadWord(a1 + (uint)(skip * 6)) == 0)
            skip++;

        if (skip == 0)
            return false;

        state.Data[6] &= 0xffff_0000u;
        state.Data[7] = (state.Data[7] & 0xffff_0000u) | (ushort)((d7 - skip) & 0xffff);
        state.Address[1] = (state.Address[1] + (uint)(skip * 6)) & 0x00ff_ffff;
        state.Address[2] = (state.Address[2] + (uint)(skip * 0x180)) & 0x00ff_ffff;
        state.Address[3] = (state.Address[3] + (uint)(skip * 0x80)) & 0x00ff_ffff;

        uint nextPc = skip == remaining ? 0x0016ecu : 0x0016b8u;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)skip;
        cycles = (uint)(skip * 50);
        return true;
    }

    private bool TryExecuteF3TrapSchedulerStub(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (op != 0x4e41 && op != 0x4e42 && op != 0x4e43 && op != 0x4e44 && op != 0x4e45)
            return false;
        if (UseNativeF3TrapScheduler)
            return false;

        var state = _mainCpu.GetState();
        uint nextPc;
        uint usp = state.Usp;
        uint ssp = state.Ssp;
        if (op == 0x4e41)
        {
            _lastF3TrapPc = pc;
            uint sp = (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp;
            uint entry = _bus.ReadLong(sp) & 0x00ff_ffff;
            int priority = (int)(_bus.ReadLong(sp + 4) & 31);
            if (entry >= 0x000100 && entry < 0x200000 && !IsSkippableF3BringupTask(entry))
            {
                if (entry == 0x0c63e8)
                    _gameStartAccepted = true;
                EnqueueF3Task(CreateNewF3TaskState(state, entry, priority));
                _f3TasksEnqueued++;
                _lastF3TaskEntry = entry;
                RecordRecentTask(_recentF3EnqueuedTasks, ref _recentF3EnqueuedIndex, entry);
            }
            nextPc = (pc + 2) & 0x00ff_ffff;
        }
        else if (op == 0x4e42)
        {
            _lastF3TrapPc = pc;
            uint sp = (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp;
            uint mask = _bus.ReadLong(sp);
            RemoveF3TasksByMask(mask, pc);
            uint currentBit = _currentF3TaskPriority < 32 ? 1u << _currentF3TaskPriority : 0;
            bool mainGateRemove = pc is 0x010306 or 0x010334;
            if (pc == 0x010334)
                _bus.ClearStartSceneGateBit0();
            if (!mainGateRemove && (mask & currentBit) != 0)
            {
                nextPc = 0x002326;
                ushort idlePrefetch = _bus.ReadOpcodeWord(nextPc);
                _mainCpu.SetState(CreateF3IdleState(state, nextPc, idlePrefetch, usp, ssp));
                _m68ec020ProbeInstructions++;
                cycles = 34;
                return true;
            }
            nextPc = (pc + 2) & 0x00ff_ffff;
        }
        else if (op == 0x4e43)
        {
            _lastF3TrapPc = pc;
            _currentF3TaskPriority = 0;
            nextPc = 0x002326;
            ushort idlePrefetch = _bus.ReadOpcodeWord(nextPc);
            _mainCpu.SetState(CreateF3IdleState(state, nextPc, idlePrefetch, usp, ssp));
            _m68ec020ProbeInstructions++;
            cycles = 34;
            return true;
        }
        else
        {
            _lastF3TrapPc = pc;
            if (op == 0x4e44 || op == 0x4e45)
            {
                uint continuation = (pc + 2) & 0x00ff_ffff;
                if (continuation >= 0x000100 && continuation < 0x200000)
                {
                    bool ridesFastCurrent = IsF3SceneFastContinuation(continuation);
                    int delayFrames = ridesFastCurrent
                        ? 0
                        : op == 0x4e44
                            ? Math.Clamp((int)(_bus.ReadLong((state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp) & 0x7fff), 1, 600)
                            : 0;
                    if (_gameStartAccepted && op == 0x4e45 && !ridesFastCurrent)
                        delayFrames = Math.Max(delayFrames, 1);
                    bool persistentYield = IsPersistentF3YieldContinuation(pc, continuation, _currentF3TaskPriority);
                    if (persistentYield)
                        delayFrames = Math.Max(delayFrames, 1);
                    F3TaskState continuationTask = CreateContinuationF3TaskState(state, continuation, delayFrames, _currentF3TaskPriority);
                    if (persistentYield)
                        _lastPersistentYieldTask = continuationTask;
                    EnqueueF3Task(
                        continuationTask,
                        preferFront: ridesFastCurrent);
                    if (ridesFastCurrent)
                        _sceneContinuationEnqueued++;
                    _f3TasksEnqueued++;
                    _lastF3TaskEntry = continuation;
                    RecordRecentTask(_recentF3EnqueuedTasks, ref _recentF3EnqueuedIndex, continuation);
                }

                nextPc = 0x002326;
                ushort idlePrefetch = _bus.ReadOpcodeWord(nextPc);
                _mainCpu.SetState(CreateF3IdleState(state, nextPc, idlePrefetch, usp, ssp));
                _m68ec020ProbeInstructions++;
                cycles = 34;
                return true;
            }
            nextPc = (pc + 2) & 0x00ff_ffff;
        }

        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, usp, ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = op == 0x4e45 && _f3TaskQueue.Count == 0
            ? (uint)((int)(MainClockHz / TargetFps) * CpuScale)
            : 34;
        return true;
    }

    private bool TryExecuteDariusSpriteReefClear(uint pc, out uint cycles)
    {
        cycles = 0;
        if (pc < 0x0011c4 || pc > 0x0011fa)
            return false;

        var state = _mainCpu.GetState();
        int remaining = ((ushort)state.Data[0]) + 1;
        uint a0 = state.Address[0] & 0x00ff_ffff;
        uint a1 = state.Address[1] & 0x00ff_ffff;
        if (remaining <= 0 || remaining > 0x400)
            return false;
        if (!IsSpriteRamAddress(a0) || !IsSpriteRamAddress(a1))
            return false;

        for (int i = 0; i < remaining; i++)
        {
            WriteDariusSpriteClearSentinel(a0);
            WriteDariusSpriteClearSentinel(a1);
            a0 = (a0 + 0x10) & 0x00ff_ffff;
            a1 = (a1 + 0x10) & 0x00ff_ffff;
        }

        state.Data[0] = (state.Data[0] & 0xffff_0000u) | 0xffffu;
        state.Address[0] = a0;
        state.Address[1] = a1;
        uint nextPc = 0x001206;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)remaining;
        cycles = (uint)Math.Max(34, remaining * 12);
        return true;
    }

    private static bool IsSpriteRamAddress(uint address)
        => address >= 0x600000 && address <= 0x60fff0;

    private void WriteDariusSpriteClearSentinel(uint address)
    {
        _bus.WriteWord(address + 0x04, 0x0ff0);
        _bus.WriteWord(address + 0x06, 0x0ff0);
        _bus.WriteWord(address + 0x08, 0x0000);
        _bus.WriteWord(address + 0x0a, 0x0000);
        _bus.WriteWord(address + 0x0c, 0x0000);
        _bus.WriteWord(address + 0x0e, 0x0000);
    }

    private bool TryExecuteDariusWorkRamCurrent(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x0104b2 || op != 0x51c8 || _bus.PeekWord(pc + 2) != 0xfffc)
            return false;

        var state = _mainCpu.GetState();
        int remaining = (ushort)state.Data[0];
        uint address = state.Address[0] & 0x00ff_ffff;
        if (remaining <= 0 || remaining > 0x5000 || address < 0x410060 || address + (uint)(remaining * 2) >= 0x420000)
            return false;

        ushort value = (ushort)state.Data[1];
        if (!_bus.TryFillWorkRamWordRange(address, (uint)remaining, value))
        {
            for (int i = 0; i < remaining; i++)
            {
                _bus.WriteWord(address, value);
                address = (address + 2) & 0x00ff_ffff;
            }
        }
        else
        {
            address = (address + (uint)remaining * 2u) & 0x00ff_ffff;
        }

        state.Data[0] = (state.Data[0] & 0xffff_0000u) | 0xffffu;
        state.Address[0] = address;
        uint nextPc = 0x0104b6;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)remaining;
        cycles = (uint)Math.Max(34, remaining * 4);
        return true;
    }

    private bool TryExecuteDariusBootRamClear(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        bool atLoopHead = pc == 0x01009a && op == 0x13fc;
        bool atLongWrite = pc == 0x0100a2 && op == 0x20c1;
        if (!atLoopHead && !atLongWrite)
            return false;

        if (_bus.PeekWord(0x01009a) != 0x13fc
            || _bus.PeekWord(0x0100a2) != 0x20c1
            || _bus.PeekWord(0x0100a4) != 0x5380
            || _bus.PeekWord(0x0100a6) != 0x66f2)
        {
            return false;
        }

        var state = _mainCpu.GetState();
        uint remaining = state.Data[0] & 0x00ff_ffff;
        uint address = state.Address[0] & 0x00ff_ffff;
        if (remaining == 0 || remaining > 0x20000 || state.Data[1] != 0)
            return false;

        uint byteCount = remaining * 4;
        if (!IsF3WritableRamRange(address, byteCount))
            return false;

        if (atLoopHead)
        {
            byte value = (byte)_bus.PeekWord(0x01009c);
            uint watchdogAddress = _bus.ReadLong(0x01009e) & 0x00ff_ffff;
            _bus.WriteByte(watchdogAddress, value);
        }

        if (!_bus.TryClearWritableRamRange(address, byteCount))
        {
            for (uint i = 0; i < remaining; i++)
            {
                _bus.WriteLong(address, 0);
                address = (address + 4) & 0x00ff_ffff;
            }
        }
        else
        {
            address = (address + byteCount) & 0x00ff_ffff;
        }

        state.Data[0] = 0;
        state.Address[0] = address;
        uint nextPc = 0x0100a8;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        ushort sr = (ushort)((state.Sr & 0xfff0) | 0x0004);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += remaining * 4;
        cycles = Math.Max(34u, remaining * 10u);
        return true;
    }

    private bool TryExecuteDariusF3MemoryTide(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (TryExecuteDariusF3RamPatternFill(pc, op, out cycles))
            return true;

        if (pc == 0x005880 && op == 0x48e7)
            return TryExecuteDariusTextRamClearRoutine(pc, out cycles);
        if (pc == 0x0058ae && op == 0x48e7)
            return TryExecuteDariusTextRamColumnClearRoutine(pc, out cycles);
        if ((pc == 0x0058da || pc == 0x005918 || pc == 0x005956 || pc == 0x0059b4) && op == 0x48e7)
            return TryExecuteDariusPlayfieldClearRoutine(pc, out cycles);

        if (op == 0x20c1 && _bus.PeekWord(pc + 2) == 0x51c8)
            return TryExecuteDariusDbraLongFill(pc, out cycles);

        if (op == 0x51c8 && _bus.PeekWord(pc - 2) == 0x20c1 && _bus.PeekWord(pc + 2) == 0xfffc)
            return TryExecuteDariusDbraLongFillAfterFirstWrite(pc, out cycles);

        if (op == 0x30c1 && _bus.PeekWord(pc + 2) == 0x51c8)
            return TryExecuteDariusDbraWordFill(pc, out cycles);

        if (pc == 0x005be8 && op == 0x323c && _bus.PeekWord(pc + 2) == 0x00e7 && _bus.PeekWord(pc + 4) == 0x32c0)
            return TryExecuteDariusDbraWordFillA1D1(pc + 4, out cycles);

        if (op == 0x32c0 && _bus.PeekWord(pc + 2) == 0x51c9)
            return TryExecuteDariusDbraWordFillA1D1(pc, out cycles);

        if ((pc == 0x005994 || pc == 0x0059f4) && op == 0xc559 && _bus.PeekWord(pc + 2) == 0x51c8)
            return TryExecuteDariusLineMaskTide(pc, out cycles);

        return false;
    }

    private bool TryExecuteDariusF3RamPatternFill(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        int unitSize;
        uint finalD1;
        bool byteFill = false;

        if (pc == 0x000522
            && op == 0x7603
            && _bus.PeekWord(pc + 2) == 0x123c
            && _bus.PeekWord(pc + 6) == 0x13fc
            && _bus.PeekWord(pc + 0x26) == 0x51cb)
        {
            unitSize = 1;
            finalD1 = 0xab;
            byteFill = true;
        }
        else if (pc == 0x000554
            && op == 0x7603
            && _bus.PeekWord(pc + 2) == 0x323c
            && _bus.PeekWord(pc + 6) == 0x13fc
            && _bus.PeekWord(pc + 0x26) == 0x51cb)
        {
            unitSize = 2;
            finalD1 = 0xaaab;
        }
        else if (pc == 0x000586
            && op == 0x7603
            && _bus.PeekWord(pc + 2) == 0x72ff
            && _bus.PeekWord(pc + 4) == 0x13fc
            && _bus.PeekWord(pc + 0x26) == 0x51cb)
        {
            unitSize = 4;
            finalD1 = 0xaaaa_aaab;
        }
        else
        {
            return false;
        }

        var state = _mainCpu.GetState();
        uint address = state.Address[0] & 0x00ff_ffff;
        uint byteCount = state.Data[0] & 0x00ff_ffff;
        if (byteCount == 0 || byteCount > 0x400000 || (byteCount % (uint)unitSize) != 0)
            return false;
        if (!IsF3WritableRamRange(address, byteCount))
            return false;

        uint elements = byteCount / (uint)unitSize;
        if (!_bus.TryClearWritableRamRange(address, byteCount))
        {
            uint current = address;
            for (uint i = 0; i < elements; i++)
            {
                if (unitSize == 1)
                    _bus.WriteByte(current, 0);
                else if (unitSize == 2)
                    _bus.WriteWord(current, 0);
                else
                    _bus.WriteLong(current, 0);

                current = (current + (uint)unitSize) & 0x00ff_ffff;
            }
        }

        uint[] data = CloneRegisters(state.Data);
        uint[] addressRegs = CloneRegisters(state.Address);
        data[0] = 0;
        data[1] = unitSize switch
        {
            1 => (data[1] & 0xffff_ff00u) | finalD1,
            2 => (data[1] & 0xffff_0000u) | finalD1,
            _ => finalD1
        };
        data[2] = byteFill ? data[2] & 0xffff_ff00u : unitSize == 2 ? data[2] & 0xffff_0000u : 0;
        data[3] = 0x0000_ffff;
        addressRegs[0] = (address + byteCount) & 0x00ff_ffff;

        uint nextPc = state.Address[6] & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        ushort sr = (ushort)((state.Sr & 0xffe0) | 0x0004);
        _mainCpu.SetState(new M68000.M68000State(data, addressRegs, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += elements * 4;
        cycles = Math.Max(34u, elements * (uint)(unitSize == 4 ? 12 : unitSize == 2 ? 8 : 6));
        return true;
    }

    private bool TryExecuteDariusDbraLongFill(uint pc, out uint cycles)
    {
        cycles = 0;
        var state = _mainCpu.GetState();
        int remaining = ((ushort)state.Data[0]) + 1;
        uint address = state.Address[0] & 0x00ff_ffff;
        if (remaining <= 0 || remaining > 0x1000 || !IsF3VideoAddress(address))
            return false;

        uint value = state.Data[1];
        uint byteCount = (uint)remaining * 4;
        if (value == 0 && _bus.TryClearWritableRamRange(address, byteCount))
        {
            address = (address + byteCount) & 0x00ff_ffff;
        }
        else
        {
            for (int i = 0; i < remaining; i++)
            {
                _bus.WriteLong(address, value);
                address = (address + 4) & 0x00ff_ffff;
            }
        }

        state.Data[0] = (state.Data[0] & 0xffff_0000u) | 0xffffu;
        state.Address[0] = address;
        uint nextPc = (pc + 6) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)remaining;
        cycles = (uint)Math.Max(34, remaining * 8);
        return true;
    }

    private bool TryExecuteDariusDbraLongFillAfterFirstWrite(uint pc, out uint cycles)
    {
        cycles = 0;
        var state = _mainCpu.GetState();
        int remaining = (ushort)state.Data[0];
        uint address = state.Address[0] & 0x00ff_ffff;
        if (remaining <= 0 || remaining > 0x1000 || !IsF3VideoAddress(address))
            return false;

        uint value = state.Data[1];
        uint byteCount = (uint)remaining * 4;
        if (value == 0 && _bus.TryClearWritableRamRange(address, byteCount))
        {
            address = (address + byteCount) & 0x00ff_ffff;
        }
        else
        {
            for (int i = 0; i < remaining; i++)
            {
                _bus.WriteLong(address, value);
                address = (address + 4) & 0x00ff_ffff;
            }
        }

        state.Data[0] = (state.Data[0] & 0xffff_0000u) | 0xffffu;
        state.Address[0] = address;
        uint nextPc = (pc + 4) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)remaining;
        cycles = (uint)Math.Max(34, remaining * 8);
        return true;
    }

    private bool TryExecuteDariusTextRamClearRoutine(uint pc, out uint cycles)
    {
        cycles = 0;
        if (_bus.PeekWord(pc + 0x00) != 0x48e7
            || _bus.PeekWord(pc + 0x02) != 0xc080
            || _bus.PeekWord(pc + 0x04) != 0x41f9
            || _bus.PeekLong(pc + 0x06) != 0x0061_c000u
            || _bus.PeekWord(pc + 0x0a) != 0x303c
            || _bus.PeekWord(pc + 0x0c) != 0x07ff
            || _bus.PeekWord(pc + 0x0e) != 0x7200
            || _bus.PeekWord(pc + 0x10) != 0x20c1
            || _bus.PeekWord(pc + 0x12) != 0x51c8
            || _bus.PeekWord(pc + 0x14) != 0xfffc
            || _bus.PeekWord(pc + 0x16) != 0x7000)
        {
            return false;
        }

        var state = _mainCpu.GetState();
        _bus.TryClearWritableRamRange(0x61c000, 0x2000);
        _bus.WriteLong(F3SchedulerA5 + 0x2278, 0);
        _bus.WriteWord(F3SchedulerA5 + 0x227c, 0);
        _bus.WriteLong(F3SchedulerA5 + 0x22a0, 0);
        _bus.WriteWord(F3SchedulerA5 + 0x22a4, 0);

        ushort sr = (ushort)((state.Sr & 0xffe0) | 0x0004);
        SetStateAfterRts(state, sr);
        _m68ec020ProbeInstructions += 0x0800;
        cycles = 8;
        return true;
    }

    private bool TryExecuteDariusTextRamColumnClearRoutine(uint pc, out uint cycles)
    {
        cycles = 0;
        if (_bus.PeekWord(pc + 0x00) != 0x48e7
            || _bus.PeekWord(pc + 0x02) != 0xf080
            || _bus.PeekWord(pc + 0x04) != 0x41f9
            || _bus.PeekLong(pc + 0x06) != 0x0061_c000u
            || _bus.PeekWord(pc + 0x0a) != 0x7000
            || _bus.PeekWord(pc + 0x0c) != 0x323c
            || _bus.PeekWord(pc + 0x0e) != 0x001d
            || _bus.PeekWord(pc + 0x10) != 0x343c
            || _bus.PeekWord(pc + 0x12) != 0x0013
            || _bus.PeekWord(pc + 0x14) != 0x2608
            || _bus.PeekWord(pc + 0x16) != 0x20c0
            || _bus.PeekWord(pc + 0x18) != 0x51ca
            || _bus.PeekWord(pc + 0x1a) != 0xfffc
            || _bus.PeekWord(pc + 0x1c) != 0x2043
            || _bus.PeekWord(pc + 0x1e) != 0x41e8
            || _bus.PeekWord(pc + 0x20) != 0x0080
            || _bus.PeekWord(pc + 0x22) != 0x51c9
            || _bus.PeekWord(pc + 0x24) != 0xffec
            || _bus.PeekWord(pc + 0x26) != 0x4cdf
            || _bus.PeekWord(pc + 0x28) != 0x010f
            || _bus.PeekWord(pc + 0x2a) != 0x4e75)
        {
            return false;
        }

        var state = _mainCpu.GetState();
        for (uint row = 0; row < 0x1e; row++)
            _bus.TryClearWritableRamRange(0x61c000 + row * 0x80, 0x50);

        ushort sr = (ushort)((state.Sr & 0xffe0) | 0x0004);
        SetStateAfterRts(state, sr);
        _m68ec020ProbeInstructions += 0x0258;
        cycles = 8;
        return true;
    }

    private bool TryExecuteDariusPlayfieldClearRoutine(uint pc, out uint cycles)
    {
        cycles = 0;
        uint playfieldAddress;
        uint lineAddress;
        ushort controlLowOffset;
        ushort controlHighOffset;
        uint? maskAddress = null;

        switch (pc)
        {
            case 0x0058da:
                playfieldAddress = 0x610000;
                lineAddress = 0x62a000;
                controlLowOffset = 0x2258;
                controlHighOffset = 0x2280;
                break;
            case 0x005918:
                playfieldAddress = 0x611000;
                lineAddress = 0x62a200;
                controlLowOffset = 0x2260;
                controlHighOffset = 0x2288;
                break;
            case 0x005956:
                playfieldAddress = 0x612000;
                lineAddress = 0x62a400;
                maskAddress = 0x628400;
                controlLowOffset = 0x2268;
                controlHighOffset = 0x2290;
                break;
            case 0x0059b4:
                playfieldAddress = 0x613000;
                lineAddress = 0x62a600;
                maskAddress = 0x628600;
                controlLowOffset = 0x2270;
                controlHighOffset = 0x2298;
                break;
            default:
                return false;
        }

        if (_bus.PeekWord(pc + 0x00) != 0x48e7
            || _bus.PeekWord(pc + 0x04) != 0x41f9
            || _bus.PeekLong(pc + 0x06) != playfieldAddress
            || _bus.PeekWord(pc + 0x0a) != 0x303c
            || _bus.PeekWord(pc + 0x0c) != 0x03ff
            || _bus.PeekWord(pc + 0x0e) != 0x7200
            || _bus.PeekWord(pc + 0x10) != 0x20c1
            || _bus.PeekWord(pc + 0x12) != 0x51c8
            || _bus.PeekWord(pc + 0x14) != 0xfffc
            || _bus.PeekWord(pc + 0x16) != 0x41f9
            || _bus.PeekLong(pc + 0x18) != lineAddress
            || _bus.PeekWord(pc + 0x1c) != 0x303c
            || _bus.PeekWord(pc + 0x1e) != 0x00ff
            || _bus.PeekWord(pc + 0x20) != 0x30c1
            || _bus.PeekWord(pc + 0x22) != 0x51c8
            || _bus.PeekWord(pc + 0x24) != 0xfffc)
        {
            return false;
        }

        var state = _mainCpu.GetState();
        _bus.TryClearWritableRamRange(playfieldAddress, 0x1000);
        _bus.TryClearWritableRamRange(lineAddress, 0x200);
        if (maskAddress.HasValue)
        {
            uint address = maskAddress.Value;
            for (int i = 0; i < 0x100; i++)
            {
                _bus.WriteWord(address, 0x0080);
                address += 2;
            }
        }

        _bus.WriteLong(F3SchedulerA5 + controlLowOffset, 0);
        _bus.WriteWord(F3SchedulerA5 + controlLowOffset + 4u, 0);
        _bus.WriteLong(F3SchedulerA5 + controlHighOffset, 0);
        _bus.WriteWord(F3SchedulerA5 + controlHighOffset + 4u, 0);

        ushort sr = (ushort)((state.Sr & 0xffe0) | 0x0004);
        SetStateAfterRts(state, sr);
        _m68ec020ProbeInstructions += maskAddress.HasValue ? 0x0500u : 0x0400u;
        cycles = 8;
        return true;
    }

    private void SetStateAfterRts(M68000.M68000State state, ushort sr)
    {
        uint sp = (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp;
        uint returnPc = _bus.ReadLong(sp) & 0x00ff_ffff;
        sp = (sp + 4) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(returnPc);
        uint usp = (state.Sr & 0x2000) != 0 ? state.Usp : sp;
        uint ssp = (state.Sr & 0x2000) != 0 ? sp : state.Ssp;
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, usp, ssp, sr, returnPc, prefetch));
    }

    private M68000.M68000State GetScratchMainCpuState()
        => _mainCpu.GetState(_m68ec020ScratchData, _m68ec020ScratchAddress);

    private void InvalidateDebugSummary()
    {
        _debugSummaryFrame = long.MinValue;
        _debugSummaryCache = string.Empty;
    }

    private bool TryExecuteDariusDbraWordFill(uint pc, out uint cycles)
    {
        cycles = 0;
        var state = _mainCpu.GetState();
        int remaining = ((ushort)state.Data[0]) + 1;
        uint address = state.Address[0] & 0x00ff_ffff;
        if (remaining <= 0 || remaining > 0x1000 || !IsF3VideoAddress(address))
            return false;

        ushort value = (ushort)state.Data[1];
        uint byteCount = (uint)remaining * 2;
        if (value == 0 && _bus.TryClearWritableRamRange(address, byteCount))
        {
            address = (address + byteCount) & 0x00ff_ffff;
        }
        else
        {
            for (int i = 0; i < remaining; i++)
            {
                _bus.WriteWord(address, value);
                address = (address + 2) & 0x00ff_ffff;
            }
        }

        state.Data[0] = (state.Data[0] & 0xffff_0000u) | 0xffffu;
        state.Address[0] = address;
        uint nextPc = (pc + 6) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)remaining;
        cycles = (uint)Math.Max(34, remaining * 6);
        return true;
    }

    private bool TryExecuteDariusDbraWordFillA1D1(uint pc, out uint cycles)
    {
        cycles = 0;
        var state = _mainCpu.GetState();
        int remaining = pc == 0x005bec ? 0x00e8 : ((ushort)state.Data[1]) + 1;
        uint address = state.Address[1] & 0x00ff_ffff;
        if (remaining <= 0 || remaining > 0x1000 || !IsF3VideoAddress(address))
            return false;

        ushort value = (ushort)state.Data[0];
        uint byteCount = (uint)remaining * 2;
        if (value == 0 && _bus.TryClearWritableRamRange(address, byteCount))
        {
            address = (address + byteCount) & 0x00ff_ffff;
        }
        else
        {
            for (int i = 0; i < remaining; i++)
            {
                _bus.WriteWord(address, value);
                address = (address + 2) & 0x00ff_ffff;
            }
        }

        state.Data[1] = (state.Data[1] & 0xffff_0000u) | 0xffffu;
        state.Address[1] = address;
        uint nextPc = (pc + 6) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)remaining;
        cycles = (uint)Math.Max(34, remaining * 6);
        return true;
    }

    private bool TryExecuteDariusLineMaskTide(uint pc, out uint cycles)
    {
        cycles = 0;
        var state = _mainCpu.GetState();
        int remaining = ((ushort)state.Data[0]) + 1;
        uint a0 = state.Address[0] & 0x00ff_ffff;
        uint a1 = state.Address[1] & 0x00ff_ffff;
        if (remaining <= 0 || remaining > 0x1000 || !IsF3VideoAddress(a0) || !IsF3VideoAddress(a1))
            return false;

        // Entry is after MOVE.W D1,(A0)+ for this iteration. Finish the
        // paired line-RAM source advance and remaining D1 fills.
        a1 = (a1 + 2) & 0x00ff_ffff;
        for (int i = 1; i < remaining; i++)
        {
            _bus.WriteWord(a0, (ushort)state.Data[1]);
            a0 = (a0 + 2) & 0x00ff_ffff;
            a1 = (a1 + 2) & 0x00ff_ffff;
        }

        state.Data[0] = (state.Data[0] & 0xffff_0000u) | 0xffffu;
        state.Address[0] = a0;
        state.Address[1] = a1;
        uint nextPc = (pc + 6) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)remaining;
        cycles = (uint)Math.Max(34, remaining * 8);
        return true;
    }

    private static bool IsF3VideoAddress(uint address)
        => (address >= 0x440000 && address < 0x448000)
            || (address >= 0x600000 && address < 0x640000);

    private static bool IsF3WritableRamRange(uint address, uint byteCount)
    {
        if (byteCount == 0)
            return false;

        ulong endExclusive = (ulong)address + byteCount;
        return (address >= 0x400000 && endExclusive <= 0x440000)
            || (address >= 0x440000 && endExclusive <= 0x448000)
            || (address >= 0x600000 && endExclusive <= 0x640000);
    }

    private bool TryExecuteDariusPaletteCurrent(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x001540 || op != 0x5880 || _bus.PeekWord(pc + 2) != 0x5841 || _bus.PeekWord(pc + 4) != 0x51ca)
            return false;

        var state = _mainCpu.GetState();
        int remaining = (ushort)state.Data[2];
        uint paletteBase = state.Address[0] & 0x00ff_ffff;
        uint sourceBase = state.Address[1] & 0x00ff_ffff;
        uint sourceOffset = state.Data[0];
        ushort destOffset = (ushort)state.Data[1];
        if (remaining <= 0 || remaining > 0x400 || paletteBase != 0x440000 || sourceBase >= 0x200000)
            return false;

        // The first MOVE.L (A1,D0.L),(A0,D1.W) has just executed. Finish the
        // ADDQ/DBRA color stream so the ROM's palette tide does not eat a frame.
        sourceOffset += 4;
        destOffset = (ushort)(destOffset + 4);
        for (int i = 0; i < remaining; i++)
        {
            uint sourceAddress = (sourceBase + sourceOffset) & 0x00ff_ffff;
            uint destAddress = (paletteBase + destOffset) & 0x00ff_ffff;
            if (sourceAddress >= 0x200000 || destAddress < 0x440000 || destAddress + 3 >= 0x448000)
                return false;

            _bus.WriteLong(destAddress, _bus.ReadLong(sourceAddress));
            sourceOffset += 4;
            destOffset = (ushort)(destOffset + 4);
        }

        state.Data[0] = sourceOffset;
        state.Data[1] = (state.Data[1] & 0xffff_0000u) | destOffset;
        state.Data[2] = (state.Data[2] & 0xffff_0000u) | 0xffffu;
        uint nextPc = 0x001548;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions += (ulong)remaining;
        cycles = (uint)Math.Max(34, remaining * 10);
        return true;
    }

    private bool TryExecuteF3SchedulerYieldEntry(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (UseNativeF3TrapScheduler)
            return false;
        if (pc != 0x001f62 || op != 0x40ed)
            return false;

        var state = _mainCpu.GetState();
        uint sp = (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp;
        uint continuation = _bus.ReadLong(sp) & 0x00ff_ffff;
        if (continuation >= 0x000100 && continuation < 0x200000)
        {
            var nextState = new M68000.M68000State(
                CloneRegisters(state.Data),
                CloneRegisters(state.Address),
                (state.Sr & 0x2000) != 0 ? state.Usp : (sp + 4) & 0x00ff_ffff,
                (state.Sr & 0x2000) != 0 ? (sp + 4) & 0x00ff_ffff : state.Ssp,
                F3TaskStatusRegister(state.Sr),
                continuation,
                _bus.ReadOpcodeWord(continuation));
            EnqueueF3Task(
                new F3TaskState(continuation, nextState, _currentF3TaskPriority),
                preferFront: IsF3SceneFastContinuation(continuation));
            _f3TasksEnqueued++;
            _lastF3TaskEntry = continuation;
            RecordRecentTask(_recentF3EnqueuedTasks, ref _recentF3EnqueuedIndex, continuation);
            if (IsF3SceneFastContinuation(continuation))
                _sceneContinuationEnqueued++;
        }

        uint idlePc = 0x002312;
        ushort prefetch = _bus.ReadOpcodeWord(idlePc);
        _mainCpu.SetState(CreateF3IdleState(state, idlePc, prefetch, state.Usp, state.Ssp));
        _m68ec020ProbeInstructions++;
        cycles = 34;
        return true;
    }

    private bool TryRecoverInvalidF3ProgramCounter(uint pc, out uint cycles)
    {
        cycles = 0;
        pc &= 0x00ff_ffff;
        if (pc < 0x00200000)
            return false;

        _lastRecoveredInvalidPc = pc;
        if (UseNativeF3TrapScheduler)
            return false;

        var state = _mainCpu.GetState();
        if (_gameStartAccepted && pc == 0x00ff_ffff)
        {
            uint safeIdlePc = 0x002312;
            ushort idlePrefetch = _bus.ReadOpcodeWord(safeIdlePc);
            _mainCpu.SetState(CreateF3IdleState(state, safeIdlePc, idlePrefetch, state.Usp, state.Ssp));
            cycles = (uint)((int)(MainClockHz / TargetFps) * CpuScale);
            return true;
        }

        if (_f3TaskQueue.Count > 0 && TryDispatchNextF3Task(state, out cycles))
            return true;
        if (_gameStartAccepted && _lastPersistentYieldTask is { } persistentTask)
        {
            EnqueueF3Task(persistentTask);
            ulong dispatchedBefore = _f3TasksDispatched;
            if (TryDispatchNextF3Task(state, out cycles))
            {
                if (_f3TasksDispatched == dispatchedBefore)
                {
                    uint safeIdlePc = 0x002312;
                    ushort idlePrefetch = _bus.ReadOpcodeWord(safeIdlePc);
                    _mainCpu.SetState(CreateF3IdleState(state, safeIdlePc, idlePrefetch, state.Usp, state.Ssp));
                }
                return true;
            }
        }

        uint idlePc = 0x002312;
        ushort prefetch = _bus.ReadOpcodeWord(idlePc);
        _mainCpu.SetState(CreateF3IdleState(state, idlePc, prefetch, state.Usp, state.Ssp));
        cycles = (uint)((int)(MainClockHz / TargetFps) * CpuScale);
        return true;
    }

    private bool TryDispatchF3QueuedTask(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (UseNativeF3TrapScheduler)
            return false;
        if (_f3TaskQueue.Count == 0)
            return TryHoldF3IdleLoop(pc, out cycles);

        bool startedIdleDispatch = _gameStartAccepted && pc is 0x002312 or 0x002320;
        if (pc != 0x01014c && pc != 0x010170 && pc != 0x002326 && !startedIdleDispatch)
            return false;

        var state = _mainCpu.GetState();
        return TryDispatchNextF3Task(state, out cycles);
    }

    private static ushort F3IdleStatusRegister(ushort sr)
        => (ushort)(sr & 0xf0ff);

    private static ushort F3TaskStatusRegister(ushort sr)
        => (ushort)(sr & 0xf0ff);

    private static M68000.M68000State CreateF3IdleState(M68000.M68000State source, uint pc, ushort prefetch, uint usp, uint ssp)
    {
        uint[] data = CloneRegisters(source.Data);
        uint[] address = CloneRegisters(source.Address);
        address[5] = F3SchedulerA5;
        address[6] = F3SchedulerA5 - 0x194c;
        return new M68000.M68000State(data, address, usp, ssp, F3IdleStatusRegister(source.Sr), pc, prefetch);
    }

    private bool TryDispatchNextF3Task(M68000.M68000State state, out uint cycles)
    {
        int scanCount = _f3TaskQueue.Count;
        while (scanCount-- > 0)
        {
            F3TaskState delayed = _f3TaskQueue.Dequeue();
            if (delayed.DelayFrames == 0)
            {
                return DispatchF3Task(delayed, out cycles);
            }

            _f3TaskQueue.Enqueue(delayed.WithDelay(delayed.DelayFrames - 1));
        }

        cycles = (uint)((int)(MainClockHz / TargetFps) * CpuScale);
        return true;
    }

    private bool TryHoldF3IdleLoop(uint pc, out uint cycles)
    {
        cycles = 0;
        if (pc is not (0x002312 or 0x002320 or 0x002326))
            return false;
        if (_gameStartAccepted && _lastPersistentYieldTask is { } persistentTask)
        {
            EnqueueF3Task(persistentTask);
            var dispatchState = _mainCpu.GetState();
            return TryDispatchNextF3Task(dispatchState, out cycles);
        }

        var state = _mainCpu.GetState();
        uint idlePc = 0x002312;
        ushort prefetch = _bus.ReadOpcodeWord(idlePc);
        _mainCpu.SetState(CreateF3IdleState(state, idlePc, prefetch, state.Usp, state.Ssp));
        cycles = (uint)((int)(MainClockHz / TargetFps) * CpuScale);
        return true;
    }

    private bool DispatchF3Task(F3TaskState task, out uint cycles)
    {
        _f3TasksDispatched++;
        _lastF3TaskEntry = task.Pc;
        _currentF3TaskPriority = task.Priority;
        if (IsF3SceneFastContinuation(task.Pc))
            _sceneContinuationDispatched++;
        RecordRecentTask(_recentF3DispatchedTasks, ref _recentF3DispatchedIndex, task.Pc);
        _lastF3TaskStack = (task.State.Sr & 0x2000) != 0 ? task.State.Ssp : task.State.Usp;
        _mainCpu.SetState(task.State);
        _m68ec020ProbeInstructions++;
        cycles = 34;
        return true;
    }

    private static bool IsSkippableF3BringupTask(uint entry)
        => false;

    private void EnqueueF3Task(F3TaskState task, bool preferFront = false)
    {
        RemoveDuplicateF3Task(task);
        if (!preferFront || _f3TaskQueue.Count == 0)
        {
            _f3TaskQueue.Enqueue(task);
            return;
        }

        int count = _f3TaskQueue.Count;
        _f3TaskQueue.Enqueue(task);
        while (count-- > 0)
            _f3TaskQueue.Enqueue(_f3TaskQueue.Dequeue());
    }

    private void RemoveDuplicateF3Task(F3TaskState incoming)
    {
        int count = _f3TaskQueue.Count;
        while (count-- > 0)
        {
            F3TaskState task = _f3TaskQueue.Dequeue();
            if (task.Pc == incoming.Pc
                && task.Priority == incoming.Priority
                && F3TaskStackKey(task) == F3TaskStackKey(incoming))
                continue;

            _f3TaskQueue.Enqueue(task);
        }
    }

    private static uint F3TaskStackKey(F3TaskState task)
        => (task.State.Sr & 0x2000) != 0 ? task.State.Ssp : task.State.Usp;

    private static bool IsF3SceneFastContinuation(uint pc)
    {
        return false;
    }

    private static bool IsPersistentF3YieldContinuation(uint trapPc, uint continuation, int priority)
        => trapPc == 0x13f2ca && continuation == 0x13f2cc && priority == 6;

    private static void RecordRecentTask(uint[] ring, ref int nextIndex, uint pc)
    {
        ring[nextIndex & (ring.Length - 1)] = pc;
        nextIndex++;
    }

    private void RemoveF3TasksByMask(uint mask, uint pc)
    {
        _lastF3TaskRemoveMask = mask;
        if (_f3TaskQueue.Count == 0)
            return;

        int count = _f3TaskQueue.Count;
        while (count-- > 0)
        {
            F3TaskState task = _f3TaskQueue.Dequeue();
            uint bit = task.Priority < 32 ? 1u << task.Priority : 0;
            if ((mask & bit) == 0)
            {
                _f3TaskQueue.Enqueue(task);
            }
            else if (IsF3SceneFastContinuation(task.Pc))
            {
                _sceneContinuationRemoved++;
            }
        }
    }

    private F3TaskState CreateNewF3TaskState(M68000.M68000State source, uint pc, int priority)
    {
        uint stack = AllocateF3TaskStack();
        _bus.WriteLong(stack, 0x002326);
        ushort prefetch = _bus.ReadOpcodeWord(pc);
        uint[] data = CloneRegisters(source.Data);
        uint[] address = CloneRegisters(source.Address);
        ushort sr = F3TaskStatusRegister(source.Sr);
        uint usp = (sr & 0x2000) != 0 ? source.Usp : stack;
        uint ssp = (sr & 0x2000) != 0 ? stack : source.Ssp;
        return new F3TaskState(pc, new M68000.M68000State(data, address, usp, ssp, sr, pc, prefetch), priority);
    }

    private F3TaskState CreateContinuationF3TaskState(M68000.M68000State source, uint pc, int delayFrames = 0, int priority = 0)
    {
        ushort prefetch = _bus.ReadOpcodeWord(pc);
        var state = new M68000.M68000State(CloneRegisters(source.Data), CloneRegisters(source.Address), source.Usp, source.Ssp, F3TaskStatusRegister(source.Sr), pc, prefetch);
        return new F3TaskState(pc, state, priority, delayFrames);
    }

    private uint AllocateF3TaskStack()
    {
        uint stack = _nextF3TaskStack;
        _nextF3TaskStack = _nextF3TaskStack > 0x0041_1000 ? _nextF3TaskStack - 0x800u : 0x0041_f000u;
        return stack;
    }

    private static uint[] CloneRegisters(uint[] source)
    {
        var clone = new uint[source.Length];
        Array.Copy(source, clone, source.Length);
        return clone;
    }

    private bool TryForceInitialTaskBackupOk(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x004e3c || op != 0x4eb9 || _lastF3TaskStack == 0)
            return false;
        if ((_bus.ReadLong(pc + 2) & 0x00ff_ffff) != 0x01014e)
            return false;

        var state = _mainCpu.GetState();
        uint sp = (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp;
        uint returnPc = _bus.ReadLong(sp + 4) & 0x00ff_ffff;
        if (returnPc >= 0x000100 && returnPc < 0x200000)
            return false;

        state.Data[0] = 0;
        uint nextPc = 0x010180;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        uint usp = (state.Sr & 0x2000) != 0 ? state.Usp : _lastF3TaskStack;
        uint ssp = (state.Sr & 0x2000) != 0 ? _lastF3TaskStack : state.Ssp;
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, usp, ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 44;
        return true;
    }

    private bool TryBypassWaitAMomentGate(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x010420 || op != 0x082d)
            return false;
        if (_f3TasksEnqueued != 0)
            return false;

        var state = _mainCpu.GetState();
        uint nextPc = 0x010478;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 16;
        return true;
    }

    private bool TryBypassCreditStartGate(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x0102e4 || op != 0x4eb9 || (_bus.ReadLong(pc + 2) & 0x00ff_ffff) != 0x000fec)
            return false;

        var state = _mainCpu.GetState();
        state.Data[0] = _bus.HasRomCredit && _bus.IsStartLatched ? 0u : 0xffff_ffffu;
        uint nextPc = 0x0102ea;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 20;
        return true;
    }

    private bool TryBypassCoinErrorStatusGate(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x004404 || op != 0x4eb9 || (_bus.ReadLong(pc + 2) & 0x00ff_ffff) != 0x000fec)
            return false;

        var state = _mainCpu.GetState();
        state.Data[0] = 0;
        uint nextPc = 0x00440a;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 20;
        return true;
    }

    private bool TryBypassBackupSettingsGate(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x01017a || op != 0x4eb9 || (_bus.ReadLong(pc + 2) & 0x00ff_ffff) != 0x0050dc)
            return false;

        _bus.EnsureBackupDefaults();
        var state = _mainCpu.GetState();
        state.Data[0] = 0;
        uint nextPc = 0x010180;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 20;
        return true;
    }

    private bool TryForceBackupCheckResult(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x010180 || op != 0x4a80)
            return false;

        var state = _mainCpu.GetState();
        state.Data[0] = 0;
        ushort sr = UpdateCcr(state.Sr, negative: false, zero: true, overflow: false, carry: false);
        uint nextPc = 0x010182;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 4;
        return true;
    }

    private bool TryExecuteMovec(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        var state = _mainCpu.GetState();
        if ((state.Sr & 0x2000) == 0)
            return false;

        ushort extension = _bus.ReadOpcodeWord(pc + 2);
        int register = (extension >> 12) & 15;
        ushort controlRegister = (ushort)(extension & 0x0fff);
        uint usp = state.Usp;

        if (op == 0x4e7a)
        {
            uint value = controlRegister switch
            {
                0x000 => _bus.SourceFunctionCode,
                0x001 => _bus.DestinationFunctionCode,
                0x002 => _bus.CacheControl,
                0x800 => state.Usp,
                0x801 => _bus.VectorBase,
                0x802 => _bus.CacheAddress,
                _ => 0
            };

            if (!IsSupportedMovecControlRegister(controlRegister))
                return false;

            SetMovecRegisterValue(state, register, value);
        }
        else
        {
            uint value = GetMovecRegisterValue(state, register);
            switch (controlRegister)
            {
                case 0x000:
                    _bus.SourceFunctionCode = value & 7;
                    break;
                case 0x001:
                    _bus.DestinationFunctionCode = value & 7;
                    break;
                case 0x002:
                    _bus.CacheControl = value;
                    break;
                case 0x800:
                    usp = value;
                    break;
                case 0x801:
                    _bus.VectorBase = value & 0x00ff_ffff;
                    _mainCpu.VectorBase = _bus.VectorBase;
                    break;
                case 0x802:
                    _bus.CacheAddress = value;
                    break;
                default:
                    return false;
            }
        }

        uint nextPc = (pc + 4) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 12;
        return true;
    }

    private bool TryExecuteExtByteToLong(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        int register = op & 7;
        var state = _mainCpu.GetState();
        uint value = unchecked((uint)(sbyte)(byte)state.Data[register]);
        state.Data[register] = value;
        ushort sr = UpdateCcr(state.Sr, (value & 0x8000_0000u) != 0, value == 0, overflow: false, carry: false);
        uint nextPc = (pc + 2) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 4;
        return true;
    }

    private bool TryBypassKnownFioSelfTestFatal(uint pc, out uint cycles)
    {
        cycles = 0;
        if (pc != 0x000f3c && pc != 0x000f3e)
            return false;

        var state = _mainCpu.GetState();
        uint sp = (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp;
        uint returnPc = _bus.ReadLong(sp) & 0x00ff_ffff;
        sp = (sp + 4) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(returnPc);
        uint usp = (state.Sr & 0x2000) != 0 ? state.Usp : sp;
        uint ssp = (state.Sr & 0x2000) != 0 ? sp : state.Ssp;
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, usp, ssp, state.Sr, returnPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 16;
        return true;
    }

    private bool TryBypassBackupRamInitReset(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        uint nextPc = pc switch
        {
            0x004e3c => 0x004e42,
            0x005138 => 0x00513e,
            _ => 0
        };
        if (nextPc == 0 || op != 0x4eb9 || (_bus.ReadLong(pc + 2) & 0x00ff_ffff) != 0x01014e)
            return false;

        var state = _mainCpu.GetState();
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 18;
        return true;
    }

    private bool TryExecuteBtstImmediateByteDisplacement(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if ((op & 0xfff8) != 0x0828)
            return false;

        var state = _mainCpu.GetState();
        int addressRegister = op & 7;
        int bit = _bus.ReadOpcodeWord(pc + 2) & 7;
        short displacement = unchecked((short)_bus.ReadOpcodeWord(pc + 4));
        uint address = unchecked(state.Address[addressRegister] + (uint)(int)displacement) & 0x00ff_ffff;
        byte value = _bus.ReadByte(address);
        _bus.LastBtstAddress = address;
        _bus.LastBtstValue = value;
        _bus.LastBtstBit = bit;
        bool zero = (value & (1 << bit)) == 0;
        if (pc is 0x004d3e or 0x004d5a)
        {
            _bus.LastModeBtstPc = pc;
            _bus.LastModeBtstAddress = address;
            _bus.LastModeBtstValue = value;
            _bus.LastModeBtstBit = bit;
            _bus.LastModeBtstZero = zero;
        }
        ushort sr = zero ? (ushort)(state.Sr | 0x0004) : (ushort)(state.Sr & ~0x0004);
        uint nextPc = (pc + 6) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 12;
        return true;
    }

    private static bool IsSupportedMovecControlRegister(ushort controlRegister)
        => controlRegister is 0x000 or 0x001 or 0x002 or 0x800 or 0x801 or 0x802;

    private static uint GetMovecRegisterValue(M68000.M68000State state, int register)
        => register < 8 ? state.Data[register] : state.Address[register - 8];

    private static void SetMovecRegisterValue(M68000.M68000State state, int register, uint value)
    {
        if (register < 8)
            state.Data[register] = value;
        else
            state.Address[register - 8] = value;
    }

    private bool TryExecuteBfextuDisplacement(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        int addressRegister = op & 7;
        ushort extension = _bus.ReadOpcodeWord(pc + 2);
        short displacement = unchecked((short)_bus.ReadOpcodeWord(pc + 4));
        var state = _mainCpu.GetState();
        int offset = (extension & 0x0800) != 0
            ? unchecked((int)state.Data[(extension >> 6) & 7])
            : (extension >> 6) & 31;
        uint widthRaw = (extension & 0x0020) != 0
            ? state.Data[extension & 7]
            : (uint)(extension & 31);
        int width = (int)(((widthRaw - 1) & 31) + 1);
        uint ea = unchecked(state.Address[addressRegister] + (uint)(int)displacement);

        if ((extension & 0x0800) != 0)
        {
            ea = unchecked(ea + (uint)(offset / 8));
            offset %= 8;
            if (offset < 0)
            {
                offset += 8;
                ea--;
            }
        }

        uint aligned = ReadBitfieldWindow(ea, offset, width);
        uint extracted = aligned >> (32 - width);
        state.Data[(extension >> 12) & 7] = extracted;

        bool negative = (aligned & 0x8000_0000u) != 0;
        bool zero = extracted == 0;
        ushort sr = UpdateCcr(state.Sr, negative, zero, overflow: false, carry: false);
        uint nextPc = (pc + 6) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 20;
        return true;
    }

    private bool TryExecuteBfextuDataRegister(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        int sourceRegister = op & 7;
        ushort extension = _bus.ReadOpcodeWord(pc + 2);
        var state = _mainCpu.GetState();
        int offset = (extension & 0x0800) != 0
            ? unchecked((int)state.Data[(extension >> 6) & 7])
            : (extension >> 6) & 31;
        uint widthRaw = (extension & 0x0020) != 0
            ? state.Data[extension & 7]
            : (uint)(extension & 31);
        int width = (int)(((widthRaw - 1) & 31) + 1);
        int bitOffset = offset & 31;

        uint source = state.Data[sourceRegister];
        ulong repeated = ((ulong)source << 32) | source;
        int shift = 64 - bitOffset - width;
        uint extracted = shift >= 0
            ? (uint)(repeated >> shift)
            : (uint)(repeated << -shift);
        extracted &= width == 32 ? uint.MaxValue : (uint)((1UL << width) - 1UL);
        state.Data[(extension >> 12) & 7] = extracted;

        bool negative = (extracted & (1u << (width - 1))) != 0;
        bool zero = extracted == 0;
        ushort sr = UpdateCcr(state.Sr, negative, zero, overflow: false, carry: false);
        uint nextPc = (pc + 4) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 12;
        return true;
    }

    private bool TryExecuteBfinsDisplacement(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        int addressRegister = op & 7;
        ushort extension = _bus.ReadOpcodeWord(pc + 2);
        short displacement = unchecked((short)_bus.ReadOpcodeWord(pc + 4));
        var state = _mainCpu.GetState();
        uint ea = unchecked(state.Address[addressRegister] + (uint)(int)displacement);
        return ExecuteBfins(pc, extension, state, ea, instructionLength: 6, out cycles);
    }

    private bool TryExecuteBfinsDataRegister(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        int destinationRegister = op & 7;
        ushort extension = _bus.ReadOpcodeWord(pc + 2);
        var state = _mainCpu.GetState();
        int offset = (extension & 0x0800) != 0
            ? unchecked((int)state.Data[(extension >> 6) & 7])
            : (extension >> 6) & 31;
        uint widthRaw = (extension & 0x0020) != 0
            ? state.Data[extension & 7]
            : (uint)(extension & 31);
        int width = (int)(((widthRaw - 1) & 31) + 1);
        int bitOffset = offset & 31;

        uint insert = width == 32
            ? state.Data[(extension >> 12) & 7]
            : state.Data[(extension >> 12) & 7] & (uint)((1UL << width) - 1UL);
        uint destination = state.Data[destinationRegister];
        for (int bit = 0; bit < width; bit++)
        {
            int destinationBit = 31 - ((bitOffset + bit) & 31);
            uint mask = 1u << destinationBit;
            uint sourceBit = (insert >> (width - 1 - bit)) & 1u;
            destination = sourceBit == 0
                ? destination & ~mask
                : destination | mask;
        }

        state.Data[destinationRegister] = destination;
        bool negative = (insert & (1u << (width - 1))) != 0;
        bool zero = insert == 0;
        ushort sr = UpdateCcr(state.Sr, negative, zero, overflow: false, carry: false);
        uint nextPc = (pc + 4) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 12;
        return true;
    }

    private bool TryExecuteBfinsIndexed(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        int addressRegister = op & 7;
        ushort extension = _bus.ReadOpcodeWord(pc + 2);
        ushort indexExtension = _bus.ReadOpcodeWord(pc + 4);
        var state = _mainCpu.GetState();
        uint ea = CalculateBriefIndexedAddress(state, state.Address[addressRegister], indexExtension);
        return ExecuteBfins(pc, extension, state, ea, instructionLength: 6, out cycles);
    }

    private bool TryExecuteBfffo(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        ushort extension = _bus.ReadOpcodeWord(pc + 2);
        var state = _mainCpu.GetState();
        if (!TryReadBitfieldOperand(pc, op, extension, state, normalizeMemoryOffset: true, out uint aligned, out uint extracted, out int offset, out int width, out _, out int instructionLength))
            return false;

        int resultOffset = offset;
        for (uint bit = 1u << (width - 1); bit != 0 && (extracted & bit) == 0; bit >>= 1)
            resultOffset++;

        state.Data[(extension >> 12) & 7] = (uint)resultOffset;
        bool negative = (aligned & 0x8000_0000u) != 0;
        bool zero = extracted == 0;
        ushort sr = UpdateCcr(state.Sr, negative, zero, overflow: false, carry: false);
        uint nextPc = (pc + (uint)instructionLength) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 28;
        return true;
    }

    private bool TryExecuteBfclr(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        ushort extension = _bus.ReadOpcodeWord(pc + 2);
        var state = _mainCpu.GetState();
        if (!TryReadBitfieldOperand(pc, op, extension, state, normalizeMemoryOffset: true, out uint aligned, out uint extracted, out int offset, out int width, out uint ea, out int instructionLength))
            return false;

        if ((op & 0x38) == 0)
        {
            int destinationRegister = op & 7;
            uint destination = state.Data[destinationRegister];
            for (int bit = 0; bit < width; bit++)
                destination &= ~(1u << (31 - ((offset + bit) & 31)));
            state.Data[destinationRegister] = destination;
        }
        else
        {
            uint maskBase = unchecked(uint.MaxValue << (32 - width));
            uint maskLong = maskBase >> offset;
            uint dataLong = _bus.ReadLong(ea);
            uint clearedLong = dataLong & ~maskLong;

            _bus.WriteLong(ea, clearedLong);

            if (offset + width > 32)
            {
                byte maskByte = (byte)((byte)maskBase << (8 - offset));
                byte dataByte = _bus.ReadByte(ea + 4);
                _bus.WriteByte(ea + 4, (byte)(dataByte & ~maskByte));
            }
        }

        bool negative = (aligned & 0x8000_0000u) != 0;
        bool zero = extracted == 0;
        ushort sr = UpdateCcr(state.Sr, negative, zero, overflow: false, carry: false);
        uint nextPc = (pc + (uint)instructionLength) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 28;
        return true;
    }

    private bool TryExecuteBfset(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        ushort extension = _bus.ReadOpcodeWord(pc + 2);
        var state = _mainCpu.GetState();
        if (!TryReadBitfieldOperand(pc, op, extension, state, normalizeMemoryOffset: true, out uint aligned, out uint extracted, out int offset, out int width, out uint ea, out int instructionLength))
            return false;

        if ((op & 0x38) == 0)
        {
            int destinationRegister = op & 7;
            uint destination = state.Data[destinationRegister];
            for (int bit = 0; bit < width; bit++)
                destination |= 1u << (31 - ((offset + bit) & 31));
            state.Data[destinationRegister] = destination;
        }
        else
        {
            uint maskBase = unchecked(uint.MaxValue << (32 - width));
            uint maskLong = maskBase >> offset;
            uint dataLong = _bus.ReadLong(ea);
            uint setLong = dataLong | maskLong;

            _bus.WriteLong(ea, setLong);

            if (offset + width > 32)
            {
                byte maskByte = (byte)((byte)maskBase << (8 - offset));
                byte dataByte = _bus.ReadByte(ea + 4);
                _bus.WriteByte(ea + 4, (byte)(dataByte | maskByte));
            }
        }

        bool negative = (aligned & 0x8000_0000u) != 0;
        bool zero = extracted == 0;
        ushort sr = UpdateCcr(state.Sr, negative, zero, overflow: false, carry: false);
        uint nextPc = (pc + (uint)instructionLength) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 28;
        return true;
    }

    private bool TryReadBitfieldOperand(
        uint pc,
        ushort op,
        ushort extension,
        M68000.M68000State state,
        bool normalizeMemoryOffset,
        out uint aligned,
        out uint extracted,
        out int offset,
        out int width,
        out uint ea,
        out int instructionLength)
    {
        aligned = 0;
        extracted = 0;
        ea = 0;
        instructionLength = 4;
        offset = (extension & 0x0800) != 0
            ? unchecked((int)state.Data[(extension >> 6) & 7])
            : (extension >> 6) & 31;
        uint widthRaw = (extension & 0x0020) != 0
            ? state.Data[extension & 7]
            : (uint)(extension & 31);
        width = (int)(((widthRaw - 1) & 31) + 1);

        int mode = (op >> 3) & 7;
        int reg = op & 7;
        if (mode == 0)
        {
            offset &= 31;
            uint source = state.Data[reg];
            aligned = RotateLeft32(source, offset);
            extracted = width == 32 ? aligned : aligned >> (32 - width);
            return true;
        }

        if (!TryResolveSimpleBitfieldEa(pc, mode, reg, state, out ea, out instructionLength))
            return false;

        if (normalizeMemoryOffset || (extension & 0x0800) != 0)
        {
            ea = unchecked(ea + (uint)(offset / 8));
            offset %= 8;
            if (offset < 0)
            {
                offset += 8;
                ea--;
            }
        }

        aligned = ReadBitfieldWindow(ea, offset, width);
        extracted = width == 32 ? aligned : aligned >> (32 - width);
        return true;
    }

    private bool TryResolveSimpleBitfieldEa(uint pc, int mode, int reg, M68000.M68000State state, out uint ea, out int instructionLength)
    {
        instructionLength = 4;
        ea = 0;
        switch (mode)
        {
            case 2:
                ea = state.Address[reg];
                return true;
            case 5:
                ea = unchecked(state.Address[reg] + (uint)(int)(short)_bus.ReadOpcodeWord(pc + 4));
                instructionLength = 6;
                return true;
            case 6:
                ea = CalculateBriefIndexedAddress(state, state.Address[reg], _bus.ReadOpcodeWord(pc + 4));
                instructionLength = 6;
                return true;
            case 7 when reg == 0:
                ea = unchecked((uint)(int)(short)_bus.ReadOpcodeWord(pc + 4));
                instructionLength = 6;
                return true;
            case 7 when reg == 1:
                ea = _bus.ReadLong(pc + 4);
                instructionLength = 8;
                return true;
            default:
                return false;
        }
    }

    private bool ExecuteBfins(uint pc, ushort extension, M68000.M68000State state, uint ea, int instructionLength, out uint cycles)
    {
        cycles = 0;
        int offset = (extension & 0x0800) != 0
            ? unchecked((int)state.Data[(extension >> 6) & 7])
            : (extension >> 6) & 31;
        uint widthRaw = (extension & 0x0020) != 0
            ? state.Data[extension & 7]
            : (uint)(extension & 31);
        int width = (int)(((widthRaw - 1) & 31) + 1);

        if ((extension & 0x0800) != 0)
        {
            ea = unchecked(ea + (uint)(offset / 8));
            offset %= 8;
            if (offset < 0)
            {
                offset += 8;
                ea--;
            }
        }

        uint insertBase = unchecked(state.Data[(extension >> 12) & 7] << (32 - width));
        uint maskBase = unchecked(uint.MaxValue << (32 - width));
        uint maskLong = maskBase >> offset;
        uint insertLong = insertBase >> offset;
        uint dataLong = offset + width <= 8
            ? (uint)_bus.ReadByte(ea) << 24
            : offset + width <= 16
                ? (uint)_bus.ReadWord(ea) << 16
                : _bus.ReadLong(ea);
        uint mergedLong = (dataLong & ~maskLong) | insertLong;

        if (offset + width <= 8)
            _bus.WriteByte(ea, (byte)(mergedLong >> 24));
        else if (offset + width <= 16)
            _bus.WriteWord(ea, (ushort)(mergedLong >> 16));
        else
            _bus.WriteLong(ea, mergedLong);

        if (offset + width > 32)
        {
            byte maskByte = (byte)((byte)maskBase << (8 - offset));
            byte insertByte = (byte)((byte)insertBase << (8 - offset));
            byte dataByte = _bus.ReadByte(ea + 4);
            _bus.WriteByte(ea + 4, (byte)((dataByte & ~maskByte) | insertByte));
        }

        bool negative = (insertBase & 0x8000_0000u) != 0;
        bool zero = insertBase == 0;
        ushort sr = UpdateCcr(state.Sr, negative, zero, overflow: false, carry: false);
        uint nextPc = (pc + (uint)instructionLength) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 20;
        return true;
    }

    private bool TryExecuteAddSubQuickDisplacement(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        int sizeCode = (op >> 6) & 3;
        int addressRegister = op & 7;
        int quick = ((op >> 9) & 7) == 0 ? 8 : (op >> 9) & 7;
        bool subtract = (op & 0x0100) != 0;
        short displacement = unchecked((short)_bus.ReadOpcodeWord(pc + 2));
        var state = _mainCpu.GetState();
        uint address = unchecked(state.Address[addressRegister] + (uint)(int)displacement) & 0x00ff_ffff;
        int bits = sizeCode == 0 ? 8 : sizeCode == 1 ? 16 : 32;
        uint mask = bits == 32 ? uint.MaxValue : (1u << bits) - 1u;
        uint sign = 1u << (bits - 1);
        uint destination = sizeCode switch
        {
            0 => _bus.ReadByte(address),
            1 => _bus.ReadWord(address),
            _ => _bus.ReadLong(address)
        } & mask;
        uint source = (uint)quick & mask;
        ulong raw = subtract
            ? (ulong)destination - source
            : (ulong)destination + source;
        uint result = (uint)raw & mask;

        if (sizeCode == 0)
            _bus.WriteByte(address, (byte)result);
        else if (sizeCode == 1)
            _bus.WriteWord(address, (ushort)result);
        else
            _bus.WriteLong(address, result);

        bool negative = (result & sign) != 0;
        bool zero = result == 0;
        bool overflow = subtract
            ? ((destination ^ source) & (destination ^ result) & sign) != 0
            : (~(destination ^ source) & (destination ^ result) & sign) != 0;
        bool carry = subtract ? destination < source : raw > mask;
        ushort sr = UpdateAddSubCcr(state.Sr, negative, zero, overflow, carry);
        uint nextPc = (pc + 4) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = sizeCode == 2 ? 20u : 16u;
        return true;
    }

    private bool TryExecuteAddWordIndexedToData(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        int destinationRegister = (op >> 9) & 7;
        int addressRegister = op & 7;
        ushort extension = _bus.ReadOpcodeWord(pc + 2);
        var state = _mainCpu.GetState();
        uint ea = CalculateBriefIndexedAddress(state, state.Address[addressRegister], extension);
        ushort source = ReadUnalignedWord(ea);
        ushort destination = (ushort)state.Data[destinationRegister];
        int result = destination + source;
        state.Data[destinationRegister] = (state.Data[destinationRegister] & 0xffff_0000u) | (ushort)result;

        bool negative = ((ushort)result & 0x8000) != 0;
        bool zero = (ushort)result == 0;
        bool overflow = (~(destination ^ source) & (destination ^ result) & 0x8000) != 0;
        bool carry = result > 0xffff;
        ushort sr = UpdateCcr(state.Sr, negative, zero, overflow, carry);
        uint nextPc = (pc + 4) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = 14;
        return true;
    }

    private uint ReadBitfieldWindow(uint ea, int offset, int width)
    {
        uint data = offset + width < 8
            ? (uint)_bus.ReadByte(ea) << 24
            : offset + width < 16
                ? (uint)_bus.ReadWord(ea) << 16
                : _bus.ReadLong(ea);
        data = unchecked(data << offset);
        if (offset + width > 32)
            data |= (uint)(_bus.ReadByte(ea + 4) << offset) >> 8;
        return data;
    }

    private ushort ReadUnalignedWord(uint address)
        => (ushort)((_bus.ReadByte(address) << 8) | _bus.ReadByte(address + 1));

    private static ushort UpdateCcr(ushort sr, bool negative, bool zero, bool overflow, bool carry)
    {
        ushort next = (ushort)(sr & 0xffe0);
        next |= (ushort)(sr & 0x0010);
        if (negative) next |= 0x0008;
        if (zero) next |= 0x0004;
        if (overflow) next |= 0x0002;
        if (carry) next |= 0x0001;
        return next;
    }

    private static ushort UpdateAddSubCcr(ushort sr, bool negative, bool zero, bool overflow, bool carry)
    {
        ushort next = (ushort)(sr & 0xffe0);
        if (carry) next |= 0x0010;
        if (negative) next |= 0x0008;
        if (zero) next |= 0x0004;
        if (overflow) next |= 0x0002;
        if (carry) next |= 0x0001;
        return next;
    }

    private static ushort UpdateSubWordCcr(ushort sr, ushort source, ushort destination, ushort result)
    {
        bool negative = (result & 0x8000) != 0;
        bool zero = result == 0;
        bool overflow = ((destination ^ source) & (destination ^ result) & 0x8000) != 0;
        bool carry = source > destination;
        return UpdateAddSubCcr(sr, negative, zero, overflow, carry);
    }

    private static uint RotateLeft32(uint value, int count)
    {
        count &= 31;
        return count == 0
            ? value
            : (value << count) | (value >> (32 - count));
    }

    private static uint CalculateBriefIndexedAddress(M68000.M68000State state, uint baseAddress, ushort extension)
    {
        int register = (extension >> 12) & 7;
        bool addressIndex = (extension & 0x8000) != 0;
        bool longIndex = (extension & 0x0800) != 0;
        int scale = 1 << ((extension >> 9) & 3);
        int displacement = unchecked((sbyte)(extension & 0xff));
        uint rawIndex = addressIndex
            ? register == 7
                ? ((state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp)
                : state.Address[register]
            : state.Data[register];
        int index = longIndex ? unchecked((int)rawIndex) : unchecked((short)rawIndex);
        return unchecked(baseAddress + (uint)(index * scale + displacement));
    }

    private bool RenderPlayfields(TaitoF3RomSet roms)
    {
        _lastPlayfieldCandidates = 0;
        _lastPlayfieldPixels = 0;
        if (RenderStats)
        {
            Array.Clear(_lastPlayfieldLayerCandidates);
            Array.Clear(_lastPlayfieldLayerPixels);
            Array.Clear(_lastPlayfieldBlendSelect0);
            Array.Clear(_lastPlayfieldBlendSelect1);
        }
        Span<int> regSx = stackalloc int[4];
        Span<int> regFxY = stackalloc int[4];
        for (int layer = 0; layer < 4; layer++)
            GetMamePlayfieldScroll(layer, out regSx[layer], out regFxY[layer]);

        bool drewAny = false;
        Span<int> layerOrder = stackalloc int[4];
        Span<int> layerPriority = stackalloc int[4];
        for (int screenY = 0; screenY < FrameHeight; screenY++)
        {
            int screenLine = screenY + VisibleAreaMinY;
            BuildMamePlayfieldOrder(screenLine, layerOrder, layerPriority);
            for (int i = 0; i < layerOrder.Length; i++)
            {
                int layer = layerOrder[i];
                drewAny |= RenderPlayfieldLine(roms, layer, screenY, regSx[layer], regFxY[layer]);
            }

            if (screenY != 0)
            {
                F3LineState line = _lineStates[screenLine & 0xff];
                for (int layer = 0; layer < 4; layer++)
                    regFxY[layer] += line.PlayfieldYScale[layer];
            }
        }

        return drewAny;
    }

    private void BuildMamePlayfieldOrder(int screenLine, Span<int> order, Span<int> priority)
    {
        order[0] = 0;
        order[1] = 3;
        order[2] = 2;
        order[3] = 1;

        for (int i = 0; i < order.Length; i++)
        {
            int layer = order[i];
            priority[i] = TryReadMixableCurrentState(screenLine, 7, layer, spriteRules: false, out int layerPriority, out _, out _)
                ? layerPriority
                : -1;
        }

        for (int i = 1; i < order.Length; i++)
        {
            int layer = order[i];
            int layerPriority = priority[i];
            int j = i - 1;
            while (j >= 0 && layerPriority > priority[j])
            {
                order[j + 1] = order[j];
                priority[j + 1] = priority[j];
                j--;
            }

            order[j + 1] = layer;
            priority[j + 1] = layerPriority;
        }
    }

    private bool RenderPlayfieldLine(TaitoF3RomSet roms, int layer, int screenY, int regSx, int regFxY)
    {
        const int tileSize = 16;
        const int mapTiles = 32;
        int lineRamLayer = layer & 3;
        int screenLine = screenY + VisibleAreaMinY;
        if (!TryReadMixableCurrentState(screenLine, 7, lineRamLayer, spriteRules: false, out int layerPriority, out int layerBlendMode, out _))
            return false;

        F3LineState line = _lineStates[screenLine & 0xff];
        int tilemap = layer + (line.PlayfieldAltTilemap[lineRamLayer] ? 2 : 0);
        int sourceY = ((regFxY >> 8) + line.PlayfieldColScroll[lineRamLayer]) & 0x01ff;
        if (!_bus.IsPlayfieldRowUsed(sourceY >> 4, tilemap))
            return false;

        bool drewAny = false;
        int tileY = sourceY / tileSize;
        int pixelY = sourceY & 15;
        int lineRegFxX = regSx + line.PlayfieldRowScroll[lineRamLayer] + (10 * (line.PlayfieldXScale[lineRamLayer] - 0x100));
        int layerWordBase = tilemap * 0x800;
        ushort layerMixValue = line.PlayfieldMix[lineRamLayer];
        bool clippedLine = ((layerMixValue >> 8) & 0x0f) != 0;
        int tilemapElements = roms.TilemapPixels.Length >> 8;
        if (tilemapElements <= 0)
            return false;

        byte layerRank = GetPlayfieldLayerRank(lineRamLayer);
        int paletteAdd = line.PlayfieldPaletteAdd[lineRamLayer];
        int lineXScale = line.PlayfieldXScale[lineRamLayer];
        int rowOffset = screenY * FrameWidth;
        int tileRowWordBase = layerWordBase + tileY * mapTiles * 2;
        for (int tileX = 0; tileX < mapTiles; tileX++)
        {
            int entry = tileRowWordBase + tileX * 2;
            _playfieldLineAttrCache[tileX] = _bus.ReadPlayfieldWord(entry);
            int code = _bus.ReadPlayfieldWord(entry + 1);
            _playfieldLineCodeCache[tileX] = (ushort)(code < tilemapElements ? code : code % tilemapElements);
        }

        for (int screenX = 0, sourceXAccumulator = lineRegFxX; screenX < FrameWidth; screenX++, sourceXAccumulator += lineXScale)
        {
            if (clippedLine && !IsMameClipAllowed(screenLine, layerMixValue, screenX + VisibleAreaMinX))
                continue;

            int sourceX = ((sourceXAccumulator >> 8) + VisibleAreaMinX) & 0x01ff;
            int tileX = sourceX >> 4;
            int pixelX = sourceX & 15;
            ushort attr = _playfieldLineAttrCache[tileX];
            ushort code = _playfieldLineCodeCache[tileX];
            if ((attr | code) == 0)
                continue;
            if (RenderStats)
            {
                _lastPlayfieldCandidates++;
                _lastPlayfieldLayerCandidates[lineRamLayer]++;
            }

            int reefPixelX = pixelX;
            int reefPixelY = pixelY;
            if ((attr & 0x4000) != 0)
                reefPixelX = 15 - reefPixelX;
            if ((attr & 0x8000) != 0)
                reefPixelY = 15 - reefPixelY;

            int extraPlanes = (attr >> 10) & 3;
            bool tileBlendSelect = (attr & 0x0200) != 0;
            int palette = attr & 0x01ff;
            int penMask = ((extraPlanes & ~palette) << 4) | 0x0f;
            int pen = roms.TilemapPixels[(code << 8) | (reefPixelY << 4) | reefPixelX] & penMask;
            if (pen == 0)
                continue;

            if (RenderStats)
            {
                if (tileBlendSelect)
                    _lastPlayfieldBlendSelect1[lineRamLayer]++;
                else
                    _lastPlayfieldBlendSelect0[lineRamLayer]++;
            }
            WritePalettePixelAtOffset(rowOffset + screenX, (ushort)(paletteAdd + palette * 16 + pen), layerPriority, layerRank, layerBlendMode, tileBlendSelect, line);
            if (RenderStats)
            {
                _lastPlayfieldPixels++;
                _lastPlayfieldLayerPixels[lineRamLayer]++;
            }
            drewAny = true;
        }

        return drewAny;
    }

    private void GetMamePlayfieldScroll(int playfield, out int regSx, out int regSy)
    {
        int sxRaw = (short)_bus.ReadControlWord(0, playfield);
        int syRaw = (short)_bus.ReadControlWord(0, 4 + playfield);

        syRaw += 1 << 7;
        sxRaw += (40 - 4 * playfield) << 6;

        regSx = sxRaw << 2;
        regSy = syRaw << 1;
        regSx ^= 0xfc;
        regSx -= VisibleAreaMinX << 8;
    }

    private bool RenderTextLayer(TaitoF3RomSet roms)
    {
        const int tileSize = 8;
        const int columns = 64;
        const int rows = 64;
        bool drewAny = false;

        for (int row = 0; row < rows; row++)
        {
            int screenYBase = row * tileSize;
            if (screenYBase >= FrameHeight)
                break;

            for (int column = 0; column < columns; column++)
            {
                int screenXBase = column * tileSize;
                if (screenXBase >= FrameWidth)
                    break;

                ushort word = _bus.ReadTextWord(row * columns + column);
                int code = word & 0x00ff;
                if (code == 0)
                    continue;

                int palette = (word >> 9) & 0x3f;
                for (int pixelY = 0; pixelY < tileSize; pixelY++)
                {
                    int screenY = screenYBase + pixelY;
                    if ((uint)screenY >= FrameHeight)
                        continue;

                    int screenLine = screenY + VisibleAreaMinY;
                    if (!TryReadMixableCurrentState(screenLine, 3, 1, spriteRules: false, out int layerPriority, out int layerBlendMode, out bool layerBlendSelect))
                        continue;
                    F3LineState line = _lineStates[screenLine & 0xff];
                    if (PivotUsesPixelLayer(line))
                        continue;
                    ushort pivotMix = line.PivotMix;

                    for (int pixelX = 0; pixelX < tileSize; pixelX++)
                    {
                        int screenX = screenXBase + pixelX;
                        if (!IsMameClipAllowed(screenLine, pivotMix, screenX + VisibleAreaMinX))
                            continue;

                        int drawX = (word & 0x0100) != 0 ? 7 - pixelX : pixelX;
                        int drawY = (word & 0x8000) != 0 ? 7 - pixelY : pixelY;
                        int pen = DecodeF3CharPixel(code, drawX, drawY);
                        if (pen == 0)
                            continue;

                        WritePalettePixel(screenX, screenY, palette * 16 + pen, layerPriority, PivotLayerRank, layerBlendMode, layerBlendSelect);
                        drewAny = true;
                    }
                }
            }
        }

        return drewAny;
    }

    private bool RenderPivotPixelLayer(TaitoF3RomSet roms)
    {
        const int tileSize = 8;
        const int columns = 64;
        const int rows = 32;
        bool drewAny = false;

        if (_bus.PivotNonZeroWords == 0)
            return false;

        int controlX = _bus.ReadControlWord(1, 4);
        int controlY = _bus.ReadControlWord(1, 5);
        int scrollX = _flipScreen
            ? (controlX - 12) & 0x01ff
            : (-controlX - 5) & 0x01ff;
        int scrollY = _flipScreen
            ? controlY & 0x01ff
            : (-controlY) & 0x01ff;

        for (int screenY = 0; screenY < FrameHeight; screenY++)
        {
            int screenLine = screenY + VisibleAreaMinY;
            if (!TryReadMixableCurrentState(screenLine, 3, 1, spriteRules: false, out int layerPriority, out int layerBlendMode, out bool layerBlendSelect))
                continue;
            F3LineState line = _lineStates[screenLine & 0xff];
            if (!PivotUsesPixelLayer(line))
                continue;
            ushort pivotMix = line.PivotMix;

            int sourceY = (screenY + scrollY) & 0x01ff;
            int row = (sourceY >> 3) & 31;
            int pixelY = sourceY & 7;
            if (_flipScreen)
                pixelY ^= 7;

            for (int screenX = 0; screenX < FrameWidth; screenX++)
            {
                if (!IsMameClipAllowed(screenLine, pivotMix, screenX + VisibleAreaMinX))
                    continue;

                int sourceX = (screenX + scrollX) & 0x01ff;
                int column = (sourceX >> 3) & 63;
                int pixelX = sourceX & 7;
                if (_flipScreen)
                    pixelX ^= 7;
                int tileIndex = column * rows + row;
                int attributeRow = row;
                int yOffset = row * tileSize + controlY;
                if (_flipScreen)
                    yOffset += 0x100;
                if ((yOffset & 0x01ff) >= 256)
                    attributeRow += 32;

                ushort word = _bus.ReadTextWord((attributeRow << 6) | column);
                int palette = (word >> 9) & 0x3f;
                int drawX = (word & 0x0100) != 0 ? 7 - pixelX : pixelX;
                int drawY = (word & 0x8000) != 0 ? 7 - pixelY : pixelY;
                int pen = DecodeF3PivotPixel(tileIndex, drawX, drawY);
                if (pen == 0)
                    continue;

                WritePalettePixel(screenX, screenY, palette * 16 + pen, layerPriority, PivotLayerRank, layerBlendMode, layerBlendSelect);
                drewAny = true;
            }
        }

        return drewAny;
    }

    private static bool PivotUsesPixelLayer(F3LineState line)
        => (line.PivotControl & 0xa0) != 0;

    private int DecodeF3CharPixel(int code, int x, int y)
    {
        int bitOffset = code * 32 * 8 + y * 32 + GetF3CharXOffset(x);
        int byteOffset = bitOffset >> 3;
        if ((uint)byteOffset >= 0x2000)
            return 0;

        int pen = 0;
        for (int plane = 0; plane < 4; plane++)
        {
            int planeBitOffset = bitOffset + plane;
            int planeByteOffset = planeBitOffset >> 3;
            if ((uint)planeByteOffset >= 0x2000)
                continue;

            int bit = 7 - (planeBitOffset & 7);
            pen |= ((_bus.ReadCharGfxByte(planeByteOffset) >> bit) & 1) << plane;
        }
        return pen;
    }

    private int DecodeF3PivotPixel(int code, int x, int y)
    {
        int bitOffset = code * 32 * 8 + y * 32 + GetF3CharXOffset(x);
        int byteOffset = bitOffset >> 3;
        if ((uint)byteOffset >= 0x10000)
            return 0;

        int pen = 0;
        for (int plane = 0; plane < 4; plane++)
        {
            int planeBitOffset = bitOffset + plane;
            int planeByteOffset = planeBitOffset >> 3;
            if ((uint)planeByteOffset >= 0x10000)
                continue;

            int bit = 7 - (planeBitOffset & 7);
            pen |= ((_bus.ReadPivotGfxByte(planeByteOffset) >> bit) & 1) << plane;
        }

        return pen;
    }

    private static int GetF3CharXOffset(int x)
        => (x & 7) switch
        {
            0 => 20,
            1 => 16,
            2 => 28,
            3 => 24,
            4 => 4,
            5 => 0,
            6 => 12,
            _ => 8,
        };

    private bool RenderSprites(TaitoF3RomSet roms)
    {
        bool drewAny = RenderSpriteReefToFrame();
        _lastSpritePixels = 0;

        if (!_spriteTrails)
            ClearSpriteReefForNextFrame();

        int drawnSpriteCount = _latchedSprites.Count;
        bool drewIntoReef = false;
        for (int i = _latchedSprites.Count - 1; i >= 0; i--)
            drewIntoReef |= DrawSpriteToReef(roms, _latchedSprites[i]);

        BuildSpriteList();
        _latchedSprites.Clear();
        _latchedSprites.AddRange(_sprites);

        if (RenderStats)
            _lastVisibleSprites = drawnSpriteCount;
        return drewAny || drewIntoReef;
    }

    private void BuildSpriteList()
    {
        _sprites.Clear();
        _lastSpriteCandidates = 0;
        if (RenderStats)
        {
            _lastVisibleSprites = 0;
            _lastSpriteCandidateEntry = -1;
            _lastSpriteCandidateX = 0;
            _lastSpriteCandidateY = 0;
            _lastSpriteCandidateScaleX = 0;
            _lastSpriteCandidateScaleY = 0;
            _lastSpriteCandidateTile = 0;
            _lastSpriteCandidateControl = 0;
            _lastSpriteMinX = int.MaxValue;
            _lastSpriteMinY = int.MaxValue;
            _lastSpriteMaxX = int.MinValue;
            _lastSpriteMaxY = int.MinValue;
            _lastSpriteClosestDistance = int.MaxValue;
        }
        BuildSpriteListFrom(0, _spriteBank, updateSpriteBank: true);
        if (_sprites.Count == 0)
        {
            if (!TryBuildSpriteListFromPointer(_bus.PeekLong(0x407360), skipHeader: true)
                && !TryBuildSpriteListFromPointer(_bus.PeekLong(0x407364), skipHeader: true))
                TryBuildSpriteListFromPointer(_bus.PeekLong(0x407368), skipHeader: true);
        }
    }

    private bool TryBuildSpriteListFromPointer(uint pointer, bool skipHeader)
    {
        pointer &= 0x00ff_ffff;
        if (pointer < 0x600000 || pointer >= 0x610000)
            return false;

        int wordOffset = (int)((pointer - 0x600000) >> 1);
        if ((uint)wordOffset >= 0x8000)
            return false;

        bool pointerBank = (wordOffset & 0x4000) != 0;
        int startEntry = (wordOffset & 0x3fff) / 8;
        if (skipHeader)
            startEntry++;

        int previousCount = _sprites.Count;
        BuildSpriteListFrom(startEntry, pointerBank, updateSpriteBank: false);
        return _sprites.Count != previousCount;
    }

    private void BuildSpriteListFrom(int startEntry, bool initialBank, bool updateSpriteBank)
    {
        bool spriteBank = initialBank;
        var xAxis = new SpriteAxis();
        var yAxis = new SpriteAxis();
        byte color = 0;
        bool multi = false;
        int visibleMinX = VisibleAreaMinX << 8;
        int visibleMinY = VisibleAreaMinY << 8;
        int visibleMaxX = (VisibleAreaMinX + FrameWidth - 1) << 8;
        int visibleMaxY = (VisibleAreaMinY + FrameHeight - 1) << 8;

        for (int offs = startEntry, totalSprites = 0; (uint)offs < 0x400 && totalSprites < 0x400; offs++, totalSprites++)
        {
            int bank = spriteBank ? 0x4000 : 0;
            int wordOffset = bank + offs * 8;
            ushort w0 = _bus.ReadSpriteWord(wordOffset + 0);
            ushort w1 = _bus.ReadSpriteWord(wordOffset + 1);
            ushort w2 = _bus.ReadSpriteWord(wordOffset + 2);
            ushort w3 = _bus.ReadSpriteWord(wordOffset + 3);
            ushort w4 = _bus.ReadSpriteWord(wordOffset + 4);
            ushort w5 = _bus.ReadSpriteWord(wordOffset + 5);
            ushort w6 = _bus.ReadSpriteWord(wordOffset + 6);
            bool command = (w3 & 0x8000) != 0;

            if (command)
            {
                if (RenderStats)
                    _lastSpriteControlWord = w5;
                _flipScreen = (w5 & 0x2000) != 0;
                int extraPlanes = (w5 >> 8) & 3;
                _spritePenMask = (extraPlanes << 4) | 0x0f;
                _spriteTrails = (w5 & 0x0002) != 0;
                spriteBank = (w5 & 0x0001) != 0;
                if (updateSpriteBank)
                    _spriteBank = spriteBank;
            }

            if ((w6 & 0x8000) != 0)
            {
                int newOffs = w6 & 0x03ff;
                if (newOffs == offs)
                    break;
                offs = newOffs - 1;
            }

            byte spriteControl = (byte)(w4 >> 8);
            bool lockPalette = (spriteControl & 0x04) != 0;
            if (!lockPalette)
                color = (byte)w4;

            byte scrollMode = (byte)(w2 >> 12);
            xAxis.Update(scrollMode, (ushort)(w2 & 0x0fff), multi, (byte)((spriteControl >> 6) & 3), (byte)w1);
            yAxis.Update(scrollMode, (ushort)(w3 & 0x0fff), multi, (byte)((spriteControl >> 4) & 3), (byte)(w1 >> 8));
            multi = (spriteControl & 0x08) != 0;

            int tile = w0 | ((w5 & 1) << 16);
            if (tile == 0)
                continue;

            int x = _flipScreen ? (512 << 8) - xAxis.BlockScale * 16 - xAxis.Pos : xAxis.Pos;
            int y = _flipScreen ? (256 << 8) - yAxis.BlockScale * 16 - yAxis.Pos : yAxis.Pos;
            if (RenderStats)
            {
                _lastSpriteCandidates++;
                _lastSpriteCandidateEntry = offs;
                _lastSpriteCandidateX = x;
                _lastSpriteCandidateY = y;
                _lastSpriteCandidateScaleX = xAxis.BlockScale;
                _lastSpriteCandidateScaleY = yAxis.BlockScale;
                _lastSpriteCandidateTile = tile;
                _lastSpriteCandidateControl = spriteControl;
                TrackSpriteCandidateBounds(x, y, xAxis.BlockScale, yAxis.BlockScale, visibleMinX, visibleMinY, visibleMaxX, visibleMaxY);
            }
            if (x + xAxis.BlockScale * 16 <= visibleMinX || x > visibleMaxX || y + yAxis.BlockScale * 16 <= visibleMinY || y > visibleMaxY)
                continue;

            bool flipX = (spriteControl & 0x01) != 0;
            bool flipY = (spriteControl & 0x02) != 0;
            _sprites.Add(new F3Sprite(
                x - visibleMinX,
                y - visibleMinY,
                _flipScreen ? !flipX : flipX,
                _flipScreen ? !flipY : flipY,
                tile,
                color,
                xAxis.BlockScale,
                yAxis.BlockScale));
        }
    }

    private void TrackSpriteCandidateBounds(int x, int y, int scaleX, int scaleY, int visibleMinX, int visibleMinY, int visibleMaxX, int visibleMaxY)
    {
        _lastSpriteMinX = Math.Min(_lastSpriteMinX, x);
        _lastSpriteMinY = Math.Min(_lastSpriteMinY, y);
        _lastSpriteMaxX = Math.Max(_lastSpriteMaxX, x + scaleX * 16);
        _lastSpriteMaxY = Math.Max(_lastSpriteMaxY, y + scaleY * 16);

        int dx = x > visibleMaxX ? x - visibleMaxX : visibleMinX - (x + scaleX * 16);
        int dy = y > visibleMaxY ? y - visibleMaxY : visibleMinY - (y + scaleY * 16);
        _lastSpriteClosestDistance = Math.Min(_lastSpriteClosestDistance, Math.Max(0, dx) + Math.Max(0, dy));
    }

    private bool RenderSpriteReefToFrame()
    {
        bool drewAny = false;
        if (RenderStats)
        {
            _lastSpriteMixSource = 0;
            _lastSpriteMixDest = 0;
            _lastSpriteMixBehind = 0;
            _lastSpriteMixSameBlend = 0;
            _lastSpriteMixDisabled = 0;
            _lastSpriteMixClipped = 0;
        }

        if (_spriteReefTouchedRows.Count != 0)
        {
            for (int i = 0; i < _spriteReefTouchedRows.Count; i++)
            {
                int y = _spriteReefTouchedRows[i];
                int rowOffset = y * FrameWidth;
                int screenY = y + VisibleAreaMinY;
                drewAny |= RenderSpriteReefRow(y, rowOffset, screenY);
            }
            return drewAny;
        }

        if (_spriteReefOffsetCount != 0)
        {
            for (int i = 0; i < _spriteReefOffsetCount; i++)
                drewAny |= RenderSpriteReefPixel(_spriteReefOffsets[i]);
            return drewAny;
        }

        for (int y = 0; y < FrameHeight; y++)
        {
            int row = y * FrameWidth;
            for (int x = 0; x < FrameWidth; x++)
            {
                int offset = row + x;
                if (_spriteReefPalette[offset] != 0)
                    drewAny |= RenderSpriteReefPixel(offset);
            }
        }

        return drewAny;
    }

    private bool RenderSpriteReefPixel(int offset)
    {
        ushort paletteIndex = _spriteReefPalette[offset];
        if (paletteIndex == 0)
            return false;

        int y = offset / FrameWidth;
        int x = offset - y * FrameWidth;
        int screenY = y + VisibleAreaMinY;
        return RenderSpriteReefPixelAt(offset, x, y, screenY, paletteIndex);
    }

    private bool RenderSpriteReefRow(int y, int rowOffset, int screenY)
    {
        bool drewAny = false;
        F3LineState line = _lineStates[screenY & 0xff];
        ushort mix0 = line.SpriteMix[0];
        ushort mix1 = line.SpriteMix[1];
        ushort mix2 = line.SpriteMix[2];
        ushort mix3 = line.SpriteMix[3];
        int state0 = PackSpriteCurrentState(mix0, line.SpriteBlendSelect[0], Sprite0LayerRank);
        int state1 = PackSpriteCurrentState(mix1, line.SpriteBlendSelect[1], Sprite1LayerRank);
        int state2 = PackSpriteCurrentState(mix2, line.SpriteBlendSelect[2], Sprite2LayerRank);
        int state3 = PackSpriteCurrentState(mix3, line.SpriteBlendSelect[3], Sprite3LayerRank);

        for (int node = _spriteReefRowHead[y]; node != 0;)
        {
            int offset = node - 1;
            node = _spriteReefNext[offset];
            ushort paletteIndex = _spriteReefPalette[offset];
            if (paletteIndex == 0)
                continue;

            int spriteGroup = _spriteReefGroup[offset] & 3;
            ushort spriteMix = spriteGroup switch
            {
                0 => mix0,
                1 => mix1,
                2 => mix2,
                _ => mix3,
            };
            int spriteState = spriteGroup switch
            {
                0 => state0,
                1 => state1,
                2 => state2,
                _ => state3,
            };

            drewAny |= RenderSpriteReefPixelAt(offset, offset - rowOffset, paletteIndex, spriteMix, spriteState, line);
        }

        return drewAny;
    }

    private static int PackSpriteCurrentState(ushort mixValue, bool blendSelect, byte rank)
    {
        int blendMode = (mixValue >> 14) & 3;
        int state = (mixValue & 0x0f) | (blendMode << 4) | (rank << 8);
        if ((mixValue & 0x2000) != 0 && blendMode != 0)
            state |= 1 << 16;
        if (((mixValue >> 8) & 0x0f) != 0)
            state |= 1 << 17;
        if (blendSelect)
            state |= 1 << 18;
        return state;
    }

    private bool RenderSpriteReefPixelAt(int offset, int x, int y, int screenY)
    {
        ushort paletteIndex = _spriteReefPalette[offset];
        if (paletteIndex == 0)
            return false;

        return RenderSpriteReefPixelAt(offset, x, y, screenY, paletteIndex);
    }

    private bool RenderSpriteReefPixelAt(int offset, int x, int y, int screenY, ushort paletteIndex)
    {
        int spriteGroup = _spriteReefGroup[offset];
        if (!TryReadSpriteCurrentState(screenY, spriteGroup, out int spritePriority, out int spriteBlendMode, out bool spriteBlendSelect))
        {
            if (RenderStats)
                _lastSpriteMixDisabled++;
            return false;
        }

        ushort spriteMix = _lineStates[screenY & 0xff].SpriteMix[spriteGroup & 3];
        if (!IsMameClipAllowed(screenY, spriteMix, x + VisibleAreaMinX))
        {
            if (RenderStats)
                _lastSpriteMixClipped++;
            return false;
        }

        byte spriteRank = GetSpriteLayerRank(spriteGroup);
        if (spriteBlendMode == _mixSrcBlendMode[offset])
        {
            if (RenderStats)
                _lastSpriteMixSameBlend++;
            return false;
        }

        if (RenderStats)
        {
            int currentSourcePriority = _mixSrcPriority[offset];
            if (spritePriority > currentSourcePriority
                || spritePriority == currentSourcePriority && spriteRank < _framePriorityRank[offset])
                _lastSpriteMixSource++;
            else if (spritePriority >= _mixDstPriority[offset])
                _lastSpriteMixDest++;
            else
                _lastSpriteMixBehind++;
        }

        WritePalettePixel(x, y, paletteIndex, spritePriority, spriteRank, spriteBlendMode, spriteBlendSelect);
        return true;
    }

    private bool RenderSpriteReefPixelAt(int offset, int x, ushort paletteIndex, ushort spriteMix, int spriteState, F3LineState line)
    {
        if ((spriteState & (1 << 16)) == 0)
        {
            if (RenderStats)
                _lastSpriteMixDisabled++;
            return false;
        }

        if ((spriteState & (1 << 17)) != 0
            && !IsMameClipAllowed(line, spriteMix, x + VisibleAreaMinX))
        {
            if (RenderStats)
                _lastSpriteMixClipped++;
            return false;
        }

        int spriteBlendMode = (spriteState >> 4) & 3;
        if (spriteBlendMode == _mixSrcBlendMode[offset])
        {
            if (RenderStats)
                _lastSpriteMixSameBlend++;
            return false;
        }

        int spritePriority = spriteState & 0x0f;
        byte spriteRank = (byte)((spriteState >> 8) & 0xff);
        if (RenderStats)
        {
            int currentSourcePriority = _mixSrcPriority[offset];
            if (spritePriority > currentSourcePriority
                || spritePriority == currentSourcePriority && spriteRank < _framePriorityRank[offset])
                _lastSpriteMixSource++;
            else if (spritePriority >= _mixDstPriority[offset])
                _lastSpriteMixDest++;
            else
                _lastSpriteMixBehind++;
        }

        WritePalettePixelAtOffset(offset, paletteIndex, spritePriority, spriteRank, spriteBlendMode, (spriteState & (1 << 18)) != 0, line);
        return true;
    }

    private void ClearSpriteReefForNextFrame()
    {
        if (_spriteReefOffsetCount == 0)
        {
            Array.Clear(_spriteReefPalette);
            Array.Clear(_spriteReefGroup);
            Array.Clear(_spriteReefRowActive);
            _spriteReefTouchedRows.Clear();
            return;
        }

        for (int i = 0; i < _spriteReefOffsetCount; i++)
        {
            int offset = _spriteReefOffsets[i];
            _spriteReefPalette[offset] = 0;
            _spriteReefGroup[offset] = 0;
            _spriteReefNext[offset] = 0;
        }
        _spriteReefOffsetCount = 0;

        for (int i = 0; i < _spriteReefTouchedRows.Count; i++)
        {
            int y = _spriteReefTouchedRows[i];
            _spriteReefRowActive[y] = false;
            _spriteReefRowHead[y] = 0;
        }
        _spriteReefTouchedRows.Clear();
    }

    private bool TryReadSpriteCurrentState(int screenY, int spriteGroup, out int priority, out int blendMode, out bool blendSelect)
    {
        F3LineState line = _lineStates[screenY & 0xff];
        ushort mixValue = line.SpriteMix[spriteGroup & 3];
        priority = mixValue & 0x0f;
        blendMode = (mixValue >> 14) & 3;
        blendSelect = line.SpriteBlendSelect[spriteGroup & 3];
        return (mixValue & 0x2000) != 0 && blendMode != 0;
    }

    private bool TryReadMixableCurrentState(int screenY, int section, int subsection, bool spriteRules, out int priority, out int blendMode, out bool blendSelect)
    {
        F3LineState line = _lineStates[screenY & 0xff];
        ushort mixValue = section == 7
            ? line.PlayfieldMix[subsection & 3]
            : section == 3 && subsection == 1
                ? line.PivotMix
                : (ushort)0;

        blendMode = (mixValue >> 14) & 3;
        priority = mixValue & 0x0f;
        blendSelect = section == 3 && subsection == 1 && line.PivotBlendSelect;
        return spriteRules
            ? (mixValue & 0x2000) != 0 && blendMode != 0
            : (mixValue & 0x2000) != 0 && blendMode != 3;
    }

    private void BuildMameLineStates()
    {
        _lineBuildState.Reset();
        var line = _lineBuildState;
        for (int y = 0; y < 256; y++)
        {
            if (TryReadLatchedLineWord(y, 2, 0, out ushort line6000))
            {
                line.PivotBlendSelect = (line6000 & 0x0200) != 0;
                line.PivotControl = (byte)(line6000 >> 8);
                for (int group = 0; group < 4; group++)
                {
                    int blend = (line6000 >> (group * 2)) & 3;
                    line.SpriteMix[group] = (ushort)((line.SpriteMix[group] & 0x3fff) | (blend << 14));
                }
            }

            for (int playfield = 2; playfield < 4; playfield++)
            {
                if (TryReadLatchedLineWord(y, 0, playfield, out ushort colScroll))
                {
                    line.PlayfieldColScroll[playfield] = (ushort)(colScroll & 0x01ff);
                    line.PlayfieldAltTilemap[playfield] = (colScroll & 0x0200) != 0;
                    int plane = 2 * (playfield - 2);
                    line.Clip[plane].SetUpper((colScroll >> 12) & 1, (colScroll >> 13) & 1);
                    line.Clip[plane + 1].SetUpper((colScroll >> 14) & 1, (colScroll >> 15) & 1);
                }
            }

            for (int plane = 0; plane < 4; plane++)
            {
                if (TryReadLatchedLineWord(y, 1, plane, out ushort clipLows))
                    line.Clip[plane].SetLower(clipLows & 0xff, clipLows >> 8);
            }

            if (TryReadLatchedLineWord(y, 2, 1, out ushort blendValues))
            {
                for (int index = 0; index < 4; index++)
                    line.Blend[index] = (byte)Math.Min(8, 0x0f - ((blendValues >> (index * 4)) & 0x0f));
            }

            if (TryReadLatchedLineWord(y, 2, 3, out ushort bgPalette))
                line.BgPalette = bgPalette;

            if (TryReadLatchedLineWord(y, 3, 1, out ushort pivotMix))
                line.PivotMix = pivotMix;

            if (TryReadLatchedLineWord(y, 3, 2, out ushort spriteMix))
            {
                for (int group = 0; group < 4; group++)
                {
                    line.SpriteMix[group] = (ushort)((line.SpriteMix[group] & 0xc00f) | ((spriteMix & 0x03ff) << 4));
                    line.SpriteBlendSelect[group] = ((spriteMix >> (12 + group)) & 1) != 0;
                }
            }

            if (TryReadLatchedLineWord(y, 3, 3, out ushort spritePriority))
            {
                for (int group = 0; group < 4; group++)
                    line.SpriteMix[group] = (ushort)((line.SpriteMix[group] & 0xfff0) | ((spritePriority >> (group * 4)) & 0x0f));
            }

            for (int playfield = 0; playfield < 4; playfield++)
            {
                if (TryReadLatchedLineWord(y, 4, playfield, out ushort playfieldScale))
                {
                    int scaledYPlayfield = playfield switch
                    {
                        1 => 3,
                        2 => 2,
                        3 => 1,
                        _ => 0,
                    };
                    line.PlayfieldXScale[playfield] = 0x100 - ((playfieldScale >> 8) & 0xff);
                    line.PlayfieldYScale[scaledYPlayfield] = (playfieldScale & 0xff) << 1;
                }
            }

            for (int playfield = 0; playfield < 4; playfield++)
            {
                if (TryReadLatchedLineWord(y, 5, playfield, out ushort paletteAdd))
                    line.PlayfieldPaletteAdd[playfield] = paletteAdd * 16;
            }

            for (int playfield = 0; playfield < 4; playfield++)
            {
                if (TryReadLatchedLineWord(y, 6, playfield, out ushort rowScroll))
                {
                    int fixedRowScroll = rowScroll << 2;
                    line.PlayfieldRowScroll[playfield] = (fixedRowScroll & unchecked((int)0xffffff00)) - (fixedRowScroll & 0xff);
                }
            }

            for (int playfield = 0; playfield < 4; playfield++)
            {
                if (TryReadLatchedLineWord(y, 7, playfield, out ushort playfieldMix))
                    line.PlayfieldMix[playfield] = playfieldMix;
            }

            if (!_lineStates[y].IsInitialized)
                _lineStates[y] = new F3LineState();
            _lineStates[y].CopyFrom(line);
        }

        _lineBuildState = line;
    }

    private bool TryReadLatchedLineWord(int screenY, int section, int subsection, out ushort value)
    {
        value = 0;
        if ((uint)screenY >= 256)
            return false;

        ushort latches = _bus.ReadLineRamWord(section * 0x100 + screenY);
        int baseWord = (0x4000 + 0x1000 * section + 0x200 * subsection) >> 1;
        int latchBit = subsection;
        if ((latches & (1 << (latchBit + 4))) != 0)
        {
            value = _bus.ReadLineRamWord(baseWord + 0x400 + screenY);
            return true;
        }

        if ((latches & (1 << latchBit)) != 0)
        {
            value = _bus.ReadLineRamWord(baseWord + screenY);
            return true;
        }

        return false;
    }

    private bool IsMameClipAllowed(int screenY, int mixValue, int x)
        => IsMameClipAllowed(_lineStates[screenY & 0xff], mixValue, x);

    private static bool IsMameClipAllowed(F3LineState line, int mixValue, int x)
    {
        int clipEnable = (mixValue >> 8) & 0x0f;
        if (clipEnable == 0)
            return true;

        int clipInvert = (mixValue >> 4) & 0x0f;
        bool invertMode = (mixValue & 0x1000) != 0;
        int normalPlanes = clipEnable & ~clipInvert;
        int invertPlanes = clipEnable & clipInvert;
        if (!invertMode)
            (normalPlanes, invertPlanes) = (invertPlanes, normalPlanes);

        bool allowed = true;
        for (int plane = 0; plane < 4; plane++)
        {
            int clipL = line.Clip[plane].Left - 1;
            int clipR = line.Clip[plane].Right - 2;
            bool inside = clipL <= clipR && x >= clipL && x <= clipR;
            if (((normalPlanes >> plane) & 1) != 0)
                allowed &= inside;
            else if (((invertPlanes >> plane) & 1) != 0 && inside)
                allowed = false;
        }

        return allowed;
    }

    private static byte GetSpriteLayerRank(int spriteGroup)
        => spriteGroup switch
        {
            0 => Sprite0LayerRank,
            3 => Sprite3LayerRank,
            2 => Sprite2LayerRank,
            1 => Sprite1LayerRank,
            _ => EmptyLayerRank,
        };

    private static byte GetPlayfieldLayerRank(int playfield)
        => playfield switch
        {
            0 => Playfield0LayerRank,
            3 => Playfield3LayerRank,
            2 => Playfield2LayerRank,
            1 => Playfield1LayerRank,
            _ => EmptyLayerRank,
        };

    private bool DrawSpriteToReef(TaitoF3RomSet roms, F3Sprite sprite)
    {
        int elements = roms.SpritePixels.Length >> 8;
        if (elements <= 0)
            return false;

        if (sprite.ScaleX == 0x100 && sprite.ScaleY == 0x100)
            return DrawUnscaledSpriteToReef(roms, sprite, elements);

        bool drewAny = false;
        int codeBase = (sprite.Code % elements) << 8;
        int dy8 = sprite.Y;
        if (!_flipScreen)
            dy8 += 255;

        for (int sy = 0; sy < 16; sy++)
        {
            int dy = dy8 >> 8;
            dy8 += sprite.ScaleY;
            if ((uint)dy >= FrameHeight)
                continue;

            int dx8 = sprite.X + 128;
            int sourceY = sprite.FlipY ? sy ^ 0x0f : sy;
            int sourceRow = codeBase + (sourceY << 4);
            int rowOffset = dy * FrameWidth;
            for (int sx = 0; sx < 16; sx++)
            {
                int dx = dx8 >> 8;
                dx8 += sprite.ScaleX;
                if ((uint)dx >= FrameWidth || dx == (dx8 >> 8))
                    continue;

                int sourceX = sprite.FlipX ? sx ^ 0x0f : sx;
                int pen = roms.SpritePixels[sourceRow | sourceX] & _spritePenMask;
                if (pen == 0)
                    continue;

                int reefOffset = rowOffset + dx;
                if (_spriteReefPalette[reefOffset] != 0)
                    continue;

                _spriteReefPalette[reefOffset] = (ushort)(0x1000 + ((sprite.Color << 4) | pen));
                _spriteReefGroup[reefOffset] = (byte)((sprite.Color >> 6) & 3);
                AddSpriteReefOffset(reefOffset);
                LinkSpriteReefOffset(dy, reefOffset);
                if (RenderStats)
                    _lastSpritePixels++;
                drewAny = true;
            }
        }

        return drewAny;
    }

    private bool DrawUnscaledSpriteToReef(TaitoF3RomSet roms, F3Sprite sprite, int elements)
    {
        bool drewAny = false;
        int codeBase = (sprite.Code % elements) << 8;
        int baseY = ((_flipScreen ? sprite.Y : sprite.Y + 255) >> 8);
        int baseX = ((sprite.X + 128) >> 8);
        byte[] spritePixels = roms.SpritePixels;
        int penMask = _spritePenMask;

        for (int sy = 0; sy < 16; sy++)
        {
            int dy = baseY + sy;
            if ((uint)dy >= FrameHeight)
                continue;

            int sourceY = sprite.FlipY ? sy ^ 0x0f : sy;
            int sourceRow = codeBase + (sourceY << 4);
            int rowOffset = dy * FrameWidth;
            for (int sx = 0; sx < 16; sx++)
            {
                int dx = baseX + sx;
                if ((uint)dx >= FrameWidth)
                    continue;

                int sourceX = sprite.FlipX ? sx ^ 0x0f : sx;
                int pen = spritePixels[sourceRow | sourceX] & penMask;
                if (pen == 0)
                    continue;

                int reefOffset = rowOffset + dx;
                if (_spriteReefPalette[reefOffset] != 0)
                    continue;

                _spriteReefPalette[reefOffset] = (ushort)(0x1000 + ((sprite.Color << 4) | pen));
                _spriteReefGroup[reefOffset] = (byte)((sprite.Color >> 6) & 3);
                AddSpriteReefOffset(reefOffset);
                LinkSpriteReefOffset(dy, reefOffset);
                if (RenderStats)
                    _lastSpritePixels++;
                drewAny = true;
            }
        }

        return drewAny;
    }

    private void LinkSpriteReefOffset(int y, int offset)
    {
        if (!_spriteReefRowActive[y])
        {
            _spriteReefRowActive[y] = true;
            _spriteReefTouchedRows.Add(y);
        }

        _spriteReefNext[offset] = _spriteReefRowHead[y];
        _spriteReefRowHead[y] = offset + 1;
    }

    private void AddSpriteReefOffset(int offset)
    {
        if ((uint)_spriteReefOffsetCount < (uint)_spriteReefOffsets.Length)
            _spriteReefOffsets[_spriteReefOffsetCount++] = offset;
    }

    private static int DecodeSpritePixel(TaitoF3RomSet roms, int code, int x, int y)
    {
        int elements = roms.SpritePixels.Length / 0x100;
        if (elements <= 0)
            return 0;

        code %= elements;
        return roms.SpritePixels[(code << 8) | ((y & 0x0f) << 4) | (x & 0x0f)];
    }

    private static int SpriteHiXBitOffset(int x)
    {
        int group = x >> 2;
        int inGroup = x & 3;
        return group * 8 + (3 - inGroup) * 2;
    }

    private void ClearWithPalette(int paletteIndex)
        => ClearWithPalette(paletteIndex, clearFrameBuffer: true);

    private void ClearWithPalette(int paletteIndex, bool clearFrameBuffer)
    {
        uint color = _bus.ReadPaletteColor(paletteIndex, fallback: 0xff081020);
        if (clearFrameBuffer)
        {
            byte b = (byte)color;
            byte g = (byte)(color >> 8);
            byte r = (byte)(color >> 16);
            for (int offset = 0; offset < _frameBuffer.Length; offset += 4)
            {
                _frameBuffer[offset + 0] = b;
                _frameBuffer[offset + 1] = g;
                _frameBuffer[offset + 2] = r;
                _frameBuffer[offset + 3] = 0xff;
            }
        }

        Array.Clear(_framePriority);
        Array.Fill(_framePriorityRank, EmptyLayerRank);
        Array.Clear(_framePriorityConflict);
        Array.Clear(_mixSrcPalette);
        Array.Clear(_mixSrcBlend);
        Array.Clear(_mixSrcPriority);
        Array.Clear(_mixDstPriority);
        if (RenderStats)
            _lastMixPriorityZeroConflicts = 0;
        Array.Fill(_mixDstPalette, (ushort)paletteIndex);
        Array.Fill(_mixDstBlend, (byte)8);
        Array.Fill(_mixSrcBlendMode, (byte)0xff);
        Array.Fill(_mixDstBlendMode, (byte)0xff);
    }

    private void InitializeMameLineBackgrounds()
    {
        for (int y = 0; y < FrameHeight; y++)
        {
            F3LineState line = _lineStates[(y + VisibleAreaMinY) & 0xff];
            Array.Fill(_mixDstPalette, line.BgPalette, y * FrameWidth, FrameWidth);
        }
    }

    private void WritePalettePixel(int x, int y, int paletteIndex)
        => WritePalettePixel(x, y, paletteIndex, priority: 0, layerRank: PivotLayerRank, blendMode: 0, blendSelect: false);

    private void WritePalettePixel(int x, int y, int paletteIndex, int priority, byte layerRank)
        => WritePalettePixel(x, y, paletteIndex, priority, layerRank, blendMode: 0, blendSelect: false);

    private void WritePalettePixel(int x, int y, int paletteIndex, int priority, byte layerRank, int blendMode, bool blendSelect)
    {
        int priorityOffset = y * FrameWidth + x;
        if ((uint)priorityOffset >= (uint)_mixSrcPalette.Length)
            return;

        WritePalettePixelAtOffset(priorityOffset, (ushort)paletteIndex, priority, layerRank, blendMode, blendSelect, _lineStates[(y + VisibleAreaMinY) & 0xff]);
    }

    private void WritePalettePixelAtOffset(int priorityOffset, ushort palette, int priority, byte layerRank, int blendMode, bool blendSelect, F3LineState line)
    {
        if (blendMode == _mixSrcBlendMode[priorityOffset])
            return;

        int select = blendSelect ? 1 : 0;

        int currentSourcePriority = _mixSrcPriority[priorityOffset];
        bool sourceWins = priority > currentSourcePriority
            || priority == currentSourcePriority && layerRank < _framePriorityRank[priorityOffset];
        if (sourceWins)
        {
            switch (blendMode)
            {
                case 1:
                    select += 2;
                    goto case 2;
                case 2:
                    if (line.Blend[select] == 0)
                        return;
                    _mixSrcBlend[priorityOffset] = line.Blend[select];
                    break;
                default:
                    if (line.Blend[select] + line.Blend[2 + select] == 0)
                        return;
                    _mixSrcBlend[priorityOffset] = line.Blend[2 + select];
                    _mixDstBlend[priorityOffset] = line.Blend[select];
                    _mixDstPriority[priorityOffset] = (byte)priority;
                    _mixDstPalette[priorityOffset] = palette;
                    _mixDstBlendMode[priorityOffset] = (byte)blendMode;
                    break;
            }

            _mixSrcPalette[priorityOffset] = palette;
            _mixSrcBlendMode[priorityOffset] = (byte)blendMode;
            _mixSrcPriority[priorityOffset] = (byte)priority;
            _framePriorityRank[priorityOffset] = layerRank;
            return;
        }

        if (priority >= _mixDstPriority[priorityOffset])
        {
            if (RenderStats && priority == _mixDstPriority[priorityOffset] && priority == 0)
                _lastMixPriorityZeroConflicts++;
            _mixDstPalette[priorityOffset] = priority != _mixDstPriority[priorityOffset] ? palette : (ushort)0;
            _mixDstPriority[priorityOffset] = (byte)priority;
            _mixDstBlendMode[priorityOffset] = (byte)blendMode;

            switch (_mixSrcBlendMode[priorityOffset])
            {
                case 1:
                    _mixDstBlend[priorityOffset] = line.Blend[select];
                    break;
                default:
                    _mixDstBlend[priorityOffset] = line.Blend[2 + select];
                    break;
            }
        }
    }

    private void RenderMameMixBufferToFrame()
    {
        if (RenderStats)
        {
            RenderMameMixBufferToFrameWithStats();
            return;
        }

        int cacheFrame = unchecked(_paletteColorCacheFrame + 1);
        if (cacheFrame == 0)
        {
            Array.Clear(_paletteColorCacheStamp);
            cacheFrame = 1;
        }
        _paletteColorCacheFrame = cacheFrame;

        if (!BitConverter.IsLittleEndian)
        {
            RenderMameMixBufferToFrameBytes(cacheFrame);
            return;
        }

        Span<uint> framePixels = MemoryMarshal.Cast<byte, uint>(_frameBuffer);
        byte[] srcBlendMode = _mixSrcBlendMode;
        ushort[] srcPalette = _mixSrcPalette;
        ushort[] dstPalette = _mixDstPalette;
        byte[] srcBlend = _mixSrcBlend;
        byte[] dstBlend = _mixDstBlend;

        int pixelCount = FrameWidth * FrameHeight;
        for (int offset = 0; offset < pixelCount; offset++)
        {
            uint color;
            if (srcBlendMode[offset] == 0xff || srcBlend[offset] == 0)
            {
                color = ReadCachedPaletteColor(dstPalette[offset], cacheFrame);
            }
            else if (dstBlend[offset] == 0)
            {
                color = ReadCachedPaletteColor(srcPalette[offset], cacheFrame);
            }
            else
            {
                uint source = ReadCachedPaletteColor(srcPalette[offset], cacheFrame);
                uint destination = ReadCachedPaletteColor(dstPalette[offset], cacheFrame);
                color = BlendFixed3(source, destination, srcBlend[offset], dstBlend[offset]);
            }
            framePixels[offset] = color | 0xff000000u;
        }
    }

    private void RenderMameMixBufferToFrameBytes(int cacheFrame)
    {
        byte[] srcBlendMode = _mixSrcBlendMode;
        ushort[] srcPalette = _mixSrcPalette;
        ushort[] dstPalette = _mixDstPalette;
        byte[] srcBlend = _mixSrcBlend;
        byte[] dstBlend = _mixDstBlend;

        int pixelCount = FrameWidth * FrameHeight;
        for (int offset = 0; offset < pixelCount; offset++)
        {
            uint color;
            if (srcBlendMode[offset] == 0xff || srcBlend[offset] == 0)
            {
                color = ReadCachedPaletteColor(dstPalette[offset], cacheFrame);
            }
            else if (dstBlend[offset] == 0)
            {
                color = ReadCachedPaletteColor(srcPalette[offset], cacheFrame);
            }
            else
            {
                uint source = ReadCachedPaletteColor(srcPalette[offset], cacheFrame);
                uint destination = ReadCachedPaletteColor(dstPalette[offset], cacheFrame);
                color = BlendFixed3(source, destination, srcBlend[offset], dstBlend[offset]);
            }
            int framePixelOffset = offset * 4;
            _frameBuffer[framePixelOffset + 0] = (byte)color;
            _frameBuffer[framePixelOffset + 1] = (byte)(color >> 8);
            _frameBuffer[framePixelOffset + 2] = (byte)(color >> 16);
            _frameBuffer[framePixelOffset + 3] = 0xff;
        }
    }

    private void RenderMameMixBufferToFrameWithStats()
    {
        int sourcePixels = 0;
        int litSourcePixels = 0;
        int destOnlyPixels = 0;
        int cacheFrame = unchecked(_paletteColorCacheFrame + 1);
        if (cacheFrame == 0)
        {
            Array.Clear(_paletteColorCacheStamp);
            cacheFrame = 1;
        }
        _paletteColorCacheFrame = cacheFrame;

        for (int y = 0; y < FrameHeight; y++)
        {
            int row = y * FrameWidth;
            int frameOffset = y * FrameStride;
            for (int x = 0; x < FrameWidth; x++)
            {
                int offset = row + x;
                if (RenderStats && _mixSrcBlendMode[offset] != 0xff)
                {
                    sourcePixels++;
                    if (_mixSrcBlend[offset] != 0 && _mixSrcPalette[offset] != 0)
                        litSourcePixels++;
                }
                else if (RenderStats && _mixDstPalette[offset] != 0)
                {
                    destOnlyPixels++;
                }
                uint color;
                if (_mixSrcBlendMode[offset] == 0xff || _mixSrcBlend[offset] == 0)
                {
                    color = ReadCachedPaletteColor(_mixDstPalette[offset], cacheFrame);
                }
                else if (_mixDstBlend[offset] == 0)
                {
                    color = ReadCachedPaletteColor(_mixSrcPalette[offset], cacheFrame);
                }
                else
                {
                    uint source = ReadCachedPaletteColor(_mixSrcPalette[offset], cacheFrame);
                    uint destination = ReadCachedPaletteColor(_mixDstPalette[offset], cacheFrame);
                    color = BlendFixed3(source, destination, _mixSrcBlend[offset], _mixDstBlend[offset]);
                }
                int framePixelOffset = frameOffset + x * 4;
                _frameBuffer[framePixelOffset + 0] = (byte)color;
                _frameBuffer[framePixelOffset + 1] = (byte)(color >> 8);
                _frameBuffer[framePixelOffset + 2] = (byte)(color >> 16);
                _frameBuffer[framePixelOffset + 3] = 0xff;
            }
        }
        if (RenderStats)
        {
            _lastMixSourcePixels = sourcePixels;
            _lastMixLitSourcePixels = litSourcePixels;
            _lastMixDestOnlyPixels = destOnlyPixels;
        }
    }

    private uint ReadCachedPaletteColor(ushort paletteIndex, int cacheFrame)
    {
        int index = paletteIndex & 0x1fff;
        if (_paletteColorCacheStamp[index] == cacheFrame)
            return _paletteColorCache[index];

        uint color = _bus.ReadPaletteColor(index, fallback: SynthColor(index));
        _paletteColorCache[index] = color;
        _paletteColorCacheStamp[index] = cacheFrame;
        return color;
    }

    private static uint BlendFixed3(uint source, uint destination, int sourceWeight, int destinationWeight)
    {
        int b = Math.Min(255, ((((int)(source & 0xff) * sourceWeight) + ((int)(destination & 0xff) * destinationWeight)) >> 3));
        int g = Math.Min(255, ((((int)((source >> 8) & 0xff) * sourceWeight) + ((int)((destination >> 8) & 0xff) * destinationWeight)) >> 3));
        int r = Math.Min(255, ((((int)((source >> 16) & 0xff) * sourceWeight) + ((int)((destination >> 16) & 0xff) * destinationWeight)) >> 3));
        return 0xff000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private int ReadCurrentBlendWeight(int screenY, int blendIndex)
    {
        if (!TryReadLatchedLineWord(screenY, 2, 1, out ushort blendValues))
            return 8;

        int alpha = (blendValues >> (blendIndex * 4)) & 0x0f;
        return Math.Min(8, 0x0f - alpha);
    }

    private static uint BlendFrameColor(uint source, uint destination, int sourceWeight)
    {
        int destinationWeight = 8 - sourceWeight;
        int b = (((int)(source & 0xff) * sourceWeight) + ((int)(destination & 0xff) * destinationWeight)) >> 3;
        int g = (((int)((source >> 8) & 0xff) * sourceWeight) + ((int)((destination >> 8) & 0xff) * destinationWeight)) >> 3;
        int r = (((int)((source >> 16) & 0xff) * sourceWeight) + ((int)((destination >> 16) & 0xff) * destinationWeight)) >> 3;
        return 0xff000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private uint ReadFrameColor(int x, int y)
        => ReadFramePixel(y * FrameStride + x * 4);

    private void WriteFrameColor(int x, int y, uint color)
    {
        int offset = y * FrameStride + x * 4;
        _frameBuffer[offset + 0] = (byte)color;
        _frameBuffer[offset + 1] = (byte)(color >> 8);
        _frameBuffer[offset + 2] = (byte)(color >> 16);
        _frameBuffer[offset + 3] = 0xff;
    }

    private bool CanWritePalettePixel(int priorityOffset, int priority, byte layerRank)
    {
        int currentPriority = _framePriority[priorityOffset];
        if (priority != currentPriority)
            return priority > currentPriority;

        return layerRank < _framePriorityRank[priorityOffset];
    }

    private bool MarksPriorityConflict(int priorityOffset, int priority, byte layerRank)
    {
        if (_framePriorityRank[priorityOffset] == EmptyLayerRank)
            return false;
        if (_framePriorityConflict[priorityOffset] != 0)
            return false;

        return priority == _framePriority[priorityOffset]
            && layerRank != _framePriorityRank[priorityOffset];
    }

    private static uint SynthColor(int paletteIndex)
    {
        int r = ((paletteIndex * 37) ^ (paletteIndex >> 2)) & 0xff;
        int g = ((paletteIndex * 73) ^ (paletteIndex >> 1)) & 0xff;
        int b = ((paletteIndex * 19) ^ (paletteIndex << 1)) & 0xff;
        return 0xff000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static int DecodeTilemapPixel(TaitoF3RomSet roms, int code, int x, int y, int penMask)
    {
        int elements = roms.TilemapPixels.Length / 0x100;
        if (elements <= 0)
            return 0;

        code %= elements;
        return roms.TilemapPixels[(code << 8) | ((y & 0x0f) << 4) | (x & 0x0f)] & penMask;
    }

    private static int DecodePacked4BppTilePixel(byte[] rom, int code, int x, int y)
    {
        int tileOffset = code * 16 * 8;
        int offset = tileOffset + y * 8 + (x >> 1);
        if ((uint)offset >= (uint)rom.Length)
            return 0;

        byte packed = rom[offset];
        return DecodeMamePackedLsbNibble(packed, x);
    }

    private static int DecodeTilemapHighPlanes(byte[] rom, int code, int x, int y)
    {
        int pixelBit = x < 8 ? 7 - x : 23 - (x - 8);
        int bitOffset = code * 16 * 16 * 2 + y * 32 + pixelBit;
        int byteOffset = bitOffset >> 3;
        if ((uint)byteOffset >= (uint)rom.Length)
            return 0;

        int bit = 7 - (bitOffset & 7);
        int hi1 = (rom[byteOffset] >> bit) & 1;
        int plane0Offset = bitOffset + 8;
        int plane0ByteOffset = plane0Offset >> 3;
        int hi0 = (uint)plane0ByteOffset < (uint)rom.Length
            ? (rom[plane0ByteOffset] >> (7 - (plane0Offset & 7))) & 1
            : 0;
        return (hi0 << 4) | (hi1 << 5);
    }

    private static int DecodePacked4BppTextPixel(byte[] rom, int code, int x, int y)
    {
        int tileOffset = code * 8 * 4;
        int offset = tileOffset + y * 4 + (x >> 1);
        if ((uint)offset >= (uint)rom.Length)
            return 0;

        byte packed = rom[offset];
        return DecodeMamePackedLsbNibble(packed, x);
    }

    private static int DecodeMamePackedLsbNibble(byte packed, int x)
        => (x & 1) == 0 ? packed & 0x0f : packed >> 4;

    private void DrawBringupFrame()
    {
        Array.Clear(_frameBuffer);
        uint pc = _mainCpu.Pc;
        int bar = (int)((pc >> 1) % FrameWidth);
        for (int y = 0; y < FrameHeight; y++)
        {
            for (int x = 0; x < FrameWidth; x++)
            {
                int offset = y * FrameStride + x * 4;
                byte grid = (byte)(((x ^ y) & 0x10) != 0 ? 0x18 : 0x08);
                _frameBuffer[offset + 0] = (byte)(grid + ((pc >> 0) & 0x1f));
                _frameBuffer[offset + 1] = (byte)(grid + ((pc >> 8) & 0x3f));
                _frameBuffer[offset + 2] = (byte)(grid + ((pc >> 16) & 0x1f));
                _frameBuffer[offset + 3] = 0xff;
            }
        }

        for (int y = 0; y < FrameHeight; y++)
        {
            int offset = y * FrameStride + bar * 4;
            _frameBuffer[offset + 0] = 0x20;
            _frameBuffer[offset + 1] = 0xe0;
            _frameBuffer[offset + 2] = 0xff;
            _frameBuffer[offset + 3] = 0xff;
        }
    }

    private void UpdateRomInfo(string path)
    {
        RomInfo.Summary = _driverName.Equals("dariusg", StringComparison.OrdinalIgnoreCase)
            ? "Taito F3 Darius Gaiden bringup"
            : $"Taito F3 Darius Gaiden bringup ({_driverName})";
        RomInfo.ExtraInfo =
            $"MAME set: {_driverName}\n" +
            $"Archive: {Path.GetFileName(path)}\n" +
            "Reference: ~/mame/src/mame/taito/taito_f3.cpp\n" +
            "Hardware target: Taito F3, M68EC020 @ 16 MHz, sound 68000, ES5505/ES5510, raster 432x262, visible 320x224, 58.94 Hz.\n" +
            "Bringup status: main ROM mapping and RAM/register stubs execute via existing EutherDrive 68000 core; 020-only instructions/devices are logged as missing.";
        RomInfo.RegionHint = ConsoleRegion.Auto;
    }

    private static byte[] BuildRomHash(byte[] mainRom)
    {
        return RomIdentity.ComputeSha256(mainRom);
    }

    private static string GetDriverName(string path)
        => Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();

    private static bool LooksLikeDariusGaidenArchive(string path)
    {
        try
        {
            using IArchive archive = ArchiveFactory.Open(path);
            var names = new HashSet<string>(
                archive.Entries
                    .Where(static entry => !entry.IsDirectory)
                    .Select(static entry => Path.GetFileName(entry.Key).ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            return RequiredDariusGaidenEntries.All(names.Contains);
        }
        catch
        {
            return false;
        }
    }

    private static int ParseEnvInt(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 0
            ? value
            : fallback;
    }

    private bool ShouldTraceDariusBootPc(uint pc)
    {
        pc &= 0x00ff_ffff;
        if (pc == 0x010170)
            return false;
        if (pc == 0x001280)
            return false;

        return pc == 0x0100ec
            || pc == 0x010100
            || (pc >= 0x010172 && pc <= 0x01025e)
            || (pc >= 0x01025e && pc <= 0x010338)
            || pc == 0x00117e
            || (pc >= 0x001206 && pc <= 0x001230)
            || pc == 0x001db8
            || pc == 0x001de8
            || pc == 0x001dee
            || pc == 0x001df0
            || pc == 0x001e76
            || pc == 0x001e7e
            || pc == 0x001e86
            || pc == 0x002292
            || pc == 0x00229a
            || (pc >= 0x180100 && pc <= 0x18017a)
            || (pc >= 0x17c238 && pc <= 0x17c240)
            || pc == 0x00ffff
            || (_sceneEntryHits != 0 && pc >= 0x001282 && pc <= 0x001700);
    }

    private void ResetSceneDiagnostics()
    {
        _sceneEntryHits = 0;
        _sceneGateRoutineHits = 0;
        _sceneGateSetInstructionHits = 0;
        _sceneGateWaitHits = 0;
        _mainGateWaitHits = 0;
        _sceneInitResumeHits = 0;
        _sceneInitMainHits = 0;
        _sceneMenuInitHits = 0;
        _sceneMenuYieldHits = 0;
        _sceneSpawnerYieldHits = 0;
        _sceneContinuationEnqueued = 0;
        _sceneContinuationDispatched = 0;
        _sceneContinuationRemoved = 0;
        _gameStartAccepted = false;
        _lastPersistentYieldTask = null;
        _spriteListBuildHits = 0;
        _spriteListProducerWrites = 0;
        _spriteListFinalizeHits = 0;
        _spriteListLatchedWrites = 0;
        _lastSceneAbsoluteCallTarget = 0;
        _lastF3TaskRemoveMask = 0;
    }

    private void TrackSceneDiagnosticPc(uint pc)
    {
        switch (pc & 0x00ff_ffff)
        {
            case 0x004146:
                _sceneEntryHits++;
                break;
            case 0x004148:
                _sceneInitResumeHits++;
                break;
            case 0x00414c:
                _sceneInitMainHits++;
                break;
            case 0x004276:
                _sceneGateRoutineHits++;
                break;
            case 0x004294:
                _sceneGateSetInstructionHits++;
                break;
            case 0x00429c:
                _sceneMenuInitHits++;
                break;
            case 0x0043ac:
                _sceneMenuYieldHits++;
                break;
            case 0x0043fe:
                _lastSceneAbsoluteCallTarget = _bus.ReadLong(0x004400) & 0x00ff_ffff;
                break;
            case 0x0045a6:
                _sceneSpawnerYieldHits++;
                break;
            case 0x0041dc:
                _sceneGateWaitHits++;
                break;
            case 0x001282:
                _spriteListBuildHits++;
                break;
            case 0x0013c8:
            case 0x00140a:
                _spriteListProducerWrites++;
                break;
            case 0x00144a:
                _spriteListFinalizeHits++;
                break;
            case 0x0014cc:
                _spriteListLatchedWrites++;
                break;
            case 0x010326:
                _mainGateWaitHits++;
                break;
        }
    }

    internal readonly struct TaitoF3InputState
    {
        public readonly bool Up;
        public readonly bool Down;
        public readonly bool Left;
        public readonly bool Right;
        public readonly bool A;
        public readonly bool B;
        public readonly bool C;
        public readonly bool Start;
        public readonly bool X;
        public readonly bool Y;
        public readonly bool Z;
        public readonly bool Coin1;

        public TaitoF3InputState(bool up, bool down, bool left, bool right, bool a, bool b, bool c, bool start, bool x, bool y, bool z, bool coin1)
        {
            Up = up;
            Down = down;
            Left = left;
            Right = right;
            A = a;
            B = b;
            C = c;
            Start = start;
            X = x;
            Y = y;
            Z = z;
            Coin1 = coin1;
        }
    }

    private struct F3LineState
    {
        public ushort PivotMix;
        public ushort BgPalette;
        public byte PivotControl;
        public bool PivotBlendSelect;
        public readonly ushort[] PlayfieldMix;
        public readonly ushort[] PlayfieldColScroll;
        public readonly bool[] PlayfieldAltTilemap;
        public readonly int[] PlayfieldXScale;
        public readonly int[] PlayfieldYScale;
        public readonly int[] PlayfieldRowScroll;
        public readonly int[] PlayfieldPaletteAdd;
        public readonly ushort[] SpriteMix;
        public readonly bool[] SpriteBlendSelect;
        public readonly byte[] Blend;
        public readonly F3ClipPlane[] Clip;

        public F3LineState()
        {
            PivotMix = 0;
            BgPalette = 0;
            PivotControl = 0;
            PivotBlendSelect = false;
            PlayfieldMix = new ushort[4];
            PlayfieldColScroll = new ushort[4];
            PlayfieldAltTilemap = new bool[4];
            PlayfieldXScale = new int[4];
            PlayfieldYScale = new int[4];
            PlayfieldRowScroll = new int[4];
            PlayfieldPaletteAdd = new int[4];
            SpriteMix = new ushort[4];
            SpriteBlendSelect = new bool[4];
            Blend = new byte[4];
            Clip = new F3ClipPlane[4];
            Array.Fill(PlayfieldXScale, 0x80);
        }

        public bool IsInitialized => PlayfieldMix != null;

        public void Reset()
        {
            PivotMix = 0;
            BgPalette = 0;
            PivotControl = 0;
            PivotBlendSelect = false;
            Array.Clear(PlayfieldMix);
            Array.Clear(PlayfieldColScroll);
            Array.Clear(PlayfieldAltTilemap);
            Array.Fill(PlayfieldXScale, 0x80);
            Array.Clear(PlayfieldYScale);
            Array.Clear(PlayfieldRowScroll);
            Array.Clear(PlayfieldPaletteAdd);
            Array.Clear(SpriteMix);
            Array.Clear(SpriteBlendSelect);
            Array.Clear(Blend);
            Array.Clear(Clip);
        }

        public void CopyFrom(F3LineState source)
        {
            PivotMix = source.PivotMix;
            BgPalette = source.BgPalette;
            PivotControl = source.PivotControl;
            PivotBlendSelect = source.PivotBlendSelect;
            Array.Copy(source.PlayfieldMix, PlayfieldMix, PlayfieldMix.Length);
            Array.Copy(source.PlayfieldColScroll, PlayfieldColScroll, PlayfieldColScroll.Length);
            Array.Copy(source.PlayfieldAltTilemap, PlayfieldAltTilemap, PlayfieldAltTilemap.Length);
            Array.Copy(source.PlayfieldXScale, PlayfieldXScale, PlayfieldXScale.Length);
            Array.Copy(source.PlayfieldYScale, PlayfieldYScale, PlayfieldYScale.Length);
            Array.Copy(source.PlayfieldRowScroll, PlayfieldRowScroll, PlayfieldRowScroll.Length);
            Array.Copy(source.PlayfieldPaletteAdd, PlayfieldPaletteAdd, PlayfieldPaletteAdd.Length);
            Array.Copy(source.SpriteMix, SpriteMix, SpriteMix.Length);
            Array.Copy(source.SpriteBlendSelect, SpriteBlendSelect, SpriteBlendSelect.Length);
            Array.Copy(source.Blend, Blend, Blend.Length);
            Array.Copy(source.Clip, Clip, Clip.Length);
        }

        public F3LineState Clone()
        {
            var clone = new F3LineState
            {
                PivotMix = PivotMix,
                BgPalette = BgPalette,
                PivotControl = PivotControl,
                PivotBlendSelect = PivotBlendSelect,
            };
            Array.Copy(PlayfieldMix, clone.PlayfieldMix, PlayfieldMix.Length);
            Array.Copy(PlayfieldColScroll, clone.PlayfieldColScroll, PlayfieldColScroll.Length);
            Array.Copy(PlayfieldAltTilemap, clone.PlayfieldAltTilemap, PlayfieldAltTilemap.Length);
            Array.Copy(PlayfieldXScale, clone.PlayfieldXScale, PlayfieldXScale.Length);
            Array.Copy(PlayfieldYScale, clone.PlayfieldYScale, PlayfieldYScale.Length);
            Array.Copy(PlayfieldRowScroll, clone.PlayfieldRowScroll, PlayfieldRowScroll.Length);
            Array.Copy(PlayfieldPaletteAdd, clone.PlayfieldPaletteAdd, PlayfieldPaletteAdd.Length);
            Array.Copy(SpriteMix, clone.SpriteMix, SpriteMix.Length);
            Array.Copy(SpriteBlendSelect, clone.SpriteBlendSelect, SpriteBlendSelect.Length);
            Array.Copy(Blend, clone.Blend, Blend.Length);
            Array.Copy(Clip, clone.Clip, Clip.Length);
            return clone;
        }
    }

    private struct F3ClipPlane
    {
        public short Left;
        public short Right;

        public void SetUpper(int left, int right)
        {
            Left = (short)((Left & 0x00ff) | ((sbyte)left << 8));
            Right = (short)((Right & 0x00ff) | ((sbyte)right << 8));
        }

        public void SetLower(int left, int right)
        {
            Left = (short)((Left & 0x0100) | (left & 0xff));
            Right = (short)((Right & 0x0100) | (right & 0xff));
        }
    }

    private readonly record struct F3Sprite(
        int X,
        int Y,
        bool FlipX,
        bool FlipY,
        int Code,
        byte Color,
        int ScaleX,
        int ScaleY);

    private sealed class F3TaskState
    {
        public F3TaskState(uint pc, M68000.M68000State state, int priority = 0, int delayFrames = 0)
        {
            Pc = pc;
            State = state;
            Priority = priority;
            DelayFrames = delayFrames;
        }

        public uint Pc { get; }
        public M68000.M68000State State { get; }
        public int Priority { get; }
        public int DelayFrames { get; }

        public F3TaskState WithDelay(int delayFrames)
            => new(Pc, State, Priority, delayFrames);
    }

    private struct SpriteAxis
    {
        public int BlockScale;
        public int Pos;
        public int BlockPos;
        private short _global;
        private short _subglobal;

        public SpriteAxis()
        {
            BlockScale = 1 << 8;
            Pos = 0;
            BlockPos = 0;
            _global = 0;
            _subglobal = 0;
        }

        public void Update(byte scroll, ushort positionWord, bool multi, byte blockControl, byte zoom)
        {
            short newPosition = SignExtend12(positionWord);
            if ((scroll & 0x01) != 0)
                _subglobal = newPosition;
            if ((scroll & 0x02) != 0)
                _global = newPosition;
            if ((scroll & 0x08) == 0)
            {
                newPosition = unchecked((short)(newPosition + _global));
                if ((scroll & 0x04) == 0)
                    newPosition = unchecked((short)(newPosition + _subglobal));
            }

            switch (blockControl)
            {
                case 0:
                    if (!multi)
                    {
                        BlockPos = newPosition << 8;
                        BlockScale = 0x100 - zoom;
                    }
                    Pos = BlockPos;
                    break;
                case 2:
                    Pos = BlockPos;
                    break;
                case 3:
                    Pos += BlockScale * 16;
                    break;
            }
        }

        private static short SignExtend12(ushort value)
            => (short)(((value & 0x0800) != 0 ? value | 0xf000 : value) & 0xffff);
    }

    internal sealed class TaitoF3RomSet
    {
        public byte[] MainCpu { get; private init; } = Array.Empty<byte>();
        public byte[] SoundCpu { get; private init; } = Array.Empty<byte>();
        public byte[] Sprites { get; private init; } = Array.Empty<byte>();
        public byte[] SpritesHi { get; private init; } = Array.Empty<byte>();
        public byte[] SpritePixels { get; private init; } = Array.Empty<byte>();
        public byte[] Tilemap { get; private init; } = Array.Empty<byte>();
        public byte[] TilemapHi { get; private init; } = Array.Empty<byte>();
        public byte[] TilemapPixels { get; private init; } = Array.Empty<byte>();
        public byte[] Ensoniq { get; private init; } = Array.Empty<byte>();

        public static TaitoF3RomSet Load(string path)
        {
            Dictionary<string, byte[]> entries = ReadArchive(path);
            byte[] main = new byte[0x200000];
            Load32Byte(entries, main, "d87-12.bin", 0);
            Load32Byte(entries, main, "d87-11.bin", 1);
            Load32Byte(entries, main, "d87-10.bin", 2);
            Load32Byte(entries, main, "d87-16.bin", 3);

            byte[] sound = new byte[0x180000];
            Load16Byte(entries, sound, "d87-13.bin", 0x100000);
            Load16Byte(entries, sound, "d87-14.bin", 0x100001);

            byte[] sprites = new byte[0x400000];
            Load16Byte(entries, sprites, "d87-03.bin", 0);
            Load16Byte(entries, sprites, "d87-04.bin", 1);

            byte[] tilemap = new byte[0x400000];
            Load32Word(entries, tilemap, "d87-06.bin", 0);
            Load32Word(entries, tilemap, "d87-17.bin", 2);

            byte[] ensoniq = new byte[0x800000];
            Load16Byte(entries, ensoniq, "d87-01.bin", 0);
            Load16Byte(entries, ensoniq, "d87-02.bin", 0x400000);

            byte[] spritesHi = entries["d87-05.bin"];
            byte[] tilemapHi = entries["d87-08.bin"];

            return new TaitoF3RomSet
            {
                MainCpu = main,
                SoundCpu = sound,
                Sprites = sprites,
                SpritesHi = spritesHi,
                SpritePixels = BuildSpritePixelCache(sprites, spritesHi),
                Tilemap = tilemap,
                TilemapHi = tilemapHi,
                TilemapPixels = BuildTilemapPixelCache(tilemap, tilemapHi),
                Ensoniq = ensoniq
            };
        }

        private static byte[] BuildSpritePixelCache(byte[] lowPlanes, byte[] highPlanes)
        {
            int elements = lowPlanes.Length / (16 * 8);
            byte[] pixels = new byte[elements * 0x100];
            for (int code = 0; code < elements; code++)
            {
                int tileBase = code << 8;
                for (int y = 0; y < 16; y++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        int lowOffset = code * 16 * 8 + y * 8 + (x >> 1);
                        byte packed = lowOffset < lowPlanes.Length ? lowPlanes[lowOffset] : (byte)0;
                        int pen = DecodeMamePackedLsbNibble(packed, x);
                        int highBitOffset = code * 16 * 16 * 2 + y * 16 * 2 + SpriteHiXBitOffset(x);
                        int highOffset0 = highBitOffset >> 3;
                        if ((uint)highOffset0 < (uint)highPlanes.Length)
                        {
                            pen |= ((highPlanes[highOffset0] >> (7 - (highBitOffset & 7))) & 1) << 4;
                            int highOffset1 = (highBitOffset + 1) >> 3;
                            if ((uint)highOffset1 < (uint)highPlanes.Length)
                                pen |= ((highPlanes[highOffset1] >> (7 - ((highBitOffset + 1) & 7))) & 1) << 5;
                        }

                        pixels[tileBase | (y << 4) | x] = (byte)pen;
                    }
                }
            }

            return pixels;
        }

        private static byte[] BuildTilemapPixelCache(byte[] lowPlanes, byte[] highPlanes)
        {
            int elements = lowPlanes.Length / (16 * 8);
            byte[] pixels = new byte[elements * 0x100];
            for (int code = 0; code < elements; code++)
            {
                int tileBase = code << 8;
                for (int y = 0; y < 16; y++)
                {
                    for (int x = 0; x < 16; x++)
                    {
                        int pen = DecodePacked4BppTilePixel(lowPlanes, code, x, y);
                        pen |= DecodeTilemapHighPlanes(highPlanes, code, x, y);
                        pixels[tileBase | (y << 4) | x] = (byte)pen;
                    }
                }
            }

            return pixels;
        }

        private static Dictionary<string, byte[]> ReadArchive(string path)
        {
            using IArchive archive = ArchiveFactory.Open(path);
            var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (IArchiveEntry entry in archive.Entries)
            {
                if (entry.IsDirectory)
                    continue;

                using Stream stream = entry.OpenEntryStream();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                result[Path.GetFileName(entry.Key).ToLowerInvariant()] = memory.ToArray();
            }

            foreach (string required in RequiredDariusGaidenEntries)
            {
                if (!result.ContainsKey(required))
                    throw new InvalidDataException($"Darius Gaiden ROM archive is missing '{required}'.");
            }

            return result;
        }

        private static void Load32Byte(Dictionary<string, byte[]> entries, byte[] dest, string name, int start)
        {
            byte[] source = entries[name];
            for (int i = 0, o = start; i < source.Length && o < dest.Length; i++, o += 4)
                dest[o] = source[i];
        }

        private static void Load16Byte(Dictionary<string, byte[]> entries, byte[] dest, string name, int start)
        {
            byte[] source = entries[name];
            for (int i = 0, o = start; i < source.Length && o < dest.Length; i++, o += 2)
                dest[o] = source[i];
        }

        private static void Load32Word(Dictionary<string, byte[]> entries, byte[] dest, string name, int start)
        {
            byte[] source = entries[name];
            for (int i = 0, o = start; i + 1 < source.Length && o + 1 < dest.Length; i += 2, o += 4)
            {
                dest[o] = source[i];
                dest[o + 1] = source[i + 1];
            }
        }
    }

    private sealed class Eeprom93C46
    {
        private readonly ushort[] _words = new ushort[64];
        private int _inputShift;
        private int _inputBits;
        private int _outputShift;
        private int _outputBits;
        private bool _writeEnabled;
        private bool _clock;
        private bool _chipSelect;

        public bool DataOut { get; private set; } = true;

        public void Reset()
        {
            Array.Fill(_words, (ushort)0xffff);
            _inputShift = 0;
            _inputBits = 0;
            _outputShift = 0;
            _outputBits = 0;
            _writeEnabled = false;
            _clock = false;
            _chipSelect = false;
            DataOut = true;
        }

        public void SaveState(BinaryWriter writer)
        {
            for (int i = 0; i < _words.Length; i++)
                writer.Write(_words[i]);
            writer.Write(_inputShift);
            writer.Write(_inputBits);
            writer.Write(_outputShift);
            writer.Write(_outputBits);
            writer.Write(_writeEnabled);
            writer.Write(_clock);
            writer.Write(_chipSelect);
            writer.Write(DataOut);
        }

        public void LoadState(BinaryReader reader)
        {
            for (int i = 0; i < _words.Length; i++)
                _words[i] = reader.ReadUInt16();
            _inputShift = reader.ReadInt32();
            _inputBits = reader.ReadInt32();
            _outputShift = reader.ReadInt32();
            _outputBits = reader.ReadInt32();
            _writeEnabled = reader.ReadBoolean();
            _clock = reader.ReadBoolean();
            _chipSelect = reader.ReadBoolean();
            DataOut = reader.ReadBoolean();
        }

        public void WriteLines(bool dataIn, bool clock, bool chipSelect)
        {
            if (!chipSelect)
            {
                _chipSelect = false;
                _clock = clock;
                _inputShift = 0;
                _inputBits = 0;
                _outputBits = 0;
                DataOut = true;
                return;
            }

            if (!_chipSelect)
            {
                _inputShift = 0;
                _inputBits = 0;
                _outputBits = 0;
                DataOut = true;
            }

            if (clock && !_clock)
                ClockBit(dataIn);

            _chipSelect = true;
            _clock = clock;
        }

        private void ClockBit(bool dataIn)
        {
            if (_outputBits > 0)
            {
                DataOut = ((_outputShift >> 15) & 1) != 0;
                _outputShift = (_outputShift << 1) & 0xffff;
                _outputBits--;
                return;
            }

            _inputShift = ((_inputShift << 1) | (dataIn ? 1 : 0)) & 0x1ff;
            _inputBits++;
            if (_inputBits < 9)
                return;

            int command = _inputShift & 0x1ff;
            int op = (command >> 6) & 3;
            int address = command & 0x3f;
            switch (op)
            {
                case 0b10:
                    _outputShift = _words[address];
                    _outputBits = 16;
                    DataOut = true;
                    break;
                case 0b00:
                    _writeEnabled = ((address >> 4) & 3) == 3;
                    break;
                case 0b01:
                case 0b11:
                    // Writes/erase commands are accepted but conservatively ignored
                    // until a persistent NVRAM backend is wired in.
                    _ = _writeEnabled;
                    break;
            }

            _inputShift = 0;
            _inputBits = 0;
        }
    }

    internal sealed class TaitoF3MainBus : IBusInterface, IOpcodeBusInterface
    {
        private byte[] _rom = Array.Empty<byte>();
        private byte[] _workRam = new byte[0x20000];
        private byte[] _palette = new byte[0x8000];
        private byte[] _spriteRam = new byte[0x10000];
        private byte[] _playfieldRam = new byte[0xc000];
        private byte[] _textRam = new byte[0x2000];
        private byte[] _charRam = new byte[0x2000];
        private byte[] _lineRam = new byte[0x10000];
        private byte[] _pivotRam = new byte[0x10000];
        private byte[] _dualPortRam = new byte[0x800];
        private byte[] _control0 = new byte[0x10];
        private byte[] _control1 = new byte[0x10];
        private readonly int[,] _tilemapRowUsage = new int[32, 8];
        private int _playfieldNonZeroWords;
        private int _textNonZeroWords;
        private int _pivotNonZeroWords;
        private int _spriteNonZeroWords;
        private bool _interrupt2Asserted;
        private bool _interrupt3Asserted;
        private bool _pendingInterrupt3;
        private bool _interrupt3Ready;
        private int _interrupt3DelayCycles;
        private ushort _coinWord0;
        private ushort _coinWord1;
        private ushort _timerControl0;
        private ushort _timerControl1;
        private byte _eepromOutLatch;
        private bool _previousCoin1;
        private bool _previousStart;
        private byte _coinPulseFrames;
        private byte _startLatchFrames;
        private bool _startSceneGateConsumed;
        private ushort _creditCount;
        private bool _soundCpuResetAsserted;
        private int _watchdogCyclesRemaining = F3WatchdogTimeoutCycles;
        private bool _watchdogSoftResetRequested;
        private int _watchdogKicks;
        private int _watchdogSoftResets;
        private readonly uint[] _observedTaskStacks = new uint[128];
        private int _observedTaskStackCount;
        private readonly Eeprom93C46 _eeprom = new();

        public TaitoF3InputState Input;
        public uint VectorBase { get; set; }
        public uint SourceFunctionCode { get; set; }
        public uint DestinationFunctionCode { get; set; }
        public uint CacheControl { get; set; }
        public uint CacheAddress { get; set; }
        public int WorkRamWrites { get; private set; }
        public int PaletteWrites { get; private set; }
        public int SpriteWrites { get; private set; }
        public int PlayfieldWrites { get; private set; }
        public int ControlReads { get; private set; }
        public int ControlWrites { get; private set; }
        public int DualPortReads { get; private set; }
        public int DualPortWrites { get; private set; }
        public int DualPortWriteSerial { get; private set; }
        public uint CurrentCpuPc { get; set; }
        public uint LastControlReadAddress { get; private set; }
        public byte LastControlReadValue { get; private set; }
        public uint LastDualPortReadPc { get; private set; }
        public uint LastDualPortReadAddress { get; private set; }
        public byte LastDualPortReadValue { get; private set; }
        public uint LastDualPortWritePc { get; private set; }
        public uint LastDualPortWriteAddress { get; private set; }
        public byte LastDualPortWriteValue { get; private set; }
        public uint LastSoundResetWritePc { get; private set; }
        public uint LastSoundResetWriteAddress { get; private set; }
        public uint LastModeWritePc { get; private set; }
        public uint LastModeWriteAddress { get; private set; }
        public byte LastModeWriteValue { get; private set; }
        public uint LastBackupWritePc { get; private set; }
        public byte LastBackupWriteValue { get; private set; }
        public uint LastGateWritePc { get; private set; }
        public uint LastGateWriteAddress { get; private set; }
        public byte LastGateWriteValue { get; private set; }
        public uint LastNonZeroGateWritePc { get; private set; }
        public uint LastNonZeroGateWriteAddress { get; private set; }
        public byte LastNonZeroGateWriteValue { get; private set; }
        public int NonZeroGateWrites { get; private set; }
        public uint LastNonZeroSpriteWritePc { get; private set; }
        public uint LastNonZeroSpriteWriteAddress { get; private set; }
        public byte LastNonZeroSpriteWriteValue { get; private set; }
        public uint LastNonZeroPlayfieldWritePc { get; private set; }
        public uint LastNonZeroPlayfieldWriteAddress { get; private set; }
        public byte LastNonZeroPlayfieldWriteValue { get; private set; }
        public uint LastNonZeroTextWritePc { get; private set; }
        public uint LastNonZeroTextWriteAddress { get; private set; }
        public byte LastNonZeroTextWriteValue { get; private set; }
        public uint LastSpriteListPointerWritePc { get; private set; }
        public uint LastSpriteListPointerWriteAddress { get; private set; }
        public uint LastSpriteListPointerValue { get; private set; }
        public uint LastSpriteListCountWritePc { get; private set; }
        public uint LastSpriteListCountWriteAddress { get; private set; }
        public ushort LastSpriteListCountValue { get; private set; }
        public uint LastBtstAddress { get; set; }
        public byte LastBtstValue { get; set; }
        public int LastBtstBit { get; set; }
        public uint LastModeBtstPc { get; set; }
        public uint LastModeBtstAddress { get; set; }
        public byte LastModeBtstValue { get; set; }
        public int LastModeBtstBit { get; set; }
        public bool LastModeBtstZero { get; set; }
        public int UnmappedReads { get; private set; }
        public int UnmappedWrites { get; private set; }
        public uint LastUnmappedReadPc { get; private set; }
        public uint LastUnmappedReadAddress { get; private set; }
        public uint LastUnmappedWritePc { get; private set; }
        public uint LastUnmappedWriteAddress { get; private set; }
        public byte LastUnmappedWriteValue { get; private set; }
        public uint LastIrqWorkPointerWritePc { get; private set; }
        public uint LastIrqWorkPointerWriteAddress { get; private set; }
        public uint LastIrqWorkPointerValue { get; private set; }
        public uint LastSchedulerStackWritePc { get; private set; }
        public uint LastSchedulerStackWriteAddress { get; private set; }
        public uint LastSchedulerStackPointerValue { get; private set; }
        public uint LastSchedulerMaskWritePc { get; private set; }
        public uint LastSchedulerMaskWriteAddress { get; private set; }
        public uint LastSchedulerMaskValue { get; private set; }
        public uint LastSchedulerTaskControlWritePc { get; private set; }
        public uint LastSchedulerTaskControlWriteAddress { get; private set; }
        public uint LastSchedulerTaskControlValue { get; private set; }
        public uint LastLowSchedulerStackWritePc { get; private set; }
        public uint LastLowSchedulerStackWriteAddress { get; private set; }
        public uint LastLowSchedulerStackPointerValue { get; private set; }
        public int LowSchedulerStackWrites { get; private set; }
        public uint LastTaskFrameWritePc { get; private set; }
        public uint LastTaskFrameWriteAddress { get; private set; }
        public uint LastTaskFrameWriteStack { get; private set; }
        public uint LastTaskFrameWriteFramePc { get; private set; }
        public uint FirstBadTaskFrameWritePc { get; private set; }
        public uint FirstBadTaskFrameWriteAddress { get; private set; }
        public uint FirstBadTaskFrameWriteStack { get; private set; }
        public uint FirstBadTaskFrameWriteFramePc { get; private set; }
        public uint LastSceneCopyCountWritePc { get; private set; }
        public uint LastSceneCopyCountWriteAddress { get; private set; }
        public ushort LastSceneCopyCountWriteValue { get; private set; }
        public int PlayfieldNonZeroWords => _playfieldNonZeroWords;
        public int TextNonZeroWords => _textNonZeroWords;
        public int PivotNonZeroWords => _pivotNonZeroWords;
        public int SpriteNonZeroWords => _spriteNonZeroWords;
        public int FirstNonZeroSpriteWordOffset => FindFirstNonZeroWord(_spriteRam);
        public ushort CoinWord0 => _coinWord0;
        public ushort CoinWord1 => _coinWord1;
        public int StartLatchFrames => _startLatchFrames;
        public uint InputPort0Snapshot => ReadInputPort0();
        public bool HasInsertedCredit => _creditCount != 0;
        public bool HasRomCredit => PeekWord(0x400090) != 0;
        public bool IsStartLatched => Input.Start || (!UseNativeF3TrapScheduler && _startLatchFrames != 0);
        public bool SoundCpuResetAsserted => _soundCpuResetAsserted;
        public bool CanEndFrameAtIdle => !_interrupt2Asserted && !_interrupt3Asserted && !_pendingInterrupt3;
        public uint CyclesUntilInterrupt3Ready
            => _pendingInterrupt3 && !_interrupt3Ready && !_interrupt3Asserted && _interrupt3DelayCycles > 0
                ? (uint)_interrupt3DelayCycles
                : 0;
        public int WatchdogKicks => _watchdogKicks;
        public int WatchdogSoftResets => _watchdogSoftResets;
        public int WatchdogCyclesRemaining => _watchdogCyclesRemaining;
        public bool IsPlayfieldRowUsed(int row, int tilemap)
            => (uint)row < 32 && (uint)tilemap < 8 && _tilemapRowUsage[row, tilemap] > 0;

        public BusSignals Signals => new(false);
        public ushort CurrentOpcode { get; private set; }

        public void Load(TaitoF3RomSet roms)
        {
            _rom = roms.MainCpu;
            ResetRuntime();
        }

        public void ResetRuntime()
        {
            Array.Clear(_workRam);
            Array.Clear(_palette);
            Array.Clear(_spriteRam);
            Array.Clear(_playfieldRam);
            Array.Clear(_textRam);
            Array.Clear(_charRam);
            Array.Clear(_lineRam);
            Array.Clear(_pivotRam);
            Array.Clear(_dualPortRam);
            Array.Clear(_control0);
            Array.Clear(_control1);
            Array.Clear(_tilemapRowUsage);
            _playfieldNonZeroWords = 0;
            _textNonZeroWords = 0;
            _pivotNonZeroWords = 0;
            _spriteNonZeroWords = 0;
            _interrupt2Asserted = false;
            _interrupt3Asserted = false;
            _pendingInterrupt3 = false;
            _interrupt3Ready = false;
            _interrupt3DelayCycles = 0;
            _coinWord0 = 0;
            _coinWord1 = 0;
            _timerControl0 = 0;
            _timerControl1 = 0;
            _eepromOutLatch = 0;
            _previousCoin1 = false;
            _previousStart = false;
            _coinPulseFrames = 0;
            _startLatchFrames = 0;
            _startSceneGateConsumed = false;
            _creditCount = 0;
            _eeprom.Reset();
            _soundCpuResetAsserted = true;
            _watchdogCyclesRemaining = F3WatchdogTimeoutCycles;
            _watchdogSoftResetRequested = false;
            _watchdogKicks = 0;
            _watchdogSoftResets = 0;
            VectorBase = 0;
            SourceFunctionCode = 0;
            DestinationFunctionCode = 0;
            CacheControl = 0;
            CacheAddress = 0;
            WorkRamWrites = PaletteWrites = SpriteWrites = PlayfieldWrites = 0;
            ControlReads = ControlWrites = DualPortReads = DualPortWrites = UnmappedReads = UnmappedWrites = 0;
            LastControlReadAddress = 0;
            LastControlReadValue = 0;
            LastDualPortReadPc = 0;
            LastDualPortReadAddress = 0;
            LastDualPortReadValue = 0;
            LastDualPortWritePc = 0;
            LastDualPortWriteAddress = 0;
            LastDualPortWriteValue = 0;
            LastSoundResetWritePc = 0;
            LastSoundResetWriteAddress = 0;
            LastModeWritePc = 0;
            LastModeWriteAddress = 0;
            LastModeWriteValue = 0;
            LastBackupWritePc = 0;
            LastBackupWriteValue = 0;
            LastGateWritePc = 0;
            LastGateWriteAddress = 0;
            LastGateWriteValue = 0;
            LastNonZeroGateWritePc = 0;
            LastNonZeroGateWriteAddress = 0;
            LastNonZeroGateWriteValue = 0;
            NonZeroGateWrites = 0;
            LastNonZeroSpriteWritePc = 0;
            LastNonZeroSpriteWriteAddress = 0;
            LastNonZeroSpriteWriteValue = 0;
            LastNonZeroPlayfieldWritePc = 0;
            LastNonZeroPlayfieldWriteAddress = 0;
            LastNonZeroPlayfieldWriteValue = 0;
            LastNonZeroTextWritePc = 0;
            LastNonZeroTextWriteAddress = 0;
            LastNonZeroTextWriteValue = 0;
            LastSpriteListPointerWritePc = 0;
            LastSpriteListPointerWriteAddress = 0;
            LastSpriteListPointerValue = 0;
            LastSpriteListCountWritePc = 0;
            LastSpriteListCountWriteAddress = 0;
            LastSpriteListCountValue = 0;
            LastBtstAddress = 0;
            LastBtstValue = 0;
            LastBtstBit = 0;
            LastModeBtstPc = 0;
            LastModeBtstAddress = 0;
            LastModeBtstValue = 0;
            LastModeBtstBit = 0;
            LastModeBtstZero = false;
            LastUnmappedReadPc = 0;
            LastUnmappedReadAddress = 0;
            LastUnmappedWritePc = 0;
            LastUnmappedWriteAddress = 0;
            LastUnmappedWriteValue = 0;
            LastIrqWorkPointerWritePc = 0;
            LastIrqWorkPointerWriteAddress = 0;
            LastIrqWorkPointerValue = 0;
            LastSchedulerStackWritePc = 0;
            LastSchedulerStackWriteAddress = 0;
            LastSchedulerStackPointerValue = 0;
            LastSchedulerMaskWritePc = 0;
            LastSchedulerMaskWriteAddress = 0;
            LastSchedulerMaskValue = 0;
            LastSchedulerTaskControlWritePc = 0;
            LastSchedulerTaskControlWriteAddress = 0;
            LastSchedulerTaskControlValue = 0;
            LastLowSchedulerStackWritePc = 0;
            LastLowSchedulerStackWriteAddress = 0;
            LastLowSchedulerStackPointerValue = 0;
            LowSchedulerStackWrites = 0;
            LastTaskFrameWritePc = 0;
            LastTaskFrameWriteAddress = 0;
            LastTaskFrameWriteStack = 0;
            LastTaskFrameWriteFramePc = 0;
            FirstBadTaskFrameWritePc = 0;
            FirstBadTaskFrameWriteAddress = 0;
            FirstBadTaskFrameWriteStack = 0;
            FirstBadTaskFrameWriteFramePc = 0;
            Array.Clear(_observedTaskStacks);
            _observedTaskStackCount = 0;
            LastSceneCopyCountWritePc = 0;
            LastSceneCopyCountWriteAddress = 0;
            LastSceneCopyCountWriteValue = 0;
            EnsureBackupDefaults();
        }

        public void BeginFrameInterrupt()
        {
            // MAME's F3 driver asserts IRQ2 on vblank and then IRQ3 after
            // roughly 10000 main CPU cycles. Keep IRQ3 delayed so the ROM
            // scheduler sees the same ocean tide instead of a back-to-back pair.
            _interrupt2Asserted = true;
            _pendingInterrupt3 = true;
            _interrupt3Ready = false;
            _interrupt3DelayCycles = 10_000;
            if (!UseNativeF3TrapScheduler)
                LatchSchedulerFrameTick();
        }

        public void PulseBootSchedulerGate()
        {
            // The boot scheduler needs one external wake edge before the ROM's
            // own task traps are active. Keep it out of the steady-state path:
            // bit 0 is later used by game code as a scene restart gate.
            PulseSchedulerGateBit0();
        }

        public void PulseSchedulerGateBit0()
        {
            _workRam[0x006bb4] |= 0x01;
            _workRam[0x006bb5] |= 0x01;
        }

        public void ClearStartSceneGateBit0()
        {
            _workRam[0x006bb4] = (byte)(_workRam[0x006bb4] & ~0x01);
            _workRam[0x006bb5] = (byte)(_workRam[0x006bb5] & ~0x01);
            _startLatchFrames = 0;
            _startSceneGateConsumed = true;
        }

        public void AdvanceMainCycles(uint cycles)
        {
            uint pc = CurrentCpuPc & 0x00ff_ffff;
            if (!_watchdogSoftResetRequested && pc is not 0x002312 and not 0x002326)
            {
                uint emulatedCycles = Math.Max(1u, cycles / (uint)CpuScale);
                _watchdogCyclesRemaining -= (int)Math.Min(emulatedCycles, int.MaxValue);
                if (_watchdogCyclesRemaining <= 0)
                {
                    _watchdogSoftResetRequested = true;
                    _watchdogSoftResets++;
                }
            }

            if (!_pendingInterrupt3 || _interrupt3Ready || _interrupt3DelayCycles <= 0)
                return;

            _interrupt3DelayCycles -= (int)Math.Min(cycles, int.MaxValue);
            if (_interrupt3DelayCycles > 0)
                return;

            _interrupt3Ready = true;
            _interrupt3DelayCycles = 0;
            _interrupt3Asserted = true;
        }

        public bool ConsumeWatchdogSoftReset()
        {
            if (!_watchdogSoftResetRequested)
                return false;

            _watchdogSoftResetRequested = false;
            _watchdogCyclesRemaining = F3WatchdogTimeoutCycles;
            ClearInterruptLines();
            return true;
        }

        public void RefreshInputLatches()
        {
            // MAME exposes TC0640FIO through the 0x4a0000 control window. The
            // game is responsible for copying live inputs into its own work RAM.
            if (!UseNativeF3TrapScheduler && Input.Start && !_previousStart)
                _startLatchFrames = 18;

            _previousCoin1 = Input.Coin1;
            _previousStart = Input.Start;
            if (!UseNativeF3TrapScheduler && _startLatchFrames != 0)
                _startLatchFrames--;
        }

        public ushort ReadPlayfieldWord(int wordOffset)
        {
            int offset = wordOffset * 2;
            if ((uint)(offset + 1) >= (uint)_playfieldRam.Length)
                return 0;

            return (ushort)((_playfieldRam[offset] << 8) | _playfieldRam[offset + 1]);
        }

        public ushort ReadSpriteWord(int wordOffset)
        {
            int offset = wordOffset * 2;
            if ((uint)(offset + 1) >= (uint)_spriteRam.Length)
                return 0;

            return (ushort)((_spriteRam[offset] << 8) | _spriteRam[offset + 1]);
        }

        public void WriteSpriteWordAddress(uint address, ushort value)
        {
            uint relative = (address & 0x00ff_ffff) - 0x600000u;
            if (relative >= (uint)_spriteRam.Length - 1u)
            {
                WriteWord(address, value);
                return;
            }

            int spriteOffset = (int)relative;
            ushort before = ReadBigEndianWord(_spriteRam, spriteOffset & ~1);
            _spriteRam[spriteOffset] = (byte)(value >> 8);
            _spriteRam[spriteOffset + 1] = (byte)value;
            ushort after = ReadBigEndianWord(_spriteRam, spriteOffset & ~1);
            if (before == 0 && after != 0)
                _spriteNonZeroWords++;
            else if (before != 0 && after == 0)
                _spriteNonZeroWords--;
            if (value != 0)
            {
                LastNonZeroSpriteWritePc = CurrentCpuPc;
                LastNonZeroSpriteWriteAddress = address & 0x00ff_ffff;
                LastNonZeroSpriteWriteValue = (byte)value;
            }
            SpriteWrites += 2;
        }

        public void WriteSpriteLongAddress(uint address, uint value)
        {
            WriteSpriteWordAddress(address, (ushort)(value >> 16));
            WriteSpriteWordAddress(address + 2, (ushort)value);
        }

        public ushort ReadTextWord(int wordOffset)
        {
            int offset = wordOffset * 2;
            if ((uint)(offset + 1) >= (uint)_textRam.Length)
                return 0;

            return (ushort)((_textRam[offset] << 8) | _textRam[offset + 1]);
        }

        public byte ReadCharGfxByte(int offset)
        {
            if ((uint)offset >= (uint)_charRam.Length)
                return 0;

            return _charRam[offset ^ 1];
        }

        public byte ReadPivotGfxByte(int offset)
        {
            if ((uint)offset >= (uint)_pivotRam.Length)
                return 0;

            return _pivotRam[offset ^ 1];
        }

        public ushort ReadControlWord(int group, int wordOffset)
        {
            byte[] source = group == 0 ? _control0 : _control1;
            int offset = wordOffset * 2;
            if ((uint)(offset + 1) >= (uint)source.Length)
                return 0;

            return (ushort)((source[offset] << 8) | source[offset + 1]);
        }

        public ushort ReadLineRamWord(int wordOffset)
        {
            int offset = wordOffset * 2;
            if ((uint)(offset + 1) >= (uint)_lineRam.Length)
                return 0;

            return ReadBigEndianWord(_lineRam, offset);
        }

        public uint ReadPaletteColor(int paletteIndex, uint fallback)
        {
            int offset = (paletteIndex & 0x1fff) * 4;
            if ((uint)(offset + 2) >= (uint)_palette.Length)
                return fallback;

            byte r = _palette[offset + 1];
            byte g = _palette[offset + 2];
            byte b = _palette[offset + 3];
            if ((r | g | b) == 0)
                return fallback;

            return 0xff000000u | ((uint)r << 16) | ((uint)g << 8) | b;
        }

        private void WriteWorkRamByteSilently(uint address, byte value)
        {
            if (MapWindow(address & 0x00ff_ffff, F3WorkRamBase, F3WorkRamMirrorWindowSize, _workRam, out int ramOffset))
                _workRam[ramOffset] = value;
        }

        private void WriteWorkRamWordSilently(uint address, ushort value)
        {
            if (!MapWindow(address & 0x00ff_ffff, F3WorkRamBase, F3WorkRamMirrorWindowSize, _workRam, out int ramOffset))
                return;

            _workRam[ramOffset] = (byte)(value >> 8);
            _workRam[ramOffset + 1] = (byte)value;
        }

        private void WriteWorkRamLongSilently(uint address, uint value)
        {
            WriteWorkRamWordSilently(address, (ushort)(value >> 16));
            WriteWorkRamWordSilently(address + 2, (ushort)value);
        }

        private void LatchSchedulerFrameTick()
        {
            // The ROM copies A5-$144c to A5-$144b in its own scheduler path.
            // Bit 0 is a scene/restart gate set by game code, not a vblank tick;
            // forcing it every frame makes Darius re-enter scene init forever.
            // Darius' p4 task spins while bit 1 is high, so clear the hold bit
            // before the ROM task scheduler tests it.
            _workRam[0x006bb4] = (byte)(_workRam[0x006bb4] & ~0x02);
            _workRam[0x006bb5] = (byte)(_workRam[0x006bb5] & ~0x02);

            // The ROM's sprite producer advances A5-$0ca0 as it emits entries,
            // and the frame-end F3 task at 0x00144a rewinds it to 0x600310.
            // Our trap scheduler can starve that finalize task, so mirror the
            // same per-vblank latch here to keep the producer inside sprite RAM.
            WriteWorkRamLongSilently(0x407360, 0x0060_0310);
        }

        public byte ReadByte(uint address)
        {
            address &= 0x00ff_ffff;
            if (TryReadByte(address, out byte value))
                return value;

            UnmappedReads++;
            LastUnmappedReadPc = CurrentCpuPc;
            LastUnmappedReadAddress = address;
            return 0xff;
        }

        public ushort ReadWord(uint address)
        {
            address &= 0x00ff_ffff;
            if (TryReadWordFast(address, out ushort value))
            {
                CurrentOpcode = value;
                return value;
            }

            CurrentOpcode = (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
            return CurrentOpcode;
        }

        public ushort ReadDataWord(uint address)
        {
            address &= 0x00ff_ffff;
            if (TryReadWordFast(address, out ushort value))
                return value;

            return (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
        }

        public uint ReadLong(uint address)
        {
            address &= 0x00ff_ffff;
            if (TryReadLongFast(address, out uint value))
                return value;

            return ((uint)ReadWord(address) << 16) | ReadWord(address + 2);
        }

        public uint ReadDataLong(uint address)
        {
            address &= 0x00ff_ffff;
            if (TryReadLongFast(address, out uint value))
                return value;

            return ((uint)ReadDataWord(address) << 16) | ReadDataWord(address + 2);
        }

        public ushort ReadOpcodeWord(uint address)
        {
            address &= 0x00ff_ffff;
            if (address + 1u < (uint)_rom.Length)
            {
                ushort value = (ushort)((_rom[address] << 8) | _rom[address + 1]);
                CurrentOpcode = value;
                return value;
            }

            return ReadWord(address);
        }

        public byte SoundReadDualPortByte(int offset)
        {
            offset &= _dualPortRam.Length - 1;
            return _dualPortRam[offset];
        }

        public void SoundWriteDualPortByte(int offset, byte value)
        {
            offset &= _dualPortRam.Length - 1;
            _dualPortRam[offset] = value;
        }

        public byte PeekByte(uint address)
            => TryReadByte(address & 0x00ff_ffff, out byte value) ? value : (byte)0xff;

        public ushort PeekWord(uint address)
            => (ushort)((PeekByte(address) << 8) | PeekByte(address + 1));

        public uint PeekLong(uint address)
            => ((uint)PeekWord(address) << 16) | PeekWord(address + 2);

        public void WriteByte(uint address, byte value)
        {
            address &= 0x00ff_ffff;
            if (TryWriteByte(address, value))
                return;

            UnmappedWrites++;
            LastUnmappedWritePc = CurrentCpuPc;
            LastUnmappedWriteAddress = address;
            LastUnmappedWriteValue = value;
        }

        public void WriteWord(uint address, ushort value)
        {
            address &= 0x00ff_ffff;
            if (TryWriteControlWord(address, value))
                return;
            if (TryWriteWordFast(address, value))
                return;

            WriteByte(address, (byte)(value >> 8));
            WriteByte(address + 1, (byte)value);
        }

        public void WriteLong(uint address, uint value)
        {
            address &= 0x00ff_ffff;
            if (TryWriteControlLong(address, value))
                return;

            WriteWord(address, (ushort)(value >> 16));
            WriteWord(address + 2, (ushort)value);
        }

        public byte InterruptLevel()
        {
            // MAME's CPU line state resolves to the highest asserted interrupt
            // level, even though F3 asserts IRQ2 before the delayed IRQ3.
            if (_interrupt3Asserted)
                return 3;
            if (_interrupt2Asserted)
                return 2;
            return 0;
        }

        public void AcknowledgeInterrupt(byte level)
        {
            if (level == 2)
            {
                _interrupt2Asserted = false;
                return;
            }

            if (level == 3)
            {
                _interrupt3Asserted = false;
                _pendingInterrupt3 = false;
                _interrupt3Ready = false;
                _interrupt3DelayCycles = 0;
            }
        }

        private void ClearInterruptLines()
        {
            _interrupt2Asserted = false;
            _interrupt3Asserted = false;
            _pendingInterrupt3 = false;
            _interrupt3Ready = false;
            _interrupt3DelayCycles = 0;
        }

        public bool Reset() => _watchdogSoftResetRequested;
        public bool Halt() => false;

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_workRam);
            writer.Write(_palette);
            writer.Write(_spriteRam);
            writer.Write(_playfieldRam);
            writer.Write(_textRam);
            writer.Write(_charRam);
            writer.Write(_lineRam);
            writer.Write(_pivotRam);
            writer.Write(_dualPortRam);
            writer.Write(_textNonZeroWords);
            writer.Write(_spriteNonZeroWords);
            writer.Write(InterruptLevel());
            writer.Write(_pendingInterrupt3);
            writer.Write(_interrupt3Ready);
            writer.Write(_interrupt3DelayCycles);
            writer.Write(_coinWord0);
            writer.Write(_coinWord1);
            writer.Write(_timerControl0);
            writer.Write(_timerControl1);
            writer.Write(_eepromOutLatch);
            writer.Write(_coinPulseFrames);
            writer.Write(_startLatchFrames);
            writer.Write(_startSceneGateConsumed);
            _eeprom.SaveState(writer);
            writer.Write(_soundCpuResetAsserted);
            writer.Write(VectorBase);
            writer.Write(SourceFunctionCode);
            writer.Write(DestinationFunctionCode);
            writer.Write(CacheControl);
            writer.Write(CacheAddress);
        }

        public void LoadState(BinaryReader reader, int version)
        {
            ReadExact(reader, _workRam);
            ReadExact(reader, _palette);
            ReadExact(reader, _spriteRam);
            ReadExact(reader, _playfieldRam);
            ReadExact(reader, _textRam);
            ReadExact(reader, _charRam);
            ReadExact(reader, _lineRam);
            ReadExact(reader, _pivotRam);
            ReadExact(reader, _dualPortRam);
            RebuildPlayfieldRowUsage();
            _textNonZeroWords = version >= 6 ? reader.ReadInt32() : CountNonZeroWords(_textRam);
            _spriteNonZeroWords = version >= 7 ? reader.ReadInt32() : CountNonZeroWords(_spriteRam);
            _pivotNonZeroWords = CountNonZeroWords(_pivotRam);
            byte savedInterruptLevel = reader.ReadByte();
            _interrupt2Asserted = savedInterruptLevel == 2;
            _interrupt3Asserted = savedInterruptLevel == 3;
            _pendingInterrupt3 = version >= 5 && reader.ReadBoolean();
            _interrupt3Ready = version >= 10 && reader.ReadBoolean();
            _interrupt3DelayCycles = version >= 10 ? reader.ReadInt32() : 0;
            if (_interrupt3Ready)
                _interrupt3Asserted = true;
            _timerControl0 = 0;
            _timerControl1 = 0;
            _eepromOutLatch = 0;
            _eeprom.Reset();
            _soundCpuResetAsserted = true;
            if (version >= 4)
            {
                _coinWord0 = reader.ReadUInt16();
                _coinWord1 = reader.ReadUInt16();
                if (version >= 8)
                {
                    _timerControl0 = reader.ReadUInt16();
                    _timerControl1 = reader.ReadUInt16();
                    _eepromOutLatch = reader.ReadByte();
                    if (version >= 11)
                    {
                        _coinPulseFrames = reader.ReadByte();
                        _startLatchFrames = reader.ReadByte();
                        _startSceneGateConsumed = reader.ReadBoolean();
                    }
                    else
                    {
                        _coinPulseFrames = 0;
                        _startLatchFrames = 0;
                        _startSceneGateConsumed = false;
                    }
                    if (version >= 9)
                        _eeprom.LoadState(reader);
                    _soundCpuResetAsserted = reader.ReadBoolean();
                }
                VectorBase = reader.ReadUInt32();
                SourceFunctionCode = reader.ReadUInt32();
                DestinationFunctionCode = reader.ReadUInt32();
                CacheControl = reader.ReadUInt32();
                CacheAddress = reader.ReadUInt32();
            }
            else
            {
                _pendingInterrupt3 = false;
                _interrupt2Asserted = false;
                _interrupt3Asserted = false;
                _coinWord0 = 0;
                _coinWord1 = 0;
                _timerControl0 = 0;
                _timerControl1 = 0;
                _eepromOutLatch = 0;
                _coinPulseFrames = 0;
                _startLatchFrames = 0;
                _startSceneGateConsumed = false;
                _eeprom.Reset();
                _soundCpuResetAsserted = true;
                VectorBase = 0;
                SourceFunctionCode = 0;
                DestinationFunctionCode = 0;
                CacheControl = 0;
                CacheAddress = 0;
            }
        }

        private bool TryReadByte(uint address, out byte value)
        {
            if (address < _rom.Length)
            {
                value = _rom[address];
                return true;
            }

            if (MapWindow(address, F3WorkRamBase, F3WorkRamMirrorWindowSize, _workRam, out int ramOffset))
            {
                value = ShouldReadNeutralFioSoftStatus(address)
                    ? (byte)0x00
                    : _workRam[ramOffset];
                return true;
            }

            if (MapWindow(address, 0x440000, 0x8000, _palette, out int paletteOffset))
            {
                value = _palette[paletteOffset];
                return true;
            }

            if (address >= 0x4a0000 && address <= 0x4a001b)
            {
                ControlReads++;
                value = ReadControlByte(address);
                LastControlReadAddress = address;
                LastControlReadValue = value;
                return true;
            }

            if (MapWindow(address, 0x600000, 0x10000, _spriteRam, out int spriteOffset))
            {
                value = _spriteRam[spriteOffset];
                return true;
            }

            if (MapWindow(address, 0x610000, 0xc000, _playfieldRam, out int pfOffset))
            {
                value = _playfieldRam[pfOffset];
                return true;
            }

            if (MapWindow(address, 0x61c000, 0x2000, _textRam, out int textOffset))
            {
                value = _textRam[textOffset];
                return true;
            }

            if (MapWindow(address, 0x61e000, 0x2000, _charRam, out int charOffset))
            {
                value = _charRam[charOffset];
                return true;
            }

            if (MapWindow(address, 0x620000, 0x10000, _lineRam, out int lineOffset))
            {
                value = _lineRam[lineOffset];
                return true;
            }

            if (MapWindow(address, 0x630000, 0x10000, _pivotRam, out int pivotOffset))
            {
                value = _pivotRam[pivotOffset];
                return true;
            }

            if (MapWindow(address, 0xc00000, 0x800, _dualPortRam, out int dpramOffset))
            {
                value = _dualPortRam[dpramOffset];
                DualPortReads++;
                LastDualPortReadPc = CurrentCpuPc;
                LastDualPortReadAddress = address;
                LastDualPortReadValue = value;
                return true;
            }

            value = 0xff;
            return false;
        }

        private bool TryReadWordFast(uint address, out ushort value)
        {
            if (address + 1u < (uint)_rom.Length)
            {
                value = (ushort)((_rom[address] << 8) | _rom[address + 1]);
                return true;
            }

            uint relative = address - F3WorkRamBase;
            if (relative < F3WorkRamMirrorWindowSize)
            {
                int ramOffset = (int)(relative & ((uint)_workRam.Length - 1u));
                if ((uint)(ramOffset + 1) < (uint)_workRam.Length)
                {
                    value = (ushort)((_workRam[ramOffset] << 8) | _workRam[ramOffset + 1]);
                    return true;
                }
            }

            relative = address - 0x440000u;
            if (relative < (uint)_palette.Length - 1u)
            {
                int paletteOffset = (int)relative;
                value = (ushort)((_palette[paletteOffset] << 8) | _palette[paletteOffset + 1]);
                return true;
            }

            relative = address - 0x600000u;
            if (relative < (uint)_spriteRam.Length - 1u)
            {
                int spriteOffset = (int)relative;
                value = (ushort)((_spriteRam[spriteOffset] << 8) | _spriteRam[spriteOffset + 1]);
                return true;
            }

            relative = address - 0x610000u;
            if (relative < (uint)_playfieldRam.Length - 1u)
            {
                int pfOffset = (int)relative;
                value = (ushort)((_playfieldRam[pfOffset] << 8) | _playfieldRam[pfOffset + 1]);
                return true;
            }

            relative = address - 0x61c000u;
            if (relative < (uint)_textRam.Length - 1u)
            {
                int textOffset = (int)relative;
                value = (ushort)((_textRam[textOffset] << 8) | _textRam[textOffset + 1]);
                return true;
            }

            relative = address - 0x61e000u;
            if (relative < (uint)_charRam.Length - 1u)
            {
                int charOffset = (int)relative;
                value = (ushort)((_charRam[charOffset] << 8) | _charRam[charOffset + 1]);
                return true;
            }

            relative = address - 0x620000u;
            if (relative < (uint)_lineRam.Length - 1u)
            {
                int lineOffset = (int)relative;
                value = (ushort)((_lineRam[lineOffset] << 8) | _lineRam[lineOffset + 1]);
                return true;
            }

            relative = address - 0x630000u;
            if (relative < (uint)_pivotRam.Length - 1u)
            {
                int pivotOffset = (int)relative;
                value = (ushort)((_pivotRam[pivotOffset] << 8) | _pivotRam[pivotOffset + 1]);
                return true;
            }

            value = 0xffff;
            return false;
        }

        private bool TryReadLongFast(uint address, out uint value)
        {
            if (address + 3u < (uint)_rom.Length)
            {
                value = ((uint)_rom[address] << 24)
                    | ((uint)_rom[address + 1] << 16)
                    | ((uint)_rom[address + 2] << 8)
                    | _rom[address + 3];
                return true;
            }

            if (TryReadWordFast(address, out ushort high) && TryReadWordFast(address + 2, out ushort low))
            {
                value = ((uint)high << 16) | low;
                return true;
            }

            value = 0xffff_ffffu;
            return false;
        }

        private bool TryWriteByte(uint address, byte value)
        {
            if (MapWindow(address, F3WorkRamBase, F3WorkRamMirrorWindowSize, _workRam, out int ramOffset))
            {
                _workRam[ramOffset] = value;
                if (address is 0x40221d or 0x40223d or 0x40223f)
                {
                    LastModeWritePc = CurrentCpuPc;
                    LastModeWriteAddress = address;
                    LastModeWriteValue = value;
                }
                if (address == 0x406c6c)
                {
                    LastBackupWritePc = CurrentCpuPc;
                    LastBackupWriteValue = value;
                }
                if (address is 0x406bb4 or 0x406bb5)
                {
                    LastGateWritePc = CurrentCpuPc;
                    LastGateWriteAddress = address;
                    LastGateWriteValue = value;
                    if (value != 0)
                    {
                        LastNonZeroGateWritePc = CurrentCpuPc;
                        LastNonZeroGateWriteAddress = address;
                        LastNonZeroGateWriteValue = value;
                        NonZeroGateWrites++;
                    }
                }
                if (address >= 0x407360 && address <= 0x40736b)
                {
                    LastSpriteListPointerWritePc = CurrentCpuPc;
                    LastSpriteListPointerWriteAddress = address;
                    LastSpriteListPointerValue = PeekLong(address & 0x00ff_fffcu);
                }
                if (address is 0x402218 or 0x402219)
                {
                    LastSpriteListCountWritePc = CurrentCpuPc;
                    LastSpriteListCountWriteAddress = address;
                    LastSpriteListCountValue = PeekWord(0x402218);
                }
                if (address >= 0x406704 && address <= 0x406707)
                {
                    LastIrqWorkPointerWritePc = CurrentCpuPc;
                    LastIrqWorkPointerWriteAddress = address;
                    LastIrqWorkPointerValue = PeekLong(0x406704);
                }
                if (address >= 0x4066fc && address <= 0x4066ff)
                {
                    LastSchedulerMaskWritePc = CurrentCpuPc;
                    LastSchedulerMaskWriteAddress = address;
                    LastSchedulerMaskValue = PeekLong(0x4066fc);
                }
                if (address >= 0x406708 && address <= 0x406787)
                {
                    uint slotAddress = address & 0x00ff_fffcu;
                    LastSchedulerTaskControlWritePc = CurrentCpuPc;
                    LastSchedulerTaskControlWriteAddress = address;
                    LastSchedulerTaskControlValue = PeekLong(slotAddress);
                }
                if (address >= 0x4066b4 && address <= 0x4066f7)
                {
                    uint slotAddress = address & 0x00ff_fffcu;
                    uint slotValue = PeekLong(slotAddress);
                    LastSchedulerStackWritePc = CurrentCpuPc;
                    LastSchedulerStackWriteAddress = address;
                    LastSchedulerStackPointerValue = slotValue;
                    RecordObservedTaskStack(slotValue);
                    if ((address & 3) == 3 && slotValue != 0 && slotValue < F3WorkRamBase)
                    {
                        LastLowSchedulerStackWritePc = CurrentCpuPc;
                        LastLowSchedulerStackWriteAddress = address;
                        LastLowSchedulerStackPointerValue = slotValue;
                        LowSchedulerStackWrites++;
                    }
                }
                if (address is 0x402210 or 0x402211)
                {
                    LastSceneCopyCountWritePc = CurrentCpuPc;
                    LastSceneCopyCountWriteAddress = address;
                    LastSceneCopyCountWriteValue = PeekWord(0x402210);
                }
                TrackTaskFrameWrite(address);
                WorkRamWrites++;
                return true;
            }

            if (MapWindow(address, 0x440000, 0x8000, _palette, out int paletteOffset))
            {
                _palette[paletteOffset] = value;
                PaletteWrites++;
                return true;
            }

            if (address >= 0x4a0000 && address <= 0x4a001f)
            {
                WriteControlByte(address, value);
                ControlWrites++;
                return true;
            }

            if (address >= 0x4c0000 && address <= 0x4c0003)
            {
                WriteTimerControlByte(address, value);
                ControlWrites++;
                return true;
            }

            if (MapWindow(address, 0x600000, 0x10000, _spriteRam, out int spriteOffset))
            {
                ushort before = ReadBigEndianWord(_spriteRam, spriteOffset & ~1);
                _spriteRam[spriteOffset] = value;
                ushort after = ReadBigEndianWord(_spriteRam, spriteOffset & ~1);
                if (before == 0 && after != 0)
                    _spriteNonZeroWords++;
                else if (before != 0 && after == 0)
                    _spriteNonZeroWords--;
                if (value != 0)
                {
                    LastNonZeroSpriteWritePc = CurrentCpuPc;
                    LastNonZeroSpriteWriteAddress = address;
                    LastNonZeroSpriteWriteValue = value;
                }
                SpriteWrites++;
                return true;
            }

            if (MapWindow(address, 0x610000, 0xc000, _playfieldRam, out int pfOffset))
            {
                int wordOffset = pfOffset >> 1;
                ushort before = ReadBigEndianWord(_playfieldRam, pfOffset & ~1);
                _playfieldRam[pfOffset] = value;
                ushort after = ReadBigEndianWord(_playfieldRam, pfOffset & ~1);
                if (before == 0 && after != 0)
                    _playfieldNonZeroWords++;
                else if (before != 0 && after == 0)
                    _playfieldNonZeroWords--;
                UpdatePlayfieldRowUsage(wordOffset, before, after);
                if (value != 0)
                {
                    LastNonZeroPlayfieldWritePc = CurrentCpuPc;
                    LastNonZeroPlayfieldWriteAddress = address;
                    LastNonZeroPlayfieldWriteValue = value;
                }
                PlayfieldWrites++;
                return true;
            }

            if (MapWindow(address, 0x61c000, 0x2000, _textRam, out int textOffset))
            {
                ushort before = ReadBigEndianWord(_textRam, textOffset & ~1);
                _textRam[textOffset] = value;
                ushort after = ReadBigEndianWord(_textRam, textOffset & ~1);
                if (before == 0 && after != 0)
                    _textNonZeroWords++;
                else if (before != 0 && after == 0)
                    _textNonZeroWords--;
                if (value != 0)
                {
                    LastNonZeroTextWritePc = CurrentCpuPc;
                    LastNonZeroTextWriteAddress = address;
                    LastNonZeroTextWriteValue = value;
                }
                PlayfieldWrites++;
                return true;
            }

            if (MapWindow(address, 0x61e000, 0x2000, _charRam, out int charOffset))
            {
                _charRam[charOffset] = value;
                PlayfieldWrites++;
                return true;
            }

            if (MapWindow(address, 0x620000, 0x10000, _lineRam, out int lineOffset))
            {
                _lineRam[lineOffset] = value;
                PlayfieldWrites++;
                return true;
            }

            if (MapWindow(address, 0x630000, 0x10000, _pivotRam, out int pivotOffset))
            {
                ushort before = ReadBigEndianWord(_pivotRam, pivotOffset & ~1);
                _pivotRam[pivotOffset] = value;
                ushort after = ReadBigEndianWord(_pivotRam, pivotOffset & ~1);
                if (before == 0 && after != 0)
                    _pivotNonZeroWords++;
                else if (before != 0 && after == 0)
                    _pivotNonZeroWords--;
                PlayfieldWrites++;
                return true;
            }

            if (address >= 0x660000 && address <= 0x66001f)
            {
                byte[] target = address < 0x660010 ? _control0 : _control1;
                target[(address - (address < 0x660010 ? 0x660000 : 0x660010)) & 0x0f] = value;
                ControlWrites++;
                return true;
            }

            if (MapWindow(address, 0xc00000, 0x800, _dualPortRam, out int dpramOffset))
            {
                _dualPortRam[dpramOffset] = value;
                DualPortWrites++;
                DualPortWriteSerial++;
                LastDualPortWritePc = CurrentCpuPc;
                LastDualPortWriteAddress = address;
                LastDualPortWriteValue = value;
                return true;
            }

            if ((address >= 0x300000 && address <= 0x30007f)
                || (address >= 0xc80000 && address <= 0xc80003)
                || (address >= 0xc80100 && address <= 0xc80103))
            {
                if (address >= 0xc80000 && address <= 0xc80003)
                    _soundCpuResetAsserted = false;
                else if (address >= 0xc80100 && address <= 0xc80103)
                    _soundCpuResetAsserted = true;
                if (address >= 0xc80000 && address <= 0xc80103)
                {
                    LastSoundResetWritePc = CurrentCpuPc;
                    LastSoundResetWriteAddress = address;
                }
                ControlWrites++;
                return true;
            }

            return false;
        }

        private bool TryWriteWordFast(uint address, ushort value)
        {
            if ((address & 1) != 0)
                return false;

            uint relative = address - 0x440000u;
            if (relative < (uint)_palette.Length - 1u)
            {
                int paletteOffset = (int)relative;
                _palette[paletteOffset] = (byte)(value >> 8);
                _palette[paletteOffset + 1] = (byte)value;
                PaletteWrites += 2;
                return true;
            }

            relative = address - 0x600000u;
            if (relative < (uint)_spriteRam.Length - 1u)
            {
                int spriteOffset = (int)relative;
                ushort before = ReadBigEndianWord(_spriteRam, spriteOffset & ~1);
                _spriteRam[spriteOffset] = (byte)(value >> 8);
                _spriteRam[spriteOffset + 1] = (byte)value;
                ushort after = ReadBigEndianWord(_spriteRam, spriteOffset & ~1);
                if (before == 0 && after != 0)
                    _spriteNonZeroWords++;
                else if (before != 0 && after == 0)
                    _spriteNonZeroWords--;
                if (value != 0)
                {
                    LastNonZeroSpriteWritePc = CurrentCpuPc;
                    LastNonZeroSpriteWriteAddress = address;
                    LastNonZeroSpriteWriteValue = (byte)value;
                }
                SpriteWrites += 2;
                return true;
            }

            relative = address - 0x610000u;
            if (relative < (uint)_playfieldRam.Length - 1u)
            {
                int pfOffset = (int)relative;
                int wordOffset = pfOffset >> 1;
                ushort before = ReadBigEndianWord(_playfieldRam, pfOffset & ~1);
                _playfieldRam[pfOffset] = (byte)(value >> 8);
                _playfieldRam[pfOffset + 1] = (byte)value;
                ushort after = ReadBigEndianWord(_playfieldRam, pfOffset & ~1);
                if (before == 0 && after != 0)
                    _playfieldNonZeroWords++;
                else if (before != 0 && after == 0)
                    _playfieldNonZeroWords--;
                UpdatePlayfieldRowUsage(wordOffset, before, after);
                if (value != 0)
                {
                    LastNonZeroPlayfieldWritePc = CurrentCpuPc;
                    LastNonZeroPlayfieldWriteAddress = address;
                    LastNonZeroPlayfieldWriteValue = (byte)value;
                }
                PlayfieldWrites += 2;
                return true;
            }

            relative = address - 0x61c000u;
            if (relative < (uint)_textRam.Length - 1u)
            {
                int textOffset = (int)relative;
                ushort before = ReadBigEndianWord(_textRam, textOffset & ~1);
                _textRam[textOffset] = (byte)(value >> 8);
                _textRam[textOffset + 1] = (byte)value;
                ushort after = ReadBigEndianWord(_textRam, textOffset & ~1);
                if (before == 0 && after != 0)
                    _textNonZeroWords++;
                else if (before != 0 && after == 0)
                    _textNonZeroWords--;
                if (value != 0)
                {
                    LastNonZeroTextWritePc = CurrentCpuPc;
                    LastNonZeroTextWriteAddress = address;
                    LastNonZeroTextWriteValue = (byte)value;
                }
                PlayfieldWrites += 2;
                return true;
            }

            relative = address - 0x61e000u;
            if (relative < (uint)_charRam.Length - 1u)
            {
                int charOffset = (int)relative;
                _charRam[charOffset] = (byte)(value >> 8);
                _charRam[charOffset + 1] = (byte)value;
                PlayfieldWrites += 2;
                return true;
            }

            relative = address - 0x620000u;
            if (relative < (uint)_lineRam.Length - 1u)
            {
                int lineOffset = (int)relative;
                _lineRam[lineOffset] = (byte)(value >> 8);
                _lineRam[lineOffset + 1] = (byte)value;
                PlayfieldWrites += 2;
                return true;
            }

            relative = address - 0x630000u;
            if (relative < (uint)_pivotRam.Length - 1u)
            {
                int pivotOffset = (int)relative;
                ushort before = ReadBigEndianWord(_pivotRam, pivotOffset & ~1);
                _pivotRam[pivotOffset] = (byte)(value >> 8);
                _pivotRam[pivotOffset + 1] = (byte)value;
                ushort after = ReadBigEndianWord(_pivotRam, pivotOffset & ~1);
                if (before == 0 && after != 0)
                    _pivotNonZeroWords++;
                else if (before != 0 && after == 0)
                    _pivotNonZeroWords--;
                PlayfieldWrites += 2;
                return true;
            }

            return false;
        }

        private bool ShouldReadNeutralFioSoftStatus(uint address)
        {
            _ = address;
            return false;
        }

        public void EnsureBackupDefaults()
        {
            const int backupRamOffset = 0x006c54;
            const int backupRomOffset = 0x1a1bd8;
            const int backupLength = 0x60;
            if (_rom.Length < backupRomOffset + backupLength || _workRam.Length < backupRamOffset + backupLength)
                return;

            _rom[backupRomOffset + 24] &= 0xfc;
            if (_workRam[backupRamOffset + 0] == (byte)'T'
                && _workRam[backupRamOffset + 1] == (byte)'A'
                && _workRam[backupRamOffset + 2] == (byte)'I'
                && _workRam[backupRamOffset + 3] == (byte)'T'
                && _workRam[backupRamOffset + 4] == (byte)'O')
            {
                if (_rom.Length >= 0x200000)
                    _workRam[backupRamOffset + 5] = _rom[0x1fffff];
                _workRam[backupRamOffset + 24] &= 0xfc;
                return;
            }

            Array.Copy(_rom, backupRomOffset, _workRam, backupRamOffset, backupLength);
            if (_rom.Length >= 0x200000)
                _workRam[backupRamOffset + 5] = _rom[0x1fffff];
            _workRam[backupRamOffset + 24] &= 0xfc;
        }

        private byte ReadControlByte(uint address)
        {
            uint offset = address - 0x4a0000;
            int port = (int)(offset / 4);
            int byteIndex = (int)(offset & 3);
            uint value = port switch
            {
                0 => ReadInputPort0(),
                1 => ReadInputPort1(),
                2 => 0xffff_0000u,
                3 => 0xffff_0000u,
                4 => 0xffff_ffffu,
                5 => ((uint)_coinWord1 << 16) | 0x0000_ffffu,
                _ => 0xffff_ffffu
            };

            return (byte)(value >> ((3 - byteIndex) * 8));
        }

        private uint ReadInputPort0()
        {
            byte eepromIn = 0xff;
            if (!_eeprom.DataOut)
                eepromIn &= unchecked((byte)~0x01);
            if (Input.Coin1)
                eepromIn &= unchecked((byte)~0x10);

            uint value = ((uint)eepromIn << 24) | ((uint)eepromIn << 16) | 0x0000_ffffu;
            if (Input.A) value &= ~0x0000_0001u;
            if (Input.B) value &= ~0x0000_0002u;
            if (Input.C) value &= ~0x0000_0004u;
            if (Input.X) value &= ~0x0000_0008u;
            if (Input.Start || (!UseNativeF3TrapScheduler && _startLatchFrames != 0)) value &= ~0x0000_1000u;
            if (Input.Z) value &= ~0x0000_2000u;
            return value;
        }

        private uint ReadInputPort1()
        {
            uint value = ((uint)_coinWord0 << 16) | 0x0000_ffffu;
            if (Input.Up) value &= ~0x0000_0001u;
            if (Input.Down) value &= ~0x0000_0002u;
            if (Input.Left) value &= ~0x0000_0004u;
            if (Input.Right) value &= ~0x0000_0008u;
            return value;
        }

        private void WriteControlByte(uint address, byte value)
        {
            uint offset = address - 0x4a0000;
            if (offset < 4)
            {
                ResetWatchdog();
                return;
            }

            switch (offset)
            {
                case 0x04:
                    _coinWord0 = (ushort)((_coinWord0 & 0x00ff) | (value << 8));
                    break;
                case 0x14:
                    _coinWord1 = (ushort)((_coinWord1 & 0x00ff) | (value << 8));
                    break;
                case 0x13:
                    _eepromOutLatch = value;
                    _eeprom.WriteLines(
                        dataIn: (value & 0x04) != 0,
                        clock: (value & 0x08) != 0,
                        chipSelect: (value & 0x10) != 0);
                    break;
            }
        }

        private bool TryWriteControlWord(uint address, ushort value)
        {
            switch (address)
            {
                case 0x4a0000:
                case 0x4a0002:
                    ResetWatchdog();
                    ControlWrites++;
                    return true;
                case 0x4a0004:
                    _coinWord0 = value;
                    ControlWrites++;
                    return true;
                case 0x4a0014:
                    _coinWord1 = value;
                    ControlWrites++;
                    return true;
                default:
                    return false;
            }
        }

        private bool TryWriteControlLong(uint address, uint value)
        {
            switch (address)
            {
                case 0x4a0000:
                    ResetWatchdog();
                    ControlWrites++;
                    return true;
                case 0x4a0004:
                    _coinWord0 = (ushort)(value >> 16);
                    ControlWrites++;
                    return true;
                case 0x4a0014:
                    _coinWord1 = (ushort)(value >> 16);
                    ControlWrites++;
                    return true;
                default:
                    return false;
            }
        }

        private void ResetWatchdog()
        {
            _watchdogCyclesRemaining = F3WatchdogTimeoutCycles;
            _watchdogSoftResetRequested = false;
            _watchdogKicks++;
        }

        private void TrackTaskFrameWrite(uint address)
        {
            uint pc = CurrentCpuPc & 0x00ff_ffff;
            if (pc == 0x00ff_ffff)
                return;

            uint canonicalAddress = F3WorkRamBase + ((address - F3WorkRamBase) % (uint)_workRam.Length);
            for (int i = 0; i < _observedTaskStackCount; i++)
            {
                uint stack = _observedTaskStacks[i];
                uint frame = stack + 60u;
                if (canonicalAddress < frame || canonicalAddress > frame + 7u)
                    continue;

                TrackTaskFrameWriteAt(canonicalAddress, pc, stack);
                return;
            }

            for (uint slot = 0; slot < 32; slot++)
            {
                uint stack = PeekLong(0x4066b8 + slot * 4u);
                if (stack < 0x00402000u || stack >= F3SchedulerStackSlots - 4u)
                    continue;

                uint frame = stack + 60u;
                if (canonicalAddress < frame || canonicalAddress > frame + 7u)
                    continue;

                TrackTaskFrameWriteAt(canonicalAddress, pc, stack);
                return;
            }
        }

        private void TrackTaskFrameWriteAt(uint address, uint pc, uint stack)
        {
            uint frame = stack + 60u;
            uint framePc = PeekLong(frame + 2u);
            LastTaskFrameWritePc = pc;
            LastTaskFrameWriteAddress = address;
            LastTaskFrameWriteStack = stack;
            LastTaskFrameWriteFramePc = framePc;
            if (framePc == uint.MaxValue && FirstBadTaskFrameWritePc == 0)
            {
                FirstBadTaskFrameWritePc = pc;
                FirstBadTaskFrameWriteAddress = address;
                FirstBadTaskFrameWriteStack = stack;
                FirstBadTaskFrameWriteFramePc = framePc;
            }
        }

        private void RecordObservedTaskStack(uint stack)
        {
            if (stack < 0x00402000u || stack >= F3SchedulerStackSlots - 4u)
                return;

            for (int i = 0; i < _observedTaskStackCount; i++)
            {
                if (_observedTaskStacks[i] == stack)
                    return;
            }

            if (_observedTaskStackCount < _observedTaskStacks.Length)
                _observedTaskStacks[_observedTaskStackCount++] = stack;
        }

        private void WriteTimerControlByte(uint address, byte value)
        {
            switch (address - 0x4c0000)
            {
                case 0:
                    _timerControl0 = (ushort)((value << 8) | (_timerControl0 & 0x00ff));
                    break;
                case 1:
                    _timerControl0 = (ushort)((_timerControl0 & 0xff00) | value);
                    break;
                case 2:
                    _timerControl1 = (ushort)((value << 8) | (_timerControl1 & 0x00ff));
                    break;
                case 3:
                    _timerControl1 = (ushort)((_timerControl1 & 0xff00) | value);
                    break;
            }
        }

        public bool TryClearWritableRamRange(uint address, uint byteCount)
        {
            address &= 0x00ff_ffff;
            if (byteCount == 0 || byteCount > int.MaxValue)
                return false;

            if (TryClearWindow(address, byteCount, F3WorkRamBase, F3WorkRamMirrorWindowSize, _workRam))
            {
                WorkRamWrites += (int)Math.Min(byteCount, int.MaxValue);
                return true;
            }

            if (TryClearWindow(address, byteCount, 0x440000, 0x8000, _palette))
            {
                PaletteWrites += (int)Math.Min(byteCount, int.MaxValue);
                return true;
            }

            if (TryClearWindow(address, byteCount, 0x600000, 0x10000, _spriteRam))
            {
                _spriteNonZeroWords = 0;
                SpriteWrites += (int)Math.Min(byteCount, int.MaxValue);
                return true;
            }

            if (TryClearWindow(address, byteCount, 0x610000, 0xc000, _playfieldRam))
            {
                _playfieldNonZeroWords = 0;
                RebuildPlayfieldRowUsage();
                PlayfieldWrites += (int)Math.Min(byteCount, int.MaxValue);
                return true;
            }

            if (TryClearWindow(address, byteCount, 0x61c000, 0x2000, _textRam))
            {
                _textNonZeroWords = 0;
                PlayfieldWrites += (int)Math.Min(byteCount, int.MaxValue);
                return true;
            }

            if (TryClearWindow(address, byteCount, 0x61e000, 0x2000, _charRam)
                || TryClearWindow(address, byteCount, 0x620000, 0x10000, _lineRam))
            {
                PlayfieldWrites += (int)Math.Min(byteCount, int.MaxValue);
                return true;
            }

            if (TryClearWindow(address, byteCount, 0x630000, 0x10000, _pivotRam))
            {
                _pivotNonZeroWords = 0;
                PlayfieldWrites += (int)Math.Min(byteCount, int.MaxValue);
                return true;
            }

            return false;
        }

        public bool TryFillWorkRamWordRange(uint address, uint wordCount, ushort value)
        {
            address &= 0x00ff_ffff;
            if (wordCount == 0 || wordCount > int.MaxValue / 2)
                return false;

            uint byteCount = wordCount * 2u;
            if (!TryFillWordWindow(address, byteCount, value, F3WorkRamBase, F3WorkRamMirrorWindowSize, _workRam))
                return false;

            WorkRamWrites += (int)Math.Min(byteCount, int.MaxValue);
            return true;
        }

        private static bool TryClearWindow(uint address, uint byteCount, uint baseAddress, int windowSize, byte[] storage)
        {
            if (!MapWindow(address, baseAddress, windowSize, storage, out int offset))
                return false;
            if ((ulong)address + byteCount > (ulong)baseAddress + (uint)windowSize)
                return false;
            if ((ulong)offset + byteCount > (ulong)storage.Length)
                return false;

            Array.Clear(storage, offset, (int)byteCount);
            return true;
        }

        private static bool TryFillWordWindow(uint address, uint byteCount, ushort value, uint baseAddress, int windowSize, byte[] storage)
        {
            if ((byteCount & 1) != 0)
                return false;
            if (!MapWindow(address, baseAddress, windowSize, storage, out int offset))
                return false;
            if ((ulong)address + byteCount > (ulong)baseAddress + (uint)windowSize)
                return false;
            if ((ulong)offset + byteCount > (ulong)storage.Length)
                return false;

            byte high = (byte)(value >> 8);
            byte low = (byte)value;
            int end = offset + (int)byteCount;
            for (int i = offset; i < end; i += 2)
            {
                storage[i] = high;
                storage[i + 1] = low;
            }

            return true;
        }

        private static bool MapWindow(uint address, uint baseAddress, int windowSize, byte[] storage, out int offset)
        {
            if (address >= baseAddress && address < baseAddress + windowSize)
            {
                offset = (int)((address - baseAddress) % (uint)storage.Length);
                return true;
            }

            offset = 0;
            return false;
        }

        private static ushort ReadBigEndianWord(byte[] data, int offset)
        {
            if ((uint)(offset + 1) >= (uint)data.Length)
                return 0;

            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        private static int CountNonZeroWords(byte[] data)
        {
            int count = 0;
            for (int offset = 0; offset + 1 < data.Length; offset += 2)
            {
                if (ReadBigEndianWord(data, offset) != 0)
                    count++;
            }

            return count;
        }

        private void RebuildPlayfieldRowUsage()
        {
            Array.Clear(_tilemapRowUsage);
            int words = Math.Min(_playfieldRam.Length / 2, 0x4000);
            for (int wordOffset = 1; wordOffset < words; wordOffset += 2)
            {
                ushort tile = ReadBigEndianWord(_playfieldRam, wordOffset * 2);
                if (tile == 0)
                    continue;

                int row = (wordOffset >> 6) & 0x1f;
                int tilemap = wordOffset >> 11;
                if ((uint)tilemap < 8)
                    _tilemapRowUsage[row, tilemap]++;
            }
        }

        private void UpdatePlayfieldRowUsage(int wordOffset, ushort before, ushort after)
        {
            if ((uint)wordOffset >= 0x4000 || (wordOffset & 1) == 0)
                return;
            if (before == after)
                return;

            int row = (wordOffset >> 6) & 0x1f;
            int tilemap = wordOffset >> 11;
            if ((uint)tilemap >= 8)
                return;

            if (before == 0 && after != 0)
                _tilemapRowUsage[row, tilemap]++;
            else if (before != 0 && after == 0 && _tilemapRowUsage[row, tilemap] > 0)
                _tilemapRowUsage[row, tilemap]--;
        }

        private static int FindFirstNonZeroWord(byte[] data)
        {
            for (int offset = 0; offset + 1 < data.Length; offset += 2)
            {
                if (ReadBigEndianWord(data, offset) != 0)
                    return offset >> 1;
            }

            return -1;
        }

        private static void ReadExact(BinaryReader reader, byte[] dest)
        {
            byte[] data = reader.ReadBytes(dest.Length);
            if (data.Length != dest.Length)
                throw new EndOfStreamException();
            Buffer.BlockCopy(data, 0, dest, 0, dest.Length);
        }
    }
}
