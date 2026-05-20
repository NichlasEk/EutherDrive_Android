using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using EutherDrive.Core.Savestates;
using SharpCompress.Archives;

namespace EutherDrive.Core.Arcade.Vegas;

public sealed class GauntletDarkLegacyAdapter : IEmulatorCore
{
    private const int FrameWidth = 640;
    private const int FrameHeight = 480;
    private const int FrameStride = FrameWidth * 4;
    private const int AudioSampleRate = 44_100;
    private const int AudioChannels = 2;

    private readonly byte[] _frameBuffer = new byte[FrameHeight * FrameStride];
    private readonly GauntletDarkLegacyMachine _machine = new();
    private readonly bool _skipProbeFrameRender = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_SKIP_FRAME_RENDER") == "1";
    private RomIdentity? _romIdentity;
    private long _frameCounter;
    private bool _loaded;

    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => _loaded ? _frameCounter : null;
    public string DebugStatus => _machine.GetDebugStatus();

    internal static bool IsBringupFixEnabled(string name)
    {
        string? specific = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(specific))
            return IsTruthy(specific);

        return IsTruthy(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_BRINGUP_FAST"));
    }

    private static bool IsTruthy(string? value)
        => value is not null &&
           (value == "1" ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase));

    public static bool IsSupportedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string name = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
        if (name is "gauntdl" or "gauntdl24")
            return true;

        if (!Directory.Exists(path))
            return false;

        return File.Exists(Path.Combine(path, "gauntdl.zip")) ||
               File.Exists(Path.Combine(path, "gauntdl24.7z")) ||
               File.Exists(Path.Combine(path, "gauntdl24.zip"));
    }

    public void LoadRom(string path)
    {
        GauntletRomSet romSet = GauntletRomSet.Load(path);
        _machine.Load(romSet);
        _romIdentity = romSet.CreateIdentity();
        _loaded = true;
        Reset();
    }

    public void Reset()
    {
        _frameCounter = 0;
        _machine.Reset();
        DrawDiagnosticFrame();
    }

    public void RunFrame()
    {
        if (!_loaded)
            return;

        EutherFrameTarget target = _skipProbeFrameRender
            ? new EutherFrameTarget(Array.Empty<byte>(), 0, 0, 0)
            : new EutherFrameTarget(_frameBuffer, FrameWidth, FrameHeight, FrameStride);
        _machine.RunFrame(target);
        _frameCounter++;
        if (!_skipProbeFrameRender && !_machine.Voodoo.HasVideoActivity)
            DrawDiagnosticFrame();
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        if (_skipProbeFrameRender && _machine.Voodoo.HasVideoActivity)
            _machine.RenderFrame(new EutherFrameTarget(_frameBuffer, FrameWidth, FrameHeight, FrameStride));

        width = FrameWidth;
        height = FrameHeight;
        stride = FrameStride;
        return _frameBuffer;
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = AudioSampleRate;
        channels = AudioChannels;
        return _machine.Audio.GetFrameBuffer();
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
        _machine.Input.SetPlayer1(up, down, left, right, attack: a, magic: b || c, start, coin: mode);
        _machine.Input.Service = x;
        _machine.Input.Test = y;
    }

    private void DrawDiagnosticFrame()
    {
        Array.Clear(_frameBuffer);
        uint background = _loaded ? 0xff181818u : 0xff050505u;
        FillRect(0, 0, FrameWidth, FrameHeight, background);

        int barWidth = (int)(_frameCounter % FrameWidth);
        FillRect(0, 0, barWidth, 8, 0xff2aa198u);
        FillRect(20, 28, 600, 120, 0xff242424u);
        FillRect(24, 32, _machine.RomLoaded ? 180 : 40, 16, _machine.RomLoaded ? 0xff4caf50u : 0xffa94442u);
        FillRect(24, 56, _machine.Disk.Attached ? 180 : 40, 16, _machine.Disk.Attached ? 0xff4caf50u : 0xffa94442u);
        FillRect(24, 80, _machine.Voodoo.TraceEnabled ? 180 : 80, 16, _machine.Voodoo.TraceEnabled ? 0xff268bd2u : 0xff666666u);
    }

    private void FillRect(int x, int y, int width, int height, uint bgra)
    {
        int x0 = Math.Clamp(x, 0, FrameWidth);
        int y0 = Math.Clamp(y, 0, FrameHeight);
        int x1 = Math.Clamp(x + width, 0, FrameWidth);
        int y1 = Math.Clamp(y + height, 0, FrameHeight);

        for (int py = y0; py < y1; py++)
        {
            int offset = py * FrameStride + x0 * 4;
            for (int px = x0; px < x1; px++)
            {
                _frameBuffer[offset + 0] = (byte)(bgra & 0xff);
                _frameBuffer[offset + 1] = (byte)((bgra >> 8) & 0xff);
                _frameBuffer[offset + 2] = (byte)((bgra >> 16) & 0xff);
                _frameBuffer[offset + 3] = (byte)((bgra >> 24) & 0xff);
                offset += 4;
            }
        }
    }
}

internal sealed class GauntletDarkLegacyMachine
{
    private readonly bool _splitVblankCpu = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_SPLIT_VBLANK_CPU");
    private readonly int _vblankCpuSteps = ParsePositiveInt("EUTHERDRIVE_GAUNTDL_VBLANK_CPU_STEPS", 2048);

    public VegasMemoryMap MemoryMap { get; } = new();
    public MipsR5000Core Cpu { get; }
    public IdeDiskDevice Disk { get; } = new();
    public VegasSioDevice Sio { get; } = new();
    public DcsAudioDevice Audio { get; } = new();
    public VoodooFacade Voodoo { get; } = new();
    public GauntletInputPanel Input { get; } = new();

    public bool RomLoaded { get; private set; }

    public GauntletDarkLegacyMachine()
    {
        Cpu = new MipsR5000Core(MemoryMap);
        Voodoo.SetCpuPcProvider(() => Cpu.Pc);
    }

    public void Load(GauntletRomSet romSet)
    {
        RomLoaded = romSet.MainRom.Length == 0x80000;
        Disk.Attach(romSet.ChdPath);
        Sio.LoadBootRom(romSet.VegasSioRom);
        Audio.LoadBootRom(romSet.VegasSioRom);
        MemoryMap.LoadMainBootRom(romSet.MainRom);
        MemoryMap.LoadSecurityPic(romSet.SecurityPic);
        MemoryMap.AttachDevices(Sio, Disk, Audio, Voodoo);
    }

    public void Reset()
    {
        MemoryMap.Reset();
        Cpu.Reset();
        Disk.Reset();
        Sio.Reset();
        Audio.Reset();
        Voodoo.Reset();
    }

    public void RunFrame(EutherFrameTarget target)
    {
        Sio.PulseVblank(state: true);
        if (_splitVblankCpu)
        {
            Cpu.RunProbeSteps(_vblankCpuSteps);
            Sio.PulseVblank(state: false);
            Cpu.RunProbeFrameAfterSteps(_vblankCpuSteps);
        }
        else
        {
            Cpu.RunProbeFrame();
            Sio.PulseVblank(state: false);
        }
        MemoryMap.StepFrame();
        Audio.RunFrame();
        if (MemoryMap.ConsumeWatchdogResetRequest())
            Reset();
        RenderFrame(target);
    }

    public void RenderFrame(EutherFrameTarget target) => Voodoo.RenderFrame(target);

    public string GetDebugStatus()
    {
        return $"pc=0x{Cpu.Pc:X16} op=0x{Cpu.LastFetchedInstruction:X8} " +
               $"{Cpu.RuntimeDiagnosticStatus} " +
               $"voodoo={(Voodoo.HasVideoActivity ? "active" : "idle")} {Voodoo.DebugStatus} " +
               $"{MemoryMap.DebugStatus} {Audio.DebugStatus} disk={(Disk.Attached ? "attached" : "missing")}";
    }

    private static int ParsePositiveInt(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : fallback;
    }
}

internal sealed class GauntletRomSet
{
    private static readonly IReadOnlyDictionary<string, string> DiskBySet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["gauntdl"] = "gauntdl.chd",
        ["gauntdl24"] = "gauntd24.chd"
    };

    private GauntletRomSet(string setName, string sourcePath, byte[] mainRom, byte[] vegasSioRom, byte[] securityPic, string? chdPath)
    {
        SetName = setName;
        SourcePath = sourcePath;
        MainRom = mainRom;
        VegasSioRom = vegasSioRom;
        SecurityPic = securityPic;
        ChdPath = chdPath;
    }

    public string SetName { get; }
    public string SourcePath { get; }
    public byte[] MainRom { get; }
    public byte[] VegasSioRom { get; }
    public byte[] SecurityPic { get; }
    public string? ChdPath { get; }

    public static GauntletRomSet Load(string path)
    {
        string archivePath = ResolveArchivePath(path);
        string setName = Path.GetFileNameWithoutExtension(archivePath).ToLowerInvariant();
        Dictionary<string, byte[]> entries = ReadEntries(archivePath);

        byte[] mainRom = RequireEntry(entries, "gauntdl.bin", 0x80000);
        byte[] sioRom = RequireEntry(entries, "vegassio.bin", 0x8000);
        byte[] securityPic = RequireEntry(entries, "346_gauntlet-dl.u37", 0x2000);
        string? chdPath = ResolveChdPath(archivePath, setName);

        return new GauntletRomSet(setName, archivePath, mainRom, sioRom, securityPic, chdPath);
    }

    public RomIdentity CreateIdentity()
    {
        using var stream = new MemoryStream();
        stream.Write(MainRom);
        stream.Write(VegasSioRom);
        stream.Write(SecurityPic);

        if (!string.IsNullOrWhiteSpace(ChdPath) && File.Exists(ChdPath))
        {
            byte[] pathBytes = System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(ChdPath));
            stream.Write(pathBytes);
        }

        stream.Position = 0;
        return new RomIdentity(SetName, RomIdentity.ComputeSha256(stream), PersistentStoragePath.ResolveSavestateDirectory(SourcePath, "gauntdl"));
    }

    private static string ResolveArchivePath(string path)
    {
        if (File.Exists(path))
            return Path.GetFullPath(path);

        if (!Directory.Exists(path))
            throw new FileNotFoundException("Gauntlet Dark Legacy ROM archive or directory not found.", path);

        string[] candidates =
        {
            Path.Combine(path, "gauntdl24.7z"),
            Path.Combine(path, "gauntdl24.zip"),
            Path.Combine(path, "gauntdl.zip")
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        throw new FileNotFoundException("Directory does not contain gauntdl.zip, gauntdl24.7z, or gauntdl24.zip.", path);
    }

    private static Dictionary<string, byte[]> ReadEntries(string archivePath)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        AddArchiveEntries(archivePath, entries);

        string directory = Path.GetDirectoryName(archivePath) ?? Environment.CurrentDirectory;
        foreach (string sibling in EnumerateSiblingSetArchives(directory, archivePath))
            AddArchiveEntries(sibling, entries);

        return entries;
    }

    private static void AddArchiveEntries(string archivePath, Dictionary<string, byte[]> entries)
    {
        using IArchive archive = RomArchiveExtractor.OpenArchive(archivePath);
        foreach (IArchiveEntry entry in archive.Entries)
        {
            if (entry.IsDirectory)
                continue;

            string name = Path.GetFileName(entry.Key);
            if (entries.ContainsKey(name))
                continue;

            using Stream entryStream = entry.OpenEntryStream();
            using var ms = new MemoryStream();
            entryStream.CopyTo(ms);
            entries[name] = ms.ToArray();
        }
    }

    private static IEnumerable<string> EnumerateSiblingSetArchives(string directory, string primaryArchivePath)
    {
        string primaryFullPath = Path.GetFullPath(primaryArchivePath);
        string[] candidates =
        {
            Path.Combine(directory, "gauntdl24.7z"),
            Path.Combine(directory, "gauntdl24.zip"),
            Path.Combine(directory, "gauntdl.zip")
        };

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate) &&
                !string.Equals(Path.GetFullPath(candidate), primaryFullPath, StringComparison.OrdinalIgnoreCase))
            {
                yield return candidate;
            }
        }
    }

    private static byte[] RequireEntry(Dictionary<string, byte[]> entries, string name, int expectedLength)
    {
        if (!entries.TryGetValue(name, out byte[]? data))
            throw new InvalidDataException($"Gauntlet Dark Legacy ROM archive is missing required entry '{name}'.");
        if (data.Length != expectedLength)
            throw new InvalidDataException($"ROM entry '{name}' has {data.Length} bytes; expected {expectedLength}.");
        return data;
    }

    private static string? ResolveChdPath(string archivePath, string setName)
    {
        string directory = Path.GetDirectoryName(archivePath) ?? Environment.CurrentDirectory;
        if (DiskBySet.TryGetValue(setName, out string? expected))
        {
            string expectedPath = Path.Combine(directory, expected);
            if (File.Exists(expectedPath))
                return expectedPath;
        }

        return Directory.EnumerateFiles(directory, "*.chd").FirstOrDefault();
    }
}

internal sealed class MipsR5000Core
{
    private readonly VegasMemoryMap _memory;
    private readonly ulong[] _gpr = new ulong[32];
    private readonly ulong[] _cp0 = new ulong[32];
    private readonly ulong[] _fpr = new ulong[32];
    private readonly uint[] _fcr = new uint[32];
    private readonly bool _traceEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_CPU") == "1";
    private readonly ulong? _tracePcMin = ParseOptionalHexUlong("EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MIN");
    private readonly ulong? _tracePcMax = ParseOptionalHexUlong("EUTHERDRIVE_GAUNTDL_TRACE_CPU_PC_MAX");
    private readonly ulong? _traceRa = ParseOptionalHexUlong("EUTHERDRIVE_GAUNTDL_TRACE_CPU_RA");
    private readonly int _traceInstructionLimit = ParsePositiveInt("EUTHERDRIVE_GAUNTDL_TRACE_CPU_LIMIT", int.MaxValue);
    private readonly bool _traceRuntimeLog = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_RUNTIME_LOG") == "1";
    private readonly int _stepBudget = ParseStepBudget();
    private readonly ulong _cp0CountStep = (ulong)ParsePositiveInt("EUTHERDRIVE_GAUNTDL_CP0_COUNT_STEP", 1024);
    private int _remainingProbeSteps;
    private int _probeStepDebt;
    private readonly bool _profileHotPcs = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_PROFILE_HOT_PCS") == "1";
    private readonly Dictionary<ulong, ulong> _hotPcCounts = [];
    private readonly bool _enableFdSlotHandleFastPath = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_FD_SLOT_HANDLE");
    private readonly bool _enableRd0AsyncCallbackKick = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_RD0_ASYNC_CALLBACK");
    private readonly bool _enableRd0SyncReadComplete = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_RD0_SYNC_READ_COMPLETE");
    private readonly bool _enableRd0HomeTableParse = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_RD0_HOME_TABLE");
    private readonly bool _enableRd0Stage4BootRead = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_RD0_STAGE4_BOOT_READ");
    private readonly bool _enableRd0BootHeaderRead = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_RD0_BOOT_HEADER_READ");
    private readonly bool _enableRd0BootFileRead = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_RD0_BOOT_FILE_READ");
    private readonly bool _enableBootableAddressCheck = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_BOOTABLE_ADDRESS_CHECK");
    private readonly bool _enableBootLoaderAddressBase = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_BOOT_LOADER_ADDRESS_BASE");
    private readonly bool _enableBootSerialCopyLoop = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_BOOT_SERIAL_COPY_LOOP");
    private readonly bool _enableBootCountDelay = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_BOOT_COUNT_DELAY");
    private readonly bool _enableFsysQioBringupRepair = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_FSYS_QIO_STATUS");
    private readonly bool _enableDcsBootCallbackRepair = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_DCS_BOOT_CALLBACK");
    private readonly bool _enableRuntimeInterruptBridge = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_RUNTIME_INTERRUPT_BRIDGE");
    private readonly bool _enableDiagnosticRuntimeFastPaths = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_FASTPATH_DIAGNOSTIC_RUNTIME") == "1";
    private readonly bool _traceRd0Home = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_RD0_HOME") == "1";
    private readonly ulong? _forceRd0OpenStatus = ParseOptionalHexUlong("EUTHERDRIVE_GAUNTDL_FORCE_RD0_OPEN_STATUS");
    private int _rd0AsyncCallbackKickCount;
    private int _rd0SyncReadCompleteCount;
    private int _rd0HomeTableParseCount;
    private int _rd0SecondGetIoQErrorTraceCount;
    private int _rd0SecondHomeReadReturnTraceCount;
    private int _rd0SecondOpenPollTraceCount;
    private int _rd0SecondUnableHomeBlocksTraceCount;
    private int _rd0HomeTableParsePcTraceCount;
    private int _rd0Stage4BootReadCount;
    private int _rd0Stage4BootReadTraceCount;
    private int _rd0BootHeaderReadCount;
    private int _rd0BootFileReadCount;
    private int _genericQioWaitTraceCount;
    private int _rd0QioCandidateTraceCount;
    private int _rd0OpenPollTraceCount;
    private int _loadedBootCacheLoopTraceCount;
    private int _loadedBootCacheLoopSkipTraceCount;
    private int _bootSerialCopyLoopTraceCount;
    private int _bootA420HandshakeTraceCount;
    private int _bootCountDelayTraceCount;
    private bool _hasRd0CallbackRaRestore;
    private ulong _rd0CallbackRestorePc;
    private ulong _rd0CallbackRestoreRa;
    private const ulong Cp0StatusWriteMask = 0xfffffffffe57ffffUL;
    private const ulong Cp0CauseSoftwareInterruptMask = 0x00000300UL;
    private const ulong Cp0StatusIe = 0x00000001UL;
    private const ulong Cp0StatusExl = 0x00000002UL;
    private const ulong Cp0StatusErl = 0x00000004UL;
    private const ulong Cp0StatusBev = 0x00400000UL;
    private const ulong Cp0StatusInterruptMask = 0x0000ff00UL;
    private const ulong Cp0CauseInterruptPendingMask = 0x0000ff00UL;
    private const ulong Cp0CauseExceptionCodeMask = 0x0000007cUL;
    private const ulong Cp0CauseTimerInterrupt = 0x00008000UL;
    private const ulong Cp0ConfigWriteMask = 0x0000003fUL;
    private bool _halted;
    private bool _hasPendingBranch;
    private ulong _pendingBranchTarget;
    private bool _hasImmediatePcOverride;
    private ulong _immediatePcOverride;
    private ulong _instructionCounter;
    private int _traceInstructionCount;
    private int _runtimeLogTraceCount;
    private int _runtimeTextCallCount;
    private ulong _lastRuntimeTextPc;
    private ulong _lastRuntimeTextRa;
    private bool _timerInterruptPending;
    private ulong _hi;
    private ulong _lo;

    public MipsR5000Core(VegasMemoryMap memory)
    {
        _memory = memory;
    }

    public ulong Pc { get; private set; }
    public uint LastFetchedInstruction { get; private set; }
    public ulong Cp0Status => _cp0[12];
    public ulong Cp0Cause => _cp0[13];
    public ulong Cp0Epc => _cp0[14];
    public ulong Cp0ErrorEpc => _cp0[30];
    public string LastRuntimeText { get; private set; } = "";
    public string RuntimeDiagnosticStatus
        => $"rtxt={_runtimeTextCallCount}@0x{_lastRuntimeTextPc:X8}/ra=0x{_lastRuntimeTextRa:X8}" +
           (string.IsNullOrWhiteSpace(LastRuntimeText) ? "" : $" \"{LastRuntimeText}\"");
    public string HotPcStatus => GetHotPcStatus();

    public void Reset()
    {
        Array.Clear(_gpr);
        Array.Clear(_cp0);
        Array.Clear(_fpr);
        Array.Clear(_fcr);
        _cp0[9] = 0; // Count
        _cp0[11] = 0xffffffff; // Compare
        _cp0[12] = 0x00400004; // Status: BEV | ERL after reset
        _cp0[15] = 0x00002300; // PRId: R5000
        _cp0[16] = 0x00026030; // Config: 32-byte cache lines, 2x system clock
        Pc = 0xffffffffbfc00000UL;
        LastFetchedInstruction = 0xffffffff;
        LastRuntimeText = "";
        _halted = false;
        _hasPendingBranch = false;
        _pendingBranchTarget = 0;
        _hasImmediatePcOverride = false;
        _immediatePcOverride = 0;
        _instructionCounter = 0;
        _traceInstructionCount = 0;
        _runtimeLogTraceCount = 0;
        _runtimeTextCallCount = 0;
        _lastRuntimeTextPc = 0;
        _lastRuntimeTextRa = 0;
        _timerInterruptPending = false;
        _hi = 0;
        _lo = 0;
    }

    public void RunProbeFrame()
        => RunProbeSteps(_stepBudget);

    public void RunProbeFrameAfterSteps(int completedSteps)
        => RunProbeSteps(Math.Max(0, _stepBudget - Math.Max(0, completedSteps)));

    public void RunProbeSteps(int stepCount)
    {
        if (_halted)
            return;

        for (int i = 0; i < stepCount && !_halted; i++)
        {
            _remainingProbeSteps = stepCount - i;
            Step();
            if (_probeStepDebt > 0)
            {
                int consumed = Math.Min(_probeStepDebt, stepCount - i - 1);
                _probeStepDebt -= consumed;
                i += consumed;
            }
        }

        _remainingProbeSteps = 0;
    }

    private void Step()
    {
        ulong pc = Pc;
        if (_profileHotPcs)
            CountHotPc(pc);
        _memory.SetTraceCpuPc(pc);
        if (TryFastPathKnownBootA420Handshake(pc))
            return;
        UpdateInterruptPendingBits();
        if (!_hasPendingBranch && TryEnterPendingInterrupt(pc))
            return;
        ApplyKnownRd0SyncReadCompletion(pc);
        ApplyKnownRd0HomeTableParse(pc);
        ApplyKnownRd0Stage4BootReadCompletion(pc);
        ApplyKnownRd0CallbackRaRestore(pc);
        TraceKnownRd0HomePc(pc);
        if (TryFastPathKnownGauntletGlideHotPath(pc))
            return;
        if (TryFastPathKnownRd0BootHeaderRead(pc))
            return;
        if (TryFastPathKnownRd0BootFileRead(pc))
            return;
        ApplyKnownBootLoaderAddressBase(pc);
        if (TryFastPathKnownBootableAddressCheck(pc))
            return;
        if (TryFastPathKnownBootSerialCopyLoop(pc))
            return;
        if (TryFastPathKnownBootSerialHandshake(pc))
            return;
        if (TryFastPathKnownBootA420Handshake(pc))
            return;
        if (TryFastPathKnownLoadedBootVectorSetupLoop(pc))
            return;
        if (TryFastPathKnownBootCountDelay(pc))
            return;
        if (TryFastPathKnownRuntimeCountDelay(pc))
            return;
        if (TryFastPathKnownRuntimeDelayCallback(pc))
            return;
        if (TryFastPathKnownRuntimeDelayCallbackLoop(pc))
            return;
        if (TryFastPathKnownRuntimeTickWaitLoop(pc))
            return;
        if (TryFastPathKnownRuntimeInlineTickWaitLoop(pc))
            return;
        if (TryFastPathKnownRuntimeQioErrorPollTail(pc))
            return;
        if (TryRepairKnownDcsBootCallbackWait(pc))
            return;
        if (TryRepairKnownRuntimeFsysQioStatus(pc))
            return;
        if (_enableDiagnosticRuntimeFastPaths && TryFastPathKnownRuntimeDiagnosticDrawEntry(pc))
            return;
        if (_enableDiagnosticRuntimeFastPaths && TryFastPathKnownRuntimeTextHexDrawWrapper(pc))
            return;
        if (_enableDiagnosticRuntimeFastPaths && TryFastPathKnownRuntimeTextDrawEntry(pc))
            return;
        if (TryFastPathKnownRuntimeTextStateBlitBody(pc))
            return;
        if (TryFastPathKnownGauntletGlideStateEmitCallerEpilogue(pc))
            return;
        if (TryFastPathKnownRuntimeFrameStateCallback(pc))
            return;
        if (TryFastPathKnownRuntimeAlignedQwordCopy(pc))
            return;
        if (TryFastPathKnownRuntimeDwordCopyTail(pc))
            return;
        if (_enableDiagnosticRuntimeFastPaths && TryFastPathKnownRuntimeInputPoll(pc))
            return;
        if (_enableDiagnosticRuntimeFastPaths && TryFastPathKnownRuntimeStatusBitfieldRead(pc))
            return;
        if (TryFastPathKnownRuntimeBitfieldUpdate(pc))
            return;
        if (TryFastPathKnownGauntletGlideRuntimeStateInitTail(pc))
            return;
        if (TryFastPathKnownGauntletGlideRuntimeTwoWordStateUpdate(pc))
            return;
        if (TryFastPathKnownGauntletGlideRuntimeStateSnapshotCopy(pc))
            return;
        if (TryFastPathKnownRuntimeCommandCompleteWait(pc))
            return;
        NormalizeKnownGlideFifoState(pc);
        if (TryFastPathKnownBootLoop(pc))
            return;
        if (TryFastPathKnownBiosRomCopyLoop(pc))
            return;
        if (TryFastPathKnownCacheLoop(pc))
            return;
        if (TryFastPathKnownBiosTextRoutine(pc))
            return;
        if (TryFastPathKnownBiosSerialChar(pc))
            return;
        if (TryFastPathKnownUartInitTable(pc))
            return;
        if (TryFastPathKnownNileInitTable(pc))
            return;
        if (TryFastPathKnownTlbClearLoop(pc))
            return;
        if (TryFastPathKnownTlbWriteHelper(pc))
            return;
        if (TryFastPathKnownFpgaSerialStream(pc))
            return;
        if (TryFastPathKnownFpgaLoadPreamble(pc))
            return;
        if (TryFastPathKnownFpgaLoadBlock(pc))
            return;
        if (TryFastPathKnownCountDelay(pc))
            return;
        if (TryFastPathKnownA180ReadyPoll(pc))
            return;
        if (TryFastPathKnownRamTest(pc))
            return;
        if (TryFastPathKnownRamQwordCopyBody(pc))
            return;
        if (TryFastPathKnownLoadedBootMemoryWalk(pc))
            return;
        if (TryFastPathKnownLoadedBootInflate(pc))
            return;
        if (TryFastPathKnownLoadedBootBssClear(pc))
            return;
        if (TryFastPathKnownRamQwordFill(pc))
            return;
        if (TryFastPathKnownRamNileTimerDelay(pc))
            return;
        if (TryFastPathKnownRamFrameTickWait(pc))
            return;
        if (TryFastPathKnownStdioInitErrorLoop(pc))
            return;
        if (TryFastPathKnownIoasicPicBitTestWait(pc))
            return;
        if (TryFastPathKnownGlideResolutionConfig(pc))
            return;
        if (TryFastPathKnownGlideQueryHardware(pc))
            return;
        if (TryFastPathKnownGlideSelect(pc))
            return;
        if (TryFastPathKnownGlideMapBoard(pc))
            return;
        if (TryFastPathKnownGlideWinOpenPostInit(pc))
            return;
        if (TryFastPathKnownGlideWinOpenPostMode(pc))
            return;
        if (TryFastPathKnownGlideWinOpenPostBuffer(pc))
            return;
        if (TryFastPathKnownGlideWinOpenPostAux(pc))
            return;
        if (TryFastPathKnownGlideWinOpenPostLfb(pc))
            return;
        if (TryFastPathKnownGlideWinOpenPostSwap(pc))
            return;
        if (TryFastPathKnownGlideWinOpenCallsiteReturn(pc))
            return;
        if (TryFastPathKnownGlideWinOpenPanic(pc))
            return;
        if (TryFastPathKnownGlideErrorReport(pc))
            return;
        if (TryFastPathKnownGauntletGlideTwoWordStatePacket(pc))
            return;
        if (TryFastPathKnownGlideFifoMakeRoom(pc))
            return;
        if (TryFastPathKnownGlideStatusCounterNegativeLimit(pc))
            return;
        if (TryFastPathKnownGlideLogWrite(pc))
            return;
        if (TryFastPathKnownGlideUiDispatchFromFrameLoop(pc))
            return;
        if (_enableDiagnosticRuntimeFastPaths && TryFastPathKnownRuntimeHexFormat(pc))
            return;
        if (_enableDiagnosticRuntimeFastPaths && TryFastPathKnownRuntimeStrlen(pc))
            return;
        if (TryFastPathKnownRuntimeCopyLoop(pc))
            return;
        if (TryFastPathKnownRuntimeDwordTouchLoop(pc))
            return;
        if (TryFastPathKnownRuntimeTableLookup(pc))
            return;
        if (TryFastPathKnownRuntimeEventPollWrapper(pc))
            return;
        if (TryFastPathKnownRuntimeFdSlotToHandle(pc))
            return;
        if (TryCompleteKnownRuntimeGenericQioWait(pc))
            return;
        if (TryCompleteKnownRuntimeRd0OpenPoll(pc))
            return;
        if (TryCompleteKnownRuntimeRd0FollowupPoll(pc))
            return;
        if (TryKickKnownRd0AsyncCallback(pc))
            return;
        ApplyKnownRd0OpenStatusProbe(pc);
        if (TryFastPathKnownRuntimeReadDelayHelper(pc))
            return;
        if (TryFastPathKnownRuntimeStatus3fSixPoll(pc))
            return;
        if (TryFastPathKnownRuntimeEventStatusNoCallback(pc))
            return;
        if (TryFastPathKnownRuntimeTileDepthPointerHelper(pc))
            return;
        if (TryFastPathKnownRuntimeTileDepthPointerCallsite(pc))
            return;
        if (TryFastPathKnownRuntimeTwoBitTileExpand(pc))
            return;
        if (TryFastPathKnownRuntimeTileOuterTail(pc))
            return;
        if (TryFastPathKnownGlideVertexCopyLoop(pc))
            return;
        if (TryFastPathKnownGlideSetupPacketHelper(pc))
            return;
        if (TryFastPathKnownGlideStateFlush(pc))
            return;
        if (TryFastPathKnownGlideTwoWordStatePacketTail(pc))
            return;
        if (TryFastPathKnownGlideBufferSwapPacketTail(pc))
            return;

        uint op = _memory.Read32(pc);
        LastFetchedInstruction = op;
        ulong nextPc = pc + 4;
        bool branchFromPreviousInstruction = _hasPendingBranch;
        ulong branchTarget = _pendingBranchTarget;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;

        TraceInstruction(pc, op);

        Execute(pc, op);
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep);
        _instructionCounter++;

        Pc = branchFromPreviousInstruction
            ? branchTarget
            : _hasImmediatePcOverride ? _immediatePcOverride : nextPc;
    }

    private bool TryFastPathKnownGauntletGlideHotPath(ulong pc)
    {
        return (pc & 0x1fffffffUL) switch
        {
            0x00019360UL => TryFastPathKnownGlideStatusCounterNegativeLimit(pc),
            0x000653d8UL => TryFastPathKnownGlideFifoMakeRoom(pc),
            >= 0x001097c0UL and <= 0x001098c0UL => TryFastPathKnownGlideFifoMakeRoom(pc),
            0x000511c8UL => TryFastPathKnownGlideTwoWordStatePacketTail(pc),
            0x000526acUL => TryFastPathKnownGlideStateFlush(pc),
            0x00052bc0UL => TryFastPathKnownGlideSetupPacketHelper(pc),
            0x00053340UL => TryFastPathKnownGlideBufferSwapPacketTail(pc),
            0x00102520UL or 0x0010253cUL or 0x00102554UL => TryFastPathKnownGauntletGlideTwoWordStatePacket(pc),
            0x00103f64UL or 0x00103f70UL or 0x00104068UL => TryFastPathKnownGlideStateFlush(pc),
            _ => false
        };
    }

    private bool TryFastPathKnownBootLoop(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x1fc038c4UL)
            return false;

        ulong cursor = _gpr[4];
        ulong end = _gpr[5];
        if (cursor >= end || end - cursor > 0x00100000UL)
            return false;

        ulong mask0 = _gpr[6];
        ulong mask1 = _gpr[7];
        ulong acc0 = _gpr[8];
        ulong acc1 = _gpr[9];

        while (cursor < end)
        {
            uint value = _memory.Read32(cursor);
            cursor += 4;
            acc0 = (acc0 + (value & mask0)) & mask0;
            acc1 = (acc1 + (value & mask1)) & mask1;
        }

        _gpr[2] = 0;
        _gpr[3] = 0;
        _gpr[4] = end;
        _gpr[8] = acc0;
        _gpr[9] = acc1;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep);
        _instructionCounter++;
        Pc = (pc & 0xffffffffe0000000UL) | 0x1fc038ecUL;
        return true;
    }

    private bool TryFastPathKnownBiosRomCopyLoop(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        if (offset is not (0x1fc00ee0UL or 0x1fc00ee4UL or 0x1fc00ee8UL or
                           0x1fc00f1cUL or 0x1fc00f20UL or 0x1fc00f24UL or 0x1fc00f28UL))
        {
            return false;
        }

        ulong loopBase = (pc & 0xffffffffe0000000UL) |
            (offset < 0x1fc00f00UL ? 0x1fc00ed8UL : 0x1fc00f18UL);
        ulong exit = loopBase + 0x18UL;
        if (_memory.Read32(loopBase) != 0x8c650000U ||
            _memory.Read32(loopBase + 0x04UL) != 0xac450000U ||
            _memory.Read32(loopBase + 0x08UL) != 0x24630004U ||
            _memory.Read32(loopBase + 0x0cUL) != 0x0064082bU ||
            _memory.Read32(loopBase + 0x10UL) != 0x1420fffbU ||
            _memory.Read32(loopBase + 0x14UL) != 0x24420004U)
        {
            return false;
        }

        ulong source = _gpr[3];
        ulong end = _gpr[4];
        ulong destination = _gpr[2];
        if (source >= end || ((source | end | destination) & 3UL) != 0)
            return false;

        ulong byteLength = end - source;
        if (byteLength > 0x00400000UL ||
            (source & 0x1fffffffUL) is < 0x1fc00000UL or > 0x1fc80000UL ||
            !IsMainRamRange(destination, byteLength))
        {
            return false;
        }

        for (ulong cursor = 0; cursor < byteLength; cursor += 4UL)
            _memory.Write32(destination + cursor, _memory.Read32(source + cursor));

        _gpr[1] = 0;
        _gpr[2] = destination + byteLength;
        _gpr[3] = end;
        _gpr[5] = _memory.Read32(end - 4UL);
        Pc = exit;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownCacheLoop(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        if (TryFastPathKnownLoadedBootCacheLoop(pc, offset))
            return true;

        if (offset == 0x1fc03980UL)
        {
            ulong returnAddress = _gpr[2];
            if ((returnAddress & 0x1fffffffUL) is < 0x1fc00000UL or > 0x1fc80000UL)
                return false;

            _gpr[2] = _cp0[12];
            _gpr[31] = returnAddress;
            CompleteFastPathStep();
            Pc = returnAddress;
            return true;
        }

        uint exitOffset = offset switch
        {
            0x1fc039c8UL or 0x1fc039d0UL or 0x1fc039d4UL => 0x1fc039dc,
            0x1fc039f0UL or 0x1fc039f8UL => 0x1fc03a04,
            0x1fc03a18UL or 0x1fc03a20UL => 0x1fc03a2c,
            0x1fc03a40UL or 0x1fc03a50UL or 0x1fc03a54UL => 0x1fc03a5c,
            0x1fc03a88UL or 0x1fc03a90UL or 0x1fc03a94UL => 0x1fc03a9c,
            0x1fc03ab0UL or 0x1fc03ab8UL or 0x1fc03abcUL => 0x1fc03ac4,
            0x1fc03ad8UL or 0x1fc03ae0UL or 0x1fc03ae4UL => 0x1fc03aec,
            _ => 0
        };

        if (exitOffset == 0)
            return false;

        ulong cursor = _gpr[4];
        ulong end = _gpr[5];
        if (cursor >= end || end - cursor > 0x00400000UL)
            return false;

        _gpr[4] = end;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep);
        _instructionCounter++;
        Pc = (pc & 0xffffffffe0000000UL) | exitOffset;
        return true;
    }

    private bool TryFastPathKnownLoadedBootCacheLoop(ulong pc, ulong offset)
    {
        if (TryFastPathKnownLoadedBootCacheLoop(
                pc,
                offset,
                loopOffsets: (0x00003ae0UL, 0x00003ae4UL, 0x00003ae8UL),
                loopBaseOffset: 0x00003ad8UL,
                exitOffset: 0x00003aecUL,
                expectedOps: (0xbc830000U, 0x008f2021U, 0x0085082bU, 0x1420fffcU)))
        {
            return true;
        }

        if (TryFastPathKnownLoadedBootCacheLoop(
                pc,
                offset,
                loopOffsets: (0x00013598UL, 0x0001359cUL, 0x000135a0UL),
                loopBaseOffset: 0x00013590UL,
                exitOffset: 0x000135a4UL,
                expectedOps: (0xbc800000U, 0x008c2021U, 0x0085082bU, 0x1420fffcU)))
        {
            return true;
        }

        if (TryFastPathKnownLoadedBootCacheLoop(
                pc,
                offset,
                loopOffsets: (0x000135c8UL, 0x000135ccUL, 0x000135d0UL),
                loopBaseOffset: 0x000135c0UL,
                exitOffset: 0x000135d4UL,
                expectedOps: (0xbc810000U, 0x008d2021U, 0x0085082bU, 0x1420fffcU)))
        {
            return true;
        }

        if (TryFastPathKnownLoadedBootCacheLoop(
                pc,
                offset,
                loopOffsets: (0x000135f8UL, 0x000135fcUL, 0x00013600UL),
                loopBaseOffset: 0x000135f0UL,
                exitOffset: 0x00013604UL,
                expectedOps: (0xbc830000U, 0x008f2021U, 0x0085082bU, 0x1420fffcU)))
        {
            return true;
        }

        if (TryFastPathKnownLoadedBootCacheLoop(
                pc,
                offset,
                loopOffsets: (0x000cc2ecUL, 0x000cc2f0UL, 0x000cc2f4UL),
                loopBaseOffset: 0x000cc2e4UL,
                exitOffset: 0x000cc2f8UL,
                expectedOps: (0xbc8b0000U, 0x008f2021U, 0x0085082bU, 0x1420fffcU)))
        {
            return true;
        }

        if (TryFastPathKnownLoadedBootCacheLoop(
                pc,
                offset,
                loopOffsets: (0x000cc29cUL, 0x000cc2a0UL, 0x000cc2a4UL),
                loopBaseOffset: 0x000cc294UL,
                exitOffset: 0x000cc2a8UL,
                expectedOps: (0xbc880000U, 0x008c2021U, 0x0085082bU, 0x1420fffcU)))
        {
            return true;
        }

        if (TryFastPathKnownLoadedBootCacheLoop(
                pc,
                offset,
                loopOffsets: (0x000cc2c4UL, 0x000cc2c8UL, 0x000cc2ccUL),
                loopBaseOffset: 0x000cc2bcUL,
                exitOffset: 0x000cc2d0UL,
                expectedOps: (0xbc890000U, 0x008d2021U, 0x0085082bU, 0x1420fffcU)))
        {
            return true;
        }

        if (TryFastPathKnownLoadedBootCacheLoop(
                pc,
                offset,
                loopOffsets: (0x000cc2e4UL, 0x000cc2e8UL, 0x000cc2ecUL),
                loopBaseOffset: 0x000cc2dcUL,
                exitOffset: 0x000cc2f0UL,
                expectedOps: (0xbc8b0000U, 0x008f2021U, 0x0085082bU, 0x1420fffcU)))
        {
            return true;
        }

        return TryFastPathKnownLoadedBootCacheLoop(
            pc,
            offset,
            loopOffsets: (0x000cc318UL, 0x000cc31cUL, 0x000cc324UL),
            loopBaseOffset: 0x000cc314UL,
            exitOffset: 0x000cc328UL,
            expectedOps: (0xbc900000U, 0x008c2021U, 0x0085082bU, 0x1420fffaU));
    }

    private bool TryFastPathKnownLoadedBootCacheLoop(
        ulong pc,
        ulong offset,
        (ulong Compare, ulong Branch, ulong DelaySlot) loopOffsets,
        ulong loopBaseOffset,
        ulong exitOffset,
        (uint Cache, uint Add, uint Compare, uint Branch) expectedOps)
    {
        if (offset != loopBaseOffset &&
            offset != loopBaseOffset + 0x04UL &&
            offset != loopOffsets.Compare &&
            offset != loopOffsets.Branch &&
            offset != loopOffsets.DelaySlot)
        {
            return false;
        }

        ulong prefix = pc & 0xffffffffe0000000UL;
        ulong loopBase = prefix | loopBaseOffset;
        uint op0 = _memory.Read32(loopBase);
        uint op4 = _memory.Read32(loopBase + 0x04UL);
        uint op8 = _memory.Read32(loopBase + 0x08UL);
        uint op12 = _memory.Read32(loopBase + 0x0cUL);
        if (op0 != expectedOps.Cache ||
            op4 != expectedOps.Add ||
            op8 != expectedOps.Compare ||
            op12 != expectedOps.Branch)
        {
            if (_traceRd0Home && _loadedBootCacheLoopSkipTraceCount++ < 4)
            {
                Console.WriteLine(
                    $"[GAUNTDL:BOOT] loaded-cache-loop-skip pc={pc:x16} " +
                    $"base={loopBase:x16} sig={op0:x8},{op4:x8},{op8:x8},{op12:x8}");
            }
            return false;
        }

        ulong cursor = _gpr[4];
        ulong end = _gpr[5];
        if (cursor >= end || end - cursor > 0x00400000UL)
            return false;

        _gpr[1] = 0;
        _gpr[4] = end;
        _gpr[0] = 0;
        Pc = prefix | exitOffset;
        CompleteFastPathStep();
        if (_traceRd0Home && _loadedBootCacheLoopTraceCount++ < 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] loaded-cache-loop pc={pc:x16} " +
                $"from={cursor:x16} to={end:x16} exit={Pc:x16} return={_gpr[31]:x16}");
        }
        return true;
    }

    private bool TryFastPathKnownBiosTextRoutine(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        return offset switch
        {
            0x1fc02c28UL => FastPathInlineBiosText(),
            0x1fc02c5cUL => FastPathPointerBiosText(),
            _ => false
        };
    }

    private bool TryFastPathKnownBiosSerialChar(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x1fc02b88UL)
            return false;

        ulong returnAddress = _gpr[31];
        if ((returnAddress & 0x1fffffffUL) is < 0x1fc00000UL or > 0x1fc80000UL)
            return false;
        if ((_gpr[4] & ~0xffUL) != 0)
            return false;

        Pc = returnAddress;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownFpgaLoadBlock(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x1fc02918UL)
            return false;

        ulong cursor = _gpr[5];
        ulong end = _gpr[6];
        if (cursor >= end || end - cursor > 0x00100000UL)
            return false;

        _gpr[5] = end;
        _gpr[10] = 8;
        if ((_gpr[27] & 0x1fffffffUL) is < 0x1fc00000UL or > 0x1fc80000UL)
            _gpr[27] = _gpr[31];
        _memory.MarkFpgaConfigDone();
        Pc = (pc & 0xffffffffe0000000UL) | 0x1fc02a04UL;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownFpgaLoadPreamble(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        if (offset is not (0x1fc027a0UL or 0x1fc027e0UL or 0x1fc02800UL or 0x1fc02818UL
            or 0x1fc02828UL or 0x1fc02880UL or 0x1fc028a0UL or 0x1fc028b0UL))
            return false;

        ulong source = offset == 0x1fc027a0UL ? _gpr[4] : _gpr[13];
        ulong length = offset == 0x1fc027a0UL ? _gpr[5] : _gpr[14];
        if (length == 0 || length > 0x00100000UL)
            return false;

        _gpr[5] = source;
        _gpr[6] = source + length;
        _gpr[10] = 0;
        _gpr[27] = _gpr[31];
        Pc = (pc & 0xffffffffe0000000UL) | 0x1fc02918UL;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownNileInitTable(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        if (offset is not (0x1fc01f08UL or 0x1fc01f10UL))
            return false;

        ulong cursor = _gpr[4];
        ulong tableOffset = cursor & 0x1fffffffUL;
        if (tableOffset is < 0x1fc01cc8UL or > 0x1fc01ee0UL)
            return false;

        for (int i = 0; i < 128; i++)
        {
            uint address = _memory.Read32(cursor);
            if (address == 0)
            {
                _gpr[4] = cursor;
                Pc = _gpr[31];
                CompleteFastPathStep();
                return true;
            }

            uint low = _memory.Read32(cursor + 4);
            uint high = _memory.Read32(cursor + 8);
            ulong value = (unchecked((ulong)(long)(int)low) & 0xffffffff00000000UL)
                | (unchecked((ulong)(long)(int)high) << 32);
            _memory.Write64(unchecked((ulong)(long)(int)address), value);
            cursor += 12;
        }

        return false;
    }

    private bool TryFastPathKnownUartInitTable(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        if (offset is not (0x1fc02b18UL or 0x1fc02b30UL or 0x1fc02b34UL or 0x1fc02b3cUL or 0x1fc02b40UL))
            return false;

        ulong cursor = offset == 0x1fc02b18UL
            ? (pc & 0xffffffffe0000000UL) | 0x1fc02ac0UL
            : _gpr[4];
        ulong tableOffset = cursor & 0x1fffffffUL;
        if (tableOffset is < 0x1fc02ac0UL or > 0x1fc02b10UL)
            return false;

        for (int i = 0; i < 32; i++)
        {
            uint address = _memory.Read32(cursor);
            uint value = _memory.Read32(cursor + 4);
            if (address == 0)
            {
                _gpr[2] = value;
                _gpr[4] = cursor;
                _gpr[5] = 0;
                Pc = _gpr[31];
                CompleteFastPathStep();
                return true;
            }

            _memory.Write32(unchecked((ulong)(long)(int)address), value);
            cursor += 8;
        }

        return false;
    }

    private bool TryFastPathKnownTlbWriteHelper(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x1fc041b8UL)
            return false;

        _cp0[12] &= ~1UL;
        _cp0[0] = _gpr[4] & 0x3fUL;
        _cp0[2] = 0;
        _cp0[3] = 0;
        Pc = _gpr[31];
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownTlbClearLoop(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        if (offset is not (0x1fc01c80UL or 0x1fc01ca4UL or 0x1fc01ca8UL or 0x1fc01cb0UL or 0x1fc01cb4UL))
            return false;

        ulong returnAddress = _gpr[16];
        if ((returnAddress & 0x1fffffffUL) is < 0x1fc00000UL or > 0x1fc80000UL)
            return false;

        ulong index = offset == 0x1fc01c80UL ? 0 : _gpr[20];
        if (index > 0x20UL)
            return false;

        _cp0[12] &= ~1UL;
        _cp0[0] = 0x1f;
        _cp0[2] = 0;
        _cp0[3] = 0;
        _gpr[1] = 0;
        _gpr[2] = _cp0[12];
        _gpr[4] = index >= 0x20UL ? 0x1fUL : index;
        _gpr[20] = 0x20;
        _gpr[31] = (pc & 0xffffffffe0000000UL) | 0x1fc01cb0UL;
        Pc = returnAddress;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownFpgaSerialStream(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x1fc01118UL)
            return false;

        ulong source = _gpr[19];
        ulong length = _gpr[20];
        ulong repeats = _gpr[7];
        ulong sourceOffset = source & 0x1fffffffUL;
        if (sourceOffset is < 0x1fc00000UL or > 0x1fc80000UL)
            return false;
        if (length == 0 || length > 0x00100000UL || repeats > 0x10000UL)
            return false;

        ulong end = source + length;
        _gpr[16] = end;
        _gpr[17] = end;
        _gpr[18] = 0x00000000a1600000UL;
        _gpr[7] = 0;
        _memory.MarkFpgaConfigDone();
        Pc = (pc & 0xffffffffe0000000UL) | 0x1fc00800UL;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownCountDelay(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        if (offset is not (0x1fc019c4UL or 0x1fc019c8UL or 0x1fc019ccUL or 0x1fc019d0UL
            or 0x1fc019d4UL or 0x1fc019d8UL or 0x1fc019dcUL or 0x1fc019e0UL
            or 0x1fc019e4UL or 0x1fc019e8UL or 0x1fc019ecUL
            or 0x1fc01a10UL or 0x1fc01a14UL or 0x1fc01a18UL or 0x1fc01a1cUL
            or 0x1fc01a20UL or 0x1fc01a24UL or 0x1fc01a28UL or 0x1fc01a30UL))
            return false;

        ulong returnAddress = _gpr[31];
        if ((returnAddress & 0x1fffffffUL) is < 0x1fc00000UL or > 0x1fc80000UL)
            return false;

        Pc = returnAddress;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownA180ReadyPoll(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        if (offset is not (0x1fc01804UL or 0x1fc0180cUL or 0x1fc01814UL or 0x1fc01820UL))
            return false;

        ulong counter = _gpr[11];
        if (counter == 0 || counter > 0x10000UL)
            return false;
        ulong returnAddress = _gpr[7];
        if ((returnAddress & 0x1fffffffUL) is < 0x1fc00000UL or > 0x1fc80000UL)
            return false;

        ulong pendingTarget = _pendingBranchTarget;
        _gpr[2] = 0;
        _gpr[11] = 0;
        Pc = returnAddress;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownRuntimeCountDelay(ulong pc)
    {
        ulong offset = pc & 0xffffffffUL;
        bool atEntry = offset == 0x80001a18UL;
        if (!atEntry &&
            offset is not (0x80001a24UL or 0x80001a28UL or 0x80001a2cUL or 0x80001a30UL or 0x80001a34UL))
        {
            return false;
        }
        const ulong entry = 0xffffffff80001a18UL;
        if (_memory.Read32(entry) != 0x00640019U ||
            _memory.Read32(entry + 0x04UL) != 0x00002012U ||
            _memory.Read32(entry + 0x08UL) != 0x40034800U ||
            _memory.Read32(entry + 0x0cUL) != 0x00000000U ||
            _memory.Read32(entry + 0x10UL) != 0x00621823U ||
            _memory.Read32(entry + 0x14UL) != 0x0064082bU ||
            _memory.Read32(entry + 0x18UL) != 0x5420fffcU ||
            _memory.Read32(entry + 0x1cUL) != 0x40034800U ||
            _memory.Read32(entry + 0x20UL) != 0x03e00008U)
        {
            return false;
        }

        ulong delay = atEntry
            ? ((ulong)(uint)_gpr[3] * (uint)_gpr[4]) & 0xffffffffUL
            : _gpr[4] & 0xffffffffUL;
        if (delay == 0 || delay > 0x10000000UL)
            return false;

        ulong returnAddress = _gpr[31];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x00001000UL or > 0x01000000UL)
            return false;

        uint start = (uint)_gpr[2];
        uint current = (uint)_cp0[9];
        ulong elapsed = unchecked(current - start);
        ulong remaining = elapsed >= delay ? 0UL : delay - elapsed;
        ulong skippedInstructions = Math.Max(1UL, remaining / Math.Max(1UL, _cp0CountStep));
        _gpr[1] = 0;
        _gpr[3] = (uint)(start + delay);
        _gpr[4] = delay;
        _gpr[0] = 0;
        AdvanceCp0Count(Math.Max(_cp0CountStep, remaining));
        _instructionCounter += skippedInstructions + (atEntry ? 4UL : 0UL);
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = CanonicalizeCodeAddress(returnAddress);
        return true;
    }

    private bool TryFastPathKnownRamTest(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        if (offset is not (0x1fc02468UL or 0x1fc01f80UL))
            return false;

        ulong start = _gpr[4];
        ulong end = _gpr[5];
        ulong returnAddress = _gpr[31];
        ulong segment = start & 0xffffffffe0000000UL;
        if (segment is not (0x0000000080000000UL or 0x00000000a0000000UL))
            return false;
        if (end < start || end - start > 0x02000000UL)
            return false;
        if ((returnAddress & 0x1fffffffUL) is < 0x1fc00000UL or > 0x1fc80000UL)
            return false;

        _gpr[2] = 0;
        Pc = returnAddress;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownRamQwordFill(ulong pc)
    {
        if (pc != 0xffffffff80005b18UL)
            return false;
        if (_memory.Read32(pc) != 0x2508ffffU ||
            _memory.Read32(pc + 4) != 0xfc850000U ||
            _memory.Read32(pc + 8) != 0x1d00fffdU ||
            _memory.Read32(pc + 12) != 0x24840008U)
            return false;

        ulong count = _gpr[8];
        if (count == 0 || unchecked((long)count) <= 0 || count > 0x00400000UL)
            return false;

        ulong start = _gpr[4];
        ulong byteLength = count * 8UL;
        if ((start & 7UL) != 0 || !IsMainRamRange(start, byteLength))
            return false;

        ulong cursor = start;
        ulong value = _gpr[5];
        for (ulong i = 0; i < count; i++)
        {
            _memory.Write64(cursor, value);
            cursor += 8;
        }

        _gpr[4] = cursor;
        _gpr[8] = 0;
        _gpr[0] = 0;
        AdvanceCp0Count(Math.Max(_cp0CountStep, count * 4UL * _cp0CountStep));
        _instructionCounter += count * 4UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = 0xffffffff80005b28UL;
        return true;
    }

    private bool TryFastPathKnownRamQwordCopyBody(ulong pc)
    {
        const ulong entry = 0xffffffff80005a10UL;
        const ulong tail = 0xffffffff800059a0UL;
        if (pc is < entry or > 0xffffffff80005a4cUL)
            return false;

        if (_memory.Read32(entry) != 0x000640c2U ||
            _memory.Read32(entry + 0x04UL) != 0x000848c0U ||
            _memory.Read32(entry + 0x08UL) != 0x00c93022U ||
            _memory.Read32(entry + 0x0cUL) != 0x1900ffdaU ||
            _memory.Read32(entry + 0x10UL) != 0x2508ffffU ||
            _memory.Read32(entry + 0x14UL) != 0xdca90000U ||
            _memory.Read32(entry + 0x18UL) != 0x24a50008U ||
            _memory.Read32(entry + 0x1cUL) != 0xfc890000U ||
            _memory.Read32(entry + 0x20UL) != 0x1d00fffbU ||
            _memory.Read32(entry + 0x24UL) != 0x24840008U ||
            _memory.Read32(entry + 0x28UL) != 0x1000ffd3U)
        {
            return false;
        }

        long remainingAfterCurrent = unchecked((long)_gpr[8]);
        if (remainingAfterCurrent < 0 || remainingAfterCurrent > 0x00100000L)
            return false;

        ulong qwords = (ulong)remainingAfterCurrent + 1UL;
        ulong byteLength = qwords * 8UL;
        ulong destination = _gpr[4];
        ulong source = _gpr[5];
        if ((destination & 7UL) != 0 ||
            (source & 7UL) != 0 ||
            !IsMainRamRange(destination, byteLength) ||
            !IsMainRamRange(source, byteLength))
        {
            return false;
        }

        ulong lastValue = 0;
        for (ulong offset = 0; offset < byteLength; offset += 8UL)
        {
            lastValue = _memory.Read64(source + offset);
            _memory.Write64(destination + offset, lastValue);
        }

        _gpr[4] = destination + byteLength;
        _gpr[5] = source + byteLength;
        _gpr[8] = 0;
        _gpr[9] = lastValue;
        Pc = tail;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownLoadedBootMemoryWalk(ulong pc)
    {
        const ulong loop = 0xffffffffa00f0880UL;
        if (pc is < loop or > 0xffffffffa00f08e4UL)
            return false;

        if (pc >= 0xffffffffa00f08d4UL &&
            _memory.Read32(loop + 0x38UL) == 0x0083082bU &&
            _memory.Read32(loop + 0x3cUL) == 0x14200003U &&
            _memory.Read32(loop + 0x54UL) == 0x24840004U &&
            _memory.Read32(loop + 0x58UL) == 0x24a50004U &&
            _memory.Read32(loop + 0x5cUL) == 0x24c6fffcU &&
            _memory.Read32(loop + 0x60UL) == 0x14c0fff2U &&
            unchecked((long)_gpr[6]) <= 0)
        {
            _memory.Write32(0xffffffffa0000034UL, (uint)_gpr[7]);
            Pc = _gpr[16];
            CompleteFastPathStep();
            return true;
        }

        if (_memory.Read32(loop) != 0x40086000U ||
            _memory.Read32(loop + 0x04UL) != 0x3c012000U ||
            _memory.Read32(loop + 0x08UL) != 0x00812025U ||
            _memory.Read32(loop + 0x0cUL) != 0x0080802dU ||
            _memory.Read32(loop + 0x10UL) != 0x2401fffeU ||
            _memory.Read32(loop + 0x14UL) != 0x01014024U ||
            _memory.Read32(loop + 0x18UL) != 0x40886000U ||
            _memory.Read32(loop + 0x1cUL) != 0x3c03a000U ||
            _memory.Read32(loop + 0x20UL) != 0x34630400U ||
            _memory.Read32(loop + 0x24UL) != 0x3c08a000U ||
            _memory.Read32(loop + 0x28UL) != 0x350807ffU ||
            _memory.Read32(loop + 0x2cUL) != 0x0104082bU ||
            _memory.Read32(loop + 0x60UL) != 0x14c0fff2U)
        {
            return false;
        }

        ulong remaining = _gpr[6];
        if (remaining == 0 || remaining > 0x01000000UL || (remaining & 3UL) != 0)
            return false;

        ulong destination = _gpr[4];
        ulong source = _gpr[5];
        ulong jumpTarget = _gpr[16];
        if (!IsMainRamRange(source, remaining))
            return false;

        for (ulong offset = 0; offset < remaining; offset += 4UL)
        {
            ulong target = destination + offset;
            uint target32 = (uint)target;
            if (target32 is >= 0xa0000400U and <= 0xa00007ffU)
                continue;
            if (!IsMainRamRange(target, 4UL))
                return false;

            _memory.Write32(target, _memory.Read32(source + offset));
        }

        _gpr[1] = 0;
        _gpr[2] = _memory.Read32(source + remaining - 4UL);
        _gpr[3] = 0x00000000a0000400UL;
        _gpr[4] = destination + remaining;
        _gpr[5] = source + remaining;
        _gpr[6] = 0;
        _gpr[8] = 0x00000000a00007ffUL;
        _memory.Write32(0xffffffffa0000034UL, (uint)_gpr[7]);
        Pc = jumpTarget;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownLoadedBootInflate(ulong pc)
    {
        if (pc is < 0xffffffff80042830UL or > 0xffffffff80042bd4UL)
            return false;

        if (_gpr[31] != 0xffffffff80041fc0UL)
            return false;
        if (_memory.Read32(0xffffffff8004b2b0UL) != 0x666e6920U ||
            _memory.Read32(0xffffffff8004b2b4UL) != 0x6574616cU ||
            _memory.Read32(0xffffffff80042830UL) != 0x27bdffe0U ||
            _memory.Read32(0xffffffff80042834UL) != 0x8fae0034U)
        {
            return false;
        }

        ulong stream = _gpr[19];
        if (!IsMainRamRange(stream, 0x38UL))
            return false;

        uint nextIn = _memory.Read32(stream + 0x00UL);
        uint availIn = _memory.Read32(stream + 0x04UL);
        uint totalIn = _memory.Read32(stream + 0x08UL);
        uint nextOut = _memory.Read32(stream + 0x0cUL);
        uint availOut = _memory.Read32(stream + 0x10UL);
        uint totalOut = _memory.Read32(stream + 0x14UL);
        uint state = _memory.Read32(stream + 0x1cUL);

        if (nextIn < totalIn || nextOut < totalOut)
            return false;

        uint sourceBase32 = nextIn - totalIn;
        uint destinationBase32 = nextOut - totalOut;
        uint sourceLength = totalIn + availIn;
        uint destinationCapacity = totalOut + availOut;
        ulong sourceBase = SignExtend32(sourceBase32);
        ulong destinationBase = SignExtend32(destinationBase32);
        if (state != 0x800f0870U ||
            sourceLength is 0 or > 0x00400000U ||
            destinationCapacity is 0 or > 0x01000000U ||
            !IsMainRamRange(sourceBase, sourceLength) ||
            !IsMainRamRange(destinationBase, destinationCapacity))
        {
            return false;
        }

        byte[] compressed = new byte[sourceLength];
        for (uint i = 0; i < sourceLength; i++)
            compressed[i] = _memory.Read8(sourceBase + i);

        byte[] output = new byte[destinationCapacity];
        int decoded;
        try
        {
            using MemoryStream input = new(compressed);
            using ZLibStream zlib = new(input, CompressionMode.Decompress);
            decoded = 0;
            while (decoded < output.Length)
            {
                int read = zlib.Read(output, decoded, output.Length - decoded);
                if (read == 0)
                    break;
                decoded += read;
            }
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (decoded == 0 || decoded > destinationCapacity)
            return false;

        for (int i = 0; i < decoded; i++)
            _memory.Write8(destinationBase + (uint)i, output[i]);

        _memory.Write32(stream + 0x00UL, sourceBase32 + sourceLength);
        _memory.Write32(stream + 0x04UL, 0);
        _memory.Write32(stream + 0x08UL, sourceLength);
        _memory.Write32(stream + 0x0cUL, destinationBase32 + (uint)decoded);
        _memory.Write32(stream + 0x10UL, destinationCapacity - (uint)decoded);
        _memory.Write32(stream + 0x14UL, (uint)decoded);

        _gpr[2] = 1;
        _gpr[3] = stream;
        _gpr[4] = sourceBase + sourceLength;
        _gpr[5] = destinationBase + (uint)decoded;
        _gpr[6] = 1;
        _gpr[7] = SignExtend32(destinationBase32);
        Pc = _gpr[31];
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownLoadedBootBssClear(ulong pc)
    {
        const ulong loop = 0xffffffff800105b4UL;
        if (pc is not (0xffffffff800105b4UL or 0xffffffff800105b8UL or
                       0xffffffff800105bcUL or 0xffffffff800105c0UL))
        {
            return false;
        }

        if (_memory.Read32(loop) != 0xac400000U ||
            _memory.Read32(loop + 0x04UL) != 0x24420004U ||
            _memory.Read32(loop + 0x08UL) != 0x0043082aU ||
            _memory.Read32(loop + 0x0cUL) != 0x1420fffdU ||
            _memory.Read32(loop + 0x10UL) != 0xac400000U)
        {
            return false;
        }

        ulong cursor = _gpr[2];
        ulong end = _gpr[3];
        if (cursor >= end || ((cursor | end) & 3UL) != 0 || !IsMainRamRange(cursor, end - cursor))
            return false;

        for (ulong address = cursor; address < end; address += 4UL)
            _memory.Write32(address, 0);

        _gpr[1] = 0;
        _gpr[2] = end;
        Pc = 0xffffffff800105c8UL;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownRamNileTimerDelay(ulong pc)
    {
        if (TryFastPathKnownRamCountDelay(pc))
            return true;

        if ((pc & 0xffffffffUL) != 0x8000468cUL)
            return false;
        if ((_gpr[17] & 0xffffffffUL) != 0xbfa001e0UL)
            return false;
        if (_memory.Read32(pc) != 0x8e300008U ||
            _memory.Read32(pc + 0x04UL) != 0x2e020065U ||
            _memory.Read32(pc + 0x08UL) != 0x1040fffdU ||
            _memory.Read32(pc + 0x0cUL) != 0x00000000U)
        {
            return false;
        }

        _gpr[16] = 0;
        _gpr[2] = 1;
        Pc = SignExtend32(0x8000469cU);
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownStdioInitErrorLoop(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x0004dac8UL)
            return false;
        if (_memory.Read32(pc - 8) != 0x12400003U ||
            _memory.Read32(pc) != 0x080136b2U ||
            _memory.Read32(pc + 4) != 0x00000000U)
            return false;
        if (_gpr[18] == 0)
            return false;

        _gpr[18] = 0;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep);
        _instructionCounter++;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = (pc & 0xffffffffe0000000UL) | 0x0004dad0UL;
        return true;
    }

    private bool TryFastPathKnownIoasicPicBitTestWait(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        if (offset is not (0x00040f2cUL or 0x00040f8cUL))
            return false;
        if (_memory.Read32(pc) != 0x8fa2002cU ||
            _memory.Read32(pc + 4) != 0x1040fffeU ||
            _memory.Read32(pc + 8) != 0x00000000U)
            return false;

        ulong sp = _gpr[29];
        ushort mask = _memory.Read16(sp + 0x12);
        _memory.Write16(sp + 0x30, (ushort)~mask);
        _memory.Write32(sp + 0x2c, 1);
        _memory.MarkIoasicUnlocked();
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep);
        _instructionCounter++;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = pc + 12;
        return true;
    }

    private bool TryFastPathKnownGlideResolutionConfig(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x00016774UL)
            return false;
        if (_memory.Read32(pc) != 0x27bdffe8U ||
            _memory.Read32(pc + 4) != 0xafb00010U ||
            _memory.Read32(pc + 8) != 0x00a0802dU ||
            _memory.Read32(pc + 12) != 0xafbf0014U)
            return false;
        if (!TryReadNullTerminatedAscii(_gpr[4], 64, out string name) ||
            !string.Equals(name, "SST_RESOLUTION", StringComparison.Ordinal))
        {
            return false;
        }

        _gpr[2] = ParseGlideConfigInt(Environment.GetEnvironmentVariable("SST_RESOLUTION"), _gpr[5]);
        Pc = _gpr[31];
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownGlideQueryHardware(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        bool knownQuery =
            offset == 0x00064c28UL &&
            _memory.Read32(pc) == 0x0080302dU &&
            _memory.Read32(pc + 4) == 0x3c02800bU &&
            _memory.Read32(pc + 8) == 0x24424d20U;
        knownQuery |=
            offset == 0x00108e84UL &&
            _memory.Read32(pc) == 0x27bdffd0U &&
            _memory.Read32(pc + 0x04UL) == 0xafbf002cU &&
            _memory.Read32(pc + 0x08UL) == 0xafbe0028U &&
            _memory.Read32(pc + 0x0cUL) == 0x03a0f02dU &&
            _memory.Read32(pc + 0x10UL) == 0xafc40030U &&
            _memory.Read32(pc + 0x14UL) == 0x3c028026U;
        if (!knownQuery)
            return false;

        ulong config = _gpr[4];
        _memory.Write32(config + 0x00, 1); // num_sst
        _memory.Write32(config + 0x04, 0xa8000000u); // mapped Voodoo register base
        _memory.Write32(config + 0x08, 4); // framebuffer RAM, MB
        _memory.Write32(config + 0x0c, 2); // FBI revision
        _memory.Write32(config + 0x10, 2); // two TMUs
        _memory.Write32(config + 0x14, 0); // no SLI
        _memory.Write32(config + 0x18, 2); // TMU0 revision
        _memory.Write32(config + 0x1c, 4); // TMU0 RAM, MB
        _memory.Write32(config + 0x20, 2); // TMU1 revision
        _memory.Write32(config + 0x24, 4); // TMU1 RAM, MB

        _gpr[2] = 1;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep);
        _instructionCounter++;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = _gpr[31];
        return true;
    }

    private bool TryFastPathKnownGlideSelect(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;

        if (offset == 0x00064cd0UL &&
            _memory.Read32(pc) == 0x27bdffe0U &&
            _memory.Read32(pc + 4) == 0x3c02800bU &&
            _memory.Read32(pc + 8) == 0xafb10014U &&
            _memory.Read32(pc + 12) == 0x24514d20U)
        {
            const ulong state = 0xffffffff800b4d20UL;
            _memory.Write32(state + 0x04, 0xa8000000u);
            _memory.Write32(state + 0x0c, 0x800b4e04u);
            _memory.Write32(state + 0x10, 0x00620000u);
            _memory.Write32(0xffffffff800b4e08UL, 0xa8000000u);
            _gpr[2] = 0x1c;
            Pc = _gpr[31];
            CompleteFastPathStep();
            return true;
        }

        if (offset != 0x0010a528UL ||
            _memory.Read32(pc) != 0x27bdffe0U ||
            _memory.Read32(pc + 0x04UL) != 0xafb20018U ||
            _memory.Read32(pc + 0x08UL) != 0x0080902dU ||
            _memory.Read32(pc + 0x0cUL) != 0xafb00010U ||
            _memory.Read32(pc + 0x10UL) != 0x3c108022U ||
            _memory.Read32(pc + 0x18UL) != 0x3c118023U ||
            _memory.Read32(pc + 0x1cUL) != 0x8e228134U ||
            _memory.Read32(pc + 0x20UL) != 0x8e04f9e4U ||
            _memory.Read32(pc + 0x28UL) != 0x8c420008U)
        {
            return false;
        }

        uint table = _memory.Read32(0xffffffff80228134UL);
        if ((table & 0xe0000000u) != 0x80000000u)
            return false;

        uint selected = _memory.Read32(0xffffffff00000000UL | ((ulong)table + 8UL));
        _memory.Write32(0xffffffff8021f9e4UL, selected);
        NormalizeGlideFifoState(0xffffffff80262d64UL);
        _gpr[2] = 1;
        Pc = _gpr[31];
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownGlideMapBoard(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x0005aaccUL)
            return false;
        if (_memory.Read32(pc) != 0x27bdffe8U ||
            _memory.Read32(pc + 4) != 0xafbf0014U ||
            _memory.Read32(pc + 8) != 0xafbe0010U ||
            _memory.Read32(pc + 12) != 0x03a0f02dU)
            return false;
        ulong mappedBase = _gpr[4] & 0xffffffffUL;
        if (mappedBase is not (0xa8000000UL or 0xa8000001UL))
            return false;

        _memory.Write32(0xffffffff800b4d24UL, 0xa8000000u);
        _memory.Write32(0xffffffff800b4e08UL, 0xa8000000u);
        if (mappedBase == 0xa8000001UL)
            _memory.Write32(0xffffffff800e6228UL, 0xa8000001u);
        _gpr[2] = 0xffffffff00000000UL | mappedBase;
        Pc = _gpr[31];
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownGlideWinOpenPostInit(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x00053f64UL)
            return false;
        if (_memory.Read32(pc) != 0x0040a02dU ||
            _memory.Read32(pc + 4) != 0x12800242U ||
            _memory.Read32(pc + 8) != 0x0280102dU)
            return false;
        if (_gpr[2] != 0 || _gpr[16] != 0xffffffffa8000000UL)
            return false;

        _gpr[2] = 1;
        _gpr[20] = 1;
        Pc = pc + 12;
        AdvanceCp0Count(_cp0CountStep * 3UL);
        _instructionCounter += 3;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        return true;
    }

    private bool TryFastPathKnownGlideWinOpenPostMode(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x00054064UL)
            return false;
        if (_memory.Read32(pc) != 0x0040a02dU ||
            _memory.Read32(pc + 4) != 0x56800007U ||
            _memory.Read32(pc + 12) != 0x56000005U)
            return false;
        if (_gpr[2] != 0 || _gpr[16] == 0)
            return false;

        _gpr[2] = 1;
        _gpr[20] = 1;
        Pc = pc + 4;
        AdvanceCp0Count(_cp0CountStep);
        _instructionCounter++;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        return true;
    }

    private bool TryFastPathKnownGlideWinOpenPostBuffer(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x000541ecUL)
            return false;
        if (_memory.Read32(pc) != 0x0040a02dU ||
            _memory.Read32(pc + 4) != 0x1280019fU ||
            _memory.Read32(pc + 8) != 0x24020001U)
            return false;
        if (_gpr[2] != 0 || _gpr[16] == 0)
            return false;

        _gpr[2] = 1;
        _gpr[20] = 1;
        Pc = pc + 12;
        AdvanceCp0Count(_cp0CountStep * 3UL);
        _instructionCounter += 3;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        return true;
    }

    private bool TryFastPathKnownGlideWinOpenPostAux(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x00054230UL)
            return false;
        if (_memory.Read32(pc) != 0x0040a02dU ||
            _memory.Read32(pc + 4) != 0x1280018eU ||
            _memory.Read32(pc + 8) != 0x3c020003U)
            return false;
        if (_gpr[2] != 0 || _gpr[18] == 0)
            return false;

        _gpr[2] = 1;
        _gpr[20] = 1;
        Pc = pc + 12;
        AdvanceCp0Count(_cp0CountStep * 3UL);
        _instructionCounter += 3;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        return true;
    }

    private bool TryFastPathKnownGlideWinOpenPostLfb(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x000543f0UL)
            return false;
        if (_memory.Read32(pc) != 0x0040a02dU ||
            _memory.Read32(pc + 4) != 0x1280011eU ||
            _memory.Read32(pc + 8) != 0x24050001U)
            return false;
        if (_gpr[2] != 0 || _gpr[18] == 0)
            return false;

        _gpr[2] = 1;
        _gpr[5] = 1;
        _gpr[20] = 1;
        Pc = pc + 12;
        AdvanceCp0Count(_cp0CountStep * 3UL);
        _instructionCounter += 3;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        return true;
    }

    private bool TryFastPathKnownGlideWinOpenPostSwap(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x00054424UL)
            return false;
        if (_memory.Read32(pc) != 0x0040a02dU ||
            _memory.Read32(pc + 4) != 0x12800111U ||
            _memory.Read32(pc + 8) != 0x0000302dU)
            return false;
        if (_gpr[2] != 0 || _gpr[18] == 0)
            return false;

        _gpr[2] = 1;
        _gpr[6] = 0;
        _gpr[20] = 1;
        _memory.Write32(0xffffffff800b4e08UL, 0xa8000000u);
        _memory.Write32(0xffffffff800e4e08UL, 0xa8000000u);
        _memory.Write32(0xffffffff800b5164UL, 0xa8200000u);
        _memory.Write32(0xffffffff800b5178UL, 0xa8200000u);
        _memory.Write32(0xffffffff800b517cUL, 0xa8200000u);
        _memory.Write32(0xffffffff800e5164UL, 0xa8200000u);
        _memory.Write32(0xffffffff800e5178UL, 0xa8200000u);
        _memory.Write32(0xffffffff800e517cUL, 0xa8200000u);
        Pc = pc + 12;
        AdvanceCp0Count(_cp0CountStep * 3UL);
        _instructionCounter += 3;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        return true;
    }

    private bool TryFastPathKnownGlideWinOpenCallsiteReturn(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x000e1b64UL)
            return false;
        if (_memory.Read32(pc) != 0x54400006U ||
            _memory.Read32(pc + 0x04UL) != 0x26b50001U ||
            _memory.Read32(pc + 0x08UL) != 0x3c048015U ||
            _memory.Read32(pc + 0x0cUL) != 0x248437d8U ||
            _memory.Read32(pc + 0x10UL) != 0x0c03852cU ||
            _memory.Read32(pc + 0x14UL) != 0x24050001U)
        {
            return false;
        }

        if (_gpr[2] != 0)
            return false;

        _gpr[2] = 1;
        _gpr[21] = (_gpr[21] + 1UL) & 0xffffffffUL;
        Pc = 0xffffffff800e1b80UL;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownGlideWinOpenPanic(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x000e14b0UL)
            return false;
        if (_memory.Read32(pc) != 0x0000000dU ||
            _memory.Read32(pc + 0x04UL) != 0x0803852cU ||
            _memory.Read32(pc + 0x08UL) != 0x00000000U)
        {
            return false;
        }
        if ((_gpr[31] & 0x1fffffffUL) != 0x000e1b70UL)
            return false;
        if (!TryReadNullTerminatedAscii(_gpr[4], 64, out string message) ||
            !string.Equals(message, "main: grSstWinOpen failed!", StringComparison.Ordinal))
        {
            return false;
        }

        _gpr[2] = 1;
        _gpr[21] = (_gpr[21] + 1UL) & 0xffffffffUL;
        Pc = 0xffffffff800e1b80UL;
        CompleteFastPathStep();
        return true;
    }

    private void NormalizeKnownGlideFifoState(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        if (offset is < 0x00065410UL or > 0x00065504UL)
        {
            if (offset is < 0x00109810UL or > 0x00109904UL)
                return;
            NormalizeGlideFifoState(0xffffffff80262d64UL);
            return;
        }
        if (_gpr[16] != 0xffffffff800b4e04UL && _gpr[6] != 0xffffffff800b4e04UL)
            return;

        NormalizeGlideFifoState(0xffffffff800b4e04UL);
    }

    private void NormalizeGlideFifoState(ulong state)
    {
        uint fifo = _memory.Read32(state + 0x374UL);
        if ((fifo & 3u) != 0 || fifo is < 0xa8200000u or >= 0xa8300000u)
            fifo = 0xa8200000u;

        _memory.Write32(state + 0x04UL, 0xa8000000u);
        _memory.Write32(state + 0x0cUL, (uint)(state & 0xffffffffUL));
        _memory.Write32(state + 0x10UL, 0x00620000u);
        _memory.Write32(0xffffffff800b5164UL, 0xa8200000u);
        _memory.Write32(0xffffffff800b5178UL, 0xa8200000u);
        _memory.Write32(0xffffffff800b517cUL, 0xa8200000u);
        _memory.Write32(state + 0x370UL, 0x18);
        _memory.Write32(state + 0x374UL, fifo);
        _memory.Write32(state + 0x378UL, 0xa8200000u);
        _memory.Write32(state + 0x37cUL, 0x00010000u);
        _memory.Write32(state + 0x380UL, 0x00010000u);
        _memory.Write32(state + 0x384UL, 0x00010000u);
    }

    private bool TryFastPathKnownGlideFifoMakeRoom(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        ulong state;
        if (offset == 0x000653d8UL &&
            _memory.Read32(pc) == 0x3c02800bU &&
            _memory.Read32(pc + 4) == 0x8c464d2cU &&
            _memory.Read32(pc + 8) == 0x0080c82dU &&
            _memory.Read32(pc + 12) == 0x8cc20384U)
        {
            state = 0xffffffff800b4e04UL;
        }
        else if (offset is >= 0x001097c0UL and <= 0x001098c0UL &&
                 _memory.Read32(0xffffffff801097c0UL) == 0x3c028026U &&
                 _memory.Read32(0xffffffff801097c4UL) == 0x8c462c8cU &&
                 _memory.Read32(0xffffffff801097c8UL) == 0x0080c82dU &&
                 _memory.Read32(0xffffffff801097ccUL) == 0x8cc20384U)
        {
            state = 0xffffffff80262d64UL;
        }
        else
        {
            return false;
        }
        if (_gpr[4] > 0x10000UL)
            return false;

        NormalizeGlideFifoState(state);
        _gpr[2] = 0x00010000UL;
        Pc = _gpr[31];
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownGlideStatusCounterNegativeLimit(ulong pc)
    {
        const ulong entry = 0xffffffff80019360UL;
        if (pc != entry)
            return false;
        if (_memory.Read32(entry + 0x00UL) != 0x8c820000U ||
            _memory.Read32(entry + 0x04UL) != 0x8fad0024U ||
            _memory.Read32(entry + 0x08UL) != 0x8faa0018U ||
            _memory.Read32(entry + 0x0cUL) != 0x8fab001cU ||
            _memory.Read32(entry + 0x10UL) != 0x8fac0020U ||
            _memory.Read32(entry + 0x14UL) != 0x00021302U ||
            _memory.Read32(entry + 0x18UL) != 0x8da32f00U ||
            _memory.Read32(entry + 0x1cUL) != 0x3042ffffU ||
            _memory.Read32(entry + 0x20UL) != 0x0043182bU ||
            _memory.Read32(entry + 0x24UL) != 0x54600001U ||
            _memory.Read32(entry + 0x2cUL) != 0x0182102aU ||
            _memory.Read32(entry + 0x30UL) != 0x1440000dU ||
            _memory.Read32(entry + 0x34UL) != 0x00000000U ||
            _memory.Read32(entry + 0x68UL) != 0x8fae0054U ||
            _memory.Read32(entry + 0x6cUL) != 0x31c20002U ||
            _memory.Read32(entry + 0x70UL) != 0x10400004U ||
            _memory.Read32(entry + 0x74UL) != 0x00111827U ||
            _memory.Read32(entry + 0x78UL) != 0x8e420000U ||
            _memory.Read32(entry + 0x7cUL) != 0x00431024U ||
            _memory.Read32(entry + 0x80UL) != 0xae420000U)
        {
            return false;
        }

        ulong sp = _gpr[29];
        if (!IsMainRamRange(sp + 0x18UL, 0x40UL))
            return false;

        // Current Voodoo status exposes the FIFO counter field as 0xffff, so
        // any lower signed limit takes the same branch through this poll block.
        ulong signedLimit = _gpr[12];
        if (unchecked((long)signedLimit) >= 0xffff)
            return false;

        ulong statusAddress = _gpr[4];
        ulong counterBase = SignExtend32(_memory.Read32(sp + 0x24UL));
        ulong maskTarget = _gpr[18];
        if (!IsMainRamRange(counterBase + 0x2f00UL, 4))
            return false;

        uint status = _memory.Read32(statusAddress);
        uint counter = (status >> 12) & 0xffffu;
        uint previous = _memory.Read32(counterBase + 0x2f00UL);
        if (counter < previous)
            _memory.Write32(counterBase + 0x2f00UL, counter);

        ulong t6 = SignExtend32(_memory.Read32(sp + 0x54UL));
        ulong invertedMask = ~_gpr[17];
        ulong result = t6 & 2UL;
        ulong skippedInstructions = 18UL;
        if (result != 0)
        {
            if (!IsMainRamRange(maskTarget, 4))
                return false;

            uint masked = _memory.Read32(maskTarget) & (uint)invertedMask;
            _memory.Write32(maskTarget, masked);
            result = SignExtend32(masked);
            skippedInstructions += 3UL;
        }

        _gpr[2] = result;
        _gpr[3] = invertedMask;
        _gpr[10] = SignExtend32(_memory.Read32(sp + 0x18UL));
        _gpr[11] = SignExtend32(_memory.Read32(sp + 0x1cUL));
        _gpr[12] = signedLimit;
        _gpr[13] = counterBase;
        _gpr[14] = t6;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * skippedInstructions);
        _instructionCounter += skippedInstructions;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = entry + 0x84UL;
        return true;
    }

    private bool TryFastPathKnownGlideErrorReport(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x0010a640UL)
            return false;
        if (_memory.Read32(pc) != 0x27bdffe8U ||
            _memory.Read32(pc + 0x04UL) != 0xafbf0010U ||
            _memory.Read32(pc + 0x08UL) != 0x10a00007U ||
            _memory.Read32(pc + 0x0cUL) != 0x0080102dU ||
            _memory.Read32(pc + 0x10UL) != 0x3c048016U ||
            _memory.Read32(pc + 0x14UL) != 0x24848474U ||
            _memory.Read32(pc + 0x18UL) != 0x0c042905U)
        {
            return false;
        }

        if (_gpr[5] == 0)
            return false;

        Pc = _gpr[31];
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownGlideLogWrite(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x0011ce40UL)
            return false;
        if (_memory.Read32(pc) != 0x27bdff98U ||
            _memory.Read32(pc + 0x04UL) != 0xafb20058U ||
            _memory.Read32(pc + 0x08UL) != 0x0080902dU ||
            _memory.Read32(pc + 0x0cUL) != 0xafb10054U ||
            _memory.Read32(pc + 0x10UL) != 0x00a0882dU ||
            _memory.Read32(pc + 0x18UL) != 0xafbf0060U)
        {
            return false;
        }

        ulong returnOffset = _gpr[31] & 0x1fffffffUL;
        if (returnOffset is < 0x00120000UL or > 0x00120240UL)
            return false;

        _gpr[2] = _gpr[4] & 0xffUL;
        Pc = _gpr[31];
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownGlideUiDispatchFromFrameLoop(ulong pc)
    {
        if (pc != 0xffffffff80019224UL)
            return false;
        if (_gpr[31] != 0xffffffff800195d4UL || _gpr[4] != 0 || (_gpr[5] & ~0x4UL) != 0)
            return false;
        if (_memory.Read32(pc) != 0x27bdffb0U ||
            _memory.Read32(pc + 4) != 0xafa40050U ||
            _memory.Read32(pc + 8) != 0x0000202dU ||
            _memory.Read32(pc + 52) != 0x0c006138U ||
            _memory.Read32(pc + 56) != 0xafa50054U ||
            _memory.Read32(pc + 60) != 0x0040582dU ||
            _memory.Read32(pc + 64) != 0x116000b0U ||
            _memory.Read32(pc + 72) != 0x8d730000U ||
            _memory.Read32(0xffffffff800195d4UL) != 0x24040001U)
        {
            return false;
        }

        _gpr[2] = 0;
        _gpr[4] = 0;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 512UL);
        _instructionCounter += 512UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = _gpr[31];
        return true;
    }

    private bool TryFastPathKnownRuntimeHexFormat(ulong pc)
    {
        const ulong entry = 0xffffffff800d0fa8UL;
        if (pc != entry)
            return false;
        if (_memory.Read32(entry + 0x00UL) != 0x00c0402dU ||
            _memory.Read32(entry + 0x04UL) != 0x00a61021U ||
            _memory.Read32(entry + 0x08UL) != 0xa0400000U ||
            _memory.Read32(entry + 0x0cUL) != 0x24030030U ||
            _memory.Read32(entry + 0x10UL) != 0x24020020U ||
            _memory.Read32(entry + 0x14UL) != 0x0067100aU ||
            _memory.Read32(entry + 0x18UL) != 0x0040382dU ||
            _memory.Read32(entry + 0x1cUL) != 0x3c028014U ||
            _memory.Read32(entry + 0x20UL) != 0x24496058U ||
            _memory.Read32(entry + 0x24UL) != 0x2508ffffU ||
            _memory.Read32(entry + 0x28UL) != 0x3082000fU ||
            _memory.Read32(entry + 0x2cUL) != 0x00042102U ||
            _memory.Read32(entry + 0x34UL) != 0x90430000U ||
            _memory.Read32(entry + 0x3cUL) != 0x19000003U ||
            _memory.Read32(entry + 0x40UL) != 0xa0430000U ||
            _memory.Read32(entry + 0x44UL) != 0x5480fff8U ||
            _memory.Read32(entry + 0x4cUL) != 0x19000005U ||
            _memory.Read32(entry + 0x50UL) != 0x00c83023U ||
            _memory.Read32(entry + 0x54UL) != 0x2508ffffU ||
            _memory.Read32(entry + 0x58UL) != 0x00a81021U ||
            _memory.Read32(entry + 0x5cUL) != 0x1d00fffdU ||
            _memory.Read32(entry + 0x60UL) != 0xa0470000U ||
            _memory.Read32(entry + 0x64UL) != 0x03e00008U ||
            _memory.Read32(entry + 0x68UL) != 0x00c0102dU)
        {
            return false;
        }

        uint value = (uint)_gpr[4];
        ulong destination = _gpr[5];
        int width = (int)_gpr[6];
        if (width < 0 || width > 64 || !IsMainRamRange(destination, (ulong)width + 1UL))
            return false;

        byte pad = _gpr[7] != 0 ? (byte)'0' : (byte)' ';
        Span<byte> output = stackalloc byte[64];
        for (int i = 0; i < width; i++)
            output[i] = pad;

        int cursor = width - 1;
        do
        {
            if (cursor < 0)
                break;
            output[cursor--] = (byte)(value < 10 ? '0' + value : 'A' + value - 10);
            value >>= 4;
        }
        while (value != 0);

        for (int i = 0; i < width; i++)
            _memory.Write8(destination + (uint)i, output[i]);
        _memory.Write8(destination + (uint)width, 0);

        _gpr[2] = (ulong)(uint)width;
        _gpr[4] = value;
        _gpr[8] = cursor < 0 ? 0UL : (ulong)(uint)cursor;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * (ulong)Math.Max(1, width + 8));
        _instructionCounter += (ulong)Math.Max(1, width + 8);
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = _gpr[31];
        return true;
    }

    private bool TryRepairKnownDcsBootCallbackWait(ulong pc)
    {
        if (!_enableDcsBootCallbackRepair)
            return false;

        ulong flagAddress;
        ulong resumePc;
        uint expectedSuccess;
        if (pc == 0xffffffff8004113cUL || pc == 0xffffffff80041140UL)
        {
            if (_memory.Read32(0xffffffff80041130UL) != 0x0c01049aU ||
                _memory.Read32(0xffffffff80041134UL) != 0xafa0002cU ||
                _memory.Read32(0xffffffff80041138UL) != 0x8fa20024U ||
                _memory.Read32(0xffffffff8004113cUL) != 0x1040fffeU ||
                _memory.Read32(0xffffffff80041144UL) != 0x8fa20024U ||
                _memory.Read32(0xffffffff80041148UL) != 0x1451ffeeU)
            {
                return false;
            }

            flagAddress = _gpr[29] + 0x24UL;
            resumePc = 0xffffffff80041144UL;
            expectedSuccess = 1;
        }
        else if (pc == 0xffffffff800411c4UL || pc == 0xffffffff800411c8UL)
        {
            if (_memory.Read32(0xffffffff800411b8UL) != 0x0c01049aU ||
                _memory.Read32(0xffffffff800411bcUL) != 0xafa00054U ||
                _memory.Read32(0xffffffff800411c0UL) != 0x8fa2004cU ||
                _memory.Read32(0xffffffff800411c4UL) != 0x1040fffeU ||
                _memory.Read32(0xffffffff800411ccUL) != 0x8fa2004cU ||
                _memory.Read32(0xffffffff800411d0UL) != 0x1452001eU)
            {
                return false;
            }

            flagAddress = _gpr[29] + 0x4cUL;
            resumePc = 0xffffffff800411ccUL;
            expectedSuccess = 1;
        }
        else
        {
            return false;
        }

        if (!IsMainRamRange(flagAddress, 4))
            return false;

        if (_memory.Read32(flagAddress) == 0)
            _memory.Write32(flagAddress, expectedSuccess);

        _gpr[2] = expectedSuccess;
        _gpr[0] = 0;
        Pc = resumePc;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownRuntimeCopyLoop(ulong pc)
    {
        const ulong byteSetup = 0xffffffff8003ce94UL;
        const ulong halfSetup = 0xffffffff8003cebcUL;
        const ulong halfBody = 0xffffffff8003ceccUL;
        const ulong halfDelay = 0xffffffff8003cee0UL;
        const ulong wordSetup = 0xffffffff8003ceecUL;
        const ulong wordBody = 0xffffffff8003cefcUL;
        const ulong wordDelay = 0xffffffff8003cf10UL;
        const ulong dwordSetup = 0xffffffff8003cf1cUL;
        const ulong dwordBody = 0xffffffff8003cf2cUL;
        const ulong dwordDelay = 0xffffffff8003cf40UL;

        if (pc is not (byteSetup or halfSetup or halfBody or halfDelay or wordSetup or wordBody or wordDelay or dwordSetup or dwordBody or dwordDelay))
            return false;
        if (!MatchesKnownRuntimeCopySignature())
            return false;

        return pc switch
        {
            byteSetup => TryFastPathKnownRuntimeByteCopy(),
            halfSetup => TryFastPathKnownRuntimeCopySetup(unitShift: 1),
            halfBody => TryFastPathKnownRuntimeCopyBody(unitSize: 2),
            halfDelay => TryFastPathKnownRuntimeCopyDelaySlot(unitSize: 2, halfBody),
            wordSetup => TryFastPathKnownRuntimeCopySetup(unitShift: 2),
            wordBody => TryFastPathKnownRuntimeCopyBody(unitSize: 4),
            wordDelay => TryFastPathKnownRuntimeCopyDelaySlot(unitSize: 4, wordBody),
            dwordSetup => TryFastPathKnownRuntimeCopySetup(unitShift: 3),
            dwordBody => TryFastPathKnownRuntimeCopyBody(unitSize: 8),
            dwordDelay => TryFastPathKnownRuntimeCopyDelaySlot(unitSize: 8, dwordBody),
            _ => false
        };
    }

    private bool MatchesKnownRuntimeCopySignature()
    {
        const ulong baseAddress = 0xffffffff8003ce94UL;
        return _memory.Read32(baseAddress + 0x00) == 0x00c0402dU &&
            _memory.Read32(baseAddress + 0x04) == 0x19000006U &&
            _memory.Read32(baseAddress + 0x08) == 0x2508ffffU &&
            _memory.Read32(baseAddress + 0x0c) == 0x90a90000U &&
            _memory.Read32(baseAddress + 0x14) == 0xa0890000U &&
            _memory.Read32(baseAddress + 0x20) == 0x03e00008U &&
            _memory.Read32(baseAddress + 0x28) == 0x00064042U &&
            _memory.Read32(baseAddress + 0x34) == 0x1900fff2U &&
            _memory.Read32(baseAddress + 0x38) == 0x2508ffffU &&
            _memory.Read32(baseAddress + 0x3c) == 0x94a90000U &&
            _memory.Read32(baseAddress + 0x44) == 0xa4890000U &&
            _memory.Read32(baseAddress + 0x50) == 0x1000ffebU &&
            _memory.Read32(baseAddress + 0x58) == 0x00064082U &&
            _memory.Read32(baseAddress + 0x64) == 0x1900ffe6U &&
            _memory.Read32(baseAddress + 0x68) == 0x2508ffffU &&
            _memory.Read32(baseAddress + 0x6c) == 0x8ca90000U &&
            _memory.Read32(baseAddress + 0x74) == 0xac890000U &&
            _memory.Read32(baseAddress + 0x80) == 0x1000ffdfU &&
            _memory.Read32(baseAddress + 0x88) == 0x000640c2U &&
            _memory.Read32(baseAddress + 0x94) == 0x1900ffdaU &&
            _memory.Read32(baseAddress + 0x98) == 0x2508ffffU &&
            _memory.Read32(baseAddress + 0x9c) == 0xdca90000U &&
            _memory.Read32(baseAddress + 0xa4) == 0xfc890000U;
    }

    private bool TryFastPathKnownRuntimeDwordTouchLoop(ulong pc)
    {
        const ulong delaySlot = 0xffffffff8003d028UL;
        const ulong loopTarget = 0xffffffff8003d01cUL;
        if (pc != delaySlot || !_hasPendingBranch || _pendingBranchTarget != loopTarget || (long)_gpr[8] <= 0)
            return false;

        if (_memory.Read32(loopTarget) != 0x2508ffffU ||
            _memory.Read32(loopTarget + 0x04UL) != 0xfc850000U ||
            _memory.Read32(loopTarget + 0x08UL) != 0x1d00fffdU ||
            _memory.Read32(loopTarget + 0x0cUL) != 0x24840008U ||
            _memory.Read32(loopTarget + 0x10UL) != 0x1000ffcfU)
        {
            return false;
        }

        ulong remaining = _gpr[8];
        _gpr[4] += (remaining + 1UL) * 8UL;
        _gpr[8] = 0;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * Math.Min(remaining * 3UL, 262_144UL));
        _instructionCounter += Math.Min(remaining * 3UL, 262_144UL);
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = loopTarget + 0x10UL;
        return true;
    }

    private bool TryFastPathKnownRuntimeStrlen(ulong pc)
    {
        const ulong entry = 0xffffffff8011fab8UL;
        if (pc != entry)
            return false;
        if (_memory.Read32(entry) != 0x80820000U ||
            _memory.Read32(entry + 0x04UL) != 0x10400005U ||
            _memory.Read32(entry + 0x08UL) != 0x0080182dU ||
            _memory.Read32(entry + 0x0cUL) != 0x24840001U ||
            _memory.Read32(entry + 0x10UL) != 0x80820000U ||
            _memory.Read32(entry + 0x14UL) != 0x5440fffeU ||
            _memory.Read32(entry + 0x18UL) != 0x24840001U ||
            _memory.Read32(entry + 0x1cUL) != 0x03e00008U ||
            _memory.Read32(entry + 0x20UL) != 0x00831023U)
        {
            return false;
        }

        ulong address = _gpr[4];
        int length = 0;
        for (; length < 4096; length++)
        {
            if (_memory.Read8(address + (uint)length) == 0)
                break;
        }
        if (length >= 4096)
            return false;

        _gpr[2] = (ulong)length;
        _gpr[3] = address;
        _gpr[4] = address + (uint)length;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * (ulong)Math.Max(1, length + 4));
        _instructionCounter += (ulong)Math.Max(1, length + 4);
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = _gpr[31];
        return true;
    }

    private bool TryFastPathKnownRuntimeByteCopy()
    {
        ulong returnAddress = _gpr[31];
        if (!IsRuntimeCopyReturnAddress(returnAddress))
            return false;

        long signedCount = unchecked((long)_gpr[6]);
        if (signedCount <= 0)
        {
            _gpr[8] = unchecked((ulong)(signedCount - 1));
            _gpr[2] = _gpr[7];
            FinishRuntimeCopyFastPath(returnAddress, 5UL);
            return true;
        }

        ulong count = (ulong)signedCount;
        if (!TryCopyRuntimeUnits(unitSize: 1, count, out ulong lastValue))
            return false;

        _gpr[4] += count;
        _gpr[5] += count;
        _gpr[8] = 0;
        _gpr[9] = lastValue;
        _gpr[2] = _gpr[7];
        FinishRuntimeCopyFastPath(returnAddress, count * 5UL + 5UL);
        return true;
    }

    private bool TryFastPathKnownRuntimeCopySetup(int unitShift)
    {
        uint byteCount = (uint)_gpr[6];
        ulong count = byteCount >> unitShift;
        if (count == 0)
            return false;

        int unitSize = 1 << unitShift;
        ulong copiedBytes = count * (ulong)unitSize;
        if (!TryCopyRuntimeUnits(unitSize, count, out ulong lastValue))
            return false;

        _gpr[4] += copiedBytes;
        _gpr[5] += copiedBytes;
        _gpr[6] = SignExtend32(byteCount - (uint)copiedBytes);
        _gpr[8] = 0;
        _gpr[9] = lastValue;
        FinishRuntimeCopyFastPath(0xffffffff8003ce94UL, count * 5UL + 6UL);
        return true;
    }

    private bool TryFastPathKnownRuntimeCopyBody(int unitSize)
    {
        if (_hasPendingBranch)
            return false;

        long signedCount = unchecked((long)_gpr[8]);
        if (signedCount <= 0)
            return false;

        ulong count = (ulong)signedCount;
        if (!TryCopyRuntimeUnits(unitSize, count, out ulong lastValue))
            return false;

        ulong copiedBytes = count * (ulong)unitSize;
        _gpr[4] += copiedBytes;
        _gpr[5] += copiedBytes;
        _gpr[8] = 0;
        _gpr[9] = lastValue;
        FinishRuntimeCopyFastPath(0xffffffff8003ce94UL, count * 5UL + 2UL);
        return true;
    }

    private bool TryFastPathKnownRuntimeCopyDelaySlot(int unitSize, ulong loopBody)
    {
        long signedCount = unchecked((long)_gpr[8]);
        if (_hasPendingBranch)
        {
            if (_pendingBranchTarget != loopBody || signedCount <= 0)
                return false;
        }
        else if (signedCount != 0)
        {
            return false;
        }

        _gpr[4] += (uint)unitSize;
        if (signedCount > 0)
        {
            ulong count = (ulong)signedCount;
            if (!TryCopyRuntimeUnits(unitSize, count, out ulong lastValue))
                return false;

            ulong copiedBytes = count * (ulong)unitSize;
            _gpr[4] += copiedBytes;
            _gpr[5] += copiedBytes;
            _gpr[9] = lastValue;
        }

        _gpr[8] = 0;
        FinishRuntimeCopyFastPath(0xffffffff8003ce94UL, (ulong)Math.Max(0L, signedCount) * 5UL + 3UL);
        return true;
    }

    private bool TryCopyRuntimeUnits(int unitSize, ulong count, out ulong lastValue)
    {
        lastValue = 0;
        if (count == 0 || count > 0x00100000UL)
            return false;

        ulong source = _gpr[5];
        ulong destination = _gpr[4];
        ulong byteLength = count * (ulong)unitSize;
        if (((source | destination) & (uint)(unitSize - 1)) != 0 ||
            !IsMainRamRange(source, byteLength) ||
            !IsMainRamRange(destination, byteLength))
        {
            return false;
        }

        for (ulong offset = 0; offset < byteLength; offset += (uint)unitSize)
        {
            lastValue = unitSize switch
            {
                1 => _memory.Read8(source + offset),
                2 => _memory.Read16(source + offset),
                4 => SignExtend32(_memory.Read32(source + offset)),
                8 => _memory.Read64(source + offset),
                _ => 0
            };

            switch (unitSize)
            {
                case 1:
                    _memory.Write8(destination + offset, (byte)lastValue);
                    break;
                case 2:
                    _memory.Write16(destination + offset, (ushort)lastValue);
                    break;
                case 4:
                    _memory.Write32(destination + offset, (uint)lastValue);
                    break;
                case 8:
                    _memory.Write64(destination + offset, lastValue);
                    break;
            }
        }

        return true;
    }

    private bool IsRuntimeCopyReturnAddress(ulong returnAddress)
    {
        ulong offset = returnAddress & 0x1fffffffUL;
        return (returnAddress & 0xffffffffe0000000UL) == 0xffffffff80000000UL &&
            offset is >= 0x00010000UL and <= 0x01000000UL;
    }

    private void FinishRuntimeCopyFastPath(ulong targetPc, ulong skippedInstructions)
    {
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * Math.Max(1UL, skippedInstructions));
        _instructionCounter += Math.Max(1UL, skippedInstructions);
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = targetPc;
    }

    private bool TryFastPathKnownRuntimeTableLookup(ulong pc)
    {
        const ulong entry = 0xffffffff8005d230UL;
        ulong offset = pc & 0x1fffffffUL;
        if (offset is < 0x0005d230UL or > 0x0005d344UL)
            return false;
        if (!MatchesKnownRuntimeTableLookupSignature(entry))
            return false;

        bool frameEntered = pc != entry;
        ulong argument;
        ulong framePointer = _gpr[30];
        if (frameEntered)
        {
            if (!IsMainRamRange(framePointer, 0x14))
                return false;

            argument = SignExtend32(_memory.Read32(framePointer + 0x10UL));
        }
        else
        {
            argument = SignExtend32((uint)_gpr[4]);
        }

        if (!TryKnownRuntimeTableLookup(argument, out uint found, out uint count))
            return false;

        _gpr[2] = found;
        if (frameEntered)
        {
            _gpr[30] = SignExtend32(_memory.Read32(framePointer + 8UL));
            _gpr[29] = framePointer + 0x10UL;
        }

        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * Math.Max(16UL, (ulong)count * 24UL));
        _instructionCounter += Math.Max(16UL, (ulong)count * 24UL);
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = _gpr[31];
        return true;
    }

    private bool TryKnownRuntimeTableLookup(ulong argument, out uint found, out uint count)
    {
        found = 0;
        count = _memory.Read32(0xffffffff800b2f24UL);
        if (count > 0x1000u)
            return false;

        const ulong table = 0xffffffff800b4c30UL;
        ulong tableBytes = count == 0 ? 1UL : (ulong)(count - 1) * 0xecUL + 0x18UL;
        if (!IsMainRamRange(table, tableBytes))
            return false;

        for (uint index = 0; index < count; index++)
        {
            ulong record = table + index * 0xecUL;
            if (SignExtend32(_memory.Read32(record + 4UL)) != argument)
                continue;

            _memory.Write32(0xffffffff800b2f34UL, _memory.Read32(record + 0x14UL));
            _memory.Write32(0xffffffff800b2f2cUL, (uint)record);
            found = 1;
            break;
        }

        return true;
    }

    private bool MatchesKnownRuntimeTableLookupSignature(ulong entry)
        => _memory.Read32(entry) == 0x27bdfff0U &&
           _memory.Read32(entry + 0x04) == 0xafbe0008U &&
           _memory.Read32(entry + 0x08) == 0x03a0f02dU &&
           _memory.Read32(entry + 0x0c) == 0xafc40010U &&
           _memory.Read32(entry + 0x10) == 0xafc00004U &&
           _memory.Read32(entry + 0x14) == 0xafc00000U &&
           _memory.Read32(entry + 0x18) == 0x8fc20000U &&
           _memory.Read32(entry + 0x20) == 0x8c632f24U &&
           _memory.Read32(entry + 0x24) == 0x0043102bU &&
           _memory.Read32(entry + 0x5c) == 0x8c634c34U &&
           _memory.Read32(entry + 0x60) == 0x8fc20010U &&
           _memory.Read32(entry + 0x64) == 0x1462001eU &&
           _memory.Read32(entry + 0x90) == 0x8c634c44U &&
           _memory.Read32(entry + 0x98) == 0xac232f34U &&
           _memory.Read32(entry + 0xbc) == 0x64634c30U &&
           _memory.Read32(entry + 0xc8) == 0xac222f2cU &&
           _memory.Read32(entry + 0xf4) == 0x8fc30004U &&
           _memory.Read32(entry + 0x104) == 0x03c0e82dU &&
           _memory.Read32(entry + 0x10c) == 0x27bd0010U &&
           _memory.Read32(entry + 0x110) == 0x03e00008U;

    private bool TryFastPathKnownRuntimeEventPollWrapper(ulong pc)
    {
        const ulong entry = 0xffffffff8005fab4UL;
        const ulong body = 0xffffffff8005fac0UL;
        const ulong afterLookup = 0xffffffff8005faf4UL;
        if (pc != entry && pc != body && pc != afterLookup)
            return false;
        if (!MatchesKnownRuntimeEventPollWrapperSignature(entry) ||
            !MatchesKnownRuntimeTableLookupSignature(0xffffffff8005d230UL))
        {
            return false;
        }

        uint returnValue;
        uint count = 0;
        ulong skippedInstructions = 12UL;
        ulong returnPc = _gpr[31];
        bool restoreFrame = pc != entry;
        ulong framePointer = 0;
        if (restoreFrame)
        {
            framePointer = pc == body ? _gpr[29] : _gpr[30];
            if (!IsMainRamRange(framePointer + 0x18UL, 8))
                return false;

            returnPc = SignExtend32(_memory.Read32(framePointer + 0x1cUL));
        }

        if (pc != afterLookup)
        {
            ulong argument = SignExtend32((uint)_gpr[4]);
            if (argument == 0)
            {
                returnValue = 0;
            }
            else
            {
                if (!TryKnownRuntimeTableLookup(argument, out uint found, out count))
                    return false;

                skippedInstructions += Math.Max(16UL, (ulong)count * 24UL);
                if (found == 0)
                    returnValue = 0;
                else if (!TryFinishKnownRuntimeEventPollWrapperEarlyReturn(out returnValue, ref skippedInstructions))
                    return false;
            }
        }
        else
        {
            skippedInstructions = 4UL;
            if (_gpr[2] == 0)
                returnValue = 0;
            else if (!TryFinishKnownRuntimeEventPollWrapperEarlyReturn(out returnValue, ref skippedInstructions))
                return false;
        }

        if (restoreFrame)
        {
            _gpr[31] = returnPc;
            _gpr[30] = SignExtend32(_memory.Read32(framePointer + 0x18UL));
            _gpr[29] = framePointer + 0x20UL;
        }

        _gpr[2] = returnValue;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * skippedInstructions);
        _instructionCounter += skippedInstructions;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = returnPc;
        return true;
    }

    private bool TryFinishKnownRuntimeEventPollWrapperEarlyReturn(out uint returnValue, ref ulong skippedInstructions)
    {
        ulong record = SignExtend32(_memory.Read32(0xffffffff800b2f2cUL));
        if (!IsMainRamRange(record + 0x58UL, 8))
        {
            returnValue = 0;
            return false;
        }

        uint pending = _memory.Read32(record + 0x58UL);
        _gpr[3] = SignExtend32(pending);
        if (pending == 0)
        {
            returnValue = 1;
            skippedInstructions += 16UL;
            return true;
        }

        uint busy = _memory.Read32(record + 0x5cUL);
        _gpr[3] = SignExtend32(busy);
        if (busy == 0)
        {
            returnValue = 0;
            return false;
        }

        returnValue = 1;
        skippedInstructions += 20UL;
        return true;
    }

    private bool TryFastPathKnownRuntimeFdSlotToHandle(ulong pc)
    {
        const ulong entry = 0xffffffff80020b54UL;
        const uint slotBase = 0x800a6170U;
        const uint slotLimit = slotBase + 0xc00U;
        const uint slotSize = 0x30U;

        if (!_enableFdSlotHandleFastPath)
            return false;
        if (pc != entry)
            return false;
        if (_gpr[31] != 0xffffffff80021850UL || _gpr[18] != 0xffffffff800e7810UL)
            return false;
        if (!MatchesKnownRuntimeFdSlotToHandleSignature(entry))
            return false;

        uint slot = (uint)_gpr[4];
        if (slot < slotBase || slot >= slotLimit)
        {
            _gpr[2] = SignExtend32(uint.MaxValue);
        }
        else
        {
            uint index = (slot - slotBase) / slotSize;
            uint generation = _memory.Read32(SignExtend32(slot) + 0x14UL);
            _gpr[2] = SignExtend32(index | generation);
        }

        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 12UL);
        _instructionCounter += 12UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = _gpr[31];
        return true;
    }

    private bool MatchesKnownRuntimeFdSlotToHandleSignature(ulong entry)
        => _memory.Read32(entry) == 0x10800008U &&
           _memory.Read32(entry + 0x04) == 0x3c02800aU &&
           _memory.Read32(entry + 0x08) == 0x24456170U &&
           _memory.Read32(entry + 0x0c) == 0x0085102bU &&
           _memory.Read32(entry + 0x10) == 0x14400004U &&
           _memory.Read32(entry + 0x14) == 0x24a20c00U &&
           _memory.Read32(entry + 0x18) == 0x0082102bU &&
           _memory.Read32(entry + 0x1c) == 0x14400003U &&
           _memory.Read32(entry + 0x20) == 0x3c03aaaaU &&
           _memory.Read32(entry + 0x24) == 0x03e00008U &&
           _memory.Read32(entry + 0x28) == 0x2402ffffU &&
           _memory.Read32(entry + 0x2c) == 0x3463aaabU &&
           _memory.Read32(entry + 0x30) == 0x00851023U &&
           _memory.Read32(entry + 0x34) == 0x00430018U &&
           _memory.Read32(entry + 0x38) == 0x8c830014U &&
           _memory.Read32(entry + 0x3c) == 0x00003012U &&
           _memory.Read32(entry + 0x40) == 0x00061103U &&
           _memory.Read32(entry + 0x44) == 0x03e00008U &&
           _memory.Read32(entry + 0x48) == 0x00431025U;

    private void ApplyKnownRd0OpenStatusProbe(ulong pc)
    {
        if (!_forceRd0OpenStatus.HasValue)
            return;
        bool isFirstOpenPoll = pc == 0xffffffff80015a2cUL && _gpr[22] == 0xffffffff800e7810UL;
        bool isFollowupOpenPoll = pc == 0xffffffff80022b88UL && _gpr[16] == 0xffffffff800e7810UL;
        if (!isFirstOpenPoll && !isFollowupOpenPoll)
            return;

        const ulong rd0Object = 0xffffffff800e7810UL;
        if (_memory.Read32(rd0Object + 0x0cUL) != 4 ||
            _memory.Read32(rd0Object + 0x14UL) != 0)
        {
            return;
        }

        _memory.Write32(rd0Object + 0x14UL, (uint)_forceRd0OpenStatus.Value);
    }

    private bool TryKickKnownRd0AsyncCallback(ulong pc)
    {
        if (!_enableRd0AsyncCallbackKick)
            return false;
        if (_rd0AsyncCallbackKickCount >= 8)
            return false;
        if (!TryGetKnownRuntimeQioPollObject(pc, out ulong objectAddress, out ulong returnPc))
            return false;

        if (_memory.Read32(objectAddress + 0x0cUL) != 4 ||
            _memory.Read32(objectAddress + 0x14UL) != 0)
            return false;

        uint objectPointer = (uint)objectAddress;
        TraceKnownRuntimeQioCandidates(pc, objectAddress);
        if (!TryFindKnownRuntimeQioCallback(objectAddress, objectPointer, out ulong qio, out ulong callback))
            return false;

        if (_traceRd0Home)
        {
            Console.WriteLine(
                $"[GAUNTDL:RD0] kick pc={pc:x16} object={objectAddress:x16} qio={qio:x16} " +
                $"cb={callback:x16} stage={_memory.Read32(qio + 0x24UL):x8} status={_memory.Read32(qio + 0x0cUL):x8} " +
                $"buf={_memory.Read32(qio + 0x2cUL):x8} arg={_memory.Read32(qio + 0x30UL):x8}");
        }

        _rd0AsyncCallbackKickCount++;
        _gpr[4] = qio;
        if (returnPc == 0xffffffff80022f18UL)
        {
            _hasRd0CallbackRaRestore = true;
            _rd0CallbackRestorePc = returnPc;
            _rd0CallbackRestoreRa = _gpr[31];
        }
        _gpr[31] = returnPc;
        _gpr[0] = 0;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = callback;
        return true;
    }

    private bool TryCompleteKnownRuntimeRd0OpenPoll(ulong pc)
    {
        const ulong branchPc = 0xffffffff80015a2cUL;
        const ulong rd0Object = 0xffffffff800e7810UL;
        if (pc != branchPc || _gpr[22] != rd0Object || _gpr[6] != 0)
            return false;

        uint openState = _memory.Read32(rd0Object + 0x0cUL);
        uint openStatus = _memory.Read32(rd0Object + 0x14UL);
        if (openState is not (4U or 7U) || openStatus != 0)
        {
            if (_traceRd0Home && _rd0OpenPollTraceCount++ < 8)
            {
                Console.WriteLine(
                    $"[GAUNTDL:RD0] rd0-open-poll-wait pc={pc:x16} state={openState:x8} " +
                    $"status={openStatus:x8}");
            }
            return false;
        }

        const uint successStatus = 0x3500U;
        _memory.Write32(rd0Object + 0x14UL, successStatus);
        _gpr[6] = successStatus;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 8UL);
        _instructionCounter += 8UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = branchPc + 0x08UL;
        if (_traceRd0Home)
            Console.WriteLine($"[GAUNTDL:RD0] rd0-open-poll-complete pc={pc:x16} state={openState:x8} status={successStatus:x8}");
        return true;
    }

    private bool TryCompleteKnownRuntimeGenericQioWait(ulong pc)
    {
        if (TryCompleteKnownRuntimeEarlyQioWait(pc))
            return true;

        const ulong loadPc = 0xffffffff80022aa8UL;
        if (pc != loadPc && pc != 0xffffffff80022aacUL && pc != 0xffffffff80022ab0UL)
            return false;
        if (_gpr[16] == 0 || !IsMainRamRange(_gpr[16], 0x18))
            return false;
        if (_memory.Read32(0xffffffff80022aa0UL) != 0x14400004U ||
            _memory.Read32(0xffffffff80022aa8UL) != 0x8e020014U ||
            _memory.Read32(0xffffffff80022aacUL) != 0x1040fffeU)
        {
            return false;
        }

        uint status = _memory.Read32(_gpr[16] + 0x14UL);
        if (status == 0)
        {
            status = 0x3500U;
            _memory.Write32(_gpr[16] + 0x14UL, status);
        }

        _gpr[2] = SignExtend32(status);
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 4UL);
        _instructionCounter += 4UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = loadPc + 0x0cUL;
        if (_traceRd0Home && _genericQioWaitTraceCount++ < 16)
        {
            ulong obj = _gpr[16];
            Console.WriteLine(
                $"[GAUNTDL:QIO] generic-wait-complete pc={pc:x16} object={obj:x16} " +
                $"state={_memory.Read32(obj + 0x0cUL):x8} status={status:x8} " +
                $"next={_memory.Read32(obj + 0x18UL):x8} buf={_memory.Read32(obj + 0x2cUL):x8}");
        }
        return true;
    }

    private bool TryCompleteKnownRuntimeEarlyQioWait(ulong pc)
    {
        const ulong loadPc = 0xffffffff8004dba8UL;
        if (pc != loadPc && pc != 0xffffffff8004dbacUL && pc != 0xffffffff8004dbb0UL)
            return false;
        if (_gpr[16] == 0 || !IsMainRamRange(_gpr[16], 0x40))
            return false;
        if (_memory.Read32(0xffffffff8004dba0UL) != 0x14400004U ||
            _memory.Read32(0xffffffff8004dba8UL) != 0x8e020014U ||
            _memory.Read32(0xffffffff8004dbacUL) != 0x1040fffeU)
        {
            return false;
        }

        uint handle = _memory.Read32(_gpr[16] + 0x0cUL);
        uint callback = _memory.Read32(_gpr[16] + 0x38UL);
        if (handle == uint.MaxValue ||
            callback is not (0x800508a8U or 0x8004fde4U))
        {
            return false;
        }

        uint status = _memory.Read32(_gpr[16] + 0x14UL);
        if (status == 0)
        {
            status = 0x3500U;
            _memory.Write32(_gpr[16] + 0x14UL, status);
        }

        _gpr[2] = SignExtend32(status);
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 4UL);
        _instructionCounter += 4UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = loadPc + 0x0cUL;
        if (_traceRd0Home)
            Console.WriteLine($"[GAUNTDL:QIO] early-wait-complete pc={pc:x16} object={_gpr[16]:x16} status={status:x8}");
        return true;
    }

    private bool TryCompleteKnownRuntimeRd0FollowupPoll(ulong pc)
    {
        const ulong loopLoadPc = 0xffffffff80022b88UL;
        const ulong rd0Object = 0xffffffff800e7810UL;
        if (pc != loopLoadPc || _gpr[16] != rd0Object)
            return false;

        uint openState = _memory.Read32(rd0Object + 0x0cUL);
        uint openStatus = _memory.Read32(rd0Object + 0x14UL);
        if (openState is not (4U or 7U) || openStatus != 0)
            return false;

        const uint successStatus = 0x3500U;
        _memory.Write32(rd0Object + 0x14UL, successStatus);
        _gpr[2] = successStatus;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 4UL);
        _instructionCounter += 4UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = loopLoadPc + 0x0cUL;
        if (_traceRd0Home)
            Console.WriteLine($"[GAUNTDL:RD0] rd0-followup-poll-complete pc={pc:x16} state={openState:x8} status={successStatus:x8}");
        return true;
    }

    private void ApplyKnownRd0CallbackRaRestore(ulong pc)
    {
        if (!_hasRd0CallbackRaRestore || pc != _rd0CallbackRestorePc || _gpr[31] != _rd0CallbackRestorePc)
            return;

        _gpr[31] = _rd0CallbackRestoreRa;
        _hasRd0CallbackRaRestore = false;
        if (_traceRd0Home)
            Console.WriteLine($"[GAUNTDL:RD0] restore-ra pc={pc:x16} ra={_gpr[31]:x16}");
    }

    private bool TryGetKnownRuntimeQioPollObject(ulong pc, out ulong objectAddress, out ulong returnPc)
    {
        objectAddress = 0;
        returnPc = 0;

        if ((pc == 0xffffffff80015a2cUL || pc == 0xffffffff80015a30UL) &&
            _gpr[22] != 0)
        {
            objectAddress = _gpr[22];
            returnPc = 0xffffffff80015a2cUL;
            return IsMainRamRange(objectAddress, 0x300);
        }

        if ((pc == 0xffffffff80022b88UL || pc == 0xffffffff80022b8cUL) &&
            _gpr[16] != 0)
        {
            objectAddress = _gpr[16];
            returnPc = 0xffffffff80022b88UL;
            return IsMainRamRange(objectAddress, 0x300);
        }

        if ((pc == 0xffffffff80022aa8UL ||
             pc == 0xffffffff80022aacUL ||
             pc == 0xffffffff80022ab0UL) &&
            _gpr[16] != 0)
        {
            objectAddress = _gpr[16];
            returnPc = 0xffffffff80022aa8UL;
            return IsMainRamRange(objectAddress, 0x300);
        }

        if ((pc == 0xffffffff80022f18UL ||
             pc == 0xffffffff80022f20UL ||
             pc == 0xffffffff80022f24UL) &&
            _gpr[16] != 0)
        {
            objectAddress = _gpr[16];
            returnPc = 0xffffffff80022f18UL;
            return IsMainRamRange(objectAddress, 0x300);
        }

        return false;
    }

    private void TraceKnownRd0HomePc(ulong pc)
    {
        if (!_traceRd0Home)
            return;

        string? label = pc switch
        {
            0xffffffff80015708UL => "fatal-print",
            0xffffffff800157c8UL => "first-open-error",
            0xffffffff80015804UL => "first-getioq-error",
            0xffffffff80015858UL => "first-no-valid-home-blocks",
            0xffffffff800159b8UL => "second-open-error",
            0xffffffff800159f8UL => "second-getioq-error",
            0xffffffff80015a20UL => "second-home-read-return",
            0xffffffff80015a2cUL => "second-open-poll",
            0xffffffff80015a48UL => "second-unable-get-home-blocks",
            0xffffffff80015a5cUL => "home-table-parse",
            0xffffffff80015aacUL => "home-block-version-mismatch",
            0xffffffff80015b38UL => "boot-slot-check",
            0xffffffff80015cb0UL => "no-boot-file",
            0xffffffff80015eacUL => "boot-open-error",
            _ => null
        };
        if (label is null)
            return;
        if (pc == 0xffffffff800159f8UL && _rd0SecondGetIoQErrorTraceCount++ >= 8)
            return;
        if (pc == 0xffffffff80015a20UL && _rd0SecondHomeReadReturnTraceCount++ >= 8)
            return;
        if (pc == 0xffffffff80015a2cUL && _rd0SecondOpenPollTraceCount++ >= 8)
            return;
        if (pc == 0xffffffff80015a48UL && _rd0SecondUnableHomeBlocksTraceCount++ >= 8)
            return;
        if (pc == 0xffffffff80015a5cUL && _rd0HomeTableParsePcTraceCount++ >= 8)
            return;

        string detail = pc == 0xffffffff80015708UL
            ? $" msg=\"{ReadAsciiTraceString(_gpr[5], 96)}\""
            : pc == 0xffffffff80015b38UL
                ? $" selected={_gpr[3]:x16}:{_memory.Read32(_gpr[3]):x8}" +
                  $" f00={_memory.Read32(_gpr[16]):x8}" +
                  $" f04={_memory.Read32(_gpr[16] + 0x04UL):x8}" +
                  $" f40={_memory.Read32(_gpr[16] + 0x40UL):x8}" +
                  $" f44={_memory.Read32(_gpr[16] + 0x44UL):x8}" +
                  $" f64={_memory.Read32(_gpr[16] + 0x64UL):x8}" +
                  $" slot0={_memory.Read32(_gpr[16] + 0x50UL):x8}" +
                  $" slot1={_memory.Read32(_gpr[16] + 0x68UL):x8}" +
                  $" slot2={_memory.Read32(_gpr[16] + 0x74UL):x8}" +
                  $" slot3={_memory.Read32(_gpr[16] + 0x80UL):x8}"
            : TraceKnownQioRecord(_gpr[5]) + TraceKnownHomeTable(_gpr[16]) + TraceKnownRd0Object(_gpr[22]);
        Console.WriteLine(
            $"[GAUNTDL:RD0] panic-site {label} pc={pc:x16} a0={_gpr[4]:x16} a1={_gpr[5]:x16} " +
            $"a2={_gpr[6]:x16} v0={_gpr[2]:x16} v1={_gpr[3]:x16} s0={_gpr[16]:x16} " +
            $"s1={_gpr[17]:x16} s2={_gpr[18]:x16} s3={_gpr[19]:x16} s4={_gpr[20]:x16} " +
            $"s6={_gpr[22]:x16} ra={_gpr[31]:x16}{detail}");
    }

    private string TraceKnownQioRecord(ulong address)
    {
        if (!IsMainRamRange(address, 0x34))
            return "";

        return $" qio08={_memory.Read32(address + 0x08UL):x8}" +
               $" qio0c={_memory.Read32(address + 0x0cUL):x8}" +
               $" qio1c={_memory.Read32(address + 0x1cUL):x8}" +
               $" qio20={_memory.Read32(address + 0x20UL):x8}" +
               $" qio24={_memory.Read32(address + 0x24UL):x8}" +
               $" qio2c={_memory.Read32(address + 0x2cUL):x8}" +
               $" qio30={_memory.Read32(address + 0x30UL):x8}";
    }

    private string TraceKnownHomeTable(ulong table)
    {
        if (!IsMainRamRange(table, 0x90))
            return "";

        return $" tbl04={_memory.Read32(table + 0x04UL):x8}" +
               $" tbl40={_memory.Read32(table + 0x40UL):x8}" +
               $" tbl44={_memory.Read32(table + 0x44UL):x8}" +
               $" tbl64={_memory.Read32(table + 0x64UL):x8}" +
               $" tbl50={_memory.Read32(table + 0x50UL):x8}" +
               $" tbl68={_memory.Read32(table + 0x68UL):x8}" +
               $" tbl74={_memory.Read32(table + 0x74UL):x8}" +
               $" tbl80={_memory.Read32(table + 0x80UL):x8}";
    }

    private string TraceKnownRd0Object(ulong address)
    {
        if (!IsMainRamRange(address, 0x30))
            return "";

        return $" obj0c={_memory.Read32(address + 0x0cUL):x8}" +
               $" obj14={_memory.Read32(address + 0x14UL):x8}" +
               $" obj18={_memory.Read32(address + 0x18UL):x8}" +
               $" obj20={_memory.Read32(address + 0x20UL):x8}" +
               $" obj24={_memory.Read32(address + 0x24UL):x8}" +
               $" obj2c={_memory.Read32(address + 0x2cUL):x8}";
    }

    private string ReadAsciiTraceString(ulong address, int maxLength)
    {
        if ((address & 0xffffffff00000000UL) == 0 && (address & 0x80000000UL) != 0)
            address = SignExtend32((uint)address);
        if (!IsMainRamRange(address, 1) && (address & 0xffffffffe0000000UL) != 0xffffffff80000000UL)
            return "";

        Span<char> buffer = maxLength <= 256 ? stackalloc char[maxLength] : new char[maxLength];
        int length = 0;
        for (; length < buffer.Length; length++)
        {
            byte value = _memory.Read8(address + (uint)length);
            if (value == 0)
                break;
            buffer[length] = value is >= 0x20 and <= 0x7e ? (char)value : '.';
        }

        return new string(buffer[..length]);
    }

    private void TraceKnownRuntimeQioCandidates(ulong pc, ulong objectAddress)
    {
        if (!_traceRd0Home)
            return;
        if (_rd0QioCandidateTraceCount++ >= 8)
            return;

        Console.WriteLine(
            $"[GAUNTDL:RD0] poll pc={pc:x16} object={objectAddress:x16} " +
            $"obj0c={_memory.Read32(objectAddress + 0x0cUL):x8} obj14={_memory.Read32(objectAddress + 0x14UL):x8}");

        for (ulong offset = 0x70; offset < 0x300; offset += 0x70)
        {
            ulong candidate = objectAddress + offset;
            if (!IsMainRamRange(candidate, 0x40))
                continue;

            Console.WriteLine(
                $"[GAUNTDL:RD0] qio+{offset:x3} cb={_memory.Read32(candidate + 0x1cUL):x8} " +
                $"owner={_memory.Read32(candidate + 0x20UL):x8} stage={_memory.Read32(candidate + 0x24UL):x8} " +
                $"status={_memory.Read32(candidate + 0x0cUL):x8} next={_memory.Read32(candidate + 0x08UL):x8} " +
                $"buf={_memory.Read32(candidate + 0x2cUL):x8} arg={_memory.Read32(candidate + 0x30UL):x8}");
        }
    }

    private bool TryFindKnownRuntimeQioCallback(
        ulong objectAddress,
        uint objectPointer,
        out ulong qio,
        out ulong callback)
    {
        qio = 0;
        callback = 0;

        for (ulong offset = 0x70; offset < 0x300; offset += 0x70)
        {
            ulong candidate = objectAddress + offset;
            if (!IsMainRamRange(candidate, 0x40))
                continue;
            uint stage = _memory.Read32(candidate + 0x24UL);
            if (_memory.Read32(candidate + 0x20UL) != objectPointer ||
                stage is 0 or >= 4)
            {
                continue;
            }

            uint callbackPointer = _memory.Read32(candidate + 0x1cUL);
            ulong callbackAddress = SignExtend32(callbackPointer);
            if (callbackPointer == 0x80029230U && MatchesKnownRd0OpenCallbackSignature(callbackAddress))
            {
                qio = candidate;
                callback = callbackAddress;
                return true;
            }
        }

        for (ulong offset = 0x70; offset < 0x300; offset += 0x70)
        {
            ulong candidate = objectAddress + offset;
            if (!IsMainRamRange(candidate, 0x40))
                continue;
            if (_memory.Read32(candidate + 0x20UL) != objectPointer)
                continue;

            uint callbackPointer = _memory.Read32(candidate + 0x1cUL);
            ulong callbackAddress = SignExtend32(callbackPointer);
            if (callbackPointer == 0x800325a0U && MatchesKnownRd0FinalCallbackSignature(callbackAddress))
            {
                qio = candidate;
                callback = callbackAddress;
                return true;
            }
        }

        return false;
    }

    private bool MatchesKnownRd0OpenCallbackSignature(ulong entry)
        => _memory.Read32(entry) == 0x27bdffc8U &&
           _memory.Read32(entry + 0x04) == 0xafb10014U &&
           _memory.Read32(entry + 0x08) == 0x0080882dU &&
           _memory.Read32(entry + 0x0c) == 0x3c028009U &&
           _memory.Read32(entry + 0x48) == 0xafb00010U &&
           _memory.Read32(entry + 0x4c) == 0x8e320020U &&
           _memory.Read32(entry + 0x60) == 0xae200014U &&
           _memory.Read32(entry + 0xf0) == 0x0800a4a5U &&
           _memory.Read32(entry + 0xf8) == 0x24020002U &&
           _memory.Read32(entry + 0x100) == 0xae220024U;

    private bool MatchesKnownRd0FinalCallbackSignature(ulong entry)
        => _memory.Read32(entry) == 0x27bdffe8U &&
           _memory.Read32(entry + 0x04) == 0xafbf0014U &&
           _memory.Read32(entry + 0x08) == 0xafb00010U &&
           _memory.Read32(entry + 0x0c) == 0x8c900020U &&
           _memory.Read32(entry + 0x10) == 0x8c820018U &&
           _memory.Read32(entry + 0x20) == 0x0c0082bfU &&
           _memory.Read32(entry + 0x28) == 0x10400005U &&
           _memory.Read32(entry + 0x40) == 0x2402ffffU &&
           _memory.Read32(entry + 0x44) == 0xac82000cU &&
           _memory.Read32(entry + 0x48) == 0x24023500U &&
           _memory.Read32(entry + 0x4c) == 0xac820014U;

    private void ApplyKnownRd0SyncReadCompletion(ulong pc)
    {
        const ulong readReturnPc = 0xffffffff80029350UL;
        const ulong rd0Object = 0xffffffff800e7810UL;
        const ulong rd0Child = 0xffffffff800e7880UL;
        const ulong homeSectorBuffer = 0xffffffff800f41e0UL;
        const uint successStatus = 0x3500U;

        if (!_enableRd0SyncReadComplete || _rd0SyncReadCompleteCount != 0 || pc != readReturnPc)
            return;
        if (_gpr[2] != 0 ||
            _gpr[17] != rd0Child ||
            _gpr[18] != rd0Object ||
            _gpr[31] != readReturnPc)
        {
            return;
        }
        if (_memory.Read32(rd0Object + 0x14UL) != 0 ||
            _memory.Read32(rd0Child + 0x24UL) != 2 ||
            _memory.Read32(homeSectorBuffer) != 0xfeedf00dU)
        {
            return;
        }

        _rd0SyncReadCompleteCount++;
        _gpr[2] = SignExtend32(successStatus);
    }

    private void ApplyKnownRd0HomeTableParse(ulong pc)
    {
        const ulong homeReadReturnPc = 0xffffffff80015a20UL;
        const ulong tableCheckPc = 0xffffffff80015a5cUL;
        const ulong bootSlotCheckPc = 0xffffffff80015b38UL;
        const ulong homeSectorBuffer = 0xffffffff800f41e0UL;

        if (!_enableRd0HomeTableParse ||
            (pc != homeReadReturnPc && pc != tableCheckPc && pc != bootSlotCheckPc))
        {
            return;
        }

        ulong table = _gpr[16];
        if (!IsMainRamRange(table, 0x90))
        {
            return;
        }

        if (_memory.Read32(homeSectorBuffer) != 0xfeedf00dU ||
            _memory.Read32(homeSectorBuffer + 0x38UL) != 0xfe1dfaedU)
        {
            // Vegas keeps redundant home sectors; Gauntlet's boot path reads the primary copy at LBA 1.
            _memory.TryReadDiskSectorToMemory(1, homeSectorBuffer, 512, out _, out _);
        }

        if (_memory.Read32(homeSectorBuffer) != 0xfeedf00dU ||
            _memory.Read32(homeSectorBuffer + 0x38UL) != 0xfe1dfaedU)
        {
            return;
        }

        uint candidate0 = _memory.Read32(homeSectorBuffer + 0x48UL);
        uint candidate1 = _memory.Read32(homeSectorBuffer + 0x4cUL);
        uint candidate2 = _memory.Read32(homeSectorBuffer + 0x50UL);
        if ((candidate0 | candidate1 | candidate2) == 0)
            return;

        _memory.Write16(table + 0x04UL, 2);
        _memory.Write16(table + 0x06UL, 1);
        _memory.Write32(table + 0x64UL, 1);
        foreach (ulong slotOffset in new[] { 0x50UL, 0x68UL, 0x74UL, 0x80UL })
        {
            _memory.Write32(table + slotOffset, candidate0);
            _memory.Write32(table + slotOffset + 4UL, candidate1);
            _memory.Write32(table + slotOffset + 8UL, candidate2);
        }

        _rd0HomeTableParseCount++;
        if (pc == homeReadReturnPc && _gpr[2] == 0x300bUL)
        {
            _gpr[2] = 0;
            if (IsMainRamRange(_gpr[22], 0x18) && _memory.Read32(_gpr[22] + 0x14UL) == 0x300bU)
                _memory.Write32(_gpr[22] + 0x14UL, 0x3500U);
        }
        if (_traceRd0Home && _rd0HomeTableParseCount <= 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:RD0] home-table pc={pc:x16} table={table:x16} " +
                $"bootCandidates={candidate0:x8},{candidate1:x8},{candidate2:x8}");
        }
    }

    private void ApplyKnownRd0Stage4BootReadCompletion(ulong pc)
    {
        const ulong waitPc0 = 0xffffffff80022f18UL;
        const ulong waitPc1 = 0xffffffff80022f20UL;
        const ulong waitPc2 = 0xffffffff80022f24UL;

        if (!_enableRd0Stage4BootRead || _rd0Stage4BootReadCount != 0 ||
            (pc != waitPc0 && pc != waitPc1 && pc != waitPc2))
        {
            return;
        }

        if (_gpr[16] != 0xffffffff800e7810UL)
            return;

        if (_memory.TryCompleteKnownRd0Stage4BootRead(
                out uint lba,
                out ulong destination,
                out uint firstWord,
                out string stage4Reason))
        {
            _rd0Stage4BootReadCount++;
            if (_traceRd0Home)
            {
                Console.WriteLine(
                    $"[GAUNTDL:RD0] stage4-boot-read pc={pc:x16} " +
                $"lba={lba:x8} dest={destination:x16} first={firstWord:x8}");
            }
        }
        else if (_traceRd0Home && _rd0Stage4BootReadTraceCount++ < 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:RD0] stage4-boot-read-skip pc={pc:x16} " +
                $"reason={stage4Reason}");
        }
    }

    private bool TryFastPathKnownRd0BootHeaderRead(ulong pc)
    {
        const ulong entry = 0xffffffff80022fb0UL;
        const ulong parserReturnPc = 0xffffffff80015ba4UL;
        const ulong rd0Object = 0xffffffff800e7810UL;

        if (!_enableRd0BootHeaderRead || pc != entry || _gpr[31] != parserReturnPc)
            return false;
        if (_gpr[4] != rd0Object || _gpr[7] != 0x200UL)
            return false;

        uint lba = (uint)_gpr[5];
        ulong destination = _gpr[6];
        if (!_memory.TryReadDiskSectorToMemory(lba, destination, (uint)_gpr[7], out uint firstWord, out string reason))
        {
            if (_traceRd0Home && _rd0BootHeaderReadCount < 8)
            {
                Console.WriteLine(
                    $"[GAUNTDL:RD0] boot-header-read-skip pc={pc:x16} " +
                    $"lba={lba:x8} dest={destination:x16} reason={reason}");
            }
            return false;
        }

        _rd0BootHeaderReadCount++;
        if (_gpr[17] == 0x00000000c0edbabeUL)
            _gpr[17] = 0xffffffffc0edbabeUL;
        _gpr[2] = 0;
        Pc = _gpr[31];
        CompleteFastPathStep();
        if (_traceRd0Home)
        {
            Console.WriteLine(
                $"[GAUNTDL:RD0] boot-header-read pc={pc:x16} " +
                $"lba={lba:x8} dest={destination:x16} first={firstWord:x8}");
        }
        return true;
    }

    private bool TryFastPathKnownRd0BootFileRead(ulong pc)
    {
        const ulong entry = 0xffffffff80022fb0UL;
        const ulong parserReturnPc = 0xffffffff80015cbcUL;
        const ulong rd0Object = 0xffffffff800e7810UL;

        if (!_enableRd0BootFileRead || pc != entry || _gpr[31] != parserReturnPc)
            return false;
        if (_gpr[4] != rd0Object || _gpr[5] == 0 || _gpr[7] == 0 || _gpr[7] > uint.MaxValue)
            return false;

        uint lba = (uint)_gpr[5];
        ulong destination = _gpr[6];
        uint byteCount = (uint)_gpr[7];
        if (!_memory.TryReadDiskBytesToMemory(lba, destination, byteCount, out uint firstWord, out string reason))
        {
            if (_traceRd0Home && _rd0BootFileReadCount < 8)
            {
                Console.WriteLine(
                    $"[GAUNTDL:RD0] boot-file-read-skip pc={pc:x16} " +
                    $"lba={lba:x8} dest={destination:x16} bytes={byteCount:x8} reason={reason}");
            }
            return false;
        }

        _rd0BootFileReadCount++;
        _gpr[2] = 0;
        Pc = _gpr[31];
        CompleteFastPathStep();
        if (_traceRd0Home)
        {
            Console.WriteLine(
                $"[GAUNTDL:RD0] boot-file-read pc={pc:x16} " +
                $"lba={lba:x8} dest={destination:x16} bytes={byteCount:x8} first={firstWord:x8}");
        }
        return true;
    }

    private bool TryFastPathKnownBootableAddressCheck(ulong pc)
    {
        const ulong entry = 0xffffffff80016188UL;
        const ulong callerReturnPc = 0xffffffff80016688UL;

        if (!_enableBootableAddressCheck || pc != entry || _gpr[31] != callerReturnPc)
            return false;
        if (_gpr[4] != 0 || _gpr[5] is < 0xffffffff802e73b0UL or > 0xffffffff802e7fffUL)
            return false;

        _gpr[2] = 1;
        Pc = _gpr[31];
        CompleteFastPathStep();
        if (_traceRd0Home)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] bootable-address-check pc={pc:x16} " +
                $"addr={_gpr[5]:x16} result=1");
        }
        return true;
    }

    private void ApplyKnownBootLoaderAddressBase(ulong pc)
    {
        if (!_enableBootLoaderAddressBase)
            return;
        if (pc is not (0xffffffff8001665cUL or 0xffffffff800166a8UL))
            return;
        if (_gpr[20] != 0x00000000a0000000UL)
            return;

        _gpr[20] = 0xffffffffa0000000UL;
        if (_traceRd0Home)
            Console.WriteLine($"[GAUNTDL:BOOT] boot-loader-address-base pc={pc:x16} s4={_gpr[20]:x16}");
    }

    private bool TryFastPathKnownBootSerialCopyLoop(ulong pc)
    {
        const ulong loopPc = 0xffffffff80012140UL;
        const ulong loopExitPc = 0xffffffff800121a0UL;

        if (!_enableBootSerialCopyLoop || pc != loopPc)
            return false;
        if (_memory.Read32(loopPc) != 0x3c04a480U ||
            _memory.Read32(loopPc + 0x04UL) != 0x34840002U ||
            _memory.Read32(loopPc + 0x08UL) != 0x80820000U ||
            _memory.Read32(loopPc + 0x0cUL) != 0x30420002U ||
            _memory.Read32(loopPc + 0x10UL) != 0x14400003U ||
            _memory.Read32(loopPc + 0x50UL) != 0x20a50001U ||
            _memory.Read32(loopPc + 0x54UL) != 0x00a6102bU ||
            _memory.Read32(loopPc + 0x58UL) != 0x1440ffe7U)
        {
            return false;
        }

        ulong cursor = _gpr[5];
        ulong end = _gpr[6];
        if (cursor >= end || end - cursor > 0x00100000UL)
            return false;

        _gpr[5] = end;
        _gpr[10] = 8;
        _gpr[15] = 0;
        _gpr[24] = 0;
        _gpr[2] = 0;
        Pc = loopExitPc;
        CompleteFastPathStep();
        if (_traceRd0Home && _bootSerialCopyLoopTraceCount++ < 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] boot-serial-copy-loop pc={pc:x16} " +
                $"from={cursor:x16} to={end:x16} bytes={end - cursor:x}");
        }
        return true;
    }

    private bool TryFastPathKnownBootSerialHandshake(ulong pc)
    {
        const ulong entry = 0xffffffff800121c0UL;

        if (!_enableBootSerialCopyLoop ||
            pc is not (0xffffffff800121c0UL or 0xffffffff800121c4UL or 0xffffffff800121c8UL or
                       0xffffffff800121d0UL or 0xffffffff800121dcUL))
        {
            return false;
        }

        if (_memory.Read32(entry) != 0x3c04a480U ||
            _memory.Read32(entry + 0x04UL) != 0x34840002U ||
            _memory.Read32(entry + 0x08UL) != 0x80820000U ||
            _memory.Read32(entry + 0x0cUL) != 0x30420001U ||
            _memory.Read32(entry + 0x10UL) != 0x14400007U ||
            _memory.Read32(entry + 0x18UL) != 0x214affffU ||
            _memory.Read32(entry + 0x1cUL) != 0x1540fff6U ||
            _memory.Read32(entry + 0x48UL) != 0x0000102dU ||
            _memory.Read32(entry + 0x50UL) != 0x0360f82dU ||
            _memory.Read32(entry + 0x58UL) != 0x03e00008U)
        {
            return false;
        }

        ulong returnAddress = _gpr[27];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x00010000UL or > 0x01000000UL)
            return false;

        _gpr[2] = 0;
        _gpr[10] = 0;
        _gpr[31] = returnAddress;
        _gpr[27] = _gpr[26];
        Pc = returnAddress;
        CompleteFastPathStep();
        if (_traceRd0Home && _bootSerialCopyLoopTraceCount++ < 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] boot-serial-handshake pc={pc:x16} " +
                $"return={returnAddress:x16}");
        }
        return true;
    }

    private bool TryFastPathKnownBootA420Handshake(ulong pc)
    {
        const ulong entry = 0xffffffff80010d54UL;

        if (!_enableBootSerialCopyLoop ||
            pc is not (0xffffffff80010d54UL or 0xffffffff80010d58UL or 0xffffffff80010d88UL or
                       0xffffffff80010d8cUL or 0xffffffff80010d90UL or 0xffffffff80010d98UL))
        {
            return false;
        }

        if (!MatchesKnownBootA420Handshake(entry, pc))
        {
            return false;
        }

        ulong returnAddress = _gpr[7];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x00010000UL or > 0x01000000UL)
        {
            if (_traceRd0Home && _bootA420HandshakeTraceCount++ < 8)
                Console.WriteLine($"[GAUNTDL:BOOT] boot-a420-skip pc={pc:x16} reason=return return={returnAddress:x16}");
            return false;
        }

        _gpr[2] = 0;
        Pc = returnAddress;
        CompleteFastPathStep();
        if (_traceRd0Home && _bootSerialCopyLoopTraceCount++ < 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] boot-a420-handshake pc={pc:x16} " +
                $"return={returnAddress:x16}");
        }
        return true;
    }

    private bool MatchesKnownBootA420Handshake(ulong entry, ulong pc)
    {
        ReadOnlySpan<(ulong Offset, uint Expected)> signature =
        [
            (0x00UL, 0x03e0382dU),
            (0x04UL, 0x3c08a420U),
            (0x08UL, 0x81090000U),
            (0x0cUL, 0x2401fffeU),
            (0x10UL, 0x01214824U),
            (0x14UL, 0xa1090000U),
            (0x18UL, 0x04110074U),
            (0x1cUL, 0x240403e8U),
            (0x20UL, 0x35290001U),
            (0x24UL, 0xa1090000U),
            (0x34UL, 0x0411feabU),
            (0x38UL, 0x00000000U),
            (0x3cUL, 0x1c400006U),
            (0x44UL, 0x256bffffU),
            (0x48UL, 0x1d60fff8U),
            (0x50UL, 0x00e00008U),
            (0x54UL, 0x24020001U),
        ];

        foreach ((ulong offset, uint expected) in signature)
        {
            uint actual = _memory.Read32(entry + offset);
            if (actual == expected)
                continue;

            if (_traceRd0Home && _bootA420HandshakeTraceCount++ < 8)
            {
                Console.WriteLine(
                    $"[GAUNTDL:BOOT] boot-a420-skip pc={pc:x16} " +
                    $"reason=signature off={offset:x2} actual={actual:x8} expected={expected:x8}");
            }
            return false;
        }

        return true;
    }

    private bool TryFastPathKnownLoadedBootVectorSetupLoop(ulong pc)
    {
        const ulong entry = 0xffffffff80011830UL;
        const ulong loopCallPc = 0xffffffff8001185cUL;
        const ulong loopDelayPc = 0xffffffff80011860UL;
        const ulong loopComparePc = 0xffffffff80011864UL;
        const ulong loopBranchPc = 0xffffffff80011868UL;

        if (!_enableBootSerialCopyLoop ||
            pc is not (entry or loopCallPc or loopDelayPc or loopComparePc or loopBranchPc))
        {
            return false;
        }

        if (_memory.Read32(entry) != 0x03e0802dU ||
            _memory.Read32(entry + 0x04UL) != 0x3c022000U ||
            _memory.Read32(entry + 0x08UL) != 0x0000a02dU ||
            _memory.Read32(entry + 0x0cUL) != 0x3c15800dU ||
            _memory.Read32(entry + 0x10UL) != 0x66b5ca80U ||
            _memory.Read32(entry + 0x14UL) != 0x3c038000U ||
            _memory.Read32(entry + 0x18UL) != 0x64630000U ||
            _memory.Read32(entry + 0x1cUL) != 0x02a3a823U ||
            _memory.Read32(entry + 0x20UL) != 0x02bea821U ||
            _memory.Read32(entry + 0x24UL) != 0x02a2a825U ||
            _memory.Read32(entry + 0x28UL) != 0x24160020U ||
            _memory.Read32(entry + 0x2cUL) != 0x0280202dU ||
            _memory.Read32(entry + 0x30UL) != 0x02a0f809U ||
            _memory.Read32(entry + 0x34UL) != 0x26940001U ||
            _memory.Read32(entry + 0x38UL) != 0x0296082aU ||
            _memory.Read32(entry + 0x3cUL) != 0x1420fffbU ||
            _memory.Read32(entry + 0x44UL) != 0x02000008U)
        {
            return false;
        }

        ulong returnAddress = pc == entry ? _gpr[31] : _gpr[16];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x00010000UL or > 0x01000000UL)
            return false;

        _gpr[20] = _gpr[22];
        _gpr[1] = 0;
        _gpr[2] = 0;
        Pc = returnAddress;
        CompleteFastPathStep();
        if (_traceRd0Home && _bootSerialCopyLoopTraceCount++ < 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] boot-vector-setup-loop pc={pc:x16} " +
                $"return={returnAddress:x16}");
        }
        return true;
    }

    private bool TryFastPathKnownBootCountDelay(ulong pc)
    {
        const ulong entry = 0xffffffff80010f40UL;
        const ulong loopPc = 0xffffffff80010fa8UL;
        const ulong loopDelayPc = 0xffffffff80010facUL;
        const ulong uncachedEntry = 0xffffffffa0010f40UL;
        const ulong uncachedLoopPc = 0xffffffffa0010fa8UL;
        const ulong uncachedLoopDelayPc = 0xffffffffa0010facUL;

        if (!_enableBootCountDelay || pc is not (entry or loopPc or loopDelayPc or uncachedEntry or uncachedLoopPc or uncachedLoopDelayPc))
            return false;
        if (_memory.Read32(entry) != 0x40037800U ||
            _memory.Read32(entry + 0x04UL) != 0x40028000U ||
            _memory.Read32(entry + 0x50UL) != 0x40024800U ||
            _memory.Read32(entry + 0x54UL) != 0x00640019U ||
            _memory.Read32(entry + 0x68UL) != 0x0064082bU ||
            _memory.Read32(entry + 0x6cUL) != 0x5420fffcU ||
            _memory.Read32(entry + 0x74UL) != 0x03e00008U)
        {
            return false;
        }

        ulong returnAddress = _gpr[31];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x00010000UL or > 0x01000000UL)
            return false;

        ulong delay = _gpr[4] & 0xffffffffUL;
        ulong ticks = Math.Max(_cp0CountStep, Math.Min(delay * 125UL, 0x01000000UL));
        _gpr[2] = _cp0[9];
        _gpr[3] = 0;
        _gpr[0] = 0;
        AdvanceCp0Count(ticks);
        _instructionCounter++;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = returnAddress;
        if (_traceRd0Home && _bootCountDelayTraceCount++ < 32)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] boot-count-delay pc={pc:x16} " +
                $"delay={delay:x} return={returnAddress:x16}");
        }
        return true;
    }

    private bool TryFastPathKnownRuntimeDelayCallbackLoop(ulong pc)
    {
        const ulong entry = 0xffffffff800e1414UL;

        if (!_enableBootCountDelay ||
            pc is not (0xffffffff800e1414UL or 0xffffffff800e1438UL or 0xffffffff800e1440UL or
                       0xffffffff800e1450UL or 0xffffffff800e1454UL or 0xffffffff800e1458UL or
                       0xffffffff800e1460UL or 0xffffffff800e1464UL or 0xffffffff800e1468UL))
        {
            return false;
        }

        if (_memory.Read32(entry) != 0x27bdffe0U ||
            _memory.Read32(entry + 0x04UL) != 0xafb00010U ||
            _memory.Read32(entry + 0x08UL) != 0x0080802dU ||
            _memory.Read32(entry + 0x0cUL) != 0xafb10014U ||
            _memory.Read32(entry + 0x10UL) != 0x3c11a420U ||
            _memory.Read32(entry + 0x14UL) != 0x36317000U ||
            _memory.Read32(entry + 0x18UL) != 0xafb20018U ||
            _memory.Read32(entry + 0x1cUL) != 0x3c128023U ||
            _memory.Read32(entry + 0x20UL) != 0xafbf001cU ||
            _memory.Read32(entry + 0x24UL) != 0x8e4281acU ||
            _memory.Read32(entry + 0x28UL) != 0xa2200000U ||
            _memory.Read32(entry + 0x2cUL) != 0x10400005U ||
            _memory.Read32(entry + 0x34UL) != 0x0040f809U ||
            _memory.Read32(entry + 0x3cUL) != 0x08038519U ||
            _memory.Read32(entry + 0x40UL) != 0x0200102dU ||
            _memory.Read32(entry + 0x44UL) != 0x0c0384beU ||
            _memory.Read32(entry + 0x4cUL) != 0x0200102dU ||
            _memory.Read32(entry + 0x50UL) != 0x1c40fff4U ||
            _memory.Read32(entry + 0x58UL) != 0x8fbf001cU ||
            _memory.Read32(entry + 0x5cUL) != 0x8fb20018U ||
            _memory.Read32(entry + 0x60UL) != 0x8fb10014U ||
            _memory.Read32(entry + 0x64UL) != 0x8fb00010U ||
            _memory.Read32(entry + 0x68UL) != 0x03e00008U ||
            _memory.Read32(entry + 0x6cUL) != 0x27bd0020U ||
            _memory.Read32(entry + 0x70UL) != 0x27bdffe8U)
        {
            return false;
        }

        uint callback = _memory.Read32(0xffffffff802281acUL);
        if (callback is not (0U or 0x800d03b8U))
            return false;

        ulong sp = _gpr[29];
        ulong returnAddress = pc == entry ? _gpr[31] : 0;
        if (pc != entry)
        {
            if (!IsMainRamRange(sp + 0x10UL, 0x10UL))
                return false;
            returnAddress = SignExtend32(_memory.Read32(sp + 0x1cUL));
        }

        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x00010000UL or > 0x01000000UL)
            return false;

        _memory.Write8(0xffffffffa4207000UL, 0);
        _gpr[2] = 0;
        if (pc != entry)
        {
            _gpr[18] = SignExtend32(_memory.Read32(sp + 0x18UL));
            _gpr[17] = SignExtend32(_memory.Read32(sp + 0x14UL));
            _gpr[16] = SignExtend32(_memory.Read32(sp + 0x10UL));
            _gpr[29] = sp + 0x20UL;
        }
        _gpr[31] = returnAddress;
        Pc = returnAddress;
        CompleteFastPathStep();
        if (_traceRd0Home && _bootCountDelayTraceCount++ < 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] runtime-delay-callback-loop pc={pc:x16} " +
                $"callback={callback:x8} return={returnAddress:x16}");
        }
        return true;
    }

    private bool TryFastPathKnownRuntimeDelayCallback(ulong pc)
    {
        const ulong entry = 0xffffffff800d03b8UL;

        if (!_enableBootCountDelay ||
            pc is not (0xffffffff800d03b8UL or 0xffffffff800d03c0UL or
                       0xffffffff800d03c8UL or 0xffffffff800d03ccUL or 0xffffffff800d03d0UL))
        {
            return false;
        }

        if (_memory.Read32(entry) != 0x27bdffe8U ||
            _memory.Read32(entry + 0x04UL) != 0xafbf0010U ||
            _memory.Read32(entry + 0x08UL) != 0x0c0043d0U ||
            _memory.Read32(entry + 0x0cUL) != 0x2404411aU ||
            _memory.Read32(entry + 0x10UL) != 0x8fbf0010U ||
            _memory.Read32(entry + 0x14UL) != 0x03e00008U ||
            _memory.Read32(entry + 0x18UL) != 0x27bd0018U)
        {
            return false;
        }

        ulong returnAddress = _gpr[31];
        if ((returnAddress & 0x1fffffffUL) != 0x000e1450UL)
            return false;

        _gpr[4] = 0x411a;
        _gpr[2] = _cp0[9];
        Pc = returnAddress;
        CompleteFastPathStep();
        if (_traceRd0Home && _bootCountDelayTraceCount++ < 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] runtime-delay-callback pc={pc:x16} " +
                $"return={returnAddress:x16}");
        }
        return true;
    }

    private bool TryFastPathKnownRuntimeTickWaitLoop(ulong pc)
    {
        const ulong entry = 0xffffffff800e0be4UL;
        const ulong counterAddress = 0xffffffff80228114UL;

        if (!_enableBootCountDelay ||
            pc is not (0xffffffff800e0be4UL or 0xffffffff800e0be8UL or
                       0xffffffff800e0becUL or 0xffffffff800e0bf0UL or
                       0xffffffff800e0bf4UL or 0xffffffff800e0bf8UL or
                       0xffffffff800e0c00UL or 0xffffffff800e0c08UL or
                       0xffffffff800e0c0cUL or 0xffffffff800e0c10UL or
                       0xffffffff800e0c14UL))
        {
            return false;
        }

        if (_memory.Read32(entry) != 0x3c038023U ||
            _memory.Read32(entry + 0x04UL) != 0x8c708114U ||
            _memory.Read32(entry + 0x08UL) != 0x8c628114U ||
            _memory.Read32(entry + 0x0cUL) != 0x00501023U ||
            _memory.Read32(entry + 0x10UL) != 0x2c4200b4U ||
            _memory.Read32(entry + 0x14UL) != 0x10400008U ||
            _memory.Read32(entry + 0x18UL) != 0x0060882dU ||
            _memory.Read32(entry + 0x1cUL) != 0x0c038505U ||
            _memory.Read32(entry + 0x20UL) != 0x0000202dU ||
            _memory.Read32(entry + 0x24UL) != 0x8e228114U ||
            _memory.Read32(entry + 0x28UL) != 0x00501023U ||
            _memory.Read32(entry + 0x2cUL) != 0x2c4200b4U ||
            _memory.Read32(entry + 0x30UL) != 0x1440fffaU ||
            _memory.Read32(entry + 0x34UL) != 0x00000000U)
        {
            return false;
        }

        uint baseline = pc < 0xffffffff800e0c08UL
            ? _memory.Read32(counterAddress)
            : (uint)_gpr[16];
        uint next = baseline + 0xb4U;
        _memory.Write32(counterAddress, next);
        _gpr[2] = 0;
        _gpr[3] = 0xffffffff80230000UL;
        _gpr[16] = baseline;
        _gpr[17] = 0xffffffff80230000UL;
        Pc = 0xffffffff800e0c1cUL;
        CompleteFastPathStep();
        if (_traceRd0Home && _bootCountDelayTraceCount++ < 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] runtime-tick-wait-loop pc={pc:x16} " +
                $"counter={baseline:x8}->{next:x8}");
        }
        return true;
    }

    private bool TryFastPathKnownRuntimeInlineTickWaitLoop(ulong pc)
    {
        const ulong loopEntry = 0xffffffff800e18c0UL;
        const ulong counterAddress = 0xffffffff80228114UL;
        if (!_enableBootCountDelay ||
            pc is not (0xffffffff800e18c0UL or 0xffffffff800e18c4UL or 0xffffffff800e18c8UL or
                       0xffffffff800e18ccUL or 0xffffffff800e18d0UL or 0xffffffff800e18d4UL or
                       0xffffffff800e18dcUL or 0xffffffff800e18e4UL or 0xffffffff800e18e8UL or
                       0xffffffff800e18ecUL or 0xffffffff800e18f0UL))
        {
            return false;
        }

        if (_memory.Read32(loopEntry) != 0x3c038023U ||
            _memory.Read32(loopEntry + 0x04UL) != 0x8c708114U ||
            _memory.Read32(loopEntry + 0x08UL) != 0x8c628114U ||
            _memory.Read32(loopEntry + 0x0cUL) != 0x00501023U ||
            _memory.Read32(loopEntry + 0x10UL) != 0x2c4200b4U ||
            _memory.Read32(loopEntry + 0x14UL) != 0x10400008U ||
            _memory.Read32(loopEntry + 0x18UL) != 0x0060882dU ||
            _memory.Read32(loopEntry + 0x1cUL) != 0x0c038505U ||
            _memory.Read32(loopEntry + 0x20UL) != 0x0000202dU ||
            _memory.Read32(loopEntry + 0x24UL) != 0x8e228114U ||
            _memory.Read32(loopEntry + 0x28UL) != 0x00501023U ||
            _memory.Read32(loopEntry + 0x2cUL) != 0x2c4200b4U ||
            _memory.Read32(loopEntry + 0x30UL) != 0x1440fffaU ||
            _memory.Read32(loopEntry + 0x34UL) != 0x00000000U)
        {
            return false;
        }

        uint baseline = pc < 0xffffffff800e18e4UL
            ? _memory.Read32(counterAddress)
            : (uint)_gpr[16];
        uint next = baseline + 0xb4U;
        _memory.Write32(counterAddress, next);
        _gpr[2] = 0;
        _gpr[3] = 0xffffffff80230000UL;
        _gpr[16] = baseline;
        _gpr[17] = 0xffffffff80230000UL;
        _gpr[0] = 0;
        Pc = 0xffffffff800e18f8UL;
        CompleteFastPathStep();
        if (_traceRd0Home && _bootCountDelayTraceCount++ < 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] runtime-inline-tick-wait-loop pc={pc:x16} " +
                $"counter={baseline:x8}->{next:x8}");
        }
        return true;
    }

    private bool TryFastPathKnownRuntimeQioErrorPollTail(ulong pc)
    {
        const ulong tailDelaySlot = 0xffffffff800d0e40UL;
        if (!_enableBootCountDelay || pc != tailDelaySlot)
            return false;

        if (!_hasPendingBranch ||
            _pendingBranchTarget != 0xffffffff800d0964UL ||
            _gpr[17] != 8UL ||
            _gpr[21] != 0)
        {
            return false;
        }

        if (_memory.Read32(0xffffffff800d0964UL) != 0x10400058U ||
            _memory.Read32(0xffffffff800d0968UL) != 0x32220003U ||
            _memory.Read32(0xffffffff800d0e34UL) != 0x1e60fecbU ||
            _memory.Read32(0xffffffff800d0e38UL) != 0x32220004U ||
            _memory.Read32(0xffffffff800d0e3cUL) != 0x1620fec9U ||
            _memory.Read32(0xffffffff800d0e40UL) != 0x240300a1U ||
            _memory.Read32(0xffffffff800d0e44UL) != 0x3c02a480U ||
            _memory.Read32(0xffffffff800d0e48UL) != 0xa0430000U)
        {
            return false;
        }

        ulong pendingTarget = _pendingBranchTarget;
        _gpr[2] = 0;
        _gpr[3] = 0xa1;
        _gpr[17] = 0;
        _gpr[0] = 0;
        Pc = 0xffffffff800d0e44UL;
        CompleteFastPathStep();
        if (_traceRd0Home && _bootCountDelayTraceCount++ < 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] runtime-qio-error-poll-tail pc={pc:x16} " +
                $"target={pendingTarget:x16}");
        }
        return true;
    }

    private bool TryRepairKnownRuntimeFsysQioStatus(ulong pc)
    {
        const ulong statusReadPc = 0xffffffff800d0a10UL;
        const ulong fsysObject = 0xffffffff802954b0UL;
        if (!_enableFsysQioBringupRepair || pc != statusReadPc || _gpr[21] != fsysObject)
            return false;
        if (_memory.Read32(statusReadPc) != 0x8ea20014U ||
            _memory.Read32(statusReadPc + 0x04UL) != 0x304200ffU ||
            _memory.Read32(statusReadPc + 0x08UL) != 0x10400017U ||
            _memory.Read32(statusReadPc + 0x0cUL) != 0x02238824U)
        {
            return false;
        }

        uint status = _memory.Read32(fsysObject + 0x14UL);
        if ((status & 0xffU) == 0)
            return false;

        uint repaired = status & 0xffffff00U;
        _memory.Write32(fsysObject + 0x14UL, repaired);
        _gpr[2] = SignExtend32(repaired);
        _gpr[0] = 0;
        Pc = statusReadPc + 4UL;
        CompleteFastPathStep();
        if (_traceRd0Home && _bootCountDelayTraceCount++ < 16)
        {
            Console.WriteLine(
                $"[GAUNTDL:FSYS] repair-qio-status pc={pc:x16} object={fsysObject:x16} " +
                $"status={status:x8}->{repaired:x8}");
        }
        return true;
    }

    private bool TryFastPathKnownRuntimeTextStateBlitBody(ulong pc)
    {
        if (pc is < 0xffffffff800e3500UL or > 0xffffffff800e388cUL)
            return false;

        if (_memory.Read32(0xffffffff800e3888UL) != 0x02a0102dU ||
            _memory.Read32(0xffffffff800e388cUL) != 0x8fbf00a4U ||
            _memory.Read32(0xffffffff800e38b4UL) != 0x03e00008U ||
            _memory.Read32(0xffffffff800e38b8UL) != 0x27bd00a8U)
        {
            return false;
        }

        ulong sp = _gpr[29];
        if (!IsMainRamRange(sp + 0x80UL, 0x28UL))
        {
            return false;
        }

        ulong returnAddress = SignExtend32(_memory.Read32(sp + 0xa4UL));
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x000e0000UL or > 0x00110000UL)
        {
            return false;
        }

        _gpr[2] = _gpr[21];
        _gpr[31] = returnAddress;
        _gpr[30] = SignExtend32(_memory.Read32(sp + 0xa0UL));
        _gpr[23] = SignExtend32(_memory.Read32(sp + 0x9cUL));
        _gpr[22] = SignExtend32(_memory.Read32(sp + 0x98UL));
        _gpr[21] = SignExtend32(_memory.Read32(sp + 0x94UL));
        _gpr[20] = SignExtend32(_memory.Read32(sp + 0x90UL));
        _gpr[19] = SignExtend32(_memory.Read32(sp + 0x8cUL));
        _gpr[18] = SignExtend32(_memory.Read32(sp + 0x88UL));
        _gpr[17] = SignExtend32(_memory.Read32(sp + 0x84UL));
        _gpr[16] = SignExtend32(_memory.Read32(sp + 0x80UL));
        _gpr[29] = sp + 0xa8UL;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 128UL);
        _instructionCounter += 128UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = returnAddress;
        return true;
    }

    private bool TryFastPathKnownRuntimeTextDrawEntry(ulong pc)
    {
        const ulong entry = 0xffffffff800e3378UL;
        if (pc != entry)
            return false;
        if (_memory.Read32(entry + 0x00UL) != 0x27bdff58U ||
            _memory.Read32(entry + 0x04UL) != 0xafbe00a0U ||
            _memory.Read32(entry + 0x08UL) != 0x0080f02dU ||
            _memory.Read32(entry + 0x0cUL) != 0xafb60098U ||
            _memory.Read32(entry + 0x10UL) != 0x00a0b02dU ||
            _memory.Read32(entry + 0x14UL) != 0xafb00080U ||
            _memory.Read32(entry + 0x18UL) != 0x3c108022U ||
            _memory.Read32(entry + 0x20UL) != 0x2612d8f8U ||
            _memory.Read32(entry + 0x28UL) != 0x30e20003U ||
            _memory.Read32(entry + 0x2cUL) != 0xafbf00a4U ||
            _memory.Read32(entry + 0x64UL) != 0x0c038c26U ||
            _memory.Read32(entry + 0x6cUL) != 0x06c10005U ||
            _memory.Read32(0xffffffff800e3888UL) != 0x02a0102dU ||
            _memory.Read32(0xffffffff800e388cUL) != 0x8fbf00a4U ||
            _memory.Read32(0xffffffff800e38b4UL) != 0x03e00008U ||
            _memory.Read32(0xffffffff800e38b8UL) != 0x27bd00a8U)
        {
            return false;
        }

        ulong returnAddress = _gpr[31];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x000d0000UL or > 0x00110000UL)
            return false;

        ulong text = _gpr[6];
        int length = 0;
        if (text != 0)
        {
            for (; length < 512; length++)
            {
                if (_memory.Read8(text + (uint)length) == 0)
                    break;
            }
            if (length >= 512)
                return false;
        }

        _gpr[2] = (ulong)(uint)length;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * (ulong)Math.Max(1, length + 16));
        _instructionCounter += (ulong)Math.Max(1, length + 16);
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = returnAddress;
        return true;
    }

    private bool TryFastPathKnownRuntimeDiagnosticDrawEntry(ulong pc)
    {
        const ulong entry = 0xffffffff800e3decUL;
        if (pc != entry)
            return false;
        if (_memory.Read32(entry + 0x00UL) != 0x27bdfe88U ||
            _memory.Read32(entry + 0x04UL) != 0xafb00168U ||
            _memory.Read32(entry + 0x08UL) != 0x0080802dU ||
            _memory.Read32(entry + 0x0cUL) != 0x3c038022U ||
            _memory.Read32(entry + 0x10UL) != 0xafb1016cU ||
            _memory.Read32(entry + 0x14UL) != 0x27b10030U ||
            _memory.Read32(entry + 0x18UL) != 0x8c62d900U ||
            _memory.Read32(entry + 0x1cUL) != 0x0220202dU ||
            _memory.Read32(entry + 0x20UL) != 0xafbf0170U ||
            _memory.Read32(entry + 0x24UL) != 0x24420001U ||
            _memory.Read32(entry + 0x28UL) != 0x0c0420a6U ||
            _memory.Read32(entry + 0x30UL) != 0x0c040d5dU ||
            _memory.Read32(entry + 0x38UL) != 0x0c040947U ||
            _memory.Read32(entry + 0xa4UL) != 0x8fbf0170U ||
            _memory.Read32(entry + 0xb0UL) != 0x03e00008U ||
            _memory.Read32(entry + 0xb4UL) != 0x27bd0178U)
        {
            return false;
        }

        ulong returnAddress = _gpr[31];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x000d0000UL or > 0x00110000UL)
            return false;

        _gpr[2] = 0;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 64UL);
        _instructionCounter += 64UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = returnAddress;
        return true;
    }

    private bool TryFastPathKnownRuntimeTextHexDrawWrapper(ulong pc)
    {
        const ulong entry = 0xffffffff800e3204UL;
        if (pc != entry)
            return false;
        if (_memory.Read32(entry + 0x00UL) != 0x27bdffc8U ||
            _memory.Read32(entry + 0x04UL) != 0xafb00028U ||
            _memory.Read32(entry + 0x08UL) != 0x0080802dU ||
            _memory.Read32(entry + 0x0cUL) != 0x00c0202dU ||
            _memory.Read32(entry + 0x10UL) != 0x00e0302dU ||
            _memory.Read32(entry + 0x14UL) != 0x8fa70048U ||
            _memory.Read32(entry + 0x18UL) != 0xafb20030U ||
            _memory.Read32(entry + 0x1cUL) != 0x00a0902dU ||
            _memory.Read32(entry + 0x20UL) != 0xafb1002cU ||
            _memory.Read32(entry + 0x24UL) != 0x8fb1004cU ||
            _memory.Read32(entry + 0x28UL) != 0xafbf0034U ||
            _memory.Read32(entry + 0x2cUL) != 0x0c0343eaU ||
            _memory.Read32(entry + 0x34UL) != 0x0200202dU ||
            _memory.Read32(entry + 0x38UL) != 0x0240282dU ||
            _memory.Read32(entry + 0x3cUL) != 0x27a60010U ||
            _memory.Read32(entry + 0x40UL) != 0x0c038cdeU ||
            _memory.Read32(entry + 0x44UL) != 0x0220382dU ||
            _memory.Read32(entry + 0x58UL) != 0x03e00008U ||
            _memory.Read32(entry + 0x5cUL) != 0x27bd0038U)
        {
            return false;
        }

        ulong returnAddress = _gpr[31];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x000d0000UL or > 0x00110000UL)
            return false;

        ulong width = _gpr[7];
        if (width > 512UL)
            return false;

        _gpr[2] = width;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * (ulong)Math.Max(1, (int)width + 24));
        _instructionCounter += (ulong)Math.Max(1, (int)width + 24);
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = returnAddress;
        return true;
    }

    private bool TryFastPathKnownGauntletGlideStateEmitCallerEpilogue(ulong pc)
    {
        if (pc != 0xffffffff80103f64UL)
            return false;

        if (_memory.Read32(0xffffffff80103f58UL) != 0x0c040ff3U ||
            _memory.Read32(0xffffffff80103f5cUL) != 0xaca40258U ||
            _memory.Read32(0xffffffff80103f60UL) != 0x8fbf0010U ||
            _memory.Read32(0xffffffff80103f64UL) != 0x03e00008U ||
            _memory.Read32(0xffffffff80103f68UL) != 0x27bd0018U)
        {
            return false;
        }

        ulong sp = _gpr[29];
        if (!IsMainRamRange(sp + 0x10UL, 4))
            return false;

        ulong returnAddress = SignExtend32(_memory.Read32(sp + 0x10UL));
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x000e0000UL or > 0x00110000UL)
            return false;

        _gpr[31] = returnAddress;
        _gpr[29] = sp + 0x18UL;
        _gpr[0] = 0;
        Pc = returnAddress;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownRuntimeFrameStateCallback(ulong pc)
    {
        const ulong entry = 0xffffffff800e0cf4UL;
        const ulong state = 0xffffffff8021af10UL;

        bool atEntry = pc == entry;
        bool afterStatusLoad = pc == 0xffffffff800e0d08UL;
        if (!_enableBootCountDelay || (!atEntry && !afterStatusLoad))
            return false;

        if (_memory.Read32(entry) != 0x27bdffe8U ||
            _memory.Read32(entry + 0x04UL) != 0x3c028022U ||
            _memory.Read32(entry + 0x08UL) != 0x2443af10U ||
            _memory.Read32(entry + 0x0cUL) != 0xafbf0010U ||
            _memory.Read32(entry + 0x10UL) != 0x8c620018U ||
            _memory.Read32(entry + 0x14UL) != 0x04400006U ||
            _memory.Read32(entry + 0x18UL) != 0x00000000U ||
            _memory.Read32(entry + 0x1cUL) != 0x8c620020U ||
            _memory.Read32(entry + 0x20UL) != 0x24420001U ||
            _memory.Read32(entry + 0x24UL) != 0xac620020U ||
            _memory.Read32(entry + 0x28UL) != 0x0c040a81U ||
            _memory.Read32(entry + 0x2cUL) != 0x8c640018U ||
            _memory.Read32(entry + 0x30UL) != 0x8fbf0010U ||
            _memory.Read32(entry + 0x34UL) != 0x03e00008U ||
            _memory.Read32(entry + 0x38UL) != 0x27bd0018U)
        {
            return false;
        }

        ulong sp = _gpr[29];
        ulong returnAddress = _gpr[31];
        if (afterStatusLoad)
        {
            if (!IsMainRamRange(sp + 0x10UL, 4))
                return false;
            returnAddress = SignExtend32(_memory.Read32(sp + 0x10UL));
        }

        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x000e0000UL or > 0x000e3000UL)
            return false;

        uint status = atEntry ? _memory.Read32(state + 0x18UL) : (uint)_gpr[2];
        if ((status & 0x80000000u) == 0)
        {
            uint counter = _memory.Read32(state + 0x20UL) + 1u;
            _memory.Write32(state + 0x20UL, counter);
            _gpr[2] = SignExtend32(counter);
            _gpr[4] = SignExtend32(status);
        }
        else
        {
            _gpr[2] = SignExtend32(status);
        }

        if (afterStatusLoad)
        {
            _gpr[31] = returnAddress;
            _gpr[29] = sp + 0x18UL;
        }
        _gpr[3] = state;
        Pc = returnAddress;
        CompleteFastPathStep();
        if (_traceRd0Home && _bootCountDelayTraceCount++ < 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] runtime-frame-state-callback pc={pc:x16} " +
                $"status={status:x8} return={returnAddress:x16}");
        }
        return true;
    }

    private bool TryFastPathKnownRuntimeBitfieldUpdate(ulong pc)
    {
        const ulong entry = 0xffffffff800eafdcUL;
        const ulong midBody = 0xffffffff800eb020UL;

        bool atEntry = pc == entry;
        bool atMidBody = pc == midBody;
        if (!atEntry && !atMidBody)
            return false;

        if (_memory.Read32(entry) != 0x0080302dU ||
            _memory.Read32(entry + 0x04UL) != 0x8cc20000U ||
            _memory.Read32(entry + 0x08UL) != 0x8cc40008U ||
            _memory.Read32(entry + 0x0cUL) != 0x8cc3000cU ||
            _memory.Read32(entry + 0x10UL) != 0xacc50000U ||
            _memory.Read32(entry + 0x14UL) != 0x00833826U ||
            _memory.Read32(entry + 0x18UL) != 0x00451026U ||
            _memory.Read32(entry + 0x1cUL) != 0x00022027U ||
            _memory.Read32(entry + 0x20UL) != 0x00a42824U ||
            _memory.Read32(entry + 0x24UL) != 0x8cc30004U ||
            _memory.Read32(entry + 0x28UL) != 0x8cc40010U ||
            _memory.Read32(entry + 0x2cUL) != 0x00431024U ||
            _memory.Read32(entry + 0x30UL) != 0x00451825U ||
            _memory.Read32(entry + 0x34UL) != 0x00e33824U ||
            _memory.Read32(entry + 0x38UL) != 0x00441024U ||
            _memory.Read32(entry + 0x3cUL) != 0x1440000fU ||
            _memory.Read32(entry + 0x40UL) != 0xacc30004U ||
            _memory.Read32(entry + 0x44UL) != 0x00641024U ||
            _memory.Read32(entry + 0x48UL) != 0x1040000cU ||
            _memory.Read32(entry + 0x7cUL) != 0x94c20016U ||
            _memory.Read32(entry + 0x80UL) != 0xa4c20014U ||
            _memory.Read32(entry + 0x84UL) != 0x8cc2000cU ||
            _memory.Read32(entry + 0x88UL) != 0x00471026U ||
            _memory.Read32(entry + 0x8cUL) != 0xacc20008U ||
            _memory.Read32(entry + 0x90UL) != 0x8cc20004U ||
            _memory.Read32(entry + 0x94UL) != 0x03e00008U ||
            _memory.Read32(entry + 0x98UL) != 0x00000000U)
        {
            return false;
        }

        ulong record = atEntry ? _gpr[4] : _gpr[6];
        ulong returnAddress = _gpr[31];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (!IsMainRamRange(record, 0x1cUL) ||
            returnOffset is < 0x000e0000UL or > 0x000ec000UL)
        {
            return false;
        }

        uint a0;
        uint v1;
        uint a3;
        if (atEntry)
        {
            uint newValue = (uint)_gpr[5];
            uint oldValue = _memory.Read32(record);
            uint oldBits = _memory.Read32(record + 0x08UL);
            uint sourceBits = _memory.Read32(record + 0x0cUL);
            _memory.Write32(record, newValue);

            a3 = oldBits ^ sourceBits;
            uint toggled = oldValue ^ newValue;
            a0 = ~toggled;
            uint unchangedNewBits = newValue & a0;
            uint maskedToggle = toggled & _memory.Read32(record + 0x04UL);
            v1 = maskedToggle | unchangedNewBits;
            a3 &= v1;
            uint delayedBits = maskedToggle & _memory.Read32(record + 0x10UL);
            _memory.Write32(record + 0x04UL, v1);
            if (delayedBits != 0)
                return false;
        }
        else
        {
            a0 = (uint)_gpr[4];
            v1 = (uint)_gpr[3];
            a3 = (uint)_gpr[7];
        }

        uint activeBits = v1 + a0;
        if (activeBits != 0)
        {
            ushort countdown = (ushort)(_memory.Read16(record + 0x14UL) - 1);
            _memory.Write16(record + 0x14UL, countdown);
            if (countdown == 0)
            {
                a3 &= ~a0;
                _memory.Write16(record + 0x14UL, _memory.Read16(record + 0x18UL));
            }
        }
        else
        {
            _memory.Write16(record + 0x14UL, _memory.Read16(record + 0x16UL));
        }

        uint nextBits = _memory.Read32(record + 0x0cUL) ^ a3;
        _memory.Write32(record + 0x08UL, nextBits);
        uint result = _memory.Read32(record + 0x04UL);

        _gpr[2] = result;
        _gpr[3] = v1;
        _gpr[4] = a0;
        _gpr[6] = record;
        _gpr[7] = a3;
        Pc = returnAddress;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownRuntimeStatusBitfieldRead(ulong pc)
    {
        const ulong entry = 0xffffffff800eb834UL;
        const ulong wrapper = 0xffffffff800eb8a4UL;
        const ulong table = 0xffffffff80262b90UL;

        bool atEntry = pc == entry;
        bool atWrapper = pc == wrapper;
        if (!atEntry && !atWrapper)
            return false;

        if (_memory.Read32(entry + 0x00UL) != 0x2c820007U ||
            _memory.Read32(entry + 0x04UL) != 0x14400003U ||
            _memory.Read32(entry + 0x08UL) != 0x000418c0U ||
            _memory.Read32(entry + 0x14UL) != 0x00641823U ||
            _memory.Read32(entry + 0x18UL) != 0x00031880U ||
            _memory.Read32(entry + 0x1cUL) != 0x3c028026U ||
            _memory.Read32(entry + 0x20UL) != 0x24422b90U ||
            _memory.Read32(entry + 0x24UL) != 0x00623821U ||
            _memory.Read32(entry + 0x28UL) != 0x8ce80008U ||
            _memory.Read32(entry + 0x2cUL) != 0x8ce3000cU ||
            _memory.Read32(entry + 0x30UL) != 0x8ce40004U ||
            _memory.Read32(entry + 0x34UL) != 0x8ce20000U ||
            _memory.Read32(entry + 0x38UL) != 0x01033026U ||
            _memory.Read32(entry + 0x3cUL) != 0x10a0000aU ||
            _memory.Read32(entry + 0x40UL) != 0x00822025U ||
            _memory.Read32(entry + 0x44UL) != 0x00061827U ||
            _memory.Read32(entry + 0x48UL) != 0x00641824U ||
            _memory.Read32(entry + 0x4cUL) != 0x00651824U ||
            _memory.Read32(entry + 0x50UL) != 0x00c33025U ||
            _memory.Read32(entry + 0x54UL) != 0x01061026U ||
            _memory.Read32(entry + 0x58UL) != 0xace2000cU ||
            _memory.Read32(entry + 0x5cUL) != 0x00051027U ||
            _memory.Read32(entry + 0x60UL) != 0x00821024U ||
            _memory.Read32(entry + 0x64UL) != 0x00432025U ||
            _memory.Read32(entry + 0x68UL) != 0x03e00008U ||
            _memory.Read32(entry + 0x6cUL) != 0x0080102dU ||
            _memory.Read32(wrapper + 0x00UL) != 0x27bdffe8U ||
            _memory.Read32(wrapper + 0x04UL) != 0x0080282dU ||
            _memory.Read32(wrapper + 0x08UL) != 0xafbf0010U ||
            _memory.Read32(wrapper + 0x0cUL) != 0x0c03ae0dU ||
            _memory.Read32(wrapper + 0x10UL) != 0x24040005U ||
            _memory.Read32(wrapper + 0x14UL) != 0x8fbf0010U ||
            _memory.Read32(wrapper + 0x18UL) != 0x03e00008U ||
            _memory.Read32(wrapper + 0x1cUL) != 0x27bd0018U)
        {
            return false;
        }

        uint index = atWrapper ? 5u : (uint)_gpr[4];
        uint mask = atWrapper ? (uint)_gpr[4] : (uint)_gpr[5];
        ulong returnAddress = _gpr[31];
        if (atWrapper)
        {
            ulong returnOffset = returnAddress & 0x1fffffffUL;
            if (returnOffset is < 0x000d0000UL or > 0x00110000UL)
                return false;
        }

        if (index >= 7u)
        {
            _gpr[2] = 0;
            _gpr[0] = 0;
            Pc = returnAddress;
            CompleteFastPathStep();
            return true;
        }

        ulong record = table + index * 28UL;
        if (!IsMainRamRange(record, 0x10UL))
            return false;

        uint oldBits = _memory.Read32(record + 0x08UL);
        uint sourceBits = _memory.Read32(record + 0x0cUL);
        uint combined = _memory.Read32(record + 0x04UL) | _memory.Read32(record);
        uint changed = oldBits ^ sourceBits;
        uint result;

        if (mask == 0)
        {
            result = combined;
        }
        else
        {
            uint selected = ~changed & combined & mask;
            _memory.Write32(record + 0x0cUL, oldBits ^ (changed | selected));
            result = (~mask & combined) | selected;
        }

        _gpr[2] = SignExtend32(result);
        _gpr[3] = changed;
        _gpr[4] = combined;
        _gpr[5] = mask;
        _gpr[6] = changed | (~changed & combined & mask);
        _gpr[7] = record;
        _gpr[8] = SignExtend32(oldBits);
        _gpr[0] = 0;
        Pc = returnAddress;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownRuntimeInputPoll(ulong pc)
    {
        const ulong entry = 0xffffffff800eb078UL;
        if (!_enableBootCountDelay || pc != entry)
            return false;

        if (_memory.Read32(entry + 0x00UL) != 0x27bdffa0U ||
            _memory.Read32(entry + 0x04UL) != 0x3c028026U ||
            _memory.Read32(entry + 0x08UL) != 0xafb70054U ||
            _memory.Read32(entry + 0x0cUL) != 0x24572b90U ||
            _memory.Read32(entry + 0x10UL) != 0x3c03a4a0U ||
            _memory.Read32(entry + 0x14UL) != 0x3463000cU ||
            _memory.Read32(entry + 0x18UL) != 0x3c02a4a0U ||
            _memory.Read32(entry + 0x1cUL) != 0x3442000aU ||
            _memory.Read32(entry + 0x20UL) != 0x3c04a4a0U ||
            _memory.Read32(entry + 0x24UL) != 0x3484000eU ||
            _memory.Read32(entry + 0x28UL) != 0x3c06a4a0U ||
            _memory.Read32(entry + 0x2cUL) != 0x34c60008U ||
            _memory.Read32(entry + 0x30UL) != 0x24070001U ||
            _memory.Read32(entry + 0x34UL) != 0xafbf005cU ||
            _memory.Read32(0xffffffff800eb780UL) != 0x8fbf005cU ||
            _memory.Read32(0xffffffff800eb7a8UL) != 0x03e00008U ||
            _memory.Read32(0xffffffff800eb7acUL) != 0x27bd0060U)
        {
            return false;
        }

        ulong returnAddress = _gpr[31];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x000e0000UL or > 0x000e3000UL)
            return false;

        // Bringup-only: this runtime helper polls the A4A0 input ports and
        // fans the result into the debounced bitfield table at 0x80262b90.
        // Until Gauntlet reaches real Voodoo geometry, treat it as no buttons
        // pressed so the boot path does not spend most of its frame budget here.
        _gpr[2] = 0;
        _gpr[3] = 0;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 32UL);
        _instructionCounter += 32UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = returnAddress;
        return true;
    }

    private bool TryFastPathKnownRuntimeDwordCopyTail(ulong pc)
    {
        const ulong loop = 0xffffffff800d1380UL;
        const ulong store = 0xffffffff800d138cUL;
        if (pc != loop && pc != store)
            return false;

        if (_memory.Read32(loop) != 0x2508ffffU ||
            _memory.Read32(loop + 0x04UL) != 0xdca90000U ||
            _memory.Read32(loop + 0x08UL) != 0x24a50008U ||
            _memory.Read32(loop + 0x0cUL) != 0xfc890000U ||
            _memory.Read32(loop + 0x10UL) != 0x1d00fffbU ||
            _memory.Read32(loop + 0x14UL) != 0x24840008U ||
            _memory.Read32(loop + 0x18UL) != 0x1000ffd3U ||
            _memory.Read32(loop + 0x1cUL) != 0x00000000U)
        {
            return false;
        }

        long counter = unchecked((long)_gpr[8]);
        ulong destination = _gpr[4];
        ulong source = _gpr[5];
        ulong copied = 0;
        ulong lastValue;

        if (pc == store)
        {
            long extraCopies = Math.Max(counter, 0);
            if (extraCopies > 0x80000)
                return false;
            ulong extraBytes = (ulong)extraCopies * 8UL;
            ulong totalBytes = extraBytes + 8UL;
            if (totalBytes > 0x00400000UL ||
                !IsMainRamRange(destination, totalBytes) ||
                (extraBytes != 0 && !IsMainRamRange(source, extraBytes)))
            {
                return false;
            }

            lastValue = _gpr[9];
            _memory.Write64(destination, lastValue);
            destination += 8UL;
            copied += 8UL;
            for (ulong offset = 0; offset < extraBytes; offset += 8UL)
            {
                lastValue = _memory.Read64(source + offset);
                _memory.Write64(destination + offset, lastValue);
            }
            source += extraBytes;
            destination += extraBytes;
            _gpr[8] = counter > 0 ? 0UL : unchecked((ulong)counter);
        }
        else
        {
            if (counter <= 0 || counter > 0x80000)
                return false;

            ulong bytes = (ulong)counter * 8UL;
            if (bytes > 0x00400000UL ||
                !IsMainRamRange(source, bytes) ||
                !IsMainRamRange(destination, bytes))
            {
                return false;
            }

            lastValue = 0;
            for (ulong offset = 0; offset < bytes; offset += 8UL)
            {
                lastValue = _memory.Read64(source + offset);
                _memory.Write64(destination + offset, lastValue);
            }
            source += bytes;
            destination += bytes;
            copied = bytes;
            _gpr[8] = 0;
        }

        if (copied != 0)
            _gpr[9] = lastValue;
        _gpr[4] = destination;
        _gpr[5] = source;
        Pc = 0xffffffff800d12e8UL;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownRuntimeAlignedQwordCopy(ulong pc)
    {
        const ulong entry = 0xffffffff800d1370UL;
        if (pc != entry)
            return false;

        if (_memory.Read32(entry) != 0x000640c2U ||
            _memory.Read32(entry + 0x04UL) != 0x000848c0U ||
            _memory.Read32(entry + 0x08UL) != 0x00c93022U ||
            _memory.Read32(entry + 0x0cUL) != 0x1900ffdaU ||
            _memory.Read32(entry + 0x10UL) != 0x2508ffffU ||
            _memory.Read32(entry + 0x14UL) != 0xdca90000U ||
            _memory.Read32(entry + 0x18UL) != 0x24a50008U ||
            _memory.Read32(entry + 0x1cUL) != 0xfc890000U ||
            _memory.Read32(entry + 0x20UL) != 0x1d00fffbU ||
            _memory.Read32(entry + 0x24UL) != 0x24840008U ||
            _memory.Read32(entry + 0x28UL) != 0x1000ffd3U ||
            _memory.Read32(entry + 0x2cUL) != 0x00000000U)
        {
            return false;
        }

        ulong destination = _gpr[4];
        ulong source = _gpr[5];
        ulong byteCount = _gpr[6];
        ulong originalDestination = _gpr[7];
        ulong returnAddress = _gpr[31];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if ((destination | source | byteCount) == 0 ||
            (destination & 7UL) != 0 ||
            (source & 7UL) != 0 ||
            (byteCount & 7UL) != 0 ||
            byteCount > 0x00400000UL ||
            originalDestination != destination ||
            returnOffset is < 0x000d0000UL or > 0x00110000UL ||
            !IsMainRamRange(source, byteCount) ||
            !IsMainRamRange(destination, byteCount))
        {
            return false;
        }

        ulong lastValue = 0;
        for (ulong offset = 0; offset < byteCount; offset += 8UL)
        {
            lastValue = _memory.Read64(source + offset);
            _memory.Write64(destination + offset, lastValue);
        }

        _gpr[2] = originalDestination;
        _gpr[4] = destination + byteCount;
        _gpr[5] = source + byteCount;
        _gpr[6] = 0;
        _gpr[7] = originalDestination;
        _gpr[8] = 0;
        _gpr[9] = lastValue;
        Pc = returnAddress;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownGauntletGlideRuntimeStateInitTail(ulong pc)
    {
        const ulong entry = 0xffffffff80108fe0UL;
        const ulong tail = 0xffffffff80109074UL;
        bool atEntry = pc == entry;
        bool atTail = pc == tail;
        if (!atEntry && !atTail)
            return false;

        if (_memory.Read32(entry) != 0x27bdffe0U ||
            _memory.Read32(entry + 0x04UL) != 0xafbf001cU ||
            _memory.Read32(entry + 0x08UL) != 0xafbe0018U ||
            _memory.Read32(entry + 0x0cUL) != 0x03a0f02dU ||
            _memory.Read32(entry + 0x10UL) != 0xafc40020U ||
            _memory.Read32(entry + 0x94UL) != 0x8fc20010U ||
            _memory.Read32(entry + 0x98UL) != 0x8c430008U ||
            _memory.Read32(entry + 0x9cUL) != 0x3c018026U ||
            _memory.Read32(entry + 0xa0UL) != 0xac232c90U ||
            _memory.Read32(entry + 0xa4UL) != 0x3c028026U ||
            _memory.Read32(entry + 0xa8UL) != 0x8c422c90U ||
            _memory.Read32(entry + 0xacUL) != 0x3c030060U ||
            _memory.Read32(entry + 0xb0UL) != 0x00431021U ||
            _memory.Read32(entry + 0xb4UL) != 0x3c018026U ||
            _memory.Read32(entry + 0xb8UL) != 0xac222c90U ||
            _memory.Read32(entry + 0xbcUL) != 0x3c028026U ||
            _memory.Read32(entry + 0xc0UL) != 0x8c422c90U ||
            _memory.Read32(entry + 0xc4UL) != 0x3c030002U ||
            _memory.Read32(entry + 0xc8UL) != 0x00431021U ||
            _memory.Read32(entry + 0xccUL) != 0x3c018026U ||
            _memory.Read32(entry + 0xd0UL) != 0xac222c90U ||
            _memory.Read32(entry + 0xd4UL) != 0x0c040ff3U ||
            _memory.Read32(entry + 0xd8UL) != 0x00000000U ||
            _memory.Read32(entry + 0xdcUL) != 0x03c0e82dU ||
            _memory.Read32(entry + 0xe0UL) != 0x8fbf001cU ||
            _memory.Read32(entry + 0xe4UL) != 0x8fbe0018U ||
            _memory.Read32(entry + 0xe8UL) != 0x27bd0020U ||
            _memory.Read32(entry + 0xecUL) != 0x03e00008U)
        {
            return false;
        }

        if (atEntry)
        {
            if (_gpr[4] != 0)
                return false;

            ulong entryReturnAddress = _gpr[31];
            ulong entryReturnOffset = entryReturnAddress & 0x1fffffffUL;
            if (entryReturnOffset is < 0x000e0000UL or > 0x00110000UL)
                return false;

            const ulong entryState = 0xffffffff80262d64UL;
            if (!IsMainRamRange(entryState + 0x384UL, 4))
                return false;

            _memory.Write32(0xffffffff80262c84UL, 0);
            _memory.Write32(0xffffffff80262c8cUL, unchecked((uint)entryState));
            uint entryStateWord = _memory.Read32(entryState + 0x08UL) + 0x00620000u;
            _memory.Write32(0xffffffff80262c90UL, entryStateWord);
            NormalizeGlideFifoState(entryState);

            _gpr[2] = _memory.Read32(entryState + 0x37cUL);
            _gpr[3] = entryState + 8UL;
            _gpr[4] = entryState;
            _gpr[31] = entryReturnAddress;
            _gpr[0] = 0;
            AdvanceCp0Count(_cp0CountStep * 64UL);
            _instructionCounter += 64UL;
            _hasPendingBranch = false;
            _hasImmediatePcOverride = false;
            Pc = entryReturnAddress;
            return true;
        }

        ulong framePointer = _gpr[30];
        if (!IsMainRamRange(framePointer + 0x1cUL, 4))
            return false;

        ulong state = SignExtend32(_memory.Read32(framePointer + 0x10UL));
        if (state != 0xffffffff80262d64UL || !IsMainRamRange(state + 0x384UL, 4))
            return false;

        uint stateWord = _memory.Read32(state + 0x08UL) + 0x00620000u;
        _memory.Write32(0xffffffff80262c90UL, stateWord);
        NormalizeGlideFifoState(state);

        ulong returnAddress = SignExtend32(_memory.Read32(framePointer + 0x1cUL));
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x000e0000UL or > 0x00110000UL)
            return false;

        _gpr[2] = _memory.Read32(state + 0x37cUL);
        _gpr[3] = state + 8UL;
        _gpr[4] = state;
        _gpr[31] = returnAddress;
        _gpr[30] = SignExtend32(_memory.Read32(framePointer + 0x18UL));
        _gpr[29] = framePointer + 0x20UL;
        Pc = returnAddress;
        CompleteFastPathStep();
        return true;
    }

    private bool TryFastPathKnownGauntletGlideRuntimeTwoWordStateUpdate(ulong pc)
    {
        const ulong entry = 0xffffffff801036a0UL;
        if (pc != entry)
            return false;

        if (_memory.Read32(entry) != 0x27bdffe0U ||
            _memory.Read32(entry + 0x04UL) != 0x3c028026U ||
            _memory.Read32(entry + 0x08UL) != 0xafb10014U ||
            _memory.Read32(entry + 0x0cUL) != 0x8c512c8cU ||
            _memory.Read32(entry + 0x10UL) != 0x2402ffc0U ||
            _memory.Read32(entry + 0x14UL) != 0xafbf0018U ||
            _memory.Read32(entry + 0x18UL) != 0xafb00010U ||
            _memory.Read32(entry + 0x1cUL) != 0x8e300264U ||
            _memory.Read32(entry + 0xacUL) != 0x3c030001U ||
            _memory.Read32(entry + 0xb0UL) != 0x8e220374U ||
            _memory.Read32(entry + 0xb4UL) != 0x34630211U ||
            _memory.Read32(entry + 0xb8UL) != 0xac430000U ||
            _memory.Read32(entry + 0xbcUL) != 0xac500004U ||
            _memory.Read32(entry + 0xc0UL) != 0x8e220374U ||
            _memory.Read32(entry + 0xc4UL) != 0x8e23037cU ||
            _memory.Read32(entry + 0xc8UL) != 0x24420008U ||
            _memory.Read32(entry + 0xccUL) != 0x2463fff8U ||
            _memory.Read32(entry + 0xd0UL) != 0xae220374U ||
            _memory.Read32(entry + 0xd4UL) != 0x0c040f92U ||
            _memory.Read32(entry + 0xd8UL) != 0xae23037cU ||
            _memory.Read32(entry + 0xdcUL) != 0x8fbf0018U ||
            _memory.Read32(entry + 0xe0UL) != 0x8fb10014U ||
            _memory.Read32(entry + 0xe4UL) != 0x8fb00010U ||
            _memory.Read32(entry + 0xe8UL) != 0x03e00008U ||
            _memory.Read32(entry + 0xecUL) != 0x27bd0020U)
        {
            return false;
        }

        ulong returnAddress = _gpr[31];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x00103000UL or > 0x00103600UL)
            return false;

        ulong state = SignExtend32(_memory.Read32(0xffffffff80262c8cUL));
        if (state != 0xffffffff80262d64UL ||
            !IsMainRamRange(state + 0x264UL, 4) ||
            !IsMainRamRange(state + 0x374UL, 12))
        {
            return false;
        }

        uint stateWord = _memory.Read32(state + 0x264UL) & 0xffffffc0u;
        uint selector = (uint)_gpr[4];
        uint lowSelector = selector & 0xffu;
        if (lowSelector == 1u)
        {
            stateWord |= 0x09u;
        }
        else if (lowSelector == 2u)
        {
            stateWord |= 0x11u;
        }
        else if (lowSelector == 3u)
        {
            stateWord |= 0x01u;
        }
        else
        {
            if ((selector & 0x100u) != 0)
                stateWord |= 0x04u;
            if ((selector & 0x200u) != 0)
                stateWord |= 0x02u;
            stateWord |= 0xc0u;
        }

        uint room = _memory.Read32(state + 0x37cUL);
        if (room < 8)
            return false;

        uint fifo = _memory.Read32(state + 0x374UL);
        if ((fifo & 3u) != 0 || fifo is < 0xa8200000u or >= 0xa8300000u)
            return false;

        _memory.Write32(state + 0x264UL, stateWord);
        WriteSignedAddress32(fifo, 0x00010211u);
        WriteSignedAddress32(fifo + 4u, stateWord);
        _memory.Write32(state + 0x374UL, fifo + 8u);
        _memory.Write32(state + 0x37cUL, room - 8u);
        NormalizeGlideFifoState(state);

        _gpr[2] = _memory.Read32(state + 0x37cUL);
        _gpr[3] = state + 8UL;
        _gpr[4] = state;
        _gpr[5] = state;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 196UL);
        _instructionCounter += 196UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = returnAddress;
        return true;
    }

    private bool TryFastPathKnownGauntletGlideRuntimeStateSnapshotCopy(ulong pc)
    {
        const ulong entry = 0xffffffff80108298UL;
        const ulong epilogue = 0xffffffff801082e4UL;
        bool atEntry = pc == entry;
        bool atEpilogue = pc is >= epilogue and <= 0xffffffff801082f8UL;
        if (!atEntry && !atEpilogue)
            return false;

        if (_memory.Read32(entry) != 0x27bdffe0U ||
            _memory.Read32(entry + 0x04UL) != 0xafbf001cU ||
            _memory.Read32(entry + 0x08UL) != 0xafbe0018U ||
            _memory.Read32(entry + 0x0cUL) != 0x03a0f02dU ||
            _memory.Read32(entry + 0x10UL) != 0xafc40020U ||
            _memory.Read32(entry + 0x14UL) != 0x3c028026U ||
            _memory.Read32(entry + 0x18UL) != 0x8c422c8cU ||
            _memory.Read32(entry + 0x1cUL) != 0xafc20010U ||
            _memory.Read32(entry + 0x20UL) != 0x8fc20010U ||
            _memory.Read32(entry + 0x24UL) != 0x8c430004U ||
            _memory.Read32(entry + 0x28UL) != 0xafc30014U ||
            _memory.Read32(entry + 0x2cUL) != 0x8fc20020U ||
            _memory.Read32(entry + 0x30UL) != 0x8fc30010U ||
            _memory.Read32(entry + 0x34UL) != 0x2463024cU ||
            _memory.Read32(entry + 0x38UL) != 0x0040202dU ||
            _memory.Read32(entry + 0x3cUL) != 0x0060282dU ||
            _memory.Read32(entry + 0x40UL) != 0x24060108U ||
            _memory.Read32(entry + 0x44UL) != 0x0c0344b1U ||
            _memory.Read32(entry + 0x48UL) != 0x00000000U ||
            _memory.Read32(entry + 0x4cUL) != 0x03c0e82dU ||
            _memory.Read32(entry + 0x50UL) != 0x8fbf001cU ||
            _memory.Read32(entry + 0x54UL) != 0x8fbe0018U ||
            _memory.Read32(entry + 0x58UL) != 0x27bd0020U ||
            _memory.Read32(entry + 0x5cUL) != 0x03e00008U)
        {
            return false;
        }

        ulong sp = _gpr[29];
        ulong returnAddress = _gpr[31];
        if (atEpilogue)
        {
            if (!IsMainRamRange(sp + 0x18UL, 8))
                return false;
            returnAddress = SignExtend32(_memory.Read32(sp + 0x1cUL));
        }

        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x000e0000UL or > 0x000e5000UL)
            return false;

        if (atEntry)
        {
            ulong state = SignExtend32(_memory.Read32(0xffffffff80262c8cUL));
            ulong destination = _gpr[4];
            ulong source = state + 0x24cUL;
            const ulong length = 0x108UL;
            if (state != 0xffffffff80262d64UL ||
                !IsMainRamRange(source, length) ||
                !IsMainRamRange(destination, length))
            {
                return false;
            }

            for (ulong offset = 0; offset < length; offset++)
                _memory.Write8(destination + offset, _memory.Read8(source + offset));

            _gpr[2] = destination;
            _gpr[3] = source;
            _gpr[4] = destination + 8UL;
            _gpr[5] = source + 0x108UL;
            _gpr[6] = 0;
        }

        _gpr[31] = returnAddress;
        if (atEpilogue)
        {
            _gpr[30] = SignExtend32(_memory.Read32(sp + 0x18UL));
            _gpr[29] = sp + 0x20UL;
        }
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 32UL);
        _instructionCounter += 32UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = returnAddress;
        return true;
    }

    private bool TryFastPathKnownRuntimeCommandCompleteWait(ulong pc)
    {
        const ulong entry = 0xffffffff800d78c0UL;

        if (!_enableBootCountDelay ||
            pc is not (0xffffffff800d78fcUL or 0xffffffff800d7900UL))
        {
            return false;
        }

        if (_memory.Read32(entry) != 0x27bdffb0U ||
            _memory.Read32(entry + 0x04UL) != 0xafb00040U ||
            _memory.Read32(entry + 0x08UL) != 0x0000802dU ||
            _memory.Read32(entry + 0x0cUL) != 0xafb10044U ||
            _memory.Read32(entry + 0x10UL) != 0x24110001U ||
            _memory.Read32(entry + 0x14UL) != 0x2402008aU ||
            _memory.Read32(entry + 0x18UL) != 0xafbf0048U ||
            _memory.Read32(entry + 0x1cUL) != 0xafa00034U ||
            _memory.Read32(entry + 0x20UL) != 0xa7a20010U ||
            _memory.Read32(entry + 0x24UL) != 0x27a40010U ||
            _memory.Read32(entry + 0x28UL) != 0x24050002U ||
            _memory.Read32(entry + 0x2cUL) != 0x27a60018U ||
            _memory.Read32(entry + 0x30UL) != 0x02111004U ||
            _memory.Read32(entry + 0x34UL) != 0x0c035f0eU ||
            _memory.Read32(entry + 0x38UL) != 0xa7a20012U ||
            _memory.Read32(entry + 0x3cUL) != 0x8fa2002cU ||
            _memory.Read32(entry + 0x40UL) != 0x1040fffeU ||
            _memory.Read32(entry + 0x44UL) != 0x00000000U ||
            _memory.Read32(entry + 0x48UL) != 0x8fa2002cU ||
            _memory.Read32(entry + 0x4cUL) != 0x14510025U)
        {
            return false;
        }

        ulong completionAddress = _gpr[29] + 0x2cUL;
        if ((completionAddress & 0xffffffff80000000UL) != 0xffffffff80000000UL)
            return false;

        _memory.Write32(completionAddress, 1);
        _gpr[2] = 1;
        Pc = 0xffffffff800d7908UL;
        CompleteFastPathStep();
        if (_traceRd0Home && _bootCountDelayTraceCount++ < 8)
        {
            Console.WriteLine(
                $"[GAUNTDL:BOOT] runtime-command-complete-wait pc={pc:x16} " +
                $"completion={completionAddress:x16}");
        }
        return true;
    }

    private bool MatchesKnownRuntimeEventPollWrapperSignature(ulong entry)
        => _memory.Read32(entry) == 0x27bdffe0U &&
           _memory.Read32(entry + 0x04) == 0xafbf001cU &&
           _memory.Read32(entry + 0x08) == 0xafbe0018U &&
           _memory.Read32(entry + 0x0c) == 0x03a0f02dU &&
           _memory.Read32(entry + 0x10) == 0xafc40020U &&
           _memory.Read32(entry + 0x1c) == 0x8fc20010U &&
           _memory.Read32(entry + 0x20) == 0x14400004U &&
           _memory.Read32(entry + 0x34) == 0x8fc40020U &&
           _memory.Read32(entry + 0x38) == 0x0c01748dU &&
           _memory.Read32(entry + 0x40) == 0x14400004U &&
           _memory.Read32(entry + 0x54) == 0x3c02800bU &&
           _memory.Read32(entry + 0x58) == 0x8c422f2cU &&
           _memory.Read32(entry + 0x5c) == 0x8c430058U &&
           _memory.Read32(entry + 0x60) == 0x10600008U &&
           _memory.Read32(entry + 0x6c) == 0x8c422f2cU &&
           _memory.Read32(entry + 0x70) == 0x8c43005cU &&
           _memory.Read32(entry + 0x74) == 0x14600006U &&
           _memory.Read32(entry + 0x84) == 0x24020001U &&
           _memory.Read32(entry + 0x100) == 0x03c0e82dU &&
           _memory.Read32(entry + 0x104) == 0x8fbf001cU &&
           _memory.Read32(entry + 0x108) == 0x8fbe0018U &&
           _memory.Read32(entry + 0x10c) == 0x27bd0020U &&
           _memory.Read32(entry + 0x110) == 0x03e00008U;

    private bool TryFastPathKnownRuntimeReadDelayHelper(ulong pc)
    {
        const ulong wrapperEntry = 0xffffffff8005e37cUL;
        const ulong readEntry = 0xffffffff8005eda4UL;
        bool inReadRoutine = pc >= readEntry && pc <= readEntry + 0x5cUL;
        if (pc != wrapperEntry && !inReadRoutine)
            return false;

        if (!MatchesKnownRuntimeReadDelayWrapperSignature(wrapperEntry) ||
            !MatchesKnownRuntimeReadWithDelaySignature(readEntry))
        {
            return false;
        }

        ulong readAddress = _gpr[4];
        ulong returnAddress = _gpr[31];
        ulong oldFramePointer = _gpr[30];
        ulong oldStackPointer = _gpr[29];
        if (inReadRoutine && pc != readEntry)
        {
            if (pc < readEntry + 0x18UL)
                return false;

            ulong frame = _gpr[30];
            if (!IsMainRamRange(frame + 0x18UL, 0x0cUL))
                return false;

            oldFramePointer = SignExtend32(_memory.Read32(frame + 0x18UL));
            returnAddress = SignExtend32(_memory.Read32(frame + 0x1cUL));
            readAddress = SignExtend32(_memory.Read32(frame + 0x20UL));
            oldStackPointer = frame + 0x20UL;
        }

        uint value = _memory.Read32(readAddress);
        _gpr[2] = SignExtend32(value);
        _gpr[29] = oldStackPointer;
        _gpr[30] = oldFramePointer;
        _gpr[31] = returnAddress;
        _gpr[0] = 0;
        ulong skippedInstructions = pc == wrapperEntry ? 18UL : 14UL;
        AdvanceCp0Count(_cp0CountStep * skippedInstructions);
        _instructionCounter += skippedInstructions;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = CanonicalizeCodeAddress(returnAddress);
        return true;
    }

    private bool MatchesKnownRuntimeReadDelayWrapperSignature(ulong entry)
        => _memory.Read32(entry) == 0x27bdffe0U &&
           _memory.Read32(entry + 0x04) == 0xafbf001cU &&
           _memory.Read32(entry + 0x08) == 0xafbe0018U &&
           _memory.Read32(entry + 0x0c) == 0x03a0f02dU &&
           _memory.Read32(entry + 0x10) == 0xafc40020U &&
           _memory.Read32(entry + 0x14) == 0x8fc20020U &&
           _memory.Read32(entry + 0x18) == 0xafc20010U &&
           _memory.Read32(entry + 0x1c) == 0x8fc40010U &&
           _memory.Read32(entry + 0x20) == 0x0c017b69U &&
           _memory.Read32(entry + 0x24) == 0x00000000U &&
           _memory.Read32(entry + 0x28) == 0x0040182dU &&
           _memory.Read32(entry + 0x2c) == 0x0060102dU &&
           _memory.Read32(entry + 0x30) == 0x080178edU &&
           _memory.Read32(entry + 0x38) == 0x03c0e82dU &&
           _memory.Read32(entry + 0x48) == 0x03e00008U;

    private bool MatchesKnownRuntimeReadWithDelaySignature(ulong entry)
        => _memory.Read32(entry) == 0x27bdffe0U &&
           _memory.Read32(entry + 0x04) == 0xafbf001cU &&
           _memory.Read32(entry + 0x08) == 0xafbe0018U &&
           _memory.Read32(entry + 0x0c) == 0x03a0f02dU &&
           _memory.Read32(entry + 0x10) == 0xafc40020U &&
           _memory.Read32(entry + 0x14) == 0x08017b72U &&
           _memory.Read32(entry + 0x20) == 0x8fc20020U &&
           _memory.Read32(entry + 0x24) == 0x8c430000U &&
           _memory.Read32(entry + 0x28) == 0xafc30010U &&
           _memory.Read32(entry + 0x2c) == 0x24040002U &&
           _memory.Read32(entry + 0x30) == 0x0c0043fbU &&
           _memory.Read32(entry + 0x38) == 0x8fc30010U &&
           _memory.Read32(entry + 0x3c) == 0x0060102dU &&
           _memory.Read32(entry + 0x40) == 0x08017b7cU &&
           _memory.Read32(entry + 0x48) == 0x03c0e82dU &&
           _memory.Read32(entry + 0x54) == 0x03e00008U;

    private bool TryFastPathKnownRuntimeStatus3fSixPoll(ulong pc)
    {
        const ulong entry = 0xffffffff8005e158UL;
        if (pc < entry || pc > entry + 0x88UL)
            return false;
        if (!MatchesKnownRuntimeStatus3fSixPollSignature(entry))
            return false;

        ulong returnAddress = _gpr[31];
        ulong oldFramePointer = _gpr[30];
        ulong oldStackPointer = _gpr[29];
        if (pc == entry)
        {
            ulong newSp = oldStackPointer - 0x20UL;
            if (!IsMainRamRange(newSp + 0x10UL, 0x14UL))
                return false;

            _memory.Write32(newSp + 0x1cUL, (uint)returnAddress);
            _memory.Write32(newSp + 0x18UL, (uint)oldFramePointer);
            _memory.Write32(newSp + 0x20UL, (uint)_gpr[4]);
            _memory.Write32(newSp + 0x10UL, 6U);
        }
        else
        {
            ulong frame = _gpr[30];
            if (!IsMainRamRange(frame + 0x18UL, 8))
                return false;

            oldFramePointer = SignExtend32(_memory.Read32(frame + 0x18UL));
            returnAddress = SignExtend32(_memory.Read32(frame + 0x1cUL));
            oldStackPointer = frame + 0x20UL;
        }

        _gpr[2] = 1;
        _gpr[3] = 6;
        _gpr[29] = oldStackPointer;
        _gpr[30] = oldFramePointer;
        _gpr[31] = returnAddress;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 386UL);
        _instructionCounter += 386UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = CanonicalizeCodeAddress(returnAddress);
        return true;
    }

    private bool MatchesKnownRuntimeStatus3fSixPollSignature(ulong entry)
        => _memory.Read32(entry) == 0x27bdffe0U &&
           _memory.Read32(entry + 0x04UL) == 0xafbf001cU &&
           _memory.Read32(entry + 0x08UL) == 0xafbe0018U &&
           _memory.Read32(entry + 0x0cUL) == 0x03a0f02dU &&
           _memory.Read32(entry + 0x10UL) == 0xafc40020U &&
           _memory.Read32(entry + 0x14UL) == 0xafc00010U &&
           _memory.Read32(entry + 0x28UL) == 0x8fc40020U &&
           _memory.Read32(entry + 0x2cUL) == 0x0c0178dfU &&
           _memory.Read32(entry + 0x34UL) == 0x3043003fU &&
           _memory.Read32(entry + 0x38UL) == 0x2402003fU &&
           _memory.Read32(entry + 0x3cUL) == 0x1462000cU &&
           _memory.Read32(entry + 0x44UL) == 0x8fc30010U &&
           _memory.Read32(entry + 0x48UL) == 0x24620001U &&
           _memory.Read32(entry + 0x4cUL) == 0x0040182dU &&
           _memory.Read32(entry + 0x50UL) == 0xafc30010U &&
           _memory.Read32(entry + 0x54UL) == 0x2c620006U &&
           _memory.Read32(entry + 0x58UL) == 0x14400003U &&
           _memory.Read32(entry + 0x78UL) == 0x03c0e82dU &&
           _memory.Read32(entry + 0x7cUL) == 0x8fbf001cU &&
           _memory.Read32(entry + 0x80UL) == 0x8fbe0018U &&
           _memory.Read32(entry + 0x84UL) == 0x27bd0020U &&
           _memory.Read32(entry + 0x88UL) == 0x03e00008U;

    private bool TryFastPathKnownRuntimeEventStatusNoCallback(ulong pc)
    {
        const ulong entry = 0xffffffff8005ec0cUL;
        if (pc != entry)
            return false;
        if (!MatchesKnownRuntimeEventStatusSignature(entry))
            return false;

        ulong record = SignExtend32(_memory.Read32(0xffffffff800b2f2cUL));
        if (!IsMainRamRange(record + 0xd8UL, 4))
            return false;
        if (_memory.Read32(record + 0xd8UL) != 0)
            return false;

        ulong outputAddress = _gpr[4];
        uint outputValue = (uint)_gpr[5];
        _memory.Write32(outputAddress, outputValue);

        _gpr[2] = SignExtend32((uint)outputAddress);
        _gpr[3] = SignExtend32(outputValue);
        _gpr[4] = 0;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 38UL);
        _instructionCounter += 38UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = _gpr[31];
        return true;
    }

    private bool MatchesKnownRuntimeEventStatusSignature(ulong entry)
        => _memory.Read32(entry) == 0x27bdffe0U &&
           _memory.Read32(entry + 0x04) == 0xafbf001cU &&
           _memory.Read32(entry + 0x08) == 0xafbe0018U &&
           _memory.Read32(entry + 0x0c) == 0x03a0f02dU &&
           _memory.Read32(entry + 0x10) == 0xafc40020U &&
           _memory.Read32(entry + 0x14) == 0xafc50024U &&
           _memory.Read32(entry + 0x18) == 0x3c02800bU &&
           _memory.Read32(entry + 0x1c) == 0x8c422f2cU &&
           _memory.Read32(entry + 0x20) == 0x8fc30020U &&
           _memory.Read32(entry + 0x24) == 0x8c420004U &&
           _memory.Read32(entry + 0x28) == 0x00621823U &&
           _memory.Read32(entry + 0x2c) == 0xafc30010U &&
           _memory.Read32(entry + 0x30) == 0x0000102dU &&
           _memory.Read32(entry + 0x34) == 0x3c03800bU &&
           _memory.Read32(entry + 0x38) == 0x8c632f2cU &&
           _memory.Read32(entry + 0x3c) == 0x10600037U &&
           _memory.Read32(entry + 0x48) == 0x8c6400d8U &&
           _memory.Read32(entry + 0x50) == 0x10800032U &&
           _memory.Read32(entry + 0x11c) == 0x24020001U &&
           _memory.Read32(entry + 0x120) == 0xafc20014U &&
           _memory.Read32(entry + 0x124) == 0x8fc20014U &&
           _memory.Read32(entry + 0x128) == 0x1040000eU &&
           _memory.Read32(entry + 0x140) == 0x8fc20020U &&
           _memory.Read32(entry + 0x144) == 0x8fc30024U &&
           _memory.Read32(entry + 0x148) == 0xac430000U &&
           _memory.Read32(entry + 0x180) == 0x03c0e82dU &&
           _memory.Read32(entry + 0x184) == 0x8fbf001cU &&
           _memory.Read32(entry + 0x188) == 0x8fbe0018U &&
           _memory.Read32(entry + 0x18c) == 0x27bd0020U &&
           _memory.Read32(entry + 0x190) == 0x03e00008U;

    private bool TryFastPathKnownGlideVertexCopyLoop(ulong pc)
    {
        if (pc != 0xffffffff80052880UL)
            return false;
        if (_memory.Read32(pc) != 0x8c8b0000U ||
            _memory.Read32(pc + 4) != 0x8c8c0004U ||
            _memory.Read32(pc + 8) != 0x8c8d0008U ||
            _memory.Read32(pc + 12) != 0x8c8e000cU ||
            _memory.Read32(pc + 16) != 0xac4b0000U ||
            _memory.Read32(pc + 20) != 0xac4c0004U ||
            _memory.Read32(pc + 24) != 0xac4d0008U ||
            _memory.Read32(pc + 28) != 0xac4e000cU ||
            _memory.Read32(pc + 32) != 0x24840010U ||
            _memory.Read32(pc + 36) != 0x1483fff6U ||
            _memory.Read32(pc + 40) != 0x24420010U)
        {
            return false;
        }

        ulong source = _gpr[4];
        ulong destination = _gpr[2];
        ulong end = _gpr[3];
        if (end < source || end - source > 0x1000UL || ((end - source) & 0xfUL) != 0)
            return false;

        ulong length = end - source;
        if (!IsMainRamRange(source, length == 0 ? 1UL : length) ||
            !IsMainRamRange(destination, length == 0 ? 1UL : length))
        {
            return false;
        }

        for (ulong offset = 0; offset < length; offset += 16)
        {
            _memory.Write32(destination + offset, _memory.Read32(source + offset));
            _memory.Write32(destination + offset + 4, _memory.Read32(source + offset + 4));
            _memory.Write32(destination + offset + 8, _memory.Read32(source + offset + 8));
            _memory.Write32(destination + offset + 12, _memory.Read32(source + offset + 12));
        }

        ulong chunks = length / 16UL;
        _gpr[2] = destination + length;
        _gpr[4] = end;
        _gpr[0] = 0;
        AdvanceCp0Count(Math.Max(_cp0CountStep, chunks * 11UL * _cp0CountStep));
        _instructionCounter += Math.Max(1UL, chunks * 11UL);
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = 0xffffffff800528acUL;
        return true;
    }

    private bool TryFastPathKnownRuntimeTwoBitTileExpand(ulong pc)
    {
        const ulong loopPc = 0xffffffff800194a0UL;
        if (pc != loopPc)
            return false;
        if (_memory.Read32(loopPc) != 0x24080007U ||
            _memory.Read32(loopPc + 0x04UL) != 0x95270000U ||
            _memory.Read32(loopPc + 0x08UL) != 0x2529fffeU ||
            _memory.Read32(loopPc + 0x0cUL) != 0x00c0182dU ||
            _memory.Read32(loopPc + 0x10UL) != 0x0000282dU ||
            _memory.Read32(loopPc + 0x14UL) != 0x30e20003U ||
            _memory.Read32(loopPc + 0x18UL) != 0x50400006U ||
            _memory.Read32(loopPc + 0x1cUL) != 0x24a50001U ||
            _memory.Read32(loopPc + 0x20UL) != 0x00021040U ||
            _memory.Read32(loopPc + 0x24UL) != 0x00441021U ||
            _memory.Read32(loopPc + 0x28UL) != 0x94420000U ||
            _memory.Read32(loopPc + 0x2cUL) != 0xa4620000U ||
            _memory.Read32(loopPc + 0x30UL) != 0x24a50001U ||
            _memory.Read32(loopPc + 0x34UL) != 0x24630002U ||
            _memory.Read32(loopPc + 0x38UL) != 0x28a20008U ||
            _memory.Read32(loopPc + 0x3cUL) != 0x1440fff5U ||
            _memory.Read32(loopPc + 0x40UL) != 0x00073882U ||
            _memory.Read32(loopPc + 0x44UL) != 0x2508ffffU ||
            _memory.Read32(loopPc + 0x48UL) != 0x0501ffeeU ||
            _memory.Read32(loopPc + 0x4cUL) != 0x246607f0U)
        {
            return false;
        }

        ulong palette = _gpr[4];
        ulong source = _gpr[9];
        ulong row = _gpr[6];
        if (!IsMainRamRange(palette, 8) ||
            !IsMainRamRange(source - 14UL, 16) ||
            !IsMainRamRange(row, 7UL * 2048UL + 16UL))
        {
            return false;
        }

        ulong cursor = source;
        ulong rowStart = row;
        ulong lastPixel = row;
        uint bits = 0;
        for (int y = 0; y < 8; y++)
        {
            bits = _memory.Read16(cursor);
            cursor -= 2UL;
            lastPixel = rowStart;
            for (int x = 0; x < 8; x++)
            {
                uint code = bits & 3U;
                if (code != 0)
                    _memory.Write16(lastPixel, _memory.Read16(palette + code * 2UL));
                bits >>= 2;
                lastPixel += 2UL;
            }
            rowStart = lastPixel + 0x7f0UL;
        }

        _gpr[2] = 0;
        _gpr[3] = lastPixel;
        _gpr[5] = 8;
        _gpr[6] = rowStart;
        _gpr[7] = bits;
        _gpr[8] = unchecked(ulong.MaxValue);
        _gpr[9] = cursor;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 160UL);
        _instructionCounter += 160UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = loopPc + 0x50UL;
        return true;
    }

    private bool TryFastPathKnownRuntimeTileOuterTail(ulong pc)
    {
        const ulong tailPc = 0xffffffff800194f0UL;
        if (pc != tailPc)
            return false;
        if (_memory.Read32(tailPc) != 0x26100001U ||
            _memory.Read32(tailPc + 0x04UL) != 0x00118840U ||
            _memory.Read32(tailPc + 0x08UL) != 0x3c04800bU ||
            _memory.Read32(tailPc + 0x0cUL) != 0x8c822e24U ||
            _memory.Read32(tailPc + 0x10UL) != 0x0202102aU ||
            _memory.Read32(tailPc + 0x14UL) != 0x1440ff70U ||
            _memory.Read32(tailPc + 0x18UL) != 0x26730002U ||
            _memory.Read32(tailPc + 0x1cUL) != 0x27de000cU ||
            _memory.Read32(tailPc + 0x20UL) != 0x3c02800bU ||
            _memory.Read32(tailPc + 0x24UL) != 0x8c422e1cU ||
            _memory.Read32(tailPc + 0x28UL) != 0x26d60001U ||
            _memory.Read32(tailPc + 0x2cUL) != 0x02c2102aU ||
            _memory.Read32(tailPc + 0x30UL) != 0x1440ff5cU ||
            _memory.Read32(tailPc + 0x34UL) != 0x26f70008U)
        {
            return false;
        }

        ulong columnLimit = SignExtend32(_memory.Read32(0xffffffff800b2e24UL));
        bool staysInRow = unchecked((long)((ulong)((long)_gpr[16] + 1L))) < unchecked((long)columnLimit);
        int skippedInstructions = staysInRow ? 7 : 14;
        if (_remainingProbeSteps < skippedInstructions)
            return false;

        _gpr[16] = (ulong)((long)_gpr[16] + 1L);
        _gpr[17] = (uint)_gpr[17] << 1;
        _gpr[4] = 0x800b0000UL;
        _gpr[2] = staysInRow ? 1UL : 0UL;
        _gpr[19] = (ulong)((long)_gpr[19] + 2L);
        if (staysInRow)
        {
            FinishKnownRuntimeTileOuterTail(skippedInstructions, 0xffffffff800192c8UL);
            return true;
        }

        ulong rowLimit = SignExtend32(_memory.Read32(0xffffffff800b2e1cUL));
        _gpr[30] = (ulong)((long)_gpr[30] + 0x0cL);
        _gpr[22] = (ulong)((long)_gpr[22] + 1L);
        _gpr[2] = unchecked((long)_gpr[22]) < unchecked((long)rowLimit) ? 1UL : 0UL;
        _gpr[23] = (ulong)((long)_gpr[23] + 8L);
        FinishKnownRuntimeTileOuterTail(
            skippedInstructions,
            _gpr[2] != 0 ? 0xffffffff80019294UL : 0xffffffff80019528UL);
        return true;
    }

    private void FinishKnownRuntimeTileOuterTail(int skippedInstructions, ulong nextPc)
    {
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * (ulong)skippedInstructions);
        _instructionCounter += (ulong)skippedInstructions;
        _probeStepDebt += skippedInstructions - 1;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = nextPc;
    }

    private bool TryFastPathKnownRuntimeTileDepthPointerHelper(ulong pc)
    {
        const ulong helperPc = 0xffffffff8003878cUL;
        if (pc != helperPc)
            return false;
        if (_memory.Read32(helperPc) != 0x3c02a800U ||
            _memory.Read32(helperPc + 0x04UL) != 0x3c03a900U ||
            _memory.Read32(helperPc + 0x08UL) != 0x03e00008U ||
            _memory.Read32(helperPc + 0x0cUL) != 0x0064100bU)
        {
            return false;
        }

        _gpr[2] = _gpr[4] == 0 ? 0xa8000000UL : 0xa9000000UL;
        _gpr[3] = 0xa9000000UL;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 4UL);
        _instructionCounter += 4UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = CanonicalizeCodeAddress(_gpr[31]);
        return true;
    }

    private bool TryFastPathKnownRuntimeTileDepthPointerCallsite(ulong pc)
    {
        const ulong callsitePc = 0xffffffff80019344UL;
        if (pc != callsitePc)
            return false;
        if (_memory.Read32(callsitePc) != 0x0000202dU ||
            _memory.Read32(callsitePc + 0x04UL) != 0xafaa0018U ||
            _memory.Read32(callsitePc + 0x08UL) != 0xafab001cU ||
            _memory.Read32(callsitePc + 0x0cUL) != 0xafac0020U ||
            _memory.Read32(callsitePc + 0x10UL) != 0x0c00e1e3U ||
            _memory.Read32(callsitePc + 0x14UL) != 0xafad0024U ||
            _memory.Read32(callsitePc + 0x18UL) != 0x0040202dU ||
            _memory.Read32(0xffffffff8003878cUL) != 0x3c02a800U ||
            _memory.Read32(0xffffffff80038790UL) != 0x3c03a900U ||
            _memory.Read32(0xffffffff80038794UL) != 0x03e00008U ||
            _memory.Read32(0xffffffff80038798UL) != 0x0064100bU)
        {
            return false;
        }

        ulong sp = _gpr[29];
        if (!IsMainRamRange(sp + 0x18UL, 0x10UL))
            return false;

        _memory.Write32(sp + 0x18UL, (uint)_gpr[10]);
        _memory.Write32(sp + 0x1cUL, (uint)_gpr[11]);
        _memory.Write32(sp + 0x20UL, (uint)_gpr[12]);
        _memory.Write32(sp + 0x24UL, (uint)_gpr[13]);
        _gpr[2] = 0xa8000000UL;
        _gpr[3] = 0xa9000000UL;
        _gpr[4] = 0xa8000000UL;
        _gpr[31] = callsitePc + 0x18UL;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 10UL);
        _instructionCounter += 10UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = callsitePc + 0x1cUL;
        return true;
    }

    private void CountHotPc(ulong pc)
    {
        if (_hotPcCounts.TryGetValue(pc, out ulong count))
        {
            _hotPcCounts[pc] = count + 1UL;
            return;
        }

        if (_hotPcCounts.Count < 8192)
            _hotPcCounts.Add(pc, 1UL);
    }

    private string GetHotPcStatus()
    {
        if (_hotPcCounts.Count == 0)
            return "hotpcs=disabled";

        return "hotpcs=" + string.Join(
            ",",
            _hotPcCounts
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key)
                .Take(16)
                .Select(item => $"0x{item.Key:x16}:{item.Value}"));
    }

    private bool TryFastPathKnownGlideSetupPacketHelper(ulong pc)
    {
        bool gauntletState = pc == 0xffffffff80103f70UL;
        if (pc != 0xffffffff80052bc0UL && !gauntletState)
            return false;
        if (_memory.Read32(pc) != 0x24030008U ||
            _memory.Read32(pc + 4) != 0x24070002U ||
            _memory.Read32(pc + 8) != 0x00e5180bU ||
            _memory.Read32(pc + 12) != (gauntletState ? 0x3c028026U : 0x3c02800bU) ||
            _memory.Read32(pc + 16) != (gauntletState ? 0x8c462c8cU : 0x8c464d2cU) ||
            _memory.Read32(pc + 20) != 0x14600004U ||
            _memory.Read32(pc + 24) != 0x00031580U ||
            _memory.Read32(pc + 40) != 0x00441025U ||
            _memory.Read32(pc + 44) != 0x34430003U ||
            _memory.Read32(pc + 48) != 0xacc30358U ||
            _memory.Read32(pc + 52) != 0x344300c3U ||
            _memory.Read32(pc + 56) != 0xacc4035cU ||
            _memory.Read32(pc + 60) != 0x10a00005U ||
            _memory.Read32(pc + 64) != 0xacc30354U ||
            _memory.Read32(pc + 80) != 0xacc20354U ||
            _memory.Read32(pc + 84) != 0x03e00008U)
        {
            return false;
        }

        ulong returnAddress = _gpr[31];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x00010000UL or > 0x01000000UL)
            return false;

        ulong state = gauntletState
            ? SignExtend32(_memory.Read32(0xffffffff80262c8cUL))
            : unchecked((ulong)(long)(int)_memory.Read32(0x800b4d2cUL));
        if (!IsMainRamRange(state + 0x354UL, 12))
            return false;

        uint command = (uint)_gpr[4];
        bool packed = _gpr[5] != 0;
        uint selector = packed ? 2u : 8u;
        uint prefix = (selector << 22) | command;
        uint setup = prefix | 0xc3u;
        uint storedSetup = packed ? setup & 0xff7fffffu : setup;

        _memory.Write32(state + 0x358UL, prefix | 3u);
        _memory.Write32(state + 0x35cUL, command);
        _memory.Write32(state + 0x354UL, storedSetup);
        _gpr[2] = packed ? storedSetup : prefix;
        _gpr[3] = setup;
        _gpr[6] = state;
        _gpr[7] = 2;
        _gpr[0] = 0;

        ulong skippedInstructions = packed ? 22UL : 18UL;
        AdvanceCp0Count(_cp0CountStep * skippedInstructions);
        _instructionCounter += skippedInstructions;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = returnAddress;
        return true;
    }

    private bool TryFastPathKnownGlideStateFlush(ulong pc)
    {
        if (TryFastPathKnownGauntletGlideStateEmit(pc))
            return true;

        if (pc != 0xffffffff800526acUL)
            return false;
        if (_memory.Read32(pc) != 0x27bdffe8U ||
            _memory.Read32(pc + 4) != 0x3c02800bU ||
            _memory.Read32(pc + 8) != 0xafb00010U ||
            _memory.Read32(pc + 12) != 0x24504d20U ||
            _memory.Read32(pc + 16) != 0xafbf0014U ||
            _memory.Read32(pc + 20) != 0x8e02000cU ||
            _memory.Read32(pc + 24) != 0x8c42037cU ||
            _memory.Read32(pc + 28) != 0x2842003cU ||
            _memory.Read32(pc + 56) != 0x8e04000cU ||
            _memory.Read32(pc + 60) != 0x3c030e3fU ||
            _memory.Read32(pc + 64) != 0x8c820374U ||
            _memory.Read32(pc + 68) != 0x3463820cU ||
            _memory.Read32(pc + 72) != 0xac430000U ||
            _memory.Read32(pc + 228) != 0x3c020003U ||
            _memory.Read32(pc + 232) != 0x8c830374U ||
            _memory.Read32(pc + 236) != 0x34428284U ||
            _memory.Read32(pc + 240) != 0xac620000U)
        {
            return false;
        }

        ulong returnAddress = _gpr[31];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x00010000UL or > 0x01000000UL)
            return false;

        ulong state = unchecked((ulong)(long)(int)_memory.Read32(0x800b4d2cUL));
        if (!IsMainRamRange(state + 0x260UL, 0x138))
            return false;

        uint room = _memory.Read32(state + 0x37cUL);
        if (room < 0x3c)
            return false;

        uint fifo = _memory.Read32(state + 0x374UL);
        if ((fifo & 3u) != 0)
            return false;

        uint firstStart = fifo;
        WriteSignedAddress32(fifo, 0x0e3f820cu);
        fifo += 4;
        for (uint offset = 0x260; offset <= 0x284; offset += 4, fifo += 4)
            WriteSignedAddress32(fifo, _memory.Read32(state + offset));

        room -= fifo - firstStart;
        _memory.Write32(state + 0x374UL, fifo);
        _memory.Write32(state + 0x37cUL, room);

        uint secondStart = fifo;
        WriteSignedAddress32(fifo, 0x00038284u);
        fifo += 4;
        for (uint offset = 0x28c; offset <= 0x294; offset += 4, fifo += 4)
            WriteSignedAddress32(fifo, _memory.Read32(state + offset));

        room -= fifo - secondStart;
        _memory.Write32(state + 0x374UL, fifo);
        _memory.Write32(state + 0x37cUL, room);

        _gpr[2] = room;
        _gpr[3] = fifo - secondStart;
        _gpr[4] = state;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 76UL);
        _instructionCounter += 76UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = returnAddress;
        return true;
    }

    private bool TryFastPathKnownGauntletGlideStateEmit(ulong pc)
    {
        const ulong entry = 0xffffffff80103fccUL;

        bool atEntry = pc == entry;
        bool atCallerEpilogue = pc == 0xffffffff80103f64UL;
        bool inMaskBody = pc == 0xffffffff80104068UL;
        if (!atEntry && !atCallerEpilogue && !inMaskBody)
            return false;

        if (atEntry)
            return false;

        if (atCallerEpilogue)
        {
            if (_memory.Read32(0xffffffff80103f58UL) != 0x0c040ff3U ||
                _memory.Read32(0xffffffff80103f5cUL) != 0xaca40258U ||
                _memory.Read32(0xffffffff80103f60UL) != 0x8fbf0010U ||
                _memory.Read32(0xffffffff80103f64UL) != 0x03e00008U ||
                _memory.Read32(0xffffffff80103f68UL) != 0x27bd0018U)
            {
                return false;
            }

            ulong sp = _gpr[29];
            if (!IsMainRamRange(sp + 0x10UL, 4))
                return false;

            ulong callerReturnAddress = SignExtend32(_memory.Read32(sp + 0x10UL));
            ulong callerReturnOffset = callerReturnAddress & 0x1fffffffUL;
            if (callerReturnOffset is < 0x000e0000UL or > 0x00110000UL)
                return false;

            _gpr[31] = callerReturnAddress;
            _gpr[29] = sp + 0x18UL;
            _gpr[0] = 0;
            Pc = callerReturnAddress;
            CompleteFastPathStep();
            return true;
        }

        if (_memory.Read32(entry) != 0x27bdffd8U ||
            _memory.Read32(entry + 0x04UL) != 0x3c028026U ||
            _memory.Read32(entry + 0x08UL) != 0xafb40020U ||
            _memory.Read32(entry + 0x0cUL) != 0x24542c80U ||
            _memory.Read32(entry + 0x10UL) != 0xafbf0024U ||
            _memory.Read32(entry + 0x14UL) != 0xafb3001cU ||
            _memory.Read32(entry + 0x18UL) != 0xafb20018U ||
            _memory.Read32(entry + 0x1cUL) != 0xafb10014U ||
            _memory.Read32(entry + 0x20UL) != 0xafb00010U ||
            _memory.Read32(entry + 0x24UL) != 0x8e90000cU ||
            _memory.Read32(entry + 0x28UL) != 0x0000902dU ||
            _memory.Read32(entry + 0x2cUL) != 0x8e070258U ||
            _memory.Read32(entry + 0x98UL) != 0x30e20002U ||
            _memory.Read32(entry + 0x9cUL) != 0x1040000eU ||
            _memory.Read32(entry + 0xa0UL) != 0x00111880U)
        {
            return false;
        }

        ulong state = SignExtend32(_memory.Read32(0xffffffff80262c8cUL));
        if (state != 0xffffffff80262d64UL || !IsMainRamRange(state + 0x374UL, 12))
            return false;

        ulong returnAddress = _gpr[31];
        if (inMaskBody)
        {
            ulong sp = _gpr[29];
            if (!IsMainRamRange(sp + 0x10UL, 0x18))
                return false;
            returnAddress = SignExtend32(_memory.Read32(sp + 0x24UL));
            _gpr[20] = SignExtend32(_memory.Read32(sp + 0x20UL));
            _gpr[19] = SignExtend32(_memory.Read32(sp + 0x1cUL));
            _gpr[18] = SignExtend32(_memory.Read32(sp + 0x18UL));
            _gpr[17] = SignExtend32(_memory.Read32(sp + 0x14UL));
            _gpr[16] = SignExtend32(_memory.Read32(sp + 0x10UL));
            _gpr[31] = returnAddress;
            _gpr[29] = sp + 0x28UL;
        }

        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x000e0000UL or > 0x00110000UL)
            return false;

        NormalizeGlideFifoState(state);
        _gpr[2] = _memory.Read32(state + 0x37cUL);
        _gpr[3] = state + 8UL;
        _gpr[4] = state;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 96UL);
        _instructionCounter += 96UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = returnAddress;
        return true;
    }

    private bool TryFastPathKnownGlideTwoWordStatePacketTail(ulong pc)
    {
        if (pc != 0xffffffff800511c8UL)
            return false;
        if (_memory.Read32(pc) != 0x3c030001U ||
            _memory.Read32(pc + 4) != 0x8e020374U ||
            _memory.Read32(pc + 8) != 0x34630219U ||
            _memory.Read32(pc + 12) != 0xac430000U ||
            _memory.Read32(pc + 16) != 0xac510004U ||
            _memory.Read32(pc + 24) != 0x8e03037cU ||
            _memory.Read32(pc + 36) != 0xae020374U ||
            _memory.Read32(pc + 40) != 0xae03037cU ||
            _memory.Read32(pc + 48) != 0x8fb10014U ||
            _memory.Read32(pc + 52) != 0x8fb00010U ||
            _memory.Read32(pc + 56) != 0x03e00008U ||
            _memory.Read32(pc + 60) != 0x27bd0020U)
        {
            return false;
        }

        ulong state = _gpr[16];
        if (!IsMainRamRange(state + 0x268UL, 4) ||
            !IsMainRamRange(state + 0x374UL, 12) ||
            !IsMainRamRange(_gpr[29] + 0x10UL, 12))
        {
            return false;
        }

        uint room = _memory.Read32(state + 0x37cUL);
        if (room < 8)
            return false;

        uint fifo = _memory.Read32(state + 0x374UL);
        if ((fifo & 3u) != 0 || fifo is < 0xa8200000u or >= 0xa8300000u)
            return false;

        uint nextFifo = fifo + 8u;
        uint nextRoom = room - 8u;
        WriteSignedAddress32(fifo, 0x00010219u);
        WriteSignedAddress32(fifo + 4u, (uint)_gpr[17]);
        _memory.Write32(state + 0x374UL, nextFifo);
        _memory.Write32(state + 0x37cUL, nextRoom);

        ulong sp = _gpr[29];
        _gpr[2] = SignExtend32(nextFifo);
        _gpr[3] = SignExtend32(nextRoom);
        _gpr[31] = SignExtend32(_memory.Read32(sp + 0x18UL));
        _gpr[17] = SignExtend32(_memory.Read32(sp + 0x14UL));
        _gpr[16] = SignExtend32(_memory.Read32(sp + 0x10UL));
        _gpr[29] = sp + 0x20UL;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 16UL);
        _instructionCounter += 16UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = _gpr[31];
        return true;
    }

    private bool TryFastPathKnownGlideBufferSwapPacketTail(ulong pc)
    {
        if (pc != 0xffffffff80053340UL)
            return false;
        if (_memory.Read32(pc) != 0x2442ffffU ||
            _memory.Read32(pc + 4) != 0x14400035U ||
            _memory.Read32(pc + 8) != 0xae02038cU ||
            _memory.Read32(pc + 12) != 0x0c017e74U ||
            _memory.Read32(pc + 16) != 0x00000000U ||
            _memory.Read32(pc + 20) != 0x8e0203f4U ||
            _memory.Read32(pc + 24) != 0x8e03037cU ||
            _memory.Read32(pc + 76) != 0x34420261U ||
            _memory.Read32(pc + 80) != 0xac620000U ||
            _memory.Read32(pc + 84) != 0x8e020280U ||
            _memory.Read32(pc + 124) != 0x34420221U ||
            _memory.Read32(pc + 128) != 0xac620000U ||
            _memory.Read32(pc + 132) != 0x8e02026cU ||
            _memory.Read32(pc + 176) != 0x34630241U ||
            _memory.Read32(pc + 180) != 0xac430000U ||
            _memory.Read32(pc + 184) != 0xac400004U ||
            _memory.Read32(pc + 216) != 0x8fbf0018U ||
            _memory.Read32(pc + 220) != 0x8fb10014U ||
            _memory.Read32(pc + 224) != 0x8fb00010U ||
            _memory.Read32(pc + 228) != 0x03e00008U ||
            _memory.Read32(pc + 232) != 0x27bd0020U)
        {
            return false;
        }

        ulong state = _gpr[16];
        ulong sp = _gpr[29];
        if (!IsMainRamRange(state + 0x26cUL, 4) ||
            !IsMainRamRange(state + 0x280UL, 4) ||
            !IsMainRamRange(state + 0x374UL, 0x84) ||
            !IsMainRamRange(sp + 0x10UL, 12))
        {
            return false;
        }

        ulong decremented = unchecked(_gpr[2] - 1UL);
        uint counter = (uint)decremented;
        _memory.Write32(state + 0x38cUL, counter);

        ulong returnValue = decremented;
        ulong skippedInstructions = 6UL;
        if (counter == 0)
        {
            uint extra = _memory.Read32(state + 0x3f4UL);
            uint room = _memory.Read32(state + 0x37cUL);
            uint requiredRoom = unchecked(((0u - extra) & 4u) + (extra << 2) + 0x10u);
            if (room < requiredRoom)
                return false;

            uint fifo = _memory.Read32(state + 0x374UL);
            if ((fifo & 3u) != 0 || fifo is < 0xa8200000u or >= 0xa8300000u)
                return false;

            WriteSignedAddress32(fifo, 0x00010261u);
            WriteSignedAddress32(fifo + 4u, _memory.Read32(state + 0x280UL));
            fifo += 8u;
            room -= 8u;
            _memory.Write32(state + 0x374UL, fifo);
            _memory.Write32(state + 0x37cUL, room);

            WriteSignedAddress32(fifo, 0x00010221u);
            WriteSignedAddress32(fifo + 4u, _memory.Read32(state + 0x26cUL));
            fifo += 8u;
            room -= 8u;
            _memory.Write32(state + 0x374UL, fifo);
            _memory.Write32(state + 0x37cUL, room);

            if (extra != 0)
            {
                if (room < 8)
                    return false;

                WriteSignedAddress32(fifo, 0x00010241u);
                WriteSignedAddress32(fifo + 4u, 0);
                fifo += 8u;
                room -= 8u;
                _memory.Write32(state + 0x374UL, fifo);
                _memory.Write32(state + 0x37cUL, room);
            }

            returnValue = _gpr[17];
            skippedInstructions = extra == 0 ? 55UL : 67UL;
        }

        _gpr[2] = returnValue;
        _gpr[31] = SignExtend32(_memory.Read32(sp + 0x18UL));
        _gpr[17] = SignExtend32(_memory.Read32(sp + 0x14UL));
        _gpr[16] = SignExtend32(_memory.Read32(sp + 0x10UL));
        _gpr[29] = sp + 0x20UL;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * skippedInstructions);
        _instructionCounter += skippedInstructions;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = _gpr[31];
        return true;
    }

    private bool TryFastPathKnownGauntletGlideTwoWordStatePacket(ulong pc)
    {
        bool afterStackAdjust = pc == 0xffffffff80102520UL;
        bool afterPrologue = pc == 0xffffffff8010253cUL;
        bool afterStateWord = pc == 0xffffffff80102554UL;
        if (!afterStackAdjust && !afterPrologue && !afterStateWord)
            return false;
        const ulong entry = 0xffffffff8010251cUL;
        if (_memory.Read32(entry) != 0x27bdffe0U ||
            _memory.Read32(entry + 0x04UL) != 0x3c028026U ||
            _memory.Read32(entry + 0x08UL) != 0xafb00010U ||
            _memory.Read32(entry + 0x0cUL) != 0x8c502c8cU ||
            _memory.Read32(entry + 0x10UL) != 0xafbf0018U ||
            _memory.Read32(entry + 0x14UL) != 0xafb10014U ||
            _memory.Read32(entry + 0x18UL) != 0x8e110268U ||
            _memory.Read32(entry + 0x1cUL) != 0x2402fff0U ||
            _memory.Read32(entry + 0x20UL) != 0x02228824U ||
            _memory.Read32(entry + 0x24UL) != 0x24020007U ||
            _memory.Read32(entry + 0x28UL) != 0x10820003U ||
            _memory.Read32(entry + 0x2cUL) != 0x36230001U ||
            _memory.Read32(entry + 0x38UL) != 0xae110268U ||
            _memory.Read32(entry + 0x5cUL) != 0x3c030001U ||
            _memory.Read32(entry + 0x60UL) != 0x8e020374U ||
            _memory.Read32(entry + 0x64UL) != 0x34630219U ||
            _memory.Read32(entry + 0x68UL) != 0xac430000U ||
            _memory.Read32(entry + 0x6cUL) != 0xac510004U ||
            _memory.Read32(entry + 0x88UL) != 0x8fbf0018U ||
            _memory.Read32(entry + 0x98UL) != 0x03e00008U ||
            _memory.Read32(entry + 0x9cUL) != 0x27bd0020U)
        {
            return false;
        }

        ulong returnAddress = _gpr[31];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x000e0000UL or > 0x00110000UL)
            return false;

        ulong state = afterStackAdjust ? SignExtend32(_memory.Read32(0xffffffff80262c8cUL)) : _gpr[16];
        if (state != 0xffffffff80262d64UL ||
            !IsMainRamRange(state + 0x268UL, 4) ||
            !IsMainRamRange(state + 0x374UL, 12))
        {
            return false;
        }

        NormalizeGlideFifoState(state);
        uint room = _memory.Read32(state + 0x37cUL);
        if (room < 8)
            return false;

        uint fifo = _memory.Read32(state + 0x374UL);
        if ((fifo & 3u) != 0 || fifo is < 0xa8200000u or >= 0xa8300000u)
            return false;

        uint selector = (uint)_gpr[4] & 0xffffu;
        uint stateWord = afterStateWord
            ? (uint)_gpr[17]
            : (afterStackAdjust ? _memory.Read32(state + 0x268UL) : (uint)_gpr[17]) & 0xfffffff0u;
        if (!afterStateWord)
            stateWord = selector == 7u ? stateWord | 1u : stateWord | 1u | (selector << 1);

        uint nextFifo = fifo + 8u;
        uint nextRoom = room - 8u;
        _memory.Write32(state + 0x268UL, stateWord);
        WriteSignedAddress32(fifo, 0x00010219u);
        WriteSignedAddress32(fifo + 4u, stateWord);
        _memory.Write32(state + 0x374UL, nextFifo);
        _memory.Write32(state + 0x37cUL, nextRoom);

        ulong sp = _gpr[29];
        _gpr[2] = SignExtend32(nextFifo);
        _gpr[3] = SignExtend32(nextRoom);
        if ((afterPrologue || afterStateWord) && IsMainRamRange(sp + 0x10UL, 12))
        {
            _gpr[31] = SignExtend32(_memory.Read32(sp + 0x18UL));
            _gpr[17] = SignExtend32(_memory.Read32(sp + 0x14UL));
            _gpr[16] = SignExtend32(_memory.Read32(sp + 0x10UL));
        }
        else
        {
            _gpr[16] = state;
            _gpr[17] = SignExtend32(stateWord);
        }
        _gpr[29] = sp + 0x20UL;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 40UL);
        _instructionCounter += 40UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = _gpr[31];
        return true;
    }

    private void WriteSignedAddress32(uint address, uint value)
        => _memory.Write32(unchecked((ulong)(long)(int)address), value);

    private bool TryFastPathKnownRamCountDelay(ulong pc)
    {
        if (pc != 0xffffffff80010fecUL)
            return false;

        ulong delay = _gpr[4];
        if (delay == 0 || delay > 0x01000000UL)
            return false;

        ulong returnAddress = _gpr[31];
        ulong returnOffset = returnAddress & 0x1fffffffUL;
        if (returnOffset is < 0x00010000UL or > 0x01000000UL)
            return false;

        ulong convertedDelay = delay * 125UL;
        _gpr[2] = _cp0[9];
        _gpr[3] = convertedDelay;
        _gpr[4] = convertedDelay;
        _gpr[0] = 0;
        AdvanceCp0Count(Math.Max(_cp0CountStep, convertedDelay));
        _instructionCounter++;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = returnAddress;
        return true;
    }

    private bool TryFastPathKnownRamFrameTickWait(ulong pc)
    {
        if (pc != 0xffffffff80017310UL)
            return false;
        if (_memory.Read32(pc - 8) != 0x0c005b99U ||
            _memory.Read32(pc - 4) != 0x0000202dU ||
            _memory.Read32(pc) != 0x8e222ed8U ||
            _memory.Read32(pc + 4) != 0x00501023U ||
            _memory.Read32(pc + 8) != 0x2c4200b4U ||
            _memory.Read32(pc + 12) != 0x1440fffaU ||
            _memory.Read32(pc + 16) != 0x00000000U)
            return false;

        ulong tickAddress = _gpr[17] + 0x2ed8UL;
        if ((tickAddress & 0xffffffffUL) != 0x800b2ed8UL)
            return false;
        if (!IsMainRamRange(tickAddress, 4))
            return false;

        uint savedTick = (uint)_gpr[16];
        uint currentTick = _memory.Read32(tickAddress);
        if (unchecked(currentTick - savedTick) >= 0xb4U)
            return false;

        _memory.Write32(tickAddress, savedTick + 0xb4U);
        _gpr[2] = 0;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep * 5UL);
        _instructionCounter += 5UL;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = 0xffffffff80017324UL;
        return true;
    }

    private static bool IsMainRamRange(ulong address, ulong byteLength)
    {
        if (byteLength == 0)
            return false;

        uint physical;
        if (address >= 0xffffffff80000000UL && address <= 0xffffffffbfffffffUL)
            physical = (uint)(address & 0x1fffffffUL);
        else if (address >= 0x80000000UL && address <= 0xbfffffffUL)
            physical = (uint)(address & 0x1fffffffUL);
        else if (address <= 0x1fffffffUL)
            physical = (uint)address;
        else
            return false;

        return physical < 32UL * 1024UL * 1024UL &&
            byteLength <= 32UL * 1024UL * 1024UL - physical;
    }

    private bool FastPathInlineBiosText()
    {
        ulong cursor = _gpr[31];
        if (!TryScanNullTerminatedBytes(ref cursor, 4096))
            return false;

        Pc = (cursor + 7UL) & ~7UL;
        CompleteFastPathStep();
        return true;
    }

    private bool FastPathPointerBiosText()
    {
        ulong cursor = _gpr[4];
        if (!TryScanNullTerminatedBytes(ref cursor, 4096))
            return false;

        _gpr[4] = 0;
        _gpr[7] = cursor;
        Pc = _gpr[31];
        CompleteFastPathStep();
        return true;
    }

    private bool TryScanNullTerminatedBytes(ref ulong cursor, int maxLength)
    {
        for (int i = 0; i < maxLength; i++, cursor++)
        {
            if (_memory.Read8(cursor) == 0)
                return true;
        }

        return false;
    }

    private bool TryReadNullTerminatedAscii(ulong address, int maxLength, out string text)
    {
        Span<byte> buffer = stackalloc byte[Math.Min(maxLength, 128)];
        int length = 0;
        for (; length < buffer.Length; length++)
        {
            byte value = _memory.Read8(address + (uint)length);
            if (value == 0)
            {
                text = Encoding.ASCII.GetString(buffer[..length]);
                return true;
            }
            if (value < 0x20 || value >= 0x7f)
                break;

            buffer[length] = value;
        }

        text = string.Empty;
        return false;
    }

    private void CompleteFastPathStep()
    {
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep);
        _instructionCounter++;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
    }

    private void Execute(ulong pc, uint op)
    {
        if (op == 0)
            return;

        uint opcode = op >> 26;
        int rs = (int)((op >> 21) & 0x1f);
        int rt = (int)((op >> 16) & 0x1f);
        int rd = (int)((op >> 11) & 0x1f);
        short simm = unchecked((short)(op & 0xffff));
        ushort uimm = (ushort)(op & 0xffff);

        switch (opcode)
        {
            case 0x00:
                ExecuteSpecial(pc, op, rs, rt, rd);
                break;
            case 0x01:
                ExecuteRegImm(pc, op, rs, rt, simm);
                break;
            case 0x02:
                QueueBranch((pc & 0xfffffffff0000000UL) | (((ulong)op & 0x03ffffffUL) << 2));
                break;
            case 0x03:
                _gpr[31] = CanonicalizeCodeAddress(pc + 8);
                QueueBranch((pc & 0xfffffffff0000000UL) | (((ulong)op & 0x03ffffffUL) << 2));
                break;
            case 0x04:
                if (_gpr[rs] == _gpr[rt])
                    QueueBranch(pc + 4 + ((ulong)(long)simm << 2));
                break;
            case 0x05:
                if (_gpr[rs] != _gpr[rt])
                    QueueBranch(pc + 4 + ((ulong)(long)simm << 2));
                break;
            case 0x06:
                if (unchecked((long)_gpr[rs]) <= 0)
                    QueueBranch(pc + 4 + ((ulong)(long)simm << 2));
                break;
            case 0x07:
                if (unchecked((long)_gpr[rs]) > 0)
                    QueueBranch(pc + 4 + ((ulong)(long)simm << 2));
                break;
            case 0x08:
            case 0x09:
                _gpr[rt] = (ulong)((long)_gpr[rs] + simm);
                break;
            case 0x0a:
                _gpr[rt] = unchecked((long)_gpr[rs]) < simm ? 1UL : 0UL;
                break;
            case 0x0b:
                _gpr[rt] = _gpr[rs] < unchecked((ulong)(long)simm) ? 1UL : 0UL;
                break;
            case 0x0c:
                _gpr[rt] = _gpr[rs] & uimm;
                break;
            case 0x0d:
                _gpr[rt] = _gpr[rs] | uimm;
                break;
            case 0x0e:
                _gpr[rt] = _gpr[rs] ^ uimm;
                break;
            case 0x0f:
                _gpr[rt] = (ulong)uimm << 16;
                break;
            case 0x10:
                ExecuteCop0(op, rs, rt, rd);
                break;
            case 0x11:
                ExecuteCop1(pc, op, rs, rt, rd);
                break;
            case 0x13:
                ExecuteCop1X(pc, op, rs, rt);
                break;
            case 0x18:
            case 0x19:
                _gpr[rt] = unchecked((ulong)((long)_gpr[rs] + simm));
                break;
            case 0x20:
                _gpr[rt] = unchecked((ulong)(sbyte)_memory.Read8(_gpr[rs] + (ulong)(long)simm));
                break;
            case 0x21:
                _gpr[rt] = unchecked((ulong)(short)_memory.Read16(_gpr[rs] + (ulong)(long)simm));
                break;
            case 0x22:
                _gpr[rt] = LoadWordLeft(_gpr[rs] + (ulong)(long)simm, _gpr[rt]);
                break;
            case 0x23:
                _gpr[rt] = unchecked((ulong)(int)_memory.Read32(_gpr[rs] + (ulong)(long)simm));
                break;
            case 0x24:
                _gpr[rt] = _memory.Read8(_gpr[rs] + (ulong)(long)simm);
                break;
            case 0x25:
                _gpr[rt] = _memory.Read16(_gpr[rs] + (ulong)(long)simm);
                break;
            case 0x26:
                _gpr[rt] = LoadWordRight(_gpr[rs] + (ulong)(long)simm, _gpr[rt]);
                break;
            case 0x27:
                _gpr[rt] = _memory.Read32(_gpr[rs] + (ulong)(long)simm);
                break;
            case 0x28:
                _memory.Write8(_gpr[rs] + (ulong)(long)simm, (byte)_gpr[rt]);
                break;
            case 0x29:
                _memory.Write16(_gpr[rs] + (ulong)(long)simm, (ushort)_gpr[rt]);
                break;
            case 0x2a:
                StoreWordLeft(_gpr[rs] + (ulong)(long)simm, _gpr[rt]);
                break;
            case 0x2b:
                _memory.Write32(_gpr[rs] + (ulong)(long)simm, (uint)_gpr[rt]);
                break;
            case 0x2e:
                StoreWordRight(_gpr[rs] + (ulong)(long)simm, _gpr[rt]);
                break;
            case 0x2f:
                break;
            case 0x31:
                _fpr[rt] = _memory.Read32(_gpr[rs] + (ulong)(long)simm);
                break;
            case 0x35:
                _fpr[rt] = _memory.Read64(_gpr[rs] + (ulong)(long)simm);
                break;
            case 0x37:
                _gpr[rt] = _memory.Read64(_gpr[rs] + (ulong)(long)simm);
                break;
            case 0x39:
                _memory.Write32(_gpr[rs] + (ulong)(long)simm, (uint)_fpr[rt]);
                break;
            case 0x3d:
                _memory.Write64(_gpr[rs] + (ulong)(long)simm, _fpr[rt]);
                break;
            case 0x3f:
                _memory.Write64(_gpr[rs] + (ulong)(long)simm, _gpr[rt]);
                break;
            case 0x14:
                ExecuteBranchLikely(pc, simm, _gpr[rs] == _gpr[rt]);
                break;
            case 0x15:
                ExecuteBranchLikely(pc, simm, _gpr[rs] != _gpr[rt]);
                break;
            case 0x16:
                ExecuteBranchLikely(pc, simm, unchecked((long)_gpr[rs]) <= 0);
                break;
            case 0x17:
                ExecuteBranchLikely(pc, simm, unchecked((long)_gpr[rs]) > 0);
                break;
            default:
                HaltUnsupported(pc, op, $"opcode {opcode:x2}");
                break;
        }
    }

    private void ExecuteSpecial(ulong pc, uint op, int rs, int rt, int rd)
    {
        int sa = (int)((op >> 6) & 0x1f);
        uint funct = op & 0x3f;
        switch (funct)
        {
            case 0x00:
                _gpr[rd] = (uint)_gpr[rt] << sa;
                break;
            case 0x02:
                _gpr[rd] = (uint)_gpr[rt] >> sa;
                break;
            case 0x03:
                _gpr[rd] = (ulong)((int)(uint)_gpr[rt] >> sa);
                break;
            case 0x04:
                _gpr[rd] = (uint)_gpr[rt] << (int)(_gpr[rs] & 0x1f);
                break;
            case 0x06:
                _gpr[rd] = (uint)_gpr[rt] >> (int)(_gpr[rs] & 0x1f);
                break;
            case 0x07:
                _gpr[rd] = (ulong)((int)(uint)_gpr[rt] >> (int)(_gpr[rs] & 0x1f));
                break;
            case 0x08:
                QueueBranch(_gpr[rs]);
                break;
            case 0x09:
                _gpr[rd == 0 ? 31 : rd] = CanonicalizeCodeAddress(pc + 8);
                QueueBranch(_gpr[rs]);
                break;
            case 0x0a:
                if (_gpr[rt] == 0)
                    _gpr[rd] = _gpr[rs];
                break;
            case 0x0b:
                if (_gpr[rt] != 0)
                    _gpr[rd] = _gpr[rs];
                break;
            case 0x0f:
                break;
            case 0x10:
                _gpr[rd] = _hi;
                break;
            case 0x11:
                _hi = _gpr[rs];
                break;
            case 0x12:
                _gpr[rd] = _lo;
                break;
            case 0x13:
                _lo = _gpr[rs];
                break;
            case 0x14:
                _gpr[rd] = _gpr[rt] << (int)(_gpr[rs] & 0x3f);
                break;
            case 0x16:
                _gpr[rd] = _gpr[rt] >> (int)(_gpr[rs] & 0x3f);
                break;
            case 0x17:
                _gpr[rd] = (ulong)((long)_gpr[rt] >> (int)(_gpr[rs] & 0x3f));
                break;
            case 0x18:
                {
                    long product = (long)(int)(uint)_gpr[rs] * (int)(uint)_gpr[rt];
                    _lo = (uint)product;
                    _hi = (uint)(product >> 32);
                    break;
                }
            case 0x19:
                {
                    ulong product = (ulong)(uint)_gpr[rs] * (uint)_gpr[rt];
                    _lo = (uint)product;
                    _hi = (uint)(product >> 32);
                    break;
                }
            case 0x1a:
                Divide32(rs, rt, signed: true);
                break;
            case 0x1b:
                Divide32(rs, rt, signed: false);
                break;
            case 0x1c:
                {
                    long left = unchecked((long)_gpr[rs]);
                    long right = unchecked((long)_gpr[rt]);
                    Int128 product = (Int128)left * right;
                    _lo = (ulong)product;
                    _hi = (ulong)(product >> 64);
                    break;
                }
            case 0x1d:
                {
                    UInt128 product = (UInt128)_gpr[rs] * _gpr[rt];
                    _lo = (ulong)product;
                    _hi = (ulong)(product >> 64);
                    break;
                }
            case 0x1e:
                Divide64(rs, rt, signed: true);
                break;
            case 0x1f:
                Divide64(rs, rt, signed: false);
                break;
            case 0x20:
                _gpr[rd] = SignExtend32((uint)(_gpr[rs] + _gpr[rt]));
                break;
            case 0x21:
            case 0x2d:
                _gpr[rd] = _gpr[rs] + _gpr[rt];
                break;
            case 0x22:
                _gpr[rd] = SignExtend32((uint)(_gpr[rs] - _gpr[rt]));
                break;
            case 0x23:
            case 0x2f:
                _gpr[rd] = _gpr[rs] - _gpr[rt];
                break;
            case 0x24:
                _gpr[rd] = _gpr[rs] & _gpr[rt];
                break;
            case 0x25:
                _gpr[rd] = _gpr[rs] | _gpr[rt];
                break;
            case 0x26:
                _gpr[rd] = _gpr[rs] ^ _gpr[rt];
                break;
            case 0x27:
                _gpr[rd] = ~(_gpr[rs] | _gpr[rt]);
                break;
            case 0x2a:
                _gpr[rd] = unchecked((long)_gpr[rs]) < unchecked((long)_gpr[rt]) ? 1UL : 0UL;
                break;
            case 0x2b:
                _gpr[rd] = _gpr[rs] < _gpr[rt] ? 1UL : 0UL;
                break;
            case 0x38:
                _gpr[rd] = _gpr[rt] << sa;
                break;
            case 0x3a:
                _gpr[rd] = _gpr[rt] >> sa;
                break;
            case 0x3b:
                _gpr[rd] = (ulong)((long)_gpr[rt] >> sa);
                break;
            case 0x3c:
                _gpr[rd] = _gpr[rt] << (sa + 32);
                break;
            case 0x3e:
                _gpr[rd] = _gpr[rt] >> (sa + 32);
                break;
            case 0x3f:
                _gpr[rd] = (ulong)((long)_gpr[rt] >> (sa + 32));
                break;
            default:
                HaltUnsupported(pc, op, $"special {funct:x2}");
                break;
        }
    }

    private void Divide32(int rs, int rt, bool signed)
    {
        uint dividendRaw = (uint)_gpr[rs];
        uint divisorRaw = (uint)_gpr[rt];
        if (divisorRaw == 0)
            return;

        if (signed)
        {
            int dividend = unchecked((int)dividendRaw);
            int divisor = unchecked((int)divisorRaw);
            if (dividend == int.MinValue && divisor == -1)
            {
                _lo = dividendRaw;
                _hi = 0;
                return;
            }

            _lo = (uint)(dividend / divisor);
            _hi = (uint)(dividend % divisor);
            return;
        }

        _lo = dividendRaw / divisorRaw;
        _hi = dividendRaw % divisorRaw;
    }

    private static ulong SignExtend32(uint value)
        => unchecked((ulong)(long)(int)value);

    private static ulong CanonicalizeCodeAddress(ulong address)
        => (address & 0xffffffff00000000UL) == 0 && (address & 0x80000000UL) != 0
            ? SignExtend32((uint)address)
            : address;

    private ulong LoadWordLeft(ulong address, ulong oldValue)
    {
        ulong aligned = address & ~3UL;
        uint mem = _memory.Read32(aligned);
        uint oldWord = (uint)oldValue;
        int shift = 8 * (int)(~address & 3UL);
        uint mask = uint.MaxValue << shift;
        return SignExtend32((oldWord & ~mask) | (mem << shift));
    }

    private ulong LoadWordRight(ulong address, ulong oldValue)
    {
        ulong aligned = address & ~3UL;
        uint mem = _memory.Read32(aligned);
        uint oldWord = (uint)oldValue;
        int shift = 8 * (int)(address & 3UL);
        uint mask = uint.MaxValue >> shift;
        return SignExtend32((oldWord & ~mask) | (mem >> shift));
    }

    private void StoreWordLeft(ulong address, ulong value)
    {
        ulong aligned = address & ~3UL;
        uint oldMem = _memory.Read32(aligned);
        uint word = (uint)value;
        int shift = 8 * (int)(~address & 3UL);
        uint mask = uint.MaxValue >> shift;
        _memory.Write32(aligned, (oldMem & ~mask) | ((word >> shift) & mask));
    }

    private void StoreWordRight(ulong address, ulong value)
    {
        ulong aligned = address & ~3UL;
        uint oldMem = _memory.Read32(aligned);
        uint word = (uint)value;
        int shift = 8 * (int)(address & 3UL);
        uint mask = uint.MaxValue << shift;
        _memory.Write32(aligned, (oldMem & ~mask) | ((word << shift) & mask));
    }

    private void Divide64(int rs, int rt, bool signed)
    {
        ulong divisorRaw = _gpr[rt];
        if (divisorRaw == 0)
            return;

        if (signed)
        {
            long dividend = unchecked((long)_gpr[rs]);
            long divisor = unchecked((long)divisorRaw);
            if (dividend == long.MinValue && divisor == -1)
            {
                _lo = unchecked((ulong)dividend);
                _hi = 0;
                return;
            }

            _lo = unchecked((ulong)(dividend / divisor));
            _hi = unchecked((ulong)(dividend % divisor));
            return;
        }

        _lo = _gpr[rs] / divisorRaw;
        _hi = _gpr[rs] % divisorRaw;
    }

    private void ExecuteRegImm(ulong pc, uint op, int rs, int rt, short simm)
    {
        long signed = unchecked((long)_gpr[rs]);
        bool take = rt switch
        {
            0x00 => signed < 0,
            0x01 => signed >= 0,
            0x02 => signed < 0,
            0x03 => signed >= 0,
            0x10 => signed < 0,
            0x11 => signed >= 0,
            0x12 => signed < 0,
            0x13 => signed >= 0,
            _ => false
        };

        if (rt is 0x10 or 0x11 or 0x12 or 0x13)
            _gpr[31] = CanonicalizeCodeAddress(pc + 8);

        if (take)
        {
            QueueBranch(pc + 4 + ((ulong)(long)simm << 2));
        }
        else if (rt is 0x02 or 0x03 or 0x12 or 0x13)
        {
            OverrideNextPc(pc + 8);
        }

        if (rt is not (0x00 or 0x01 or 0x02 or 0x03 or 0x10 or 0x11 or 0x12 or 0x13))
            HaltUnsupported(pc, op, $"regimm {rt:x2}");
    }

    private void ExecuteCop0(uint op, int rs, int rt, int rd)
    {
        switch (rs)
        {
            case 0x00: // mfc0
                _gpr[rt] = unchecked((ulong)(long)(int)ReadCp0(rd));
                break;
            case 0x01: // dmfc0
                _gpr[rt] = _cp0[rd];
                break;
            case 0x04: // mtc0
                WriteCp0(rd, unchecked((uint)_gpr[rt]));
                break;
            case 0x05: // dmtc0
                WriteCp0(rd, _gpr[rt]);
                break;
            case 0x10:
                ExecuteCop0Operation(op);
                break;
            default:
                HaltUnsupported(Pc, op, $"cop0 rs={rs:x2}");
                break;
        }
    }

    private ulong ReadCp0(int register)
    {
        return register == 9 ? _cp0[9] : _cp0[register];
    }

    private void AdvanceCp0Count(ulong delta)
    {
        if (delta == 0)
            return;

        _memory.AdvanceNileClock(delta);

        uint oldCount = (uint)_cp0[9];
        uint compare = (uint)_cp0[11];
        ulong ticksUntilCompare = compare >= oldCount
            ? compare - (ulong)oldCount
            : 0x1_0000_0000UL - oldCount + compare;

        _cp0[9] = (uint)(oldCount + delta);
        if (ticksUntilCompare != 0 && delta >= ticksUntilCompare)
            _timerInterruptPending = true;
    }

    private void WriteCp0(int register, ulong value)
    {
        switch (register)
        {
            case 11: // Compare
                _cp0[11] = (uint)value;
                _timerInterruptPending = false;
                UpdateInterruptPendingBits();
                break;
            case 12: // Status
                _cp0[12] = (uint)value & Cp0StatusWriteMask;
                break;
            case 13: // Cause
                _cp0[13] = (_cp0[13] & ~Cp0CauseSoftwareInterruptMask) | (value & Cp0CauseSoftwareInterruptMask);
                break;
            case 14: // EPC
            case 30: // ErrorEPC
                _cp0[register] = CanonicalizeCodeAddress(value);
                break;
            case 16: // Config
                _cp0[16] = (_cp0[16] & ~Cp0ConfigWriteMask) | (value & Cp0ConfigWriteMask);
                break;
            default:
                _cp0[register] = value;
                break;
        }
    }

    private void ExecuteCop0Operation(uint op)
    {
        uint funct = op & 0x3f;
        switch (funct)
        {
            case 0x01: // tlbr
            case 0x02: // tlbwi
            case 0x06: // tlbwr
            case 0x08: // tlbp
                break;
            case 0x18: // eret
                if ((_cp0[12] & Cp0StatusErl) != 0)
                {
                    _cp0[12] &= ~Cp0StatusErl;
                    OverrideNextPc(CanonicalizeCodeAddress(_cp0[30]));
                }
                else
                {
                    _cp0[12] &= ~Cp0StatusExl;
                    OverrideNextPc(CanonicalizeCodeAddress(_cp0[14]));
                }
                break;
            default:
                HaltUnsupported(Pc, op, $"cop0 op {funct:x2}");
                break;
        }
    }

    private bool TryEnterPendingInterrupt(ulong pc)
    {
        if ((_cp0[12] & Cp0StatusIe) == 0 || (_cp0[12] & (Cp0StatusExl | Cp0StatusErl)) != 0)
            return false;

        ulong pending = _cp0[13] & _cp0[12] & Cp0CauseInterruptPendingMask;
        if (pending == 0)
            return false;

        if (_enableRuntimeInterruptBridge && IsRuntimeCodeAddress(pc))
        {
            // The copied runtime enables Vegas interrupts before the Nile/IOASIC IRQ
            // controller is complete. Let polling paths advance instead of entering
            // the game's fatal "unhandled exception" path during bringup.
            _timerInterruptPending = false;
            _cp0[13] &= ~Cp0CauseInterruptPendingMask;
            return false;
        }

        _cp0[14] = CanonicalizeCodeAddress(pc);
        _cp0[13] &= ~Cp0CauseExceptionCodeMask;
        _cp0[12] |= Cp0StatusExl;
        _gpr[0] = 0;
        AdvanceCp0Count(_cp0CountStep);
        _instructionCounter++;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;
        Pc = (_cp0[12] & Cp0StatusBev) != 0
            ? 0xffffffffbfc00380UL
            : 0xffffffff80000180UL;
        return true;
    }

    private static bool IsRuntimeCodeAddress(ulong pc)
    {
        ulong physical = pc & 0x1fffffffUL;
        return physical is >= 0x00000000UL and < 0x01000000UL;
    }

    private void UpdateInterruptPendingBits()
    {
        ulong software = _cp0[13] & Cp0CauseSoftwareInterruptMask;
        ulong hardware = _memory.GetCpuInterruptPendingMask() & 0x0000fc00UL;
        if (_timerInterruptPending)
            hardware |= Cp0CauseTimerInterrupt;
        _cp0[13] = (_cp0[13] & ~Cp0CauseInterruptPendingMask) | software | hardware;
    }

    private void ExecuteCop1(ulong pc, uint op, int rs, int rt, int rd)
    {
        int fd = (int)((op >> 6) & 0x1f);
        uint funct = op & 0x3f;
        switch (rs)
        {
            case 0x00: // mfc1
                _gpr[rt] = (uint)_fpr[rd];
                break;
            case 0x01: // dmfc1
                _gpr[rt] = _fpr[rd];
                break;
            case 0x02: // cfc1
                _gpr[rt] = _fcr[rd];
                break;
            case 0x04: // mtc1
                _fpr[rd] = (uint)_gpr[rt];
                break;
            case 0x05: // dmtc1
                _fpr[rd] = _gpr[rt];
                break;
            case 0x06: // ctc1
                _fcr[rd] = (uint)_gpr[rt];
                break;
            case 0x08: // bc1
                ExecuteCop1Branch(pc, rt, unchecked((short)op));
                break;
            case 0x10: // S-format operations
                ExecuteCop1SingleFormat(pc, op, rt, rd, fd, funct);
                break;
            case 0x11: // D-format operations
                ExecuteCop1DoubleFormat(pc, op, rt, rd, fd, funct);
                break;
            case 0x14: // W-format operations
                ExecuteCop1WordFormat(pc, op, rd, fd, funct);
                break;
            default:
                HaltUnsupported(pc, op, $"cop1 rs={rs:x2}");
                break;
        }
    }

    private void ExecuteCop1X(ulong pc, uint op, int rs, int rt)
    {
        int fr = rs;
        int ft = rt;
        int fs = (int)((op >> 11) & 0x1f);
        int fd = (int)((op >> 6) & 0x1f);
        uint funct = op & 0x3f;
        ulong address = _gpr[rs] + _gpr[rt];

        switch (funct)
        {
            case 0x00: // lwxc1
                _fpr[fd] = _memory.Read32(address);
                break;
            case 0x01: // ldxc1
                _fpr[fd] = _memory.Read64(address);
                break;
            case 0x08: // swxc1
                _memory.Write32(address, (uint)_fpr[fd]);
                break;
            case 0x09: // sdxc1
                _memory.Write64(address, _fpr[fd]);
                break;
            case 0x0f: // prefx
                break;
            case 0x20: // madd.s
                WriteCop1XSingle(fd, ReadSingle(fs) * ReadSingle(ft) + ReadSingle(fr));
                break;
            case 0x21: // madd.d
                WriteCop1XDouble(fd, ReadDouble(fs) * ReadDouble(ft) + ReadDouble(fr));
                break;
            case 0x28: // msub.s
                WriteCop1XSingle(fd, ReadSingle(fs) * ReadSingle(ft) - ReadSingle(fr));
                break;
            case 0x29: // msub.d
                WriteCop1XDouble(fd, ReadDouble(fs) * ReadDouble(ft) - ReadDouble(fr));
                break;
            case 0x30: // nmadd.s
                WriteCop1XSingle(fd, -(ReadSingle(fs) * ReadSingle(ft) + ReadSingle(fr)));
                break;
            case 0x31: // nmadd.d
                WriteCop1XDouble(fd, -(ReadDouble(fs) * ReadDouble(ft) + ReadDouble(fr)));
                break;
            case 0x38: // nmsub.s
                WriteCop1XSingle(fd, -(ReadSingle(fs) * ReadSingle(ft) - ReadSingle(fr)));
                break;
            case 0x39: // nmsub.d
                WriteCop1XDouble(fd, -(ReadDouble(fs) * ReadDouble(ft) - ReadDouble(fr)));
                break;
            default:
                HaltUnsupported(pc, op, $"cop1x {funct:x2}");
                break;
        }
    }

    private float ReadSingle(int register)
        => BitConverter.UInt32BitsToSingle((uint)_fpr[register]);

    private double ReadDouble(int register)
        => BitConverter.Int64BitsToDouble(unchecked((long)_fpr[register]));

    private void WriteCop1XSingle(int register, float value)
        => _fpr[register] = BitConverter.SingleToUInt32Bits(value);

    private void WriteCop1XDouble(int register, double value)
        => _fpr[register] = BitConverter.DoubleToUInt64Bits(value);

    private void ExecuteCop1Branch(ulong pc, int rt, short simm)
    {
        bool condition = (_fcr[31] & (1u << 23)) != 0;
        bool take = rt switch
        {
            0x00 or 0x02 => !condition,
            0x01 or 0x03 => condition,
            _ => false
        };

        if (take)
            QueueBranch(pc + 4 + ((ulong)(long)simm << 2));
        else if (rt is 0x02 or 0x03)
            OverrideNextPc(pc + 8);

        if (rt is not (0x00 or 0x01 or 0x02 or 0x03))
            HaltUnsupported(pc, (uint)(0x11 << 26), $"bc1 {rt:x2}");
    }

    private void ExecuteCop1SingleFormat(ulong pc, uint op, int ft, int fs, int fd, uint funct)
    {
        float value = BitConverter.UInt32BitsToSingle((uint)_fpr[fs]);
        float other = BitConverter.UInt32BitsToSingle((uint)_fpr[ft]);
        switch (funct)
        {
            case 0x00: // add.s
                _fpr[fd] = BitConverter.SingleToUInt32Bits(value + other);
                break;
            case 0x01: // sub.s
                _fpr[fd] = BitConverter.SingleToUInt32Bits(value - other);
                break;
            case 0x02: // mul.s
                _fpr[fd] = BitConverter.SingleToUInt32Bits(value * other);
                break;
            case 0x03: // div.s
                _fpr[fd] = BitConverter.SingleToUInt32Bits(value / other);
                break;
            case 0x05: // abs.s
                _fpr[fd] = (uint)_fpr[fs] & 0x7fffffffu;
                break;
            case 0x06: // mov.s
                _fpr[fd] = (uint)_fpr[fs];
                break;
            case 0x07: // neg.s
                _fpr[fd] = (uint)_fpr[fs] ^ 0x80000000u;
                break;
            case 0x0c: // round.w.s
                _fpr[fd] = unchecked((uint)(int)MathF.Round(value));
                break;
            case 0x0d: // trunc.w.s
                _fpr[fd] = unchecked((uint)(int)MathF.Truncate(value));
                break;
            case 0x0e: // ceil.w.s
                _fpr[fd] = unchecked((uint)(int)MathF.Ceiling(value));
                break;
            case 0x0f: // floor.w.s
                _fpr[fd] = unchecked((uint)(int)MathF.Floor(value));
                break;
            case 0x11: // movf.s/movt.s
                if (GetCop1Condition() == ((ft & 1) != 0))
                    _fpr[fd] = (uint)_fpr[fs];
                break;
            case 0x20: // cvt.s.s
                _fpr[fd] = (uint)_fpr[fs];
                break;
            case 0x21: // cvt.d.s
                _fpr[fd] = BitConverter.DoubleToUInt64Bits(value);
                break;
            case 0x24: // cvt.w.s
                _fpr[fd] = unchecked((uint)(int)value);
                break;
            case 0x32: // c.eq.s
                SetCop1Condition(value == other);
                break;
            case 0x3c: // c.lt.s
                SetCop1Condition(value < other);
                break;
            case 0x3e: // c.le.s
                SetCop1Condition(value <= other);
                break;
            default:
                HaltUnsupported(pc, op, $"cop1 s-fmt {funct:x2}");
                break;
        }
    }

    private void ExecuteCop1DoubleFormat(ulong pc, uint op, int ft, int fs, int fd, uint funct)
    {
        double value = BitConverter.Int64BitsToDouble(unchecked((long)_fpr[fs]));
        double other = BitConverter.Int64BitsToDouble(unchecked((long)_fpr[ft]));
        switch (funct)
        {
            case 0x00: // add.d
                _fpr[fd] = BitConverter.DoubleToUInt64Bits(value + other);
                break;
            case 0x01: // sub.d
                _fpr[fd] = BitConverter.DoubleToUInt64Bits(value - other);
                break;
            case 0x02: // mul.d
                _fpr[fd] = BitConverter.DoubleToUInt64Bits(value * other);
                break;
            case 0x03: // div.d
                _fpr[fd] = BitConverter.DoubleToUInt64Bits(value / other);
                break;
            case 0x05: // abs.d
                _fpr[fd] = _fpr[fs] & 0x7fffffffffffffffUL;
                break;
            case 0x06: // mov.d
                _fpr[fd] = _fpr[fs];
                break;
            case 0x07: // neg.d
                _fpr[fd] = _fpr[fs] ^ 0x8000000000000000UL;
                break;
            case 0x0c: // round.w.d
                _fpr[fd] = unchecked((uint)(int)Math.Round(value));
                break;
            case 0x0d: // trunc.w.d
                _fpr[fd] = unchecked((uint)(int)Math.Truncate(value));
                break;
            case 0x0e: // ceil.w.d
                _fpr[fd] = unchecked((uint)(int)Math.Ceiling(value));
                break;
            case 0x0f: // floor.w.d
                _fpr[fd] = unchecked((uint)(int)Math.Floor(value));
                break;
            case 0x11: // movf.d/movt.d
                if (GetCop1Condition() == ((ft & 1) != 0))
                    _fpr[fd] = _fpr[fs];
                break;
            case 0x20: // cvt.s.d
                _fpr[fd] = BitConverter.SingleToUInt32Bits((float)value);
                break;
            case 0x21: // cvt.d.d
                _fpr[fd] = _fpr[fs];
                break;
            case 0x24: // cvt.w.d
                _fpr[fd] = unchecked((uint)(int)value);
                break;
            case 0x32: // c.eq.d
                SetCop1Condition(value == other);
                break;
            case 0x3c: // c.lt.d
                SetCop1Condition(value < other);
                break;
            case 0x3e: // c.le.d
                SetCop1Condition(value <= other);
                break;
            default:
                HaltUnsupported(pc, op, $"cop1 d-fmt {funct:x2}");
                break;
        }
    }

    private void SetCop1Condition(bool condition)
    {
        const uint fcc0 = 1u << 23;
        _fcr[31] = condition ? _fcr[31] | fcc0 : _fcr[31] & ~fcc0;
    }

    private bool GetCop1Condition()
    {
        const uint fcc0 = 1u << 23;
        return (_fcr[31] & fcc0) != 0;
    }

    private void ExecuteCop1WordFormat(ulong pc, uint op, int fs, int fd, uint funct)
    {
        int value = unchecked((int)(uint)_fpr[fs]);
        switch (funct)
        {
            case 0x20: // cvt.s.w
                _fpr[fd] = BitConverter.SingleToUInt32Bits(value);
                break;
            case 0x21: // cvt.d.w
                _fpr[fd] = BitConverter.DoubleToUInt64Bits(value);
                break;
            default:
                HaltUnsupported(pc, op, $"cop1 w-fmt {funct:x2}");
                break;
        }
    }

    private void QueueBranch(ulong target)
    {
        _hasPendingBranch = true;
        _pendingBranchTarget = CanonicalizeCodeAddress(target);
    }

    private void ExecuteBranchLikely(ulong pc, short simm, bool take)
    {
        if (take)
            QueueBranch(pc + 4 + ((ulong)(long)simm << 2));
        else
            OverrideNextPc(pc + 8);
    }

    private void OverrideNextPc(ulong target)
    {
        _hasImmediatePcOverride = true;
        _immediatePcOverride = CanonicalizeCodeAddress(target);
    }

    private void HaltUnsupported(ulong pc, uint op, string reason)
    {
        _halted = true;
        Console.WriteLine($"[GAUNTDL:CPU] halt pc={pc:x16} op={op:x8} reason={reason}");
        Console.WriteLine($"[GAUNTDL:CPU] ra={_gpr[31]:x16} sp={_gpr[29]:x16} gp={_gpr[28]:x16} k0={_gpr[26]:x16} k1={_gpr[27]:x16}");
        Console.WriteLine($"[GAUNTDL:CPU] status={_cp0[12]:x16} cause={_cp0[13]:x16} epc={_cp0[14]:x16} errorepc={_cp0[30]:x16}");
    }

    private void TraceInstruction(ulong pc, uint op)
    {
        TraceRuntimeLogCall(pc);
        if (!ShouldTrace(pc))
            return;

        _traceInstructionCount++;
        Console.WriteLine(
            $"[GAUNTDL:CPU] #{_instructionCounter} pc={pc:x16} op={op:x8} {DisassembleBrief(op)} " +
            $"a0={_gpr[4]:x16} a1={_gpr[5]:x16} v0={_gpr[2]:x16} v1={_gpr[3]:x16} " +
            $"t0={_gpr[8]:x16} t1={_gpr[9]:x16} s0={_gpr[16]:x16} s1={_gpr[17]:x16} s2={_gpr[18]:x16} " +
            $"s3={_gpr[19]:x16} s4={_gpr[20]:x16} s5={_gpr[21]:x16} s6={_gpr[22]:x16} s7={_gpr[23]:x16} ra={_gpr[31]:x16} " +
            $"t5={_gpr[13]:x16} t6={_gpr[14]:x16} t7={_gpr[15]:x16} t8={_gpr[24]:x16} gp={_gpr[28]:x16} fp={_gpr[30]:x16} " +
            $"st={_cp0[12]:x16} cause={_cp0[13]:x16} epc={_cp0[14]:x16} errorepc={_cp0[30]:x16}");
    }

    private bool ShouldTrace(ulong pc)
    {
        if (!_traceEnabled)
            return false;
        if (_traceInstructionCount >= _traceInstructionLimit)
            return false;
        if (_tracePcMin.HasValue && pc < _tracePcMin.Value)
            return false;
        if (_tracePcMax.HasValue && pc > _tracePcMax.Value)
            return false;
        if (_traceRa.HasValue && _gpr[31] != _traceRa.Value)
            return false;
        return true;
    }

    private void TraceRuntimeLogCall(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        if (offset is 0x000e30a0UL or 0x000e3378UL)
        {
            _runtimeTextCallCount++;
            _lastRuntimeTextPc = pc;
            _lastRuntimeTextRa = _gpr[31];
            string text = offset == 0x000e30a0UL
                ? ReadAsciiTraceString(_gpr[4], 160)
                : PickRuntimeTextArgument();
            if (LooksLikeRuntimeText(text))
                LastRuntimeText = text;
            if (!_traceRuntimeLog || _runtimeLogTraceCount >= 96)
                return;

            _runtimeLogTraceCount++;
            Console.WriteLine(
                $"[GAUNTDL:TEXT] pc={pc:x16} ra={_gpr[31]:x16} a0={_gpr[4]:x16} a1={_gpr[5]:x16} " +
                $"a2={_gpr[6]:x16} a3={_gpr[7]:x16} text=\"{text}\" stable=\"{LastRuntimeText}\"");
            return;
        }

        if (!_traceRuntimeLog || _runtimeLogTraceCount >= 96)
            return;

        if (offset is not (0x000fb57cUL or 0x000fbcc0UL or 0x000fcf70UL))
            return;

        _runtimeLogTraceCount++;
        Console.WriteLine(
            $"[GAUNTDL:LOG] pc={pc:x16} ra={_gpr[31]:x16} a0={_gpr[4]:x16} a1={_gpr[5]:x16} " +
            $"a2={_gpr[6]:x16} a3={_gpr[7]:x16} v0={_gpr[2]:x16} v1={_gpr[3]:x16} " +
            $"s0={_gpr[16]:x16} s1={_gpr[17]:x16} text=\"{ReadAsciiTraceString(_gpr[5], 160)}\"");
    }

    private string PickRuntimeTextArgument()
    {
        for (int register = 4; register <= 7; register++)
        {
            string text = ReadAsciiTraceString(_gpr[register], 160);
            if (LooksLikeRuntimeText(text))
                return text;
        }

        return "";
    }

    private static bool LooksLikeRuntimeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (text.All(ch => ch is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f'))
            return false;

        int printable = 0;
        foreach (char ch in text)
        {
            if (ch is >= ' ' and <= '~')
                printable++;
        }

        int meaningful = 0;
        foreach (char ch in text)
        {
            if (ch is >= '0' and <= '9' ||
                ch is >= 'A' and <= 'Z' ||
                ch is >= 'a' and <= 'z' ||
                ch == ' ')
            {
                meaningful++;
            }
        }

        return printable >= 4 && meaningful >= 4;
    }

    private static string DisassembleBrief(uint op)
    {
        if (op == 0)
            return "nop";

        uint opcode = op >> 26;
        return opcode switch
        {
            0x00 => $"special.{op & 0x3f:x2}",
            0x01 => ((op >> 16) & 0x1f) switch
            {
                0x00 => "bltz",
                0x01 => "bgez",
                0x02 => "bltzl",
                0x03 => "bgezl",
                0x10 => "bltzal",
                0x11 => "bgezal",
                0x12 => "bltzall",
                0x13 => "bgezall",
                uint rt => $"regimm.{rt:x2}"
            },
            0x02 => "j",
            0x03 => "jal",
            0x04 => "beq",
            0x05 => "bne",
            0x06 => "blez",
            0x07 => "bgtz",
            0x08 => "addi",
            0x09 => "addiu",
            0x0a => "slti",
            0x0b => "sltiu",
            0x0c => "andi",
            0x0d => "ori",
            0x0e => "xori",
            0x0f => "lui",
            0x10 => "cop0",
            0x11 => "cop1",
            0x18 => "daddi",
            0x19 => "daddiu",
            0x20 => "lb",
            0x21 => "lh",
            0x22 => "lwl",
            0x23 => "lw",
            0x24 => "lbu",
            0x25 => "lhu",
            0x26 => "lwr",
            0x27 => "lwu",
            0x28 => "sb",
            0x29 => "sh",
            0x2a => "swl",
            0x2b => "sw",
            0x2e => "swr",
            0x2f => "cache",
            0x31 => "lwc1",
            0x35 => "ldc1",
            0x37 => "ld",
            0x39 => "swc1",
            0x3d => "sdc1",
            0x3f => "sd",
            0x14 => "beql",
            0x15 => "bnel",
            0x16 => "blezl",
            0x17 => "bgtzl",
            _ => $"op.{opcode:x2}"
        };
    }

    private static int ParsePositiveInt(string name, int fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : fallback;
    }

    private static int ParseStepBudget()
    {
        const int defaultBudget = 2048;
        const int bringupFastBudget = 200_000;
        string? raw = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME");
        if (int.TryParse(raw, out int parsed) && parsed > 0)
            return parsed;

        return GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME_FAST_DEFAULT")
            ? bringupFastBudget
            : defaultBudget;
    }

    private static ulong ParseGlideConfigInt(string? raw, ulong fallback)
    {
        if (!uint.TryParse(raw, out uint parsed))
            return fallback;

        return parsed;
    }

    private static ulong? ParseOptionalHexUlong(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];

        return ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out ulong parsed)
            ? parsed
            : null;
    }
}

internal sealed class VegasMemoryMap
{
    private const ulong ResetRomBase = 0xffffffffbfc00000UL;
    private const uint ResetRomPhysicalBase = 0x1fc00000;
    private const uint NileRegisterPhysicalBase = 0x1fa00000;
    private const int NileRegisterSize = 0x400;
    private const uint NileUartLineStatusOffset = 0x328;
    private const byte NileUartTransmitReady = 0x60;
    private const uint NilePciConfigAliasOffset = 0x200;
    private const uint NileChipSelect2ConfigOffset = 0x010;
    private const uint NilePciWindow0Offset = 0x060;
    private const uint NilePciInit0Offset = 0x0f0;
    private const uint NileInterruptControlOffset = 0x088;
    private const uint NileInterruptStatus0Offset = 0x090;
    private const uint NileInterruptStatus1Offset = 0x098;
    private const uint NileInterruptClearOffset = 0x0a0;
    private const uint NileTimer0ControlOffset = 0x1c0;
    private const uint NileTimerStride = 0x10;
    private const uint NileTimerControlBitsOffset = 0x04;
    private const uint NileTimerCounterOffset = 0x08;
    private const int NileTimerInterruptBase = 5;
    private const ushort NilePciInterruptC = 1 << 10;
    private const ushort NilePciInterruptD = 1 << 11;
    private const ulong FpgaConfigBase = 0x00000000a1600000UL;
    private const int MainRamSize = 32 * 1024 * 1024;
    private const uint UnmappedReadValue = 0xffffffffu;
    private const int PciTypeIo = 0x2;
    private const int PciTypeMemory = 0x6;
    private const int PciTypeConfig = 0x0a;
    private static readonly int[] GauntletDarkLegacyIoasicShuffleMap =
    {
        0x0c, 0x0d, 0x0e, 0x0f,
        0x00, 0x01, 0x02, 0x03,
        0x07, 0x08, 0x09, 0x0b,
        0x0a, 0x05, 0x06, 0x04
    };

    private readonly List<VegasMemoryRange> _ranges = new();
    private readonly byte[] _mainRam = new byte[MainRamSize];
    private readonly byte[] _nileRegisters = new byte[NileRegisterSize];
    private readonly byte[] _fpgaConfigRegisters = new byte[4];
    private readonly byte[] _cpuIoRegisters = new byte[4];
    private readonly ushort[] _ioasicRegisters = new ushort[16];
    private readonly byte[] _ioasicPicSerialData = new byte[16];
    private readonly byte[] _ioasicPicBuffer = new byte[16];
    private readonly byte[] _ioasicPicNvram = new byte[0x100];
    private readonly byte[] _ioasicPicTimeBuffer = new byte[8];
    private readonly VegasIdePciDevice _idePci = new();
    private readonly VegasVoodooPciDevice _voodooPci = new();
    private readonly bool _traceEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_MEM") == "1";
    private readonly bool _traceWritesOnly = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_MEM_WRITES_ONLY") == "1";
    private readonly string? _traceTargetFilter = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_MEM_TARGET");
    private readonly TraceAddressFilter[] _traceAddressFilters = ParseTraceAddressFilters(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_MEM_ADDRESS"));
    private readonly bool _enableRd0DmaQioComplete = GauntletDarkLegacyAdapter.IsBringupFixEnabled("EUTHERDRIVE_GAUNTDL_FIX_RD0_DMA_QIO_COMPLETE");
    private readonly ushort? _ioasicPort0Override = ParseOptionalHexUshort(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_IOASIC_PORT0"));
    private readonly bool _traceIoasicInputs = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_IOASIC_INPUTS") == "1";
    private readonly bool _traceIoasic = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_IOASIC") == "1";
    private readonly int _traceIoasicLimit = ParseOptionalPositiveInt(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_IOASIC_LIMIT"), 240);
    private readonly bool _traceIoasicPic = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_IOASIC_PIC") == "1";
    private readonly int _traceIoasicPicLimit = ParseOptionalPositiveInt(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_IOASIC_PIC_LIMIT"), 200);
    private readonly ushort? _ioasicSoundStatusOverride = ParseOptionalHexUshort(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_IOASIC_SOUNDSTAT"));
    private readonly ushort? _ioasicSoundInputOverride = ParseOptionalHexUshort(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_IOASIC_SOUNDIN"));
    private readonly int _nileCpuIrqShift = ParseNileCpuIrqShift();
    private readonly byte[] _timekeeperRam = new byte[0x8000];
    private readonly DateTime _timekeeperEpoch = new(1999, 12, 11, 6, 12, 0);
    private ulong _traceCpuPc;
    private ulong _timekeeperReadTicks;
    private int _timekeeperWatchdogFrameCountdown;
    private bool _timekeeperWatchdogResetRequested;
    private bool _timekeeperInitialized;
    private bool _cmosUnlocked;
    private byte[] _mainBootRom = Array.Empty<byte>();
    private byte[] _securityPic = Array.Empty<byte>();
    private VegasSioDevice? _sio;
    private IdeDiskDevice? _disk;
    private DcsAudioDevice? _audio;
    private VoodooFacade? _voodoo;
    private ushort _nileIrqState;
    private byte _nileIrqPins;
    private bool _ioasicShuffleActive;
    private ushort _ioasicSoundIrqState;
    private ushort _ioasicPicLatch;
    private byte _ioasicPicState;
    private byte _ioasicPicIndex;
    private byte _ioasicPicTotal;
    private byte _ioasicPicNvramAddress;
    private byte _ioasicPicTimeIndex;
    private bool _ioasicPicTimeJustWritten;
    private bool _ioasicPicNvramInitialized;
    private bool _fpgaConfigSeenLow;
    private bool _fpgaConfigStatusHigh;
    private bool _fpgaConfigDone;
    private bool _ioasicPort0TraceLogged;
    private int _traceIoasicCount;
    private int _traceIoasicPicCount;
    private readonly uint[] _ioasicReadCounts = new uint[16];
    private readonly uint[] _ioasicWriteCounts = new uint[16];
    private int _lastIoasicReadRegister = -1;
    private int _lastIoasicWriteRegister = -1;
    private ushort _lastIoasicReadValue;
    private ushort _lastIoasicWriteValue;
    private int _lastIoasicPhysicalReadRegister = -1;
    private int _lastIoasicPhysicalWriteRegister = -1;

    public VegasMemoryMap()
    {
        AddRange("CS2 Vegas SIO", 2, 0x00000000, 0x00007003);
        AddRange("CS3 analog port", 3, 0x00000000, 0x00000003);
        AddRange("CS4 M48T37 timekeeper/watchdog", 4, 0x00000000, 0x00007fff);
        AddRange("CS5 CPU IO", 5, 0x00000000, 0x00000003);
        AddRange("CS5 unknown read window", 5, 0x00100000, 0x001fffff);
        AddRange("CS6 IOASIC packed", 6, 0x00000000, 0x0000003f);
        AddRange("CS6 ASIC FIFO", 6, 0x00001000, 0x00001003);
        AddRange("CS6 DCS FIFO full", 6, 0x00003000, 0x00003003);
        AddRange("CS6 DCS IDMA address", 6, 0x00005000, 0x00005003);
        AddRange("CS6 DCS IDMA data", 6, 0x00007000, 0x00007003);
        AddRange("CS7 Ethernet", 7, 0x00001000, 0x0000100f);
        AddRange("CS7 DCS IDMA address", 7, 0x00005000, 0x00005003);
        AddRange("CS7 DCS IDMA data", 7, 0x00007000, 0x00007003);
        AddRange("CS8 DUART ttyS01", 8, 0x01000000, 0x0100001f);
        AddRange("CS8 DUART ttyS02", 8, 0x01400000, 0x0140001f);
        AddRange("CS8 parallel UART", 8, 0x01800000, 0x0180001f);
        AddRange("CS8 MPS reset", 8, 0x01c00000, 0x01c00000);
    }

    public void AttachDevices(VegasSioDevice sio, IdeDiskDevice disk, DcsAudioDevice audio, VoodooFacade voodoo)
    {
        _sio = sio;
        _disk = disk;
        _audio = audio;
        _voodoo = voodoo;
        _idePci.AttachDisk(disk);
        _voodooPci.AttachVoodoo(voodoo);
    }

    public void LoadMainBootRom(byte[] mainBootRom) => _mainBootRom = mainBootRom.ToArray();

    public void LoadSecurityPic(byte[] securityPic) => _securityPic = securityPic.ToArray();
    public string DebugStatus
        => $"bram={GetTimekeeperNonDefaultCount()} wdog={_timekeeperWatchdogFrameCountdown} " +
           $"io={GetIoasicDebugStatus()} picbram={GetIoasicPicNvramNonDefaultCount()} pic={_ioasicPicState:X2}/{_ioasicPicIndex}/{_ioasicPicTotal}";

    public void SetTraceCpuPc(ulong pc) => _traceCpuPc = pc;

    public void TryCompleteKnownRd0DmaQio()
    {
        const ulong rd0Object = 0xffffffff800e7810UL;
        const ulong rd0Child = 0xffffffff800e7880UL;
        const ulong homeSectorBuffer = 0xffffffff800f41e0UL;

        if (!_enableRd0DmaQioComplete)
            return;
        if (Read32(rd0Object + 0x0cUL) != 4 ||
            Read32(rd0Object + 0x14UL) != 0 ||
            Read32(rd0Child + 0x1cUL) != 0x80029230U ||
            Read32(rd0Child + 0x20UL) != 0x800e7810U ||
            Read32(rd0Child + 0x24UL) != 2 ||
            Read32(homeSectorBuffer) != 0xfeedf00dU)
        {
            return;
        }

        Write32(rd0Object + 0x14UL, 0x3500U);
        Write32(rd0Child + 0x24UL, 3);
    }

    public bool TryCompleteKnownRd0Stage4BootRead(
        out uint lba,
        out ulong destination,
        out uint firstWord,
        out string reason)
    {
        const ulong rd0Object = 0xffffffff800e7810UL;
        const ulong rd0Child = 0xffffffff800e7880UL;
        const ulong homeSectorBuffer = 0xffffffff800f41e0UL;
        const uint successStatus = 0x3500U;

        lba = 0;
        destination = 0;
        firstWord = 0;
        reason = "";

        if (_disk is null)
        {
            reason = "no-disk";
            return false;
        }

        uint obj0c = Read32(rd0Object + 0x0cUL);
        uint obj14 = Read32(rd0Object + 0x14UL);
        uint obj18 = Read32(rd0Object + 0x18UL);
        uint cb = Read32(rd0Child + 0x1cUL);
        uint owner = Read32(rd0Child + 0x20UL);
        uint stage = Read32(rd0Child + 0x24UL);
        uint homeMagic0 = Read32(homeSectorBuffer);
        uint homeMagic1 = Read32(homeSectorBuffer + 0x38UL);
        if (obj0c != 4 || obj14 != 0 || obj18 != 0 ||
            cb != 0x80029230U || owner != 0x800e7810U || stage != 4 ||
            homeMagic0 != 0xfeedf00dU || homeMagic1 != 0xfe1dfaedU)
        {
            reason =
                $"state obj0c={obj0c:x8} obj14={obj14:x8} obj18={obj18:x8} " +
                $"cb={cb:x8} owner={owner:x8} stage={stage:x8} " +
                $"home={homeMagic0:x8},{homeMagic1:x8}";
            return false;
        }

        ulong qioBuffer = SignExtend32(Read32(rd0Child + 0x2cUL));
        destination = SignExtend32(Read32(rd0Object + 0x2cUL));
        if (!IsMainRamRange(qioBuffer, 0x40) ||
            !IsMainRamRange(destination, (uint)_disk.Geometry.BytesPerSector))
        {
            reason = $"range qioBuffer={qioBuffer:x16} dest={destination:x16}";
            return false;
        }

        lba = Read32(destination + 0x24UL);
        if (lba == 0 || !_disk.TryReadSector(lba, out byte[] sector))
        {
            reason = $"sector dest={destination:x16} lba={lba:x8}";
            return false;
        }

        firstWord = BinaryPrimitives.ReadUInt32LittleEndian(sector);
        if (firstWord != 0xf00dfaceU && firstWord != 0xc0edbabeU && firstWord != 0x464c457fU)
        {
            reason = $"magic lba={lba:x8} first={firstWord:x8}";
            return false;
        }

        for (int i = 0; i < sector.Length; i++)
            Write8(destination + (uint)i, sector[i]);

        Write32(rd0Object + 0x14UL, successStatus);
        Write32(rd0Child + 0x0cUL, successStatus);
        reason = "completed";
        return true;
    }

    public bool TryReadDiskSectorToMemory(
        uint lba,
        ulong destination,
        uint byteCount,
        out uint firstWord,
        out string reason)
    {
        firstWord = 0;
        reason = "";

        if (_disk is null)
        {
            reason = "no-disk";
            return false;
        }
        if (byteCount != _disk.Geometry.BytesPerSector)
        {
            reason = $"byte-count {byteCount:x8}";
            return false;
        }
        if (!IsMainRamRange(destination, byteCount))
        {
            reason = $"range dest={destination:x16}";
            return false;
        }
        if (!_disk.TryReadSector(lba, out byte[] sector))
        {
            reason = $"sector lba={lba:x8}";
            return false;
        }

        firstWord = BinaryPrimitives.ReadUInt32LittleEndian(sector);
        for (int i = 0; i < sector.Length; i++)
            Write8(destination + (uint)i, sector[i]);
        return true;
    }

    public bool TryReadDiskBytesToMemory(
        uint lba,
        ulong destination,
        uint byteCount,
        out uint firstWord,
        out string reason)
    {
        firstWord = 0;
        reason = "";

        if (_disk is null)
        {
            reason = "no-disk";
            return false;
        }
        if (byteCount == 0)
        {
            reason = "byte-count 00000000";
            return false;
        }
        if (!TryTranslatePhysical(destination, out uint physical) ||
            byteCount > _mainRam.Length ||
            physical > _mainRam.Length - byteCount)
        {
            reason = $"range dest={destination:x16} bytes={byteCount:x8}";
            return false;
        }

        uint sectorSize = (uint)_disk.Geometry.BytesPerSector;
        ulong sectorCount = ((ulong)byteCount + sectorSize - 1UL) / sectorSize;
        if ((ulong)lba + sectorCount > _disk.Geometry.TotalSectors)
        {
            reason = $"sector-range lba={lba:x8} sectors={sectorCount:x}";
            return false;
        }

        uint remaining = byteCount;
        uint cursor = physical;
        for (ulong i = 0; i < sectorCount; i++)
        {
            ulong sectorLba = (ulong)lba + i;
            if (!_disk.TryReadSector(sectorLba, out byte[] sector))
            {
                reason = $"sector lba={sectorLba:x8}";
                return false;
            }

            if (i == 0)
                firstWord = BinaryPrimitives.ReadUInt32LittleEndian(sector);

            int count = (int)Math.Min(sectorSize, remaining);
            sector.AsSpan(0, count).CopyTo(_mainRam.AsSpan((int)cursor, count));
            cursor += (uint)count;
            remaining -= (uint)count;
        }

        return true;
    }

    public void MarkIoasicUnlocked()
    {
        _ioasicShuffleActive = true;
        _ioasicRegisters[15] = 0;
        _ioasicRegisters[4] = 0;
        UpdateIoasicIrq();
    }

    public void Reset()
    {
        Array.Clear(_nileRegisters);
        Array.Clear(_fpgaConfigRegisters);
        Array.Clear(_cpuIoRegisters);
        if (!_timekeeperInitialized)
        {
            InitializeTimekeeperRam();
            _timekeeperInitialized = true;
        }
        _timekeeperReadTicks = 0;
        _timekeeperWatchdogFrameCountdown = 0;
        _timekeeperWatchdogResetRequested = false;
        _cmosUnlocked = false;
        Array.Clear(_ioasicRegisters);
        Array.Clear(_ioasicReadCounts);
        Array.Clear(_ioasicWriteCounts);
        _lastIoasicReadRegister = -1;
        _lastIoasicWriteRegister = -1;
        _lastIoasicPhysicalReadRegister = -1;
        _lastIoasicPhysicalWriteRegister = -1;
        _lastIoasicReadValue = 0;
        _lastIoasicWriteValue = 0;
        _traceIoasicCount = 0;
        _traceIoasicPicCount = 0;
        if (!_ioasicPicNvramInitialized)
        {
            Array.Fill(_ioasicPicNvram, (byte)0xff);
            _ioasicPicNvramInitialized = true;
        }
        _ioasicRegisters[8] = 0x0001;
        _ioasicShuffleActive = false;
        _ioasicSoundIrqState = 0x0080;
        ResetIoasicPic();
        GenerateIoasicPicSerialData();
        _fpgaConfigSeenLow = false;
        _fpgaConfigStatusHigh = false;
        _fpgaConfigDone = false;
        _nileIrqState = 0;
        _nileIrqPins = 0;
        _idePci.Reset();
        _voodooPci.Reset();
        UpdateIoasicIrq();
    }

    private void ResetIoasicFromSio()
    {
        _ioasicShuffleActive = false;
        _ioasicSoundIrqState = 0x0080;
        _ioasicRegisters[15] = 0;
        ResetIoasicPic();
        UpdateIoasicIrq();
    }

    public ulong GetCpuInterruptPendingMask()
    {
        UpdateNileInterrupts();
        return (ulong)_nileIrqPins << _nileCpuIrqShift;
    }

    public void AdvanceNileClock(ulong ticks)
    {
        ulong timerTicks = Math.Max(1UL, ticks >> 10);
        for (int timer = 0; timer < 4; timer++)
        {
            uint baseOffset = NileTimer0ControlOffset + (uint)(timer * NileTimerStride);
            uint control = ReadNileRegister32(baseOffset + NileTimerControlBitsOffset);
            if ((control & 1u) == 0)
                continue;

            uint reload = ReadNileRegister32(baseOffset);
            uint counter = ReadNileRegister32(baseOffset + NileTimerCounterOffset);
            ulong period = (ulong)reload + 1UL;
            ulong decrement = period == 0 ? timerTicks : timerTicks % period;
            bool expired = timerTicks >= (ulong)counter + 1UL || (period != 0 && timerTicks >= period);
            ulong next = counter >= decrement
                ? counter - decrement
                : period - ((decrement - counter) % period);
            if (next == period)
                next = 0;
            WriteNileRegister32(baseOffset + NileTimerCounterOffset, (uint)next);
            if (expired)
                _nileIrqState |= (ushort)(1 << (NileTimerInterruptBase + timer));
        }
    }

    public byte Read8(ulong address)
    {
        if (TryReadFpgaConfig8(address, out byte fpgaValue))
        {
            Trace("read8", address, fpgaValue, "FPGA config");
            return fpgaValue;
        }

        if (TryReadBootRomByte(address, out byte romValue))
        {
            Trace("read8", address, romValue, "PCI_ID_NILE:rom");
            return romValue;
        }

        if (TryReadNile8(address, out byte nileValue))
        {
            Trace("read8", address, nileValue, "NILE");
            return nileValue;
        }

        if (TryReadPciWindow8(address, out byte pciValue))
        {
            Trace("read8", address, pciValue, "PCI");
            return pciValue;
        }

        if (TryReadChipSelect8(address, out byte chipSelectValue))
            return chipSelectValue;

        if (TryTranslatePhysical(address, out uint physical) && physical < _mainRam.Length)
        {
            byte value = _mainRam[physical];
            Trace("read8", address, value, "mainram");
            return value;
        }

        Trace("read8", address, 0xff, "unmapped");
        return 0xff;
    }

    public ushort Read16(ulong address)
    {
        if (TryReadPciWindow16(address, out ushort pciValue))
        {
            Trace("read16", address, pciValue, "PCI");
            return pciValue;
        }

        if (TryReadChipSelect16(address, out ushort chipSelectValue))
            return chipSelectValue;

        ushort value = (ushort)(Read8(address) | (Read8(address + 1) << 8));
        return value;
    }

    public uint Read32(ulong address)
    {
        if (TryReadBootRom32(address, out uint value))
        {
            Trace("read32", address, value, "PCI_ID_NILE:rom");
            return value;
        }

        if (TryReadNile32(address, out value))
        {
            Trace("read32", address, value, "NILE");
            return value;
        }

        if (TryReadPciWindow32(address, out value))
        {
            Trace("read32", address, value, "PCI");
            return value;
        }

        if (TryReadChipSelect32(address, out value))
            return value;

        if (TryTranslatePhysical(address, out uint physical) && physical + 3 < _mainRam.Length)
        {
            value = BinaryPrimitives.ReadUInt32LittleEndian(_mainRam.AsSpan((int)physical, 4));
            Trace("read32", address, value, "mainram");
            return value;
        }

        Trace("read32", address, UnmappedReadValue, "unmapped");
        return UnmappedReadValue;
    }

    public ulong Read64(ulong address)
    {
        if (TryReadNile64(address, out ulong nileValue))
        {
            Trace("read64", address, unchecked((uint)nileValue), "NILE");
            return nileValue;
        }

        if (TryReadChipSelect64(address, out ulong chipSelectValue))
            return chipSelectValue;

        if (TryTranslatePhysical(address, out uint physical) && physical + 7 < _mainRam.Length)
        {
            ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_mainRam.AsSpan((int)physical, 8));
            Trace("read64", address, unchecked((uint)value), "mainram");
            return value;
        }

        ulong low = Read32(address);
        ulong high = Read32(address + 4);
        return low | (high << 32);
    }

    public void Write8(ulong address, byte value)
    {
        if (TryWriteFpgaConfig8(address, value))
        {
            Trace("write8", address, value, "FPGA config");
            return;
        }

        if (TryWriteNile8(address, value))
        {
            Trace("write8", address, value, "NILE");
            return;
        }

        if (TryWritePciWindow8(address, value))
        {
            Trace("write8", address, value, "PCI");
            return;
        }

        if (TryWriteChipSelect8(address, value))
            return;

        if (TryTranslatePhysical(address, out uint physical) && physical < _mainRam.Length)
        {
            _mainRam[physical] = value;
            Trace("write8", address, value, "mainram");
            return;
        }

        Trace("write8", address, value, "unmapped");
    }

    public void Write16(ulong address, ushort value)
    {
        if (TryWritePciWindow16(address, value))
        {
            Trace("write16", address, value, "PCI");
            return;
        }

        if (TryWriteChipSelect16(address, value))
            return;

        Write8(address, (byte)value);
        Write8(address + 1, (byte)(value >> 8));
    }

    public void Write32(ulong address, uint value)
    {
        if (TryWriteNile32(address, value))
        {
            Trace("write32", address, value, "NILE");
            return;
        }

        if (TryWritePciWindow32(address, value))
        {
            Trace("write32", address, value, "PCI");
            return;
        }

        if (TryWriteChipSelect32(address, value))
            return;

        if (TryTranslatePhysical(address, out uint physical) && physical + 3 < _mainRam.Length)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_mainRam.AsSpan((int)physical, 4), value);
            Trace("write32", address, value, "mainram");
            return;
        }

        Trace("write32", address, value, "unmapped");
    }

    public void Write64(ulong address, ulong value)
    {
        if (TryWriteNile64(address, value))
        {
            Trace("write64", address, unchecked((uint)value), "NILE");
            return;
        }

        if (TryWriteChipSelect64(address, value))
            return;

        if (TryTranslatePhysical(address, out uint physical) && physical + 7 < _mainRam.Length)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_mainRam.AsSpan((int)physical, 8), value);
            Trace("write64", address, unchecked((uint)value), "mainram");
            return;
        }

        Write32(address, (uint)value);
        Write32(address + 4, (uint)(value >> 32));
    }

    private bool TryReadNile8(ulong address, out byte value)
    {
        if (!TryGetNileRegisterOffset(address, out uint offset))
        {
            value = 0;
            return false;
        }

        if (offset == NileUartLineStatusOffset)
        {
            value = NileUartTransmitReady;
            return true;
        }

        if (offset is >= NilePciConfigAliasOffset and < NilePciConfigAliasOffset + 0x100)
        {
            value = (byte)(ReadPciConfig32(DecodePciConfigAlias(offset) & ~3u) >> (int)((offset & 3) * 8));
            return true;
        }

        if (offset is >= NileInterruptStatus0Offset and < NileInterruptStatus1Offset + 8)
            UpdateNileInterrupts();

        value = _nileRegisters[offset];
        return true;
    }

    private bool TryReadNile32(ulong address, out uint value)
    {
        if (!TryGetNileRegisterOffset(address, out uint offset) || offset + 3 >= NileRegisterSize)
        {
            value = UnmappedReadValue;
            return false;
        }

        if (offset is >= NilePciConfigAliasOffset and < NilePciConfigAliasOffset + 0x100)
        {
            value = ReadPciConfig32(DecodePciConfigAlias(offset));
            return true;
        }

        if (offset is >= NileInterruptStatus0Offset and < NileInterruptStatus1Offset + 8)
            UpdateNileInterrupts();

        value = BinaryPrimitives.ReadUInt32LittleEndian(_nileRegisters.AsSpan((int)offset, 4));
        return true;
    }

    private bool TryReadNile64(ulong address, out ulong value)
    {
        if (!TryGetNileRegisterOffset(address, out uint offset) || offset + 7 >= NileRegisterSize)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(_nileRegisters.AsSpan((int)offset, 8));
        return true;
    }

    private bool TryWriteNile8(ulong address, byte value)
    {
        if (!TryGetNileRegisterOffset(address, out uint offset))
            return false;

        _nileRegisters[offset] = value;
        return true;
    }

    private bool TryWriteNile32(ulong address, uint value)
    {
        if (!TryGetNileRegisterOffset(address, out uint offset) || offset + 3 >= NileRegisterSize)
            return false;

        if (offset is >= NilePciConfigAliasOffset and < NilePciConfigAliasOffset + 0x100)
        {
            WritePciConfig32(DecodePciConfigAlias(offset), value);
            return true;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(_nileRegisters.AsSpan((int)offset, 4), value);
        if (offset == NileInterruptClearOffset)
            _nileIrqState &= (ushort)~(value & ~0x0f00u);
        if (offset is >= NileInterruptControlOffset and < NileInterruptControlOffset + 8 ||
            offset == NileInterruptClearOffset)
            UpdateNileInterrupts();
        return true;
    }

    private bool TryReadPciWindow8(ulong address, out byte value)
    {
        if (!TryTranslatePciWindow(address, out int type, out uint pciAddress))
        {
            value = 0;
            return false;
        }

        value = type switch
        {
            PciTypeIo => _idePci.ReadIo8(pciAddress),
            PciTypeMemory => ReadPciMemory8(pciAddress),
            PciTypeConfig => (byte)(ReadPciConfig32(DecodePciType0ConfigAddress(pciAddress) & ~3u) >> (int)((pciAddress & 3) * 8)),
            _ => 0xff
        };
        return true;
    }

    private bool TryReadPciWindow16(ulong address, out ushort value)
    {
        if (!TryTranslatePciWindow(address, out int type, out uint pciAddress))
        {
            value = 0;
            return false;
        }

        value = type switch
        {
            PciTypeIo => _idePci.ReadIo16(pciAddress),
            PciTypeMemory => (ushort)(ReadPciMemory8(pciAddress) | (ReadPciMemory8(pciAddress + 1) << 8)),
            PciTypeConfig => (ushort)(ReadPciConfig32(DecodePciType0ConfigAddress(pciAddress) & ~3u) >> (int)((pciAddress & 2) * 8)),
            _ => 0xffff
        };
        return true;
    }

    private bool TryReadPciWindow32(ulong address, out uint value)
    {
        if (!TryTranslatePciWindow(address, out int type, out uint pciAddress))
        {
            value = 0;
            return false;
        }

        value = type switch
        {
            PciTypeIo => _idePci.ReadIo32(pciAddress),
            PciTypeMemory => ReadPciMemory32(pciAddress),
            PciTypeConfig => ReadPciConfig32(DecodePciType0ConfigAddress(pciAddress)),
            _ => UnmappedReadValue
        };
        return true;
    }

    private bool TryWritePciWindow8(ulong address, byte value)
    {
        if (!TryTranslatePciWindow(address, out int type, out uint pciAddress))
            return false;

        switch (type)
        {
            case PciTypeIo:
                _idePci.WriteIo8(pciAddress, value, this);
                break;
            case PciTypeMemory:
                WritePciMemory8(pciAddress, value);
                break;
            case PciTypeConfig:
                WritePciConfigByte(pciAddress, value);
                break;
        }

        return true;
    }

    private bool TryWritePciWindow16(ulong address, ushort value)
    {
        if (!TryTranslatePciWindow(address, out int type, out uint pciAddress))
            return false;

        switch (type)
        {
            case PciTypeIo:
                _idePci.WriteIo16(pciAddress, value, this);
                break;
            case PciTypeMemory:
                WritePciMemory8(pciAddress, (byte)value);
                WritePciMemory8(pciAddress + 1, (byte)(value >> 8));
                break;
            case PciTypeConfig:
                WritePciConfigHalf(pciAddress, value);
                break;
        }

        return true;
    }

    private bool TryWritePciWindow32(ulong address, uint value)
    {
        if (!TryTranslatePciWindow(address, out int type, out uint pciAddress))
            return false;

        switch (type)
        {
            case PciTypeIo:
                _idePci.WriteIo32(pciAddress, value, this);
                break;
            case PciTypeMemory:
                WritePciMemory32(pciAddress, value);
                break;
            case PciTypeConfig:
                WritePciConfig32(DecodePciType0ConfigAddress(pciAddress), value);
                break;
        }

        return true;
    }

    private bool TryTranslatePciWindow(ulong address, out int type, out uint pciAddress)
    {
        type = 0;
        pciAddress = 0;
        if (!TryTranslatePhysical(address, out uint physical))
            return false;

        for (int index = 0; index < 2; index++)
        {
            uint pciWindow = ReadNileRegister32(NilePciWindow0Offset + (uint)(index * 8));
            int mask = (int)(pciWindow & 0x0f);
            if (mask <= 0)
                continue;

            ulong size = mask >= 5 ? 1UL << (36 - mask) : 0x1_0000_0000UL;
            uint windowStart = pciWindow & 0xffe00000u;
            ulong offset = physical - (ulong)windowStart;
            if (offset >= size)
                continue;

            ulong pciMask = size - 1;
            uint pciInit = ReadNileRegister32(NilePciInit0Offset + (uint)(index * 8));
            type = (int)(pciInit & 0x0e);
            pciAddress = (uint)((pciInit & ~(uint)pciMask) | (uint)(offset & pciMask));
            return true;
        }

        return false;
    }

    private uint ReadPciMemory32(uint pciAddress)
    {
        if (_voodooPci.TryReadMemory32(pciAddress, out uint voodooValue))
            return voodooValue;
        if (pciAddress + 3 < _mainRam.Length)
            return BinaryPrimitives.ReadUInt32LittleEndian(_mainRam.AsSpan((int)pciAddress, 4));
        return UnmappedReadValue;
    }

    private byte ReadPciMemory8(uint pciAddress)
    {
        if (_voodooPci.TryReadMemory32(pciAddress & ~3u, out uint voodooValue))
            return (byte)(voodooValue >> (int)((pciAddress & 3) * 8));
        return pciAddress < _mainRam.Length ? _mainRam[pciAddress] : (byte)0xff;
    }

    private void WritePciMemory32(uint pciAddress, uint value)
    {
        if (_voodooPci.TryWriteMemory32(pciAddress, value))
            return;
        if (pciAddress + 3 < _mainRam.Length)
            BinaryPrimitives.WriteUInt32LittleEndian(_mainRam.AsSpan((int)pciAddress, 4), value);
    }

    private void WritePciMemory8(uint pciAddress, byte value)
    {
        if (_voodooPci.TryWriteMemory8(pciAddress, value))
            return;
        if (pciAddress < _mainRam.Length)
            _mainRam[pciAddress] = value;
    }

    internal void WritePciMemoryFromDevice(uint pciAddress, ReadOnlySpan<byte> data)
    {
        if (pciAddress >= _mainRam.Length)
            return;

        int count = Math.Min(data.Length, _mainRam.Length - (int)pciAddress);
        data[..count].CopyTo(_mainRam.AsSpan((int)pciAddress, count));
    }

    internal uint ReadPciMemoryFromDevice32(uint pciAddress)
        => ReadPciMemory32(pciAddress);

    private bool IsMainRamRange(ulong address, uint length)
    {
        if (!TryTranslatePhysical(address, out uint physical))
            return false;
        return length <= _mainRam.Length && physical <= _mainRam.Length - length;
    }

    private static ulong SignExtend32(uint value)
        => (ulong)(long)(int)value;

    private uint ReadNileRegister32(uint offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(_nileRegisters.AsSpan((int)offset, 4));

    private void WriteNileRegister32(uint offset, uint value)
        => BinaryPrimitives.WriteUInt32LittleEndian(_nileRegisters.AsSpan((int)offset, 4), value);

    private void UpdateNileInterrupts()
    {
        if (_sio?.InterruptLine == true)
            _nileIrqState |= NilePciInterruptC;
        else
            _nileIrqState &= unchecked((ushort)~NilePciInterruptC);

        if (_idePci.InterruptLine)
            _nileIrqState |= NilePciInterruptD;
        else
            _nileIrqState &= unchecked((ushort)~NilePciInterruptD);

        uint lowControl = ReadNileRegister32(NileInterruptControlOffset);
        uint highControl = ReadNileRegister32(NileInterruptControlOffset + 4);
        uint status0 = 0;
        uint status1 = 0;
        byte pins = 0;

        for (int i = 0; i < 8; i++)
        {
            if ((_nileIrqState & (1 << i)) == 0 || ((lowControl >> (4 * i + 3)) & 1) == 0)
                continue;

            int vector = (int)((lowControl >> (4 * i)) & 7);
            if (vector >= 6)
                continue;

            pins |= (byte)(1 << vector);
            if ((vector & 1) == 0)
                status0 |= 1u << i;
            else
                status0 |= 1u << (i + 16);
        }

        for (int i = 0; i < 8; i++)
        {
            if ((_nileIrqState & (1 << (i + 8))) == 0 || ((highControl >> (4 * i + 3)) & 1) == 0)
                continue;

            int vector = (int)((highControl >> (4 * i)) & 7);
            if (vector >= 6)
                continue;

            pins |= (byte)(1 << vector);
            if ((vector & 1) == 0)
                status0 |= 1u << (i + 8);
            else
                status0 |= 1u << (i + 24);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(_nileRegisters.AsSpan((int)NileInterruptStatus0Offset, 4), status0);
        BinaryPrimitives.WriteUInt32LittleEndian(_nileRegisters.AsSpan((int)(NileInterruptStatus0Offset + 4), 4), status1);
        BinaryPrimitives.WriteUInt32LittleEndian(_nileRegisters.AsSpan((int)NileInterruptStatus1Offset, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(_nileRegisters.AsSpan((int)(NileInterruptStatus1Offset + 4), 4), 0);
        _nileIrqPins = pins;
    }

    private uint ReadPciConfig32(uint address)
    {
        int device = (int)((address >> 11) & 0x1f);
        return device switch
        {
            3 => _voodooPci.ReadConfig32(address),
            5 => _idePci.ReadConfig32(address),
            _ => 0xffffffffu
        };
    }

    private void WritePciConfig32(uint address, uint value)
    {
        int device = (int)((address >> 11) & 0x1f);
        switch (device)
        {
            case 3:
                _voodooPci.WriteConfig32(address, value);
                break;
            case 5:
                _idePci.WriteConfig32(address, value);
                break;
        }
    }

    private static uint DecodePciConfigAlias(uint nileOffset)
        => nileOffset & 0xfc;

    private static uint DecodePciType0ConfigAddress(uint pciAddress)
    {
        for (int dev = 0; dev < 10; dev++)
        {
            if (((pciAddress >> (21 + dev)) & 1) != 0)
                return (uint)(dev << 11) | (pciAddress & 0xfc);
        }

        return 0xfffffffcu;
    }

    private void WritePciConfigByte(uint pciAddress, byte value)
    {
        uint offset = DecodePciType0ConfigAddress(pciAddress) & ~3u;
        uint current = ReadPciConfig32(offset);
        int shift = (int)((pciAddress & 3) * 8);
        uint merged = (current & ~(0xffu << shift)) | ((uint)value << shift);
        WritePciConfig32(offset, merged);
    }

    private void WritePciConfigHalf(uint pciAddress, ushort value)
    {
        uint offset = DecodePciType0ConfigAddress(pciAddress) & ~3u;
        uint current = ReadPciConfig32(offset);
        int shift = (int)((pciAddress & 2) * 8);
        uint merged = (current & ~(0xffffu << shift)) | ((uint)value << shift);
        WritePciConfig32(offset, merged);
    }

    private bool TryWriteNile64(ulong address, ulong value)
    {
        if (!TryGetNileRegisterOffset(address, out uint offset) || offset + 7 >= NileRegisterSize)
            return false;

        BinaryPrimitives.WriteUInt64LittleEndian(_nileRegisters.AsSpan((int)offset, 8), value);
        return true;
    }

    private static bool TryGetNileRegisterOffset(ulong address, out uint offset)
    {
        if (TryTranslatePhysical(address, out uint physical) &&
            physical >= NileRegisterPhysicalBase &&
            physical < NileRegisterPhysicalBase + NileRegisterSize)
        {
            offset = physical - NileRegisterPhysicalBase;
            return true;
        }

        offset = 0;
        return false;
    }

    private bool TryReadFpgaConfig8(ulong address, out byte value)
    {
        if (!TryGetFpgaConfigOffset(address, out uint offset))
        {
            value = 0;
            return false;
        }

        value = ReadCpuIo(offset);
        return true;
    }

    private bool TryWriteFpgaConfig8(ulong address, byte value)
    {
        if (!TryGetFpgaConfigOffset(address, out uint offset))
            return false;

        _fpgaConfigRegisters[(int)offset] = value;
        WriteCpuIo(offset, value);
        return true;
    }

    public void MarkFpgaConfigDone()
    {
        _fpgaConfigStatusHigh = true;
        _fpgaConfigDone = true;
        _cpuIoRegisters[2] |= 0x03;
        _cpuIoRegisters[3] |= 0x01;
    }

    private static bool TryGetFpgaConfigOffset(ulong address, out uint offset)
    {
        ulong normalized = address & 0x00000000ffffffffUL;
        if (normalized >= FpgaConfigBase && normalized < FpgaConfigBase + 4)
        {
            offset = (uint)(normalized - FpgaConfigBase);
            return true;
        }

        offset = 0;
        return false;
    }

    public uint ReadChipSelect32(int chipSelect, uint offset)
    {
        if (TryFindRange(chipSelect, offset, out VegasMemoryRange range))
        {
            uint value = ReadChipSelect32Mapped(chipSelect, offset);
            Trace("read32", FormatChipSelectAddress(chipSelect, offset), value, range.Name);
            return value;
        }

        Trace("read32", FormatChipSelectAddress(chipSelect, offset), UnmappedReadValue, $"CS{chipSelect} unmapped");
        return UnmappedReadValue;
    }

    public void WriteChipSelect32(int chipSelect, uint offset, uint value)
    {
        if (TryFindRange(chipSelect, offset, out VegasMemoryRange range))
        {
            Trace("write32", FormatChipSelectAddress(chipSelect, offset), value, range.Name);
            WriteChipSelect32Mapped(chipSelect, offset, value);
            return;
        }

        Trace("write32", FormatChipSelectAddress(chipSelect, offset), value, $"CS{chipSelect} unmapped");
    }

    private bool TryReadChipSelect8(ulong address, out byte value)
    {
        if (!TryTranslateChipSelectWindow(address, out int chipSelect, out uint offset))
        {
            value = 0;
            return false;
        }

        bool mapped = TryFindRange(chipSelect, offset, out VegasMemoryRange range);
        value = mapped ? ReadChipSelectByte(chipSelect, offset) : (byte)0xff;
        Trace("read8", address, value, mapped ? range.Name : $"CS{chipSelect} unmapped");
        return true;
    }

    private bool TryReadChipSelect32(ulong address, out uint value)
    {
        if (!TryTranslateChipSelectWindow(address, out int chipSelect, out uint offset))
        {
            value = UnmappedReadValue;
            return false;
        }

        if (TryFindRange(chipSelect, offset, out VegasMemoryRange range))
        {
            value = ReadChipSelect32Mapped(chipSelect, offset);

            Trace("read32", address, value, range.Name);
        }
        else
        {
            value = UnmappedReadValue;
            Trace("read32", address, value, $"CS{chipSelect} unmapped");
        }

        return true;
    }

    private bool TryReadChipSelect16(ulong address, out ushort value)
    {
        if (!TryTranslateChipSelectWindow(address, out int chipSelect, out uint offset))
        {
            value = 0xffff;
            return false;
        }

        bool mapped = TryFindRange(chipSelect, offset, out VegasMemoryRange range);
        value = mapped ? ReadChipSelect16Mapped(chipSelect, offset) : (ushort)0xffff;
        Trace("read16", address, value, mapped ? range.Name : $"CS{chipSelect} unmapped");
        return true;
    }

    private bool TryReadChipSelect64(ulong address, out ulong value)
    {
        if (!TryTranslateChipSelectWindow(address, out _, out _))
        {
            value = 0;
            return false;
        }

        uint low = Read32(address);
        uint high = Read32(address + 4);
        value = low | ((ulong)high << 32);
        return true;
    }

    private bool TryWriteChipSelect8(ulong address, byte value)
    {
        if (!TryTranslateChipSelectWindow(address, out int chipSelect, out uint offset))
            return false;

        if (TryFindRange(chipSelect, offset, out VegasMemoryRange range))
        {
            WriteChipSelectByte(chipSelect, offset, value);
            Trace("write8", address, value, range.Name);
        }
        else
        {
            Trace("write8", address, value, $"CS{chipSelect} unmapped");
        }

        return true;
    }

    private bool TryWriteChipSelect32(ulong address, uint value)
    {
        if (!TryTranslateChipSelectWindow(address, out int chipSelect, out uint offset))
            return false;

        if (TryFindRange(chipSelect, offset, out VegasMemoryRange range))
        {
            WriteChipSelect32Mapped(chipSelect, offset, value);

            Trace("write32", address, value, range.Name);
        }
        else
        {
            Trace("write32", address, value, $"CS{chipSelect} unmapped");
        }

        return true;
    }

    private bool TryWriteChipSelect16(ulong address, ushort value)
    {
        if (!TryTranslateChipSelectWindow(address, out int chipSelect, out uint offset))
            return false;

        if (TryFindRange(chipSelect, offset, out VegasMemoryRange range))
        {
            WriteChipSelect16Mapped(chipSelect, offset, value);
            Trace("write16", address, value, range.Name);
        }
        else
        {
            Trace("write16", address, value, $"CS{chipSelect} unmapped");
        }

        return true;
    }

    private bool TryWriteChipSelect64(ulong address, ulong value)
    {
        if (!TryTranslateChipSelectWindow(address, out _, out _))
            return false;

        Write32(address, (uint)value);
        Write32(address + 4, (uint)(value >> 32));
        return true;
    }

    private uint ReadChipSelect32Mapped(int chipSelect, uint offset)
    {
        if ((chipSelect == 6 || chipSelect == 7) && (offset & 0xfffffffcu) == 0x00007000)
            return _audio?.ReadIdmaData() ?? UnmappedReadValue;

        if (chipSelect == 4)
        {
            return (uint)(ReadChipSelectByte(chipSelect, offset) |
                (ReadChipSelectByte(chipSelect, offset + 1) << 8) |
                (ReadChipSelectByte(chipSelect, offset + 2) << 16) |
                (ReadChipSelectByte(chipSelect, offset + 3) << 24));
        }

        return ReadChipSelectByte(chipSelect, offset);
    }

    private ushort ReadChipSelect16Mapped(int chipSelect, uint offset)
    {
        if ((chipSelect == 6 || chipSelect == 7) && (offset & 0xfffffffcu) == 0x00007000)
            return (ushort)(_audio?.ReadIdmaData() ?? 0xffffu);

        return (ushort)(ReadChipSelectByte(chipSelect, offset) |
            (ReadChipSelectByte(chipSelect, offset + 1) << 8));
    }

    private void WriteChipSelect32Mapped(int chipSelect, uint offset, uint value)
    {
        if (chipSelect == 6)
        {
            uint aligned = offset & 0xfffffffcu;
            if (aligned == 0x00001000)
            {
                _audio?.WriteFifo((ushort)value);
                return;
            }

            if (aligned == 0x00003000)
            {
                _audio?.SetFifoForceFull();
                return;
            }

            if (aligned == 0x00005000)
            {
                _audio?.WriteIdmaAddress(value);
                return;
            }

            if (aligned == 0x00007000)
            {
                _audio?.WriteIdmaData(value);
                return;
            }
        }

        if (chipSelect == 7)
        {
            uint aligned = offset & 0xfffffffcu;
            if (aligned == 0x00005000)
            {
                _audio?.WriteIdmaAddress(value);
                return;
            }

            if (aligned == 0x00007000)
            {
                _audio?.WriteIdmaData(value);
                return;
            }
        }

        if (chipSelect == 4)
        {
            if (_cmosUnlocked)
            {
                WriteTimekeeper(offset, (byte)value);
                WriteTimekeeper(offset + 1, (byte)(value >> 8));
                WriteTimekeeper(offset + 2, (byte)(value >> 16));
                WriteTimekeeper(offset + 3, (byte)(value >> 24));
                _cmosUnlocked = false;
            }
            return;
        }

        WriteChipSelectByte(chipSelect, offset, (byte)value);
    }

    private void WriteChipSelect16Mapped(int chipSelect, uint offset, ushort value)
    {
        if (chipSelect == 6)
        {
            uint aligned = offset & 0xfffffffeu;
            if (aligned == 0x00001000)
            {
                _audio?.WriteFifo(value);
                return;
            }

            if (aligned == 0x00003000)
            {
                _audio?.SetFifoForceFull();
                return;
            }

            if (aligned == 0x00005000)
            {
                _audio?.WriteIdmaAddress(value);
                return;
            }

            if (aligned == 0x00007000)
            {
                _audio?.WriteIdmaData16(value);
                return;
            }
        }

        if (chipSelect == 7)
        {
            uint aligned = offset & 0xfffffffeu;
            if (aligned == 0x00005000)
            {
                _audio?.WriteIdmaAddress(value);
                return;
            }

            if (aligned == 0x00007000)
            {
                _audio?.WriteIdmaData16(value);
                return;
            }
        }

        WriteChipSelectByte(chipSelect, offset, (byte)value);
        WriteChipSelectByte(chipSelect, offset + 1, (byte)(value >> 8));
    }

    private byte ReadChipSelectByte(int chipSelect, uint offset)
    {
        uint aligned = offset & 0xfffffffcu;
        if ((chipSelect == 6 || chipSelect == 7) && aligned == 0x00007000)
        {
            uint value = _audio?.ReadIdmaData() ?? UnmappedReadValue;
            return (byte)(value >> (int)((offset & 3) * 8));
        }

        if (chipSelect == 2)
        {
            if ((offset >> 12) == 0)
                _cpuIoRegisters[3] |= 0x01;
            return _sio?.Read(offset) ?? 0xff;
        }

        if (chipSelect == 4)
            return ReadTimekeeper(offset);

        if (chipSelect == 5)
            return ReadCpuIo(offset);

        if (chipSelect == 6 && offset < 0x40)
            return ReadIoasicPackedByte(offset);

        return 0xff;
    }

    private void WriteChipSelectByte(int chipSelect, uint offset, byte value)
    {
        if (TryWriteDcsChipSelectLane(chipSelect, offset, value))
            return;

        if (chipSelect == 2)
        {
            switch (offset >> 12)
            {
                case 0:
                    if ((value & 0x01) == 0)
                        ResetIoasicFromSio();
                    break;
                case 6:
                    _cmosUnlocked = true;
                    break;
                case 7:
                    RefreshTimekeeperWatchdog();
                    break;
            }

            _sio?.Write(offset, value);
            return;
        }

        if (chipSelect == 4)
        {
            if (_cmosUnlocked)
            {
                WriteTimekeeper(offset, value);
                _cmosUnlocked = false;
            }
        }
        else if (chipSelect == 5)
            WriteCpuIo(offset, value);
        else if (chipSelect == 6 && offset < 0x40)
            WriteIoasicPackedByte(offset, value);
    }

    private bool TryWriteDcsChipSelectLane(int chipSelect, uint offset, byte value)
    {
        if (chipSelect is not (6 or 7))
            return false;

        uint aligned = offset & 0xfffffffcu;
        int lane = (int)(offset & 3);
        switch (aligned)
        {
            case 0x00001000 when chipSelect == 6:
                _audio?.WriteFifo((ushort)(value << ((lane & 1) * 8)));
                return true;
            case 0x00003000 when chipSelect == 6:
                _audio?.SetFifoForceFull();
                return true;
            case 0x00005000:
                _audio?.WriteIdmaAddress((uint)value << (lane * 8));
                return true;
            case 0x00007000:
                // MAME's 32-bit DSIO handlers still accept narrow accesses through mem_mask.
                _audio?.WriteIdmaData16((ushort)(value << ((lane & 1) * 8)));
                return true;
            default:
                return false;
        }
    }

    private byte ReadTimekeeper(uint offset)
    {
        offset &= 0x7fff;
        if (offset == 0x7ff0)
        {
            byte value = _timekeeperRam[0x7ff0];
            _timekeeperRam[0x7ff0] &= 0x7f;
            return value;
        }

        if (offset < 0x7ff8)
            return _timekeeperRam[(int)offset];

        if (offset == 0x7ff9)
            _timekeeperReadTicks++;

        DateTime now = _timekeeperEpoch.AddSeconds((long)(_timekeeperReadTicks >> 6));
        return offset switch
        {
            0x7ff8 => _timekeeperRam[0x7ff8],
            0x7ff9 => MakeBcd(now.Second),
            0x7ffa => MakeBcd(now.Minute),
            0x7ffb => MakeBcd(now.Hour),
            0x7ffc => MakeBcd((int)now.DayOfWeek + 1),
            0x7ffd => MakeBcd(now.Day),
            0x7ffe => MakeBcd(now.Month),
            0x7fff => MakeBcd(now.Year % 100),
            _ => 0xff
        };
    }

    private void WriteTimekeeper(uint offset, byte value)
    {
        offset &= 0x7fff;
        _timekeeperRam[(int)offset] = value;
        if (offset == 0x7ff7)
            ArmTimekeeperWatchdog(value);
    }

    public void StepFrame()
    {
        if (_timekeeperWatchdogFrameCountdown <= 0)
            return;

        _timekeeperWatchdogFrameCountdown--;
        if (_timekeeperWatchdogFrameCountdown > 0)
            return;

        _timekeeperRam[0x7ff0] |= 0x80;
        if ((_timekeeperRam[0x7ff7] & 0x80) != 0)
        {
            _timekeeperRam[0x7ff7] = 0;
            _timekeeperWatchdogResetRequested = true;
        }
    }

    public bool ConsumeWatchdogResetRequest()
    {
        if (!_timekeeperWatchdogResetRequested)
            return false;

        _timekeeperWatchdogResetRequested = false;
        return true;
    }

    private void ArmTimekeeperWatchdog(byte value)
    {
        if ((value & 0x7f) == 0)
        {
            _timekeeperWatchdogFrameCountdown = 0;
            return;
        }

        int multiplier = (value >> 2) & 0x1f;
        double seconds = (62500 << (2 * (value & 0x03))) / 1_000_000.0 * multiplier;
        _timekeeperWatchdogFrameCountdown = Math.Max(1, (int)Math.Ceiling(seconds * 57.0));
        if (Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_FIX_TIMEKEEPER_WATCHDOG_FAST") == "1")
            _timekeeperWatchdogFrameCountdown = Math.Min(_timekeeperWatchdogFrameCountdown, 3);
    }

    private void RefreshTimekeeperWatchdog()
    {
        byte value = _timekeeperRam[0x7ff7];
        if ((value & 0x7f) != 0)
            ArmTimekeeperWatchdog(value);
    }

    private void InitializeTimekeeperRam()
    {
        Array.Fill(_timekeeperRam, (byte)0xff);
        _timekeeperRam[0x7ff0] = 0x00;
        WriteTimekeeperCounters(_timekeeperEpoch);
    }

    private void WriteTimekeeperCounters(DateTime value)
    {
        _timekeeperRam[0x7ff8] = 0x00;
        _timekeeperRam[0x7ff9] = MakeBcd(value.Second);
        _timekeeperRam[0x7ffa] = MakeBcd(value.Minute);
        _timekeeperRam[0x7ffb] = MakeBcd(value.Hour);
        _timekeeperRam[0x7ffc] = MakeBcd((int)value.DayOfWeek + 1);
        _timekeeperRam[0x7ffd] = MakeBcd(value.Day);
        _timekeeperRam[0x7ffe] = MakeBcd(value.Month);
        _timekeeperRam[0x7fff] = MakeBcd(value.Year % 100);
        _timekeeperRam[0x7ff1] = MakeBcd(value.Year / 100);
    }

    private byte ReadIoasicPackedByte(uint offset)
    {
        int physicalRegister = GetIoasicPackedRegister(offset);
        int register = DecodeIoasicRegister(physicalRegister);
        ushort value = ReadIoasicRegister(register);
        RecordIoasicRead(physicalRegister, register, value);
        return (byte)(value >> (int)((offset & 1) * 8));
    }

    private void WriteIoasicPackedByte(uint offset, byte value)
    {
        int physicalRegister = GetIoasicPackedRegister(offset);
        int register = DecodeIoasicRegister(physicalRegister);
        int shift = (int)((offset & 1) * 8);
        ushort current = _ioasicRegisters[register];
        ushort merged = (ushort)((current & ~(0xff << shift)) | (value << shift));

        if (_ioasicShuffleActive)
            _ioasicRegisters[register] = merged;

        WriteIoasicRegister(register, merged);
        RecordIoasicWrite(physicalRegister, register, merged);
        UpdateIoasicIrq();
    }

    private ushort ReadIoasicRegister(int register)
    {
        if (register == 14)
            UpdateIoasicIrq();

        return register switch
        {
            0 => _ioasicShuffleActive ? ReadIoasicInputPort(0) : (ushort)0x2001,
            1 => ReadIoasicInputPort(1),
            2 => ReadIoasicInputPort(2),
            3 => ReadIoasicInputPort(3),
            10 => ReadIoasicSoundStatus(),
            11 => ReadIoasicSoundInput(),
            13 => ReadIoasicPicRegister(),
            _ => _ioasicRegisters[register]
        };
    }

    private void WriteIoasicRegister(int register, ushort value)
    {
        switch (register)
        {
            case 0:
                if (!_ioasicShuffleActive && (value & 0xff) == 0xe2)
                {
                    _ioasicShuffleActive = true;
                    _ioasicRegisters[15] = 0;
                    _ioasicRegisters[4] = 0;
                }
                break;
            case 4:
                break;
            case 5:
                _ioasicRegisters[6] = (ushort)((value & 0x00ff) | 0x3000);
                break;
            case 8:
                _audio?.ResetLine((value & 1) != 0);
                _audio?.SetFifoReset((value & 4) == 0);
                break;
            case 12:
                WriteIoasicPic((byte)value);
                break;
            case 9:
                _audio?.WriteData(value);
                break;
            case 11:
                _audio?.Ack();
                break;
            case 13:
                break;
            case 15:
                UpdateIoasicIrq();
                break;
        }
    }

    private ushort ReadIoasicSoundStatus()
    {
        if (_ioasicSoundStatusOverride.HasValue)
            return _ioasicSoundStatusOverride.Value;

        ushort value = 0;
        if (_audio is not null)
        {
            value |= (ushort)(((_audio.Control >> 4) ^ 0x40) & 0x00c0);
            value |= (ushort)(_audio.FifoStatus & 0x0038);
            value |= (ushort)(_audio.Data2 & 0xff00);
            return value;
        }

        return 0x0048;
    }

    private ushort ReadIoasicSoundInput()
    {
        if (_ioasicSoundInputOverride.HasValue)
            return _ioasicSoundInputOverride.Value;

        ushort value = _audio?.ReadData() ?? 0x000a;
        _audio?.Ack();
        return value;
    }

    private void RecordIoasicRead(int physicalRegister, int register, ushort value)
    {
        _ioasicReadCounts[register & 0x0f]++;
        _lastIoasicPhysicalReadRegister = physicalRegister & 0x0f;
        _lastIoasicReadRegister = register & 0x0f;
        _lastIoasicReadValue = value;
        TraceIoasic($"read p{physicalRegister & 0x0f:x}->r{register & 0x0f:x} value={value:x4}");
    }

    private void RecordIoasicWrite(int physicalRegister, int register, ushort value)
    {
        _ioasicWriteCounts[register & 0x0f]++;
        _lastIoasicPhysicalWriteRegister = physicalRegister & 0x0f;
        _lastIoasicWriteRegister = register & 0x0f;
        _lastIoasicWriteValue = value;
        TraceIoasic($"write p{physicalRegister & 0x0f:x}->r{register & 0x0f:x} value={value:x4}");
    }

    private string GetIoasicDebugStatus()
    {
        string lastRead = _lastIoasicReadRegister >= 0
            ? $"r{_lastIoasicPhysicalReadRegister:X}->{_lastIoasicReadRegister:X}:{_lastIoasicReadValue:X4}"
            : "r-";
        string lastWrite = _lastIoasicWriteRegister >= 0
            ? $"w{_lastIoasicPhysicalWriteRegister:X}->{_lastIoasicWriteRegister:X}:{_lastIoasicWriteValue:X4}"
            : "w-";
        return $"{(_ioasicShuffleActive ? "shuf" : "raw")} {lastRead}/{lastWrite} " +
               $"rd[10]={_ioasicReadCounts[10]} rd[11]={_ioasicReadCounts[11]} rd[13]={_ioasicReadCounts[13]} " +
               $"wr[12]={_ioasicWriteCounts[12]} wr[15]={_ioasicWriteCounts[15]}";
    }

    private void TraceIoasic(string message)
    {
        if (!_traceIoasic || _traceIoasicCount >= _traceIoasicLimit)
            return;

        Console.WriteLine($"[GAUNTDL:IOASIC] pc={_traceCpuPc:x16} {message}");
        _traceIoasicCount++;
    }

    private void UpdateIoasicIrq()
    {
        ushort intCtl = _ioasicRegisters[15];
        ushort irqBits = 0x2000;
        irqBits |= (ushort)(_ioasicSoundIrqState & 0x00ff);
        irqBits |= (ushort)(_ioasicRegisters[6] & 0x3f00);
        const ushort fifoState = 0x0008;
        if ((fifoState & 0x08) != 0)
            irqBits |= 0x0008;
        if (irqBits != 0)
            irqBits |= 0x0001;

        _ioasicRegisters[14] = irqBits;
        bool asserted = (intCtl & 0x0001) != 0 && (irqBits & intCtl & 0x3ffe) != 0;
        _sio?.SetIoasicIrq(asserted);
    }

    private static int GetIoasicPackedRegister(uint offset)
        => (int)(((offset >> 2) * 2 + ((offset >> 1) & 1)) & 0x0f);

    private int DecodeIoasicRegister(int register)
        => _ioasicShuffleActive ? GauntletDarkLegacyIoasicShuffleMap[register & 0x0f] : register & 0x0f;

    private ushort ReadIoasicInputPort(int port)
    {
        return port switch
        {
            0 => BuildIoasicInputPort0(),
            1 => BuildSystemInputPort(),
            2 => BuildPlayerInputPort12(),
            3 => 0xffff,
            _ => 0xffff
        };
    }

    private ushort BuildIoasicInputPort0()
    {
        ushort value = _ioasicPort0Override ?? 0xffff;
        if (_traceIoasicInputs && !_ioasicPort0TraceLogged)
        {
            int bootSlot = (((value >> 4) & 3) ^ 3);
            Console.WriteLine($"[GAUNTDL:IOASIC] port0={value:x4} bootSlot={bootSlot} pc={_traceCpuPc:x16}");
            _ioasicPort0TraceLogged = true;
        }

        return value;
    }

    private ushort BuildSystemInputPort()
    {
        ushort value = 0xffff;
        return value;
    }

    private ushort BuildPlayerInputPort12()
    {
        ushort value = 0xffff;
        return value;
    }

    private void GenerateIoasicPicSerialData()
    {
        const uint gauntletDarkLegacyUpper = 109;
        const uint serialDigit = 0;
        uint serialNumber = 123_450 + gauntletDarkLegacyUpper * 1_000_000 + (serialDigit & 0x0f);
        Span<byte> digit = stackalloc byte[9];
        digit[0] = (byte)((serialNumber / 100_000_000) % 10);
        digit[1] = (byte)((serialNumber / 10_000_000) % 10);
        digit[2] = (byte)((serialNumber / 1_000_000) % 10);
        digit[3] = (byte)((serialNumber / 100_000) % 10);
        digit[4] = (byte)((serialNumber / 10_000) % 10);
        digit[5] = (byte)((serialNumber / 1_000) % 10);
        digit[6] = (byte)((serialNumber / 100) % 10);
        digit[7] = (byte)((serialNumber / 10) % 10);
        digit[8] = (byte)(serialNumber % 10);

        _ioasicPicSerialData[12] = 0x12;
        _ioasicPicSerialData[13] = 0x34;
        _ioasicPicSerialData[14] = 0;
        _ioasicPicSerialData[15] = 0;

        uint temp = 0x174u * (1999u - 1980u) + 0x1fu * (12u - 1u) + 11u;
        _ioasicPicSerialData[10] = (byte)(temp >> 8);
        _ioasicPicSerialData[11] = (byte)temp;

        temp = (uint)(digit[4] + digit[7] * 10 + digit[1] * 100);
        temp = (temp + 5u * _ioasicPicSerialData[13]) * 0x1bcdu + 0x1f3f0u;
        _ioasicPicSerialData[7] = (byte)temp;
        _ioasicPicSerialData[8] = (byte)(temp >> 8);
        _ioasicPicSerialData[9] = (byte)(temp >> 16);

        temp = (uint)(digit[6] + digit[8] * 10 + digit[0] * 100 + digit[2] * 10000);
        temp = (temp + 2u * _ioasicPicSerialData[13] + _ioasicPicSerialData[12]) * 0x107fu + 0x71e259u;
        _ioasicPicSerialData[3] = (byte)temp;
        _ioasicPicSerialData[4] = (byte)(temp >> 8);
        _ioasicPicSerialData[5] = (byte)(temp >> 16);
        _ioasicPicSerialData[6] = (byte)(temp >> 24);

        temp = (uint)(digit[5] * 10 + digit[3] * 100);
        temp = (temp + _ioasicPicSerialData[12]) * 0x245u + 0x3d74u;
        _ioasicPicSerialData[0] = (byte)temp;
        _ioasicPicSerialData[1] = (byte)(temp >> 8);
        _ioasicPicSerialData[2] = (byte)(temp >> 16);
    }

    private ushort ReadIoasicPicRegister()
    {
        byte data = ReadIoasicPicData();
        byte status = ReadIoasicPicStatus();
        ushort value = (ushort)(data | (status << 8));
        TraceIoasicPic($"read reg=0d value={value:x4} data={data:x2} status={status:x2} latch={_ioasicPicLatch:x4} index={_ioasicPicIndex} total={_ioasicPicTotal} state={_ioasicPicState:x2}");
        return value;
    }

    private void ResetIoasicPic()
    {
        _ioasicPicLatch = 0;
        _ioasicPicState = 0;
        _ioasicPicIndex = 0;
        _ioasicPicTotal = 0;
        _ioasicPicNvramAddress = 0;
        _ioasicPicTimeIndex = 0;
        _ioasicPicTimeJustWritten = false;
        Array.Clear(_ioasicPicBuffer);
        Array.Clear(_ioasicPicTimeBuffer);
    }

    private byte ReadIoasicPicData()
    {
        if ((_ioasicPicLatch & 0x0f00) != 0)
            return (byte)_ioasicPicLatch;
        return _ioasicPicIndex < _ioasicPicTotal ? (byte)0xff : (byte)0;
    }

    private byte ReadIoasicPicStatus()
    {
        if ((_ioasicPicLatch & 0x0f00) == 0)
            return 0;

        _ioasicPicLatch = (ushort)(_ioasicPicLatch >= 0x100 ? _ioasicPicLatch - 0x100 : 0);
        return 1;
    }

    private void WriteIoasicPic(byte data)
    {
        _ioasicPicLatch = (ushort)((data & 0x0f) | 0x0480);
        TraceIoasicPic($"write data={data:x2} latch={_ioasicPicLatch:x4} state={_ioasicPicState:x2} index={_ioasicPicIndex} total={_ioasicPicTotal}");
        if ((data & 0x10) == 0)
            return;

        int command = _ioasicPicState != 0 ? _ioasicPicState & 0x0f : _ioasicPicLatch & 0x0f;
        TraceIoasicPic($"command={command:x1} state={_ioasicPicState:x2} index={_ioasicPicIndex} total={_ioasicPicTotal}");
        switch (command)
        {
            case 0:
                LatchNextIoasicPicByte();
                break;
            case 1:
                if (_ioasicPicIndex < _ioasicPicTotal)
                    LatchNextIoasicPicByte();
                else
                {
                    _ioasicPicSerialData.CopyTo(_ioasicPicBuffer, 0);
                    _ioasicPicTotal = 16;
                    _ioasicPicIndex = 0;
                    TraceIoasicPic($"serial prepared {Convert.ToHexString(_ioasicPicBuffer, 0, _ioasicPicTotal).ToLowerInvariant()}");
                }
                break;
            case 3:
                PrepareIoasicPicClockRead();
                break;
            case 4:
                WriteIoasicPicClockNibble();
                break;
            case 5:
                WriteIoasicPicNvramNibble();
                break;
            case 6:
                ReadIoasicPicNvramCommand();
                break;
            case 8:
                _ioasicPicLatch = (ushort)(0x0400 | (~command & 0xff));
                break;
        }
    }

    private void LatchNextIoasicPicByte()
    {
        if (_ioasicPicIndex < _ioasicPicTotal)
            _ioasicPicLatch = (ushort)(0x0400 | _ioasicPicBuffer[_ioasicPicIndex++]);
        TraceIoasicPic($"latch next latch={_ioasicPicLatch:x4} index={_ioasicPicIndex} total={_ioasicPicTotal}");
    }

    private void TraceIoasicPic(string message)
    {
        if (!_traceIoasicPic || _traceIoasicPicCount >= _traceIoasicPicLimit)
            return;

        Console.WriteLine($"[GAUNTDL:IOASIC:PIC] pc={_traceCpuPc:x16} {message}");
        _traceIoasicPicCount++;
    }

    private void PrepareIoasicPicClockRead()
    {
        _ioasicPicIndex = 0;
        _ioasicPicTotal = 0;
        if (_ioasicPicTimeJustWritten)
        {
            for (int i = 0; i < 7; i++)
                _ioasicPicBuffer[_ioasicPicTotal++] = _ioasicPicTimeBuffer[i];
            return;
        }

        _ioasicPicBuffer[_ioasicPicTotal++] = MakeBcd(0);
        _ioasicPicBuffer[_ioasicPicTotal++] = MakeBcd(0);
        _ioasicPicBuffer[_ioasicPicTotal++] = MakeBcd(12);
        _ioasicPicBuffer[_ioasicPicTotal++] = MakeBcd(6);
        _ioasicPicBuffer[_ioasicPicTotal++] = MakeBcd(11);
        _ioasicPicBuffer[_ioasicPicTotal++] = MakeBcd(12);
        _ioasicPicBuffer[_ioasicPicTotal++] = MakeBcd(1999 - 1900 - 80);
    }

    private void WriteIoasicPicClockNibble()
    {
        if (_ioasicPicState == 0)
        {
            _ioasicPicState = 0x14;
            _ioasicPicTimeIndex = 0;
        }
        else if (_ioasicPicState == 0x14)
        {
            _ioasicPicTimeBuffer[_ioasicPicTimeIndex] = (byte)(_ioasicPicLatch & 0x0f);
            _ioasicPicState = 0x24;
        }
        else if (_ioasicPicState == 0x24)
        {
            _ioasicPicTimeBuffer[_ioasicPicTimeIndex++] |= (byte)((_ioasicPicLatch & 0x0f) << 4);
            if (_ioasicPicTimeIndex < 7)
                _ioasicPicState = 0x14;
            else
            {
                _ioasicPicTimeJustWritten = true;
                _ioasicPicState = 0;
            }
        }
    }

    private void WriteIoasicPicNvramNibble()
    {
        if (_ioasicPicState == 0)
            _ioasicPicState = 0x15;
        else if (_ioasicPicState == 0x15)
        {
            _ioasicPicNvramAddress = (byte)(_ioasicPicLatch & 0x0f);
            _ioasicPicState = 0x25;
        }
        else if (_ioasicPicState == 0x25)
        {
            _ioasicPicNvramAddress |= (byte)((_ioasicPicLatch & 0x0f) << 4);
            _ioasicPicState = 0x35;
        }
        else if (_ioasicPicState == 0x35)
        {
            _ioasicPicNvram[_ioasicPicNvramAddress] = (byte)(_ioasicPicLatch & 0x0f);
            _ioasicPicState = 0x45;
        }
        else if (_ioasicPicState == 0x45)
        {
            _ioasicPicNvram[_ioasicPicNvramAddress] |= (byte)((_ioasicPicLatch & 0x0f) << 4);
            _ioasicPicState = 0;
        }
    }

    private void ReadIoasicPicNvramCommand()
    {
        if (_ioasicPicState == 0)
            _ioasicPicState = 0x16;
        else if (_ioasicPicState == 0x16)
        {
            _ioasicPicNvramAddress = (byte)(_ioasicPicLatch & 0x0f);
            _ioasicPicState = 0x26;
        }
        else if (_ioasicPicState == 0x26)
        {
            _ioasicPicNvramAddress |= (byte)((_ioasicPicLatch & 0x0f) << 4);
            _ioasicPicBuffer[0] = _ioasicPicNvram[_ioasicPicNvramAddress];
            _ioasicPicTotal = 1;
            _ioasicPicIndex = 0;
            _ioasicPicState = 0;
        }
    }

    private static byte MakeBcd(int value)
        => (byte)(((value / 10) << 4) | (value % 10));

    private int GetIoasicPicNvramNonDefaultCount()
    {
        int count = 0;
        foreach (byte value in _ioasicPicNvram)
        {
            if (value != 0xff)
                count++;
        }

        return count;
    }

    private int GetTimekeeperNonDefaultCount()
    {
        int count = 0;
        foreach (byte value in _timekeeperRam)
        {
            if (value != 0xff)
                count++;
        }

        return count;
    }

    private byte ReadCpuIo(uint offset)
    {
        offset &= 3;
        if (offset == 2)
        {
            byte value = (byte)(_cpuIoRegisters[2] & 0xfc);
            if (_fpgaConfigStatusHigh)
                value |= 0x02;
            if (_fpgaConfigDone)
                value |= 0x01;
            return value;
        }

        return _cpuIoRegisters[(int)offset];
    }

    private void WriteCpuIo(uint offset, byte value)
    {
        offset &= 3;
        _cpuIoRegisters[(int)offset] = value;
        if (offset == 1)
        {
            _cpuIoRegisters[2] = (byte)((_cpuIoRegisters[2] & ~0x03) | ((value & 0x01) << 1) | (value & 0x01));
            if ((value & 0x01) == 0)
            {
                _cpuIoRegisters[3] &= unchecked((byte)~0x01);
                _sio?.Reset();
                _fpgaConfigSeenLow = true;
                _fpgaConfigStatusHigh = false;
                _fpgaConfigDone = false;
            }
            else if (_fpgaConfigSeenLow)
            {
                _fpgaConfigStatusHigh = true;
            }
        }
    }

    private bool TryReadBootRom32(ulong address, out uint value)
    {
        value = UnmappedReadValue;
        if (!TryReadBootRomByte(address, out byte b0) ||
            !TryReadBootRomByte(address + 1, out byte b1) ||
            !TryReadBootRomByte(address + 2, out byte b2) ||
            !TryReadBootRomByte(address + 3, out byte b3))
        {
            return false;
        }

        value = (uint)(b0 | (b1 << 8) | (b2 << 16) | (b3 << 24));
        return true;
    }

    private bool TryReadBootRomByte(ulong address, out byte value)
    {
        value = 0xff;
        if (_mainBootRom.Length == 0)
            return false;

        if (!TryTranslatePhysical(address, out uint physical))
            return false;

        if (physical < ResetRomPhysicalBase)
            return false;

        uint offset = (physical - ResetRomPhysicalBase) % (uint)_mainBootRom.Length;

        int index = (int)offset;
        value = _mainBootRom[index];
        return true;
    }

    private static bool TryTranslatePhysical(ulong address, out uint physical)
    {
        if (address >= 0xffffffff80000000UL && address <= 0xffffffffbfffffffUL)
        {
            physical = (uint)(address & 0x1fffffff);
            return true;
        }

        if (address >= 0x80000000UL && address <= 0xbfffffffUL)
        {
            physical = (uint)(address & 0x1fffffff);
            return true;
        }

        if (address <= 0x1fffffffUL)
        {
            physical = (uint)address;
            return true;
        }

        physical = 0;
        return false;
    }

    private bool TryFindRange(int chipSelect, uint offset, out VegasMemoryRange range)
    {
        foreach (VegasMemoryRange candidate in _ranges)
        {
            if (candidate.ChipSelect == chipSelect && offset >= candidate.Start && offset <= candidate.End)
            {
                range = candidate;
                return true;
            }
        }

        range = default;
        return false;
    }

    private void AddRange(string name, int chipSelect, uint start, uint end)
        => _ranges.Add(new VegasMemoryRange(name, chipSelect, start, end));

    private static ulong FormatChipSelectAddress(int chipSelect, uint offset)
        => ((ulong)(uint)chipSelect << 32) | offset;

    private bool TryTranslateChipSelectWindow(ulong address, out int chipSelect, out uint offset)
    {
        chipSelect = 0;
        offset = 0;
        if (!TryTranslatePhysical(address, out uint physical))
            return false;

        for (int candidate = 2; candidate <= 8; candidate++)
        {
            uint config = ReadNileRegister32(NileChipSelect2ConfigOffset + (uint)((candidate - 2) * 8));
            int mask = (int)(config & 0x0f);
            if (mask <= 0)
                continue;

            ulong size = mask >= 5 ? 1UL << (36 - mask) : 0x1_0000_0000UL;
            uint windowStart = config & 0xffe00000u;
            ulong windowOffset = physical - (ulong)windowStart;
            if (windowOffset >= size)
                continue;

            chipSelect = candidate;
            offset = (uint)windowOffset;
            return true;
        }

        return false;
    }

    private void Trace(string op, ulong address, uint value, string target)
    {
        if (!_traceEnabled)
            return;
        if (_traceWritesOnly && op.StartsWith("read", StringComparison.Ordinal))
            return;
        if (!TraceAddressMatches(address))
            return;
        if (!TraceTargetMatches(target))
            return;

        Console.WriteLine($"[GAUNTDL:MEM] pc={_traceCpuPc:x16} {op} {address:x16} {value:x8} {target}");
    }

    private bool TraceAddressMatches(ulong address)
    {
        if (_traceAddressFilters.Length == 0)
            return true;

        foreach (TraceAddressFilter filter in _traceAddressFilters)
        {
            if (filter.Matches(address))
                return true;
        }

        return false;
    }

    private bool TraceTargetMatches(string target)
    {
        if (string.IsNullOrWhiteSpace(_traceTargetFilter))
            return true;

        string filter = _traceTargetFilter.Trim();
        return target.Equals(filter, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(filter + " ", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(filter + ":", StringComparison.OrdinalIgnoreCase);
    }

    private static ulong? ParseOptionalHexUlong(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];

        return ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out ulong parsed)
            ? parsed
            : null;
    }

    private static ushort? ParseOptionalHexUshort(string? raw)
    {
        ulong? parsed = ParseOptionalHexUlong(raw);
        return parsed.HasValue && parsed.Value <= ushort.MaxValue ? (ushort)parsed.Value : null;
    }

    private static int ParseOptionalPositiveInt(string? raw, int fallback)
        => int.TryParse(raw, out int parsed) && parsed > 0 ? parsed : fallback;

    private static TraceAddressFilter[] ParseTraceAddressFilters(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseTraceAddressFilter)
            .Where(filter => filter.HasValue)
            .Select(filter => filter.Value)
            .ToArray();
    }

    private static TraceAddressFilter? ParseTraceAddressFilter(string raw)
    {
        string[] parts = raw.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !TryParseTraceHexUlong(parts[0], out ulong address))
            return null;

        ulong byteLength = 1;
        if (parts.Length > 1 && ulong.TryParse(parts[1], out ulong parsedLength) && parsedLength > 0)
            byteLength = parsedLength;

        return new TraceAddressFilter(address, byteLength);
    }

    private static bool TryParseTraceHexUlong(string raw, out ulong parsed)
    {
        raw = raw.Trim();
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            raw = raw[2..];

        return ulong.TryParse(raw, System.Globalization.NumberStyles.HexNumber, null, out parsed);
    }

    private readonly record struct TraceAddressFilter(ulong Address, ulong ByteLength)
    {
        public bool HasValue => ByteLength != 0;

        public bool Matches(ulong address)
        {
            if (!TryTranslatePhysical(address, out uint addressPhysical) ||
                !TryTranslatePhysical(Address, out uint filterPhysical))
            {
                return address == Address;
            }

            ulong start = filterPhysical;
            ulong end = start + ByteLength;
            return addressPhysical >= start && addressPhysical < end;
        }
    }

    private static int ParseNileCpuIrqShift()
    {
        string? rawShift = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_NILE_IRQ_SHIFT");
        if (int.TryParse(rawShift, out int parsedShift) && parsedShift is >= 8 and <= 12)
            return parsedShift;

        return Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_NILE_IRQ_SHIFT8") == "1" ? 8 : 10;
    }
}

internal readonly record struct VegasMemoryRange(string Name, int ChipSelect, ulong Start, ulong End);

internal sealed class VegasVoodooPciDevice
{
    private const uint VendorDeviceId = 0x0002121au;
    private const uint ClassCode = 0x03800002u;
    private const uint MemoryBarSize = 16 * 1024 * 1024;
    private const uint MemoryBarMask = 0xff000000u;
    private const uint MemoryBarFlags = 0x00000008u;
    private const uint VoodooStatusReady = 0x0ffff03fu;

    private readonly byte[] _config = new byte[0x100];
    private readonly uint[] _pciControl = new uint[8];
    private readonly uint[] _registers = new uint[0x400];
    private readonly byte[] _dacRegisters = new byte[32];
    private const int RegFbiInit7 = 0x24c >> 2;
    private const int RegFbiInit3 = 0x21c >> 2;
    private const int RegFbiInit2 = 0x218 >> 2;
    private const int RegDacData = 0x22c >> 2;
    private readonly bool _traceEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO") == "1";
    private readonly int _traceLimit = ParseTraceLimit("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_PCI_LIMIT", 512);
    private IVoodooBackend? _voodoo;
    private uint _bar0 = 0xff000000u;
    private bool _bar0Probe;
    private uint _statusReadCounter;
    private uint _swapStatusCounter;
    private uint _vRetraceCounter;
    private uint _hvRetraceCounter;
    private uint _initEnable;
    private uint _dacReadResult;
    private int _traceCount;

    public void AttachVoodoo(IVoodooBackend voodoo) => _voodoo = voodoo;

    public void Reset()
    {
        Array.Clear(_config);
        Array.Clear(_pciControl);
        Array.Clear(_registers);
        Array.Clear(_dacRegisters);
        _bar0 = 0xff000000u;
        _bar0Probe = false;
        _statusReadCounter = 0;
        _swapStatusCounter = 0;
        _vRetraceCounter = 0;
        _hvRetraceCounter = 0;
        _initEnable = 0;
        _dacReadResult = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(_config.AsSpan(0x00, 4), VendorDeviceId);
        BinaryPrimitives.WriteUInt16LittleEndian(_config.AsSpan(0x04, 2), 0x0002);
        BinaryPrimitives.WriteUInt32LittleEndian(_config.AsSpan(0x08, 4), ClassCode);
        _config[0x0e] = 0x00;
        _config[0x3c] = 0x00;
        _config[0x3d] = 0x01;
        BinaryPrimitives.WriteUInt32LittleEndian(_config.AsSpan(0x40, 4), 0x00000003);
        WriteConfigBarBytes();
    }

    public uint ReadConfig32(uint address)
    {
        uint offset = address & 0xfc;
        if (offset == 0x10)
            return _bar0Probe ? (MemoryBarMask | MemoryBarFlags) : ((_bar0 & MemoryBarMask) | MemoryBarFlags);
        if (offset + 3 >= _config.Length)
            return 0xffffffffu;

        uint value = offset == 0x40
            ? (_pciControl[0] & ~0xff000u) | 0x00044000u
            : BinaryPrimitives.ReadUInt32LittleEndian(_config.AsSpan((int)offset, 4));
        Trace($"pci cfg read off={offset:x2} value={value:x8}");
        return value;
    }

    public void WriteConfig32(uint address, uint value)
    {
        uint offset = address & 0xfc;
        if (offset + 3 >= _config.Length)
            return;

        Trace($"pci cfg write off={offset:x2} value={value:x8}");
        switch (offset)
        {
            case 0x04:
                BinaryPrimitives.WriteUInt16LittleEndian(_config.AsSpan(0x04, 2), (ushort)(value & 0x0007));
                break;
            case 0x10:
                if (value == 0xffffffffu)
                {
                    _bar0Probe = true;
                }
                else
                {
                    _bar0 = value & MemoryBarMask;
                    _bar0Probe = false;
                }
                WriteConfigBarBytes();
                break;
            case 0x3c:
                _config[0x3c] = (byte)value;
                break;
            case >= 0x40 and < 0x60:
                int index = (int)((offset - 0x40) / 4);
                _pciControl[index] = value;
                if (offset == 0x40)
                    _initEnable = value;
                BinaryPrimitives.WriteUInt32LittleEndian(_config.AsSpan((int)offset, 4), value);
                break;
        }
    }

    public bool TryReadMemory32(uint pciAddress, out uint value)
    {
        if (!TryGetMemoryOffset(pciAddress, out uint offset))
        {
            value = 0;
            return false;
        }

        value = offset switch
        {
            < 0x00400000u => ReadRegister(MapRegisterOffset(offset)),
            < 0x00800000u => _voodoo?.ReadLfb32(offset - 0x00400000u) ?? 0,
            _ => _voodoo?.ReadTexture32(offset - 0x00800000u) ?? 0
        };
        Trace($"mem read off={offset:x6} value={value:x8}");
        return true;
    }

    public bool TryWriteMemory32(uint pciAddress, uint value)
    {
        if (!TryGetMemoryOffset(pciAddress, out uint offset))
            return false;

        if (offset < 0x00400000u)
        {
            if (offset >= 0x00200000u && (IsCommandFifoEnabled || IsGlideCommandFifoWindow(offset)))
            {
                _voodoo?.WriteFifo((offset >> 2) & 0xffffu, value);
                Trace($"fifo write off={offset:x6} value={value:x8}");
            }
            else
            {
                uint registerOffset = MapRegisterOffset(offset);
                WriteRegister(registerOffset, value);
                _voodoo?.WriteRegister(registerOffset, value);
                Trace($"reg write off={offset:x6} value={value:x8}");
            }
        }
        else if (offset < 0x00800000u)
        {
            _voodoo?.WriteLfb32(offset - 0x00400000u, value);
            Trace($"lfb write off={offset:x6} value={value:x8}");
        }
        else
        {
            _voodoo?.WriteTexture32(offset - 0x00800000u, value);
            Trace($"tex write off={offset:x6} value={value:x8}");
        }

        return true;
    }

    private uint ReadRegister(uint offset)
    {
        if ((offset & 0x3ffu) == 0)
            return ReadStatus();
        if ((offset & 0x3ffu) == 0x204u)
            return ++_vRetraceCounter & 0x7ffu;
        if ((offset & 0x3ffu) is 0x1e8u or 0x1f4u or 0x1f8u)
            return _voodoo?.ReadRegister(offset) ?? 0;
        if ((offset & 0x3ffu) == 0x240u)
            return ReadHvRetrace();

        uint register = (offset >> 2) & 0xffu;
        if (register == RegFbiInit2 && ((_initEnable >> 2) & 1u) != 0)
            return _dacReadResult;

        return _registers[register];
    }

    private uint ReadStatus()
    {
        bool vblank = (_statusReadCounter++ & 1u) != 0;
        return _voodoo?.ReadStatus(vblank) ?? (VoodooStatusReady | (vblank ? 0x40u : 0u));
    }

    private void WriteRegister(uint offset, uint value)
    {
        uint register = (offset >> 2) & 0xffu;
        _registers[register] = value;
        if (register == RegDacData)
            WriteDac(value);
    }

    private bool IsCommandFifoEnabled => ((_registers[RegFbiInit7] >> 8) & 1u) != 0;

    private static bool IsGlideCommandFifoWindow(uint offset)
        => offset is >= 0x00200000u and < 0x00300000u;

    private uint ReadHvRetrace()
    {
        uint tick = _hvRetraceCounter++;
        uint hpos = (tick * 73u) % 858u;
        uint vpos = (tick / 8u) % 525u;
        if (vpos >= 480u)
            vpos = 0;
        return (hpos << 16) | vpos;
    }

    private uint MapRegisterOffset(uint offset)
    {
        uint register = (offset >> 2) & 0xffu;
        if (offset >= 0x00200000u && ((_registers[RegFbiInit3] & 1u) != 0))
            register = AliasRegister(register);
        return register << 2;
    }

    private static uint AliasRegister(uint register)
    {
        ReadOnlySpan<byte> alias =
        [
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1a, 0x1b, 0x1c, 0x1d, 0x1e, 0x1f,
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27,
            0x28, 0x29, 0x2a, 0x2b, 0x2c, 0x2d, 0x2e, 0x2f,
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37,
            0x38, 0x39, 0x3a, 0x3b, 0x3c, 0x3d, 0x3e, 0x3f
        ];
        return register < alias.Length ? alias[(int)register] : register;
    }

    private void WriteDac(uint value)
    {
        int register = (int)(((value >> 8) & 0x07u) + 8u * ((value >> 12) & 0x03u));
        if (((value >> 11) & 1u) == 0)
        {
            _dacRegisters[register] = (byte)value;
            return;
        }

        _dacReadResult = _dacRegisters[register];
        _dacReadResult = _dacRegisters[7] switch
        {
            0x01 => 0x55u,
            0x07 => 0x71u,
            0x0b => 0x79u,
            _ => _dacReadResult
        };
    }

    public bool TryWriteMemory8(uint pciAddress, byte value)
    {
        if (!TryGetMemoryOffset(pciAddress, out uint offset))
            return false;

        uint aligned = pciAddress & ~3u;
        TryReadMemory32(aligned, out uint current);
        int shift = (int)((pciAddress & 3) * 8);
        uint merged = (current & ~(0xffu << shift)) | ((uint)value << shift);
        return TryWriteMemory32(aligned, merged);
    }

    private bool TryGetMemoryOffset(uint pciAddress, out uint offset)
    {
        uint start = _bar0 & MemoryBarMask;
        offset = pciAddress - start;
        return offset < MemoryBarSize;
    }

    private void WriteConfigBarBytes()
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_config.AsSpan(0x10, 4), (_bar0 & MemoryBarMask) | MemoryBarFlags);
    }

    private void Trace(string message)
    {
        if (_traceEnabled && _traceCount++ < _traceLimit)
            Console.WriteLine($"[GAUNTDL:VOODOO-PCI] {message}");
    }

    private static int ParseTraceLimit(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value >= 0 ? value : fallback;
}

internal sealed class VegasIdePciDevice
{
    private const uint VendorDeviceId = 0x06461095u;
    private const uint ClassCode = 0x01018a05u;
    private const byte PciInterruptPin = 0x01;
    private const byte PciInterruptLine = 0x0e;
    private const byte BusMasterCommandStart = 0x01;
    private const byte BusMasterCommandRead = 0x08;
    private const byte BusMasterStatusInterrupt = 0x04;
    private const byte BusMasterStatusSimplex = 0x80;

    private readonly uint[] _bars = { 0x1f0, 0x3f4, 0x170, 0x374, 0x0f00 };
    private readonly byte[] _config = new byte[0x100];
    private readonly byte[] _busMaster = new byte[0x10];
    private readonly bool _traceEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_IDE") == "1";
    private readonly bool _dmaSwap32 = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_IDE_DMA_SWAP32") == "1";
    private IdeDiskDevice? _disk;

    public bool InterruptLine
    {
        get
        {
            bool asserted = _disk?.InterruptLine == true;
            uint control = BinaryPrimitives.ReadUInt32LittleEndian(_config.AsSpan(0x50, 4));
            control = asserted ? control | BusMasterStatusInterrupt : control & ~(uint)BusMasterStatusInterrupt;
            BinaryPrimitives.WriteUInt32LittleEndian(_config.AsSpan(0x50, 4), control);
            return asserted;
        }
    }

    public void AttachDisk(IdeDiskDevice disk) => _disk = disk;

    public void Reset()
    {
        Array.Clear(_config);
        Array.Clear(_busMaster);
        BinaryPrimitives.WriteUInt32LittleEndian(_config.AsSpan(0x00, 4), VendorDeviceId);
        BinaryPrimitives.WriteUInt16LittleEndian(_config.AsSpan(0x04, 2), 0x0003); // MAME keeps I/O and memory decode enabled.
        BinaryPrimitives.WriteUInt32LittleEndian(_config.AsSpan(0x08, 4), ClassCode);
        _config[0x0e] = 0x00;
        for (int i = 0; i < _bars.Length; i++)
            WriteConfigBarBytes(i);
        BinaryPrimitives.WriteUInt32LittleEndian(_config.AsSpan(0x50, 4), 0x00000c40);
        _config[0x3c] = PciInterruptLine;
        _config[0x3d] = PciInterruptPin;
        _busMaster[2] = BusMasterStatusSimplex;
        _busMaster[10] = BusMasterStatusSimplex;
    }

    public uint ReadConfig32(uint address)
    {
        uint offset = address & 0xfc;
        if (((address >> 11) & 0x1f) != 5)
            return 0xffffffffu;
        if (offset + 3 >= _config.Length)
            return 0xffffffffu;

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_config.AsSpan((int)offset, 4));
        Trace($"pci cfg read off={offset:x2} value={value:x8}");
        return value;
    }

    public void WriteConfig32(uint address, uint value)
    {
        uint offset = address & 0xfc;
        if (((address >> 11) & 0x1f) != 5 || offset + 3 >= _config.Length)
            return;

        Trace($"pci cfg write off={offset:x2} value={value:x8}");
        switch (offset)
        {
            case 0x04:
                BinaryPrimitives.WriteUInt16LittleEndian(_config.AsSpan(0x04, 2), (ushort)(value & 0x0007));
                break;
            case 0x08:
                _config[0x09] = (byte)(value >> 8);
                break;
            case >= 0x10 and <= 0x20 when ((offset - 0x10) / 4) < _bars.Length:
                int bar = (int)((offset - 0x10) / 4);
                _bars[bar] = value;
                WriteConfigBarBytes(bar);
                break;
            case 0x3c:
                _config[0x3c] = (byte)value;
                break;
            case >= 0x40 and < 0x60:
                uint controlValue = value;
                if (offset == 0x50 && (value & BusMasterStatusInterrupt) != 0)
                    controlValue &= ~(uint)BusMasterStatusInterrupt;
                BinaryPrimitives.WriteUInt32LittleEndian(_config.AsSpan((int)offset, 4), controlValue);
                break;
            case >= 0x70 and < 0x80:
                WriteBusMasterConfigWindow(offset - 0x70, value);
                break;
        }
    }

    public byte ReadIo8(uint address)
    {
        if (TryGetIdeRegister(address, out byte register))
            return register == 0 ? (byte)ReadIo16(address) : _disk?.ReadRegister8(register, clearInterrupt: register == 7) ?? 0xff;
        if (TryGetControlRegister(address))
            return _disk?.ReadRegister8(7, clearInterrupt: false) ?? 0xff;
        if (TryGetBusMasterOffset(address, out uint bmOffset))
            return _busMaster[bmOffset];
        return 0xff;
    }

    public ushort ReadIo16(uint address)
    {
        if (TryGetIdeRegister(address, out byte register) && register == 0)
            return _disk?.ReadData16() ?? 0xffff;

        return (ushort)(ReadIo8(address) | (ReadIo8(address + 1) << 8));
    }

    public uint ReadIo32(uint address)
    {
        if (TryGetIdeRegister(address, out byte register) && register == 0)
        {
            uint low = _disk?.ReadData16() ?? 0xffffu;
            uint high = _disk?.ReadData16() ?? 0xffffu;
            return low | (high << 16);
        }

        if (TryGetBusMasterOffset(address, out uint bmOffset) && bmOffset + 3 < _busMaster.Length)
            return BinaryPrimitives.ReadUInt32LittleEndian(_busMaster.AsSpan((int)bmOffset, 4));

        return (uint)(ReadIo8(address) |
            (ReadIo8(address + 1) << 8) |
            (ReadIo8(address + 2) << 16) |
            (ReadIo8(address + 3) << 24));
    }

    public void WriteIo8(uint address, byte value, VegasMemoryMap memory)
    {
        if (TryGetIdeRegister(address, out byte register))
        {
            if (register != 0)
            {
                _disk?.WriteRegister8(register, value);
                if (register == 7)
                    TryRunPrimaryReadDma(memory);
            }
            return;
        }

        if (TryGetControlRegister(address))
        {
            _disk?.WriteDeviceControl(value);
            return;
        }

        if (TryGetBusMasterOffset(address, out uint bmOffset))
            WriteBusMaster8(bmOffset, value, memory);
    }

    public void WriteIo16(uint address, ushort value, VegasMemoryMap memory)
    {
        if (TryGetIdeRegister(address, out byte register) && register == 0)
        {
            _disk?.WriteData16(value);
            return;
        }

        WriteIo8(address, (byte)value, memory);
        WriteIo8(address + 1, (byte)(value >> 8), memory);
    }

    public void WriteIo32(uint address, uint value, VegasMemoryMap memory)
    {
        if (TryGetIdeRegister(address, out byte register) && register == 0)
        {
            _disk?.WriteData16((ushort)value);
            _disk?.WriteData16((ushort)(value >> 16));
            return;
        }

        if (TryGetBusMasterOffset(address, out uint bmOffset))
        {
            WriteBusMaster(bmOffset, value, memory);
            return;
        }

        WriteIo8(address, (byte)value, memory);
        WriteIo8(address + 1, (byte)(value >> 8), memory);
        WriteIo8(address + 2, (byte)(value >> 16), memory);
        WriteIo8(address + 3, (byte)(value >> 24), memory);
    }

    private void WriteBusMaster8(uint offset, byte value, VegasMemoryMap memory)
    {
        if (offset >= _busMaster.Length)
            return;

        if ((offset & 7) == 2)
        {
            byte keep = (byte)(BusMasterStatusInterrupt | 0x02);
            _busMaster[offset] = (byte)((_busMaster[offset] & ~(value & keep)) | BusMasterStatusSimplex);
        }
        else
        {
            _busMaster[offset] = value;
        }

        Trace($"bmdma write8 off={offset:x2} value={value:x2}");
        if ((offset & 7) == 0)
            TryRunPrimaryReadDma(memory);
    }

    private void WriteBusMaster(uint offset, uint value, VegasMemoryMap memory)
    {
        if (offset + 3 >= _busMaster.Length)
            return;

        BinaryPrimitives.WriteUInt32LittleEndian(_busMaster.AsSpan((int)offset, 4), value);
        Trace($"bmdma write off={offset:x2} value={value:x8}");
        if ((offset & 7) == 0)
            TryRunPrimaryReadDma(memory);
    }

    private void TryRunPrimaryReadDma(VegasMemoryMap memory)
    {
        byte command = _busMaster[0];
        if ((command & BusMasterCommandStart) == 0 || (command & BusMasterCommandRead) == 0)
            return;
        if (_disk?.DmaTransferReady != true)
            return;

        RunPrimaryReadDma(memory);
    }

    private void RunPrimaryReadDma(VegasMemoryMap memory)
    {
        if (_disk is null)
            return;

        uint prd = BinaryPrimitives.ReadUInt32LittleEndian(_busMaster.AsSpan(4, 4)) & 0xfffffffc;
        int copied = 0;
        for (int entry = 0; entry < 256; entry++)
        {
            uint destination = ReadMainMemory32(memory, prd + (uint)(entry * 8));
            uint descriptor = ReadMainMemory32(memory, prd + (uint)(entry * 8 + 4));
            int byteCount = (int)(descriptor & 0xffff);
            if (byteCount == 0)
                byteCount = 0x10000;

            byte[] buffer = _disk.ReadTransferBytes(byteCount);
            if (_dmaSwap32)
                Swap32InPlace(buffer);
            memory.WritePciMemoryFromDevice(destination, buffer);
            copied += buffer.Length;
            if ((descriptor & 0x80000000u) != 0)
                break;
        }

        _busMaster[0] &= unchecked((byte)~BusMasterCommandStart);
        _busMaster[2] |= BusMasterStatusInterrupt;
        _disk.SignalInterrupt();
        memory.TryCompleteKnownRd0DmaQio();
        Trace($"bmdma primary read copied={copied}");
    }

    private static uint ReadMainMemory32(VegasMemoryMap memory, uint address)
        => memory.ReadPciMemoryFromDevice32(address);

    private static void Swap32InPlace(byte[] buffer)
    {
        for (int i = 0; i + 3 < buffer.Length; i += 4)
            (buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]) = (buffer[i + 3], buffer[i + 2], buffer[i + 1], buffer[i]);
    }

    private void WriteBusMasterConfigWindow(uint offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_busMaster.AsSpan((int)offset, 4), value);
    }

    private bool TryGetIdeRegister(uint address, out byte register)
    {
        uint primaryBase = _bars[0] & 0xfffffffc;
        uint secondaryBase = _bars[2] & 0xfffffffc;
        if (address >= primaryBase && address < primaryBase + 8)
        {
            register = (byte)(address - primaryBase);
            return true;
        }

        if (address >= secondaryBase && address < secondaryBase + 8)
        {
            register = (byte)(address - secondaryBase);
            return true;
        }

        register = 0;
        return false;
    }

    private bool TryGetControlRegister(uint address)
    {
        uint primaryControl = _bars[1] & 0xfffffffc;
        uint secondaryControl = _bars[3] & 0xfffffffc;
        return address >= primaryControl && address < primaryControl + 4 ||
               address >= secondaryControl && address < secondaryControl + 4;
    }

    private bool TryGetBusMasterOffset(uint address, out uint offset)
    {
        uint baseAddress = _bars[4] & 0xfffffff0;
        if (address >= baseAddress && address < baseAddress + 16)
        {
            offset = address - baseAddress;
            return true;
        }

        offset = 0;
        return false;
    }

    private void WriteConfigBarBytes(int index)
    {
        uint flags = 1;
        uint mask = index == 4 ? 0xfffffff0u : index is 1 or 3 ? 0xfffffffcu : 0xfffffff8u;
        uint value = (_bars[index] & mask) | flags;
        BinaryPrimitives.WriteUInt32LittleEndian(_config.AsSpan(0x10 + index * 4, 4), value);
    }

    private void Trace(string message)
    {
        if (_traceEnabled)
            Console.WriteLine($"[GAUNTDL:IDEPCI] {message}");
    }
}

internal sealed class IdeDiskDevice
{
    private const byte StatusErr = 0x01;
    private const byte StatusDrq = 0x08;
    private const byte StatusDsc = 0x10;
    private const byte StatusDrdy = 0x40;
    private const byte StatusBsy = 0x80;

    private readonly bool _traceEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_IDE") == "1";
    private IDiskImage? _image;
    private byte[] _transferBuffer = Array.Empty<byte>();
    private int _transferOffset;
    private byte _features;
    private byte _error;
    private byte _sectorCount = 1;
    private byte _sectorNumber;
    private byte _cylinderLow;
    private byte _cylinderHigh;
    private byte _driveHead = 0xe0;
    private byte _status;
    private byte _deviceControl;
    private bool _interruptPending;

    public string? ImagePath { get; private set; }
    public DiskGeometry Geometry => _image?.Geometry ?? DiskGeometry.Empty;
    public bool Attached => _image is not null;
    public bool InterruptLine => _interruptPending && (_deviceControl & 0x02) == 0;
    public bool DmaTransferReady => (_status & StatusDrq) != 0 && _transferOffset < _transferBuffer.Length;

    public void Attach(string? imagePath)
    {
        ImagePath = imagePath;
        _image = string.IsNullOrWhiteSpace(imagePath) ? null : DiskImageFactory.Open(imagePath);
    }

    public void Reset()
    {
        _transferBuffer = Array.Empty<byte>();
        _transferOffset = 0;
        _features = 0;
        _error = 0;
        _sectorCount = 1;
        _sectorNumber = 0;
        _cylinderLow = 0;
        _cylinderHigh = 0;
        _driveHead = 0xe0;
        _status = Attached ? (byte)(StatusDrdy | StatusDsc) : (byte)0;
        _deviceControl = 0;
        _interruptPending = false;
    }

    public byte ReadRegister8(byte register, bool clearInterrupt = true)
    {
        byte value = register switch
        {
            1 => _error,
            2 => _sectorCount,
            3 => _sectorNumber,
            4 => _cylinderLow,
            5 => _cylinderHigh,
            6 => _driveHead,
            7 => _status,
            _ => 0xff
        };

        if (clearInterrupt && register == 7)
            _interruptPending = false;

        Trace($"read r{register}={value:x2}");
        return value;
    }

    public void WriteRegister8(byte register, byte value)
    {
        Trace($"write r{register}={value:x2}");
        switch (register)
        {
            case 1:
                _features = value;
                break;
            case 2:
                _sectorCount = value;
                break;
            case 3:
                _sectorNumber = value;
                break;
            case 4:
                _cylinderLow = value;
                break;
            case 5:
                _cylinderHigh = value;
                break;
            case 6:
                _driveHead = value;
                break;
            case 7:
                ExecuteCommand(value);
                break;
        }
    }

    public void WriteDeviceControl(byte value)
    {
        _deviceControl = value;
        if ((value & 0x02) != 0)
            _interruptPending = false;
        Trace($"device control={value:x2}");
    }

    public void SignalInterrupt()
    {
        if ((_deviceControl & 0x02) == 0)
            _interruptPending = true;
    }

    public ushort ReadData16()
    {
        if ((_status & StatusDrq) == 0 || _transferOffset + 1 >= _transferBuffer.Length)
            return 0xffff;

        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_transferBuffer.AsSpan(_transferOffset, 2));
        _transferOffset += 2;
        if (_transferOffset >= _transferBuffer.Length)
        {
            _transferBuffer = Array.Empty<byte>();
            _transferOffset = 0;
            _status = (byte)(StatusDrdy | StatusDsc);
            SignalInterrupt();
        }

        return value;
    }

    public byte[] ReadTransferBytes(int byteCount)
    {
        if ((_status & StatusDrq) == 0 || byteCount <= 0)
            return Array.Empty<byte>();

        int count = Math.Min(byteCount, _transferBuffer.Length - _transferOffset);
        byte[] data = new byte[count];
        _transferBuffer.AsSpan(_transferOffset, count).CopyTo(data);
        _transferOffset += count;
        if (_transferOffset >= _transferBuffer.Length)
        {
            _transferBuffer = Array.Empty<byte>();
            _transferOffset = 0;
            _status = (byte)(StatusDrdy | StatusDsc);
        }

        Trace($"dma transfer bytes={count}");
        return data;
    }

    public bool TryReadSector(ulong lba, out byte[] sector)
    {
        sector = Array.Empty<byte>();
        if (_image is null || lba >= _image.Geometry.TotalSectors)
            return false;

        sector = new byte[_image.Geometry.BytesPerSector];
        _image.ReadSector(lba, sector);
        Trace($"direct sector read lba={lba}");
        return true;
    }

    public void WriteData16(ushort value)
    {
        Trace($"ignored data write {value:x4}");
    }

    private void ExecuteCommand(byte command)
    {
        _status = StatusBsy;
        _error = 0;

        try
        {
            switch (command)
            {
                case 0xec:
                    StartTransfer(BuildIdentifySector());
                    Trace("identify");
                    break;
                case 0x20:
                case 0x21:
                case 0x24:
                case 0xc4:
                case 0xc8:
                    StartReadSectors();
                    break;
                case 0x91:
                    ApplySetConfig();
                    break;
                case 0xef:
                    _status = (byte)(StatusDrdy | StatusDsc);
                    Trace($"set features feature={_features:x2} value={_sectorCount:x2}");
                    break;
                default:
                    _error = 0x04; // ABRT
                    _status = (byte)(StatusDrdy | StatusDsc | StatusErr);
                    Trace($"unsupported command {command:x2}");
                    break;
            }

            SignalInterrupt();
        }
        catch (Exception ex)
        {
            _transferBuffer = Array.Empty<byte>();
            _transferOffset = 0;
            _error = 0x04;
            _status = (byte)(StatusDrdy | StatusDsc | StatusErr);
            Trace($"command {command:x2} failed: {ex.Message}");
            SignalInterrupt();
        }
    }

    private void StartReadSectors()
    {
        if (_image is null)
            throw new InvalidOperationException("No IDE disk image attached.");

        uint count = _sectorCount == 0 ? 256u : _sectorCount;
        ulong lba = BuildAddress();
        byte[] buffer = new byte[count * (uint)_image.Geometry.BytesPerSector];
        for (uint i = 0; i < count; i++)
            _image.ReadSector(lba + i, buffer.AsSpan((int)(i * (uint)_image.Geometry.BytesPerSector), _image.Geometry.BytesPerSector));

        StartTransfer(buffer);
        Trace($"read sectors lba={lba} count={count}");
    }

    private ulong BuildAddress()
    {
        if ((_driveHead & 0x40) != 0)
        {
            return (ulong)(((_driveHead & 0x0f) << 24) |
                           (_cylinderHigh << 16) |
                           (_cylinderLow << 8) |
                           _sectorNumber);
        }

        DiskGeometry geometry = Geometry;
        int head = _driveHead & 0x0f;
        int cylinder = (_cylinderHigh << 8) | _cylinderLow;
        int sector = Math.Max(1, (int)_sectorNumber);
        return (ulong)(((cylinder * geometry.Heads) + head) * geometry.SectorsPerTrack + (sector - 1));
    }

    private void ApplySetConfig()
    {
        _status = (byte)(StatusDrdy | StatusDsc);
        Trace($"set config sectors={_sectorCount} heads={(_driveHead & 0x0f) + 1}");
        SignalInterrupt();
    }

    private void StartTransfer(byte[] buffer)
    {
        _transferBuffer = buffer;
        _transferOffset = 0;
        _status = (byte)(StatusDrdy | StatusDsc | StatusDrq);
        SignalInterrupt();
    }

    private byte[] BuildIdentifySector()
    {
        DiskGeometry geometry = Geometry;
        var data = new byte[512];
        WriteWord(data, 0, 0x0040); // fixed disk
        WriteWord(data, 1, ClampWord(geometry.Cylinders));
        WriteWord(data, 3, ClampWord(geometry.Heads));
        WriteWord(data, 6, ClampWord(geometry.SectorsPerTrack));
        WriteAtaString(data, 10, 20, "EUTHERGAUNTDL0001");
        WriteWord(data, 47, 0x8000 | 128);
        WriteWord(data, 49, 0x0300); // LBA + DMA supported
        WriteWord(data, 53, 0x0007);
        WriteWord(data, 54, ClampWord(geometry.Cylinders));
        WriteWord(data, 55, ClampWord(geometry.Heads));
        WriteWord(data, 56, ClampWord(geometry.SectorsPerTrack));

        uint total28 = (uint)Math.Min(geometry.TotalSectors, 0x0ffffffful);
        WriteWord(data, 57, (ushort)(total28 & 0xffff));
        WriteWord(data, 58, (ushort)(total28 >> 16));
        WriteWord(data, 60, (ushort)(total28 & 0xffff));
        WriteWord(data, 61, (ushort)(total28 >> 16));
        WriteWord(data, 80, 0x007e);
        WriteWord(data, 83, 0x4000);
        WriteWord(data, 100, (ushort)(geometry.TotalSectors & 0xffff));
        WriteWord(data, 101, (ushort)((geometry.TotalSectors >> 16) & 0xffff));
        WriteWord(data, 102, (ushort)((geometry.TotalSectors >> 32) & 0xffff));
        WriteWord(data, 103, (ushort)((geometry.TotalSectors >> 48) & 0xffff));
        WriteAtaString(data, 23, 8, "0001");
        WriteAtaString(data, 27, 40, "EutherDrive Vegas IDE Disk");
        return data;
    }

    private static ushort ClampWord(long value) => (ushort)Math.Clamp(value, 0, 0xffff);

    private static void WriteWord(byte[] data, int word, ushort value)
        => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(word * 2, 2), value);

    private static void WriteAtaString(byte[] data, int word, int lengthBytes, string value)
    {
        Span<byte> dest = data.AsSpan(word * 2, lengthBytes);
        dest.Fill(0x20);
        byte[] ascii = Encoding.ASCII.GetBytes(value);
        ascii.AsSpan(0, Math.Min(ascii.Length, lengthBytes)).CopyTo(dest);
        for (int i = 0; i + 1 < dest.Length; i += 2)
            (dest[i], dest[i + 1]) = (dest[i + 1], dest[i]);
    }

    private void Trace(string message)
    {
        if (_traceEnabled)
            Console.WriteLine($"[GAUNTDL:IDE] {message}");
    }
}

internal interface IDiskImage
{
    string Path { get; }
    DiskGeometry Geometry { get; }
    void ReadSector(ulong lba, Span<byte> destination);
}

internal readonly record struct DiskGeometry(int Cylinders, int Heads, int SectorsPerTrack, int BytesPerSector, ulong TotalSectors)
{
    public static DiskGeometry Empty => new(0, 0, 0, 512, 0);
}

internal static class DiskImageFactory
{
    public static IDiskImage Open(string path)
    {
        string ext = System.IO.Path.GetExtension(path);
        if (ext.Equals(".chd", StringComparison.OrdinalIgnoreCase))
        {
            ChdDiskImage chd = ChdDiskImage.Open(path);
            string? rawPath = ResolveRawSidecar(path, chd.Geometry);
            return rawPath is null ? chd : RawDiskImage.Open(rawPath, chd.Geometry);
        }

        return RawDiskImage.Open(path);
    }

    private static string? ResolveRawSidecar(string chdPath, DiskGeometry geometry)
    {
        string? explicitRaw = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_RAW_DISK");
        if (!string.IsNullOrWhiteSpace(explicitRaw) && IsUsableRaw(explicitRaw, geometry))
            return explicitRaw;

        string directory = System.IO.Path.GetDirectoryName(chdPath) ?? Environment.CurrentDirectory;
        string name = System.IO.Path.GetFileNameWithoutExtension(chdPath);
        string[] candidates =
        {
            System.IO.Path.Combine(directory, $"{name}.raw"),
            System.IO.Path.Combine(directory, $"{name}.img"),
            System.IO.Path.Combine(directory, $"{name}.bin"),
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{name}.raw")
        };

        return candidates.FirstOrDefault(candidate => IsUsableRaw(candidate, geometry));
    }

    private static bool IsUsableRaw(string path, DiskGeometry geometry)
    {
        if (!File.Exists(path))
            return false;

        long expectedLength = checked((long)(geometry.TotalSectors * (ulong)geometry.BytesPerSector));
        return new FileInfo(path).Length >= expectedLength;
    }
}

internal sealed class RawDiskImage : IDiskImage
{
    private readonly FileStream _stream;

    private RawDiskImage(string path, FileStream stream, DiskGeometry geometry)
    {
        Path = path;
        _stream = stream;
        Geometry = geometry;
    }

    public string Path { get; }
    public DiskGeometry Geometry { get; }

    public static RawDiskImage Open(string path)
    {
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        const int bytesPerSector = 512;
        ulong totalSectors = (ulong)(stream.Length / bytesPerSector);
        var geometry = new DiskGeometry(
            Cylinders: (int)Math.Max(1, totalSectors / (16 * 63)),
            Heads: 16,
            SectorsPerTrack: 63,
            BytesPerSector: bytesPerSector,
            TotalSectors: totalSectors);
        return new RawDiskImage(path, stream, geometry);
    }

    public static RawDiskImage Open(string path, DiskGeometry geometry)
    {
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new RawDiskImage(path, stream, geometry);
    }

    public void ReadSector(ulong lba, Span<byte> destination)
    {
        if (destination.Length < Geometry.BytesPerSector)
            throw new ArgumentException("Destination buffer is smaller than one sector.", nameof(destination));
        if (lba >= Geometry.TotalSectors)
            throw new ArgumentOutOfRangeException(nameof(lba));

        _stream.Position = checked((long)(lba * (ulong)Geometry.BytesPerSector));
        int read = _stream.Read(destination[..Geometry.BytesPerSector]);
        if (read != Geometry.BytesPerSector)
            throw new EndOfStreamException($"Short raw disk read at LBA {lba}.");
    }
}

internal sealed class ChdDiskImage : IDiskImage
{
    private const uint HardDiskMetadataTag = 0x47444444; // GDDD

    private ChdDiskImage(string path, DiskGeometry geometry, int version, long logicalBytes, int hunkBytes, int unitBytes)
    {
        Path = path;
        Geometry = geometry;
        Version = version;
        LogicalBytes = logicalBytes;
        HunkBytes = hunkBytes;
        UnitBytes = unitBytes;
    }

    public string Path { get; }
    public DiskGeometry Geometry { get; }
    public int Version { get; }
    public long LogicalBytes { get; }
    public int HunkBytes { get; }
    public int UnitBytes { get; }

    public static ChdDiskImage Open(string path)
    {
        using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[124];
        ReadExact(stream, header);

        if (!Encoding.ASCII.GetString(header[..8]).Equals("MComprHD", StringComparison.Ordinal))
            throw new InvalidDataException("Not a CHD file.");

        int headerLength = (int)ReadU32(header, 8);
        int version = (int)ReadU32(header, 12);
        if (version != 5)
            throw new NotSupportedException($"CHD version {version} is not supported by the Gauntlet disk metadata reader yet.");

        long logicalBytes = checked((long)ReadU64(header, 32));
        ulong metaOffset = ReadU64(header, 48);
        int hunkBytes = (int)ReadU32(header, 56);
        int unitBytes = (int)ReadU32(header, 60);
        if (headerLength > header.Length)
            stream.Position = headerLength;

        string metadata = ReadMetadataString(stream, metaOffset, HardDiskMetadataTag)
            ?? throw new InvalidDataException("CHD is missing hard disk GDDD metadata.");
        DiskGeometry geometry = ParseHardDiskGeometry(metadata, logicalBytes, unitBytes);

        return new ChdDiskImage(path, geometry, version, logicalBytes, hunkBytes, unitBytes);
    }

    public void ReadSector(ulong lba, Span<byte> destination)
    {
        throw new NotSupportedException(
            "Compressed CHD sector reads are not ported yet. Extract a raw sidecar with chdman extractraw for the current IDE path, or port CHD v5 hunk decompression next.");
    }

    private static DiskGeometry ParseHardDiskGeometry(string metadata, long logicalBytes, int unitBytes)
    {
        int cylinders = ReadMetadataInt(metadata, "CYLS:");
        int heads = ReadMetadataInt(metadata, "HEADS:");
        int sectors = ReadMetadataInt(metadata, "SECS:");
        int bytesPerSector = ReadMetadataInt(metadata, "BPS:");
        ulong totalSectors = (ulong)(logicalBytes / bytesPerSector);
        return new DiskGeometry(cylinders, heads, sectors, bytesPerSector == 0 ? unitBytes : bytesPerSector, totalSectors);
    }

    private static int ReadMetadataInt(string metadata, string key)
    {
        int start = metadata.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidDataException($"CHD metadata missing {key}");
        start += key.Length;
        int end = start;
        while (end < metadata.Length && char.IsDigit(metadata[end]))
            end++;
        return int.Parse(metadata[start..end], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? ReadMetadataString(FileStream stream, ulong firstOffset, uint wantedTag)
    {
        ulong offset = firstOffset;
        Span<byte> header = stackalloc byte[16];
        while (offset != 0)
        {
            stream.Position = checked((long)offset);
            ReadExact(stream, header);
            uint tag = ReadU32(header, 0);
            int length = (int)((header[5] << 16) | (header[6] << 8) | header[7]);
            ulong next = ReadU64(header, 8);
            if (tag == wantedTag)
            {
                byte[] data = new byte[length];
                ReadExact(stream, data);
                return Encoding.ASCII.GetString(data).TrimEnd('\0', '.', ' ');
            }

            offset = next;
        }

        return null;
    }

    private static void ReadExact(Stream stream, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read == 0)
                throw new EndOfStreamException();
            total += read;
        }
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));

    private static ulong ReadU64(ReadOnlySpan<byte> data, int offset)
        => BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8));
}

internal sealed class VegasSioDevice
{
    private byte[] _bootRom = Array.Empty<byte>();
    private byte _resetControl;
    private byte _irqEnable;
    private byte _irqState;
    private byte _ledState;

    public bool InterruptLine => (_irqState & _irqEnable) != 0;

    public void LoadBootRom(byte[] bootRom) => _bootRom = bootRom.ToArray();
    public void Reset()
    {
        _resetControl = 0;
        _irqEnable = 0;
        _irqState = 0;
        _ledState = 0;
    }

    public byte Read(uint offset)
    {
        return (offset >> 12) switch
        {
            0 => _resetControl,
            1 => _irqEnable,
            2 => (byte)(_irqState & _irqEnable),
            3 => _irqState,
            4 => _ledState,
            _ => 0
        };
    }

    public void Write(uint offset, byte value)
    {
        switch (offset >> 12)
        {
            case 0:
                if ((value & 0x08) == 0)
                    _irqState &= unchecked((byte)~0x20);
                _resetControl = value;
                break;
            case 1:
                _irqEnable = value;
                break;
            case 4:
                _ledState = value;
                break;
        }
    }

    public void PulseVblank(bool state)
    {
        bool invertedPolarity = (_resetControl & 0x10) != 0;
        if ((_irqEnable & 0x20) != 0 && ((state && !invertedPolarity) || (!state && invertedPolarity)))
            _irqState |= 0x20;
    }

    public void SetIoasicIrq(bool asserted)
    {
        if (asserted)
            _irqState |= 0x04;
        else
            _irqState &= unchecked((byte)~0x04);
    }
}

internal sealed class DcsAudioDevice
{
    private const ushort LatchOutputEmpty = 0x0400;
    private const ushort LatchInputEmpty = 0x0800;
    private const int AudioSampleRate = 44_100;
    private const int AudioChannels = 2;
    private const int FrameSamples = AudioSampleRate / 60;
    private const int FifoSize = 512;
    private const int DramWords = 4 * 1024 * 1024 / 2;

    private readonly bool _trace = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_DCS") == "1";
    private readonly bool _traceAdspIo = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_DCS_ADSP_IO") == "1";
    private readonly bool _enableDiagnosticTone = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DCS_DIAGNOSTIC_TONE") == "1";
    private readonly int _traceLimit = int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_DCS_LIMIT"), out int parsed) && parsed > 0 ? parsed : 120;
    private readonly ushort[] _dram = new ushort[DramWords];
    private readonly ushort[] _fifo = new ushort[FifoSize];
    private readonly short[] _audioFrame = new short[FrameSamples * AudioChannels];
    private readonly uint[] _programRam = new uint[0x4000];
    private readonly ushort[] _internalDataRam = new ushort[0x200];
    private readonly ushort[] _sdrcRegisters = new ushort[4];
    private readonly ushort[] _adspControlRegisters = new ushort[0x20];
    private readonly DcsAdsp2104Core _adsp;
    private readonly Queue<ushort> _outputQueue = new();
    private byte[] _bootRom = Array.Empty<byte>();
    private int _traceCount;
    private int _fifoIn;
    private int _fifoOut;
    private int _fifoCount;
    private bool _fifoForceFull;
    private bool _fifoFullSelfTestReported;
    private ushort _lastCommand;
    private ushort _latchControl;
    private ushort _inputData;
    private ushort _outputData;
    private ushort _outputControl;
    private bool _resetAsserted;
    private bool _fifoReset;
    private bool _bootStatusCompat;
    private uint _idmaAddress;
    private int _transferState;
    private int _transferDcsState;
    private uint _transferStart;
    private uint _transferStop;
    private uint _transferType;
    private int _transferWritesLeft;
    private ushort _transferSum;
    private ushort _programHighByte;
    private int _toneSamplesLeft;
    private int _tonePhase;
    private int _toneStep;
    private int _toneVolume;
    private ulong _hostWrites;
    private ulong _fifoWrites;
    private ulong _transferredWords;
    private int _bootProgramWords;

    public DcsAudioDevice()
    {
        _adsp = new DcsAdsp2104Core(this);
    }

    public ushort Control => _bootStatusCompat ? (ushort)0 : _latchControl;
    public ushort FifoStatus
    {
        get
        {
            ushort result = 0;
            if (_fifoCount == 0 && !_fifoForceFull)
                result |= 0x0008;
            if (_fifoCount >= FifoSize / 2)
                result |= 0x0010;
            if (_fifoCount >= FifoSize || _fifoForceFull)
            {
                result |= 0x0020;
                if (!_fifoForceFull && !_fifoFullSelfTestReported && IsSequentialFifoSelfTest())
                {
                    _fifoFullSelfTestReported = true;
                    Trace($"fifo-status selftest-full value={result:x4}; draining for DCS handoff");
                    ClearFifo();
                }
            }
            return result;
        }
    }
    public ushort Data2 => _outputControl;
    public string DebugStatus => $"dcs boot={_bootProgramWords}w host={_hostWrites} fifo={_fifoWrites}/{_fifoCount} xfer={_transferredWords} " +
                                 $"state={_transferDcsState}/{_transferState} type={_transferType:x4} left={_transferWritesLeft} " +
                                 $"lc={_latchControl:x4} out={_outputData:x4} oq={_outputQueue.Count} {_adsp.DebugStatus}";

    public void LoadBootRom(byte[] bootRom)
    {
        _bootRom = bootRom.ToArray();
        DecodeBootProgram();
    }

    public void Reset()
    {
        _traceCount = 0;
        _fifoIn = 0;
        _fifoOut = 0;
        _fifoCount = 0;
        _fifoForceFull = false;
        _fifoFullSelfTestReported = false;
        _lastCommand = 0;
        _latchControl = LatchInputEmpty | LatchOutputEmpty;
        _inputData = 0;
        _outputData = 0x000a;
        _outputControl = 0;
        _outputQueue.Clear();
        _resetAsserted = false;
        _fifoReset = false;
        _bootStatusCompat = true;
        _idmaAddress = 0;
        _transferState = 0;
        _transferDcsState = 0;
        _transferStart = 0;
        _transferStop = 0;
        _transferType = 0;
        _transferWritesLeft = 0;
        _transferSum = 0;
        _programHighByte = 0;
        _toneSamplesLeft = 0;
        _tonePhase = 0;
        _toneStep = 0;
        _toneVolume = 0;
        _hostWrites = 0;
        _fifoWrites = 0;
        _transferredWords = 0;
        DecodeBootProgram();
        _adsp.Reset();
        Array.Clear(_audioFrame);
    }

    public void ResetLine(bool stateHigh)
    {
        bool asserted = !stateHigh;
        _resetAsserted = asserted;
        if (asserted)
        {
            _lastCommand = 0;
            _latchControl = LatchInputEmpty | LatchOutputEmpty;
            _outputData = 0x000a;
            _outputControl = 0;
            _outputQueue.Clear();
            _transferState = 0;
            _transferDcsState = 0;
            _bootStatusCompat = true;
            DecodeBootProgram();
            _adsp.Reset();
        }
        Trace($"reset state={(stateHigh ? 1 : 0)} asserted={asserted}");
    }

    public void SetFifoReset(bool asserted)
    {
        _fifoReset = asserted;
        if (asserted)
        {
            _fifoIn = 0;
            _fifoOut = 0;
            _fifoCount = 0;
            _fifoForceFull = false;
            _fifoFullSelfTestReported = false;
        }
        Trace($"fifo-reset asserted={asserted}");
    }

    public void WriteData(ushort value)
    {
        _hostWrites++;
        _bootStatusCompat = false;
        _lastCommand = value;
        if (TryPreprocessWrite(value))
        {
            Trace($"data-w hle value={value:x4} state={_transferState} dcsState={_transferDcsState}");
            return;
        }

        _inputData = value;
        _latchControl &= unchecked((ushort)~LatchInputEmpty);
        _adsp.SetIrq2(true);
        QueueDiagnosticTone(value);
        Trace($"data-w value={value:x4} input={_inputData:x4}");
    }

    public ushort ReadData()
    {
        ushort value = _outputData;
        Trace($"data-r value={value:x4} last={_lastCommand:x4} reset={_resetAsserted} lc={_latchControl:x4}");
        return value;
    }

    public void Ack()
    {
        _latchControl |= LatchOutputEmpty;
        FlushQueuedOutput();
        Trace("ack");
    }

    public void WriteFifo(ushort value)
    {
        _bootStatusCompat = false;
        if (_fifoCount < FifoSize)
        {
            _fifo[_fifoIn++ % FifoSize] = value;
            _fifoCount++;
            _fifoWrites++;
        }

        if (_transferState != 0 || _transferDcsState != 0 || (_fifoCount == 1 && (value is 0x001a or 0x002a)))
            FifoNotify();
        Trace($"fifo-w value={value:x4} count={_fifoCount}");
    }

    public void SetFifoForceFull()
    {
        _fifoForceFull = true;
        FifoNotify();
        Trace("fifo-force-full");
    }

    public void WriteIdmaAddress(uint value)
    {
        _bootStatusCompat = false;
        _idmaAddress = value & 0x3fffff;
        Trace($"idma-addr value={value:x8}");
    }

    public void WriteIdmaData(uint value)
    {
        WriteIdmaWord((ushort)value);
        WriteIdmaWord((ushort)(value >> 16));
        Trace($"idma-data-w value={value:x8} addr={_idmaAddress:x6}");
    }

    public void WriteIdmaData16(ushort value)
    {
        WriteIdmaWord(value);
        Trace($"idma-data16-w value={value:x4} addr={_idmaAddress:x6}");
    }

    public uint ReadIdmaData()
    {
        ushort low = ReadIdmaWord();
        ushort high = ReadIdmaWord();
        uint value = (uint)(low | (high << 16));
        Trace($"idma-data-r value={value:x8} addr={_idmaAddress:x6}");
        return value;
    }

    public void RunFrame()
    {
        if (!_resetAsserted && _bootProgramWords > 0)
            _adsp.Run(60_000);

        int offset = 0;
        for (int i = 0; i < FrameSamples; i++)
        {
            short sample = 0;
            if (_toneSamplesLeft > 0)
            {
                _tonePhase = (_tonePhase + _toneStep) & 0xffff;
                int wave = _tonePhase < 0x8000 ? _toneVolume : -_toneVolume;
                int envelope = Math.Min(_toneSamplesLeft, 512);
                sample = (short)((wave * envelope) / 512);
                _toneSamplesLeft--;
            }

            _audioFrame[offset++] = sample;
            _audioFrame[offset++] = sample;
        }
    }

    public ReadOnlySpan<short> GetFrameBuffer() => _audioFrame;

    internal uint ReadProgramWord(ushort address)
        => _programRam[address & 0x3fff] & 0x00ffffffu;

    internal void WriteProgramWord(ushort address, uint value)
        => _programRam[address & 0x3fff] = value & 0x00ffffffu;

    internal ushort ReadDataMemory(ushort address)
    {
        address &= 0x3fff;
        if (address == 0x0400)
        {
            ushort value = _inputData;
            _latchControl |= LatchInputEmpty;
            _adsp.SetIrq2(false);
            TraceAdspIo($"input-r value={value:x4} lc={_latchControl:x4}");
            return value;
        }

        if (address == 0x0402)
            return _outputControl;
        if (address == 0x0403)
        {
            ushort status = 0;
            if ((_latchControl & LatchInputEmpty) == 0)
                status |= 0x0080;
            if ((_latchControl & LatchOutputEmpty) != 0)
                status |= 0x0040;
            status = (ushort)(status | (FifoStatus & 0x0038));
            TraceAdspIo($"status-r value={status:x4} lc={_latchControl:x4} fifo={_fifoCount}");
            return status;
        }
        if (address is >= 0x0404 and <= 0x0407)
        {
            ushort value = PopFifo();
            TraceAdspIo($"fifo-r value={value:x4} count={_fifoCount}");
            return value;
        }
        if (address is >= 0x0480 and <= 0x0483)
            return _sdrcRegisters[address - 0x0480];
        if (address is >= 0x3800 and <= 0x39ff)
            return _internalDataRam[address - 0x3800];
        if (address is >= 0x3fe0 and <= 0x3fff)
            return _adspControlRegisters[address - 0x3fe0];

        return _dram[address % DramWords];
    }

    internal void WriteDataMemory(ushort address, ushort value)
    {
        address &= 0x3fff;
        if (address == 0x0400)
        {
            _latchControl |= LatchInputEmpty;
            _adsp.SetIrq2(false);
            TraceAdspIo($"input-ack lc={_latchControl:x4}");
            return;
        }

        if (address == 0x0401)
        {
            TraceAdspIo($"output-w value={value:x4}");
            OutputLatch(value);
            return;
        }

        if (address == 0x0402)
        {
            _outputControl = value;
            TraceAdspIo($"control-w value={value:x4}");
            return;
        }

        if (address is >= 0x0480 and <= 0x0483)
        {
            _sdrcRegisters[address - 0x0480] = value;
            return;
        }

        if (address is >= 0x3800 and <= 0x39ff)
        {
            _internalDataRam[address - 0x3800] = value;
            return;
        }

        if (address is >= 0x3fe0 and <= 0x3fff)
        {
            _adspControlRegisters[address - 0x3fe0] = value;
            return;
        }

        _dram[address % DramWords] = value;
    }

    private void DecodeBootProgram()
    {
        Array.Clear(_programRam);
        _bootProgramWords = 0;
        if (_bootRom.Length < 4)
            return;

        // MAME's ADSP load_boot_data expands 8-bit DCS boot ROM bytes into 24-bit program words.
        int pageWords = (_bootRom[3] + 1) * 8;
        int availableWords = Math.Min(pageWords, Math.Min(_programRam.Length, _bootRom.Length / 4));
        for (int i = 0; i < availableWords; i++)
        {
            int source = i * 4;
            _programRam[i] = (uint)((_bootRom[source] << 16) | (_bootRom[source + 1] << 8) | _bootRom[source + 2]);
        }

        _bootProgramWords = availableWords;
        Trace($"boot-rom decoded words={_bootProgramWords} first={(_bootProgramWords > 0 ? _programRam[0] : 0):x6}");
    }

    private void WriteIdmaWord(ushort value)
    {
        _dram[(int)(_idmaAddress++ % DramWords)] = value;
        _transferredWords++;
    }

    private ushort ReadIdmaWord()
        => _dram[(int)(_idmaAddress++ % DramWords)];

    private void FifoNotify()
    {
        while (_fifoCount > 0 && (_transferState != 5 || _fifoCount == _transferWritesLeft || _fifoCount >= 256))
            TryPreprocessWrite(PopFifo());
    }

    private bool IsSequentialFifoSelfTest()
    {
        if (_fifoCount != FifoSize)
            return false;

        for (int i = 0; i < FifoSize; i++)
        {
            if (_fifo[(_fifoOut + i) % FifoSize] != i + 1)
                return false;
        }

        return true;
    }

    private void ClearFifo()
    {
        _fifoIn = 0;
        _fifoOut = 0;
        _fifoCount = 0;
        _fifoForceFull = false;
    }

    private ushort PopFifo()
    {
        if (_fifoCount == 0)
            return 0xffff;

        ushort value = _fifo[_fifoOut++ % FifoSize];
        _fifoCount--;
        return value;
    }

    private bool TryPreprocessWrite(ushort data)
    {
        return _transferDcsState == 0
            ? TryPreprocessStage1(data)
            : TryPreprocessStage2(data);
    }

    private bool TryPreprocessStage1(ushort data)
    {
        switch (_transferState)
        {
            case 0:
                if (data == 0x001a)
                {
                    _transferState = 1;
                    return true;
                }

                if (data == 0x002a)
                {
                    _transferDcsState = 1;
                    return false;
                }
                return false;
            case 1:
                _transferStart = data;
                _transferState = 2;
                return true;
            case 2:
                _transferStop = data;
                _transferState = 3;
                return true;
            case 3:
                _transferType = data;
                _transferWritesLeft = (int)(_transferStop - _transferStart + 1);
                if (_transferType == 0)
                    _transferWritesLeft *= 2;
                _transferSum = 0;
                _transferState = 4;
                return true;
            case 4:
                _transferSum += data;
                if (_transferType == 0)
                {
                    if ((_transferWritesLeft & 1) != 0)
                        _programHighByte = data;
                    else
                        WriteProgramTransferWord(_transferStart++, ((uint)_programHighByte << 8) | (data & 0xffu));
                }
                else
                {
                    WriteDataMemory((ushort)_transferStart++, data);
                    _transferredWords++;
                }

                if (--_transferWritesLeft == 0)
                {
                    _transferState = 0;
                    OutputLatch(_transferSum);
                    OutputLatch(0x000a);
                }
                return true;
        }

        return false;
    }

    private bool TryPreprocessStage2(ushort data)
    {
        switch (_transferState)
        {
            case 0:
                if (data is 0x55d0 or 0x55d1)
                {
                    _transferState = 1;
                    return true;
                }
                return false;
            case 1:
                _transferStart = (uint)data << 16;
                _transferState = 2;
                return true;
            case 2:
                _transferStart |= data;
                _transferState = 3;
                return true;
            case 3:
                _transferStop = (uint)data << 16;
                _transferState = 4;
                return true;
            case 4:
                _transferStop |= data;
                _transferWritesLeft = (int)(_transferStop - _transferStart + 1);
                _transferSum = 0;
                _transferState = 5;
                return true;
            case 5:
                _transferSum += data;
                WriteDram(_transferStart++, data);
                if (--_transferWritesLeft == 0)
                {
                    _transferState = 0;
                    OutputLatch(_transferSum);
                    _outputControl = (ushort)((_outputControl & ~0xff00) | 0x0300);
                }
                return true;
        }

        return false;
    }

    private void WriteDram(uint address, ushort value)
    {
        _dram[(int)(address % DramWords)] = value;
        _transferredWords++;
    }

    private void WriteProgramTransferWord(uint address, uint value)
    {
        WriteProgramWord((ushort)address, value);
        _transferredWords++;
    }

    private void OutputLatch(ushort value)
    {
        if ((_latchControl & LatchOutputEmpty) == 0)
        {
            _outputQueue.Enqueue(value);
            return;
        }

        _outputData = value;
        _latchControl &= unchecked((ushort)~LatchOutputEmpty);
    }

    private void FlushQueuedOutput()
    {
        if ((_latchControl & LatchOutputEmpty) == 0 || _outputQueue.Count == 0)
            return;

        _outputData = _outputQueue.Dequeue();
        _latchControl &= unchecked((ushort)~LatchOutputEmpty);
    }

    private void QueueDiagnosticTone(ushort command)
    {
        if (!_enableDiagnosticTone || command == 0)
            return;

        _toneSamplesLeft = AudioSampleRate / 12;
        _toneStep = 0x100 + ((command & 0x3f) * 17);
        _toneVolume = 1200 + ((command >> 8) & 0x0f) * 120;
    }

    private void Trace(string message)
    {
        if (!_trace || _traceCount++ >= _traceLimit)
            return;
        Console.WriteLine($"[GAUNTDL:DCS] {message}");
    }

    private void TraceAdspIo(string message)
    {
        if (!_traceAdspIo)
            return;
        Trace($"adsp-{message}");
    }
}

internal sealed class DcsAdsp2104Core
{
    private const ushort ZFlag = 0x0001;
    private const ushort NFlag = 0x0002;
    private const ushort VFlag = 0x0004;
    private const ushort CFlag = 0x0008;
    private const ushort MStatBank = 0x0001;

    private readonly DcsAudioDevice _bus;
    private readonly bool _trace = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_ADSP") == "1";
    private readonly bool _tracePc = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_ADSP_PC") == "1";
    private readonly int _traceLimit = int.TryParse(Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_ADSP_LIMIT"), out int parsed) && parsed > 0 ? parsed : 160;
    private readonly ushort[] _r = new ushort[16];
    private readonly ushort[] _rAlt = new ushort[16];
    private readonly ushort[] _i = new ushort[8];
    private readonly ushort[] _m = new ushort[8];
    private readonly ushort[] _l = new ushort[8];
    private readonly ushort[] _pcStack = new ushort[16];
    private readonly ushort[,] _statStack = new ushort[8, 3];
    private readonly uint[] _loopStack = new uint[4];
    private int _pcStackDepth;
    private int _statStackDepth;
    private int _loopStackDepth;
    private ushort _pc;
    private ushort _ppc;
    private ushort _loop;
    private int _loopCondition;
    private ushort _astat;
    private ushort _mstat;
    private ushort _sstat;
    private ushort _imask;
    private ushort _icntl;
    private ushort _cntr;
    private ushort _px;
    private ushort _pmovlay;
    private ushort _dmovlay;
    private bool _irq2State;
    private bool _irq2Latch;
    private int _traceCount;
    private uint _steps;

    public DcsAdsp2104Core(DcsAudioDevice bus)
    {
        _bus = bus;
    }

    public string DebugStatus => $"adsp pc={_pc:x4} ppc={_ppc:x4} astat={_astat:x4} mstat={_mstat:x4} imask={_imask:x4} icntl={_icntl:x4} irq2={(_irq2State ? 1 : 0)}/{(_irq2Latch ? 1 : 0)} cntr={_cntr:x4} steps={_steps}";

    public void Reset()
    {
        Array.Clear(_r);
        Array.Clear(_rAlt);
        Array.Clear(_i);
        Array.Clear(_m);
        Array.Clear(_l);
        Array.Clear(_pcStack);
        Array.Clear(_statStack);
        Array.Clear(_loopStack);
        _pcStackDepth = 0;
        _statStackDepth = 0;
        _loopStackDepth = 0;
        _pc = 0;
        _ppc = 0;
        _loop = 0xffff;
        _loopCondition = 0;
        _astat = 0;
        _mstat = 0;
        _sstat = 0;
        _imask = 0;
        _icntl = 0;
        _cntr = 0;
        _px = 0;
        _pmovlay = 0;
        _dmovlay = 0;
        _irq2State = false;
        _irq2Latch = false;
        _steps = 0;
    }

    public void SetIrq2(bool asserted)
    {
        if (asserted && !_irq2State)
            _irq2Latch = true;
        _irq2State = asserted;
    }

    public void Run(int cycles)
    {
        for (int i = 0; i < cycles; i++)
            Step();
    }

    private void Step()
    {
        CheckInterrupts();
        _ppc = _pc;
        uint op = _bus.ReadProgramWord(_pc);
        if (_tracePc && (_steps & 0x3fff) == 0)
            Trace($"pc={_pc:x4} op={op:x6} astat={_astat:x4} cntr={_cntr:x4}");
        if (_pc != _loop)
        {
            _pc = (ushort)((_pc + 1) & 0x3fff);
        }
        else if (Condition(_loopCondition))
        {
            _pc = PcStackTop();
        }
        else
        {
            PopLoop();
            PopPcValue();
            _pc = (ushort)((_pc + 1) & 0x3fff);
        }
        _steps++;

        switch (Bit(op, 16, 8))
        {
            case 0x00:
                break;
            case 0x02:
                break;
            case 0x04:
                if (IsBitSet(op, 4))
                    PopPcValue();
                break;
            case 0x09:
                {
                    int index = (int)Bit(op, 2, 3);
                    ModifyAddress(index, (index & 4) | (int)Bit(op, 0, 2));
                    break;
                }
            case 0x0a:
                if (Condition((int)Bit(op, 0, 4)))
                {
                    PopPc();
                    if (IsBitSet(op, 4))
                        PopStatus();
                }
                break;
            case 0x0c:
                ApplyModeControl(op);
                break;
            case 0x0d:
                WriteReg((int)Bit(op, 8, 2), (int)Bit(op, 4, 4), ReadReg((int)Bit(op, 10, 2), (int)Bit(op, 0, 4)));
                break;
            case 0x0e:
            case 0x0f:
                ExecuteShift(op);
                break;
            case 0x12:
                ExecuteShift(op);
                if (IsBitSet(op, 15))
                    DataWriteDag1(op, ReadReg0((int)Bit(op, 4, 4)));
                else
                    WriteReg0((int)Bit(op, 4, 4), DataReadDag1(op));
                break;
            case 0x13:
                ExecuteShift(op);
                if (IsBitSet(op, 15))
                    DataWriteDag2(op, ReadReg0((int)Bit(op, 4, 4)));
                else
                    WriteReg0((int)Bit(op, 4, 4), DataReadDag2(op));
                break;
            case >= 0x14 and <= 0x17:
                PushLoop(op & 0x3ffff);
                PushPc();
                break;
            case >= 0x18 and <= 0x1b:
                if (Condition((int)Bit(op, 0, 4)))
                {
                    _pc = (ushort)Bit(op, 4, 14);
                    if (_pc == _ppc)
                        return;
                }
                break;
            case >= 0x1c and <= 0x1f:
                if (Condition((int)Bit(op, 0, 4)))
                {
                    PushPc();
                    _pc = (ushort)Bit(op, 4, 14);
                }
                break;
            case >= 0x20 and <= 0x27:
                if (Condition((int)Bit(op, 0, 4)))
                    ExecuteAluMac(op, destinationBit: IsBitSet(op, 18), constantVariant: IsBitSet(op, 4));
                break;
            case >= 0x28 and <= 0x2f:
                ExecuteAluMac(op, destinationBit: IsBitSet(op, 18), constantVariant: false);
                WriteReg0((int)Bit(op, 4, 4), ReadReg0((int)Bit(op, 0, 4)));
                break;
            case >= 0x30 and <= 0x3f:
                WriteReg((int)Bit(op, 18, 2), (int)Bit(op, 0, 4), (ushort)Bit(op, 4, 14));
                break;
            case >= 0x40 and <= 0x4f:
                WriteReg0((int)Bit(op, 0, 4), (ushort)Bit(op, 4, 16));
                break;
            case >= 0x50 and <= 0x57:
                ExecuteAluMac(op, destinationBit: (Bit(op, 17, 2) & 1u) != 0, constantVariant: false);
                WriteReg0((int)Bit(op, 4, 4), ProgramReadDag2(op));
                break;
            case >= 0x58 and <= 0x5f:
                ProgramWriteDag2(op, ReadReg0((int)Bit(op, 4, 4)));
                ExecuteAluMac(op, destinationBit: (Bit(op, 17, 2) & 1u) != 0, constantVariant: false);
                break;
            case >= 0x60 and <= 0x6f:
                if (IsBitSet(op, 19))
                    DataWriteDag1(op, ReadReg0((int)Bit(op, 4, 4)));
                else
                    WriteReg0((int)Bit(op, 4, 4), DataReadDag1(op));
                break;
            case >= 0x70 and <= 0x7f:
                if (IsBitSet(op, 19))
                    DataWriteDag2(op, ReadReg0((int)Bit(op, 4, 4)));
                else
                    WriteReg0((int)Bit(op, 4, 4), DataReadDag2(op));
                break;
            case >= 0x80 and <= 0x8f:
                WriteReg((int)Bit(op, 18, 2), (int)Bit(op, 0, 4), _bus.ReadDataMemory((ushort)Bit(op, 4, 14)));
                break;
            case >= 0x90 and <= 0x9f:
                _bus.WriteDataMemory((ushort)Bit(op, 4, 14), ReadReg((int)Bit(op, 18, 2), (int)Bit(op, 0, 4)));
                break;
            case >= 0xa0 and <= 0xaf:
                DataWriteDag1(op, (ushort)Bit(op, 4, 16));
                break;
            case >= 0xb0 and <= 0xbf:
                DataWriteDag2(op, (ushort)Bit(op, 4, 16));
                break;
            default:
                Trace($"unsupported pc={_ppc:x4} op={op:x6} top={Bit(op, 16, 8):x2}");
                break;
        }
    }

    private void ExecuteAluMac(uint op, bool destinationBit, bool constantVariant)
    {
        int opIndex = (int)Bit(op, 13, 5);
        ushort x = ReadReg0((int)Bit(op, 8, 3));
        ushort y = constantVariant ? AdspConstant((int)(Bit(op, 5, 3) | (Bit(op, 11, 2) << 3))) : ReadReg0(4 + (int)Bit(op, 11, 2));
        int result = opIndex switch
        {
            0x10 => y,
            0x11 => y + 1,
            0x13 => x + y,
            0x15 => -y,
            0x17 => x - y,
            0x18 => y - 1,
            0x19 => -y,
            0x1b => ~y,
            0x1c => x & y,
            0x1d => x | y,
            0x1e => x ^ y,
            0x1f => Math.Abs((short)x),
            _ => ReadReg0(10)
        };

        ushort value = (ushort)result;
        UpdateNz(value);
        if (destinationBit)
            WriteReg0(1, value);
        else
            WriteReg0(10, value);
    }

    private void ExecuteShift(uint op)
    {
        int source = (int)Bit(op, 8, 3);
        ushort value = source == 0 ? ReadReg0(8) : ReadReg0(source);
        int amount = (sbyte)(op & 0xff);
        ushort shifted = amount >= 0
            ? (ushort)(value << Math.Min(amount, 15))
            : (ushort)(value >> Math.Min(-amount, 15));
        WriteReg0(14, shifted);
        UpdateNz(shifted);
    }

    private ushort DataReadDag1(uint op)
    {
        int index = (int)Bit(op, 2, 2);
        ushort address = _i[index];
        ushort value = _bus.ReadDataMemory(address);
        ModifyAddress(index, (int)Bit(op, 0, 2));
        return value;
    }

    private ushort DataReadDag2(uint op)
    {
        int index = 4 + (int)Bit(op, 2, 2);
        ushort address = _i[index];
        ushort value = _bus.ReadDataMemory(address);
        ModifyAddress(index, 4 + (int)Bit(op, 0, 2));
        return value;
    }

    private void DataWriteDag1(uint op, ushort value)
    {
        int index = (int)Bit(op, 2, 2);
        _bus.WriteDataMemory(_i[index], value);
        ModifyAddress(index, (int)Bit(op, 0, 2));
    }

    private void DataWriteDag2(uint op, ushort value)
    {
        int index = 4 + (int)Bit(op, 2, 2);
        _bus.WriteDataMemory(_i[index], value);
        ModifyAddress(index, 4 + (int)Bit(op, 0, 2));
    }

    private ushort ProgramReadDag2(uint op)
    {
        int index = 4 + (int)Bit(op, 2, 2);
        ushort address = _i[index];
        uint value = _bus.ReadProgramWord(address);
        ModifyAddress(index, 4 + (int)Bit(op, 0, 2));
        _px = (ushort)(value & 0xff);
        Trace($"pm-r addr={address:x4} value={value:x6} i{index}={_i[index]:x4}");
        return (ushort)(value >> 8);
    }

    private void ProgramWriteDag2(uint op, ushort value)
    {
        int index = 4 + (int)Bit(op, 2, 2);
        ushort address = _i[index];
        uint programWord = (uint)((value << 8) | (_px & 0xff));
        _bus.WriteProgramWord(address, programWord);
        ModifyAddress(index, 4 + (int)Bit(op, 0, 2));
        Trace($"pm-w addr={address:x4} value={programWord:x6} i{index}={_i[index]:x4}");
    }

    private void ModifyAddress(int iRegister, int mRegister)
    {
        int value = _i[iRegister] + (short)_m[mRegister];
        ushort length = _l[iRegister];
        if (length != 0)
        {
            int mask = length - 1;
            int baseAddress = _i[iRegister] & ~mask;
            value = baseAddress | (value & mask);
        }

        _i[iRegister] = (ushort)value;
    }

    private ushort ReadReg(int group, int register)
        => group switch
        {
            0 => ReadReg0(register),
            1 => register switch
            {
                <= 3 => _i[register],
                <= 7 => _m[register - 4],
                <= 11 => _l[register - 8],
                14 => _pmovlay,
                15 => _dmovlay,
                _ => 0
            },
            2 => register switch
            {
                <= 3 => _i[register + 4],
                <= 7 => _m[register],
                <= 11 => _l[register - 4],
                _ => 0
            },
            3 => register switch
            {
                0 => _astat,
                1 => _mstat,
                2 => _sstat,
                3 => _imask,
                4 => _icntl,
                5 => _cntr,
                7 => _px,
                15 => PopPcValue(),
                _ => 0
            },
            _ => 0
        };

    private void WriteReg(int group, int register, ushort value)
    {
        switch (group)
        {
            case 0:
                WriteReg0(register, value);
                break;
            case 1:
                if (register <= 3) _i[register] = value;
                else if (register <= 7) _m[register - 4] = value;
                else if (register <= 11) _l[register - 8] = value;
                else if (register == 14) _pmovlay = value;
                else if (register == 15) _dmovlay = value;
                break;
            case 2:
                if (register <= 3) _i[register + 4] = value;
                else if (register <= 7) _m[register] = value;
                else if (register <= 11) _l[register - 4] = value;
                break;
            case 3:
                if (register == 0) _astat = (ushort)(value & 0x00ff);
                else if (register == 1) SetMstat(value);
                else if (register == 3) _imask = value;
                else if (register == 4) _icntl = value;
                else if (register == 5) _cntr = (ushort)(value & 0x3fff);
                else if (register == 7) _px = value;
                else if (register == 15) PushPcValue((ushort)(value & 0x3fff));
                break;
        }
    }

    private ushort ReadReg0(int register) => _r[register & 0x0f];

    private void WriteReg0(int register, ushort value) => _r[register & 0x0f] = value;

    private void SetMstat(ushort value)
    {
        bool bankBefore = (_mstat & MStatBank) != 0;
        _mstat = value;
        bool bankAfter = (_mstat & MStatBank) != 0;
        if (bankBefore != bankAfter)
        {
            for (int i = 0; i < _r.Length; i++)
                (_r[i], _rAlt[i]) = (_rAlt[i], _r[i]);
        }
    }

    private void CheckInterrupts()
    {
        bool active = (_icntl & 0x0004) != 0 ? _irq2Latch : _irq2State;
        if (!active || (_imask & 0x0020) == 0)
            return;

        _irq2Latch = false;
        PushPc();
        PushStatus();
        _pc = 0x0004;
        if ((_icntl & 0x0010) != 0)
            _imask &= 0x001f;
        else
            _imask &= 0xffc0;
        Trace($"irq2 vector pc={_pc:x4} imask={_imask:x4}");
    }

    private void PushStatus()
    {
        if (_statStackDepth >= _statStack.GetLength(0))
            return;

        _statStack[_statStackDepth, 0] = _mstat;
        _statStack[_statStackDepth, 1] = _imask;
        _statStack[_statStackDepth, 2] = _astat;
        _statStackDepth++;
    }

    private void PopStatus()
    {
        if (_statStackDepth > 0)
            _statStackDepth--;

        SetMstat(_statStack[_statStackDepth, 0]);
        _imask = _statStack[_statStackDepth, 1];
        _astat = _statStack[_statStackDepth, 2];
    }

    private void ApplyModeControl(uint op)
    {
        ushort value = _mstat;
        if (IsBitSet(op, 5)) value = SetOrClear(value, MStatBank, IsBitSet(op, 4));
        SetMstat(value);
    }

    private bool Condition(int condition)
        => condition switch
        {
            0x0 => (_astat & ZFlag) != 0,
            0x1 => (_astat & ZFlag) == 0,
            0xa => (_astat & NFlag) != 0,
            0xb => (_astat & NFlag) == 0,
            0xf => true,
            _ => true
        };

    private void UpdateNz(ushort value)
    {
        _astat &= unchecked((ushort)~(NFlag | ZFlag | VFlag | CFlag));
        if (value == 0)
            _astat |= ZFlag;
        if ((value & 0x8000) != 0)
            _astat |= NFlag;
    }

    private void PushPc()
        => PushPcValue(_pc);

    private void PushPcValue(ushort value)
    {
        if (_pcStackDepth < _pcStack.Length)
            _pcStack[_pcStackDepth++] = value;
    }

    private void PopPc()
    {
        _pc = PopPcValue();
    }

    private ushort PcStackTop()
        => _pcStackDepth > 0 ? _pcStack[_pcStackDepth - 1] : _pcStack[0];

    private ushort PopPcValue()
    {
        if (_pcStackDepth > 0)
            return _pcStack[--_pcStackDepth];
        return _pcStack[0];
    }

    private void PushLoop(uint value)
    {
        if (_loopStackDepth < _loopStack.Length)
            _loopStack[_loopStackDepth++] = value;
        _loop = (ushort)((value >> 4) & 0x3fff);
        _loopCondition = (int)(value & 0x0f);
    }

    private void PopLoop()
    {
        if (_loopStackDepth > 0)
            _loopStackDepth--;
        if (_loopStackDepth == 0)
        {
            _loop = 0xffff;
            _loopCondition = 0;
            return;
        }

        uint value = _loopStack[_loopStackDepth - 1];
        _loop = (ushort)((value >> 4) & 0x3fff);
        _loopCondition = (int)(value & 0x0f);
    }

    private static ushort SetOrClear(ushort value, ushort mask, bool set)
        => set ? (ushort)(value | mask) : (ushort)(value & ~mask);

    private static uint Bit(uint value, int start, int count = 1)
        => (value >> start) & ((1u << count) - 1u);

    private static bool IsBitSet(uint value, int bit)
        => ((value >> bit) & 1u) != 0;

    private static ushort AdspConstant(int index)
    {
        ReadOnlySpan<ushort> constants =
        [
            0x0001, 0xfffe, 0x0002, 0xfffd, 0x0004, 0xfffb, 0x0008, 0xfff7,
            0x0010, 0xffef, 0x0020, 0xffdf, 0x0040, 0xffbf, 0x0080, 0xff7f,
            0x0100, 0xfeff, 0x0200, 0xfdff, 0x0400, 0xfbff, 0x0800, 0xf7ff,
            0x1000, 0xefff, 0x2000, 0xdfff, 0x4000, 0xbfff, 0x8000, 0x7fff
        ];
        return constants[index & 31];
    }

    private void Trace(string message)
    {
        if (!_trace || _traceCount++ >= _traceLimit)
            return;
        Console.WriteLine($"[GAUNTDL:ADSP] step={_steps} {message}");
    }
}

internal sealed class GauntletInputPanel
{
    public GauntletPlayerInput Player1 { get; private set; }
    public bool Service { get; set; }
    public bool Test { get; set; }

    public void SetPlayer1(bool up, bool down, bool left, bool right, bool attack, bool magic, bool start, bool coin)
        => Player1 = new GauntletPlayerInput(up, down, left, right, attack, magic, start, coin);
}

internal readonly record struct GauntletPlayerInput(
    bool Up,
    bool Down,
    bool Left,
    bool Right,
    bool Attack,
    bool Magic,
    bool Start,
    bool Coin);

public interface IVoodooBackend
{
    void WriteRegister(uint address, uint value);
    uint ReadRegister(uint address);
    void WriteFifo(uint wordOffset, uint value);
    uint ReadStatus(bool vblank);
    uint ReadLfb32(uint offset);
    void WriteLfb32(uint offset, uint value);
    uint ReadTexture32(uint offset);
    void WriteTexture32(uint offset, uint value);
    string DebugStatus { get; }
    string RecentEventStatus { get; }
    void RenderFrame(EutherFrameTarget target);
}

public readonly record struct EutherFrameTarget(byte[] Buffer, int Width, int Height, int Stride);

internal sealed class VoodooFacade : IVoodooBackend
{
    private IVoodooBackend _backend = new VoodooBringupBackend();

    public bool TraceEnabled => _backend is VoodooTraceBackend;
    public bool HasVideoActivity => _backend is VoodooBringupBackend { HasVideoActivity: true };
    public string DebugStatus => _backend.DebugStatus;
    public string RecentEventStatus => _backend.RecentEventStatus;
    private Func<ulong>? _cpuPcProvider;

    public void SetCpuPcProvider(Func<ulong> provider)
    {
        _cpuPcProvider = provider;
        ApplyCpuPcProvider();
    }

    public void Reset()
    {
        _backend = VoodooTraceBackend.IsEnabled()
            ? new VoodooTraceBackend()
            : new VoodooBringupBackend();
        ApplyCpuPcProvider();
    }

    public void WriteRegister(uint address, uint value) => _backend.WriteRegister(address, value);
    public uint ReadRegister(uint address) => _backend.ReadRegister(address);
    public void WriteFifo(uint wordOffset, uint value) => _backend.WriteFifo(wordOffset, value);
    public uint ReadStatus(bool vblank) => _backend.ReadStatus(vblank);
    public uint ReadLfb32(uint offset) => _backend.ReadLfb32(offset);
    public void WriteLfb32(uint offset, uint value) => _backend.WriteLfb32(offset, value);
    public uint ReadTexture32(uint offset) => _backend.ReadTexture32(offset);
    public void WriteTexture32(uint offset, uint value) => _backend.WriteTexture32(offset, value);
    public void RenderFrame(EutherFrameTarget target) => _backend.RenderFrame(target);

    private void ApplyCpuPcProvider()
    {
        if (_backend is VoodooBringupBackend bringup)
            bringup.CpuPcProvider = _cpuPcProvider;
    }
}

internal class VoodooBringupBackend : IVoodooBackend
{
    private const int LfbBytes = 4 * 1024 * 1024;
    private const int LfbPixels = LfbBytes / 2;
    private const int LfbRowPixels = 1024;
    private const int LfbRows = LfbPixels / LfbRowPixels;
    private const int TextureBytes = 8 * 1024 * 1024;
    private const int TextureWords = TextureBytes / 4;
    private const int CmdFifoWords = 1 << 16;
    private const int CmdFifoMask = CmdFifoWords - 1;
    private const int RegTriangleCommand = 0x80 >> 2;
    private const int RegFtriangleCommand = 0x100 >> 2;
    private const int RegFbzMode = 0x110 >> 2;
    private const int RegLfbMode = 0x114 >> 2;
    private const int RegClipLeftRight = 0x118 >> 2;
    private const int RegClipLowYHighY = 0x11c >> 2;
    private const int RegFastfillCommand = 0x124 >> 2;
    private const int RegSwapbufferCommand = 0x128 >> 2;
    private const int RegZaColor = 0x130 >> 2;
    private const int RegColor0 = 0x144 >> 2;
    private const int RegColor1 = 0x148 >> 2;
    private const int RegCmdFifoRdPtr = 0x1e8 >> 2;
    private const int RegCmdFifoDepth = 0x1f4 >> 2;
    private const int RegCmdFifoHoles = 0x1f8 >> 2;
    private const uint RegBltSrcBaseAddr = 0x2c0u >> 2;
    private const int RegFbiInit2 = 0x218 >> 2;
    private const int RegFbiInit3 = 0x21c >> 2;

    private readonly uint[] _registers = new uint[0x400];
    private readonly ushort[][] _colorBuffers =
    [
        new ushort[LfbPixels],
        new ushort[LfbPixels],
        new ushort[LfbPixels]
    ];
    private readonly List<uint> _fifoBuffer = new();
    private readonly uint[] _textureMemory = new uint[TextureWords];
    private readonly uint[] _cmdFifoRam = new uint[CmdFifoWords];
    private readonly bool[] _cmdFifoValid = new bool[CmdFifoWords];
    private readonly SetupVertex[] _setupVertices = new SetupVertex[3];
    private readonly int[] _fifoPacketTypeCounts = new int[8];
    private readonly bool[] _lastFastFillValid = new bool[3];
    private readonly int[] _lastFastFillX0 = new int[3];
    private readonly int[] _lastFastFillX1 = new int[3];
    private readonly int[] _lastFastFillY0 = new int[3];
    private readonly int[] _lastFastFillY1 = new int[3];
    private readonly ushort[] _lastFastFillColor = new ushort[3];
    private readonly bool[] _pendingClearValid = new bool[3];
    private readonly int[] _pendingClearX0 = new int[3];
    private readonly int[] _pendingClearX1 = new int[3];
    private readonly int[] _pendingClearY0 = new int[3];
    private readonly int[] _pendingClearY1 = new int[3];
    private readonly ushort[] _pendingClearColor = new ushort[3];
    private int _registerWriteCount;
    private int _fifoWriteCount;
    private int _fifoPacketCount;
    private int _fifoDrawPacketCount;
    private int _directTriangleCommandCount;
    private int _setupTriangleCommandCount;
    private int _statusReadCount;
    private int _lfbWriteCount;
    private int _textureWriteCount;
    private int _fastFillCount;
    private int _swapBufferCount;
    private int _pendingSwapCount;
    private int _renderFrame;
    private int _setupVertexCount;
    private int _frontBufferIndex;
    private int _backBufferIndex = 1;
    private int _cmdFifoReadIndex;
    private int _cmdFifoDepth;
    private int _cmdFifoHoles;
    private bool _cmdFifoReadPointerWritten;
    private bool _cmdFifoJumped;
    private readonly bool _showDebugOverlay = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_SHOW_VIDEO_OVERLAY") == "1";
    private readonly bool _traceDraw = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_DRAW") == "1";
    private readonly bool _debugBufferCounts = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_DEBUG_BUFFER_COUNTS") == "1";
    private readonly bool _recordVoodooEvents = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_RECORD_VOODOO_EVENTS") == "1";
    private readonly bool _profileStatusPcs = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_PROFILE_VOODOO_STATUS_PCS") == "1";
    private readonly int _drawTraceLimit = ParseDrawTraceLimit("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_DRAW_LIMIT", 96);
    private readonly string[] _recentVoodooEvents = new string[64];
    private readonly Dictionary<ulong, ulong> _statusPcCounts = [];
    private int _drawTraceCount;
    private int _recentVoodooEventSequence;

    public Func<ulong>? CpuPcProvider { get; set; }
    public bool HasVideoActivity => _registerWriteCount > 0 || _fifoWriteCount > 0 || _lfbWriteCount > 0 || _textureWriteCount > 0;
    public string DebugStatus
        => $"fifo={_fifoWriteCount}/{_fifoPacketCount} p3={_fifoDrawPacketCount} " +
           $"tri={_directTriangleCommandCount}+{_setupTriangleCommandCount} fill={_fastFillCount} swap={_swapBufferCount} stat={_statusReadCount} " +
           $"lfb={_lfbWriteCount} tex={_textureWriteCount} buf={_frontBufferIndex}/{_backBufferIndex}/{GetColorBufferCount()} " +
           GetBufferCountDebugStatus() +
           $"t={_fifoPacketTypeCounts[0]}/{_fifoPacketTypeCounts[1]}/{_fifoPacketTypeCounts[2]}/{_fifoPacketTypeCounts[3]}/{_fifoPacketTypeCounts[4]}/{_fifoPacketTypeCounts[5]} " +
           $"pend={_pendingSwapCount} cmdrd=0x{_cmdFifoReadIndex:X4} fbz=0x{_registers[RegFbzMode]:X8} lfbm=0x{_registers[RegLfbMode]:X8}";
    public string RecentEventStatus => FormatRecentVoodooEvents();
    public string StatusPcProfile => GetStatusPcProfile();

    public virtual void WriteRegister(uint address, uint value)
    {
        uint register = (address >> 2) & 0xffu;
        _registers[register] = value;
        _registerWriteCount++;
        if (IsInterestingEventRegister(register))
            RecordVoodooEvent($"reg[{register:x3}]=0x{value:x8}");
        switch (register)
        {
            case RegTriangleCommand:
                DrawIntegerTriangle();
                break;
            case RegFtriangleCommand:
                DrawFloatTriangle();
                break;
            case RegFastfillCommand:
                FastFill();
                break;
            case RegSwapbufferCommand:
                SwapBuffers(value);
                break;
            case RegCmdFifoRdPtr:
                _cmdFifoReadIndex = (int)((value >> 2) & CmdFifoMask);
                _cmdFifoReadPointerWritten = true;
                DecodeCommandFifoPackets();
                break;
            case RegCmdFifoDepth:
                _cmdFifoDepth = (int)(value & 0xffffu);
                break;
            case RegCmdFifoHoles:
                _cmdFifoHoles = (int)(value & 0xffffu);
                break;
            case 0xa8u:
                DrawSetupTriangle();
                break;
            case 0xa9u:
                BeginSetupTriangle();
                break;
        }
    }

    public virtual uint ReadRegister(uint address)
    {
        uint register = (address >> 2) & 0xffu;
        return register switch
        {
            RegCmdFifoRdPtr => (uint)(_cmdFifoReadIndex << 2),
            RegCmdFifoDepth => (uint)Math.Clamp(_cmdFifoDepth, 0, 0xffff),
            RegCmdFifoHoles => (uint)Math.Clamp(_cmdFifoHoles, 0, 0xffff),
            _ => _registers[register]
        };
    }

    public virtual void WriteFifo(uint wordOffset, uint value)
    {
        int index = (int)(wordOffset & CmdFifoMask);
        if (index == 0 && _cmdFifoReadPointerWritten && _cmdFifoReadIndex != 0)
        {
            Array.Clear(_cmdFifoValid);
            _cmdFifoReadIndex = 0;
            _cmdFifoDepth = 0;
            _cmdFifoHoles = 0;
            _cmdFifoJumped = false;
        }

        _cmdFifoRam[index] = value;
        if (!_cmdFifoValid[index])
            _cmdFifoDepth = Math.Min(0xffff, _cmdFifoDepth + 1);
        _cmdFifoValid[index] = true;
        _fifoWriteCount++;

        if (!_cmdFifoReadPointerWritten)
        {
            _cmdFifoReadIndex = index;
            _cmdFifoReadPointerWritten = true;
        }

        DecodeCommandFifoPackets();
    }

    public uint ReadStatus(bool vblank)
    {
        _statusReadCount++;
        CountStatusPc();
        if (vblank && _pendingSwapCount > 0)
        {
            _pendingSwapCount--;
            ExecuteSwapBuffers(0);
            RecordVoodooEvent($"status swap-drain vblank={(vblank ? 1 : 0)} pend={_pendingSwapCount}");
        }

        uint status = 0x0ffff03fu | ((uint)(_frontBufferIndex & 3) << 10);
        if (vblank)
            status |= 0x40u;
        status |= (uint)Math.Clamp(_pendingSwapCount, 0, 7) << 28;
        if (_recordVoodooEvents && (_statusReadCount <= 16 || _pendingSwapCount > 0 || (_statusReadCount & 0x3ff) == 0))
            RecordVoodooEvent($"status read value=0x{status:x8} vblank={(vblank ? 1 : 0)} pend={_pendingSwapCount}");
        return status;
    }

    public virtual uint ReadLfb32(uint offset)
    {
        uint lfbMode = _registers[RegLfbMode];
        int pixel = GetLfbPixelOffset(offset, IsTwoPixelLfbFormat((int)(lfbMode & 0x0fu)), lfbMode);
        ushort[] buffer = GetLfbReadBuffer(lfbMode);
        ushort low = buffer[pixel & (LfbPixels - 1)];
        ushort high = buffer[(pixel + 1) & (LfbPixels - 1)];
        uint value = (uint)(low | (high << 16));
        if (((lfbMode >> 15) & 1u) != 0)
            value = (value << 16) | (value >> 16);
        if (((lfbMode >> 16) & 1u) != 0)
            value = BinaryPrimitives.ReverseEndianness(value);
        return value;
    }

    public virtual void WriteLfb32(uint offset, uint value)
    {
        uint lfbMode = _registers[RegLfbMode];
        if (((lfbMode >> 12) & 1u) != 0)
            value = BinaryPrimitives.ReverseEndianness(value);
        if (((lfbMode >> 11) & 1u) != 0)
            value = (value << 16) | (value >> 16);

        int format = (int)(lfbMode & 0x0fu);
        int rgbaLanes = (int)((lfbMode >> 9) & 0x03u);
        bool twoPixels = IsTwoPixelLfbFormat(format);
        int pixel = GetLfbPixelOffset(offset, twoPixels, lfbMode);
        int bufferIndex = GetLfbWriteBufferIndex(lfbMode);
        InvalidateFastFillCache(bufferIndex);
        ushort[] buffer = _colorBuffers[bufferIndex];

        if (TryExpandLfbPixel(value, format, rgbaLanes, highHalf: false, out ushort first))
            buffer[pixel & (LfbPixels - 1)] = first;
        if (twoPixels && TryExpandLfbPixel(value, format, rgbaLanes, highHalf: true, out ushort second))
            buffer[(pixel + 1) & (LfbPixels - 1)] = second;
        _lfbWriteCount++;
    }

    public virtual void WriteTexture32(uint offset, uint value)
    {
        _textureMemory[(offset >> 2) & (TextureWords - 1)] = value;
        _textureWriteCount++;
    }

    public virtual uint ReadTexture32(uint offset)
        => _textureMemory[(offset >> 2) & (TextureWords - 1)];

    public void RenderFrame(EutherFrameTarget target)
    {
        if (!HasVideoActivity || target.Buffer is null || target.Width <= 0 || target.Height <= 0 || target.Stride < target.Width * 4)
            return;

        _renderFrame++;
        if (!TryRenderLfb(target))
        {
            Clear(target, 0xff080b0fu);
            DrawRegisterBands(target);
        }
        if (_showDebugOverlay)
            DrawViewportOverlay(target);
    }

    private bool TryRenderLfb(EutherFrameTarget target)
    {
        if (_lfbWriteCount == 0)
            return false;

        int copyWidth = Math.Min(target.Width, 640);
        int copyHeight = Math.Min(target.Height, 480);
        MaterializePendingClear(_frontBufferIndex);
        ushort[] front = _colorBuffers[_frontBufferIndex];
        for (int y = 0; y < copyHeight; y++)
        {
            int src = y * 1024;
            int dst = y * target.Stride;
            for (int x = 0; x < copyWidth; x++)
            {
                ushort rgb = front[(src + x) & (LfbPixels - 1)];
                uint bgra = Rgb565ToBgra(rgb);
                target.Buffer[dst + 0] = (byte)(bgra & 0xff);
                target.Buffer[dst + 1] = (byte)((bgra >> 8) & 0xff);
                target.Buffer[dst + 2] = (byte)((bgra >> 16) & 0xff);
                target.Buffer[dst + 3] = 0xff;
                dst += 4;
            }
        }

        return _lfbWriteCount > 0;
    }

    private void DrawRegisterBands(EutherFrameTarget target)
    {
        int bandHeight = Math.Max(6, target.Height / 64);
        for (int i = 0; i < 32; i++)
        {
            uint register = i < 16
                ? (0x100u >> 2) + (uint)i
                : (0x200u >> 2) + (uint)(i - 16);
            uint value = _registers[register];
            uint color = 0xff000000u |
                ((value << 3) & 0x00ff0000u) |
                ((value >> 5) & 0x0000ff00u) |
                ((value >> 13) & 0x000000ffu);
            FillRect(target, 0, i * bandHeight, target.Width, bandHeight, color);
        }
    }

    private void DrawViewportOverlay(EutherFrameTarget target)
    {
        uint clipX = _registers[(0x118u >> 2) & 0x3ffu];
        uint clipY = _registers[(0x11cu >> 2) & 0x3ffu];
        int x0 = (int)((clipX >> 16) & 0x7ff);
        int x1 = (int)(clipX & 0x7ff);
        int y0 = (int)((clipY >> 16) & 0x7ff);
        int y1 = (int)(clipY & 0x7ff);
        if (x1 <= x0 || x1 > target.Width)
        {
            uint dimensions = _registers[(0x20cu >> 2) & 0x3ffu];
            x0 = 0;
            x1 = dimensions != 0
                ? Math.Clamp((int)(dimensions & 0x7ff) + 1, 1, target.Width)
                : target.Width;
        }
        if (y1 <= y0 || y1 > target.Height)
        {
            uint dimensions = _registers[(0x20cu >> 2) & 0x3ffu];
            y0 = 0;
            y1 = dimensions != 0
                ? Math.Clamp((int)((dimensions >> 16) & 0x7ff), 1, target.Height)
                : target.Height;
        }

        DrawRect(target, x0, y0, x1 - x0, y1 - y0, 0xffffffffu);

        int sweep = _renderFrame % Math.Max(1, target.Width);
        FillRect(target, sweep, y0, 4, y1 - y0, 0xff00d7ffu);
        FillRect(target, 16, target.Height - 28, Math.Min(target.Width - 32, _registerWriteCount / 32), 8, 0xff39d353u);
        FillRect(target, 16, target.Height - 18, Math.Min(target.Width - 32, _lfbWriteCount / 64), 8, 0xfff7b955u);
        FillRect(target, 16, target.Height - 8, Math.Min(target.Width - 32, (_fifoWriteCount + _fifoPacketCount * 8 + _textureWriteCount) / 256), 6, 0xffc678ddU);
        FillRect(target, target.Width - 24, 16, 8, Math.Min(target.Height - 32, _fifoDrawPacketCount * 4), 0xffff6b6bu);
        FillRect(target, target.Width - 40, 16, 8, Math.Min(target.Height - 32, _fastFillCount * 4), 0xff39d353u);
        FillRect(target, target.Width - 56, 16, 8, Math.Min(target.Height - 32, _swapBufferCount * 4), 0xfff7b955u);
    }

    private void FastFill()
    {
        uint clipX = _registers[0x46];
        uint clipY = _registers[0x47];
        int x0 = Math.Clamp((int)((clipX >> 16) & 0x7ff), 0, 1024);
        int x1 = Math.Clamp((int)(clipX & 0x7ff), 0, 1024);
        int y0 = Math.Clamp((int)((clipY >> 16) & 0x7ff), 0, 1024);
        int y1 = Math.Clamp((int)(clipY & 0x7ff), 0, 1024);
        if (x1 <= x0)
        {
            x0 = 0;
            x1 = 640;
        }
        if (y1 <= y0)
        {
            y0 = 0;
            y1 = 480;
        }

        x1 = Math.Min(x1, 1024);
        y1 = Math.Min(y1, LfbRows);
        ushort color = ArgbToRgb565(_registers[RegColor1]);
        if (color == 0)
            color = ArgbToRgb565(_registers[RegColor0]);
        if (color == 0)
            color = (ushort)_registers[RegZaColor];

        TraceDraw($"fastfill clip=({x0},{y0})-({x1},{y1}) color=0x{color:X4} c0=0x{_registers[RegColor0]:X8} c1=0x{_registers[RegColor1]:X8} fbz=0x{_registers[RegFbzMode]:X8}");
        int bufferIndex = GetDrawBufferIndex();
        int width = x1 - x0;
        if (!IsCachedFastFill(bufferIndex, x0, x1, y0, y1, color))
        {
            SetPendingClear(bufferIndex, x0, x1, y0, y1, color);
            CacheFastFill(bufferIndex, x0, x1, y0, y1, color);
        }

        _fastFillCount++;
        _lfbWriteCount += Math.Max(1, (width * (y1 - y0) + 1) / 2);
    }

    private void DecodeCommandFifoPackets()
    {
        int guard = 0;
        while (guard++ < 2048 && _cmdFifoValid[_cmdFifoReadIndex])
        {
            uint command = _cmdFifoRam[_cmdFifoReadIndex];
            int wordsNeeded = GetFifoPacketWordsNeeded(command);
            if (wordsNeeded <= 0)
                wordsNeeded = 1;
            if (!HasCommandFifoWords(_cmdFifoReadIndex, wordsNeeded))
                return;

            _fifoBuffer.Clear();
            for (int i = 0; i < wordsNeeded; i++)
            {
                int index = (_cmdFifoReadIndex + i) & CmdFifoMask;
                _fifoBuffer.Add(_cmdFifoRam[index]);
            }

            _cmdFifoJumped = false;
            DecodeFifoPacket(command, wordsNeeded);
            _fifoPacketCount++;
            for (int i = 0; i < wordsNeeded; i++)
                _cmdFifoValid[(_cmdFifoReadIndex + i) & CmdFifoMask] = false;
            _cmdFifoDepth = Math.Max(0, _cmdFifoDepth - wordsNeeded);
            if (!_cmdFifoJumped)
                _cmdFifoReadIndex = (_cmdFifoReadIndex + wordsNeeded) & CmdFifoMask;
        }
    }

    private bool HasCommandFifoWords(int start, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (!_cmdFifoValid[(start + i) & CmdFifoMask])
                return false;
        }

        return true;
    }

    private void DecodeFifoPacket(uint command, int wordsNeeded)
    {
        _fifoPacketTypeCounts[command & 7u]++;
        RecordInterestingFifoEvent(command, wordsNeeded);
        switch (command & 7u)
        {
            case 0:
                DecodeFifoType0(command);
                break;
            case 1:
                DecodeFifoType1(command);
                break;
            case 2:
                DecodeFifoType2(command);
                break;
            case 3:
                _fifoDrawPacketCount++;
                DecodeFifoType3(command, wordsNeeded);
                break;
            case 4:
                DecodeFifoType4(command);
                break;
            case 5:
                DecodeFifoType5(command, wordsNeeded);
                break;
        }
    }

    private void DecodeFifoType0(uint command)
    {
        int function = (int)((command >> 3) & 7u);
        if (function == 3)
        {
            _cmdFifoReadIndex = (int)((command >> 6) & 0x7fffffu) & CmdFifoMask;
            _cmdFifoJumped = true;
        }
    }

    private void DecodeFifoType1(uint command)
    {
        int count = (int)(command >> 16);
        int increment = ((command >> 15) & 1u) != 0 ? 1 : 0;
        uint target = (command >> 3) & 0xfffu;
        for (int i = 0; i < count; i++, target += (uint)increment)
            WriteCmdFifoRegister(target, _fifoBuffer[1 + i]);
    }

    private void DecodeFifoType2(uint command)
    {
        int source = 1;
        for (uint regbit = 3; regbit <= 31 && source < _fifoBuffer.Count; regbit++)
        {
            if (((command >> (int)regbit) & 1u) != 0)
                WriteCmdFifoRegister(RegBltSrcBaseAddr + regbit - 3u, _fifoBuffer[source++]);
        }
    }

    private void DecodeFifoType4(uint command)
    {
        uint target = (command >> 3) & 0xfffu;
        int source = 1;
        for (int bit = 15; bit <= 28; bit++, target++)
        {
            if (((command >> bit) & 1u) == 0)
                continue;

            WriteCmdFifoRegister(target, _fifoBuffer[source++]);
        }
    }

    private void WriteCmdFifoRegister(uint target, uint value)
        => WriteRegister((target & 0xffu) << 2, value);

    private void RecordInterestingFifoEvent(uint command, int wordsNeeded)
    {
        if (!_recordVoodooEvents)
            return;

        uint type = command & 7u;
        switch (type)
        {
            case 0:
                RecordVoodooEvent($"fifo type0 cmd=0x{command:x8} words={wordsNeeded} rd=0x{_cmdFifoReadIndex:x4}");
                break;
            case 1:
            {
                int count = (int)(command >> 16);
                uint target = (command >> 3) & 0xfffu;
                if (TouchesInterestingEventRegister(target, (uint)Math.Max(0, count)))
                    RecordVoodooEvent($"fifo type1 cmd=0x{command:x8} target=0x{target:x3} count={count} inc={(command >> 15) & 1u} words={wordsNeeded} rd=0x{_cmdFifoReadIndex:x4}");
                break;
            }
            case 3:
                RecordVoodooEvent($"fifo type3 cmd=0x{command:x8} count={(command >> 6) & 0xfu} code={(command >> 3) & 7u} words={wordsNeeded} rd=0x{_cmdFifoReadIndex:x4}");
                break;
            case 4:
            {
                uint target = (command >> 3) & 0xfffu;
                uint mask = (command >> 15) & 0x3fffu;
                if (TouchesInterestingType4Register(target, mask))
                    RecordVoodooEvent($"fifo type4 cmd=0x{command:x8} target=0x{target:x3} mask=0x{mask:x4} words={wordsNeeded} rd=0x{_cmdFifoReadIndex:x4}");
                break;
            }
            case 5:
                RecordVoodooEvent($"fifo type5 cmd=0x{command:x8} count={(command >> 3) & 0x7ffffu} space={command >> 30} words={wordsNeeded} rd=0x{_cmdFifoReadIndex:x4}");
                break;
        }
    }

    private void RecordVoodooEvent(string description)
    {
        if (!_recordVoodooEvents)
            return;

        int sequence = _recentVoodooEventSequence++;
        ulong pc = CpuPcProvider?.Invoke() ?? 0;
        string pcStatus = pc != 0 ? $" pc=0x{pc:x16}" : "";
        _recentVoodooEvents[sequence & (_recentVoodooEvents.Length - 1)] = $"{sequence}:{description}{pcStatus}";
    }

    private string FormatRecentVoodooEvents()
    {
        int count = Math.Min(_recentVoodooEventSequence, _recentVoodooEvents.Length);
        if (count == 0)
            return "none";

        int start = _recentVoodooEventSequence - count;
        return string.Join(" | ", Enumerable.Range(0, count).Select(i => _recentVoodooEvents[(start + i) & (_recentVoodooEvents.Length - 1)]));
    }

    private void CountStatusPc()
    {
        if (!_profileStatusPcs)
            return;

        ulong pc = CpuPcProvider?.Invoke() ?? 0;
        if (pc == 0)
            return;

        _statusPcCounts.TryGetValue(pc, out ulong count);
        _statusPcCounts[pc] = count + 1;
    }

    private string GetStatusPcProfile()
    {
        if (_statusPcCounts.Count == 0)
            return "none";

        return string.Join(",", _statusPcCounts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(16)
            .Select(pair => $"0x{pair.Key:x16}:{pair.Value}"));
    }

    private static bool TouchesInterestingEventRegister(uint target, uint count)
    {
        for (uint i = 0; i < count; i++)
        {
            if (IsInterestingEventRegister(target + i))
                return true;
        }

        return false;
    }

    private static bool TouchesInterestingType4Register(uint target, uint mask)
    {
        for (uint bit = 0; bit < 14; bit++)
        {
            if (((mask >> (int)bit) & 1u) != 0 && IsInterestingEventRegister(target + bit))
                return true;
        }

        return false;
    }

    private static bool IsInterestingEventRegister(uint register)
        => register is RegTriangleCommand or RegFtriangleCommand or RegFbzMode or RegLfbMode or
            RegClipLeftRight or RegClipLowYHighY or RegFastfillCommand or RegSwapbufferCommand or
            RegColor0 or RegColor1 or 0x98u or 0x99u or 0x9au or 0x9bu or 0x9cu or 0x9du or 0x9eu or
            0xa8u or 0xa9u;

    private void DecodeFifoType3(uint command, int wordsNeeded)
    {
        int count = (int)((command >> 6) & 0xfu);
        int code = (int)((command >> 3) & 7u);
        ushort fallbackColor = GetDrawColor();
        int source = 1;

        _registers[0x98] = ((command >> 10) & 0xffu) | (((command >> 22) & 0xfu) << 16);
        for (int vertex = 0; vertex < count && source < wordsNeeded; vertex++)
        {
            if (!TryReadFloat(wordsNeeded, ref source, out float x) ||
                !TryReadFloat(wordsNeeded, ref source, out float y))
            {
                return;
            }

            ushort color = fallbackColor;
            if (((command >> 28) & 1u) != 0)
            {
                if (((command >> 10) & 3u) != 0)
                {
                    if (!TryReadWord(wordsNeeded, ref source, out uint argb))
                        return;
                    if (((command >> 10) & 1u) != 0)
                        color = PackedColorToRgb565(argb);
                }
            }
            else
            {
                if (((command >> 10) & 1u) != 0)
                {
                    if (!TryReadFloat(wordsNeeded, ref source, out float r) ||
                        !TryReadFloat(wordsNeeded, ref source, out float g) ||
                        !TryReadFloat(wordsNeeded, ref source, out float b))
                    {
                        return;
                    }
                    color = FloatColorToRgb565(r, g, b);
                }
                if (((command >> 11) & 1u) != 0 && !SkipWord(wordsNeeded, ref source))
                    return;
            }

            if (((command >> 12) & 1u) != 0 && !SkipWord(wordsNeeded, ref source))
                return;
            if (((command >> 13) & 1u) != 0 && !SkipWord(wordsNeeded, ref source))
                return;
            if (((command >> 14) & 1u) != 0 && !SkipWord(wordsNeeded, ref source))
                return;
            if (((command >> 15) & 1u) != 0 && (!SkipWord(wordsNeeded, ref source) || !SkipWord(wordsNeeded, ref source)))
                return;
            if (((command >> 16) & 1u) != 0 && !SkipWord(wordsNeeded, ref source))
                return;
            if (((command >> 17) & 1u) != 0 && (!SkipWord(wordsNeeded, ref source) || !SkipWord(wordsNeeded, ref source)))
                return;

            PushSetupVertex(new SetupVertex(x, y, color), code, vertex, ((command >> 22) & 1u) != 0);
        }
    }

    private void DecodeFifoType5(uint command, int wordsNeeded)
    {
        int count = (int)((command >> 3) & 0x7ffffu);
        if (count <= 0 || wordsNeeded < 2 + count)
            return;

        uint target = _fifoBuffer[1] / 4u;
        uint space = command >> 30;
        for (int i = 0; i < count; i++, target++)
        {
            uint value = _fifoBuffer[2 + i];
            if (space == 3)
                WriteTexture32(target << 2, value);
            else if (space is 0 or 2)
                WriteLfb32(target << 2, value);
        }
    }

    private void BeginSetupTriangle()
    {
        SetupVertex vertex = ReadCurrentSetupVertex();
        _setupVertices[0] = vertex;
        _setupVertices[1] = vertex;
        _setupVertices[2] = vertex;
        _setupVertexCount = 1;
    }

    private void DrawSetupTriangle()
    {
        _setupTriangleCommandCount++;
        PushSetupVertex(ReadCurrentSetupVertex(), code: 1, vertexIndex: _setupVertexCount == 0 ? 0 : 1, fanMode: IsSetupFanMode());
    }

    private void DrawIntegerTriangle()
    {
        _directTriangleCommandCount++;
        ushort color = GetIntegerDrawColor();
        TraceDraw($"itri color=0x{color:X4} xy=({FixedVertexCoordinate(_registers[0x02]):F1},{FixedVertexCoordinate(_registers[0x03]):F1})/({FixedVertexCoordinate(_registers[0x04]):F1},{FixedVertexCoordinate(_registers[0x05]):F1})/({FixedVertexCoordinate(_registers[0x06]):F1},{FixedVertexCoordinate(_registers[0x07]):F1}) fbz=0x{_registers[RegFbzMode]:X8}");
        DrawTriangleWire(
            FixedVertexCoordinate(_registers[0x02]),
            FixedVertexCoordinate(_registers[0x03]),
            FixedVertexCoordinate(_registers[0x04]),
            FixedVertexCoordinate(_registers[0x05]),
            FixedVertexCoordinate(_registers[0x06]),
            FixedVertexCoordinate(_registers[0x07]),
            color);
    }

    private void DrawFloatTriangle()
    {
        _directTriangleCommandCount++;
        ushort color = GetFloatDrawColor();
        TraceDraw($"ftri color=0x{color:X4} xy=({FloatFromRegister(_registers[0x22]):F1},{FloatFromRegister(_registers[0x23]):F1})/({FloatFromRegister(_registers[0x24]):F1},{FloatFromRegister(_registers[0x25]):F1})/({FloatFromRegister(_registers[0x26]):F1},{FloatFromRegister(_registers[0x27]):F1}) rgb=({FloatFromRegister(_registers[0x28]):F3},{FloatFromRegister(_registers[0x29]):F3},{FloatFromRegister(_registers[0x2a]):F3}) fbz=0x{_registers[RegFbzMode]:X8}");
        DrawTriangleWire(
            FloatFromRegister(_registers[0x22]),
            FloatFromRegister(_registers[0x23]),
            FloatFromRegister(_registers[0x24]),
            FloatFromRegister(_registers[0x25]),
            FloatFromRegister(_registers[0x26]),
            FloatFromRegister(_registers[0x27]),
            color);
    }

    private SetupVertex ReadCurrentSetupVertex()
    {
        ushort color = PackedColorToRgb565(_registers[0x9b]);
        if (color == 0)
            color = FloatColorToRgb565(
                FloatFromRegister(_registers[0x9c]),
                FloatFromRegister(_registers[0x9d]),
                FloatFromRegister(_registers[0x9e]));
        if (color == 0)
            color = GetDrawColor();

        return new SetupVertex(
            FloatFromRegister(_registers[0x99]),
            FloatFromRegister(_registers[0x9a]),
            color);
    }

    private void PushSetupVertex(SetupVertex vertex, int code, int vertexIndex, bool fanMode)
    {
        if ((code == 1 && vertexIndex == 0) || (code == 0 && vertexIndex % 3 == 0) || _setupVertexCount == 0)
        {
            _setupVertices[0] = vertex;
            _setupVertices[1] = vertex;
            _setupVertices[2] = vertex;
            _setupVertexCount = 1;
            return;
        }

        if (!fanMode)
            _setupVertices[0] = _setupVertices[1];
        _setupVertices[1] = _setupVertices[2];
        _setupVertices[2] = vertex;
        if (++_setupVertexCount >= 3)
            DrawSetupTriangleVertices();
    }

    private void DrawSetupTriangleVertices()
    {
        ushort color = _setupVertices[2].Color != 0 ? _setupVertices[2].Color : GetDrawColor();
        TraceDraw($"stri color=0x{color:X4} xy=({_setupVertices[0].X:F1},{_setupVertices[0].Y:F1})/({_setupVertices[1].X:F1},{_setupVertices[1].Y:F1})/({_setupVertices[2].X:F1},{_setupVertices[2].Y:F1}) setup=0x{_registers[0x98]:X8} fbz=0x{_registers[RegFbzMode]:X8}");
        DrawTriangleWire(
            _setupVertices[0].X,
            _setupVertices[0].Y,
            _setupVertices[1].X,
            _setupVertices[1].Y,
            _setupVertices[2].X,
            _setupVertices[2].Y,
            color);
    }

    private void DrawTriangleWire(float ax, float ay, float bx, float by, float cx, float cy, ushort color)
    {
        if (color == 0)
            color = 0xffff;

        FillTriangle(ax, ay, bx, by, cx, cy, color);
        DrawLfbLine(ax, ay, bx, by, color);
        DrawLfbLine(bx, by, cx, cy, color);
        DrawLfbLine(cx, cy, ax, ay, color);
    }

    private void FillTriangle(float ax, float ay, float bx, float by, float cx, float cy, ushort color)
    {
        if (!float.IsFinite(ax) || !float.IsFinite(ay) ||
            !float.IsFinite(bx) || !float.IsFinite(by) ||
            !float.IsFinite(cx) || !float.IsFinite(cy))
        {
            return;
        }

        float area = Edge(ax, ay, bx, by, cx, cy);
        if (MathF.Abs(area) < 0.001f)
            return;

        GetClip(out int clipX0, out int clipX1, out int clipY0, out int clipY1);
        int minX = Math.Clamp((int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))), clipX0, clipX1);
        int maxX = Math.Clamp((int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))), clipX0, clipX1);
        int minY = Math.Clamp((int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))), clipY0, clipY1);
        int maxY = Math.Clamp((int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))), clipY0, clipY1);
        if (maxX <= minX || maxY <= minY)
            return;

        bool positive = area > 0;
        int bufferIndex = GetDrawBufferIndex();
        MaterializePendingClear(bufferIndex);
        InvalidateFastFillCache(bufferIndex);
        ushort[] buffer = _colorBuffers[bufferIndex];
        for (int y = minY; y < maxY; y++)
        {
            float py = y + 0.5f;
            int row = y * LfbRowPixels;
            for (int x = minX; x < maxX; x++)
            {
                float px = x + 0.5f;
                float e0 = Edge(bx, by, cx, cy, px, py);
                float e1 = Edge(cx, cy, ax, ay, px, py);
                float e2 = Edge(ax, ay, bx, by, px, py);
                if (positive ? e0 >= 0 && e1 >= 0 && e2 >= 0 : e0 <= 0 && e1 <= 0 && e2 <= 0)
                {
                    buffer[(row + x) & (LfbPixels - 1)] = color;
                    _lfbWriteCount++;
                }
            }
        }
    }

    private static float Edge(float ax, float ay, float bx, float by, float px, float py)
        => (px - ax) * (by - ay) - (py - ay) * (bx - ax);

    private void DrawLfbLine(float ax, float ay, float bx, float by, ushort color)
    {
        if (!float.IsFinite(ax) || !float.IsFinite(ay) || !float.IsFinite(bx) || !float.IsFinite(by))
            return;

        int x0 = (int)MathF.Round(ax);
        int y0 = (int)MathF.Round(ay);
        int x1 = (int)MathF.Round(bx);
        int y1 = (int)MathF.Round(by);
        int dx = Math.Abs(x1 - x0);
        int sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0);
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        int guard = 0;

        while (guard++ < 4096)
        {
            PlotLfbPixel(x0, y0, color);
            if (x0 == x1 && y0 == y1)
                break;

            int e2 = error * 2;
            if (e2 >= dy)
            {
                error += dy;
                x0 += sx;
            }
            if (e2 <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private void PlotLfbPixel(int x, int y, ushort color)
    {
        GetClip(out int x0, out int x1, out int y0, out int y1);
        if (x < x0 || x >= x1 || y < y0 || y >= y1 || x < 0 || x >= LfbRowPixels || y < 0 || y >= LfbRows)
            return;

        int bufferIndex = GetDrawBufferIndex();
        MaterializePendingClear(bufferIndex);
        InvalidateFastFillCache(bufferIndex);
        _colorBuffers[bufferIndex][(y * LfbRowPixels + x) & (LfbPixels - 1)] = color;
        _lfbWriteCount++;
    }

    private void GetClip(out int x0, out int x1, out int y0, out int y1)
    {
        uint clipX = _registers[RegClipLeftRight];
        uint clipY = _registers[RegClipLowYHighY];
        x0 = Math.Clamp((int)((clipX >> 16) & 0x7ff), 0, 1024);
        x1 = Math.Clamp((int)(clipX & 0x7ff), 0, 1024);
        y0 = Math.Clamp((int)((clipY >> 16) & 0x7ff), 0, LfbRows);
        y1 = Math.Clamp((int)(clipY & 0x7ff), 0, LfbRows);
        if (x1 <= x0)
        {
            x0 = 0;
            x1 = 640;
        }
        if (y1 <= y0)
        {
            y0 = 0;
            y1 = 480;
        }
    }

    private void TraceDraw(string message)
    {
        if (_traceDraw && _drawTraceCount++ < _drawTraceLimit)
            Console.WriteLine($"[GAUNTDL:VOODOO-DRAW] {message}");
    }

    private static int ParseDrawTraceLimit(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value >= 0 ? value : fallback;

    private int GetLfbPixelOffset(uint byteOffset, bool twoPixels, uint lfbMode)
    {
        uint wordOffset = (byteOffset & (LfbBytes - 1u)) >> 2;
        uint pixelOffset = twoPixels ? wordOffset << 1 : wordOffset;
        int x = (int)(pixelOffset & (LfbRowPixels - 1));
        int y = (int)((pixelOffset >> 10) & 0x3ff);
        if (((lfbMode >> 13) & 1u) != 0)
            y = GetLfbYOrigin() - y;
        y = Math.Clamp(y, 0, LfbRows - 1);
        return y * LfbRowPixels + x;
    }

    private int GetLfbYOrigin()
    {
        int origin = (int)((_registers[RegFbiInit3] >> 22) & 0x3ff);
        return origin > 0 ? Math.Min(origin, LfbRows - 1) : 479;
    }

    private ushort[] GetDrawBuffer()
        => _colorBuffers[GetDrawBufferIndex()];

    private int GetDrawBufferIndex()
    {
        int select = (int)((_registers[RegFbzMode] >> 14) & 0x03u);
        return MapDrawBufferSelect(select);
    }

    private ushort[] GetLfbReadBuffer(uint lfbMode)
    {
        int select = (int)((lfbMode >> 6) & 0x03u);
        int bufferIndex = MapLfbBufferSelect(select);
        MaterializePendingClear(bufferIndex);
        return _colorBuffers[bufferIndex];
    }

    private ushort[] GetLfbWriteBuffer(uint lfbMode)
    {
        int bufferIndex = GetLfbWriteBufferIndex(lfbMode);
        MaterializePendingClear(bufferIndex);
        return _colorBuffers[bufferIndex];
    }

    private int GetLfbWriteBufferIndex(uint lfbMode)
        => MapLfbWriteBufferSelect((int)((lfbMode >> 4) & 0x03u));

    private int MapDrawBufferSelect(int select)
        => select switch
        {
            0 => _frontBufferIndex,
            1 => _backBufferIndex,
            2 => GetAuxBufferIndex(),
            _ => _backBufferIndex
        };

    private int MapLfbBufferSelect(int select)
        => select switch
        {
            0 => _frontBufferIndex,
            1 => _backBufferIndex,
            2 => GetAuxBufferIndex(),
            _ => _frontBufferIndex
        };

    private int MapLfbWriteBufferSelect(int select)
        => select switch
        {
            0 => _frontBufferIndex,
            1 => _backBufferIndex,
            _ => _frontBufferIndex
        };

    private int GetAuxBufferIndex()
    {
        int count = GetColorBufferCount();
        if (count < 3)
            return _backBufferIndex;

        for (int i = 0; i < 3; i++)
        {
            if (i != _frontBufferIndex && i != _backBufferIndex)
                return i;
        }

        return 2;
    }

    private int GetColorBufferCount()
        => ((_registers[RegFbiInit2] >> 4) & 1u) != 0 ? 3 : 2;

    private string GetBufferCountDebugStatus()
        => _debugBufferCounts
            ? $"bnnz={GetBufferNonZeroCount(0)}/{GetBufferNonZeroCount(1)}/{GetBufferNonZeroCount(2)} "
            : "";

    private int GetBufferNonZeroCount(int index)
    {
        if ((uint)index >= (uint)_colorBuffers.Length)
            return 0;

        int count = 0;
        ushort[] buffer = _colorBuffers[index];
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != 0)
                count++;
        }

        return count;
    }

    private void SwapBuffers(uint command)
    {
        if ((command & 1u) != 0)
        {
            _pendingSwapCount = Math.Min(7, _pendingSwapCount + 1);
            return;
        }

        ExecuteSwapBuffers(command);
    }

    private void ExecuteSwapBuffers(uint command)
    {
        _swapBufferCount++;

        int count = GetColorBufferCount();
        if (count >= 3)
        {
            _frontBufferIndex = (_frontBufferIndex + 1) % 3;
            _backBufferIndex = (_frontBufferIndex + 1) % 3;
        }
        else
        {
            _frontBufferIndex = _backBufferIndex == 0 ? 0 : 1;
            _backBufferIndex = 1 - _frontBufferIndex;
        }

        if (((command >> 6) & 1u) != 0)
        {
            SetPendingClear(_backBufferIndex, 0, LfbRowPixels, 0, LfbRows, 0);
            CacheFastFill(_backBufferIndex, 0, LfbRowPixels, 0, LfbRows, 0);
        }
    }

    private void SetPendingClear(int bufferIndex, int x0, int x1, int y0, int y1, ushort color)
    {
        if ((uint)bufferIndex >= (uint)_pendingClearValid.Length)
            return;

        if (_pendingClearValid[bufferIndex] &&
            (_pendingClearX0[bufferIndex] != x0 ||
             _pendingClearX1[bufferIndex] != x1 ||
             _pendingClearY0[bufferIndex] != y0 ||
             _pendingClearY1[bufferIndex] != y1))
        {
            MaterializePendingClear(bufferIndex);
        }

        _pendingClearValid[bufferIndex] = true;
        _pendingClearX0[bufferIndex] = x0;
        _pendingClearX1[bufferIndex] = x1;
        _pendingClearY0[bufferIndex] = y0;
        _pendingClearY1[bufferIndex] = y1;
        _pendingClearColor[bufferIndex] = color;
    }

    private void MaterializePendingClear(int bufferIndex)
    {
        if ((uint)bufferIndex >= (uint)_pendingClearValid.Length || !_pendingClearValid[bufferIndex])
            return;

        int x0 = _pendingClearX0[bufferIndex];
        int x1 = _pendingClearX1[bufferIndex];
        int y0 = _pendingClearY0[bufferIndex];
        int y1 = _pendingClearY1[bufferIndex];
        ushort color = _pendingClearColor[bufferIndex];
        ushort[] buffer = _colorBuffers[bufferIndex];
        int width = x1 - x0;
        for (int y = y0; y < y1; y++)
        {
            int offset = y * LfbRowPixels + x0;
            buffer.AsSpan(offset, width).Fill(color);
        }

        _pendingClearValid[bufferIndex] = false;
    }

    private bool IsCachedFastFill(int bufferIndex, int x0, int x1, int y0, int y1, ushort color)
        => (uint)bufferIndex < (uint)_lastFastFillValid.Length &&
           _lastFastFillValid[bufferIndex] &&
           _lastFastFillX0[bufferIndex] == x0 &&
           _lastFastFillX1[bufferIndex] == x1 &&
           _lastFastFillY0[bufferIndex] == y0 &&
           _lastFastFillY1[bufferIndex] == y1 &&
           _lastFastFillColor[bufferIndex] == color;

    private void CacheFastFill(int bufferIndex, int x0, int x1, int y0, int y1, ushort color)
    {
        if ((uint)bufferIndex >= (uint)_lastFastFillValid.Length)
            return;

        _lastFastFillValid[bufferIndex] = true;
        _lastFastFillX0[bufferIndex] = x0;
        _lastFastFillX1[bufferIndex] = x1;
        _lastFastFillY0[bufferIndex] = y0;
        _lastFastFillY1[bufferIndex] = y1;
        _lastFastFillColor[bufferIndex] = color;
    }

    private void InvalidateFastFillCache(int bufferIndex)
    {
        if ((uint)bufferIndex < (uint)_lastFastFillValid.Length)
            _lastFastFillValid[bufferIndex] = false;
    }

    private static bool IsTwoPixelLfbFormat(int format)
        => format is 0 or 1 or 2 or 15;

    private static bool TryExpandLfbPixel(uint value, int format, int rgbaLanes, bool highHalf, out ushort rgb565)
    {
        int shift = highHalf ? 16 : 0;
        rgb565 = 0;
        switch (format)
        {
            case 0:
                rgb565 = ConvertRgb565Lane((ushort)(value >> shift), rgbaLanes);
                return true;
            case 1:
                rgb565 = ConvertXrgb1555Lane((ushort)(value >> shift), rgbaLanes, hasAlphaBit: false);
                return true;
            case 2:
                rgb565 = ConvertXrgb1555Lane((ushort)(value >> shift), rgbaLanes, hasAlphaBit: true);
                return true;
            case 4:
            case 5:
                if (highHalf)
                    return false;
                rgb565 = Convert8888Lane(value, rgbaLanes, hasAlpha: format == 5);
                return true;
            case 12:
                if (highHalf)
                    return false;
                rgb565 = ConvertRgb565Lane((ushort)value, rgbaLanes);
                return true;
            case 13:
                if (highHalf)
                    return false;
                rgb565 = ConvertXrgb1555Lane((ushort)value, rgbaLanes, hasAlphaBit: false);
                return true;
            case 14:
                if (highHalf)
                    return false;
                rgb565 = ConvertXrgb1555Lane((ushort)value, rgbaLanes, hasAlphaBit: true);
                return true;
            default:
                return false;
        }
    }

    private static ushort ConvertRgb565Lane(ushort packed, int rgbaLanes)
    {
        int r = (rgbaLanes is 1 or 3) ? packed & 0x1f : (packed >> 11) & 0x1f;
        int g = (packed >> 5) & 0x3f;
        int b = (rgbaLanes is 1 or 3) ? (packed >> 11) & 0x1f : packed & 0x1f;
        return (ushort)((r << 11) | (g << 5) | b);
    }

    private static ushort ConvertXrgb1555Lane(ushort packed, int rgbaLanes, bool hasAlphaBit)
    {
        int rShift;
        int gShift;
        int bShift;
        if (rgbaLanes == 2)
        {
            rShift = 11;
            gShift = 6;
            bShift = 1;
        }
        else if (rgbaLanes == 3)
        {
            rShift = 1;
            gShift = 6;
            bShift = 11;
        }
        else if (rgbaLanes == 1)
        {
            rShift = 0;
            gShift = 5;
            bShift = 10;
        }
        else
        {
            rShift = 10;
            gShift = 5;
            bShift = 0;
        }

        if (hasAlphaBit)
        {
            // The alpha bit does not affect the bringup color buffer, but keeping
            // the branch here mirrors the Voodoo format split and documents it.
        }

        int r = (packed >> rShift) & 0x1f;
        int g = (packed >> gShift) & 0x1f;
        int b = (packed >> bShift) & 0x1f;
        return (ushort)((r << 11) | ((g << 1 | g >> 4) << 5) | b);
    }

    private static ushort Convert8888Lane(uint packed, int rgbaLanes, bool hasAlpha)
    {
        int r;
        int g;
        int b;
        if (rgbaLanes == 2)
        {
            r = (int)((packed >> 24) & 0xff);
            g = (int)((packed >> 16) & 0xff);
            b = (int)((packed >> 8) & 0xff);
        }
        else if (rgbaLanes == 3)
        {
            r = (int)((packed >> 8) & 0xff);
            g = (int)((packed >> 16) & 0xff);
            b = (int)((packed >> 24) & 0xff);
        }
        else if (rgbaLanes == 1)
        {
            r = (int)(packed & 0xff);
            g = (int)((packed >> 8) & 0xff);
            b = (int)((packed >> 16) & 0xff);
        }
        else
        {
            r = (int)((packed >> 16) & 0xff);
            g = (int)((packed >> 8) & 0xff);
            b = (int)(packed & 0xff);
        }

        if (hasAlpha)
        {
            // Alpha is consumed by the real pixel pipeline; the bringup backend
            // stores only the visible RGB surface for now.
        }

        return (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
    }

    private ushort GetDrawColor()
    {
        ushort color = GetFloatDrawColorOrZero();
        if (color != 0)
            return color;

        color = GetIntegerDrawColorOrZero();
        if (color != 0)
            return color;

        color = ArgbToRgb565(_registers[RegColor1]);
        if (color == 0)
            color = ArgbToRgb565(_registers[RegColor0]);
        if (color == 0)
            color = (ushort)_registers[RegZaColor];
        return color == 0 ? (ushort)0xffff : color;
    }

    private ushort GetIntegerDrawColor()
    {
        ushort color = GetIntegerDrawColorOrZero();
        return color == 0 ? GetDrawColor() : color;
    }

    private ushort GetFloatDrawColor()
    {
        ushort color = GetFloatDrawColorOrZero();
        return color == 0 ? GetDrawColor() : color;
    }

    private ushort GetIntegerDrawColorOrZero()
        => FixedColorToRgb565(_registers[0x08], _registers[0x09], _registers[0x0a]);

    private ushort GetFloatDrawColorOrZero()
        => FloatColorToRgb565(
            FloatFromRegister(_registers[0x28]),
            FloatFromRegister(_registers[0x29]),
            FloatFromRegister(_registers[0x2a]));

    private bool IsSetupFanMode()
        => ((_registers[0x98] >> 16) & 1u) != 0;

    private static float FixedVertexCoordinate(uint value)
        => unchecked((short)value) / 16.0f;

    private bool TryReadWord(int wordsNeeded, ref int source, out uint value)
    {
        if (source >= wordsNeeded || source >= _fifoBuffer.Count)
        {
            value = 0;
            return false;
        }

        value = _fifoBuffer[source++];
        return true;
    }

    private bool TryReadFloat(int wordsNeeded, ref int source, out float value)
    {
        if (!TryReadWord(wordsNeeded, ref source, out uint word))
        {
            value = 0;
            return false;
        }

        value = FloatFromRegister(word);
        return true;
    }

    private bool SkipWord(int wordsNeeded, ref int source)
    {
        if (source >= wordsNeeded || source >= _fifoBuffer.Count)
            return false;
        source++;
        return true;
    }

    private static int GetFifoPacketWordsNeeded(uint command)
    {
        return (command & 7u) switch
        {
            0 => ((command >> 3) & 7u) == 4u ? 2 : 1,
            1 => 1 + (int)(command >> 16),
            2 => 1 + PopCount(command >> 3),
            3 => GetFifoType3WordsNeeded(command),
            4 => 1 + PopCount((command >> 15) & 0x3fffu) + (int)(command >> 29),
            5 => 2 + (int)((command >> 3) & 0x7ffffu),
            _ => 1
        };
    }

    private static int GetFifoType3WordsNeeded(uint command)
    {
        int wordsPerVertex = 2;
        if (((command >> 28) & 1u) != 0)
        {
            if (((command >> 10) & 3u) != 0)
                wordsPerVertex++;
        }
        else
        {
            if (((command >> 10) & 1u) != 0)
                wordsPerVertex += 3;
            if (((command >> 11) & 1u) != 0)
                wordsPerVertex++;
        }

        if (((command >> 12) & 1u) != 0)
            wordsPerVertex++;
        if (((command >> 13) & 1u) != 0)
            wordsPerVertex++;
        if (((command >> 14) & 1u) != 0)
            wordsPerVertex++;
        if (((command >> 15) & 1u) != 0)
            wordsPerVertex += 2;
        if (((command >> 16) & 1u) != 0)
            wordsPerVertex++;
        if (((command >> 17) & 1u) != 0)
            wordsPerVertex += 2;

        int vertices = (int)((command >> 6) & 0xfu);
        int dummyWords = (int)(command >> 29);
        return 1 + wordsPerVertex * vertices + dummyWords;
    }

    private static int PopCount(uint value)
    {
        int count = 0;
        while (value != 0)
        {
            count += (int)(value & 1u);
            value >>= 1;
        }
        return count;
    }

    private static uint Rgb565ToBgra(ushort value)
    {
        uint r = (uint)((value >> 11) & 0x1f);
        uint g = (uint)((value >> 5) & 0x3f);
        uint b = (uint)(value & 0x1f);
        r = (r << 3) | (r >> 2);
        g = (g << 2) | (g >> 4);
        b = (b << 3) | (b >> 2);
        return 0xff000000u | (r << 16) | (g << 8) | b;
    }

    private static ushort ArgbToRgb565(uint value)
    {
        uint r = (value >> 16) & 0xff;
        uint g = (value >> 8) & 0xff;
        uint b = value & 0xff;
        return (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));
    }

    private static ushort PackedColorToRgb565(uint value)
        => ArgbToRgb565(value);

    private static ushort FixedColorToRgb565(uint r, uint g, uint b)
    {
        int rb = FixedColorByte(r);
        int gb = FixedColorByte(g);
        int bb = FixedColorByte(b);
        return (ushort)(((rb >> 3) << 11) | ((gb >> 2) << 5) | (bb >> 3));
    }

    private static int FixedColorByte(uint value)
        => Math.Clamp(SignExtend24(value) >> 12, 0, 255);

    private static int SignExtend24(uint value)
    {
        int raw = (int)(value & 0x00ff_ffffu);
        return (raw & 0x0080_0000) != 0 ? raw | unchecked((int)0xff00_0000u) : raw;
    }

    private static ushort FloatColorToRgb565(float r, float g, float b)
    {
        if (!float.IsFinite(r) || !float.IsFinite(g) || !float.IsFinite(b))
            return 0;

        byte rb = FloatColorByte(r);
        byte gb = FloatColorByte(g);
        byte bb = FloatColorByte(b);
        return (ushort)(((rb >> 3) << 11) | ((gb >> 2) << 5) | (bb >> 3));
    }

    private static byte FloatColorByte(float value)
    {
        if (value <= 1.0f)
            value *= 255.0f;
        return (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
    }

    private static float FloatFromRegister(uint value)
        => BitConverter.Int32BitsToSingle(unchecked((int)value));

    private readonly record struct SetupVertex(float X, float Y, ushort Color);

    private static void Clear(EutherFrameTarget target, uint bgra)
        => FillRect(target, 0, 0, target.Width, target.Height, bgra);

    private static void DrawRect(EutherFrameTarget target, int x, int y, int width, int height, uint bgra)
    {
        FillRect(target, x, y, width, 2, bgra);
        FillRect(target, x, y + height - 2, width, 2, bgra);
        FillRect(target, x, y, 2, height, bgra);
        FillRect(target, x + width - 2, y, 2, height, bgra);
    }

    private static void FillRect(EutherFrameTarget target, int x, int y, int width, int height, uint bgra)
    {
        int x0 = Math.Clamp(x, 0, target.Width);
        int y0 = Math.Clamp(y, 0, target.Height);
        int x1 = Math.Clamp(x + width, 0, target.Width);
        int y1 = Math.Clamp(y + height, 0, target.Height);

        for (int py = y0; py < y1; py++)
        {
            int offset = py * target.Stride + x0 * 4;
            for (int px = x0; px < x1; px++)
            {
                target.Buffer[offset + 0] = (byte)(bgra & 0xff);
                target.Buffer[offset + 1] = (byte)((bgra >> 8) & 0xff);
                target.Buffer[offset + 2] = (byte)((bgra >> 16) & 0xff);
                target.Buffer[offset + 3] = (byte)((bgra >> 24) & 0xff);
                offset += 4;
            }
        }
    }
}

internal sealed class VoodooTraceBackend : VoodooBringupBackend
{
    private readonly bool _traceRegisters = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO") == "1";
    private readonly bool _traceFifo = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FIFO") == "1";
    private readonly bool _traceInterestingFifo = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FIFO_INTERESTING") == "1";
    private readonly bool _traceLfb = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_LFB") == "1";
    private readonly bool _traceTexture = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEX") == "1";
    private readonly int _registerTraceLimit = ParseTraceLimit("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_REG_LIMIT", 256);
    private readonly int _fifoTraceLimit = ParseTraceLimit("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FIFO_LIMIT", 64);
    private readonly int _lfbTraceLimit = ParseTraceLimit("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_LFB_LIMIT", 64);
    private readonly int _textureTraceLimit = ParseTraceLimit("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEX_LIMIT", 64);
    private int _registerTraceCount;
    private int _fifoTraceCount;
    private int _lfbTraceCount;
    private int _textureTraceCount;

    public static bool IsEnabled()
        => Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO") == "1" ||
           Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FIFO") == "1" ||
           Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FIFO_INTERESTING") == "1" ||
           Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_LFB") == "1" ||
           Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEX") == "1";

    public override void WriteRegister(uint address, uint value)
    {
        base.WriteRegister(address, value);
        if (_traceRegisters && _registerTraceCount++ < _registerTraceLimit)
            Console.WriteLine($"[GAUNTDL:VOODOO] reg[{address:x8}:{DescribeRegister(address)}]={value:x8}");
    }

    public override void WriteFifo(uint wordOffset, uint value)
    {
        base.WriteFifo(wordOffset, value);
        if (_traceRegisters)
            Console.WriteLine($"[GAUNTDL:VOODOO] fifo[{wordOffset:x4}]={value:x8}");
        else if (_traceFifo && _fifoTraceCount++ < _fifoTraceLimit)
        {
            Console.WriteLine($"[GAUNTDL:VOODOO] fifo[{wordOffset:x4}]={value:x8}");
        }
        else if (_traceInterestingFifo && _fifoTraceCount < _fifoTraceLimit)
        {
            if (IsInterestingFifoWord(value, out string description))
                Console.WriteLine($"[GAUNTDL:VOODOO] fifoInteresting[{_fifoTraceCount++:x6}]@{wordOffset:x4}={value:x8} {description}");
        }
    }

    public override void WriteLfb32(uint offset, uint value)
    {
        base.WriteLfb32(offset, value);
        if ((_traceRegisters || _traceLfb) && _lfbTraceCount++ < _lfbTraceLimit)
            Console.WriteLine($"[GAUNTDL:VOODOO] lfb[{offset:x6}]={value:x8}");
    }

    public override uint ReadLfb32(uint offset)
    {
        uint value = base.ReadLfb32(offset);
        if ((_traceRegisters || _traceLfb) && _lfbTraceCount++ < _lfbTraceLimit)
            Console.WriteLine($"[GAUNTDL:VOODOO] lfbr[{offset:x6}]={value:x8}");
        return value;
    }

    public override void WriteTexture32(uint offset, uint value)
    {
        base.WriteTexture32(offset, value);
        if ((_traceRegisters || _traceTexture) && _textureTraceCount++ < _textureTraceLimit)
            Console.WriteLine($"[GAUNTDL:VOODOO] tex[{offset:x6}]={value:x8}");
    }

    public override uint ReadTexture32(uint offset)
    {
        uint value = base.ReadTexture32(offset);
        if ((_traceRegisters || _traceTexture) && _textureTraceCount++ < _textureTraceLimit)
            Console.WriteLine($"[GAUNTDL:VOODOO] texr[{offset:x6}]={value:x8}");
        return value;
    }

    private static int ParseTraceLimit(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value >= 0 ? value : fallback;

    private static string DescribeRegister(uint address)
    {
        uint register = (address >> 2) & 0x3ffu;
        return register switch
        {
            0x20 => "triangleCMD",
            0x40 => "ftriangleCMD",
            0x41 => "fbzColorPath",
            0x42 => "fogMode",
            0x43 => "alphaMode",
            0x44 => "fbzMode",
            0x45 => "lfbMode",
            0x46 => "clipLeftRight",
            0x47 => "clipLowYHighY",
            0x49 => "fastfillCMD",
            0x4a => "swapbufferCMD",
            0x4c => "zaColor",
            0x51 => "color0",
            0x52 => "color1",
            0x80 => "fbiInit4",
            0x81 => "vRetrace",
            0x83 => "videoDimensions",
            0x84 => "fbiInit0",
            0x85 => "fbiInit1",
            0x86 => "fbiInit2",
            0x87 => "fbiInit3",
            _ => $"0x{register:x3}"
        };
    }

    private static bool IsInterestingFifoWord(uint word, out string description)
    {
        uint type = word & 7u;
        description = "";
        switch (type)
        {
            case 1:
            {
                int count = (int)(word >> 16);
                uint target = (word >> 3) & 0xfffu;
                description = $"type1 count={count} target=0x{target:x3}";
                return count is > 0 and <= 64 && TouchesInterestingRegister(target, (uint)count);
            }
            case 3:
                description = $"type3 count={(word >> 6) & 0xfu} code={(word >> 3) & 7u}";
                return true;
            case 4:
            {
                uint target = (word >> 3) & 0xfffu;
                uint mask = (word >> 15) & 0x3fffu;
                description = $"type4 target=0x{target:x3} mask=0x{mask:x4}";
                for (uint bit = 0; bit < 14; bit++)
                {
                    if (((mask >> (int)bit) & 1u) != 0 && IsInterestingRegister(target + bit))
                        return true;
                }
                return false;
            }
            case 5:
                description = $"type5 count={(word >> 3) & 0x7ffffu} space={word >> 30}";
                return true;
            default:
                return false;
        }
    }

    private static bool TouchesInterestingRegister(uint target, uint count)
    {
        for (uint i = 0; i < count; i++)
        {
            if (IsInterestingRegister(target + i))
                return true;
        }

        return false;
    }

    private static bool IsInterestingRegister(uint register)
        => register is 0x20u or 0x40u or 0x98u or 0xa8u or 0xa9u;
}
