using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using SharpCompress.Archives;
using EutherDrive.Core;
using EutherDrive.Core.Arcade.Cps1;
using EutherDrive.Core.Cpu.Z80Emu;
using EutherDrive.Core.Savestates;

namespace EutherDrive.Platforms.DataEast.Deco32;

public sealed class Deco32Adapter : IEmulatorCore, ISavestateCapable
{
    private const string SavestateMagic = "DECO32ST";
    private const int SavestateVersion = 1;
    private const int FrameWidth = 320;
    private const int FrameHeight = 240;
    private const int FrameStride = FrameWidth * 4;
    private const int OutputSampleRate = 44_100;
    private const int OutputChannels = 2;
    private const int ArmClockHz = 28_000_000 / 4;
    private const int CyclesPerFrame = ArmClockHz / 60;
    private static readonly bool TraceCpu =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DECO32_TRACE_CPU"), "1", StringComparison.Ordinal);
    private static readonly bool DebugWorkRamTextOverlay =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DECO32_WORKRAM_TEXT_OVERLAY"), "1", StringComparison.Ordinal);
    private static readonly int TraceCpuLimit = ParseEnvInt("EUTHERDRIVE_DECO32_TRACE_LIMIT", 2000);

    private readonly object _frameSync = new();
    private byte[] _presentFrameBuffer = new byte[FrameHeight * FrameStride];
    private byte[] _renderFrameBuffer = new byte[FrameHeight * FrameStride];
    private byte[] _snapshotFrameBuffer = new byte[FrameHeight * FrameStride];
    private readonly byte[] _priorityFrame = new byte[FrameWidth * FrameHeight];
    private readonly ushort[] _alphaTilemapFrame = new ushort[FrameWidth * FrameHeight];
    private short[] _audioBuffer = new short[(OutputSampleRate / 60) * OutputChannels];
    private short[] _scaledAudioBuffer = Array.Empty<short>();
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
    private int _masterVolumePercent = 100;
    private bool _loaded;
    private long _frameCounter;
    private int _traceLines;
    private string? _lastStopReason;
    private RomIdentity? _romIdentity;
    private string? _eepromPath;
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
            $"pc=0x{_mainCpu.Pc:X8} op=0x{_mainCpu.PeekOpcode():X8} vis=0x{_visiblePc:X8}/0x{_visibleOp:X8}/0x{_visibleCpsr:X8} vb=0x{_vblankPc:X8}/0x{_vblankOp:X8}/0x{_vblankCpsr:X8} post=0x{_postFramePc:X8}/0x{_postFrameOp:X8}/0x{_postFrameCpsr:X8} r0=0x{_mainCpu.Registers[0]:X8} r1=0x{_mainCpu.Registers[1]:X8} r2=0x{_mainCpu.Registers[2]:X8} r3=0x{_mainCpu.Registers[3]:X8} r4=0x{_mainCpu.Registers[4]:X8} r5=0x{_mainCpu.Registers[5]:X8} r6=0x{_mainCpu.Registers[6]:X8} r7=0x{_mainCpu.Registers[7]:X8} r8=0x{_mainCpu.Registers[8]:X8} r9=0x{_mainCpu.Registers[9]:X8} sl=0x{_mainCpu.Registers[10]:X8} fp=0x{_mainCpu.Registers[11]:X8} sp=0x{_mainCpu.Registers[13]:X8} lr=0x{_mainCpu.Registers[14]:X8} cpsr=0x{_mainCpu.Cpsr:X8} halted={_mainCpu.Halted} reason='{_lastStopReason ?? _mainCpu.StopReason}' frame={_frameCounter} vram={_memory.VideoWriteCount} pal={_memory.PaletteWriteCount} spr={_memory.SpriteWriteCount} {_memory.ProtectionDebugSummary} {_memory.TilemapDebugSummary} {_memory.PaletteDebugSummary} {_memory.SpriteDebugSummary} {_soundCpu?.DebugSummary ?? string.Empty}");

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
        _oki1 = new OKI6295(profile.Oki1, 32_220_000 / 32, 0.80f);
        _oki2 = new OKI6295(profile.Oki2, 32_220_000 / 16, 0.10f);
        _ym2151 = new YM2151(SoundBankswitch);
        _soundCpu = new Z80SoundCpu(profile.AudioCpu, _ym2151, _oki1, _oki2);
        _memory = new Deco32MemoryMap(profile, _palette, _tilemaps, _sprites, _soundCpu, _ym2151, _oki1, _oki2, asserted => _mainCpu.SetIrqLine(asserted), () => _mainCpu.Pc);
        _memory.Reset();
        string saveDirectory = PersistentStoragePath.ResolveSaveDirectory(path, "deco32");
        Directory.CreateDirectory(saveDirectory);
        _eepromPath = Path.Combine(saveDirectory, Path.GetFileNameWithoutExtension(path) + ".eeprom");
        LoadEeprom();
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

    private void SoundBankswitch(byte data)
    {
        _oki1?.SetRomBank((data >> 0) & 1);
        _oki2?.SetRomBank((data >> 1) & 1);
    }

    public void Reset()
    {
        if (!_loaded || _memory is null)
            return;
        _memory.Reset();
        LoadEeprom();
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

        try
        {
            _memory.SetInput(_input);
            _memory.BeginFrame();
            ExecuteMainCpu(CyclesPerFrame * 238 / 274);
            CaptureCpuPoint(out _visiblePc, out _visibleOp, out _visibleCpsr);
            _memory.AssertVblank();
            ExecuteMainCpu(512);
            CaptureCpuPoint(out _vblankPc, out _vblankOp, out _vblankCpsr);
            _memory.EndVblank();
            ExecuteMainCpu((CyclesPerFrame * 36 / 274) - 512);
            CaptureCpuPoint(out _postFramePc, out _postFrameOp, out _postFrameCpsr);
            _memory.EndFrame();
            RenderFrame();
            _soundCpu?.RunFrame(_audioBuffer);
            _frameCounter++;
        }
        finally
        {
            SaveEepromIfDirty();
        }
    }

    private void LoadEeprom()
    {
        if (_memory is null || string.IsNullOrWhiteSpace(_eepromPath))
            return;

        if (!File.Exists(_eepromPath))
        {
            PersistEepromSnapshot();
            return;
        }

        try
        {
            _memory.LoadEeprom(File.ReadAllBytes(_eepromPath));
        }
        catch
        {
            // Corrupt NVRAM should not prevent the board from booting.
        }
    }

    private void PersistEepromSnapshot()
    {
        if (_memory is null || string.IsNullOrWhiteSpace(_eepromPath))
            return;

        try
        {
            File.WriteAllBytes(_eepromPath, _memory.ExportEeprom());
            _memory.ClearEepromDirty();
        }
        catch
        {
            // Save failures are non-fatal; the board can still run with volatile NVRAM.
        }
    }

    private void SaveEepromIfDirty()
    {
        if (_memory is null || !_memory.EepromDirty || string.IsNullOrWhiteSpace(_eepromPath))
            return;

        PersistEepromSnapshot();
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
        if (_masterVolumePercent == 100 || _audioBuffer.Length == 0)
            return _audioBuffer;

        if (_scaledAudioBuffer.Length < _audioBuffer.Length)
            _scaledAudioBuffer = new short[_audioBuffer.Length];

        int volume = _masterVolumePercent;
        for (int i = 0; i < _audioBuffer.Length; i++)
            _scaledAudioBuffer[i] = (short)Math.Clamp((_audioBuffer[i] * volume) / 100, short.MinValue, short.MaxValue);
        return _scaledAudioBuffer.AsSpan(0, _audioBuffer.Length);
    }

    public void SetMasterVolumePercent(int percent)
    {
        _masterVolumePercent = Math.Clamp(percent, 0, 200);
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!_loaded || _memory is null || _palette is null || _tilemaps is null || _sprites is null || _soundCpu is null || _ym2151 is null || _oki1 is null || _oki2 is null)
            throw new InvalidOperationException("Deco32 core not initialized.");

        writer.Write(SavestateMagic);
        writer.Write(SavestateVersion);
        writer.Write(_frameCounter);
        writer.Write(_masterVolumePercent);
        writer.Write(_traceLines);
        writer.Write(_lastStopReason is not null);
        if (_lastStopReason is not null)
            writer.Write(_lastStopReason);
        WriteInputState(writer, _input);
        writer.Write(_visiblePc);
        writer.Write(_visibleOp);
        writer.Write(_visibleCpsr);
        writer.Write(_vblankPc);
        writer.Write(_vblankOp);
        writer.Write(_vblankCpsr);
        writer.Write(_postFramePc);
        writer.Write(_postFrameOp);
        writer.Write(_postFrameCpsr);
        WriteByteArray(writer, _presentFrameBuffer);
        WriteByteArray(writer, _renderFrameBuffer);
        WriteByteArray(writer, _snapshotFrameBuffer);
        WriteShortArray(writer, _audioBuffer);
        StateBinarySerializer.WriteInto(writer, _mainCpu);
        _memory.SaveState(writer);
        _palette.SaveState(writer);
        _tilemaps.SaveState(writer);
        _sprites.SaveState(writer);
        _soundCpu.SaveState(writer);
        _ym2151.SaveState(writer);
        _oki1.SaveState(writer);
        _oki2.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (!_loaded || _memory is null || _palette is null || _tilemaps is null || _sprites is null || _soundCpu is null || _ym2151 is null || _oki1 is null || _oki2 is null)
            throw new InvalidOperationException("Deco32 core not initialized.");

        string magic = reader.ReadString();
        if (!string.Equals(magic, SavestateMagic, StringComparison.Ordinal))
            throw new InvalidDataException("Deco32 savestate magic mismatch.");

        int version = reader.ReadInt32();
        if (version != SavestateVersion)
            throw new InvalidDataException($"Unsupported Deco32 savestate version: {version}.");

        _frameCounter = reader.ReadInt64();
        _masterVolumePercent = reader.ReadInt32();
        _traceLines = reader.ReadInt32();
        _lastStopReason = reader.ReadBoolean() ? reader.ReadString() : null;
        _input = ReadInputState(reader);
        _visiblePc = reader.ReadUInt32();
        _visibleOp = reader.ReadUInt32();
        _visibleCpsr = reader.ReadUInt32();
        _vblankPc = reader.ReadUInt32();
        _vblankOp = reader.ReadUInt32();
        _vblankCpsr = reader.ReadUInt32();
        _postFramePc = reader.ReadUInt32();
        _postFrameOp = reader.ReadUInt32();
        _postFrameCpsr = reader.ReadUInt32();
        ReadByteArray(reader, _presentFrameBuffer);
        ReadByteArray(reader, _renderFrameBuffer);
        ReadByteArray(reader, _snapshotFrameBuffer);
        ReadShortArray(reader, _audioBuffer);
        StateBinarySerializer.ReadInto(reader, _mainCpu);
        _mainCpu.AttachBus(_memory);
        _memory.LoadState(reader);
        _palette.LoadState(reader);
        _tilemaps.LoadState(reader);
        _sprites.LoadState(reader);
        _soundCpu.LoadState(reader);
        _ym2151.LoadState(reader);
        _oki1.LoadState(reader);
        _oki2.LoadState(reader);
        if (_scaledAudioBuffer.Length < _audioBuffer.Length)
            _scaledAudioBuffer = Array.Empty<short>();
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
                    $"[DECO32 ARM] pc=0x{pc:X8} op=0x{opcode:X8} next=0x{_mainCpu.Pc:X8} r0=0x{_mainCpu.Registers[0]:X8} r1=0x{_mainCpu.Registers[1]:X8} r2=0x{_mainCpu.Registers[2]:X8} r3=0x{_mainCpu.Registers[3]:X8} r4=0x{_mainCpu.Registers[4]:X8} r5=0x{_mainCpu.Registers[5]:X8} r6=0x{_mainCpu.Registers[6]:X8} r7=0x{_mainCpu.Registers[7]:X8} r8=0x{_mainCpu.Registers[8]:X8} r9=0x{_mainCpu.Registers[9]:X8} sl=0x{_mainCpu.Registers[10]:X8} fp=0x{_mainCpu.Registers[11]:X8} ip=0x{_mainCpu.Registers[12]:X8} sp=0x{_mainCpu.Registers[13]:X8} lr=0x{_mainCpu.Registers[14]:X8} cpsr=0x{_mainCpu.Cpsr:X8}"));
            }
        }

        if (_mainCpu.Halted)
            _lastStopReason = _mainCpu.StopReason;
    }

    private void RenderFrame()
    {
        Array.Clear(_renderFrameBuffer);
        Array.Clear(_priorityFrame);
        Array.Clear(_alphaTilemapFrame);
        _palette?.FillBackdrop(_renderFrameBuffer, FrameWidth, FrameHeight, FrameStride);
        int priority = _memory?.Priority ?? 0;
        bool alphaTilemap = _palette is not null
            && (priority & 3) != 0
            && (_palette.GetAceRam(0x17) != 0 || _palette.HasProgrammedObjectAlphaControls());
        _tilemaps?.RenderBackPlayfields(_renderFrameBuffer, _priorityFrame, _alphaTilemapFrame, FrameWidth, FrameHeight, FrameStride, priority, alphaTilemap);
        _sprites?.Render(_renderFrameBuffer, _priorityFrame, _alphaTilemapFrame, FrameWidth, FrameHeight, FrameStride, _frameCounter, priority, alphaTilemap);
        _tilemaps?.RenderTextPlayfield(_renderFrameBuffer, FrameWidth, FrameHeight, FrameStride);
        if (DebugWorkRamTextOverlay)
            _memory?.RenderWorkRamTextOverlay(_renderFrameBuffer, FrameWidth, FrameHeight, FrameStride);
        lock (_frameSync)
            Buffer.BlockCopy(_renderFrameBuffer, 0, _presentFrameBuffer, 0, _renderFrameBuffer.Length);
    }

    private static int ParseEnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    private static void WriteInputState(BinaryWriter writer, ArcadeInputState input)
    {
        writer.Write(input.Up);
        writer.Write(input.Down);
        writer.Write(input.Left);
        writer.Write(input.Right);
        writer.Write(input.A);
        writer.Write(input.B);
        writer.Write(input.C);
        writer.Write(input.Start);
        writer.Write(input.X);
        writer.Write(input.Y);
        writer.Write(input.Z);
        writer.Write(input.Mode);
    }

    private static ArcadeInputState ReadInputState(BinaryReader reader)
        => new(
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean(),
            reader.ReadBoolean());

    private static void WriteByteArray(BinaryWriter writer, byte[] data)
    {
        writer.Write(data.Length);
        writer.Write(data);
    }

    private static void ReadByteArray(BinaryReader reader, byte[] target)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 16 * 1024 * 1024)
            throw new InvalidDataException($"Invalid byte array length in Deco32 savestate: {length}.");
        byte[] data = reader.ReadBytes(length);
        if (data.Length != length)
            throw new EndOfStreamException("Deco32 savestate ended while reading byte array.");
        Array.Clear(target);
        Buffer.BlockCopy(data, 0, target, 0, Math.Min(data.Length, target.Length));
    }

    private static void WriteShortArray(BinaryWriter writer, short[] data)
    {
        writer.Write(data.Length);
        for (int i = 0; i < data.Length; i++)
            writer.Write(data[i]);
    }

    private static void ReadShortArray(BinaryReader reader, short[] target)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 1 * 1024 * 1024)
            throw new InvalidDataException($"Invalid short array length in Deco32 savestate: {length}.");
        Array.Clear(target);
        int copy = Math.Min(length, target.Length);
        for (int i = 0; i < length; i++)
        {
            short value = reader.ReadInt16();
            if (i < copy)
                target[i] = value;
        }
    }

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
        for (int i = 0, d = offset; i + 1 < src.Length && d + 1 < dest.Length; i += 2, d += 5)
        {
            dest[d] = src[i + 1];
            dest[d + 1] = src[i];
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
    [NonSerialized]
    private readonly NightSlashersGameProfile _profile;
    [NonSerialized]
    private readonly PaletteDevice _palette;
    [NonSerialized]
    private readonly DecoTilemapDevice _tilemaps;
    [NonSerialized]
    private readonly DecoSpriteDevice _sprites;
    [NonSerialized]
    private readonly Z80SoundCpu _soundCpu;
    [NonSerialized]
    private readonly YM2151 _ym2151;
    [NonSerialized]
    private readonly OKI6295 _oki1;
    [NonSerialized]
    private readonly OKI6295 _oki2;
    [NonSerialized]
    private readonly Action<bool> _setMainIrqLine;
    [NonSerialized]
    private readonly Func<uint> _getMainPc;
    private readonly byte[] _workRam = new byte[0x20000];
    private readonly Deco104Protection _ioprot = new();
    private readonly SerialEeprom93C46 _eeprom = new();
    private ArcadeInputState _input;
    private bool _vblank;
    private byte _priority;
    private uint _lastWorkRamReadAddress;
    private uint _lastWorkRamReadValue;
    private int _workRamReadProbeCount;

    public Deco32MemoryMap(NightSlashersGameProfile profile, PaletteDevice palette, DecoTilemapDevice tilemaps, DecoSpriteDevice sprites, Z80SoundCpu soundCpu, YM2151 ym2151, OKI6295 oki1, OKI6295 oki2, Action<bool> setMainIrqLine, Func<uint> getMainPc)
    {
        _profile = profile;
        _palette = palette;
        _tilemaps = tilemaps;
        _sprites = sprites;
        _soundCpu = soundCpu;
        _ym2151 = ym2151;
        _oki1 = oki1;
        _oki2 = oki2;
        _setMainIrqLine = setMainIrqLine;
        _getMainPc = getMainPc;
    }

    public int VideoWriteCount { get; private set; }
    public int PaletteWriteCount { get; private set; }
    public int SpriteWriteCount { get; private set; }
    public bool EepromDirty => _eeprom.Dirty;
    public int Priority => _priority;
    public string ProtectionDebugSummary => $"{_ioprot.DebugSummary} {RamDebugSummary}";
    public string TilemapDebugSummary => _tilemaps.DebugSummary;
    public string PaletteDebugSummary => _palette.DebugSummary;
    public string SpriteDebugSummary => _sprites.DebugSummary;

    public void LoadEeprom(ReadOnlySpan<byte> data) => _eeprom.Import(data);
    public byte[] ExportEeprom() => _eeprom.Export();
    public void ClearEepromDirty() => _eeprom.ClearDirty();

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        StateBinarySerializer.WriteInto(writer, this);
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        StateBinarySerializer.ReadInto(reader, this);
    }

    public void Reset()
    {
        Array.Clear(_workRam);
        _ioprot.Reset();
        _eeprom.Reset();
        _vblank = false;
        _priority = 0;
        _lastWorkRamReadAddress = 0;
        _lastWorkRamReadValue = 0;
        _workRamReadProbeCount = 0;
        VideoWriteCount = PaletteWriteCount = SpriteWriteCount = 0;
        _palette.Reset();
        _tilemaps.Reset();
        _sprites.Reset();
        _soundCpu.ResetSound();
        _ym2151.Reset();
        _oki1.Reset();
        _oki2.Reset();
    }

    public void SetInput(ArcadeInputState input) => _input = input;
    public void BeginFrame()
    {
    }
    public void EndFrame() => _sprites.Buffer();
    public void AssertVblank()
    {
        _vblank = true;
        _setMainIrqLine(true);
    }

    public void EndVblank() => _vblank = false;

    public void AcknowledgeVblankIrq() => _setMainIrqLine(false);

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
            return _palette.ReadAce32(address - 0x163000);
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
            return;
        }
        if (address is >= 0x140000 and <= 0x140003)
        {
            AcknowledgeVblankIrq();
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
            _palette.WriteAce32(address - 0x163000, value, mask);
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
        {
            _palette.PaletteDma();
            return;
        }
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
                _soundCpu.WriteSoundLatch(soundLatch.Value, _getMainPc());
        }
    }

    private uint ReadProtection(uint address)
    {
        ushort high = _ioprot.Read(address - 0x200000, BuildInput0(), BuildInputB(), BuildInput1());
        return ((uint)high << 16) | 0xffffu;
    }

    private uint ReadWorkRam32(uint address)
    {
        uint value = ReadLe32(_workRam, (int)(address - 0x100000));
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
        if (_input.Mode) value &= unchecked((ushort)~0x0001);
        if (!_vblank) value &= unchecked((ushort)~0x0010);
        if (_input.X) value &= unchecked((ushort)~0x0100);
        if (_input.Y) value &= unchecked((ushort)~0x1000);
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
            byte b004 = _workRam[0x00004];
            byte b005 = _workRam[0x00005];
            byte b006 = _workRam[0x00006];
            byte b007 = _workRam[0x00007];
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
            return $"ram[000={b000:X2}/{b001:X2} 003={b003:X2} 004={b004:X2}{b005:X2}{b006:X2}{b007:X2} 008={b008:X2} 00c={b00c:X2} 014={b014:X2} 034={b034:X2}/{b035:X2}/{b036:X2} 038={b038:X2}/{b039:X2}/{b03a:X2} 100={b100:X2} 1fd00={sfd0:X2}{sfd1:X2}{sfd2:X2}{sfd3:X2}/{sfd4:X2}{sfd5:X2}{sfd6:X2}{sfd7:X2} bsum={block0Sum & 0xffff:X4}/{chk0:X4}:{magic0:X8},{block1Sum & 0xffff:X4}/{chk1:X4}:{magic1:X8} br=0x{_lastWorkRamReadAddress:X6}:0x{_lastWorkRamReadValue:X8}/{_workRamReadProbeCount}] {_eeprom.DebugSummary}";
        }
    }

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
    private static readonly bool Trace =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DECO32_TRACE_EEPROM"), "1", StringComparison.Ordinal);
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
    private bool _writeAll;
    private int _reads;
    private int _writes;
    private byte _lastData;
    public bool Dirty { get; private set; }

    public bool DoRead => _do;

    public void Reset()
    {
        Array.Fill(_words, (ushort)0xffff);
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
        _writeAll = false;
        _reads = 0;
        _writes = 0;
        _lastData = 0;
        Dirty = false;
    }

    public byte[] Export()
    {
        byte[] data = new byte[_words.Length * 2];
        for (int i = 0; i < _words.Length; i++)
        {
            data[i * 2] = (byte)_words[i];
            data[i * 2 + 1] = (byte)(_words[i] >> 8);
        }
        return data;
    }

    public void Import(ReadOnlySpan<byte> data)
    {
        if (data.Length < _words.Length * 2)
            return;

        for (int i = 0; i < _words.Length; i++)
            _words[i] = (ushort)(data[i * 2] | (data[i * 2 + 1] << 8));
        ResetLines();
        Dirty = false;
    }

    public void ClearDirty() => Dirty = false;

    private void ResetLines()
    {
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
        _writeAll = false;
        _lastData = 0;
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
            _writeAll = false;
            _do = true;
            return;
        }

        _cs = true;
        _state = 0;
        _bits = 0;
        _command = 0;
        _outBits = 0;
        _writeAll = false;
        _do = true;
    }

    public string DebugSummary
        => $"eep[cs={(_cs ? 1 : 0)} clk={(_clk ? 1 : 0)} di={(_di ? 1 : 0)} do={(_do ? 1 : 0)} st={_state} cmd=0x{_command:X} a=0x{_address:X2} r={_reads} w={_writes} last=0x{_lastData:X2} w00=0x{_words[0]:X4} w10=0x{_words[0x10]:X4} w1e=0x{_words[0x1e]:X4} w1f=0x{_words[0x1f]:X4}]";

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
                        if (_writeAll)
                        {
                            for (int i = 0; i < _words.Length; i++)
                                _words[i] &= _outShift;
                            _writes += _words.Length;
                            Dirty = true;
                            if (Trace)
                                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[DECO32 EEPROM] WRITEALL v=0x{_outShift:X4}"));
                        }
                        else
                        {
                            _words[_address] = _outShift;
                            _writes++;
                            Dirty = true;
                            if (Trace)
                                Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[DECO32 EEPROM] WRITE a=0x{_address:X2} v=0x{_outShift:X4}"));
                        }
                    }
                    _state = 4;
                    _writeAll = false;
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
                if (Trace)
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[DECO32 EEPROM] READ a=0x{_address:X2} v=0x{_outShift:X4}"));
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
                    Dirty = true;
                    if (Trace)
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[DECO32 EEPROM] ERASE a=0x{_address:X2}"));
                }
                _state = 4;
                _do = true;
                break;
            default:
                switch (_address >> 4)
                {
                    case 0:
                        _writeEnabled = false;
                        _state = 4;
                        _do = true;
                        break;
                    case 1:
                        _outShift = 0;
                        _bits = 0;
                        _writeAll = true;
                        _state = 3;
                        break;
                    case 2:
                        if (_writeEnabled)
                        {
                            Array.Fill(_words, (ushort)0xffff);
                            _writes += _words.Length;
                            Dirty = true;
                            if (Trace)
                                Console.WriteLine("[DECO32 EEPROM] ERASEALL");
                        }
                        _state = 4;
                        _do = true;
                        break;
                    case 3:
                        _writeEnabled = true;
                        _state = 4;
                        _do = true;
                        break;
                }
                break;
        }
    }
}

