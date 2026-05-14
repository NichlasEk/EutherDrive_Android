using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using EutherDrive.Core.Cpu.M68000Emu;
using EutherDrive.Core.Savestates;
using EutherDrive.Platforms.DataEast.Deco32;
using SharpCompress.Archives;

namespace EutherDrive.Core.Arcade.DataEast.Boogwing;

// Boogie Wings / The Great Ragtime Show hardware notes are translated from
// MAME's BSD-3-Clause dataeast/boogwing.cpp driver.
public sealed class BoogwingAdapter : IEmulatorCore, ISavestateCapable
{
    private const string SavestateMagic = "BOOGWINGST";
    private const int SavestateVersion = 3;
    private const int FrameWidth = 320;
    private const int FrameHeight = 240;
    private const int FrameStride = FrameWidth * 4;
    private const int OutputSampleRate = 44_100;
    private const int OutputChannels = 2;
    private const int MainCpuClockHz = 28_000_000 / 2;
    private const double TargetFps = 28_000_000.0 / 4.0 / 442.0 / 274.0;
    private const int CpuCyclesPerFrame = (int)(MainCpuClockHz / TargetFps);

    private readonly object _frameSync = new();
    private byte[] _presentFrameBuffer = new byte[FrameHeight * FrameStride];
    private byte[] _renderFrameBuffer = new byte[FrameHeight * FrameStride];
    private byte[] _snapshotFrameBuffer = new byte[FrameHeight * FrameStride];
    private readonly short[] _audioBuffer = new short[(OutputSampleRate / 60) * OutputChannels];
    private short[] _scaledAudioBuffer = Array.Empty<short>();
    private readonly M68000 _mainCpu = M68000.CreateBuilder()
        .AllowTasWrites(true)
        .Name("dataeast-boogwing-main")
        .Build();
    private readonly BoogwingBus _bus = new();
    private ArcadeInputState _input;
    private ArcadeInputState _input2;
    private int _masterVolumePercent = 100;
    private bool _loaded;
    private long _frameCounter;
    private RomIdentity? _romIdentity;
    private string? _lastFault;

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string name = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
        return name is "boogwing" or "boogwingu" or "boogwinga" or "ragtime" or "ragtimea";
    }

    public RomIdentity? RomIdentity => _romIdentity;
    public long? FrameCounter => _loaded ? _frameCounter : null;
    public double GetTargetFps() => TargetFps;
    public string DebugSummary
    {
        get
        {
            M68000.M68000State state = _mainCpu.GetState();
            return string.Create(
                CultureInfo.InvariantCulture,
                $"pc=0x{_mainCpu.Pc:X6} op=0x{_mainCpu.NextOpcode:X4} sr=0x{_mainCpu.StatusRegister:X4} d0=0x{state.Data[0]:X8} a1=0x{state.Address[1]:X8} irq={_bus.InterruptLevel()} frame={_frameCounter} {_bus.DebugSummary}{(string.IsNullOrWhiteSpace(_lastFault) ? string.Empty : " fault=" + _lastFault)}");
        }
    }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Boogie Wings ROM path is empty.", nameof(path));
        if (!RomArchiveExtractor.FileExists(path))
            throw new FileNotFoundException("Boogie Wings ROM archive not found.", path);

        byte[] romHash;
        using (Stream stream = RomArchiveExtractor.OpenRead(path))
            romHash = RomIdentity.ComputeSha256(stream);

        BoogwingRomSet roms = BoogwingRomSet.Load(path);
        _bus.Load(roms);
        _mainCpu.Reset(_bus);
        _loaded = true;
        _frameCounter = 0;
        _lastFault = null;
        Array.Clear(_audioBuffer);
        Array.Clear(_presentFrameBuffer);
        Array.Clear(_renderFrameBuffer);
        Array.Clear(_snapshotFrameBuffer);
        _romIdentity = new RomIdentity(
            Path.GetFileName(path),
            romHash,
            PersistentStoragePath.ResolveSavestateDirectory(path, "boogwing"));
        RenderFrame();
    }

    public void Reset()
    {
        if (!_loaded)
            return;
        _bus.ResetBoard();
        _mainCpu.Reset(_bus);
        _frameCounter = 0;
        _lastFault = null;
        Array.Clear(_audioBuffer);
        RenderFrame();
    }

    public void RunFrame()
    {
        if (!_loaded)
            return;

        _bus.SetInput(_input);
        _bus.SetInput2(_input2);
        _bus.BeginFrame();
        ExecuteMainCpu(CpuCyclesPerFrame * 240 / 274);
        _bus.AssertVblank();
        ExecuteMainCpu(768);
        _bus.EndVblank();
        ExecuteMainCpu(Math.Max(1, CpuCyclesPerFrame * 34 / 274 - 768));
        _bus.EndFrame();
        RenderFrame();
        Array.Clear(_audioBuffer);
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
        if (_masterVolumePercent == 100)
            return _audioBuffer;
        if (_scaledAudioBuffer.Length < _audioBuffer.Length)
            _scaledAudioBuffer = new short[_audioBuffer.Length];
        for (int i = 0; i < _audioBuffer.Length; i++)
            _scaledAudioBuffer[i] = (short)Math.Clamp((_audioBuffer[i] * _masterVolumePercent) / 100, short.MinValue, short.MaxValue);
        return _scaledAudioBuffer.AsSpan(0, _audioBuffer.Length);
    }

    public void SetMasterVolumePercent(int percent)
        => _masterVolumePercent = Math.Clamp(percent, 0, 200);

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

    public void SetPad2InputState(
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
        _input2 = new ArcadeInputState(up, down, left, right, a, b, c, start, x, y, z, mode);
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(SavestateMagic);
        writer.Write(SavestateVersion);
        writer.Write(_frameCounter);
        writer.Write(_masterVolumePercent);
        writer.Write(_lastFault is not null);
        if (_lastFault is not null)
            writer.Write(_lastFault);
        WriteInputState(writer, _input);
        WriteInputState(writer, _input2);
        WriteByteArray(writer, _presentFrameBuffer);
        WriteByteArray(writer, _renderFrameBuffer);
        StateBinarySerializer.WriteInto(writer, _mainCpu);
        _bus.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        string magic = reader.ReadString();
        if (!string.Equals(magic, SavestateMagic, StringComparison.Ordinal))
            throw new InvalidDataException("Boogie Wings savestate magic mismatch.");
        int version = reader.ReadInt32();
        if (version is < 1 or > SavestateVersion)
            throw new InvalidDataException($"Unsupported Boogie Wings savestate version: {version}.");
        _frameCounter = reader.ReadInt64();
        _masterVolumePercent = reader.ReadInt32();
        _lastFault = reader.ReadBoolean() ? reader.ReadString() : null;
        _input = ReadInputState(reader);
        _input2 = version >= 3 ? ReadInputState(reader) : default;
        ReadByteArray(reader, _presentFrameBuffer);
        ReadByteArray(reader, _renderFrameBuffer);
        StateBinarySerializer.ReadInto(reader, _mainCpu);
        _bus.LoadState(reader);
    }

    private void ExecuteMainCpu(int cycleBudget)
    {
        int cycles = 0;
        while (cycles < cycleBudget && !_mainCpu.IsFrozen)
        {
            try
            {
                cycles += Math.Max(1, checked((int)_mainCpu.ExecuteInstruction(_bus)));
            }
            catch (Exception ex)
            {
                _lastFault = ex.GetType().Name + ": " + ex.Message;
                break;
            }
        }

        if (_mainCpu.IsFrozen || _mainCpu.AddressError)
            _lastFault ??= "m68k frozen/address error";
    }

    private void RenderFrame()
    {
        _bus.Render(_renderFrameBuffer, FrameWidth, FrameHeight, FrameStride, _frameCounter);
        if (!string.IsNullOrWhiteSpace(_lastFault))
            DrawText(_renderFrameBuffer, FrameWidth, FrameHeight, FrameStride, 8, 8, _lastFault);
        lock (_frameSync)
            Buffer.BlockCopy(_renderFrameBuffer, 0, _presentFrameBuffer, 0, _renderFrameBuffer.Length);
    }

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
        byte[] data = reader.ReadBytes(length);
        Array.Clear(target);
        Buffer.BlockCopy(data, 0, target, 0, Math.Min(data.Length, target.Length));
    }

    private static void DrawText(byte[] fb, int width, int height, int stride, int x, int y, string text)
    {
        for (int i = 0; i < text.Length && x + i * 6 < width - 6; i++)
            DrawGlyph(fb, width, height, stride, x + i * 6, y, text[i]);
    }

    private static void DrawGlyph(byte[] fb, int width, int height, int stride, int x, int y, char ch)
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
                fb[p] = 0x40;
                fb[p + 1] = 0xff;
                fb[p + 2] = 0xff;
                fb[p + 3] = 0xff;
            }
        }
    }

    private static ReadOnlySpan<byte> Glyph5x7(char ch)
        => char.ToUpperInvariant(ch) switch
        {
            'A' => [0x0e, 0x11, 0x11, 0x1f, 0x11, 0x11, 0x11],
            'B' => [0x1e, 0x11, 0x11, 0x1e, 0x11, 0x11, 0x1e],
            'C' => [0x0e, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0e],
            'D' => [0x1e, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1e],
            'E' => [0x1f, 0x10, 0x10, 0x1e, 0x10, 0x10, 0x1f],
            'F' => [0x1f, 0x10, 0x10, 0x1e, 0x10, 0x10, 0x10],
            'G' => [0x0e, 0x10, 0x10, 0x17, 0x11, 0x11, 0x0e],
            'H' => [0x11, 0x11, 0x11, 0x1f, 0x11, 0x11, 0x11],
            'I' => [0x0e, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0e],
            'J' => [0x01, 0x01, 0x01, 0x01, 0x11, 0x11, 0x0e],
            'K' => [0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11],
            'L' => [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1f],
            'M' => [0x11, 0x1b, 0x15, 0x15, 0x11, 0x11, 0x11],
            'N' => [0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11],
            'O' => [0x0e, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0e],
            'P' => [0x1e, 0x11, 0x11, 0x1e, 0x10, 0x10, 0x10],
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
            ':' => [0x00, 0x0c, 0x0c, 0x00, 0x0c, 0x0c, 0x00],
            '-' => [0x00, 0x00, 0x00, 0x1f, 0x00, 0x00, 0x00],
            ' ' => [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            _ => [0x1f, 0x11, 0x15, 0x15, 0x11, 0x11, 0x1f]
        };
}

