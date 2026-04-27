using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EutherDrive.Core.Cpu.M68000Emu;
using SharpCompress.Archives;

namespace EutherDrive.Core.Arcade.Cps1;

// CPS1 hardware notes and register constants are translated from MAME's
// BSD-3-Clause Capcom CPS1 driver by Paul Leaman.
public sealed class Cps1DinoAdapter : IEmulatorCore
{
    private const int OutputSampleRate = 44_100;
    private const int OutputChannels = 2;
    private const double TargetFps = (16_000_000.0 / 2.0) / (512.0 * 262.0);
    private const int FrameWidth = 384;
    private const int FrameHeight = 224;
    private const int FrameStride = FrameWidth * 4;
    private const int CpuCyclesPerFrame = 201_216;
    private const int AudioCpuCyclesPerFrame = 134_144;

    private readonly byte[] _frameBuffer = new byte[FrameHeight * FrameStride];
    private readonly Cps1Bus _bus = new();
    private readonly Cps1AudioBus _audioBus;
    private readonly M68000 _mainCpu = M68000.CreateBuilder()
        .AllowTasWrites(true)
        .Name("cps1-main")
        .Build();
    private readonly EutherDrive.Core.Cpu.Z80Emu.Z80 _audioCpu = new();

    private Cps1Video? _video;
    private short[] _audioBuffer = Array.Empty<short>();
    private int _audioSampleFramesThisFrame;
    private double _audioSampleAccumulator;
    private ArcadeInputState _input;
    private bool _loaded;