internal enum Deco104AddressScramble
{
    Interleave,
    Reverse
}

internal sealed class Deco104Protection
{
    private const short InputPortA = -1;
    private const short InputPortB = -2;
    private const short InputPortC = -3;
    private const byte Blank = 0xff;
    private const byte ConfigRegion = 0x08;

    private readonly ushort[][] _ram = { new ushort[0x80], new ushort[0x80] };
    private readonly byte[] _regionSelects = new byte[6];
    private readonly Deco104AddressScramble _addressScramble;
    private readonly bool _useMagicReadAddressXor;
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
    private int _portAReadCount;
    private int _portBReadCount;
    private int _portCReadCount;
    private ushort _lastPortAValue;
    private ushort _lastPortBValue;
    private ushort _lastPortCValue;

    public Deco104Protection(
        Deco104AddressScramble addressScramble = Deco104AddressScramble.Interleave,
        bool useMagicReadAddressXor = false)
    {
        _addressScramble = addressScramble;
        _useMagicReadAddressXor = useMagicReadAddressXor;
    }

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
        _portAReadCount = 0;
        _portBReadCount = 0;
        _portCReadCount = 0;
        _lastPortAValue = 0;
        _lastPortBValue = 0;
        _lastPortCValue = 0;
    }

    public ushort Read(uint byteOffset, ushort portA, ushort portB, ushort portC)
    {
        ushort address = DecodeCpuAddress(byteOffset);
        return ReadDecodedAddress(address, portA, portB, portC);
    }

    public ushort ReadDecodedAddress(uint decodedAddress, ushort portA, ushort portB, ushort portC)
    {
        ushort address = (ushort)(decodedAddress & 0x7fff);
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
        return WriteDecodedAddress(address, data, mask);
    }

    public byte? WriteDecodedAddress(uint decodedAddress, ushort data, ushort mask)
    {
        ushort address = (ushort)(decodedAddress & 0x7fff);
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
            $"protR={_readCount}/0x{_lastReadAddress:X4}=0x{_lastReadValue:X4}/raw0x{_lastReadRaw:X4}/loc{_lastReadLocation}/fl0x{_lastReadFlags:X1} protW={_writeCount}/0x{_lastWriteAddress:X4}=0x{_lastWriteData:X4} cfg={_configWriteCount} up=0x{_lastUpper:X1} cs=0x{_lastCsFlags:X2} bank={_currentRamBank} ports={_portAReadCount}:0x{_lastPortAValue:X4}/{_portBReadCount}:0x{_lastPortBValue:X4}/{_portCReadCount}:0x{_lastPortCValue:X4} rs=[0x{_regionSelects[0]:X1},0x{_regionSelects[1]:X1},0x{_regionSelects[2]:X1},0x{_regionSelects[3]:X1},0x{_regionSelects[4]:X1},0x{_regionSelects[5]:X1}]");

    private ushort ReadProtectionPort(ushort address, ushort portA, ushort portB, ushort portC)
    {
        if (address == _latchAddress && _latchValid)
        {
            _latchValid = false;
            return _latchData;
        }

        _latchValid = false;
        if (_useMagicReadAddressXor)
            address ^= 0x02a4;

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
        switch (writeOffset)
        {
            case InputPortA:
                _portAReadCount++;
                _lastPortAValue = portA;
                break;
            case InputPortB:
                _portBReadCount++;
                _lastPortBValue = portB;
                break;
            case InputPortC:
                _portCReadCount++;
                _lastPortCValue = portC;
                break;
        }

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

    private ushort DecodeCpuAddress(uint byteOffset)
    {
        ushort realAddress = (ushort)(((byteOffset >> 2) * 2) & 0xffff);
        ushort decoAddress = (ushort)((((realAddress >> 14) & 0x0f) << 11) | (realAddress & 0x07ff));
        return DecodeExternalAddress(decoAddress);
    }

    public ushort DecodeExternalAddress(uint byteOffset)
        => BitswapExternalAddress((ushort)(byteOffset & 0x7fff));

    private ushort BitswapExternalAddress(ushort address)
    {
        ushort input = (ushort)(address >> 1);
        ReadOnlySpan<byte> swap = _addressScramble == Deco104AddressScramble.Reverse
            ? stackalloc byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0 }
            : stackalloc byte[] { 9, 0, 8, 1, 7, 2, 6, 3, 5, 4 };
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
    [NonSerialized]
    private readonly NightSlashersGameProfile _profile;
    [NonSerialized]
    private readonly PaletteDevice _palette;
    private readonly ushort[][] _pf = { new ushort[0x800], new ushort[0x800], new ushort[0x800], new ushort[0x800] };
    private readonly ushort[][] _rowscroll = { new ushort[0x800], new ushort[0x800], new ushort[0x800], new ushort[0x800] };
    private readonly ushort[][] _control = { new ushort[0x10], new ushort[0x10] };
    private readonly int[] _nonzeroWriteCount = new int[4];
    private readonly int[] _nonzeroAttemptCount = new int[4];
    private readonly int[] _dataWriteCount = new int[4];
    private readonly int[] _ignoredHighHalfWriteCount = new int[4];
    private readonly int[] _nonzeroHighHalfWriteCount = new int[4];
    private readonly int[] _rowscrollWriteCount = new int[4];
    private readonly int[] _controlWriteCount = new int[2];
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
                $"pf0={nonzero[0]}/0x{first[0]:X4}/w{_dataWriteCount[0]}/nw{_nonzeroWriteCount[0]}/na{_nonzeroAttemptCount[0]}/hi{_ignoredHighHalfWriteCount[0]}/{_nonzeroHighHalfWriteCount[0]}/@0x{_lastDataOffset[0]:X4}=0x{_lastDataValue[0]:X4}/raw0x{_lastRawDataValue[0]:X8}m0x{_lastRawDataMask[0]:X8} pf1={nonzero[1]}/0x{first[1]:X4}/w{_dataWriteCount[1]}/nw{_nonzeroWriteCount[1]}/na{_nonzeroAttemptCount[1]}/hi{_ignoredHighHalfWriteCount[1]}/{_nonzeroHighHalfWriteCount[1]}/@0x{_lastDataOffset[1]:X4}=0x{_lastDataValue[1]:X4}/raw0x{_lastRawDataValue[1]:X8}m0x{_lastRawDataMask[1]:X8} pf2={nonzero[2]}/0x{first[2]:X4}/w{_dataWriteCount[2]}/nw{_nonzeroWriteCount[2]}/na{_nonzeroAttemptCount[2]}/hi{_ignoredHighHalfWriteCount[2]}/{_nonzeroHighHalfWriteCount[2]}/@0x{_lastDataOffset[2]:X4}=0x{_lastDataValue[2]:X4}/raw0x{_lastRawDataValue[2]:X8}m0x{_lastRawDataMask[2]:X8} pf3={nonzero[3]}/0x{first[3]:X4}/w{_dataWriteCount[3]}/nw{_nonzeroWriteCount[3]}/na{_nonzeroAttemptCount[3]}/hi{_ignoredHighHalfWriteCount[3]}/{_nonzeroHighHalfWriteCount[3]}/@0x{_lastDataOffset[3]:X4}=0x{_lastDataValue[3]:X4}/raw0x{_lastRawDataValue[3]:X8}m0x{_lastRawDataMask[3]:X8} rs=[{_rowscrollWriteCount[0]},{_rowscrollWriteCount[1]},{_rowscrollWriteCount[2]},{_rowscrollWriteCount[3]}] cb=0x{ColorBank:X2} cw=[{_controlWriteCount[0]},{_controlWriteCount[1]}] c0=[0x{_control[0][1]:X4},0x{_control[0][2]:X4},0x{_control[0][3]:X4},0x{_control[0][4]:X4},0x{_control[0][5]:X4},0x{_control[0][6]:X4}] c1=[0x{_control[1][1]:X4},0x{_control[1][2]:X4},0x{_control[1][3]:X4},0x{_control[1][4]:X4},0x{_control[1][5]:X4},0x{_control[1][6]:X4}]");
        }
    }

    public void Reset()
    {
        foreach (ushort[] ram in _pf) Array.Clear(ram);
        foreach (ushort[] ram in _rowscroll) Array.Clear(ram);
        foreach (ushort[] ram in _control) Array.Clear(ram);
        Array.Clear(_nonzeroWriteCount);
        Array.Clear(_nonzeroAttemptCount);
        Array.Clear(_dataWriteCount);
        Array.Clear(_ignoredHighHalfWriteCount);
        Array.Clear(_nonzeroHighHalfWriteCount);
        Array.Clear(_rowscrollWriteCount);
        Array.Clear(_controlWriteCount);
        Array.Clear(_lastDataOffset);
        Array.Clear(_lastDataValue);
        Array.Clear(_lastRawDataValue);
        Array.Clear(_lastRawDataMask);
        ColorBank = 0;
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        StateBinarySerializer.WriteInto(writer, this);
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        StateBinarySerializer.ReadInto(reader, this);
    }

    public uint ReadData32(int chip, uint offset) => ReadLow16Dword(_pf[chip * 2 + ((offset >> 13) & 1)], offset);
    public void WriteData32(int chip, uint offset, uint value, uint mask)
    {
        int layer = chip * 2 + (int)((offset >> 13) & 1);
        _lastRawDataValue[layer] = value;
        _lastRawDataMask[layer] = mask;
        if ((value & mask) != 0)
            _nonzeroAttemptCount[layer]++;
        if ((mask & 0x0000ffff) == 0 && (mask & 0xffff0000) != 0)
        {
            _ignoredHighHalfWriteCount[layer]++;
            if ((value & 0xffff0000) != 0)
                _nonzeroHighHalfWriteCount[layer]++;
        }
        if (WriteLow16Dword(_pf[layer], offset, value, mask, out ushort data))
        {
            _dataWriteCount[layer]++;
            _lastDataOffset[layer] = offset;
            _lastDataValue[layer] = data;
            if (data != 0)
                _nonzeroWriteCount[layer]++;
        }
    }
    public uint ReadRowscroll32(int chip, uint offset) => ReadLow16Dword(_rowscroll[chip * 2 + ((offset >> 13) & 1)], offset);
    public void WriteRowscroll32(int chip, uint offset, uint value, uint mask)
    {
        int layer = chip * 2 + (int)((offset >> 13) & 1);
        if (WriteLow16Dword(_rowscroll[layer], offset, value, mask, out _))
            _rowscrollWriteCount[layer]++;
    }
    public uint ReadControl32(int chip, uint offset) => ReadLow16Dword(_control[chip], offset);
    public void WriteControl32(int chip, uint offset, uint value, uint mask)
    {
        if (WriteLow16Dword(_control[chip], offset, value, mask, out _))
            _controlWriteCount[chip]++;
    }

    public void RenderBackPlayfields(byte[] fb, byte[] priorityMap, ushort[] alphaMap, int width, int height, int stride, int priority, bool alphaTilemap)
    {
        if ((priority & 2) != 0)
        {
            RenderChip1Combined(fb, priorityMap, width, height, stride, priorityValue: 1);
            RenderLayer(fb, priorityMap, null, width, height, stride, 1, _profile.Tiles1, 0x10, opaque: false, priorityValue: 4);
            return;
        }

        RenderLayer(fb, priorityMap, null, width, height, stride, 3, _profile.Tiles2, Chip1Pf2ColorBase, opaque: false, priorityValue: 1);
        if ((priority & 1) != 0)
        {
            RenderLayer(fb, priorityMap, null, width, height, stride, 1, _profile.Tiles1, 0x10, opaque: false, priorityValue: 2);
            RenderLayer(alphaTilemap ? null : fb, priorityMap, alphaTilemap ? alphaMap : null, width, height, stride, 2, _profile.Tiles2, Chip1Pf1ColorBase, opaque: false, priorityValue: 4);
        }
        else
        {
            RenderLayer(fb, priorityMap, null, width, height, stride, 2, _profile.Tiles2, Chip1Pf1ColorBase, opaque: false, priorityValue: 2);
            RenderLayer(alphaTilemap ? null : fb, priorityMap, alphaTilemap ? alphaMap : null, width, height, stride, 1, _profile.Tiles1, 0x10, opaque: false, priorityValue: 4);
        }
    }

    public void RenderTextPlayfield(byte[] fb, int width, int height, int stride)
    {
        RenderLayer(fb, null, null, width, height, stride, 0, _profile.Tiles1, 0x80, opaque: false, priorityValue: 0);
    }

    private void RenderChip1Combined(byte[] fb, byte[] priorityMap, int width, int height, int stride, byte priorityValue)
    {
        ushort[] pf1 = _pf[2];
        ushort[] pf2 = _pf[3];
        ushort[] ctrl = _control[1];
        int control0 = ctrl[5] & 0xff;
        int control1 = ctrl[6] & 0xff;
        if ((control0 & 0x80) == 0)
            return;

        int scrollX = ctrl[1] & 0x3ff;
        int scrollY = ctrl[2] & 0x1ff;
        ushort[] rowscroll = _rowscroll[2];
        const int tileSize = 16;
        const int mapCols = 64;
        const int mapRows = 32;
        int widthMask = mapCols * tileSize - 1;
        int heightMask = mapRows * tileSize - 1;
        int rowType = 1 << ((control0 >> 3) & 0x0f);
        int colType = 8 << (control0 & 7);
        int tileBank1 = DecoBankCallback(ctrl[7] & 0xff);
        int tileBank2 = DecoBankCallback(ctrl[7] >> 8);
        bool enableTileFlipX = (control1 & 0x01) != 0;
        bool enableTileFlipY = (control1 & 0x02) != 0;
        bool rowScroll = (control1 & 0x40) != 0;
        bool columnScroll = (control1 & 0x20) != 0;

        for (int y = 0; y < height; y++)
        {
            int baseSy = (y + scrollY) & heightMask;
            int sourceX = rowScroll ? scrollX + rowscroll[(baseSy / rowType) & 0x7ff] : scrollX;
            for (int x = 0; x < width; x++)
            {
                int sx = (x + sourceX) & widthMask;
                int columnOffset = columnScroll ? rowscroll[0x200 + (((sx & 0x1ff) / colType) & 0x1ff)] : 0;
                int sy = (baseSy + columnOffset) & heightMask;
                int ty = sy / tileSize;
                int py = sy & (tileSize - 1);
                int tx = sx / tileSize;
                int px = sx & (tileSize - 1);
                int entry = Deco16ScanRows(tx, ty) & 0x7ff;
                int p = ReadLayerPalettePixel(pf1, _profile.Tiles2, entry, tileBank1, Chip1Pf1ColorBase, px, py, tileSize, enableTileFlipX, enableTileFlipY, out int pen1);
                int p2 = ReadLayerPalettePixel(pf2, _profile.Tiles2, entry, tileBank2, Chip1Pf2ColorBase, px, py, tileSize, enableTileFlipX, enableTileFlipY, out int pen2);
                if ((pen1 | pen2) == 0)
                    continue;

                int mixed = MixNightSlashersTilePixel(p, p2);
                _palette.WritePixel(fb, stride, x, y, mixed);
                priorityMap[y * width + x] = priorityValue;
            }
        }
    }

    private void RenderLayer(byte[]? fb, byte[]? priorityMap, ushort[]? alphaMap, int width, int height, int stride, int layer, byte[] gfx, int colorBase, bool opaque, byte priorityValue)
    {
        ushort[] ram = _pf[layer];
        ushort[] ctrl = _control[layer >> 1];
        int control0 = ((layer & 1) == 0 ? ctrl[5] : ctrl[5] >> 8) & 0xff;
        int control1 = ((layer & 1) == 0 ? ctrl[6] : ctrl[6] >> 8) & 0xff;
        if ((control0 & 0x80) == 0)
            return;

        int scrollX = ctrl[(layer & 1) == 0 ? 1 : 3] & 0x3ff;
        int scrollY = ctrl[(layer & 1) == 0 ? 2 : 4] & 0x1ff;
        ushort[] rowscroll = _rowscroll[layer];
        bool charMode = (control1 & 0x80) != 0;
        int tileSize = charMode ? 8 : 16;
        int mapCols = 64;
        int mapRows = 32;
        int widthMask = mapCols * tileSize - 1;
        int heightMask = mapRows * tileSize - 1;
        int tileBank = DecoBankCallback((layer & 1) == 0 ? ctrl[7] & 0xff : ctrl[7] >> 8);
        bool enableTileFlipX = (control1 & 0x01) != 0;
        bool enableTileFlipY = (control1 & 0x02) != 0;
        int rowType = 1 << ((control0 >> 3) & 0x0f);
        int colType = 8 << (control0 & 7);
        bool rowScroll = (control1 & 0x40) != 0;
        bool columnScroll = (control1 & 0x20) != 0;

        for (int y = 0; y < height; y++)
        {
            int baseSy = (y + scrollY) & heightMask;
            int sourceX = rowScroll ? scrollX + rowscroll[(baseSy / rowType) & 0x7ff] : scrollX;
            for (int x = 0; x < width; x++)
            {
                int sx = (x + sourceX) & widthMask;
                int columnOffset = columnScroll ? rowscroll[0x200 + (((sx & 0x1ff) / colType) & 0x1ff)] : 0;
                int sy = (baseSy + columnOffset) & heightMask;
                int ty = sy / tileSize;
                int py = sy & (tileSize - 1);
                int tx = sx / tileSize;
                int px = sx & (tileSize - 1);
                int entry = TilemapEntryIndex(tx, ty, mapCols, charMode) & 0x7ff;
                ushort tileWord = ram[entry & (ram.Length - 1)];
                int palettePixel = ReadLayerPalettePixel(ram, gfx, entry, tileBank, colorBase, px, py, tileSize, enableTileFlipX, enableTileFlipY, out int pen);
                if (charMode)
                {
                    int colorNibble = (tileWord >> 12) & 0x0f;
                    bool tileFlipX = false;
                    bool tileFlipY = false;
                    if ((tileWord & 0x8000) != 0)
                    {
                        if (enableTileFlipX)
                        {
                            tileFlipX = true;
                            colorNibble &= 0x07;
                        }
                        if (enableTileFlipY)
                        {
                            tileFlipY = true;
                            colorNibble &= 0x07;
                        }
                    }
                    int srcX = tileFlipX ? tileSize - 1 - px : px;
                    int srcY = tileFlipY ? tileSize - 1 - py : py;
                    pen = Decode4BppChar(gfx, (tileWord & 0x0fff) + tileBank, srcX, srcY);
                    palettePixel = (colorBase + colorNibble) * 16 + pen;
                }
                if (pen == 0 && !opaque)
                    continue;
                if (alphaMap is not null)
                    alphaMap[y * width + x] = (ushort)palettePixel;
                else if (fb is not null)
                    _palette.WritePixel(fb, stride, x, y, palettePixel);
                if (priorityMap is not null)
                    priorityMap[y * width + x] = priorityValue;
            }
        }
    }

    private static int ReadLayerPalettePixel(ushort[] ram, byte[] gfx, int entry, int tileBank, int colorBase, int px, int py, int tileSize, bool enableTileFlipX, bool enableTileFlipY, out int pen)
    {
        ushort tile = ram[entry & (ram.Length - 1)];
        int colorNibble = (tile >> 12) & 0x0f;
        bool tileFlipX = false;
        bool tileFlipY = false;
        if ((tile & 0x8000) != 0)
        {
            if (enableTileFlipX)
            {
                tileFlipX = true;
                colorNibble &= 0x07;
            }
            if (enableTileFlipY)
            {
                tileFlipY = true;
                colorNibble &= 0x07;
            }
        }

        int srcX = tileFlipX ? tileSize - 1 - px : px;
        int srcY = tileFlipY ? tileSize - 1 - py : py;
        pen = Decode4BppTile(gfx, (tile & 0x0fff) + tileBank, srcX, srcY);
        return (colorBase + colorNibble) * 16 + pen;
    }

    private static int MixNightSlashersTilePixel(int p, int p2)
        => ((p & 0x70f) + (((p & 0x30) | (p2 & 0x0f)) << 4)) & 0x7ff;

    private static int Deco16ScanRows(int col, int row)
        => (col & 0x1f) + ((row & 0x1f) << 5) + ((col & 0x20) << 5) + ((row & 0x20) << 6);

    private static int TilemapEntryIndex(int col, int row, int mapCols, bool charMode)
        => charMode ? col + row * mapCols : Deco16ScanRows(col, row);

    private static int DecoBankCallback(int bank)
        => (bank & ~0x0f) << 8;

    private int Chip1Pf1ColorBase => ((ColorBank >> 0) & 7) << 4;
    private int Chip1Pf2ColorBase => ((ColorBank >> 3) & 7) << 4;

    private static int Decode4BppChar(byte[] rom, int code, int x, int y)
    {
        int half = rom.Length / 2;
        int chars = Math.Max(1, half / 16);
        int baseBit = (code % chars) * 16 * 8 + y * 16 + x;
        int p0 = ReadBit(rom, half * 8 + baseBit + 8);
        int p1 = ReadBit(rom, half * 8 + baseBit);
        int p2 = ReadBit(rom, baseBit + 8);
        int p3 = ReadBit(rom, baseBit);
        return (p0 << 3) | (p1 << 2) | (p2 << 1) | p3;
    }

    private static int Decode4BppTile(byte[] rom, int code, int x, int y)
    {
        int half = rom.Length / 2;
        int tiles = Math.Max(1, half / 64);
        int baseBit = (code % tiles) * 64 * 8 + y * 16 + (x < 8 ? 16 * 8 * 2 : 0) + (x & 7);
        int p0 = ReadBit(rom, half * 8 + baseBit + 8);
        int p1 = ReadBit(rom, half * 8 + baseBit);
        int p2 = ReadBit(rom, baseBit + 8);
        int p3 = ReadBit(rom, baseBit);
        return (p0 << 3) | (p1 << 2) | (p2 << 1) | p3;
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

        uint lowMask = mask & 0x0000ffff;
        data = (ushort)((ram[word] & ~lowMask) | (value & lowMask));
        ram[word] = data;
        return true;
    }
}