internal sealed class BoogwingBus : IBusInterface, IOpcodeBusInterface
{
    private readonly byte[] _workRam = new byte[0x10000];
    private readonly ushort[][] _pf = { new ushort[0x1000], new ushort[0x1000], new ushort[0x1000], new ushort[0x1000] };
    private readonly ushort[][] _rowscroll = { new ushort[0x800], new ushort[0x800], new ushort[0x800], new ushort[0x800] };
    private readonly ushort[][] _control = { new ushort[8], new ushort[8] };
    private readonly ushort[][] _spriteRam = { new ushort[0x400], new ushort[0x400] };
    private readonly ushort[][] _spriteBuffered = { new ushort[0x400], new ushort[0x400] };
    private readonly uint[] _paletteRam = new uint[0x800];
    private readonly uint[] _paletteBuffered = new uint[0x800];
    private readonly uint[] _colors = new uint[0x1000];
    private readonly ushort[] _ace = new ushort[0x28];
    private readonly Deco104Protection _prot = new(
        Deco104AddressScramble.Reverse,
        useMagicReadAddressXor: true);
    [NonSerialized] private BoogwingRomSet _roms = BoogwingRomSet.Empty;
    private ArcadeInputState _input;
    private ArcadeInputState _input2;
    private bool _vblank;
    private byte _irqLevel;
    private ushort _priority;
    private int _ramWrites;
    private int _paletteWrites;
    private int _paletteDmas;
    private int _tileWrites;
    private int _spriteWrites;
    private int _protReads;
    private int _protWrites;
    private int _unknownReads;
    private int _unknownWrites;
    private byte _soundLatch;
    private int _soundWrites;
    [NonSerialized] private ushort[] _tempBitmap = Array.Empty<ushort>();
    [NonSerialized] private ushort[] _alphaTilemap = Array.Empty<ushort>();
    [NonSerialized] private byte[] _priorityMap = Array.Empty<byte>();
    [NonSerialized] private ushort[] _spriteRaw0 = Array.Empty<ushort>();
    [NonSerialized] private ushort[] _spriteRaw1 = Array.Empty<ushort>();

    public string DebugSummary
        => string.Create(
            CultureInfo.InvariantCulture,
            $"ramW={_ramWrites} palW/D={_paletteWrites}/{_paletteDmas} {PaletteDebugSummary()} tileW={_tileWrites} sprW={_spriteWrites} pri=0x{_priority:X4} prot={_protReads}/{_protWrites} snd=0x{_soundLatch:X2}/{_soundWrites} unk={_unknownReads}/{_unknownWrites} {_prot.DebugSummary}");

    public BusSignals Signals => new(false);
    public ushort CurrentOpcode { get; private set; }

    public void Load(BoogwingRomSet roms)
    {
        _roms = roms;
        ResetBoard();
    }

