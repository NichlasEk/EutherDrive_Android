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
    private readonly int _stepBudget = ParsePositiveInt("EUTHERDRIVE_GAUNTDL_CPU_STEPS_PER_FRAME", 2048);
    private readonly ulong _cp0CountStep = (ulong)ParsePositiveInt("EUTHERDRIVE_GAUNTDL_CP0_COUNT_STEP", 1024);
    private bool _halted;
    private bool _hasPendingBranch;
    private ulong _pendingBranchTarget;
    private bool _hasImmediatePcOverride;
    private ulong _immediatePcOverride;
    private ulong _instructionCounter;
    private ulong _hi;
    private ulong _lo;

    public MipsR5000Core(VegasMemoryMap memory)
    {
        _memory = memory;
    }

    public ulong Pc { get; private set; }
    public uint LastFetchedInstruction { get; private set; }

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

        uint op = _memory.Read32(pc);
        LastFetchedInstruction = op;
        ulong nextPc = pc + 4;
        bool branchFromPreviousInstruction = _hasPendingBranch;
        ulong branchTarget = _pendingBranchTarget;
        _hasPendingBranch = false;
        _hasImmediatePcOverride = false;

        if (ShouldTrace(pc))
            Console.WriteLine($"[GAUNTDL:CPU] #{_instructionCounter} pc={pc:x16} op={op:x8} {DisassembleBrief(op)} a0={_gpr[4]:x16} a1={_gpr[5]:x16} v0={_gpr[2]:x16} v1={_gpr[3]:x16}");

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
        uint exitOffset = offset switch
        {
            0x1fc039c8UL or 0x1fc039d0UL or 0x1fc039d4UL => 0x1fc039dc,
            0x1fc039f0UL or 0x1fc039f8UL => 0x1fc03a04,
            0x1fc03a18UL or 0x1fc03a20UL => 0x1fc03a2c,
            0x1fc03a40UL or 0x1fc03a50UL or 0x1fc03a54UL => 0x1fc03a5c,
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
            case 0x00:
            case 0x01:
                _gpr[rt] = _cp0[rd];
                break;
            case 0x04:
            case 0x05:
                _cp0[rd] = _gpr[rt];
                break;
            case 0x10:
                ExecuteCop0Operation(op);
                break;
            default:
                HaltUnsupported(Pc, op, $"cop0 rs={rs:x2}");
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
    }

    private bool ShouldTrace(ulong pc)
    {
        if (!_traceEnabled)
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
    private const int MainRamSize = 32 * 1024 * 1024;
    private const uint UnmappedReadValue = 0xffffffffu;

    private readonly List<VegasMemoryRange> _ranges = new();
    private readonly byte[] _mainRam = new byte[MainRamSize];
    private readonly bool _traceEnabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_GAUNTDL_TRACE_MEM") == "1";
    private byte[] _mainBootRom = Array.Empty<byte>();
    private VegasSioDevice? _sio;
    private IdeDiskDevice? _disk;
    private DcsAudioDevice? _audio;
    private VoodooFacade? _voodoo;

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
    }

    public void LoadMainBootRom(byte[] mainBootRom) => _mainBootRom = mainBootRom.ToArray();

    public byte Read8(ulong address)
    {
        if (TryReadBootRomByte(address, out byte romValue))
        {
            Trace("read8", address, romValue, "PCI_ID_NILE:rom");
            return romValue;
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
        Write8(address, (byte)value);
        Write8(address + 1, (byte)(value >> 8));
    }

    public void Write32(ulong address, uint value)
    {
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
        if (TryTranslatePhysical(address, out uint physical) && physical + 7 < _mainRam.Length)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_mainRam.AsSpan((int)physical, 8), value);
            Trace("write64", address, unchecked((uint)value), "mainram");
            return;
        }

        Write32(address, (uint)value);
        Write32(address + 4, (uint)(value >> 32));
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
