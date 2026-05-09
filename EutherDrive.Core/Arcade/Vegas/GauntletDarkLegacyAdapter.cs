using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
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
    private RomIdentity? _romIdentity;
    private long _frameCounter;
    private bool _loaded;

    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => _loaded ? _frameCounter : null;

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

        _machine.RunFrame(new EutherFrameTarget(_frameBuffer, FrameWidth, FrameHeight, FrameStride));
        _frameCounter++;
        if (!_machine.Voodoo.HasVideoActivity)
            DrawDiagnosticFrame();
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
        sampleRate = AudioSampleRate;
        channels = AudioChannels;
        return ReadOnlySpan<short>.Empty;
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
    }

    public void Load(GauntletRomSet romSet)
    {
        RomLoaded = romSet.MainRom.Length == 0x80000;
        Disk.Attach(romSet.ChdPath);
        Sio.LoadBootRom(romSet.VegasSioRom);
        MemoryMap.LoadMainBootRom(romSet.MainRom);
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
        Cpu.RunProbeFrame();
        Sio.PulseVblank(state: false);
        Voodoo.RenderFrame(target);
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
    private readonly int _traceInstructionLimit = ParsePositiveInt("EUTHERDRIVE_GAUNTDL_TRACE_CPU_LIMIT", int.MaxValue);
    private readonly int _stepBudget = ParsePositiveInt("EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME", 2048);
    private readonly ulong _cp0CountStep = (ulong)ParsePositiveInt("EUTHERDRIVE_GAUNTDL_CP0_COUNT_STEP", 1024);
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
        _halted = false;
        _hasPendingBranch = false;
        _pendingBranchTarget = 0;
        _hasImmediatePcOverride = false;
        _immediatePcOverride = 0;
        _instructionCounter = 0;
        _traceInstructionCount = 0;
        _timerInterruptPending = false;
        _hi = 0;
        _lo = 0;
    }

    public void RunProbeFrame()
    {
        if (_halted)
            return;

        for (int i = 0; i < _stepBudget && !_halted; i++)
            Step();
    }

    private void Step()
    {
        ulong pc = Pc;
        UpdateInterruptPendingBits();
        if (TryEnterPendingInterrupt(pc))
            return;
        NormalizeKnownGlideFifoState(pc);
        if (TryFastPathKnownBootLoop(pc))
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
        if (TryFastPathKnownRamQwordFill(pc))
            return;
        if (TryFastPathKnownRamNileTimerDelay(pc))
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
        if (TryFastPathKnownGlideFifoMakeRoom(pc))
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

    private bool TryFastPathKnownCacheLoop(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
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

        _gpr[2] = 0;
        _gpr[11] = 0;
        Pc = returnAddress;
        CompleteFastPathStep();
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

    private bool TryFastPathKnownRamNileTimerDelay(ulong pc)
    {
        if (TryFastPathKnownRamCountDelay(pc))
            return true;

        if (pc != 0x000000008000468cUL)
            return false;
        if ((_gpr[17] & 0xffffffffUL) != 0xbfa001e0UL)
            return false;

        _gpr[16] = 0;
        _gpr[2] = 1;
        Pc = 0x000000008000469cUL;
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
        if ((pc & 0x1fffffffUL) != 0x00064c28UL)
            return false;
        if (_memory.Read32(pc) != 0x0080302dU ||
            _memory.Read32(pc + 4) != 0x3c02800bU ||
            _memory.Read32(pc + 8) != 0x24424d20U)
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
        if ((pc & 0x1fffffffUL) != 0x00064cd0UL)
            return false;
        if (_memory.Read32(pc) != 0x27bdffe0U ||
            _memory.Read32(pc + 4) != 0x3c02800bU ||
            _memory.Read32(pc + 8) != 0xafb10014U ||
            _memory.Read32(pc + 12) != 0x24514d20U)
            return false;
        if ((_gpr[4] & 0xffffffffUL) != 0)
            return false;

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

    private void NormalizeKnownGlideFifoState(ulong pc)
    {
        ulong offset = pc & 0x1fffffffUL;
        if (offset is < 0x00065410UL or > 0x00065504UL)
            return;
        if (_gpr[16] != 0xffffffff800b4e04UL && _gpr[6] != 0xffffffff800b4e04UL)
            return;

        _memory.Write32(0xffffffff800b5164UL, 0xa8200000u);
        _memory.Write32(0xffffffff800b5178UL, 0xa8200000u);
        _memory.Write32(0xffffffff800b517cUL, 0xa8200000u);
    }

    private bool TryFastPathKnownGlideFifoMakeRoom(ulong pc)
    {
        if ((pc & 0x1fffffffUL) != 0x000653d8UL)
            return false;
        if (_memory.Read32(pc) != 0x3c02800bU ||
            _memory.Read32(pc + 4) != 0x8c464d2cU ||
            _memory.Read32(pc + 8) != 0x0080c82dU ||
            _memory.Read32(pc + 12) != 0x8cc20384U)
            return false;
        if (_gpr[4] > 0x10000UL)
            return false;

        const ulong state = 0xffffffff800b4e04UL;
        _memory.Write32(state + 0x370, 0x18);
        _memory.Write32(state + 0x374, 0xa8200000u);
        _memory.Write32(state + 0x378, 0xa8200000u);
        _memory.Write32(state + 0x37c, 0x00010000u);
        _memory.Write32(state + 0x380, 0x00010000u);
        _memory.Write32(state + 0x384, 0x00010000u);

        _gpr[2] = 0x00010000UL;
        Pc = _gpr[31];
        CompleteFastPathStep();
        return true;
    }

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
                _gpr[31] = pc + 8;
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
                _gpr[rd == 0 ? 31 : rd] = pc + 8;
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
            _gpr[31] = pc + 8;

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
                    OverrideNextPc(_cp0[30]);
                }
                else
                {
                    _cp0[12] &= ~Cp0StatusExl;
                    OverrideNextPc(_cp0[14]);
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

        _cp0[14] = pc;
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
        _pendingBranchTarget = target;
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
        _immediatePcOverride = target;
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
        if (!ShouldTrace(pc))
            return;

        _traceInstructionCount++;
        Console.WriteLine(
            $"[GAUNTDL:CPU] #{_instructionCounter} pc={pc:x16} op={op:x8} {DisassembleBrief(op)} " +
            $"a0={_gpr[4]:x16} a1={_gpr[5]:x16} v0={_gpr[2]:x16} v1={_gpr[3]:x16} " +
            $"t0={_gpr[8]:x16} t1={_gpr[9]:x16} s0={_gpr[16]:x16} s1={_gpr[17]:x16} s2={_gpr[18]:x16} ra={_gpr[31]:x16} " +
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
        return true;
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
    private const ushort NilePciInterruptC = 1 << 10;
    private const ulong FpgaConfigBase = 0x00000000a1600000UL;
    private const int MainRamSize = 32 * 1024 * 1024;
    private const uint UnmappedReadValue = 0xffffffffu;
    private const int PciTypeIo = 0x2;
    private const int PciTypeMemory = 0x6;
    private const int PciTypeConfig = 0x0a;

    private readonly List<VegasMemoryRange> _ranges = new();
    private readonly byte[] _mainRam = new byte[MainRamSize];
    private readonly byte[] _nileRegisters = new byte[NileRegisterSize];
    private readonly byte[] _fpgaConfigRegisters = new byte[4];
    private readonly byte[] _cpuIoRegisters = new byte[4];
    private readonly ushort[] _ioasicRegisters = new ushort[16];
    private readonly VegasIdePciDevice _idePci = new();
    private readonly VegasVoodooPciDevice _voodooPci = new();
    private readonly bool _traceEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_MEM") == "1";
    private readonly string? _traceTargetFilter = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_MEM_TARGET");
    private byte[] _mainBootRom = Array.Empty<byte>();
    private VegasSioDevice? _sio;
    private IdeDiskDevice? _disk;
    private DcsAudioDevice? _audio;
    private VoodooFacade? _voodoo;
    private ushort _nileIrqState;
    private byte _nileIrqPins;
    private bool _fpgaConfigSeenLow;
    private bool _fpgaConfigStatusHigh;
    private bool _fpgaConfigDone;

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

    public void Reset()
    {
        Array.Clear(_nileRegisters);
        Array.Clear(_fpgaConfigRegisters);
        Array.Clear(_cpuIoRegisters);
        Array.Clear(_ioasicRegisters);
        _ioasicRegisters[8] = 0x0001;
        _fpgaConfigSeenLow = false;
        _fpgaConfigStatusHigh = false;
        _fpgaConfigDone = false;
        _nileIrqState = 0;
        _nileIrqPins = 0;
        _idePci.Reset();
        _voodooPci.Reset();
        UpdateIoasicIrq();
    }

    public ulong GetCpuInterruptPendingMask()
    {
        UpdateNileInterrupts();
        return (ulong)_nileIrqPins << 10;
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
                _idePci.WriteIo8(pciAddress, value);
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
                _idePci.WriteIo16(pciAddress, value);
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

    private uint ReadNileRegister32(uint offset)
        => BinaryPrimitives.ReadUInt32LittleEndian(_nileRegisters.AsSpan((int)offset, 4));

    private void UpdateNileInterrupts()
    {
        if (_sio?.InterruptLine == true)
            _nileIrqState |= NilePciInterruptC;
        else
            _nileIrqState &= unchecked((ushort)~NilePciInterruptC);

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
            uint value = ReadChipSelectByte(chipSelect, offset);
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
            WriteChipSelectByte(chipSelect, offset, (byte)(value & 0xff));
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
            value = ReadChipSelectByte(chipSelect, offset);
            Trace("read32", address, value, range.Name);
        }
        else
        {
            value = UnmappedReadValue;
            Trace("read32", address, value, $"CS{chipSelect} unmapped");
        }

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
            WriteChipSelectByte(chipSelect, offset, (byte)value);
            Trace("write32", address, value, range.Name);
        }
        else
        {
            Trace("write32", address, value, $"CS{chipSelect} unmapped");
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

    private byte ReadChipSelectByte(int chipSelect, uint offset)
    {
        if (chipSelect == 2)
        {
            if ((offset >> 12) == 0)
                _cpuIoRegisters[3] |= 0x01;
            return _sio?.Read(offset) ?? 0xff;
        }

        if (chipSelect == 5)
            return ReadCpuIo(offset);

        if (chipSelect == 6 && offset < 0x40)
            return ReadIoasicPackedByte(offset);

        return 0xff;
    }

    private void WriteChipSelectByte(int chipSelect, uint offset, byte value)
    {
        if (chipSelect == 2)
        {
            _sio?.Write(offset, value);
            return;
        }

        if (chipSelect == 5)
            WriteCpuIo(offset, value);
        else if (chipSelect == 6 && offset < 0x40)
            WriteIoasicPackedByte(offset, value);
    }

    private byte ReadIoasicPackedByte(uint offset)
    {
        ushort value = ReadIoasicRegister(GetIoasicPackedRegister(offset));
        return (byte)(value >> (int)((offset & 1) * 8));
    }

    private void WriteIoasicPackedByte(uint offset, byte value)
    {
        int register = GetIoasicPackedRegister(offset);
        int shift = (int)((offset & 1) * 8);
        ushort current = _ioasicRegisters[register];
        _ioasicRegisters[register] = (ushort)((current & ~(0xff << shift)) | (value << shift));
        UpdateIoasicIrq();
    }

    private ushort ReadIoasicRegister(int register)
    {
        if (register == 14)
            UpdateIoasicIrq();

        return register switch
        {
            0 => 0x2001,
            10 => 0x0048,
            11 => 0x000a,
            13 => 0x0100,
            _ => _ioasicRegisters[register]
        };
    }

    private void UpdateIoasicIrq()
    {
        ushort intCtl = _ioasicRegisters[15];
        ushort irqBits = (ushort)(_ioasicRegisters[6] & 0x3f00);
        const ushort fifoState = 0x0008;
        if ((fifoState & 0x08) != 0)
            irqBits |= 0x0002;
        if ((fifoState & 0x10) != 0)
            irqBits |= 0x0004;
        if ((fifoState & 0x20) != 0)
            irqBits |= 0x0008;

        _ioasicRegisters[14] = irqBits;
        bool asserted = (intCtl & 0x0001) != 0 && (irqBits & intCtl & 0x3ffe) != 0;
        _sio?.SetIoasicIrq(asserted);
    }

    private static int GetIoasicPackedRegister(uint offset)
        => (int)(((offset >> 2) * 2 + ((offset >> 1) & 1)) & 0x0f);

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
        if (!TraceTargetMatches(target))
            return;

        Console.WriteLine($"[GAUNTDL:MEM] {op} {address:x16} {value:x8} {target}");
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
}

internal readonly record struct VegasMemoryRange(string Name, int ChipSelect, ulong Start, ulong End);

internal sealed class VegasVoodooPciDevice
{
    private const uint VendorDeviceId = 0x0002121au;
    private const uint ClassCode = 0x03800002u;
    private const uint MemoryBarSize = 16 * 1024 * 1024;
    private const uint MemoryBarMask = 0xff000000u;
    private const uint MemoryBarFlags = 0x00000008u;
    private const uint VoodooStatusReady = 0x0fffff7fu;

    private readonly byte[] _config = new byte[0x100];
    private readonly uint[] _pciControl = new uint[8];
    private readonly uint[] _registers = new uint[0x400];
    private readonly bool _traceEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO") == "1";
    private IVoodooBackend? _voodoo;
    private uint _bar0 = 0xff000000u;
    private bool _bar0Probe;
    private uint _swapStatusCounter;

    public void AttachVoodoo(IVoodooBackend voodoo) => _voodoo = voodoo;

    public void Reset()
    {
        Array.Clear(_config);
        Array.Clear(_pciControl);
        Array.Clear(_registers);
        _bar0 = 0xff000000u;
        _bar0Probe = false;
        _swapStatusCounter = 0;
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

        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_config.AsSpan((int)offset, 4));
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
            < 0x00400000u => ReadRegister(offset),
            < 0x00800000u => _voodoo?.ReadLfb32(offset - 0x00400000u) ?? 0,
            _ => 0
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
            if (offset >= 0x00200000u)
            {
                Span<uint> word = stackalloc uint[1];
                word[0] = value;
                _voodoo?.WriteFifo(word);
                Trace($"fifo write off={offset:x6} value={value:x8}");
            }
            else
            {
                WriteRegister(offset, value);
                _voodoo?.WriteRegister(offset, value);
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
            return VoodooStatusReady;
        if ((offset & 0x3ffu) == 0x1e8u)
            return ++_swapStatusCounter;

        return _registers[(offset >> 2) & 0x3ffu];
    }

    private void WriteRegister(uint offset, uint value)
    {
        _registers[(offset >> 2) & 0x3ffu] = value;
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
        if (_traceEnabled)
            Console.WriteLine($"[GAUNTDL:VOODOO-PCI] {message}");
    }
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
    private IdeDiskDevice? _disk;

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
        BinaryPrimitives.WriteUInt32LittleEndian(_config.AsSpan(0x40, 4), 0x00000c40);
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
            case >= 0x10 and <= 0x20 when ((offset - 0x10) / 4) < _bars.Length:
                int bar = (int)((offset - 0x10) / 4);
                _bars[bar] = value;
                WriteConfigBarBytes(bar);
                break;
            case 0x3c:
                _config[0x3c] = (byte)value;
                break;
            case >= 0x40 and < 0x60:
                BinaryPrimitives.WriteUInt32LittleEndian(_config.AsSpan((int)offset, 4), value);
                break;
            case >= 0x70 and < 0x80:
                WriteBusMasterConfigWindow(offset - 0x70, value);
                break;
        }
    }

    public byte ReadIo8(uint address)
    {
        if (TryGetIdeRegister(address, out byte register))
            return register == 0 ? (byte)ReadIo16(address) : _disk?.ReadRegister8(register) ?? 0xff;
        if (TryGetControlRegister(address))
            return _disk?.ReadRegister8(7) ?? 0xff;
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

    public void WriteIo8(uint address, byte value)
    {
        if (TryGetIdeRegister(address, out byte register))
        {
            if (register != 0)
                _disk?.WriteRegister8(register, value);
            return;
        }

        if (TryGetControlRegister(address))
            return;

        if (TryGetBusMasterOffset(address, out uint bmOffset))
            _busMaster[bmOffset] = value;
    }

    public void WriteIo16(uint address, ushort value)
    {
        if (TryGetIdeRegister(address, out byte register) && register == 0)
        {
            _disk?.WriteData16(value);
            return;
        }

        WriteIo8(address, (byte)value);
        WriteIo8(address + 1, (byte)(value >> 8));
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

        WriteIo8(address, (byte)value);
        WriteIo8(address + 1, (byte)(value >> 8));
        WriteIo8(address + 2, (byte)(value >> 16));
        WriteIo8(address + 3, (byte)(value >> 24));
    }

    private void WriteBusMaster(uint offset, uint value, VegasMemoryMap memory)
    {
        if (offset + 3 >= _busMaster.Length)
            return;

        BinaryPrimitives.WriteUInt32LittleEndian(_busMaster.AsSpan((int)offset, 4), value);
        Trace($"bmdma write off={offset:x2} value={value:x8}");
        if ((offset & 7) == 0 && (value & BusMasterCommandStart) != 0 && (value & BusMasterCommandRead) != 0)
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
            memory.WritePciMemoryFromDevice(destination, buffer);
            copied += buffer.Length;
            if ((descriptor & 0x80000000u) != 0)
                break;
        }

        _busMaster[0] &= unchecked((byte)~BusMasterCommandStart);
        _busMaster[2] |= BusMasterStatusInterrupt;
        Trace($"bmdma primary read copied={copied}");
    }

    private static uint ReadMainMemory32(VegasMemoryMap memory, uint address)
        => memory.ReadPciMemoryFromDevice32(address);

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

    public string? ImagePath { get; private set; }
    public DiskGeometry Geometry => _image?.Geometry ?? DiskGeometry.Empty;
    public bool Attached => _image is not null;

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
    }

    public byte ReadRegister8(byte register)
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
                case 0x24:
                    StartReadSectors(command == 0x24);
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
        }
        catch (Exception ex)
        {
            _transferBuffer = Array.Empty<byte>();
            _transferOffset = 0;
            _error = 0x04;
            _status = (byte)(StatusDrdy | StatusDsc | StatusErr);
            Trace($"command {command:x2} failed: {ex.Message}");
        }
    }

    private void StartReadSectors(bool lba48)
    {
        if (_image is null)
            throw new InvalidOperationException("No IDE disk image attached.");

        uint count = _sectorCount == 0 ? 256u : _sectorCount;
        ulong lba = lba48 ? BuildLba28() : BuildLba28();
        byte[] buffer = new byte[count * (uint)_image.Geometry.BytesPerSector];
        for (uint i = 0; i < count; i++)
            _image.ReadSector(lba + i, buffer.AsSpan((int)(i * (uint)_image.Geometry.BytesPerSector), _image.Geometry.BytesPerSector));

        StartTransfer(buffer);
        Trace($"read sectors lba={lba} count={count}");
    }

    private ulong BuildLba28()
        => (ulong)(((_driveHead & 0x0f) << 24) |
                   (_cylinderHigh << 16) |
                   (_cylinderLow << 8) |
                   _sectorNumber);

    private void StartTransfer(byte[] buffer)
    {
        _transferBuffer = buffer;
        _transferOffset = 0;
        _status = (byte)(StatusDrdy | StatusDsc | StatusDrq);
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
            System.IO.Path.Combine(directory, $"{name}.bin")
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
    public void Reset() { }
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
    void WriteFifo(ReadOnlySpan<uint> words);
    uint ReadLfb32(uint offset);
    void WriteLfb32(uint offset, uint value);
    void WriteTexture32(uint offset, uint value);
    void RenderFrame(EutherFrameTarget target);
}

