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

        _machine.RunFrame();
        _frameCounter++;
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

    public void RunFrame()
    {
        Cpu.RunProbeFrame();
        Voodoo.RenderFrame(new EutherFrameTarget());
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
    private const ulong Cp0ConfigWriteMask = 0x0000003fUL;
    private bool _halted;
    private bool _hasPendingBranch;
    private ulong _pendingBranchTarget;
    private bool _hasImmediatePcOverride;
    private ulong _immediatePcOverride;
    private ulong _instructionCounter;
    private int _traceInstructionCount;
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
        if (TryFastPathKnownRamNileTimerDelay(pc))
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
        _cp0[9] += _cp0CountStep;
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
        _cp0[9] += _cp0CountStep;
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
        _cp0[9] += _cp0CountStep;
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

    private bool TryFastPathKnownRamNileTimerDelay(ulong pc)
    {
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

    private void CompleteFastPathStep()
    {
        _gpr[0] = 0;
        _cp0[9] += _cp0CountStep;
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
            case 0x23:
                _gpr[rt] = unchecked((ulong)(int)_memory.Read32(_gpr[rs] + (ulong)(long)simm));
                break;
            case 0x24:
                _gpr[rt] = _memory.Read8(_gpr[rs] + (ulong)(long)simm);
                break;
            case 0x25:
                _gpr[rt] = _memory.Read16(_gpr[rs] + (ulong)(long)simm);
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
            case 0x2b:
                _memory.Write32(_gpr[rs] + (ulong)(long)simm, (uint)_gpr[rt]);
                break;
            case 0x2f:
                break;
            case 0x37:
                _gpr[rt] = _memory.Read64(_gpr[rs] + (ulong)(long)simm);
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
            case 0x21:
            case 0x2d:
                _gpr[rd] = _gpr[rs] + _gpr[rt];
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
            0x10 => signed < 0,
            0x11 => signed >= 0,
            _ => false
        };

        if (rt is 0x10 or 0x11)
            _gpr[31] = pc + 8;

        if (take)
            QueueBranch(pc + 4 + ((ulong)(long)simm << 2));

        if (rt is not (0x00 or 0x01 or 0x10 or 0x11))
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

    private void WriteCp0(int register, ulong value)
    {
        switch (register)
        {
            case 11: // Compare
                _cp0[11] = (uint)value;
                _cp0[13] &= ~0x00008000UL; // Clear timer interrupt pending.
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
            default:
                HaltUnsupported(Pc, op, $"cop0 op {funct:x2}");
                break;
        }
    }

    private void ExecuteCop1(ulong pc, uint op, int rs, int rt, int rd)
    {
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
            default:
                HaltUnsupported(pc, op, $"cop1 rs={rs:x2}");
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
            0x01 => $"regimm.{(op >> 16) & 0x1f:x2}",
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
            0x23 => "lw",
            0x24 => "lbu",
            0x25 => "lhu",
            0x27 => "lwu",
            0x28 => "sb",
            0x29 => "sh",
            0x2b => "sw",
            0x2f => "cache",
            0x37 => "ld",
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
    private const uint NilePciWindow0Offset = 0x060;
    private const uint NilePciInit0Offset = 0x0f0;
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
    private readonly VegasIdePciDevice _idePci = new();
    private readonly bool _traceEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_MEM") == "1";
    private byte[] _mainBootRom = Array.Empty<byte>();
    private VegasSioDevice? _sio;
    private IdeDiskDevice? _disk;
    private DcsAudioDevice? _audio;
    private VoodooFacade? _voodoo;
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
    }

    public void LoadMainBootRom(byte[] mainBootRom) => _mainBootRom = mainBootRom.ToArray();

    public void Reset()
    {
        Array.Clear(_nileRegisters);
        Array.Clear(_fpgaConfigRegisters);
        _fpgaConfigSeenLow = false;
        _fpgaConfigStatusHigh = false;
        _fpgaConfigDone = false;
        _idePci.Reset();
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
            value = (byte)(_idePci.ReadConfig32(DecodePciConfigAlias(offset) & ~3u) >> (int)((offset & 3) * 8));
            return true;
        }

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
            value = _idePci.ReadConfig32(DecodePciConfigAlias(offset));
            return true;
        }

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
            _idePci.WriteConfig32(DecodePciConfigAlias(offset), value);
            return true;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(_nileRegisters.AsSpan((int)offset, 4), value);
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
            PciTypeConfig => (byte)(_idePci.ReadConfig32(DecodePciType0ConfigAddress(pciAddress) & ~3u) >> (int)((pciAddress & 3) * 8)),
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
            PciTypeConfig => (ushort)(_idePci.ReadConfig32(DecodePciType0ConfigAddress(pciAddress) & ~3u) >> (int)((pciAddress & 2) * 8)),
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
            PciTypeConfig => _idePci.ReadConfig32(DecodePciType0ConfigAddress(pciAddress)),
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
                _idePci.WriteConfig32(DecodePciType0ConfigAddress(pciAddress), value);
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
        if (pciAddress + 3 < _mainRam.Length)
            return BinaryPrimitives.ReadUInt32LittleEndian(_mainRam.AsSpan((int)pciAddress, 4));
        return UnmappedReadValue;
    }

    private byte ReadPciMemory8(uint pciAddress)
        => pciAddress < _mainRam.Length ? _mainRam[pciAddress] : (byte)0xff;

    private void WritePciMemory32(uint pciAddress, uint value)
    {
        if (pciAddress + 3 < _mainRam.Length)
            BinaryPrimitives.WriteUInt32LittleEndian(_mainRam.AsSpan((int)pciAddress, 4), value);
    }

    private void WritePciMemory8(uint pciAddress, byte value)
    {
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
        uint current = _idePci.ReadConfig32(offset);
        int shift = (int)((pciAddress & 3) * 8);
        uint merged = (current & ~(0xffu << shift)) | ((uint)value << shift);
        _idePci.WriteConfig32(offset, merged);
    }

    private void WritePciConfigHalf(uint pciAddress, ushort value)
    {
        uint offset = DecodePciType0ConfigAddress(pciAddress) & ~3u;
        uint current = _idePci.ReadConfig32(offset);
        int shift = (int)((pciAddress & 2) * 8);
        uint merged = (current & ~(0xffffu << shift)) | ((uint)value << shift);
        _idePci.WriteConfig32(offset, merged);
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

        if (offset == 2)
        {
            value = (byte)(_fpgaConfigRegisters[offset] & 0xf0);
            if (_fpgaConfigStatusHigh)
                value |= 0x02;
            if (_fpgaConfigDone)
                value |= 0x01;
        }
        else
        {
            value = _fpgaConfigRegisters[offset];
        }

        return true;
    }

    private bool TryWriteFpgaConfig8(ulong address, byte value)
    {
        if (!TryGetFpgaConfigOffset(address, out uint offset))
            return false;

        _fpgaConfigRegisters[offset] = value;
        if (offset == 1)
        {
            if ((value & 0x01) == 0)
            {
                _fpgaConfigSeenLow = true;
                _fpgaConfigStatusHigh = false;
                _fpgaConfigDone = false;
            }
            else if (_fpgaConfigSeenLow)
            {
                _fpgaConfigStatusHigh = true;
            }
        }

        return true;
    }

    public void MarkFpgaConfigDone()
    {
        _fpgaConfigStatusHigh = true;
        _fpgaConfigDone = true;
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
            uint value = chipSelect == 2
                ? _sio?.Read(offset) ?? UnmappedReadValue
                : UnmappedReadValue;
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
            if (chipSelect == 2)
                _sio?.Write(offset, (byte)(value & 0xff));
            return;
        }

        Trace("write32", FormatChipSelectAddress(chipSelect, offset), value, $"CS{chipSelect} unmapped");
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

    private void Trace(string op, ulong address, uint value, string target)
    {
        if (_traceEnabled)
            Console.WriteLine($"[GAUNTDL:MEM] {op} {address:x16} {value:x8} {target}");
    }
}

