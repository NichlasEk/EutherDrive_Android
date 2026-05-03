using System;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Archives;
using EutherDrive.Core.Arcade.Cps1;
using EutherDrive.Core.Cpu.M68000Emu;
using EutherDrive.Core.Cpu.Z80Emu;
using EutherDrive.Core.Savestates;

namespace EutherDrive.Core.Arcade.Konami;

// Teenage Mutant Ninja Turtles hardware notes and the K052109/K051960 register
// behavior below are translated from MAME's BSD-3-Clause Konami TMNT driver and
// video chip devices:
//   src/mame/konami/tmnt.cpp
//   src/mame/konami/k052109.cpp
//   src/mame/konami/k051960.cpp
public sealed class TmntAdapter : IEmulatorCore, ISavestateCapable
{
    private const string SavestateMagic = "KONAMITMNT";
    private const int SavestateVersion = 1;
    private const int FrameWidth = 320;
    private const int FrameHeight = 224;
    private const int FrameStride = FrameWidth * 4;
    private const double TargetFps = 24_000_000.0 / 4.0 / 384.0 / 264.0;
    private const int MainCpuCyclesPerFrame = 135_168;
    private const int ScreenTotalLines = 264;
    private const int ScreenVisibleLines = 224;
    private const int MainCpuVisibleCycles = MainCpuCyclesPerFrame * ScreenVisibleLines / ScreenTotalLines;
    private const int MainCpuVblankCycles = MainCpuCyclesPerFrame - MainCpuVisibleCycles;
    private const int OutputSampleRate = 44_100;
    private const int OutputChannels = 2;
    private const float Ym2151RouteGain = 0.40f;
    private const float K007232RouteGain = 0.15f;
    private const float Upd7759RouteGain = 0.30f;
    private const float TitleSampleRouteGain = 0.25f;

    private readonly TmntBus _bus = new();
    private readonly TmntSound _sound = new();
    private readonly M68000 _mainCpu = M68000.CreateBuilder()
        .AllowTasWrites(true)
        .Name("konami-tmnt-main")
        .Build();

    private readonly object _frameSync = new();
    private byte[] _presentFrameBuffer = new byte[FrameHeight * FrameStride];
    private byte[] _renderFrameBuffer = new byte[FrameHeight * FrameStride];
    private byte[] _snapshotFrameBuffer = new byte[FrameHeight * FrameStride];
    private short[] _audioBuffer = Array.Empty<short>();
    private short[] _scaledAudioBuffer = Array.Empty<short>();
    private ArcadeInputState _input;
    private int _masterVolumePercent = 100;
    private bool _loaded;
    private long _frameCounter;
    private RomIdentity? _romIdentity;

    public string DebugSummary => _bus.DebugSummary(_mainCpu.Pc) + " " + _sound.DebugSummary;

    public double GetTargetFps() => TargetFps;

    public RomIdentity? RomIdentity => _romIdentity;