public readonly record struct EutherFrameTarget(byte[] Buffer, int Width, int Height, int Stride);

internal sealed class VoodooFacade : IVoodooBackend
{
    private IVoodooBackend _backend = new VoodooBringupBackend();

    public bool TraceEnabled => _backend is VoodooTraceBackend;
    public bool HasVideoActivity => _backend is VoodooBringupBackend { HasVideoActivity: true };

    public void Reset()
    {
        _backend = VoodooTraceBackend.IsEnabled()
            ? new VoodooTraceBackend()
            : new VoodooBringupBackend();
    }

    public void WriteRegister(uint address, uint value) => _backend.WriteRegister(address, value);
    public void WriteFifo(ReadOnlySpan<uint> words) => _backend.WriteFifo(words);
    public uint ReadLfb32(uint offset) => _backend.ReadLfb32(offset);
    public void WriteLfb32(uint offset, uint value) => _backend.WriteLfb32(offset, value);
    public void WriteTexture32(uint offset, uint value) => _backend.WriteTexture32(offset, value);
    public void RenderFrame(EutherFrameTarget target) => _backend.RenderFrame(target);
}

internal class VoodooBringupBackend : IVoodooBackend
{
    private const int LfbBytes = 4 * 1024 * 1024;
    private const int LfbPixels = LfbBytes / 2;