internal readonly record struct VegasMemoryRange(string Name, int ChipSelect, ulong Start, ulong End);

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
    private const byte StatusDrdy = 0x40;
    private const byte StatusBsy = 0x80;

    private readonly bool _traceEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_IDE") == "1";
    private IDiskImage? _image;
    private byte[] _transferBuffer = Array.Empty<byte>();
    private int _transferOffset;
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
        _error = 0;
        _sectorCount = 1;
        _sectorNumber = 0;
        _cylinderLow = 0;
        _cylinderHigh = 0;
        _driveHead = 0xe0;
        _status = Attached ? StatusDrdy : (byte)0;
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
                break; // features
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
            _status = StatusDrdy;
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
            _status = StatusDrdy;
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
                default:
                    _error = 0x04; // ABRT
                    _status = (byte)(StatusDrdy | StatusErr);
                    Trace($"unsupported command {command:x2}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _transferBuffer = Array.Empty<byte>();
            _transferOffset = 0;
            _error = 0x04;
            _status = (byte)(StatusDrdy | StatusErr);
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
        _status = (byte)(StatusDrdy | StatusDrq);
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
    void RenderFrame(EutherFrameTarget target);
}

public readonly record struct EutherFrameTarget;

internal sealed class VoodooFacade : IVoodooBackend
{
    private IVoodooBackend _backend = new VoodooNullBackend();

    public bool TraceEnabled => _backend is VoodooTraceBackend;

    public void Reset()
    {
        _backend = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_VOODOO") == "1"
            ? new VoodooTraceBackend()
            : new VoodooNullBackend();
    }

    public void WriteRegister(uint address, uint value) => _backend.WriteRegister(address, value);
    public void WriteFifo(ReadOnlySpan<uint> words) => _backend.WriteFifo(words);
    public void RenderFrame(EutherFrameTarget target) => _backend.RenderFrame(target);
}

internal sealed class VoodooNullBackend : IVoodooBackend
{
    public void WriteRegister(uint address, uint value) { }
    public void WriteFifo(ReadOnlySpan<uint> words) { }
    public void RenderFrame(EutherFrameTarget target) { }
}

internal sealed class VoodooTraceBackend : IVoodooBackend
{
    public void WriteRegister(uint address, uint value)
        => Console.WriteLine($"[GAUNTDL:VOODOO] reg[{address:x8}]={value:x8}");

    public void WriteFifo(ReadOnlySpan<uint> words)
        => Console.WriteLine($"[GAUNTDL:VOODOO] fifo words={words.Length}");

    public void RenderFrame(EutherFrameTarget target) { }
}