    public Cps1DinoAdapter()
    {
        _audioBus = new Cps1AudioBus(_bus);
    }

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string name = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
        return name is "dino";
    }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("CPS1 ROM path is empty.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("CPS1 ROM archive not found.", path);

        Cps1DinoRomSet roms = Cps1DinoRomSet.Load(path);
        _bus.Load(roms);
        _video = new Cps1Video(_bus, roms.Graphics);
        _mainCpu.Reset(_bus);
        _audioCpu.ApplyResetLine();
        _loaded = true;

        Array.Clear(_frameBuffer);
        _audioBuffer = Array.Empty<short>();
        _audioSampleFramesThisFrame = 0;
        _audioSampleAccumulator = 0;
    }

    public void Reset()
    {
        if (!_loaded)
            return;

        _bus.ResetMachine();
        _mainCpu.Reset(_bus);
        _audioCpu.ApplyResetLine();
        _audioSampleFramesThisFrame = 0;
        _audioSampleAccumulator = 0;
    }

    public void RunFrame()
    {
        if (!_loaded)
            return;

        _bus.SetInput(_input);
        _audioSampleFramesThisFrame = GetAudioSampleFramesPerFrame();
        EnsureAudioBuffer(_audioSampleFramesThisFrame * OutputChannels);
        _bus.BeginAudioFrame(_audioBuffer);
        _bus.RunAudioCpu(_audioCpu, _audioBus, AudioCpuCyclesPerFrame / 2);

        _bus.AssertVBlankInterrupt();

        int cycles = 0;
        while (cycles < CpuCyclesPerFrame)
            cycles += checked((int)_mainCpu.ExecuteInstruction(_bus));

        _bus.ClearInterrupt();
        _bus.RunAudioCpu(_audioCpu, _audioBus, AudioCpuCyclesPerFrame - (AudioCpuCyclesPerFrame / 2));
        _bus.EndAudioFrame();
        _video!.Render(_frameBuffer);
        _bus.LatchSprites();
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
        sampleRate = OutputSampleRate;
        channels = OutputChannels;
        return _audioBuffer.AsSpan(0, _audioSampleFramesThisFrame * OutputChannels);
    }

    public double GetTargetFps() => TargetFps;

    private int GetAudioSampleFramesPerFrame()
    {
        _audioSampleAccumulator += OutputSampleRate / TargetFps;
        int sampleFrames = (int)_audioSampleAccumulator;
        if (sampleFrames < 1)
            sampleFrames = 1;
        _audioSampleAccumulator -= sampleFrames;
        return sampleFrames;
    }

    private void EnsureAudioBuffer(int samples)
    {
        if (_audioBuffer.Length != samples)
            _audioBuffer = new short[samples];
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

    private sealed class Cps1Bus : IBusInterface
    {
        private const int MainRomSize = 0x20_0000;
        private const int MainRamSize = 0x01_0000;
        private const int GfxRamBytes = 0x03_0000;
        private const int GfxRamWords = GfxRamBytes / 2;
        private const int ObjBytes = 0x0800;
        private const int ObjWords = ObjBytes / 2;
        private const int PaletteAlignBytes = 0x0400;
        private const int PaletteEntries = 0x0c00;

        private readonly byte[] _mainRom = new byte[MainRomSize];
        private readonly byte[] _mainRam = new byte[MainRamSize];
        private readonly byte[] _audioCpu = new byte[0x2_8000];
        private readonly byte[] _audioOpcodes = new byte[0x8000];
        private readonly byte[] _qsoundShared0 = new byte[0x1000];
        private readonly byte[] _qsoundShared1 = new byte[0x1000];
        private readonly ushort[] _gfxRam = new ushort[GfxRamWords];
        private readonly ushort[] _paletteRam = new ushort[PaletteEntries];
        private readonly ushort[] _bufferedObj = new ushort[ObjWords];
        private readonly ushort[] _cpsA = new ushort[0x20];
        private readonly ushort[] _cpsB = new ushort[0x20];
        private readonly Cps1QSound _qsound = new();
        private readonly Cps1SerialEeprom _eeprom = new();

        private ArcadeInputState _input;
        private byte _interruptLevel;
        private int _audioBank;
        private int _audioIrqCountdown;
        private int _audioIrqHoldCycles;
        private bool _audioIrqAsserted;
        private short[]? _audioFrameBuffer;
        private int _audioFrameCycles;
        private int _audioFrameSampleIndex;

        public ReadOnlySpan<ushort> GfxRam => _gfxRam;
        public ReadOnlySpan<ushort> PaletteRam => _paletteRam;
        public ReadOnlySpan<ushort> BufferedObj => _bufferedObj;
        public ReadOnlySpan<ushort> CpsA => _cpsA;
        public ReadOnlySpan<ushort> CpsB => _cpsB;
        public bool InterruptAsserted => _interruptLevel != 0;
        public BusSignals Signals => new(false);
        public ushort CurrentOpcode => 0;

        public void Load(Cps1DinoRomSet roms)
        {
            Array.Fill(_mainRom, (byte)0xff);
            Array.Clear(_mainRam);
            Array.Clear(_audioCpu);
            Array.Clear(_audioOpcodes);
            Array.Clear(_qsoundShared0);
            Array.Clear(_qsoundShared1);
            Array.Clear(_gfxRam);
            Array.Clear(_paletteRam);
            Array.Clear(_bufferedObj);
            Array.Clear(_cpsA);
            Array.Clear(_cpsB);

            roms.Program.CopyTo(_mainRom.AsSpan(0, roms.Program.Length));
            roms.AudioCpu.CopyTo(_audioCpu.AsSpan(0, roms.AudioCpu.Length));
            byte[] encryptedAudio = _audioCpu.AsSpan(0, 0x8000).ToArray();
            Cps1Kabuki.DecodeDino(encryptedAudio, _audioOpcodes, _audioCpu.AsSpan(0, 0x8000));
            _qsound.Load(roms.QSound, roms.QSoundDsp);
            _eeprom.ResetPins();
            ResetVideoRegisters();
            ResetAudioState();
            _interruptLevel = 0;
        }

        public void ResetMachine()
        {
            Array.Clear(_mainRam);
            Array.Clear(_qsoundShared0);
            Array.Clear(_qsoundShared1);
            Array.Clear(_gfxRam);
            Array.Clear(_paletteRam);
            Array.Clear(_bufferedObj);
            Array.Clear(_cpsA);
            Array.Clear(_cpsB);
            _eeprom.ResetPins();
            ResetVideoRegisters();
            ResetAudioState();
            _interruptLevel = 0;
        }

        public void BeginAudioFrame(short[] destination)
        {
            Array.Clear(destination);
            _audioFrameBuffer = destination;
            _audioFrameCycles = 0;
            _audioFrameSampleIndex = 0;
        }

        public void SetInput(ArcadeInputState input) => _input = input;

        public void AssertVBlankInterrupt() => _interruptLevel = 2;

        public void ClearInterrupt() => _interruptLevel = 0;

        public void LatchSprites()
        {
            int objBase = Cps1Base(Cps1Regs.ObjBase, ObjBytes);
            for (int i = 0; i < _bufferedObj.Length; i++)
                _bufferedObj[i] = ReadGfxWord(objBase + i);
        }

        public void RunAudioCpu(
            EutherDrive.Core.Cpu.Z80Emu.Z80 audioCpu,
            Cps1AudioBus audioBus,
            int cycleBudget)
        {
            int cycles = 0;
            while (cycles < cycleBudget)
            {
                if (_audioIrqCountdown <= 0)
                {
                    _audioIrqAsserted = true;
                    _audioIrqHoldCycles = 64;
                    _audioIrqCountdown += 32_000;
                }

                int elapsed = checked((int)audioCpu.ExecuteInstruction(audioBus));
                cycles += elapsed;
                _audioIrqCountdown -= elapsed;
                AdvanceAudioFrame(elapsed);

                if (_audioIrqAsserted)
                {
                    _audioIrqHoldCycles -= elapsed;
                    if (_audioIrqHoldCycles <= 0)
                    {
                        _audioIrqAsserted = false;
                        _audioIrqHoldCycles = 0;
                    }
                }
                else
                {
                    _audioIrqAsserted = false;
                }
            }
        }

        public void EndAudioFrame()
        {
            if (_audioFrameBuffer is null)
                return;

            _qsound.RenderFrames(_audioFrameBuffer, ref _audioFrameSampleIndex, _audioFrameBuffer.Length / 2);
            _audioFrameBuffer = null;
            _audioFrameCycles = 0;
            _audioFrameSampleIndex = 0;
        }

        private void AdvanceAudioFrame(int elapsedCycles)
        {
            if (_audioFrameBuffer is null || elapsedCycles <= 0)
                return;

            _audioFrameCycles = Math.Min(_audioFrameCycles + elapsedCycles, AudioCpuCyclesPerFrame);
            int sampleFrames = _audioFrameBuffer.Length / 2;
            int targetFrames = (int)((long)_audioFrameCycles * sampleFrames / AudioCpuCyclesPerFrame);
            _qsound.RenderFrames(_audioFrameBuffer, ref _audioFrameSampleIndex, targetFrames);
        }

        public EutherDrive.Core.Cpu.Z80Emu.InterruptLine AudioInterruptLine
            => _audioIrqAsserted
                ? EutherDrive.Core.Cpu.Z80Emu.InterruptLine.Low
                : EutherDrive.Core.Cpu.Z80Emu.InterruptLine.High;

        public byte ReadAudioMemory(ushort address)
        {
            if (address < 0x8000)
                return _audioCpu[address];
            if (address >= 0x8000 && address <= 0xbfff)
            {
                int offset = 0x10000 + _audioBank * 0x4000 + (address - 0x8000);
                return (uint)offset < _audioCpu.Length ? _audioCpu[offset] : (byte)0xff;
            }
            if (address >= 0xc000 && address <= 0xcfff)
                return _qsoundShared0[address - 0xc000];
            if (address == 0xd007)
                return _qsound.ReadStatus();
            if (address >= 0xf000)
                return _qsoundShared1[address - 0xf000];

            return 0xff;
        }

        public byte ReadAudioOpcode(ushort address)
        {
            if (address < 0x8000)
                return _audioOpcodes[address];
            return ReadAudioMemory(address);
        }

        public void WriteAudioMemory(ushort address, byte value)
        {
            if (address >= 0xc000 && address <= 0xcfff)
            {
                _qsoundShared0[address - 0xc000] = value;
                return;
            }
            if (address >= 0xd000 && address <= 0xd002)
            {
                _qsound.Write(address - 0xd000, value);
                return;
            }
            if (address == 0xd003)
            {
                int bank = value & 0x0f;
                _audioBank = 0x10000 + bank * 0x4000 < _audioCpu.Length ? bank : 0;
                return;
            }
            if (address >= 0xf000)
                _qsoundShared1[address - 0xf000] = value;
        }

        public int Cps1Base(int registerIndex, int boundaryBytes)
        {
            int baseAddress = _cpsA[registerIndex] * 256;
            baseAddress &= ~(boundaryBytes - 1);
            return (baseAddress & 0x3ffff) / 2;
        }

        public ushort ReadGfxWord(int wordIndex)
            => _gfxRam[wordIndex % GfxRamWords];

        public byte ReadByte(uint address)
        {
            address &= 0x00ff_ffff;

            if (address < MainRomSize)
                return _mainRom[address];
            if (address >= 0x900000 && address <= 0x92ffff)
                return ReadWordByte(ReadGfxWord((int)((address - 0x900000) >> 1)), address);
            if (address >= 0xff0000)
                return _mainRam[address & 0xffff];
            if (IsWordMapped(address))
                return ReadWordByte(ReadWord(address & ~1u), address);
            if (address >= 0xf18000 && address <= 0xf19fff)
                return ReadQSoundSharedByte(_qsoundShared0, address - 0xf18000);
            if (address >= 0xf1e000 && address <= 0xf1ffff)
                return ReadQSoundSharedByte(_qsoundShared1, address - 0xf1e000);

            return 0xff;
        }

        public ushort ReadWord(uint address)
        {
            address &= 0x00ff_ffff;

            if (address < MainRomSize - 1)
                return ReadBigEndianWord(_mainRom, (int)address);
            if (address >= 0x800000 && address <= 0x800007)
                return Input1();
            if (address >= 0x800018 && address <= 0x80001f)
                return ReadDsw((int)((address - 0x800018) >> 1));
            if (address >= 0x800140 && address <= 0x80017f)
                return ReadCpsB((int)((address - 0x800140) >> 1));
            if (address >= 0x900000 && address <= 0x92ffff)
                return ReadGfxWord((int)((address - 0x900000) >> 1));
            if (address >= 0xf00000 && address <= 0xf0ffff)
                return ReadQSoundRom((int)(address - 0xf00000));
            if (address >= 0xf18000 && address <= 0xf19fff)
                return (ushort)(0xff00 | _qsoundShared0[(address - 0xf18000) >> 1]);
            if (address >= 0xf1c000 && address <= 0xf1c001)
                return Input2();
            if (address >= 0xf1c002 && address <= 0xf1c003)
                return 0xffff;
            if (address >= 0xf1c006 && address <= 0xf1c007)
                return (ushort)(_eeprom.DataOut ? 0x0001 : 0x0000);
            if (address >= 0xf1e000 && address <= 0xf1ffff)
                return (ushort)(0xff00 | _qsoundShared1[(address - 0xf1e000) >> 1]);
            if (address >= 0xff0000)
                return ReadBigEndianWord(_mainRam, (int)(address & 0xffff));

            return 0xffff;
        }

        public uint ReadLong(uint address)
        {
            ushort hi = ReadWord(address);
            ushort lo = ReadWord(address + 2);
            return ((uint)hi << 16) | lo;
        }

        public void WriteByte(uint address, byte value)
        {
            address &= 0x00ff_ffff;

            if (address >= 0x900000 && address <= 0x92ffff)
            {
                int index = (int)((address - 0x900000) >> 1);
                WriteWordByte(ref _gfxRam[index], address, value);
                return;
            }
            if (address >= 0x800100 && address <= 0x80013f)
            {
                int index = (int)((address - 0x800100) >> 1);
                WriteWordByte(ref _cpsA[index], address, value);
                if (index == Cps1Regs.PaletteBase)
                    LatchPalette();
                return;
            }
            if (address >= 0x800140 && address <= 0x80017f)
            {
                int index = (int)((address - 0x800140) >> 1);
                WriteWordByte(ref _cpsB[index], address, value);
                if (index == Cps1DinoConfig.PaletteControl / 2)
                    LatchPalette();
                return;
            }
            if (address >= 0xf18000 && address <= 0xf19fff)
            {
                if ((address & 1) != 0)
                    _qsoundShared0[(address - 0xf18000) >> 1] = value;
                return;
            }
            if (address >= 0xf1e000 && address <= 0xf1ffff)
            {
                if ((address & 1) != 0)
                    _qsoundShared1[(address - 0xf1e000) >> 1] = value;
                return;
            }
            if (address >= 0xf1c006 && address <= 0xf1c007)
            {
                if ((address & 1) != 0)
                    _eeprom.Write(value);
                return;
            }
            if (address >= 0xff0000)
            {
                _mainRam[address & 0xffff] = value;
                return;
            }
        }

        public void WriteWord(uint address, ushort value)
        {
            address &= 0x00ff_ffff;

            if (address >= 0x800030 && address <= 0x800037)
                return;
            if (address >= 0x800100 && address <= 0x80013f)
            {
                int index = (int)((address - 0x800100) >> 1);
                _cpsA[index] = value;
                if (index == Cps1Regs.PaletteBase)
                    LatchPalette();
                return;
            }
            if (address >= 0x800140 && address <= 0x80017f)
            {
                int index = (int)((address - 0x800140) >> 1);
                _cpsB[index] = value;
                if (index == Cps1DinoConfig.PaletteControl / 2)
                    LatchPalette();
                return;
            }
            if (address >= 0x900000 && address <= 0x92ffff)
            {
                _gfxRam[(address - 0x900000) >> 1] = value;
                return;
            }
            if (address >= 0xf18000 && address <= 0xf19fff)
            {
                _qsoundShared0[(address - 0xf18000) >> 1] = (byte)value;
                return;
            }
            if (address >= 0xf1c004 && address <= 0xf1c007)
            {
                if (address >= 0xf1c006)
                    _eeprom.Write((byte)value);
                return;
            }
            if (address >= 0xf1e000 && address <= 0xf1ffff)
            {
                _qsoundShared1[(address - 0xf1e000) >> 1] = (byte)value;
                return;
            }
            if (address >= 0xff0000)
            {
                WriteBigEndianWord(_mainRam, (int)(address & 0xffff), value);
                return;
            }
        }

        public void WriteLong(uint address, uint value)
        {
            WriteWord(address, (ushort)(value >> 16));
            WriteWord(address + 2, (ushort)value);
        }

        public byte InterruptLevel() => _interruptLevel;

        public void AcknowledgeInterrupt(byte level)
        {
            if (_interruptLevel == level)
                _interruptLevel = 0;
        }

        public bool Reset() => false;

        public bool Halt() => false;

        private void ResetVideoRegisters()
        {
            _cpsA[Cps1Regs.ObjBase] = 0x9200;
            _cpsA[Cps1Regs.Scroll1Base] = 0x9000;
            _cpsA[Cps1Regs.Scroll2Base] = 0x9040;
            _cpsA[Cps1Regs.Scroll3Base] = 0x9080;
            _cpsA[Cps1Regs.OtherBase] = 0x9100;
        }

        private void ResetAudioState()
        {
            _audioBank = 0;
            _audioIrqCountdown = 32_000;
            _audioIrqHoldCycles = 0;
            _audioIrqAsserted = false;
            _qsound.Reset();
        }

        private void LatchPalette()
        {
            int source = Cps1Base(Cps1Regs.PaletteBase, PaletteAlignBytes);
            int cursor = source;
            ushort control = _cpsB[Cps1DinoConfig.PaletteControl / 2];

            for (int page = 0; page < 6; page++)
            {
                if (((control >> page) & 1) != 0)
                {
                    int destination = page * 0x200;
                    for (int offset = 0; offset < 0x200; offset++)
                        _paletteRam[destination + offset] = ReadGfxWord(cursor++);
                }
                else if (cursor != source)
                {
                    cursor += 0x200;
                }
            }
        }

        private ushort ReadDsw(int offset)
        {
            int input = offset switch
            {
                0 => Input0(),
                1 => 0xff,
                2 => 0xff,
                3 => 0xff,
                _ => 0xff
            };
            return (ushort)((input << 8) | 0xff);
        }

        private static ushort ReadCpsB(int offset)
        {
            _ = offset;
            return 0xffff;
        }

        private ushort ReadQSoundRom(int offset)
        {
            if ((uint)offset < _audioCpu.Length)
                return (ushort)(0xff00 | _audioCpu[offset]);
            return 0xffff;
        }

        private int Input0()
        {
            int value = 0xff;
            if (_input.Coin)
                value &= ~0x01;
            if (_input.Start)
                value &= ~0x10;
            return value;
        }

        private ushort Input1()
        {
            int value = 0xffff;
            if (_input.Right)
                value &= ~0x0001;
            if (_input.Left)
                value &= ~0x0002;
            if (_input.Down)
                value &= ~0x0004;
            if (_input.Up)
                value &= ~0x0008;
            if (_input.Button1)
                value &= ~0x0010;
            if (_input.Button2)
                value &= ~0x0020;
            if (_input.Button3)
                value &= ~0x0040;
            return (ushort)value;
        }

        private static ushort Input2() => 0xffff;

        private static bool IsWordMapped(uint address)
            => (address >= 0x800000 && address <= 0x800007)
               || (address >= 0x800018 && address <= 0x80001f)
               || (address >= 0x800100 && address <= 0x80017f)
               || (address >= 0xf00000 && address <= 0xf0ffff)
               || (address >= 0xf1c000 && address <= 0xf1c007);

        private static byte ReadWordByte(ushort word, uint address)
            => (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;

        private static byte ReadQSoundSharedByte(byte[] ram, uint offset)
            => (offset & 1) == 0 ? (byte)0xff : ram[offset >> 1];

        private static ushort ReadBigEndianWord(byte[] data, int offset)
            => (ushort)((data[offset] << 8) | data[(offset + 1) & (data.Length - 1)]);

        private static void WriteBigEndianWord(byte[] data, int offset, ushort value)
        {
            data[offset] = (byte)(value >> 8);
            data[(offset + 1) & (data.Length - 1)] = (byte)value;
        }

        private static void WriteWordByte(ref ushort word, uint address, byte value)
        {
            word = (address & 1) == 0
                ? (ushort)((word & 0x00ff) | (value << 8))
                : (ushort)((word & 0xff00) | value);
        }
    }

    private sealed class Cps1SerialEeprom
    {
        private readonly byte[] _data = new byte[128];
        private bool _chipSelect;
        private bool _clock;
        private bool _dataIn;
        private bool _dataOut = true;
        private int _shift;
        private int _bits;
        private int _output;
        private int _outputBits;
        private bool _receivingWriteData;
        private int _writeAddress;
        private int _writeData;
        private int _writeBits;

        public Cps1SerialEeprom()
        {
            Array.Fill(_data, (byte)0xff);
        }

        public bool DataOut => _chipSelect ? _dataOut : true;

        public void ResetPins()
        {
            _chipSelect = false;
            _clock = false;
            _dataIn = false;
            _dataOut = true;
            ResetTransfer();
        }

        public void Write(byte value)
        {
            bool dataIn = (value & 0x01) != 0;
            bool clock = (value & 0x40) != 0;
            bool chipSelect = (value & 0x80) != 0;

            if (!chipSelect)
            {
                _chipSelect = false;
                _clock = clock;
                _dataIn = dataIn;
                _dataOut = true;
                ResetTransfer();
                return;
            }

            bool risingClock = chipSelect && _chipSelect && !_clock && clock;
            _chipSelect = true;
            _clock = clock;
            _dataIn = dataIn;

            if (risingClock)
                ClockBit(dataIn);
        }

        private void ClockBit(bool bit)
        {
            if (_outputBits > 0)
            {
                ShiftOutputBit();
                return;
            }

            if (_receivingWriteData)
            {
                _writeData = ((_writeData << 1) | (bit ? 1 : 0)) & 0xff;
                _writeBits++;
                if (_writeBits == 8)
                {
                    _data[_writeAddress & 0x7f] = (byte)_writeData;
                    ResetTransfer();
                    _dataOut = true;
                }
                return;
            }

            _shift = ((_shift << 1) | (bit ? 1 : 0)) & 0x3ff;
            _bits++;
            if (_bits < 10)
                return;

            int start = (_shift >> 9) & 1;
            int op = (_shift >> 7) & 0x03;
            int address = _shift & 0x7f;
            ResetTransfer();

            if (start == 0)
                return;

            switch (op)
            {
                case 0x02:
                    StartOutput(_data[address]);
                    break;
                case 0x01:
                    _receivingWriteData = true;
                    _writeAddress = address;
                    _writeData = 0;
                    _writeBits = 0;
                    break;
                case 0x03:
                    _data[address] = 0xff;
                    break;
            }
        }

        private void StartOutput(byte value)
        {
            _output = value;
            _outputBits = 8;
            ShiftOutputBit();
        }

        private void ShiftOutputBit()
        {
            _dataOut = ((_output >> 7) & 1) != 0;
            _output = (_output << 1) & 0xff;
            _outputBits--;
        }

        private void ResetTransfer()
        {
            _shift = 0;
            _bits = 0;
            _output = 0;
            _outputBits = 0;
            _receivingWriteData = false;
            _writeAddress = 0;
            _writeData = 0;
            _writeBits = 0;
        }
    }

    private sealed class Cps1AudioBus : EutherDrive.Core.Cpu.Z80Emu.IOpcodeBusInterface
    {
        private readonly Cps1Bus _bus;

        public Cps1AudioBus(Cps1Bus bus)
        {
            _bus = bus;
        }

        public byte ReadMemory(ushort address) => _bus.ReadAudioMemory(address);

        public byte ReadOpcode(ushort address) => _bus.ReadAudioOpcode(address);

        public void WriteMemory(ushort address, byte value) => _bus.WriteAudioMemory(address, value);

        public byte ReadIo(ushort address)
        {
            _ = address;
            return 0xff;
        }

        public void WriteIo(ushort address, byte value)
        {
            _ = address;
            _ = value;
        }

        public EutherDrive.Core.Cpu.Z80Emu.InterruptLine Nmi()
            => EutherDrive.Core.Cpu.Z80Emu.InterruptLine.High;

        public EutherDrive.Core.Cpu.Z80Emu.InterruptLine Int() => _bus.AudioInterruptLine;

        public bool BusReq() => false;

        public bool Reset() => false;
    }

    private static class Cps1Kabuki
    {
        public static void DecodeDino(ReadOnlySpan<byte> source, Span<byte> opcodeDestination, Span<byte> dataDestination)
        {
            int length = Math.Min(source.Length, Math.Min(Math.Min(opcodeDestination.Length, dataDestination.Length), 0x8000));
            for (int address = 0; address < length; address++)
            {
                opcodeDestination[address] = (byte)ByteDecode(source[address], 0x76543210, 0x24601357, 0x43, address + 0x4343);
                dataDestination[address] = (byte)ByteDecode(source[address], 0x76543210, 0x24601357, 0x43, (address ^ 0x1fc0) + 0x4343 + 1);
            }
        }

        private static int ByteDecode(int value, int swapKey1, int swapKey2, int xorKey, int select)
        {
            value = BitSwap1(value, swapKey1 & 0xffff, select & 0xff);
            value = RotateLeft1(value);
            value = BitSwap2(value, swapKey1 >> 16, select & 0xff);
            value ^= xorKey;
            value = RotateLeft1(value);
            value = BitSwap2(value, swapKey2 & 0xffff, select >> 8);
            value = RotateLeft1(value);
            value = BitSwap1(value, swapKey2 >> 16, select >> 8);
            return value & 0xff;
        }

        private static int BitSwap1(int value, int key, int select)
        {
            if ((select & (1 << ((key >> 0) & 7))) != 0)
                value = (value & 0xfc) | ((value & 0x01) << 1) | ((value & 0x02) >> 1);
            if ((select & (1 << ((key >> 4) & 7))) != 0)
                value = (value & 0xf3) | ((value & 0x04) << 1) | ((value & 0x08) >> 1);
            if ((select & (1 << ((key >> 8) & 7))) != 0)
                value = (value & 0xcf) | ((value & 0x10) << 1) | ((value & 0x20) >> 1);
            if ((select & (1 << ((key >> 12) & 7))) != 0)
                value = (value & 0x3f) | ((value & 0x40) << 1) | ((value & 0x80) >> 1);
            return value;
        }

        private static int BitSwap2(int value, int key, int select)
        {
            if ((select & (1 << ((key >> 12) & 7))) != 0)
                value = (value & 0xfc) | ((value & 0x01) << 1) | ((value & 0x02) >> 1);
            if ((select & (1 << ((key >> 8) & 7))) != 0)
                value = (value & 0xf3) | ((value & 0x04) << 1) | ((value & 0x08) >> 1);
            if ((select & (1 << ((key >> 4) & 7))) != 0)
                value = (value & 0xcf) | ((value & 0x10) << 1) | ((value & 0x20) >> 1);
            if ((select & (1 << ((key >> 0) & 7))) != 0)
                value = (value & 0x3f) | ((value & 0x40) << 1) | ((value & 0x80) >> 1);
            return value;
        }

        private static int RotateLeft1(int value)
            => ((value & 0x7f) << 1) | ((value & 0x80) >> 7);
    }

    private sealed class Cps1Video
    {
        private const int InternalWidth = 512;
        private const int InternalHeight = 256;
        private const int CropX = 64;
        private const int CropY = 16;
        private const int PaletteEntries = 0x0c00;
        private const int ObjBytes = 0x0800;
        private const int ScrollBytes = 0x4000;
        private const int OtherBytes = 0x0800;
        private const int TransparentPen = 15;

        private readonly Cps1Bus _bus;
        private readonly Cps1Graphics _graphics;
        private readonly ushort[] _pixels = new ushort[InternalWidth * InternalHeight];
        private readonly byte[] _spritePriority = new byte[InternalWidth * InternalHeight];
        private readonly uint[] _palette = new uint[PaletteEntries];

        public Cps1Video(Cps1Bus bus, byte[] gfxRom)
        {
            _bus = bus;
            _graphics = new Cps1Graphics(gfxRom);
            for (int i = 0; i < _palette.Length; i++)
                _palette[i] = 0xff000000;
        }

        public void Render(byte[] frameBuffer)
        {
            BuildPalette();
            Array.Fill(_pixels, (ushort)0x0bff);
            Array.Clear(_spritePriority);

            ushort layerControl = CpsB(Cps1DinoConfig.LayerControl / 2);
            int l0 = (layerControl >> 6) & 0x03;
            int l1 = (layerControl >> 8) & 0x03;
            int l2 = (layerControl >> 10) & 0x03;
            int l3 = (layerControl >> 12) & 0x03;

            DrawLayer(l0);
            if (l1 == 0)
                MarkSpritePriorityLayer(l0);
            DrawLayer(l1);
            if (l2 == 0)
                MarkSpritePriorityLayer(l1);
            DrawLayer(l2);
            if (l3 == 0)
                MarkSpritePriorityLayer(l2);
            DrawLayer(l3);

            int dst = 0;
            for (int y = 0; y < FrameHeight; y++)
            {
                int src = (y + CropY) * InternalWidth + CropX;
                for (int x = 0; x < FrameWidth; x++)
                {
                    uint argb = _palette[_pixels[src + x] % _palette.Length];
                    frameBuffer[dst + 0] = (byte)argb;
                    frameBuffer[dst + 1] = (byte)(argb >> 8);
                    frameBuffer[dst + 2] = (byte)(argb >> 16);
                    frameBuffer[dst + 3] = 0xff;
                    dst += 4;
                }
            }
        }

        private void BuildPalette()
        {
            ReadOnlySpan<ushort> paletteRam = _bus.PaletteRam;
            for (int offset = 0; offset < paletteRam.Length; offset++)
            {
                ushort value = paletteRam[offset];
                int bright = 0x0f + ((value >> 12) << 1);
                int r = ((value >> 8) & 0x0f) * 0x11 * bright / 0x2d;
                int g = ((value >> 4) & 0x0f) * 0x11 * bright / 0x2d;
                int b = (value & 0x0f) * 0x11 * bright / 0x2d;
                _palette[offset] = 0xff000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
            }
        }

        private void DrawLayer(int layer)
        {
            switch (layer)
            {
                case 0:
                    DrawSprites();
                    break;
                case 1:
                    DrawTilemap(0);
                    break;
                case 2:
                    DrawTilemap(1);
                    break;
                case 3:
                    DrawTilemap(2);
                    break;
            }
        }

        private void DrawTilemap(int layer)
            => ProcessTilemap(layer, markSpritePriority: false);

        private void MarkSpritePriorityLayer(int layer)
        {
            if (layer is >= 1 and <= 3)
                ProcessTilemap(layer - 1, markSpritePriority: true);
        }

        private void ProcessTilemap(int layer, bool markSpritePriority)
        {
            int tileSize = layer switch { 0 => 8, 1 => 16, _ => 32 };
            int mapSize = tileSize * 64;
            int baseIndex = _bus.Cps1Base(layer switch
            {
                0 => Cps1Regs.Scroll1Base,
                1 => Cps1Regs.Scroll2Base,
                _ => Cps1Regs.Scroll3Base
            }, ScrollBytes);

            int otherBase = _bus.Cps1Base(Cps1Regs.OtherBase, OtherBytes);
            ushort videoControl = CpsA(Cps1Regs.VideoControl);
            bool rowScroll = layer == 1 && (videoControl & 1) != 0;
            int rowScrollOffset = CpsA(Cps1Regs.RowScrollOffset);

            int scrollX = CpsA(layer switch
            {
                0 => Cps1Regs.Scroll1ScrollX,
                1 => Cps1Regs.Scroll2ScrollX,
                _ => Cps1Regs.Scroll3ScrollX
            });
            int scrollY = CpsA(layer switch
            {
                0 => Cps1Regs.Scroll1ScrollY,
                1 => Cps1Regs.Scroll2ScrollY,
                _ => Cps1Regs.Scroll3ScrollY
            });

            for (int y = 0; y < InternalHeight; y++)
            {
                int effectiveScrollX = scrollX;
                if (rowScroll)
                {
                    int row = (y - scrollY) & 0x3ff;
                    effectiveScrollX += _bus.ReadGfxWord(otherBase + ((row + rowScrollOffset) & 0x3ff));
                }

                int sourceY = (y + scrollY) & (mapSize - 1);
                int tileRow = sourceY / tileSize;
                int localY = sourceY & (tileSize - 1);

                int dst = y * InternalWidth;
                for (int x = 0; x < InternalWidth; x++)
                {
                    int sourceX = (x + effectiveScrollX) & (mapSize - 1);
                    int tileCol = sourceX / tileSize;
                    int localX = sourceX & (tileSize - 1);
                    int tileIndex = TileIndex(layer, tileCol, tileRow);
                    ushort codeWord = _bus.ReadGfxWord(baseIndex + tileIndex * 2);
                    ushort attr = _bus.ReadGfxWord(baseIndex + tileIndex * 2 + 1);
                    int code = layer == 2 ? codeWord & 0x3fff : codeWord;
                    int priorityGroup = (attr >> 7) & 0x03;
                    ushort priorityMask = PriorityMask(priorityGroup);
                    int flip = (attr >> 5) & 0x03;
                    int px = (flip & 0x01) != 0 ? tileSize - 1 - localX : localX;
                    int py = (flip & 0x02) != 0 ? tileSize - 1 - localY : localY;
                    int pen = layer switch
                    {
                        0 => _graphics.GetScroll1Pen(code, (tileIndex & 0x20) != 0, px, py),
                        1 => _graphics.GetTile16Pen(code, px, py),
                        _ => _graphics.GetTile32Pen(code, px, py)
                    };
                    if (pen != TransparentPen)
                    {
                        if (markSpritePriority)
                        {
                            if (((priorityMask >> pen) & 1) != 0)
                                _spritePriority[dst + x] = 1;
                        }
                        else
                        {
                            int color = ((attr & 0x1f) + (layer switch { 0 => 0x20, 1 => 0x40, _ => 0x60 })) * 16;
                            _pixels[dst + x] = (ushort)((color + pen) % PaletteEntries);
                        }
                    }
                }
            }
        }

        private void DrawSprites()
        {
            ReadOnlySpan<ushort> obj = _bus.BufferedObj;
            int last = obj.Length - 4;
            for (int offset = 0; offset < obj.Length; offset += 4)
            {
                if ((obj[offset + 3] & 0xff00) == 0xff00)
                {
                    last = offset - 4;
                    break;
                }
            }

            int baseIndex = 0;
            for (int i = last; i >= 0; i -= 4)
            {
                int x = obj[baseIndex + 0] & 0x01ff;
                int y = obj[baseIndex + 1] & 0x01ff;
                int code = obj[baseIndex + 2];
                ushort attr = obj[baseIndex + 3];
                baseIndex += 4;

                int color = attr & 0x1f;
                bool flipX = (attr & 0x20) != 0;
                bool flipY = (attr & 0x40) != 0;

                if ((attr & 0xff00) != 0)
                {
                    int nx = ((attr >> 8) & 0x0f) + 1;
                    int ny = ((attr >> 12) & 0x0f) + 1;
                    for (int yy = 0; yy < ny; yy++)
                    {
                        int sy = (y + yy * 16) & 0x01ff;
                        for (int xx = 0; xx < nx; xx++)
                        {
                            int sx = (x + xx * 16) & 0x01ff;
                            int tile = SpriteBlockCode(code, xx, yy, nx, ny, flipX, flipY);
                            DrawSpriteTile(tile, color, flipX, flipY, sx, sy);
                        }
                    }
                }
                else
                {
                    DrawSpriteTile(code, color, flipX, flipY, x, y);
                }
            }
        }

        private void DrawSpriteTile(int code, int color, bool flipX, bool flipY, int sx, int sy)
        {
            if ((uint)code >= _graphics.Tile16Count)
                return;

            int baseColor = color * 16;
            for (int y = 0; y < 16; y++)
            {
                int dy = sy + y;
                if ((uint)dy >= InternalHeight)
                    continue;

                int py = flipY ? 15 - y : y;
                int row = dy * InternalWidth;
                for (int x = 0; x < 16; x++)
                {
                    int dx = sx + x;
                    if ((uint)dx >= InternalWidth)
                        continue;

                    int px = flipX ? 15 - x : x;
                    int pen = _graphics.GetTile16Pen(code, px, py);
                    if (pen != TransparentPen && _spritePriority[row + dx] == 0)
                    {
                        _pixels[row + dx] = (ushort)(baseColor + pen);
                        _spritePriority[row + dx] = 31;
                    }
                }
            }
        }

        private static int SpriteBlockCode(int code, int x, int y, int nx, int ny, bool flipX, bool flipY)
        {
            int localX = flipX ? nx - 1 - x : x;
            int localY = flipY ? ny - 1 - y : y;
            return (code & ~0x0f) + ((code + localX) & 0x0f) + 0x10 * localY;
        }

        private static int TileIndex(int layer, int col, int row)
        {
            return layer switch
            {
                0 => (row & 0x1f) + ((col & 0x3f) << 5) + ((row & 0x20) << 6),
                1 => (row & 0x0f) + ((col & 0x3f) << 4) + ((row & 0x30) << 6),
                _ => (row & 0x07) + ((col & 0x3f) << 3) + ((row & 0x38) << 6)
            };
        }

        private ushort CpsA(int index) => _bus.CpsA[index];

        private ushort CpsB(int index) => _bus.CpsB[index];

        private ushort PriorityMask(int group)
        {
            int register = group switch
            {
                0 => Cps1DinoConfig.PriorityMask0 / 2,
                1 => Cps1DinoConfig.PriorityMask1 / 2,
                2 => Cps1DinoConfig.PriorityMask2 / 2,
                _ => Cps1DinoConfig.PriorityMask3 / 2
            };
            return CpsB(register);
        }
    }

    private sealed class Cps1Graphics
    {
        private readonly byte[] _scroll1Left;
        private readonly byte[] _scroll1Right;
        private readonly byte[] _tiles16;
        private readonly byte[] _tiles32;

        public Cps1Graphics(byte[] gfx)
        {
            _scroll1Left = Decode(gfx, 8, 8, 64, 0);
            _scroll1Right = Decode(gfx, 8, 8, 64, 32);
            _tiles16 = Decode(gfx, 16, 16, 128, 0);
            _tiles32 = Decode(gfx, 32, 32, 512, 0);
            Tile16Count = _tiles16.Length / (16 * 16);
        }

        public int Tile16Count { get; }

        public int GetScroll1Pen(int code, bool rightHalf, int x, int y)
        {
            byte[] data = rightHalf ? _scroll1Right : _scroll1Left;
            int offset = code * 64 + y * 8 + x;
            return (uint)offset < data.Length ? data[offset] : 15;
        }

        public int GetTile16Pen(int code, int x, int y)
        {
            int offset = code * 256 + y * 16 + x;
            return (uint)offset < _tiles16.Length ? _tiles16[offset] : 15;
        }

        public int GetTile32Pen(int code, int x, int y)
        {
            int offset = code * 1024 + y * 32 + x;
            return (uint)offset < _tiles32.Length ? _tiles32[offset] : 15;
        }

        private static byte[] Decode(byte[] gfx, int width, int height, int bytesPerTile, int xStartBits)
        {
            int tileCount = gfx.Length / bytesPerTile;
            byte[] decoded = new byte[tileCount * width * height];
            int[] planeOffsets = { 24, 16, 8, 0 };

            for (int tile = 0; tile < tileCount; tile++)
            {
                int tileBitBase = tile * bytesPerTile * 8;
                int tilePixelBase = tile * width * height;
                int rowStrideBits = Math.Max(width, 16) * 4;
                for (int y = 0; y < height; y++)
                {
                    int yBits = y * rowStrideBits;
                    for (int x = 0; x < width; x++)
                    {
                        int xBits = xStartBits + (x & 7) + (x & ~7) * 4;
                        int pen = 0;
                        for (int plane = 0; plane < 4; plane++)
                        {
                            int bit = tileBitBase + yBits + xBits + planeOffsets[plane];
                            pen |= ReadBit(gfx, bit) << (3 - plane);
                        }
                        decoded[tilePixelBase + y * width + x] = (byte)pen;
                    }
                }
            }

            return decoded;
        }

        private static int ReadBit(byte[] data, int bitOffset)
        {
            int byteOffset = bitOffset >> 3;
            if ((uint)byteOffset >= data.Length)
                return 0;
            return (data[byteOffset] >> (7 - (bitOffset & 7))) & 1;
        }
    }

    private static class Cps1Regs
    {
        public const int ObjBase = 0x00 / 2;
        public const int Scroll1Base = 0x02 / 2;
        public const int Scroll2Base = 0x04 / 2;
        public const int Scroll3Base = 0x06 / 2;
        public const int OtherBase = 0x08 / 2;
        public const int PaletteBase = 0x0a / 2;
        public const int Scroll1ScrollX = 0x0c / 2;
        public const int Scroll1ScrollY = 0x0e / 2;
        public const int Scroll2ScrollX = 0x10 / 2;
        public const int Scroll2ScrollY = 0x12 / 2;
        public const int Scroll3ScrollX = 0x14 / 2;
        public const int Scroll3ScrollY = 0x16 / 2;
        public const int RowScrollOffset = 0x20 / 2;
        public const int VideoControl = 0x22 / 2;
    }

    private static class Cps1DinoConfig
    {
        public const int LayerControl = 0x0a;
        public const int PriorityMask0 = 0x0c;
        public const int PriorityMask1 = 0x0e;
        public const int PriorityMask2 = 0x00;
        public const int PriorityMask3 = 0x02;
        public const int PaletteControl = 0x04;
    }

    private sealed class Cps1DinoRomSet
    {
        private Cps1DinoRomSet(byte[] program, byte[] graphics, byte[] audioCpu, byte[] qsound, byte[] qsoundDsp)
        {
            Program = program;
            Graphics = graphics;
            AudioCpu = audioCpu;
            QSound = qsound;
            QSoundDsp = qsoundDsp;
        }

        public byte[] Program { get; }
        public byte[] Graphics { get; }
        public byte[] AudioCpu { get; }
        public byte[] QSound { get; }
        public byte[] QSoundDsp { get; }

        public static Cps1DinoRomSet Load(string path)
        {
            Dictionary<string, byte[]> entries = ReadArchive(path);

            byte[] program = new byte[0x20_0000];
            Array.Fill(program, (byte)0xff);
            Load16WordSwap(entries, program, 0x000000, "cde_23a.8f", "cde_23a.rom");
            Load16WordSwap(entries, program, 0x080000, "cde_22a.7f", "cde_22a.rom");
            Load16WordSwap(entries, program, 0x100000, "cde_21a.6f", "cde_21a.rom");

            byte[] gfx = new byte[0x40_0000];
            Load64Word(entries, gfx, 0x000000, "cd-1m.3a", "cd_gfx01.rom");
            Load64Word(entries, gfx, 0x000002, "cd-3m.5a", "cd_gfx03.rom");
            Load64Word(entries, gfx, 0x000004, "cd-2m.4a", "cd_gfx02.rom");
            Load64Word(entries, gfx, 0x000006, "cd-4m.6a", "cd_gfx04.rom");
            Load64Word(entries, gfx, 0x200000, "cd-5m.7a", "cd_gfx05.rom");
            Load64Word(entries, gfx, 0x200002, "cd-7m.9a", "cd_gfx07.rom");
            Load64Word(entries, gfx, 0x200004, "cd-6m.8a", "cd_gfx06.rom");
            Load64Word(entries, gfx, 0x200006, "cd-8m.10a", "cd_gfx08.rom");

            byte[] audioCpu = new byte[0x2_8000];
            byte[] audio = Find(entries, "cd_q.5k", "cd_q.rom");
            audio.AsSpan(0, 0x8000).CopyTo(audioCpu);
            audio.AsSpan(0x8000, Math.Min(0x18000, audio.Length - 0x8000)).CopyTo(audioCpu.AsSpan(0x10000));

            byte[] qsound = new byte[0x20_0000];
            Find(entries, "cd-q1.1k", "cd_q1.rom").CopyTo(qsound.AsSpan(0x000000));
            Find(entries, "cd-q2.2k", "cd_q2.rom").CopyTo(qsound.AsSpan(0x080000));
            Find(entries, "cd-q3.3k", "cd_q3.rom").CopyTo(qsound.AsSpan(0x100000));
            Find(entries, "cd-q4.4k", "cd_q4.rom").CopyTo(qsound.AsSpan(0x180000));

            byte[] qsoundDsp = LoadQSoundDsp(path, entries);

            return new Cps1DinoRomSet(program, gfx, audioCpu, qsound, qsoundDsp);
        }

        private static Dictionary<string, byte[]> ReadArchive(string path)
        {
            using IArchive archive = ArchiveFactory.Open(path);
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

        private static void Load16WordSwap(Dictionary<string, byte[]> entries, byte[] destination, int offset, params string[] names)
        {
            byte[] source = Find(entries, names);
            if (offset + source.Length > destination.Length)
                throw new InvalidDataException($"ROM '{names[0]}' is too large for the CPS1 program region.");

            for (int i = 0; i < source.Length; i += 2)
            {
                destination[offset + i] = source[i + 1];
                destination[offset + i + 1] = source[i];
            }
        }

        private static void Load64Word(Dictionary<string, byte[]> entries, byte[] destination, int offset, params string[] names)
        {
            byte[] source = Find(entries, names);
            int words = source.Length / 2;
            for (int i = 0; i < words; i++)
            {
                int src = i * 2;
                int dst = offset + i * 8;
                if (dst + 1 >= destination.Length)
                    throw new InvalidDataException($"ROM '{names[0]}' is too large for the CPS1 graphics region.");
                destination[dst] = source[src];
                destination[dst + 1] = source[src + 1];
            }
        }

        private static byte[] Find(Dictionary<string, byte[]> entries, params string[] names)
        {
            foreach (string name in names)
            {
                if (entries.TryGetValue(name, out byte[]? data))
                    return data;
            }

            string wanted = string.Join(", ", names);
            string present = string.Join(", ", entries.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).Take(24));
            throw new InvalidDataException(
                string.Create(CultureInfo.InvariantCulture, $"Missing CPS1 dino ROM file ({wanted}). Present files: {present}"));
        }

        private static byte[] LoadQSoundDsp(string mainArchivePath, Dictionary<string, byte[]> mainEntries)
        {
            if (TryFind(mainEntries, out byte[] embedded, "dl-1425.bin"))
                return NormalizeQSoundDsp(embedded);

            string? directory = Path.GetDirectoryName(mainArchivePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                string dspPath = Path.Combine(directory, "dl-1425.bin");
                if (File.Exists(dspPath))
                    return NormalizeQSoundDsp(File.ReadAllBytes(dspPath));

                string qsoundZip = Path.Combine(directory, "qsound.zip");
                if (File.Exists(qsoundZip))
                {
                    Dictionary<string, byte[]> qsoundEntries = ReadArchive(qsoundZip);
                    if (TryFind(qsoundEntries, out byte[] fromZip, "dl-1425.bin"))
                        return NormalizeQSoundDsp(fromZip);
                }
            }

            throw new InvalidDataException(
                "Missing CPS1 QSound DSP ROM 'dl-1425.bin'. Put dl-1425.bin or qsound.zip beside dino.zip; the pure C# QSound HLE needs the real DL-1425 tables.");
        }

        private static bool TryFind(Dictionary<string, byte[]> entries, out byte[] data, params string[] names)
        {
            foreach (string name in names)
            {
                if (entries.TryGetValue(name, out byte[]? found) && found is not null)
                {
                    data = found;
                    return true;
                }
            }

            data = Array.Empty<byte>();
            return false;
        }

        private static byte[] NormalizeQSoundDsp(byte[] source)
        {
            if (source.Length < 0x2000)
                throw new InvalidDataException("QSound DSP ROM 'dl-1425.bin' is shorter than 0x2000 bytes.");

            byte[] result = new byte[0x2000];
            source.AsSpan(0, 0x2000).CopyTo(result);
            return result;
        }
    }
}