public sealed class DecoSpriteDevice
{
    [NonSerialized]
    private readonly NightSlashersGameProfile _profile;
    [NonSerialized]
    private readonly PaletteDevice _palette;
    private readonly ushort[][] _ram = { new ushort[0x800], new ushort[0x800] };
    private readonly ushort[][] _buffered = { new ushort[0x800], new ushort[0x800] };
    [NonSerialized]
    private ushort[] _raw0 = Array.Empty<ushort>();
    [NonSerialized]
    private ushort[] _raw1 = Array.Empty<ushort>();

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
                $"spr0={nonzero[0]}/y0x{firstY[0]:X4}c0x{firstCode[0]:X4}x0x{firstX[0]:X4} spr1={nonzero[1]}/y0x{firstY[1]:X4}c0x{firstCode[1]:X4}x0x{firstX[1]:X4} scb=[0x{ColorBank0:X2},0x{ColorBank1:X2}]");
        }
    }

    public void Reset()
    {
        foreach (ushort[] ram in _ram) Array.Clear(ram);
        foreach (ushort[] ram in _buffered) Array.Clear(ram);
        ColorBank0 = ColorBank1 = 0;
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        StateBinarySerializer.WriteInto(writer, this);
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        StateBinarySerializer.ReadInto(reader, this);
        _raw0 = Array.Empty<ushort>();
        _raw1 = Array.Empty<ushort>();
    }

    public uint Read32(int list, uint offset) => ReadLow16Dword(_ram[list], offset);
    public void Write32(int list, uint offset, uint value, uint mask) => WriteLow16Dword(_ram[list], offset, value, mask);
    public void Buffer() { Buffer(0); Buffer(1); }
    public void Buffer(int list) => Array.Copy(_ram[list], _buffered[list], _ram[list].Length);

    public void Render(byte[] fb, byte[] priorityMap, ushort[] alphaMap, int width, int height, int stride, long frame, int priority, bool alphaTilemap)
    {
        EnsureRawBuffers(width * height);
        Array.Clear(_raw0);
        Array.Clear(_raw1);
        RenderListRaw(_raw0, width, height, _buffered[0], _profile.Sprites1, fiveBpp: true, frame);
        RenderListRaw(_raw1, width, height, _buffered[1], _profile.Sprites2, fiveBpp: false, frame);
        MixRawSprites(fb, priorityMap, alphaMap, width, height, stride, priority, alphaTilemap);
    }

    private void EnsureRawBuffers(int pixels)
    {
        if (_raw0.Length != pixels)
        {
            _raw0 = new ushort[pixels];
            _raw1 = new ushort[pixels];
        }
    }

    private void RenderListRaw(ushort[] raw, int width, int height, ushort[] spr, byte[] gfx, bool fiveBpp, long frame)
    {
        for (int offs = 0; offs + 3 < 0x800; offs += 4)
        {
            ushort yraw = spr[offs];
            if (((yraw >> 12) & 1) != 0 && (frame & 1) != 0)
                continue;
            int sprite = spr[offs + 1];
            int xraw = spr[offs + 2];
            int color = (xraw >> 9) & 0x7f;
            if ((yraw & 0x8000) != 0)
                color |= 0x80;
            bool fx = (yraw & 0x2000) != 0;
            bool fy = (yraw & 0x4000) != 0;
            bool wide = (yraw & 0x0800) != 0;
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
            y = 240 - y;
            x = 304 - x;
            fx = !fx;
            fy = !fy;
            int mult = 16;
            int mult2 = multi + 1;
            for (int m = multi; m >= 0; m--)
            {
                int tile = sprite - m * inc;
                int dy = y + mult * m;
                DrawSpriteTileRaw(raw, width, height, gfx, fiveBpp, tile, color, x, dy, fx, fy);
                if (wide)
                    DrawSpriteTileRaw(raw, width, height, gfx, fiveBpp, tile - mult2, color, x + 16, dy, fx, fy);
            }
        }
    }

    private void MixRawSprites(byte[] fb, byte[] priorityMap, ushort[] alphaMap, int width, int height, int stride, int priority, bool alphaTilemap)
    {
        int sprite0ColorBase = (ColorBank0 & 7) << 8;
        int sprite1ColorBase = (ColorBank1 & 7) << 8;
        int sprite0ExtraBank = (priority & 4) == 0 ? 0x800 : 0;

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int offset = row + x;
                ushort pix0 = _raw0[offset];
                ushort pix1 = _raw1[offset];
                int pri0 = (pix0 & 0x6000) >> 13;
                int pri1 = (pix1 & 0x6000) >> 13;
                int tilePri = priorityMap[offset];
                bool sprite0Drawn = false;
                bool sprite1Alpha = (pix1 & 0x8000) != 0;
                int coloffs = sprite0ExtraBank;

                if ((pix0 & 0xff) != 0)
                {
                    bool draw0 = pri0 <= 1 || (pri0 == 2 ? alphaTilemap || tilePri < 4 : tilePri < 2);
                    if (draw0)
                    {
                        int color0 = (((pix0 & 0x1f00) >> 8) % 16) * 32;
                        _palette.WritePixel(fb, stride, x, y, sprite0ColorBase + (sprite0ExtraBank | color0 | (pix0 & 0xff)));
                        sprite0Drawn = true;
                    }
                }

                coloffs = ((priority & 4) == 0 && sprite0Drawn) ? 0x800 : 0;
                if ((pix1 & 0xff) != 0)
                {
                    bool draw1;
                    if (sprite1Alpha)
                    {
                        draw1 = pri1 switch
                        {
                            0 => (pix0 & 0xff) == 0 || (pri0 != 0 && pri0 != 1 && pri0 != 2),
                            1 => ((priority & 1) == 0 || tilePri < 4)
                                && ((pix0 & 0xff) == 0 || (pri0 != 0 && pri0 != 1 && ((priority & 1) == 0 || pri0 != 2))),
                            _ => true
                        };
                        if (draw1 && pri1 == 0 && (priority & 1) != 0)
                            draw1 = tilePri < 4 || (alphaTilemap && (alphaMap[offset] & 0x0f) == 0);
                    }
                    else
                    {
                        draw1 = pri1 == 0
                            ? (pix0 & 0xff) == 0 || pri0 != 0
                            : true;
                    }

                    if (draw1)
                    {
                        int rawColor1 = (pix1 & 0x0f00) >> 8;
                        bool alpha2 = (pix1 & 0x1000) == 0;
                        int alpha = (!sprite1Alpha || alpha2)
                            ? _palette.GetObjectAlpha((rawColor1 & 0x8) != 0 ? 0x4 + ((rawColor1 & 0x3) / 2) : ((rawColor1 & 0x7) / 2))
                            : 0xff;
                        int color1 = (rawColor1 % 16) * 16;
                        _palette.BlendPixel(fb, stride, x, y, sprite1ColorBase + (coloffs | color1 | (pix1 & 0xff)), alpha);
                    }
                }

                if (alphaTilemap)
                {
                    ushort alphaPix = alphaMap[offset];
                    if ((alphaPix & 0x0f) != 0
                        && ((pix0 & 0xff) == 0 || pri0 >= 2)
                        && ((pix1 & 0xff) == 0 || pri1 >= 2 || sprite1Alpha))
                    {
                        int alpha = _palette.GetTilemapAlpha(0x17 + (((alphaPix & 0xf0) >> 4) / 2));
                        _palette.BlendPixel(fb, stride, x, y, coloffs | alphaPix, alpha);
                    }
                }
            }
        }
    }

    private static void DrawSpriteTileRaw(ushort[] raw, int width, int height, byte[] gfx, bool fiveBpp, int code, int color, int sx, int sy, bool fx, bool fy)
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
                raw[dy * width + dx] = (ushort)((color << 8) | pen);
            }
        }
    }

    private static int Decode4Bpp(byte[] rom, int code, int x, int y)
    {
        int half = rom.Length / 2;
        int tiles = Math.Max(1, half / 64);
        int baseBit = (code % tiles) * 64 * 8 + y * 16 + (x < 8 ? 16 * 8 * 2 : 0) + (x & 7);
        int p0 = ReadBit(rom, half * 8 + baseBit + 8);
        int p1 = ReadBit(rom, half * 8 + baseBit);
        int p2 = ReadBit(rom, baseBit + 8);
        int p3 = ReadBit(rom, baseBit);
        return (p0 << 3) | (p1 << 2) | (p2 << 1) | p3;
    }

    private static int Decode5Bpp(byte[] rom, int code, int x, int y)
    {
        int tiles = Math.Max(1, rom.Length / (16 * 16 * 5 / 8));
        int baseBit = (code % tiles) * 16 * 16 * 5 + y * 8 * 5 + (x < 8 ? 16 * 8 * 5 : 0) + (x & 7);
        return (ReadBit(rom, baseBit) << 4)
            | (ReadBit(rom, baseBit + 8) << 3)
            | (ReadBit(rom, baseBit + 16) << 2)
            | (ReadBit(rom, baseBit + 24) << 1)
            | ReadBit(rom, baseBit + 32);
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
        uint lowMask = mask & 0x0000ffff;
        ram[word] = (ushort)((ram[word] & ~lowMask) | (value & lowMask));
    }
}