    private readonly uint[] _registers = new uint[0x400];
    private readonly ushort[] _lfb = new ushort[LfbPixels];
    private int _registerWriteCount;
    private int _fifoWriteCount;
    private int _lfbWriteCount;
    private int _textureWriteCount;
    private int _renderFrame;

    public bool HasVideoActivity => _registerWriteCount > 0 || _fifoWriteCount > 0 || _lfbWriteCount > 0 || _textureWriteCount > 0;

    public virtual void WriteRegister(uint address, uint value)
    {
        _registers[(address >> 2) & 0x3ffu] = value;
        _registerWriteCount++;
    }

    public virtual void WriteFifo(ReadOnlySpan<uint> words)
    {
        _fifoWriteCount += words.Length;
    }

    public uint ReadLfb32(uint offset)
    {
        int pixel = (int)((offset & (LfbBytes - 1u)) >> 1);
        ushort low = _lfb[pixel & (LfbPixels - 1)];
        ushort high = _lfb[(pixel + 1) & (LfbPixels - 1)];
        return (uint)(low | (high << 16));
    }

    public virtual void WriteLfb32(uint offset, uint value)
    {
        int pixel = (int)((offset & (LfbBytes - 1u)) >> 1);
        _lfb[pixel & (LfbPixels - 1)] = (ushort)value;
        _lfb[(pixel + 1) & (LfbPixels - 1)] = (ushort)(value >> 16);
        _lfbWriteCount++;
    }

