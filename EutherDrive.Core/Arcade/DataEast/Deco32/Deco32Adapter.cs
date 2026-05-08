using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using SharpCompress.Archives;
using EutherDrive.Core;
using EutherDrive.Core.Savestates;

namespace EutherDrive.Platforms.DataEast.Deco32;

public sealed class Deco32Adapter : IEmulatorCore
{
    private const int FrameWidth = 320;
    private const int FrameHeight = 240;
    private const int FrameStride = FrameWidth * 4;
    private const int OutputSampleRate = 44_100;
    private const int OutputChannels = 2;
    private const int ArmClockHz = 28_000_000 / 4;
    private const int CyclesPerFrame = ArmClockHz / 60;
    private static readonly bool TraceCpu =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DECO32_TRACE_CPU"), "1", StringComparison.Ordinal);
    private static readonly int TraceCpuLimit = ParseEnvInt("EUTHERDRIVE_DECO32_TRACE_LIMIT", 2000);

    private readonly object _frameSync = new();
    private byte[] _presentFrameBuffer = new byte[FrameHeight * FrameStride];
    private byte[] _renderFrameBuffer = new byte[FrameHeight * FrameStride];
    private byte[] _snapshotFrameBuffer = new byte[FrameHeight * FrameStride];
    private short[] _audioBuffer = new short[(OutputSampleRate / 60) * OutputChannels];
    private readonly Arm6Cpu _mainCpu = new();
    private Deco32MemoryMap? _memory;
    private DecoTilemapDevice? _tilemaps;
    private DecoSpriteDevice? _sprites;
    private PaletteDevice? _palette;
    private Z80SoundCpu? _soundCpu;
    private YM2151? _ym2151;
    private OKI6295? _oki1;
    private OKI6295? _oki2;
    private ArcadeInputState _input;
    private bool _loaded;
    private long _frameCounter;
    private int _traceLines;
    private string? _lastStopReason;
    private RomIdentity? _romIdentity;
    private uint _visiblePc;
    private uint _visibleOp;
    private uint _visibleCpsr;
    private uint _vblankPc;
    private uint _vblankOp;
    private uint _vblankCpsr;
    private uint _postFramePc;
    private uint _postFrameOp;
    private uint _postFrameCpsr;

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;
        return NightSlashersGameProfile.IsSupportedSet(Path.GetFileNameWithoutExtension(path));
    }

    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => _loaded ? _frameCounter : null;
    public double GetTargetFps() => 60.0;
    public string DebugSummary => _memory is null
        ? "not-loaded"
        : string.Create(
            CultureInfo.InvariantCulture,
            $"pc=0x{_mainCpu.Pc:X8} op=0x{_mainCpu.PeekOpcode():X8} vis=0x{_visiblePc:X8}/0x{_visibleOp:X8}/0x{_visibleCpsr:X8} vb=0x{_vblankPc:X8}/0x{_vblankOp:X8}/0x{_vblankCpsr:X8} post=0x{_postFramePc:X8}/0x{_postFrameOp:X8}/0x{_postFrameCpsr:X8} r0=0x{_mainCpu.Registers[0]:X8} r1=0x{_mainCpu.Registers[1]:X8} r2=0x{_mainCpu.Registers[2]:X8} r3=0x{_mainCpu.Registers[3]:X8} r9=0x{_mainCpu.Registers[9]:X8} sl=0x{_mainCpu.Registers[10]:X8} fp=0x{_mainCpu.Registers[11]:X8} sp=0x{_mainCpu.Registers[13]:X8} lr=0x{_mainCpu.Registers[14]:X8} cpsr=0x{_mainCpu.Cpsr:X8} halted={_mainCpu.Halted} reason='{_lastStopReason ?? _mainCpu.StopReason}' frame={_frameCounter} vram={_memory.VideoWriteCount} pal={_memory.PaletteWriteCount} spr={_memory.SpriteWriteCount} {_memory.ProtectionDebugSummary} {_memory.TilemapDebugSummary} {_memory.SpriteDebugSummary}");

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Deco32 ROM path is empty.", nameof(path));
        if (!RomArchiveExtractor.FileExists(path))
            throw new FileNotFoundException("Deco32 ROM archive not found.", path);

        byte[] romHash;
        using (Stream stream = RomArchiveExtractor.OpenRead(path))
            romHash = RomIdentity.ComputeSha256(stream);

        NightSlashersGameProfile profile = NightSlashersGameProfile.Load(path);
        _palette = new PaletteDevice();
        _tilemaps = new DecoTilemapDevice(profile, _palette);
        _sprites = new DecoSpriteDevice(profile, _palette);
        _soundCpu = new Z80SoundCpu(profile.AudioCpu);
        _ym2151 = new YM2151();
        _oki1 = new OKI6295(profile.Oki1);
        _oki2 = new OKI6295(profile.Oki2);
        _memory = new Deco32MemoryMap(profile, _palette, _tilemaps, _sprites, _soundCpu, _ym2151, _oki1, _oki2);
        _mainCpu.Reset(_memory);
        _loaded = true;
        _frameCounter = 0;
        _traceLines = 0;
        _lastStopReason = null;
        ClearCpuCapture();
        Array.Clear(_presentFrameBuffer);
        Array.Clear(_renderFrameBuffer);
        Array.Clear(_snapshotFrameBuffer);
        Array.Clear(_audioBuffer);
        _romIdentity = new RomIdentity(
            Path.GetFileName(path),
            romHash,
            PersistentStoragePath.ResolveSavestateDirectory(path, "deco32"));
        RenderFrame();
    }

    public void Reset()
    {
        if (!_loaded || _memory is null)
            return;
        _memory.Reset();
        _mainCpu.Reset(_memory);
        _frameCounter = 0;
        _traceLines = 0;
        _lastStopReason = null;
        ClearCpuCapture();
        Array.Clear(_audioBuffer);
        RenderFrame();
    }

    public void RunFrame()
    {
        if (!_loaded || _memory is null)
            return;

        _memory.SetInput(_input);
        _memory.BeginFrame();
        ExecuteMainCpu(CyclesPerFrame * 238 / 274);
        CaptureCpuPoint(out _visiblePc, out _visibleOp, out _visibleCpsr);
        _memory.AssertVblank();
        _mainCpu.SetIrqLine(true);
        ExecuteMainCpu(512);
        CaptureCpuPoint(out _vblankPc, out _vblankOp, out _vblankCpsr);
        _memory.ClearVblank();
        ExecuteMainCpu((CyclesPerFrame * 36 / 274) - 512);
        _mainCpu.SetIrqLine(false);
        CaptureCpuPoint(out _postFramePc, out _postFrameOp, out _postFrameCpsr);
        _memory.EndFrame();
        RenderFrame();
        Array.Clear(_audioBuffer);
        _soundCpu?.RunFrame();
        _frameCounter++;
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        lock (_frameSync)
        {
            Buffer.BlockCopy(_presentFrameBuffer, 0, _snapshotFrameBuffer, 0, _presentFrameBuffer.Length);
            width = FrameWidth;
            height = FrameHeight;
            stride = FrameStride;
            return _snapshotFrameBuffer;
        }
    }

    public ReadOnlySpan<short> GetAudioBuffer(out int sampleRate, out int channels)
    {
        sampleRate = OutputSampleRate;
        channels = OutputChannels;
        return _audioBuffer;
    }

    public void SetMasterVolumePercent(int percent)
    {
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
        _input = new ArcadeInputState(up, down, left, right, a, b, c, start, x, y, z, mode);
    }

    private void ExecuteMainCpu(int cycleBudget)
    {
        int elapsed = 0;
        while (elapsed < cycleBudget && !_mainCpu.Halted)
        {
            uint pc = _mainCpu.Pc;
            uint opcode = _mainCpu.PeekOpcode();
            int cycles = _mainCpu.ExecuteInstruction();
            elapsed += Math.Max(1, cycles);
            if (TraceCpu && _traceLines++ < TraceCpuLimit)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[DECO32 ARM] pc=0x{pc:X8} op=0x{opcode:X8} next=0x{_mainCpu.Pc:X8} r0=0x{_mainCpu.Registers[0]:X8} r1=0x{_mainCpu.Registers[1]:X8} r2=0x{_mainCpu.Registers[2]:X8} r3=0x{_mainCpu.Registers[3]:X8} r4=0x{_mainCpu.Registers[4]:X8} r5=0x{_mainCpu.Registers[5]:X8} r6=0x{_mainCpu.Registers[6]:X8} r7=0x{_mainCpu.Registers[7]:X8} sp=0x{_mainCpu.Registers[13]:X8} lr=0x{_mainCpu.Registers[14]:X8} cpsr=0x{_mainCpu.Cpsr:X8}"));
            }
        }

        if (_mainCpu.Halted)
            _lastStopReason = _mainCpu.StopReason;
    }

    private void RenderFrame()
    {
        Array.Clear(_renderFrameBuffer);
        _palette?.FillBackdrop(_renderFrameBuffer, FrameWidth, FrameHeight, FrameStride);
        _tilemaps?.Render(_renderFrameBuffer, FrameWidth, FrameHeight, FrameStride);
        _sprites?.Render(_renderFrameBuffer, FrameWidth, FrameHeight, FrameStride, _frameCounter);
        _memory?.RenderWorkRamTextOverlay(_renderFrameBuffer, FrameWidth, FrameHeight, FrameStride);
        lock (_frameSync)
            Buffer.BlockCopy(_renderFrameBuffer, 0, _presentFrameBuffer, 0, _renderFrameBuffer.Length);
    }

    private static int ParseEnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private void ClearCpuCapture()
    {
        _visiblePc = _visibleOp = _visibleCpsr = 0;
        _vblankPc = _vblankOp = _vblankCpsr = 0;
        _postFramePc = _postFrameOp = _postFrameCpsr = 0;
    }

    private void CaptureCpuPoint(out uint pc, out uint opcode, out uint cpsr)
    {
        pc = _mainCpu.Pc;
        opcode = _mainCpu.PeekOpcode();
        cpsr = _mainCpu.Cpsr;
    }
}

public sealed class NightSlashersGameProfile
{
    public byte[] MainCpu { get; private init; } = Array.Empty<byte>();
    public byte[] AudioCpu { get; private init; } = Array.Empty<byte>();
    public byte[] Tiles1 { get; private init; } = Array.Empty<byte>();
    public byte[] Tiles2 { get; private init; } = Array.Empty<byte>();
    public byte[] Sprites1 { get; private init; } = Array.Empty<byte>();
    public byte[] Sprites2 { get; private init; } = Array.Empty<byte>();
    public byte[] Oki1 { get; private init; } = Array.Empty<byte>();
    public byte[] Oki2 { get; private init; } = Array.Empty<byte>();
    public byte[] Prom { get; private init; } = Array.Empty<byte>();

    public static bool IsSupportedSet(string? setName)
        => string.Equals(setName, "nslasher", StringComparison.OrdinalIgnoreCase);

