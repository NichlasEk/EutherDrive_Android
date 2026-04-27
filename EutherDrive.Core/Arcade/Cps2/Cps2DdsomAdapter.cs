using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using EutherDrive.Core.Arcade.Cps1;
using EutherDrive.Core.Cpu.M68000Emu;
using SharpCompress.Archives;

namespace EutherDrive.Core.Arcade.Cps2;

// CPS2 hardware notes, memory map and ROM layout are translated from MAME's
// BSD-3-Clause Capcom CPS2 driver by Paul Leaman.
public sealed class Cps2DdsomAdapter : IEmulatorCore
{
    private const int OutputSampleRate = 44_100;
    private const int OutputChannels = 2;
    private const double TargetFps = (16_000_000.0 / 2.0) / (512.0 * 262.0);
    private const int FrameWidth = 384;
    private const int FrameHeight = 224;
    private const int FrameStride = FrameWidth * 4;
    private static readonly int CpuCyclesPerFrame = Math.Max(1, (int)Math.Round(16_000_000.0 / TargetFps));
    private static readonly int AudioCpuCyclesPerFrame = Math.Max(1, (int)Math.Round(8_000_000.0 / TargetFps));

    private readonly byte[] _frameBuffer = new byte[FrameHeight * FrameStride];
    private readonly Cps2Bus _bus = new();
    private readonly Cps2AudioBus _audioBus;
    private readonly M68000 _mainCpu = M68000.CreateBuilder()
        .AllowTasWrites(true)
        .Name("cps2-main")
        .Build();
    private readonly EutherDrive.Core.Cpu.Z80Emu.Z80 _audioCpu = new();

    private Cps2Video? _video;
    private short[] _audioBuffer = Array.Empty<short>();
    private int _audioSampleFramesThisFrame;
    private double _audioSampleAccumulator;
    private ArcadeInputState _input;
    private bool _loaded;

    public Cps2DdsomAdapter()
    {
        _audioBus = new Cps2AudioBus(_bus);
    }

    public static bool IsSupportedArchive(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !RomArchiveExtractor.IsArchivePath(path))
            return false;