    public virtual void WriteTexture32(uint offset, uint value)
    {
        _textureWriteCount++;
    }

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
        DrawViewportOverlay(target);
    }

    private bool TryRenderLfb(EutherFrameTarget target)
    {
        if (_lfbWriteCount == 0)
            return false;

        int copyWidth = Math.Min(target.Width, 640);
        int copyHeight = Math.Min(target.Height, 480);
        int visiblePixels = 0;
        for (int y = 0; y < copyHeight; y++)
        {
            int src = y * 1024;
            int dst = y * target.Stride;
            for (int x = 0; x < copyWidth; x++)
            {
                ushort rgb = _lfb[(src + x) & (LfbPixels - 1)];
                if (rgb != 0)
                    visiblePixels++;
                uint bgra = Rgb565ToBgra(rgb);
                target.Buffer[dst + 0] = (byte)(bgra & 0xff);
                target.Buffer[dst + 1] = (byte)((bgra >> 8) & 0xff);
                target.Buffer[dst + 2] = (byte)((bgra >> 16) & 0xff);
                target.Buffer[dst + 3] = 0xff;
                dst += 4;
            }
        }

        return visiblePixels > 0;
    }

    private void DrawRegisterBands(EutherFrameTarget target)
    {
        int bandHeight = Math.Max(6, target.Height / 64);
        for (int i = 0; i < 32; i++)
        {
            uint value = _registers[(0x200u >> 2) + i];
            uint color = 0xff000000u |
                ((value << 3) & 0x00ff0000u) |
                ((value >> 5) & 0x0000ff00u) |
                ((value >> 13) & 0x000000ffu);
            FillRect(target, 0, i * bandHeight, target.Width, bandHeight, color);
        }
    }

    private void DrawViewportOverlay(EutherFrameTarget target)
    {
        uint clipX = _registers[(0x208u >> 2) & 0x3ffu];
        uint clipY = _registers[(0x20cu >> 2) & 0x3ffu];
        int x0 = (int)((clipX >> 16) & 0x7ff);
        int x1 = (int)(clipX & 0x7ff);
        int y0 = (int)((clipY >> 16) & 0x7ff);
        int y1 = (int)(clipY & 0x7ff);
        if (x1 <= x0 || x1 > target.Width)
        {
            x0 = 0;
            x1 = target.Width;
        }
        if (y1 <= y0 || y1 > target.Height)
        {
            y0 = 0;
            y1 = target.Height;
        }

        DrawRect(target, x0, y0, x1 - x0, y1 - y0, 0xffffffffu);

        int sweep = _renderFrame % Math.Max(1, target.Width);
        FillRect(target, sweep, y0, 4, y1 - y0, 0xff00d7ffu);
        FillRect(target, 16, target.Height - 28, Math.Min(target.Width - 32, _registerWriteCount / 32), 8, 0xff39d353u);
        FillRect(target, 16, target.Height - 18, Math.Min(target.Width - 32, _lfbWriteCount / 64), 8, 0xfff7b955u);
        FillRect(target, 16, target.Height - 8, Math.Min(target.Width - 32, (_fifoWriteCount + _textureWriteCount) / 256), 6, 0xffc678ddU);
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
    private readonly bool _traceLfb = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_LFB") == "1";
    private readonly bool _traceTexture = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEX") == "1";
    private readonly int _fifoTraceLimit = ParseTraceLimit("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FIFO_LIMIT", 64);
    private readonly int _lfbTraceLimit = ParseTraceLimit("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_LFB_LIMIT", 64);
    private readonly int _textureTraceLimit = ParseTraceLimit("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEX_LIMIT", 64);
    private int _fifoTraceCount;
    private int _lfbTraceCount;
    private int _textureTraceCount;

    public static bool IsEnabled()
        => Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO") == "1" ||
           Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_FIFO") == "1" ||
           Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_LFB") == "1" ||
           Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO_TEX") == "1";

    public override void WriteRegister(uint address, uint value)
    {
        base.WriteRegister(address, value);
        if (_traceRegisters)
            Console.WriteLine($"[GAUNTDL:VOODOO] reg[{address:x8}]={value:x8}");
    }

    public override void WriteFifo(ReadOnlySpan<uint> words)
    {
        base.WriteFifo(words);
        if (_traceRegisters)
            Console.WriteLine($"[GAUNTDL:VOODOO] fifo words={words.Length}");
        else if (_traceFifo)
        {
            for (int i = 0; i < words.Length && _fifoTraceCount < _fifoTraceLimit; i++, _fifoTraceCount++)
                Console.WriteLine($"[GAUNTDL:VOODOO] fifo[{_fifoTraceCount:x6}]={words[i]:x8}");
        }
    }

    public override void WriteLfb32(uint offset, uint value)
    {
        base.WriteLfb32(offset, value);
        if ((_traceRegisters || _traceLfb) && _lfbTraceCount++ < _lfbTraceLimit)
            Console.WriteLine($"[GAUNTDL:VOODOO] lfb[{offset:x6}]={value:x8}");
    }

    public override void WriteTexture32(uint offset, uint value)
    {
        base.WriteTexture32(offset, value);
        if ((_traceRegisters || _traceTexture) && _textureTraceCount++ < _textureTraceLimit)
            Console.WriteLine($"[GAUNTDL:VOODOO] tex[{offset:x6}]={value:x8}");
    }

    private static int ParseTraceLimit(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value >= 0 ? value : fallback;
}
