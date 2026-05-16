namespace EutherDrive.Core.Arcade.Taito;

using System.Globalization;
using EutherDrive.Core.Cpu.M68000Emu;
using EutherDrive.Core.Savestates;
using SharpCompress.Archives;

public sealed class DariusGaidenAdapter : IEmulatorCore, ISavestateCapable, IDisposable
{
    private const int FrameWidth = 320;
    private const int FrameHeight = 224;
    private const int FrameStride = FrameWidth * 4;
    private const double TargetFps = 26_686_000.0 / 4.0 / (432.0 * 262.0);
    private const int MainClockHz = 16_000_000;
    private const int MainInstructionLimitPerFrame = 30_000;

    private static readonly bool Trace = Environment.GetEnvironmentVariable("EUTHERDRIVE_DARIUSG_TRACE") == "1";
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

    private readonly byte[] _frameBuffer = new byte[FrameHeight * FrameStride];
    private readonly M68000 _mainCpu = M68000.CreateBuilder()
        .AllowUnalignedWordLongAccess(true)
        .Name("dariusg-main-020-probe")
        .Build();
    private readonly TaitoF3MainBus _bus = new();
    private RomIdentity? _romIdentity;
    private TaitoF3RomSet? _roms;
    private string _driverName = "dariusg";
    private long _frameCounter;
    private bool _loaded;
    private bool _cpuFaulted;
    private string _lastStopReason = "idle";
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
    private readonly List<F3Sprite> _latchedSprites = new(0x400);
    private int _lastSpriteCandidates;
    private int _lastVisibleSprites;
    private int _lastSpritePixels;
    private ushort _lastSpriteControlWord;
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
        $"op=0x{_mainCpu.NextOpcode:X4} d0=0x{state.Data[0]:X8} d1=0x{state.Data[1]:X8} a0=0x{state.Address[0]:X8} a1=0x{state.Address[1]:X8} " +
        $"cycles={_executedCycles} instr={_executedInstructions} " +
        $"020probe={_m68ec020ProbeInstructions} tasks={_f3TaskQueue.Count} q={BuildTaskQueueSample()} taskEnq={_f3TasksEnqueued} taskRun={_f3TasksDispatched} " +
        $"lastTask=0x{_lastF3TaskEntry:X6} enq={BuildRecentTaskSample(_recentF3EnqueuedTasks, _recentF3EnqueuedIndex)} run={BuildRecentTaskSample(_recentF3DispatchedTasks, _recentF3DispatchedIndex)} lastTrap=0x{_lastF3TrapPc:X6} vbr=0x{_bus.VectorBase:X6} " +
        $"ramW={_bus.WorkRamWrites} palW={_bus.PaletteWrites} sprW={_bus.SpriteWrites} pfW={_bus.PlayfieldWrites} pfNZ={_bus.PlayfieldNonZeroWords} txtNZ={_bus.TextNonZeroWords} pivNZ={_bus.PivotNonZeroWords} " +
        $"lastSprNZ=0x{_bus.LastNonZeroSpriteWritePc:X6}->0x{_bus.LastNonZeroSpriteWriteAddress:X6}:0x{_bus.LastNonZeroSpriteWriteValue:X2} lastPfNZ=0x{_bus.LastNonZeroPlayfieldWritePc:X6}->0x{_bus.LastNonZeroPlayfieldWriteAddress:X6}:0x{_bus.LastNonZeroPlayfieldWriteValue:X2} lastTxtNZ=0x{_bus.LastNonZeroTextWritePc:X6}->0x{_bus.LastNonZeroTextWriteAddress:X6}:0x{_bus.LastNonZeroTextWriteValue:X2} " +
        $"mode=0x{_bus.PeekByte(0x40221d):X2}/0x{_bus.PeekByte(0x40223d):X2}/0x{_bus.PeekByte(0x40223f):X2} bkup18=0x{_bus.PeekByte(0x406c6c):X2} cfg2_18=0x{_bus.PeekByte(0x406c8c):X2} gateEbb4=0x{_bus.PeekByte(0x406bb4):X2} gateEbb5=0x{_bus.PeekByte(0x406bb5):X2} gateW=0x{_bus.LastGateWritePc:X6}->0x{_bus.LastGateWriteAddress:X6}:0x{_bus.LastGateWriteValue:X2} gateNZ=0x{_bus.LastNonZeroGateWritePc:X6}->0x{_bus.LastNonZeroGateWriteAddress:X6}:0x{_bus.LastNonZeroGateWriteValue:X2}/{_bus.NonZeroGateWrites} scene=entry:{_sceneEntryHits}/init:{_sceneInitResumeHits},{_sceneInitMainHits},{_sceneMenuInitHits},{_sceneMenuYieldHits},{_sceneSpawnerYieldHits}/gate:{_sceneGateRoutineHits}/bset:{_sceneGateSetInstructionHits}/wait:{_sceneGateWaitHits}/mainwait:{_mainGateWaitHits}/call=0x{_lastSceneAbsoluteCallTarget:X6}/cont:{_sceneContinuationEnqueued},{_sceneContinuationDispatched},{_sceneContinuationRemoved}/rm=0x{_lastF3TaskRemoveMask:X8} flag224=0x{_bus.PeekByte(0x402224):X2} obj916=0x{_bus.PeekByte(0x408916):X2} obj917=0x{_bus.PeekByte(0x408917):X2} listCnt=0x{_bus.PeekWord(0x402218):X4} listPtr=0x{_bus.PeekLong(0x407360):X6} sprNZ={_bus.SpriteNonZeroWords} sprFirst={_bus.FirstNonZeroSpriteWordOffset:X4} sprRaw={BuildSpriteRamSample()} sprHead={BuildSpritePointerSample()} sprTiles={BuildSpriteTileSample()} " +
        $"sprCand={_lastSpriteCandidates} sprVis={_lastVisibleSprites} sprPix={_lastSpritePixels} sprCtl=0x{_lastSpriteControlWord:X4} sprBank={(_spriteBank ? 1 : 0)} " +
        $"ctrlR={_bus.ControlReads} lastCtrl=0x{_bus.LastControlReadAddress:X6}:0x{_bus.LastControlReadValue:X2} modeW=0x{_bus.LastModeWritePc:X6}->0x{_bus.LastModeWriteAddress:X6}:0x{_bus.LastModeWriteValue:X2} modeBtst=0x{_bus.LastModeBtstPc:X6}@0x{_bus.LastModeBtstAddress:X6}:0x{_bus.LastModeBtstValue:X2}/b{_bus.LastModeBtstBit}/z{(_bus.LastModeBtstZero ? 1 : 0)} btst=0x{_bus.LastBtstAddress:X6}:0x{_bus.LastBtstValue:X2}/b{_bus.LastBtstBit} bkupW=0x{_bus.LastBackupWritePc:X6}:0x{_bus.LastBackupWriteValue:X2} ctrlW={_bus.ControlWrites} unmappedR={_bus.UnmappedReads} unmappedW={_bus.UnmappedWrites} stop={_lastStopReason}";
    }

    private string BuildSpritePointerSample()
    {
        uint pointer = _bus.PeekLong(0x407360) & 0x00ff_ffff;
        if (pointer < 0x600000 || pointer >= 0x610000)
            return "--------";

        return string.Create(CultureInfo.InvariantCulture, $"{_bus.PeekWord(pointer):X4},{_bus.PeekWord(pointer + 2):X4},{_bus.PeekWord(pointer + 4):X4},{_bus.PeekWord(pointer + 6):X4},{_bus.PeekWord(pointer + 8):X4},{_bus.PeekWord(pointer + 10):X4},{_bus.PeekWord(pointer + 12):X4},{_bus.PeekWord(pointer + 14):X4}");
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
                ? task.Pc.ToString("X6", CultureInfo.InvariantCulture)
                : string.Create(CultureInfo.InvariantCulture, $"{task.Pc:X6}:{task.DelayFrames}")));

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
        _frameCounter = 0;
        _executedInstructions = 0;
        _executedCycles = 0;
        _m68ec020ProbeInstructions = 0;
        _f3TasksEnqueued = 0;
        _f3TasksDispatched = 0;
        _lastF3TaskEntry = 0;
        _lastF3TaskStack = 0;
        _lastF3TrapPc = 0;
        _currentF3TaskPriority = 0;
        ResetSceneDiagnostics();
        _nextF3TaskStack = 0x0041_f000;
        _traceInstructionsRemaining = TraceInstructionLimit;
        _traceBootPcRemaining = TraceBootPcLimit;
        _f3TaskQueue.Clear();
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
        _f3TasksEnqueued = 0;
        _f3TasksDispatched = 0;
        _lastF3TaskEntry = 0;
        _lastF3TaskStack = 0;
        _lastF3TrapPc = 0;
        _currentF3TaskPriority = 0;
        ResetSceneDiagnostics();
        _nextF3TaskStack = 0x0041_f000;
        _lastStopReason = "reset";
        _traceBootPcRemaining = TraceBootPcLimit;
        _traceInstructionsRemaining = TraceInstructionLimit;
        _f3TaskQueue.Clear();
        RenderUglyVideo();
    }

    public void RunFrame()
    {
        if (!_loaded)
            return;

        _frameCounter++;
        _bus.BeginFrameInterrupt();
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
                instructions++;
                _executedCycles += used;
                _executedInstructions++;

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

        RenderUglyVideo();
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        width = FrameWidth;
        height = FrameHeight;
        stride = FrameStride;
        return _frameBuffer;
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
        _bus.Input = new TaitoF3InputState(up, down, left, right, a, b, c, start, x, y, z, mode);
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write("DARIUSG");
        writer.Write(9);
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
        if (version < 3 || version > 9)
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

        bool drewAny = false;
        if (_bus.PlayfieldNonZeroWords != 0)
            drewAny |= RenderPlayfields(roms);
        drewAny |= RenderPivotPixelLayer(roms);
        drewAny |= RenderSprites(roms);
        drewAny |= RenderTextLayer(roms);

        if (!drewAny)
            ClearWithPalette(0);
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
        if (TryBypassCreditStartGate(pc, op, out cycles))
            return true;
        if (TryBypassWaitAMomentGate(pc, op, out cycles))
            return true;
        if (TryBypassBackupRamInitReset(pc, op, out cycles))
            return true;
        if (TryExecuteBtstImmediateByteDisplacement(pc, op, out cycles))
            return true;
        if (TryExecuteF3SchedulerYieldEntry(pc, op, out cycles))
            return true;
        if (TryExecuteF3TrapSchedulerStub(pc, op, out cycles))
            return true;
        if (TryDispatchF3QueuedTask(pc, op, out cycles))
            return true;
        if (TrySkipEmptySpriteControlSlots(pc, op, out cycles))
            return true;

        // 68020 MULL.L. Darius Gaiden uses this during F3 boot math before
        // video RAM is fully populated; keep it local until a real 020 core exists.
        if ((op & 0xffc0) == 0x4c00)
            return TryExecuteMullLong(pc, op, out cycles);

        if ((op & 0xfff8) == 0xe9c0)
            return TryExecuteBfextuDataRegister(pc, op, out cycles);
        if ((op & 0xfff8) == 0xe9e8)
            return TryExecuteBfextuDisplacement(pc, op, out cycles);
        if ((op & 0xfff8) == 0xefe8)
            return TryExecuteBfinsDisplacement(pc, op, out cycles);
        if ((op & 0xfff8) == 0xeff0)
            return TryExecuteBfinsIndexed(pc, op, out cycles);
        if ((op & 0xfff8) == 0x49c0)
            return TryExecuteExtByteToLong(pc, op, out cycles);
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
                    int delayFrames = op == 0x4e44
                        ? Math.Clamp((int)(_bus.ReadLong((state.Sr & 0x2000) != 0 ? state.Ssp : state.Usp) & 0x7fff), 1, 600)
                        : 1;
                    EnqueueF3Task(
                        CreateContinuationF3TaskState(state, continuation, delayFrames, _currentF3TaskPriority),
                        preferFront: IsF3SceneInitializationContinuation(continuation));
                    if (IsF3SceneInitializationContinuation(continuation))
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

    private bool TryExecuteF3SchedulerYieldEntry(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
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
                preferFront: IsF3SceneInitializationContinuation(continuation));
            _f3TasksEnqueued++;
            _lastF3TaskEntry = continuation;
            RecordRecentTask(_recentF3EnqueuedTasks, ref _recentF3EnqueuedIndex, continuation);
            if (IsF3SceneInitializationContinuation(continuation))
                _sceneContinuationEnqueued++;
        }

        uint idlePc = 0x002326;
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

        var state = _mainCpu.GetState();
        if (_f3TaskQueue.Count > 0 && TryDispatchNextF3Task(state, out cycles))
            return true;

        uint idlePc = 0x002326;
        ushort prefetch = _bus.ReadOpcodeWord(idlePc);
        _mainCpu.SetState(new M68000.M68000State(state.Data, state.Address, state.Usp, state.Ssp, state.Sr, idlePc, prefetch));
        cycles = (uint)((int)(MainClockHz / TargetFps) * CpuScale);
        return true;
    }

    private bool TryDispatchF3QueuedTask(uint pc, ushort op, out uint cycles)
    {
        cycles = 0;
        if ((pc != 0x010170 && pc != 0x002326) || _f3TaskQueue.Count == 0)
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
        if (IsF3SceneInitializationContinuation(task.Pc))
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

    private static bool IsF3SceneInitializationContinuation(uint pc)
        => (pc >= 0x004148 && pc < 0x004152) || (pc >= 0x0042a0 && pc < 0x0045d8);

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
            else if (IsF3SceneInitializationContinuation(task.Pc))
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
        state.Data[0] = 0;
        uint nextPc = 0x0102ee;
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
        if (pc != 0x000f3c)
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
        bool drewAny = false;
        for (int layer = 0; layer < 8; layer++)
            drewAny |= RenderPlayfieldLayer(roms, layer);
        return drewAny;
    }

    private bool RenderPlayfieldLayer(TaitoF3RomSet roms, int layer)
    {
        const int tileSize = 16;
        const int mapTiles = 32;
        int layerWordBase = layer * 0x800;
        int scrollX = _bus.ReadControlWord(0, layer & 3) & 0x01ff;
        int scrollY = _bus.ReadControlWord(0, 4 + (layer & 3)) & 0x01ff;
        bool drewAny = false;

        for (int screenY = 0; screenY < FrameHeight; screenY++)
        {
            int sourceY = (screenY + scrollY) & 0x01ff;
            int tileY = sourceY / tileSize;
            int pixelY = sourceY & 15;
            for (int screenX = 0; screenX < FrameWidth; screenX++)
            {
                int sourceX = (screenX + scrollX) & 0x01ff;
                int tileX = sourceX / tileSize;
                int pixelX = sourceX & 15;
                int entry = layerWordBase + (tileY * mapTiles + tileX) * 2;
                ushort attr = _bus.ReadPlayfieldWord(entry);
                ushort code = _bus.ReadPlayfieldWord(entry + 1);
                if ((attr | code) == 0)
                    continue;

                if ((attr & 0x4000) != 0)
                    pixelX = 15 - pixelX;
                if ((attr & 0x8000) != 0)
                    pixelY = 15 - pixelY;

                int extraPlanes = (attr >> 10) & 3;
                int pen = DecodeTilemapPixel(roms, code, pixelX, pixelY, extraPlanes);
                if (pen == 0)
                    continue;

                int palette = attr & 0x01ff;
                WritePalettePixel(screenX, screenY, palette * 16 + pen);
                drewAny = true;
            }
        }

        return drewAny;
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

                    for (int pixelX = 0; pixelX < tileSize; pixelX++)
                    {
                        int drawX = (word & 0x0100) != 0 ? 7 - pixelX : pixelX;
                        int drawY = (word & 0x8000) != 0 ? 7 - pixelY : pixelY;
                        int pen = DecodeF3CharPixel(code, drawX, drawY);
                        if (pen == 0)
                            continue;

                        WritePalettePixel(screenXBase + pixelX, screenY, palette * 16 + pen);
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

                WritePalettePixel(screenX, screenY, palette * 16 + pen);
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
        bool drewAny = false;
        List<F3Sprite> drawSprites = _latchedSprites.Count == 0 ? _sprites : _latchedSprites;
        for (int i = drawSprites.Count - 1; i >= 0; i--)
            drewAny |= DrawSprite(roms, drawSprites[i]);

        BuildSpriteList();
        _latchedSprites.Clear();
        _latchedSprites.AddRange(_sprites);

        _lastVisibleSprites = drawSprites.Count;
        return drewAny;
    }

    private void BuildSpriteList()
    {
        _sprites.Clear();
        _lastSpriteCandidates = 0;
        _lastVisibleSprites = 0;
        _lastSpritePixels = 0;
        bool requestedBank = _spriteBank;
        BuildSpriteListFrom(0, requestedBank);

        if (_sprites.Count != 0 || _lastSpriteCandidates != 0)
            return;
        BuildSpriteListFrom(0, !requestedBank);

        if (_sprites.Count != 0 || _lastSpriteCandidates != 0)
            return;

        uint listPointer = _bus.PeekLong(0x407360) & 0x00ff_ffff;
        if (listPointer < 0x600000 || listPointer >= 0x610000)
            return;

        bool savedBank = _spriteBank;
        bool pointerBank = listPointer >= 0x608000;
        int bank = pointerBank ? 0x4000 : 0;
        int startWordOffset = (int)((listPointer - 0x600000) >> 1);
        int startEntry = (startWordOffset - bank) / 8;
        if ((uint)startEntry < 0x400)
            BuildSpriteListFrom(startEntry, pointerBank);
        if (_sprites.Count == 0 && _lastSpriteCandidates == 0)
            _spriteBank = savedBank;
    }

    private void BuildSpriteListFrom(int startEntry, bool bankSelect)
    {
        var xAxis = new SpriteAxis();
        var yAxis = new SpriteAxis();
        byte color = 0;
        bool multi = false;
        int bank = bankSelect ? 0x4000 : 0;

        for (int offs = startEntry, guard = 0; (uint)offs < 0x400 && guard < 0x400; offs++, guard++)
        {
            int wordOffset = bank + offs * 8;
            ushort w0 = _bus.ReadSpriteWord(wordOffset + 0);
            ushort w1 = _bus.ReadSpriteWord(wordOffset + 1);
            ushort w2 = _bus.ReadSpriteWord(wordOffset + 2);
            ushort w3 = _bus.ReadSpriteWord(wordOffset + 3);
            ushort w4 = _bus.ReadSpriteWord(wordOffset + 4);
            ushort w5 = _bus.ReadSpriteWord(wordOffset + 5);
            ushort w6 = _bus.ReadSpriteWord(wordOffset + 6);

            if ((w3 & 0x8000) != 0)
            {
                _lastSpriteControlWord = w5;
                _flipScreen = (w5 & 0x2000) != 0;
                int extraPlanes = (w5 >> 8) & 3;
                _spritePenMask = (extraPlanes << 4) | 0x0f;
                _spriteTrails = (w5 & 0x0002) != 0;
                _spriteBank = (w5 & 0x0001) != 0;
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
            if (x + xAxis.BlockScale * 16 <= 0 || x > (FrameWidth - 1) << 8 || y + yAxis.BlockScale * 16 <= 0 || y > (FrameHeight - 1) << 8)
                continue;

            bool flipX = (spriteControl & 0x01) != 0;
            bool flipY = (spriteControl & 0x02) != 0;
            _sprites.Add(new F3Sprite(
                x,
                y,
                _flipScreen ? !flipX : flipX,
                _flipScreen ? !flipY : flipY,
                tile,
                color,
                xAxis.BlockScale,
                yAxis.BlockScale));
        }
    }

    private bool DrawSprite(TaitoF3RomSet roms, F3Sprite sprite)
    {
        bool drewAny = false;
        int dy8 = sprite.Y;

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

                WritePalettePixel(dx, dy, 0x1000 + ((sprite.Color << 4) | pen));
                drewAny = true;
                _lastSpritePixels++;
            }
        }

        return drewAny;
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
    }

    private void WritePalettePixel(int x, int y, int paletteIndex)
    {
        uint color = _bus.ReadPaletteColor(paletteIndex, fallback: SynthColor(paletteIndex));
        int offset = y * FrameStride + x * 4;
        _frameBuffer[offset + 0] = (byte)color;
        _frameBuffer[offset + 1] = (byte)(color >> 8);
        _frameBuffer[offset + 2] = (byte)(color >> 16);
        _frameBuffer[offset + 3] = 0xff;
    }

    private static uint SynthColor(int paletteIndex)
    {
        int r = ((paletteIndex * 37) ^ (paletteIndex >> 2)) & 0xff;
        int g = ((paletteIndex * 73) ^ (paletteIndex >> 1)) & 0xff;
        int b = ((paletteIndex * 19) ^ (paletteIndex << 1)) & 0xff;
        return 0xff000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static int DecodeTilemapPixel(TaitoF3RomSet roms, int code, int x, int y, int extraPlanes)
    {
        int elements = roms.Tilemap.Length / (16 * 8);
        if (elements <= 0)
            return 0;

        code %= elements;
        int pen = DecodePacked4BppTilePixel(roms.Tilemap, code, x, y);
        if (extraPlanes == 0)
            return pen;

        pen |= DecodeTilemapHighPlanes(roms.TilemapHi, code, x, y) & (extraPlanes << 4);
        return pen;
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
            case 0x004274:
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
        public readonly bool Mode;

        public TaitoF3InputState(bool up, bool down, bool left, bool right, bool a, bool b, bool c, bool start, bool x, bool y, bool z, bool mode)
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
            Mode = mode;
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
        private int _playfieldNonZeroWords;
        private int _textNonZeroWords;
        private int _pivotNonZeroWords;
        private int _spriteNonZeroWords;
        private byte _interruptLevel;
        private bool _pendingInterrupt3;
        private ushort _coinWord0;
        private ushort _coinWord1;
        private ushort _timerControl0;
        private ushort _timerControl1;
        private byte _eepromOutLatch;
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
        public uint CurrentCpuPc { get; set; }
        public uint LastControlReadAddress { get; private set; }
        public byte LastControlReadValue { get; private set; }
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
        public int PlayfieldNonZeroWords => _playfieldNonZeroWords;
        public int TextNonZeroWords => _textNonZeroWords;
        public int PivotNonZeroWords => _pivotNonZeroWords;
        public int SpriteNonZeroWords => _spriteNonZeroWords;
        public int FirstNonZeroSpriteWordOffset => FindFirstNonZeroWord(_spriteRam);

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
            _playfieldNonZeroWords = 0;
            _textNonZeroWords = 0;
            _pivotNonZeroWords = 0;
            _spriteNonZeroWords = 0;
            _interruptLevel = 0;
            _pendingInterrupt3 = false;
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
            WorkRamWrites = PaletteWrites = SpriteWrites = PlayfieldWrites = 0;
            ControlReads = ControlWrites = UnmappedReads = UnmappedWrites = 0;
            LastControlReadAddress = 0;
            LastControlReadValue = 0;
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
            LastBtstAddress = 0;
            LastBtstValue = 0;
            LastBtstBit = 0;
            LastModeBtstPc = 0;
            LastModeBtstAddress = 0;
            LastModeBtstValue = 0;
            LastModeBtstBit = 0;
            LastModeBtstZero = false;
            EnsureBackupDefaults();
        }

        public void BeginFrameInterrupt()
        {
            // The bringup scheduler below drives F3 cooperative tasks through TRAP #5.
            // Injecting the ROM IRQ scheduler at the same time corrupts early task state.
            _interruptLevel = 0;
            _pendingInterrupt3 = false;
            LatchSchedulerFrameTick();
        }

        public void RefreshInputLatches()
        {
            // F3 games consume debounced active-low controls from work RAM. The real
            // FIO IRQ path is not running in this bringup scheduler yet, so mirror a
            // stable no-input latch here and clear bits only for live host input.
            byte p1 = 0xff;
            if (Input.A) p1 &= unchecked((byte)~0x01);
            if (Input.B) p1 &= unchecked((byte)~0x02);
            if (Input.C) p1 &= unchecked((byte)~0x04);
            if (Input.X) p1 &= unchecked((byte)~0x08);
            if (Input.Start) p1 &= unchecked((byte)~0x08);
            if (Input.Up) p1 &= unchecked((byte)~0x10);
            if (Input.Down) p1 &= unchecked((byte)~0x20);
            if (Input.Left) p1 &= unchecked((byte)~0x40);
            if (Input.Right) p1 &= unchecked((byte)~0x80);

            WriteWorkRamByteSilently(0x40221c, p1);
            WriteWorkRamByteSilently(0x40221d, p1);
            WriteWorkRamByteSilently(0x40223c, p1);
            WriteWorkRamByteSilently(0x40223d, p1);
            WriteWorkRamByteSilently(0x40223e, p1);
            WriteWorkRamByteSilently(0x40223f, p1);

            // The ROM also mirrors the TC0640FIO EEPROM/service/coin byte into this
            // nearby block. Leaving it as zero makes service/coin bits look asserted
            // and holds Darius Gaiden on the boot disk/status screen.
            byte eepromIn = 0xff;
            if (!_eeprom.DataOut)
                eepromIn &= unchecked((byte)~0x01);
            if (Input.Mode) eepromIn &= unchecked((byte)~0x02);
            WriteWorkRamByteSilently(0x4022a8, eepromIn);
            WriteWorkRamByteSilently(0x4022a9, eepromIn);
            WriteWorkRamByteSilently(0x4022aa, eepromIn);
            WriteWorkRamByteSilently(0x4022ab, eepromIn);
            WriteWorkRamByteSilently(0x4022ac, eepromIn);
            WriteWorkRamByteSilently(0x4022ad, eepromIn);
            WriteWorkRamByteSilently(0x4022ae, eepromIn);
            WriteWorkRamByteSilently(0x4022af, eepromIn);
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

        private void LatchSchedulerFrameTick()
        {
            // The ROM scheduler copies A5-$144c to A5-$144b each tick. Bit 0
            // wakes the main scheduler task; bit 1 is tested by update tasks as
            // a skip/hold phase, so forcing it high stalls boot progression.
            _workRam[0x006bb4] = (byte)((_workRam[0x006bb4] | 0x01) & ~0x02);
            _workRam[0x006bb5] = (byte)((_workRam[0x006bb5] | 0x01) & ~0x02);
        }

        public byte ReadByte(uint address)
        {
            address &= 0x00ff_ffff;
            if (TryReadByte(address, out byte value))
                return value;

            UnmappedReads++;
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
        }

        public void WriteWord(uint address, ushort value)
        {
            WriteByte(address, (byte)(value >> 8));
            WriteByte(address + 1, (byte)value);
        }

        public void WriteLong(uint address, uint value)
        {
            WriteWord(address, (ushort)(value >> 16));
            WriteWord(address + 2, (ushort)value);
        }

        public byte InterruptLevel() => _interruptLevel;

        public void AcknowledgeInterrupt(byte level)
        {
            if (level == _interruptLevel)
            {
                if (level == 2 && _pendingInterrupt3)
                {
                    _pendingInterrupt3 = false;
                    _interruptLevel = 3;
                }
                else
                {
                    _interruptLevel = 0;
                }
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
            writer.Write(_interruptLevel);
            writer.Write(_pendingInterrupt3);
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
            _textNonZeroWords = version >= 6 ? reader.ReadInt32() : CountNonZeroWords(_textRam);
            _spriteNonZeroWords = version >= 7 ? reader.ReadInt32() : CountNonZeroWords(_spriteRam);
            _pivotNonZeroWords = CountNonZeroWords(_pivotRam);
            _interruptLevel = reader.ReadByte();
            _pendingInterrupt3 = version >= 5 && reader.ReadBoolean();
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
                value = _workRam[ramOffset];
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
                ushort before = ReadBigEndianWord(_playfieldRam, pfOffset & ~1);
                _playfieldRam[pfOffset] = value;
                ushort after = ReadBigEndianWord(_playfieldRam, pfOffset & ~1);
                if (before == 0 && after != 0)
                    _playfieldNonZeroWords++;
                else if (before != 0 && after == 0)
                    _playfieldNonZeroWords--;
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
                ControlWrites++;
                return true;
            }

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
            if (Input.Mode)
                eepromIn &= unchecked((byte)~0x02);

            uint value = ((uint)eepromIn << 24) | ((uint)eepromIn << 16) | 0x0000_ffffu;
            if (Input.A) value &= ~0x0000_0001u;
            if (Input.B) value &= ~0x0000_0002u;
            if (Input.C) value &= ~0x0000_0004u;
            if (Input.X) value &= ~0x0000_0008u;
            if (Input.Mode) value &= ~0x0000_0200u;
            if (Input.Start) value &= ~0x0000_1000u;
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
                    _coinWord0 = (ushort)((value << 8) | (_coinWord0 & 0x00ff));
                    break;
                case 0x05:
                    _coinWord0 = (ushort)((_coinWord0 & 0xff00) | value);
                    break;
                case 0x14:
                    _coinWord1 = (ushort)((value << 8) | (_coinWord1 & 0x00ff));
                    break;
                case 0x15:
                    _coinWord1 = (ushort)((_coinWord1 & 0xff00) | value);
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