        string name = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
        return name is "ddsom" or "ddsomu";
    }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("CPS2 ROM path is empty.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("CPS2 ROM archive not found.", path);

        Cps2DdsomRomSet roms = Cps2DdsomRomSet.Load(path);
        _bus.Load(roms);
        _video = new Cps2Video(_bus, roms.Graphics);
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
        _bus.AssertVBlankInterrupt();

        int mainCycles = 0;
        int audioCycles = 0;
        while (mainCycles < CpuCyclesPerFrame)
        {
            mainCycles += checked((int)_mainCpu.ExecuteInstruction(_bus));
            int targetAudioCycles = Math.Min(
                AudioCpuCyclesPerFrame,
                (int)((long)mainCycles * AudioCpuCyclesPerFrame / CpuCyclesPerFrame));
            int audioSlice = targetAudioCycles - audioCycles;
            if (audioSlice > 0)
                audioCycles += _bus.RunAudioCpu(_audioCpu, _audioBus, audioSlice);
        }

        _bus.ClearInterrupt();
        if (audioCycles < AudioCpuCyclesPerFrame)
            _bus.RunAudioCpu(_audioCpu, _audioBus, AudioCpuCyclesPerFrame - audioCycles);
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
        _input = new ArcadeInputState(up, down, left, right, a, b, c, x, start, mode);
    }

    private readonly record struct ArcadeInputState(
        bool Up,
        bool Down,
        bool Left,
        bool Right,
        bool Button1,
        bool Button2,
        bool Button3,
        bool Button4,
        bool Start,
        bool Coin);

    private sealed class Cps2Bus : IBusInterface, IOpcodeBusInterface
    {
        private const int MainRomSize = 0x40_0000;
        private const int MainRamSize = 0x01_0000;
        private const int AddonRamSize = 0x004000;
        private const int GfxRamBytes = 0x03_0000;
        private const int GfxRamWords = GfxRamBytes / 2;
        private const int ObjBytes = 0x2000;
        private const int ObjWords = ObjBytes / 2;
        private const int PaletteAlignBytes = 0x0400;
        private const int PaletteEntries = 0x0c00;

        private readonly byte[] _mainRom = new byte[MainRomSize];
        private readonly byte[] _opcodeRom = new byte[MainRomSize];
        private readonly byte[] _mainRam = new byte[MainRamSize];
        private readonly byte[] _addonRam = new byte[AddonRamSize];
        private readonly byte[] _audioCpu = new byte[0x5_0000];
        private readonly byte[] _qsoundShared0 = new byte[0x1000];
        private readonly byte[] _qsoundShared1 = new byte[0x1000];
        private readonly ushort[] _gfxRam = new ushort[GfxRamWords];
        private readonly ushort[] _paletteRam = new ushort[PaletteEntries];
        private readonly ushort[][] _objRam = { new ushort[ObjWords], new ushort[ObjWords] };
        private readonly ushort[] _bufferedObj = new ushort[ObjWords];
        private readonly ushort[] _output = new ushort[0x06];
        private readonly ushort[] _cpsA = new ushort[0x20];
        private readonly ushort[] _cpsB = new ushort[0x20];
        private readonly Cps1QSound _qsound = new();

        private ArcadeInputState _input;
        private byte _interruptLevel;
        private int _objRamBank;
        private int _audioBank;
        private int _audioIrqCountdown;
        private bool _audioIrqAsserted;
        private short[]? _audioFrameBuffer;
        private int _audioFrameCycles;
        private int _audioFrameSampleIndex;

        public ReadOnlySpan<ushort> GfxRam => _gfxRam;
        public ReadOnlySpan<ushort> PaletteRam => _paletteRam;
        public ReadOnlySpan<ushort> BufferedObj => _bufferedObj;
        public ReadOnlySpan<ushort> Output => _output;
        public ReadOnlySpan<ushort> CpsA => _cpsA;
        public ReadOnlySpan<ushort> CpsB => _cpsB;
        public BusSignals Signals => new(false);
        public ushort CurrentOpcode => 0;

        public void Load(Cps2DdsomRomSet roms)
        {
            Array.Fill(_mainRom, (byte)0xff);
            Array.Fill(_opcodeRom, (byte)0xff);
            Array.Clear(_mainRam);
            Array.Clear(_addonRam);
            Array.Clear(_audioCpu);
            Array.Clear(_qsoundShared0);
            Array.Clear(_qsoundShared1);
            Array.Clear(_gfxRam);
            Array.Clear(_paletteRam);
            Array.Clear(_objRam[0]);
            Array.Clear(_objRam[1]);
            Array.Clear(_bufferedObj);
            Array.Clear(_output);
            Array.Clear(_cpsA);
            Array.Clear(_cpsB);

            roms.Program.CopyTo(_mainRom.AsSpan(0, roms.Program.Length));
            roms.Opcodes.CopyTo(_opcodeRom.AsSpan(0, roms.Opcodes.Length));
            roms.AudioCpu.CopyTo(_audioCpu.AsSpan(0, roms.AudioCpu.Length));
            _qsound.Load(roms.QSound, roms.QSoundDsp);
            ResetVideoRegisters();
            ResetAudioState();
            _interruptLevel = 0;
            _objRamBank = 0;
        }

        public void ResetMachine()
        {
            Array.Clear(_mainRam);
            Array.Clear(_addonRam);
            Array.Clear(_qsoundShared0);
            Array.Clear(_qsoundShared1);
            Array.Clear(_gfxRam);
            Array.Clear(_paletteRam);
            Array.Clear(_objRam[0]);
            Array.Clear(_objRam[1]);
            Array.Clear(_bufferedObj);
            Array.Clear(_output);
            Array.Clear(_cpsA);
            Array.Clear(_cpsB);
            ResetVideoRegisters();
            ResetAudioState();
            _interruptLevel = 0;
            _objRamBank = 0;
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
            ushort[] source = (_objRamBank & 1) != 0 ? _objRam[1] : _objRam[0];
            Array.Copy(source, _bufferedObj, _bufferedObj.Length);
        }

        public int RunAudioCpu(EutherDrive.Core.Cpu.Z80Emu.Z80 audioCpu, Cps2AudioBus audioBus, int cycleBudget)
        {
            int cycles = 0;
            while (cycles < cycleBudget)
            {
                if (_audioIrqCountdown <= 0)
                {
                    if (!_audioIrqAsserted)
                        _audioIrqAsserted = true;
                    do
                    {
                        _audioIrqCountdown += 32_000;
                    }
                    while (_audioIrqCountdown <= 0);
                }

                int elapsed = checked((int)audioCpu.ExecuteInstruction(audioBus));
                cycles += elapsed;
                _audioIrqCountdown -= elapsed;
                AdvanceAudioFrame(elapsed);

                if (audioCpu.LastInterruptAccepted)
                    _audioIrqAsserted = false;
            }

            return cycles;
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

        public byte ReadAudioOpcode(ushort address) => ReadAudioMemory(address);

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
            if (address >= 0x618000 && address <= 0x619fff)
                return ReadQSoundSharedByte(_qsoundShared0, address - 0x618000);

            return 0xff;
        }

        public ushort ReadWord(uint address)
        {
            address &= 0x00ff_ffff;

            if (address < MainRomSize - 1)
                return ReadBigEndianWord(_mainRom, (int)address);
            if (address >= 0x400000 && address <= 0x40000b)
                return _output[(address - 0x400000) >> 1];
            if (address >= 0x618000 && address <= 0x619fff)
                return (ushort)(0xff00 | _qsoundShared0[(address - 0x618000) >> 1]);
            if (address >= 0x660000 && address <= 0x663fff)
                return ReadBigEndianWord(_addonRam, (int)(address - 0x660000));
            if (address >= 0x700000 && address <= 0x701fff)
                return _objRam[_objRamBank & 1][(address - 0x700000) >> 1];
            if (address >= 0x708000 && address <= 0x70ffff)
                return _objRam[(_objRamBank ^ 1) & 1][((address - 0x708000) & 0x1fff) >> 1];
            if (address >= 0x800140 && address <= 0x80017f)
                return ReadCpsB((int)((address - 0x800140) >> 1));
            if (address >= 0x804000 && address <= 0x804001)
                return Input0();
            if (address >= 0x804010 && address <= 0x804011)
                return 0xffff;
            if (address >= 0x804020 && address <= 0x804021)
                return Input2();
            if (address >= 0x804030 && address <= 0x804031)
                return 0xe021;
            if (address >= 0x804140 && address <= 0x80417f)
                return ReadCpsB((int)((address - 0x804140) >> 1));
            if (address >= 0x900000 && address <= 0x92ffff)
                return ReadGfxWord((int)((address - 0x900000) >> 1));
            if (address >= 0xff0000)
                return ReadBigEndianWord(_mainRam, (int)(address & 0xffff));

            return 0xffff;
        }

        public ushort ReadOpcodeWord(uint address)
        {
            address &= 0x00ff_ffff;
            if (address < MainRomSize - 1)
                return ReadBigEndianWord(_opcodeRom, (int)address);
            return ReadWord(address);
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

            if (address >= 0x400000 && address <= 0x40000b)
            {
                WriteWordByte(ref _output[(address - 0x400000) >> 1], address, value);
                return;
            }
            if (address >= 0x618000 && address <= 0x619fff)
            {
                if ((address & 1) != 0)
                    _qsoundShared0[(address - 0x618000) >> 1] = value;
                return;
            }
            if (address >= 0x660000 && address <= 0x663fff)
            {
                _addonRam[address - 0x660000] = value;
                return;
            }
            if (address >= 0x700000 && address <= 0x701fff)
            {
                WriteWordByte(ref _objRam[_objRamBank & 1][(address - 0x700000) >> 1], address, value);
                return;
            }
            if (address >= 0x708000 && address <= 0x70ffff)
            {
                WriteWordByte(ref _objRam[(_objRamBank ^ 1) & 1][((address - 0x708000) & 0x1fff) >> 1], address, value);
                return;
            }
            if (address >= 0x804100 && address <= 0x80413f)
            {
                int index = (int)((address - 0x804100) >> 1);
                WriteWordByte(ref _cpsA[index], address, value);
                if (index == Cps1Regs.PaletteBase)
                    LatchPalette();
                return;
            }
            if (address >= 0x804140 && address <= 0x80417f)
            {
                int index = (int)((address - 0x804140) >> 1);
                WriteWordByte(ref _cpsB[index], address, value);
                if (index == Cps2Config.PaletteControl / 2)
                    LatchPalette();
                return;
            }
            if (address >= 0x900000 && address <= 0x92ffff)
            {
                int index = (int)((address - 0x900000) >> 1);
                WriteWordByte(ref _gfxRam[index], address, value);
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

            if (address >= 0x400000 && address <= 0x40000b)
            {
                _output[(address - 0x400000) >> 1] = value;
                return;
            }
            if (address >= 0x618000 && address <= 0x619fff)
            {
                _qsoundShared0[(address - 0x618000) >> 1] = (byte)value;
                return;
            }
            if (address >= 0x660000 && address <= 0x663fff)
            {
                WriteBigEndianWord(_addonRam, (int)(address - 0x660000), value);
                return;
            }
            if (address >= 0x700000 && address <= 0x701fff)
            {
                _objRam[_objRamBank & 1][(address - 0x700000) >> 1] = value;
                return;
            }
            if (address >= 0x708000 && address <= 0x70ffff)
            {
                _objRam[(_objRamBank ^ 1) & 1][((address - 0x708000) & 0x1fff) >> 1] = value;
                return;
            }
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
                if (index == Cps2Config.PaletteControl / 2)
                    LatchPalette();
                return;
            }
            if (address >= 0x804040 && address <= 0x804041)
                return;
            if (address >= 0x8040e0 && address <= 0x8040e1)
            {
                _objRamBank = value & 1;
                return;
            }
            if (address >= 0x804100 && address <= 0x80413f)
            {
                int index = (int)((address - 0x804100) >> 1);
                _cpsA[index] = value;
                if (index == Cps1Regs.PaletteBase)
                    LatchPalette();
                return;
            }
            if (address >= 0x804140 && address <= 0x80417f)
            {
                int index = (int)((address - 0x804140) >> 1);
                _cpsB[index] = value;
                if (index == Cps2Config.PaletteControl / 2)
                    LatchPalette();
                return;
            }
            if (address >= 0x900000 && address <= 0x92ffff)
            {
                _gfxRam[(address - 0x900000) >> 1] = value;
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
            _audioIrqAsserted = false;
            _qsound.Reset();
        }

        private void LatchPalette()
        {
            int source = Cps1Base(Cps1Regs.PaletteBase, PaletteAlignBytes);
            int cursor = source;
            ushort control = _cpsB[Cps2Config.PaletteControl / 2];

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

        private ushort Input0()
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
            if (_input.Button4)
                value &= ~0x0080;
            return (ushort)value;
        }

        private ushort Input2()
        {
            int value = 0xffff;
            value |= 0x0001;
            if (_input.Start)
                value &= ~0x0100;
            if (_input.Coin)
                value &= ~0x1000;
            return (ushort)value;
        }

        private static ushort ReadCpsB(int offset)
        {
            _ = offset;
            return 0xffff;
        }

        private static bool IsWordMapped(uint address)
            => (address >= 0x400000 && address <= 0x40000b)
               || (address >= 0x804000 && address <= 0x804041)
               || (address >= 0x804100 && address <= 0x80417f)
               || (address >= 0x800100 && address <= 0x80017f)
               || (address >= 0x700000 && address <= 0x70ffff);

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

    private sealed class Cps2AudioBus : EutherDrive.Core.Cpu.Z80Emu.IOpcodeBusInterface
    {
        private readonly Cps2Bus _bus;

        public Cps2AudioBus(Cps2Bus bus)
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

    private sealed class Cps2Video
    {
        private const int InternalWidth = 512;
        private const int InternalHeight = 256;
        private const int CropX = 64;
        private const int CropY = 16;
        private const int PaletteEntries = 0x0c00;
        private const int ScrollBytes = 0x4000;
        private const int OtherBytes = 0x0800;
        private const int TransparentPen = 15;

        private readonly Cps2Bus _bus;
        private readonly Cps2Graphics _graphics;
        private readonly ushort[] _pixels = new ushort[InternalWidth * InternalHeight];
        private readonly uint[] _palette = new uint[PaletteEntries];

        public Cps2Video(Cps2Bus bus, byte[] gfxRom)
        {
            _bus = bus;
            _graphics = new Cps2Graphics(gfxRom);
            for (int i = 0; i < _palette.Length; i++)
                _palette[i] = 0xff000000;
        }

        public void Render(byte[] frameBuffer)
        {
            BuildPalette();
            Array.Fill(_pixels, (ushort)0x0bff);

            ushort layerControl = CpsB(Cps2Config.LayerControl / 2);
            int l0 = (layerControl >> 6) & 0x03;
            int l1 = (layerControl >> 8) & 0x03;
            int l2 = (layerControl >> 10) & 0x03;
            int l3 = (layerControl >> 12) & 0x03;

            if (l0 == 0) { l0 = l1; l1 = 0; }
            if (l1 == 0) { l1 = l2; l2 = 0; }
            if (l2 == 0) { l2 = l3; l3 = 0; }

            DrawLayer(l0);
            DrawLayer(l1);
            DrawLayer(l2);
            DrawSprites();

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
            if (layer is >= 1 and <= 3)
                DrawTilemap(layer - 1);
        }

        private void DrawTilemap(int layer)
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
                        int color = ((attr & 0x1f) + (layer switch { 0 => 0x20, 1 => 0x40, _ => 0x60 })) * 16;
                        _pixels[dst + x] = (ushort)((color + pen) % PaletteEntries);
                    }
                }
            }
        }

        private void DrawSprites()
        {
            ReadOnlySpan<ushort> obj = _bus.BufferedObj;
            ReadOnlySpan<ushort> output = _bus.Output;
            int last = obj.Length - 4;
            for (int offset = 0; offset < obj.Length; offset += 4)
            {
                if (obj[offset + 1] >= 0x8000 || obj[offset + 3] >= 0xff00)
                {
                    last = offset - 4;
                    break;
                }
            }

            int xoffs = 64 - output[4];
            int yoffs = 16 - output[5];
            for (int i = last; i >= 0; i -= 4)
            {
                int x = obj[i + 0];
                int y = obj[i + 1];
                int code = obj[i + 2] + ((y & 0x6000) << 3);
                ushort attr = obj[i + 3];
                int color = attr & 0x1f;
                bool flipX = (attr & 0x20) != 0;
                bool flipY = (attr & 0x40) != 0;
                if ((attr & 0x80) != 0)
                {
                    x += output[4];
                    y += output[5];
                }

                if ((attr & 0xff00) != 0)
                {
                    int nx = ((attr >> 8) & 0x0f) + 1;
                    int ny = ((attr >> 12) & 0x0f) + 1;
                    for (int yy = 0; yy < ny; yy++)
                    {
                        int sy = (y + yy * 16 + yoffs) & 0x3ff;
                        for (int xx = 0; xx < nx; xx++)
                        {
                            int sx = (x + xx * 16 + xoffs) & 0x3ff;
                            int tile = SpriteBlockCode(code, xx, yy, nx, ny, flipX, flipY);
                            DrawSpriteTile(tile, color, flipX, flipY, sx, sy);
                        }
                    }
                }
                else
                {
                    DrawSpriteTile(code, color, flipX, flipY, (x + xoffs) & 0x3ff, (y + yoffs) & 0x3ff);
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
                    if (pen != TransparentPen)
                        _pixels[row + dx] = (ushort)(baseColor + pen);
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
    }

    private sealed class Cps2Graphics
    {
        private readonly byte[] _scroll1Left;
        private readonly byte[] _scroll1Right;
        private readonly byte[] _tiles16;
        private readonly byte[] _tiles32;

        public Cps2Graphics(byte[] gfx)
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

    private static class Cps2Config
    {
        public const int LayerControl = 0x26;
        public const int PaletteControl = 0x30;
    }

    private sealed class Cps2DdsomRomSet
    {
        private Cps2DdsomRomSet(byte[] program, byte[] opcodes, byte[] graphics, byte[] audioCpu, byte[] qsound, byte[] qsoundDsp)
        {
            Program = program;
            Opcodes = opcodes;
            Graphics = graphics;
            AudioCpu = audioCpu;
            QSound = qsound;
            QSoundDsp = qsoundDsp;
        }

        public byte[] Program { get; }
        public byte[] Opcodes { get; }
        public byte[] Graphics { get; }
        public byte[] AudioCpu { get; }
        public byte[] QSound { get; }
        public byte[] QSoundDsp { get; }

        public static Cps2DdsomRomSet Load(string path)
        {
            string setName = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
            Dictionary<string, byte[]> entries = ReadArchive(path);
            if (setName == "ddsomu")
                MergeParentIfPresent(path, entries, "ddsom.zip", "ddsom.7z");

            byte[] program = new byte[0x40_0000];
            Array.Fill(program, (byte)0xff);
            if (setName == "ddsomu")
            {
                Load16WordSwap(entries, program, 0x000000, "dd2u.03g");
                Load16WordSwap(entries, program, 0x080000, "dd2u.04g");
                Load16WordSwap(entries, program, 0x100000, "dd2.05g");
                Load16WordSwap(entries, program, 0x180000, "dd2.06g");
            }
            else
            {
                Load16WordSwap(entries, program, 0x000000, "dd2e.03e");
                Load16WordSwap(entries, program, 0x080000, "dd2e.04e");
                Load16WordSwap(entries, program, 0x100000, "dd2e.05e");
                Load16WordSwap(entries, program, 0x180000, "dd2e.06e");
            }
            Load16WordSwap(entries, program, 0x200000, setName == "ddsomu" ? "dd2.07" : "dd2e.07");
            Load16WordSwap(entries, program, 0x280000, setName == "ddsomu" ? "dd2.08" : "dd2e.08");
            Load16WordSwap(entries, program, 0x300000, setName == "ddsomu" ? "dd2.09" : "dd2e.09");
            Load16WordSwap(entries, program, 0x380000, setName == "ddsomu" ? "dd2.10" : "dd2e.10");

            byte[] key = Find(entries, setName == "ddsomu" ? "ddsomu.key" : "ddsom.key");
            byte[] opcodes = Cps2Decrypter.Decrypt(program, key);

            byte[] gfx = new byte[0x180_0000];
            Load64Word(entries, gfx, 0x0000000, "dd2.13m");
            Load64Word(entries, gfx, 0x0000002, "dd2.15m");
            Load64Word(entries, gfx, 0x0000004, "dd2.17m");
            Load64Word(entries, gfx, 0x0000006, "dd2.19m");
            Load64Word(entries, gfx, 0x1000000, "dd2.14m");
            Load64Word(entries, gfx, 0x1000002, "dd2.16m");
            Load64Word(entries, gfx, 0x1000004, "dd2.18m");
            Load64Word(entries, gfx, 0x1000006, "dd2.20m");
            UnshuffleGraphics(gfx, 0x20_0000);

            byte[] audioCpu = new byte[0x5_0000];
            byte[] audio0 = Find(entries, "dd2.01");
            audio0.AsSpan(0, Math.Min(0x8000, audio0.Length)).CopyTo(audioCpu);
            if (audio0.Length > 0x8000)
                audio0.AsSpan(0x8000, Math.Min(0x18000, audio0.Length - 0x8000)).CopyTo(audioCpu.AsSpan(0x10000));
            Find(entries, "dd2.02").AsSpan(0, 0x20000).CopyTo(audioCpu.AsSpan(0x28000));

            byte[] qsound = new byte[0x40_0000];
            Load16WordSwap(entries, qsound, 0x000000, "dd2.11m");
            Load16WordSwap(entries, qsound, 0x200000, "dd2.12m");
            byte[] qsoundDsp = LoadQSoundDsp(path, entries);

            return new Cps2DdsomRomSet(program, opcodes, gfx, audioCpu, qsound, qsoundDsp);
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

        private static void MergeParentIfPresent(string childPath, Dictionary<string, byte[]> entries, params string[] parentArchives)
        {
            string? directory = Path.GetDirectoryName(childPath);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            string? parentPath = parentArchives
                .Select(parentArchive => Path.Combine(directory, parentArchive))
                .FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(parentPath))
                return;

            foreach (KeyValuePair<string, byte[]> entry in ReadArchive(parentPath))
                entries.TryAdd(entry.Key, entry.Value);
        }

        private static void Load16WordSwap(Dictionary<string, byte[]> entries, byte[] destination, int offset, params string[] names)
        {
            byte[] source = Find(entries, names);
            if (offset + source.Length > destination.Length)
                throw new InvalidDataException($"ROM '{names[0]}' is too large for the CPS2 region.");

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
                    throw new InvalidDataException($"ROM '{names[0]}' is too large for the CPS2 graphics region.");
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
            string present = string.Join(", ", entries.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase).Take(32));
            throw new InvalidDataException(
                string.Create(CultureInfo.InvariantCulture, $"Missing CPS2 ddsom ROM file ({wanted}). Put the parent ddsom.zip or ddsom.7z beside clone zips if this is a split MAME set. Present files: {present}"));
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

                string? qsoundArchive = new[] { "qsound.zip", "qsound.7z" }
                    .Select(fileName => Path.Combine(directory, fileName))
                    .FirstOrDefault(File.Exists);
                if (!string.IsNullOrWhiteSpace(qsoundArchive))
                {
                    Dictionary<string, byte[]> qsoundEntries = ReadArchive(qsoundArchive);
                    if (TryFind(qsoundEntries, out byte[] fromZip, "dl-1425.bin"))
                        return NormalizeQSoundDsp(fromZip);
                }
            }

            throw new InvalidDataException(
                "Missing CPS2 QSound DSP ROM 'dl-1425.bin'. Put dl-1425.bin, qsound.zip or qsound.7z beside ddsom/ddsomu.");
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

        private static void UnshuffleGraphics(byte[] data, int bankSize)
        {
            for (int offset = 0; offset < data.Length; offset += bankSize)
                Unshuffle64(data, offset, bankSize / 8);
        }

        private static void Unshuffle64(byte[] data, int byteOffset, int len)
        {
            if (len == 2)
                return;
            if ((len % 4) != 0)
                throw new InvalidDataException("CPS2 graphics bank has invalid unshuffle length.");

            len /= 2;
            Unshuffle64(data, byteOffset, len);
            Unshuffle64(data, byteOffset + len * 8, len);

            Span<byte> temp = stackalloc byte[8];
            for (int i = 0; i < len / 2; i++)
            {
                int a = byteOffset + (len / 2 + i) * 8;
                int b = byteOffset + (len + i) * 8;
                data.AsSpan(a, 8).CopyTo(temp);
                data.AsSpan(b, 8).CopyTo(data.AsSpan(a, 8));
                temp.CopyTo(data.AsSpan(b, 8));
            }
        }
    }
}