    public void ResetBoard()
    {
        Array.Clear(_workRam);
        foreach (ushort[] ram in _pf) Array.Clear(ram);
        foreach (ushort[] ram in _rowscroll) Array.Clear(ram);
        foreach (ushort[] ram in _control) Array.Clear(ram);
        foreach (ushort[] ram in _spriteRam) Array.Clear(ram);
        foreach (ushort[] ram in _spriteBuffered) Array.Clear(ram);
        Array.Clear(_paletteRam);
        Array.Clear(_paletteBuffered);
        Array.Clear(_colors);
        Array.Clear(_ace);
        _prot.Reset();
        _vblank = false;
        _irqLevel = 0;
        _priority = 0;
        _ramWrites = _paletteWrites = _paletteDmas = _tileWrites = _spriteWrites = 0;
        _protReads = _protWrites = _unknownReads = _unknownWrites = 0;
        _soundLatch = 0xff;
        _soundWrites = 0;
        UpdatePalette();
    }

    public void SaveState(BinaryWriter writer) => StateBinarySerializer.WriteInto(writer, this);
    public void LoadState(BinaryReader reader) => StateBinarySerializer.ReadInto(reader, this);
    public void SetInput(ArcadeInputState input) => _input = input;
    public void SetInput2(ArcadeInputState input) => _input2 = input;
    public void BeginFrame() { }
    public void EndFrame()
    {
        Array.Copy(_spriteRam[0], _spriteBuffered[0], _spriteRam[0].Length);
        Array.Copy(_spriteRam[1], _spriteBuffered[1], _spriteRam[1].Length);
    }

    public void AssertVblank()
    {
        _vblank = true;
        _irqLevel = 6;
    }

    public void EndVblank() => _vblank = false;

    public byte ReadByte(uint address)
    {
        ushort word = ReadWord(address & 0x00fffffe);
        return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
    }

    public ushort ReadWord(uint address)
    {
        address &= 0x00fffffe;
        if (address < 0x100000)
            return ReadBe16(_roms.MainData, (int)address);
        if (address is >= 0x200000 and <= 0x20ffff)
            return ReadBe16(_workRam, (int)(address - 0x200000));
        if (address is >= 0x242000 and <= 0x2427ff)
            return _spriteRam[0][((address - 0x242000) >> 1) & 0x3ff];
        if (address is >= 0x246000 and <= 0x2467ff)
            return _spriteRam[1][((address - 0x246000) >> 1) & 0x3ff];
        if (address is >= 0x264000 and <= 0x265fff)
            return _pf[0][((address - 0x264000) >> 1) & 0xfff];
        if (address is >= 0x266000 and <= 0x267fff)
            return _pf[1][((address - 0x266000) >> 1) & 0xfff];
        if (address is >= 0x268000 and <= 0x268fff)
            return _rowscroll[0][((address - 0x268000) >> 1) & 0x7ff];
        if (address is >= 0x26a000 and <= 0x26afff)
            return _rowscroll[1][((address - 0x26a000) >> 1) & 0x7ff];
        if (address is >= 0x274000 and <= 0x275fff)
            return _pf[2][((address - 0x274000) >> 1) & 0xfff];
        if (address is >= 0x276000 and <= 0x277fff)
            return _pf[3][((address - 0x276000) >> 1) & 0xfff];
        if (address is >= 0x278000 and <= 0x278fff)
            return _rowscroll[2][((address - 0x278000) >> 1) & 0x7ff];
        if (address is >= 0x27a000 and <= 0x27afff)
            return _rowscroll[3][((address - 0x27a000) >> 1) & 0x7ff];
        if (address is >= 0x284000 and <= 0x285fff)
            return ReadPalette16(address - 0x284000);
        if (address is >= 0x3c0000 and <= 0x3c004f)
            return _ace[((address - 0x3c0000) >> 1) & 0x27];
        if (address is >= 0x24e000 and <= 0x24efff)
        {
            _protReads++;
            uint protAddress = _prot.DecodeExternalAddress(BoogwingProtAddress(address - 0x24e000));
            return _prot.ReadDecodedAddress(protAddress, BuildInputs(), BuildSystem(), 0x7fff);
        }

        _unknownReads++;
        return 0xffff;
    }

    public uint ReadLong(uint address)
    {
        address &= 0x00fffffe;
        return ((uint)ReadWord(address) << 16) | ReadWord(address + 2);
    }

    public ushort ReadOpcodeWord(uint address)
    {
        ushort op = ReadBe16(_roms.MainOpcodes, (int)(address & 0x00fffffe));
        CurrentOpcode = op;
        return op;
    }

    public void WriteByte(uint address, byte value)
    {
        ushort old = ReadWord(address);
        ushort next = (address & 1) == 0 ? (ushort)((old & 0x00ff) | (value << 8)) : (ushort)((old & 0xff00) | value);
        WriteWord(address & 0x00fffffe, next);
    }

    public void WriteWord(uint address, ushort value)
    {
        address &= 0x00fffffe;
        if (address is >= 0x200000 and <= 0x20ffff)
        {
            WriteBe16(_workRam, (int)(address - 0x200000), value);
            _ramWrites++;
            return;
        }
        if (address == 0x220000)
        {
            _priority = value;
            return;
        }
        if (address == 0x240000)
        {
            Array.Copy(_spriteRam[0], _spriteBuffered[0], _spriteRam[0].Length);
            return;
        }
        if (address is >= 0x242000 and <= 0x2427ff)
        {
            _spriteRam[0][((address - 0x242000) >> 1) & 0x3ff] = value;
            _spriteWrites++;
            return;
        }
        if (address == 0x244000)
        {
            Array.Copy(_spriteRam[1], _spriteBuffered[1], _spriteRam[1].Length);
            return;
        }
        if (address is >= 0x246000 and <= 0x2467ff)
        {
            _spriteRam[1][((address - 0x246000) >> 1) & 0x3ff] = value;
            _spriteWrites++;
            return;
        }
        if (address is >= 0x260000 and <= 0x26000f)
        {
            _control[0][((address - 0x260000) >> 1) & 7] = value;
            return;
        }
        if (address is >= 0x264000 and <= 0x265fff)
        {
            _pf[0][((address - 0x264000) >> 1) & 0xfff] = value;
            _tileWrites++;
            return;
        }
        if (address is >= 0x266000 and <= 0x267fff)
        {
            _pf[1][((address - 0x266000) >> 1) & 0xfff] = value;
            _tileWrites++;
            return;
        }
        if (address is >= 0x268000 and <= 0x268fff)
        {
            _rowscroll[0][((address - 0x268000) >> 1) & 0x7ff] = value;
            return;
        }
        if (address is >= 0x26a000 and <= 0x26afff)
        {
            _rowscroll[1][((address - 0x26a000) >> 1) & 0x7ff] = value;
            return;
        }
        if (address is >= 0x270000 and <= 0x27000f)
        {
            _control[1][((address - 0x270000) >> 1) & 7] = value;
            return;
        }
        if (address is >= 0x274000 and <= 0x275fff)
        {
            _pf[2][((address - 0x274000) >> 1) & 0xfff] = value;
            _tileWrites++;
            return;
        }
        if (address is >= 0x276000 and <= 0x277fff)
        {
            _pf[3][((address - 0x276000) >> 1) & 0xfff] = value;
            _tileWrites++;
            return;
        }
        if (address is >= 0x278000 and <= 0x278fff)
        {
            _rowscroll[2][((address - 0x278000) >> 1) & 0x7ff] = value;
            return;
        }
        if (address is >= 0x27a000 and <= 0x27afff)
        {
            _rowscroll[3][((address - 0x27a000) >> 1) & 0x7ff] = value;
            return;
        }
        if (address == 0x282008)
        {
            PaletteDma();
            return;
        }
        if (address is >= 0x284000 and <= 0x285fff)
        {
            WritePalette16(address - 0x284000, value);
            _paletteWrites++;
            return;
        }
        if (address is >= 0x3c0000 and <= 0x3c004f)
        {
            _ace[((address - 0x3c0000) >> 1) & 0x27] = value;
            if ((uint)(((address - 0x3c0000) >> 1) - 0x20) <= 0x06)
                UpdatePalette();
            return;
        }
        if (address is >= 0x24e000 and <= 0x24efff)
        {
            _protWrites++;
            uint protAddress = _prot.DecodeExternalAddress(BoogwingProtAddress(address - 0x24e000));
            byte? sound = _prot.WriteDecodedAddress(protAddress, value, 0xffff);
            if (sound.HasValue)
            {
                _soundLatch = sound.Value;
                _soundWrites++;
            }
            return;
        }

        _unknownWrites++;
    }