public sealed class PaletteDevice
{
    private readonly uint[] _colors = new uint[4096];
    private readonly uint[] _ram = new uint[2048];
    private readonly uint[] _buffered = new uint[2048];
    private readonly ushort[] _aceRam = new ushort[0x28];
    private int _dmaCount;

    public void Reset()
    {
        Array.Clear(_ram);
        Array.Clear(_buffered);
        Array.Clear(_aceRam);
        Array.Clear(_colors);
        _dmaCount = 0;
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        StateBinarySerializer.WriteInto(writer, this);
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        StateBinarySerializer.ReadInto(reader, this);
    }

    public uint ReadAce32(uint offset)
    {
        int index = (int)(offset >> 2) & (_aceRam.Length - 1);
        return 0xffff0000u | _aceRam[index];
    }

    public void WriteAce32(uint offset, uint value, uint mask)
    {
        if ((mask & 0x0000ffff) == 0)
            return;

        int index = (int)(offset >> 2) & (_aceRam.Length - 1);
        _aceRam[index] = CombineMasked16(_aceRam[index], value, mask);
        if ((uint)(index - 0x20) <= 0x06)
            UpdatePalette();
    }

    public uint Read32(uint offset)
    {
        int index = (int)(offset >> 2) & (_ram.Length - 1);
        return _ram[index];
    }

