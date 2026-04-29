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
    private int _audioCpuCyclesPerFrame = GetQSoundAudioCpuCyclesPerFrame();
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
        return Cps1DinoRomSet.IsSupportedSet(name);
    }

    public void LoadRom(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("CPS1 ROM path is empty.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("CPS1 ROM archive not found.", path);

        Cps1DinoRomSet roms = Cps1DinoRomSet.Load(path);
        _bus.Load(roms);
        _audioCpuCyclesPerFrame = Math.Max(1, (int)Math.Round(roms.AudioCpuClockHz / TargetFps));
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
            {
                audioCycles += _bus.RunAudioCpu(_audioCpu, _audioBus, audioSlice);
            }
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

    private int AudioCpuCyclesPerFrame => _audioCpuCyclesPerFrame;

    private static int GetQSoundAudioCpuCyclesPerFrame()
    {
        const double nominalAudioCpuClock = 8_000_000.0;
        const double defaultTimingScale = 1.00;
        double scale = ReadDoubleEnv("EUTHERDRIVE_CPS1_QSOUND_Z80_SCALE", defaultTimingScale);
        if (scale < 0.50)
            scale = 0.50;
        else if (scale > 1.10)
            scale = 1.10;

        return Math.Max(1, (int)Math.Round((nominalAudioCpuClock * scale) / TargetFps));
    }

    private static double ReadDoubleEnv(string name, double fallback)
    {
        string? raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? value
            : fallback;
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

    private enum Cps1AudioHardware
    {
        QSound,
        YmOki
    }

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
        private readonly byte[] _classicAudioRam = new byte[0x0800];
        private readonly byte[] _qsoundShared0 = new byte[0x1000];
        private readonly byte[] _qsoundShared1 = new byte[0x1000];
        private readonly ushort[] _gfxRam = new ushort[GfxRamWords];
        private readonly ushort[] _paletteRam = new ushort[PaletteEntries];
        private readonly ushort[] _bufferedObj = new ushort[ObjWords];
        private readonly ushort[] _cpsA = new ushort[0x20];
        private readonly ushort[] _cpsB = new ushort[0x20];
        private readonly Cps1QSound _qsound = new();
        private readonly Cps1Oki6295 _oki = new();
        private readonly Cps1Ym2151 _ym2151 = new();
        private readonly Cps1SerialEeprom _eeprom = new();
        private Cps1VideoConfig _videoConfig = Cps1VideoConfig.QSound2;
        private Cps1AudioHardware _audioHardware = Cps1AudioHardware.QSound;

        private ArcadeInputState _input;
        private byte _soundLatch0 = 0xff;
        private byte _soundLatch1 = 0xff;
        private byte _interruptLevel;
        private int _audioBank;
        private int _audioIrqCountdown;
        private bool _audioIrqAsserted;
        private short[]? _audioFrameBuffer;
        private int _audioFrameCycles;
        private int _qsoundFrameSampleIndex;
        private int _okiFrameSampleIndex;
        private int _ymFrameSampleIndex;
        private int _audioCpuCyclesPerFrame = GetQSoundAudioCpuCyclesPerFrame();
        private double _audioCpuClockHz = 8_000_000.0;

        public ReadOnlySpan<ushort> GfxRam => _gfxRam;
        public ReadOnlySpan<ushort> PaletteRam => _paletteRam;
        public ReadOnlySpan<ushort> BufferedObj => _bufferedObj;
        public ReadOnlySpan<ushort> CpsA => _cpsA;
        public ReadOnlySpan<ushort> CpsB => _cpsB;
        public Cps1VideoConfig VideoConfig => _videoConfig;
        public bool InterruptAsserted => _interruptLevel != 0;
        public BusSignals Signals => new(false);
        public ushort CurrentOpcode => 0;

        public void Load(Cps1DinoRomSet roms)
        {
            Array.Fill(_mainRom, (byte)0xff);
            Array.Clear(_mainRam);
            Array.Clear(_audioCpu);
            Array.Clear(_audioOpcodes);
            Array.Clear(_classicAudioRam);
            Array.Clear(_qsoundShared0);
            Array.Clear(_qsoundShared1);
            Array.Clear(_gfxRam);
            Array.Clear(_paletteRam);
            Array.Clear(_bufferedObj);
            Array.Clear(_cpsA);
            Array.Clear(_cpsB);

            roms.Program.CopyTo(_mainRom.AsSpan(0, roms.Program.Length));
            roms.AudioCpu.CopyTo(_audioCpu.AsSpan(0, roms.AudioCpu.Length));
            if (roms.AudioHardware == Cps1AudioHardware.QSound)
            {
                byte[] encryptedAudio = _audioCpu.AsSpan(0, 0x8000).ToArray();
                Cps1Kabuki.Decode(
                    encryptedAudio,
                    _audioOpcodes,
                    _audioCpu.AsSpan(0, 0x8000),
                    roms.KabukiSwapKey1,
                    roms.KabukiSwapKey2,
                    roms.KabukiAddressKey,
                    roms.KabukiXorKey);
            }
            else
            {
                _audioCpu.AsSpan(0, 0x8000).CopyTo(_audioOpcodes);
            }

            _audioHardware = roms.AudioHardware;
            _videoConfig = roms.VideoConfig;
            _audioCpuClockHz = roms.AudioCpuClockHz;
            _audioCpuCyclesPerFrame = Math.Max(1, (int)Math.Round(roms.AudioCpuClockHz / TargetFps));
            if (roms.AudioHardware == Cps1AudioHardware.QSound)
                _qsound.Load(roms.QSound, roms.QSoundDsp);
            else
                _qsound.Reset();
            _oki.Load(roms.Oki);
            _eeprom.ResetPins();
            ResetVideoRegisters();
            ResetAudioState();
            _interruptLevel = 0;
        }

        public void ResetMachine()
        {
            Array.Clear(_mainRam);
            Array.Clear(_classicAudioRam);
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
            _qsoundFrameSampleIndex = 0;
            _okiFrameSampleIndex = 0;
            _ymFrameSampleIndex = 0;
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

        public int RunAudioCpu(
            EutherDrive.Core.Cpu.Z80Emu.Z80 audioCpu,
            Cps1AudioBus audioBus,
            int cycleBudget)
        {
            int cycles = 0;
            while (cycles < cycleBudget)
            {
                if (_audioHardware == Cps1AudioHardware.QSound && _audioIrqCountdown <= 0)
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
                if (_audioHardware == Cps1AudioHardware.QSound)
                    _audioIrqCountdown -= elapsed;
                else
                    _ym2151.AdvanceTimersByCpuCycles(elapsed, _audioCpuClockHz);
                AdvanceAudioFrame(elapsed);

                if (_audioHardware == Cps1AudioHardware.QSound && audioCpu.LastInterruptAccepted)
                    _audioIrqAsserted = false;
            }

            return cycles;
        }

        public void EndAudioFrame()
        {
            if (_audioFrameBuffer is null)
                return;

            if (_audioHardware == Cps1AudioHardware.QSound)
            {
                _qsound.RenderFrames(_audioFrameBuffer, ref _qsoundFrameSampleIndex, _audioFrameBuffer.Length / 2);
            }
            else
            {
                int sampleFrames = _audioFrameBuffer.Length / 2;
                _ym2151.RenderStereo(_audioFrameBuffer, ref _ymFrameSampleIndex, sampleFrames, routeToMono: true);
                _oki.RenderStereo(_audioFrameBuffer, ref _okiFrameSampleIndex, sampleFrames);
            }

            _audioFrameBuffer = null;
            _audioFrameCycles = 0;
            _qsoundFrameSampleIndex = 0;
            _okiFrameSampleIndex = 0;
            _ymFrameSampleIndex = 0;
        }

        private void AdvanceAudioFrame(int elapsedCycles)
        {
            if (_audioFrameBuffer is null || elapsedCycles <= 0)
                return;

            _audioFrameCycles = Math.Min(_audioFrameCycles + elapsedCycles, _audioCpuCyclesPerFrame);
        }

        private void SynchronizeQSoundStream()
        {
            if (_audioFrameBuffer is null)
                return;

            int sampleFrames = _audioFrameBuffer.Length / 2;
            int targetFrames = (int)((long)_audioFrameCycles * sampleFrames / _audioCpuCyclesPerFrame);
            _qsound.RenderFrames(_audioFrameBuffer, ref _qsoundFrameSampleIndex, targetFrames);
        }

        private void SynchronizeYmStream()
        {
            if (_audioFrameBuffer is null)
                return;

            int sampleFrames = _audioFrameBuffer.Length / 2;
            int targetFrames = (int)((long)_audioFrameCycles * sampleFrames / _audioCpuCyclesPerFrame);
            _ym2151.RenderStereo(_audioFrameBuffer, ref _ymFrameSampleIndex, targetFrames, routeToMono: true);
        }

        private void SynchronizeOkiStream()
        {
            if (_audioFrameBuffer is null)
                return;

            int sampleFrames = _audioFrameBuffer.Length / 2;
            int targetFrames = (int)((long)_audioFrameCycles * sampleFrames / _audioCpuCyclesPerFrame);
            _oki.RenderStereo(_audioFrameBuffer, ref _okiFrameSampleIndex, targetFrames);
        }

        public EutherDrive.Core.Cpu.Z80Emu.InterruptLine AudioInterruptLine
            => (_audioHardware == Cps1AudioHardware.YmOki ? _ym2151.IrqAsserted : _audioIrqAsserted)
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

            if (_audioHardware == Cps1AudioHardware.QSound)
            {
                if (address >= 0xc000 && address <= 0xcfff)
                    return _qsoundShared0[address - 0xc000];
                if (address == 0xd007)
                {
                    SynchronizeQSoundStream();
                    return _qsound.ReadStatus();
                }
                if (address >= 0xf000)
                    return _qsoundShared1[address - 0xf000];

                return 0xff;
            }

            if (address >= 0xd000 && address <= 0xd7ff)
                return _classicAudioRam[address - 0xd000];
            if (address == 0xf000)
                return 0xff;
            if (address == 0xf001)
                return _ym2151.ReadStatus();
            if (address == 0xf002)
            {
                SynchronizeOkiStream();
                return _oki.ReadStatus();
            }
            if (address == 0xf008)
                return _soundLatch0;
            if (address == 0xf00a)
                return _soundLatch1;

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
            if (_audioHardware == Cps1AudioHardware.QSound)
            {
                if (address >= 0xc000 && address <= 0xcfff)
                {
                    _qsoundShared0[address - 0xc000] = value;
                    return;
                }
                if (address >= 0xd000 && address <= 0xd002)
                {
                    if (address == 0xd002)
                        SynchronizeQSoundStream();
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

                return;
            }

            if (address >= 0xd000 && address <= 0xd7ff)
            {
                _classicAudioRam[address - 0xd000] = value;
                return;
            }
            if (address == 0xf000 || address == 0xf001)
            {
                SynchronizeYmStream();
                _ym2151.Write(address - 0xf000, value);
                return;
            }
            if (address == 0xf002)
            {
                SynchronizeOkiStream();
                _oki.Write(value);
                return;
            }
            if (address == 0xf004)
            {
                _audioBank = (value & 0x01) != 0 ? 1 : 0;
                return;
            }
            if (address == 0xf006)
            {
                SynchronizeOkiStream();
                _oki.SetPin7((value & 0x01) != 0);
            }
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
                if (index == _videoConfig.PaletteControl / 2)
                    LatchPalette();
                return;
            }
            if (_audioHardware == Cps1AudioHardware.YmOki && address >= 0x800180 && address <= 0x800187)
            {
                _soundLatch0 = value;
                return;
            }
            if (_audioHardware == Cps1AudioHardware.YmOki && address >= 0x800188 && address <= 0x80018f)
            {
                _soundLatch1 = value;
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
                if (index == _videoConfig.PaletteControl / 2)
                    LatchPalette();
                return;
            }
            if (_audioHardware == Cps1AudioHardware.YmOki && address >= 0x800180 && address <= 0x800187)
            {
                _soundLatch0 = (byte)value;
                return;
            }
            if (_audioHardware == Cps1AudioHardware.YmOki && address >= 0x800188 && address <= 0x80018f)
            {
                _soundLatch1 = (byte)value;
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
            _audioIrqAsserted = false;
            _audioCpuClockHz = _audioCpuClockHz <= 0.0 ? 8_000_000.0 : _audioCpuClockHz;
            _soundLatch0 = 0xff;
            _soundLatch1 = 0xff;
            _qsound.Reset();
            _oki.Reset();
            _ym2151.Reset();
        }

        private void LatchPalette()
        {
            int source = Cps1Base(Cps1Regs.PaletteBase, PaletteAlignBytes);
            int cursor = source;
            ushort control = _cpsB[_videoConfig.PaletteControl / 2];

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
               || (address >= 0x800100 && address <= 0x80018f)
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
            => Decode(source, opcodeDestination, dataDestination, 0x76543210, 0x24601357, 0x4343, 0x43);

        public static void Decode(
            ReadOnlySpan<byte> source,
            Span<byte> opcodeDestination,
            Span<byte> dataDestination,
            int swapKey1,
            int swapKey2,
            int addressKey,
            int xorKey)
        {
            int length = Math.Min(source.Length, Math.Min(Math.Min(opcodeDestination.Length, dataDestination.Length), 0x8000));
            for (int address = 0; address < length; address++)
            {
                opcodeDestination[address] = (byte)ByteDecode(source[address], swapKey1, swapKey2, xorKey, address + addressKey);
                dataDestination[address] = (byte)ByteDecode(source[address], swapKey1, swapKey2, xorKey, (address ^ 0x1fc0) + addressKey + 1);
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

            Cps1VideoConfig config = _bus.VideoConfig;
            ushort layerControl = CpsB(config.LayerControl / 2);
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
            int register = _bus.VideoConfig.PriorityMask(group) / 2;
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

    private readonly record struct Cps1VideoConfig(int LayerControl, int Priority0, int Priority1, int Priority2, int Priority3, int PaletteControl)
    {
        public static readonly Cps1VideoConfig Default = new(0x26, 0x28, 0x2a, 0x2c, 0x2e, 0x30);
        public static readonly Cps1VideoConfig QSound1 = new(0x22, 0x24, 0x26, 0x28, 0x2a, 0x2c);
        public static readonly Cps1VideoConfig QSound2 = new(0x0a, 0x0c, 0x0e, 0x00, 0x02, 0x04);
        public static readonly Cps1VideoConfig QSound3 = new(0x12, 0x14, 0x16, 0x08, 0x0a, 0x0c);
        public static readonly Cps1VideoConfig QSound4 = new(0x16, 0x00, 0x02, 0x28, 0x2a, 0x2c);
        public static readonly Cps1VideoConfig QSound5 = new(0x2a, 0x2c, 0x2e, 0x30, 0x32, 0x1c);

        public int PriorityMask(int group) => group switch
        {
            0 => Priority0,
            1 => Priority1,
            2 => Priority2,
            _ => Priority3
        };
    }

    private sealed class Cps1DinoRomSet
    {
        private Cps1DinoRomSet(
            string setName,
            Cps1VideoConfig videoConfig,
            Cps1AudioHardware audioHardware,
            double audioCpuClockHz,
            KabukiKeys kabukiKeys,
            byte[] program,
            byte[] graphics,
            byte[] audioCpu,
            byte[] oki,
            byte[] qsound,
            byte[] qsoundDsp)
        {
            SetName = setName;
            VideoConfig = videoConfig;
            AudioHardware = audioHardware;
            AudioCpuClockHz = audioCpuClockHz;
            KabukiSwapKey1 = kabukiKeys.SwapKey1;
            KabukiSwapKey2 = kabukiKeys.SwapKey2;
            KabukiAddressKey = kabukiKeys.AddressKey;
            KabukiXorKey = kabukiKeys.XorKey;
            Program = program;
            Graphics = graphics;
            AudioCpu = audioCpu;
            Oki = oki;
            QSound = qsound;
            QSoundDsp = qsoundDsp;
        }

        public string SetName { get; }
        public Cps1VideoConfig VideoConfig { get; }
        public Cps1AudioHardware AudioHardware { get; }
        public double AudioCpuClockHz { get; }
        public int KabukiSwapKey1 { get; }
        public int KabukiSwapKey2 { get; }
        public int KabukiAddressKey { get; }
        public int KabukiXorKey { get; }
        public byte[] Program { get; }
        public byte[] Graphics { get; }
        public byte[] AudioCpu { get; }
        public byte[] Oki { get; }
        public byte[] QSound { get; }
        public byte[] QSoundDsp { get; }

        private const int ProgramRomSize = 0x20_0000;
        private const int AudioCpuRomSize = 0x2_8000;
        private const int QSoundRomBankSize = 0x08_0000;

        private static readonly KabukiKeys WofKabuki = new(0x01234567, 0x54163072, 0x5151, 0x51);
        private static readonly KabukiKeys DinoKabuki = new(0x76543210, 0x24601357, 0x4343, 0x43);
        private static readonly KabukiKeys PunisherKabuki = new(0x67452103, 0x75316024, 0x2222, 0x22);
        private static readonly KabukiKeys SlamMastersKabuki = new(0x54321076, 0x65432107, 0x3131, 0x19);

        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["punishru"] = "punisheru",
            ["punishrj"] = "punisherj",
            ["slammasu"] = "slammastu"
        };

        private static readonly Dictionary<string, Cps1QSoundDefinition> Definitions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["wof"] = WofDefinition("wof", null, Cps1VideoConfig.QSound1),
            ["wofr1"] = WofDefinition("wofr1", "wof", Cps1VideoConfig.Default),
            ["wofu"] = WofDefinition("wofu", "wof", Cps1VideoConfig.QSound1),
            ["wofa"] = WofDefinition("wofa", "wof", Cps1VideoConfig.Default),
            ["wofj"] = WofDefinition("wofj", "wof", Cps1VideoConfig.QSound1),

            ["dino"] = DinoDefinition("dino", null),
            ["dinou"] = DinoDefinition("dinou", "dino"),
            ["dinoa"] = DinoDefinition("dinoa", "dino"),
            ["dinoj"] = DinoDefinition("dinoj", "dino"),

            ["punisher"] = PunisherDefinition("punisher", null),
            ["punisheru"] = PunisherDefinition("punisheru", "punisher"),
            ["punisherh"] = PunisherDefinition("punisherh", "punisher"),
            ["punisherj"] = PunisherDefinition("punisherj", "punisher"),

            ["slammast"] = SlamMastersDefinition("slammast", null, Cps1VideoConfig.QSound4),
            ["slammastu"] = SlamMastersDefinition("slammastu", "slammast", Cps1VideoConfig.QSound4),
            ["mbomberj"] = SlamMastersDefinition("mbomberj", "slammast", Cps1VideoConfig.QSound4),
            ["mbombrd"] = SlamMastersDefinition("mbombrd", null, Cps1VideoConfig.QSound5),
            ["mbombrdj"] = SlamMastersDefinition("mbombrdj", "mbombrd", Cps1VideoConfig.QSound5)
        };

        private static readonly Dictionary<string, Cps1ClassicDefinition> ClassicDefinitions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ffight"] = FinalFightDefinition("ffight", null),
            ["ffightu"] = FinalFightDefinition("ffightu", "ffight"),
            ["ffightj"] = FinalFightJapanDefinition("ffightj", null),
            ["ffightj1"] = FinalFightJapanDefinition("ffightj1", null),
            ["ffightj2"] = FinalFightJapanDefinition("ffightj2", null)
        };

        public static bool IsSupportedSet(string setName)
        {
            string canonical = CanonicalSetName(setName);
            return Definitions.ContainsKey(canonical) || ClassicDefinitions.ContainsKey(canonical);
        }

        public static Cps1DinoRomSet Load(string path)
        {
            string requestedSetName = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
            string setName = CanonicalSetName(requestedSetName);
            if (Definitions.TryGetValue(setName, out Cps1QSoundDefinition? definition))
                return LoadQSoundSet(path, setName, definition);
            if (ClassicDefinitions.TryGetValue(setName, out Cps1ClassicDefinition? classicDefinition))
                return LoadClassicSet(path, setName, classicDefinition);

            throw new NotSupportedException($"CPS1 ROM set '{requestedSetName}' is not registered in the EutherDrive CPS1 loader.");
        }

        private static Cps1DinoRomSet LoadQSoundSet(string path, string setName, Cps1QSoundDefinition definition)
        {
            Dictionary<string, byte[]> entries = ReadArchive(path);
            MergeParentArchivesIfPresent(path, definition.ParentSetName, entries);

            byte[] program = new byte[ProgramRomSize];
            Array.Fill(program, (byte)0xff);
            foreach (RomLoad load in definition.ProgramLoads)
                LoadProgram(entries, program, load);

            byte[] gfx = new byte[definition.GraphicsSize];
            foreach (RomLoad load in definition.GraphicsLoads)
                Load64Word(entries, gfx, load.Offset, load.Names);

            byte[] audioCpu = LoadAudioCpu(entries, definition.AudioCpuNames);
            byte[] qsound = LoadQSound(entries, definition.QSoundSize, definition.QSoundNames);

            byte[] qsoundDsp = LoadQSoundDsp(path, entries);

            return new Cps1DinoRomSet(
                setName,
                definition.VideoConfig,
                Cps1AudioHardware.QSound,
                8_000_000.0,
                definition.KabukiKeys,
                program,
                gfx,
                audioCpu,
                Array.Empty<byte>(),
                qsound,
                qsoundDsp);
        }

        private static Cps1DinoRomSet LoadClassicSet(string path, string setName, Cps1ClassicDefinition definition)
        {
            Dictionary<string, byte[]> entries = ReadArchive(path);
            MergeParentArchivesIfPresent(path, definition.ParentSetName, entries);

            byte[] program = new byte[ProgramRomSize];
            Array.Fill(program, (byte)0xff);
            foreach (RomLoad load in definition.ProgramLoads)
                LoadProgram(entries, program, load);

            byte[] gfx = new byte[definition.GraphicsSize];
            foreach (RomLoad load in definition.GraphicsLoads)
                LoadGraphics(entries, gfx, load);

            byte[] audioCpu = LoadAudioCpu(entries, definition.AudioCpuNames);
            byte[] oki = LoadOki(entries, definition.OkiSize, definition.OkiLoads);

            return new Cps1DinoRomSet(
                setName,
                definition.VideoConfig,
                Cps1AudioHardware.YmOki,
                3_579_545.0,
                new KabukiKeys(0, 0, 0, 0),
                program,
                gfx,
                audioCpu,
                oki,
                Array.Empty<byte>(),
                Array.Empty<byte>());
        }

        private static Cps1QSoundDefinition WofDefinition(string setName, string? parentSetName, Cps1VideoConfig videoConfig)
            => new(
                setName,
                parentSetName,
                videoConfig,
                WofKabuki,
                0x40_0000,
                0x20_0000,
                new[]
                {
                    Word(0x000000, $"{setName}_23c.8f", $"{setName}_23b.8f", $"{setName}.23c", $"{setName}_23b.rom", "tk2e_23c.8f", "tk2e_23b.8f", "tk2e_23b.rom", "tk2u.23c", "tk2a_23b.rom", "tk2j22c.bin"),
                    Word(0x080000, $"{setName}_22c.7f", $"{setName}_22b.7f", $"{setName}.22c", $"{setName}_22b.rom", "tk2e_22c.7f", "tk2e_22b.7f", "tk2e_22b.rom", "tk2u.22c", "tk2a_22b.rom", "tk2j23c.bin")
                },
                new[]
                {
                    Gfx(0x000000, "tk2-1m.3a", "tk2_gfx1.rom", "tk2_01.3a"),
                    Gfx(0x000002, "tk2-3m.5a", "tk2_gfx3.rom", "tk2_02.4a"),
                    Gfx(0x000004, "tk2-2m.4a", "tk2_gfx2.rom", "tk2_03.5a"),
                    Gfx(0x000006, "tk2-4m.6a", "tk2_gfx4.rom", "tk2_04.6a"),
                    Gfx(0x200000, "tk2-5m.7a", "tk2_gfx5.rom", "tk2_05.7a"),
                    Gfx(0x200002, "tk2-7m.9a", "tk2_gfx7.rom", "tk2_06.8a"),
                    Gfx(0x200004, "tk2-6m.8a", "tk2_gfx6.rom", "tk2_07.9a"),
                    Gfx(0x200006, "tk2-8m.10a", "tk2_gfx8.rom", "tk2_08.10a")
                },
                new[] { "tk2_qa.5k", "tk2_qa.rom" },
                new[]
                {
                    new[] { "tk2-q1.1k", "tk2_q1.rom" },
                    new[] { "tk2-q2.2k", "tk2_q2.rom" },
                    new[] { "tk2-q3.3k", "tk2_q3.rom" },
                    new[] { "tk2-q4.4k", "tk2_q4.rom" }
                });

        private static Cps1QSoundDefinition DinoDefinition(string setName, string? parentSetName)
            => new(
                setName,
                parentSetName,
                Cps1VideoConfig.QSound2,
                DinoKabuki,
                0x40_0000,
                0x20_0000,
                new[]
                {
                    Word(0x000000, $"{setName}_23a.8f", $"{setName}.23a", $"{setName}-23a.8f", "cde_23a.8f", "cde_23a.rom", "cdu.23a", "cdj-23a.8f"),
                    Word(0x080000, $"{setName}_22a.7f", $"{setName}.22a", $"{setName}-22a.7f", "cde_22a.7f", "cde_22a.rom", "cdu.22a", "cdj-22a.7f"),
                    Word(0x100000, $"{setName}_21a.6f", "cde_21a.6f", "cde_21a.rom")
                },
                new[]
                {
                    Gfx(0x000000, "cd-1m.3a", "cd_gfx01.rom"),
                    Gfx(0x000002, "cd-3m.5a", "cd_gfx03.rom"),
                    Gfx(0x000004, "cd-2m.4a", "cd_gfx02.rom"),
                    Gfx(0x000006, "cd-4m.6a", "cd_gfx04.rom"),
                    Gfx(0x200000, "cd-5m.7a", "cd_gfx05.rom"),
                    Gfx(0x200002, "cd-7m.9a", "cd_gfx07.rom"),
                    Gfx(0x200004, "cd-6m.8a", "cd_gfx06.rom"),
                    Gfx(0x200006, "cd-8m.10a", "cd_gfx08.rom")
                },
                new[] { "cd_q.5k", "cd_q.rom" },
                new[]
                {
                    new[] { "cd-q1.1k", "cd_q1.rom" },
                    new[] { "cd-q2.2k", "cd_q2.rom" },
                    new[] { "cd-q3.3k", "cd_q3.rom" },
                    new[] { "cd-q4.4k", "cd_q4.rom" }
                });

        private static Cps1QSoundDefinition PunisherDefinition(string setName, string? parentSetName)
            => new(
                setName,
                parentSetName,
                Cps1VideoConfig.QSound3,
                PunisherKabuki,
                0x40_0000,
                0x20_0000,
                new[]
                {
                    Byte(0x000000, $"{setName[..Math.Min(setName.Length, 3)]}_26.11e", "pse_26.11e", "psu_26.11e", "psu26.rom"),
                    Byte(0x000001, $"{setName[..Math.Min(setName.Length, 3)]}_30.11f", "pse_30.11f", "psu_30.11f", "psu30.rom"),
                    Byte(0x040000, "pse_27.12e", "psu_27.12e", "psu27.rom"),
                    Byte(0x040001, "pse_31.12f", "psu_31.12f", "psu31.rom"),
                    Byte(0x080000, "pse_24.9e", "psu_24.9e", "psu24.rom"),
                    Byte(0x080001, "pse_28.9f", "psu_28.9f", "psu28.rom"),
                    Byte(0x0c0000, "pse_25.10e", "psu_25.10e", "psu25.rom"),
                    Byte(0x0c0001, "pse_29.10f", "psu_29.10f", "psu29.rom"),
                    Word(0x000000, "psj_23.8f", "psj23.bin"),
                    Word(0x080000, "psj_22.7f", "psj22.bin"),
                    Word(0x100000, "ps_21.6f", "ps_21.rom", "psj_21.6f")
                },
                new[]
                {
                    Gfx(0x000000, "ps-1m.3a", "ps_gfx1.rom", "ps_01.3a"),
                    Gfx(0x000002, "ps-3m.5a", "ps_gfx3.rom", "ps_02.4a"),
                    Gfx(0x000004, "ps-2m.4a", "ps_gfx2.rom", "ps_03.5a"),
                    Gfx(0x000006, "ps-4m.6a", "ps_gfx4.rom", "ps_04.6a"),
                    Gfx(0x200000, "ps-5m.7a", "ps_gfx5.rom", "ps_05.7a"),
                    Gfx(0x200002, "ps-7m.9a", "ps_gfx7.rom", "ps_06.8a"),
                    Gfx(0x200004, "ps-6m.8a", "ps_gfx6.rom", "ps_07.9a"),
                    Gfx(0x200006, "ps-8m.10a", "ps_gfx8.rom", "ps_08.10a")
                },
                new[] { "ps_q.5k", "ps_q.rom" },
                new[]
                {
                    new[] { "ps-q1.1k", "ps_q1.rom" },
                    new[] { "ps-q2.2k", "ps_q2.rom" },
                    new[] { "ps-q3.3k", "ps_q3.rom" },
                    new[] { "ps-q4.4k", "ps_q4.rom" }
                });

        private static Cps1QSoundDefinition SlamMastersDefinition(string setName, string? parentSetName, Cps1VideoConfig videoConfig)
            => new(
                setName,
                parentSetName,
                videoConfig,
                SlamMastersKabuki,
                0x60_0000,
                0x40_0000,
                new[]
                {
                    Word(0x000000, "mbe_23e.8f", "mbe_23e.rom", "mbu_23e.8f", "mbu-23e.rom", "mbj_23e.8f", "mbj23e"),
                    Byte(0x000000, "mbde_26.11e", "mbd_26.bin", "mbdj_26.11e"),
                    Byte(0x000001, "mbde_30.11f", "mbde_30.rom", "mbdj_30.11f", "mbdj_30.bin"),
                    Byte(0x040000, "mbde_27.12e", "mbd_27.bin", "mbdj_27.12e"),
                    Byte(0x040001, "mbde_31.12f", "mbd_31.bin", "mbdj_31.12f"),
                    Byte(0x080000, "mbe_24b.9e", "mbe_24b.rom", "mbu_24b.9e", "mbde_24.9e", "mbd_24.bin", "mbdj_24.9e"),
                    Byte(0x080001, "mbe_28b.9f", "mbe_28b.rom", "mbu_28b.9f", "mbde_28.9f", "mbd_28.bin", "mbdj_28.9f"),
                    Byte(0x0c0000, "mbe_25b.10e", "mbe_25b.rom", "mbu_25b.10e", "mbde_25.10e", "mbd_25.bin", "mbdj_25.10e"),
                    Byte(0x0c0001, "mbe_29b.10f", "mbe_29b.rom", "mbu_29b.10f", "mbde_29.10f", "mbd_29.bin", "mbdj_29.10f"),
                    Word(0x080000, "mbj_22b.7f"),
                    Word(0x100000, "mbe_21a.6f", "mbu_21a.6f", "mbj_21a.6f", "mbde_21.6f", "mbd_21.bin", "mbdj_21.6f"),
                    Word(0x180000, "mbe_20a.5f", "mbu_20a.5f", "mbu-20a.rom", "mbj_20a.5f", "mbde_20.5f", "mbd_20.bin", "mbdj_20.5f")
                },
                new[]
                {
                    Gfx(0x000000, "mb-1m.3a", "mb_gfx01.rom", "mb_01.3a", "mbj_01.bin"),
                    Gfx(0x000002, "mb-3m.5a", "mb_gfx03.rom", "mb_03.5a", "mbj_03.bin"),
                    Gfx(0x000004, "mb-2m.4a", "mb_gfx02.rom", "mb_02.4a", "mbj_02.bin"),
                    Gfx(0x000006, "mb-4m.6a", "mb_gfx04.rom", "mb_04.6a", "mbj_04.bin"),
                    Gfx(0x200000, "mb-5m.7a", "mb_05.bin", "mb_05.7a"),
                    Gfx(0x200002, "mb-7m.9a", "mb_07.bin", "mb_06.8a"),
                    Gfx(0x200004, "mb-6m.8a", "mb_06.bin", "mb_07.9a"),
                    Gfx(0x200006, "mb-8m.10a", "mb_08.bin", "mb_08.10a"),
                    Gfx(0x400000, "mb-10m.3c", "mb_10.bin", "mb_10.3c"),
                    Gfx(0x400002, "mb-12m.5c", "mb_12.bin", "mb_11.4c"),
                    Gfx(0x400004, "mb-11m.4c", "mb_11.bin", "mb_12.5c"),
                    Gfx(0x400006, "mb-13m.6c", "mb_13.bin", "mb_13.6c")
                },
                new[] { "mb_qa.5k", "mb_qa.rom", "mb_q.5k", "mb_q.bin" },
                new[]
                {
                    new[] { "mb-q1.1k", "mb_q1.bin" },
                    new[] { "mb-q2.2k", "mb_q2.bin" },
                    new[] { "mb-q3.3k", "mb_q3.bin" },
                    new[] { "mb-q4.4k", "mb_q4.bin" },
                    new[] { "mb-q5.1m", "mb_q5.bin" },
                    new[] { "mb-q6.2m", "mb_q6.bin" },
                    new[] { "mb-q7.3m", "mb_q7.bin" },
                    new[] { "mb-q8.4m", "mb_q8.bin" }
                });

        private static Cps1ClassicDefinition FinalFightDefinition(string setName, string? parentSetName)
            => new(
                setName,
                parentSetName,
                Cps1VideoConfig.Default,
                0x20_0000,
                0x40_000,
                new[]
                {
                    Byte(0x00000, "ff_36.11f"),
                    Byte(0x00001, "ff_42.11h"),
                    Byte(0x40000, "ff_37.12f"),
                    Byte(0x40001, "ffe_43.12h", "ffu_43.12h", "ff43.rom"),
                    Word(0x80000, "ff-32m.8h")
                },
                new[]
                {
                    Gfx(0x000000, "ff-5m.7a"),
                    Gfx(0x000002, "ff-7m.9a"),
                    Gfx(0x000004, "ff-1m.3a"),
                    Gfx(0x000006, "ff-3m.5a")
                },
                new[] { "ff_09.12b", "ffe_23.12b", "ff_23.12b" },
                new[]
                {
                    new RomLoad(RomLoadKind.Raw, 0x00000, new[] { "ff_18.11c" }),
                    new RomLoad(RomLoadKind.Raw, 0x20000, new[] { "ff_19.12c" })
                });

        private static Cps1ClassicDefinition FinalFightJapanDefinition(string setName, string? parentSetName)
            => new(
                setName,
                parentSetName,
                Cps1VideoConfig.Default,
                0x20_0000,
                0x40_000,
                new[]
                {
                    Byte(0x00000, "ff36.bin", "ffj_36.12f", "ffj_36a.12f", "ff30-36.rom"),
                    Byte(0x00001, "ff42.bin", "ffj_42.12h", "ffj_42a.12h", "ff35-42.rom"),
                    Byte(0x40000, "ff37.bin", "ffj_37.13f", "ffj_37a.13f", "ff31-37.rom"),
                    Byte(0x40001, "ff43.bin", "ffj_43.13h", "ffj_43a.13h", "ff36-43.rom"),
                    Byte(0x80000, "ffj_34.10f", "ff_34.10f"),
                    Byte(0x80001, "ffj_40.10h", "ff_40.10h"),
                    Byte(0xc0000, "ffj_35.11f", "ff_35.11f"),
                    Byte(0xc0001, "ffj_41.11h", "ff_41.11h")
                },
                new[]
                {
                    GfxByte(0x000000, "ffj_09.4b"),
                    GfxByte(0x000001, "ffj_01.4a"),
                    GfxByte(0x000002, "ffj_13.9b"),
                    GfxByte(0x000003, "ffj_05.9a"),
                    GfxByte(0x000004, "ffj_24.5e"),
                    GfxByte(0x000005, "ffj_17.5c"),
                    GfxByte(0x000006, "ffj_38.8h"),
                    GfxByte(0x000007, "ffj_32.8f"),
                    GfxByte(0x100000, "ffj_10.5b"),
                    GfxByte(0x100001, "ffj_02.5a"),
                    GfxByte(0x100002, "ffj_14.10b"),
                    GfxByte(0x100003, "ffj_06.10a"),
                    GfxByte(0x100004, "ffj_25.7e"),
                    GfxByte(0x100005, "ffj_18.7c"),
                    GfxByte(0x100006, "ffj_39.9h"),
                    GfxByte(0x100007, "ffj_33.9f")
                },
                new[] { "ff_23.bin", "ff_23.13c", "ff_23.13b" },
                new[]
                {
                    new RomLoad(RomLoadKind.Raw, 0x00000, new[] { "ffj_30.bin", "ffj_30.12e", "ffj_30.12c", "ff_30.12e" }),
                    new RomLoad(RomLoadKind.Raw, 0x20000, new[] { "ffj_31.bin", "ffj_31.13e", "ffj_31.13c", "ff_31.13e" })
                });

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

        private static string CanonicalSetName(string setName)
            => Aliases.TryGetValue(setName.Trim().ToLowerInvariant(), out string? canonical) ? canonical : setName.Trim().ToLowerInvariant();

        private static RomLoad Word(int offset, params string[] names)
            => new(RomLoadKind.WordSwap, offset, names);

        private static RomLoad Byte(int offset, params string[] names)
            => new(RomLoadKind.Byte, offset, names);

        private static RomLoad Gfx(int offset, params string[] names)
            => new(RomLoadKind.Graphics64Word, offset, names);

        private static RomLoad GfxByte(int offset, params string[] names)
            => new(RomLoadKind.Graphics64Byte, offset, names);

        private static void MergeParentArchivesIfPresent(string path, string? parentSetName, Dictionary<string, byte[]> entries)
        {
            if (string.IsNullOrWhiteSpace(parentSetName))
                return;

            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                return;

            string parentPath = Path.Combine(directory, parentSetName + ".zip");
            if (!File.Exists(parentPath))
                return;

            Dictionary<string, byte[]> parentEntries = ReadArchive(parentPath);
            foreach ((string name, byte[] data) in parentEntries)
            {
                if (!entries.ContainsKey(name))
                    entries[name] = data;
            }
        }

        private static void LoadProgram(Dictionary<string, byte[]> entries, byte[] destination, RomLoad load)
        {
            if (!TryFind(entries, out byte[] source, load.Names))
                return;

            switch (load.Kind)
            {
                case RomLoadKind.WordSwap:
                    Copy16WordSwap(source, destination, load.Offset, load.Names[0]);
                    break;
                case RomLoadKind.Byte:
                    Copy16Byte(source, destination, load.Offset, load.Names[0]);
                    break;
            }
        }

        private static void LoadGraphics(Dictionary<string, byte[]> entries, byte[] destination, RomLoad load)
        {
            switch (load.Kind)
            {
                case RomLoadKind.Graphics64Word:
                    Load64Word(entries, destination, load.Offset, load.Names);
                    break;
                case RomLoadKind.Graphics64Byte:
                    Load64Byte(entries, destination, load.Offset, load.Names);
                    break;
            }
        }

        private static void Load64Byte(Dictionary<string, byte[]> entries, byte[] destination, int offset, params string[] names)
        {
            byte[] source = Find(entries, names);
            for (int i = 0; i < source.Length; i++)
            {
                int dst = offset + i * 8;
                if ((uint)dst >= destination.Length)
                    throw new InvalidDataException($"ROM '{names[0]}' is too large for the CPS1 graphics region.");
                destination[dst] = source[i];
            }
        }

        private static void Copy16WordSwap(byte[] source, byte[] destination, int offset, string name)
        {
            if (offset + source.Length > destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for the CPS1 program region.");

            for (int i = 0; i + 1 < source.Length; i += 2)
            {
                destination[offset + i] = source[i + 1];
                destination[offset + i + 1] = source[i];
            }
        }

        private static void Copy16Byte(byte[] source, byte[] destination, int offset, string name)
        {
            int last = offset + (source.Length - 1) * 2;
            if ((uint)last >= destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for the CPS1 program region.");

            for (int i = 0; i < source.Length; i++)
                destination[offset + i * 2] = source[i];
        }

        private static byte[] LoadAudioCpu(Dictionary<string, byte[]> entries, string[] names)
        {
            byte[] audio = Find(entries, names);
            byte[] audioCpu = new byte[AudioCpuRomSize];
            audio.AsSpan(0, Math.Min(0x8000, audio.Length)).CopyTo(audioCpu);
            if (audio.Length > 0x8000)
                audio.AsSpan(0x8000, Math.Min(0x18000, audio.Length - 0x8000)).CopyTo(audioCpu.AsSpan(0x10000));
            return audioCpu;
        }

        private static byte[] LoadQSound(Dictionary<string, byte[]> entries, int size, string[][] banks)
        {
            byte[] qsound = new byte[size];
            for (int bank = 0; bank < banks.Length; bank++)
            {
                byte[] source = Find(entries, banks[bank]);
                int offset = bank * QSoundRomBankSize;
                if (offset + source.Length > qsound.Length)
                    throw new InvalidDataException($"ROM '{banks[bank][0]}' is too large for the CPS1 QSound sample region.");

                source.CopyTo(qsound.AsSpan(offset));
            }

            return qsound;
        }

        private static byte[] LoadOki(Dictionary<string, byte[]> entries, int size, RomLoad[] loads)
        {
            byte[] oki = new byte[size];
            foreach (RomLoad load in loads)
            {
                byte[] source = Find(entries, load.Names);
                if (load.Offset + source.Length > oki.Length)
                    throw new InvalidDataException($"ROM '{load.Names[0]}' is too large for the CPS1 OKI sample region.");

                source.CopyTo(oki.AsSpan(load.Offset));
            }

            return oki;
        }

        private readonly record struct KabukiKeys(int SwapKey1, int SwapKey2, int AddressKey, int XorKey);

        private enum RomLoadKind
        {
            WordSwap,
            Byte,
            Graphics64Word,
            Graphics64Byte,
            Raw
        }

        private readonly record struct RomLoad(RomLoadKind Kind, int Offset, string[] Names);

        private sealed record Cps1QSoundDefinition(
            string SetName,
            string? ParentSetName,
            Cps1VideoConfig VideoConfig,
            KabukiKeys KabukiKeys,
            int GraphicsSize,
            int QSoundSize,
            RomLoad[] ProgramLoads,
            RomLoad[] GraphicsLoads,
            string[] AudioCpuNames,
            string[][] QSoundNames);

        private sealed record Cps1ClassicDefinition(
            string SetName,
            string? ParentSetName,
            Cps1VideoConfig VideoConfig,
            int GraphicsSize,
            int OkiSize,
            RomLoad[] ProgramLoads,
            RomLoad[] GraphicsLoads,
            string[] AudioCpuNames,
            RomLoad[] OkiLoads);

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