    public void WriteLong(uint address, uint value)
    {
        WriteWord(address, (ushort)(value >> 16));
        WriteWord(address + 2, (ushort)value);
    }

    public byte InterruptLevel() => _irqLevel;
    public void AcknowledgeInterrupt(byte level)
    {
        if (_irqLevel == (level & 7))
            _irqLevel = 0;
    }

    public bool Reset() => false;
    public bool Halt() => false;

    public void Render(byte[] fb, int width, int height, int stride, long frame)
    {
        EnsureRenderBuffers(width * height);
        Fill(fb, width, height, stride, _colors[0x400]);
        Array.Fill(_tempBitmap, (ushort)0xffff);
        Array.Clear(_alphaTilemap);
        Array.Clear(_priorityMap);
        Array.Clear(_spriteRaw0);
        Array.Clear(_spriteRaw1);

        RenderSpritesRaw(_spriteRaw1, width, height, _spriteBuffered[1], _roms.Sprites2, frame);
        RenderSpritesRaw(_spriteRaw0, width, height, _spriteBuffered[0], _roms.Sprites1, frame);

        int priority = _priority & 0x07;
        if (priority == 0x05)
        {
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 1, _roms.Tiles2, 0x100, tileSize: 16, fiveBpp: true, opaque: true, priorityValue: 0);
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 3, _roms.Tiles3, 0x400, tileSize: 16, fiveBpp: false, opaque: false, priorityValue: 32);
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 2, _roms.Tiles3, 0x300, tileSize: 16, fiveBpp: false, opaque: false, priorityValue: 32);
        }
        else if (priority == 0x04)
        {
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 3, _roms.Tiles3, 0x400, tileSize: 16, fiveBpp: false, opaque: true, priorityValue: 0);
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 2, _roms.Tiles3, 0x300, tileSize: 16, fiveBpp: false, opaque: false, priorityValue: 0);
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 1, _roms.Tiles2, 0x100, tileSize: 16, fiveBpp: true, opaque: false, priorityValue: 32);
        }
        else if (priority == 0x01 || priority == 0x02)
        {
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 3, _roms.Tiles3, 0x400, tileSize: 16, fiveBpp: false, opaque: true, priorityValue: 0);
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 1, _roms.Tiles2, 0x100, tileSize: 16, fiveBpp: true, opaque: false, priorityValue: 8);
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 2, _roms.Tiles3, 0x300, tileSize: 16, fiveBpp: false, opaque: false, priorityValue: 32);
        }
        else if (priority == 0x03)
        {
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 3, _roms.Tiles3, 0x400, tileSize: 16, fiveBpp: false, opaque: true, priorityValue: 0);
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 1, _roms.Tiles2, 0x100, tileSize: 16, fiveBpp: true, opaque: false, priorityValue: 8);
            RenderLayerIndexed(_alphaTilemap, null, width, height, 2, _roms.Tiles3, 0x300, tileSize: 16, fiveBpp: false, opaque: false, priorityValue: 0);
        }
        else
        {
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 3, _roms.Tiles3, 0x400, tileSize: 16, fiveBpp: false, opaque: true, priorityValue: 0);
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 2, _roms.Tiles3, 0x300, tileSize: 16, fiveBpp: false, opaque: false, priorityValue: 8);
            RenderLayerIndexed(_tempBitmap, _priorityMap, width, height, 1, _roms.Tiles2, 0x100, tileSize: 16, fiveBpp: true, opaque: false, priorityValue: 32);
        }

        MixSpritesAndPlayfields(fb, width, height, stride);
        RenderLayerFrame(fb, width, height, stride, 0, _roms.Tiles1, 0x800, tileSize: 8, fiveBpp: false);
    }

    private void EnsureRenderBuffers(int pixels)
    {
        if (_tempBitmap.Length == pixels)
            return;
        _tempBitmap = new ushort[pixels];
        _alphaTilemap = new ushort[pixels];
        _priorityMap = new byte[pixels];
        _spriteRaw0 = new ushort[pixels];
        _spriteRaw1 = new ushort[pixels];
    }

    private void RenderLayerFrame(byte[] fb, int width, int height, int stride, int layer, byte[] gfx, int colorBase, int tileSize, bool fiveBpp)
    {
        RenderLayerPixels(width, height, layer, gfx, colorBase, tileSize, fiveBpp, opaque: false, (x, y, palettePixel, _) =>
            WritePixel(fb, stride, x, y, palettePixel));
    }

    private void RenderLayerIndexed(ushort[] target, byte[]? priorityMap, int width, int height, int layer, byte[] gfx, int colorBase, int tileSize, bool fiveBpp, bool opaque, byte priorityValue)
    {
        RenderLayerPixels(width, height, layer, gfx, colorBase, tileSize, fiveBpp, opaque, (x, y, palettePixel, pen) =>
        {
            int offset = y * width + x;
            target[offset] = (ushort)palettePixel;
            if (priorityMap is not null)
                priorityMap[offset] = priorityValue;
        });
    }

    private void RenderLayerPixels(int width, int height, int layer, byte[] gfx, int colorBase, int tileSize, bool fiveBpp, bool opaque, Action<int, int, int, int> plot)
    {
        ushort[] ctrl = _control[layer >> 1];
        int half = layer & 1;
        int control0 = half == 0 ? ctrl[5] & 0xff : ctrl[5] >> 8;
        int control1 = half == 0 ? ctrl[6] & 0xff : ctrl[6] >> 8;
        if ((control0 & 0x80) == 0)
            return;

        bool charMode = tileSize == 8 || (control1 & 0x80) != 0;
        int actualTileSize = charMode ? 8 : 16;
        int mapCols = 64;
        int mapRows = 32;
        int widthMask = mapCols * actualTileSize - 1;
        int heightMask = mapRows * actualTileSize - 1;
        int scrollX = ctrl[half == 0 ? 1 : 3] & 0x3ff;
        int scrollY = ctrl[half == 0 ? 2 : 4] & 0x1ff;
        int bank = layer switch
        {
            1 => BankCallback(ctrl[7] >> 8),
            2 => BankCallback2(ctrl[7] & 0xff),
            3 => BankCallback2(ctrl[7] >> 8),
            _ => 0
        };
        bool enableTileFlipX = (control1 & 0x01) != 0;
        bool enableTileFlipY = (control1 & 0x02) != 0;

        for (int y = 0; y < height; y++)
        {
            int sy = (y + scrollY) & heightMask;
            for (int x = 0; x < width; x++)
            {
                int sx = (x + scrollX) & widthMask;
                int tx = sx / actualTileSize;
                int ty = sy / actualTileSize;
                int px = sx & (actualTileSize - 1);
                int py = sy & (actualTileSize - 1);
                int entry = charMode ? (tx + ty * mapCols) : Deco16ScanRows(tx, ty);
                ushort tile = _pf[layer][entry & 0xfff];
                int color = (tile >> 12) & 0x0f;
                bool tileFlipX = false;
                bool tileFlipY = false;
                if ((tile & 0x8000) != 0)
                {
                    if (enableTileFlipX)
                    {
                        tileFlipX = true;
                        color &= 0x07;
                    }
                    if (enableTileFlipY)
                    {
                        tileFlipY = true;
                        color &= 0x07;
                    }
                }
                int srcX = tileFlipX ? actualTileSize - 1 - px : px;
                int srcY = tileFlipY ? actualTileSize - 1 - py : py;
                int code = (tile & 0x0fff) + bank;
                int pen = actualTileSize == 8
                    ? Decode4BppChar(gfx, code, srcX, srcY)
                    : fiveBpp ? Decode5BppTile(gfx, code, srcX, srcY) : Decode4BppTile(gfx, code, srcX, srcY);
                if (pen == 0 && !opaque)
                    continue;
                plot(x, y, (colorBase + color) * 16 + pen, pen);
            }
        }
    }

    private void RenderSpritesRaw(ushort[] raw, int width, int height, ushort[] spr, byte[] gfx, long frame)
    {
        for (int offs = 0; offs + 3 < spr.Length; offs += 4)
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
            int mult2 = multi + 1;
            for (int m = multi; m >= 0; m--)
            {
                int tile = sprite - m * inc;
                int dy = y + 16 * m;
                DrawSpriteTileRaw(raw, width, height, gfx, tile, color, x, dy, fx, fy);
                if (wide)
                    DrawSpriteTileRaw(raw, width, height, gfx, tile - mult2, color, x + 16, dy, fx, fy);
            }
        }
    }

    private static void DrawSpriteTileRaw(ushort[] raw, int width, int height, byte[] gfx, int code, int color, int sx, int sy, bool fx, bool fy)
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
                int pen = Decode4BppTile(gfx, code, px, py);
                if (pen == 0)
                    continue;
                raw[dy * width + dx] = (ushort)((color << 4) | pen);
            }
        }
    }

    private void MixSpritesAndPlayfields(byte[] fb, int width, int height, int stride)
    {
        int priority = _priority & 0x07;
        int calculatedColoffs = (_priority & 0x08) != 0 ? 0x800 : 0;
        int alpha3 = GetAceAlpha(0x1f);

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int offset = row + x;
                int pix1 = _spriteRaw0[offset];
                int pix2 = _spriteRaw1[offset];
                int pix3 = _alphaTilemap[offset];
                int tmappix = _tempBitmap[offset];
                int bgpri = _priorityMap[offset];

                int spri1;
                if (priority == 0x00)
                {
                    if ((pix1 & 0x600) == 0x600)
                        spri1 = 2;
                    else if ((pix1 & 0x600) == 0x400)
                        spri1 = 8;
                    else
                        spri1 = 32;
                }
                else
                {
                    spri1 = (pix1 & 0x400) != 0 ? 8 : 32;
                }

                int pri1;
                if (priority == 0x01)
                    pri1 = (pix1 & 0x600) != 0 ? 16 : 64;
                else if (priority == 0x00)
                    pri1 = (pix1 & 0x400) == 0x400 ? 16 : 64;
                else if ((pix1 & 0x600) == 0x600)
                    pri1 = 4;
                else if ((pix1 & 0x600) == 0x400)
                    pri1 = 16;
                else
                    pri1 = 64;

                int spri2;
                if ((pix2 & 0x600) == 0x600)
                    spri2 = 4;
                else if ((pix2 & 0x600) != 0)
                    spri2 = 16;
                else
                    spri2 = 64;

                int alpha2 = GetAceAlpha((pix2 >> 4) & 0x0f);
                if ((pix2 & 0x100) != 0)
                {
                    if ((pix2 & 0x800) != 0)
                        alpha2 = (pix2 & 8) != 0 ? 0xff : GetAceAlpha(0x14 + ((pix2 - 1) & 0x7));
                    else
                        alpha2 = GetAceAlpha(0x10 + ((pix2 & 0x80) >> 7));
                }
                else if ((pix2 & 0x800) != 0)
                {
                    alpha2 = GetAceAlpha(0x12 + ((pix2 & 0x80) >> 7));
                }

                int pri2;
                int pri3 = 0;
                if (priority == 0x02)
                {
                    if ((pix2 & 0x600) == 0x600)
                        pri2 = 4;
                    else if ((pix2 & 0x600) == 0x400)
                        pri2 = 16;
                    else
                        pri2 = 64;
                }
                else
                {
                    if (priority == 0x03)
                        pri3 = 32;
                    pri2 = (pix2 & 0x400) == 0x400 ? 16 : 64;
                }

                int drawn = 0;
                if ((pix1 & 0x0f) != 0 && pri1 > bgpri)
                {
                    WritePixel(fb, stride, x, y, calculatedColoffs | ((pix1 & 0x1ff) + 0x500));
                    drawn |= 1;
                }

                if ((pix2 & 0x0f) != 0)
                {
                    if (drawn == 0 && tmappix != 0xffff)
                        WritePixel(fb, stride, x, y, calculatedColoffs | tmappix);

                    if (pri2 > bgpri && (drawn == 0 || spri2 > spri1))
                    {
                        int paletteIndex = calculatedColoffs | ((pix2 & 0xff) + 0x700);
                        if (alpha2 >= 0xff)
                            WritePixel(fb, stride, x, y, paletteIndex);
                        else
                            BlendPixel(fb, stride, x, y, paletteIndex, alpha2);
                        drawn |= 2;
                    }
                }

                if (drawn == 0 && tmappix != 0xffff)
                    WritePixel(fb, stride, x, y, tmappix);

                if ((pix3 & 0x0f) != 0 && priority == 0x03)
                {
                    bool bg2Drawn = bgpri == 8 && drawn == 0;
                    bool sprite1Drawn = (drawn & 1) != 0 && pri1 <= pri3;
                    bool sprite2Drawn = (drawn & 2) != 0 && pri2 <= pri3;
                    if (bg2Drawn || ((sprite1Drawn && (drawn & 2) == 0) || (sprite2Drawn && (drawn & 1) == 0) || (sprite1Drawn && sprite2Drawn)))
                    {
                        if (((pix2 & 0x900) != 0x900) || (spri2 <= spri1 && sprite1Drawn))
                            BlendPixel(fb, stride, x, y, ((drawn & 3) != 0 ? calculatedColoffs : 0) | pix3, alpha3);
                    }
                }
            }
        }
    }

    private void PaletteDma()
    {
        Array.Copy(_paletteRam, _paletteBuffered, _paletteRam.Length);
        _paletteDmas++;
        UpdatePalette();
    }

    private ushort ReadPalette16(uint byteOffset)
    {
        int wordOffset = (int)(byteOffset >> 1) & 0xfff;
        uint value = _paletteRam[(wordOffset >> 1) & 0x7ff];
        return (wordOffset & 1) == 0 ? (ushort)(value >> 16) : (ushort)value;
    }

    private void WritePalette16(uint byteOffset, ushort data)
    {
        int wordOffset = (int)(byteOffset >> 1) & 0xfff;
        int index = (wordOffset >> 1) & 0x7ff;
        if ((wordOffset & 1) == 0)
            _paletteRam[index] = (_paletteRam[index] & 0x0000ffffu) | ((uint)data << 16);
        else
            _paletteRam[index] = (_paletteRam[index] & 0xffff0000u) | data;
    }

    private void UpdatePalette()
    {
        int fadePtr = _ace[0x20] & 0xff;
        int fadePtg = _ace[0x21] & 0xff;
        int fadePtb = _ace[0x22] & 0xff;
        int fadeStr = _ace[0x23] & 0xff;
        int fadeStg = _ace[0x24] & 0xff;
        int fadeStb = _ace[0x25] & 0xff;
        int mode = _ace[0x26] & 0xffff;
        for (int i = 0; i < _paletteBuffered.Length; i++)
        {
            uint raw = _paletteBuffered[i];
            int b = (int)((raw >> 16) & 0xff);
            int g = (int)((raw >> 8) & 0xff);
            int r = (int)(raw & 0xff);
            if ((raw & 0x00f0f0f0u) == 0 && (raw & 0x000f0f0fu) != 0)
            {
                b = Expand4(b & 0x0f);
                g = Expand4(g & 0x0f);
                r = Expand4(r & 0x0f);
            }
            _colors[i + 0x800] = 0xff000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;

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

            _colors[i] = 0xff000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }
    }

    private string PaletteDebugSummary()
    {
        int staged = 0;
        int buffered = 0;
        int visible = 0;
        int first = -1;
        uint firstRaw = 0;
        for (int i = 0; i < _paletteRam.Length; i++)
        {
            if ((_paletteRam[i] & 0x00ffffffu) != 0)
                staged++;
            if ((_paletteBuffered[i] & 0x00ffffffu) != 0)
            {
                if (first < 0)
                {
                    first = i;
                    firstRaw = _paletteBuffered[i];
                }
                buffered++;
            }
        }
        for (int i = 0; i < _colors.Length; i++)
        {
            if ((_colors[i] & 0x00ffffffu) != 0)
                visible++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"palnz={staged}/{buffered}/{visible}/first{first}:0x{firstRaw:X8}");
    }

    private int GetAceAlpha(int index)
    {
        int alpha = _ace[index & 0x1f] & 0xff;
        if (alpha > 0x20)
            return 0x80;
        alpha = 255 - (alpha << 3);
        return Math.Max(alpha, 0);
    }

    private void BlendPixel(byte[] fb, int stride, int x, int y, int paletteIndex, int alpha)
    {
        alpha = Math.Clamp(alpha, 0, 255);
        if (alpha <= 0)
            return;
        int offset = y * stride + x * 4;
        uint src = _colors[paletteIndex & 0xfff];
        int sb = (int)(src & 0xff);
        int sg = (int)((src >> 8) & 0xff);
        int sr = (int)((src >> 16) & 0xff);
        int inv = 256 - alpha;
        fb[offset] = (byte)(((sb * alpha) + (fb[offset] * inv)) >> 8);
        fb[offset + 1] = (byte)(((sg * alpha) + (fb[offset + 1] * inv)) >> 8);
        fb[offset + 2] = (byte)(((sr * alpha) + (fb[offset + 2] * inv)) >> 8);
        fb[offset + 3] = 0xff;
    }

    private ushort BuildInputs()
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
        if (_input2.Up) value &= unchecked((ushort)~0x0100);
        if (_input2.Down) value &= unchecked((ushort)~0x0200);
        if (_input2.Left) value &= unchecked((ushort)~0x0400);
        if (_input2.Right) value &= unchecked((ushort)~0x0800);
        if (_input2.A) value &= unchecked((ushort)~0x1000);
        if (_input2.B) value &= unchecked((ushort)~0x2000);
        if (_input2.C) value &= unchecked((ushort)~0x4000);
        if (_input2.Start) value &= unchecked((ushort)~0x8000);
        return value;
    }

    private ushort BuildSystem()
    {
        ushort value = 0xffff;
        if (_input.Mode) value &= unchecked((ushort)~0x0001);
        if (_input2.Mode) value &= unchecked((ushort)~0x0002);
        if (_vblank) value |= 0x0008;
        else value &= unchecked((ushort)~0x0008);
        return value;
    }

    private static int BankCallback(int bank) => ((bank >> 4) & 7) * 0x1000;
    private static int BankCallback2(int bank)
    {
        int offset = ((bank >> 4) & 7) * 0x1000;
        if ((bank & 0x0f) == 0x0a)
            offset += 0x800;
        return offset;
    }

    private static int Deco16ScanRows(int col, int row)
        => (col & 0x1f) + ((row & 0x1f) << 5) + ((col & 0x20) << 5) + ((row & 0x20) << 6);

    private static uint BoogwingProtAddress(uint byteOffset)
        => Bitswap32(
            byteOffset,
            31, 30, 29, 28, 27, 26, 25, 24,
            23, 22, 21, 20, 19, 18, 13, 12,
            11, 17, 16, 15, 14, 10, 9, 8,
            7, 6, 5, 4, 3, 2, 1, 0) & 0x7fffu;

    private static uint Bitswap32(uint value, params int[] bits)
    {
        uint result = 0;
        for (int i = 0; i < bits.Length; i++)
            result = (result << 1) | ((value >> bits[i]) & 1u);
        return result;
    }

    private static int Decode4BppChar(byte[] rom, int code, int x, int y)
    {
        int half = rom.Length / 2;
        int chars = Math.Max(1, half / 16);
        int baseBit = (Math.Abs(code) % chars) * 16 * 8 + y * 16 + x;
        return (ReadBit(rom, half * 8 + baseBit + 8) << 3)
            | (ReadBit(rom, half * 8 + baseBit) << 2)
            | (ReadBit(rom, baseBit + 8) << 1)
            | ReadBit(rom, baseBit);
    }

    private static int Decode4BppTile(byte[] rom, int code, int x, int y)
    {
        int half = rom.Length / 2;
        int tiles = Math.Max(1, half / 64);
        int baseBit = (Math.Abs(code) % tiles) * 64 * 8 + y * 16 + (x < 8 ? 16 * 8 * 2 : 0) + (x & 7);
        return (ReadBit(rom, half * 8 + baseBit + 8) << 3)
            | (ReadBit(rom, half * 8 + baseBit) << 2)
            | (ReadBit(rom, baseBit + 8) << 1)
            | ReadBit(rom, baseBit);
    }

    private static int Decode5BppTile(byte[] rom, int code, int x, int y)
    {
        int plane = rom.Length / 3;
        int tiles = Math.Max(1, plane / 64);
        int baseBit = (Math.Abs(code) % tiles) * 64 * 8 + y * 16 + (x < 8 ? 16 * 8 * 2 : 0) + (x & 7);
        return (ReadBit(rom, plane * 2 * 8 + baseBit) << 4)
            | (ReadBit(rom, plane * 8 + baseBit + 8) << 3)
            | (ReadBit(rom, plane * 8 + baseBit) << 2)
            | (ReadBit(rom, baseBit + 8) << 1)
            | ReadBit(rom, baseBit);
    }

    private static int ReadBit(byte[] data, int bit)
    {
        int byteOffset = bit >> 3;
        if ((uint)byteOffset >= (uint)data.Length)
            return 0;
        return (data[byteOffset] >> (7 - (bit & 7))) & 1;
    }

    private void WritePixel(byte[] fb, int stride, int x, int y, int paletteIndex)
    {
        uint color = _colors[paletteIndex & 0xfff];
        int offset = y * stride + x * 4;
        fb[offset] = (byte)color;
        fb[offset + 1] = (byte)(color >> 8);
        fb[offset + 2] = (byte)(color >> 16);
        fb[offset + 3] = 0xff;
    }

    private static void Fill(byte[] fb, int width, int height, int stride, uint color)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * stride;
            for (int x = 0; x < width; x++)
            {
                int p = row + x * 4;
                fb[p] = (byte)color;
                fb[p + 1] = (byte)(color >> 8);
                fb[p + 2] = (byte)(color >> 16);
                fb[p + 3] = 0xff;
            }
        }
    }

    private static int Expand4(int value) => (value << 4) | value;

    private static ushort ReadBe16(byte[] data, int offset)
    {
        if ((uint)(offset + 1) >= (uint)data.Length)
            return 0xffff;
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    private static void WriteBe16(byte[] data, int offset, ushort value)
    {
        if ((uint)(offset + 1) >= (uint)data.Length)
            return;
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
}

internal sealed class BoogwingRomSet
{
    public static readonly BoogwingRomSet Empty = new()
    {
        MainData = Array.Empty<byte>(),
        MainOpcodes = Array.Empty<byte>(),
        AudioCpu = Array.Empty<byte>(),
        Tiles1 = Array.Empty<byte>(),
        Tiles2 = Array.Empty<byte>(),
        Tiles3 = Array.Empty<byte>(),
        Sprites1 = Array.Empty<byte>(),
        Sprites2 = Array.Empty<byte>(),
        Oki1 = Array.Empty<byte>(),
        Oki2 = Array.Empty<byte>()
    };

    public byte[] MainData { get; private init; } = Array.Empty<byte>();
    public byte[] MainOpcodes { get; private init; } = Array.Empty<byte>();
    public byte[] AudioCpu { get; private init; } = Array.Empty<byte>();
    public byte[] Tiles1 { get; private init; } = Array.Empty<byte>();
    public byte[] Tiles2 { get; private init; } = Array.Empty<byte>();
    public byte[] Tiles3 { get; private init; } = Array.Empty<byte>();
    public byte[] Sprites1 { get; private init; } = Array.Empty<byte>();
    public byte[] Sprites2 { get; private init; } = Array.Empty<byte>();
    public byte[] Oki1 { get; private init; } = Array.Empty<byte>();
    public byte[] Oki2 { get; private init; } = Array.Empty<byte>();

    public static BoogwingRomSet Load(string archivePath)
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
        Load16Byte(main, Required(entries, "kn_00-2.2b"), 0x000000);
        Load16Byte(main, Required(entries, "kn_02-2.2e"), 0x000001);
        Load16Byte(main, Required(entries, "kn_01-2.4b"), 0x080000);
        Load16Byte(main, Required(entries, "kn_03-2.4e"), 0x080001);
        DecryptDeco102(main, out byte[] mainData, out byte[] mainOpcodes);

        byte[] tiles1 = new byte[0x20000];
        Load16Byte(tiles1, Required(entries, "km05.9e"), 0x00000);
        Load16Byte(tiles1, Required(entries, "km04.8e"), 0x00001);
        Deco32GfxDecryptor.Decrypt56(tiles1);

        byte[] tiles2 = new byte[0x300000];
        Copy(Required(entries, "mbd-01.9b"), tiles2, 0x000000);
        Copy(Required(entries, "mbd-00.8b"), tiles2, 0x100000);
        byte[] tiles2Hi = new byte[0x100000];
        Load16Byte(tiles2Hi, Required(entries, "mbd-02.10e"), 0x000000);
        Deco56RemapGfx(tiles2Hi);
        Copy(tiles2Hi, tiles2, 0x200000);
        Deco32GfxDecryptor.Decrypt56(tiles2);

        byte[] tiles3 = new byte[0x200000];
        Copy(Required(entries, "mbd-03.13b"), tiles3, 0x000000);
        Copy(Required(entries, "mbd-04.14b"), tiles3, 0x100000);
        Deco32GfxDecryptor.Decrypt56(tiles3);

        return new BoogwingRomSet
        {
            MainData = mainData,
            MainOpcodes = mainOpcodes,
            AudioCpu = Required(entries, "km06.18p"),
            Tiles1 = tiles1,
            Tiles2 = tiles2,
            Tiles3 = tiles3,
            Sprites1 = Combine(entries, ("mbd-06.17b", 0x000000), ("mbd-05.16b", 0x200000), 0x400000),
            Sprites2 = Combine(entries, ("mbd-08.19b", 0x000000), ("mbd-07.18b", 0x200000), 0x400000),
            Oki1 = Required(entries, "mbd-10.17p"),
            Oki2 = Required(entries, "mbd-09.16p")
        };
    }

    private static byte[] Required(Dictionary<string, byte[]> entries, string name)
    {
        if (!entries.TryGetValue(name, out byte[]? data))
            throw new InvalidDataException($"Boogie Wings ROM is missing '{name}'.");
        return data;
    }

    private static byte[] Combine(Dictionary<string, byte[]> entries, (string Name, int Offset) a, (string Name, int Offset) b, int length)
    {
        byte[] result = new byte[length];
        Copy(Required(entries, a.Name), result, a.Offset);
        Copy(Required(entries, b.Name), result, b.Offset);
        return result;
    }

    private static void Copy(byte[] src, byte[] dst, int offset)
        => Buffer.BlockCopy(src, 0, dst, offset, Math.Min(src.Length, dst.Length - offset));

    private static void Load16Byte(byte[] dest, byte[] src, int offset)
    {
        for (int i = 0, d = offset; i < src.Length && d < dest.Length; i++, d += 2)
            dest[d] = src[i];
    }

    private static void DecryptDeco102(byte[] encrypted, out byte[] data, out byte[] opcodes)
    {
        data = new byte[encrypted.Length];
        opcodes = new byte[encrypted.Length];
        ushort[] source = new ushort[encrypted.Length / 2];
        for (int i = 0; i < source.Length; i++)
            source[i] = ReadBe16(encrypted, i * 2);

        for (int i = 0; i < source.Length; i++)
        {
            int src = i & 0xf0000;
            if ((i & 0x0001) != 0) src ^= 0xbe0b;
            if ((i & 0x0002) != 0) src ^= 0x5699;
            if ((i & 0x0004) != 0) src ^= 0x1322;
            if ((i & 0x0008) != 0) src ^= 0x0004;
            if ((i & 0x0010) != 0) src ^= 0x08a0;
            if ((i & 0x0020) != 0) src ^= 0x0089;
            if ((i & 0x0040) != 0) src ^= 0x0408;
            if ((i & 0x0080) != 0) src ^= 0x1212;
            if ((i & 0x0100) != 0) src ^= 0x08e0;
            if ((i & 0x0200) != 0) src ^= 0x5499;
            if ((i & 0x0400) != 0) src ^= 0x9a8b;
            if ((i & 0x0800) != 0) src ^= 0x1222;
            if ((i & 0x1000) != 0) src ^= 0x1200;
            if ((i & 0x2000) != 0) src ^= 0x0008;
            if ((i & 0x4000) != 0) src ^= 0x1210;
            if ((i & 0x8000) != 0) src ^= 0x00e0;
            src ^= 0x42ba;
            src &= source.Length - 1;

            WriteBe16(data, i * 2, DecryptWord(source[src], i, 0x00));
            WriteBe16(opcodes, i * 2, DecryptWord(source[src], i, 0x18));
        }
    }

    private static ushort DecryptWord(ushort data, int address, int selectXor)
    {
        ReadOnlySpan<ushort> xors = stackalloc ushort[]
        {
            0xb52c,0x2458,0x139a,0xc998,0xce8e,0x5144,0x0429,0xaad4,
            0xa331,0x3645,0x69a3,0xac64,0x1a53,0x5083,0x4dea,0xd237
        };
        ReadOnlySpan<byte> bitswaps = stackalloc byte[]
        {
            12,8,13,11,14,10,15,9,3,2,1,0,4,5,6,7, 10,11,14,12,15,13,8,9,6,7,5,3,0,4,2,1,
            14,13,15,9,8,12,11,10,7,4,1,5,6,0,3,2, 15,14,8,9,10,11,13,12,1,2,7,3,4,6,0,5,
            10,9,13,14,15,8,12,11,5,2,1,0,3,4,7,6, 8,9,15,14,10,11,13,12,0,6,5,4,1,2,3,7,
            14,8,15,9,10,11,13,12,4,5,3,0,2,7,6,1, 13,11,12,10,15,9,14,8,6,0,7,5,1,4,3,2,
            12,11,13,10,9,8,14,15,0,2,4,6,7,5,3,1, 15,13,9,8,10,11,12,14,2,1,0,7,6,5,4,3,
            13,8,9,10,11,12,15,14,6,0,1,2,3,7,4,5, 12,11,10,8,9,13,14,15,6,5,4,0,7,1,2,3,
            12,15,8,13,9,11,14,10,6,5,4,3,2,1,0,7, 11,12,13,14,15,8,9,10,4,5,7,1,6,3,2,0,
            13,8,12,14,11,15,10,9,7,6,5,4,3,2,1,0, 15,14,13,12,11,10,9,8,0,6,7,4,3,2,1,5
        };
        int swap = ((address ^ selectXor) & 0xf0) >> 4;
        if ((address & 0x20000) != 0)
            swap ^= 4;
        int xor = (address ^ selectXor) & 0x0f;
        if ((address & 0x40000) != 0)
            xor ^= 2;
        ushort swapped = Bitswap16(data, bitswaps.Slice(swap * 16, 16));
        return (ushort)(xors[xor] ^ swapped);
    }

    private static ushort Bitswap16(ushort value, ReadOnlySpan<byte> bits)
    {
        ushort result = 0;
        for (int i = 0; i < bits.Length; i++)
            result = (ushort)((result << 1) | ((value >> bits[i]) & 1));
        return result;
    }

    private static void Deco56RemapGfx(byte[] data)
    {
        byte[] copy = (byte[])data.Clone();
        for (int i = 0; i < data.Length; i++)
            data[i] = copy[Bitswap24((uint)i, 23,22,21,20,19,18,17,16,15,14,13,12,11,10,9,8,7,6,5,4,3,0,1,2) & (copy.Length - 1)];
    }

    private static int Bitswap24(uint value, params int[] bits)
    {
        int result = 0;
        for (int i = 0; i < bits.Length; i++)
            result = (result << 1) | (int)((value >> bits[i]) & 1);
        return result;
    }

    private static uint Bitswap32(uint value, params int[] bits)
    {
        uint result = 0;
        for (int i = 0; i < bits.Length; i++)
            result = (result << 1) | ((value >> bits[i]) & 1u);
        return result;
    }

    private static ushort ReadBe16(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static void WriteBe16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

}