    public void Write32(uint offset, uint value, uint mask)
    {
        int index = (int)(offset >> 2) & (_ram.Length - 1);
        _ram[index] = CombineMasked(_ram[index], value, mask);
    }

    public void PaletteDma()
    {
        Array.Copy(_ram, _buffered, _ram.Length);
        _dmaCount++;
        UpdatePalette();
    }

    public void FillBackdrop(byte[] fb, int width, int height, int stride)
    {
        uint color = _colors[0x300];
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

    public void BlendPixel(byte[] fb, int stride, int x, int y, int paletteIndex, int alpha)
    {
        alpha = Math.Clamp(alpha, 0, 255);
        if (alpha <= 0)
            return;

        int offset = y * stride + x * 4;
        uint src = _colors[paletteIndex & (_colors.Length - 1)];
        int sr = (int)((src >> 16) & 0xff);
        int sg = (int)((src >> 8) & 0xff);
        int sb = (int)(src & 0xff);
        int db = fb[offset];
        int dg = fb[offset + 1];
        int dr = fb[offset + 2];
        int invAlpha = 256 - alpha;
        fb[offset] = (byte)(((sb * alpha) + (db * invAlpha)) >> 8);
        fb[offset + 1] = (byte)(((sg * alpha) + (dg * invAlpha)) >> 8);
        fb[offset + 2] = (byte)(((sr * alpha) + (dr * invAlpha)) >> 8);
        fb[offset + 3] = 0xff;
    }

    public int GetAlpha(int index)
    {
        index &= 0x1f;
        int alpha = _aceRam[index] & 0xff;
        if (alpha > 0x20)
            return 0x80;
        alpha = 255 - (alpha << 3);
        return Math.Max(alpha, 0);
    }

    public int GetObjectAlpha(int index)
    {
        index &= 0x1f;
        if ((_aceRam[index] & 0xff) == 0 && index <= 0x05)
            return 0x80;
        return GetAlpha(index);
    }

    public int GetTilemapAlpha(int index)
    {
        index &= 0x1f;
        if ((_aceRam[index] & 0xff) == 0)
            return 0x80;
        return GetAlpha(index);
    }

    public bool HasProgrammedObjectAlphaControls()
    {
        for (int i = 0; i <= 0x05; i++)
        {
            if ((_aceRam[i] & 0xff) != 0)
                return true;
        }

        return false;
    }

    public ushort GetAceRam(int index)
        => (uint)index < (uint)_aceRam.Length ? _aceRam[index] : (ushort)0;

    public string DebugSummary
    {
        get
        {
            int rawNonzero = 0;
            int stagedNonzero = 0;
            int visibleNonzero = 0;
            int firstVisible = -1;
            for (int i = 0; i < _buffered.Length; i++)
            {
                if ((_ram[i] & 0x00ffffff) != 0)
                    stagedNonzero++;
                if ((_buffered[i] & 0x00ffffff) != 0)
                    rawNonzero++;
                if ((_colors[i] & 0x00ffffff) == 0)
                    continue;
                if (firstVisible < 0)
                    firstVisible = i;
                visibleNonzero++;
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"ace=[0x{_aceRam[0x20]:X4},0x{_aceRam[0x21]:X4},0x{_aceRam[0x22]:X4},0x{_aceRam[0x23]:X4},0x{_aceRam[0x24]:X4},0x{_aceRam[0x25]:X4},0x{_aceRam[0x26]:X4}] paldma={_dmaCount} palnz={stagedNonzero}/{rawNonzero}/{visibleNonzero}/first{firstVisible}");
        }
    }

    private void UpdatePalette()
    {
        int fadePtr = _aceRam[0x20] & 0xff;
        int fadePtg = _aceRam[0x21] & 0xff;
        int fadePtb = _aceRam[0x22] & 0xff;
        int fadeStr = _aceRam[0x23] & 0xff;
        int fadeStg = _aceRam[0x24] & 0xff;
        int fadeStb = _aceRam[0x25] & 0xff;
        int mode = _aceRam[0x26] & 0xffff;

        for (int i = 0; i < _buffered.Length; i++)
        {
            uint value = _buffered[i];
            int b = (int)((value >> 16) & 0xff);
            int g = (int)((value >> 8) & 0xff);
            int r = (int)(value & 0xff);
            _colors[i + 2048] = 0xff000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;

            if (mode == 0x1000)
            {
                b = Math.Min(b + fadeStb, 0xff);
                g = Math.Min(g + fadeStg, 0xff);
                r = Math.Min(r + fadeStr, 0xff);
            }
            else
            {
                b = (byte)(b + (((fadePtb - b) * fadeStb) / 255));
                g = (byte)(g + (((fadePtg - g) * fadeStg) / 255));
                r = (byte)(r + (((fadePtr - r) * fadeStr) / 255));
            }

            _colors[i] = 0xff000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b;
        }
    }

    private static uint CombineMasked(uint oldValue, uint value, uint mask)
    {
        for (int i = 0; i < 4; i++)
        {
            uint byteMask = 0xffu << (i * 8);
            if ((mask & byteMask) != 0)
                oldValue = (oldValue & ~byteMask) | (value & byteMask);
        }

        return oldValue;
    }

    private static ushort CombineMasked16(ushort oldValue, uint value, uint mask)
    {
        uint lowMask = mask & 0x0000ffff;
        return (ushort)((oldValue & ~lowMask) | (value & lowMask));
    }

    private static void WriteBgra(byte[] fb, int offset, uint argb)
    {
        fb[offset] = (byte)argb;
        fb[offset + 1] = (byte)(argb >> 8);
        fb[offset + 2] = (byte)(argb >> 16);
        fb[offset + 3] = 0xff;
    }

}

public sealed class Z80SoundCpu : IOpcodeBusInterface
{
    private const int AudioCpuClock = 32_220_000 / 9;
    private const int OutputChannels = 2;
    private const int MaxChipLogsPerFrame = 100;
    private static readonly bool TraceSound =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DECO32_TRACE_SOUND"), "1", StringComparison.Ordinal);
    private static readonly bool TraceOki =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DECO32_TRACE_OKI"), "1", StringComparison.Ordinal);
    private static readonly bool MuteYm =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DECO32_MUTE_YM"), "1", StringComparison.Ordinal);
    private static readonly bool MuteOki1 =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DECO32_MUTE_OKI1"), "1", StringComparison.Ordinal);
    private static readonly bool MuteOki2 =
        string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_DECO32_MUTE_OKI2"), "1", StringComparison.Ordinal);

