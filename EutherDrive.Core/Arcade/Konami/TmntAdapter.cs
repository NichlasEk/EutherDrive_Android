using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
    private const string SavestateExtendedMagic = "KONAMITMNTE";
    private const int SavestateVersion = 1;
    private const int SavestateExtendedVersion = 7;
    private const int FrameWidth = 320;
    private const int FrameHeight = 224;
    private const int FrameStride = FrameWidth * 4;
    private const int MysticVisibleOriginX = 24;
    private const int MysticVisibleOriginY = 16;
    private const int MysticVisibleWidth = 288;
    private const int Tmnt2RawFrameHeight = 240;
    private const int Tmnt2VisibleStartY = 16;
    private const double TargetFps = 24_000_000.0 / 4.0 / 384.0 / 264.0;
    private const int MainCpuCyclesPerFrame = 135_168;
    private const int MysticCpuCyclesPerFrame = 16_000_000 / 60;
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
    private static readonly byte[] Color5To8 = BuildColor5To8();

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
    private TmntHardwareVariant _loadedVariant;

    public string DebugSummary => _bus.DebugSummary(_mainCpu.Pc) + " " + _sound.DebugSummary;

    public double GetTargetFps() => TargetFps;

    public RomIdentity? RomIdentity => _romIdentity;

    public long? FrameCounter => _loaded ? _frameCounter : null;

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string name = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
        return name is "tmnt" or "tmntu" or "tmntj" or "tmhta" or "tmnt2p" or "tmht2p" or "tmnt2" or "ssriders" or "mystwarr" or "metamrph" or "moomesa";
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
        _loadedVariant = roms.Variant;
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
        _bus.BeginVisible();

        int mainCyclesPerFrame = MainCyclesPerFrame(_loadedVariant);
        int visibleCycles = mainCyclesPerFrame * ScreenVisibleLines / ScreenTotalLines;
        int vblankCycles = mainCyclesPerFrame - visibleCycles;
        int cycles = 0;
        while (cycles < visibleCycles)
        {
            int elapsed = checked((int)_mainCpu.ExecuteInstruction(_bus));
            cycles += elapsed;
            _sound.RunMainCpuCycles(elapsed, mainCyclesPerFrame);
        }

        _bus.Render(_renderFrameBuffer);
        lock (_frameSync)
        {
            Buffer.BlockCopy(_renderFrameBuffer, 0, _presentFrameBuffer, 0, _renderFrameBuffer.Length);
        }

        _bus.BeginVblank();
        cycles = 0;
        while (cycles < vblankCycles)
        {
            int elapsed = checked((int)_mainCpu.ExecuteInstruction(_bus));
            cycles += elapsed;
            _sound.RunMainCpuCycles(elapsed, mainCyclesPerFrame);
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
        writer.Write(SavestateExtendedMagic);
        writer.Write(SavestateExtendedVersion);
        _sound.SaveExtendedState(writer);
        _bus.SaveExtendedState(writer);
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
        _sound.RestoreRuntimeState(_loadedVariant);
        _bus.RestoreRuntimeState(_sound, _loadedVariant);
        TryReadExtendedState(reader);
        if (_audioBuffer.Length == 0)
            _audioBuffer = new short[Math.Max(1, (int)Math.Round(OutputSampleRate / TargetFps)) * OutputChannels];
    }

    private void TryReadExtendedState(BinaryReader reader)
    {
        if (!reader.BaseStream.CanSeek || reader.BaseStream.Position >= reader.BaseStream.Length)
            return;

        long position = reader.BaseStream.Position;
        try
        {
            string magic = reader.ReadString();
            if (!string.Equals(magic, SavestateExtendedMagic, StringComparison.Ordinal))
            {
                reader.BaseStream.Position = position;
                return;
            }

            int version = reader.ReadInt32();
            if (version is >= 1 and <= SavestateExtendedVersion)
            {
                _sound.LoadExtendedState(reader, version);
                if (version >= 4)
                    _bus.LoadExtendedState(reader, version);
            }
        }
        catch (EndOfStreamException)
        {
            reader.BaseStream.Position = position;
        }
    }

    public ReadOnlySpan<byte> GetFrameBuffer(out int width, out int height, out int stride)
    {
        lock (_frameSync)
        {
            Buffer.BlockCopy(_presentFrameBuffer, 0, _snapshotFrameBuffer, 0, _presentFrameBuffer.Length);
            width = IsMystwarrFamily(_loadedVariant) ? MysticVisibleWidth : FrameWidth;
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

    private readonly record struct MystwarrRenderItem(uint Order, int Layer, int SpriteOffset, int DrawMode, int ShadowMode);

    private enum TmntHardwareVariant
    {
        Tmnt,
        Tmnt2,
        Ssriders,
        Mystwarr,
        Metamrph,
        Moomesa
    }

    private static bool IsMystwarrFamily(TmntHardwareVariant variant)
        => variant is TmntHardwareVariant.Mystwarr or TmntHardwareVariant.Metamrph;

    private static int MainCyclesPerFrame(TmntHardwareVariant variant)
        => variant is TmntHardwareVariant.Mystwarr or TmntHardwareVariant.Metamrph or TmntHardwareVariant.Moomesa
            ? MysticCpuCyclesPerFrame
            : MainCpuCyclesPerFrame;

    private sealed class TmntBus : EutherDrive.Core.Cpu.M68000Emu.IBusInterface, EutherDrive.Core.Cpu.M68000Emu.IOpcodeBusInterface
    {
        [NonSerialized] private readonly byte[] _program = new byte[0x200000];
        private readonly byte[] _ram = new byte[0x10000];
        private readonly byte[] _metamrphExtraRam = new byte[0xf000];
        private readonly byte[] _moomesaSpriteRam = new byte[0x10000];
        private readonly byte[] _paletteRam = new byte[0x4000];
        private readonly ushort[] _palette = new ushort[0x800];
        [NonSerialized] private readonly byte[] _tileRom = new byte[0x500000];
        [NonSerialized] private readonly byte[] _spriteRom = new byte[0x800000];
        private readonly K052109 _k052109 = new();
        private readonly K056832 _k056832 = new();
        private readonly K051960 _k051960 = new();
        private readonly K053245 _k053245 = new();
        private readonly K055555 _k055555 = new();
        private readonly K053250 _k053250 = new();
        [NonSerialized] private readonly K054338 _k054338 = new();
        [NonSerialized] private readonly K053252 _k053252 = new();
        private readonly Tmnt2SerialEeprom _tmnt2Eeprom = new();
        private readonly byte[] _tmnt2UnknownRam = new byte[0x80];
        private readonly ushort[] _tmnt2ProtRam = new ushort[0x10];
        private readonly ushort[] _moomesaProtRam = new ushort[0x10];
        private readonly byte[] _k053251 = new byte[0x10];
        private readonly byte[] _k053251PaletteIndex = new byte[5];
        [NonSerialized] private TmntSound? _sound;
        [NonSerialized] private byte[]? _tmnt2RawFrameBuffer;
        [NonSerialized] private byte[]? _tmnt2PriorityBuffer;

        private ArcadeInputState _input;
        private TmntHardwareVariant _variant;
        private byte _interruptLevel;
        private bool _irq5Enabled;
        private bool _moomesaSpriteIrqEnabled;
        private bool _moomesaVblankIrqEnabled;
        private ushort _moomesaControl2;
        private bool _tmnt2InVblank;
        private byte _soundLatch = 0xff;
        private byte _lastSoundIrqBit;
        private int _priority;
        private byte _mystwarrIrqControl;

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
        [NonSerialized] private int _tmnt2ProtectionRuns;
        [NonSerialized] private string _lastTmnt2Protection = "";
        [NonSerialized] private int _ssridersProtectionReads;
        [NonSerialized] private int _ssridersUnknownProtectionReads;
        [NonSerialized] private string _lastSsridersProtectionRead = "";

        private bool UsesK053245Hardware => _variant is TmntHardwareVariant.Tmnt2 or TmntHardwareVariant.Ssriders;
        private bool UsesMystwarrHardware => IsMystwarrFamily(_variant);
        private bool UsesMetamrphHardware => _variant == TmntHardwareVariant.Metamrph;
        private bool UsesMoomesaHardware => _variant == TmntHardwareVariant.Moomesa;

        private int ProgramRomLength => UsesMoomesaHardware ? 0x180000 : UsesMystwarrHardware ? 0x200000 : UsesK053245Hardware ? 0x100000 : 0x60000;

        public void AttachSound(TmntSound sound) => _sound = sound;

        public void RestoreRuntimeState(TmntSound sound, TmntHardwareVariant loadedVariant)
        {
            _sound = sound;
            _variant = loadedVariant;
            _k053245.Tmnt2CoordinateMode = _variant == TmntHardwareVariant.Tmnt2;
            _k053245.MystwarrSpriteLayout = _variant == TmntHardwareVariant.Mystwarr || UsesMoomesaHardware;
            _k053245.MetamrphSpriteLayout = UsesMetamrphHardware;
            _k056832.MoomesaTileCallback = UsesMoomesaHardware;
            _k056832.Bpp4TileDecode = UsesMoomesaHardware;
        }

        public void Load(TmntRomSet roms)
        {
            _variant = roms.Variant;
            Array.Fill(_program, (byte)0xff);
            Array.Clear(_ram);
            Array.Clear(_metamrphExtraRam);
            Array.Clear(_moomesaSpriteRam);
            Array.Clear(_paletteRam);
            Array.Clear(_palette);
            Array.Clear(_tmnt2UnknownRam);
            Array.Clear(_tmnt2ProtRam);
            Array.Clear(_moomesaProtRam);
            Array.Clear(_k053251);
            ResetK053251Indexes();
            Array.Copy(roms.Program, _program, Math.Min(roms.Program.Length, _program.Length));
            Array.Copy(roms.TileRom, _tileRom, Math.Min(roms.TileRom.Length, _tileRom.Length));
            Array.Copy(roms.SpriteRom, _spriteRom, Math.Min(roms.SpriteRom.Length, _spriteRom.Length));
            _k052109.Load(_tileRom);
            _k056832.Load(_tileRom);
            _k051960.Load(_spriteRom);
            _k053245.Load(_spriteRom);
            _k053245.Tmnt2CoordinateMode = _variant == TmntHardwareVariant.Tmnt2;
            _k053245.MystwarrSpriteLayout = _variant == TmntHardwareVariant.Mystwarr || UsesMoomesaHardware;
            _k053245.MetamrphSpriteLayout = UsesMetamrphHardware;
            _k056832.MoomesaTileCallback = UsesMoomesaHardware;
            _k056832.Bpp4TileDecode = UsesMoomesaHardware;
            _k053250.Load(roms.K053250);
            _tmnt2Eeprom.ResetContents();
            if (UsesK053245Hardware || UsesMystwarrHardware || UsesMoomesaHardware)
                _tmnt2Eeprom.Import(roms.Eeprom);
            ResetMachine();
        }

        public void ResetMachine()
        {
            Array.Clear(_ram);
            Array.Clear(_metamrphExtraRam);
            Array.Clear(_moomesaSpriteRam);
            Array.Clear(_paletteRam);
            Array.Clear(_palette);
            Array.Clear(_tmnt2UnknownRam);
            Array.Clear(_tmnt2ProtRam);
            Array.Clear(_moomesaProtRam);
            Array.Clear(_k053251);
            ResetK053251Indexes();
            _k052109.Reset();
            _k056832.Reset();
            _k051960.Reset();
            _k053245.Reset();
            _k055555.Reset();
            _k053250.Reset();
            _k054338.Reset();
            _k053252.Reset();
            _k053245.Tmnt2CoordinateMode = _variant == TmntHardwareVariant.Tmnt2;
            _k053245.MystwarrSpriteLayout = _variant == TmntHardwareVariant.Mystwarr || UsesMoomesaHardware;
            _k053245.MetamrphSpriteLayout = UsesMetamrphHardware;
            _k056832.MoomesaTileCallback = UsesMoomesaHardware;
            _k056832.Bpp4TileDecode = UsesMoomesaHardware;
            _interruptLevel = 0;
            _irq5Enabled = false;
            _moomesaSpriteIrqEnabled = false;
            _moomesaVblankIrqEnabled = false;
            _moomesaControl2 = 0;
            _tmnt2InVblank = false;
            _soundLatch = 0xff;
            _lastSoundIrqBit = 0;
            _priority = 0;
            _mystwarrIrqControl = 0;
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
            _tmnt2ProtectionRuns = 0;
            _lastTmnt2Protection = "";
            _ssridersProtectionReads = 0;
            _ssridersUnknownProtectionReads = 0;
            _lastSsridersProtectionRead = "";
        }

        public void SetInput(ArcadeInputState input) => _input = input;

        public void BeginVisible()
        {
            if (UsesMoomesaHardware)
            {
                _k053252.SetVpos(0);
                return;
            }
            if (UsesK053245Hardware)
                _tmnt2InVblank = false;
            if (UsesMystwarrHardware)
            {
                _k053252.SetVpos(0);
                if (UsesMetamrphHardware)
                    _interruptLevel = 6;
                else if ((_mystwarrIrqControl & 0x01) != 0)
                    _interruptLevel = 4;
            }
        }

        public void BeginVblank()
        {
            if (UsesMoomesaHardware)
            {
                _k053252.SetVpos(ScreenVisibleLines);
                if (_k053245.ObjectIrqEnabled)
                    MoomesaObjectDma();
                if (_moomesaSpriteIrqEnabled && _k053245.ObjectIrqEnabled)
                    _interruptLevel = 4;
                if (_moomesaVblankIrqEnabled)
                    _interruptLevel = 5;
                return;
            }
            if (UsesK053245Hardware)
            {
                _tmnt2InVblank = true;
                _k053245.BufferSprites();
                if (_k052109.IrqEnabled)
                    _interruptLevel = 4;
                return;
            }
            if (UsesMystwarrHardware)
            {
                _k053252.SetVpos(ScreenVisibleLines);
                if (UsesMetamrphHardware)
                {
                    _k053245.BufferSprites();
                    if (_k053245.ObjectIrqEnabled)
                        _interruptLevel = 5;
                }
                else if ((_mystwarrIrqControl & 0x01) != 0)
                    _interruptLevel = 2;
                return;
            }

            _k051960.BufferSprites();
            if (_irq5Enabled)
                _interruptLevel = 5;
        }

        public void Render(byte[] frameBuffer)
        {
            if (UsesMoomesaHardware)
            {
                RenderMoomesa(frameBuffer);
                return;
            }
            if (UsesK053245Hardware)
            {
                RenderTmnt2(frameBuffer);
                return;
            }
            if (UsesMystwarrHardware)
            {
                RenderMystwarr(frameBuffer);
                return;
            }

            string renderMask = Environment.GetEnvironmentVariable("EUTHERDRIVE_TMNT_RENDER_MASK") ?? "all";
            bool drawLayer0 = renderMask == "all" || renderMask.Contains('0', StringComparison.Ordinal);
            bool drawLayer1 = renderMask == "all" || renderMask.Contains('1', StringComparison.Ordinal);
            bool drawLayer2 = renderMask == "all" || renderMask.Contains('2', StringComparison.Ordinal);
            bool drawSprites = renderMask == "all" || renderMask.Contains('s', StringComparison.OrdinalIgnoreCase);

            _k054338.FillSolidBackground(frameBuffer);
            if (drawLayer2 && _k052109.LayerHasContent(2))
                _k052109.RenderLayer(frameBuffer, _palette, 2, opaque: true, paletteMask: 0x3ff);
            if (drawSprites && (_priority & 1) != 0)
                _k051960.Render(frameBuffer, _palette);
            if (drawLayer1 && !_k052109.LayerIsUniform(1))
                _k052109.RenderLayer(frameBuffer, _palette, 1, opaque: false, paletteMask: 0x3ff);
            if (drawSprites && (_priority & 1) == 0)
                _k051960.Render(frameBuffer, _palette);
            if (drawLayer0 && !_k052109.LayerIsUniform(0))
                _k052109.RenderLayer(frameBuffer, _palette, 0, opaque: false, paletteMask: 0x3ff);
        }

        public byte ReadByte(uint address)
        {
            address &= 0x00ff_ffff;
            if (UsesMoomesaHardware)
                return ReadByteMoomesa(address);
            if (UsesK053245Hardware)
                return ReadByteTmnt2(address);
            if (UsesMystwarrHardware)
                return ReadByteMystwarr(address);

            if (address < ProgramRomLength)
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
            if (UsesMoomesaHardware)
                return ReadWordMoomesa(address);
            if (UsesK053245Hardware)
                return ReadWordTmnt2(address);
            if (UsesMystwarrHardware)
                return ReadWordMystwarr(address);

            if (address < ProgramRomLength - 1)
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
            if (UsesMoomesaHardware)
            {
                WriteByteMoomesa(address, value);
                return;
            }
            if (UsesK053245Hardware)
            {
                WriteByteTmnt2(address, value);
                return;
            }
            if (UsesMystwarrHardware)
            {
                WriteByteMystwarr(address, value);
                return;
            }

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
            if (UsesMoomesaHardware)
            {
                WriteWordMoomesa(address, value);
                return;
            }
            if (UsesK053245Hardware)
            {
                WriteWordTmnt2(address, value);
                return;
            }
            if (UsesMystwarrHardware)
            {
                WriteWordMystwarr(address, value);
                return;
            }

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
            => $"var={_variant} pc=0x{pc:X6} irq={_interruptLevel} irq5={_irq5Enabled} pri={_priority} sound=0x{_soundLatch:X2} "
               + $"palW={_paletteWrites} k052W={_k052109Writes} k052R={_k052109Reads} sprW={_spriteWrites} "
               + $"k052Seg={_k052ColorWrites}/{_k052CodeLowWrites}/{_k052CodeHighWrites}/{_k052RegisterWrites} "
               + $"k052Byte={_k052EvenByteWrites}/{_k052OddByteWrites} "
               + $"prot={_tmnt2ProtectionRuns}:{_lastTmnt2Protection} "
               + $"ssprot={_ssridersProtectionReads}/{_ssridersUnknownProtectionReads}:{_lastSsridersProtectionRead} "
               + PaletteDebugSummary()
               + _k052109.DebugSummary()
               + $" k055555={_k055555.DebugSummary()} k056832={_k056832.DebugSummary()} k053245={_k053245.DebugSummary()} k054338={_k054338.DebugSummary()} k053252={_k053252.DebugSummary()} {_k053250.DebugSummary()} k053260={_sound?.K053260DebugSummary ?? "detached"} eep={_tmnt2Eeprom.DebugSummary()}";

        public void SaveExtendedState(BinaryWriter writer)
        {
            StateBinarySerializer.WriteInto(writer, _k054338);
            StateBinarySerializer.WriteInto(writer, _k053252);
            StateBinarySerializer.WriteInto(writer, _k053250);
        }

        public void LoadExtendedState(BinaryReader reader, int version)
        {
            StateBinarySerializer.ReadInto(reader, _k054338);
            StateBinarySerializer.ReadInto(reader, _k053252);
            if (version >= 7)
                StateBinarySerializer.ReadInto(reader, _k053250);
        }

        private string PaletteDebugSummary()
        {
            int nonZero = 0;
            int first = -1;
            int last = -1;
            for (int i = 0; i < _palette.Length; i++)
            {
                if ((_palette[i] & 0x7fff) == 0)
                    continue;
                nonZero++;
                if (first < 0)
                    first = i;
                last = i;
            }
            return $"palnz={nonZero}:{first:X3}-{last:X3} ";
        }

        private void RenderTmnt2(byte[] frameBuffer)
        {
            byte[] rawFrameBuffer = EnsureTmnt2RawFrameBuffer();
            byte[] priorityBuffer = EnsureTmnt2PriorityBuffer();
            string renderMask = Environment.GetEnvironmentVariable("EUTHERDRIVE_TMNT_RENDER_MASK") ?? "all";
            bool drawLayer0 = renderMask == "all" || renderMask.Contains('0', StringComparison.Ordinal);
            bool drawLayer1 = renderMask == "all" || renderMask.Contains('1', StringComparison.Ordinal);
            bool drawLayer2 = renderMask == "all" || renderMask.Contains('2', StringComparison.Ordinal);
            bool drawSprites = renderMask == "all" || renderMask.Contains('s', StringComparison.OrdinalIgnoreCase);

            UpdateTmnt2LayerColorBases();
            FillFrame(rawFrameBuffer, _palette[(16 * _k053251PaletteIndex[0]) & 0x7ff]);
            Array.Clear(priorityBuffer);

            Span<int> layer = stackalloc int[] { 0, 1, 2 };
            Span<int> priority = stackalloc int[]
            {
                K053251Priority(2),
                K053251Priority(4),
                K053251Priority(3)
            };
            SortKonamiLayers3(layer, priority);

            for (int i = 0; i < 3; i++)
            {
                int currentLayer = layer[i];
                bool drawLayer = currentLayer switch
                {
                    0 => drawLayer0,
                    1 => drawLayer1,
                    _ => drawLayer2
                };
                if (drawLayer)
                    _k052109.RenderLayer(rawFrameBuffer, _palette, currentLayer, opaque: false, paletteMask: 0x7ff,
                        outputHeight: Tmnt2RawFrameHeight, priorityBuffer: priorityBuffer, priorityCode: 1 << i);
            }

            if (drawSprites)
                _k053245.RenderPriorityMasked(rawFrameBuffer, _palette, priority, priorityBuffer, Tmnt2RawFrameHeight);

            CopyTmnt2VisibleArea(rawFrameBuffer, frameBuffer);
        }

        private void RenderMystwarr(byte[] frameBuffer)
        {
            string renderMask = Environment.GetEnvironmentVariable("EUTHERDRIVE_MYSTWARR_RENDER_MASK") ?? "all";
            bool drawLayer0 = renderMask == "all" || renderMask.Contains('0', StringComparison.Ordinal);
            bool drawLayer1 = renderMask == "all" || renderMask.Contains('1', StringComparison.Ordinal);
            bool drawLayer2 = renderMask == "all" || renderMask.Contains('2', StringComparison.Ordinal);
            bool drawLayer3 = renderMask == "all" || renderMask.Contains('3', StringComparison.Ordinal);
            bool drawSprites = renderMask == "all" || renderMask.Contains('s', StringComparison.OrdinalIgnoreCase);
            int inputEnables = _k055555.ReadRegister(K055555.InputEnables);

            _k056832.LayerColorBase[0] = _k055555.GetPaletteIndex(0) << 4;
            _k056832.LayerColorBase[1] = _k055555.GetPaletteIndex(1) << 4;
            _k056832.LayerColorBase[2] = _k055555.GetPaletteIndex(2) << 4;
            _k056832.LayerColorBase[3] = _k055555.GetPaletteIndex(3) << 4;
            _k056832.MetamrphTileCallback = UsesMetamrphHardware;
            _k053245.SpriteColorBase = _k055555.GetPaletteIndex(4) << (UsesMetamrphHardware ? 4 : 5);

            _k054338.FillSolidBackground(frameBuffer);
            if (inputEnables == 0)
                return;
            _k053245.BufferSprites();

            Span<MystwarrRenderItem> items = stackalloc MystwarrRenderItem[520];
            int count = 0;
            if (drawLayer0 && (inputEnables & K055555.InputVramA) != 0) items[count++] = LayerRenderItem(_k055555.ReadRegister(K055555.PriInp0), 0);
            if (drawLayer1 && (inputEnables & K055555.InputVramB) != 0) items[count++] = LayerRenderItem(_k055555.ReadRegister(K055555.PriInp3), 1);
            if (drawLayer2 && (inputEnables & K055555.InputVramC) != 0) items[count++] = LayerRenderItem(_k055555.ReadRegister(K055555.PriInp6), 2);
            if (drawLayer3 && (inputEnables & K055555.InputVramD) != 0) items[count++] = LayerRenderItem(_k055555.ReadRegister(K055555.PriInp7), 3);
            if (UsesMetamrphHardware && (inputEnables & K055555.InputSub1) != 0) items[count++] = LayerRenderItem(_k055555.ReadRegister(K055555.PriInp9), -2);
            if (drawSprites && (inputEnables & K055555.InputObj) != 0)
                count = _k053245.AppendMystwarrSpriteRenderItems(items, count, _k055555, _k054338);

            ReverseMystwarrRenderItems(items[..count]);
            SortMystwarrRenderItems(items[..count]);
            Span<int> mixAlphas = stackalloc int[4];
            int frameTileMixCode = _k056832.LastAlphaTileMixCode;
            _k053245.BeginMystwarrObjectFrame();
            for (int i = 0; i < count; i++)
            {
                MystwarrRenderItem item = items[i];
                if (item.Layer >= 0)
                {
                    bool splitMixCategory = BuildMystwarrLayerMixAlphas(item.Layer, frameTileMixCode, mixAlphas);
                    int brightness = MystwarrLayerBrightness(item.Layer);
                    if (splitMixCategory)
                    {
                        _k056832.RenderLayer(frameBuffer, _palette, item.Layer, opaque: false, mixAlphas, K056832.TileCategoryMixed, brightness);
                        _k056832.RenderLayer(frameBuffer, _palette, item.Layer, opaque: false, mixAlphas, K056832.TileCategoryNormal, brightness);
                    }
                    else
                    {
                        _k056832.RenderLayer(frameBuffer, _palette, item.Layer, opaque: false, mixAlphas, K056832.TileCategoryAll, brightness);
                    }
                }
                else if (item.Layer == -2)
                {
                    _k053250.Render(frameBuffer, _palette, _k055555.GetPaletteIndex(5) << 4, offX: -7, offY: 0);
                }
                else
                    _k053245.RenderMystwarrObject(frameBuffer, _palette, item.SpriteOffset, item.DrawMode, item.ShadowMode,
                        (int)((item.Order >> 16) & 0xff), (int)((item.Order >> 24) & 0xff), _k054338);
            }
            _k053245.FinishMystwarrRenderFrameStats();
        }

        private void RenderMoomesa(byte[] frameBuffer)
        {
            string renderMask = Environment.GetEnvironmentVariable("EUTHERDRIVE_MOOMESA_RENDER_MASK") ?? "all";
            bool drawLayer0 = renderMask == "all" || renderMask.Contains('0', StringComparison.Ordinal);
            bool drawLayer1 = renderMask == "all" || renderMask.Contains('1', StringComparison.Ordinal);
            bool drawLayer2 = renderMask == "all" || renderMask.Contains('2', StringComparison.Ordinal);
            bool drawLayer3 = renderMask == "all" || renderMask.Contains('3', StringComparison.Ordinal);
            bool drawSprites = renderMask == "all" || renderMask.Contains('s', StringComparison.OrdinalIgnoreCase);

            _k056832.LayerColorBase[0] = 0x70;
            _k056832.LayerColorBase[1] = _k053251PaletteIndex[2] << 4;
            _k056832.LayerColorBase[2] = _k053251PaletteIndex[3] << 4;
            _k056832.LayerColorBase[3] = _k053251PaletteIndex[4] << 4;
            _k056832.MoomesaTileCallback = true;
            _k056832.MetamrphTileCallback = false;
            _k053245.SpriteColorBase = _k053251PaletteIndex[0] << 4;

            _k054338.FillSolidBackground(frameBuffer);

            Span<int> layer = stackalloc int[] { 1, 2, 3 };
            Span<int> priority = stackalloc int[]
            {
                K053251Priority(2),
                K053251Priority(3),
                K053251Priority(4)
            };
            SortKonamiLayers3(layer, priority);

            _k053245.BeginMystwarrObjectFrame();
            int textPriority = K053251Priority(1);
            if (priority[0] < textPriority && ShouldDrawMoomesaLayer(layer[0], drawLayer1, drawLayer2, drawLayer3))
                _k056832.RenderLayer(frameBuffer, _palette, layer[0], opaque: false);
            if (ShouldDrawMoomesaLayer(layer[1], drawLayer1, drawLayer2, drawLayer3))
                _k056832.RenderLayer(frameBuffer, _palette, layer[1], opaque: false);
            if (ShouldDrawMoomesaLayer(layer[2], drawLayer1, drawLayer2, drawLayer3))
                _k056832.RenderLayer(frameBuffer, _palette, layer[2], opaque: false);
            if (drawSprites)
                _k053245.RenderMystwarrPriority(frameBuffer, _palette, 0, _k054338);
            if (drawLayer0)
                _k056832.RenderLayer(frameBuffer, _palette, 0, opaque: false);
            _k053245.FinishMystwarrRenderFrameStats();
        }

        private static bool ShouldDrawMoomesaLayer(int layer, bool drawLayer1, bool drawLayer2, bool drawLayer3)
            => layer switch
            {
                1 => drawLayer1,
                2 => drawLayer2,
                _ => drawLayer3
            };

        private bool BuildMystwarrLayerMixAlphas(int layer, int frameTileMixCode, Span<int> mixAlphas)
        {
            int shift = (layer & 3) << 1;
            int internalMix = ((_k055555.ReadRegister(K055555.BlendEnables) >> shift) & 3)
                & ((_k055555.ReadRegister(K055555.VInMixOn) >> shift) & 3);
            int internalAlpha = _k054338.TilemapAlphaLevel(internalMix);
            mixAlphas[0] = internalAlpha;

            int internalMask = (_k055555.ReadRegister(K055555.VInMixOn) >> shift) & 3;
            int externalMix = frameTileMixCode & ~internalMask;
            int externalAlpha = _k054338.ExternalTilemapAlphaLevel(externalMix);
            int mixedAlpha = externalAlpha < 255 ? externalAlpha : internalAlpha;
            for (int mixCode = 1; mixCode < mixAlphas.Length; mixCode++)
                mixAlphas[mixCode] = mixedAlpha;

            return externalAlpha < 255;
        }

        private int MystwarrLayerBrightness(int layer)
        {
            int mode = (_k055555.ReadRegister(K055555.VramBrightness) >> ((layer & 3) << 1)) & 3;
            return _k054338.BrightnessLevel(mode);
        }

        private byte[] EnsureTmnt2RawFrameBuffer()
        {
            int length = Tmnt2RawFrameHeight * FrameStride;
            if (_tmnt2RawFrameBuffer == null || _tmnt2RawFrameBuffer.Length != length)
                _tmnt2RawFrameBuffer = new byte[length];
            return _tmnt2RawFrameBuffer;
        }

        private byte[] EnsureTmnt2PriorityBuffer()
        {
            int length = Tmnt2RawFrameHeight * FrameWidth;
            if (_tmnt2PriorityBuffer == null || _tmnt2PriorityBuffer.Length != length)
                _tmnt2PriorityBuffer = new byte[length];
            return _tmnt2PriorityBuffer;
        }

        private static void CopyTmnt2VisibleArea(byte[] rawFrameBuffer, byte[] frameBuffer)
        {
            for (int y = 0; y < FrameHeight; y++)
            {
                int src = (y + Tmnt2VisibleStartY) * FrameStride;
                int dst = y * FrameStride;
                Buffer.BlockCopy(rawFrameBuffer, src, frameBuffer, dst, FrameStride);
            }
        }

        private byte ReadByteMoomesa(uint address)
        {
            if (address < 0x080000 || (address >= 0x100000 && address < 0x180000))
                return _program[address];
            if (address >= 0x180000 && address <= 0x18ffff)
                return _ram[address - 0x180000];
            if (address >= 0x190000 && address <= 0x19ffff)
                return _moomesaSpriteRam[address - 0x190000];
            if (address >= 0x1a0000 && address <= 0x1a3fff)
                return ReadWordByte(_k056832.ReadRamWord((int)(((address - 0x1a0000) & 0x1fff) >> 1)), address);
            if (address >= 0x1b0000 && address <= 0x1b1fff)
                return ReadWordByte(_k056832.ReadRomWord((int)((address - 0x1b0000) >> 1)), address);
            if (address >= 0x1c0000 && address <= 0x1c1fff)
                return _paletteRam[address - 0x1c0000];
            if ((address >= 0x0c4000 && address <= 0x0c4001)
                || (address >= 0x0d0000 && address <= 0x0d001f)
                || (address >= 0x0d6000 && address <= 0x0d601f)
                || (address >= 0x0da000 && address <= 0x0dc003)
                || (address >= 0x0de000 && address <= 0x0de001))
                return ReadWordByte(ReadWordMoomesa(address & ~1u), address);
            return 0xff;
        }

        private ushort ReadWordMoomesa(uint address)
        {
            if (address < 0x07ffff || (address >= 0x100000 && address < 0x17ffff))
                return ReadBigEndianWord(_program, (int)address);
            if (address >= 0x180000 && address <= 0x18fffe)
                return ReadBigEndianWord(_ram, (int)(address - 0x180000));
            if (address >= 0x190000 && address <= 0x19fffe)
                return ReadBigEndianWord(_moomesaSpriteRam, (int)(address - 0x190000));
            if (address >= 0x0c4000 && address <= 0x0c4001)
                return _k053245.ReadControlWordNoA1(0);
            if (address >= 0x0d0000 && address <= 0x0d001e)
                return (ushort)(0xff00 | _k053252.Read((int)((address - 0x0d0000) >> 1)));
            if (address >= 0x0d6000 && address <= 0x0d601e)
                return (ushort)(0xff00 | (_sound?.K054321MainRead((int)((address - 0x0d6000) >> 1)) ?? 0xff));
            if (address >= 0x0da000 && address <= 0x0da001)
                return (ushort)((Player(3) << 8) | Player(1));
            if (address >= 0x0da002 && address <= 0x0da003)
                return (ushort)((Player(2) << 8) | 0xff);
            if (address >= 0x0dc000 && address <= 0x0dc001)
                return (ushort)(0xff00 | MysticCoins());
            if (address >= 0x0dc002 && address <= 0x0dc003)
                return (ushort)(0xff00 | MoomesaEepromPort());
            if (address >= 0x0de000 && address <= 0x0de001)
                return _moomesaControl2;
            if (address >= 0x1a0000 && address <= 0x1a3ffe)
                return _k056832.ReadRamWord((int)(((address - 0x1a0000) & 0x1fff) >> 1));
            if (address >= 0x1b0000 && address <= 0x1b1ffe)
                return _k056832.ReadRomWord((int)((address - 0x1b0000) >> 1));
            if (address >= 0x1c0000 && address <= 0x1c1ffe)
                return ReadBigEndianWord(_paletteRam, (int)(address - 0x1c0000));
            return 0xffff;
        }

        private void WriteByteMoomesa(uint address, byte value)
        {
            if (address >= 0x180000 && address <= 0x18ffff)
            {
                _ram[address - 0x180000] = value;
                return;
            }
            if (address >= 0x190000 && address <= 0x19ffff)
            {
                _moomesaSpriteRam[address - 0x190000] = value;
                return;
            }
            if (address >= 0x0c0000 && address <= 0x0c003f)
            {
                _k056832.WriteRegByte((int)(address - 0x0c0000), value);
                return;
            }
            if (address >= 0x0c2000 && address <= 0x0c2007)
            {
                _k053245.WriteControl((int)(address - 0x0c2000), value);
                return;
            }
            if (address >= 0x0ca000 && address <= 0x0ca01f)
            {
                _k054338.WriteByte((int)(address - 0x0ca000), value);
                return;
            }
            if (address >= 0x0cc000 && address <= 0x0cc01f)
            {
                if ((address & 1) != 0)
                    WriteK053251((int)((address - 0x0cc000) >> 1), value);
                return;
            }
            if (address >= 0x0d0000 && address <= 0x0d001f)
            {
                if ((address & 1) != 0)
                    _k053252.Write((int)((address - 0x0d0000) >> 1), value);
                return;
            }
            if (address >= 0x0d4000 && address <= 0x0d4001)
            {
                _sound?.PulseIrq();
                return;
            }
            if (address >= 0x0d6000 && address <= 0x0d601f)
            {
                if ((address & 1) != 0)
                    _sound?.K054321MainWrite((int)((address - 0x0d6000) >> 1), value);
                return;
            }
            if (address >= 0x0d8000 && address <= 0x0d8007)
            {
                _k056832.WriteBoardRegByte((int)(address - 0x0d8000), value);
                return;
            }
            if (address >= 0x0de000 && address <= 0x0de001)
            {
                ushort control = _moomesaControl2;
                WriteWordByte(ref control, address, value);
                WriteMoomesaControl2(control);
                return;
            }
            if (address >= 0x1a0000 && address <= 0x1a3fff)
            {
                uint wordAddress = address & ~1u;
                ushort tileWord = _k056832.ReadRamWord((int)(((wordAddress - 0x1a0000) & 0x1fff) >> 1));
                WriteWordByte(ref tileWord, address, value);
                _k056832.WriteRamWord((int)(((wordAddress - 0x1a0000) & 0x1fff) >> 1), tileWord);
                return;
            }
            if (address >= 0x1c0000 && address <= 0x1c1fff)
            {
                _paletteRam[address - 0x1c0000] = value;
                UpdatePaletteMoomesa((int)((address - 0x1c0000) >> 2));
                _paletteWrites++;
                return;
            }
            ushort word = ReadWordMoomesa(address & ~1u);
            WriteWordByte(ref word, address, value);
            WriteWordMoomesa(address & ~1u, word);
        }

        private void WriteWordMoomesa(uint address, ushort value)
        {
            if (address >= 0x180000 && address <= 0x18fffe)
            {
                WriteBigEndianWord(_ram, (int)(address - 0x180000), value);
                return;
            }
            if (address >= 0x190000 && address <= 0x19fffe)
            {
                WriteBigEndianWord(_moomesaSpriteRam, (int)(address - 0x190000), value);
                return;
            }
            if (address >= 0x0c0000 && address <= 0x0c003e)
            {
                _k056832.WriteRegWord((int)((address - 0x0c0000) >> 1), value);
                return;
            }
            if (address >= 0x0c2000 && address <= 0x0c2006)
            {
                _k053245.WriteControlWordNoA1((int)(address - 0x0c2000), value);
                return;
            }
            if (address >= 0x0ca000 && address <= 0x0ca01e)
            {
                _k054338.WriteWord((int)((address - 0x0ca000) >> 1), value);
                return;
            }
            if (address >= 0x0cc000 && address <= 0x0cc01e)
            {
                WriteK053251((int)((address - 0x0cc000) >> 1), (byte)value);
                return;
            }
            if (address >= 0x0ce000 && address <= 0x0ce01e)
            {
                WriteMoomesaProtection((int)((address - 0x0ce000) >> 1), value);
                return;
            }
            if (address >= 0x0d0000 && address <= 0x0d001e)
            {
                _k053252.Write((int)((address - 0x0d0000) >> 1), (byte)value);
                return;
            }
            if (address >= 0x0d4000 && address <= 0x0d4001)
            {
                _sound?.PulseIrq();
                return;
            }
            if (address >= 0x0d6000 && address <= 0x0d601e)
            {
                _sound?.K054321MainWrite((int)((address - 0x0d6000) >> 1), (byte)value);
                return;
            }
            if (address >= 0x0d8000 && address <= 0x0d8006)
            {
                _k056832.WriteBoardRegWord((int)((address - 0x0d8000) >> 1), value);
                return;
            }
            if (address >= 0x0de000 && address <= 0x0de001)
            {
                WriteMoomesaControl2(value);
                return;
            }
            if (address >= 0x1a0000 && address <= 0x1a3ffe)
            {
                _k056832.WriteRamWord((int)(((address - 0x1a0000) & 0x1fff) >> 1), value);
                return;
            }
            if (address >= 0x1c0000 && address <= 0x1c1ffe)
            {
                int offset = (int)(address - 0x1c0000);
                WriteBigEndianWord(_paletteRam, offset, value);
                UpdatePaletteMoomesa(offset >> 2);
                _paletteWrites++;
            }
        }

        private void UpdatePaletteMoomesa(int index)
        {
            index &= 0x7ff;
            int offset = index * 4;
            int r = _paletteRam[offset + 1];
            int g = _paletteRam[offset + 2];
            int b = _paletteRam[offset + 3];
            _palette[index] = (ushort)((r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10));
        }

        private void WriteMoomesaControl2(ushort value)
        {
            _moomesaControl2 = value;
            _tmnt2Eeprom.Write((byte)value);
            _moomesaVblankIrqEnabled = (value & 0x20) != 0;
            _moomesaSpriteIrqEnabled = (value & 0x0800) != 0;
        }

        private byte MoomesaEepromPort()
        {
            int value = 0xac;
            if (_tmnt2Eeprom.DataOut)
                value |= 0x01;
            if (_tmnt2Eeprom.Ready)
                value |= 0x02;
            return (byte)value;
        }

        private void MoomesaObjectDma()
        {
            int destination = 0;
            for (int i = 0; i < 256; i++)
            {
                int source = i * 0x100;
                ushort flags = ReadBigEndianWord(_moomesaSpriteRam, source);
                if ((flags & 0x8000) == 0 || (flags & 0x00ff) == 0)
                    continue;

                for (int word = 0; word < 8; word++)
                    _k053245.WriteHardwareWord(destination + word, ReadBigEndianWord(_moomesaSpriteRam, source + word * 2));
                destination += 8;
            }
            for (; destination < 256 * 8; destination++)
                _k053245.WriteHardwareWord(destination, 0);
        }

        private void WriteMoomesaProtection(int offset, ushort value)
        {
            _moomesaProtRam[offset & 0x0f] = value;
            if ((offset & 0x0f) != 0x0c)
                return;

            uint src1 = (uint)((_moomesaProtRam[1] & 0xff) << 16 | _moomesaProtRam[0]);
            uint src2 = (uint)((_moomesaProtRam[3] & 0xff) << 16 | _moomesaProtRam[2]);
            uint dst = (uint)((_moomesaProtRam[5] & 0xff) << 16 | _moomesaProtRam[4]);
            int length = _moomesaProtRam[0x0f];
            while (length-- > 0)
            {
                ushort a = ReadWordMoomesa(src1);
                ushort b = ReadWordMoomesa(src2);
                WriteWordMoomesa(dst, (ushort)(a + 2 * b));
                src1 += 2;
                src2 += 2;
                dst += 2;
            }
        }

        private byte ReadByteMystwarr(uint address)
        {
            if (UsesMetamrphHardware)
                return ReadByteMetamrph(address);

            if (address < 0x200000)
                return _program[address];
            if (address >= 0x200000 && address <= 0x20ffff)
                return _ram[address - 0x200000];
            if (address >= 0x400000 && address <= 0x40ffff)
                return _k053245.ReadMystwarrScatteredByte((int)(address - 0x400000));
            if (address >= 0x482000 && address <= 0x48200f)
                return ReadWordByte(_k053245.ReadMystwarrRomWord((int)((address - 0x482000) >> 1)), address);
            if (address >= 0x494000 && address <= 0x496003)
                return ReadWordByte(ReadWord(address & ~1u), address);
            if (address >= 0x498000 && address <= 0x49801f)
                return ReadK054321MainByte(address);
            if (address >= 0x49c000 && address <= 0x49c01f)
                return ReadK053252MainByte(address);
            if (address >= 0x600000 && address <= 0x603fff)
                return ReadWordByte(_k056832.ReadRamWord((int)((address - 0x600000) >> 1)), address);
            if (address >= 0x680000 && address <= 0x683fff)
                return ReadWordByte(_k056832.ReadRomWord((int)((address - 0x680000) >> 1)), address);
            if (address >= 0x700000 && address <= 0x701fff)
                return _paletteRam[address - 0x700000];
            return 0xff;
        }

        private ushort ReadWordMystwarr(uint address)
        {
            if (UsesMetamrphHardware)
                return ReadWordMetamrph(address);

            if (address < 0x1fffff)
                return ReadBigEndianWord(_program, (int)address);
            if (address >= 0x200000 && address <= 0x20fffe)
                return ReadBigEndianWord(_ram, (int)(address - 0x200000));
            if (address >= 0x400000 && address <= 0x40fffe)
                return _k053245.ReadMystwarrScatteredWord((int)((address - 0x400000) >> 1));
            if (address >= 0x482000 && address <= 0x48200e)
                return _k053245.ReadMystwarrRomWord((int)((address - 0x482000) >> 1));
            if (address >= 0x494000 && address <= 0x494001)
                return (ushort)((Player(2) << 8) | Player(1));
            if (address >= 0x494002 && address <= 0x494003)
                return 0xffff;
            if (address >= 0x496000 && address <= 0x496001)
                return (ushort)(0xff00 | MysticCoins());
            if (address >= 0x496002 && address <= 0x496003)
                return (ushort)(0xff00 | MysticEepromPort());
            if (address >= 0x498000 && address <= 0x49801e)
                return (ushort)(0xff00 | ReadK054321MainByte(address | 1));
            if (address >= 0x49c000 && address <= 0x49c01e)
                return (ushort)(0xff00 | _k053252.Read((int)((address - 0x49c000) >> 1)));
            if (address >= 0x600000 && address <= 0x603ffe)
                return _k056832.ReadRamWord((int)((address - 0x600000) >> 1));
            if (address >= 0x680000 && address <= 0x683ffe)
                return _k056832.ReadRomWord((int)((address - 0x680000) >> 1));
            if (address >= 0x700000 && address <= 0x701ffe)
                return ReadBigEndianWord(_paletteRam, (int)(address - 0x700000));
            return 0xffff;
        }

        private void WriteByteMystwarr(uint address, byte value)
        {
            if (UsesMetamrphHardware)
            {
                WriteByteMetamrph(address, value);
                return;
            }

            if (address >= 0x480000 && address <= 0x4800ff)
            {
                _k055555.WriteByte((int)(address - 0x480000), value);
                return;
            }
            if (address >= 0x482010 && address <= 0x48201f)
            {
                _k053245.WriteObjRegByte((int)(address - 0x482010), value);
                return;
            }
            if (address >= 0x484000 && address <= 0x484007)
            {
                _k053245.WriteControl((int)(address - 0x484000), value);
                return;
            }
            if (address >= 0x48a000 && address <= 0x48a01f)
            {
                _k054338.WriteByte((int)(address - 0x48a000), value);
                return;
            }
            if (address >= 0x48c000 && address <= 0x48c03f)
            {
                _k056832.WriteRegByte((int)(address - 0x48c000), value);
                return;
            }
            if (address >= 0x490000 && address <= 0x490001)
            {
                _tmnt2Eeprom.Write(value);
                return;
            }
            if (address >= 0x498000 && address <= 0x49801f)
            {
                if ((address & 1) != 0)
                    _sound?.K054321MainWrite((int)((address - 0x498000) >> 1), value);
                return;
            }
            if (address >= 0x49c000 && address <= 0x49c01f)
            {
                WriteK053252MainByte(address, value);
                return;
            }
            if (address >= 0x49e000 && address <= 0x49e007)
            {
                int byteOffset = (int)(address - 0x49e000);
                _k056832.WriteBoardRegByte(byteOffset, value);
                if ((byteOffset >> 1) == 3)
                    _mystwarrIrqControl = value;
                return;
            }

            ushort word = ReadWord(address & ~1u);
            WriteWordByte(ref word, address, value);
            WriteWordMystwarr(address & ~1u, word);
        }

        private byte ReadByteMetamrph(uint address)
        {
            if (address < 0x200000)
                return _program[address];
            if (address >= 0x200000 && address <= 0x20ffff)
                return _ram[address - 0x200000];
            if (address >= 0x210000 && address <= 0x210fff)
                return ReadWordByte(_k053245.ReadHardwareWord((int)((address - 0x210000) >> 1)), address);
            if (address >= 0x211000 && address <= 0x21ffff)
                return _metamrphExtraRam[address - 0x211000];
            if (address >= 0x244000 && address <= 0x24400f)
                return ReadWordByte(_k053245.ReadMetamrphRomWord((int)((address - 0x244000) >> 1)), address);
            if (address >= 0x24c000 && address <= 0x24ffff)
                return ReadWordByte(_k053250.ReadRamWord((int)((address - 0x24c000) >> 1)), address);
            if (address >= 0x250000 && address <= 0x25000f)
                return ReadWordByte(_k053250.ReadRegWord((int)((address - 0x250000) >> 1)), address);
            if (address >= 0x260000 && address <= 0x26001f)
                return ReadK053252MainByte(address);
            if (address >= 0x268000 && address <= 0x26801f)
                return ReadK054321MainByte(address);
            if (address >= 0x274000 && address <= 0x278003)
                return ReadWordByte(ReadWord(address & ~1u), address);
            if (address >= 0x300000 && address <= 0x303fff)
                return ReadWordByte(_k056832.ReadRamWord((int)((address - 0x300000) >> 1)), address);
            if (address >= 0x310000 && address <= 0x311fff)
                return ReadWordByte(_k056832.ReadRomWord((int)((address - 0x310000) >> 1)), address);
            if (address >= 0x320000 && address <= 0x321fff)
                return ReadWordByte(_k053250.ReadRomWord((int)((address - 0x320000) >> 1)), address);
            if (address >= 0x330000 && address <= 0x331fff)
                return _paletteRam[address - 0x330000];
            return 0xff;
        }

        private ushort ReadWordMetamrph(uint address)
        {
            if (address < 0x1fffff)
                return ReadBigEndianWord(_program, (int)address);
            if (address >= 0x200000 && address <= 0x20fffe)
                return ReadBigEndianWord(_ram, (int)(address - 0x200000));
            if (address >= 0x210000 && address <= 0x210ffe)
                return _k053245.ReadHardwareWord((int)((address - 0x210000) >> 1));
            if (address >= 0x211000 && address <= 0x21fffe)
                return ReadBigEndianWord(_metamrphExtraRam, (int)(address - 0x211000));
            if (address >= 0x244000 && address <= 0x24400e)
                return _k053245.ReadMetamrphRomWord((int)((address - 0x244000) >> 1));
            if (address >= 0x24c000 && address <= 0x24fffe)
                return _k053250.ReadRamWord((int)((address - 0x24c000) >> 1));
            if (address >= 0x250000 && address <= 0x25000e)
                return _k053250.ReadRegWord((int)((address - 0x250000) >> 1));
            if (address >= 0x260000 && address <= 0x26001e)
                return (ushort)(0xff00 | _k053252.Read((int)((address - 0x260000) >> 1)));
            if (address >= 0x268000 && address <= 0x26801e)
                return (ushort)(0xff00 | ReadK054321MainByte(address | 1));
            if (address >= 0x274000 && address <= 0x274001)
                return (ushort)((Player(3) << 8) | Player(1));
            if (address >= 0x274002 && address <= 0x274003)
                return 0xffff;
            if (address >= 0x278000 && address <= 0x278001)
                return (ushort)(0xff00 | MysticCoins());
            if (address >= 0x278002 && address <= 0x278003)
                return (ushort)(0xff00 | MetamrphEepromPort());
            if (address >= 0x300000 && address <= 0x303ffe)
                return _k056832.ReadRamWord((int)((address - 0x300000) >> 1));
            if (address >= 0x310000 && address <= 0x311ffe)
                return _k056832.ReadRomWord((int)((address - 0x310000) >> 1));
            if (address >= 0x320000 && address <= 0x321ffe)
                return _k053250.ReadRomWord((int)((address - 0x320000) >> 1));
            if (address >= 0x330000 && address <= 0x331ffe)
                return ReadBigEndianWord(_paletteRam, (int)(address - 0x330000));
            return 0xffff;
        }

        private void WriteByteMetamrph(uint address, byte value)
        {
            if (address >= 0x210000 && address <= 0x210fff)
            {
                ushort spriteWord = _k053245.ReadHardwareWord((int)((address - 0x210000) >> 1));
                WriteWordByte(ref spriteWord, address, value);
                _k053245.WriteHardwareWord((int)((address - 0x210000) >> 1), spriteWord);
                return;
            }
            if (address >= 0x211000 && address <= 0x21ffff)
            {
                _metamrphExtraRam[address - 0x211000] = value;
                return;
            }
            if (address >= 0x240000 && address <= 0x240007)
            {
                _k053245.WriteControl((int)(address - 0x240000), value);
                return;
            }
            if (address >= 0x244010 && address <= 0x24401f)
            {
                _k053245.WriteObjRegByte((int)(address - 0x244010), value);
                return;
            }
            if (address >= 0x24c000 && address <= 0x24ffff)
            {
                ushort k053250RamWord = _k053250.ReadRamWord((int)((address - 0x24c000) >> 1));
                WriteWordByte(ref k053250RamWord, address, value);
                _k053250.WriteRamWord((int)((address - 0x24c000) >> 1), k053250RamWord);
                return;
            }
            if (address >= 0x250000 && address <= 0x25000f)
            {
                ushort k053250RegWord = _k053250.ReadRegWord((int)((address - 0x250000) >> 1));
                WriteWordByte(ref k053250RegWord, address, value);
                _k053250.WriteRegWord((int)((address - 0x250000) >> 1), k053250RegWord);
                return;
            }
            if (address >= 0x254000 && address <= 0x25401f)
            {
                _k054338.WriteByte((int)(address - 0x254000), value);
                return;
            }
            if (address >= 0x258000 && address <= 0x2580ff)
            {
                _k055555.WriteByte((int)(address - 0x258000), value);
                return;
            }
            if (address >= 0x260000 && address <= 0x26001f)
            {
                WriteK053252MainByte(address, value);
                return;
            }
            if (address >= 0x268000 && address <= 0x26801f)
            {
                if ((address & 1) != 0)
                    _sound?.K054321MainWrite((int)((address - 0x268000) >> 1), value);
                return;
            }
            if (address >= 0x26c000 && address <= 0x26c007)
            {
                _k056832.WriteBoardRegByte((int)(address - 0x26c000), value);
                return;
            }
            if (address >= 0x270000 && address <= 0x27003f)
            {
                _k056832.WriteRegByte((int)(address - 0x270000), value);
                return;
            }

            ushort word = ReadWord(address & ~1u);
            WriteWordByte(ref word, address, value);
            WriteWordMetamrph(address & ~1u, word);
        }

        private void WriteWordMetamrph(uint address, ushort value)
        {
            if (address >= 0x200000 && address <= 0x20fffe)
            {
                WriteBigEndianWord(_ram, (int)(address - 0x200000), value);
                return;
            }
            if (address >= 0x210000 && address <= 0x210ffe)
            {
                _spriteWrites++;
                _k053245.WriteHardwareWord((int)((address - 0x210000) >> 1), value);
                return;
            }
            if (address >= 0x211000 && address <= 0x21fffe)
            {
                WriteBigEndianWord(_metamrphExtraRam, (int)(address - 0x211000), value);
                return;
            }
            if (address >= 0x240000 && address <= 0x240006)
            {
                _k053245.WriteControlWordNoA1((int)(address - 0x240000), value);
                return;
            }
            if (address >= 0x244010 && address <= 0x24401e)
            {
                _k053245.WriteObjRegWord((int)((address - 0x244010) >> 1), value);
                return;
            }
            if (address >= 0x24c000 && address <= 0x24fffe)
            {
                _k053250.WriteRamWord((int)((address - 0x24c000) >> 1), value);
                return;
            }
            if (address >= 0x250000 && address <= 0x25000e)
            {
                _k053250.WriteRegWord((int)((address - 0x250000) >> 1), value);
                return;
            }
            if (address >= 0x254000 && address <= 0x25401e)
            {
                _k054338.WriteWord((int)((address - 0x254000) >> 1), value);
                return;
            }
            if (address >= 0x258000 && address <= 0x2580fe)
            {
                _k055555.WriteWord((int)((address - 0x258000) >> 1), value);
                return;
            }
            if (address >= 0x260000 && address <= 0x26001e)
            {
                _k053252.Write((int)((address - 0x260000) >> 1), (byte)value);
                return;
            }
            if (address >= 0x264000 && address <= 0x264001)
            {
                _sound?.PulseIrq();
                return;
            }
            if (address >= 0x268000 && address <= 0x26801e)
            {
                _sound?.K054321MainWrite((int)((address - 0x268000) >> 1), (byte)value);
                return;
            }
            if (address >= 0x26c000 && address <= 0x26c006)
            {
                _k056832.WriteBoardRegWord((int)((address - 0x26c000) >> 1), value);
                return;
            }
            if (address >= 0x270000 && address <= 0x27003e)
            {
                _k056832.WriteRegWord((int)((address - 0x270000) >> 1), value);
                return;
            }
            if (address >= 0x27c000 && address <= 0x27c001)
            {
                _tmnt2Eeprom.Write((byte)value);
                return;
            }
            if (address >= 0x300000 && address <= 0x303ffe)
            {
                _k056832.WriteRamWord((int)((address - 0x300000) >> 1), value);
                return;
            }
            if (address >= 0x330000 && address <= 0x331ffe)
            {
                int offset = (int)(address - 0x330000);
                WriteBigEndianWord(_paletteRam, offset, value);
                UpdatePaletteMystwarr(offset >> 2);
                _paletteWrites++;
            }
        }

        private byte ReadK054321MainByte(uint address)
        {
            if ((address & 1) == 0)
                return 0xff;

            uint baseAddress = UsesMetamrphHardware ? 0x268000u : 0x498000u;
            int offset = (int)((address - baseAddress) >> 1) & 0x0f;
            return _sound?.K054321MainRead(offset) ?? 0xff;
        }

        private byte ReadK053252MainByte(uint address)
        {
            if ((address & 1) == 0)
                return 0xff;

            uint baseAddress = UsesMetamrphHardware ? 0x260000u : 0x49c000u;
            return _k053252.Read((int)((address - baseAddress) >> 1));
        }

        private void WriteK053252MainByte(uint address, byte value)
        {
            if ((address & 1) == 0)
                return;

            uint baseAddress = UsesMetamrphHardware ? 0x260000u : 0x49c000u;
            _k053252.Write((int)((address - baseAddress) >> 1), value);
        }

        private void WriteWordMystwarr(uint address, ushort value)
        {
            if (UsesMetamrphHardware)
            {
                WriteWordMetamrph(address, value);
                return;
            }

            if (address >= 0x200000 && address <= 0x20fffe)
            {
                WriteBigEndianWord(_ram, (int)(address - 0x200000), value);
                return;
            }
            if (address >= 0x400000 && address <= 0x40fffe)
            {
                _spriteWrites++;
                _k053245.WriteMystwarrScatteredWord((int)((address - 0x400000) >> 1), value);
                return;
            }
            if (address >= 0x480000 && address <= 0x4800fe)
            {
                _k055555.WriteWord((int)((address - 0x480000) >> 1), value);
                return;
            }
            if (address >= 0x482010 && address <= 0x48201e)
            {
                _k053245.WriteObjRegWord((int)((address - 0x482010) >> 1), value);
                return;
            }
            if (address >= 0x484000 && address <= 0x484006)
            {
                _k053245.WriteControlWordNoA1((int)(address - 0x484000), value);
                return;
            }
            if (address >= 0x48a000 && address <= 0x48a01e)
            {
                _k054338.WriteWord((int)((address - 0x48a000) >> 1), value);
                return;
            }
            if (address >= 0x48c000 && address <= 0x48c03e)
            {
                _k056832.WriteRegWord((int)((address - 0x48c000) >> 1), value);
                return;
            }
            if (address >= 0x490000 && address <= 0x490001)
            {
                _tmnt2Eeprom.Write((byte)(value >> 8));
                return;
            }
            if (address >= 0x498000 && address <= 0x49801e)
            {
                _sound?.K054321MainWrite((int)((address - 0x498000) >> 1), (byte)value);
                return;
            }
            if (address >= 0x49a000 && address <= 0x49a001)
            {
                _sound?.PulseIrq();
                return;
            }
            if (address >= 0x49c000 && address <= 0x49c01e)
            {
                _k053252.Write((int)((address - 0x49c000) >> 1), (byte)value);
                return;
            }
            if (address >= 0x49e000 && address <= 0x49e006)
            {
                int offset = (int)((address - 0x49e000) >> 1);
                _k056832.WriteBoardRegWord(offset, value);
                if (offset == 3)
                    _mystwarrIrqControl = (byte)value;
                return;
            }
            if (address >= 0x600000 && address <= 0x603ffe)
            {
                _k056832.WriteRamWord((int)((address - 0x600000) >> 1), value);
                return;
            }
            if (address >= 0x700000 && address <= 0x701ffe)
            {
                int offset = (int)(address - 0x700000);
                WriteBigEndianWord(_paletteRam, offset, value);
                UpdatePaletteMystwarr(offset >> 2);
                _paletteWrites++;
            }
        }

        private byte ReadByteTmnt2(uint address)
        {
            if (address < ProgramRomLength)
                return _program[address];
            if (address >= 0x104000 && address <= 0x107fff)
                return _ram[address - 0x104000];
            if (address >= 0x140000 && address <= 0x140fff)
                return _paletteRam[address - 0x140000];
            if (address >= 0x180000 && address <= 0x183fff)
                return _k053245.ReadScatteredByte((int)(address - 0x180000));
            if (address >= 0x1c0000 && address <= 0x1c081f)
                return ReadWordByte(ReadWord(address & ~1u), address);
            if (address >= 0x5a0000 && address <= 0x5a001f)
                return ReadWordByte(ReadWord(address & ~1u), address);
            if (address >= 0x5c0600 && address <= 0x5c0603)
                return ReadWordByte(ReadWord(address & ~1u), address);
            if (address >= 0x600000 && address <= 0x603fff)
            {
                int offset = (int)((address - 0x600000) >> 1);
                return _k052109.Read((address & 1) == 0 ? offset : offset + 0x2000);
            }
            return 0xff;
        }

        private ushort ReadWordTmnt2(uint address)
        {
            if (address < ProgramRomLength - 1)
                return ReadBigEndianWord(_program, (int)address);
            if (address >= 0x104000 && address <= 0x107ffe)
                return ReadBigEndianWord(_ram, (int)(address - 0x104000));
            if (address >= 0x140000 && address <= 0x140ffe)
                return ReadBigEndianWord(_paletteRam, (int)(address - 0x140000));
            if (address >= 0x180000 && address <= 0x183ffe)
                return _k053245.ReadScatteredWord((int)((address - 0x180000) >> 1));
            if (_variant == TmntHardwareVariant.Ssriders && address >= 0x1c0000 && address <= 0x1c0001)
                return (ushort)(0xff00 | Player(1));
            if (_variant == TmntHardwareVariant.Ssriders && address >= 0x1c0002 && address <= 0x1c0003)
                return (ushort)(0xff00 | Player(2));
            if (_variant == TmntHardwareVariant.Ssriders && address >= 0x1c0004 && address <= 0x1c0007)
                return 0xffff;
            if (_variant == TmntHardwareVariant.Ssriders && address >= 0x1c0100 && address <= 0x1c0101)
                return (ushort)(0xff00 | SsridersCoins());
            if (_variant == TmntHardwareVariant.Ssriders && address >= 0x1c0102 && address <= 0x1c0103)
                return (ushort)(0xff00 | SsridersEepromPort());
            if (address >= 0x1c0000 && address <= 0x1c0001)
                return (ushort)(0xff00 | Player(1));
            if (address >= 0x1c0002 && address <= 0x1c0007)
                return 0xffff;
            if (address >= 0x1c0100 && address <= 0x1c0101)
                return (ushort)(0xff00 | Coins());
            if (address >= 0x1c0102 && address <= 0x1c0103)
                return (ushort)(0xff00 | Tmnt2EepromPort());
            if (address >= 0x1c0400 && address <= 0x1c0401)
                return 0xffff;
            if (address >= 0x1c0500 && address <= 0x1c057e)
                return ReadBigEndianWord(_tmnt2UnknownRam, (int)(address - 0x1c0500));
            if (address >= 0x1c0800 && address <= 0x1c081e)
            {
                if (_variant == TmntHardwareVariant.Ssriders)
                    return ReadSsridersProtection();
                return _tmnt2ProtRam[(address - 0x1c0800) >> 1];
            }
            if (address >= 0x5a0000 && address <= 0x5a001e)
                return _k053245.ReadControlWordNoA1((int)(address - 0x5a0000));
            if (address >= 0x5c0600 && address <= 0x5c0603)
                return (ushort)(0xff00 | (_sound?.K053260MainRead((int)((address - 0x5c0600) >> 1)) ?? 0xff));
            if (address >= 0x600000 && address <= 0x603ffe)
            {
                int offset = (int)((address - 0x600000) >> 1);
                return (ushort)(_k052109.Read(offset) << 8);
            }
            return 0xffff;
        }

        private void WriteByteTmnt2(uint address, byte value)
        {
            if (address >= 0x104000 && address <= 0x107fff)
            {
                _ram[address - 0x104000] = value;
                return;
            }
            if (address >= 0x140000 && address <= 0x140fff)
            {
                int offset = (int)(address - 0x140000);
                _paletteRam[offset] = value;
                UpdatePaletteTmnt2(offset >> 1);
                _paletteWrites++;
                return;
            }
            if (address >= 0x180000 && address <= 0x183fff)
            {
                _spriteWrites++;
                _k053245.WriteScatteredByte((int)(address - 0x180000), value);
                return;
            }
            if (address >= 0x1c0000 && address <= 0x1c081f)
            {
                ushort word = ReadWord(address & ~1u);
                WriteWordByte(ref word, address, value);
                WriteWordTmnt2(address & ~1u, word, highByteAccess: (address & 1) == 0);
                return;
            }
            if (address >= 0x5a0000 && address <= 0x5a001f)
            {
                _k053245.WriteControl((int)(address - 0x5a0000), value);
                return;
            }
            if (address >= 0x5c0600 && address <= 0x5c0603)
            {
                if ((address & 1) != 0)
                    _sound?.K053260MainWrite((int)((address - 0x5c0600) >> 1), value);
                return;
            }
            if (address >= 0x5c0604 && address <= 0x5c0605)
            {
                _sound?.PulseIrq();
                return;
            }
            if (address >= 0x5c0700 && address <= 0x5c071f)
            {
                WriteK053251((int)((address - 0x5c0700) >> 1), value);
                return;
            }
            if (address >= 0x600000 && address <= 0x603fff)
            {
                int offset = (int)((address - 0x600000) >> 1);
                WriteK052109((address & 1) == 0 ? offset : offset + 0x2000, value);
            }
        }

        private void WriteWordTmnt2(uint address, ushort value, bool highByteAccess = true)
        {
            if (address >= 0x104000 && address <= 0x107ffe)
            {
                WriteBigEndianWord(_ram, (int)(address - 0x104000), value);
                return;
            }
            if (address >= 0x140000 && address <= 0x140ffe)
            {
                int offset = (int)(address - 0x140000);
                WriteBigEndianWord(_paletteRam, offset, value);
                UpdatePaletteTmnt2(offset >> 1);
                _paletteWrites++;
                return;
            }
            if (address >= 0x180000 && address <= 0x183ffe)
            {
                _spriteWrites++;
                _k053245.WriteScatteredWord((int)((address - 0x180000) >> 1), value);
                return;
            }
            if (address >= 0x1c0200 && address <= 0x1c0201)
            {
                if (_variant == TmntHardwareVariant.Ssriders)
                    WriteSsridersEepromAndGfxControl((byte)value);
                else
                    WriteTmnt2EepromAndGfxControl((byte)value);
                return;
            }
            if (address >= 0x1c0300 && address <= 0x1c0301)
            {
                _k052109.Rmrd = (value & 0x08) != 0;
                return;
            }
            if (address >= 0x1c0400 && address <= 0x1c0401)
                return;
            if (address >= 0x1c0500 && address <= 0x1c057e)
            {
                WriteBigEndianWord(_tmnt2UnknownRam, (int)(address - 0x1c0500), value);
                return;
            }
            if (address >= 0x1c0800 && address <= 0x1c081e)
            {
                if (_variant == TmntHardwareVariant.Ssriders)
                    WriteSsridersProtection((int)((address - 0x1c0800) >> 1));
                else
                    WriteTmnt2Protection((int)((address - 0x1c0800) >> 1), value, highByteAccess);
                return;
            }
            if (address >= 0x5a0000 && address <= 0x5a001e)
            {
                _k053245.WriteControlWordNoA1((int)(address - 0x5a0000), value);
                return;
            }
            if (address >= 0x5c0600 && address <= 0x5c0603)
            {
                _sound?.K053260MainWrite((int)((address - 0x5c0600) >> 1), (byte)value);
                return;
            }
            if (address >= 0x5c0604 && address <= 0x5c0605)
            {
                _sound?.PulseIrq();
                return;
            }
            if (address >= 0x5c0700 && address <= 0x5c071e)
            {
                WriteK053251((int)((address - 0x5c0700) >> 1), (byte)value);
                return;
            }
            if (address >= 0x600000 && address <= 0x603ffe)
            {
                int offset = (int)((address - 0x600000) >> 1);
                WriteK052109(offset, (byte)(value >> 8));
            }
        }

        private void WriteTmnt2Protection(int offset, ushort value, bool highByteAccess)
        {
            _tmnt2ProtRam[offset & 0x0f] = value;
            if (offset != 0x0c || !highByteAccess || (_tmnt2ProtRam[8] & 0xff00) != 0x8200)
                return;

            uint srcAddr = (uint)(_tmnt2ProtRam[0] | ((_tmnt2ProtRam[1] & 0xff) << 16)) >> 1;
            uint dstAddr = (uint)(_tmnt2ProtRam[2] | ((_tmnt2ProtRam[3] & 0xff) << 16)) >> 1;
            uint modAddr = (uint)(_tmnt2ProtRam[4] | ((_tmnt2ProtRam[5] & 0xff) << 16)) >> 1;
            bool zlock = (_tmnt2ProtRam[8] & 0xff) == 1;

            Span<ushort> src = stackalloc ushort[4];
            Span<ushort> mod = stackalloc ushort[24];
            for (int i = 0; i < src.Length; i++)
                src[i] = Tmnt2GetWord(srcAddr + (uint)i);
            for (int i = 0; i < mod.Length; i++)
                mod[i] = Tmnt2GetWord(modAddr + (uint)i);

            int code = src[0];
            int f1 = src[1];
            int attr1 = (f1 >> 2) & 0x3f00;
            int attr2 = f1 & 0x0380;
            int cbase = f1 & 0x001f;
            int cmod = mod[0x2a / 2] >> 8;
            int color = cbase != 0x0f && cmod <= 0x1f && !zlock ? cmod : cbase;
            int xoffs = (short)src[2];
            int yoffs = (short)src[3];
            int f2 = mod[0];
            attr2 |= f2 & 0x0060;
            bool keepAspect = (f2 & 0x0014) == 0x0014;
            if ((f2 & 0x8000) != 0) attr1 |= 0x8000;
            if (keepAspect) attr1 |= 0x4000;
            if ((f2 & 0x4000) != 0)
            {
                attr1 ^= 0x1000;
                xoffs = -xoffs;
            }

            int xmod = (short)mod[6];
            int ymod = (short)mod[7];
            int zmod = (short)mod[8];
            int xzoom = mod[0x1c / 2];
            int yzoom = keepAspect ? xzoom : mod[0x1e / 2];
            bool xyLock = (f2 & 0x003b) == 0x0020;
            if (!xyLock)
            {
                xoffs = ApplyTmnt2ZoomOffset(xoffs, xzoom);
                yoffs = ApplyTmnt2ZoomOffset(yoffs, yzoom);
            }
            if (!zlock)
                yoffs += zmod;
            xoffs += xmod;
            yoffs += ymod;

            _tmnt2ProtectionRuns++;
            _lastTmnt2Protection = $"s={srcAddr:X5} d={dstAddr:X5} m={modAddr:X5} code={code:X4} f1={f1:X4} f2={f2:X4} "
                                   + $"srcxy={(short)src[2]},{(short)src[3]} mod={xmod},{ymod},{zmod} zoom={xzoom:X4}/{yzoom:X4} out={xoffs},{yoffs} attr={attr1:X4}/{(attr2 | color):X4}";

            Tmnt2PutWord(dstAddr + 0, (ushort)attr1);
            Tmnt2PutWord(dstAddr + 2, (ushort)code);
            Tmnt2PutWord(dstAddr + 4, (ushort)yoffs);
            Tmnt2PutWord(dstAddr + 6, (ushort)xoffs);
            Tmnt2PutWord(dstAddr + 12, (ushort)(attr2 | color));
        }

        private ushort ReadSsridersProtection()
        {
            int data = ReadBigEndianWord(_ram, 0x1a0a);
            int command = ReadBigEndianWord(_ram, 0x18fc);
            ushort result = command switch
            {
                0x100b => 0x0064,
                0x6003 => (ushort)(data & 0x000f),
                0x6004 => (ushort)(data & 0x001f),
                0x6000 => (ushort)(data & 0x0001),
                0x0000 => (ushort)(data & 0x00ff),
                0x6007 => (ushort)(data & 0x00ff),
                0x8abc => SsridersCollisionTableIndex(),
                _ => 0xffff
            };
            _ssridersProtectionReads++;
            if (result == 0xffff)
                _ssridersUnknownProtectionReads++;
            _lastSsridersProtectionRead = $"cmd={command:X4} data={data:X4} -> {result:X4}";
            return result;
        }

        private ushort SsridersCollisionTableIndex()
        {
            int data = -ReadBigEndianWord(_ram, 0x1818);
            data = ((data / 8 - 4) & 0x1f) * 0x40;
            data += ((ReadBigEndianWord(_ram, 0x1cb0) + ReadBigEndianWord(_ram, 0x00c8) - 6) / 8 + 12) & 0x3f;
            return (ushort)data;
        }

        private void WriteSsridersProtection(int offset)
        {
            if (offset != 1)
                return;

            int hardwarePriority = 1;
            for (int logicalPriority = 1; logicalPriority < 0x100; logicalPriority <<= 1)
            {
                for (int i = 0; i < 128; i++)
                {
                    int sourceOffset = 3 + 64 * i;
                    if ((_k053245.ReadCpuRamWord(sourceOffset) >> 8) != logicalPriority)
                        continue;

                    ushort existing = _k053245.ReadHardwareWord(8 * i);
                    _k053245.WriteHardwareWord(8 * i, (ushort)((existing & 0xff00) | hardwarePriority));
                    hardwarePriority++;
                }
            }

            _tmnt2ProtectionRuns++;
            _lastTmnt2Protection = $"ssriders-pri count={hardwarePriority - 1}";
        }

        private byte Tmnt2EepromPort()
        {
            int value = 0xf0; // Bit 2 is active-high OBJMPX/unknown and idles low; bit 3 is active-low VBLANK.
            if (_tmnt2Eeprom.DataOut)
                value |= 0x01;
            if (_tmnt2Eeprom.Ready)
                value |= 0x02;
            if (!_tmnt2InVblank)
                value |= 0x08;
            return (byte)value;
        }

        private byte SsridersEepromPort()
        {
            int value = 0xf0; // Bit 2 is active-high OBJMPX/unknown and idles low; bit 3 is active-low VBLANK.
            if (_tmnt2Eeprom.DataOut)
                value |= 0x01;
            if (_tmnt2Eeprom.Ready)
                value |= 0x02;
            if (!_tmnt2InVblank)
                value |= 0x08;
            return (byte)value;
        }

        private void WriteTmnt2EepromAndGfxControl(byte value)
        {
            _tmnt2Eeprom.Write(value);
            _k053245.BankSelect((value & 0x20) != 0 ? 4 : 0);
        }

        private void WriteSsridersEepromAndGfxControl(byte value)
        {
            _tmnt2Eeprom.Write(value);
            _k053245.BankSelect((value & 0x20) != 0 ? 4 : 0);
        }

        private int ApplyTmnt2ZoomOffset(int offset, int zoom)
        {
            int z = zoom - 0x4f00;
            if (z > 0)
            {
                z >>= 8;
                return offset + (int)(Math.Pow(z, 1.891292) * offset / 599.250121);
            }
            if (z < 0)
            {
                z = (z >> 3) + (z >> 4) + (z >> 5) + (z >> 6) + zoom;
                return z > 0 ? offset * z / 0x4f00 : 0;
            }
            return offset;
        }

        private ushort Tmnt2GetWord(uint wordAddress)
        {
            uint byteAddress = wordAddress << 1;
            if (byteAddress <= 0x07fffe)
                return ReadBigEndianWord(_program, (int)byteAddress);
            if (byteAddress >= 0x104000 && byteAddress <= 0x107ffe)
                return ReadBigEndianWord(_ram, (int)(byteAddress - 0x104000));
            if (byteAddress >= 0x180000 && byteAddress <= 0x183ffe)
                return _k053245.ReadScatteredWord((int)((byteAddress - 0x180000) >> 1));
            return 0;
        }

        private void Tmnt2PutWord(uint wordAddress, ushort value)
        {
            uint byteAddress = wordAddress << 1;
            if (byteAddress >= 0x180000 && byteAddress <= 0x183ffe)
                _k053245.WriteScatteredWord((int)((byteAddress - 0x180000) >> 1), value);
            else if (byteAddress >= 0x104000 && byteAddress <= 0x107ffe)
                WriteBigEndianWord(_ram, (int)(byteAddress - 0x104000), value);
        }

        private void UpdatePaletteTmnt2(int index)
        {
            index &= 0x7ff;
            _palette[index] = ReadBigEndianWord(_paletteRam, index * 2);
        }

        private void UpdatePaletteMystwarr(int index)
        {
            index &= 0x7ff;
            int offset = index * 4;
            int raw = (_paletteRam[offset] << 24) | (_paletteRam[offset + 1] << 16) | (_paletteRam[offset + 2] << 8) | _paletteRam[offset + 3];
            int r = (raw >> 16) & 0xff;
            int g = (raw >> 8) & 0xff;
            int b = raw & 0xff;
            _palette[index] = (ushort)((r >> 3) | ((g >> 3) << 5) | ((b >> 3) << 10));
        }

        private void WriteK053251(int offset, byte value)
        {
            offset &= 0x0f;
            _k053251[offset] = (byte)(value & 0x3f);
            if (offset == 9 || offset == 10)
                ResetK053251Indexes();
        }

        private void ResetK053251Indexes()
        {
            _k053251PaletteIndex[0] = (byte)(32 * ((_k053251[9] >> 0) & 0x03));
            _k053251PaletteIndex[1] = (byte)(32 * ((_k053251[9] >> 2) & 0x03));
            _k053251PaletteIndex[2] = (byte)(32 * ((_k053251[9] >> 4) & 0x03));
            _k053251PaletteIndex[3] = (byte)(16 * ((_k053251[10] >> 0) & 0x07));
            _k053251PaletteIndex[4] = (byte)(16 * ((_k053251[10] >> 3) & 0x07));
        }

        private void UpdateTmnt2LayerColorBases()
        {
            _k052109.LayerColorBase[0] = _k053251PaletteIndex[2];
            _k052109.LayerColorBase[1] = _k053251PaletteIndex[4];
            _k052109.LayerColorBase[2] = _k053251PaletteIndex[3];
            _k053245.SpriteColorBase = _k053251PaletteIndex[1];
        }

        private int K053251Priority(int colorIndex) => _k053251[colorIndex & 0x0f];

        private static void SortKonamiLayers3(Span<int> layer, Span<int> priority)
        {
            SortKonamiLayerPair(layer, priority, 1, 2);
            SortKonamiLayerPair(layer, priority, 0, 2);
            SortKonamiLayerPair(layer, priority, 0, 1);
        }

        private static void SortKonamiLayers4(Span<int> layer, Span<int> priority)
        {
            SortKonamiLayerPair(layer, priority, 0, 1);
            SortKonamiLayerPair(layer, priority, 2, 3);
            SortKonamiLayerPair(layer, priority, 0, 2);
            SortKonamiLayerPair(layer, priority, 1, 3);
            SortKonamiLayerPair(layer, priority, 1, 2);
        }

        private static void SortKonamiLayerPair(Span<int> layer, Span<int> priority, int a, int b)
        {
            if (priority[a] >= priority[b])
                return;

            (priority[a], priority[b]) = (priority[b], priority[a]);
            (layer[a], layer[b]) = (layer[b], layer[a]);
        }

        private static MystwarrRenderItem LayerRenderItem(int priority, int layer)
            => new(((uint)(priority & 0xff) << 24), layer, -1, 0, 0);

        private static void ReverseMystwarrRenderItems(Span<MystwarrRenderItem> items)
        {
            for (int left = 0, right = items.Length - 1; left < right; left++, right--)
                (items[left], items[right]) = (items[right], items[left]);
        }

        private static void SortMystwarrRenderItems(Span<MystwarrRenderItem> items)
        {
            for (int i = 1; i < items.Length; i++)
            {
                MystwarrRenderItem item = items[i];
                int j = i - 1;
                while (j >= 0 && items[j].Order < item.Order)
                {
                    items[j + 1] = items[j];
                    j--;
                }
                items[j + 1] = item;
            }
        }

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

        private byte SsridersCoins()
        {
            int value = 0xff;
            if (_input.Coin || _input.Start)
                value &= ~0x01;
            return (byte)value;
        }

        private byte MysticCoins()
        {
            int value = 0xff;
            if (_input.Coin || _input.Start)
                value &= ~0x01;
            return (byte)value;
        }

        private byte MysticEepromPort()
        {
            int value = 0xe4; // Bit 3 must idle low; MAME notes the game loops if it is set.
            if (_tmnt2Eeprom.DataOut)
                value |= 0x01;
            if (_tmnt2Eeprom.Ready)
                value |= 0x02;
            return (byte)value;
        }

        private byte MetamrphEepromPort()
        {
            int value = 0xec;
            if (_tmnt2Eeprom.DataOut)
                value |= 0x01;
            if (_tmnt2Eeprom.Ready)
                value |= 0x02;
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
        private byte _irqControl;
        private int _charRomReads;

        public bool Rmrd { get; set; }
        public bool IrqEnabled => (_irqControl & 0x04) != 0;
        public int[] LayerColorBase { get; } = { 0, 32, 40 };

        public void Load(byte[] rom) => Array.Copy(rom, _rom, Math.Min(rom.Length, _rom.Length));

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
            _irqControl = 0;
            _charRomReads = 0;
            LayerColorBase[0] = 0;
            LayerColorBase[1] = 32;
            LayerColorBase[2] = 40;
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
                case 0x1d00:
                    _irqControl = data;
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

        public void RenderLayer(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int layer, bool opaque, int paletteMask,
            int outputHeight = FrameHeight, byte[]? priorityBuffer = null, int priorityCode = 0)
        {
            int attrBase = layer switch { 0 => 0x0000, 1 => 0x0800, _ => 0x1000 };
            int codeBase = layer switch { 0 => 0x2000, 1 => 0x2800, _ => 0x3000 };
            int code2Base = layer switch { 0 => 0x4000, 1 => 0x4800, _ => 0x5000 };
            int scrollY = GetScrollY(layer);

            for (int sy = 0; sy < outputHeight; sy++)
            {
                int scrollX = GetScrollX(layer, sy, scrollY);
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

                    TmntTileCallback(layer, bank, ref code, ref attr, LayerColorBase);
                    int pen = DecodeTilePixel(code, pixelX, pixelY);
                    if (pen == 0 && !opaque)
                        continue;

                    int color = (attr & 0x7f) * 16 + pen;
                    WritePixel(frameBuffer, sx, sy, palette[color & paletteMask]);
                    if (priorityBuffer != null)
                        priorityBuffer[sy * FrameWidth + sx] = (byte)(priorityBuffer[sy * FrameWidth + sx] | priorityCode);
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
                   + $"addrMap=0x{_addrMap:X2} scroll=0x{_scrollCtrl:X2} flip=0x{_tileFlipEnable:X2} rsub=0x{_romSubBank:X2} cromR={_charRomReads}";
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

        private int GetScrollY(int layer)
        {
            if (layer == 0)
                return 0;

            int baseMask = layer == 1 ? 0x0000 : 0x2000;
            int scrollYBase = 0x1800 | baseMask;
            return _ram[scrollYBase + 12];
        }

        private int GetScrollX(int layer, int screenY, int scrollY)
        {
            if (layer == 0)
                return 96;

            int tmap = layer - 1;
            int scrollControl = (_scrollCtrl >> (tmap * 3)) & 0x07;
            int rows = scrollControl switch
            {
                0 => 1,
                1 => 1,
                2 => 32,
                _ => 256
            };
            int baseMask = layer == 1 ? 0x0000 : 0x2000;
            int scrollXBase = 0x1a00 | baseMask;

            if (rows == 1)
                return (_ram[scrollXBase] | (_ram[scrollXBase + 1] << 8)) + 90;

            int row = (screenY - scrollY) & 0xff;
            int rowMask = rows == 256 ? 0xff : 0xf8;
            int offset = 2 * (row & rowMask);
            return (_ram[scrollXBase + offset] | (_ram[scrollXBase + offset + 1] << 8)) + 90;
        }

        private byte ReadCharRom(int offset)
        {
            _charRomReads++;
            int code = (offset & 0x1fff) >> 5;
            byte color = _romSubBank;
            int bankIndex = (color & 0x0c) >> 2;
            int bank = (_charBank[bankIndex] >> 2) | (_charBank2[bankIndex] >> 2);
            TmntTileCallback(0, bank, ref code, ref color, LayerColorBase);
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

        public void Load(byte[] rom) => Array.Copy(rom, _rom, Math.Min(rom.Length, _rom.Length));

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

    private sealed class K055555
    {
        public const int PriInpObj = 15;
        public const int InputEnables = 45;
        public const int InputVramA = 0x01;
        public const int InputVramB = 0x02;
        public const int InputVramC = 0x04;
        public const int InputVramD = 0x08;
        public const int InputObj = 0x10;
        public const int InputSub1 = 0x20;
        public const int BlendEnables = 33;
        public const int VInMixOn = 34;
        public const int ShadowPriority1 = 37;
        public const int ShadowPriority2 = 38;
        public const int ShadowPriority3 = 39;
        public const int ShadowOn = 40;
        public const int ShadowPrioritySelect = 41;
        public const int VramBrightness = 42;
        public const int ObjBrightness = 43;
        public const int ObjBrightnessOn = 44;
        public const int PriInp0 = 7;
        public const int PriInp3 = 10;
        public const int PriInp6 = 13;
        public const int PriInp7 = 14;
        public const int PriInp9 = 16;

        private readonly byte[] _regs = new byte[128];

        public void Reset() => Array.Clear(_regs);

        public void WriteWord(int offset, ushort data)
        {
            offset &= 0x7f;
            _regs[offset] = (byte)(data >> 8);
        }

        public void WriteByte(int byteOffset, byte data)
        {
            _regs[(byteOffset >> 1) & 0x7f] = data;
        }

        public int ReadRegister(int index) => _regs[index & 0x7f];

        public int GetPaletteIndex(int index) => _regs[(23 + index) & 0x7f];

        public string DebugSummary()
            => $"inp={_regs[InputEnables]:X2} pri={_regs[PriInp0]:X2}/{_regs[PriInp3]:X2}/{_regs[PriInp6]:X2}/{_regs[PriInp7]:X2}/obj{_regs[PriInpObj]:X2} "
               + $"pal={_regs[23]:X2}/{_regs[24]:X2}/{_regs[25]:X2}/{_regs[26]:X2}/{_regs[27]:X2} "
               + $"mix={_regs[BlendEnables]:X2}/{_regs[VInMixOn]:X2} shd={_regs[ShadowPriority1]:X2}/{_regs[ShadowPriority2]:X2}/{_regs[ShadowPriority3]:X2}:{_regs[ShadowOn]:X2}/{_regs[ShadowPrioritySelect]:X2} bri={_regs[VramBrightness]:X2}/{_regs[ObjBrightness]:X2}/{_regs[ObjBrightnessOn]:X2}";
    }

    private sealed class K054338
    {
        private readonly ushort[] _regs = new ushort[16];
        private int _writes;

        public void Reset()
        {
            Array.Clear(_regs);
            _writes = 0;
        }

        public void WriteWord(int offset, ushort data)
        {
            _regs[offset & 0x0f] = data;
            _writes++;
        }

        public void WriteByte(int byteOffset, byte data)
        {
            int offset = (byteOffset >> 1) & 0x0f;
            ushort old = _regs[offset];
            _regs[offset] = (byteOffset & 1) == 0
                ? (ushort)((old & 0x00ff) | (data << 8))
                : (ushort)((old & 0xff00) | data);
            _writes++;
        }

        public ushort ReadRegister(int offset) => _regs[offset & 0x0f];

        public void FillSolidBackground(byte[] frameBuffer)
        {
            byte r = (byte)(_regs[0] & 0xff);
            byte g = (byte)((_regs[1] >> 8) & 0xff);
            byte b = (byte)(_regs[1] & 0xff);
            for (int offset = 0; offset < frameBuffer.Length; offset += 4)
            {
                frameBuffer[offset] = b;
                frameBuffer[offset + 1] = g;
                frameBuffer[offset + 2] = r;
                frameBuffer[offset + 3] = 0xff;
            }
        }

        public int TilemapAlphaLevel(int pblend)
            => NormalizeTilemapAlpha(TilemapAlphaLevelRaw(pblend), invertAdditiveZero: false);

        public int ExternalTilemapAlphaLevel(int pblend)
            => NormalizeTilemapAlpha(TilemapAlphaLevelRaw(pblend), invertAdditiveZero: true);

        private int TilemapAlphaLevelRaw(int pblend)
        {
            if (pblend == 0)
                return 255;

            pblend &= 3;
            int register = 13 + ((pblend >> 1) & 1);
            int shift = ((~pblend << 3) & 8);
            int mixset = (_regs[register] >> shift) & 0xff;
            int mixLevel = mixset & 0x1f;
            int alpha = (mixLevel << 3) | (mixLevel >> 2);
            if ((mixset & 0x20) != 0)
                alpha |= 0x100;
            if ((_regs[0] & 0x20) != 0)
                alpha |= 0x200;
            return alpha;
        }

        private static int NormalizeTilemapAlpha(int rawAlpha, bool invertAdditiveZero)
        {
            int alpha = rawAlpha & 0x1ff;
            if ((alpha & 0x100) != 0)
            {
                alpha &= 0xff;
                if (invertAdditiveZero || alpha != 0)
                    alpha = (~alpha) & 0xff;
            }
            return Math.Clamp(alpha & 0xff, 0, 255);
        }

        public int BrightnessLevel(int mode)
        {
            return (mode & 3) switch
            {
                1 => _regs[11] & 0xff,
                2 => (_regs[12] >> 8) & 0xff,
                3 => _regs[12] & 0xff,
                _ => 255
            };
        }

        public bool ShadowEnabled(int mode)
        {
            mode = Math.Clamp(mode, 0, 2);
            int baseRegister = 2 + mode * 3;
            return IsActiveShadowComponent(_regs[baseRegister])
                   || IsActiveShadowComponent(_regs[baseRegister + 1])
                   || IsActiveShadowComponent(_regs[baseRegister + 2]);
        }

        public void ApplyShadow(byte[] frameBuffer, int x, int y, int mode)
        {
            mode = Math.Clamp(mode, 0, 2);
            int baseRegister = 2 + mode * 3;
            int offset = y * FrameStride + x * 4;
            int r = QuantizeRgb15(frameBuffer[offset + 2]) + DecodeShadowDelta(_regs[baseRegister]);
            int g = QuantizeRgb15(frameBuffer[offset + 1]) + DecodeShadowDelta(_regs[baseRegister + 1]);
            int b = QuantizeRgb15(frameBuffer[offset]) + DecodeShadowDelta(_regs[baseRegister + 2]);
            bool noClip = (_regs[15] & 0x20) != 0;
            if (!noClip)
            {
                r = Math.Clamp(r, 0, 255);
                g = Math.Clamp(g, 0, 255);
                b = Math.Clamp(b, 0, 255);
            }

            frameBuffer[offset] = (byte)Math.Clamp(b, 0, 255);
            frameBuffer[offset + 1] = (byte)Math.Clamp(g, 0, 255);
            frameBuffer[offset + 2] = (byte)Math.Clamp(r, 0, 255);
            frameBuffer[offset + 3] = 0xff;
        }

        private static bool IsActiveShadowComponent(ushort value)
        {
            int delta = DecodeShadowDelta(value);
            return delta < -7 || delta > 7;
        }

        private static int DecodeShadowDelta(ushort value)
        {
            int delta = value & 0x1ff;
            return delta >= 0x100 ? delta - 0x200 : delta;
        }

        private static int QuantizeRgb15(byte value)
        {
            int fiveBit = value >> 3;
            return (fiveBit << 3) | (fiveBit >> 2);
        }

        public string DebugSummary()
            => $"w={_writes} bg={_regs[0]:X4}/{_regs[1]:X4} ctl={_regs[15]:X4} pblend={_regs[13]:X4}/{_regs[14]:X4}";
    }

    private sealed class K053252
    {
        private readonly byte[] _regs = new byte[16];
        private int _hc;
        private int _hfp;
        private int _hbp;
        private int _vc;
        private int _vfp;
        private int _vbp;
        private int _vsw;
        private int _hsw;
        private int _vpos;
        private int _writes;
        private int _int1Acks;
        private int _int2Acks;

        public void Reset()
        {
            Array.Clear(_regs);
            _regs[0x08] = 1;
            _hc = 0;
            _hfp = 0;
            _hbp = 0;
            _vc = 0;
            _vfp = 0;
            _vbp = 0;
            _vsw = 0;
            _hsw = 0;
            _vpos = 0;
            _writes = 0;
            _int1Acks = 0;
            _int2Acks = 0;
        }

        public void SetVpos(int vpos) => _vpos = vpos;

        public byte Read(int offset)
        {
            offset &= 0x0f;
            int vct = _vpos - _vc;
            return offset switch
            {
                0x0e => (byte)((vct >> 8) & 1),
                0x0f => (byte)(vct & 0xff),
                _ => _regs[offset]
            };
        }

        public void Write(int offset, byte data)
        {
            offset &= 0x0f;
            _regs[offset] = data;
            _writes++;

            switch (offset)
            {
                case 0x00:
                case 0x01:
                    _hc = (((_regs[0] << 8) | _regs[1]) & 0x03ff) + 1;
                    break;
                case 0x02:
                case 0x03:
                    _hfp = ((_regs[2] << 8) | _regs[3]) & 0x01ff;
                    break;
                case 0x04:
                case 0x05:
                    _hbp = ((_regs[4] << 8) | _regs[5]) & 0x01ff;
                    break;
                case 0x08:
                case 0x09:
                    _vc = (((_regs[8] << 8) | _regs[9]) & 0x01ff) + 1;
                    break;
                case 0x0a:
                    _vfp = data;
                    break;
                case 0x0b:
                    _vbp = data + 1;
                    break;
                case 0x0c:
                    _vsw = ((data & 0xf0) >> 4) + 1;
                    _hsw = (data & 0x0f) + 1;
                    break;
                case 0x0e:
                    _int1Acks++;
                    break;
                case 0x0f:
                    _int2Acks++;
                    break;
            }
        }

        public string DebugSummary()
            => $"w={_writes} hc={_hc}/{_hfp}/{_hbp}/{_hsw} vc={_vc}/{_vfp}/{_vbp}/{_vsw} irq={_regs[6]:X2}/{_regs[7]:X2} ack={_int1Acks}/{_int2Acks}";
    }

        private sealed class K056832
        {
        public const int TileCategoryAll = -1;
        public const int TileCategoryNormal = 0;
        public const int TileCategoryMixed = 1;
        private const int PageCount = 16;
        private const int PageWords = 0x1000;
        private readonly ushort[] _vram = new ushort[PageCount * PageWords];
        private readonly ushort[] _regs = new ushort[0x20];
        private readonly ushort[] _boardRegs = new ushort[4];
        [NonSerialized] private readonly byte[] _rom = new byte[0x500000];
        [NonSerialized] private readonly byte[] _decodedRom = new byte[0x500000];
        [NonSerialized] private readonly byte[] _decodedTilePens = new byte[0x20000 * 8 * 8];
        private static readonly int[] MystwarrTileFlipShifts = { 6, 4, 2, 0 };
        private static readonly int[] MystwarrTilePaletteMask1 = { 0x3f, 0x0f, 0x03, 0x00 };
        private static readonly int[] MystwarrTilePaletteShift2 = { 0, 2, 2, 2 };
        private static readonly int[] MystwarrTilePaletteMask2 = { 0x00, 0x30, 0x3c, 0x3f };
        private static readonly int[] MystwarrTilePlaneOffsets = { 32, 24, 8, 16, 0 };
        private int _selectedPage;
        private int _selectedPageBase;
        private int _romBank;
        private int _romReads;
        private int _lastRomReadOffset;
        private ushort _lastRomReadValue;
        [NonSerialized] private int _lastAlphaTileMixCode;
        [NonSerialized] private bool _metamrphTileCallback;
        [NonSerialized] private bool _moomesaTileCallback;
        [NonSerialized] private bool _bpp4TileDecode;

        public int[] LayerColorBase { get; } = new int[4];
        public int LastAlphaTileMixCode => _lastAlphaTileMixCode;
        public bool Bpp4TileDecode
        {
            get => _bpp4TileDecode;
            set => _bpp4TileDecode = value;
        }

        public bool MetamrphTileCallback
        {
            get => _metamrphTileCallback;
            set => _metamrphTileCallback = value;
        }

        public bool MoomesaTileCallback
        {
            get => _moomesaTileCallback;
            set => _moomesaTileCallback = value;
        }

        public void Load(byte[] rom)
        {
            Array.Clear(_rom);
            Array.Clear(_decodedRom);
            Array.Clear(_decodedTilePens);
            Array.Copy(rom, _rom, Math.Min(rom.Length, _rom.Length));
            DecodeMystwarrTiles();
            DecodeMystwarrTilePens();
        }

        public void Reset()
        {
            Array.Clear(_vram);
            Array.Clear(_regs);
            Array.Clear(_boardRegs);
            Array.Clear(LayerColorBase);
            _selectedPage = 0;
            _selectedPageBase = 0;
            _romBank = 0;
            _romReads = 0;
            _lastRomReadOffset = 0;
            _lastRomReadValue = 0;
            _lastAlphaTileMixCode = 0;
            _metamrphTileCallback = false;
            _moomesaTileCallback = false;
            _bpp4TileDecode = false;
        }

        public ushort ReadRamWord(int offset)
        {
            offset &= PageWords - 1;
            return _vram[(_selectedPageBase + offset) & (_vram.Length - 1)];
        }

        public void WriteRamWord(int offset, ushort value)
        {
            offset &= PageWords - 1;
            _vram[(_selectedPageBase + offset) & (_vram.Length - 1)] = value;
        }

        public ushort ReadRomWord(int offset)
        {
            int bank = 10240 * _romBank;
            int addr;
            ushort value;
            if ((_boardRegs[2] & 0x8) != 0)
            {
                int bit = offset & 3;
                int temp = _rom[((offset / 4) * 5 + 4 + bank) % _rom.Length];
                value = bit switch
                {
                    0 => (ushort)(((temp & 0x80) << 5) | ((temp & 0x40) >> 2)),
                    1 => (ushort)(((temp & 0x20) << 7) | (temp & 0x10)),
                    2 => (ushort)(((temp & 0x08) << 9) | ((temp & 0x04) << 2)),
                    _ => (ushort)(((temp & 0x02) << 11) | ((temp & 0x01) << 4))
                };
                CountRomRead(offset, value);
                return value;
            }

            addr = ((offset >> 1) * 5) + ((offset & 1) != 0 ? 2 : 0) + bank;
            addr %= _rom.Length;
            value = (ushort)((_rom[addr] << 8) | _rom[(addr + 1) % _rom.Length]);
            CountRomRead(offset, value);
            return value;
        }

        public string DebugSummary()
        {
            int nonzero = 0;
            int first = -1;
            for (int i = 0; i < _vram.Length; i++)
            {
                if (_vram[i] == 0)
                    continue;
                nonzero++;
                if (first < 0)
                    first = i;
            }
            return $"romR={_romReads} last={_lastRomReadOffset:X4}:{_lastRomReadValue:X4} bank={_romBank} page={_selectedPage} "
                   + $"regs={_regs[0]:X4}/{_regs[1]:X4}/{_regs[3]:X4}/{_regs[4]:X4}/{_regs[5]:X4}/{_regs[0x19]:X4}/{_regs[0x1A]:X4}/{_regs[0x1B]:X4} "
                   + $"l2={_regs[0x0A]:X4}/{_regs[0x0E]:X4}/{_regs[0x12]:X4}/{_regs[0x16]:X4} "
                   + $"layout={LayerLayoutSummary(0)}|{LayerLayoutSummary(1)}|{LayerLayoutSummary(2)}|{LayerLayoutSummary(3)} "
                   + $"p1={_vram[1 * PageWords]:X4}:{_vram[1 * PageWords + 1]:X4} p5={_vram[5 * PageWords]:X4}:{_vram[5 * PageWords + 1]:X4} p8={_vram[8 * PageWords]:X4}:{_vram[8 * PageWords + 1]:X4} "
                   + $"vram={nonzero}:{first:X5}";
        }

        private string LayerLayoutSummary(int layer)
        {
            int y = (_regs[0x08 + layer] >> 3) & 3;
            int h = _regs[0x08 + layer] & 3;
            int x = (_regs[0x0c + layer] >> 3) & 3;
            int w = _regs[0x0c + layer] & 3;
            return $"{layer}:{x},{y},{w + 1}x{h + 1},dx={(short)_regs[0x14 + layer]},dy={(short)_regs[0x10 + layer]}";
        }

        private void CountRomRead(int offset, ushort value)
        {
            _romReads++;
            _lastRomReadOffset = offset;
            _lastRomReadValue = value;
        }

        public void WriteRegWord(int offset, ushort value)
        {
            offset &= 0x1f;
            _regs[offset] = value;
            if (offset == 0x19)
                ChangeRamBank();
            else if (offset == 0x1a || offset == 0x1b)
                ChangeRomBank();
        }

        public void WriteRegByte(int byteOffset, byte value)
        {
            int offset = (byteOffset >> 1) & 0x1f;
            ushort old = _regs[offset];
            ushort data = (byteOffset & 1) == 0
                ? (ushort)((old & 0x00ff) | (value << 8))
                : (ushort)((old & 0xff00) | value);
            WriteRegWord(offset, data);
        }

        public void WriteBoardRegWord(int offset, ushort value)
        {
            _boardRegs[offset & 3] = value;
        }

        public void WriteBoardRegByte(int byteOffset, byte value)
        {
            int offset = (byteOffset >> 1) & 3;
            ushort old = _boardRegs[offset];
            _boardRegs[offset] = (byteOffset & 1) == 0
                ? (ushort)((old & 0x00ff) | (value << 8))
                : (ushort)((old & 0xff00) | value);
        }

        public void RenderLayer(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int layer, bool opaque)
            => RenderLayer(frameBuffer, palette, layer, opaque, default);

        public void RenderLayer(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int layer, bool opaque, ReadOnlySpan<int> mixAlphas)
            => RenderLayer(frameBuffer, palette, layer, opaque, mixAlphas, TileCategoryAll);

        public void RenderLayer(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int layer, bool opaque, ReadOnlySpan<int> mixAlphas, int tileCategory)
            => RenderLayer(frameBuffer, palette, layer, opaque, mixAlphas, tileCategory, 255);

        public void RenderLayer(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int layer, bool opaque, ReadOnlySpan<int> mixAlphas, int tileCategory, int brightness)
        {
            brightness = Math.Clamp(brightness, 0, 255);
            RenderLayerMameSource(frameBuffer, palette, layer, opaque, mixAlphas, tileCategory, brightness);
        }

        private void RenderLayerMameSource(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int layer, bool opaque, ReadOnlySpan<int> mixAlphas, int tileCategory, int brightness)
        {
            layer &= 3;
            if ((_regs[4] & (1 << layer)) == 0)
                return;

            int scrollMode = (_regs[5] >> ((layer & 3) << 1)) & 3;
            bool flipX = (_regs[0] & 0x10) != 0;
            bool flipY = (_regs[0] & 0x20) != 0;
            int rowStart = (_regs[0x08 + layer] >> 3) & 3;
            int rowSpan = (_regs[0x08 + layer] & 3) + 1;
            int colStart = (_regs[0x0c + layer] >> 3) & 3;
            int colSpan = (_regs[0x0c + layer] & 3) + 1;
            int mapWidth = colSpan * 512;
            int mapHeight = rowSpan * 256;
            int rawDx = (short)_regs[0x14 + layer];
            int rawDy = (short)_regs[0x10 + layer];
            int ay = PositiveMod(rawDy - LayerOffsetY(layer), mapHeight);
            int pageColorBase = LayerColorBase[layer];
            Span<bool> pageBelongsToLayer = stackalloc bool[16];
            for (int page = 0; page < pageBelongsToLayer.Length; page++)
                pageBelongsToLayer[page] = AssociatedLayerForPage(page) == layer;

            for (int py = 0; py < FrameHeight; py++)
            {
                int screenY = flipY ? FrameHeight - 1 - py + VisibleOriginY : py + VisibleOriginY;
                int sourceYAbsolute = screenY + ay;
                int sourceY = sourceYAbsolute;
                if (sourceY >= mapHeight)
                    sourceY %= mapHeight;
                int pageRow = sourceY >> 8;
                int localY = sourceY & 0xff;
                int tileY = localY >> 3;
                int pixelY = localY & 7;
                int scrollX = ScrollXForSourceLine(layer, scrollMode, rawDx, rawDy + screenY) - LayerOffsetX(layer);
                int sourceX = PositiveMod((flipX ? FrameWidth - 1 + 24 : 24) + scrollX, mapWidth);

                for (int px = 0; px < FrameWidth;)
                {
                    int pageCol = sourceX >> 9;
                    int page = (((rowStart + pageRow) & 3) << 2) | ((colStart + pageCol) & 3);
                    int localX = sourceX & 0x1ff;
                    int pixelX = localX & 7;
                    int run = Math.Min(FrameWidth - px, flipX ? pixelX + 1 : 8 - pixelX);
                    run = Math.Min(run, flipX ? sourceX + 1 : mapWidth - sourceX);

                    if (!pageBelongsToLayer[page])
                    {
                        AdvanceSourceRun(ref px, ref sourceX, run, mapWidth, flipX);
                        continue;
                    }

                    int pageBase = page * PageWords;
                    if (PageUsesTileMode(page))
                        DrawTileSourceRun(frameBuffer, palette, pageBase, layer, pageColorBase, px, py, run, localX, tileY, pixelX, pixelY, flipX, opaque, mixAlphas, tileCategory, brightness);
                    else
                        DrawLineSourceRun(frameBuffer, palette, pageBase, layer, pageColorBase, px, py, run, localX, localY, flipX, opaque, mixAlphas, tileCategory, brightness);

                    AdvanceSourceRun(ref px, ref sourceX, run, mapWidth, flipX);
                }
            }
        }

        private int ScrollXForSourceLine(int layer, int scrollMode, int rawDx, int sourceY)
        {
            return rawDx;
        }

        private static void AdvanceSourceRun(ref int px, ref int sourceX, int run, int mapWidth, bool flipX)
        {
            px += run;
            sourceX += flipX ? -run : run;
            sourceX = PositiveMod(sourceX, mapWidth);
        }

        private void DrawTileSourceRun(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int pageBase, int layer, int colorBase, int px, int py, int run,
            int localX, int tileY, int pixelX, int pixelY, bool screenFlipX, bool opaque, ReadOnlySpan<int> mixAlphas, int tileCategory, int brightness)
        {
            int tileX = localX >> 3;
            int tileIndex = (tileY * 64 + tileX) & 0x7ff;
            int attr = _vram[pageBase + tileIndex * 2];
            int code = _vram[pageBase + tileIndex * 2 + 1];
            DecodeMystwarrTileAttr(layer, attr, colorBase, out int color, out bool tileFlipX, out bool tileFlipY, out int mixCode);
            if (!TileCategoryMatches(mixCode, tileCategory))
                return;
            int alpha = TileAlphaForMixCode(mixAlphas, mixCode);
            if (alpha <= 0)
                return;

            int srcY = tileFlipY ? 7 - pixelY : pixelY;
            int tileCode = code & 0x1ffff;
            for (int i = 0; i < run; i++)
            {
                int runPixelX = screenFlipX ? pixelX - i : pixelX + i;
                int srcX = tileFlipX ? 7 - runPixelX : runPixelX;
                int pen = Decode5BppPixel(tileCode, srcX, srcY);
                if (pen == 0 && !opaque)
                    continue;

                WriteTilePixel(frameBuffer, px + i, py, palette[MystwarrTilePaletteIndex(color, pen)], alpha, brightness);
            }
        }

        private void DrawLineSourceRun(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int pageBase, int layer, int colorBase, int px, int py, int run,
            int localX, int localY, bool screenFlipX, bool opaque, ReadOnlySpan<int> mixAlphas, int tileCategory, int brightness)
        {
            int attr = _vram[pageBase + localY * 2];
            int code = _vram[pageBase + localY * 2 + 1] & ~7;
            DecodeMystwarrTileAttr(layer, attr, colorBase, out int color, out _, out _, out int mixCode);
            if (!TileCategoryMatches(mixCode, tileCategory))
                return;
            int alpha = TileAlphaForMixCode(mixAlphas, mixCode);
            if (alpha <= 0)
                return;

            for (int i = 0; i < run; i++)
            {
                int x = screenFlipX ? localX - i : localX + i;
                int src = x & 0x3f;
                int pen = Decode5BppPixel(code + ((x >> 6) & 7), src & 7, src >> 3);
                if (pen == 0 && !opaque)
                    continue;
                WriteTilePixel(frameBuffer, px + i, py, palette[MystwarrTilePaletteIndex(color, pen)], alpha, brightness);
            }
        }

        private void RenderLayerMameXy(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int layer, bool opaque, ReadOnlySpan<int> mixAlphas, int tileCategory, int brightness)
        {
            layer &= 3;
            if ((_regs[4] & (1 << layer)) == 0)
                return;

            int rowStart = (_regs[0x08 + layer] >> 3) & 3;
            int rowSpan = (_regs[0x08 + layer] & 3) + 1;
            int colStart = (_regs[0x0c + layer] >> 3) & 3;
            int colSpan = (_regs[0x0c + layer] & 3) + 1;
            int mapWidth = colSpan * 512;
            int mapHeight = rowSpan * 256;
            int dx = ((int)_regs[0x14 + layer] + LayerOffsetX(layer)) & 0xffff;
            int dy = -(short)_regs[0x10 + layer] - LayerOffsetY(layer);
            int ay = PositiveMod(dy, mapHeight);
            int pageColorBase = LayerColorBase[layer];
            Span<bool> pageBelongsToLayer = stackalloc bool[16];
            for (int page = 0; page < pageBelongsToLayer.Length; page++)
                pageBelongsToLayer[page] = AssociatedLayerForPage(page) == layer;

            for (int py = 0; py < FrameHeight; py++)
            {
                int sourceY = py + VisibleOriginY + ay;
                if (sourceY >= mapHeight)
                    sourceY %= mapHeight;
                int pageRow = sourceY >> 8;
                int localY = sourceY & 0xff;
                int tileY = localY >> 3;
                int pixelY = localY & 7;

                int sourceX = PositiveMod(24 + dx, mapWidth);
                for (int px = 0; px < FrameWidth;)
                {
                    int pageCol = sourceX >> 9;
                    int page = (((rowStart + pageRow) & 3) << 2) | ((colStart + pageCol) & 3);
                    int localX = sourceX & 0x1ff;
                    int pixelX = localX & 7;
                    int run = Math.Min(FrameWidth - px, 8 - pixelX);
                    run = Math.Min(run, mapWidth - sourceX);

                    if (!pageBelongsToLayer[page])
                    {
                        px += run;
                        sourceX += run;
                        if (sourceX >= mapWidth)
                            sourceX = 0;
                        continue;
                    }

                    int tileX = localX >> 3;
                    int pageBase = page * PageWords;
                    int tileIndex = (tileY * 64 + tileX) & 0x7ff;
                    int attr = _vram[pageBase + tileIndex * 2];
                    int code = _vram[pageBase + tileIndex * 2 + 1];
                    DecodeMystwarrTileAttr(layer, attr, pageColorBase, out int color, out bool tileFlipX, out bool tileFlipY, out int mixCode);
                    if (!TileCategoryMatches(mixCode, tileCategory))
                    {
                        px += run;
                        sourceX += run;
                        if (sourceX >= mapWidth)
                            sourceX = 0;
                        continue;
                    }
                    int alpha = TileAlphaForMixCode(mixAlphas, mixCode);
                    if (alpha <= 0)
                    {
                        px += run;
                        sourceX += run;
                        if (sourceX >= mapWidth)
                            sourceX = 0;
                        continue;
                    }

                    int srcY = tileFlipY ? 7 - pixelY : pixelY;
                    int tileCode = code & 0x1ffff;
                    for (int i = 0; i < run; i++)
                    {
                        int srcX = tileFlipX ? 7 - (pixelX + i) : pixelX + i;
                        int pen = Decode5BppPixel(tileCode, srcX, srcY);
                        if (pen == 0 && !opaque)
                            continue;

                        WriteTilePixel(frameBuffer, px + i, py, palette[MystwarrTilePaletteIndex(color, pen)], alpha, brightness);
                    }

                    px += run;
                    sourceX += run;
                    if (sourceX >= mapWidth)
                        sourceX = 0;
                }
            }
        }

        private void DrawPage(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int pageBase, int layer, int colorBase, int pageX, int pageY, bool screenFlipX, bool screenFlipY, bool opaque, ReadOnlySpan<int> mixAlphas, int tileCategory, int brightness)
        {
            if (pageX <= -512 || pageX >= FrameWidth || pageY <= -256 || pageY >= FrameHeight)
                return;

            int page = (pageBase / PageWords) & 0x0f;
            int pageLayer = AssociatedLayerForPage(page);
            if (pageLayer < 0 || pageLayer != (layer & 3))
                return;

            int pageColorBase = LayerColorBase[pageLayer];
            if (!PageUsesTileMode(page))
            {
                DrawLinePage(frameBuffer, palette, pageBase, pageLayer, pageColorBase, pageX, pageY, screenFlipX, screenFlipY, opaque, mixAlphas, tileCategory, brightness);
                return;
            }

            for (int ty = 0; ty < 32; ty++)
            {
                int sy = screenFlipY ? pageY + (31 - ty) * 8 : pageY + ty * 8;
                if (sy <= -8 || sy >= FrameHeight)
                    continue;

                for (int tx = 0; tx < 64; tx++)
                {
                    int sx = screenFlipX ? pageX + (63 - tx) * 8 : pageX + tx * 8;
                    if (sx <= -8 || sx >= FrameWidth)
                        continue;

                    int tileIndex = (ty * 64 + tx) & 0x7ff;
                    int attr = _vram[pageBase + tileIndex * 2];
                    int code = _vram[pageBase + tileIndex * 2 + 1];
                    DecodeMystwarrTileAttr(pageLayer, attr, pageColorBase, out int color, out bool flipX, out bool flipY, out int mixCode);
                    if (!TileCategoryMatches(mixCode, tileCategory))
                        continue;
                    int alpha = TileAlphaForMixCode(mixAlphas, mixCode);
                    if (alpha > 0)
                        DrawTile(frameBuffer, palette, code, color, sx, sy, flipX ^ screenFlipX, flipY ^ screenFlipY, opaque, alpha, brightness);
                }
            }
        }

        private void DrawLinePage(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int pageBase, int layer, int colorBase, int pageX, int pageY, bool screenFlipX, bool screenFlipY, bool opaque, ReadOnlySpan<int> mixAlphas, int tileCategory, int brightness)
        {
            for (int line = 0; line < 256; line++)
            {
                int py = pageY + (screenFlipY ? 255 - line : line);
                if ((uint)py >= FrameHeight)
                    continue;

                int attr = _vram[pageBase + line * 2];
                int code = _vram[pageBase + line * 2 + 1] & ~7;
                DecodeMystwarrTileAttr(layer, attr, colorBase, out int color, out _, out _, out int mixCode);
                if (!TileCategoryMatches(mixCode, tileCategory))
                    continue;
                int alpha = TileAlphaForMixCode(mixAlphas, mixCode);
                if (alpha <= 0)
                    continue;

                for (int x = 0; x < 512; x++)
                {
                    int px = pageX + (screenFlipX ? 511 - x : x);
                    if ((uint)px >= FrameWidth)
                        continue;

                    int src = x & 0x3f;
                    int pen = Decode5BppPixel(code + (x >> 6), src & 7, src >> 3);
                    if (pen == 0 && !opaque)
                        continue;
                    WriteTilePixel(frameBuffer, px, py, palette[MystwarrTilePaletteIndex(color, pen)], alpha, brightness);
                }
            }
        }

        private void DecodeMystwarrTileAttr(int layer, int attr, int colorBase, out int color, out bool flipX, out bool flipY, out int mixCode)
        {
            int fbits = (_regs[3] >> 6) & 3;
            int flip = (_regs[1] >> ((layer & 3) << 1)) & 3;
            flip &= (attr >> MystwarrTileFlipShifts[fbits]) & 3;

            int rawColor = (attr & MystwarrTilePaletteMask1[fbits]) | ((attr >> MystwarrTilePaletteShift2[fbits]) & MystwarrTilePaletteMask2[fbits]);
            color = colorBase | ((_metamrphTileCallback || _moomesaTileCallback) ? ((rawColor >> 2) & 0x0f) : ((rawColor >> 1) & 0x0f));
            flipX = (flip & 1) != 0;
            flipY = (flip & 2) != 0;
            mixCode = _metamrphTileCallback ? attr & 3 : (attr >> 2) & 3;
            if (mixCode != 0)
                _lastAlphaTileMixCode = mixCode;
        }

        private static int TileAlphaForMixCode(ReadOnlySpan<int> mixAlphas, int mixCode)
            => mixAlphas.Length > mixCode ? mixAlphas[mixCode] : 255;

        private static bool TileCategoryMatches(int mixCode, int tileCategory)
            => tileCategory == TileCategoryAll
               || (tileCategory == TileCategoryNormal && mixCode == 0)
               || (tileCategory == TileCategoryMixed && mixCode != 0);

        private int LayerOffsetX(int layer)
        {
            if (_metamrphTileCallback)
            {
                return layer switch
                {
                    0 => 2,
                    1 => 4,
                    2 => 6,
                    _ => 7
                };
            }

            return layer switch
        {
            0 => -5,
            1 => -3,
            2 => -1,
            _ => 0
        };
        }

        private int LayerOffsetY(int layer) => 0;

        private int VisibleOriginY => _metamrphTileCallback ? 15 : 16;

        private bool PageUsesTileMode(int page)
        {
            int layer = AssociatedLayerForPage(page);
            return layer < 0 || (_regs[4] & (1 << layer)) != 0;
        }

        private int AssociatedLayerForPage(int page)
        {
            if (!LayerAssociationEnabled())
                return ActiveLayerForUnassociatedPages();

            int result = -1;
            for (int layer = 0; layer < 4; layer++)
            {
                int rowStart = (_regs[0x08 + layer] >> 3) & 3;
                int rowSpan = (_regs[0x08 + layer] & 3) + 1;
                int colStart = (_regs[0x0c + layer] >> 3) & 3;
                int colSpan = (_regs[0x0c + layer] & 3) + 1;
                for (int row = 0; row < rowSpan; row++)
                {
                    for (int col = 0; col < colSpan; col++)
                    {
                        int candidate = (((rowStart + row) & 3) << 2) | ((colStart + col) & 3);
                        if (candidate == page)
                            result = layer;
                    }
                }
            }
            return result;
        }

        private bool LayerAssociationEnabled()
        {
            for (int layer = 0; layer < 4; layer++)
            {
                if (((_regs[0x08 + layer] & 3) == 3)
                    && ((_regs[0x0c + layer] & 3) == 3)
                    && (((_regs[0x08 + layer] >> 3) & 3) == 0)
                    && (((_regs[0x0c + layer] >> 3) & 3) == 0))
                {
                    return false;
                }
            }
            return true;
        }

        private int ActiveLayerForUnassociatedPages()
        {
            for (int layer = 3; layer >= 0; layer--)
            {
                if ((_regs[0x08 + layer] | _regs[0x0c + layer]) != 0)
                    return layer;
            }
            return 0;
        }

        private static int PositiveMod(int value, int divisor)
        {
            if (divisor <= 0)
                return 0;
            int result = value % divisor;
            return result < 0 ? result + divisor : result;
        }

        private void DrawTile(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int code, int color, int sx, int sy, bool flipX, bool flipY, bool opaque, int alpha, int brightness)
        {
            int tileCode = code & 0x1ffff;
            for (int y = 0; y < 8; y++)
            {
                int py = sy + y;
                if ((uint)py >= FrameHeight)
                    continue;
                int srcY = flipY ? 7 - y : y;
                for (int x = 0; x < 8; x++)
                {
                    int px = sx + x;
                    if ((uint)px >= FrameWidth)
                        continue;
                    int srcX = flipX ? 7 - x : x;
                    int pen = Decode5BppPixel(tileCode, srcX, srcY);
                    if (pen == 0 && !opaque)
                        continue;
                    WriteTilePixel(frameBuffer, px, py, palette[MystwarrTilePaletteIndex(color, pen)], alpha, brightness);
                }
            }
        }

        private static int MystwarrTilePaletteIndex(int color, int pen)
            => (color * 16 + pen) & 0x7ff;

        private static void WriteTilePixel(byte[] frameBuffer, int x, int y, ushort xBgr555, int alpha)
            => WriteTilePixel(frameBuffer, x, y, xBgr555, alpha, 255);

        private static void WriteTilePixel(byte[] frameBuffer, int x, int y, ushort xBgr555, int alpha, int brightness)
        {
            int offset = y * FrameStride + x * 4;
            if (alpha >= 255 && brightness >= 255)
            {
                frameBuffer[offset] = Color5To8[(xBgr555 >> 10) & 0x1f];
                frameBuffer[offset + 1] = Color5To8[(xBgr555 >> 5) & 0x1f];
                frameBuffer[offset + 2] = Color5To8[xBgr555 & 0x1f];
                frameBuffer[offset + 3] = 0xff;
                return;
            }

            int r = Color5To8[xBgr555 & 0x1f];
            int g = Color5To8[(xBgr555 >> 5) & 0x1f];
            int b = Color5To8[(xBgr555 >> 10) & 0x1f];
            if (brightness < 255)
            {
                r = (r * brightness + 127) / 255;
                g = (g * brightness + 127) / 255;
                b = (b * brightness + 127) / 255;
            }

            if (alpha >= 255)
            {
                frameBuffer[offset] = (byte)b;
                frameBuffer[offset + 1] = (byte)g;
                frameBuffer[offset + 2] = (byte)r;
                frameBuffer[offset + 3] = 0xff;
                return;
            }

            int inv = 255 - alpha;
            frameBuffer[offset] = (byte)((b * alpha + frameBuffer[offset] * inv + 127) / 255);
            frameBuffer[offset + 1] = (byte)((g * alpha + frameBuffer[offset + 1] * inv + 127) / 255);
            frameBuffer[offset + 2] = (byte)((r * alpha + frameBuffer[offset + 2] * inv + 127) / 255);
            frameBuffer[offset + 3] = 0xff;
        }

        private int Decode5BppPixel(int code, int x, int y)
            => _bpp4TileDecode
                ? Decode4BppPixel(code, x, y)
                : _decodedTilePens[((code & 0x1ffff) << 6) | ((y & 7) << 3) | (x & 7)];

        private int Decode4BppPixel(int code, int x, int y)
        {
            int[] xOffsets = { 8, 12, 0, 4, 24, 28, 16, 20 };
            int bitIndexBase = (code & 0x1ffff) * 256 + (y & 7) * 32 + xOffsets[x & 7];
            int pen = 0;
            for (int plane = 0; plane < 4; plane++)
            {
                int bitIndex = bitIndexBase + plane;
                int byteIndex = (bitIndex >> 3) % _rom.Length;
                int bit = 7 - (bitIndex & 7);
                pen |= ((_rom[byteIndex] >> bit) & 1) << (3 - plane);
            }
            return pen;
        }

        private int Decode5BppPixelFromDecodedRom(int code, int x, int y)
        {
            int bitIndexBase = code * 320 + y * 40 + x;
            int pen = 0;
            for (int plane = 0; plane < 5; plane++)
            {
                int bitIndex = bitIndexBase + MystwarrTilePlaneOffsets[plane];
                int byteIndex = (bitIndex >> 3) % _decodedRom.Length;
                int bit = 7 - (bitIndex & 7);
                pen |= ((_decodedRom[byteIndex] >> bit) & 1) << (4 - plane);
            }
            return pen;
        }

        private void DecodeMystwarrTilePens()
        {
            for (int code = 0; code < 0x20000; code++)
            {
                int decodedTileBase = code << 6;
                for (int y = 0; y < 8; y++)
                {
                    int decodedRowBase = decodedTileBase + (y << 3);
                    for (int x = 0; x < 8; x++)
                        _decodedTilePens[decodedRowBase + x] = (byte)Decode5BppPixelFromDecodedRom(code, x, y);
                }
            }
        }

        private void DecodeMystwarrTiles()
        {
            int src = 0;
            int dst = 0;
            int finish = Math.Min(_rom.Length, _decodedRom.Length) - 4;
            while (src < finish)
            {
                int s0 = _rom[src];
                int s1 = _rom[src + 1];
                int s2 = _rom[src + 2];
                int s3 = _rom[src + 3];
                int d0 = (s0 & 0x80) | ((s0 & 0x08) << 3) | ((s1 & 0x80) >> 2) | ((s1 & 0x08) << 1)
                    | ((s2 & 0x80) >> 4) | ((s2 & 0x08) >> 1) | ((s3 & 0x80) >> 6) | ((s3 & 0x08) >> 3);
                int d1 = ((s0 & 0x40) << 1) | ((s0 & 0x04) << 4) | ((s1 & 0x40) >> 1) | ((s1 & 0x04) << 2)
                    | ((s2 & 0x40) >> 3) | (s2 & 0x04) | ((s3 & 0x40) >> 5) | ((s3 & 0x04) >> 2);
                int d2 = ((s0 & 0x20) << 2) | ((s0 & 0x02) << 5) | (s1 & 0x20) | ((s1 & 0x02) << 3)
                    | ((s2 & 0x20) >> 2) | ((s2 & 0x02) << 1) | ((s3 & 0x20) >> 4) | ((s3 & 0x02) >> 1);
                int d3 = ((s0 & 0x10) << 3) | ((s0 & 0x01) << 6) | ((s1 & 0x10) << 1) | ((s1 & 0x01) << 4)
                    | ((s2 & 0x10) >> 1) | ((s2 & 0x01) << 2) | ((s3 & 0x10) >> 3) | (s3 & 0x01);

                _decodedRom[dst] = (byte)d3;
                _decodedRom[dst + 1] = (byte)d1;
                _decodedRom[dst + 2] = (byte)d2;
                _decodedRom[dst + 3] = (byte)d0;
                _decodedRom[dst + 4] = _rom[src + 4];
                src += 5;
                dst += 5;
            }
        }

        private void ChangeRamBank()
        {
            int bank = _regs[0x19];
            _selectedPage = (((bank >> 1) & 0x0c) | (bank & 3)) & 0x0f;
            _selectedPageBase = _selectedPage * PageWords;
        }

        private void ChangeRomBank()
        {
            int banks = Math.Max(1, _rom.Length / 10240);
            _romBank = (_regs[0x1a] | (_regs[0x1b] << 16)) % banks;
        }
    }

    private sealed class K053250
    {
        private const int OrientationSwapXy = 1;
        private const int OrientationFlipX = 2;
        private const int OrientationFlipY = 4;

        private readonly ushort[] _ram = new ushort[0x3000];
        private readonly ushort[] _regs = new ushort[8];
        private int _page;
        private int _frame;
        [NonSerialized] private readonly byte[] _rom = new byte[0x40000];

        public void Load(byte[] rom)
        {
            Array.Clear(_rom);
            Array.Copy(rom, _rom, Math.Min(rom.Length, _rom.Length));
        }

        public void Reset()
        {
            Array.Clear(_ram);
            Array.Clear(_regs);
            _page = 0;
            _frame = -1;
        }

        public ushort ReadRamWord(int offset) => _ram[offset & 0x1fff];

        public void WriteRamWord(int offset, ushort value) => _ram[offset & 0x1fff] = value;

        public ushort ReadRegWord(int offset) => _regs[offset & 7];

        public void WriteRegWord(int offset, ushort value)
        {
            offset &= 7;
            ushort data = (ushort)(value & 0xff);
            if (offset == 4 && (data & 2) == 0 && (_regs[4] & 2) != 0)
                Dma();
            _regs[offset] = data;
        }

        public ushort ReadRomWord(int offset)
        {
            int byteOffset = (0x80000 * (_regs[6] & 0xff) + 0x800 * (_regs[7] & 0xff) + (offset >> 1)) & (_rom.Length - 1);
            int value = _rom[byteOffset];
            return (ushort)((value << 8) | value);
        }

        public void Render(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int colorBase, int offX, int offY)
        {
            ReadOnlySpan<ushort> lineRam = _ram.AsSpan((_page & 1) == 0 ? 0x2000 : 0x2800, 0x800);
            int mapScrollX = (short)(((_regs[0] & 0xff) << 8) | (_regs[1] & 0xff)) - offX;
            int mapScrollY = (short)(((_regs[2] & 0xff) << 8) | (_regs[3] & 0xff)) - offY;
            int ctrl = _regs[4] & 0xff;
            int orientation = 0;
            int dstHeight = 512;
            int lineDataAdv = 4;
            bool wrap500 = false;

            if ((ctrl & 0x01) == 0)
                orientation |= OrientationSwapXy;
            if ((ctrl & 0x08) != 0)
                orientation |= OrientationFlipX;
            if ((ctrl & 0x10) != 0)
                orientation |= OrientationFlipY;

            int srcClipMask;
            int srcWrapMask;
            switch (ctrl >> 5)
            {
                case 0:
                    srcClipMask = srcWrapMask = 0xff;
                    dstHeight = 0x100;
                    break;
                case 1:
                    srcClipMask = srcWrapMask = 0x1ff;
                    break;
                case 4:
                    srcClipMask = srcWrapMask = 0xff;
                    wrap500 = true;
                    break;
                default:
                    srcClipMask = srcWrapMask = 0x3ff;
                    break;
            }

            if ((ctrl & 0x04) != 0)
                srcClipMask = 0;

            int lineStart;
            int lineEnd;
            int scrollCorr;
            int lineDataOffset;
            int dstWrapMask;
            int passes;
            int clipMinX = MysticVisibleOriginX;
            int clipMaxX = MysticVisibleOriginX + MysticVisibleWidth - 1;
            int clipMinY = 15;
            int clipMaxY = clipMinY + FrameHeight - 1;
            if ((orientation & OrientationSwapXy) == 0)
            {
                lineStart = clipMinY;
                lineEnd = clipMaxY;
                scrollCorr = mapScrollX;
                lineDataOffset = mapScrollY;
                if ((orientation & OrientationFlipX) != 0)
                    scrollCorr = -scrollCorr;
                if ((orientation & OrientationFlipY) != 0)
                {
                    lineDataAdv = -lineDataAdv;
                    lineDataOffset += clipMaxY;
                }
                dstWrapMask = ~0;
                passes = 1;
            }
            else
            {
                lineStart = clipMinX;
                lineEnd = clipMaxX;
                scrollCorr = mapScrollY;
                lineDataOffset = mapScrollX;
                if ((orientation & OrientationFlipY) != 0)
                {
                    scrollCorr = 0x100 - scrollCorr;
                    scrollCorr -= 2;
                    lineDataOffset -= 5;
                }
                if ((orientation & OrientationFlipX) != 0)
                {
                    lineDataAdv = -lineDataAdv;
                    lineDataOffset += clipMaxX;
                }
                dstWrapMask = srcClipMask != 0 ? dstHeight - 1 : ~0;
                passes = srcClipMask != 0 ? 2 : 1;
            }

            lineDataOffset = ((lineDataOffset * 4) & 0x7ff) + lineStart * lineDataAdv;
            for (int linePos = lineStart; linePos <= lineEnd; linePos++, lineDataOffset += lineDataAdv)
            {
                int packet = lineDataOffset & 0x7ff;
                int color = lineRam[packet];
                if (color == 0xffff)
                    continue;
                int pixelOffset = lineRam[(packet + 1) & 0x7ff];
                if ((color & 0xff) == 0 && pixelOffset == 0)
                    continue;

                int zoom = lineRam[(packet + 2) & 0x7ff];
                int scroll = (short)lineRam[(packet + 3) & 0x7ff];
                if (wrap500 && scroll >= 0x500)
                    scroll -= 0x800;
                scroll = (scroll + scrollCorr) & dstWrapMask;

                int paletteBase = (colorBase + ((color & 0x1f) << 4)) & 0x7ff;
                int sourceBase = (pixelOffset << 8) % (_rom.Length * 2);
                for (int pass = 0; pass < passes; pass++)
                {
                    DrawScanline(frameBuffer, palette, paletteBase, sourceBase, linePos, scroll, zoom,
                        srcClipMask, srcWrapMask, orientation, clipMinX, clipMaxX, clipMinY, clipMaxY);
                    scroll -= dstHeight;
                }
            }
        }

        private void DrawScanline(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int paletteBase, int sourceBase, int linePos,
            int scroll, int zoom, int clipMask, int wrapMask, int orientation, int clipMinX, int clipMaxX, int clipMinY, int clipMaxY)
        {
            const int precision = 16;
            const int half = 1 << (precision - 1);

            bool swap = (orientation & OrientationSwapXy) != 0;
            bool flip = swap ? (orientation & OrientationFlipY) != 0 : (orientation & OrientationFlipX) != 0;
            int dstMin = swap ? clipMinY : clipMinX;
            int dstMax = swap ? clipMaxY : clipMaxX;
            int dstStart;
            int dstLength;
            int srcFx;
            int srcFdx;

            if (clipMask != 0)
            {
                dstStart = -scroll;
                if (dstStart > dstMax)
                    return;
                dstLength = clipMask + 1;
                if (zoom != 0)
                    dstLength = (dstLength << 6) / zoom;
                int endPixel = dstStart + dstLength - 1;
                if (endPixel < dstMin)
                    return;
                int tail = endPixel - dstMax;
                if (tail > 0)
                    dstLength -= tail;
                if (dstLength <= 0)
                    return;
                srcFdx = zoom << (precision - 6);
                int head = dstMin - dstStart;
                if (head > 0)
                {
                    dstLength -= head;
                    dstStart = dstMin;
                    srcFx = head * srcFdx + half;
                }
                else
                {
                    srcFx = half;
                }

                if (flip)
                {
                    dstStart = dstMax + dstMin - dstStart - (dstLength - 1);
                    srcFx += (dstLength - 1) * srcFdx - 1;
                    srcFdx = -srcFdx;
                }
            }
            else
            {
                dstStart = dstMin;
                dstLength = dstMax - dstMin + 1;
                srcFdx = zoom << (precision - 6);
                if (!flip)
                    srcFx = (scroll + dstMin) * srcFdx + half;
                else
                {
                    srcFx = (scroll + dstMax) * srcFdx + half - 1;
                    srcFdx = -srcFdx;
                }
            }

            int srcMask = clipMask != 0 ? ~0 : wrapMask;
            for (int i = 0; i < dstLength; i++, srcFx += srcFdx)
            {
                int pen = ReadUnpackedPixel(sourceBase + (((srcFx >> precision) & srcMask) & (_rom.Length * 2 - 1)));
                if (pen == 0)
                    continue;
                int px = (swap ? linePos : dstStart + i) - clipMinX;
                int py = (swap ? dstStart + i : linePos) - clipMinY;
                if ((uint)px >= FrameWidth || (uint)py >= FrameHeight)
                    continue;
                TmntAdapter.WritePixel(frameBuffer, px, py, palette[(paletteBase + pen) & 0x7ff]);
            }
        }

        private int ReadUnpackedPixel(int offset)
        {
            int value = _rom[(offset >> 1) & (_rom.Length - 1)];
            return (offset & 1) == 0 ? value >> 4 : value & 0x0f;
        }

        private void Dma()
        {
            int destination = _page == 0 ? 0x2000 : 0x2800;
            Array.Copy(_ram, 0, _ram, destination, 0x800);
            _page ^= 1;
            _frame++;
        }

        public string DebugSummary()
            => $"k053250 r0={_regs[0]:X4} r1={_regs[1]:X4} r2={_regs[2]:X4} r3={_regs[3]:X4} r4={_regs[4]:X4} pg={_page} dma={_frame}";
    }

    private sealed class K053245
    {
        private const int SpriteCount = 256;
        private const int RamWords = 0x800;
        private const int CpuRamWords = 0x8000;
        private readonly ushort[] _cpuRam = new ushort[CpuRamWords];
        private readonly ushort[] _ram = new ushort[RamWords];
        private readonly ushort[] _buffer = new ushort[RamWords];
        private readonly byte[] _regs = new byte[0x10];
        private readonly ushort[] _objRegs = new ushort[8];
        [NonSerialized] private readonly byte[] _rom = new byte[0x800000];
        [NonSerialized] private readonly byte[] _mystwarrDecodedSprites = new byte[0x8000 * 16 * 16];
        [NonSerialized] private readonly byte[] _metamrphDecodedSprites = new byte[0x10000 * 16 * 16];
        private int _romMask = 0x3fffff; // Savestate compatibility only; ROM decode always uses full loaded region.
        private int _romBank;
        private int _controlRomReads;
        private int _lastControlRomAddress;
        [NonSerialized] private int _lastVisibleCandidates;
        [NonSerialized] private int _lastDrawnPixels;
        [NonSerialized] private int _lastMinX;
        [NonSerialized] private int _lastMinY;
        [NonSerialized] private int _lastMaxX;
        [NonSerialized] private int _lastMaxY;
        [NonSerialized] private bool _tmnt2CoordinateMode;
        [NonSerialized] private bool _mystwarrSpriteLayout;
        [NonSerialized] private bool _metamrphSpriteLayout;
        [NonSerialized] private readonly byte[] _mystwarrObjZBuffer = new byte[FrameWidth * FrameHeight];
        [NonSerialized] private readonly byte[] _mystwarrShadowZBuffer = new byte[FrameWidth * FrameHeight];
        [NonSerialized] private readonly byte[] _mystwarrShadowPriorityBuffer = new byte[FrameWidth * FrameHeight];

        public int SpriteColorBase { get; set; }
        public bool Tmnt2CoordinateMode
        {
            get => _tmnt2CoordinateMode;
            set => _tmnt2CoordinateMode = value;
        }

        public bool MystwarrSpriteLayout
        {
            get => _mystwarrSpriteLayout;
            set => _mystwarrSpriteLayout = value;
        }

        public bool MetamrphSpriteLayout
        {
            get => _metamrphSpriteLayout;
            set => _metamrphSpriteLayout = value;
        }

        public bool ObjectIrqEnabled => (_regs[5] & 0x10) != 0;

        public void Load(byte[] rom)
        {
            Array.Clear(_rom);
            Array.Copy(rom, _rom, Math.Min(rom.Length, _rom.Length));
            DecodeMystwarrSprites();
            DecodeMetamrphSprites();
        }

        public void Reset()
        {
            Array.Clear(_cpuRam);
            Array.Clear(_ram);
            Array.Clear(_buffer);
            Array.Clear(_regs);
            Array.Clear(_objRegs);
            _romBank = 0;
            _controlRomReads = 0;
            _lastControlRomAddress = 0;
            _lastVisibleCandidates = 0;
            _lastDrawnPixels = 0;
            _lastMinX = 0;
            _lastMinY = 0;
            _lastMaxX = 0;
            _lastMaxY = 0;
            SpriteColorBase = 0;
            _tmnt2CoordinateMode = false;
            _mystwarrSpriteLayout = false;
            _metamrphSpriteLayout = false;
        }

        public void BufferSprites() => Array.Copy(_ram, _buffer, _ram.Length);

        public ushort ReadScatteredWord(int offset)
        {
            offset &= CpuRamWords - 1;
            if ((offset & 0x0031) != 0)
                return _cpuRam[offset];

            return _ram[ScatterOffset(offset)];
        }

        public ushort ReadMystwarrScatteredWord(int offset)
        {
            offset &= CpuRamWords - 1;
            if ((offset & 0x0078) != 0)
                return _cpuRam[offset];

            return _ram[MystwarrScatterOffset(offset)];
        }

        public ushort ReadCpuRamWord(int offset)
        {
            offset &= CpuRamWords - 1;
            return _cpuRam[offset];
        }

        public ushort ReadHardwareWord(int offset)
        {
            offset &= RamWords - 1;
            return _ram[offset];
        }

        public byte ReadScatteredByte(int byteOffset)
        {
            ushort word = ReadScatteredWord(byteOffset >> 1);
            return (byteOffset & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        }

        public byte ReadMystwarrScatteredByte(int byteOffset)
        {
            ushort word = ReadMystwarrScatteredWord(byteOffset >> 1);
            return (byteOffset & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        }

        public byte ReadCpuRamByte(int byteOffset)
        {
            ushort word = ReadCpuRamWord(byteOffset >> 1);
            return (byteOffset & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        }

        public void WriteScatteredWord(int offset, ushort value)
        {
            offset &= CpuRamWords - 1;
            _cpuRam[offset] = value;

            if ((offset & 0x0031) == 0)
                _ram[ScatterOffset(offset)] = value;
        }

        public void WriteMystwarrScatteredWord(int offset, ushort value)
        {
            offset &= CpuRamWords - 1;
            _cpuRam[offset] = value;

            if ((offset & 0x0078) == 0)
                _ram[MystwarrScatterOffset(offset)] = value;
        }

        public void WriteHardwareWord(int offset, ushort value)
        {
            offset &= RamWords - 1;
            _ram[offset] = value;
        }

        public void WriteScatteredByte(int byteOffset, byte value)
        {
            int offset = byteOffset >> 1;
            ushort word = ReadScatteredWord(offset);
            word = (byteOffset & 1) == 0
                ? (ushort)((word & 0x00ff) | (value << 8))
                : (ushort)((word & 0xff00) | value);
            WriteScatteredWord(offset, word);
        }

        public byte ReadControl(int offset)
        {
            offset &= 0x0f;
            if (offset == 0x06)
                BufferSprites();
            if (offset == 0x07)
                ClearBuffer();
            if (offset is >= 0x0c and <= 0x0f)
            {
                int addr = (_romBank << 19)
                           | ((_regs[11] & 0x07) << 18)
                           | (_regs[8] << 10)
                           | (_regs[9] << 2)
                           | ((offset & 3) ^ 1);
                _controlRomReads++;
                _lastControlRomAddress = addr;
                return _rom[addr & (_rom.Length - 1)];
            }
            return 0;
        }

        public string DebugSummary()
        {
            int active = 0;
            int first = -1;
            for (int offs = 0; offs < _buffer.Length; offs += 8)
            {
                if ((_buffer[offs] & 0x8000) == 0)
                    continue;

                active++;
                if (first < 0)
                    first = offs;
            }

            string firstSprite = first >= 0
                ? $" f=[{_buffer[first]:X4},{_buffer[first + 1]:X4},{_buffer[first + 2]:X4},{_buffer[first + 3]:X4},{_buffer[first + 4]:X4},{_buffer[first + 5]:X4},{_buffer[first + 6]:X4}]"
                : " f=none";
            string calc = DebugFirstSortedSprite();
            string spriteList = (_mystwarrSpriteLayout || _metamrphSpriteLayout) && Environment.GetEnvironmentVariable("EUTHERDRIVE_MYSTWARR_SPRITE_TRACE") == "1"
                ? " " + DebugMystwarrSpriteList()
                : "";
            int cpuActive = CountActive(_ram);
            int cpuMirrorActive = CountCpuActive();
            return $"romR={_controlRomReads} last=0x{_lastControlRomAddress:X6} bank={_romBank} "
                   + $"regs={_regs[0]:X2}/{_regs[1]:X2}/{_regs[2]:X2}/{_regs[3]:X2}/{_regs[5]:X2}/{_regs[8]:X2}/{_regs[9]:X2}/{_regs[11]:X2} "
                   + $"act={active} live={cpuActive}/{cpuMirrorActive} vis={_lastVisibleCandidates} pix={_lastDrawnPixels} bb={_lastMinX},{_lastMinY}-{_lastMaxX},{_lastMaxY}{firstSprite} {calc}"
                   + spriteList;
        }

        private static int CountActive(ushort[] ram)
        {
            int active = 0;
            for (int offs = 0; offs < ram.Length; offs += 8)
            {
                if ((ram[offs] & 0x8000) != 0)
                    active++;
            }
            return active;
        }

        private int CountCpuActive()
        {
            int active = 0;
            for (int i = 0; i < SpriteCount; i++)
            {
                int sourceOffset = 64 * i;
                if ((_cpuRam[sourceOffset] & 0x8000) != 0)
                    active++;
            }
            return active;
        }

        private string DebugFirstSortedSprite()
        {
            ushort[] spriteRam = (_mystwarrSpriteLayout || _metamrphSpriteLayout) ? _ram : _buffer;
            Span<int> sorted = stackalloc int[256];
            int sortedCount = BuildSortedSpriteList(sorted, spriteRam);

            for (int priCode = sortedCount - 1; priCode >= 0; priCode--)
            {
                int offs = sorted[priCode];
                if (offs < 0)
                    continue;
                if (!TryComputeSpriteBounds(offs, spriteRam, out var info))
                    return $"calc=skip@{offs:X3}/p{priCode}";
                return $"calc=@{offs:X3}/p{priCode} raw={info.RawY:X3} y={info.Y}..{info.Bottom} x={info.X}..{info.Right} wh={info.Width}x{info.Height} z={info.ZoomX:X}/{info.ZoomY:X}";
            }

            return "calc=none";
        }

        private string DebugMystwarrSpriteList()
        {
            ushort[] spriteRam = _ram;
            Span<int> sorted = stackalloc int[SpriteCount];
            int sortedCount = BuildSortedSpriteList(sorted, spriteRam);
            var writer = new StringBuilder(2048);
            writer.Append("spr=[");
            int emitted = 0;
            int visible = 0;
            for (int i = sortedCount - 1; i >= 0 && emitted < 64; i--)
            {
                int offs = sorted[i];
                if (offs < 0)
                    continue;

                bool hasBounds = TryComputeSpriteBounds(offs, spriteRam, out var info);
                if (hasBounds && info.Right > 0 && info.Bottom > 0 && info.X < FrameWidth && info.Y < FrameHeight)
                    visible++;
                if (emitted > 0)
                    writer.Append(' ');
                writer.Append('@');
                writer.Append(offs.ToString("X3"));
                writer.Append("/z");
                writer.Append((spriteRam[offs] & 0xff).ToString("X2"));
                writer.Append("/c");
                writer.Append((spriteRam[offs + 6] & 0xffff).ToString("X4"));
                writer.Append("/xy");
                writer.Append((spriteRam[offs + 3] & 0x03ff).ToString("X3"));
                writer.Append(',');
                writer.Append((spriteRam[offs + 2] & 0x03ff).ToString("X3"));
                if (hasBounds)
                {
                    writer.Append("=>");
                    writer.Append(info.X.ToString());
                    writer.Append(',');
                    writer.Append(info.Y.ToString());
                    writer.Append('-');
                    writer.Append(info.Right.ToString());
                    writer.Append(',');
                    writer.Append(info.Bottom.ToString());
                }
                else
                {
                    writer.Append("=>skip");
                }
                emitted++;
            }
            writer.Append("] on=");
            writer.Append(visible.ToString());
            return writer.ToString();
        }

        public ushort ReadControlWordNoA1(int offset)
        {
            offset &= ~1;
            return (ushort)((ReadControl(offset) << 8) | ReadControl(offset + 1));
        }

        public ushort ReadMystwarrRomWord(int offset)
        {
            offset &= 7;
            int romOffset = (_regs[6] << 16) | (_regs[7] << 8) | _regs[4];
            _controlRomReads++;
            _lastControlRomAddress = romOffset;

            return offset switch
            {
                0 => ReadLittleEndianWord(_rom, ((romOffset + 2) * 2) & (_rom.Length - 1)),
                1 => ReadLittleEndianWord(_rom, ((romOffset + 3) * 2) & (_rom.Length - 1)),
                2 or 3 => _rom[(0x400000 + (romOffset / 2) + 1) & (_rom.Length - 1)],
                4 => ReadLittleEndianWord(_rom, (romOffset * 2) & (_rom.Length - 1)),
                5 => ReadLittleEndianWord(_rom, ((romOffset + 1) * 2) & (_rom.Length - 1)),
                6 or 7 => _rom[(0x400000 + (romOffset / 2)) & (_rom.Length - 1)],
                _ => 0
            };
        }

        public ushort ReadMetamrphRomWord(int offset)
        {
            offset &= 7;
            int romOffset = (_regs[6] << 16) | (_regs[7] << 8) | _regs[4];
            _controlRomReads++;
            _lastControlRomAddress = romOffset;

            int wordOffset = ((romOffset >> 2) * 4) + (((offset & 4) == 0) ? 2 : 0) + (offset & 3);
            return ReadLittleEndianWord(_rom, (wordOffset << 1) & (_rom.Length - 1));
        }

        public void WriteControl(int offset, byte value)
        {
            offset &= 0x0f;
            _regs[offset] = value;
            if (_mystwarrSpriteLayout || _metamrphSpriteLayout)
                return;
            if (offset == 0x06)
                BufferSprites();
            else if (offset == 0x07)
                ClearBuffer();
        }

        public void WriteControlWordNoA1(int offset, ushort value)
        {
            offset &= ~1;
            WriteControl(offset, (byte)(value >> 8));
            WriteControl(offset + 1, (byte)value);
        }

        public void WriteObjRegByte(int byteOffset, byte value)
        {
            int offset = (byteOffset >> 1) & 7;
            ushort old = _objRegs[offset];
            _objRegs[offset] = (byteOffset & 1) == 0
                ? (ushort)((old & 0x00ff) | (value << 8))
                : (ushort)((old & 0xff00) | value);
        }

        public void WriteObjRegWord(int offset, ushort value)
        {
            _objRegs[offset & 7] = value;
        }

        public void BankSelect(int bank) => _romBank = bank;

        public void BeginRenderFrameStats()
        {
            _lastVisibleCandidates = 0;
            _lastDrawnPixels = 0;
            _lastMinX = FrameWidth;
            _lastMinY = FrameHeight;
            _lastMaxX = -1;
            _lastMaxY = -1;
        }

        public void BeginMystwarrObjectFrame()
        {
            BeginRenderFrameStats();
            Array.Fill(_mystwarrObjZBuffer, (byte)0xff);
            Array.Fill(_mystwarrShadowZBuffer, (byte)0xff);
            Array.Fill(_mystwarrShadowPriorityBuffer, (byte)0xff);
        }

        public void Render(byte[] frameBuffer, ReadOnlySpan<ushort> palette)
        {
            BeginRenderFrameStats();
            RenderSprites(frameBuffer, palette, default, -1, FrameHeight, null);
            FinishRenderFrameStats();
        }

        public void RenderMystwarrPriority(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int priority, K054338 k054338)
        {
            RenderSprites(frameBuffer, palette, default, -1, FrameHeight, null, priority, k054338);
        }

        public int AppendMystwarrSpriteRenderItems(Span<MystwarrRenderItem> items, int count, K055555 mixer, K054338 k054338)
        {
            ushort[] spriteRam = _ram;
            for (int offs = 0; offs < spriteRam.Length && count < items.Length; offs += 8)
            {
                if ((spriteRam[offs] & 0x8000) == 0)
                    continue;

                int rawColor = spriteRam[offs + 6];
                int priority = _metamrphSpriteLayout ? ((rawColor & 0xe0) >> 2) : (rawColor & 0xe0);
                int zcode = spriteRam[offs] & 0xff;
                if ((_objRegs[6] & 0x0010) != 0)
                    zcode = 0xff - zcode;

                int effect = (rawColor >> 8) & 3;
                int shadow = (rawColor >> 10) & 3;
                if (shadow != 0)
                {
                    int shadowMode = shadow - 1;
                    bool objSet1Shadow0Enabled = (_regs[5] & 0x20) != 0;
                    if (shadow != 1 || objSet1Shadow0Enabled)
                    {
                        int drawMode = effect != 0 ? 3 : 1;
                        uint order = ((uint)priority << 24) | ((uint)zcode << 16) | ((uint)offs << 5) | ((uint)drawMode << 4);
                        items[count++] = new MystwarrRenderItem(order, -1, offs, drawMode, shadowMode);

                        if (shadow != 3 && k054338.ShadowEnabled(shadowMode) && count < items.Length)
                        {
                            int shadowPriority = (_objRegs[6] & 0x0020) != 0
                                ? priority
                                : mixer.ReadRegister(K055555.ShadowPriority1 + shadowMode);
                            drawMode = 4;
                            order = ((uint)shadowPriority << 24) | ((uint)zcode << 16) | ((uint)offs << 5) | ((uint)drawMode << 4) | (uint)shadowMode;
                            items[count++] = new MystwarrRenderItem(order, -1, offs, drawMode, shadowMode);
                        }
                    }
                    else if (k054338.ShadowEnabled(0))
                    {
                        int drawMode = 5;
                        uint order = ((uint)priority << 24) | ((uint)zcode << 16) | ((uint)offs << 5) | ((uint)drawMode << 4);
                        items[count++] = new MystwarrRenderItem(order, -1, offs, drawMode, 0);
                    }
                }
                else
                {
                    int drawMode = effect != 0 ? 2 : 0;
                    uint order = ((uint)priority << 24) | ((uint)zcode << 16) | ((uint)offs << 5) | ((uint)drawMode << 4);
                    items[count++] = new MystwarrRenderItem(order, -1, offs, drawMode, 0);
                }
            }

            return count;
        }

        public void RenderMystwarrObject(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int spriteOffset, int drawMode, int shadowMode, int zcode, int priority, K054338 k054338)
        {
            RenderSpriteObject(frameBuffer, palette, _ram, spriteOffset, default, -1, FrameHeight, null, -1, k054338, drawMode, shadowMode, zcode, priority);
        }

        public void FinishMystwarrRenderFrameStats() => FinishRenderFrameStats();

        public void RenderPriorityBand(byte[] frameBuffer, ReadOnlySpan<ushort> palette, ReadOnlySpan<int> sortedLayerPriorities, int band, int outputHeight = FrameHeight)
        {
            RenderSprites(frameBuffer, palette, sortedLayerPriorities, band, outputHeight, null);
            if (band == 3)
                FinishRenderFrameStats();
        }

        public void RenderPriorityMasked(byte[] frameBuffer, ReadOnlySpan<ushort> palette, ReadOnlySpan<int> sortedLayerPriorities, byte[] priorityBuffer, int outputHeight = FrameHeight)
        {
            BeginRenderFrameStats();
            RenderSprites(frameBuffer, palette, sortedLayerPriorities, -1, outputHeight, priorityBuffer);
            FinishRenderFrameStats();
        }

        private void RenderSprites(byte[] frameBuffer, ReadOnlySpan<ushort> palette, ReadOnlySpan<int> sortedLayerPriorities, int band, int outputHeight, byte[]? priorityBuffer, int mystwarrPriority = -1, K054338? k054338 = null)
        {
            ushort[] spriteRam = (_mystwarrSpriteLayout || _metamrphSpriteLayout) ? _ram : _buffer;
            Span<int> sorted = stackalloc int[SpriteCount];
            int sortedCount = BuildSortedSpriteList(sorted, spriteRam);

            for (int sortedIndex = sortedCount - 1; sortedIndex >= 0; sortedIndex--)
            {
                int offs = sorted[sortedIndex];
                RenderSpriteObject(frameBuffer, palette, spriteRam, offs, sortedLayerPriorities, band, outputHeight, priorityBuffer, mystwarrPriority, k054338, -1, 0, -1, -1);
            }
        }

        private void RenderSpriteObject(byte[] frameBuffer, ReadOnlySpan<ushort> palette, ushort[] spriteRam, int offs,
            ReadOnlySpan<int> sortedLayerPriorities, int band, int outputHeight, byte[]? priorityBuffer,
            int mystwarrPriority, K054338? k054338, int gxDrawMode, int gxShadowMode, int gxZCode, int gxPriority)
        {
            if ((uint)offs >= (uint)spriteRam.Length)
                return;

            int code = spriteRam[offs + 1];
            if (!_mystwarrSpriteLayout && !_metamrphSpriteLayout)
                code = (code & 0xffe1) + ((code & 0x0010) >> 2) + ((code & 0x0008) << 1) + ((code & 0x0004) >> 1) + ((code & 0x0002) << 2);
            int rawColorWord = spriteRam[offs + 6];
            int rawColor = (_mystwarrSpriteLayout || _metamrphSpriteLayout) ? rawColorWord : rawColorWord & 0xff;
            int callbackPriority = _metamrphSpriteLayout ? ((rawColor & 0xe0) >> 2) : (rawColor & 0xe0);
            if (mystwarrPriority >= 0 && callbackPriority != mystwarrPriority)
                return;
            if (band >= 0 && SpritePriorityBand(rawColor, sortedLayerPriorities) != band)
                return;
            int priorityMask = priorityBuffer != null ? SpritePriorityMask(rawColor, sortedLayerPriorities) : 0;
            int color = SpriteColorBase + (rawColor & 0x1f);
            int alpha = (_mystwarrSpriteLayout || _metamrphSpriteLayout) ? SpriteAlpha(rawColorWord, k054338, gxDrawMode) : 255;
            if (!TryComputeSpriteBounds(offs, spriteRam, out var bounds))
                return;

            int w = bounds.Width;
            int h = bounds.Height;
            int zoomX = bounds.ZoomX;
            int zoomY = bounds.ZoomY;
            int ox = bounds.X;
            int oy = bounds.Y;
            bool flipX = (spriteRam[offs] & 0x1000) != 0;
            bool flipY = (spriteRam[offs] & 0x2000) != 0;
            bool gxLayout = _mystwarrSpriteLayout || _metamrphSpriteLayout;
            bool mirrorX = (spriteRam[offs + 6] & (gxLayout ? 0x4000 : 0x0100)) != 0;
            bool mirrorY = (spriteRam[offs + 6] & (gxLayout ? 0x8000 : 0x0200)) != 0;
            bool shadow = !gxLayout && (spriteRam[offs + 6] & 0x0080) != 0;
            if (mirrorX)
                flipX = false;
            if ((_regs[5] & 0x01) != 0 && !mirrorX) flipX = !flipX;
            if (!Tmnt2CoordinateMode && (_regs[5] & 0x02) != 0 && !mirrorY) flipY = !flipY;

            int spriteMinX = ox;
            int spriteMinY = oy;
            int spriteMaxX = bounds.Right;
            int spriteMaxY = bounds.Bottom;
            if (spriteMaxX > 0 && spriteMaxY > 0 && spriteMinX < FrameWidth && spriteMinY < outputHeight)
            {
                _lastVisibleCandidates++;
                _lastMinX = Math.Min(_lastMinX, spriteMinX);
                _lastMinY = Math.Min(_lastMinY, spriteMinY);
                _lastMaxX = Math.Max(_lastMaxX, spriteMaxX);
                _lastMaxY = Math.Max(_lastMaxY, spriteMaxY);
            }

            for (int y = 0; y < h; y++)
            {
                int sy = oy + ((zoomY * y + (1 << 11)) >> 12);
                int zh = Math.Max(1, oy + ((zoomY * (y + 1) + (1 << 11)) >> 12) - sy);
                for (int x = 0; x < w; x++)
                {
                    int sx = ox + ((zoomX * x + (1 << 11)) >> 12);
                    int zw = Math.Max(1, ox + ((zoomX * (x + 1) + (1 << 11)) >> 12) - sx);
                    int tile = SpriteTileCode(code, x, y, w, h, flipX, flipY, mirrorX, mirrorY, gxLayout, out bool tileFlipX, out bool tileFlipY);
                    _lastDrawnPixels += DrawSpriteTile(frameBuffer, palette, tile, color, sx, sy, zw, zh, tileFlipX, tileFlipY, outputHeight, priorityBuffer, priorityMask, shadow, alpha, gxDrawMode, gxShadowMode, gxZCode, gxPriority, k054338);
                }
            }
        }

        private static int SpriteAlpha(int rawColorWord, K054338? k054338, int gxDrawMode)
        {
            if (gxDrawMode >= 0 && (gxDrawMode & 2) == 0)
                return 255;

            int effect = (rawColorWord >> 8) & 3;
            if (effect == 0 || k054338 == null)
                return 255;

            return k054338.TilemapAlphaLevel(effect);
        }

        private int BuildSortedSpriteList(Span<int> sorted, ushort[] spriteRam)
        {
            sorted.Fill(-1);

            if (!_mystwarrSpriteLayout && !_metamrphSpriteLayout)
            {
                for (int offs = 0; offs < spriteRam.Length; offs += 8)
                {
                    int priCode = spriteRam[offs];
                    if ((priCode & 0x8000) == 0)
                        continue;
                    priCode &= 0x7f;
                    if (sorted[priCode] < 0)
                        sorted[priCode] = offs;
                }
                return sorted.Length;
            }

            int count = 0;
            for (int offs = 0; offs < spriteRam.Length && count < sorted.Length; offs += 8)
            {
                if ((spriteRam[offs] & 0x8000) != 0)
                    sorted[count++] = offs;
            }

            bool ascending = (_objRegs[6] & 0x0010) != 0;
            for (int y = 0; y < count - 1; y++)
            {
                int offs = sorted[y];
                int zcode = spriteRam[offs] & 0xff;
                for (int x = y + 1; x < count; x++)
                {
                    int temp = sorted[x];
                    int candidate = spriteRam[temp] & 0xff;
                    bool swap = ascending ? zcode >= candidate : zcode <= candidate;
                    if (!swap)
                        continue;

                    zcode = candidate;
                    sorted[x] = offs;
                    sorted[y] = offs = temp;
                }
            }

            return count;
        }

        private void FinishRenderFrameStats()
        {
            if (_lastVisibleCandidates == 0)
            {
                _lastMinX = 0;
                _lastMinY = 0;
                _lastMaxX = 0;
                _lastMaxY = 0;
            }
        }

        private static int SpritePriorityBand(int rawColor, ReadOnlySpan<int> sortedLayerPriorities)
        {
            if (sortedLayerPriorities.Length < 3)
                return 3;

            int priority = 0x20 | ((rawColor & 0x60) >> 2);
            if (priority <= sortedLayerPriorities[2])
                return 3;
            if (priority <= sortedLayerPriorities[1])
                return 2;
            if (priority <= sortedLayerPriorities[0])
                return 1;
            return 0;
        }

        private static int SpritePriorityMask(int rawColor, ReadOnlySpan<int> sortedLayerPriorities)
        {
            if (sortedLayerPriorities.Length < 3)
                return 0;

            int priority = 0x20 | ((rawColor & 0x60) >> 2);
            if (priority <= sortedLayerPriorities[2])
                return 0;
            if (priority <= sortedLayerPriorities[1])
                return 0xf0;
            if (priority <= sortedLayerPriorities[0])
                return 0xf0 | 0xcc;
            return 0xf0 | 0xcc | 0xaa;
        }

        private readonly record struct SpriteBounds(int RawY, int X, int Y, int Right, int Bottom, int Width, int Height, int ZoomX, int ZoomY);

        private bool TryComputeSpriteBounds(int offs, ushort[] spriteRam, out SpriteBounds bounds)
        {
            int size = (spriteRam[offs] & 0x0f00) >> 8;
            int w = 1 << (size & 0x03);
            int h = 1 << ((size >> 2) & 0x03);
            bool gxLayout = _mystwarrSpriteLayout || _metamrphSpriteLayout;
            int rawZoomY = gxLayout ? spriteRam[offs + 4] & 0x03ff : spriteRam[offs + 4];
            int rawZoomX = gxLayout ? spriteRam[offs + 5] & 0x03ff : spriteRam[offs + 5];
            int zoomY = SpriteZoom((ushort)rawZoomY, cullZero: gxLayout);
            int zoomX = (spriteRam[offs] & 0x4000) == 0 ? SpriteZoom((ushort)rawZoomX, cullZero: gxLayout) : zoomY;
            if (zoomX < 0 || zoomY < 0)
            {
                bounds = default;
                return false;
            }

            int spriteoffsX = (_regs[0] << 8) | _regs[1];
            int spriteoffsY = (_regs[2] << 8) | _regs[3];
            bool flipScreenX = (_regs[5] & 0x01) != 0;
            bool flipScreenY = !Tmnt2CoordinateMode && (_regs[5] & 0x02) != 0;
            bool mirrorX = (spriteRam[offs + 6] & (gxLayout ? 0x4000 : 0x0100)) != 0;
            bool mirrorY = (spriteRam[offs + 6] & (gxLayout ? 0x8000 : 0x0200)) != 0;

            int rawY = spriteRam[offs + 2] & 0x03ff;
            if (gxLayout)
            {
                int offx = (short)((_regs[0] << 8) | _regs[1]);
                int offy = (short)((_regs[2] << 8) | _regs[3]);
                int mysticX = spriteRam[offs + 3] & 0x03ff;
                int mysticY = rawY;
                int wrapSize;
                int xWrapLimit;
                int yWrapLimit;
                if ((_objRegs[6] & 0x0040) != 0)
                {
                    wrapSize = 512;
                    xWrapLimit = 512 - 64;
                    yWrapLimit = 512 - 128;
                }
                else
                {
                    wrapSize = 1024;
                    xWrapLimit = 1024 - 384;
                    yWrapLimit = 1024 - 512;
                }

                if (flipScreenX)
                    mysticX = -mysticX;
                if (flipScreenY)
                    mysticY = -mysticY;

                int chipDx = _metamrphSpriteLayout ? -51 : -48;
                int chipDy = -24;
                int visibleOriginY = _metamrphSpriteLayout ? 15 : MysticVisibleOriginY;
                mysticX += chipDx;
                mysticY -= chipDy;
                mysticX = (mysticX - offx) & (wrapSize - 1);
                mysticY = (-mysticY - offy) & (wrapSize - 1);
                if (mysticX >= xWrapLimit) mysticX -= wrapSize;
                if (mysticY >= yWrapLimit) mysticY -= wrapSize;
                mysticX -= (zoomX * w) >> 13;
                mysticY -= (zoomY * h) >> 13;
                mysticX -= MysticVisibleOriginX;
                mysticY -= visibleOriginY;

                int mysticRight = mysticX + ((zoomX * w + (1 << 11)) >> 12);
                int mysticBottom = mysticY + ((zoomY * h + (1 << 11)) >> 12);
                bounds = new SpriteBounds(rawY, mysticX, mysticY, mysticRight, mysticBottom, w, h, zoomX, zoomY);
                return true;
            }

            int ox = spriteRam[offs + 3] + spriteoffsX - 96;
            int oy = rawY;

            if (flipScreenX)
                ox = 320 - ox;
            if (flipScreenY)
                oy = -oy;

            ox = (ox + 0x5d) & 0x3ff;
            if (ox >= 768) ox -= 1024;
            oy = (-(oy + spriteoffsY + 0x07)) & 0x3ff;
            if (oy >= 640) oy -= 1024;
            ox -= (zoomX * w) >> 13;
            oy -= (zoomY * h) >> 13;
            // TMNT2's protection output lands in the adjacent K053245 Y phase for gameplay sprites.
            if (rawY is >= 0x0100 and < 0x0200 && oy >= Tmnt2RawFrameHeight)
                oy -= 128;
            if (Tmnt2CoordinateMode && rawY is >= 0x0100 and < 0x0200 && oy < -128)
                oy += 384;

            int right = ox + ((zoomX * w + (1 << 11)) >> 12);
            int bottom = oy + ((zoomY * h + (1 << 11)) >> 12);
            bounds = new SpriteBounds(rawY, ox, oy, right, bottom, w, h, zoomX, zoomY);
            return true;
        }

        private void ClearBuffer()
        {
            for (int i = 0; i < _buffer.Length; i += 8)
                _buffer[i] = 0;
        }

        private static int ScatterOffset(int offset)
            => (((offset & 0x000e) >> 1) | ((offset & 0x1fc0) >> 3)) & (RamWords - 1);

        private static int MystwarrScatterOffset(int offset)
            => ((offset & 0x0007) | ((offset & 0x7f80) >> 4)) & (RamWords - 1);

        private static int SpriteZoom(ushort value, bool cullZero = false)
        {
            if (value == 0 && cullZero)
                return -1;
            if (value > 0x2000)
                return -1;
            return value != 0 ? (0x400000 + value / 2) / value : 2 * 0x400000;
        }

        private static int SpriteTileCode(int code, int x, int y, int w, int h, bool flipX, bool flipY, bool mirrorX, bool mirrorY, bool useGxLayout, out bool tileFlipX, out bool tileFlipY)
        {
            if (!useGxLayout)
                return SpriteTileCodeLinear(code, x, y, w, h, flipX, flipY, mirrorX, mirrorY, out tileFlipX, out tileFlipY);

            ReadOnlySpan<int> xoffset = stackalloc[] { 0, 1, 4, 5, 16, 17, 20, 21 };
            ReadOnlySpan<int> yoffset = stackalloc[] { 0, 2, 8, 10, 32, 34, 40, 42 };
            int xa = 0;
            int ya = 0;
            if ((code & 0x01) != 0) xa += 1;
            if ((code & 0x02) != 0) ya += 1;
            if ((code & 0x04) != 0) xa += 2;
            if ((code & 0x08) != 0) ya += 2;
            if ((code & 0x10) != 0) xa += 4;
            if ((code & 0x20) != 0) ya += 4;
            int baseCode = code & ~0x3f;

            int c;
            if (mirrorX)
            {
                if ((flipX == false) ^ (2 * x < w))
                {
                    c = baseCode + xoffset[(w - 1 - x + xa) & 7];
                    tileFlipX = true;
                }
                else
                {
                    c = baseCode + xoffset[(x + xa) & 7];
                    tileFlipX = false;
                }
            }
            else
            {
                c = baseCode + xoffset[(flipX ? w - 1 - x + xa : x + xa) & 7];
                tileFlipX = flipX;
            }

            if (mirrorY)
            {
                if ((flipY == false) ^ (2 * y >= h))
                {
                    c += yoffset[(h - 1 - y + ya) & 7];
                    tileFlipY = true;
                }
                else
                {
                    c += yoffset[(y + ya) & 7];
                    tileFlipY = false;
                }
            }
            else
            {
                c += yoffset[(flipY ? h - 1 - y + ya : y + ya) & 7];
                tileFlipY = flipY;
            }

            return c;
        }

        private static int SpriteTileCodeLinear(int code, int x, int y, int w, int h, bool flipX, bool flipY, bool mirrorX, bool mirrorY, out bool tileFlipX, out bool tileFlipY)
        {
            int c;
            if (mirrorX)
            {
                if ((flipX == false) ^ (2 * x < w))
                {
                    c = code + (w - x - 1);
                    tileFlipX = true;
                }
                else
                {
                    c = code + x;
                    tileFlipX = false;
                }
            }
            else
            {
                c = code + (flipX ? w - 1 - x : x);
                tileFlipX = flipX;
            }

            if (mirrorY)
            {
                if ((flipY == false) ^ (2 * y >= h))
                {
                    c += 8 * (h - y - 1);
                    tileFlipY = true;
                }
                else
                {
                    c += 8 * y;
                    tileFlipY = false;
                }
            }
            else
            {
                c += 8 * (flipY ? h - 1 - y : y);
                tileFlipY = flipY;
            }

            return (c & 0x3f) | (code & ~0x3f);
        }

        private int DrawSpriteTile(byte[] frameBuffer, ReadOnlySpan<ushort> palette, int code, int colorBase, int sx, int sy, int zw, int zh,
            bool flipX, bool flipY, int outputHeight, byte[]? priorityBuffer, int priorityMask, bool shadow, int alpha, int gxDrawMode, int gxShadowMode, int gxZCode, int gxPriority, K054338? k054338)
        {
            int drawn = 0;
            int tileMask = _metamrphSpriteLayout ? 0xffff : 0x7fff;
            int baseAddress = ((code & tileMask) * 128) & (_rom.Length - 1);
            bool gxLayout = _mystwarrSpriteLayout || _metamrphSpriteLayout;
            bool gxShadowDraw = gxLayout && gxDrawMode >= 4;
            int shadowPen = gxShadowDraw && gxDrawMode == 5 ? 1 : 31;
            bool gxSolidOnly = gxLayout && (gxDrawMode & 3) != 0;
            for (int dy = 0; dy < zh; dy++)
            {
                int py = sy + dy;
                if ((uint)py >= (uint)outputHeight)
                    continue;
                int srcY = Math.Clamp(dy * 16 / zh, 0, 15);
                if (flipY) srcY = 15 - srcY;
                for (int dx = 0; dx < zw; dx++)
                {
                    int px = sx + dx;
                    if ((uint)px >= FrameWidth)
                        continue;
                    int srcX = Math.Clamp(dx * 16 / zw, 0, 15);
                    if (flipX) srcX = 15 - srcX;
                    int pen = DecodeSpritePixel(baseAddress, srcX, srcY);
                    if (pen == 0)
                        continue;

                    if (gxShadowDraw)
                    {
                        if (pen < shadowPen || k054338 == null)
                            continue;

                        if (gxZCode >= 0 && gxPriority >= 0)
                        {
                            int zOffset = py * FrameWidth + px;
                            if (_mystwarrShadowZBuffer[zOffset] < gxZCode || _mystwarrShadowPriorityBuffer[zOffset] <= gxPriority)
                                continue;
                            _mystwarrShadowZBuffer[zOffset] = (byte)gxZCode;
                            _mystwarrShadowPriorityBuffer[zOffset] = (byte)gxPriority;
                        }
                        k054338.ApplyShadow(frameBuffer, px, py, gxShadowMode);
                        drawn++;
                        continue;
                    }

                    if (gxSolidOnly && pen >= shadowPen)
                        continue;

                    if (gxZCode >= 0)
                    {
                        int zOffset = py * FrameWidth + px;
                        if (_mystwarrObjZBuffer[zOffset] < gxZCode)
                            continue;
                        _mystwarrObjZBuffer[zOffset] = (byte)gxZCode;
                    }
                    if (priorityBuffer != null)
                    {
                        int priorityOffset = py * FrameWidth + px;
                        int priority = priorityBuffer[priorityOffset] & 0x1f;
                        if (priority == 31 || (((1 << priority) & priorityMask) != 0))
                        {
                            priorityBuffer[priorityOffset] = 31;
                            continue;
                        }
                        priorityBuffer[priorityOffset] = 31;
                    }
                    if (shadow && pen == 0x0f)
                    {
                        ApplyShadow(frameBuffer, px, py);
                        drawn++;
                        continue;
                    }
                    int paletteIndex = _mystwarrSpriteLayout ? colorBase * 32 + pen : colorBase * 16 + pen;
                    WriteSpritePixel(frameBuffer, px, py, palette[paletteIndex & 0x7ff], alpha);
                    drawn++;
                }
            }
            return drawn;
        }

        private static void WriteSpritePixel(byte[] frameBuffer, int x, int y, ushort xBgr555, int alpha)
        {
            if (alpha >= 255)
            {
                WritePixel(frameBuffer, x, y, xBgr555);
                return;
            }

            int r = Color5To8[xBgr555 & 0x1f];
            int g = Color5To8[(xBgr555 >> 5) & 0x1f];
            int b = Color5To8[(xBgr555 >> 10) & 0x1f];
            int inv = 255 - alpha;
            int offset = y * FrameStride + x * 4;
            frameBuffer[offset] = (byte)((b * alpha + frameBuffer[offset] * inv + 127) / 255);
            frameBuffer[offset + 1] = (byte)((g * alpha + frameBuffer[offset + 1] * inv + 127) / 255);
            frameBuffer[offset + 2] = (byte)((r * alpha + frameBuffer[offset + 2] * inv + 127) / 255);
            frameBuffer[offset + 3] = 0xff;
        }

        private static void ApplyShadow(byte[] frameBuffer, int x, int y)
        {
            int offset = y * FrameStride + x * 4;
            frameBuffer[offset] = (byte)(frameBuffer[offset] * 5 / 8);
            frameBuffer[offset + 1] = (byte)(frameBuffer[offset + 1] * 5 / 8);
            frameBuffer[offset + 2] = (byte)(frameBuffer[offset + 2] * 5 / 8);
        }

        private int DecodeSpritePixel(int baseAddress, int x, int y)
        {
            if (_mystwarrSpriteLayout)
                return DecodeMystwarrSpritePixel(baseAddress / 128, x, y);
            if (_metamrphSpriteLayout)
                return DecodeMetamrphSpritePixel(baseAddress / 128, x, y);

            int address = (baseAddress + (y & 7) * 4 + (y >= 8 ? 64 : 0)) & (_rom.Length - 1);
            if (x >= 8)
                address += 32;
            int bit = 7 - (x & 7);
            return (((_rom[address + 3] >> bit) & 1) << 3)
                   | (((_rom[address + 2] >> bit) & 1) << 2)
                   | (((_rom[address + 1] >> bit) & 1) << 1)
                   | ((_rom[address + 0] >> bit) & 1);
        }

        private int DecodeMystwarrSpritePixel(int code, int x, int y)
            => _mystwarrDecodedSprites[((code & 0x7fff) << 8) | ((y & 15) << 4) | (x & 15)];

        private int DecodeMetamrphSpritePixel(int code, int x, int y)
            => _metamrphDecodedSprites[((code & 0xffff) << 8) | ((y & 15) << 4) | (x & 15)];

        private void DecodeMystwarrSprites()
        {
            for (int code = 0; code < 0x8000; code++)
            {
                int bitTileBase = code * 1280;
                int decodedTileBase = code << 8;
                for (int y = 0; y < 16; y++)
                {
                    int bitRowBase = bitTileBase + y * 80;
                    int decodedRowBase = decodedTileBase + (y << 4);
                    for (int x = 0; x < 16; x++)
                    {
                        int bitIndexBase = bitRowBase + (x >= 8 ? 40 : 0) + (x & 7);
                        int pen = (ReadMystwarrCombinedBit(bitIndexBase + 32) << 4)
                                  | (ReadMystwarrCombinedBit(bitIndexBase + 24) << 3)
                                  | (ReadMystwarrCombinedBit(bitIndexBase + 16) << 2)
                                  | (ReadMystwarrCombinedBit(bitIndexBase + 8) << 1)
                                  | ReadMystwarrCombinedBit(bitIndexBase);
                        _mystwarrDecodedSprites[decodedRowBase + x] = (byte)pen;
                    }
                }
            }
        }

        private void DecodeMetamrphSprites()
        {
            for (int code = 0; code < 0x10000; code++)
            {
                int bitTileBase = code * 1024;
                int decodedTileBase = code << 8;
                for (int y = 0; y < 16; y++)
                {
                    int bitRowBase = bitTileBase + y * 64;
                    int decodedRowBase = decodedTileBase + (y << 4);
                    for (int x = 0; x < 16; x++)
                    {
                        int bitIndexBase = bitRowBase + (x >= 8 ? 32 : 0) + (x & 7);
                        int pen = (ReadRomBit(bitIndexBase + 24) << 3)
                                  | (ReadRomBit(bitIndexBase + 16) << 2)
                                  | (ReadRomBit(bitIndexBase + 8) << 1)
                                  | ReadRomBit(bitIndexBase);
                        _metamrphDecodedSprites[decodedRowBase + x] = (byte)pen;
                    }
                }
            }
        }

        private int ReadRomBit(int bitIndex)
        {
            int byteIndex = (bitIndex >> 3) & (_rom.Length - 1);
            return (_rom[byteIndex] >> (7 - (bitIndex & 7))) & 1;
        }

        private int ReadMystwarrCombinedBit(int bitIndex)
        {
            int byteIndex = bitIndex >> 3;
            return (ReadMystwarrCombinedSpriteByte(byteIndex) >> (7 - (bitIndex & 7))) & 1;
        }

        private byte ReadMystwarrCombinedSpriteByte(int combinedIndex)
        {
            int group = combinedIndex / 5;
            int lane = combinedIndex % 5;
            if (lane < 4)
                return _rom[(group * 4 + lane) & (_rom.Length - 1)];
            return _rom[(0x400000 + group) & (_rom.Length - 1)];
        }

        private static ushort ReadLittleEndianWord(byte[] data, int offset)
            => (ushort)(data[offset] | (data[(offset + 1) & (data.Length - 1)] << 8));
    }

    private sealed class Tmnt2SerialEeprom
    {
        private const int ByteCount = 0x80;
        private const int AddressBits = 7;
        private const int CommandAddressBits = 9;
        private readonly byte[] _data = new byte[ByteCount];

        private bool _chipSelect;
        private bool _clock;
        private Mode _mode;
        private int _command;
        private int _commandBits;
        private int _address;
        private int _readShift;
        private int _readBitsRemaining;
        private int _writeData;
        private int _writeBits;
        private bool _writeEnabled;
        private int _writes;
        private int _reads;
        private int _commands;
        private int _lastCommand;
        private int _lastAddress;

        private enum Mode
        {
            Reset,
            WaitStart,
            Command,
            Read,
            Write,
            Done
        }

        public bool DataOut { get; private set; } = true;
        public bool Ready
        {
            get
            {
                _reads++;
                return true;
            }
        }

        public string DebugSummary()
            => $"w={_writes} r={_reads} cmd={_commands} last=0x{_lastCommand:X3}@{_lastAddress:X2} cs={(_chipSelect ? 1 : 0)} clk={(_clock ? 1 : 0)} out={(DataOut ? 1 : 0)} mode={_mode}";

        public void ResetContents()
        {
            Array.Fill(_data, (byte)0xff);
            _writeEnabled = false;
            ResetPins();
        }

        public void Import(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
                return;
            data[..Math.Min(data.Length, _data.Length)].CopyTo(_data);
            ResetPins();
        }

        public void Write(byte value)
        {
            _writes++;
            bool dataIn = (value & 0x01) != 0;
            bool chipSelect = (value & 0x02) != 0;
            bool clock = (value & 0x04) != 0;

            if (!chipSelect)
            {
                _chipSelect = false;
                _clock = clock;
                _mode = Mode.Reset;
                ResetSerial();
                return;
            }

            if (!_chipSelect)
            {
                _chipSelect = true;
                _clock = clock;
                _mode = Mode.WaitStart;
                ResetSerial();
                return;
            }

            bool risingClock = !_clock && clock;
            _clock = clock;
            if (risingClock)
                Clock(dataIn);
        }

        private void ResetPins()
        {
            _chipSelect = false;
            _clock = false;
            _mode = Mode.Reset;
            ResetSerial();
        }

        private void ResetSerial()
        {
            _command = 0;
            _commandBits = 0;
            _address = 0;
            _readShift = 0;
            _readBitsRemaining = 0;
            _writeData = 0;
            _writeBits = 0;
            DataOut = true;
        }

        private void Clock(bool dataIn)
        {
            if (_mode == Mode.WaitStart)
            {
                if (!dataIn)
                    return;
                _mode = Mode.Command;
                _command = 0;
                _commandBits = 0;
                return;
            }

            if (_mode == Mode.Read)
            {
                if (_readBitsRemaining > 0)
                {
                    DataOut = ((_readShift >> 7) & 1) != 0;
                    _readShift = ((_readShift << 1) | 1) & 0xff;
                    _readBitsRemaining--;
                }
                else
                {
                    DataOut = true;
                }
                return;
            }

            if (_mode == Mode.Write)
            {
                _writeData = ((_writeData << 1) | (dataIn ? 1 : 0)) & 0xff;
                _writeBits++;
                if (_writeBits == 8)
                {
                    if (_writeEnabled)
                        _data[_address] = (byte)_writeData;
                    _mode = Mode.Done;
                    DataOut = true;
                }
                return;
            }

            if (_mode != Mode.Command)
                return;

            _command = ((_command << 1) | (dataIn ? 1 : 0)) & ((1 << (2 + CommandAddressBits)) - 1);
            _commandBits++;
            if (_commandBits == 2 + CommandAddressBits)
                DecodeCommand();
        }

        private void DecodeCommand()
        {
            _commands++;
            _lastCommand = _command;
            int op = (_command >> CommandAddressBits) & 0x03;
            int address = _command & (ByteCount - 1);
            _address = address;
            _lastAddress = address;

            switch (op)
            {
                case 0x02:
                    _readShift = _data[address];
                    _readBitsRemaining = 8;
                    _mode = Mode.Read;
                    DataOut = false;
                    break;

                case 0x01:
                case 0x03:
                    _writeData = 0;
                    _writeBits = 0;
                    _mode = Mode.Write;
                    DataOut = true;
                    break;

                default:
                    DecodeSpecial(_command & ((1 << CommandAddressBits) - 1));
                    break;
            }
        }

        private void DecodeSpecial(int commandAddress)
        {
            switch ((commandAddress >> (CommandAddressBits - 2)) & 0x03)
            {
                case 0x00:
                    _writeEnabled = false;
                    _mode = Mode.Reset;
                    break;

                case 0x01:
                    _mode = Mode.Done;
                    break;

                case 0x02:
                    if (_writeEnabled)
                        Array.Fill(_data, (byte)0xff);
                    _mode = Mode.Done;
                    break;

                case 0x03:
                    _writeEnabled = true;
                    _mode = Mode.Reset;
                    break;
            }
            DataOut = true;
        }
    }

    private sealed class TmntSound : EutherDrive.Core.Cpu.Z80Emu.IOpcodeBusInterface
    {
        private const int AudioCpuClock = 3_579_545;
        private const int AudioCpuCyclesPerFrame = 60_480;
        private const int Tmnt2AudioCpuClock = 8_000_000;
        private const int Tmnt2AudioCpuCyclesPerFrame = 135_168;
        private const float K053260RouteGain = 0.75f;
        private const int MystwarrK054539Clock = 18_432_000;
        private const float MystwarrK054539RouteGain = 1.0f;
        private static readonly bool Tmnt2MuteYm2151 =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_TMNT2_YM2151_MUTE") == "1";
        private static readonly int Tmnt2Z80MemoryWaitCycles =
            ParseEnvInt("EUTHERDRIVE_TMNT2_Z80_WAIT_CYCLES", defaultValue: 0, minValue: 0, maxValue: 4);

        [NonSerialized] private readonly byte[] _program = new byte[0x40000];
        private readonly byte[] _ram = new byte[0x2000];
        private readonly Z80 _cpu = new();
        private readonly Cps1Ym2151 _ym = new();
        private readonly K007232Pcm _pcm = new();
        [NonSerialized] private readonly K053260Pcm _k053260 = new();
        [NonSerialized] private readonly K054539Pcm _k054539_1 = new();
        [NonSerialized] private readonly K054539Pcm _k054539_2 = new();
        private readonly K054321SoundInterface _k054321 = new();
        private readonly Upd7759Adpcm _upd = new();
        private readonly byte[] _mystwarrK054539Scratch1 = new byte[0x1d0];
        private readonly byte[] _mystwarrK054539Scratch2 = new byte[0x1d0];
        [NonSerialized] private short[] _titleSample = Array.Empty<short>();

        [NonSerialized] private TmntHardwareVariant _variant;
        private byte _soundLatch = 0xff;
        private byte _sres = 0xff;
        private byte _mystwarrSoundCtrl = 2;
        private bool _mystwarrNmiClock;
        private bool _irqAsserted;
        [NonSerialized] private bool _nmiAsserted;
        [NonSerialized] private int _nmiBlockedCycles;
        [NonSerialized] private bool _nmiBlockArmedThisInstruction;
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
        [NonSerialized] private int _tmnt2Z80WaitCycles;
        [NonSerialized] private int _tmnt2WaitCyclesThisFrame;
        private long _soundFrameCounter;
        [NonSerialized] private TmntAudioProbe? _audioProbe;
        [NonSerialized] private TmntAudioTrace? _audioTrace;

        public string DebugSummary
            => $"z80pc=0x{_cpu.Pc:X4} z80stalled={_cpu.Stalled} sndLatch=0x{_soundLatch:X2} "
               + $"sres=0x{_sres:X2} sresW={_sresWrites} ymW={_ymWrites} {_ym.DebugSummary} pcmW={_pcmWrites} {_pcm.DebugSummary} "
               + $"{_upd.DebugSummary} {K053260DebugSummary} k054321={_k054321.DebugSummary} k054539a={_k054539_1.DebugSummary} k054539b={_k054539_2.DebugSummary} irqP={_irqPulses} z80wait={_tmnt2WaitCyclesThisFrame} "
               + $"audPeak={_lastPeak} probe={(_audioProbe?.Enabled == true ? 1 : 0)}";

        public string K053260DebugSummary => _k053260.DebugSummary;

        public void Load(TmntRomSet roms)
        {
            _variant = roms.Variant;
            Array.Clear(_program);
            Array.Copy(roms.AudioCpu, _program, Math.Min(roms.AudioCpu.Length, _program.Length));
            _pcm.Load(roms.K007232);
            _k053260.Load(roms.K053260);
            _k054539_1.Load(roms.K054539);
            _k054539_2.Load(roms.K054539);
            _upd.Load(roms.Upd7759);
            _titleSample = DecodeTitleSample(roms.TitleSample);
            ResetMachine();
        }

        public void RestoreRuntimeState(TmntHardwareVariant variant)
        {
            _variant = variant;
            _audioFrameBuffer = null;
            _audioFrameSampleIndex = 0;
            if (!double.IsFinite(_z80CycleAccumulator) || Math.Abs(_z80CycleAccumulator) > CurrentAudioCpuCyclesPerFrame * 4.0)
                _z80CycleAccumulator = 0;
            if (!double.IsFinite(_outputFrameAccumulator) || Math.Abs(_outputFrameAccumulator) > OutputSampleRate)
                _outputFrameAccumulator = 0;
            if (_pendingRenderCycles < 0 || _pendingRenderCycles > CurrentAudioCpuCyclesPerFrame * 4)
                _pendingRenderCycles = 0;
            if (_nmiBlockedCycles < 0 || _nmiBlockedCycles > CurrentAudioCpuClock)
                _nmiBlockedCycles = 0;
        }

        public void SaveExtendedState(BinaryWriter writer)
        {
            writer.Write(UsesK053260Sound);
            writer.Write(_nmiAsserted);
            writer.Write(_nmiBlockedCycles);
            writer.Write(_outputFrameAccumulator);
            writer.Write(_z80CycleAccumulator);
            writer.Write(_pendingRenderCycles);
            _k053260.SaveState(writer);
            writer.Write(UsesMystwarrSound);
            if (UsesMystwarrSound)
            {
                _k054539_1.SaveState(writer);
                _k054539_2.SaveState(writer);
            }
        }

        public void LoadExtendedState(BinaryReader reader, int version)
        {
            bool hasTmnt2Sound = reader.ReadBoolean();
            _nmiAsserted = reader.ReadBoolean();
            _nmiBlockedCycles = reader.ReadInt32();
            _outputFrameAccumulator = reader.ReadDouble();
            _z80CycleAccumulator = reader.ReadDouble();
            _pendingRenderCycles = reader.ReadInt32();
            _k053260.LoadState(reader, version);
            if (version >= 5)
            {
                bool hasMystwarrSound = reader.ReadBoolean();
                if (hasMystwarrSound)
                {
                    _k054539_1.LoadState(reader);
                    _k054539_2.LoadState(reader);
                }
            }
        }

        public void ResetMachine()
        {
            Array.Clear(_ram);
            Array.Clear(_mystwarrK054539Scratch1);
            Array.Clear(_mystwarrK054539Scratch2);
            _cpu.ApplyResetLine();
            _ym.Reset();
            _pcm.Reset();
            _k053260.Reset();
            _k054539_1.Reset();
            _k054539_2.Reset();
            if (UsesMystwarrSound || UsesMoomesaSound)
            {
                if (_variant == TmntHardwareVariant.Metamrph)
                {
                    for (int i = 0; i <= 3; i++)
                    {
                        _k054539_1.SetGain(i, 0.8);
                        _k054539_1.SetGain(i + 4, 1.8);
                        _k054539_2.SetGain(i, 0.8);
                        _k054539_2.SetGain(i + 4, 0.8);
                    }
                }
                else if (_variant == TmntHardwareVariant.Moomesa)
                {
                    for (int i = 0; i <= 7; i++)
                        _k054539_1.SetGain(i, 1.0);
                }
                else
                {
                    for (int i = 0; i <= 3; i++)
                        _k054539_1.SetGain(i, 0.8);
                    for (int i = 4; i <= 7; i++)
                        _k054539_1.SetGain(i, 2.0);
                    for (int i = 0; i <= 7; i++)
                        _k054539_2.SetGain(i, 0.5);
                }
            }
            _k054321.Reset();
            _upd.Reset();
            _soundLatch = 0xff;
            _sres = 0xff;
            _mystwarrSoundCtrl = (UsesMystwarrSound || UsesMoomesaSound) ? (byte)2 : (byte)0;
            _mystwarrNmiClock = false;
            _irqAsserted = false;
            _nmiAsserted = UsesK053260Sound;
            _nmiBlockedCycles = 0;
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
            _tmnt2Z80WaitCycles = 0;
            _tmnt2WaitCyclesThisFrame = 0;
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
            _tmnt2Z80WaitCycles = 0;
            _tmnt2WaitCyclesThisFrame = 0;
            _audioProbe ??= TmntAudioProbe.TryCreate();
            _audioProbe?.BeginFrame(audioBuffer.Length);
            _audioTrace ??= TmntAudioTrace.TryCreate(_upd);
            _audioTrace?.BeginFrame(_soundFrameCounter);
        }

        public void RunMainCpuCycles(int mainCpuCycles, int mainCpuCyclesPerFrame)
        {
            if (_audioFrameBuffer is null || mainCpuCycles <= 0 || mainCpuCyclesPerFrame <= 0)
                return;

            _z80CycleAccumulator += mainCpuCycles * (CurrentAudioCpuCyclesPerFrame / (double)mainCpuCyclesPerFrame);
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
            RunAudioCpuCycles(CurrentAudioCpuCyclesPerFrame);
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

                _tmnt2Z80WaitCycles = 0;
                uint elapsed = _cpu.ExecuteInstruction(this);
                if (UsesK053260Sound && _tmnt2Z80WaitCycles > 0)
                {
                    elapsed += (uint)_tmnt2Z80WaitCycles;
                    _tmnt2WaitCyclesThisFrame += _tmnt2Z80WaitCycles;
                }
                _z80CycleAccumulator -= elapsed;
                _z80CyclesThisFrame += (int)elapsed;
                int audioClock = CurrentAudioCpuClock;
                _ym.AdvanceTimersByCpuCycles((int)elapsed, audioClock);
                if (UsesK053260Sound)
                    _k053260.AdvanceControlCycles((int)elapsed, audioClock, OnK053260Sh1);
                if (UsesMystwarrSound || UsesMoomesaSound)
                    _k054539_1.AdvanceTimerCycles((int)elapsed, audioClock, OnK054539Timer);
                _pendingRenderCycles += (int)elapsed;
                if (_nmiBlockArmedThisInstruction)
                {
                    _nmiBlockArmedThisInstruction = false;
                }
                else if (_nmiBlockedCycles > 0)
                {
                    _nmiBlockedCycles = Math.Max(0, _nmiBlockedCycles - (int)elapsed);
                }

                if (_cpu.LastInterruptAccepted)
                {
                    bool acceptedNmi = _cpu.Pc == 0x0066;
                    TraceAudioState($"z80-int-ack vector=0xFF nmiLine={(_nmiAsserted ? 1 : 0)} type={(acceptedNmi ? "nmi" : "irq")}");
                    if (_irqAsserted && !acceptedNmi)
                        _irqAsserted = false;
                }
            }
        }

        private void RenderElapsedAudioCycles(int elapsedCycles)
        {
            if (_audioFrameBuffer is not { } audioBuffer || elapsedCycles <= 0)
                return;

            if (UsesK053260Sound)
                _k053260.AdvanceStreamCycles(elapsedCycles, CurrentAudioCpuClock);

            double outputFramesPerZ80Cycle = OutputSampleRate / (double)CurrentAudioCpuClock;
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
            if (UsesMoomesaSound)
            {
                int ymIndex = _audioFrameSampleIndex;
                _ym.RenderStereo(audioBuffer, ref ymIndex, targetFrame, gain: Ym2151RouteGain, outputSampleRate: OutputSampleRate, routeToMono: false);
                _audioFrameSampleIndex = ymIndex;
                float gain = MystwarrK054539RouteGain * _k054321.OutputGain;
                _k054539_1.RenderStereo(audioBuffer, startFrame, targetFrame, gain: gain, outputSampleRate: OutputSampleRate, swapStereo: true);
                return;
            }

            if (UsesMystwarrSound)
            {
                float gain = MystwarrK054539RouteGain * _k054321.OutputGain;
                _k054539_1.RenderStereo(audioBuffer, startFrame, targetFrame, gain: gain, outputSampleRate: OutputSampleRate, swapStereo: true);
                _k054539_2.RenderStereo(audioBuffer, startFrame, targetFrame, gain: gain, outputSampleRate: OutputSampleRate, swapStereo: true);
                _audioFrameSampleIndex = Math.Max(_audioFrameSampleIndex, targetFrame);
                return;
            }

            if (UsesK053260Sound)
            {
                if (_audioProbe?.Enabled == true)
                {
                    short[] ym = _audioProbe.Ym;
                    short[] k053260 = _audioProbe.K053260;
                    int ymIndex = _audioFrameSampleIndex;
                    if (Tmnt2MuteYm2151)
                        ymIndex = targetFrame;
                    else
                        _ym.RenderStereo(ym, ref ymIndex, targetFrame, gain: Ym2151RouteGain, outputSampleRate: OutputSampleRate, routeToMono: false);
                    _audioFrameSampleIndex = ymIndex;
                    _k053260.RenderStereo(k053260, startFrame, targetFrame, gain: K053260RouteGain, outputSampleRate: OutputSampleRate);
                    MixStems(audioBuffer, startFrame, targetFrame, ym, k053260);
                }
                else
                {
                    int ymIndex = _audioFrameSampleIndex;
                    if (Tmnt2MuteYm2151)
                        ymIndex = targetFrame;
                    else
                        _ym.RenderStereo(audioBuffer, ref ymIndex, targetFrame, gain: Ym2151RouteGain, outputSampleRate: OutputSampleRate, routeToMono: false);
                    _audioFrameSampleIndex = ymIndex;
                    _k053260.RenderStereo(audioBuffer, startFrame, targetFrame, gain: K053260RouteGain, outputSampleRate: OutputSampleRate);
                }
                return;
            }

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

        public byte ReadOpcode(ushort address)
        {
            if (UsesMoomesaSound)
            {
                if (address < 0x8000)
                    return _program[address];
                if (address is >= 0x8000 and <= 0xbfff)
                    return _program[MystwarrBankedRomOffset(address)];
                return ReadMemoryMoomesa(address);
            }

            if (UsesMystwarrSound)
            {
                if (address < 0x8000)
                    return _program[address];
                if (address is >= 0x8000 and <= 0xbfff)
                    return _program[MystwarrBankedRomOffset(address)];
                return ReadMemoryMystwarr(address);
            }

            if (UsesK053260Sound)
            {
                AddTmnt2Z80Wait(address);
                if (address < 0xf000)
                    return _program[address];
                if (address is >= 0xf000 and <= 0xf7ff)
                    return _ram[address - 0xf000];
            }

            return ReadMemory(address);
        }

        public byte ReadMemory(ushort address)
        {
            if (UsesMoomesaSound)
                return ReadMemoryMoomesa(address);

            if (UsesMystwarrSound)
                return ReadMemoryMystwarr(address);

            if (UsesK053260Sound)
                return ReadMemoryTmnt2(address);

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
            if (UsesMoomesaSound)
            {
                WriteMemoryMoomesa(address, value);
                return;
            }

            if (UsesMystwarrSound)
            {
                WriteMemoryMystwarr(address, value);
                return;
            }

            if (UsesK053260Sound)
            {
                WriteMemoryTmnt2(address, value);
                return;
            }

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
        public InterruptLine Nmi() => _nmiAsserted ? InterruptLine.Low : InterruptLine.High;
        public InterruptLine Int() => _irqAsserted ? InterruptLine.Low : InterruptLine.High;
        public byte InterruptVector() => 0xff;
        public bool BusReq() => false;
        public bool Reset() => false;

        public byte K053260MainRead(int offset) => _k053260.MainRead(offset);

        public void K053260MainWrite(int offset, byte value) => _k053260.MainWrite(offset, value);

        private bool UsesK053260Sound => _variant is TmntHardwareVariant.Tmnt2 or TmntHardwareVariant.Ssriders;

        private bool UsesMystwarrSound => IsMystwarrFamily(_variant);

        private bool UsesMoomesaSound => _variant == TmntHardwareVariant.Moomesa;

        private int CurrentAudioCpuClock => UsesK053260Sound || UsesMystwarrSound || UsesMoomesaSound ? Tmnt2AudioCpuClock : AudioCpuClock;

        private int CurrentAudioCpuCyclesPerFrame => UsesK053260Sound || UsesMystwarrSound || UsesMoomesaSound ? Tmnt2AudioCpuCyclesPerFrame : AudioCpuCyclesPerFrame;

        private byte ReadMemoryMoomesa(ushort address)
        {
            if (address < 0x8000)
                return _program[address];
            if (address is >= 0x8000 and <= 0xbfff)
                return _program[MystwarrBankedRomOffset(address)];
            if (address is >= 0xc000 and <= 0xdfff)
                return _ram[address - 0xc000];
            if (address is >= 0xe000 and <= 0xe22f)
            {
                FlushPendingAudioStream();
                return _k054539_1.Read(address - 0xe000);
            }
            if (address is >= 0xec00 and <= 0xec01)
                return _ym.ReadStatus();
            if (address is >= 0xf000 and <= 0xf003)
                return _k054321.SoundRead(address - 0xf000);
            return 0xff;
        }

        private void WriteMemoryMoomesa(ushort address, byte value)
        {
            if (address is >= 0xc000 and <= 0xdfff)
            {
                _ram[address - 0xc000] = value;
                return;
            }
            if (address is >= 0xe000 and <= 0xe22f)
            {
                FlushPendingAudioStream();
                _k054539_1.Write(address - 0xe000, value, OnK054539Timer);
                return;
            }
            if (address is >= 0xec00 and <= 0xec01)
            {
                _ym.Write(address - 0xec00, value);
                _ymWrites++;
                return;
            }
            if (address is >= 0xf000 and <= 0xf003)
            {
                _k054321.SoundWrite(address - 0xf000, value);
                return;
            }
            if (address == 0xf800)
            {
                _mystwarrSoundCtrl = value;
                _nmiAsserted = false;
            }
        }

        private byte ReadMemoryMystwarr(ushort address)
        {
            if (address < 0x8000)
                return _program[address];
            if (address is >= 0x8000 and <= 0xbfff)
                return _program[MystwarrBankedRomOffset(address)];
            if (address is >= 0xc000 and <= 0xdfff)
                return _ram[address - 0xc000];
            if (address is >= 0xe000 and <= 0xe22f)
            {
                FlushPendingAudioStream();
                return _k054539_1.Read(address - 0xe000);
            }
            if (address is >= 0xe230 and <= 0xe3ff)
                return _mystwarrK054539Scratch1[address - 0xe230];
            if (address is >= 0xe400 and <= 0xe62f)
            {
                FlushPendingAudioStream();
                return _k054539_2.Read(address - 0xe400);
            }
            if (address is >= 0xe630 and <= 0xe7ff)
                return _mystwarrK054539Scratch2[address - 0xe630];
            if (address is >= 0xf000 and <= 0xf003)
                return _k054321.SoundRead(address - 0xf000);
            return 0xff;
        }

        private void WriteMemoryMystwarr(ushort address, byte value)
        {
            if (address is >= 0xc000 and <= 0xdfff)
            {
                _ram[address - 0xc000] = value;
                return;
            }
            if (address is >= 0xe000 and <= 0xe22f)
            {
                FlushPendingAudioStream();
                _k054539_1.Write(address - 0xe000, value, OnK054539Timer);
                return;
            }
            if (address is >= 0xe230 and <= 0xe3ff)
            {
                _mystwarrK054539Scratch1[address - 0xe230] = value;
                return;
            }
            if (address is >= 0xe400 and <= 0xe62f)
            {
                FlushPendingAudioStream();
                _k054539_2.Write(address - 0xe400, value);
                return;
            }
            if (address is >= 0xe630 and <= 0xe7ff)
            {
                _mystwarrK054539Scratch2[address - 0xe630] = value;
                return;
            }
            if (address is >= 0xf000 and <= 0xf003)
            {
                _k054321.SoundWrite(address - 0xf000, value);
                return;
            }
            if (address == 0xf800)
            {
                _mystwarrSoundCtrl = value;
                _nmiAsserted = false;
                TraceAudioState($"mystwarr sound-ctrl value=0x{value:X2} bank={value & 0x0f}");
            }
        }

        private int MystwarrBankedRomOffset(ushort address)
            => (((_mystwarrSoundCtrl & 0x0f) * 0x4000) + (address - 0x8000)) & (_program.Length - 1);

        public byte K054321MainRead(int offset) => _k054321.MainRead(offset);

        public void K054321MainWrite(int offset, byte value) => _k054321.MainWrite(offset, value);

        private void OnK054539Timer(bool state)
        {
            if ((_mystwarrSoundCtrl & 0x10) != 0 && !_mystwarrNmiClock && state)
                _nmiAsserted = true;
            _mystwarrNmiClock = state;
        }

        private byte ReadMemoryTmnt2(ushort address)
        {
            if (address < 0xf000)
            {
                AddTmnt2Z80Wait(address);
                return _program[address];
            }
            if (address is >= 0xf000 and <= 0xf7ff)
            {
                AddTmnt2Z80Wait(address);
                return _ram[address - 0xf000];
            }
            if (address is >= 0xf800 and <= 0xf801)
            {
                FlushPendingAudioStream();
                return _ym.ReadStatus();
            }
            if (address is >= 0xfa00 and <= 0xfa2f)
            {
                int offset = address - 0xfa00;
                if (offset == 0x29)
                    FlushPendingAudioStream();
                byte value = _k053260.Read(offset);
                TraceAudioState($"read k053260 off=0x{offset:X2} value=0x{value:X2} state={_k053260.DebugSummary}");
                return value;
            }
            return 0xff;
        }

        private void WriteMemoryTmnt2(ushort address, byte value)
        {
            if (address is >= 0xf000 and <= 0xf7ff)
            {
                AddTmnt2Z80Wait(address);
                _ram[address - 0xf000] = value;
                return;
            }
            if (address is >= 0xf800 and <= 0xf801)
            {
                FlushPendingAudioStream();
                TraceAudioEvent($"ym2151 write off={address - 0xf800} value=0x{value:X2}");
                TraceAudioState($"write ym2151 off={address - 0xf800} value=0x{value:X2}");
                _ym.Write(address - 0xf800, value);
                _ymWrites++;
                return;
            }
            if (address is >= 0xfa00 and <= 0xfa2f)
            {
                FlushPendingAudioStream();
                _k053260.Write(address - 0xfa00, value);
                if (_k053260.TryConsumeLastEvent(out string eventText))
                    TraceAudioEvent(eventText);
                TraceAudioState($"write k053260 off=0x{address - 0xfa00:X2} value=0x{value:X2} state={_k053260.DebugSummary}");
                _pcmWrites++;
                return;
            }
            if (address == 0xfc00)
            {
                _nmiAsserted = false;
                _nmiBlockedCycles = 4;
                _nmiBlockArmedThisInstruction = true;
            }
        }

        private void OnK053260Sh1(bool asserted)
        {
            if (asserted && _nmiBlockedCycles == 0)
                _nmiAsserted = true;
        }

        private void AddTmnt2Z80Wait(ushort address)
        {
            if (Tmnt2Z80MemoryWaitCycles != 0 && address < 0xf800)
                _tmnt2Z80WaitCycles += Tmnt2Z80MemoryWaitCycles;
        }

        private void FlushPendingAudioStream()
        {
            if (_pendingRenderCycles <= 0)
                return;

            RenderElapsedAudioCycles(_pendingRenderCycles);
            _pendingRenderCycles = 0;
        }

        private static int ParseEnvInt(string name, int defaultValue, int minValue, int maxValue)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!int.TryParse(value, out int parsed))
                return defaultValue;
            return Math.Clamp(parsed, minValue, maxValue);
        }

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

        private static void MixStems(
            short[] destination,
            int startFrame,
            int targetFrame,
            short[] ym,
            short[] k053260)
        {
            int start = Math.Clamp(startFrame * 2, 0, destination.Length);
            int end = Math.Clamp(targetFrame * 2, start, destination.Length);
            for (int i = start; i < end; i++)
                destination[i] = Mix(ym[i], k053260[i]);
        }

        private sealed class TmntAudioProbe
        {
            private readonly string _directory;
            private StreamWriter? _events;
            private FileStream? _mixStream;
            private FileStream? _ymStream;
            private FileStream? _pcmStream;
            private FileStream? _k053260Stream;
            private FileStream? _updStream;
            private FileStream? _titleStream;

            private TmntAudioProbe(string directory)
            {
                _directory = directory;
            }

            public bool Enabled => true;
            public short[] Ym { get; private set; } = Array.Empty<short>();
            public short[] K007232 { get; private set; } = Array.Empty<short>();
            public short[] K053260 { get; private set; } = Array.Empty<short>();
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
                Array.Clear(K053260);
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
                WriteRaw(_k053260Stream, K053260);
                WriteRaw(_updStream, Upd7759);
                WriteRaw(_titleStream, Title);
                _events?.WriteLine(
                    $"frame={frame} end z80cyc={z80Cycles} mixPeak={peak} ymPeak={Peak(Ym)} k007Peak={Peak(K007232)} k053260Peak={Peak(K053260)} updPeak={Peak(Upd7759)} titlePeak={Peak(Title)}");
                _events?.Flush();
            }

            private void EnsureBuffers(int samples)
            {
                if (Ym.Length == samples)
                    return;

                Ym = new short[samples];
                K007232 = new short[samples];
                K053260 = new short[samples];
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
                _k053260Stream ??= OpenRaw("k053260_s16le.raw");
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

    private sealed class K054321SoundInterface
    {
        private byte _mainToSound0;
        private byte _mainToSound1;
        private byte _soundToMain;
        private byte _volume;
        private byte _active;
        private int _mainWrites;
        private int _soundReads;

        public float OutputGain
        {
            get
            {
                if ((_active & 0x03) == 0)
                    return 0.0f;
                return (float)Math.Pow(2.0, (_volume - 40) / 10.0);
            }
        }

        public string DebugSummary => $"m2s=0x{_mainToSound0:X2}/0x{_mainToSound1:X2} s2m=0x{_soundToMain:X2} vol={_volume} act=0x{_active:X2} mw={_mainWrites} sr={_soundReads}";

        public void Reset()
        {
            _mainToSound0 = 0;
            _mainToSound1 = 0;
            _soundToMain = 0;
            _volume = 0;
            _active = 0;
            _mainWrites = 0;
            _soundReads = 0;
        }

        public byte MainRead(int offset)
        {
            offset &= 0x0f;
            return offset switch
            {
                0x08 => 0x00,
                0x0a => _soundToMain,
                _ => 0xff
            };
        }

        public void MainWrite(int offset, byte value)
        {
            offset &= 0x0f;
            switch (offset)
            {
                case 0x00:
                    _active = value;
                    break;
                case 0x02:
                    _volume = 0;
                    break;
                case 0x03:
                    if (value != 0 && _volume < 64)
                        _volume++;
                    break;
                case 0x06:
                    _mainToSound0 = value;
                    _mainWrites++;
                    break;
                case 0x07:
                    _mainToSound1 = value;
                    _mainWrites++;
                    break;
            }
        }

        public byte SoundRead(int offset)
        {
            offset &= 3;
            _soundReads++;
            return offset switch
            {
                2 => _mainToSound0,
                3 => _mainToSound1,
                _ => 0xff
            };
        }

        public void SoundWrite(int offset, byte value)
        {
            offset &= 3;
            if (offset == 0)
                _soundToMain = value;
        }
    }

    // BSD-3-Clause semantic port of MAME's k054539_device for Mystic Warriors.
    private sealed class K054539Pcm
    {
        private const int ChipClock = 18_432_000;
        private const int SourceSampleRate = ChipClock / 384;
        private static readonly int[] DpcmDelta =
        {
            0 * 0x100, 1 * 0x100, 2 * 0x100, 4 * 0x100,
            8 * 0x100, 16 * 0x100, 32 * 0x100, 64 * 0x100,
            0 * 0x100, -64 * 0x100, -32 * 0x100, -16 * 0x100,
            -8 * 0x100, -4 * 0x100, -2 * 0x100, -1 * 0x100
        };
        private static readonly double[] VolumeTable = BuildVolumeTable();
        private static readonly double[] PanTable = BuildPanTable();

        [NonSerialized] private byte[] _rom = Array.Empty<byte>();
        private readonly byte[] _regs = new byte[0x230];
        private readonly byte[] _ram = new byte[0x8000];
        private readonly short[] _reverb = new short[0x2000];
        private readonly byte[,] _posLatch = new byte[8, 3];
        private readonly Channel[] _channels;
        private readonly double[] _gain = new double[8];
        private double _sourcePhase;
        private double _lastSourceLeft;
        private double _lastSourceRight;
        private int _reverbPos;
        private int _curPtr;
        private int _romAddr;
        private int _timerState;
        private double _timerCycleAccumulator;
        private double _timerPeriodChipCycles;
        private int _keyOns;
        private int _keyOffs;
        private int _writes;
        private int _reads;
        private int _lastPeak;

        public K054539Pcm()
        {
            _channels = new Channel[8];
            for (int i = 0; i < _channels.Length; i++)
                _channels[i] = new Channel();
            Array.Fill(_gain, 1.0);
        }

        public string DebugSummary
            => $"w={_writes} r={_reads} key={_regs[0x22c]:X2} ko={_keyOns} kf={_keyOffs} ctl={_regs[0x22f]:X2} rom={_romAddr:X2}:{_curPtr:X5} pk={_lastPeak}";

        public void Load(byte[] rom)
        {
            _rom = rom;
            Reset();
        }

        public void Reset()
        {
            Array.Clear(_regs);
            Array.Clear(_ram);
            Array.Clear(_reverb);
            Array.Clear(_posLatch);
            foreach (Channel channel in _channels)
                channel.Reset();
            _sourcePhase = 0;
            _lastSourceLeft = 0;
            _lastSourceRight = 0;
            _reverbPos = 0;
            _curPtr = 0;
            _romAddr = 0;
            _timerState = 0;
            _timerCycleAccumulator = 0;
            _timerPeriodChipCycles = 0;
            _keyOns = _keyOffs = _writes = _reads = _lastPeak = 0;
        }

        public void SaveState(BinaryWriter writer)
        {
            WriteByteArray(writer, _regs);
            WriteByteArray(writer, _ram);
            writer.Write(_reverb.Length);
            for (int i = 0; i < _reverb.Length; i++)
                writer.Write(_reverb[i]);
            for (int ch = 0; ch < 8; ch++)
                for (int i = 0; i < 3; i++)
                    writer.Write(_posLatch[ch, i]);
            foreach (Channel channel in _channels)
                channel.SaveState(writer);
            writer.Write(_gain.Length);
            for (int i = 0; i < _gain.Length; i++)
                writer.Write(_gain[i]);
            writer.Write(_sourcePhase);
            writer.Write(_lastSourceLeft);
            writer.Write(_lastSourceRight);
            writer.Write(_reverbPos);
            writer.Write(_curPtr);
            writer.Write(_romAddr);
            writer.Write(_timerState);
            writer.Write(_timerCycleAccumulator);
            writer.Write(_timerPeriodChipCycles);
            writer.Write(_keyOns);
            writer.Write(_keyOffs);
            writer.Write(_writes);
            writer.Write(_reads);
            writer.Write(_lastPeak);
        }

        public void LoadState(BinaryReader reader)
        {
            ReadByteArray(reader, _regs);
            ReadByteArray(reader, _ram);
            int reverbLength = reader.ReadInt32();
            if (reverbLength != _reverb.Length)
                throw new InvalidDataException($"K054539 reverb length mismatch: {reverbLength}.");
            for (int i = 0; i < _reverb.Length; i++)
                _reverb[i] = reader.ReadInt16();
            for (int ch = 0; ch < 8; ch++)
                for (int i = 0; i < 3; i++)
                    _posLatch[ch, i] = reader.ReadByte();
            foreach (Channel channel in _channels)
                channel.LoadState(reader);
            int gainLength = reader.ReadInt32();
            if (gainLength != _gain.Length)
                throw new InvalidDataException($"K054539 gain length mismatch: {gainLength}.");
            for (int i = 0; i < _gain.Length; i++)
                _gain[i] = reader.ReadDouble();
            _sourcePhase = reader.ReadDouble();
            _lastSourceLeft = reader.ReadDouble();
            _lastSourceRight = reader.ReadDouble();
            _reverbPos = reader.ReadInt32() & 0x1fff;
            _curPtr = reader.ReadInt32() & 0x1ffff;
            _romAddr = reader.ReadInt32();
            _timerState = reader.ReadInt32();
            _timerCycleAccumulator = reader.ReadDouble();
            _timerPeriodChipCycles = reader.ReadDouble();
            _keyOns = reader.ReadInt32();
            _keyOffs = reader.ReadInt32();
            _writes = reader.ReadInt32();
            _reads = reader.ReadInt32();
            _lastPeak = reader.ReadInt32();
        }

        public void SetGain(int channel, double gain)
        {
            if ((uint)channel < _gain.Length)
                _gain[channel] = gain;
        }

        public byte Read(int offset)
        {
            if ((uint)offset >= _regs.Length)
                return 0xff;
            _reads++;
            if (offset == 0x22d)
            {
                if ((_regs[0x22f] & 0x10) == 0)
                    return 0;

                byte value;
                if (_romAddr == 0x80)
                {
                    int addr = (_curPtr & 0x3fff) | ((_curPtr & 0x10000) >> 2);
                    value = _ram[addr & (_ram.Length - 1)];
                }
                else
                {
                    value = ReadRom(0x20000 * _romAddr + _curPtr);
                }
                _curPtr = (_curPtr + 1) & 0x1ffff;
                return value;
            }

            return _regs[offset];
        }

        public void Write(int offset, byte value, Action<bool>? timerHandler = null)
        {
            if ((uint)offset >= _regs.Length)
                return;
            _writes++;

            bool latch = (_regs[0x22f] & 0x01) != 0;
            if (latch && offset < 0x100)
            {
                int latchOffset = (offset & 0x1f) - 0x0c;
                if ((uint)latchOffset <= 2)
                {
                    _posLatch[offset >> 5, latchOffset] = value;
                    return;
                }
            }

            switch (offset)
            {
                case 0x214:
                    for (int ch = 0; ch < 8; ch++)
                    {
                        if ((value & (1 << ch)) == 0)
                            continue;
                        if (latch)
                        {
                            int baseOffset = ch << 5;
                            _regs[baseOffset + 0x0c] = _posLatch[ch, 0];
                            _regs[baseOffset + 0x0d] = _posLatch[ch, 1];
                            _regs[baseOffset + 0x0e] = _posLatch[ch, 2];
                        }
                        KeyOn(ch);
                    }
                    break;

                case 0x215:
                    for (int ch = 0; ch < 8; ch++)
                        if ((value & (1 << ch)) != 0)
                            KeyOff(ch);
                    break;

                case 0x227:
                    _timerState = 0;
                    _timerCycleAccumulator = 0;
                    _timerPeriodChipCycles = (2.0 * 384.0 * 14_400.0) / Math.Max(1, 38 + value);
                    timerHandler?.Invoke(false);
                    break;

                case 0x22d:
                    if (_romAddr == 0x80)
                    {
                        int addr = (_curPtr & 0x3fff) | ((_curPtr & 0x10000) >> 2);
                        _ram[addr & (_ram.Length - 1)] = value;
                    }
                    _curPtr = (_curPtr + 1) & 0x1ffff;
                    break;

                case 0x22e:
                    _romAddr = value;
                    _curPtr = 0;
                    break;

                case 0x22f:
                    if ((value & 0x20) == 0)
                    {
                        _timerState = 0;
                        timerHandler?.Invoke(false);
                    }
                    break;
            }

            _regs[offset] = value;
        }

        public void AdvanceTimerCycles(int audioCpuCycles, int audioCpuClock, Action<bool> timerHandler)
        {
            if (audioCpuCycles <= 0 || audioCpuClock <= 0 || _timerPeriodChipCycles <= 0 || (_regs[0x22f] & 0x20) == 0)
                return;

            _timerCycleAccumulator += audioCpuCycles * (ChipClock / (double)audioCpuClock);
            while (_timerCycleAccumulator >= _timerPeriodChipCycles)
            {
                _timerState ^= 1;
                timerHandler(_timerState != 0);
                _timerCycleAccumulator -= _timerPeriodChipCycles;
            }
        }

        public void RenderStereo(short[] destination, int startFrame, int targetFrame, float gain, int outputSampleRate, bool swapStereo)
        {
            if (_rom.Length == 0 || destination.Length == 0 || (_regs[0x22f] & 0x01) == 0)
                return;

            int maxFrames = destination.Length / 2;
            startFrame = Math.Clamp(startFrame, 0, maxFrames);
            targetFrame = Math.Clamp(targetFrame, startFrame, maxFrames);
            if (targetFrame <= startFrame)
                return;

            double step = SourceSampleRate / (double)outputSampleRate;
            int peak = 0;
            for (int frame = startFrame; frame < targetFrame; frame++)
            {
                _sourcePhase += step;
                while (_sourcePhase >= 1.0)
                {
                    GenerateSourceFrame(out _lastSourceLeft, out _lastSourceRight);
                    _sourcePhase -= 1.0;
                }

                double left = _lastSourceLeft;
                double right = _lastSourceRight;
                if (swapStereo)
                    (left, right) = (right, left);
                int mixLeft = (int)Math.Round(left * gain);
                int mixRight = (int)Math.Round(right * gain);
                int offset = frame * 2;
                destination[offset] = Mix(destination[offset], mixLeft);
                destination[offset + 1] = Mix(destination[offset + 1], mixRight);
                peak = Math.Max(peak, Math.Max(Math.Abs(mixLeft), Math.Abs(mixRight)));
            }
            _lastPeak = peak;
        }

        private void GenerateSourceFrame(out double left, out double right)
        {
            left = _reverb[_reverbPos];
            right = _reverb[_reverbPos];
            _reverb[_reverbPos] = 0;
            for (int ch = 0; ch < 8; ch++)
            {
                if ((_regs[0x22c] & (1 << ch)) == 0)
                    continue;
                PlayChannel(ch, ref left, ref right);
            }
            _reverbPos = (_reverbPos + 1) & 0x1fff;
        }

        private void PlayChannel(int ch, ref double left, ref double right)
        {
            int base1 = ch << 5;
            int base2 = 0x200 + (ch << 1);
            int delta = _regs[base1] | (_regs[base1 + 1] << 8) | (_regs[base1 + 2] << 16);
            int vol = _regs[base1 + 3];
            int reverbVol = Math.Min(255, vol + _regs[base1 + 4]);
            int pan = DecodePan(_regs[base1 + 5]);
            double lvol = Math.Min(1.8, VolumeTable[vol] * PanTable[pan] * _gain[ch]);
            double rvol = Math.Min(1.8, VolumeTable[vol] * PanTable[0x0e - pan] * _gain[ch]);
            double rbvol = Math.Min(1.8, VolumeTable[reverbVol] * _gain[ch] / 2.0);
            int reverbDelta = ((_regs[base1 + 6] | (_regs[base1 + 7] << 8)) >> 3) & 0x3fff;
            int curPos = _regs[base1 + 0x0c] | (_regs[base1 + 0x0d] << 8) | (_regs[base1 + 0x0e] << 16);

            Channel channel = _channels[ch];
            int curPfrac;
            int curVal;
            int curPval;
            if (curPos != channel.Pos)
            {
                channel.Pos = curPos;
                curPfrac = 0;
                curVal = 0;
                curPval = 0;
            }
            else
            {
                curPfrac = channel.Pfrac;
                curVal = channel.Val;
                curPval = channel.Pval;
            }

            int pdelta;
            int fdelta;
            if ((_regs[base2] & 0x20) != 0)
            {
                delta = -delta;
                fdelta = 0x10000;
                pdelta = -1;
            }
            else
            {
                fdelta = -0x10000;
                pdelta = 1;
            }

            switch (_regs[base2] & 0x0c)
            {
                case 0x00:
                    Step8Bit(ch, base1, base2, delta, fdelta, pdelta, ref curPos, ref curPfrac, ref curVal, ref curPval);
                    break;
                case 0x04:
                    Step16Bit(ch, base1, base2, delta, fdelta, pdelta << 1, ref curPos, ref curPfrac, ref curVal, ref curPval);
                    break;
                case 0x08:
                    StepDpcm(ch, base1, base2, delta, fdelta, pdelta, ref curPos, ref curPfrac, ref curVal, ref curPval);
                    break;
            }

            left += curVal * lvol;
            right += curVal * rvol;
            int reverbIndex = (reverbDelta + _reverbPos) & 0x1fff;
            _reverb[reverbIndex] = (short)Math.Clamp(_reverb[reverbIndex] + (curVal * rbvol), short.MinValue, short.MaxValue);
            channel.Pos = curPos;
            channel.Pfrac = curPfrac;
            channel.Pval = curPval;
            channel.Val = curVal;
            if (((_regs[0x22f] & 0x80) == 0))
            {
                _regs[base1 + 0x0c] = (byte)curPos;
                _regs[base1 + 0x0d] = (byte)(curPos >> 8);
                _regs[base1 + 0x0e] = (byte)(curPos >> 16);
            }
        }

        private void Step8Bit(int ch, int base1, int base2, int delta, int fdelta, int pdelta, ref int curPos, ref int curPfrac, ref int curVal, ref int curPval)
        {
            curPfrac += delta;
            while (curPfrac < 0 || curPfrac > 0xffff)
            {
                curPfrac += fdelta;
                curPos += pdelta;
                curPval = curVal;
                curVal = unchecked((short)(ReadRom(curPos) << 8));
                if (curVal == short.MinValue)
                {
                    if ((_regs[base2 + 1] & 1) != 0)
                    {
                        curPos = LoopAddress(base1);
                        curVal = unchecked((short)(ReadRom(curPos) << 8));
                    }
                    if (curVal == short.MinValue)
                    {
                        KeyOff(ch);
                        curVal = 0;
                        break;
                    }
                }
            }
        }

        private void Step16Bit(int ch, int base1, int base2, int delta, int fdelta, int pdelta, ref int curPos, ref int curPfrac, ref int curVal, ref int curPval)
        {
            curPfrac += delta;
            while (curPfrac < 0 || curPfrac > 0xffff)
            {
                curPfrac += fdelta;
                curPos += pdelta;
                curPval = curVal;
                curVal = unchecked((short)(ReadRom(curPos) | (ReadRom(curPos + 1) << 8)));
                if (curVal == short.MinValue)
                {
                    if ((_regs[base2 + 1] & 1) != 0)
                    {
                        curPos = LoopAddress(base1);
                        curVal = unchecked((short)(ReadRom(curPos) | (ReadRom(curPos + 1) << 8)));
                    }
                    if (curVal == short.MinValue)
                    {
                        KeyOff(ch);
                        curVal = 0;
                        break;
                    }
                }
            }
        }

        private void StepDpcm(int ch, int base1, int base2, int delta, int fdelta, int pdelta, ref int curPos, ref int curPfrac, ref int curVal, ref int curPval)
        {
            curPos <<= 1;
            curPfrac <<= 1;
            if ((curPfrac & 0x10000) != 0)
            {
                curPfrac &= 0xffff;
                curPos |= 1;
            }

            curPfrac += delta;
            while (curPfrac < 0 || curPfrac > 0xffff)
            {
                curPfrac += fdelta;
                curPos += pdelta;
                curPval = curVal;
                int raw = ReadRom(curPos >> 1);
                if (raw == 0x88)
                {
                    if ((_regs[base2 + 1] & 1) != 0)
                    {
                        curPos = LoopAddress(base1) << 1;
                        raw = ReadRom(curPos >> 1);
                    }
                    if (raw == 0x88)
                    {
                        KeyOff(ch);
                        curVal = 0;
                        break;
                    }
                }
                int nibble = (curPos & 1) != 0 ? raw >> 4 : raw & 0x0f;
                curVal = Math.Clamp(curPval + DpcmDelta[nibble], short.MinValue, short.MaxValue);
            }

            curPfrac >>= 1;
            if ((curPos & 1) != 0)
                curPfrac |= 0x8000;
            curPos >>= 1;
        }

        private void KeyOn(int ch)
        {
            if ((_regs[0x22f] & 0x80) == 0)
                _regs[0x22c] |= (byte)(1 << ch);
            _channels[ch].Reset();
            _keyOns++;
        }

        private void KeyOff(int ch)
        {
            if ((_regs[0x22f] & 0x80) == 0)
                _regs[0x22c] &= (byte)~(1 << ch);
            _keyOffs++;
        }

        private int LoopAddress(int base1)
            => _regs[base1 + 0x08] | (_regs[base1 + 0x09] << 8) | (_regs[base1 + 0x0a] << 16);

        private byte ReadRom(int address)
            => _rom.Length == 0 ? (byte)0 : _rom[address & (_rom.Length - 1)];

        private static int DecodePan(byte value)
        {
            if (value is >= 0x81 and <= 0x8f)
                return value - 0x81;
            if (value is >= 0x11 and <= 0x1f)
                return value - 0x11;
            return 0x18 - 0x11;
        }

        private static double[] BuildVolumeTable()
        {
            double[] table = new double[256];
            for (int i = 0; i < table.Length; i++)
                table[i] = Math.Pow(10.0, (-36.0 * i / 0x40) / 20.0) / 4.0;
            return table;
        }

        private static double[] BuildPanTable()
        {
            double[] table = new double[0x0f];
            for (int i = 0; i < table.Length; i++)
                table[i] = Math.Sqrt(i) / Math.Sqrt(0x0e);
            return table;
        }

        private static short Mix(short current, int sample)
            => (short)Math.Clamp(current + sample, short.MinValue, short.MaxValue);

        private static void WriteByteArray(BinaryWriter writer, byte[] data)
        {
            writer.Write(data.Length);
            writer.Write(data);
        }

        private static void ReadByteArray(BinaryReader reader, byte[] destination)
        {
            int length = reader.ReadInt32();
            if (length != destination.Length)
                throw new InvalidDataException($"K054539 byte array length mismatch: {length}.");
            int read = reader.Read(destination, 0, destination.Length);
            if (read != destination.Length)
                throw new EndOfStreamException("Unexpected end of K054539 byte array.");
        }

        private sealed class Channel
        {
            public int Pos;
            public int Pfrac;
            public int Val;
            public int Pval;

            public void Reset()
            {
                Pos = 0;
                Pfrac = 0;
                Val = 0;
                Pval = 0;
            }

            public void SaveState(BinaryWriter writer)
            {
                writer.Write(Pos);
                writer.Write(Pfrac);
                writer.Write(Val);
                writer.Write(Pval);
            }

            public void LoadState(BinaryReader reader)
            {
                Pos = reader.ReadInt32();
                Pfrac = reader.ReadInt32();
                Val = reader.ReadInt32();
                Pval = reader.ReadInt32();
            }
        }
    }

    // Minimal K007232 PCM translation from MAME's BSD-3-Clause k007232 device.
    // BSD-3-Clause semantic port of MAME's k053260_device for TMNT2 / Sunset Riders hardware.
    private sealed class K053260Pcm
    {
        private const int ChipClock = 3_579_545;
        private const int ClocksPerSample = 64;
        private const int SourceSampleRate = ChipClock / ClocksPerSample;
        private const double DcBlockerR = 0.995;
        private static readonly bool EnableDcBlocker =
            Environment.GetEnvironmentVariable("EUTHERDRIVE_TMNT2_K053260_DC_BLOCK") == "1";
        private static readonly int MuteMask =
            ParseMuteMask();
        private static readonly sbyte[] KadpcmDeltaTable =
            { 0, 1, 2, 4, 8, 16, 32, 64, -128, -64, -32, -16, -8, -4, -2, -1 };

        private static readonly int[,] PanMul =
        {
            {     0,     0 },
            { 65536,     0 },
            { 59870, 26656 },
            { 53684, 37950 },
            { 46341, 46341 },
            { 37950, 53684 },
            { 26656, 59870 },
            {     0, 65536 },
        };

        [NonSerialized] private byte[] _rom = Array.Empty<byte>();
        private readonly byte[] _portData = new byte[4];
        private readonly Voice[] _voices;
        private byte _keyOn;
        private byte _mode;
        private int _timerState;
        private double _controlCycleAccumulator;
        private double _streamCycleAccumulator;
        private double _resamplePhase;
        private int _lastLeft;
        private int _lastRight;
        private int _nextLeft;
        private int _nextRight;
        private readonly Queue<int> _queuedLeft = new();
        private readonly Queue<int> _queuedRight = new();
        private double _dcLeftX;
        private double _dcLeftY;
        private double _dcRightX;
        private double _dcRightY;
        private bool _primed;
        private int _mainReads;
        private int _mainWrites;
        private int _subReads;
        private int _subWrites;
        private int _keyOns;
        private int _lastPeak;
        [NonSerialized] private string? _lastEvent;

        public K053260Pcm()
        {
            _voices = new[] { new Voice(this), new Voice(this), new Voice(this), new Voice(this) };
        }

        public string DebugSummary
            => $"mr={_mainReads} mw={_mainWrites} sr={_subReads} sw={_subWrites} mode=0x{_mode:X2} key=0x{_keyOn:X2} pcm=s8 dc={(EnableDcBlocker ? 1 : 0)} mute=0x{MuteMask:X1} "
               + $"play={(_voices[0].Playing ? 1 : 0)}{(_voices[1].Playing ? 1 : 0)}{(_voices[2].Playing ? 1 : 0)}{(_voices[3].Playing ? 1 : 0)} "
               + $"ko={_keyOns} pk={_lastPeak} p={_portData[0]:X2}/{_portData[1]:X2}/{_portData[2]:X2}/{_portData[3]:X2} "
               + $"v0={_voices[0].DebugSummary} v1={_voices[1].DebugSummary} v2={_voices[2].DebugSummary} v3={_voices[3].DebugSummary}";

        public void Load(byte[] rom)
        {
            _rom = rom;
            Reset();
        }

        public void Reset()
        {
            Array.Clear(_portData);
            foreach (Voice voice in _voices)
                voice.Reset();
            _keyOn = 0;
            _mode = 0;
            _timerState = 0;
            _controlCycleAccumulator = 0;
            _streamCycleAccumulator = 0;
            _resamplePhase = 0;
            _lastLeft = _lastRight = _nextLeft = _nextRight = 0;
            _queuedLeft.Clear();
            _queuedRight.Clear();
            _dcLeftX = _dcLeftY = _dcRightX = _dcRightY = 0;
            _primed = false;
            _mainReads = _mainWrites = _subReads = _subWrites = _keyOns = _lastPeak = 0;
            _lastEvent = null;
        }

        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_portData);
            writer.Write(_keyOn);
            writer.Write(_mode);
            writer.Write(_timerState);
            writer.Write(_controlCycleAccumulator);
            writer.Write(_streamCycleAccumulator);
            writer.Write(_resamplePhase);
            writer.Write(_lastLeft);
            writer.Write(_lastRight);
            writer.Write(_nextLeft);
            writer.Write(_nextRight);
            writer.Write(_dcLeftX);
            writer.Write(_dcLeftY);
            writer.Write(_dcRightX);
            writer.Write(_dcRightY);
            writer.Write(_primed);
            writer.Write(_mainReads);
            writer.Write(_mainWrites);
            writer.Write(_subReads);
            writer.Write(_subWrites);
            writer.Write(_keyOns);
            writer.Write(_lastPeak);
            for (int i = 0; i < _voices.Length; i++)
                _voices[i].SaveState(writer);
        }

        public void LoadState(BinaryReader reader, int version)
        {
            byte[] portData = reader.ReadBytes(_portData.Length);
            if (portData.Length == _portData.Length)
                Buffer.BlockCopy(portData, 0, _portData, 0, _portData.Length);
            _keyOn = reader.ReadByte();
            _mode = reader.ReadByte();
            _timerState = reader.ReadInt32();
            _controlCycleAccumulator = reader.ReadDouble();
            _streamCycleAccumulator = reader.ReadDouble();
            _resamplePhase = version >= 3 ? reader.ReadDouble() : 0;
            _lastLeft = reader.ReadInt32();
            _lastRight = reader.ReadInt32();
            _nextLeft = reader.ReadInt32();
            _nextRight = reader.ReadInt32();
            if (version >= 2)
            {
                _dcLeftX = reader.ReadDouble();
                _dcLeftY = reader.ReadDouble();
                _dcRightX = reader.ReadDouble();
                _dcRightY = reader.ReadDouble();
            }
            else
            {
                _dcLeftX = _dcLeftY = _dcRightX = _dcRightY = 0;
            }
            _primed = reader.ReadBoolean();
            _mainReads = reader.ReadInt32();
            _mainWrites = reader.ReadInt32();
            _subReads = reader.ReadInt32();
            _subWrites = reader.ReadInt32();
            _keyOns = reader.ReadInt32();
            _lastPeak = reader.ReadInt32();
            for (int i = 0; i < _voices.Length; i++)
                _voices[i].LoadState(reader);
            _primed = false;
            _queuedLeft.Clear();
            _queuedRight.Clear();
            _lastEvent = null;
        }

        public bool TryConsumeLastEvent(out string eventText)
        {
            if (_lastEvent is null)
            {
                eventText = string.Empty;
                return false;
            }

            eventText = _lastEvent;
            _lastEvent = null;
            return true;
        }

        public byte MainRead(int offset)
        {
            _mainReads++;
            return _portData[2 + (offset & 1)];
        }

        public void MainWrite(int offset, byte data)
        {
            _mainWrites++;
            _portData[offset & 1] = data;
        }

        public byte Read(int offset)
        {
            offset &= 0x3f;
            _subReads++;
            return offset switch
            {
                0x00 or 0x01 => _portData[offset],
                0x29 => VoiceStatus(),
                0x2e => (_mode & 1) != 0 ? _voices[0].ReadRom(sideEffects: true) : (byte)0,
                _ => 0
            };
        }

        public void Write(int offset, byte data)
        {
            offset &= 0x3f;
            _subWrites++;

            if (offset is >= 0x08 and <= 0x27)
            {
                _voices[(offset - 8) / 8].SetRegister(offset, data);
                return;
            }

            switch (offset)
            {
                case 0x02:
                case 0x03:
                    _portData[offset] = data;
                    break;

                case 0x28:
                {
                    byte rising = (byte)(data & ~_keyOn);
                    string? keyOnEvent = rising != 0 ? $"k053260 keyon data=0x{data:X2} rising=0x{rising:X2}" : null;
                    for (int i = 0; i < 4; i++)
                    {
                        _voices[i].Reverse = (data & (1 << (i + 4))) != 0;
                        if ((rising & (1 << i)) != 0)
                        {
                            _voices[i].KeyOn();
                            _keyOns++;
                            keyOnEvent += $" v{i}={_voices[i].KeyOnSummary}";
                        }
                        else if ((data & (1 << i)) == 0)
                        {
                            _voices[i].KeyOff();
                        }
                    }
                    _keyOn = data;
                    if (keyOnEvent is not null)
                        _lastEvent = keyOnEvent;
                    break;
                }

                case 0x2a:
                    for (int i = 0; i < 4; i++)
                    {
                        _voices[i].Loop = (data & (1 << i)) != 0;
                        _voices[i].Kadpcm = (data & (1 << (i + 4))) != 0;
                    }
                    break;

                case 0x2c:
                    _voices[0].SetPan(data);
                    _voices[1].SetPan(data >> 3);
                    break;

                case 0x2d:
                    _voices[2].SetPan(data);
                    _voices[3].SetPan(data >> 3);
                    break;

                case 0x2f:
                    _mode = data;
                    break;
            }
        }

        public void AdvanceControlCycles(int audioCpuCycles, int audioCpuClock, Action<bool> sh1Callback)
        {
            if (audioCpuCycles <= 0 || audioCpuClock <= 0)
                return;

            _controlCycleAccumulator += audioCpuCycles * (ChipClock / (double)audioCpuClock);
            while (_controlCycleAccumulator >= 16.0)
            {
                switch (_timerState)
                {
                    case 0:
                        sh1Callback(true);
                        break;
                    case 1:
                        sh1Callback(false);
                        break;
                }
                _timerState = (_timerState + 1) & 3;
                _controlCycleAccumulator -= 16.0;
            }
        }

        public void AdvanceStreamCycles(int audioCpuCycles, int audioCpuClock)
        {
            if (audioCpuCycles <= 0 || audioCpuClock <= 0)
                return;

            _streamCycleAccumulator += audioCpuCycles * (SourceSampleRate / (double)audioCpuClock);
            while (_streamCycleAccumulator >= 1.0)
            {
                GenerateSourceFrame(out int left, out int right);
                _queuedLeft.Enqueue(left);
                _queuedRight.Enqueue(right);
                _streamCycleAccumulator -= 1.0;
            }
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
                double left = _lastLeft + ((_nextLeft - _lastLeft) * _resamplePhase);
                double right = _lastRight + ((_nextRight - _lastRight) * _resamplePhase);
                if (EnableDcBlocker)
                {
                    left = ApplyDcBlock(left, ref _dcLeftX, ref _dcLeftY);
                    right = ApplyDcBlock(right, ref _dcRightX, ref _dcRightY);
                }
                int mixLeft = (int)Math.Round(left * gain);
                int mixRight = (int)Math.Round(right * gain);
                int offset = frame * 2;
                destination[offset] = Mix(destination[offset], mixLeft);
                destination[offset + 1] = Mix(destination[offset + 1], mixRight);
                peak = Math.Max(peak, Math.Max(Math.Abs(mixLeft), Math.Abs(mixRight)));

                _resamplePhase += step;
                while (_resamplePhase >= 1.0)
                {
                    _lastLeft = _nextLeft;
                    _lastRight = _nextRight;
                    DequeueSourceFrame(out _nextLeft, out _nextRight);
                    _resamplePhase -= 1.0;
                }
            }
            _lastPeak = peak;
        }

        private void EnsurePrimed()
        {
            if (_primed)
                return;

            DequeueSourceFrame(out _lastLeft, out _lastRight);
            DequeueSourceFrame(out _nextLeft, out _nextRight);
            _resamplePhase = 0;
            _primed = true;
        }

        private void DequeueSourceFrame(out int left, out int right)
        {
            if (_queuedLeft.Count != 0 && _queuedRight.Count != 0)
            {
                left = _queuedLeft.Dequeue();
                right = _queuedRight.Dequeue();
                return;
            }

            GenerateSourceFrame(out left, out right);
        }

        private void GenerateSourceFrame(out int left, out int right)
        {
            left = 0;
            right = 0;
            if ((_mode & 0x02) == 0)
                return;

            Span<int> outputs = stackalloc int[2];
            outputs[0] = 0;
            outputs[1] = 0;
            for (int i = 0; i < _voices.Length; i++)
            {
                if (_voices[i].Playing && (MuteMask & (1 << i)) == 0)
                    _voices[i].Play(outputs);
            }
            left = Math.Clamp(outputs[0], short.MinValue, short.MaxValue);
            right = Math.Clamp(outputs[1], short.MinValue, short.MaxValue);
        }

        private byte VoiceStatus()
        {
            int status = 0;
            for (int i = 0; i < _voices.Length; i++)
                if (_voices[i].Playing)
                    status |= 1 << i;
            return (byte)status;
        }

        private byte ReadRom(int address)
        {
            if (_rom.Length == 0)
                return 0;
            return _rom[address & (_rom.Length - 1)];
        }

        private static short Mix(short current, int sample)
            => (short)Math.Clamp(current + sample, short.MinValue, short.MaxValue);

        private static double ApplyDcBlock(double input, ref double lastInput, ref double lastOutput)
        {
            double output = input - lastInput + DcBlockerR * lastOutput;
            lastInput = input;
            lastOutput = output;
            return output;
        }

        private static int ParseMuteMask()
        {
            string? value = Environment.GetEnvironmentVariable("EUTHERDRIVE_TMNT2_K053260_MUTE_MASK");
            if (string.IsNullOrWhiteSpace(value))
                return 0;
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value[2..], System.Globalization.NumberStyles.HexNumber, null, out int hex))
                return Math.Clamp(hex, 0, 0x0F);
            if (int.TryParse(value, out int parsed))
                return Math.Clamp(parsed, 0, 0x0F);
            return 0;
        }

        private sealed class Voice
        {
            private readonly K053260Pcm _device;
            private int _position;
            private readonly int[] _panVolume = new int[2];
            private int _counter;
            private int _output;
            private int _start;
            private int _length;
            private int _pitch;
            private int _volume;
            private int _pan;

            public Voice(K053260Pcm device) => _device = device;

            public bool Playing { get; private set; }
            public bool Loop { get; set; }
            public bool Kadpcm { get; set; }
            public bool Reverse { get; set; }
            public string DebugSummary
                => $"{(Playing ? 1 : 0)}:{_start:X5}+{_length:X4}:p{_pitch:X3}:v{_volume:X2}:pan{_pan}:pos{_position:X5}:out{_output}:c{_counter:X3}:"
                   + $"{(Loop ? "L" : "-")}{(Kadpcm ? "A" : "P")}{(Reverse ? "R" : "-")}";

            public string KeyOnSummary
                => $"{_start:X5}+{_length:X4}:p{_pitch:X3}:v{_volume:X2}:pan{_pan}:"
                   + $"{(Loop ? "L" : "-")}{(Kadpcm ? "A" : "P")}{(Reverse ? "R" : "-")}";

            public void Reset()
            {
                _position = 0;
                _counter = 0;
                _output = 0;
                Playing = false;
                _start = 0;
                _length = 0;
                _pitch = 0;
                _volume = 0;
                _pan = 0;
                Loop = false;
                Kadpcm = false;
                Reverse = false;
                UpdatePanVolume();
            }

            public void SetRegister(int offset, byte data)
            {
                switch (offset & 7)
                {
                    case 0:
                        _pitch = (_pitch & 0x0f00) | data;
                        break;
                    case 1:
                        _pitch = (_pitch & 0x00ff) | ((data << 8) & 0x0f00);
                        break;
                    case 2:
                        _length = (_length & 0xff00) | data;
                        break;
                    case 3:
                        _length = (_length & 0x00ff) | (data << 8);
                        break;
                    case 4:
                        _start = (_start & 0x1fff00) | data;
                        break;
                    case 5:
                        _start = (_start & 0x1f00ff) | (data << 8);
                        break;
                    case 6:
                        _start = (_start & 0x00ffff) | ((data << 16) & 0x1f0000);
                        break;
                    case 7:
                        _volume = data & 0x7f;
                        UpdatePanVolume();
                        break;
                }
            }

            public void SetPan(int data)
            {
                _pan = data & 7;
                UpdatePanVolume();
            }

            public void KeyOn()
            {
                _position = Kadpcm ? 1 : 0;
                _counter = 0x1000 - ClocksPerSample;
                _output = 0;
                Playing = true;
            }

            public void KeyOff()
            {
                _position = 0;
                _output = 0;
                Playing = false;
            }

            public void Play(Span<int> outputs)
            {
                _counter += ClocksPerSample;
                while (_counter >= 0x1000)
                {
                    _counter = _counter - 0x1000 + _pitch;
                    int bytePos = ++_position >> (Kadpcm ? 1 : 0);
                    if (bytePos > _length)
                    {
                        if (Loop)
                        {
                            _position = 0;
                            _output = 0;
                            bytePos = 0;
                        }
                        else
                        {
                            Playing = false;
                            return;
                        }
                    }

                    byte romData = _device.ReadRom(_start + (Reverse ? -bytePos : bytePos));
                    if (Kadpcm)
                    {
                        if ((_position & 1) != 0)
                            romData >>= 4;
                        _output = unchecked((sbyte)(_output + KadpcmDeltaTable[romData & 0x0f]));
                    }
                    else
                    {
                        _output = unchecked((sbyte)romData);
                    }
                }

                outputs[0] += (_output * _panVolume[0]) >> 15;
                outputs[1] += (_output * _panVolume[1]) >> 15;
            }

            public byte ReadRom(bool sideEffects)
            {
                byte value = _device.ReadRom(_start + _position);
                if (sideEffects)
                    _position = (_position + 1) & 0xffff;
                return value;
            }

            private void UpdatePanVolume()
            {
                _panVolume[0] = _volume * PanMul[_pan, 0];
                _panVolume[1] = _volume * PanMul[_pan, 1];
            }

            public void SaveState(BinaryWriter writer)
            {
                writer.Write(_position);
                writer.Write(_counter);
                writer.Write(_output);
                writer.Write(_start);
                writer.Write(_length);
                writer.Write(_pitch);
                writer.Write(_volume);
                writer.Write(_pan);
                writer.Write(Playing);
                writer.Write(Loop);
                writer.Write(Kadpcm);
                writer.Write(Reverse);
            }

            public void LoadState(BinaryReader reader)
            {
                _position = reader.ReadInt32();
                _counter = reader.ReadInt32();
                _output = reader.ReadInt32();
                _start = reader.ReadInt32();
                _length = reader.ReadInt32();
                _pitch = reader.ReadInt32();
                _volume = reader.ReadInt32();
                _pan = reader.ReadInt32();
                Playing = reader.ReadBoolean();
                Loop = reader.ReadBoolean();
                Kadpcm = reader.ReadBoolean();
                Reverse = reader.ReadBoolean();
                UpdatePanVolume();
            }
        }
    }

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
        public byte[] Program { get; } = new byte[0x200000];
        public byte[] AudioCpu { get; } = new byte[0x40000];
        public byte[] K007232 { get; } = new byte[0x20000];
        public byte[] Upd7759 { get; } = new byte[0x20000];
        public byte[] TitleSample { get; } = new byte[0x80000];
        public byte[] TileRom { get; } = new byte[0x500000];
        public byte[] SpriteRom { get; } = new byte[0x800000];
        public byte[] K053260 { get; } = new byte[0x200000];
        public byte[] K054539 { get; } = new byte[0x400000];
        public byte[] K053250 { get; } = new byte[0x40000];
        public byte[] Eeprom { get; } = new byte[0x80];
        public byte[] SpriteAddressProm { get; } = new byte[0x100];
        public TmntHardwareVariant Variant { get; private set; } = TmntHardwareVariant.Tmnt;

        public static TmntRomSet Load(string path)
        {
            Dictionary<string, byte[]> entries = ReadArchive(path);
            var roms = new TmntRomSet();
            string name = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
            if (name == "ssriders")
            {
                roms.Variant = TmntHardwareVariant.Ssriders;
                Load16Byte(entries, roms.Program, 0x000000, "064eac02.8e");
                Load16Byte(entries, roms.Program, 0x000001, "064eac03.8g");
                Load16Byte(entries, roms.Program, 0x080000, "064eab04.10e");
                Load16Byte(entries, roms.Program, 0x080001, "064eab05.10g");
                Find(entries, "064e01.2f").CopyTo(roms.AudioCpu, 0);
                Load32Word(entries, roms.TileRom, 0x000000, "064e12.16k");
                Load32Word(entries, roms.TileRom, 0x000002, "064e11.12k");
                Load32Word(entries, roms.SpriteRom, 0x000000, "064e09.7l");
                Load32Word(entries, roms.SpriteRom, 0x000002, "064e07.3l");
                Find(entries, "064e06.1d").CopyTo(roms.K053260, 0);
                if (TryFind(entries, "ssriders_eac.nv", out byte[]? nv))
                    Array.Copy(nv, roms.Eeprom, Math.Min(nv.Length, roms.Eeprom.Length));
                return roms;
            }
            if (name == "tmnt2")
            {
                roms.Variant = TmntHardwareVariant.Tmnt2;
                Load16Byte(entries, roms.Program, 0x000000, "063uaa02.8e");
                Load16Byte(entries, roms.Program, 0x000001, "063uaa03.8g");
                Load16Byte(entries, roms.Program, 0x040000, "063uaa04.10e");
                Load16Byte(entries, roms.Program, 0x040001, "063uaa05.10g");
                Find(entries, "063b01.2f").CopyTo(roms.AudioCpu, 0);
                Load32Word(entries, roms.TileRom, 0x000000, "063b12.16k");
                Load32Word(entries, roms.TileRom, 0x000002, "063b11.12k");
                Load32Word(entries, roms.SpriteRom, 0x000000, "063b09.7l");
                Load32Word(entries, roms.SpriteRom, 0x000002, "063b07.3l");
                Load32Word(entries, roms.SpriteRom, 0x200000, "063b10.7k");
                Load32Word(entries, roms.SpriteRom, 0x200002, "063b08.3k");
                Find(entries, "063b06.1d").CopyTo(roms.K053260, 0);
                if (TryFind(entries, "tmnt2_uaa.nv", out byte[]? nv))
                    Array.Copy(nv, roms.Eeprom, Math.Min(nv.Length, roms.Eeprom.Length));
                return roms;
            }
            if (name == "mystwarr")
            {
                roms.Variant = TmntHardwareVariant.Mystwarr;
                Load16Byte(entries, roms.Program, 0x000000, "128eaa01.20f");
                Load16Byte(entries, roms.Program, 0x000001, "128eaa02.20g");
                Load16Byte(entries, roms.Program, 0x100000, "128a03.19f");
                Load16Byte(entries, roms.Program, 0x100001, "128a04.19g");
                byte[] mystSound = Find(entries, "128a05.6b");
                mystSound.CopyTo(roms.AudioCpu, 0);
                mystSound.CopyTo(roms.AudioCpu, 0x20000);
                LoadTileWord(entries, roms.TileRom, 0x000000, "128a08.1h");
                LoadTileWord(entries, roms.TileRom, 0x000002, "128a09.1k");
                LoadTileByte(entries, roms.TileRom, 0x000004, "128a10.3h");
                Load64Word(entries, roms.SpriteRom, 0x000000, "128a16.22k");
                Load64Word(entries, roms.SpriteRom, 0x000002, "128a15.20k");
                Load64Word(entries, roms.SpriteRom, 0x000004, "128a14.19k");
                Load64Word(entries, roms.SpriteRom, 0x000006, "128a13.17k");
                Load16Byte(entries, roms.SpriteRom, 0x400000, "128a12.12k");
                Load16Byte(entries, roms.SpriteRom, 0x400001, "128a11.10k");
                Find(entries, "128a06.2d").CopyTo(roms.K054539, 0x000000);
                Find(entries, "128a07.1d").CopyTo(roms.K054539, 0x200000);
                if (TryFind(entries, "mystwarr.nv", out byte[]? nv))
                    Array.Copy(nv, roms.Eeprom, Math.Min(nv.Length, roms.Eeprom.Length));
                return roms;
            }
            if (name == "metamrph")
            {
                roms.Variant = TmntHardwareVariant.Metamrph;
                Load16Byte(entries, roms.Program, 0x000001, "224eaa01.15h");
                Load16Byte(entries, roms.Program, 0x000000, "224eaa02.15f");
                Load16Byte(entries, roms.Program, 0x100001, "224a03");
                Load16Byte(entries, roms.Program, 0x100000, "224a04");
                byte[] metaSound = Find(entries, "224a05");
                metaSound.CopyTo(roms.AudioCpu, 0);
                LoadTileWord(entries, roms.TileRom, 0x000000, "224a09");
                LoadTileWord(entries, roms.TileRom, 0x000002, "224a08");
                Load64Word(entries, roms.SpriteRom, 0x000000, "224a10");
                Load64Word(entries, roms.SpriteRom, 0x000002, "224a11");
                Load64Word(entries, roms.SpriteRom, 0x000004, "224a12");
                Load64Word(entries, roms.SpriteRom, 0x000006, "224a13");
                Find(entries, "224a14").CopyTo(roms.K053250, 0);
                Find(entries, "224a06").CopyTo(roms.K054539, 0x000000);
                Find(entries, "224a07").CopyTo(roms.K054539, 0x200000);
                if (TryFind(entries, "metamrph.nv", out byte[]? nv))
                    Array.Copy(nv, roms.Eeprom, Math.Min(nv.Length, roms.Eeprom.Length));
                return roms;
            }
            if (name == "moomesa")
            {
                roms.Variant = TmntHardwareVariant.Moomesa;
                Load16Byte(entries, roms.Program, 0x000000, "151b01.q5");
                Load16Byte(entries, roms.Program, 0x000001, "151eab02.q6");
                Load16Byte(entries, roms.Program, 0x100000, "151a03.t5");
                Load16Byte(entries, roms.Program, 0x100001, "151a04.t6");
                Find(entries, "151a07.f5").CopyTo(roms.AudioCpu, 0);
                Load32Word(entries, roms.TileRom, 0x000000, "151a05.t8");
                Load32Word(entries, roms.TileRom, 0x000002, "151a06.t10");
                Load64Word(entries, roms.SpriteRom, 0x000000, "151a10.b8");
                Load64Word(entries, roms.SpriteRom, 0x000002, "151a11.a8");
                Load64Word(entries, roms.SpriteRom, 0x000004, "151a12.b10");
                Load64Word(entries, roms.SpriteRom, 0x000006, "151a13.a10");
                Find(entries, "151a08.b6").CopyTo(roms.K054539, 0);
                if (TryFind(entries, "moomesa.nv", out byte[]? nv))
                    Array.Copy(nv, roms.Eeprom, Math.Min(nv.Length, roms.Eeprom.Length));
                return roms;
            }

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

        private static void Load64Word(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
        {
            byte[] source = Find(entries, name);
            for (int i = 0; i < source.Length; i += 2)
            {
                int dst = offset + (i / 2) * 8;
                destination[dst] = source[i];
                destination[dst + 1] = source[i + 1];
            }
        }

        private static void LoadTileWord(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
        {
            byte[] source = Find(entries, name);
            for (int i = 0; i < source.Length; i += 2)
            {
                int dst = offset + (i / 2) * 5;
                destination[dst] = source[i + 1];
                destination[dst + 1] = source[i];
            }
        }

        private static void LoadTileByte(Dictionary<string, byte[]> entries, byte[] destination, int offset, string name)
        {
            byte[] source = Find(entries, name);
            for (int i = 0; i < source.Length; i++)
                destination[offset + i * 5] = source[i];
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
        {
            if (TryFind(entries, name, out byte[]? data))
                return data;
            throw new FileNotFoundException($"Required TMNT ROM '{name}' was not found in archive.");
        }

        private static bool TryFind(Dictionary<string, byte[]> entries, string name, out byte[]? data)
        {
            if (entries.TryGetValue(name, out data))
                return true;

            string baseName = name.Split('.')[0];
            foreach ((string key, byte[] value) in entries)
            {
                if (string.Equals(key.Split('.')[0], baseName, StringComparison.OrdinalIgnoreCase))
                {
                    data = value;
                    return true;
                }
            }

            data = null;
            return false;
        }
    }

    private static void TmntTileCallback(int layer, int bank, ref int code, ref byte color)
        => TmntTileCallback(layer, bank, ref code, ref color, new[] { 0, 32, 40 });

    private static void TmntTileCallback(int layer, int bank, ref int code, ref byte color, IReadOnlyList<int> layerColorBase)
    {
        code |= ((color & 0x03) << 8) | ((color & 0x10) << 6) | ((color & 0x0c) << 9) | (bank << 13);
        color = (byte)(layerColorBase[layer] + ((color & 0xe0) >> 5));
    }

    private static void WritePixel(byte[] frameBuffer, int x, int y, ushort xBgr555)
    {
        int offset = y * FrameStride + x * 4;
        frameBuffer[offset] = Color5To8[(xBgr555 >> 10) & 0x1f];
        frameBuffer[offset + 1] = Color5To8[(xBgr555 >> 5) & 0x1f];
        frameBuffer[offset + 2] = Color5To8[xBgr555 & 0x1f];
        frameBuffer[offset + 3] = 0xff;
    }

    private static void FillFrame(byte[] frameBuffer, ushort xBgr555)
    {
        byte r = Color5To8[xBgr555 & 0x1f];
        byte g = Color5To8[(xBgr555 >> 5) & 0x1f];
        byte b = Color5To8[(xBgr555 >> 10) & 0x1f];
        for (int offset = 0; offset < frameBuffer.Length; offset += 4)
        {
            frameBuffer[offset] = b;
            frameBuffer[offset + 1] = g;
            frameBuffer[offset + 2] = r;
            frameBuffer[offset + 3] = 0xff;
        }
    }

    private static byte[] BuildColor5To8()
    {
        var table = new byte[32];
        for (int i = 0; i < table.Length; i++)
            table[i] = (byte)(i * 255 / 31);
        return table;
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