    public long? FrameCounter => _loaded ? _frameCounter : null;

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string name = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
        return name is "tmnt" or "tmntu" or "tmntj" or "tmhta" or "tmnt2p" or "tmht2p";
    }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("TMNT ROM path is empty.", nameof(path));
        if (!RomArchiveExtractor.FileExists(path))
            throw new FileNotFoundException("TMNT ROM archive not found.", path);

        byte[] romHash;
        using (Stream stream = RomArchiveExtractor.OpenRead(path))
            romHash = RomIdentity.ComputeSha256(stream);

        TmntRomSet roms = TmntRomSet.Load(path);
        _bus.Load(roms);
        _sound.Load(roms);
        _bus.AttachSound(_sound);
        _mainCpu.Reset(_bus);
        _loaded = true;
        _frameCounter = 0;
        _romIdentity = new RomIdentity(
            Path.GetFileName(path),
            romHash,
            PersistentStoragePath.ResolveSavestateDirectory(path, "tmnt"));
        ClearFrameBuffers();
        _audioBuffer = new short[Math.Max(1, (int)Math.Round(OutputSampleRate / TargetFps)) * OutputChannels];
    }

    public void Reset()
    {
        if (!_loaded)
            return;

        _bus.ResetMachine();
        _sound.ResetMachine();
        _mainCpu.Reset(_bus);
        _frameCounter = 0;
        ClearFrameBuffers();
    }

    public void RunFrame()
    {
        if (!_loaded)
            return;

        _bus.SetInput(_input);
        _sound.BeginFrame(_audioBuffer);

        int cycles = 0;
        while (cycles < MainCpuVisibleCycles)
        {
            int elapsed = checked((int)_mainCpu.ExecuteInstruction(_bus));
            cycles += elapsed;
            _sound.RunMainCpuCycles(elapsed, MainCpuCyclesPerFrame);
        }

        _bus.Render(_renderFrameBuffer);
        lock (_frameSync)
        {
            Buffer.BlockCopy(_renderFrameBuffer, 0, _presentFrameBuffer, 0, _renderFrameBuffer.Length);
        }

        _bus.BeginVblank();
        cycles = 0;
        while (cycles < MainCpuVblankCycles)
        {
            int elapsed = checked((int)_mainCpu.ExecuteInstruction(_bus));
            cycles += elapsed;
            _sound.RunMainCpuCycles(elapsed, MainCpuCyclesPerFrame);
        }

        _sound.EndFrame();
        _frameCounter++;
    }

    public void SaveState(BinaryWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!_loaded)
            throw new InvalidOperationException("TMNT core not initialized.");

        writer.Write(SavestateMagic);
        writer.Write(SavestateVersion);
        writer.Write(_frameCounter);
        WriteInputState(writer, _input);
        WriteByteArray(writer, _presentFrameBuffer);
        WriteByteArray(writer, _renderFrameBuffer);
        WriteByteArray(writer, _snapshotFrameBuffer);
        StateBinarySerializer.WriteInto(writer, _mainCpu);
        StateBinarySerializer.WriteInto(writer, _bus);
        StateBinarySerializer.WriteInto(writer, _sound);
    }

    public void LoadState(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (!_loaded)
            throw new InvalidOperationException("TMNT core not initialized.");

        string magic = reader.ReadString();
        if (!string.Equals(magic, SavestateMagic, StringComparison.Ordinal))
            throw new InvalidDataException("TMNT savestate magic mismatch.");

        int version = reader.ReadInt32();
        if (version != SavestateVersion)
            throw new InvalidDataException($"Unsupported TMNT savestate version: {version}.");

        _frameCounter = reader.ReadInt64();
        _input = ReadInputState(reader);
        ReadByteArray(reader, _presentFrameBuffer);
        ReadByteArray(reader, _renderFrameBuffer);
        ReadByteArray(reader, _snapshotFrameBuffer);
        StateBinarySerializer.ReadInto(reader, _mainCpu);
        StateBinarySerializer.ReadInto(reader, _bus);
        StateBinarySerializer.ReadInto(reader, _sound);
        _bus.AttachSound(_sound);
        if (_audioBuffer.Length == 0)
            _audioBuffer = new short[Math.Max(1, (int)Math.Round(OutputSampleRate / TargetFps)) * OutputChannels];
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
        _input = new ArcadeInputState(up, down, left, right, a, b, c, start, mode);
    }

    private void ClearFrameBuffers()
    {
        lock (_frameSync)
        {
            Array.Clear(_presentFrameBuffer);
            Array.Clear(_snapshotFrameBuffer);
        }
        Array.Clear(_renderFrameBuffer);
    }

    private readonly record struct ArcadeInputState(
        bool Up,
        bool Down,
        bool Left,
        bool Right,
        bool Button1,
        bool Button2,
        bool Button3,
        bool Start,
        bool Coin);

    private sealed class TmntBus : EutherDrive.Core.Cpu.M68000Emu.IBusInterface, EutherDrive.Core.Cpu.M68000Emu.IOpcodeBusInterface
    {
        [NonSerialized] private readonly byte[] _program = new byte[0x60000];
        private readonly byte[] _ram = new byte[0x4000];
        private readonly byte[] _paletteRam = new byte[0x1000];
        private readonly ushort[] _palette = new ushort[0x400];
        [NonSerialized] private readonly byte[] _tileRom = new byte[0x100000];
        [NonSerialized] private readonly byte[] _spriteRom = new byte[0x200000];
        private readonly K052109 _k052109 = new();
        private readonly K051960 _k051960 = new();
        [NonSerialized] private TmntSound? _sound;

        private ArcadeInputState _input;
        private byte _interruptLevel;
        private bool _irq5Enabled;
        private byte _soundLatch = 0xff;
        private byte _lastSoundIrqBit;
        private int _priority;

        public BusSignals Signals => new(false);
        public ushort CurrentOpcode => 0;
        private int _k052109Writes;
        private int _k052109Reads;
        private int _k052ColorWrites;
        private int _k052CodeLowWrites;
        private int _k052CodeHighWrites;
        private int _k052RegisterWrites;
        private int _k052EvenByteWrites;
        private int _k052OddByteWrites;
        private int _spriteWrites;
        private int _paletteWrites;

        public void AttachSound(TmntSound sound) => _sound = sound;

        public void Load(TmntRomSet roms)
        {
            Array.Fill(_program, (byte)0xff);
            Array.Clear(_ram);
            Array.Clear(_paletteRam);
            Array.Clear(_palette);
            Array.Copy(roms.Program, _program, Math.Min(roms.Program.Length, _program.Length));
            Array.Copy(roms.TileRom, _tileRom, Math.Min(roms.TileRom.Length, _tileRom.Length));
            Array.Copy(roms.SpriteRom, _spriteRom, Math.Min(roms.SpriteRom.Length, _spriteRom.Length));
            _k052109.Load(_tileRom);
            _k051960.Load(_spriteRom);
            ResetMachine();
        }

        public void ResetMachine()
        {
            Array.Clear(_ram);
            Array.Clear(_paletteRam);
            Array.Clear(_palette);
            _k052109.Reset();
            _k051960.Reset();
            _interruptLevel = 0;
            _irq5Enabled = false;
            _soundLatch = 0xff;
            _lastSoundIrqBit = 0;
            _priority = 0;
            _k052109Writes = 0;
            _k052109Reads = 0;
            _k052ColorWrites = 0;
            _k052CodeLowWrites = 0;
            _k052CodeHighWrites = 0;
            _k052RegisterWrites = 0;
            _k052EvenByteWrites = 0;
            _k052OddByteWrites = 0;
            _spriteWrites = 0;
            _paletteWrites = 0;
        }

        public void SetInput(ArcadeInputState input) => _input = input;

        public void BeginVblank()
        {
            _k051960.BufferSprites();
            if (_irq5Enabled)
                _interruptLevel = 5;
        }

        public void Render(byte[] frameBuffer)
        {
            string renderMask = Environment.GetEnvironmentVariable("EUTHERDRIVE_TMNT_RENDER_MASK") ?? "all";
            bool drawLayer0 = renderMask == "all" || renderMask.Contains('0', StringComparison.Ordinal);
            bool drawLayer1 = renderMask == "all" || renderMask.Contains('1', StringComparison.Ordinal);
            bool drawLayer2 = renderMask == "all" || renderMask.Contains('2', StringComparison.Ordinal);
            bool drawSprites = renderMask == "all" || renderMask.Contains('s', StringComparison.OrdinalIgnoreCase);

            Array.Fill(frameBuffer, (byte)0);
            if (drawLayer2 && _k052109.LayerHasContent(2))
                _k052109.RenderLayer(frameBuffer, _palette, 2, opaque: true);
            if (drawSprites && (_priority & 1) != 0)
                _k051960.Render(frameBuffer, _palette);
            if (drawLayer1 && !_k052109.LayerIsUniform(1))
                _k052109.RenderLayer(frameBuffer, _palette, 1, opaque: false);
            if (drawSprites && (_priority & 1) == 0)
                _k051960.Render(frameBuffer, _palette);
            if (drawLayer0 && !_k052109.LayerIsUniform(0))
                _k052109.RenderLayer(frameBuffer, _palette, 0, opaque: false);
        }

        public byte ReadByte(uint address)
        {
            address &= 0x00ff_ffff;
            if (address < _program.Length)
                return _program[address];
            if (address >= 0x060000 && address <= 0x063fff)
                return _ram[address - 0x060000];
            if (address >= 0x080000 && address <= 0x080fff)
                return _paletteRam[(address - 0x080000) >> 1];
            if (IsWordMapped(address))
                return ReadWordByte(ReadWord(address & ~1u), address);
            if (address >= 0x100000 && address <= 0x107fff)
            {
                _k052109Reads++;
                int offset = NoA12Offset(address);
                return _k052109.Read((address & 1) == 0 ? offset : offset + 0x2000);
            }
            if (address >= 0x140000 && address <= 0x140007)
                return _k051960.ReadControl((int)(address - 0x140000));
            if (address >= 0x140400 && address <= 0x1407ff)
                return _k051960.ReadRam((int)(address - 0x140400));
            return 0xff;
        }

        public ushort ReadWord(uint address)
        {
            address &= 0x00ff_ffff;
            if (address < _program.Length - 1)
                return ReadBigEndianWord(_program, (int)address);
            if (address >= 0x060000 && address <= 0x063ffe)
                return ReadBigEndianWord(_ram, (int)(address - 0x060000));
            if (address >= 0x080000 && address <= 0x080ffe)
                return (ushort)(0xff00 | _paletteRam[(address - 0x080000) >> 1]);
            if (address >= 0x0a0000 && address <= 0x0a0001)
                return (ushort)(0xff00 | Coins());
            if (address >= 0x0a0002 && address <= 0x0a0003)
                return (ushort)(0xff00 | Player(1));
            if (address >= 0x0a0004 && address <= 0x0a0005)
                return 0xffff;
            if (address >= 0x0a0006 && address <= 0x0a0007)
                return 0xffff;
            if (address >= 0x0a0010 && address <= 0x0a0011)
                return 0xffff;
            if (address >= 0x0a0012 && address <= 0x0a0013)
                return 0xff5e;
            if (address >= 0x0a0014 && address <= 0x0a0015)
                return 0xffff;
            if (address >= 0x0a0018 && address <= 0x0a0019)
                return 0xffff;
            if (address >= 0x100000 && address <= 0x107fff)
            {
                int offset = NoA12Offset(address);
                return (ushort)(_k052109.Read(offset) << 8);
            }
            if (address >= 0x140000 && address <= 0x140007)
                return ReadK051960ControlWord(address);
            if (address >= 0x140400 && address <= 0x1407ff)
                return ReadK051960SpriteWord(address);
            return 0xffff;
        }

        public uint ReadLong(uint address) => ((uint)ReadWord(address) << 16) | ReadWord(address + 2);

        public void WriteByte(uint address, byte value)
        {
            address &= 0x00ff_ffff;
            if (address >= 0x060000 && address <= 0x063fff)
            {
                _ram[address - 0x060000] = value;
                return;
            }
            if (address >= 0x080000 && address <= 0x080fff)
            {
                int offset = (int)((address - 0x080000) >> 1);
                _paletteRam[offset] = value;
                UpdatePalette(offset);
                _paletteWrites++;
                return;
            }
            if (address == 0x0a0009)
            {
                _soundLatch = value;
                _sound?.SetSoundLatch(value);
                return;
            }
            if (address >= 0x100000 && address <= 0x107fff)
            {
                _k052109Writes++;
                if ((address & 1) == 0)
                    _k052EvenByteWrites++;
                else
                    _k052OddByteWrites++;
                int offset = NoA12Offset(address);
                WriteK052109((address & 1) == 0 ? offset : offset + 0x2000, value);
                return;
            }
            if (address >= 0x140000 && address <= 0x140007)
            {
                _k051960.WriteControl((int)(address - 0x140000), value);
                return;
            }
            if (address >= 0x140400 && address <= 0x1407ff)
            {
                _spriteWrites++;
                _k051960.WriteRam((int)(address - 0x140400), value);
                return;
            }
            if (IsWordMapped(address))
            {
                ushort word = ReadWord(address & ~1u);
                WriteWordByte(ref word, address, value);
                WriteWord(address & ~1u, word);
            }
        }

        public void WriteWord(uint address, ushort value)
        {
            address &= 0x00ff_ffff;
            if (address >= 0x060000 && address <= 0x063ffe)
            {
                WriteBigEndianWord(_ram, (int)(address - 0x060000), value);
                return;
            }
            if (address >= 0x080000 && address <= 0x080ffe)
            {
                // TMNT maps palette as an 8-bit device on the low byte lane.
                int offset = (int)((address - 0x080000) >> 1);
                _paletteRam[offset] = (byte)value;
                UpdatePalette(offset);
                _paletteWrites++;
                return;
            }
            if (address >= 0x0a0000 && address <= 0x0a0001)
            {
                WriteControl0a0000((byte)value);
                return;
            }
            if (address >= 0x0a0008 && address <= 0x0a0009)
            {
                _soundLatch = (byte)value;
                _sound?.SetSoundLatch((byte)value);
                return;
            }
            if (address >= 0x0c0000 && address <= 0x0c0001)
            {
                _priority = (value & 0x0c) >> 2;
                return;
            }
            if (address >= 0x100000 && address <= 0x107fff)
            {
                int offset = NoA12Offset(address);
                _k052109Writes++;
                WriteK052109(offset, (byte)(value >> 8));
                return;
            }
            if (address >= 0x140000 && address <= 0x140007)
            {
                WriteK051960ControlWord(address, value);
                return;
            }
            if (address >= 0x140400 && address <= 0x1407ff)
            {
                _spriteWrites++;
                WriteK051960SpriteWord(address, value);
            }
        }

        public void WriteLong(uint address, uint value)
        {
            WriteWord(address, (ushort)(value >> 16));
            WriteWord(address + 2, (ushort)value);
        }

        private ushort ReadK051960ControlWord(uint address)
        {
            int offset = (int)(address - 0x140000);
            int high = _k051960.ReadControl(offset);
            int low = offset < 7 ? _k051960.ReadControl(offset + 1) : 0xff;
            return (ushort)((high << 8) | low);
        }

        private ushort ReadK051960SpriteWord(uint address)
        {
            int offset = (int)(address - 0x140400);
            int high = _k051960.ReadRam(offset);
            int low = offset < 0x3ff ? _k051960.ReadRam(offset + 1) : 0xff;
            return (ushort)((high << 8) | low);
        }

        private void WriteK051960ControlWord(uint address, ushort value)
        {
            int offset = (int)(address - 0x140000);
            _k051960.WriteControl(offset, (byte)(value >> 8));
            if (offset < 7)
                _k051960.WriteControl(offset + 1, (byte)value);
        }

        private void WriteK051960SpriteWord(uint address, ushort value)
        {
            int offset = (int)(address - 0x140400);
            _k051960.WriteRam(offset, (byte)(value >> 8));
            if (offset < 0x3ff)
                _k051960.WriteRam(offset + 1, (byte)value);
        }

        public byte InterruptLevel() => _interruptLevel;

        public void AcknowledgeInterrupt(byte level)
        {
            if (_interruptLevel == level)
                _interruptLevel = 0;
        }

        public bool Reset() => false;
        public bool Halt() => false;
        public ushort ReadOpcodeWord(uint address) => ReadWord(address);

        public string DebugSummary(uint pc)
            => $"pc=0x{pc:X6} irq5={_irq5Enabled} pri={_priority} sound=0x{_soundLatch:X2} "
               + $"palW={_paletteWrites} k052W={_k052109Writes} k052R={_k052109Reads} sprW={_spriteWrites} "
               + $"k052Seg={_k052ColorWrites}/{_k052CodeLowWrites}/{_k052CodeHighWrites}/{_k052RegisterWrites} "
               + $"k052Byte={_k052EvenByteWrites}/{_k052OddByteWrites} "
               + _k052109.DebugSummary();

        private void WriteK052109(int offset, byte value)
        {
            int mappedOffset = offset % 0x6000;
            CountK052109Write(mappedOffset);
            _k052109.Write(mappedOffset, value);
        }

        private void CountK052109Write(int offset)
        {
            if ((uint)offset < 0x1800)
            {
                _k052ColorWrites++;
            }
            else if ((uint)(offset - 0x2000) < 0x1800)
            {
                _k052CodeLowWrites++;
            }
            else if ((uint)(offset - 0x4000) < 0x1800)
            {
                _k052CodeHighWrites++;
            }
            else
            {
                _k052RegisterWrites++;
            }
        }

        private void WriteControl0a0000(byte data)
        {
            byte soundIrqBit = (byte)(data & 0x08);
            if (_lastSoundIrqBit == 0x08 && soundIrqBit == 0)
            {
                _sound?.PulseIrq();
            }
            _lastSoundIrqBit = soundIrqBit;
            _irq5Enabled = (data & 0x20) != 0;
            if (!_irq5Enabled)
                _interruptLevel = 0;
            _k052109.Rmrd = (data & 0x80) != 0;
        }

        private void UpdatePalette(int offset)
        {
            int index = (offset >> 1) & 0x3ff;
            _palette[index] = ReadBigEndianWord(_paletteRam, index * 2);
        }

        private byte Coins()
        {
            int value = 0xff;
            if (_input.Coin)
                value &= ~0x01;
            return (byte)value;
        }

        private byte Player(int player)
        {
            if (player != 1)
                return 0xff;

            int value = 0xff;
            if (_input.Left) value &= ~0x01;
            if (_input.Right) value &= ~0x02;
            if (_input.Up) value &= ~0x04;
            if (_input.Down) value &= ~0x08;
            if (_input.Button1) value &= ~0x10;
            if (_input.Button2) value &= ~0x20;
            if (_input.Button3) value &= ~0x40;
            if (_input.Start) value &= ~0x80;
            return (byte)value;
        }

        private static bool IsWordMapped(uint address)
            => (address >= 0x0a0000 && address <= 0x0a0019)
               || (address >= 0x0c0000 && address <= 0x0c0001);

        private static int NoA12Offset(uint address)
        {
            int offset = (int)((address - 0x100000) >> 1);
            return ((offset & 0x3000) >> 1) | (offset & 0x07ff);
        }

        private static byte ReadWordByte(ushort word, uint address)
            => (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;

        private static void WriteWordByte(ref ushort word, uint address, byte value)
        {
            word = (address & 1) == 0
                ? (ushort)((word & 0x00ff) | (value << 8))
                : (ushort)((word & 0xff00) | value);
        }
    }

    private sealed class K052109
    {
        private readonly byte[] _ram = new byte[0x6000];
        [NonSerialized] private readonly byte[] _rom = new byte[0x100000];
        private readonly byte[] _charBank = new byte[4];
        private readonly byte[] _charBank2 = new byte[4];
        private byte _addrMap;
        private byte _scrollCtrl;
        private byte _tileFlipEnable;
        private byte _romSubBank;

        public bool Rmrd { get; set; }

        public void Load(byte[] rom) => rom.CopyTo(_rom, 0);

        public void Reset()
        {
            Array.Clear(_ram);
            Array.Clear(_charBank);
            Array.Clear(_charBank2);
            Rmrd = false;
            _addrMap = 0;
            _scrollCtrl = 0;
            _tileFlipEnable = 0;
            _romSubBank = 0;
        }

        public byte Read(int offset)
        {
            offset = WrapRamOffset(offset);
            if (Rmrd)
                return ReadCharRom(offset);
            return _ram[offset];
        }

        public void Write(int offset, byte data)
        {
            offset = WrapRamOffset(offset);
            _ram[offset] = data;
            switch (offset)
            {
                case 0x1c00:
                    _addrMap = data;
                    break;
                case 0x1c80:
                    _scrollCtrl = data;
                    break;
                case 0x1d80:
                    _charBank[0] = (byte)(data & 0x0f);
                    _charBank[1] = (byte)(data >> 4);
                    break;
                case 0x1e80:
                    _tileFlipEnable = data;
                    break;
                case 0x1e00:
                case 0x3e00:
                    _romSubBank = data;
                    break;
                case 0x1f00:
                    _charBank[2] = (byte)(data & 0x0f);
                    _charBank[3] = (byte)(data >> 4);
                    break;
                case 0x3d80:
                    _charBank2[0] = (byte)(data & 0x0f);
                    _charBank2[1] = (byte)(data >> 4);
                    break;
                case 0x3f00:
                    _charBank2[2] = (byte)(data & 0x0f);
                    _charBank2[3] = (byte)(data >> 4);
                    break;
            }
        }

        private static int WrapRamOffset(int offset) => offset % 0x6000;

        public void RenderLayer(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int layer, bool opaque)
        {
            int attrBase = layer switch { 0 => 0x0000, 1 => 0x0800, _ => 0x1000 };
            int codeBase = layer switch { 0 => 0x2000, 1 => 0x2800, _ => 0x3000 };
            int code2Base = layer switch { 0 => 0x4000, 1 => 0x4800, _ => 0x5000 };
            (int scrollX, int scrollY) = GetScroll(layer);

            for (int sy = 0; sy < FrameHeight; sy++)
            {
                int worldY = (sy + scrollY) & 0xff;
                int tileY = worldY >> 3;
                int pixelY = worldY & 7;
                for (int sx = 0; sx < FrameWidth; sx++)
                {
                    int worldX = (sx + scrollX) & 0x1ff;
                    int tileX = worldX >> 3;
                    int pixelX = worldX & 7;
                    int tileIndex = ((tileY & 31) * 64) + (tileX & 63);

                    byte attr = _ram[attrBase + tileIndex];
                    int code = _ram[codeBase + tileIndex] | (_ram[code2Base + tileIndex] << 8);
                    int bank = _charBank[(attr & 0x0c) >> 2];
                    if ((_addrMap & 0x40) == 0)
                        attr = (byte)((attr & 0xf3) | ((bank & 0x03) << 2));
                    bank >>= 2;

                    TmntTileCallback(layer, bank, ref code, ref attr);
                    int pen = DecodeTilePixel(code, pixelX, pixelY);
                    if (pen == 0 && !opaque)
                        continue;

                    int color = (attr & 0x7f) * 16 + pen;
                    WritePixel(frameBuffer, sx, sy, palette[color & 0x3ff]);
                }
            }
        }

        public bool LayerHasContent(int layer)
        {
            int attrBase = layer switch { 0 => 0x0000, 1 => 0x0800, _ => 0x1000 };
            int codeBase = layer switch { 0 => 0x2000, 1 => 0x2800, _ => 0x3000 };
            int code2Base = layer switch { 0 => 0x4000, 1 => 0x4800, _ => 0x5000 };
            for (int i = 0; i < 0x800; i++)
            {
                if ((_ram[attrBase + i] | _ram[codeBase + i] | _ram[code2Base + i]) != 0)
                    return true;
            }
            return false;
        }

        public bool LayerIsUniform(int layer)
        {
            int attrBase = layer switch { 0 => 0x0000, 1 => 0x0800, _ => 0x1000 };
            int codeBase = layer switch { 0 => 0x2000, 1 => 0x2800, _ => 0x3000 };
            int code2Base = layer switch { 0 => 0x4000, 1 => 0x4800, _ => 0x5000 };
            byte attr = _ram[attrBase];
            byte code = _ram[codeBase];
            byte code2 = _ram[code2Base];
            for (int i = 1; i < 0x800; i++)
            {
                if (_ram[attrBase + i] != attr || _ram[codeBase + i] != code || _ram[code2Base + i] != code2)
                    return false;
            }
            return true;
        }

        public string DebugSummary()
        {
            int nonZero = 0;
            for (int i = 0; i < _ram.Length; i++)
            {
                if (_ram[i] != 0)
                    nonZero++;
            }
            return $"k052nz={nonZero} layers={LayerNonZero(0)}/{LayerNonZero(1)}/{LayerNonZero(2)} "
                   + $"l0={LayerSample(0)} l1={LayerSample(1)} l2={LayerSample(2)} "
                   + $"addrMap=0x{_addrMap:X2} scroll=0x{_scrollCtrl:X2} flip=0x{_tileFlipEnable:X2} rsub=0x{_romSubBank:X2}";
        }

        private int LayerNonZero(int layer)
        {
            int attrBase = layer switch { 0 => 0x0000, 1 => 0x0800, _ => 0x1000 };
            int codeBase = layer switch { 0 => 0x2000, 1 => 0x2800, _ => 0x3000 };
            int code2Base = layer switch { 0 => 0x4000, 1 => 0x4800, _ => 0x5000 };
            int count = 0;
            for (int i = 0; i < 0x800; i++)
            {
                if ((_ram[attrBase + i] | _ram[codeBase + i] | _ram[code2Base + i]) != 0)
                    count++;
            }
            return count;
        }

        private string LayerSample(int layer)
        {
            int attrBase = layer switch { 0 => 0x0000, 1 => 0x0800, _ => 0x1000 };
            int codeBase = layer switch { 0 => 0x2000, 1 => 0x2800, _ => 0x3000 };
            int code2Base = layer switch { 0 => 0x4000, 1 => 0x4800, _ => 0x5000 };
            int first = -1;
            int last = -1;
            int sameCode = 0;
            int firstCode = -1;
            for (int i = 0; i < 0x800; i++)
            {
                int code = _ram[codeBase + i] | (_ram[code2Base + i] << 8);
                if ((_ram[attrBase + i] | code) == 0)
                    continue;
                if (first < 0)
                {
                    first = i;
                    firstCode = code;
                }
                if (code == firstCode)
                    sameCode++;
                last = i;
            }
            return first < 0
                ? "empty"
                : $"{first:X3}-{last:X3}:a{_ram[attrBase + first]:X2}:c{firstCode:X4}:same{sameCode}";
        }

        private (int x, int y) GetScroll(int layer)
        {
            if (layer == 0)
                return (96, 0);

            int baseMask = layer == 1 ? 0x0000 : 0x2000;
            int scrollXBase = 0x1a00 | baseMask;
            int scrollYBase = 0x1800 | baseMask;
            int x = _ram[scrollXBase] | (_ram[scrollXBase + 1] << 8);
            int y = _ram[scrollYBase + 12];
            x += 90;
            return (x, y);
        }

        private byte ReadCharRom(int offset)
        {
            int code = (offset & 0x1fff) >> 5;
            byte color = _romSubBank;
            int bankIndex = (color & 0x0c) >> 2;
            int bank = (_charBank[bankIndex] >> 2) | (_charBank2[bankIndex] >> 2);
            TmntTileCallback(0, bank, ref code, ref color);
            int address = ((code << 5) | (offset & 0x1f)) & (_rom.Length - 1);
            return _rom[address];
        }

        private int DecodeTilePixel(int code, int x, int y)
        {
            int address = ((code & 0x7fff) * 32 + y * 4) & (_rom.Length - 1);
            int bit = 7 - x;
            return (((_rom[address + 3] >> bit) & 1) << 3)
                   | (((_rom[address + 2] >> bit) & 1) << 2)
                   | (((_rom[address + 1] >> bit) & 1) << 1)
                   | ((_rom[address + 0] >> bit) & 1);
        }
    }

    private sealed class K051960
    {
        private static readonly int[] XOffset = { 0, 1, 4, 5, 16, 17, 20, 21 };
        private static readonly int[] YOffset = { 0, 2, 8, 10, 32, 34, 40, 42 };
        private static readonly int[] Width = { 1, 2, 1, 2, 4, 2, 4, 8 };
        private static readonly int[] Height = { 1, 1, 2, 2, 2, 4, 4, 8 };

        private readonly byte[] _ram = new byte[0x400];
        private readonly byte[] _buffer = new byte[0x400];
        [NonSerialized] private readonly byte[] _rom = new byte[0x200000];
        private readonly byte[] _spriteRomBank = new byte[3];
        private int _romOffset;
        private byte _control;
        private byte _shadowConfig;

        public void Load(byte[] rom) => rom.CopyTo(_rom, 0);

        public void Reset()
        {
            Array.Clear(_ram);
            Array.Clear(_buffer);
            Array.Clear(_spriteRomBank);
            _romOffset = 0;
            _control = 0;
            _shadowConfig = 0;
        }

        public void VBlank()
        {
        }

        public void BufferSprites()
        {
            if ((_control & 0x10) == 0)
                _ram.CopyTo(_buffer, 0);
        }

        public byte ReadControl(int offset)
        {
            offset &= 7;
            if ((_control & 0x20) != 0 && (offset & 4) != 0)
                return FetchRom(offset & 3);
            return offset == 0 ? (byte)0 : (byte)0xff;
        }

        public void WriteControl(int offset, byte data)
        {
            offset &= 7;
            if (offset == 0)
                _control = data;
            else if (offset == 1)
                _shadowConfig = (byte)(data & 0x07);
            else if (offset >= 2 && offset < 5)
                _spriteRomBank[offset - 2] = data;
        }

        public byte ReadRam(int offset)
        {
            offset &= 0x3ff;
            if ((_control & 0x20) != 0)
            {
                _romOffset = (offset & 0x3fc) >> 2;
                return FetchRom(offset & 3);
            }
            return _ram[offset];
        }

        public void WriteRam(int offset, byte data) => _ram[offset & 0x3ff] = data;

        public void Render(byte[] frameBuffer, ReadOnlySpan<ushort> palette)
        {
            Span<int> sorted = stackalloc int[128];
            sorted.Fill(-1);
            for (int offs = 0; offs < 0x400; offs += 8)
            {
                if ((_buffer[offs] & 0x80) != 0)
                    sorted[_buffer[offs] & 0x7f] = offs;
            }

            for (int priCode = 0; priCode < 128; priCode++)
            {
                int offs = sorted[priCode];
                if (offs < 0)
                    continue;

                int code = _buffer[offs + 2] | ((_buffer[offs + 1] & 0x1f) << 8);
                byte attr = _buffer[offs + 3];
                code |= (attr & 0x10) << 9;
                int colorBase = 16 + (attr & 0x0f);

                int size = (_buffer[offs + 1] & 0xe0) >> 5;
                int w = Width[size];
                int h = Height[size];
                if (w >= 2) code &= ~0x01;
                if (h >= 2) code &= ~0x02;
                if (w >= 4) code &= ~0x04;
                if (h >= 4) code &= ~0x08;
                if (w >= 8) code &= ~0x10;
                if (h >= 8) code &= ~0x20;

                int ox = ((_buffer[offs + 6] << 8) | _buffer[offs + 7]) & 0x01ff;
                int oy = 256 - (((_buffer[offs + 4] << 8) | _buffer[offs + 5]) & 0x01ff);
                bool flipX = (_buffer[offs + 6] & 0x02) != 0;
                bool flipY = (_buffer[offs + 4] & 0x02) != 0;

                for (int y = 0; y < h; y++)
                {
                    int sy = oy + 16 * y;
                    for (int x = 0; x < w; x++)
                    {
                        int tileCode = code
                            + (flipX ? XOffset[w - 1 - x] : XOffset[x])
                            + (flipY ? YOffset[h - 1 - y] : YOffset[y]);
                        int sx = ((ox + 16 * x) & 0x1ff) - 96;
                        DrawSpriteTile(frameBuffer, palette, tileCode, colorBase, sx, sy, flipX, flipY);
                    }
                }
            }
        }

        private byte FetchRom(int offset)
        {
            int addr = _romOffset + (_spriteRomBank[0] << 8) + ((_spriteRomBank[1] & 0x03) << 16);
            int code = (addr & 0x3ffe0) >> 5;
            int off1 = addr & 0x1f;
            int color = ((_spriteRomBank[1] & 0xfc) >> 2) + ((_spriteRomBank[2] & 0x03) << 6);
            code |= (color & 0x10) << 9;
            addr = (code << 7) | (off1 << 2) | offset;
            return _rom[addr & (_rom.Length - 1)];
        }

        private void DrawSpriteTile(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int code, int colorBase, int sx, int sy, bool flipX, bool flipY)
        {
            int address = ((code & 0x3fff) * 128) & (_rom.Length - 1);
            for (int y = 0; y < 16; y++)
            {
                int py = sy + y;
                if ((uint)py >= FrameHeight)
                    continue;
                int srcY = flipY ? 15 - y : y;
                for (int x = 0; x < 16; x++)
                {
                    int px = sx + x;
                    if ((uint)px >= FrameWidth)
                        continue;
                    int srcX = flipX ? 15 - x : x;
                    int pen = DecodeSpritePixel(address, srcX, srcY);
                    if (pen == 0)
                        continue;
                    int color = colorBase * 16 + pen;
                    WritePixel(frameBuffer, px, py, palette[color & 0x3ff]);
                }
            }
        }

        private int DecodeSpritePixel(int baseAddress, int x, int y)
        {
            int address = (baseAddress + (y & 7) * 4 + (y >= 8 ? 64 : 0)) & (_rom.Length - 1);
            if (x >= 8)
                address += 32;
            int bit = 7 - (x & 7);
            return (((_rom[address + 3] >> bit) & 1) << 3)
                   | (((_rom[address + 2] >> bit) & 1) << 2)
                   | (((_rom[address + 1] >> bit) & 1) << 1)
                   | ((_rom[address + 0] >> bit) & 1);
        }
    }

    private sealed class TmntSound : EutherDrive.Core.Cpu.Z80Emu.IOpcodeBusInterface
    {
        private const int AudioCpuClock = 3_579_545;
        private const int AudioCpuCyclesPerFrame = 60_480;

        [NonSerialized] private readonly byte[] _program = new byte[0x10000];
        private readonly byte[] _ram = new byte[0x800];
        private readonly Z80 _cpu = new();
        private readonly Cps1Ym2151 _ym = new();
        private readonly K007232Pcm _pcm = new();
        private readonly Upd7759Adpcm _upd = new();
        [NonSerialized] private short[] _titleSample = Array.Empty<short>();

        private byte _soundLatch = 0xff;
        private byte _sres = 0xff;
        private bool _irqAsserted;
        private double _outputFrameAccumulator;
        private int _audioFrameSampleIndex;
        private short[]? _audioFrameBuffer;
        private bool _titlePlaying;
        private double _titleSamplePosition;
        private int _lastPeak;
        private int _ymWrites;
        private int _pcmWrites;
        private int _sresWrites;
        private int _irqPulses;
        private double _z80CycleAccumulator;
        private int _pendingRenderCycles;
        private int _z80CyclesThisFrame;
        private long _soundFrameCounter;
        [NonSerialized] private TmntAudioProbe? _audioProbe;
        [NonSerialized] private TmntAudioTrace? _audioTrace;

        public string DebugSummary
            => $"z80pc=0x{_cpu.Pc:X4} z80stalled={_cpu.Stalled} sndLatch=0x{_soundLatch:X2} "
               + $"sres=0x{_sres:X2} sresW={_sresWrites} ymW={_ymWrites} pcmW={_pcmWrites} {_pcm.DebugSummary} "
               + $"{_upd.DebugSummary} irqP={_irqPulses} audPeak={_lastPeak} probe={(_audioProbe?.Enabled == true ? 1 : 0)}";

        public void Load(TmntRomSet roms)
        {
            Array.Clear(_program);
            Array.Copy(roms.AudioCpu, _program, Math.Min(roms.AudioCpu.Length, _program.Length));
            _pcm.Load(roms.K007232);
            _upd.Load(roms.Upd7759);
            _titleSample = DecodeTitleSample(roms.TitleSample);
            ResetMachine();
        }

        public void ResetMachine()
        {
            Array.Clear(_ram);
            _cpu.ApplyResetLine();
            _ym.Reset();
            _pcm.Reset();
            _upd.Reset();
            _soundLatch = 0xff;
            _sres = 0xff;
            _irqAsserted = false;
            _outputFrameAccumulator = 0;
            _audioFrameSampleIndex = 0;
            _audioFrameBuffer = null;
            _titlePlaying = false;
            _titleSamplePosition = 0;
            _lastPeak = 0;
            _ymWrites = 0;
            _pcmWrites = 0;
            _sresWrites = 0;
            _irqPulses = 0;
            _z80CycleAccumulator = 0;
            _pendingRenderCycles = 0;
            _z80CyclesThisFrame = 0;
            _soundFrameCounter = 0;
            _audioTrace = null;
        }

        public void SetSoundLatch(byte value)
        {
            _soundLatch = value;
            TraceAudioEvent($"main-latch value=0x{value:X2}");
            TraceAudioState($"main-latch value=0x{value:X2}");
        }

        public void PulseIrq()
        {
            _irqAsserted = true;
            _irqPulses++;
            TraceAudioEvent($"sound-irq pulse={_irqPulses}");
            TraceAudioState($"sound-irq pulse={_irqPulses}");
        }

        public void BeginFrame(short[] audioBuffer)
        {
            if (audioBuffer.Length == 0)
                return;

            Array.Clear(audioBuffer);
            _audioFrameBuffer = audioBuffer;
            _audioFrameSampleIndex = 0;
            _z80CyclesThisFrame = 0;
            _audioProbe ??= TmntAudioProbe.TryCreate();
            _audioProbe?.BeginFrame(audioBuffer.Length);
            _audioTrace ??= TmntAudioTrace.TryCreate(_upd);
            _audioTrace?.BeginFrame(_soundFrameCounter);
        }

        public void RunMainCpuCycles(int mainCpuCycles, int mainCpuCyclesPerFrame)
        {
            if (_audioFrameBuffer is null || mainCpuCycles <= 0 || mainCpuCyclesPerFrame <= 0)
                return;

            _z80CycleAccumulator += mainCpuCycles * (AudioCpuCyclesPerFrame / (double)mainCpuCyclesPerFrame);
            RunAudioCpuCredit();
        }

        public void EndFrame()
        {
            if (_audioFrameBuffer is null)
                return;

            RunAudioCpuCredit();

            RenderElapsedAudioCycles(_pendingRenderCycles);
            _pendingRenderCycles = 0;

            short[] audioBuffer = _audioFrameBuffer;
            int tailStart = _audioFrameSampleIndex;
            RenderAudioRange(audioBuffer, tailStart, audioBuffer.Length / 2);
            _lastPeak = Peak(audioBuffer);
            _audioProbe?.EndFrame(_soundFrameCounter, _z80CyclesThisFrame, audioBuffer, _lastPeak);
            _soundFrameCounter++;
            _audioFrameBuffer = null;
        }

        public void RunFrame(short[] audioBuffer)
        {
            BeginFrame(audioBuffer);
            RunAudioCpuCycles(AudioCpuCyclesPerFrame);
            EndFrame();
        }

        private void RunAudioCpuCycles(int cycleBudget)
        {
            if (_audioFrameBuffer is null || cycleBudget <= 0)
                return;

            _z80CycleAccumulator += cycleBudget;
            RunAudioCpuCredit();
        }

        private void RunAudioCpuCredit()
        {
            if (_audioFrameBuffer is null)
                return;

            while (_z80CycleAccumulator >= 1.0)
            {
                RenderElapsedAudioCycles(_pendingRenderCycles);
                _pendingRenderCycles = 0;

                uint elapsed = _cpu.ExecuteInstruction(this);
                _z80CycleAccumulator -= elapsed;
                _z80CyclesThisFrame += (int)elapsed;
                _ym.AdvanceTimersByCpuCycles((int)elapsed, AudioCpuClock);
                _pendingRenderCycles += (int)elapsed;

                if (_cpu.LastInterruptAccepted)
                {
                    TraceAudioState("z80-int-ack vector=0xFF");
                    _irqAsserted = false;
                }
            }
        }

        private void RenderElapsedAudioCycles(int elapsedCycles)
        {
            if (_audioFrameBuffer is not { } audioBuffer || elapsedCycles <= 0)
                return;

            double outputFramesPerZ80Cycle = OutputSampleRate / (double)AudioCpuClock;
            _outputFrameAccumulator += elapsedCycles * outputFramesPerZ80Cycle;
            int framesToRender = (int)_outputFrameAccumulator;
            if (framesToRender <= 0)
                return;

            int before = _audioFrameSampleIndex;
            int target = Math.Min(audioBuffer.Length / 2, _audioFrameSampleIndex + framesToRender);
            RenderAudioRange(audioBuffer, before, target);
            _outputFrameAccumulator -= _audioFrameSampleIndex - before;
        }

        private void RenderAudioRange(short[] audioBuffer, int startFrame, int targetFrame)
        {
            if (_audioProbe?.Enabled == true)
            {
                short[] ym = _audioProbe.Ym;
                short[] pcm = _audioProbe.K007232;
                short[] upd = _audioProbe.Upd7759;
                short[] title = _audioProbe.Title;
                int ymIndex = _audioFrameSampleIndex;
                _ym.RenderStereo(ym, ref ymIndex, targetFrame, gain: Ym2151RouteGain, outputSampleRate: OutputSampleRate, routeToMono: true);
                _audioFrameSampleIndex = ymIndex;
                _pcm.RenderStereo(pcm, startFrame, targetFrame, gain: K007232RouteGain, outputSampleRate: OutputSampleRate, routeToMono: true);
                _upd.RenderStereo(upd, startFrame, targetFrame, gain: Upd7759RouteGain, outputSampleRate: OutputSampleRate);
                RenderTitleSample(title, startFrame, targetFrame, gain: TitleSampleRouteGain, outputSampleRate: OutputSampleRate);
                MixStems(audioBuffer, startFrame, targetFrame, ym, pcm, upd, title);
                return;
            }

            _ym.RenderStereo(audioBuffer, ref _audioFrameSampleIndex, targetFrame, gain: Ym2151RouteGain, outputSampleRate: OutputSampleRate, routeToMono: true);
            _pcm.RenderStereo(audioBuffer, startFrame, targetFrame, gain: K007232RouteGain, outputSampleRate: OutputSampleRate, routeToMono: true);
            _upd.RenderStereo(audioBuffer, startFrame, targetFrame, gain: Upd7759RouteGain, outputSampleRate: OutputSampleRate);
            RenderTitleSample(audioBuffer, startFrame, targetFrame, gain: TitleSampleRouteGain, outputSampleRate: OutputSampleRate);
        }

        public byte ReadOpcode(ushort address) => ReadMemory(address);

        public byte ReadMemory(ushort address)
        {
            if (address < 0x8000)
                return _program[address];
            if (address is >= 0x8000 and <= 0x87ff)
                return _ram[address - 0x8000];
            if (address == 0x9000)
            {
                TraceAudioState($"read sres value=0x{_sres:X2}");
                return _sres;
            }
            if (address == 0xa000)
            {
                TraceAudioState($"read soundlatch value=0x{_soundLatch:X2}");
                return _soundLatch;
            }
            if (address is >= 0xb000 and <= 0xb00d)
            {
                byte value = _pcm.Read(address - 0xb000);
                TraceAudioState($"read k007232 off=0x{address - 0xb000:X2} value=0x{value:X2} state={_pcm.DebugSummary}");
                return value;
            }
            if (address is >= 0xc000 and <= 0xc001)
            {
                byte status = _ym.ReadStatus();
                TraceAudioState($"read ym2151 status=0x{status:X2}");
                return status;
            }
            if (address == 0xf000)
            {
                byte busy = _upd.BusyRead();
                TraceAudioState($"read upd-busy value=0x{busy:X2} state={_upd.DebugSummary}");
                return busy;
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
            if (address == 0x9000)
            {
                _sres = value;
                _sresWrites++;
                TraceAudioEvent($"sres write value=0x{value:X2} reset={(value & 0x02) != 0} title={(value & 0x04) != 0}");
                TraceAudioState($"write sres value=0x{value:X2} reset={(value & 0x02) != 0} title={(value & 0x04) != 0}");
                _upd.ResetLine((value & 0x02) != 0);
                if ((value & 0x04) != 0)
                {
                    if (!_titlePlaying)
                        _titleSamplePosition = 0;
                    _titlePlaying = true;
                }
                else
                    _titlePlaying = false;
                return;
            }
            if (address is >= 0xb000 and <= 0xb00d)
            {
                TraceAudioEvent($"k007232 write off=0x{address - 0xb000:X2} value=0x{value:X2}");
                _pcm.Write(address - 0xb000, value);
                TraceAudioState($"write k007232 off=0x{address - 0xb000:X2} value=0x{value:X2} state={_pcm.DebugSummary}");
                _pcmWrites++;
                return;
            }
            if (address is >= 0xc000 and <= 0xc001)
            {
                TraceAudioEvent($"ym2151 write off={address - 0xc000} value=0x{value:X2}");
                TraceAudioState($"write ym2151 off={address - 0xc000} value=0x{value:X2}");
                _ym.Write(address - 0xc000, value);
                _ymWrites++;
                return;
            }
            if (address == 0xd000)
            {
                TraceAudioEvent($"upd port value=0x{value:X2}");
                TraceAudioState($"write upd-port value=0x{value:X2} expected={_upd.DescribeSample(value)}");
                _upd.PortWrite(value);
                return;
            }
            if (address == 0xe000)
            {
                bool startHigh = (value & 0x01) == 0;
                TraceAudioEvent($"upd start-line high={startHigh} raw=0x{value:X2}");
                TraceAudioState($"write upd-start high={startHigh} raw=0x{value:X2} before={_upd.DebugSummary}");
                _upd.StartLine(startHigh);
                TraceAudioState($"upd-start-after {_upd.DebugSummary}");
                return;
            }
        }

        public byte ReadIo(ushort address) => 0xff;
        public void WriteIo(ushort address, byte value) { }
        public InterruptLine Nmi() => InterruptLine.High;
        public InterruptLine Int() => _irqAsserted ? InterruptLine.Low : InterruptLine.High;
        public byte InterruptVector() => 0xff;
        public bool BusReq() => false;
        public bool Reset() => false;

        private void TraceAudioEvent(string message)
        {
            if (_audioProbe?.Enabled != true)
                return;
            _audioProbe.Trace(_soundFrameCounter, _z80CyclesThisFrame, _cpu.Pc, message);
        }

        private void TraceAudioState(string message)
        {
            if (_audioTrace?.Enabled != true)
                return;
            _audioTrace.Trace(_soundFrameCounter, _z80CyclesThisFrame, _cpu.Pc, message);
        }

        private static void MixStems(
            short[] destination,
            int startFrame,
            int targetFrame,
            short[] ym,
            short[] pcm,
            short[] upd,
            short[] title)
        {
            int start = Math.Clamp(startFrame * 2, 0, destination.Length);
            int end = Math.Clamp(targetFrame * 2, start, destination.Length);
            for (int i = start; i < end; i++)
                destination[i] = Mix(Mix(Mix(ym[i], pcm[i]), upd[i]), title[i]);
        }

        private sealed class TmntAudioProbe
        {
            private readonly string _directory;
            private StreamWriter? _events;
            private FileStream? _mixStream;
            private FileStream? _ymStream;
            private FileStream? _pcmStream;
            private FileStream? _updStream;
            private FileStream? _titleStream;

            private TmntAudioProbe(string directory)
            {
                _directory = directory;
            }

            public bool Enabled => true;
            public short[] Ym { get; private set; } = Array.Empty<short>();
            public short[] K007232 { get; private set; } = Array.Empty<short>();
            public short[] Upd7759 { get; private set; } = Array.Empty<short>();
            public short[] Title { get; private set; } = Array.Empty<short>();

            public static TmntAudioProbe? TryCreate()
            {
                string? enabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_TMNT_AUDIO_PROBE");
                string? directory = Environment.GetEnvironmentVariable("EUTHERDRIVE_TMNT_AUDIO_PROBE_DIR");
                if (enabled != "1" && string.IsNullOrWhiteSpace(directory))
                    return null;

                if (string.IsNullOrWhiteSpace(directory))
                    directory = Path.Combine(Directory.GetCurrentDirectory(), "logs", "tmnt_audio_probe");

                Directory.CreateDirectory(directory);
                return new TmntAudioProbe(directory);
            }

            public void BeginFrame(int samples)
            {
                EnsureBuffers(samples);
                Array.Clear(Ym);
                Array.Clear(K007232);
                Array.Clear(Upd7759);
                Array.Clear(Title);
                EnsureStreams();
            }

            public void Trace(long frame, int z80Cycle, ushort pc, string message)
            {
                EnsureStreams();
                _events?.WriteLine($"frame={frame} z80cyc={z80Cycle} pc=0x{pc:X4} {message}");
            }

            public void EndFrame(long frame, int z80Cycles, short[] mix, int peak)
            {
                EnsureStreams();
                WriteRaw(_mixStream, mix);
                WriteRaw(_ymStream, Ym);
                WriteRaw(_pcmStream, K007232);
                WriteRaw(_updStream, Upd7759);
                WriteRaw(_titleStream, Title);
                _events?.WriteLine(
                    $"frame={frame} end z80cyc={z80Cycles} mixPeak={peak} ymPeak={Peak(Ym)} k007Peak={Peak(K007232)} updPeak={Peak(Upd7759)} titlePeak={Peak(Title)}");
                _events?.Flush();
            }

            private void EnsureBuffers(int samples)
            {
                if (Ym.Length == samples)
                    return;

                Ym = new short[samples];
                K007232 = new short[samples];
                Upd7759 = new short[samples];
                Title = new short[samples];
            }

            private void EnsureStreams()
            {
                _events ??= new StreamWriter(
                    File.Open(Path.Combine(_directory, "events.log"), FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = false
                };
                _mixStream ??= OpenRaw("mix_s16le.raw");
                _ymStream ??= OpenRaw("ym2151_s16le.raw");
                _pcmStream ??= OpenRaw("k007232_s16le.raw");
                _updStream ??= OpenRaw("upd7759_s16le.raw");
                _titleStream ??= OpenRaw("title_s16le.raw");
            }

            private FileStream OpenRaw(string fileName)
                => File.Open(Path.Combine(_directory, fileName), FileMode.Append, FileAccess.Write, FileShare.Read);

            private static void WriteRaw(FileStream? stream, short[] samples)
            {
                if (stream == null || samples.Length == 0)
                    return;

                byte[] bytes = new byte[samples.Length * sizeof(short)];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private sealed class TmntAudioTrace
        {
            private readonly string _directory;
            private StreamWriter? _trace;
            private long _currentFrame = -1;

            private TmntAudioTrace(string directory, Upd7759Adpcm upd)
            {
                _directory = directory;
                Directory.CreateDirectory(directory);
                using var expected = new StreamWriter(
                    File.Open(Path.Combine(directory, "upd_expected.log"), FileMode.Create, FileAccess.Write, FileShare.Read));
                for (int sample = 0; sample <= upd.LastSampleIndex; sample++)
                    expected.WriteLine($"sample=0x{sample:X2} {upd.DescribeSample((byte)sample)}");
            }

            public bool Enabled => true;

            public static TmntAudioTrace? TryCreate(Upd7759Adpcm upd)
            {
                string? enabled = Environment.GetEnvironmentVariable("EUTHERDRIVE_TMNT_AUDIO_TRACE");
                string? directory = Environment.GetEnvironmentVariable("EUTHERDRIVE_TMNT_AUDIO_TRACE_DIR");
                if (enabled != "1" && string.IsNullOrWhiteSpace(directory))
                    return null;

                if (string.IsNullOrWhiteSpace(directory))
                    directory = Path.Combine(Directory.GetCurrentDirectory(), "logs", "tmnt_audio_trace");

                return new TmntAudioTrace(directory, upd);
            }

            public void BeginFrame(long frame)
            {
                EnsureTrace();
                if (_currentFrame != frame)
                {
                    _currentFrame = frame;
                    _trace?.WriteLine($"frame={frame} begin");
                }
            }

            public void Trace(long frame, int z80Cycle, ushort pc, string message)
            {
                EnsureTrace();
                _trace?.WriteLine($"frame={frame} z80cyc={z80Cycle} pc=0x{pc:X4} {message}");
                _trace?.Flush();
            }

            private void EnsureTrace()
            {
                _trace ??= new StreamWriter(
                    File.Open(Path.Combine(_directory, "trace.log"), FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = false
                };
            }
        }

        private void RenderTitleSample(short[] destination, int startFrame, int targetFrame, float gain, int outputSampleRate)
        {
            if (!_titlePlaying || _titleSample.Length == 0)
                return;

            int maxFrames = destination.Length / 2;
            startFrame = Math.Clamp(startFrame, 0, maxFrames);
            targetFrame = Math.Clamp(targetFrame, startFrame, maxFrames);
            double step = 20_000.0 / outputSampleRate;
            for (int frame = startFrame; frame < targetFrame && _titlePlaying; frame++)
            {
                int index = (int)_titleSamplePosition;
                if ((uint)index >= (uint)_titleSample.Length)
                {
                    _titlePlaying = false;
                    break;
                }

                int sample = (int)Math.Round(_titleSample[index] * gain);
                int offset = frame * 2;
                destination[offset] = Mix(destination[offset], sample);
                destination[offset + 1] = Mix(destination[offset + 1], sample);
                _titleSamplePosition += step;
            }
        }

        private static short[] DecodeTitleSample(byte[] source)
        {
            int samples = Math.Min(0x40000, source.Length / 2);
            short[] decoded = new short[samples];
            for (int i = 0; i < samples; i++)
            {
                short value = (short)((source[2 * i] | (source[2 * i + 1] << 8)) >> 3);
                decoded[i] = DecodeYmFp(value);
            }
            return decoded;
        }

        private static short DecodeYmFp(short value)
        {
            value ^= 0x1e00;
            int exponent = (value >> 10) & 0x07;
            return (short)((short)(value << 6) >> exponent);
        }

        private static int Peak(short[] buffer)
        {
            int peak = 0;
            for (int i = 0; i < buffer.Length; i++)
            {
                int value = buffer[i];
                if (value < 0)
                    value = -value;
                if (value > peak)
                    peak = value;
            }
            return peak;
        }

        private static short Mix(short current, int sample)
            => (short)Math.Clamp(current + sample, short.MinValue, short.MaxValue);
    }

    // Minimal K007232 PCM translation from MAME's BSD-3-Clause k007232 device.
    private sealed class K007232Pcm
    {
        private const int ChipClock = 3_579_545;
        private const int SourceSampleRate = ChipClock / 128;

        private readonly byte[] _registers = new byte[0x10];
        private readonly Channel[] _channels = { new(), new() };
        [NonSerialized] private byte[] _rom = Array.Empty<byte>();
        private double _sourcePhase;
        private short _lastLeft;
        private short _lastRight;
        private short _nextLeft;
        private short _nextRight;
        private bool _primed;
        private int _starts;
        private int _reads;
        private int _lastLeftSum;
        private int _lastRightSum;

        public string DebugSummary
            => $"pcmStart={_starts} pcmRead={_reads} pcmPlay={(_channels[0].Play ? 1 : 0)}/{(_channels[1].Play ? 1 : 0)} "
               + $"pcmStep={_channels[0].Step:X3}/{_channels[1].Step:X3} pcmAddr={_channels[0].Address:X5}/{_channels[1].Address:X5} "
               + $"pcmSum={_lastLeftSum}/{_lastRightSum}";

        public void Load(byte[] rom)
        {
            _rom = rom;
            Reset();
        }

        public void Reset()
        {
            Array.Clear(_registers);
            for (int i = 0; i < _channels.Length; i++)
            {
                Channel channel = _channels[i];
                channel.Address = 0;
                channel.Start = 0;
                channel.Counter = 0x1000;
                channel.Step = 0;
                channel.Play = false;
                channel.Bank = 0;
                channel.VolumeLeft = i == 0 ? 255 : 0;
                channel.VolumeRight = i == 0 ? 0 : 255;
            }

            _sourcePhase = 0;
            _lastLeft = _lastRight = _nextLeft = _nextRight = 0;
            _primed = false;
            _starts = 0;
            _reads = 0;
            _lastLeftSum = 0;
            _lastRightSum = 0;
        }

        public byte Read(int offset)
        {
            offset &= 0x0f;
            _reads++;
            if (offset == 5)
                Start(0);
            else if (offset == 11)
                Start(1);
            return 0;
        }

        public void Write(int offset, byte value)
        {
            offset &= 0x0f;
            _registers[offset] = value;
            if (offset == 12)
            {
                SetVolume(0, (value >> 4) * 0x11, 0);
                SetVolume(1, 0, (value & 0x0f) * 0x11);
                return;
            }
            if (offset >= 12)
                return;

            int channelIndex = offset >= 6 ? 1 : 0;
            int regBase = channelIndex == 0 ? 0 : 6;
            Channel channel = _channels[channelIndex];
            switch (offset - regBase)
            {
                case 0:
                case 1:
                    channel.Step = ((_registers[regBase + 1] & 0x0f) << 8) | _registers[regBase];
                    break;
                case 2:
                case 3:
                case 4:
                    channel.Start = ((_registers[regBase + 4] & 0x01) << 16)
                                    | (_registers[regBase + 3] << 8)
                                    | _registers[regBase + 2];
                    break;
                case 5:
                    Start(channelIndex);
                    break;
            }
        }

        public void RenderStereo(short[] destination, int startFrame, int targetFrame, float gain, int outputSampleRate, bool routeToMono)
        {
            if (_rom.Length == 0 || destination.Length == 0)
                return;

            int maxFrames = destination.Length / 2;
            startFrame = Math.Clamp(startFrame, 0, maxFrames);
            targetFrame = Math.Clamp(targetFrame, startFrame, maxFrames);
            if (targetFrame <= startFrame)
                return;

            EnsurePrimed();
            double step = SourceSampleRate / (double)outputSampleRate;
            for (int frame = startFrame; frame < targetFrame; frame++)
            {
                double left = _lastLeft + ((_nextLeft - _lastLeft) * _sourcePhase);
                double right = _lastRight + ((_nextRight - _lastRight) * _sourcePhase);
                if (routeToMono)
                {
                    double mono = left + right;
                    left = mono;
                    right = mono;
                }

                int offset = frame * 2;
                destination[offset] = Mix(destination[offset], (int)Math.Round(left * gain));
                destination[offset + 1] = Mix(destination[offset + 1], (int)Math.Round(right * gain));

                _sourcePhase += step;
                while (_sourcePhase >= 1.0)
                {
                    _lastLeft = _nextLeft;
                    _lastRight = _nextRight;
                    GenerateSourceFrame(out _nextLeft, out _nextRight);
                    _sourcePhase -= 1.0;
                }
            }
        }

        private void Start(int index)
        {
            Channel channel = _channels[index];
            if (channel.Start >= _rom.Length)
                return;

            channel.Play = true;
            channel.Address = channel.Start;
            channel.Counter = 0x1000;
            _starts++;
        }

        private void SetVolume(int index, int left, int right)
        {
            Channel channel = _channels[index];
            channel.VolumeLeft = left;
            channel.VolumeRight = right;
        }

        private void EnsurePrimed()
        {
            if (_primed)
                return;

            GenerateSourceFrame(out _lastLeft, out _lastRight);
            GenerateSourceFrame(out _nextLeft, out _nextRight);
            _sourcePhase = 0;
            _primed = true;
        }

        private void GenerateSourceFrame(out short left, out short right)
        {
            int leftSum = 0;
            int rightSum = 0;
            for (int i = 0; i < _channels.Length; i++)
            {
                Channel channel = _channels[i];
                if (!channel.Play)
                    continue;

                int address = channel.Address & 0x1ffff;
                while (channel.Counter <= channel.Step)
                {
                    if (address >= _rom.Length || (ReadSample(channel, address++) & 0x80) != 0)
                    {
                        if ((_registers[13] & (1 << i)) != 0)
                        {
                            address = channel.Start;
                        }
                        else
                        {
                            channel.Play = false;
                            break;
                        }
                    }
                    channel.Counter += 0x1000 - channel.Step;
                }

                channel.Address = address;
                if (!channel.Play)
                    break;

                channel.Counter -= 32;
                int sample = (ReadSample(channel, address) & 0x7f) - 0x40;
                leftSum += sample * channel.VolumeLeft * 2;
                rightSum += sample * channel.VolumeRight * 2;
            }

            left = Clamp16(leftSum);
            right = Clamp16(rightSum);
            _lastLeftSum = leftSum;
            _lastRightSum = rightSum;
        }

        private byte ReadSample(Channel channel, int address)
        {
            int romAddress = (channel.Bank + (address & 0x1ffff)) & (_rom.Length - 1);
            return _rom[romAddress];
        }

        private static short Mix(short current, int sample)
            => (short)Math.Clamp(current + sample, short.MinValue, short.MaxValue);

        private static short Clamp16(int sample)
            => (short)Math.Clamp(sample, short.MinValue, short.MaxValue);

        private sealed class Channel
        {
            public int VolumeLeft;
            public int VolumeRight;
            public int Address;
            public int Counter;
            public int Start;
            public int Step;
            public int Bank;
            public bool Play;
        }
    }

    // Minimal stand-alone uPD7759 translation from MAME's BSD-3-Clause upd7759 device.
    private sealed class Upd7759Adpcm
    {
        private const int ChipClock = 640_000;
        private const int SourceSampleRate = ChipClock / 4;
        private const int SampleOffsetShift = 1;

        private static readonly int[,] Step =
        {
            { 0,  0,  1,  2,  3,   5,   7,  10,  0,   0,  -1,  -2,  -3,   -5,   -7,  -10 },
            { 0,  1,  2,  3,  4,   6,   8,  13,  0,  -1,  -2,  -3,  -4,   -6,   -8,  -13 },
            { 0,  1,  2,  4,  5,   7,  10,  15,  0,  -1,  -2,  -4,  -5,   -7,  -10,  -15 },
            { 0,  1,  3,  4,  6,   9,  13,  19,  0,  -1,  -3,  -4,  -6,   -9,  -13,  -19 },
            { 0,  2,  3,  5,  8,  11,  15,  23,  0,  -2,  -3,  -5,  -8,  -11,  -15,  -23 },
            { 0,  2,  4,  7, 10,  14,  19,  29,  0,  -2,  -4,  -7, -10,  -14,  -19,  -29 },
            { 0,  3,  5,  8, 12,  16,  22,  33,  0,  -3,  -5,  -8, -12,  -16,  -22,  -33 },
            { 1,  4,  7, 10, 15,  20,  29,  43, -1,  -4,  -7, -10, -15,  -20,  -29,  -43 },
            { 1,  4,  8, 13, 18,  25,  35,  53, -1,  -4,  -8, -13, -18,  -25,  -35,  -53 },
            { 1,  6, 10, 16, 22,  31,  43,  64, -1,  -6, -10, -16, -22,  -31,  -43,  -64 },
            { 2,  7, 12, 19, 27,  37,  51,  76, -2,  -7, -12, -19, -27,  -37,  -51,  -76 },
            { 2,  9, 16, 24, 34,  46,  64,  96, -2,  -9, -16, -24, -34,  -46,  -64,  -96 },
            { 3, 11, 19, 29, 41,  57,  79, 117, -3, -11, -19, -29, -41,  -57,  -79, -117 },
            { 4, 13, 24, 36, 50,  69,  96, 143, -4, -13, -24, -36, -50,  -69,  -96, -143 },
            { 4, 16, 29, 44, 62,  85, 118, 175, -4, -16, -29, -44, -62,  -85, -118, -175 },
            { 6, 20, 36, 54, 76, 104, 144, 214, -6, -20, -36, -54, -76, -104, -144, -214 },
        };

        private static readonly sbyte[] StateTable = { -1, -1, 0, 0, 1, 2, 2, 3, -1, -1, 0, 0, 1, 2, 2, 3 };

        [NonSerialized] private byte[] _rom = Array.Empty<byte>();
        private State _state;
        private int _clocksLeft;
        private int _nibblesLeft;
        private int _repeatCount;
        private byte _fifoIn;
        private byte _requestedSample;
        private byte _lastSample;
        private byte _blockHeader;
        private byte _sampleRate;
        private bool _firstValidHeader;
        private int _offset;
        private int _repeatOffset;
        private int _adpcmState;
        private byte _adpcmData;
        private int _sample;
        private bool _resetLine = true;
        private bool _startLine = true;
        private double _sourcePhase;
        private short _lastSampleOut;
        private short _nextSampleOut;
        private bool _primed;
        private int _starts;
        private int _portWrites;
        private int _lastPeak;

        public string DebugSummary
            => $"upd={_state}:{(_state == State.Idle ? 0 : 1)} updW={_portWrites} updStart={_starts} "
               + $"updReq=0x{_requestedSample:X2} updOff=0x{_offset:X5} updPk={_lastPeak}";

        public int LastSampleIndex => _rom.Length == 0 ? -1 : ReadRom(0);

        public void Load(byte[] rom)
        {
            _rom = rom;
            Reset();
        }

        public void Reset()
        {
            _state = State.Idle;
            _clocksLeft = 0;
            _nibblesLeft = 0;
            _repeatCount = 0;
            _fifoIn = 0;
            _requestedSample = 0;
            _lastSample = 0;
            _blockHeader = 0;
            _sampleRate = 0;
            _firstValidHeader = false;
            _offset = 0;
            _repeatOffset = 0;
            _adpcmState = 0;
            _adpcmData = 0;
            _sample = 0;
            _resetLine = true;
            _startLine = true;
            _sourcePhase = 0;
            _lastSampleOut = 0;
            _nextSampleOut = 0;
            _primed = false;
            _starts = 0;
            _portWrites = 0;
            _lastPeak = 0;
        }

        public void ResetLine(bool high)
        {
            bool old = _resetLine;
            if (old && !high)
            {
                Reset();
            }
            _resetLine = high;
        }

        public void StartLine(bool high)
        {
            bool old = _startLine;
            _startLine = high;
            if (_state == State.Idle && old && !high && _resetLine)
            {
                _state = State.Start;
                _clocksLeft = 0;
                _starts++;
            }
        }

        public void PortWrite(byte value)
        {
            _fifoIn = value;
            _portWrites++;
        }

        public byte BusyRead() => _state == State.Idle ? (byte)1 : (byte)0;

        public string DescribeSample(byte sample)
        {
            if (_rom.Length == 0)
                return "rom=empty";

            byte lastSample = ReadRom(0);
            if (sample > lastSample)
                return $"invalid last=0x{lastSample:X2}";

            int offset = (ReadRom(sample * 2 + 5) << (8 + SampleOffsetShift))
                         | (ReadRom(sample * 2 + 6) << SampleOffsetShift);
            offset++;
            int startOffset = offset;
            int clocks = 70 + 44 + 28 + 32 + 44 + 36 + 36;
            int blocks = 0;
            int dataBytes = 0;
            bool firstValidHeader = false;
            int repeatCount = 0;
            int repeatOffset = 0;

            for (int guard = 0; guard < 10000; guard++)
            {
                if (repeatCount != 0)
                {
                    repeatCount--;
                    offset = repeatOffset;
                }

                byte header = ReadRom(offset++);
                blocks++;
                switch (header & 0xc0)
                {
                    case 0x00:
                        clocks += 1024 * ((header & 0x3f) + 1);
                        if (header == 0 && firstValidHeader)
                            return FormatSampleDescription(startOffset, offset, clocks, blocks, dataBytes);
                        break;
                    case 0x40:
                    {
                        int rate = (header & 0x3f) + 1;
                        clocks += 36 + 256 * rate * 4;
                        offset += 128;
                        dataBytes += 128;
                        break;
                    }
                    case 0x80:
                    {
                        int rate = (header & 0x3f) + 1;
                        int nibbles = ReadRom(offset++) + 1;
                        clocks += 36 + 36 + nibbles * rate * 4;
                        int bytes = (nibbles + 1) / 2;
                        offset += bytes;
                        dataBytes += bytes;
                        break;
                    }
                    case 0xc0:
                        repeatCount = (header & 7) + 1;
                        repeatOffset = offset;
                        clocks += 36;
                        break;
                }

                if (header != 0)
                    firstValidHeader = true;
            }

            return $"offset=0x{startOffset:X5} unterminated clocks~{clocks}";
        }

        private static string FormatSampleDescription(int startOffset, int endOffset, int clocks, int blocks, int dataBytes)
        {
            double seconds = clocks / (double)ChipClock;
            double frames = seconds * TargetFps;
            return $"offset=0x{startOffset:X5} end=0x{endOffset:X5} clocks={clocks} sec={seconds:F3} frames={frames:F1} blocks={blocks} data={dataBytes}";
        }

        public void RenderStereo(short[] destination, int startFrame, int targetFrame, float gain, int outputSampleRate)
        {
            if (_rom.Length == 0 || destination.Length == 0)
                return;

            int maxFrames = destination.Length / 2;
            startFrame = Math.Clamp(startFrame, 0, maxFrames);
            targetFrame = Math.Clamp(targetFrame, startFrame, maxFrames);
            if (targetFrame <= startFrame)
                return;

            EnsurePrimed();
            double step = SourceSampleRate / (double)outputSampleRate;
            int peak = 0;
            for (int frame = startFrame; frame < targetFrame; frame++)
            {
                double interpolated = _lastSampleOut + ((_nextSampleOut - _lastSampleOut) * _sourcePhase);
                int mixed = (int)Math.Round(interpolated * gain);
                int offset = frame * 2;
                destination[offset] = Mix(destination[offset], mixed);
                destination[offset + 1] = Mix(destination[offset + 1], mixed);
                peak = Math.Max(peak, Math.Abs(mixed));

                _sourcePhase += step;
                while (_sourcePhase >= 1.0)
                {
                    _lastSampleOut = _nextSampleOut;
                    _nextSampleOut = GenerateSourceSample();
                    _sourcePhase -= 1.0;
                }
            }
            _lastPeak = peak;
        }

        private void EnsurePrimed()
        {
            if (_primed)
                return;

            _lastSampleOut = GenerateSourceSample();
            _nextSampleOut = GenerateSourceSample();
            _sourcePhase = 0;
            _primed = true;
        }

        private short GenerateSourceSample()
        {
            short output = (short)Math.Clamp(_sample * 128, short.MinValue, short.MaxValue);
            if (_state != State.Idle)
                AdvanceClocks(4);
            return output;
        }

        private void AdvanceClocks(int clocks)
        {
            while (clocks > 0 && _state != State.Idle)
            {
                if (_clocksLeft <= 0)
                    AdvanceState();

                int step = Math.Min(clocks, Math.Max(1, _clocksLeft));
                _clocksLeft -= step;
                clocks -= step;
            }
        }

        private void AdvanceState()
        {
            switch (_state)
            {
                case State.Idle:
                    _clocksLeft = 4;
                    break;
                case State.Start:
                    _requestedSample = _fifoIn;
                    _clocksLeft = 70;
                    _state = State.FirstReq;
                    break;
                case State.FirstReq:
                    _clocksLeft = 44;
                    _state = State.LastSample;
                    break;
                case State.LastSample:
                    _lastSample = ReadRom(0);
                    _clocksLeft = 28;
                    _state = _requestedSample > _lastSample ? State.Idle : State.Dummy1;
                    break;
                case State.Dummy1:
                    _clocksLeft = 32;
                    _state = State.AddressMsb;
                    break;
                case State.AddressMsb:
                    _offset = ReadRom(_requestedSample * 2 + 5) << (8 + SampleOffsetShift);
                    _clocksLeft = 44;
                    _state = State.AddressLsb;
                    break;
                case State.AddressLsb:
                    _offset |= ReadRom(_requestedSample * 2 + 6) << SampleOffsetShift;
                    _clocksLeft = 36;
                    _state = State.Dummy2;
                    break;
                case State.Dummy2:
                    _offset++;
                    _firstValidHeader = false;
                    _clocksLeft = 36;
                    _state = State.BlockHeader;
                    break;
                case State.BlockHeader:
                    if (_repeatCount != 0)
                    {
                        _repeatCount--;
                        _offset = _repeatOffset;
                    }

                    _blockHeader = ReadRom(_offset++);
                    switch (_blockHeader & 0xc0)
                    {
                        case 0x00:
                            _clocksLeft = 1024 * ((_blockHeader & 0x3f) + 1);
                            _state = _blockHeader == 0 && _firstValidHeader ? State.Idle : State.BlockHeader;
                            _sample = 0;
                            _adpcmState = 0;
                            break;
                        case 0x40:
                            _sampleRate = (byte)((_blockHeader & 0x3f) + 1);
                            _nibblesLeft = 256;
                            _clocksLeft = 36;
                            _state = State.NibbleMsn;
                            break;
                        case 0x80:
                            _sampleRate = (byte)((_blockHeader & 0x3f) + 1);
                            _clocksLeft = 36;
                            _state = State.NibbleCount;
                            break;
                        case 0xc0:
                            _repeatCount = (_blockHeader & 7) + 1;
                            _repeatOffset = _offset;
                            _clocksLeft = 36;
                            _state = State.BlockHeader;
                            break;
                    }

                    if (_blockHeader != 0)
                        _firstValidHeader = true;
                    break;
                case State.NibbleCount:
                    _nibblesLeft = ReadRom(_offset++) + 1;
                    _clocksLeft = 36;
                    _state = State.NibbleMsn;
                    break;
                case State.NibbleMsn:
                    _adpcmData = ReadRom(_offset++);
                    UpdateAdpcm(_adpcmData >> 4);
                    _clocksLeft = _sampleRate * 4;
                    _state = --_nibblesLeft == 0 ? State.BlockHeader : State.NibbleLsn;
                    break;
                case State.NibbleLsn:
                    UpdateAdpcm(_adpcmData & 0x0f);
                    _clocksLeft = _sampleRate * 4;
                    _state = --_nibblesLeft == 0 ? State.BlockHeader : State.NibbleMsn;
                    break;
            }
        }

        private void UpdateAdpcm(int data)
        {
            _sample += Step[_adpcmState, data & 0x0f];
            _adpcmState += StateTable[data & 0x0f];
            _adpcmState = Math.Clamp(_adpcmState, 0, 15);
        }

        private byte ReadRom(int address)
        {
            if (_rom.Length == 0)
                return 0xff;
            return _rom[address & (_rom.Length - 1)];
        }

        private static short Mix(short current, int sample)
            => (short)Math.Clamp(current + sample, short.MinValue, short.MaxValue);

        private enum State
        {
            Idle,
            Start,
            LastSample,
            Dummy1,
            AddressMsb,
            AddressLsb,
            Dummy2,
            BlockHeader,
            NibbleCount,
            NibbleMsn,
            NibbleLsn,
            FirstReq
        }
    }

    private sealed class TmntRomSet
    {
        public byte[] Program { get; } = new byte[0x60000];
        public byte[] AudioCpu { get; } = new byte[0x10000];
        public byte[] K007232 { get; } = new byte[0x20000];
        public byte[] Upd7759 { get; } = new byte[0x20000];
        public byte[] TitleSample { get; } = new byte[0x80000];
        public byte[] TileRom { get; } = new byte[0x100000];
        public byte[] SpriteRom { get; } = new byte[0x200000];
        public byte[] SpriteAddressProm { get; } = new byte[0x100];

        public static TmntRomSet Load(string path)
        {
            Dictionary<string, byte[]> entries = ReadArchive(path);
            var roms = new TmntRomSet();
            Load16Byte(entries, roms.Program, 0x00000, "963-x23.j17");
            Load16Byte(entries, roms.Program, 0x00001, "963-x24.k17");
            Load16Byte(entries, roms.Program, 0x40000, "963-x21.j15");
            Load16Byte(entries, roms.Program, 0x40001, "963-x22.k15");
            Find(entries, "963e20.g13").CopyTo(roms.AudioCpu, 0);
            Find(entries, "963a26.c13").CopyTo(roms.K007232, 0);
            Find(entries, "963a27.d18").CopyTo(roms.Upd7759, 0);
            Find(entries, "963a25.d5").CopyTo(roms.TitleSample, 0);

            Load32Word(entries, roms.TileRom, 0x000000, "963a28.h27");
            Load32Word(entries, roms.TileRom, 0x000002, "963a29.k27");

            Load32Word(entries, roms.SpriteRom, 0x000000, "963a17.h4");
            Load32Word(entries, roms.SpriteRom, 0x000002, "963a15.k4");
            Load32Word(entries, roms.SpriteRom, 0x100000, "963a18.h6");
            Load32Word(entries, roms.SpriteRom, 0x100002, "963a16.k6");
            Find(entries, "963a30.g7").CopyTo(roms.SpriteAddressProm, 0);

            ChunkyToPlanar(roms.TileRom);
            ChunkyToPlanar(roms.SpriteRom);
            UnscrambleSpriteRom(roms.SpriteRom, roms.SpriteAddressProm);
            return roms;
        }

        private static Dictionary<string, byte[]> ReadArchive(string path)
        {
            using IArchive archive = RomArchiveExtractor.OpenArchive(path);
            var entries = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (IArchiveEntry entry in archive.Entries)
            {
                if (entry.IsDirectory || string.IsNullOrWhiteSpace(entry.Key))
                    continue;

                using Stream stream = entry.OpenEntryStream();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                entries[Path.GetFileName(entry.Key)] = memory.ToArray();
            }
            return entries;
        }

        private static void Load16Byte(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
        {
            byte[] source = Find(entries, name);
            for (int i = 0; i < source.Length; i++)
                destination[offset + i * 2] = source[i];
        }

        private static void Load32Word(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
        {
            byte[] source = Find(entries, name);
            for (int i = 0; i < source.Length; i += 2)
            {
                int dst = offset + (i / 2) * 4;
                destination[dst] = source[i];
                destination[dst + 1] = source[i + 1];
            }
        }

        private static void ChunkyToPlanar(byte[] rom)
        {
            int[] bitMap =
            {
                31, 27, 23, 19, 15, 11, 7, 3,
                30, 26, 22, 18, 14, 10, 6, 2,
                29, 25, 21, 17, 13, 9, 5, 1,
                28, 24, 20, 16, 12, 8, 4, 0
            };

            for (int offset = 0; offset < rom.Length; offset += 4)
            {
                uint data = (uint)(rom[offset] | (rom[offset + 1] << 8) | (rom[offset + 2] << 16) | (rom[offset + 3] << 24));
                uint planar = 0;
                for (int i = 0; i < bitMap.Length; i++)
                    planar |= ((data >> bitMap[i]) & 1u) << (31 - i);

                rom[offset] = (byte)planar;
                rom[offset + 1] = (byte)(planar >> 8);
                rom[offset + 2] = (byte)(planar >> 16);
                rom[offset + 3] = (byte)(planar >> 24);
            }
        }

        private static void UnscrambleSpriteRom(byte[] rom, byte[] codeConversionProm)
        {
            uint[] words = new uint[rom.Length / 4];
            for (int i = 0, offset = 0; i < words.Length; i++, offset += 4)
                words[i] = (uint)(rom[offset] | (rom[offset + 1] << 8) | (rom[offset + 2] << 16) | (rom[offset + 3] << 24));

            uint[] scrambled = new uint[words.Length];
            int[,] bitPickTable =
            {
                { 3, 3, 3, 3, 3, 3, 3, 3 },
                { 0, 0, 5, 5, 5, 5, 5, 5 },
                { 1, 1, 0, 0, 0, 7, 7, 7 },
                { 2, 2, 1, 1, 1, 0, 0, 9 },
                { 4, 4, 2, 2, 2, 1, 1, 0 },
                { 5, 6, 4, 4, 4, 2, 2, 1 },
                { 6, 5, 6, 6, 6, 4, 4, 2 },
                { 7, 7, 7, 7, 8, 6, 6, 4 },
                { 8, 8, 8, 8, 7, 8, 8, 6 },
                { 9, 9, 9, 9, 9, 9, 9, 8 }
            };

            for (int address = 0; address < words.Length; address++)
            {
                int entry = codeConversionProm[(address & 0x7f800) >> 11] & 7;
                int source = address & 0x7fc00;
                for (int bit = 0; bit < 10; bit++)
                    source |= ((address >> bitPickTable[bit, entry]) & 1) << bit;

                scrambled[address] = words[source];
            }

            for (int i = 0, offset = 0; i < scrambled.Length; i++, offset += 4)
            {
                uint data = scrambled[i];
                rom[offset] = (byte)data;
                rom[offset + 1] = (byte)(data >> 8);
                rom[offset + 2] = (byte)(data >> 16);
                rom[offset + 3] = (byte)(data >> 24);
            }
        }

        private static byte[] Find(Dictionary<string, byte[]> entries, string name)
            => entries.TryGetValue(name, out byte[]? data)
                ? data
                : throw new FileNotFoundException($"Required TMNT ROM '{name}' was not found in archive.");
    }

    private static void TmntTileCallback(int layer, int bank, ref int code, ref byte color)
    {
        int[] layerColorBase = { 0, 32, 40 };
        code |= ((color & 0x03) << 8) | ((color & 0x10) << 6) | ((color & 0x0c) << 9) | (bank << 13);
        color = (byte)(layerColorBase[layer] + ((color & 0xe0) >> 5));
    }

    private static void WritePixel(byte[] frameBuffer, int x, int y, ushort xBgr555)
    {
        int r = (xBgr555 & 0x1f) * 255 / 31;
        int g = ((xBgr555 >> 5) & 0x1f) * 255 / 31;
        int b = ((xBgr555 >> 10) & 0x1f) * 255 / 31;
        int offset = y * FrameStride + x * 4;
        frameBuffer[offset] = (byte)b;
        frameBuffer[offset + 1] = (byte)g;
        frameBuffer[offset + 2] = (byte)r;
        frameBuffer[offset + 3] = 0xff;
    }

    private static void WriteInputState(BinaryWriter writer, ArcadeInputState input)
    {
        writer.Write(input.Up);
        writer.Write(input.Down);
        writer.Write(input.Left);
        writer.Write(input.Right);
        writer.Write(input.Button1);
        writer.Write(input.Button2);
        writer.Write(input.Button3);
        writer.Write(input.Start);
        writer.Write(input.Coin);
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
            reader.ReadBoolean());

    private static void WriteByteArray(BinaryWriter writer, byte[] data)
    {
        writer.Write(data.Length);
        writer.Write(data);
    }

    private static void ReadByteArray(BinaryReader reader, byte[] destination)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > 16 * 1024 * 1024)
            throw new InvalidDataException($"Invalid byte array length in TMNT savestate: {length}.");

        byte[] data = reader.ReadBytes(length);
        if (data.Length != length)
            throw new EndOfStreamException("TMNT savestate ended while reading byte array.");

        Array.Clear(destination);
        Array.Copy(data, destination, Math.Min(data.Length, destination.Length));
    }

    private static ushort ReadBigEndianWord(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static void WriteBigEndianWord(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
}
