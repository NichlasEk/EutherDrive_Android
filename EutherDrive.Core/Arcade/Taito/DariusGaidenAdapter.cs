namespace EutherDrive.Core.Arcade.Taito;

using System.Globalization;
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
    private const int MainInstructionLimitPerFrame = 30_000;

    private static readonly bool Trace = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_TRACE") == "1";
    private static readonly bool UseNativeF3TrapScheduler = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_NATIVE_TRAPS") == "1";
    private static readonly bool TraceBootPc = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_TRACE_BOOT_PC") == "1";
    private static readonly int TraceInstructionLimit = ParseEnvInt("EUTHERDRIVE_DARIUSG_TRACE_INSTRUCTIONS", 64);
    private static readonly int TraceBootPcLimit = ParseEnvInt("EUTHERDRIVE_DARIUSG_TRACE_BOOT_PC_LIMIT", 256);
    private static readonly int CpuScale = Math.Clamp(ParseEnvInt("EUTHERDRIVE_DARIUSG_CPU_SCALE", 1), 1, 32);

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
    private readonly F3LineState[] _lineStates = new F3LineState[256];
    private readonly MameMusashi68Ec020 _mainCpu = new();
    private readonly TaitoF3MainBus _bus = new();
    private RomIdentity? _romIdentity;
    private TaitoF3RomSet? _roms;
    private string _driverName = "dariusg";
    private long _frameCounter;
    private bool _loaded;
    private bool _cpuFaulted;
    private string _lastStopReason = "idle";
    private uint _lastRecoveredInvalidPc;
    private uint _lastPcBeforeRecoveredInvalidPc;
    private ushort _lastOpBeforeRecoveredInvalidPc;
    private bool _dariusObjectMapSeeded;
    private ulong _dariusObjectMapSeedHits;
    private ulong _executedInstructions;
    private ulong _executedCycles;
    private ulong _m68ec020ProbeInstructions;
    private int _traceInstructionsRemaining;
    private readonly Queue<F3TaskState> _f3TaskQueue = new();
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
    private int _masterVolumePercent = 100;
    private bool _spriteBank;
    private bool _spriteTrails;
    private bool _flipScreen;
    private int _spritePenMask = 0x0f;
    private readonly List<F3Sprite> _sprites = new(0x400);
    private int _lastSpriteCandidates;
    private int _lastVisibleSprites;
    private int _lastSpritePixels;
    private int _lastPlayfieldCandidates;
    private int _lastPlayfieldPixels;
    private int _lastMixSourcePixels;
    private int _lastMixLitSourcePixels;
    private int _lastMixDestOnlyPixels;
    private int _lastMixPriorityZeroConflicts;
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

    public string DebugSummary =>
        BuildDebugSummary();

    private string BuildDebugSummary()
    {
        var state = _mainCpu.GetState();
        return $"driver={_driverName} frame={_frameCounter} pc=0x{_mainCpu.Pc:X6} sr=0x{_mainCpu.StatusRegister:X4} " +
        $"op=0x{_mainCpu.NextOpcode:X4} d0=0x{state.Data[0]:X8} d1=0x{state.Data[1]:X8} a0=0x{state.Address[0]:X8} a1=0x{state.Address[1]:X8} a5=0x{state.Address[5]:X8} a6=0x{state.Address[6]:X8} " +
        $"cycles={_executedCycles} instr={_executedInstructions} mame020={_mainCpu.ImplementedOpcodeCount}/{_mainCpu.MameEc020OpcodeCount} " +
        $"020probe={_m68ec020ProbeInstructions} tasks={_f3TaskQueue.Count} q={BuildTaskQueueSample()} taskEnq={_f3TasksEnqueued} taskRun={_f3TasksDispatched} " +
        $"lastTask=0x{_lastF3TaskEntry:X6} enq={BuildRecentTaskSample(_recentF3EnqueuedTasks, _recentF3EnqueuedIndex)} run={BuildRecentTaskSample(_recentF3DispatchedTasks, _recentF3DispatchedIndex)} lastTrap=0x{_lastF3TrapPc:X6} vbr=0x{_bus.VectorBase:X6} " +
        $"ramW={_bus.WorkRamWrites} palW={_bus.PaletteWrites} sprW={_bus.SpriteWrites} pfW={_bus.PlayfieldWrites} pfNZ={_bus.PlayfieldNonZeroWords} pfCand={_lastPlayfieldCandidates} pfPix={_lastPlayfieldPixels} pfL={BuildPlayfieldLayerSample()} mixSrc={_lastMixSourcePixels}/{_lastMixLitSourcePixels} mixDstOnly={_lastMixDestOnlyPixels} mixP0={_lastMixPriorityZeroConflicts} lineMid={BuildLineStateSample()} txtNZ={_bus.TextNonZeroWords} pivNZ={_bus.PivotNonZeroWords} " +
        $"lastSprNZ=0x{_bus.LastNonZeroSpriteWritePc:X6}->0x{_bus.LastNonZeroSpriteWriteAddress:X6}:0x{_bus.LastNonZeroSpriteWriteValue:X2} lastPfNZ=0x{_bus.LastNonZeroPlayfieldWritePc:X6}->0x{_bus.LastNonZeroPlayfieldWriteAddress:X6}:0x{_bus.LastNonZeroPlayfieldWriteValue:X2} lastTxtNZ=0x{_bus.LastNonZeroTextWritePc:X6}->0x{_bus.LastNonZeroTextWriteAddress:X6}:0x{_bus.LastNonZeroTextWriteValue:X2} " +
        $"mode=0x{_bus.PeekByte(0x40221d):X2}/0x{_bus.PeekByte(0x40223a):X2}/0x{_bus.PeekByte(0x40223d):X2}/0x{_bus.PeekByte(0x40223f):X2} coin=0x{_bus.CoinWord0:X4}/0x{_bus.CoinWord1:X4}/in{(_bus.Input.Coin1 ? 1 : 0)} fio22={BuildFioSoftSample()} coinT={_bus.PeekWord(0x400090):X4},{_bus.PeekWord(0x400092):X4},{_bus.PeekWord(0x4000a2):X4},{_bus.PeekWord(0x4000a4):X4} bkup18=0x{_bus.PeekByte(0x406c6c):X2} cfg2_18=0x{_bus.PeekByte(0x406c8c):X2} gateEbb4=0x{_bus.PeekByte(0x406bb4):X2} gateEbb5=0x{_bus.PeekByte(0x406bb5):X2} gateEbb6=0x{_bus.PeekByte(0x406bb6):X2} gateW=0x{_bus.LastGateWritePc:X6}->0x{_bus.LastGateWriteAddress:X6}:0x{_bus.LastGateWriteValue:X2} gateNZ=0x{_bus.LastNonZeroGateWritePc:X6}->0x{_bus.LastNonZeroGateWriteAddress:X6}:0x{_bus.LastNonZeroGateWriteValue:X2}/{_bus.NonZeroGateWrites} scene=entry:{_sceneEntryHits}/init:{_sceneInitResumeHits},{_sceneInitMainHits},{_sceneMenuInitHits},{_sceneSpawnerYieldHits}/gate:{_sceneGateRoutineHits}/bset:{_sceneGateSetInstructionHits}/wait:{_sceneGateWaitHits}/mainwait:{_mainGateWaitHits}/call=0x{_lastSceneAbsoluteCallTarget:X6}/cont:{_sceneContinuationEnqueued},{_sceneContinuationDispatched},{_sceneContinuationRemoved}/rm=0x{_lastF3TaskRemoveMask:X8} flag224=0x{_bus.PeekByte(0x402224):X2} irqPtr=0x{_bus.PeekLong(0x406704):X8}/W0x{_bus.LastIrqWorkPointerWritePc:X6}->0x{_bus.LastIrqWorkPointerWriteAddress:X6}:0x{_bus.LastIrqWorkPointerValue:X8} objMap={_bus.PeekLong(0x410000):X8},{_bus.PeekLong(0x410004):X8},{_bus.PeekLong(0x410030):X8}/seed:{(_dariusObjectMapSeeded ? 1 : 0)},{_dariusObjectMapSeedHits} obj916=0x{_bus.PeekByte(0x408916):X2} obj917=0x{_bus.PeekByte(0x408917):X2} listCnt=0x{_bus.PeekWord(0x402218):X4} listPtr=0x{_bus.PeekLong(0x407360):X6} listPtrW=0x{_bus.LastSpriteListPointerWritePc:X6}->0x{_bus.LastSpriteListPointerWriteAddress:X6}:0x{_bus.LastSpriteListPointerValue:X8} listFlow={_spriteListBuildHits},{_spriteListProducerWrites},{_spriteListFinalizeHits},{_spriteListLatchedWrites} sprNZ={_bus.SpriteNonZeroWords} sprFirst={_bus.FirstNonZeroSpriteWordOffset:X4} sprRaw={BuildSpriteRamSample()} sprHead={BuildSpritePointerSample()} sprTiles={BuildSpriteTileSample()} " +
        $"sprCand={_lastSpriteCandidates} sprVis={_lastVisibleSprites} sprPix={_lastSpritePixels} sprCtl=0x{_lastSpriteControlWord:X4} sprBank={(_spriteBank ? 1 : 0)} sprLast={_lastSpriteCandidateEntry:X3}/0x{_lastSpriteCandidateTile:X5}/0x{_lastSpriteCandidateControl:X2}@{_lastSpriteCandidateX},{_lastSpriteCandidateY}+{_lastSpriteCandidateScaleX},{_lastSpriteCandidateScaleY} sprBox={_lastSpriteMinX},{_lastSpriteMinY}..{_lastSpriteMaxX},{_lastSpriteMaxY}/{_lastSpriteClosestDistance} " +
        $"ctrlR={_bus.ControlReads} lastCtrl=0x{_bus.LastControlReadAddress:X6}:0x{_bus.LastControlReadValue:X2} modeW=0x{_bus.LastModeWritePc:X6}->0x{_bus.LastModeWriteAddress:X6}:0x{_bus.LastModeWriteValue:X2} modeBtst=0x{_bus.LastModeBtstPc:X6}@0x{_bus.LastModeBtstAddress:X6}:0x{_bus.LastModeBtstValue:X2}/b{_bus.LastModeBtstBit}/z{(_bus.LastModeBtstZero ? 1 : 0)} btst=0x{_bus.LastBtstAddress:X6}:0x{_bus.LastBtstValue:X2}/b{_bus.LastBtstBit} bkupW=0x{_bus.LastBackupWritePc:X6}:0x{_bus.LastBackupWriteValue:X2} ctrlW={_bus.ControlWrites} dpramR={_bus.DualPortReads}@0x{_bus.LastDualPortReadPc:X6}->0x{_bus.LastDualPortReadAddress:X6}:0x{_bus.LastDualPortReadValue:X2} dpramW={_bus.DualPortWrites}@0x{_bus.LastDualPortWritePc:X6}->0x{_bus.LastDualPortWriteAddress:X6}:0x{_bus.LastDualPortWriteValue:X2} sndRst={(_bus.SoundCpuResetAsserted ? 1 : 0)} sndRstW=0x{_bus.LastSoundResetWritePc:X6}->0x{_bus.LastSoundResetWriteAddress:X6} unmappedR={_bus.UnmappedReads}@0x{_bus.LastUnmappedReadPc:X6}->0x{_bus.LastUnmappedReadAddress:X6} unmappedW={_bus.UnmappedWrites}@0x{_bus.LastUnmappedWritePc:X6}->0x{_bus.LastUnmappedWriteAddress:X6}:0x{_bus.LastUnmappedWriteValue:X2} recover=0x{_lastRecoveredInvalidPc:X6}<-0x{_lastPcBeforeRecoveredInvalidPc:X6}/0x{_lastOpBeforeRecoveredInvalidPc:X4} stop={_lastStopReason}";
    }

    private string BuildSpritePointerSample()
    {
        uint pointer = _bus.PeekLong(0x407360) & 0x00ff_ffff;
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
        "missing: real M68EC020 core, full F3 trap scheduler, TC0630FDP priorities/blending, full F3 sprite generator, persistent EEPROM/NVRAM, watchdog, ES5505/ES5510 sound.";

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
        _mainCpu.Reset(_bus);
        _loaded = true;
        _cpuFaulted = false;
        _hasPresentFrame = false;
        _frameCounter = 0;
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
        _dariusObjectMapSeeded = false;
        _dariusObjectMapSeedHits = 0;
        _currentF3TaskPriority = 0;
        ResetSceneDiagnostics();
        _nextF3TaskStack = 0x0041_f000;
        _traceInstructionsRemaining = TraceInstructionLimit;
        _traceBootPcRemaining = TraceBootPcLimit;
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
        _dariusObjectMapSeeded = false;
        _dariusObjectMapSeedHits = 0;
        _currentF3TaskPriority = 0;
        ResetSceneDiagnostics();
        _nextF3TaskStack = 0x0041_f000;
        _lastStopReason = "reset";
        _traceBootPcRemaining = TraceBootPcLimit;
        _traceInstructionsRemaining = TraceInstructionLimit;
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
        Array.Clear(_spriteReefPalette);
        Array.Clear(_spriteReefGroup);
    }

    public void RunFrame()
    {
        if (!_loaded)
            return;

        _frameCounter++;
        _bus.BeginFrameInterrupt();
        if (_f3TasksEnqueued == 0)
            _bus.PulseBootSchedulerGate();
        if (_f3TasksEnqueued != 0)
            _bus.RefreshInputLatches();
        if (_cpuFaulted)
        {
            DrawBringupFrame();
            return;
        }

        int cycles = 0;
        int instructions = 0;
        try
        {
            int cycleBudget = (int)(MainClockHz / TargetFps) * CpuScale;
            int instructionBudget = MainInstructionLimitPerFrame * CpuScale;
            while (cycles < cycleBudget && instructions < instructionBudget)
            {
                uint pc = _mainCpu.Pc;
                ushort op = _mainCpu.NextOpcode;
                _bus.CurrentCpuPc = pc;
                TrackSceneDiagnosticPc(pc);
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
                        $"d0=0x{state.Data[0]:X8} d1=0x{state.Data[1]:X8} a2=0x{state.Address[2]:X8} a3=0x{state.Address[3]:X8} " +
                        $"sp=0x{sp:X8} st0=0x{_bus.ReadLong(sp):X8} st4=0x{_bus.ReadLong(sp + 4):X8} " +
                        $"tasks={_f3TaskQueue.Count} enq={_f3TasksEnqueued} run={_f3TasksDispatched}");
                }
                if (Trace && _traceInstructionsRemaining > 0)
                {
                    _traceInstructionsRemaining--;
                    Console.WriteLine($"[DARIUSG-020] f={_frameCounter} i={instructions} pc=0x{pc:X6} op=0x{op:X4} sr=0x{_mainCpu.StatusRegister:X4}");
                }

                uint used = TryExecuteM68ec020ProbeInstruction(pc, op, out uint probeCycles)
                    ? probeCycles
                    : _mainCpu.ExecuteInstruction(_bus);
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

        if (Trace || _frameCounter <= 3 || (_frameCounter % 60) == 0)
            Console.WriteLine($"[DARIUSG] {DebugSummary}");

        if (_f3TasksEnqueued != 0)
            _bus.RefreshInputLatches();
        RenderUglyVideo();
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = FrameWidth;
        height = FrameHeight;
        stride = FrameStride;
        return _hasPresentFrame ? _presentFrameBuffer : _frameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = 44_100;
        channels = 2;
        return ReadOnlySpan<short>.Empty;
    }

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
        writer.Write(10);
        writer.Write(_frameCounter);
        writer.Write(_executedInstructions);
        writer.Write(_executedCycles);
        writer.Write(_m68ec020ProbeInstructions);
        writer.Write(_cpuFaulted);
        writer.Write(_lastStopReason);
        _bus.SaveState(writer);
        var state = _mainCpu.GetState();
        writer.Write(state.Pc);
        writer.Write(state.Ssp);
        writer.Write(state.Usp);
        writer.Write(state.Sr);
        writer.Write(state.Prefetch);
        for (int i = 0; i < 8; i++) writer.Write(state.Data[i]);
        for (int i = 0; i < 7; i++) writer.Write(state.Address[i]);
    }

    public void LoadState(BinaryReader reader)
    {
        if (reader.ReadString() != "DARIUSG")
            throw new InvalidDataException("Not a Darius Gaiden bringup savestate.");
        int version = reader.ReadInt32();
        if (version < 3 || version > 10)
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

        ClearWithPalette(0);
        BuildMameLineStates();

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
        if (TryBypassCoinErrorStatusGate(pc, op, out cycles))
            return true;
        if (TryBypassWaitAMomentGate(pc, op, out cycles))
            return true;
        if (TryBypassBackupRamInitReset(pc, op, out cycles))
            return true;
        // Sprite RAM command/list layout is sensitive; keep these writes on the
        // ROM's real 68k path so the MAME-style sprite walker sees matching data.
        if (TryExecuteDariusWorkRamCurrent(pc, op, out cycles))
            return true;
        if (TryExecuteDariusBootRamClear(pc, op, out cycles))
            return true;
        if (TryExecuteDariusF3MemoryTide(pc, op, out cycles))
            return true;
        if (TryExecuteDariusPaletteCurrent(pc, op, out cycles))
            return true;
        if (TryExecuteBtstImmediateByteDisplacement(pc, op, out cycles))
            return true;
        if (TryExecuteF3SchedulerYieldEntry(pc, op, out cycles))
            return true;
        if (TryExecuteF3TrapSchedulerStub(pc, op, out cycles))
            return true;
        if (TryDispatchF3QueuedTask(pc, op, out cycles))
            return true;

        // 68020 MULL.L. Darius Gaiden uses this during F3 boot math before
        // video RAM is fully populated; keep it local until a real 020 core exists.
        if ((op & 0xffc0) == 0x4c00)
            return TryExecuteMullLong(pc, op, out cycles);

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
        if ((op & 0xfff8) == 0x49c0)
            return TryExecuteExtByteToLong(pc, op, out cycles);
        if ((op & 0xf038) == 0x5028 && ((op >> 6) & 3) != 3)
            return TryExecuteAddSubQuickDisplacement(pc, op, out cycles);
        if ((op & 0xf1f8) == 0xd070)
            return TryExecuteAddWordIndexedToData(pc, op, out cycles);
        if (op == 0x4e7a || op == 0x4e7b)
            return TryExecuteMovec(pc, op, out cycles);

        return false;
    }

    private bool TryExecuteMullLong(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        ushort extension = _bus.ReadOpcodeWord(pc + 2);
        var state = _mainCpu.GetState();
        if (!TryReadMullSource(pc, op, state, out uint source, out int eaBytes, out uint usp, out uint ssp))
            return false;

        bool signed = (extension & 0x0800) != 0;
        bool longResult = (extension & 0x0400) != 0;
        int lowRegister = (extension >> 12) & 7;
        int highRegister = extension & 7;

        ulong result;
        bool overflow;
        if (signed)
        {
            long signedResult = (long)(int)source * (long)(int)state.Data[lowRegister];
            result = unchecked((ulong)signedResult);
            overflow = signedResult != (int)signedResult;
        }
        else
        {
            result = (ulong)source * state.Data[lowRegister];
            overflow = result > uint.MaxValue;
        }

        bool negative;
        bool zero;
        if (longResult)
        {
            state.Data[highRegister] = (uint)(result >> 32);
            state.Data[lowRegister] = (uint)result;
            negative = (result & 0x8000_0000_0000_0000ul) != 0;
            zero = result == 0;
            overflow = false;
        }
        else
        {
            state.Data[lowRegister] = (uint)result;
            negative = ((uint)result & 0x8000_0000u) != 0;
            zero = (uint)result == 0;
        }

        ushort sr = UpdateCcr(state.Sr, negative, zero, overflow, carry: false);
        uint nextPc = (pc + 4 + (uint)eaBytes) & 0x00ff_ffff;
        ushort prefetch = _bus.ReadOpcodeWord(nextPc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, usp, ssp, sr, nextPc, prefetch));
        _m68ec020ProbeInstructions++;
        cycles = (uint)(longResult ? 47 : 43);
        return true;
    }

    private bool TryReadMullSource(uint pc, ushort op, M68000.M68000State state, out uint source, out int eaBytes, out uint usp, out uint ssp)
    {
        source = 0;
        eaBytes = 0;
        usp = state.Usp;
        ssp = state.Ssp;
        int mode = (op >> 3) & 7;
        int reg = op & 7;
        uint eaExtension = (pc + 4) & 0x00ff_ffff;
        switch (mode)
        {
            case 0:
                source = state.Data[reg];
                return true;
            case 2:
                source = _bus.ReadLong(state.Address[reg]);
                return true;
            case 3:
                if (reg == 7)
                {
                    uint sp = (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp;
                    source = _bus.ReadLong(sp);
                    if ((state.Sr & 0x2000) != 0)
                        ssp = (sp + 4) & 0x00ff_ffff;
                    else
                        usp = (sp + 4) & 0x00ff_ffff;
                }
                else
                {
                    source = _bus.ReadLong(state.Address[reg]);
                    state.Address[reg] = (state.Address[reg] + 4) & 0x00ff_ffff;
                }
                return true;
            case 4:
            {
                uint address;
                if (reg == 7)
                {
                    uint sp = (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp;
                    address = (sp - 4) & 0x00ff_ffff;
                    if ((state.Sr & 0x2000) != 0)
                        ssp = address;
                    else
                        usp = address;
                }
                else
                {
                    address = (state.Address[reg] - 4) & 0x00ff_ffff;
                    state.Address[reg] = address;
                }
                source = _bus.ReadLong(address);
                return true;
            }
            case 5:
            {
                short displacement = unchecked((short)_bus.ReadOpcodeWord(eaExtension));
                uint baseAddress = reg == 7
                    ? (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp
                    : state.Address[reg];
                source = _bus.ReadLong(unchecked(baseAddress + (uint)displacement));
                eaBytes = 2;
                return true;
            }
            case 6:
            {
                ushort indexExtension = _bus.ReadOpcodeWord(eaExtension);
                uint baseAddress = reg == 7
                    ? (state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp
                    : state.Address[reg];
                source = _bus.ReadLong(CalculateBriefIndexedAddress(state, baseAddress, indexExtension));
                eaBytes = 2;
                return true;
            }
            case 7:
                switch (reg)
                {
                    case 0:
                    {
                        short absolute = unchecked((short)_bus.ReadOpcodeWord(eaExtension));
                        source = _bus.ReadLong((uint)absolute & 0x00ff_ffff);
                        eaBytes = 2;
                        return true;
                    }
                    case 1:
                        source = _bus.ReadLong(_bus.ReadLong(eaExtension) & 0x00ff_ffff);
                        eaBytes = 4;
                        return true;
                    case 2:
                    {
                        short displacement = unchecked((short)_bus.ReadOpcodeWord(eaExtension));
                        source = _bus.ReadLong(unchecked(eaExtension + (uint)displacement));
                        eaBytes = 2;
                        return true;
                    }
                    case 3:
                    {
                        ushort indexExtension = _bus.ReadOpcodeWord(eaExtension);
                        source = _bus.ReadLong(CalculateBriefIndexedAddress(state, eaExtension, indexExtension));
                        eaBytes = 2;
                        return true;
                    }
                    case 4:
                        source = _bus.ReadLong(eaExtension);
                        eaBytes = 4;
                        return true;
                }
                break;
        }

        return false;
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
            RemoveF3TasksByMask(mask);
            uint currentBit = _currentF3TaskPriority < 32 ? 1u << _currentF3TaskPriority : 0;
            if ((mask & currentBit) != 0)
            {
                nextPc = 0x002326;
                ushort idlePrefetch = _bus.ReadOpcodeWord(nextPc);
                _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, usp, ssp, state.Sr, nextPc, idlePrefetch));
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
            _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, usp, ssp, state.Sr, nextPc, idlePrefetch));
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
                    EnqueueF3Task(
                        CreateContinuationF3TaskState(state, continuation, delayFrames, _currentF3TaskPriority),
                        preferFront: ridesFastCurrent);
                    if (ridesFastCurrent)
                        _sceneContinuationEnqueued++;
                    _f3TasksEnqueued++;
                    _lastF3TaskEntry = continuation;
                    RecordRecentTask(_recentF3EnqueuedTasks, ref _recentF3EnqueuedIndex, continuation);
                }

                nextPc = 0x002326;
                ushort idlePrefetch = _bus.ReadOpcodeWord(nextPc);
                _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, usp, ssp, state.Sr, nextPc, idlePrefetch));
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
        for (int i = 0; i < remaining; i++)
        {
            _bus.WriteWord(address, value);
            address = (address + 2) & 0x00ff_ffff;
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

        for (uint i = 0; i < remaining; i++)
        {
            _bus.WriteLong(address, 0);
            address = (address + 4) & 0x00ff_ffff;
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

        if (op == 0x20c1 && _bus.PeekWord(pc + 2) == 0x51c8)
            return TryExecuteDariusDbraLongFill(pc, out cycles);

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

        uint current = address;
        uint elements = byteCount / (uint)unitSize;
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
        for (int i = 0; i < remaining; i++)
        {
            _bus.WriteLong(address, value);
            address = (address + 4) & 0x00ff_ffff;
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

    private bool TryExecuteDariusDbraWordFill(uint pc, out uint cycles)
    {
        cycles = 0;
        var state = _mainCpu.GetState();
        int remaining = ((ushort)state.Data[0]) + 1;
        uint address = state.Address[0] & 0x00ff_ffff;
        if (remaining <= 0 || remaining > 0x1000 || !IsF3VideoAddress(address))
            return false;

        ushort value = (ushort)state.Data[1];
        for (int i = 0; i < remaining; i++)
        {
            _bus.WriteWord(address, value);
            address = (address + 2) & 0x00ff_ffff;
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
        for (int i = 0; i < remaining; i++)
        {
            _bus.WriteWord(address, value);
            address = (address + 2) & 0x00ff_ffff;
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
                state.Sr,
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
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, idlePc, prefetch));
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
        var state = _mainCpu.GetState();
        if (_f3TaskQueue.Count > 0 && TryDispatchNextF3Task(state, out cycles))
            return true;

        uint idlePc = 0x002312;
        ushort prefetch = _bus.ReadOpcodeWord(idlePc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, idlePc, prefetch));
        cycles = (uint)((int)(MainClockHz / TargetFps) * CpuScale);
        return true;
    }

    private bool TryDispatchF3QueuedTask(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if ((pc != 0x01014c && pc != 0x010170 && pc != 0x002326) || _f3TaskQueue.Count == 0)
            return false;

        var state = _mainCpu.GetState();
        return TryDispatchNextF3Task(state, out cycles);
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
            if (task.Pc == incoming.Pc && task.Priority == incoming.Priority)
                continue;

            _f3TaskQueue.Enqueue(task);
        }
    }

    private static bool IsF3SceneFastContinuation(uint pc)
    {
        if (pc >= 0x004148 && pc < 0x004152)
            return true;

        if (pc < 0x0042a0 || pc >= 0x0045d8)
            return false;

        return true;
    }

    private static void RecordRecentTask(uint[] ring, ref int nextIndex, uint pc)
    {
        ring[nextIndex & (ring.Length - 1)] = pc;
        nextIndex++;
    }

    private void RemoveF3TasksByMask(uint mask)
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
        ushort sr = source.Sr;
        uint usp = (sr & 0x2000) != 0 ? source.Usp : stack;
        uint ssp = (sr & 0x2000) != 0 ? stack : source.Ssp;
        return new F3TaskState(pc, new M68000.M68000State(data, address, usp, ssp, sr, pc, prefetch), priority);
    }

    private F3TaskState CreateContinuationF3TaskState(M68000.M68000State source, uint pc, int delayFrames = 0, int priority = 0)
    {
        ushort prefetch = _bus.ReadOpcodeWord(pc);
        var state = new M68000.M68000State(CloneRegisters(source.Data), CloneRegisters(source.Address), source.Usp, source.Ssp, source.Sr, pc, prefetch);
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
        state.Data[0] = _bus.HasInsertedCredit ? 0u : 0xffff_ffffu;
        uint nextPc = 0x0102ee;
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
        uint address = unchecked(state.Address[addressRegister] + (uint)displacement) & 0x00ff_ffff;
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
        uint ea = unchecked(state.Address[addressRegister] + (uint)displacement);

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
        uint ea = unchecked(state.Address[addressRegister] + (uint)displacement);
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
        EnsureDariusObjectAllocationMap(pc);
        ushort extension = _bus.ReadOpcodeWord(pc + 2);
        var state = _mainCpu.GetState();
        if (!TryReadBitfieldOperand(pc, op, extension, state, out uint aligned, out uint extracted, out int offset, out int width, out _, out int instructionLength))
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

    private void EnsureDariusObjectAllocationMap(uint pc)
    {
        if (_dariusObjectMapSeeded || pc != 0x0105ac)
            return;

        if (_bus.PeekLong(0x410000) != 0
            || _bus.PeekLong(0x410004) != 0
            || _bus.PeekLong(0x410030) != 0
            || _bus.PeekLong(0x410034) != 0)
        {
            _dariusObjectMapSeeded = true;
            return;
        }

        for (uint i = 0; i < 12; i++)
        {
            _bus.WriteLong(0x410000 + i * 4, 0xffff_ffff);
            _bus.WriteLong(0x410030 + i * 4, 0xffff_ffff);
        }

        _dariusObjectMapSeeded = true;
        _dariusObjectMapSeedHits++;
    }

    private bool TryExecuteBfclr(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        ushort extension = _bus.ReadOpcodeWord(pc + 2);
        var state = _mainCpu.GetState();
        if (!TryReadBitfieldOperand(pc, op, extension, state, out uint aligned, out uint extracted, out int offset, out int width, out uint ea, out int instructionLength))
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
            uint dataLong = offset + width < 8
                ? (uint)_bus.ReadByte(ea) << 24
                : offset + width < 16
                    ? (uint)_bus.ReadWord(ea) << 16
                    : _bus.ReadLong(ea);
            uint clearedLong = dataLong & ~maskLong;

            if (offset + width < 8)
                _bus.WriteByte(ea, (byte)(clearedLong >> 24));
            else if (offset + width < 16)
                _bus.WriteWord(ea, (ushort)(clearedLong >> 16));
            else
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
        if (!TryReadBitfieldOperand(pc, op, extension, state, out uint aligned, out uint extracted, out int offset, out int width, out uint ea, out int instructionLength))
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
            uint dataLong = offset + width < 8
                ? (uint)_bus.ReadByte(ea) << 24
                : offset + width < 16
                    ? (uint)_bus.ReadWord(ea) << 16
                    : _bus.ReadLong(ea);
            uint setLong = dataLong | maskLong;

            if (offset + width < 8)
                _bus.WriteByte(ea, (byte)(setLong >> 24));
            else if (offset + width < 16)
                _bus.WriteWord(ea, (ushort)(setLong >> 16));
            else
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
                ea = unchecked(state.Address[reg] + (uint)(short)_bus.ReadOpcodeWord(pc + 4));
                instructionLength = 6;
                return true;
            case 6:
                ea = CalculateBriefIndexedAddress(state, state.Address[reg], _bus.ReadOpcodeWord(pc + 4));
                instructionLength = 6;
                return true;
            case 7 when reg == 0:
                ea = unchecked((uint)(short)_bus.ReadOpcodeWord(pc + 4));
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
        uint dataLong = offset + width < 8
            ? (uint)_bus.ReadByte(ea) << 24
            : offset + width < 16
                ? (uint)_bus.ReadWord(ea) << 16
                : _bus.ReadLong(ea);
        uint mergedLong = (dataLong & ~maskLong) | insertLong;

        if (offset + width < 8)
            _bus.WriteByte(ea, (byte)(mergedLong >> 24));
        else if (offset + width < 16)
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
        uint nextPc = (pc + 6) & 0x00ff_ffff;
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
        uint address = unchecked(state.Address[addressRegister] + (uint)displacement) & 0x00ff_ffff;
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
        Array.Clear(_lastPlayfieldLayerCandidates);
        Array.Clear(_lastPlayfieldLayerPixels);
        Array.Clear(_lastPlayfieldBlendSelect0);
        Array.Clear(_lastPlayfieldBlendSelect1);
        Span<int> regSx = stackalloc int[4];
        Span<int> regFxY = stackalloc int[4];
        for (int layer = 0; layer < 4; layer++)
            GetMamePlayfieldScroll(layer, out regSx[layer], out regFxY[layer]);

        bool drewAny = false;
        for (int screenY = 0; screenY < FrameHeight; screenY++)
        {
            int screenLine = screenY + VisibleAreaMinY;
            int[] layerOrder = BuildMamePlayfieldOrder(screenLine);
            for (int i = 0; i < layerOrder.Length; i++)
                drewAny |= RenderPlayfieldLine(roms, layerOrder[i], screenY, regSx[layerOrder[i]], regFxY[layerOrder[i]]);

            if (screenY != 0)
            {
                F3LineState line = _lineStates[screenLine & 0xff];
                for (int layer = 0; layer < 4; layer++)
                    regFxY[layer] += line.PlayfieldYScale[layer];
            }
        }

        return drewAny;
    }

    private int[] BuildMamePlayfieldOrder(int screenLine)
    {
        int[] order = [0, 3, 2, 1];
        Array.Sort(order, (a, b) =>
        {
            int pa = TryReadMixableCurrentState(screenLine, 7, a, spriteRules: false, out int priorityA, out _, out _) ? priorityA : -1;
            int pb = TryReadMixableCurrentState(screenLine, 7, b, spriteRules: false, out int priorityB, out _, out _) ? priorityB : -1;
            int priorityCompare = pb.CompareTo(pa);
            if (priorityCompare != 0)
                return priorityCompare;

            return MamePlayfieldStableRank(a).CompareTo(MamePlayfieldStableRank(b));
        });
        return order;
    }

    private static int MamePlayfieldStableRank(int layer)
        => layer switch
        {
            0 => 0,
            3 => 1,
            2 => 2,
            _ => 3,
        };

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
        for (int screenX = 0; screenX < FrameWidth; screenX++)
        {
            if (!IsMameClipAllowed(screenLine, layerMixValue, screenX + VisibleAreaMinX))
                continue;

            int sourceX = (((lineRegFxX + (screenX * line.PlayfieldXScale[lineRamLayer])) >> 8) + VisibleAreaMinX) & 0x01ff;
            int tileX = sourceX / tileSize;
            int pixelX = sourceX & 15;
            int entry = layerWordBase + (tileY * mapTiles + tileX) * 2;
            ushort attr = _bus.ReadPlayfieldWord(entry);
            ushort code = _bus.ReadPlayfieldWord(entry + 1);
            if ((attr | code) == 0)
                continue;
            _lastPlayfieldCandidates++;
            _lastPlayfieldLayerCandidates[lineRamLayer]++;

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
            int pen = DecodeTilemapPixel(roms, code, reefPixelX, reefPixelY, penMask);
            if (pen == 0)
                continue;

            if (tileBlendSelect)
                _lastPlayfieldBlendSelect1[lineRamLayer]++;
            else
                _lastPlayfieldBlendSelect0[lineRamLayer]++;
            WritePalettePixel(screenX, screenY, line.PlayfieldPaletteAdd[lineRamLayer] + palette * 16 + pen, layerPriority, GetPlayfieldLayerRank(lineRamLayer), layerBlendMode, tileBlendSelect);
            _lastPlayfieldPixels++;
            _lastPlayfieldLayerPixels[lineRamLayer]++;
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

                    for (int pixelX = 0; pixelX < tileSize; pixelX++)
                    {
                        int drawX = (word & 0x0100) != 0 ? 7 - pixelX : pixelX;
                        int drawY = (word & 0x8000) != 0 ? 7 - pixelY : pixelY;
                        int pen = DecodeF3CharPixel(code, drawX, drawY);
                        if (pen == 0)
                            continue;

                        WritePalettePixel(screenXBase + pixelX, screenY, palette * 16 + pen, layerPriority, PivotLayerRank, layerBlendMode, layerBlendSelect);
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

            int sourceY = (screenY + scrollY) & 0x01ff;
            int row = (sourceY >> 3) & 31;
            int pixelY = sourceY & 7;
            if (_flipScreen)
                pixelY ^= 7;

            for (int screenX = 0; screenX < FrameWidth; screenX++)
            {
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

        int drawnSpriteCount = _sprites.Count;
        if (!_spriteTrails)
        {
            Array.Clear(_spriteReefPalette);
            Array.Clear(_spriteReefGroup);
        }

        for (int i = _sprites.Count - 1; i >= 0; i--)
            DrawSpriteToReef(roms, _sprites[i]);

        BuildSpriteList();
        _lastVisibleSprites = drawnSpriteCount;
        return drewAny || _lastSpritePixels != 0;
    }

    private void BuildSpriteList()
    {
        _sprites.Clear();
        _lastSpriteCandidates = 0;
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

            _lastSpriteCandidates++;
            int x = _flipScreen ? (512 << 8) - xAxis.BlockScale * 16 - xAxis.Pos : xAxis.Pos;
            int y = _flipScreen ? (256 << 8) - yAxis.BlockScale * 16 - yAxis.Pos : yAxis.Pos;
            _lastSpriteCandidateEntry = offs;
            _lastSpriteCandidateX = x;
            _lastSpriteCandidateY = y;
            _lastSpriteCandidateScaleX = xAxis.BlockScale;
            _lastSpriteCandidateScaleY = yAxis.BlockScale;
            _lastSpriteCandidateTile = tile;
            _lastSpriteCandidateControl = spriteControl;
            TrackSpriteCandidateBounds(x, y, xAxis.BlockScale, yAxis.BlockScale, visibleMinX, visibleMinY, visibleMaxX, visibleMaxY);
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
        for (int y = 0; y < FrameHeight; y++)
        {
            int screenY = y + VisibleAreaMinY;
            int row = y * FrameWidth;
            for (int x = 0; x < FrameWidth; x++)
            {
                ushort paletteIndex = _spriteReefPalette[row + x];
                if (paletteIndex == 0)
                    continue;

                int spriteGroup = _spriteReefGroup[row + x];
                if (!TryReadSpriteCurrentState(screenY, spriteGroup, out int spritePriority, out int spriteBlendMode, out bool spriteBlendSelect))
                    continue;

                byte spriteRank = GetSpriteLayerRank(spriteGroup);
                WritePalettePixel(x, y, paletteIndex, spritePriority, spriteRank, spriteBlendMode, spriteBlendSelect);
                drewAny = true;
            }
        }

        return drewAny;
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
        var line = new F3LineState();
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

            _lineStates[y] = line.Clone();
        }
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

        F3LineState line = _lineStates[screenY & 0xff];
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

    private void DrawSpriteToReef(TaitoF3RomSet roms, F3Sprite sprite)
    {
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
            for (int sx = 0; sx < 16; sx++)
            {
                int dx = dx8 >> 8;
                dx8 += sprite.ScaleX;
                if ((uint)dx >= FrameWidth || dx == (dx8 >> 8))
                    continue;

                int sourceX = sprite.FlipX ? sx ^ 0x0f : sx;
                int pen = DecodeSpritePixel(roms, sprite.Code, sourceX, sourceY) & _spritePenMask;
                if (pen == 0)
                    continue;

                int reefOffset = dy * FrameWidth + dx;
                if (_spriteReefPalette[reefOffset] != 0)
                    continue;

                _spriteReefPalette[reefOffset] = (ushort)(0x1000 + ((sprite.Color << 4) | pen));
                _spriteReefGroup[reefOffset] = (byte)((sprite.Color >> 6) & 3);
                _lastSpritePixels++;
            }
        }
    }

    private static int DecodeSpritePixel(TaitoF3RomSet roms, int code, int x, int y)
    {
        int elements = roms.Sprites.Length / (16 * 8);
        if (elements <= 0)
            return 0;

        code %= elements;
        int lowOffset = code * 16 * 8 + y * 8 + (x >> 1);
        if ((uint)lowOffset >= (uint)roms.Sprites.Length)
            return 0;

        byte packed = roms.Sprites[lowOffset];
        int low = (x & 1) == 0 ? packed & 0x0f : packed >> 4;
        int highBitOffset = code * 16 * 16 * 2 + y * 16 * 2 + SpriteHiXBitOffset(x);
        int highOffset = highBitOffset >> 3;
        if ((uint)highOffset >= (uint)roms.SpritesHi.Length)
            return low;

        int bit = 7 - (highBitOffset & 7);
        int hi0 = (roms.SpritesHi[highOffset] >> bit) & 1;
        int hi1 = 0;
        int highOffset1 = (highBitOffset + 1) >> 3;
        if ((uint)highOffset1 < (uint)roms.SpritesHi.Length)
            hi1 = (roms.SpritesHi[highOffset1] >> (7 - ((highBitOffset + 1) & 7))) & 1;
        return low | (hi0 << 4) | (hi1 << 5);
    }

    private static int SpriteHiXBitOffset(int x)
    {
        int group = x >> 2;
        int inGroup = x & 3;
        return group * 8 + (3 - inGroup) * 2;
    }

    private void ClearWithPalette(int paletteIndex)
    {
        uint color = _bus.ReadPaletteColor(paletteIndex, fallback: 0xff081020);
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
        Array.Clear(_framePriority);
        Array.Fill(_framePriorityRank, EmptyLayerRank);
        Array.Clear(_framePriorityConflict);
        Array.Clear(_mixSrcPalette);
        Array.Clear(_mixSrcBlend);
        Array.Clear(_mixSrcPriority);
        Array.Clear(_mixDstPriority);
        _lastMixPriorityZeroConflicts = 0;
        Array.Fill(_mixDstPalette, (ushort)paletteIndex);
        Array.Fill(_mixDstBlend, (byte)8);
        Array.Fill(_mixSrcBlendMode, (byte)0xff);
        Array.Fill(_mixDstBlendMode, (byte)0xff);
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

        if (blendMode == _mixSrcBlendMode[priorityOffset])
            return;

        F3LineState line = _lineStates[(y + VisibleAreaMinY) & 0xff];
        int select = blendSelect ? 1 : 0;
        ushort palette = (ushort)paletteIndex;

        if (priority > _mixSrcPriority[priorityOffset])
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
            return;
        }

        if (priority >= _mixDstPriority[priorityOffset])
        {
            if (priority == _mixDstPriority[priorityOffset] && priority == 0)
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
        int sourcePixels = 0;
        int litSourcePixels = 0;
        int destOnlyPixels = 0;
        for (int y = 0; y < FrameHeight; y++)
        {
            int row = y * FrameWidth;
            for (int x = 0; x < FrameWidth; x++)
            {
                int offset = row + x;
                if (_mixSrcBlendMode[offset] != 0xff)
                {
                    sourcePixels++;
                    if (_mixSrcBlend[offset] != 0 && _mixSrcPalette[offset] != 0)
                        litSourcePixels++;
                }
                else if (_mixDstPalette[offset] != 0)
                {
                    destOnlyPixels++;
                }
                uint source = _bus.ReadPaletteColor(_mixSrcPalette[offset], fallback: SynthColor(_mixSrcPalette[offset]));
                uint destination = _bus.ReadPaletteColor(_mixDstPalette[offset], fallback: SynthColor(_mixDstPalette[offset]));
                WriteFrameColor(x, y, BlendFixed3(source, destination, _mixSrcBlend[offset], _mixDstBlend[offset]));
            }
        }
        _lastMixSourcePixels = sourcePixels;
        _lastMixLitSourcePixels = litSourcePixels;
        _lastMixDestOnlyPixels = destOnlyPixels;
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
        int elements = roms.Tilemap.Length / (16 * 8);
        if (elements <= 0)
            return 0;

        code %= elements;
        int pen = DecodePacked4BppTilePixel(roms.Tilemap, code, x, y);
        pen |= DecodeTilemapHighPlanes(roms.TilemapHi, code, x, y);
        return pen & penMask;
    }

    private static int DecodePacked4BppTilePixel(byte[] rom, int code, int x, int y)
    {
        int tileOffset = code * 16 * 8;
        int offset = tileOffset + y * 8 + (x >> 1);
        if ((uint)offset >= (uint)rom.Length)
            return 0;

        byte packed = rom[offset];
        return (x & 1) == 0 ? packed & 0x0f : packed >> 4;
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
        return (x & 1) == 0 ? packed & 0x0f : packed >> 4;
    }

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

        return (pc >= 0x0043a0 && pc <= 0x0045d8)
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

    private readonly struct TaitoF3InputState
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

        public F3LineState Clone()
        {
            var clone = new F3LineState
            {
                PivotMix = PivotMix,
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

    private sealed class TaitoF3RomSet
    {
        public byte[] MainCpu { get; private init; } = Array.Empty<byte>();
        public byte[] SoundCpu { get; private init; } = Array.Empty<byte>();
        public byte[] Sprites { get; private init; } = Array.Empty<byte>();
        public byte[] SpritesHi { get; private init; } = Array.Empty<byte>();
        public byte[] Tilemap { get; private init; } = Array.Empty<byte>();
        public byte[] TilemapHi { get; private init; } = Array.Empty<byte>();
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

            return new TaitoF3RomSet
            {
                MainCpu = main,
                SoundCpu = sound,
                Sprites = sprites,
                SpritesHi = entries["d87-05.bin"],
                Tilemap = tilemap,
                TilemapHi = entries["d87-08.bin"],
                Ensoniq = ensoniq
            };
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

    private sealed class TaitoF3MainBus : IBusInterface, IOpcodeBusInterface
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
        private byte _startLatchFrames;
        private ushort _creditCount;
        private bool _soundCpuResetAsserted;
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
        public int PlayfieldNonZeroWords => _playfieldNonZeroWords;
        public int TextNonZeroWords => _textNonZeroWords;
        public int PivotNonZeroWords => _pivotNonZeroWords;
        public int SpriteNonZeroWords => _spriteNonZeroWords;
        public int FirstNonZeroSpriteWordOffset => FindFirstNonZeroWord(_spriteRam);
        public ushort CoinWord0 => _coinWord0;
        public ushort CoinWord1 => _coinWord1;
        public bool HasInsertedCredit => _creditCount != 0;
        public bool SoundCpuResetAsserted => _soundCpuResetAsserted;
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
            _startLatchFrames = 0;
            _creditCount = 0;
            _eeprom.Reset();
            _soundCpuResetAsserted = true;
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
            _workRam[0x006bb4] = (byte)((_workRam[0x006bb4] | 0x01) & ~0x02);
            _workRam[0x006bb5] = (byte)((_workRam[0x006bb5] | 0x01) & ~0x02);
        }

        public void AdvanceMainCycles(uint cycles)
        {
            if (!_pendingInterrupt3 || _interrupt3Ready || _interrupt3DelayCycles <= 0)
                return;

            _interrupt3DelayCycles -= (int)Math.Min(cycles, int.MaxValue);
            if (_interrupt3DelayCycles > 0)
                return;

            _interrupt3Ready = true;
            _interrupt3DelayCycles = 0;
            _interrupt3Asserted = true;
        }

        public void RefreshInputLatches()
        {
            // MAME exposes TC0640FIO through the 0x4a0000 control window and lets
            // the game copy those values into work RAM itself. Writing host input
            // directly into the 0x4022xx ocean corrupts Darius' own state bytes
            // (notably 0x40221d) and traps the boot flow on WAIT A MOMENT.
            //
            // Darius also seeds a few 0x4022xx soft input/status bytes from
            // backup defaults. With no real FIO IRQ task yet, they can remain
            // 0xff and the attract/title task treats them as active service/start
            // gates, looping back to its TRAP #5 wait forever. Keep only those
            // no-input status bytes neutral while leaving the ROM's live
            // 0x40221c/0x40221d latch math untouched. 0x402224 is also tested
            // by the Darius scene-gate path before it sets A5-$144c bit 0.
            WriteWorkRamByteSilently(0x402224, 0x00);
            WriteWorkRamByteSilently(0x402225, 0x00);
            if (Input.Start && !_previousStart)
                _startLatchFrames = 18;

            bool startLatched = _startLatchFrames != 0;
            WriteWorkRamByteSilently(0x402228, startLatched ? (byte)0x7f : (byte)0xff);
            if (Input.Start && !_previousStart)
            {
                WriteWorkRamByteSilently(0x402229, 0x7f);
                if (_creditCount != 0)
                {
                    _creditCount--;
                    PulseSchedulerGateBit0();
                }
            }
            else if (startLatched)
            {
                WriteWorkRamByteSilently(0x402229, 0x7f);
            }
            WriteWorkRamByteSilently(0x40223a, 0x00);

            if (Input.Coin1 && !_previousCoin1)
                _creditCount = (ushort)Math.Min(_creditCount + 1, 9);
            _previousCoin1 = Input.Coin1;
            _previousStart = Input.Start;
            if (_startLatchFrames != 0)
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
            if (MapWindow(address & 0x00ff_ffff, 0x400000, 0x40000, _workRam, out int ramOffset))
                _workRam[ramOffset] = value;
        }

        private void WriteWorkRamWordSilently(uint address, ushort value)
        {
            if (!MapWindow(address & 0x00ff_ffff, 0x400000, 0x40000, _workRam, out int ramOffset))
                return;

            _workRam[ramOffset] = (byte)(value >> 8);
            _workRam[ramOffset + 1] = (byte)value;
        }

        private void LatchSchedulerFrameTick()
        {
            // The ROM copies A5-$144c to A5-$144b in its own scheduler path.
            // Bit 0 is a scene/restart gate set by game code, not a vblank tick;
            // forcing it every frame makes Darius re-enter scene init forever.
            // Bit 1 is tested by update tasks as a skip/hold phase, so keep it
            // from sticking high until the real F3 scheduler is in place.
            _workRam[0x006bb4] = (byte)(_workRam[0x006bb4] & ~0x02);
            _workRam[0x006bb5] = (byte)(_workRam[0x006bb5] & ~0x02);
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
            CurrentOpcode = (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
            return CurrentOpcode;
        }

        public uint ReadLong(uint address)
            => ((uint)ReadWord(address) << 16) | ReadWord(address + 2);

        public ushort ReadOpcodeWord(uint address) => ReadWord(address);

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
            // MAME asserts F3 vblank IRQ2 first and schedules IRQ3 about
            // 10000 main CPU cycles later. The bringup fast paths can cross
            // that delay before the ROM has had a chance to lower SR and
            // acknowledge IRQ2, so preserve the temporal order here.
            if (_interrupt2Asserted)
                return 2;
            if (_interrupt3Asserted)
                return 3;
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
        public bool Reset() => false;
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
            if (VectorBase != 0 && address < 0x400)
                address = (VectorBase + address) & 0x00ff_ffff;

            if (address < _rom.Length)
            {
                value = _rom[address];
                return true;
            }

            if (MapWindow(address, 0x400000, 0x40000, _workRam, out int ramOffset))
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

        private bool TryWriteByte(uint address, byte value)
        {
            if (MapWindow(address, 0x400000, 0x40000, _workRam, out int ramOffset))
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
                if (address >= 0x407360 && address <= 0x407363)
                {
                    LastSpriteListPointerWritePc = CurrentCpuPc;
                    LastSpriteListPointerWriteAddress = address;
                    LastSpriteListPointerValue = PeekLong(0x407360);
                }
                if (address >= 0x406704 && address <= 0x406707)
                {
                    LastIrqWorkPointerWritePc = CurrentCpuPc;
                    LastIrqWorkPointerWriteAddress = address;
                    LastIrqWorkPointerValue = PeekLong(0x406704);
                }
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

        private bool ShouldReadNeutralFioSoftStatus(uint address)
        {
            uint pc = CurrentCpuPc & 0x00ff_ffff;
            return address switch
            {
                0x402224 => pc is 0x004276 or 0x004280 or 0x00428a,
                0x40223a => pc is 0x0042ac or 0x004410 or 0x0044d4,
                0x402225 => pc == 0x0044ca,
                0x402229 => pc == 0x0044e2,
                _ => false
            };
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
            if (Input.Start) value &= ~0x0000_1000u;
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
            switch (offset)
            {
                case 0x04:
                    _coinWord0 = (ushort)(value << 8);
                    break;
                case 0x14:
                    _coinWord1 = (ushort)(value << 8);
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