    [NonSerialized]
    private readonly byte[] _rom;
    private readonly byte[] _ram = new byte[0x800];
    private readonly Z80 _cpu = new();
    [NonSerialized]
    private readonly YM2151 _ym;
    [NonSerialized]
    private readonly OKI6295 _oki1;
    [NonSerialized]
    private readonly OKI6295 _oki2;
    private byte _soundLatch = 0xff;
    private bool _latchIrqAsserted;
    private int _frameCounter;
    private int _chipLogsThisFrame;
    private int _latchWrites;
    private int _latchReads;
    private int _irqAccepts;
    private int _lastPeak;
    private byte _lastOki1Status = 0xff;
    private byte _lastOki2Status = 0xff;

    public Z80SoundCpu(byte[] rom, YM2151 ym, OKI6295 oki1, OKI6295 oki2)
    {
        _rom = rom;
        _ym = ym;
        _oki1 = oki1;
        _oki2 = oki2;
    }

    public string DebugSummary
        => $"z80pc=0x{_cpu.Pc:X4} sndLatch=0x{_soundLatch:X2} sndW={_latchWrites} sndR={_latchReads} z80Irq={(InterruptAsserted ? 1 : 0)}/{_irqAccepts} audPeak={_lastPeak} {_ym.DebugSummary} oki1={_oki1.DebugSummary} oki2={_oki2.DebugSummary}";

