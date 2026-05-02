using System;
using System.Collections.Generic;
using System.IO;
using SharpCompress.Archives;
using EutherDrive.Core.Cpu.M68000Emu;

namespace EutherDrive.Core.Arcade.Konami;

// Teenage Mutant Ninja Turtles hardware notes and the K052109/K051960 register
// behavior below are translated from MAME's BSD-3-Clause Konami TMNT driver and
// video chip devices:
//   src/mame/konami/tmnt.cpp
//   src/mame/konami/k052109.cpp
//   src/mame/konami/k051960.cpp
public sealed class TmntAdapter : IEmulatorCore
{
    private const int FrameWidth = 320;
    private const int FrameHeight = 224;
    private const int FrameStride = FrameWidth * 4;
    private const double TargetFps = 60.0 / 1.001;
    private const int MainCpuCyclesPerFrame = 8_000_000 / 60;
    private const int ScreenTotalLines = 264;
    private const int ScreenVisibleLines = 224;
    private const int MainCpuVisibleCycles = MainCpuCyclesPerFrame * ScreenVisibleLines / ScreenTotalLines;
    private const int MainCpuVblankCycles = MainCpuCyclesPerFrame - MainCpuVisibleCycles;
    private const int OutputSampleRate = 44_100;
    private const int OutputChannels = 2;

    private readonly TmntBus _bus = new();
    private readonly M68000 _mainCpu = M68000.CreateBuilder()
        .AllowTasWrites(true)
        .Name("konami-tmnt-main")
        .Build();

    private readonly object _frameSync = new();
    private byte[] _presentFrameBuffer = new byte[FrameHeight * FrameStride];
    private byte[] _renderFrameBuffer = new byte[FrameHeight * FrameStride];
    private byte[] _snapshotFrameBuffer = new byte[FrameHeight * FrameStride];
    private short[] _audioBuffer = Array.Empty<short>();
    private ArcadeInputState _input;
    private bool _loaded;

    public string DebugSummary => _bus.DebugSummary(_mainCpu.Pc);

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

        TmntRomSet roms = TmntRomSet.Load(path);
        _bus.Load(roms);
        _mainCpu.Reset(_bus);
        _loaded = true;
        ClearFrameBuffers();
        _audioBuffer = new short[Math.Max(1, (int)Math.Round(OutputSampleRate / TargetFps)) * OutputChannels];
    }

    public void Reset()
    {
        if (!_loaded)
            return;

        _bus.ResetMachine();
        _mainCpu.Reset(_bus);
        ClearFrameBuffers();
    }

    public void RunFrame()
    {
        if (!_loaded)
            return;

        _bus.SetInput(_input);

        int cycles = 0;
        while (cycles < MainCpuVisibleCycles)
            cycles += checked((int)_mainCpu.ExecuteInstruction(_bus));

        _bus.Render(_renderFrameBuffer);
        lock (_frameSync)
        {
            Buffer.BlockCopy(_renderFrameBuffer, 0, _presentFrameBuffer, 0, _renderFrameBuffer.Length);
        }

        _bus.BeginVblank();
        cycles = 0;
        while (cycles < MainCpuVblankCycles)
            cycles += checked((int)_mainCpu.ExecuteInstruction(_bus));

        Array.Clear(_audioBuffer);
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

    private sealed class TmntBus : IBusInterface, IOpcodeBusInterface
    {
        private readonly byte[] _program = new byte[0x60000];
        private readonly byte[] _ram = new byte[0x4000];
        private readonly byte[] _paletteRam = new byte[0x1000];
        private readonly ushort[] _palette = new ushort[0x400];
        private readonly byte[] _tileRom = new byte[0x100000];
        private readonly byte[] _spriteRom = new byte[0x200000];
        private readonly K052109 _k052109 = new();
        private readonly K051960 _k051960 = new();

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
        private int _spriteWrites;
        private int _paletteWrites;

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
                return;
            }
            if (address >= 0x100000 && address <= 0x107fff)
            {
                _k052109Writes++;
                int offset = NoA12Offset(address);
                _k052109.Write((address & 1) == 0 ? offset : offset + 0x2000, value);
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
            if (address >= 0x0c0000 && address <= 0x0c0001)
            {
                _priority = (value & 0x0c) >> 2;
                return;
            }
            if (address >= 0x100000 && address <= 0x107fff)
            {
                int offset = NoA12Offset(address);
                _k052109Writes++;
                _k052109.Write(offset, (byte)(value >> 8));
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
               + _k052109.DebugSummary();

        private void WriteControl0a0000(byte data)
        {
            byte soundIrqBit = (byte)(data & 0x08);
            if (_lastSoundIrqBit == 0x08 && soundIrqBit == 0)
            {
                // Z80 sound IRQ; audio CPU is not wired yet.
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
            if (_input.Right) value &= ~0x01;
            if (_input.Left) value &= ~0x02;
            if (_input.Down) value &= ~0x04;
            if (_input.Up) value &= ~0x08;
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
        private readonly byte[] _rom = new byte[0x100000];
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
            offset &= 0x5fff;
            if (Rmrd)
                return ReadCharRom(offset);
            return _ram[offset];
        }

        public void Write(int offset, byte data)
        {
            offset &= 0x5fff;
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
            x -= layer == 0 ? 96 : 90;
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
        private readonly byte[] _rom = new byte[0x200000];
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

    private sealed class TmntRomSet
    {
        public byte[] Program { get; } = new byte[0x60000];
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

    private static ushort ReadBigEndianWord(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static void WriteBigEndianWord(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }
}