    public static NightSlashersGameProfile Load(string archivePath)
    {
        using IArchive archive = RomArchiveExtractor.OpenArchive(archivePath);
        var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (IArchiveEntry entry in archive.Entries)
        {
            if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
                continue;
            using Stream stream = entry.OpenEntryStream();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            entries[Path.GetFileName(entry.Key)] = ms.ToArray();
        }

        byte[] main = new byte[0x100000];
        Load32Word(main, Required(entries, "mainprg.1f"), 0);
        Load32Word(main, Required(entries, "mainprg.2f"), 2);
        Deco156Decrypt(main);

        byte[] tiles1 = Required(entries, "mbh-00.8c");
        byte[] tiles2 = Required(entries, "mbh-01.9c");
        ReorderNightSlashersTilePlanes(tiles1);
        ReorderNightSlashersTilePlanes(tiles2);
        Deco32GfxDecryptor.Decrypt56(tiles1);
        Deco32GfxDecryptor.Decrypt74(tiles2);

        return new NightSlashersGameProfile
        {
            MainCpu = main,
            AudioCpu = Required(entries, "sndprg.17l"),
            Tiles1 = tiles1,
            Tiles2 = tiles2,
            Sprites1 = BuildSprites1(entries),
            Sprites2 = Combine(entries, ("mbh-08.16e", 0x000000), ("mbh-09.18e", 0x080000), 0x100000),
            Oki1 = Required(entries, "mbh-10.14l"),
            Oki2 = Required(entries, "mbh-11.16l"),
            Prom = entries.TryGetValue("ln-00.j7", out byte[]? prom) ? prom : Array.Empty<byte>()
        };
    }

    private static byte[] Required(Dictionary<string, byte[]> entries, string name)
    {
        if (!entries.TryGetValue(name, out byte[]? data))
            throw new InvalidDataException($"Night Slashers ROM is missing '{name}'.");
        return data;
    }

    private static void Load32Word(byte[] dest, byte[] src, int offset)
    {
        for (int i = 0, d = offset; i + 1 < src.Length && d + 1 < dest.Length; i += 2, d += 4)
        {
            dest[d] = src[i];
            dest[d + 1] = src[i + 1];
        }
    }

    private static byte[] Combine(Dictionary<string, byte[]> entries, (string Name, int Offset) a, (string Name, int Offset) b, int length)
    {
        byte[] result = new byte[length];
        Array.Fill<byte>(result, 0xff);
        Copy(Required(entries, a.Name), result, a.Offset);
        Copy(Required(entries, b.Name), result, b.Offset);
        return result;
    }

    private static byte[] BuildSprites1(Dictionary<string, byte[]> entries)
    {
        byte[] result = new byte[0x640000];
        Array.Fill<byte>(result, 0xff);
        Load40WordSwap(result, Required(entries, "mbh-02.14c"), 0x000003);
        Load40WordSwap(result, Required(entries, "mbh-04.16c"), 0x000001);
        Load40Byte(result, Required(entries, "mbh-06.18c"), 0x000000);
        Load40WordSwap(result, Required(entries, "mbh-03.15c"), 0x500003);
        Load40WordSwap(result, Required(entries, "mbh-05.17c"), 0x500001);
        Load40Byte(result, Required(entries, "mbh-07.19c"), 0x500000);
        return result;
    }

    private static void Copy(byte[] src, byte[] dst, int offset)
        => Buffer.BlockCopy(src, 0, dst, offset, Math.Min(src.Length, dst.Length - offset));

    private static void Load40Byte(byte[] dest, byte[] src, int offset)
    {
        for (int i = 0, d = offset; i < src.Length && d < dest.Length; i++, d += 5)
            dest[d] = src[i];
    }

    private static void Load40WordSwap(byte[] dest, byte[] src, int offset)
    {
        for (int i = 0, d = offset; i + 1 < src.Length && d < dest.Length; i += 2, d += 5)
        {
            dest[d] = src[i + 1];
            if (d - 1 >= 0)
                dest[d - 1] = src[i];
        }
    }

    private static void ReorderNightSlashersTilePlanes(byte[] data)
    {
        if (data.Length < 0x180000)
            return;
        byte[] tmp = new byte[0x80000];
        Buffer.BlockCopy(data, 0x080000, tmp, 0, tmp.Length);
        Buffer.BlockCopy(data, 0x100000, data, 0x080000, tmp.Length);
        Buffer.BlockCopy(tmp, 0, data, 0x100000, tmp.Length);
    }

    private static void Deco156Decrypt(byte[] rom)
    {
        uint[] src = new uint[rom.Length / 4];
        for (int i = 0; i < src.Length; i++)
            src[i] = Read32(rom, i * 4);
        uint[] dst = new uint[src.Length];

        for (int a = 0; a < dst.Length; a++)
        {
            int addr = (a & 0xff0000) | 0x92c6;
            if ((a & 0x0001) != 0) addr ^= 0xce4a;
            if ((a & 0x0002) != 0) addr ^= 0x4db2;
            if ((a & 0x0004) != 0) addr ^= 0xef60;
            if ((a & 0x0008) != 0) addr ^= 0x5737;
            if ((a & 0x0010) != 0) addr ^= 0x13dc;
            if ((a & 0x0020) != 0) addr ^= 0x4bd9;
            if ((a & 0x0040) != 0) addr ^= 0xa209;
            if ((a & 0x0080) != 0) addr ^= 0xd996;
            if ((a & 0x0100) != 0) addr ^= 0xa700;
            if ((a & 0x0200) != 0) addr ^= 0xeca0;
            if ((a & 0x0400) != 0) addr ^= 0x7529;
            if ((a & 0x0800) != 0) addr ^= 0x3100;
            if ((a & 0x1000) != 0) addr ^= 0x33b4;
            if ((a & 0x2000) != 0) addr ^= 0x6161;
            if ((a & 0x4000) != 0) addr ^= 0x1eef;
            if ((a & 0x8000) != 0) addr ^= 0xf5a5;

            uint dword = src[addr & (src.Length - 1)];
            if ((a & 0x00004) != 0) dword ^= 0x04400000;
            if ((a & 0x00008) != 0) dword ^= 0x40000004;
            if ((a & 0x00010) != 0) dword ^= 0x00048000;
            if ((a & 0x00020) != 0) dword ^= 0x00000280;
            if ((a & 0x00040) != 0) dword ^= 0x00200040;
            if ((a & 0x00080) != 0) dword ^= 0x09000000;
            if ((a & 0x00100) != 0) dword ^= 0x00001100;
            if ((a & 0x00200) != 0) dword ^= 0x20002000;
            if ((a & 0x00400) != 0) dword ^= 0x00000022;
            if ((a & 0x00800) != 0) dword ^= 0x000a0000;
            if ((a & 0x01000) != 0) dword ^= 0x10004000;
            if ((a & 0x02000) != 0) dword ^= 0x00010400;
            if ((a & 0x04000) != 0) dword ^= 0x80000010;
            if ((a & 0x08000) != 0) dword ^= 0x00000009;
            if ((a & 0x10000) != 0) dword ^= 0x02100000;
            if ((a & 0x20000) != 0) dword ^= 0x00800800;

            dst[a] = (a & 3) switch
            {
                0 => BitSwap(dword ^ 0xec63197a, 1, 4, 7, 28, 22, 18, 20, 9, 16, 10, 30, 2, 31, 24, 19, 29, 6, 21, 23, 11, 12, 13, 5, 0, 8, 26, 27, 15, 14, 17, 25, 3),
                1 => BitSwap(dword ^ 0x58a5a55f, 14, 23, 28, 29, 6, 24, 10, 1, 5, 16, 7, 2, 30, 8, 18, 3, 31, 22, 25, 20, 17, 0, 19, 27, 9, 12, 21, 15, 26, 13, 4, 11),
                2 => BitSwap(dword ^ 0xe3a65f16, 19, 30, 21, 4, 2, 18, 15, 1, 12, 25, 8, 0, 24, 20, 17, 23, 22, 26, 28, 16, 9, 27, 6, 11, 31, 10, 3, 13, 14, 7, 29, 5),
                _ => BitSwap(dword ^ 0x28d93783, 30, 6, 15, 0, 31, 18, 26, 22, 14, 23, 19, 17, 10, 8, 11, 20, 1, 28, 2, 4, 9, 24, 25, 27, 7, 21, 13, 29, 5, 3, 16, 12)
            };
        }

        for (int i = 0; i < dst.Length; i++)
            Write32(rom, i * 4, dst[i]);
    }

    private static uint Read32(byte[] data, int offset)
        => (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static void Write32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }

    private static uint BitSwap(uint value, params int[] bits)
    {
        uint result = 0;
        for (int i = 0; i < bits.Length; i++)
            result = (result << 1) | ((value >> bits[i]) & 1u);
        return result;
    }
}

internal static class Deco32GfxDecryptor
{
    public static void Decrypt56(byte[] data)
        => Decrypt(
            data,
            Deco32GfxDecryptTables.Deco56XorTable,
            Deco32GfxDecryptTables.Deco56AddressTable,
            Deco32GfxDecryptTables.Deco56SwapTable);

    public static void Decrypt74(byte[] data)
        => Decrypt(
            data,
            Deco32GfxDecryptTables.Deco74XorTable,
            Deco32GfxDecryptTables.Deco74AddressTable,
            Deco32GfxDecryptTables.Deco74SwapTable);

    private static void Decrypt(byte[] data, byte[] xorTable, ushort[] addressTable, byte[] swapTable)
    {
        int words = data.Length >> 1;
        ushort[] source = new ushort[words];
        for (int i = 0; i < words; i++)
            source[i] = ReadBig16(data, i << 1);

        for (int i = 0; i < words; i++)
        {
            int tableOffset = i & 0x7ff;
            int sourceWord = (i & ~0x7ff) | addressTable[tableOffset];
            if ((uint)sourceWord >= (uint)source.Length)
                continue;

            ushort value = (ushort)(source[sourceWord] ^ Deco32GfxDecryptTables.XorMasks[xorTable[sourceWord & 0x7ff] & 0x0f]);
            int pattern = (swapTable[tableOffset] & 7) << 4;
            ushort decoded = BitSwap16(value, pattern);
            WriteBig16(data, i << 1, decoded);
        }
    }

    private static ushort BitSwap16(ushort value, int patternOffset)
    {
        ushort result = 0;
        for (int i = 0; i < 16; i++)
        {
            int bit = Deco32GfxDecryptTables.SwapPatternsFlat[patternOffset + i] & 0x0f;
            result = (ushort)((result << 1) | ((value >> bit) & 1));
        }
        return result;
    }

    private static ushort ReadBig16(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static void WriteBig16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
}

public sealed class Deco32MemoryMap : IArm6Bus
{
    private readonly NightSlashersGameProfile _profile;
    private readonly PaletteDevice _palette;
    private readonly DecoTilemapDevice _tilemaps;
    private readonly DecoSpriteDevice _sprites;
    private readonly Z80SoundCpu _soundCpu;
    private readonly YM2151 _ym2151;
    private readonly OKI6295 _oki1;
    private readonly OKI6295 _oki2;
    private readonly byte[] _workRam = new byte[0x20000];
    private readonly byte[] _aceRam = new byte[0xa0];
    private readonly Deco104Protection _ioprot = new();
    private readonly SerialEeprom93C46 _eeprom = new();
    private ArcadeInputState _input;
    private bool _vblank;
    private byte _priority;
    private bool _backupGuardApplied;
    private uint _lastWorkRamReadAddress;
    private uint _lastWorkRamReadValue;
    private int _workRamReadProbeCount;

    public Deco32MemoryMap(NightSlashersGameProfile profile, PaletteDevice palette, DecoTilemapDevice tilemaps, DecoSpriteDevice sprites, Z80SoundCpu soundCpu, YM2151 ym2151, OKI6295 oki1, OKI6295 oki2)
    {
        _profile = profile;
        _palette = palette;
        _tilemaps = tilemaps;
        _sprites = sprites;
        _soundCpu = soundCpu;
        _ym2151 = ym2151;
        _oki1 = oki1;
        _oki2 = oki2;
    }