    public void ResetSound()
    {
        Array.Clear(_ram);
        _cpu.ApplyResetLine();
        _soundLatch = 0xff;
        _latchIrqAsserted = false;
        _frameCounter = 0;
        _chipLogsThisFrame = 0;
        _latchWrites = 0;
        _latchReads = 0;
        _irqAccepts = 0;
        _lastPeak = 0;
        _lastOki1Status = 0xff;
        _lastOki2Status = 0xff;
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        StateBinarySerializer.WriteInto(writer, this);
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        StateBinarySerializer.ReadInto(reader, this);
    }

    public void WriteSoundLatch(byte value, uint mainPc)
    {
        _soundLatch = value;
        _latchIrqAsserted = true;
        _latchWrites++;
        if (TraceSound)
        {
            Console.WriteLine($"[SND CMD] pc=0x{mainPc:X8} val=0x{value:X2}");
            Console.WriteLine($"[Z80 IRQ] assert pc=0x{_cpu.Pc:X4}");
        }
    }

    public void RunFrame(short[] audioBuffer)
    {
        Array.Clear(audioBuffer);
        _chipLogsThisFrame = 0;
        _ym.BeginFrame();
        _oki1.BeginFrame();
        _oki2.BeginFrame();

        int cycles = 0;
        int budget = AudioCpuClock / 60;
        int sampleFrames = audioBuffer.Length / OutputChannels;
        int ymIndex = 0;
        int oki1Index = 0;
        int oki2Index = 0;

        void RenderAudioTo(int targetSampleFrames)
        {
            targetSampleFrames = Math.Clamp(targetSampleFrames, 0, sampleFrames);
            if (!MuteYm)
                _ym.RenderStereo(audioBuffer, ref ymIndex, targetSampleFrames);
            if (!MuteOki1)
                _oki1.RenderStereo(audioBuffer, ref oki1Index, targetSampleFrames);
            if (!MuteOki2)
                _oki2.RenderStereo(audioBuffer, ref oki2Index, targetSampleFrames);
        }

        while (cycles < budget)
        {
            uint elapsed = _cpu.ExecuteInstruction(this);
            int elapsedCycles = Math.Max(1, (int)elapsed);
            cycles += elapsedCycles;
            _ym.AdvanceTimersByCpuCycles(elapsedCycles, AudioCpuClock);
            int targetSampleFrames = (int)Math.Min(sampleFrames, ((long)cycles * sampleFrames) / budget);
            RenderAudioTo(targetSampleFrames);
            if (_cpu.LastInterruptAccepted)
            {
                _irqAccepts++;
                if (TraceSound && _irqAccepts <= 32)
                    Console.WriteLine($"[Z80 IRQ] accepted pc=0x{_cpu.Pc:X4}");
            }
        }

        RenderAudioTo(sampleFrames);
        int mixPeak = Deco32AudioUtil.Peak(audioBuffer, 0, sampleFrames);
        _lastPeak = mixPeak;
        if (TraceSound && (mixPeak != 0 || (_frameCounter % 60) == 0))
            Console.WriteLine($"[AUDIO] ym={_ym.LastPeak} oki1={_oki1.LastPeak} oki2={_oki2.LastPeak} mix={mixPeak}");
        _frameCounter++;
    }

    public byte ReadOpcode(ushort address) => ReadMemory(address);

    public byte ReadMemory(ushort address)
    {
        if (address < 0x8000)
            return _rom.Length == 0 ? (byte)0xff : _rom[address % _rom.Length];
        if (address is >= 0x8000 and <= 0x87ff)
            return _ram[address - 0x8000];
        if (address is >= 0xa000 and <= 0xa001)
            return _ym.Read((byte)(address & 1));
        if (address == 0xb000)
        {
            byte status = _oki1.ReadStatus();
            if (status != _lastOki1Status)
            {
                _lastOki1Status = status;
                LogOki("OKI1 status", address, status);
            }
            return status;
        }
        if (address == 0xc000)
        {
            byte status = _oki2.ReadStatus();
            if (status != _lastOki2Status)
            {
                _lastOki2Status = status;
                LogOki("OKI2 status", address, status);
            }
            return status;
        }
        if (address == 0xd000)
        {
            _latchReads++;
            _latchIrqAsserted = false;
            if (TraceSound)
                Console.WriteLine($"[Z80 LATCH READ] pc=0x{_cpu.Pc:X4} val=0x{_soundLatch:X2}");
            return _soundLatch;
        }

        return 0xff;
    }

    public void WriteMemory(ushort address, byte value)
    {
        if (address is >= 0x8000 and <= 0x87ff)
        {
            _ram[address - 0x8000] = value;
            return;
        }
        if (address is >= 0xa000 and <= 0xa001)
        {
            LogChipWrite(address == 0xa000 ? "YM2151 reg" : "YM2151 data", address, value);
            _ym.Write((byte)(address & 1), value);
            return;
        }
        if (address == 0xb000)
        {
            LogOki("OKI1 write", address, value);
            _oki1.Write(value);
            return;
        }
        if (address == 0xc000)
        {
            LogOki("OKI2 write", address, value);
            _oki2.Write(value);
            return;
        }
    }

    public byte ReadIo(ushort address)
        => _rom.Length == 0 ? (byte)0xff : _rom[address % _rom.Length];

    public void WriteIo(ushort address, byte value)
    {
        if (TraceSound)
            LogChipWrite("unmapped IO", address, value);
    }

    public InterruptLine Nmi() => InterruptLine.High;
    private bool InterruptAsserted => _latchIrqAsserted || _ym.IrqAsserted;
    public InterruptLine Int() => InterruptAsserted ? InterruptLine.Low : InterruptLine.High;
    public byte InterruptVector() => 0xff;
    public bool BusReq() => false;
    public bool Reset() => false;

    private void LogChipWrite(string target, ushort address, byte value)
    {
        if (_chipLogsThisFrame++ >= MaxChipLogsPerFrame)
            return;
        if (TraceSound)
            Console.WriteLine($"[Z80 WRITE] pc=0x{_cpu.Pc:X4} {target} addr=0x{address:X4} val=0x{value:X2}");
    }

    private void LogOki(string target, ushort address, byte value)
    {
        if (!TraceOki)
        {
            LogChipWrite(target, address, value);
            return;
        }

        if (_chipLogsThisFrame++ >= MaxChipLogsPerFrame)
            return;
        Console.WriteLine($"[Z80 OKI] f={_frameCounter} pc=0x{_cpu.Pc:X4} {target} addr=0x{address:X4} val=0x{value:X2} oki1={_oki1.DebugSummary} oki2={_oki2.DebugSummary}");
    }
}

public sealed class YM2151
{
    private readonly Cps1Ym2151 _core = new();
    [NonSerialized]
    private readonly Action<byte>? _portWrite;
    private byte _selectedRegister;
    private int _registerWrites;
    private int _dataWrites;
    public int LastPeak { get; private set; }

    public bool IrqAsserted => _core.IrqAsserted;
    public string DebugSummary => $"{_core.DebugSummary} ymW={_registerWrites}/{_dataWrites} ymPeak={LastPeak}";

    public YM2151(Action<byte>? portWrite = null)
    {
        _portWrite = portWrite;
    }

    public void Reset()
    {
        _core.Reset();
        _selectedRegister = 0;
        _registerWrites = 0;
        _dataWrites = 0;
        LastPeak = 0;
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        StateBinarySerializer.WriteInto(writer, this);
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        StateBinarySerializer.ReadInto(reader, this);
    }
    public byte ReadStatus() => _core.ReadStatus();
    public byte Read(byte offset) => ReadStatus();
    public void Write(byte offset, byte value)
    {
        _core.Write(offset, value);
        if ((offset & 1) == 0)
        {
            _selectedRegister = value;
            _registerWrites++;
        }
        else
        {
            _dataWrites++;
            if (_selectedRegister == 0x1b)
                _portWrite?.Invoke((byte)(value >> 6));
        }
    }

    public void AdvanceTimersByCpuCycles(int cpuCycles, double cpuClockHz)
        => _core.AdvanceTimersByCpuCycles(cpuCycles, cpuClockHz);

    public void BeginFrame()
    {
        LastPeak = 0;
    }

    public void RenderStereo(short[] destination, ref int sampleFrameIndex, int targetSampleFrames)
    {
        int before = sampleFrameIndex;
        _core.RenderStereo(destination, ref sampleFrameIndex, targetSampleFrames, gain: 0.40f);
        LastPeak = Math.Max(LastPeak, Deco32AudioUtil.Peak(destination, before, sampleFrameIndex));
    }
}

public sealed class OKI6295
{
    private const int BankSize = 0x40000;
    [NonSerialized]
    private readonly byte[] _rom;
    private readonly Cps1Oki6295 _core = new();
    private readonly float _gain;
    private readonly int _clockHz;
    private int _bank;
    private int _writes;
    public int LastPeak { get; private set; }

    public OKI6295(byte[] rom, int clockHz, float routeGain)
    {
        _rom = rom ?? Array.Empty<byte>();
        _clockHz = Math.Max(1, clockHz);
        _gain = routeGain * 0.30f;
        _core.SetClock(_clockHz);
        _core.SetPin7(true);
        LoadBank(0, reset: true);
    }

    public string DebugSummary => $"bank={_bank} clk={_clockHz} writes={_writes} peak={LastPeak}";
    public void Reset()
    {
        _bank = 0;
        _writes = 0;
        LastPeak = 0;
        LoadBank(0, reset: true);
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        StateBinarySerializer.WriteInto(writer, this);
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        StateBinarySerializer.ReadInto(reader, this);
        LoadBank(_bank, reset: false);
    }
    public byte ReadStatus() => _core.ReadStatus();
    public void Write(byte value)
    {
        _core.Write(value);
        _writes++;
    }
    public void SetRomBank(int bank)
    {
        bank &= 1;
        if (_bank == bank)
            return;

        _bank = bank;
        LoadBank(bank, reset: false);
    }

    public void BeginFrame()
    {
        LastPeak = 0;
    }

    public void RenderStereo(short[] destination, ref int sampleFrameIndex, int targetSampleFrames)
    {
        int before = sampleFrameIndex;
        _core.RenderStereo(destination, ref sampleFrameIndex, targetSampleFrames, gain: _gain);
        LastPeak = Math.Max(LastPeak, Deco32AudioUtil.Peak(destination, before, sampleFrameIndex));
    }

    private void LoadBank(int bank, bool reset)
    {
        if (_rom.Length <= BankSize)
        {
            LoadRomWindow(_rom, reset);
            return;
        }

        byte[] window = new byte[BankSize];
        int source = Math.Min(bank * BankSize, Math.Max(0, _rom.Length - BankSize));
        int count = Math.Min(BankSize, _rom.Length - source);
        if (count > 0)
            Array.Copy(_rom, source, window, 0, count);
        LoadRomWindow(window, reset);
    }

    private void LoadRomWindow(byte[] rom, bool reset)
    {
        if (reset)
            _core.Load(rom);
        else
            _core.ReplaceRom(rom);
    }
}

internal static class Deco32AudioUtil
{
    public static int Peak(short[] destination, int startFrame, int endFrame)
    {
        int start = Math.Clamp(startFrame * 2, 0, destination.Length);
        int end = Math.Clamp(endFrame * 2, start, destination.Length);
        int peak = 0;
        for (int i = start; i < end; i++)
        {
            int abs = Math.Abs(destination[i]);
            if (abs > peak)
                peak = abs;
        }

        return peak;
    }
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
    private const uint Arm26PsrMask = 0xf0000000u;
    private const uint Arm26IrqMask = 0x0c000000u;
    private const uint Arm26AddressMask = 0x03fffffcu;
    private const uint Arm26IMask = 0x08000000u;
    private const uint Arm26FMask = 0x04000000u;

    [NonSerialized]
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
    private bool _pcWritten;

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
        _pcWritten = false;
    }

    public void SetIrqLine(bool asserted) => _irqLine = asserted;
    public void AttachBus(IArm6Bus bus) => _bus = bus ?? throw new ArgumentNullException(nameof(bus));
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
            uint savedPc = PackArm26R15(Registers[15] + 4);
            _spsrIrq = Cpsr;
            ChangeMode(0x12);
            Registers[14] = savedPc;
            Registers[15] = 0x18;
            _irqDisable = true;
            return 3;
        }

        uint pc = Registers[15] & ~3u;
        uint op = _bus.Read32(pc);
        Registers[15] = pc + 8;
        _pcWritten = false;
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

        if (!Halted && !_pcWritten && Registers[15] == pc + 8)
            Registers[15] = pc + 4;
        return cycles;
    }

    private int Branch(uint op, uint pc)
    {
        if ((op & 0x01000000) != 0)
            Registers[14] = PackArm26R15(pc + 4);
        int disp = (int)(op & 0x00ffffff);
        if ((disp & 0x00800000) != 0)
            disp |= unchecked((int)0xff000000);
        Registers[15] = (uint)(pc + 8 + (disp << 2));
        _pcWritten = true;
        return 3;
    }

    private int DataProcessing(uint op)
    {
        int opcode = (int)((op >> 21) & 0xf);
        bool setFlags = (op & 0x00100000) != 0;
        int rn = (int)((op >> 16) & 0xf);
        int rd = (int)((op >> 12) & 0xf);

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

        uint a = rn == 15 ? Registers[15] & Arm26AddressMask : ReadReg(rn);
        uint b = Operand2(op, out bool shifterCarry);
        uint result;
        switch (opcode)
        {
            case 0x0: result = a & b; WriteReg(rd, result); if (setFlags) SetDataFlags(rd, result, shifterCarry); break;
            case 0x1: result = a ^ b; WriteReg(rd, result); if (setFlags) SetDataFlags(rd, result, shifterCarry); break;
            case 0x2:
                result = a - b;
                WriteReg(rd, result);
                if (setFlags) SetDataFlags(rd, result, () => SetSubFlags(a, b, result));
                break;
            case 0x3: result = b - a; WriteReg(rd, result); if (setFlags) SetDataFlags(rd, result, () => SetSubFlags(b, a, result)); break;
            case 0x4: result = a + b; WriteReg(rd, result); if (setFlags) SetDataFlags(rd, result, () => SetAddFlags(a, b, result)); break;
            case 0x5: result = a + b + (_flagC ? 1u : 0u); WriteReg(rd, result); if (setFlags) SetDataFlags(rd, result, () => SetAddFlags(a, b + (_flagC ? 1u : 0u), result)); break;
            case 0x6: result = a - b - (_flagC ? 0u : 1u); WriteReg(rd, result); if (setFlags) SetDataFlags(rd, result, () => SetSubFlags(a, b + (_flagC ? 0u : 1u), result)); break;
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
        uint baseAddr = rn == 15 ? Registers[15] & Arm26AddressMask : ReadReg(rn);
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
        bool immediateOffset = (op & 0x02000000) == 0;
        bool pre = (op & 0x01000000) != 0;
        bool up = (op & 0x00800000) != 0;
        bool byteAccess = (op & 0x00400000) != 0;
        bool writeback = (op & 0x00200000) != 0;
        bool load = (op & 0x00100000) != 0;
        int rn = (int)((op >> 16) & 0xf);
        int rd = (int)((op >> 12) & 0xf);
        uint offset = immediateOffset ? op & 0xfff : SingleTransferRegisterOffset(op);
        uint baseAddr = rn == 15 ? Registers[15] & Arm26AddressMask : ReadReg(rn);
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
        bool sBit = (op & 0x00400000) != 0;
        bool writeback = (op & 0x00200000) != 0;
        bool load = (op & 0x00100000) != 0;
        int rn = (int)((op >> 16) & 0xf);
        uint list = op & 0xffff;
        uint baseAddr = rn == 15 ? Registers[15] & Arm26AddressMask : ReadReg(rn);
        int count = 0;
        for (int i = 0; i < 16; i++) if (((list >> i) & 1) != 0) count++;
        uint address = up ? baseAddr : baseAddr - (uint)(count * 4);
        if (pre && up) address += 4;
        if (!pre && !up) address += 4;
        bool transferUserBank = sBit && (!load || (list & 0x8000) == 0);
        bool deferPc = false;
        uint deferredPc = 0;
        for (int i = 0; i < 16; i++)
        {
            if (((list >> i) & 1) == 0)
                continue;
            if (load)
            {
                uint value = _bus!.Read32(address);
                if (i == 15)
                {
                    deferPc = true;
                    deferredPc = sBit ? value : MergePcAddressPreserveStatus(value);
                }
                else
                    WriteTransferReg(i, value, transferUserBank);
            }
            else
            {
                uint value = i == 15
                    ? PackArm26R15(Registers[15] + 4)
                    : ReadTransferReg(i, transferUserBank);
                _bus!.Write32(address, value, 0xffffffff);
            }
            address += 4;
        }
        if (writeback && (!load || (list & (1u << rn)) == 0))
            WriteReg(rn, up ? baseAddr + (uint)(count * 4) : baseAddr - (uint)(count * 4));
        if (deferPc)
        {
            if (sBit)
                ApplyArm26R15(deferredPc);
            else
                WritePcAddressPreserveStatus(deferredPc);
        }
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

        uint value = ReadShiftOperandReg((int)(op & 0xf));
        int type = (int)((op >> 5) & 3);
        bool registerShift = (op & 0x10) != 0;
        int amount = registerShift ? (int)(ReadShiftAmountReg((int)((op >> 8) & 0xf)) & 0xff) : (int)((op >> 7) & 0x1f);
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

    private uint SingleTransferRegisterOffset(uint op)
    {
        uint value = ReadShiftOperandReg((int)(op & 0xf));
        int type = (int)((op >> 5) & 3);
        int amount = (int)((op >> 7) & 0x1f);
        if (amount == 0)
        {
            return type switch
            {
                0 => value,
                1 => 0,
                2 => (value & 0x80000000) != 0 ? 0xffffffffu : 0,
                _ => (_flagC ? 0x80000000u : 0) | (value >> 1)
            };
        }

        return type switch
        {
            0 => value << amount,
            1 => value >> amount,
            2 => (uint)((int)value >> amount),
            _ => Ror(value, amount)
        };
    }

    private uint ReadReg(int reg) => reg == 15 ? PackArm26R15(Registers[15]) : Registers[reg];
    private uint ReadShiftOperandReg(int reg) => reg == 15 ? PackArm26R15(Registers[15]) : Registers[reg];
    private uint ReadShiftAmountReg(int reg) => reg == 15 ? PackArm26R15(Registers[15]) : Registers[reg];
    private void WriteReg(int reg, uint value)
    {
        if (reg == 15)
            WritePcAddressPreserveStatus(value);
        else
            Registers[reg] = value;
    }

    private void WritePcAddressPreserveStatus(uint value)
    {
        Registers[15] = value & Arm26AddressMask;
        _pcWritten = true;
    }

    private uint MergePcAddressPreserveStatus(uint value)
        => (value & Arm26AddressMask) | (PackArm26R15(Registers[15]) & ~Arm26AddressMask);

    private uint ReadTransferReg(int reg, bool userBank)
    {
        if (!userBank)
            return ReadReg(reg);
        return reg switch
        {
            13 => _userR13,
            14 => _userR14,
            15 => PackArm26R15(Registers[15]),
            _ => Registers[reg]
        };
    }

    private void WriteTransferReg(int reg, uint value, bool userBank)
    {
        if (!userBank)
        {
            WriteReg(reg, value);
            return;
        }

        switch (reg)
        {
            case 13:
                _userR13 = value;
                if (_mode == 0x10)
                    Registers[13] = value;
                break;
            case 14:
                _userR14 = value;
                if (_mode == 0x10)
                    Registers[14] = value;
                break;
            case 15:
                WriteReg(reg, value);
                break;
            default:
                Registers[reg] = value;
                break;
        }
    }

    private uint PackArm26R15(uint address)
    {
        uint value = address & Arm26AddressMask;
        if (_flagN) value |= 0x80000000u;
        if (_flagZ) value |= 0x40000000u;
        if (_flagC) value |= 0x20000000u;
        if (_flagV) value |= 0x10000000u;
        if (_irqDisable) value |= Arm26IMask;
        if (_fiqDisable) value |= Arm26FMask;
        value |= ModeToArm26(_mode);
        return value;
    }

    private void ApplyArm26R15(uint value)
    {
        byte newMode = Arm26ToMode(value & 3);
        if (newMode == 0x10 && _mode != 0x10 && _userR13 == 0 && Registers[13] != 0)
        {
            _userR13 = Registers[13];
            _userR14 = Registers[14];
        }
        _flagN = (value & 0x80000000u) != 0;
        _flagZ = (value & 0x40000000u) != 0;
        _flagC = (value & 0x20000000u) != 0;
        _flagV = (value & 0x10000000u) != 0;
        _irqDisable = (value & Arm26IMask) != 0;
        _fiqDisable = (value & Arm26FMask) != 0;
        ChangeMode(newMode);
        Registers[15] = value & Arm26AddressMask;
        _pcWritten = true;
    }

    private void WriteDataProcessingPcArm26(uint value)
    {
        if (ModeToArm26(_mode) != 0)
        {
            ApplyArm26R15(value);
            return;
        }

        _flagN = (value & 0x80000000u) != 0;
        _flagZ = (value & 0x40000000u) != 0;
        _flagC = (value & 0x20000000u) != 0;
        _flagV = (value & 0x10000000u) != 0;
        Registers[15] = value & Arm26AddressMask;
        _pcWritten = true;
    }

    private static uint ModeToArm26(byte mode) => mode switch
    {
        0x11 => 1,
        0x12 => 2,
        0x13 => 3,
        _ => 0
    };

    private static byte Arm26ToMode(uint mode) => mode switch
    {
        1 => 0x11,
        2 => 0x12,
        3 => 0x13,
        _ => 0x10
    };

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
            WriteDataProcessingPcArm26(result);
        else
            SetLogicFlags(result, carry);
    }

    private void SetDataFlags(int rd, uint result, Action update)
    {
        if (rd == 15)
            WriteDataProcessingPcArm26(result);
        else
            update();
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