    public int VideoWriteCount { get; private set; }
    public int PaletteWriteCount { get; private set; }
    public int SpriteWriteCount { get; private set; }
    public string ProtectionDebugSummary => $"{_ioprot.DebugSummary} {RamDebugSummary}";
    public string TilemapDebugSummary => _tilemaps.DebugSummary;
    public string SpriteDebugSummary => _sprites.DebugSummary;

    public void Reset()
    {
        Array.Clear(_workRam);
        Array.Clear(_aceRam);
        _ioprot.Reset();
        _eeprom.Reset();
        _vblank = false;
        _priority = 0;
        _backupGuardApplied = false;
        _lastWorkRamReadAddress = 0;
        _lastWorkRamReadValue = 0;
        _workRamReadProbeCount = 0;
        VideoWriteCount = PaletteWriteCount = SpriteWriteCount = 0;
        _palette.Reset();
        _tilemaps.Reset();
        _sprites.Reset();
        _soundCpu.Reset();
        _ym2151.Reset();
        _oki1.Reset();
        _oki2.Reset();
    }

    public void SetInput(ArcadeInputState input) => _input = input;
    public void BeginFrame()
    {
        // The real Z80 sound board acknowledges this boot-time command flag.
        // Until the sound CPU is fully integrated, keep the main ARM from
        // spinning forever in the sound handshake.
        _workRam[0] &= 0x7f;
    }
    public void EndFrame() => _sprites.Buffer();
    public void AssertVblank() => _vblank = true;
    public void ClearVblank() => _vblank = false;

    public void RenderWorkRamTextOverlay(byte[] fb, int width, int height, int stride)
    {
        const int baseOffset = 0x0c000;
        const int columns = 64;
        const int rows = 32;
        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                int offset = baseOffset + ((row * columns + col) << 2);
                if ((uint)offset >= (uint)_workRam.Length)
                    continue;

                int ch = DecodeBootTextChar(_workRam[offset]);
                if (ch < 0x20 || ch > 0x7e)
                    continue;

                DrawDebugGlyph(fb, width, height, stride, col * 5, row * 7 + 8, (char)ch);
            }
        }
    }

    public uint Read32(uint address)
    {
        address &= 0x00ffffff;
        if (address < 0x100000)
            return ReadLe32(_profile.MainCpu, (int)address);
        if (address is >= 0x100000 and <= 0x11ffff)
            return ReadWorkRam32(address);
        if (address is >= 0x163000 and <= 0x16309f)
            return ReadMasked16(_aceRam, (int)(address - 0x163000));
        if (address is >= 0x168000 and <= 0x169fff)
            return _palette.Read32(address - 0x168000);
        if (address is >= 0x170000 and <= 0x171fff)
            return _sprites.Read32(0, address - 0x170000);
        if (address is >= 0x178000 and <= 0x179fff)
            return _sprites.Read32(1, address - 0x178000);
        if (address is >= 0x182000 and <= 0x185fff)
            return _tilemaps.ReadData32(0, address - 0x182000);
        if (address is >= 0x192000 and <= 0x195fff)
            return _tilemaps.ReadRowscroll32(0, address - 0x192000);
        if (address is >= 0x1a0000 and <= 0x1a001f)
            return _tilemaps.ReadControl32(0, address - 0x1a0000);
        if (address is >= 0x1c2000 and <= 0x1c5fff)
            return _tilemaps.ReadData32(1, address - 0x1c2000);
        if (address is >= 0x1d2000 and <= 0x1d5fff)
            return _tilemaps.ReadRowscroll32(1, address - 0x1d2000);
        if (address is >= 0x1e0000 and <= 0x1e001f)
            return _tilemaps.ReadControl32(1, address - 0x1e0000);
        if (address is >= 0x200000 and <= 0x207fff)
            return ReadProtection(address);
        return 0xffffffff;
    }

    public void Write32(uint address, uint value, uint mask)
    {
        address &= 0x00ffffff;
        if (address is >= 0x100000 and <= 0x11ffff)
        {
            WriteLe32(_workRam, (int)(address - 0x100000), value, mask);
            MaybeApplyNightSlashersBackupGuard(address - 0x100000);
            return;
        }
        if (address == 0x140000)
        {
            _vblank = false;
            return;
        }
        if ((address & ~3u) == 0x150000)
        {
            if ((mask & 0x000000ff) != 0)
            {
                _priority = (byte)(value & 0x07);
                _eeprom.Write((byte)value);
            }
            return;
        }
        if (address is >= 0x163000 and <= 0x16309f)
        {
            WriteLe32(_aceRam, (int)(address - 0x163000), value, mask & 0x0000ffff);
            return;
        }
        if (address is >= 0x164000 and <= 0x16400f)
        {
            if (address == 0x164000) _tilemaps.ColorBank = (int)(value & 0xff);
            if (address == 0x164004) _sprites.ColorBank0 = (int)(value & 0xff);
            if (address == 0x164008) _sprites.ColorBank1 = (int)(value & 0xff);
            return;
        }
        if (address is >= 0x168000 and <= 0x169fff)
        {
            _palette.Write32(address - 0x168000, value, mask);
            PaletteWriteCount++;
            return;
        }
        if (address == 0x16c008)
            return;
        if (address is >= 0x170000 and <= 0x171fff)
        {
            _sprites.Write32(0, address - 0x170000, value, mask);
            SpriteWriteCount++;
            return;
        }
        if (address == 0x174010)
        {
            _sprites.Buffer(0);
            return;
        }
        if (address is >= 0x178000 and <= 0x179fff)
        {
            _sprites.Write32(1, address - 0x178000, value, mask);
            SpriteWriteCount++;
            return;
        }
        if (address == 0x17c010)
        {
            _sprites.Buffer(1);
            return;
        }
        if (address is >= 0x182000 and <= 0x185fff)
        {
            _tilemaps.WriteData32(0, address - 0x182000, value, mask);
            VideoWriteCount++;
            return;
        }
        if (address is >= 0x192000 and <= 0x195fff)
        {
            _tilemaps.WriteRowscroll32(0, address - 0x192000, value, mask);
            return;
        }
        if (address is >= 0x1a0000 and <= 0x1a001f)
        {
            _tilemaps.WriteControl32(0, address - 0x1a0000, value, mask);
            return;
        }
        if (address is >= 0x1c2000 and <= 0x1c5fff)
        {
            _tilemaps.WriteData32(1, address - 0x1c2000, value, mask);
            VideoWriteCount++;
            return;
        }
        if (address is >= 0x1d2000 and <= 0x1d5fff)
        {
            _tilemaps.WriteRowscroll32(1, address - 0x1d2000, value, mask);
            return;
        }
        if (address is >= 0x1e0000 and <= 0x1e001f)
        {
            _tilemaps.WriteControl32(1, address - 0x1e0000, value, mask);
            return;
        }
        if (address is >= 0x200000 and <= 0x207fff)
        {
            byte? soundLatch = _ioprot.Write(address - 0x200000, (ushort)(value >> 16), (ushort)(mask >> 16));
            if (soundLatch.HasValue)
                _soundCpu.SoundLatch = soundLatch.Value;
        }
    }

    private uint ReadProtection(uint address)
    {
        ushort high = _ioprot.Read(address - 0x200000, BuildInput0(), BuildInputB(), BuildInput1());
        return ((uint)high << 16) | 0xffffu;
    }

    private uint ReadWorkRam32(uint address)
    {
        MaybeApplyNightSlashersBackupGuard(address - 0x100000);
        uint value = ReadLe32(_workRam, (int)(address - 0x100000));
        if ((address & ~3u) == 0x100000)
            value &= 0xffffff7fu;
        if (address is >= 0x11fd00 and <= 0x11fd7f)
        {
            _lastWorkRamReadAddress = address;
            _lastWorkRamReadValue = value;
            _workRamReadProbeCount++;
        }
        return value;
    }

    private ushort BuildInput0()
    {
        ushort value = 0xffff;
        if (_input.Up) value &= unchecked((ushort)~0x0001);
        if (_input.Down) value &= unchecked((ushort)~0x0002);
        if (_input.Left) value &= unchecked((ushort)~0x0004);
        if (_input.Right) value &= unchecked((ushort)~0x0008);
        if (_input.A) value &= unchecked((ushort)~0x0010);
        if (_input.B) value &= unchecked((ushort)~0x0020);
        if (_input.C) value &= unchecked((ushort)~0x0040);
        if (_input.Start) value &= unchecked((ushort)~0x0080);
        return value;
    }

    private ushort BuildInput1()
    {
        ushort value = 0xffff;
        if (!_vblank) value &= unchecked((ushort)~0x0010);
        if (_input.X) value &= unchecked((ushort)~0x0001);
        if (_input.Y) value &= unchecked((ushort)~0x0004);
        return value;
    }

    private ushort BuildInputB()
        => (ushort)(_eeprom.DoRead ? 0x0001 : 0x0000);

    private string RamDebugSummary
    {
        get
        {
            byte b000 = _workRam[0x00000];
            byte b001 = _workRam[0x00001];
            byte b003 = _workRam[0x00003];
            byte b008 = _workRam[0x00008];
            byte b00c = _workRam[0x0000c];
            byte b014 = _workRam[0x00014];
            byte b034 = _workRam[0x00034];
            byte b035 = _workRam[0x00035];
            byte b036 = _workRam[0x00036];
            byte b038 = _workRam[0x00038];
            byte b039 = _workRam[0x00039];
            byte b03a = _workRam[0x0003a];
            byte b100 = _workRam[0x00100];
            byte sfd0 = _workRam[0x1fd00];
            byte sfd1 = _workRam[0x1fd01];
            byte sfd2 = _workRam[0x1fd02];
            byte sfd3 = _workRam[0x1fd03];
            byte sfd4 = _workRam[0x1fd04];
            byte sfd5 = _workRam[0x1fd05];
            byte sfd6 = _workRam[0x1fd06];
            byte sfd7 = _workRam[0x1fd07];
            int block0Sum = 0;
            int block1Sum = 0;
            for (int i = 0; i < 0x40; i++)
            {
                block0Sum += _workRam[0x1fd00 + i];
                block1Sum += _workRam[0x1fd40 + i];
            }
            uint magic0 = ReadLe32(_workRam, 0x1fd20);
            uint magic1 = ReadLe32(_workRam, 0x1fd60);
            ushort chk0 = ReadLe16(_workRam, 0x1fd3c);
            ushort chk1 = ReadLe16(_workRam, 0x1fd7c);
            return $"ram[000={b000:X2}/{b001:X2} 003={b003:X2} 008={b008:X2} 00c={b00c:X2} 014={b014:X2} 034={b034:X2}/{b035:X2}/{b036:X2} 038={b038:X2}/{b039:X2}/{b03a:X2} 100={b100:X2} 1fd00={sfd0:X2}{sfd1:X2}{sfd2:X2}{sfd3:X2}/{sfd4:X2}{sfd5:X2}{sfd6:X2}{sfd7:X2} bsum={block0Sum & 0xffff:X4}/{chk0:X4}:{magic0:X8},{block1Sum & 0xffff:X4}/{chk1:X4}:{magic1:X8} br=0x{_lastWorkRamReadAddress:X6}:0x{_lastWorkRamReadValue:X8}/{_workRamReadProbeCount} bguard={(_backupGuardApplied ? 1 : 0)}] {_eeprom.DebugSummary}";
        }
    }

    private void MaybeApplyNightSlashersBackupGuard(uint offset)
    {
        if (offset < 0x1fd00 || offset > 0x1fd7f)
            return;

        Span<byte> block = stackalloc byte[0x40];
        _workRam.AsSpan(0x1fd00, block.Length).CopyTo(block);
        if (NightSlashersBackupBlockIsValid(block))
            return;

        SerialEeprom93C46.BuildNightSlashersFactoryBlock(block);
        block.CopyTo(_workRam.AsSpan(0x1fd00, block.Length));
        block.CopyTo(_workRam.AsSpan(0x1fd40, block.Length));
        _backupGuardApplied = true;
    }

    private static bool NightSlashersBackupBlockIsValid(ReadOnlySpan<byte> block)
    {
        if (block.Length < 0x40)
            return false;
        if (block[0x20] != 0x30 || block[0x21] != 0x32 || block[0x22] != 0x4f || block[0x23] != 0x43)
            return false;

        int sum = 0;
        for (int i = 0; i < 0x40; i++)
            sum += block[i];
        ushort expected = (ushort)(block[0x3c] | (block[0x3d] << 8));
        return ((ushort)sum) == expected;
    }

    private static uint ReadMasked16(byte[] data, int offset)
        => ReadLe16(data, offset) | 0xffff0000u;

    private static uint ReadLe32(byte[] data, int offset)
    {
        if ((uint)(offset + 3) >= (uint)data.Length)
            return 0xffffffff;
        return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
    }

    private static ushort ReadLe16(byte[] data, int offset)
    {
        if ((uint)(offset + 1) >= (uint)data.Length)
            return 0xffff;
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    private static void WriteLe32(byte[] data, int offset, uint value, uint mask)
    {
        if ((uint)offset >= (uint)data.Length)
            return;
        for (int i = 0; i < 4 && offset + i < data.Length; i++)
        {
            if (((mask >> (i * 8)) & 0xff) != 0)
                data[offset + i] = (byte)(value >> (i * 8));
        }
    }

    private static int DecodeBootTextChar(byte value)
    {
        int ch = value & 0x7f;
        if (ch == 0 || ch == 0x7c)
            return ch == 0x7c ? 'C' : ' ';
        return ch;
    }

    private static void DrawDebugGlyph(byte[] fb, int width, int height, int stride, int x, int y, char ch)
    {
        ReadOnlySpan<byte> glyph = Glyph5x7(ch);
        for (int gy = 0; gy < glyph.Length; gy++)
        {
            int dy = y + gy;
            if ((uint)dy >= (uint)height)
                continue;
            byte bits = glyph[gy];
            for (int gx = 0; gx < 5; gx++)
            {
                if (((bits >> (4 - gx)) & 1) == 0)
                    continue;
                int dx = x + gx;
                if ((uint)dx >= (uint)width)
                    continue;
                int p = dy * stride + dx * 4;
                fb[p + 0] = 0xee;
                fb[p + 1] = 0xee;
                fb[p + 2] = 0xd8;
                fb[p + 3] = 0xff;
            }
        }
    }

    private static ReadOnlySpan<byte> Glyph5x7(char ch)
        => ch switch
        {
            'A' => [0x0e, 0x11, 0x11, 0x1f, 0x11, 0x11, 0x11],
            'B' => [0x1e, 0x11, 0x11, 0x1e, 0x11, 0x11, 0x1e],
            'C' => [0x0e, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0e],
            'D' => [0x1e, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1e],
            'E' => [0x1f, 0x10, 0x10, 0x1e, 0x10, 0x10, 0x1f],
            'F' => [0x1f, 0x10, 0x10, 0x1e, 0x10, 0x10, 0x10],
            'G' => [0x0e, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0e],
            'H' => [0x11, 0x11, 0x11, 0x1f, 0x11, 0x11, 0x11],
            'I' => [0x0e, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0e],
            'J' => [0x01, 0x01, 0x01, 0x01, 0x11, 0x11, 0x0e],
            'K' => [0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11],
            'L' => [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1f],
            'M' => [0x11, 0x1b, 0x15, 0x15, 0x11, 0x11, 0x11],
            'N' => [0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11],
            'O' => [0x0e, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0e],
            'P' => [0x1e, 0x11, 0x11, 0x1e, 0x10, 0x10, 0x10],
            'Q' => [0x0e, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0d],
            'R' => [0x1e, 0x11, 0x11, 0x1e, 0x14, 0x12, 0x11],
            'S' => [0x0f, 0x10, 0x10, 0x0e, 0x01, 0x01, 0x1e],
            'T' => [0x1f, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
            'U' => [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0e],
            'V' => [0x11, 0x11, 0x11, 0x11, 0x0a, 0x0a, 0x04],
            'W' => [0x11, 0x11, 0x11, 0x15, 0x15, 0x15, 0x0a],
            'X' => [0x11, 0x11, 0x0a, 0x04, 0x0a, 0x11, 0x11],
            'Y' => [0x11, 0x11, 0x0a, 0x04, 0x04, 0x04, 0x04],
            'Z' => [0x1f, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1f],
            '0' => [0x0e, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0e],
            '1' => [0x04, 0x0c, 0x04, 0x04, 0x04, 0x04, 0x0e],
            '2' => [0x0e, 0x11, 0x01, 0x02, 0x04, 0x08, 0x1f],
            '3' => [0x1e, 0x01, 0x01, 0x06, 0x01, 0x01, 0x1e],
            '4' => [0x02, 0x06, 0x0a, 0x12, 0x1f, 0x02, 0x02],
            '5' => [0x1f, 0x10, 0x1e, 0x01, 0x01, 0x11, 0x0e],
            '6' => [0x06, 0x08, 0x10, 0x1e, 0x11, 0x11, 0x0e],
            '7' => [0x1f, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
            '8' => [0x0e, 0x11, 0x11, 0x0e, 0x11, 0x11, 0x0e],
            '9' => [0x0e, 0x11, 0x11, 0x0f, 0x01, 0x02, 0x0c],
            '.' => [0x00, 0x00, 0x00, 0x00, 0x00, 0x0c, 0x0c],
            ',' => [0x00, 0x00, 0x00, 0x00, 0x0c, 0x04, 0x08],
            '!' => [0x04, 0x04, 0x04, 0x04, 0x04, 0x00, 0x04],
            '?' => [0x0e, 0x11, 0x01, 0x02, 0x04, 0x00, 0x04],
            '-' => [0x00, 0x00, 0x00, 0x1f, 0x00, 0x00, 0x00],
            '/' => [0x01, 0x01, 0x02, 0x04, 0x08, 0x10, 0x10],
            ':' => [0x00, 0x0c, 0x0c, 0x00, 0x0c, 0x0c, 0x00],
            '&' => [0x0c, 0x12, 0x14, 0x08, 0x15, 0x12, 0x0d],
            '(' => [0x02, 0x04, 0x08, 0x08, 0x08, 0x04, 0x02],
            ')' => [0x08, 0x04, 0x02, 0x02, 0x02, 0x04, 0x08],
            ' ' => [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            _ => [0x1f, 0x11, 0x15, 0x15, 0x11, 0x11, 0x1f]
        };
}

internal sealed class SerialEeprom93C46
{
    private readonly ushort[] _words = new ushort[64];
    private bool _cs;
    private bool _clk;
    private bool _di;
    private bool _do = true;
    private int _state;
    private int _command;
    private int _bits;
    private int _address;
    private int _outBits;
    private ushort _outShift;
    private bool _writeEnabled;
    private int _reads;
    private int _writes;
    private byte _lastData;

    public bool DoRead => _do;

    public void Reset()
    {
        Array.Fill(_words, (ushort)0xffff);
        LoadNightSlashersFactoryDefaults();
        _cs = false;
        _clk = false;
        _di = false;
        _do = true;
        _state = 0;
        _command = 0;
        _bits = 0;
        _address = 0;
        _outBits = 0;
        _outShift = 0xffff;
        _writeEnabled = false;
        _reads = 0;
        _writes = 0;
        _lastData = 0;
    }

    private void LoadNightSlashersFactoryDefaults()
    {
        Span<byte> block = stackalloc byte[0x40];
        BuildNightSlashersFactoryBlock(block);

        for (int copy = 0; copy < 2; copy++)
        {
            int baseOffset = copy * block.Length;
            for (int i = 0; i < block.Length; i += 2)
                _words[(baseOffset + i) >> 1] = (ushort)((block[i] << 8) | block[i + 1]);
        }
    }

    internal static void BuildNightSlashersFactoryBlock(Span<byte> block)
    {
        block.Clear();
        block[0x00] = 0x20;
        block[0x04] = 0x01;
        block[0x20] = 0x30;
        block[0x21] = 0x32;
        block[0x22] = 0x4f;
        block[0x23] = 0x43;

        int baseSum = 0;
        for (int i = 0; i < block.Length; i++)
        {
            if (i is >= 0x3c and <= 0x3f)
                continue;
            baseSum += block[i];
        }

        ushort checksum = (ushort)(baseSum + 0x1fe);
        block[0x3c] = (byte)checksum;
        block[0x3d] = (byte)(checksum >> 8);
        block[0x3e] = (byte)(0xff - (checksum >> 8));
        block[0x3f] = (byte)(0xff - (checksum & 0xff));
    }

    public void Write(byte data)
    {
        bool cs = (data & 0x40) != 0;
        bool clk = (data & 0x20) != 0;
        bool di = (data & 0x10) != 0;
        _lastData = data;

        if (_cs && !_clk && clk)
            Clock(_di);

        _clk = clk;
        _di = di;

        if (cs == _cs)
            return;

        if (!cs)
        {
            _cs = false;
            _state = 0;
            _bits = 0;
            _command = 0;
            _outBits = 0;
            _do = true;
            return;
        }

        _cs = true;
        _state = 0;
        _bits = 0;
        _command = 0;
        _outBits = 0;
        _do = true;
    }

    public string DebugSummary
        => $"eep[cs={(_cs ? 1 : 0)} clk={(_clk ? 1 : 0)} di={(_di ? 1 : 0)} do={(_do ? 1 : 0)} st={_state} cmd=0x{_command:X} a=0x{_address:X2} r={_reads} w={_writes} last=0x{_lastData:X2}]";

    private void Clock(bool bit)
    {
        if (_outBits > 0)
        {
            _do = (_outShift & 0x8000) != 0;
            _outShift <<= 1;
            _outBits--;
            return;
        }

        switch (_state)
        {
            case 0:
                if (bit)
                {
                    _state = 1;
                    _command = 0;
                    _bits = 0;
                }
                break;
            case 1:
                _command = (_command << 1) | (bit ? 1 : 0);
                if (++_bits == 2)
                {
                    _state = 2;
                    _address = 0;
                    _bits = 0;
                }
                break;
            case 2:
                _address = ((_address << 1) | (bit ? 1 : 0)) & 0x3f;
                if (++_bits == 6)
                    FinishAddress();
                break;
            case 3:
                _outShift = (ushort)((_outShift << 1) | (bit ? 1 : 0));
                if (++_bits == 16)
                {
                    if (_writeEnabled)
                    {
                        _words[_address] = _outShift;
                        _writes++;
                    }
                    _state = 4;
                    _do = true;
                }
                break;
        }
    }

    private void FinishAddress()
    {
        switch (_command)
        {
            case 0b10:
                _outShift = _words[_address];
                _outBits = 16;
                _do = false;
                _reads++;
                _state = 4;
                break;
            case 0b01:
                _outShift = 0;
                _bits = 0;
                _state = 3;
                break;
            case 0b11:
                if (_writeEnabled)
                {
                    _words[_address] = 0xffff;
                    _writes++;
                }
                _state = 4;
                _do = true;
                break;
            default:
                if ((_address & 0x30) == 0x30)
                    _writeEnabled = true;
                else if ((_address & 0x30) == 0x00)
                    _writeEnabled = false;
                _state = 4;
                _do = true;
                break;
        }
    }
}

internal sealed class Deco104Protection
{
    private const short InputPortA = -1;
    private const short InputPortB = -2;
    private const short InputPortC = -3;
    private const byte Blank = 0xff;
    private const byte ConfigRegion = 0x0c;

    private readonly ushort[][] _ram = { new ushort[0x80], new ushort[0x80] };
    private readonly byte[] _regionSelects = new byte[6];
    private ushort _xor;
    private ushort _nand;
    private ushort _latchAddress = 0xffff;
    private ushort _latchData;
    private bool _latchValid;
    private int _currentRamBank;
    private int _readCount;
    private int _writeCount;
    private int _configWriteCount;
    private ushort _lastReadAddress;
    private ushort _lastWriteAddress;
    private ushort _lastReadValue;
    private ushort _lastWriteData;
    private int _lastReadLocation;
    private ushort _lastReadRaw;
    private byte _lastReadFlags;
    private byte _lastCsFlags;
    private byte _lastUpper;

    public void Reset()
    {
        foreach (ushort[] bank in _ram)
            Array.Fill(bank, (ushort)0xffff);
        Array.Clear(_regionSelects);
        _xor = 0;
        _nand = 0;
        _latchAddress = 0xffff;
        _latchData = 0;
        _latchValid = false;
        _currentRamBank = 0;
        _readCount = 0;
        _writeCount = 0;
        _configWriteCount = 0;
        _lastReadAddress = 0;
        _lastWriteAddress = 0;
        _lastReadValue = 0;
        _lastWriteData = 0;
        _lastReadLocation = 0;
        _lastReadRaw = 0;
        _lastReadFlags = 0;
        _lastCsFlags = 0;
        _lastUpper = 0;
    }

    public ushort Read(uint byteOffset, ushort portA, ushort portB, ushort portC)
    {
        ushort address = DecodeCpuAddress(byteOffset);
        byte upper = (byte)((address >> 11) & 0x0f);
        _readCount++;
        _lastReadAddress = address;
        _lastUpper = upper;
        _lastCsFlags = 0;
        if (upper == ConfigRegion)
            return 0;

        for (int i = 0; i < _regionSelects.Length; i++)
        {
            if (_regionSelects[i] == upper)
            {
                _lastCsFlags |= (byte)(1 << i);
                _lastReadValue = i == 0 ? ReadProtectionPort((ushort)(address & 0x07ff), portA, portB, portC) : (ushort)0;
                return _lastReadValue;
            }
        }

        _lastReadValue = 0;
        return 0;
    }

    public byte? Write(uint byteOffset, ushort data, ushort mask)
    {
        ushort address = DecodeCpuAddress(byteOffset);
        byte upper = (byte)((address >> 11) & 0x0f);
        _writeCount++;
        _lastWriteAddress = address;
        _lastWriteData = data;
        _lastUpper = upper;
        _lastCsFlags = 0;
        if (upper == ConfigRegion)
        {
            int realAddress = address & 0x0f;
            if (realAddress >= 2 && realAddress <= 0x0c)
            {
                _regionSelects[(realAddress - 2) >> 1] = (byte)(data & 0x0f);
                _configWriteCount++;
            }
            return null;
        }

        byte? soundLatch = null;
        for (int i = 0; i < _regionSelects.Length; i++)
        {
            if (_regionSelects[i] == upper && i == 0)
            {
                _lastCsFlags |= (byte)(1 << i);
                soundLatch = WriteProtectionPort((ushort)(address & 0x07ff), data, mask);
            }
        }

        return soundLatch;
    }

    public string DebugSummary
        => string.Create(
            CultureInfo.InvariantCulture,
            $"protR={_readCount}/0x{_lastReadAddress:X4}=0x{_lastReadValue:X4}/raw0x{_lastReadRaw:X4}/loc{_lastReadLocation}/fl0x{_lastReadFlags:X1} protW={_writeCount}/0x{_lastWriteAddress:X4}=0x{_lastWriteData:X4} cfg={_configWriteCount} up=0x{_lastUpper:X1} cs=0x{_lastCsFlags:X2} bank={_currentRamBank} rs=[0x{_regionSelects[0]:X1},0x{_regionSelects[1]:X1},0x{_regionSelects[2]:X1},0x{_regionSelects[3]:X1},0x{_regionSelects[4]:X1},0x{_regionSelects[5]:X1}]");

    private ushort ReadProtectionPort(ushort address, ushort portA, ushort portB, ushort portC)
    {
        if (address == _latchAddress && _latchValid)
        {
            _latchValid = false;
            return _latchData;
        }

        _latchValid = false;
        ushort value = ReadDataGetLocation(address, portA, portB, portC, out int location);
        if (location == 0x66)
            _currentRamBank ^= 1;
        return value;
    }

    private byte? WriteProtectionPort(ushort address, ushort data, ushort mask)
    {
        _latchAddress = address;
        _latchData = data;
        _latchValid = true;

        byte? soundLatch = null;
        switch (address & 0xff)
        {
            case 0x42:
                Combine(ref _xor, data, mask);
                break;
            case 0xee:
                Combine(ref _nand, data, mask);
                break;
            case 0xa8:
                soundLatch = (byte)data;
                break;
        }

        WriteRam(address, data, mask);
        return soundLatch;
    }

    private ushort ReadDataGetLocation(ushort address, ushort portA, ushort portB, ushort portC, out int location)
    {
        int index = (address >> 1) & 0x3ff;
        short writeOffset = Deco32ProtTables.WriteOffsets[index];
        location = writeOffset;
        ushort value = writeOffset switch
        {
            InputPortA => portA,
            InputPortB => portB,
            InputPortC => portC,
            _ => _ram[_currentRamBank][(writeOffset >> 1) & 0x7f]
        };

        _lastReadLocation = location;
        _lastReadRaw = value;
        ushort result = Reorder(value, index);
        byte flags = Deco32ProtTables.Flags[index];
        _lastReadFlags = flags;
        if ((flags & 1) != 0)
            result ^= _xor;
        if ((flags & 2) != 0)
            result = (ushort)(result & ~_nand);
        return result;
    }

    private void WriteRam(ushort address, ushort data, ushort mask)
    {
        ref ushort slot = ref _ram[_currentRamBank][((address & 0xff) >> 1) & 0x7f];
        Combine(ref slot, data, mask);
    }

    private static void Combine(ref ushort target, ushort data, ushort mask)
        => target = (ushort)((target & ~mask) | (data & mask));

    private static ushort DecodeCpuAddress(uint byteOffset)
    {
        ushort realAddress = (ushort)(((byteOffset >> 2) * 2) & 0xffff);
        ushort decoAddress = (ushort)((((realAddress >> 14) & 0x0f) << 11) | (realAddress & 0x07ff));
        return BitswapInterleave(decoAddress);
    }

    private static ushort BitswapInterleave(ushort address)
    {
        ushort input = (ushort)(address >> 1);
        ReadOnlySpan<byte> swap = stackalloc byte[] { 9, 0, 8, 1, 7, 2, 6, 3, 5, 4 };
        ushort output = (ushort)(input & 0xfc00);
        for (int i = 0; i < swap.Length; i++)
            output |= (ushort)(((input >> swap[i]) & 1) << i);
        return (ushort)(output << 1);
    }

    private static ushort Reorder(ushort value, int index)
    {
        ReadOnlySpan<byte> map = Deco32ProtTables.MappingsFlat.Slice(index * 16, 16);
        ushort result = 0;
        for (int bit = 0; bit < 16; bit++)
        {
            byte target = map[bit];
            if (target != Blank && ((value >> bit) & 1) != 0)
                result |= (ushort)(1 << target);
        }

        return result;
    }
}

public sealed class DecoTilemapDevice
{
    private readonly NightSlashersGameProfile _profile;
    private readonly PaletteDevice _palette;
    private readonly ushort[][] _pf = { new ushort[0x800], new ushort[0x800], new ushort[0x800], new ushort[0x800] };
    private readonly ushort[][] _rowscroll = { new ushort[0x800], new ushort[0x800], new ushort[0x800], new ushort[0x800] };
    private readonly ushort[][] _control = { new ushort[0x10], new ushort[0x10] };
    private readonly int[] _nonzeroWriteCount = new int[4];
    private readonly int[] _nonzeroAttemptCount = new int[4];
    private readonly uint[] _lastDataOffset = new uint[4];
    private readonly ushort[] _lastDataValue = new ushort[4];
    private readonly uint[] _lastRawDataValue = new uint[4];
    private readonly uint[] _lastRawDataMask = new uint[4];

    public DecoTilemapDevice(NightSlashersGameProfile profile, PaletteDevice palette)
    {
        _profile = profile;
        _palette = palette;
    }

    public int ColorBank { get; set; }
    public string DebugSummary
    {
        get
        {
            Span<int> nonzero = stackalloc int[4];
            Span<ushort> first = stackalloc ushort[4];
            for (int layer = 0; layer < 4; layer++)
            {
                foreach (ushort tile in _pf[layer])
                {
                    if (tile == 0)
                        continue;
                    if (nonzero[layer] == 0)
                        first[layer] = tile;
                    nonzero[layer]++;
                }
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"pf0={nonzero[0]}/0x{first[0]:X4}/nw{_nonzeroWriteCount[0]}/na{_nonzeroAttemptCount[0]}/@0x{_lastDataOffset[0]:X4}=0x{_lastDataValue[0]:X4}/raw0x{_lastRawDataValue[0]:X8}m0x{_lastRawDataMask[0]:X8} pf1={nonzero[1]}/0x{first[1]:X4}/nw{_nonzeroWriteCount[1]}/na{_nonzeroAttemptCount[1]}/@0x{_lastDataOffset[1]:X4}=0x{_lastDataValue[1]:X4}/raw0x{_lastRawDataValue[1]:X8}m0x{_lastRawDataMask[1]:X8} pf2={nonzero[2]}/0x{first[2]:X4}/nw{_nonzeroWriteCount[2]}/na{_nonzeroAttemptCount[2]}/@0x{_lastDataOffset[2]:X4}=0x{_lastDataValue[2]:X4}/raw0x{_lastRawDataValue[2]:X8}m0x{_lastRawDataMask[2]:X8} pf3={nonzero[3]}/0x{first[3]:X4}/nw{_nonzeroWriteCount[3]}/na{_nonzeroAttemptCount[3]}/@0x{_lastDataOffset[3]:X4}=0x{_lastDataValue[3]:X4}/raw0x{_lastRawDataValue[3]:X8}m0x{_lastRawDataMask[3]:X8} cb=0x{ColorBank:X2} c0=[0x{_control[0][1]:X4},0x{_control[0][2]:X4},0x{_control[0][3]:X4},0x{_control[0][4]:X4},0x{_control[0][5]:X4},0x{_control[0][6]:X4}] c1=[0x{_control[1][1]:X4},0x{_control[1][2]:X4},0x{_control[1][3]:X4},0x{_control[1][4]:X4},0x{_control[1][5]:X4},0x{_control[1][6]:X4}]");
        }
    }

    public void Reset()
    {
        foreach (ushort[] ram in _pf) Array.Clear(ram);
        foreach (ushort[] ram in _rowscroll) Array.Clear(ram);
        foreach (ushort[] ram in _control) Array.Clear(ram);
        Array.Clear(_nonzeroWriteCount);
        Array.Clear(_nonzeroAttemptCount);
        Array.Clear(_lastDataOffset);
        Array.Clear(_lastDataValue);
        Array.Clear(_lastRawDataValue);
        Array.Clear(_lastRawDataMask);
        ColorBank = 0;
    }

    public uint ReadData32(int chip, uint offset) => ReadLow16Dword(_pf[chip * 2 + ((offset >> 13) & 1)], offset);
    public void WriteData32(int chip, uint offset, uint value, uint mask)
    {
        int layer = chip * 2 + (int)((offset >> 13) & 1);
        _lastRawDataValue[layer] = value;
        _lastRawDataMask[layer] = mask;
        if ((value & mask) != 0)
            _nonzeroAttemptCount[layer]++;
        if (WriteLow16Dword(_pf[layer], offset, value, mask, out ushort data))
        {
            _lastDataOffset[layer] = offset;
            _lastDataValue[layer] = data;
            if (data != 0)
                _nonzeroWriteCount[layer]++;
        }
    }
    public uint ReadRowscroll32(int chip, uint offset) => ReadLow16Dword(_rowscroll[chip * 2 + ((offset >> 13) & 1)], offset);
    public void WriteRowscroll32(int chip, uint offset, uint value, uint mask) => WriteLow16Dword(_rowscroll[chip * 2 + ((offset >> 13) & 1)], offset, value, mask, out _);
    public uint ReadControl32(int chip, uint offset) => ReadLow16Dword(_control[chip], offset);
    public void WriteControl32(int chip, uint offset, uint value, uint mask) => WriteLow16Dword(_control[chip], offset, value, mask, out _);

    public void Render(byte[] fb, int width, int height, int stride)
    {
        RenderLayer(fb, width, height, stride, 3, _profile.Tiles2, 0x30, opaque: true);
        RenderLayer(fb, width, height, stride, 2, _profile.Tiles2, 0x20, opaque: false);
        RenderLayer(fb, width, height, stride, 1, _profile.Tiles1, 0x10, opaque: false);
        RenderLayer(fb, width, height, stride, 0, _profile.Tiles1, 0x00, opaque: false, charMode: true);
    }

    private void RenderLayer(byte[] fb, int width, int height, int stride, int layer, byte[] gfx, int colorBase, bool opaque, bool charMode = false)
    {
        ushort[] ram = _pf[layer];
        ushort[] ctrl = _control[layer >> 1];
        int scrollX = ctrl[(layer & 1) == 0 ? 1 : 3] & 0x1ff;
        int scrollY = ctrl[(layer & 1) == 0 ? 2 : 4] & 0x1ff;
        int tileSize = charMode ? 8 : 16;
        int mapCols = 64;
        int mapRows = 32;
        int palBase = colorBase + ((ColorBank & 0xf) << 4);

        for (int y = 0; y < height; y++)
        {
            int sy = (y + scrollY) & (mapRows * tileSize - 1);
            int ty = sy / tileSize;
            int py = sy & (tileSize - 1);
            for (int x = 0; x < width; x++)
            {
                int sx = (x + scrollX) & (mapCols * tileSize - 1);
                int tx = sx / tileSize;
                int px = sx & (tileSize - 1);
                int entry = (ty * mapCols + tx) & 0x7ff;
                ushort tile = ram[entry & (ram.Length - 1)];
                int pen = charMode ? Decode4BppChar(gfx, tile & 0x0fff, px, py) : Decode4BppTile(gfx, ApplyBank(tile), px, py);
                if (pen == 0 && !opaque)
                    continue;
                int color = palBase + ((tile >> 12) & 0x0f);
                _palette.WritePixel(fb, stride, x, y, color * 16 + pen);
            }
        }
    }

    private static int ApplyBank(ushort tile)
        => tile & 0x0fff;

    private static int Decode4BppChar(byte[] rom, int code, int x, int y)
    {
        int half = rom.Length / 2;
        int baseBit = (code % Math.Max(1, half / 64)) * 16 * 8 + y * 16 + x;
        int p0 = ReadBit(rom, baseBit);
        int p1 = ReadBit(rom, baseBit + 8);
        int p2 = ReadBit(rom, half * 8 + baseBit);
        int p3 = ReadBit(rom, half * 8 + baseBit + 8);
        return p0 | (p1 << 1) | (p2 << 2) | (p3 << 3);
    }

    private static int Decode4BppTile(byte[] rom, int code, int x, int y)
    {
        int half = rom.Length / 2;
        int tiles = Math.Max(1, half / 256);
        int baseBit = (code % tiles) * 64 * 8 + y * 16 + (x < 8 ? 128 : 0) + (x & 7);
        int p0 = ReadBit(rom, baseBit);
        int p1 = ReadBit(rom, baseBit + 8);
        int p2 = ReadBit(rom, half * 8 + baseBit);
        int p3 = ReadBit(rom, half * 8 + baseBit + 8);
        return p0 | (p1 << 1) | (p2 << 2) | (p3 << 3);
    }

    private static int ReadBit(byte[] data, int bit)
    {
        int byteOffset = bit >> 3;
        if ((uint)byteOffset >= (uint)data.Length)
            return 0;
        return (data[byteOffset] >> (7 - (bit & 7))) & 1;
    }

    private static uint ReadLow16Dword(ushort[] ram, uint offset)
    {
        int word = (int)(offset >> 2) & (ram.Length - 1);
        return 0xffff0000u | ram[word];
    }

    private static bool WriteLow16Dword(ushort[] ram, uint offset, uint value, uint mask, out ushort data)
    {
        int word = (int)(offset >> 2) & (ram.Length - 1);
        data = 0;
        if ((mask & 0x0000ffff) == 0)
            return false;

        data = (ushort)value;
        ram[word] = data;
        return true;
    }
}

public sealed class DecoSpriteDevice
{
    private readonly NightSlashersGameProfile _profile;
    private readonly PaletteDevice _palette;
    private readonly ushort[][] _ram = { new ushort[0x1000], new ushort[0x1000] };
    private readonly ushort[][] _buffered = { new ushort[0x1000], new ushort[0x1000] };

    public DecoSpriteDevice(NightSlashersGameProfile profile, PaletteDevice palette)
    {
        _profile = profile;
        _palette = palette;
    }

    public int ColorBank0 { get; set; }
    public int ColorBank1 { get; set; }
    public string DebugSummary
    {
        get
        {
            Span<int> nonzero = stackalloc int[2];
            Span<ushort> firstY = stackalloc ushort[2];
            Span<ushort> firstCode = stackalloc ushort[2];
            Span<ushort> firstX = stackalloc ushort[2];
            for (int list = 0; list < 2; list++)
            {
                ushort[] ram = _buffered[list];
                for (int offs = 0; offs + 3 < ram.Length; offs += 4)
                {
                    if ((ram[offs] | ram[offs + 1] | ram[offs + 2] | ram[offs + 3]) == 0)
                        continue;
                    if (nonzero[list] == 0)
                    {
                        firstY[list] = ram[offs];
                        firstCode[list] = ram[offs + 1];
                        firstX[list] = ram[offs + 2];
                    }
                    nonzero[list]++;
                }
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"spr0={nonzero[0]}/y0x{firstY[0]:X4}c0x{firstCode[0]:X4}x0x{firstX[0]:X4} spr1={nonzero[1]}/y0x{firstY[1]:X4}c0x{firstCode[1]:X4}x0x{firstX[1]:X4}");
        }
    }

    public void Reset()
    {
        foreach (ushort[] ram in _ram) Array.Clear(ram);
        foreach (ushort[] ram in _buffered) Array.Clear(ram);
        ColorBank0 = ColorBank1 = 0;
    }

    public uint Read32(int list, uint offset) => ReadLow16Dword(_ram[list], offset);
    public void Write32(int list, uint offset, uint value, uint mask) => WriteLow16Dword(_ram[list], offset, value, mask);
    public void Buffer() { Buffer(0); Buffer(1); }
    public void Buffer(int list) => Array.Copy(_ram[list], _buffered[list], _ram[list].Length);

    public void Render(byte[] fb, int width, int height, int stride, long frame)
    {
        RenderList(fb, width, height, stride, _buffered[1], _profile.Sprites2, false, ColorBank1, frame);
        RenderList(fb, width, height, stride, _buffered[0], _profile.Sprites1, true, ColorBank0, frame);
    }

    private void RenderList(byte[] fb, int width, int height, int stride, ushort[] spr, byte[] gfx, bool fiveBpp, int colorBank, long frame)
    {
        for (int offs = 0; offs + 3 < spr.Length; offs += 4)
        {
            ushort yraw = spr[offs];
            if (((yraw >> 12) & 1) != 0 && (frame & 1) != 0)
                continue;
            int sprite = spr[offs + 1];
            int xraw = spr[offs + 2];
            int color = ((xraw >> 9) & 0x7f) + ((colorBank & 0xf) << 4);
            bool fx = (yraw & 0x2000) != 0;
            bool fy = (yraw & 0x4000) != 0;
            int multi = (1 << (((yraw >> 9) & 1) | (((yraw >> 10) & 1) << 1))) - 1;
            sprite &= ~multi;
            int x = xraw & 0x1ff;
            int y = yraw & 0x1ff;
            if (x >= 320) x -= 512;
            if (y >= 256) y -= 512;
            y = 240 - y;
            x = 304 - x;
            int inc = fy ? -1 : 1;
            if (!fy) sprite += multi;
            for (int m = multi; m >= 0; m--)
            {
                int tile = sprite - m * inc;
                int dy = y - 16 * m;
                DrawSpriteTile(fb, width, height, stride, gfx, fiveBpp, tile, color, x, dy, fx, fy);
            }
        }
    }

    private void DrawSpriteTile(byte[] fb, int width, int height, int stride, byte[] gfx, bool fiveBpp, int code, int color, int sx, int sy, bool fx, bool fy)
    {
        for (int y = 0; y < 16; y++)
        {
            int dy = sy + y;
            if ((uint)dy >= (uint)height)
                continue;
            int py = fy ? 15 - y : y;
            for (int x = 0; x < 16; x++)
            {
                int dx = sx + x;
                if ((uint)dx >= (uint)width)
                    continue;
                int px = fx ? 15 - x : x;
                int pen = fiveBpp ? Decode5Bpp(gfx, code, px, py) : Decode4Bpp(gfx, code, px, py);
                if (pen == 0)
                    continue;
                _palette.WritePixel(fb, stride, dx, dy, color * 16 + pen);
            }
        }
    }

    private static int Decode4Bpp(byte[] rom, int code, int x, int y)
    {
        int half = rom.Length / 2;
        int tiles = Math.Max(1, half / 256);
        int baseBit = (code % tiles) * 64 * 8 + y * 16 + (x < 8 ? 128 : 0) + (x & 7);
        return ReadBit(rom, baseBit) | (ReadBit(rom, baseBit + 8) << 1) | (ReadBit(rom, half * 8 + baseBit) << 2) | (ReadBit(rom, half * 8 + baseBit + 8) << 3);
    }

    private static int Decode5Bpp(byte[] rom, int code, int x, int y)
    {
        int tiles = Math.Max(1, rom.Length / (16 * 16 * 5 / 8));
        int baseBit = (code % tiles) * 16 * 16 * 5 + y * 16 * 5 + (x < 8 ? 16 * 8 * 5 : 0) + (x & 7);
        return ReadBit(rom, baseBit) | (ReadBit(rom, baseBit + 8) << 1) | (ReadBit(rom, baseBit + 16) << 2) | (ReadBit(rom, baseBit + 24) << 3) | (ReadBit(rom, baseBit + 32) << 4);
    }

    private static int ReadBit(byte[] data, int bit)
    {
        int byteOffset = bit >> 3;
        if ((uint)byteOffset >= (uint)data.Length)
            return 0;
        return (data[byteOffset] >> (7 - (bit & 7))) & 1;
    }

    private static uint ReadLow16Dword(ushort[] ram, uint offset)
    {
        int word = (int)(offset >> 2) & (ram.Length - 1);
        return 0xffff0000u | ram[word];
    }

    private static void WriteLow16Dword(ushort[] ram, uint offset, uint value, uint mask)
    {
        if ((mask & 0x0000ffff) == 0)
            return;
        int word = (int)(offset >> 2) & (ram.Length - 1);
        ram[word] = (ushort)value;
    }
}

public sealed class PaletteDevice
{
    private readonly uint[] _colors = new uint[2048];
    private readonly ushort[] _ram = new ushort[0x1000];

    public void Reset()
    {
        Array.Clear(_ram);
        for (int i = 0; i < _colors.Length; i++)
        {
            int r = (i * 37) & 0xff;
            int g = (i * 67) & 0xff;
            int b = (i * 101) & 0xff;
            _colors[i] = 0xff000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
        }
    }

    public uint Read32(uint offset) => ReadWordPair(_ram, offset);

    public void Write32(uint offset, uint value, uint mask)
    {
        int word = (int)(offset >> 1) & (_ram.Length - 1);
        if ((mask & 0x0000ffff) != 0) Set(word, (ushort)value);
        if ((mask & 0xffff0000) != 0) Set((word + 1) & (_ram.Length - 1), (ushort)(value >> 16));
    }

    public void FillBackdrop(byte[] fb, int width, int height, int stride)
    {
        uint color = _colors[0];
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
                WriteBgra(fb, row + x * 4, color);
        }
    }

    public void WritePixel(byte[] fb, int stride, int x, int y, int paletteIndex)
    {
        uint color = _colors[paletteIndex & (_colors.Length - 1)];
        WriteBgra(fb, y * stride + x * 4, color);
    }

    private void Set(int index, ushort value)
    {
        _ram[index] = value;
        int r = (value & 0x001f) << 3;
        int g = ((value >> 5) & 0x001f) << 3;
        int b = ((value >> 10) & 0x001f) << 3;
        _colors[index & (_colors.Length - 1)] = 0xff000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
    }

    private static void WriteBgra(byte[] fb, int offset, uint argb)
    {
        fb[offset] = (byte)argb;
        fb[offset + 1] = (byte)(argb >> 8);
        fb[offset + 2] = (byte)(argb >> 16);
        fb[offset + 3] = 0xff;
    }

    private static uint ReadWordPair(ushort[] ram, uint offset)
    {
        int word = (int)(offset >> 1) & (ram.Length - 1);
        return (uint)(ram[word] | (ram[(word + 1) & (ram.Length - 1)] << 16));
    }
}

public sealed class Z80SoundCpu
{
    private readonly byte[] _rom;
    public Z80SoundCpu(byte[] rom) => _rom = rom;
    public byte SoundLatch { get; set; }
    public void Reset() => SoundLatch = 0;
    public void RunFrame() { _ = _rom; }
}

public sealed class YM2151
{
    public void Reset() { }
    public byte ReadStatus() => 0;
    public void WriteRegister(byte value) { _ = value; }
    public void WriteData(byte value) { _ = value; }
}

public sealed class OKI6295
{
    private readonly byte[] _rom;
    public OKI6295(byte[] rom) => _rom = rom;
    public void Reset() { }
    public byte ReadStatus() => 0xf0;
    public void Write(byte value) { _ = value; _ = _rom; }
}

public readonly record struct ArcadeInputState(
    bool Up,
    bool Down,
    bool Left,
    bool Right,
    bool A,
    bool B,
    bool C,
    bool Start,
    bool X,
    bool Y,
    bool Z,
    bool Mode);

public interface IArm6Bus
{
    uint Read32(uint address);
    void Write32(uint address, uint value, uint mask);
}

public sealed class Arm6Cpu
{
    private IArm6Bus? _bus;
    private bool _flagN;
    private bool _flagZ;
    private bool _flagC;
    private bool _flagV;
    private bool _irqDisable = true;
    private bool _fiqDisable = true;
    private bool _irqLine;
    private uint _savedStatus;
    private uint _spsrIrq;
    private uint _svcR13;
    private uint _svcR14;
    private uint _irqR13;
    private uint _irqR14;
    private uint _userR13;
    private uint _userR14;
    private byte _mode = 0x13;

    public uint[] Registers { get; } = new uint[16];
    public bool Halted { get; private set; }
    public string StopReason { get; private set; } = string.Empty;
    public uint Pc => Registers[15];
    public uint Cpsr => (_flagN ? 0x80000000u : 0) | (_flagZ ? 0x40000000u : 0) | (_flagC ? 0x20000000u : 0) | (_flagV ? 0x10000000u : 0) | (_irqDisable ? 0x80u : 0) | (_fiqDisable ? 0x40u : 0) | _mode;

    public void Reset(IArm6Bus bus)
    {
        _bus = bus;
        Array.Clear(Registers);
        Registers[15] = 0;
        _flagN = _flagZ = _flagC = _flagV = false;
        _irqDisable = true;
        _fiqDisable = true;
        _irqLine = false;
        _mode = 0x13;
        _savedStatus = Cpsr;
        _spsrIrq = 0;
        _svcR13 = _svcR14 = _irqR13 = _irqR14 = _userR13 = _userR14 = 0;
        Halted = false;
        StopReason = string.Empty;
    }

    public void SetIrqLine(bool asserted) => _irqLine = asserted;
    public uint PeekOpcode() => _bus?.Read32(Registers[15] & ~3u) ?? 0xffffffff;

    public int ExecuteInstruction()
    {
        if (_bus is null)
        {
            Halt("no bus");
            return 1;
        }

        if (_irqLine && !_irqDisable)
        {
            _spsrIrq = Cpsr;
            ChangeMode(0x12);
            Registers[14] = Registers[15] + 4;
            Registers[15] = 0x18;
            _irqDisable = true;
            return 3;
        }

        uint pc = Registers[15] & ~3u;
        uint op = _bus.Read32(pc);
        Registers[15] = pc + 8;
        if (!ConditionPassed(op >> 28))
        {
            Registers[15] = pc + 4;
            return 1;
        }

        int cycles;
        if ((op & 0x0e000000) == 0x0a000000)
            cycles = Branch(op, pc);
        else if ((op & 0x0c000000) == 0x04000000)
            cycles = SingleDataTransfer(op);
        else if ((op & 0x0e000000) == 0x08000000)
            cycles = BlockTransfer(op);
        else if ((op & 0x0e000090) == 0x00000090 && (op & 0x00000060) != 0)
            cycles = HalfwordTransfer(op);
        else if ((op & 0x0fc000f0) == 0x00000090)
            cycles = Multiply(op);
        else if ((op & 0x0c000000) == 0)
            cycles = DataProcessing(op);
        else if ((op & 0x0f000000) == 0x0f000000)
            cycles = 8;
        else
        {
            Halt($"unimplemented opcode 0x{op:X8} at 0x{pc:X8}");
            cycles = 1;
        }

        if (!Halted && Registers[15] == pc + 8)
            Registers[15] = pc + 4;
        return cycles;
    }

    private int Branch(uint op, uint pc)
    {
        if ((op & 0x01000000) != 0)
            Registers[14] = pc + 4;
        int disp = (int)(op & 0x00ffffff);
        if ((disp & 0x00800000) != 0)
            disp |= unchecked((int)0xff000000);
        Registers[15] = (uint)(pc + 8 + (disp << 2));
        return 3;
    }

    private int DataProcessing(uint op)
    {
        int opcode = (int)((op >> 21) & 0xf);
        bool setFlags = (op & 0x00100000) != 0;
        int rn = (int)((op >> 16) & 0xf);
        int rd = (int)((op >> 12) & 0xf);

        // Night Slashers uses old ARM6/26-bit style startup code:
        //   BICS pc, pc, #3
        // to leave supervisor init and continue with normal IRQ-visible code.
        // Treat it as the narrow mode transition it is for this board.
        if (op == 0xe3dff003)
        {
            Registers[15] &= ~3u;
            _irqDisable = false;
            _fiqDisable = false;
            ChangeMode(0x10);
            return 1;
        }

        if ((op & 0x0fbf0fff) == 0x010f0000)
        {
            Registers[rd] = Cpsr;
            return 1;
        }
        if ((op & 0x0db0f000) == 0x0120f000)
        {
            SetCpsr(Operand2(op, out _));
            return 1;
        }

        uint a = ReadReg(rn);
        uint b = Operand2(op, out bool shifterCarry);
        uint result;
        switch (opcode)
        {
            case 0x0: result = a & b; WriteReg(rd, result); if (setFlags) SetDataFlags(rd, result, shifterCarry); break;
            case 0x1: result = a ^ b; WriteReg(rd, result); if (setFlags) SetDataFlags(rd, result, shifterCarry); break;
            case 0x2: result = a - b; WriteReg(rd, result); if (setFlags) SetDataFlags(rd, () => SetSubFlags(a, b, result)); break;
            case 0x3: result = b - a; WriteReg(rd, result); if (setFlags) SetDataFlags(rd, () => SetSubFlags(b, a, result)); break;
            case 0x4: result = a + b; WriteReg(rd, result); if (setFlags) SetDataFlags(rd, () => SetAddFlags(a, b, result)); break;
            case 0x5: result = a + b + (_flagC ? 1u : 0u); WriteReg(rd, result); if (setFlags) SetDataFlags(rd, () => SetAddFlags(a, b + (_flagC ? 1u : 0u), result)); break;
            case 0x6: result = a - b - (_flagC ? 0u : 1u); WriteReg(rd, result); if (setFlags) SetDataFlags(rd, () => SetSubFlags(a, b + (_flagC ? 0u : 1u), result)); break;
            case 0x8: result = a & b; SetLogicFlags(result, shifterCarry); break;
            case 0x9: result = a ^ b; SetLogicFlags(result, shifterCarry); break;
            case 0xa: result = a - b; SetSubFlags(a, b, result); break;
            case 0xb: result = a + b; SetAddFlags(a, b, result); break;
            case 0xc: result = a | b; WriteReg(rd, result); if (setFlags) SetDataFlags(rd, result, shifterCarry); break;
            case 0xd: result = b; WriteReg(rd, result); if (setFlags) SetDataFlags(rd, result, shifterCarry); break;
            case 0xe: result = a & ~b; WriteReg(rd, result); if (setFlags) SetDataFlags(rd, result, shifterCarry); break;
            case 0xf: result = ~b; WriteReg(rd, result); if (setFlags) SetDataFlags(rd, result, shifterCarry); break;
            default: Halt($"unimplemented data op {opcode:X}"); break;
        }
        return 1;
    }

    private int Multiply(uint op)
    {
        int rd = (int)((op >> 16) & 0xf);
        int rn = (int)((op >> 12) & 0xf);
        int rs = (int)((op >> 8) & 0xf);
        int rm = (int)(op & 0xf);
        bool accumulate = (op & 0x00200000) != 0;
        bool setFlags = (op & 0x00100000) != 0;
        uint result = ReadReg(rm) * ReadReg(rs);
        if (accumulate)
            result += ReadReg(rn);
        WriteReg(rd, result);
        if (setFlags)
        {
            _flagN = (result & 0x80000000) != 0;
            _flagZ = result == 0;
        }
        return accumulate ? 4 : 3;
    }

    private int HalfwordTransfer(uint op)
    {
        bool pre = (op & 0x01000000) != 0;
        bool up = (op & 0x00800000) != 0;
        bool immediate = (op & 0x00400000) != 0;
        bool writeback = (op & 0x00200000) != 0;
        bool load = (op & 0x00100000) != 0;
        int rn = (int)((op >> 16) & 0xf);
        int rd = (int)((op >> 12) & 0xf);
        int mode = (int)((op >> 5) & 3);
        uint offset = immediate
            ? (((op >> 4) & 0xf0) | (op & 0x0f))
            : ReadReg((int)(op & 0xf));
        uint baseAddr = ReadReg(rn);
        uint address = pre ? (up ? baseAddr + offset : baseAddr - offset) : baseAddr;
        uint newBase = up ? baseAddr + offset : baseAddr - offset;

        if (load)
        {
            uint value = mode switch
            {
                1 => Read16(address),
                2 => (uint)(sbyte)Read8(address),
                3 => (uint)(short)Read16(address),
                _ => 0xffffffffu
            };
            WriteReg(rd, value);
        }
        else
        {
            if (mode == 1)
                Write16(address, (ushort)ReadReg(rd));
            else
                Halt($"unsupported halfword store mode {mode}");
        }

        if (!pre || writeback)
            WriteReg(rn, newBase);
        return load ? 3 : 2;
    }

    private int SingleDataTransfer(uint op)
    {
        bool immediateShift = (op & 0x02000000) == 0;
        bool pre = (op & 0x01000000) != 0;
        bool up = (op & 0x00800000) != 0;
        bool byteAccess = (op & 0x00400000) != 0;
        bool writeback = (op & 0x00200000) != 0;
        bool load = (op & 0x00100000) != 0;
        int rn = (int)((op >> 16) & 0xf);
        int rd = (int)((op >> 12) & 0xf);
        uint offset = immediateShift ? op & 0xfff : Operand2(op, out _);
        uint baseAddr = ReadReg(rn);
        uint address = pre ? (up ? baseAddr + offset : baseAddr - offset) : baseAddr;
        uint newBase = up ? baseAddr + offset : baseAddr - offset;

        if (load)
        {
            uint value = byteAccess ? Read8(address) : _bus!.Read32(address & ~3u);
            if (!byteAccess && (address & 3) != 0)
                value = Ror(value, (int)((address & 3) * 8));
            WriteReg(rd, value);
        }
        else
        {
            uint value = ReadReg(rd);
            if (byteAccess)
                Write8(address, (byte)value);
            else
                _bus!.Write32(address & ~3u, value, 0xffffffff);
        }

        if (!pre || writeback)
            WriteReg(rn, newBase);
        return load ? 3 : 2;
    }

    private int BlockTransfer(uint op)
    {
        bool pre = (op & 0x01000000) != 0;
        bool up = (op & 0x00800000) != 0;
        bool writeback = (op & 0x00200000) != 0;
        bool load = (op & 0x00100000) != 0;
        int rn = (int)((op >> 16) & 0xf);
        uint list = op & 0xffff;
        uint baseAddr = ReadReg(rn);
        int count = 0;
        for (int i = 0; i < 16; i++) if (((list >> i) & 1) != 0) count++;
        uint address = up ? baseAddr : baseAddr - (uint)(count * 4);
        if (pre && up) address += 4;
        if (!pre && !up) address += 4;
        for (int i = 0; i < 16; i++)
        {
            if (((list >> i) & 1) == 0)
                continue;
            if (load) WriteReg(i, _bus!.Read32(address));
            else _bus!.Write32(address, ReadReg(i), 0xffffffff);
            address += 4;
        }
        if (writeback)
            WriteReg(rn, up ? baseAddr + (uint)(count * 4) : baseAddr - (uint)(count * 4));
        return 2 + count;
    }

    private uint Operand2(uint op, out bool carry)
    {
        if ((op & 0x02000000) != 0)
        {
            int imm = (int)(op & 0xff);
            int rot = (int)((op >> 7) & 0x1e);
            uint immediate = Ror((uint)imm, rot);
            carry = rot == 0 ? _flagC : (immediate & 0x80000000) != 0;
            return immediate;
        }

        uint value = ReadReg((int)(op & 0xf));
        int type = (int)((op >> 5) & 3);
        bool registerShift = (op & 0x10) != 0;
        int amount = registerShift ? (int)(ReadReg((int)((op >> 8) & 0xf)) & 0xff) : (int)((op >> 7) & 0x1f);
        if (amount == 0)
        {
            if (!registerShift)
            {
                switch (type)
                {
                    case 1:
                        carry = (value & 0x80000000) != 0;
                        return 0;
                    case 2:
                        carry = (value & 0x80000000) != 0;
                        return carry ? 0xffffffffu : 0;
                    case 3:
                        carry = (value & 1) != 0;
                        return (_flagC ? 0x80000000u : 0u) | (value >> 1);
                }
            }
            carry = _flagC;
            return value;
        }
        return type switch
        {
            0 => Lsl(value, amount, out carry),
            1 => Lsr(value, amount, out carry),
            2 => Asr(value, amount, out carry),
            _ => RorWithCarry(value, amount, out carry)
        };
    }

    private uint ReadReg(int reg) => reg == 15 ? Registers[15] : Registers[reg];
    private void WriteReg(int reg, uint value) => Registers[reg] = reg == 15 ? value & ~3u : value;

    private byte Read8(uint address)
    {
        uint aligned = _bus!.Read32(address & ~3u);
        return (byte)(aligned >> (int)((address & 3) * 8));
    }

    private void Write8(uint address, byte value)
    {
        int shift = (int)((address & 3) * 8);
        _bus!.Write32(address & ~3u, (uint)value << shift, 0xffu << shift);
    }

    private ushort Read16(uint address)
    {
        uint aligned = _bus!.Read32(address & ~3u);
        int shift = (int)((address & 2) * 8);
        return (ushort)(aligned >> shift);
    }

    private void Write16(uint address, ushort value)
    {
        int shift = (int)((address & 2) * 8);
        _bus!.Write32(address & ~3u, (uint)value << shift, 0xffffu << shift);
    }

    private bool ConditionPassed(uint cond) => cond switch
    {
        0x0 => _flagZ,
        0x1 => !_flagZ,
        0x2 => _flagC,
        0x3 => !_flagC,
        0x4 => _flagN,
        0x5 => !_flagN,
        0x6 => _flagV,
        0x7 => !_flagV,
        0x8 => _flagC && !_flagZ,
        0x9 => !_flagC || _flagZ,
        0xa => _flagN == _flagV,
        0xb => _flagN != _flagV,
        0xc => !_flagZ && _flagN == _flagV,
        0xd => _flagZ || _flagN != _flagV,
        0xe => true,
        _ => false
    };

    private void SetCpsr(uint value)
    {
        _flagN = (value & 0x80000000) != 0;
        _flagZ = (value & 0x40000000) != 0;
        _flagC = (value & 0x20000000) != 0;
        _flagV = (value & 0x10000000) != 0;
        _irqDisable = (value & 0x80) != 0;
        _fiqDisable = (value & 0x40) != 0;
        ChangeMode((byte)(value & 0x1f));
    }

    private void SetDataFlags(int rd, uint result, bool carry)
    {
        if (rd == 15)
            RestoreSavedStatusForPcWrite();
        else
            SetLogicFlags(result, carry);
    }

    private void SetDataFlags(int rd, Action update)
    {
        if (rd == 15)
            RestoreSavedStatusForPcWrite();
        else
            update();
    }

    private void RestoreSavedStatusForPcWrite()
    {
        if (_mode == 0x12)
            SetCpsr(_spsrIrq);
    }

    private void ChangeMode(byte newMode)
    {
        newMode = newMode switch
        {
            0x10 or 0x11 or 0x12 or 0x13 or 0x17 or 0x1b or 0x1f => newMode,
            _ => _mode
        };
        if (newMode == _mode)
            return;

        SaveBankedR13R14(_mode);
        _mode = newMode;
        LoadBankedR13R14(_mode);
    }

    private void SaveBankedR13R14(byte mode)
    {
        switch (mode)
        {
            case 0x12:
                _irqR13 = Registers[13];
                _irqR14 = Registers[14];
                break;
            case 0x13:
                _svcR13 = Registers[13];
                _svcR14 = Registers[14];
                break;
            default:
                _userR13 = Registers[13];
                _userR14 = Registers[14];
                break;
        }
    }

    private void LoadBankedR13R14(byte mode)
    {
        switch (mode)
        {
            case 0x12:
                Registers[13] = _irqR13;
                Registers[14] = _irqR14;
                break;
            case 0x13:
                Registers[13] = _svcR13;
                Registers[14] = _svcR14;
                break;
            default:
                Registers[13] = _userR13;
                Registers[14] = _userR14;
                break;
        }
    }

    private void SetLogicFlags(uint result, bool carry)
    {
        _flagN = (result & 0x80000000) != 0;
        _flagZ = result == 0;
        _flagC = carry;
    }

    private void SetAddFlags(uint a, uint b, uint result)
    {
        _flagN = (result & 0x80000000) != 0;
        _flagZ = result == 0;
        _flagC = result < a;
        _flagV = ((a ^ result) & (b ^ result) & 0x80000000) != 0;
    }

    private void SetSubFlags(uint a, uint b, uint result)
    {
        _flagN = (result & 0x80000000) != 0;
        _flagZ = result == 0;
        _flagC = a >= b;
        _flagV = ((a ^ b) & (a ^ result) & 0x80000000) != 0;
    }

    private void Halt(string reason)
    {
        Halted = true;
        StopReason = reason;
    }

    private static uint Lsl(uint value, int amount, out bool carry)
    {
        if (amount >= 32) { carry = amount == 32 && (value & 1) != 0; return 0; }
        carry = ((value >> (32 - amount)) & 1) != 0;
        return value << amount;
    }

    private static uint Lsr(uint value, int amount, out bool carry)
    {
        if (amount >= 32) { carry = amount == 32 && (value & 0x80000000) != 0; return 0; }
        carry = ((value >> (amount - 1)) & 1) != 0;
        return value >> amount;
    }

    private static uint Asr(uint value, int amount, out bool carry)
    {
        if (amount >= 32)
        {
            carry = (value & 0x80000000) != 0;
            return carry ? 0xffffffffu : 0;
        }
        carry = ((value >> (amount - 1)) & 1) != 0;
        return (uint)((int)value >> amount);
    }

    private static uint RorWithCarry(uint value, int amount, out bool carry)
    {
        amount &= 31;
        uint result = Ror(value, amount);
        carry = (result & 0x80000000) != 0;
        return result;
    }

    private static uint Ror(uint value, int amount)
    {
        amount &= 31;
        return amount == 0 ? value : (value >> amount) | (value << (32 - amount));
    }
}
