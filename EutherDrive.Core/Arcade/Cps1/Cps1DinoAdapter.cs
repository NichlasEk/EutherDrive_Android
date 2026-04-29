using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
        _video = new Cps1Video(_bus, roms.Graphics, roms.GfxMapper);
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
        private readonly byte[] _audioCpuRaw = new byte[0x8000];
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
        private bool _hasQSoundProtectionRom;
        private byte _bootlegKludge;
        private bool _useSf2HackInputRead;

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
        public bool ReverseSpriteOrder => (_bootlegKludge & 0x40) != 0;
        public bool UseSf2BootlegVideoKludge => (_bootlegKludge & 0x0f) == 1;
        public bool InterruptAsserted => _interruptLevel != 0;
        public BusSignals Signals => new(false);
        public ushort CurrentOpcode => 0;

        public void Load(Cps1DinoRomSet roms)
        {
            Array.Fill(_mainRom, (byte)0xff);
            Array.Clear(_mainRam);
            Array.Clear(_audioCpu);
            Array.Clear(_audioOpcodes);
            Array.Clear(_audioCpuRaw);
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
            roms.AudioCpu.AsSpan(0, Math.Min(_audioCpuRaw.Length, roms.AudioCpu.Length)).CopyTo(_audioCpuRaw);
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
            _hasQSoundProtectionRom = roms.HasQSoundProtectionRom;
            _bootlegKludge = roms.BootlegKludge;
            _useSf2HackInputRead = roms.UseSf2HackInputRead;
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
            int objBase = UseSf2BootlegVideoKludge ? ForcedCps1Base(0x9100, ObjBytes) : Cps1Base(Cps1Regs.ObjBase, ObjBytes);
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
            => ForcedCps1Base(_cpsA[registerIndex], boundaryBytes);

        private static int ForcedCps1Base(int registerValue, int boundaryBytes)
        {
            int baseAddress = registerValue * 256;
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
                return ReadQSoundRom((int)((address - 0xf00000) >> 1));
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
                if ((address & 1) != 0)
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
            _soundLatch0 = 0;
            _soundLatch1 = 0;
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
            => _useSf2HackInputRead ? ReadSf2HackDsw(offset) : ReadStandardDsw(offset);

        private ushort ReadStandardDsw(int offset)
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

        private ushort ReadSf2HackDsw(int offset)
        {
            int input = offset switch
            {
                0 => Input0(),
                1 => 0xff,
                2 => 0xff,
                3 => 0xff,
                _ => 0xff
            };
            return (ushort)(0xff00 | input);
        }

        private ushort ReadCpsB(int offset)
        {
            if ((uint)offset >= _cpsB.Length)
                return 0xffff;

            int address = offset * 2;
            if (_videoConfig.CpsBAddress == address)
                return _videoConfig.CpsBValue;

            return _cpsB[offset];
        }

        private ushort ReadQSoundRom(int offset)
        {
            if (!_hasQSoundProtectionRom)
                return 0;
            if ((uint)offset < _audioCpuRaw.Length)
                return (ushort)(0xff00 | _audioCpuRaw[offset]);
            return 0;
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
        private bool _waitingForStartBit = true;
        private bool _readingData;
        private bool _receivingWriteData;
        private bool _receivingWriteAllData;
        private bool _locked = true;
        private bool _ignoreUntilChipDeselect;
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
            if (_ignoreUntilChipDeselect)
                return;

            if (_readingData)
            {
                if (_outputBits > 0)
                    ShiftOutputBit();
                else
                    _dataOut = true;
                return;
            }

            if (_receivingWriteData)
            {
                _writeData = ((_writeData << 1) | (bit ? 1 : 0)) & 0xff;
                _writeBits++;
                if (_writeBits == 8)
                {
                    if (!_locked)
                        _data[_writeAddress & 0x7f] = (byte)_writeData;
                    FinishCommand();
                }
                return;
            }

            if (_receivingWriteAllData)
            {
                _writeData = ((_writeData << 1) | (bit ? 1 : 0)) & 0xff;
                _writeBits++;
                if (_writeBits == 8)
                {
                    if (!_locked)
                        Array.Fill(_data, (byte)_writeData);
                    FinishCommand();
                    _dataOut = true;
                }
                return;
            }

            if (_waitingForStartBit)
            {
                if (!bit)
                    return;

                _waitingForStartBit = false;
                _shift = 0;
                _bits = 0;
                return;
            }

            _shift = ((_shift << 1) | (bit ? 1 : 0)) & 0x1ff;
            _bits++;
            if (_bits < 9)
                return;

            int op = (_shift >> 7) & 0x03;
            int address = _shift & 0x7f;
            ResetTransfer();

            switch (op)
            {
                case 0x00:
                    ExecuteControlCommand(address);
                    break;
                case 0x01:
                    _receivingWriteData = true;
                    _writeAddress = address;
                    _writeData = 0;
                    _writeBits = 0;
                    break;
                case 0x02:
                    StartOutput(_data[address]);
                    break;
                case 0x03:
                    if (!_locked)
                        _data[address] = 0xff;
                    FinishCommand();
                    break;
            }
        }

        private void ExecuteControlCommand(int address)
        {
            switch (address >> 5)
            {
                case 0:
                    _locked = true;
                    FinishCommand();
                    break;
                case 1:
                    _receivingWriteAllData = true;
                    _writeData = 0;
                    _writeBits = 0;
                    break;
                case 2:
                    if (!_locked)
                        Array.Fill(_data, (byte)0xff);
                    FinishCommand();
                    break;
                case 0x03:
                    _locked = false;
                    FinishCommand();
                    break;
            }
        }

        private void StartOutput(byte value)
        {
            _output = value;
            _outputBits = 8;
            _readingData = true;
            _dataOut = false;
        }

        private void FinishCommand()
        {
            ResetTransfer();
            _ignoreUntilChipDeselect = true;
            _dataOut = true;
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
            _waitingForStartBit = true;
            _readingData = false;
            _receivingWriteData = false;
            _receivingWriteAllData = false;
            _ignoreUntilChipDeselect = false;
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

        public Cps1Video(Cps1Bus bus, byte[] gfxRom, Cps1GfxMapper gfxMapper)
        {
            _bus = bus;
            _graphics = new Cps1Graphics(gfxRom, gfxMapper);
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

            Span<uint> frameWords = MemoryMarshal.Cast<byte, uint>(frameBuffer);
            int dst = 0;
            for (int y = 0; y < FrameHeight; y++)
            {
                int src = (y + CropY) * InternalWidth + CropX;
                for (int x = 0; x < FrameWidth; x++)
                {
                    frameWords[dst++] = _palette[_pixels[src + x]];
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
            if (_bus.UseSf2BootlegVideoKludge)
                scrollX += layer switch { 0 => -0x0c, 1 => -0x0e, _ => -0x10 };
            int scrollY = CpsA(layer switch
            {
                0 => Cps1Regs.Scroll1ScrollY,
                1 => Cps1Regs.Scroll2ScrollY,
                _ => Cps1Regs.Scroll3ScrollY
            });

            int tileShift = layer switch { 0 => 3, 1 => 4, _ => 5 };
            int tileMask = tileSize - 1;
            int mapMask = mapSize - 1;
            int layerColorBase = layer switch { 0 => 0x20, 1 => 0x40, _ => 0x60 };
            ReadOnlySpan<ushort> gfxRam = _bus.GfxRam;
            int gfxRamWords = gfxRam.Length;

            for (int y = 0; y < InternalHeight; y++)
            {
                int effectiveScrollX = scrollX;
                if (rowScroll)
                {
                    int row = (y - scrollY) & 0x3ff;
                    effectiveScrollX += _bus.ReadGfxWord(otherBase + ((row + rowScrollOffset) & 0x3ff));
                }

                int sourceY = (y + scrollY) & mapMask;
                int tileRow = sourceY >> tileShift;
                int localY = sourceY & tileMask;

                int dst = y * InternalWidth;
                int x = 0;
                while (x < InternalWidth)
                {
                    int sourceX = (x + effectiveScrollX) & mapMask;
                    int tileCol = sourceX >> tileShift;
                    int localX = sourceX & tileMask;
                    int run = Math.Min(tileSize - localX, InternalWidth - x);
                    int tileIndex = TileIndex(layer, tileCol, tileRow);
                    int tileWord = baseIndex + tileIndex * 2;
                    ushort codeWord = gfxRam[tileWord % gfxRamWords];
                    ushort attr = gfxRam[(tileWord + 1) % gfxRamWords];
                    int code = layer == 2 ? codeWord & 0x3fff : codeWord;
                    int priorityGroup = (attr >> 7) & 0x03;
                    ushort priorityMask = PriorityMask(priorityGroup);
                    int flip = (attr >> 5) & 0x03;
                    bool flipX = (flip & 0x01) != 0;
                    int py = (flip & 0x02) != 0 ? tileSize - 1 - localY : localY;
                    bool rightHalf = layer == 0 && (tileIndex & 0x20) != 0;
                    int color = ((attr & 0x1f) + layerColorBase) * 16;
                    int localStep = flipX ? -1 : 1;
                    int px = flipX ? tileSize - 1 - localX : localX;
                    int target = dst + x;

                    if (markSpritePriority)
                    {
                        if (priorityMask != 0)
                        {
                            for (int i = 0; i < run; i++, px += localStep)
                            {
                                int pen = layer switch
                                {
                                    0 => _graphics.GetScroll1Pen(code, rightHalf, px, py),
                                    1 => _graphics.GetScroll2Pen(code, px, py),
                                    _ => _graphics.GetScroll3Pen(code, px, py)
                                };
                                if (pen != TransparentPen && ((priorityMask >> pen) & 1) != 0)
                                    _spritePriority[target + i] = 1;
                            }
                        }
                    }
                    else
                    {
                        for (int i = 0; i < run; i++, px += localStep)
                        {
                            int pen = layer switch
                            {
                                0 => _graphics.GetScroll1Pen(code, rightHalf, px, py),
                                1 => _graphics.GetScroll2Pen(code, px, py),
                                _ => _graphics.GetScroll3Pen(code, px, py)
                            };
                            if (pen != TransparentPen)
                                _pixels[target + i] = (ushort)(color + pen);
                        }
                    }

                    x += run;
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

            int baseIndex = _bus.ReverseSpriteOrder ? last : 0;
            int baseStep = _bus.ReverseSpriteOrder ? -4 : 4;
            for (int i = last; i >= 0; i -= 4)
            {
                int x = obj[baseIndex + 0] & 0x01ff;
                int y = obj[baseIndex + 1] & 0x01ff;
                int code = obj[baseIndex + 2];
                ushort attr = obj[baseIndex + 3];
                baseIndex += baseStep;

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
                    int pen = _graphics.GetSpritePen(code, px, py);
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
        private readonly Cps1GfxMapper _mapper;

        public Cps1Graphics(byte[] gfx, Cps1GfxMapper mapper)
        {
            _mapper = mapper;
            _scroll1Left = Decode(gfx, 8, 8, 64, 0);
            _scroll1Right = Decode(gfx, 8, 8, 64, 32);
            _tiles16 = Decode(gfx, 16, 16, 128, 0);
            _tiles32 = Decode(gfx, 32, 32, 512, 0);
        }

        public int GetScroll1Pen(int code, bool rightHalf, int x, int y)
        {
            code = MapCode(Cps1GfxLayer.Scroll1, code);
            if (code < 0)
                return 15;
            byte[] data = rightHalf ? _scroll1Right : _scroll1Left;
            int offset = code * 64 + y * 8 + x;
            return (uint)offset < data.Length ? data[offset] : 15;
        }

        public int GetSpritePen(int code, int x, int y)
            => GetTile16Pen(MapCode(Cps1GfxLayer.Sprites, code), x, y);

        public int GetScroll2Pen(int code, int x, int y)
            => GetTile16Pen(MapCode(Cps1GfxLayer.Scroll2, code), x, y);

        public int GetScroll3Pen(int code, int x, int y)
        {
            code = MapCode(Cps1GfxLayer.Scroll3, code);
            if (code < 0)
                return 15;
            int offset = code * 1024 + y * 32 + x;
            return (uint)offset < _tiles32.Length ? _tiles32[offset] : 15;
        }

        private int GetTile16Pen(int code, int x, int y)
        {
            if (code < 0)
                return 15;
            int offset = code * 256 + y * 16 + x;
            return (uint)offset < _tiles16.Length ? _tiles16[offset] : 15;
        }

        private int MapCode(Cps1GfxLayer layer, int code)
        {
            if (_mapper != Cps1GfxMapper.S9263B)
                return code;

            int shift = layer switch
            {
                Cps1GfxLayer.Sprites => 1,
                Cps1GfxLayer.Scroll1 => 0,
                Cps1GfxLayer.Scroll2 => 1,
                _ => 3
            };
            int expandedCode = code << shift;

            if (TryMapS9263B(layer, expandedCode, shift, out int mappedCode))
                return mappedCode;

            return -1;
        }

        private static bool TryMapS9263B(Cps1GfxLayer layer, int expandedCode, int shift, out int mappedCode)
        {
            const int bankSize = 0x8000;
            mappedCode = -1;

            bool inRange;
            int bank;
            switch (layer)
            {
                case Cps1GfxLayer.Sprites:
                    if (expandedCode >= 0x00000 && expandedCode <= 0x07fff)
                    {
                        bank = 0;
                        inRange = true;
                    }
                    else if (expandedCode >= 0x08000 && expandedCode <= 0x0ffff)
                    {
                        bank = 1;
                        inRange = true;
                    }
                    else if (expandedCode >= 0x10000 && expandedCode <= 0x11fff)
                    {
                        bank = 2;
                        inRange = true;
                    }
                    else
                    {
                        bank = 0;
                        inRange = false;
                    }
                    break;
                case Cps1GfxLayer.Scroll3:
                    bank = 2;
                    inRange = expandedCode >= 0x02000 && expandedCode <= 0x03fff;
                    break;
                case Cps1GfxLayer.Scroll1:
                    bank = 2;
                    inRange = expandedCode >= 0x04000 && expandedCode <= 0x04fff;
                    break;
                default:
                    bank = 2;
                    inRange = expandedCode >= 0x05000 && expandedCode <= 0x07fff;
                    break;
            }

            if (!inRange)
                return false;

            mappedCode = (bank * bankSize + (expandedCode & (bankSize - 1))) >> shift;
            return true;
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

    private enum Cps1GfxMapper
    {
        Linear,
        S9263B
    }

    private enum Cps1GfxLayer
    {
        Sprites,
        Scroll1,
        Scroll2,
        Scroll3
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

    private readonly record struct Cps1VideoConfig(
        int LayerControl,
        int Priority0,
        int Priority1,
        int Priority2,
        int Priority3,
        int PaletteControl,
        int CpsBAddress = -1,
        ushort CpsBValue = 0xffff)
    {
        public static readonly Cps1VideoConfig Default = new(0x26, 0x28, 0x2a, 0x2c, 0x2e, 0x30);
        public static readonly Cps1VideoConfig QSound1 = new(0x22, 0x24, 0x26, 0x28, 0x2a, 0x2c);
        public static readonly Cps1VideoConfig QSound2 = new(0x0a, 0x0c, 0x0e, 0x00, 0x02, 0x04);
        public static readonly Cps1VideoConfig QSound3 = new(0x12, 0x14, 0x16, 0x08, 0x0a, 0x0c, 0x0e, 0x0c00);
        public static readonly Cps1VideoConfig QSound4 = new(0x16, 0x00, 0x02, 0x28, 0x2a, 0x2c, 0x2e, 0x0c01);
        public static readonly Cps1VideoConfig QSound5 = new(0x2a, 0x2c, 0x2e, 0x30, 0x32, 0x1c, 0x1e, 0x0c02);
        public static readonly Cps1VideoConfig CpsB05 = new(0x28, 0x2a, 0x2c, 0x2e, 0x30, 0x32, 0x20, 0x0005);
        public static readonly Cps1VideoConfig CpsB16 = new(0x0c, 0x0a, 0x08, 0x06, 0x04, 0x02, 0x00, 0x0406);

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
            Cps1GfxMapper gfxMapper,
            byte bootlegKludge,
            bool useSf2HackInputRead,
            Cps1AudioHardware audioHardware,
            double audioCpuClockHz,
            bool hasQSoundProtectionRom,
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
            GfxMapper = gfxMapper;
            BootlegKludge = bootlegKludge;
            UseSf2HackInputRead = useSf2HackInputRead;
            AudioHardware = audioHardware;
            AudioCpuClockHz = audioCpuClockHz;
            HasQSoundProtectionRom = hasQSoundProtectionRom;
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
        public Cps1GfxMapper GfxMapper { get; }
        public byte BootlegKludge { get; }
        public bool UseSf2HackInputRead { get; }
        public Cps1AudioHardware AudioHardware { get; }
        public double AudioCpuClockHz { get; }
        public bool HasQSoundProtectionRom { get; }
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

        private static readonly HashSet<string> S9263BMapperSets = new(StringComparer.OrdinalIgnoreCase)
        {
            "sf2acc",
            "sf2acca",
            "sf2accp2",
            "sf2bhh",
            "sf2ce",
            "sf2ceblp",
            "sf2cebltw",
            "sf2ceea",
            "sf2ceec",
            "sf2ceja",
            "sf2cejb",
            "sf2cejc",
            "sf2cet",
            "sf2ceua",
            "sf2ceub",
            "sf2ceuc",
            "sf2dkot2",
            "sf2dongb",
            "sf2koryu",
            "sf2level",
            "sf2m1",
            "sf2m2",
            "sf2m3",
            "sf2m4",
            "sf2m5",
            "sf2m6",
            "sf2m7",
            "sf2m8",
            "sf2m9",
            "sf2m10",
            "sf2mkot",
            "sf2rb",
            "sf2rb2",
            "sf2rb3",
            "sf2red",
            "sf2reda",
            "sf2redp2",
            "sf2v004",
            "sf2yyc"
        };

        private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["3wonderh"] = "3wondersh",
            ["3wonderu"] = "3wondersu",
            ["captcomb"] = "captcommb",
            ["captcomj"] = "captcommj",
            ["captcomu"] = "captcommu",
            ["cawingu"] = "cawingur1",
            ["daimakr2"] = "daimakair",
            ["dinoh"] = "dinohunt",
            ["dynwaru"] = "dynwara",
            ["forgott1"] = "forgottn",
            ["knightsh"] = "knights",
            ["mercsua"] = "mercsur1",
            ["punishru"] = "punisheru",
            ["punishrj"] = "punisherj",
            ["qadj"] = "qadjr",
            ["qtono2"] = "qtono2j",
            ["sf2cej"] = "sf2cejb",
            ["sf2t"] = "sf2hf",
            ["sf2tj"] = "sf2hfj",
            ["slammasu"] = "slammastu",
            ["stridrja"] = "striderjr",
            ["stridrua"] = "striderua",
            ["willowje"] = "willowj",
            ["wofh"] = "wofhfh"
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

        private static readonly Dictionary<string, Cps1QSoundDefinition> GeneratedQSoundDefinitions = BuildGeneratedQSoundDefinitions();

        private static readonly Dictionary<string, Cps1ClassicDefinition> GeneratedClassicDefinitions = BuildGeneratedClassicDefinitions();

        private static readonly Dictionary<string, string> GeneratedParentSets = BuildGeneratedParentSets();

        private static readonly Dictionary<string, uint> KnownRomCrcs = BuildKnownRomCrcs();

        private static readonly uint[] Crc32Table = BuildCrc32Table();

        public static bool IsSupportedSet(string setName)
        {
            string canonical = CanonicalSetName(setName);
            return Definitions.ContainsKey(canonical)
                || ClassicDefinitions.ContainsKey(canonical)
                || GeneratedQSoundDefinitions.ContainsKey(canonical)
                || GeneratedClassicDefinitions.ContainsKey(canonical);
        }

        public static Cps1DinoRomSet Load(string path)
        {
            string requestedSetName = Path.GetFileNameWithoutExtension(path).Trim().ToLowerInvariant();
            string setName = CanonicalSetName(requestedSetName);
            if (Definitions.TryGetValue(setName, out Cps1QSoundDefinition? definition))
                return LoadQSoundSet(path, setName, definition);
            if (ClassicDefinitions.TryGetValue(setName, out Cps1ClassicDefinition? classicDefinition))
                return LoadClassicSet(path, setName, classicDefinition);
            if (GeneratedQSoundDefinitions.TryGetValue(setName, out Cps1QSoundDefinition? generatedQSoundDefinition))
                return LoadQSoundSet(path, setName, generatedQSoundDefinition);
            if (GeneratedClassicDefinitions.TryGetValue(setName, out Cps1ClassicDefinition? generatedClassicDefinition))
                return LoadClassicSet(path, setName, generatedClassicDefinition);

            throw new NotSupportedException($"CPS1 ROM set '{requestedSetName}' is not registered in the EutherDrive CPS1 loader.");
        }

        private static Cps1DinoRomSet LoadQSoundSet(string path, string setName, Cps1QSoundDefinition definition)
        {
            Dictionary<string, byte[]> entries = ReadArchive(path);
            MergeParentArchivesIfPresent(path, ResolveParentSetName(setName, definition.ParentSetName), entries);
            definition = SelectEffectiveQSoundDefinition(setName, definition, entries);
            ValidateQSoundSetEntries(setName, entries);

            byte[] program = new byte[ProgramRomSize];
            Array.Fill(program, (byte)0xff);
            foreach (RomLoad load in definition.ProgramLoads)
                LoadProgram(entries, program, load);

            byte[] gfx = new byte[definition.GraphicsSize];
            foreach (RomLoad load in definition.GraphicsLoads)
                Load64Word(entries, gfx, load.Offset, load.Names);

            byte[] audioCpu = LoadAudioCpu(entries, definition.AudioCpuLoads);
            byte[] qsound = LoadQSound(entries, definition.QSoundSize, definition.QSoundLoads);

            byte[] qsoundDsp = LoadQSoundDsp(path, entries);

            return new Cps1DinoRomSet(
                setName,
                definition.VideoConfig,
                Cps1GfxMapper.Linear,
                0,
                false,
                Cps1AudioHardware.QSound,
                8_000_000.0,
                UsesQSoundProtectionRom(setName),
                definition.KabukiKeys,
                program,
                gfx,
                audioCpu,
                Array.Empty<byte>(),
                qsound,
                qsoundDsp);
        }

        private static void ValidateQSoundSetEntries(string setName, Dictionary<string, byte[]> entries)
        {
            if (string.Equals(setName, "mbomberj", StringComparison.OrdinalIgnoreCase))
            {
                RequireRom(entries, "mbj_23e.8f", "mbj23e");
                RequireRom(entries, "mbj_22b.7f");
            }
        }

        private static Cps1QSoundDefinition SelectEffectiveQSoundDefinition(string setName, Cps1QSoundDefinition definition, Dictionary<string, byte[]> entries)
        {
            if (string.Equals(setName, "wof", StringComparison.OrdinalIgnoreCase)
                && !TryFind(entries, out _, "tk2e_23c.8f")
                && TryFind(entries, out _, "tk2e_23b.8f", "tk2e_23b.rom"))
            {
                return definition with { VideoConfig = Cps1VideoConfig.Default };
            }

            return definition;
        }

        private static void RequireRom(Dictionary<string, byte[]> entries, params string[] names)
        {
            if (TryFind(entries, out _, names))
                return;

            string present = string.Join(", ", entries.Keys.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase));
            throw new InvalidDataException($"Missing CPS1 ROM file ({string.Join(" or ", names)}). Present files: {present}");
        }

        private static Cps1DinoRomSet LoadClassicSet(string path, string setName, Cps1ClassicDefinition definition)
        {
            Dictionary<string, byte[]> entries = ReadArchive(path);
            MergeParentArchivesIfPresent(path, ResolveParentSetName(setName, definition.ParentSetName), entries);

            byte[] program = new byte[ProgramRomSize];
            Array.Fill(program, (byte)0xff);
            foreach (RomLoad load in definition.ProgramLoads)
                LoadProgram(entries, program, load);

            byte[] gfx = new byte[definition.GraphicsSize];
            foreach (RomLoad load in definition.GraphicsLoads)
                LoadGraphics(entries, gfx, load);

            byte[] audioCpu = LoadAudioCpu(entries, definition.AudioCpuLoads);
            byte[] oki = LoadOki(entries, definition.OkiSize, definition.OkiLoads);

            return new Cps1DinoRomSet(
                setName,
                definition.VideoConfig,
                GetGfxMapper(setName),
                GetBootlegKludge(setName),
                UsesSf2HackInputRead(setName),
                Cps1AudioHardware.YmOki,
                3_579_545.0,
                false,
                new KabukiKeys(0, 0, 0, 0),
                program,
                gfx,
                audioCpu,
                oki,
                Array.Empty<byte>(),
                Array.Empty<byte>());
        }

        private static bool UsesQSoundProtectionRom(string setName)
            => string.Equals(setName, "slammast", StringComparison.OrdinalIgnoreCase)
                || string.Equals(setName, "slammastu", StringComparison.OrdinalIgnoreCase)
                || string.Equals(setName, "mbomberj", StringComparison.OrdinalIgnoreCase);

        private static Cps1GfxMapper GetGfxMapper(string setName)
            => S9263BMapperSets.Contains(setName) ? Cps1GfxMapper.S9263B : Cps1GfxMapper.Linear;

        private static byte GetBootlegKludge(string setName)
            => string.Equals(setName, "sf2m7", StringComparison.OrdinalIgnoreCase) ? (byte)0x41 : (byte)0;

        private static bool UsesSf2HackInputRead(string setName)
            => string.Equals(setName, "sf2m7", StringComparison.OrdinalIgnoreCase);

        private static Dictionary<string, string> BuildGeneratedParentSets()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["1941j"] = "1941",
                ["1941r1"] = "1941",
                ["1941u"] = "1941",
                ["3wondersb"] = "3wonders",
                ["3wondersbi"] = "3wonders",
                ["3wondersh"] = "3wonders",
                ["3wondersr1"] = "3wonders",
                ["3wondersu"] = "3wonders",
                ["area88"] = "unsquad",
                ["area88r"] = "unsquad",
                ["captcommb"] = "captcomm",
                ["captcommb2"] = "captcomm",
                ["captcommj"] = "captcomm",
                ["captcommjr1"] = "captcomm",
                ["captcommr1"] = "captcomm",
                ["captcommu"] = "captcomm",
                ["cawingb2"] = "cawing",
                ["cawingbl"] = "cawing",
                ["cawingj"] = "cawing",
                ["cawingjr"] = "cawing",
                ["cawingr1"] = "cawing",
                ["cawingu"] = "cawing",
                ["cawingur1"] = "cawing",
                ["chikij"] = "mtwins",
                ["cworld2ja"] = "cworld2j",
                ["cworld2jb"] = "cworld2j",
                ["daimakai"] = "ghouls",
                ["daimakair"] = "ghouls",
                ["dinoa"] = "dino",
                ["dinohunt"] = "dino",
                ["dinoj"] = "dino",
                ["dinopic"] = "dino",
                ["dinopic2"] = "dino",
                ["dinopic3"] = "dino",
                ["dinou"] = "dino",
                ["dynwara"] = "dynwar",
                ["dynwarj"] = "dynwar",
                ["dynwarjr"] = "dynwar",
                ["fcrash"] = "ffight",
                ["ffighta"] = "ffight",
                ["ffightae"] = "ffight",
                ["ffightbl"] = "ffight",
                ["ffightbla"] = "ffight",
                ["ffightblb"] = "ffight",
                ["ffightj"] = "ffight",
                ["ffightj1"] = "ffight",
                ["ffightj2"] = "ffight",
                ["ffightj3"] = "ffight",
                ["ffightj4"] = "ffight",
                ["ffightjh"] = "ffight",
                ["ffightu"] = "ffight",
                ["ffightu1"] = "ffight",
                ["ffightua"] = "ffight",
                ["ffightub"] = "ffight",
                ["ffightuc"] = "ffight",
                ["forgottna"] = "forgottn",
                ["forgottnj"] = "forgottn",
                ["forgottnu"] = "forgottn",
                ["forgottnua"] = "forgottn",
                ["forgottnuaa"] = "forgottn",
                ["forgottnuc"] = "forgottn",
                ["forgottnue"] = "forgottn",
                ["ghoulsu"] = "ghouls",
                ["jurassic99"] = "dino",
                ["knightsb"] = "knights",
                ["knightsb2"] = "knights",
                ["knightsb3"] = "knights",
                ["knightsj"] = "knights",
                ["knightsja"] = "knights",
                ["knightsu"] = "knights",
                ["kodb"] = "kod",
                ["kodj"] = "kod",
                ["kodja"] = "kod",
                ["kodr1"] = "kod",
                ["kodr2"] = "kod",
                ["kodu"] = "kod",
                ["lostwrld"] = "forgottn",
                ["lostwrldo"] = "forgottn",
                ["mbomberj"] = "slammast",
                ["mbombrdj"] = "mbombrd",
                ["megamana"] = "megaman",
                ["mercsj"] = "mercs",
                ["mercsu"] = "mercs",
                ["mercsur1"] = "mercs",
                ["mswordj"] = "msword",
                ["mswordr1"] = "msword",
                ["mswordu"] = "msword",
                ["mtwinsb"] = "mtwins",
                ["nemoj"] = "nemo",
                ["nemoja"] = "nemo",
                ["nemor1"] = "nemo",
                ["pang3b"] = "pang3",
                ["pang3b2"] = "pang3",
                ["pang3b3"] = "pang3",
                ["pang3b4"] = "pang3",
                ["pang3b5"] = "pang3",
                ["pang3j"] = "pang3",
                ["pang3r1"] = "pang3",
                ["punipic"] = "punisher",
                ["punipic2"] = "punisher",
                ["punipic3"] = "punisher",
                ["punisherbz"] = "punisher",
                ["punisherh"] = "punisher",
                ["punisherj"] = "punisher",
                ["punisheru"] = "punisher",
                ["qadjr"] = "qad",
                ["rockmanj"] = "megaman",
                ["sf2acc"] = "sf2ce",
                ["sf2acca"] = "sf2ce",
                ["sf2accp2"] = "sf2ce",
                ["sf2amf"] = "sf2ce",
                ["sf2amf2"] = "sf2ce",
                ["sf2amf3"] = "sf2ce",
                ["sf2b"] = "sf2",
                ["sf2b2"] = "sf2",
                ["sf2bhh"] = "sf2ce",
                ["sf2ceb"] = "sf2ce",
                ["sf2ceb2"] = "sf2ce",
                ["sf2ceb3"] = "sf2ce",
                ["sf2ceb4"] = "sf2ce",
                ["sf2ceb5"] = "sf2ce",
                ["sf2ceblp"] = "sf2ce",
                ["sf2cebltw"] = "sf2ce",
                ["sf2ceds6"] = "sf2ce",
                ["sf2ceea"] = "sf2ce",
                ["sf2ceec"] = "sf2ce",
                ["sf2ceja"] = "sf2ce",
                ["sf2cejb"] = "sf2ce",
                ["sf2cejc"] = "sf2ce",
                ["sf2cems6a"] = "sf2ce",
                ["sf2cems6b"] = "sf2ce",
                ["sf2cems6c"] = "sf2ce",
                ["sf2cet"] = "sf2ce",
                ["sf2ceua"] = "sf2ce",
                ["sf2ceub"] = "sf2ce",
                ["sf2ceuc"] = "sf2ce",
                ["sf2ceupl"] = "sf2ce",
                ["sf2dkot2"] = "sf2ce",
                ["sf2dongb"] = "sf2ce",
                ["sf2ea"] = "sf2",
                ["sf2eb"] = "sf2",
                ["sf2ebbl"] = "sf2",
                ["sf2ebbl2"] = "sf2",
                ["sf2ebbl3"] = "sf2",
                ["sf2ed"] = "sf2",
                ["sf2ee"] = "sf2",
                ["sf2ef"] = "sf2",
                ["sf2em"] = "sf2",
                ["sf2en"] = "sf2",
                ["sf2hfj"] = "sf2hf",
                ["sf2hfu"] = "sf2hf",
                ["sf2j"] = "sf2",
                ["sf2j17"] = "sf2",
                ["sf2ja"] = "sf2",
                ["sf2jc"] = "sf2",
                ["sf2jf"] = "sf2",
                ["sf2jh"] = "sf2",
                ["sf2jl"] = "sf2",
                ["sf2koryu"] = "sf2ce",
                ["sf2level"] = "sf2ce",
                ["sf2m1"] = "sf2ce",
                ["sf2m10"] = "sf2ce",
                ["sf2m2"] = "sf2ce",
                ["sf2m3"] = "sf2ce",
                ["sf2m4"] = "sf2ce",
                ["sf2m5"] = "sf2ce",
                ["sf2m6"] = "sf2ce",
                ["sf2m7"] = "sf2ce",
                ["sf2m8"] = "sf2ce",
                ["sf2m9"] = "sf2ce",
                ["sf2mdt"] = "sf2ce",
                ["sf2mdta"] = "sf2ce",
                ["sf2mdtb"] = "sf2ce",
                ["sf2mkot"] = "sf2ce",
                ["sf2qp1"] = "sf2",
                ["sf2qp2"] = "sf2",
                ["sf2rb"] = "sf2ce",
                ["sf2rb2"] = "sf2ce",
                ["sf2rb3"] = "sf2ce",
                ["sf2re"] = "sf2ce",
                ["sf2red"] = "sf2ce",
                ["sf2reda"] = "sf2ce",
                ["sf2rk"] = "sf2",
                ["sf2rules"] = "sf2",
                ["sf2stt"] = "sf2",
                ["sf2thndr"] = "sf2",
                ["sf2thndr2"] = "sf2",
                ["sf2ua"] = "sf2",
                ["sf2ub"] = "sf2",
                ["sf2uc"] = "sf2",
                ["sf2ud"] = "sf2",
                ["sf2ue"] = "sf2",
                ["sf2uf"] = "sf2",
                ["sf2ug"] = "sf2",
                ["sf2uh"] = "sf2",
                ["sf2ui"] = "sf2",
                ["sf2uk"] = "sf2",
                ["sf2um"] = "sf2",
                ["sf2v004"] = "sf2ce",
                ["sf2yyc"] = "sf2ce",
                ["sgyxz"] = "wof",
                ["slammastu"] = "slammast",
                ["slampic"] = "slammast",
                ["slampic2"] = "slammast",
                ["striderj"] = "strider",
                ["striderjr"] = "strider",
                ["striderua"] = "strider",
                ["strideruc"] = "strider",
                ["varthb"] = "varth",
                ["varthb2"] = "varth",
                ["varthb3"] = "varth",
                ["varthj"] = "varth",
                ["varthjr"] = "varth",
                ["varthr1"] = "varth",
                ["varthu"] = "varth",
                ["willowj"] = "willow",
                ["willowu"] = "willow",
                ["willowuo"] = "willow",
                ["wofa"] = "wof",
                ["wofabl"] = "wof",
                ["wofhfh"] = "wof",
                ["wofj"] = "wof",
                ["wofpic"] = "wof",
                ["wofr1"] = "wof",
                ["wofr1bl"] = "wof",
                ["wofu"] = "wof",
                ["wonder3"] = "3wonders",
            };
        }

        private static Dictionary<string, Cps1QSoundDefinition> BuildGeneratedQSoundDefinitions()
        {
            var definitions = new Dictionary<string, Cps1QSoundDefinition>(StringComparer.OrdinalIgnoreCase);
            return definitions;
        }

        private static Dictionary<string, Cps1ClassicDefinition> BuildGeneratedClassicDefinitions()
        {
            var definitions = new Dictionary<string, Cps1ClassicDefinition>(StringComparer.OrdinalIgnoreCase);
            definitions["1941"] = new Cps1ClassicDefinition(
                "1941",
                null,
                new Cps1VideoConfig(0x28, 0x2a, 0x2c, 0x2e, 0x30, 0x32),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "41em_30.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "41em_35.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "41em_31.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "41em_36.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "41-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "41-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "41-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "41-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "41-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "41_9.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "41_9.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "41_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "41_19.12c"),
                });
            definitions["1941j"] = new Cps1ClassicDefinition(
                "1941j",
                null,
                new Cps1VideoConfig(0x28, 0x2a, 0x2c, 0x2e, 0x30, 0x32),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "41_36.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "41_42.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "41_37.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "41_43.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "41_34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "41_40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "41_35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "41_41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "41_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "41_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "41_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "41_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "41_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "41_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "41_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "41_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "41_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "41_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "41_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "41_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "41_25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "41_18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "41_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "41_33.9f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "41_23.13b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "41_23.13b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "41_30.12c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "41_31.13c"),
                });
            definitions["3wonders"] = new Cps1ClassicDefinition(
                "3wonders",
                null,
                new Cps1VideoConfig(0x28, 0x26, 0x24, 0x22, 0x20, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "rte_30a.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "rte_35a.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "rte_31a.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "rte_36a.12h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "rt_28a.9f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "rt_33a.9h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "rte_29a.10f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "rte_34a.10h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "rt-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "rt-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "rt-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "rt-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "rt-6m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "rt-8m.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "rt-2m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "rt-4m.6a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "rt_9.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "rt_9.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "rt_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "rt_19.12c"),
                });
            definitions["3wondersh"] = new Cps1ClassicDefinition(
                "3wondersh",
                null,
                Cps1VideoConfig.Default,
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "22.bin"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "26.bin"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "23.bin"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "27.bin"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "rt_28a.9f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "rt_33a.9h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "rte_29a.10f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "rte_34a.10h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x40000, "16.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x40000, "6.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x40000, "18.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x40000, "8.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x40000, "12.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x40000, "2.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x40000, "14.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x40000, "4.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200000, 0x0, 0x40000, "17.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200001, 0x0, 0x40000, "7.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200002, 0x0, 0x40000, "19.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200003, 0x0, 0x40000, "9.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200004, 0x0, 0x40000, "13.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200005, 0x0, 0x40000, "3.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200006, 0x0, 0x40000, "15.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200007, 0x0, 0x40000, "5.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "rt_9.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "rt_9.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "rt_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "rt_19.12c"),
                });
            definitions["3wondersu"] = new Cps1ClassicDefinition(
                "3wondersu",
                null,
                new Cps1VideoConfig(0x28, 0x26, 0x24, 0x22, 0x20, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "rtu_30a.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "rtu_35a.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "rtu_31a.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "rtu_36a.12h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "rt_28a.9f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "rt_33a.9h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "rtu_29a.10f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "rtu_34a.10h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "rt-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "rt-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "rt-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "rt-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "rt-6m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "rt-8m.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "rt-2m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "rt-4m.6a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "rt_9.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "rt_9.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "rt_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "rt_19.12c"),
                });
            definitions["area88"] = new Cps1ClassicDefinition(
                "area88",
                null,
                new Cps1VideoConfig(0x26, 0x28, 0x2a, 0x2c, 0x2e, 0x30),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "ar_36.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "ar_42.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "ar_37.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "ar_43.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "ar_34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "ar_40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "ar_35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "ar_41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "ar_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "ar_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "ar_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "ar_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "ar_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "ar_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "ar_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "ar_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "ar_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "ar_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "ar_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "ar_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "ar_25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "ar_18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "ar_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "ar_33.9f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "ar_23.13c"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "ar_23.13c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "ar_30.12e"),
                });
            definitions["captcomm"] = new Cps1ClassicDefinition(
                "captcomm",
                null,
                new Cps1VideoConfig(0x20, 0x2e, 0x2c, 0x2a, 0x28, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "cce_23f.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "cc_22f.7f"),
                    Load(RomLoadKind.Byte, 0x100000, 0x0, 0x20000, "cc_24f.9e"),
                    Load(RomLoadKind.Byte, 0x100001, 0x0, 0x20000, "cc_28f.9f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "cc-5m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "cc-7m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "cc-1m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "cc-3m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "cc-6m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "cc-8m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "cc-2m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "cc-4m.10a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "cc_09.11a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "cc_09.11a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "cc_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "cc_19.12c"),
                });
            definitions["captcommb"] = new Cps1ClassicDefinition(
                "captcommb",
                null,
                new Cps1VideoConfig(0x20, 0x2e, 0x2c, 0x2a, 0x28, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x80000, "25.bin"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x80000, "27.bin"),
                    Load(RomLoadKind.Byte, 0x100000, 0x0, 0x40000, "24.bin"),
                    Load(RomLoadKind.Byte, 0x100001, 0x0, 0x40000, "26.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x40000, "c91e-01.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x40000, 0x40000, "c91e-01.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200000, 0x80000, 0x40000, "c91e-01.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200004, 0xc0000, 0x40000, "c91e-01.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x40000, "c91e-02.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x40000, 0x40000, "c91e-02.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200001, 0x80000, 0x40000, "c91e-02.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200005, 0xc0000, 0x40000, "c91e-02.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x40000, "c91e-03.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x40000, 0x40000, "c91e-03.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200002, 0x80000, 0x40000, "c91e-03.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200006, 0xc0000, 0x40000, "c91e-03.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x40000, "c91e-04.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x40000, 0x40000, "c91e-04.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200003, 0x80000, 0x40000, "c91e-04.bin"),
                    Load(RomLoadKind.Graphics64Byte, 0x200007, 0xc0000, 0x40000, "c91e-04.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "l.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "l.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x40000, "c91e-05.bin"),
                });
            definitions["captcommj"] = new Cps1ClassicDefinition(
                "captcommj",
                null,
                new Cps1VideoConfig(0x20, 0x2e, 0x2c, 0x2a, 0x28, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "ccj_23f.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "ccj_22f.7f"),
                    Load(RomLoadKind.Byte, 0x100000, 0x0, 0x20000, "ccj_24f.9e"),
                    Load(RomLoadKind.Byte, 0x100001, 0x0, 0x20000, "ccj_28f.9f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "cc_01.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "cc_02.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "cc_03.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "cc_04.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "cc_05.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "cc_06.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "cc_07.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "cc_08.10a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "ccj_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "ccj_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "ccj_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "ccj_19.12c"),
                });
            definitions["captcommu"] = new Cps1ClassicDefinition(
                "captcommu",
                null,
                new Cps1VideoConfig(0x20, 0x2e, 0x2c, 0x2a, 0x28, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "ccu_23b.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "ccu_22c.7f"),
                    Load(RomLoadKind.Byte, 0x100000, 0x0, 0x20000, "ccu_24b.9e"),
                    Load(RomLoadKind.Byte, 0x100001, 0x0, 0x20000, "ccu_28b.9f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "cc-5m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "cc-7m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "cc-1m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "cc-3m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "cc-6m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "cc-8m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "cc-2m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "cc-4m.10a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "ccu_09.11a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "ccu_09.11a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "ccu_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "ccu_19.12c"),
                });
            definitions["cawing"] = new Cps1ClassicDefinition(
                "cawing",
                null,
                Cps1VideoConfig.CpsB16,
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "cae_30a.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "cae_35a.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "cae_31a.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "cae_36a.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "ca-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "ca-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "ca-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "ca-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "ca-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "ca_9.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "ca_9.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "ca_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "ca_19.12c"),
                });
            definitions["cawingj"] = new Cps1ClassicDefinition(
                "cawingj",
                null,
                Cps1VideoConfig.CpsB16,
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "caj_36a.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "caj_42a.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "caj_37a.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "caj_43a.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "caj_34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "caj_40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "caj_35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "caj_41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "caj_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "caj_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "caj_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "caj_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "caj_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "caj_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "caj_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "caj_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "caj_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "caj_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "caj_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "caj_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "caj_25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "caj_18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "caj_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "caj_33.9f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "caj_23.13b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "caj_23.13b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "caj_30.12c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "caj_31.13c"),
                });
            definitions["cawingr1"] = new Cps1ClassicDefinition(
                "cawingr1",
                null,
                Cps1VideoConfig.CpsB16,
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "cae_30.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "cae_35.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "cae_31.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "cae_36.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "ca-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "ca-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "ca-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "ca-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "ca-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "cae_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "cae_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "cae_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "cae_19.12c"),
                });
            definitions["cawingu"] = new Cps1ClassicDefinition(
                "cawingu",
                null,
                Cps1VideoConfig.CpsB05,
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "cau_36.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "cau_42.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "cau_37.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "cau_43.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "cau_34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "cau_40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "cau_35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "cau_41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "cau_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "cau_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "cau_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "cau_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "cau_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "cau_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "cau_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "cau_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "cau_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "cau_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "cau_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "cau_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "cau_25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "cau_18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "cau_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "cau_33.9f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "cau_23.13b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "cau_23.13b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "cau_30.12c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "cau_31.13c"),
                });
            definitions["cawingur1"] = new Cps1ClassicDefinition(
                "cawingur1",
                "cawing",
                Cps1VideoConfig.CpsB16,
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "cau_30a.11f", "cae_30a.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "cau_35a.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "cau_31a.12f", "cae_31a.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "cau_36a.12h", "cae_36a.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "ca-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "ca-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "ca-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "ca-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "ca-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "cau_09.12b", "ca_9.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "cau_09.12b", "ca_9.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "cau_18.11c", "ca_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "cau_19.12c", "ca_19.12c"),
                });
            definitions["chikij"] = new Cps1ClassicDefinition(
                "chikij",
                null,
                new Cps1VideoConfig(0x12, 0x14, 0x16, 0x18, 0x1a, 0x1c),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "chj_36a.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "chj_42a.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "chj_37a.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "chj_43a.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "ch_34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "ch_40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "ch_35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "ch_41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "ch_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "ch_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "ch_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "ch_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "ch_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "ch_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "ch_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "ch_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "ch_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "ch_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "ch_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "ch_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "ch_25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "ch_18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "ch_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "ch_33.9f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "ch_23.13b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "ch_23.13b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "ch_30.12c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "ch_31.13c"),
                });
            definitions["cworld2j"] = new Cps1ClassicDefinition(
                "cworld2j",
                null,
                new Cps1VideoConfig(0x20, 0x2e, 0x2c, 0x2a, 0x28, 0x30),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "q5_36.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "q5_42.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "q5_37.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "q5_43.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "q5_34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "q5_40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "q5_35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "q5_41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "q5_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "q5_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "q5_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "q5_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "q5_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "q5_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "q5_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "q5_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "q5_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "q5_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "q5_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "q5_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "q5_25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "q5_18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "q5_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "q5_33.9f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "q5_23.13b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "q5_23.13b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "q5_30.12c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "q5_31.13c"),
                });
            definitions["dinohunt"] = new Cps1ClassicDefinition(
                "dinohunt",
                null,
                Cps1VideoConfig.Default,
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "u23"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "u22"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "u21"),
                    Load(RomLoadKind.WordSwap, 0x180000, 0x0, 0x80000, "u20"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "u1"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "u2"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "u3"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "u4"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "u5"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "u6"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "u7"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "u8"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "u9"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "u9"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "u18"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "u19"),
                });
            definitions["dynwar"] = new Cps1ClassicDefinition(
                "dynwar",
                null,
                new Cps1VideoConfig(0x2c, 0x2a, 0x28, 0x26, 0x24, 0x22),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "30.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "35.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "31.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "36.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "tkm-9.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "tkm-5.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "tkm-8.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "tkm-6.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "tkm-7.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "tkm-1.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "tkm-4.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "tkm-2.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "tkm-3.6a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "tke_17.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "tke_17.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "tke_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "tke_19.12c"),
                });
            definitions["dynwara"] = new Cps1ClassicDefinition(
                "dynwara",
                null,
                new Cps1VideoConfig(0x2c, 0x2a, 0x28, 0x26, 0x24, 0x22),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "tke_36.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "tke_42.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "tke_37.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "tke_43.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "33.9f"),
                    Load(RomLoadKind.Graphics64Byte, 0x200000, 0x0, 0x20000, "11.7b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200001, 0x0, 0x20000, "03.7a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200002, 0x0, 0x20000, "15.11b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200003, 0x0, 0x20000, "07.11a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200004, 0x0, 0x20000, "26.8e"),
                    Load(RomLoadKind.Graphics64Byte, 0x200005, 0x0, 0x20000, "19.8c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200006, 0x0, 0x20000, "28.10e"),
                    Load(RomLoadKind.Graphics64Byte, 0x200007, 0x0, 0x20000, "21.10c"),
                    Load(RomLoadKind.Graphics64Byte, 0x300000, 0x0, 0x20000, "12.8b"),
                    Load(RomLoadKind.Graphics64Byte, 0x300001, 0x0, 0x20000, "04.8a"),
                    Load(RomLoadKind.Graphics64Byte, 0x300002, 0x0, 0x20000, "16.12b"),
                    Load(RomLoadKind.Graphics64Byte, 0x300003, 0x0, 0x20000, "08.12a"),
                    Load(RomLoadKind.Graphics64Byte, 0x300004, 0x0, 0x20000, "27.9e"),
                    Load(RomLoadKind.Graphics64Byte, 0x300005, 0x0, 0x20000, "20.9c"),
                    Load(RomLoadKind.Graphics64Byte, 0x300006, 0x0, 0x20000, "29.11e"),
                    Load(RomLoadKind.Graphics64Byte, 0x300007, 0x0, 0x20000, "22.11c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "23.13c"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "23.13c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "tke_30.12e"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "tke_31.13e"),
                });
            definitions["dynwarj"] = new Cps1ClassicDefinition(
                "dynwarj",
                null,
                new Cps1VideoConfig(0x2c, 0x2a, 0x28, 0x26, 0x24, 0x22),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "36.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "42.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "37.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "43.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "33.9f"),
                    Load(RomLoadKind.Graphics64Byte, 0x200000, 0x0, 0x20000, "11.7b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200001, 0x0, 0x20000, "03.7a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200002, 0x0, 0x20000, "15.11b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200003, 0x0, 0x20000, "07.11a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200004, 0x0, 0x20000, "26.8e"),
                    Load(RomLoadKind.Graphics64Byte, 0x200005, 0x0, 0x20000, "19.8c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200006, 0x0, 0x20000, "28.10e"),
                    Load(RomLoadKind.Graphics64Byte, 0x200007, 0x0, 0x20000, "21.10c"),
                    Load(RomLoadKind.Graphics64Byte, 0x300000, 0x0, 0x20000, "12.8b"),
                    Load(RomLoadKind.Graphics64Byte, 0x300001, 0x0, 0x20000, "04.8a"),
                    Load(RomLoadKind.Graphics64Byte, 0x300002, 0x0, 0x20000, "16.12b"),
                    Load(RomLoadKind.Graphics64Byte, 0x300003, 0x0, 0x20000, "08.12a"),
                    Load(RomLoadKind.Graphics64Byte, 0x300004, 0x0, 0x20000, "27.9e"),
                    Load(RomLoadKind.Graphics64Byte, 0x300005, 0x0, 0x20000, "20.9c"),
                    Load(RomLoadKind.Graphics64Byte, 0x300006, 0x0, 0x20000, "29.11e"),
                    Load(RomLoadKind.Graphics64Byte, 0x300007, 0x0, 0x20000, "22.11c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "23.13c"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "23.13c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "30.12e"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "31.13e"),
                });
            definitions["ffightua"] = new Cps1ClassicDefinition(
                "ffightua",
                null,
                new Cps1VideoConfig(0x26, 0x28, 0x2a, 0x2c, 0x2e, 0x30),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "ffu_36.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "ffu_42.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "ffu_37.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "ffu_43.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "ff-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "ff-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "ff-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "ff-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "ff-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "ff_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "ff_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "ff_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "ff_19.12c"),
                });
            definitions["ffightub"] = new Cps1ClassicDefinition(
                "ffightub",
                null,
                new Cps1VideoConfig(0x30, 0x2e, 0x2c, 0x2a, 0x28, 0x26),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "ffu_30_3.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "ffu_35_3.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "ffu_31_3.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "ffu_36_3.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "ff-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "ff-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "ff-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "ff-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "ff-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "ff_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "ff_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "ff_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "ff_19.12c"),
                });
            definitions["forgottn"] = new Cps1ClassicDefinition(
                "forgottn",
                null,
                new Cps1VideoConfig(0x26, 0x28, 0x2a, 0x2c, 0x2e, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "lw40.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "lw41.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "lw42.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "lw43.13h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "lw-07.10g"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "lw_2.2b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "lw_1.2a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "lw-08.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "lw_18.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "lw_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "lw_30.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "lw_29.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "lw_4.3b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "lw_3.3a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "lw_20.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "lw_19.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "lw_32.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "lw_31.9f"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "lw-02.6b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200002, 0x0, 0x20000, "lw_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200003, 0x0, 0x20000, "lw_13.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "lw-06.9d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200006, 0x0, 0x20000, "lw_26.10e"),
                    Load(RomLoadKind.Graphics64Byte, 0x200007, 0x0, 0x20000, "lw_25.10c"),
                    Load(RomLoadKind.Graphics64Byte, 0x300002, 0x0, 0x20000, "lw_16.11b"),
                    Load(RomLoadKind.Graphics64Byte, 0x300003, 0x0, 0x20000, "lw_15.11a"),
                    Load(RomLoadKind.Graphics64Byte, 0x300006, 0x0, 0x20000, "lw_28.11e"),
                    Load(RomLoadKind.Graphics64Byte, 0x300007, 0x0, 0x20000, "lw_27.11c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "lw_37.13c"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "lw_37.13c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "lw-03u.12e"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "lw-04u.13e"),
                });
            definitions["knights"] = new Cps1ClassicDefinition(
                "knights",
                null,
                new Cps1VideoConfig(0x28, 0x26, 0x24, 0x22, 0x20, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "kr_23e.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "kr_22.7f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "kr-5m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "kr-7m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "kr-1m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "kr-3m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "kr-6m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "kr-8m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "kr-2m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "kr-4m.10a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "kr_09.11a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "kr_09.11a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "kr_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "kr_19.12c"),
                });
            definitions["knightsj"] = new Cps1ClassicDefinition(
                "knightsj",
                null,
                new Cps1VideoConfig(0x28, 0x26, 0x24, 0x22, 0x20, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "kr_23j.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "kr_22.7f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "kr_01.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "kr_02.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "kr_03.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "kr_04.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "kr_05.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "kr_06.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "kr_07.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "kr_08.10a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "kr_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "kr_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "kr_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "kr_19.12c"),
                });
            definitions["knightsu"] = new Cps1ClassicDefinition(
                "knightsu",
                null,
                new Cps1VideoConfig(0x28, 0x26, 0x24, 0x22, 0x20, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "kr_23u.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "kr_22.7f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "kr-5m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "kr-7m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "kr-1m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "kr-3m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "kr-6m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "kr-8m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "kr-2m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "kr-4m.10a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "kr_09.11a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "kr_09.11a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "kr_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "kr_19.12c"),
                });
            definitions["kod"] = new Cps1ClassicDefinition(
                "kod",
                null,
                new Cps1VideoConfig(0x20, 0x2e, 0x2c, 0x2a, 0x28, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "kde_30a.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "kde_37a.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "kde_31a.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "kde_38a.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "kd_28.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "kd_35.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "kd_29.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "kd_36a.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "kd-5m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "kd-7m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "kd-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "kd-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "kd-6m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "kd-8m.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "kd-2m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "kd-4m.5c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "kd_9.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "kd_9.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "kd_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "kd_19.12c"),
                });
            definitions["kodj"] = new Cps1ClassicDefinition(
                "kodj",
                null,
                new Cps1VideoConfig(0x20, 0x2e, 0x2c, 0x2a, 0x28, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "kdj_30a.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "kdj_37a.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "kdj_31a.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "kdj_38a.12f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "kd_33.6f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "kd_06.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "kd_08.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "kd_05.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "kd_07.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "kd_15.8c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "kd_17.10c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "kd_14.7c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "kd_16.9c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "kd_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "kd_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "kd_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "kd_19.12c"),
                });
            definitions["kodu"] = new Cps1ClassicDefinition(
                "kodu",
                null,
                new Cps1VideoConfig(0x20, 0x2e, 0x2c, 0x2a, 0x28, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "kdu_30b.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "kdu_37b.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "kdu_31b.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "kdu_38b.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "kdu_28.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "kdu_35.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "kdu_29.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "kdu_36a.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "kd-5m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "kd-7m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "kd-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "kd-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "kd-6m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "kd-8m.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "kd-2m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "kd-4m.5c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "kd_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "kd_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "kd_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "kd_19.12c"),
                });
            definitions["lostwrld"] = new Cps1ClassicDefinition(
                "lostwrld",
                null,
                new Cps1VideoConfig(0x26, 0x28, 0x2a, 0x2c, 0x2e, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "lw_11c.14f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "lw_15c.14g"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "lw_10c.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "lw_14c.13g"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "lw-07.13e"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "lw-01.9d"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "lw-08.9f"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "lw-05.9e"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "lw-12.9g"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "lw-02.12d"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "lw-09.12f"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "lw-06.12e"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "lw-13.12g"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "lw_00b.14a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "lw_00b.14a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "lw-03.14c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "lw-04.13c"),
                });
            definitions["megaman"] = new Cps1ClassicDefinition(
                "megaman",
                null,
                Cps1VideoConfig.Default,
                0x800000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "rcmu_23b.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "rcmu_22b.7f"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "rcmu_21a.6f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "rcm_01.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "rcm_02.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "rcm_03.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "rcm_04.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "rcm_05.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "rcm_06.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "rcm_07.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "rcm_08.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "rcm_10.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "rcm_11.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "rcm_12.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "rcm_13.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x600000, 0x0, 0x80000, "rcm_14.7c"),
                    Load(RomLoadKind.Graphics64Word, 0x600002, 0x0, 0x80000, "rcm_15.8c"),
                    Load(RomLoadKind.Graphics64Word, 0x600004, 0x0, 0x80000, "rcm_16.9c"),
                    Load(RomLoadKind.Graphics64Word, 0x600006, 0x0, 0x80000, "rcm_17.10c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "rcm_09.11a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "rcm_09.11a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "rcm_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "rcm_19.12c"),
                });
            definitions["mercs"] = new Cps1ClassicDefinition(
                "mercs",
                null,
                new Cps1VideoConfig(0x2c, 0x2a, 0x28, 0x26, 0x24, 0x22),
                0x300000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "so2_30e.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "so2_35e.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "so2_31e.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "so2_36e.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "so2-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "so2-6m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "so2-8m.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "so2-2m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "so2-4m.6a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200000, 0x0, 0x20000, "so2_24.7d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200001, 0x0, 0x20000, "so2_14.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200002, 0x0, 0x20000, "so2_26.9d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200003, 0x0, 0x20000, "so2_16.9c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200004, 0x0, 0x20000, "so2_20.3d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200005, 0x0, 0x20000, "so2_10.3c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200006, 0x0, 0x20000, "so2_22.5d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200007, 0x0, 0x20000, "so2_12.5c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "so2_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "so2_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "so2_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "so2_19.12c"),
                });
            definitions["mercsj"] = new Cps1ClassicDefinition(
                "mercsj",
                null,
                new Cps1VideoConfig(0x2c, 0x2a, 0x28, 0x26, 0x24, 0x22),
                0x300000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "so2_36.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "so2_42.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "so2_37.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "so2_43.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "so2_34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "so2_40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "so2_35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "so2_41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "so2_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "so2_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "so2_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "so2_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "so2_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "so2_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "so2_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "so2_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "so2_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "so2_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "so2_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "so2_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "so2_25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "so2_18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "so2_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "so2_33.9f"),
                    Load(RomLoadKind.Graphics64Byte, 0x200000, 0x0, 0x20000, "so2_11.7b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200001, 0x0, 0x20000, "so2_03.7a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200002, 0x0, 0x20000, "so2_15.11b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200003, 0x0, 0x20000, "so2_07.11a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200004, 0x0, 0x20000, "so2_26.8e"),
                    Load(RomLoadKind.Graphics64Byte, 0x200005, 0x0, 0x20000, "so2_19.8c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200006, 0x0, 0x20000, "so2_28.10e"),
                    Load(RomLoadKind.Graphics64Byte, 0x200007, 0x0, 0x20000, "so2_21.10c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "so2_23.13b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "so2_23.13b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "so2_30.12c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "so2_31.13c"),
                });
            definitions["mercsu"] = new Cps1ClassicDefinition(
                "mercsu",
                null,
                new Cps1VideoConfig(0x2c, 0x2a, 0x28, 0x26, 0x24, 0x22),
                0x300000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "so2_30a.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "so2_35a.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "so2_31a.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "so2_36a.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "so2-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "so2-6m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "so2-8m.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "so2-2m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "so2-4m.6a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200000, 0x0, 0x20000, "so2_24.7d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200001, 0x0, 0x20000, "so2_14.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200002, 0x0, 0x20000, "so2_26.9d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200003, 0x0, 0x20000, "so2_16.9c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200004, 0x0, 0x20000, "so2_20.3d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200005, 0x0, 0x20000, "so2_10.3c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200006, 0x0, 0x20000, "so2_22.5d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200007, 0x0, 0x20000, "so2_12.5c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "so2_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "so2_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "so2_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "so2_19.12c"),
                });
            definitions["mercsur1"] = new Cps1ClassicDefinition(
                "mercsur1",
                null,
                new Cps1VideoConfig(0x2c, 0x2a, 0x28, 0x26, 0x24, 0x22),
                0x300000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "so2_30.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "so2_35.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "so2_31.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "so2_36.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "so2-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "so2-6m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "so2-8m.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "so2-2m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "so2-4m.6a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200000, 0x0, 0x20000, "so2_24.7d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200001, 0x0, 0x20000, "so2_14.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200002, 0x0, 0x20000, "so2_26.9d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200003, 0x0, 0x20000, "so2_16.9c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200004, 0x0, 0x20000, "so2_20.3d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200005, 0x0, 0x20000, "so2_10.3c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200006, 0x0, 0x20000, "so2_22.5d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200007, 0x0, 0x20000, "so2_12.5c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "so2_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "so2_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "so2_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "so2_19.12c"),
                });
            definitions["msword"] = new Cps1ClassicDefinition(
                "msword",
                null,
                new Cps1VideoConfig(0x22, 0x24, 0x26, 0x28, 0x2a, 0x2c),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "mse_30.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "mse_35.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "mse_31.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "mse_36.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "ms-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "ms-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "ms-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "ms-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "ms-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "ms_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "ms_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "ms_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "ms_19.12c"),
                });
            definitions["mswordj"] = new Cps1ClassicDefinition(
                "mswordj",
                null,
                new Cps1VideoConfig(0x22, 0x24, 0x26, 0x28, 0x2a, 0x2c),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "msj_36.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "msj_42.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "msj_37.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "msj_43.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "ms_34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "ms_40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "ms_35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "ms_41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "ms_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "ms_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "ms_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "ms_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "ms_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "ms_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "ms_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "ms_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "ms_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "ms_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "ms_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "ms_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "ms_25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "ms_18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "ms_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "ms_33.9f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "ms_23.13b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "ms_23.13b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "ms_30.12c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "ms_31.13c"),
                });
            definitions["mswordr1"] = new Cps1ClassicDefinition(
                "mswordr1",
                null,
                new Cps1VideoConfig(0x22, 0x24, 0x26, 0x28, 0x2a, 0x2c),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "ms_30.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "ms_35.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "ms_31.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "ms_36.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "ms-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "ms-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "ms-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "ms-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "ms-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "ms_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "ms_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "ms_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "ms_19.12c"),
                });
            definitions["mswordu"] = new Cps1ClassicDefinition(
                "mswordu",
                null,
                new Cps1VideoConfig(0x22, 0x24, 0x26, 0x28, 0x2a, 0x2c),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "msu_30.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "msu_35.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "msu_31.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "msu_36.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "ms-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "ms-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "ms-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "ms-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "ms-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "ms_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "ms_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "ms_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "ms_19.12c"),
                });
            definitions["mtwins"] = new Cps1ClassicDefinition(
                "mtwins",
                null,
                new Cps1VideoConfig(0x12, 0x14, 0x16, 0x18, 0x1a, 0x1c),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "che_30.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "che_35.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "che_31.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "che_36.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "ck-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "ck-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "ck-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "ck-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "ck-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "ch_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "ch_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "ch_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "ch_19.12c"),
                });
            definitions["nemo"] = new Cps1ClassicDefinition(
                "nemo",
                null,
                new Cps1VideoConfig(0x02, 0x04, 0x06, 0x08, 0x0a, 0x0c),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "nme_30a.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "nme_35a.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "nme_31a.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "nme_36a.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "nm-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "nm-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "nm-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "nm-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "nm-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "nme_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "nme_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "nme_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "nme_19.12c"),
                });
            definitions["nemoj"] = new Cps1ClassicDefinition(
                "nemoj",
                null,
                new Cps1VideoConfig(0x02, 0x04, 0x06, 0x08, 0x0a, 0x0c),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "nmj_36a.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "nmj_42a.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "nmj_37a.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "nmj_43a.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "nmj_34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "nmj_40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "nmj_35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "nmj_41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "nmj_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "nmj_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "nmj_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "nmj_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "nmj_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "nmj_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "nmj_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "nmj_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "nmj_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "nmj_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "nmj_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "nmj_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "nmj_25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "nmj_18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "nmj_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "nmj_33.9f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "nmj_23.13b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "nmj_23.13b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "nmj_30.12c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "nmj_31.13c"),
                });
            definitions["pang3"] = new Cps1ClassicDefinition(
                "pang3",
                null,
                Cps1VideoConfig.Default,
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "pa3e_17a.11l"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "pa3e_16a.10l"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x100000, "pa3-01m.2c"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x100000, 0x100000, "pa3-01m.2c"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x100000, "pa3-07m.2f"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x100000, 0x100000, "pa3-07m.2f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "pa3_11.11f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "pa3_05.10d"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "pa3_06.11d"),
                });
            definitions["pang3j"] = new Cps1ClassicDefinition(
                "pang3j",
                null,
                Cps1VideoConfig.Default,
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "pa3j_17.11l"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "pa3j_16.10l"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x100000, "pa3-01m.2c"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x100000, 0x100000, "pa3-01m.2c"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x100000, "pa3-07m.2f"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x100000, 0x100000, "pa3-07m.2f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "pa3_11.11f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "pa3_05.10d"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "pa3_06.11d"),
                });
            definitions["pnickj"] = new Cps1ClassicDefinition(
                "pnickj",
                null,
                Cps1VideoConfig.Default,
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "pnij_36.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "pnij_42.12h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "pnij_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "pnij_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "pnij_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "pnij_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "pnij_26.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "pnij_18.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "pnij_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "pnij_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "pnij_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "pnij_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "pnij_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "pnij_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "pnij_27.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "pnij_19.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "pnij_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "pnij_33.9f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "pnij_17.13b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "pnij_17.13b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "pnij_24.12c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "pnij_25.13c"),
                });
            definitions["qad"] = new Cps1ClassicDefinition(
                "qad",
                null,
                new Cps1VideoConfig(0x2c, -1, -1, -1, -1, 0x12),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "qdu_36a.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "qdu_42a.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "qdu_37a.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "qdu_43a.13h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "qd_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "qd_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "qd_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "qd_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "qd_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "qd_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "qd_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "qd_32.8f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "qd_23.13b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "qd_23.13b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "qdu_30.12c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "qdu_31.13c"),
                });
            definitions["qadjr"] = new Cps1ClassicDefinition(
                "qadjr",
                null,
                Cps1VideoConfig.Default,
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "qad_23a.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "qad_22a.7f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "qad_01.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "qad_02.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "qad_03.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "qad_04.6a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "qad_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "qad_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "qad_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "qad_19.12c"),
                });
            definitions["qtono2j"] = new Cps1ClassicDefinition(
                "qtono2j",
                null,
                Cps1VideoConfig.Default,
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "tn2j_30.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "tn2j_37.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "tn2j_31.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "tn2j_38.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "tn2j_28.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "tn2j_35.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "tn2j_29.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "tn2j_36.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "tn2-02m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "tn2-04m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "tn2-01m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "tn2-03m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "tn2-11m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "tn2-13m.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "tn2-10m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "tn2-12m.5c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "tn2j_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "tn2j_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "tn2j_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "tn2j_19.12c"),
                });
            definitions["rockmanj"] = new Cps1ClassicDefinition(
                "rockmanj",
                null,
                Cps1VideoConfig.Default,
                0x800000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "rcm_23a.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "rcm_22a.7f"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "rcm_21a.6f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "rcm_01.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "rcm_02.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "rcm_03.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "rcm_04.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "rcm_05.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "rcm_06.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "rcm_07.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "rcm_08.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "rcm_10.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "rcm_11.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "rcm_12.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "rcm_13.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x600000, 0x0, 0x80000, "rcm_14.7c"),
                    Load(RomLoadKind.Graphics64Word, 0x600002, 0x0, 0x80000, "rcm_15.8c"),
                    Load(RomLoadKind.Graphics64Word, 0x600004, 0x0, 0x80000, "rcm_16.9c"),
                    Load(RomLoadKind.Graphics64Word, 0x600006, 0x0, 0x80000, "rcm_17.10c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "rcm_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x18000, "rcm_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "rcm_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "rcm_19.12c"),
                });
            definitions["sf2"] = new Cps1ClassicDefinition(
                "sf2",
                null,
                new Cps1VideoConfig(0x26, 0x28, 0x2a, 0x2c, 0x2e, 0x30),
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "sf2e_30g.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "sf2e_37g.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "sf2e_31g.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "sf2e_38g.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "sf2e_28g.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "sf2e_35g.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "sf2_29b.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "sf2_36b.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sf2-5m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sf2-7m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sf2-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sf2-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sf2-6m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sf2-8m.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sf2-2m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sf2-4m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sf2-13m.4d"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sf2-15m.6d"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sf2-9m.3d"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sf2-11m.5d"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sf2_9.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sf2_9.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sf2_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sf2_19.12c"),
                });
            definitions["sf2accp2"] = new Cps1ClassicDefinition(
                "sf2accp2",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "sf2ca-23.bin"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "sf2ca-22.bin"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x40000, "sf2ca-21.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sf2ce"] = new Cps1ClassicDefinition(
                "sf2ce",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "s92e_23b.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "s92_22b.7f"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.6f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92-2m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92-4m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92-6m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92-8m.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92-10m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92-12m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92-11m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92-13m.6c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.11a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.11a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.12c"),
                });
            definitions["sf2cejb"] = new Cps1ClassicDefinition(
                "sf2cejb",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "s92j_23b.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "s92j_22b.7f"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.6f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.6c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.12c"),
                });
            definitions["sf2ceua"] = new Cps1ClassicDefinition(
                "sf2ceua",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "s92u_23a.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "s92_22a.7f"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.6f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92-2m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92-4m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92-6m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92-8m.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92-10m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92-12m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92-11m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92-13m.6c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.11a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.11a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.12c"),
                });
            definitions["sf2ceub"] = new Cps1ClassicDefinition(
                "sf2ceub",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "s92u_23b.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "s92_22b.7f"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.6f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92-2m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92-4m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92-6m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92-8m.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92-10m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92-12m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92-11m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92-13m.6c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.11a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.11a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.12c"),
                });
            definitions["sf2ceuc"] = new Cps1ClassicDefinition(
                "sf2ceuc",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "s92u_23c.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "s92_22c.7f"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.6f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92-2m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92-4m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92-6m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92-8m.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92-10m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92-12m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92-11m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92-13m.6c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.11a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.11a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.12c"),
                });
            definitions["sf2eb"] = new Cps1ClassicDefinition(
                "sf2eb",
                null,
                new Cps1VideoConfig(0x14, 0x12, 0x10, 0x0e, 0x0c, 0x0a),
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "sf2e_30b.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "sf2e_37b.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "sf2e_31b.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "sf2e_38b.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "sf2_28b.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "sf2_35b.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "sf2_29b.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "sf2_36b.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sf2-5m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sf2-7m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sf2-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sf2-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sf2-6m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sf2-8m.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sf2-2m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sf2-4m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sf2-13m.4d"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sf2-15m.6d"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sf2-9m.3d"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sf2-11m.5d"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sf2_9.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sf2_9.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sf2_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sf2_19.12c"),
                });
            definitions["sf2hf"] = new Cps1ClassicDefinition(
                "sf2hf",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "s2te_23.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "s2te_22.7f"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s2te_21.6f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92-2m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92-4m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92-6m.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92-8m.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92-10m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92-12m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92-11m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92-13m.6c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.11a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.11a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.12c"),
                });
            definitions["sf2hfj"] = new Cps1ClassicDefinition(
                "sf2hfj",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "s2tj_23.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "s2tj_22.7f"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s2tj_21.6f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s2t_10.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s2t_11.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s2t_12.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s2t_13.6c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.12c"),
                });
            definitions["sf2j"] = new Cps1ClassicDefinition(
                "sf2j",
                null,
                new Cps1VideoConfig(0x22, 0x24, 0x26, 0x28, 0x2a, 0x2c),
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "sf2j30.bin"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "sf2j37.bin"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "sf2j31.bin"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "sf2j38.bin"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "sf2j28.bin"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "sf2j35.bin"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "sf2_29a.bin"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "sf2_36a.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sf2_06.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sf2_08.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sf2_05.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sf2_07.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sf2_15.8c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sf2_17.10c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sf2_14.7c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sf2_16.9c"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sf2_25.8d"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sf2_27.10d"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sf2_24.7d"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sf2_26.9d"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sf2_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sf2_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sf2_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sf2_19.bin"),
                });
            definitions["sf2ja"] = new Cps1ClassicDefinition(
                "sf2ja",
                null,
                new Cps1VideoConfig(0x14, 0x12, 0x10, 0x0e, 0x0c, 0x0a),
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "sf2j_30a.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "sf2j_37a.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "sf2j_31a.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "sf2j_38a.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "sf2j_28a.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "sf2j_35a.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "sf2j_29a.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "sf2j_36a.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sf2_06.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sf2_08.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sf2_05.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sf2_07.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sf2_15.8c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sf2_17.10c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sf2_14.7c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sf2_16.9c"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sf2_25.8d"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sf2_27.10d"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sf2_24.7d"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sf2_26.9d"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sf2j_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sf2j_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sf2j_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sf2j_19.12c"),
                });
            definitions["sf2jc"] = new Cps1ClassicDefinition(
                "sf2jc",
                null,
                new Cps1VideoConfig(0x2c, 0x2a, 0x28, 0x26, 0x24, 0x22),
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "sf2j_30c.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "sf2j_37c.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "sf2j_31c.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "sf2j_38c.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "sf2j_28c.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "sf2j_35c.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "sf2j_29a.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "sf2j_36a.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sf2_06.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sf2_08.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sf2_05.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sf2_07.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sf2_15.8c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sf2_17.10c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sf2_14.7c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sf2_16.9c"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sf2_25.8d"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sf2_27.10d"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sf2_24.7d"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sf2_26.9d"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sf2_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sf2_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sf2j_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sf2j_19.12c"),
                });
            definitions["sf2koryu"] = new Cps1ClassicDefinition(
                "sf2koryu",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x80000, "u222.rom"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x80000, "u196.rom"),
                    Load(RomLoadKind.Byte, 0x100000, 0x0, 0x20000, "u221.rom"),
                    Load(RomLoadKind.Byte, 0x100001, 0x0, 0x20000, "u195.rom"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sf2m1"] = new Cps1ClassicDefinition(
                "sf2m1",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x80000, "222e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x80000, "196e"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sf2m2"] = new Cps1ClassicDefinition(
                "sf2m2",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x80000, "ch222esp"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x80000, "ch196esp"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sf2m3"] = new Cps1ClassicDefinition(
                "sf2m3",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x80000, "u222chp"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x80000, "u196chp"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sf2m4"] = new Cps1ClassicDefinition(
                "sf2m4",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x80000, "u222ne"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x80000, "u196ne"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sf2m5"] = new Cps1ClassicDefinition(
                "sf2m5",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x80000, "u222"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x80000, "u196"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sf2m6"] = new Cps1ClassicDefinition(
                "sf2m6",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x80000, "27c040.u222"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x80000, "27c040.u196"),
                    Load(RomLoadKind.Byte, 0x100000, 0x0, 0x20000, "27c010.u221"),
                    Load(RomLoadKind.Byte, 0x100001, 0x0, 0x20000, "27c010.u195"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "ycecmkr001.u70"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x80000, 0x80000, "ycecmkr001.u70"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "ycecmkr003.u69"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x80000, 0x80000, "ycecmkr003.u69"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "ycecmkr002.u68"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x80000, 0x80000, "ycecmkr002.u68"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "ycecdwc011.u64"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x80000, 0x80000, "ycecdwc011.u64"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "ycecdwc012.u19"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x80000, 0x80000, "ycecdwc012.u19"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "ycecdwc013.u18"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x80000, 0x80000, "ycecdwc013.u18"),
                    Load(RomLoadKind.Graphics64Byte, 0x400004, 0x0, 0x10000, "grp1.u31"),
                    Load(RomLoadKind.Graphics64Byte, 0x400000, 0x10000, 0x10000, "grp1.u31"),
                    Load(RomLoadKind.Graphics64Byte, 0x400006, 0x0, 0x10000, "grp3.u29"),
                    Load(RomLoadKind.Graphics64Byte, 0x400002, 0x10000, 0x10000, "grp3.u29"),
                    Load(RomLoadKind.Graphics64Byte, 0x400005, 0x0, 0x10000, "grp2.u30"),
                    Load(RomLoadKind.Graphics64Byte, 0x400001, 0x10000, 0x10000, "grp2.u30"),
                    Load(RomLoadKind.Graphics64Byte, 0x400007, 0x0, 0x10000, "grp4.u28"),
                    Load(RomLoadKind.Graphics64Byte, 0x400003, 0x10000, 0x10000, "grp4.u28"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sound.u191"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sound.u191"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x40000, "voice.u210"),
                });
            definitions["sf2m7"] = new Cps1ClassicDefinition(
                "sf2m7",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x40000, "u222-2i"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x40000, "u196-2i"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x40000, "u222-2s"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x40000, "u196-2s"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sf2rb"] = new Cps1ClassicDefinition(
                "sf2rb",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sf2rb2"] = new Cps1ClassicDefinition(
                "sf2rb2",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "27.bin"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "31.bin"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "26.bin"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "30.bin"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "25.bin"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "29.bin"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "24.bin"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "28.bin"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sf2rb3"] = new Cps1ClassicDefinition(
                "sf2rb3",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "sf2_ce_rb.23"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "sf2_ce_rb.22"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "s92_21a.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sf2red"] = new Cps1ClassicDefinition(
                "sf2red",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "sf2red.23"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "sf2red.22"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "sf2red.21"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sf2ua"] = new Cps1ClassicDefinition(
                "sf2ua",
                null,
                new Cps1VideoConfig(0x14, 0x12, 0x10, 0x0e, 0x0c, 0x0a),
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "sf2u_30a.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "sf2u_37a.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "sf2u_31a.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "sf2u_38a.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "sf2u_28a.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "sf2u_35a.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "sf2_29b.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "sf2_36b.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sf2-5m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sf2-7m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sf2-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sf2-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sf2-6m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sf2-8m.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sf2-2m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sf2-4m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sf2-13m.4d"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sf2-15m.6d"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sf2-9m.3d"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sf2-11m.5d"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sf2_9.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sf2_9.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sf2_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sf2_19.12c"),
                });
            definitions["sf2ub"] = new Cps1ClassicDefinition(
                "sf2ub",
                null,
                new Cps1VideoConfig(0x14, 0x12, 0x10, 0x0e, 0x0c, 0x0a),
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "sf2u_30b.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "sf2u_37b.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "sf2u_31b.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "sf2u_38b.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "sf2u_28b.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "sf2u_35b.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "sf2_29b.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "sf2_36b.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sf2-5m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sf2-7m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sf2-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sf2-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sf2-6m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sf2-8m.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sf2-2m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sf2-4m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sf2-13m.4d"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sf2-15m.6d"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sf2-9m.3d"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sf2-11m.5d"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sf2_9.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sf2_9.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sf2_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sf2_19.12c"),
                });
            definitions["sf2ud"] = new Cps1ClassicDefinition(
                "sf2ud",
                null,
                new Cps1VideoConfig(0x28, 0x2a, 0x2c, 0x2e, 0x30, 0x32),
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "sf2u_30d.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "sf2u_37d.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "sf2u_31d.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "sf2u_38d.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "sf2u_28d.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "sf2u_35d.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "sf2_29b.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "sf2_36b.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sf2-5m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sf2-7m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sf2-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sf2-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sf2-6m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sf2-8m.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sf2-2m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sf2-4m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sf2-13m.4d"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sf2-15m.6d"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sf2-9m.3d"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sf2-11m.5d"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sf2_9.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sf2_9.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sf2_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sf2_19.12c"),
                });
            definitions["sf2ue"] = new Cps1ClassicDefinition(
                "sf2ue",
                null,
                new Cps1VideoConfig(0x1c, 0x1a, 0x18, 0x16, 0x14, 0x12),
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "sf2u_30e.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "sf2u_37e.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "sf2u_31e.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "sf2u_38e.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "sf2u_28e.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "sf2u_35e.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "sf2_29b.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "sf2_36b.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sf2-5m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sf2-7m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sf2-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sf2-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sf2-6m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sf2-8m.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sf2-2m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sf2-4m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sf2-13m.4d"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sf2-15m.6d"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sf2-9m.3d"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sf2-11m.5d"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sf2_9.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sf2_9.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sf2_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sf2_19.12c"),
                });
            definitions["sf2uf"] = new Cps1ClassicDefinition(
                "sf2uf",
                null,
                new Cps1VideoConfig(0x02, 0x04, 0x06, 0x08, 0x0a, 0x0c),
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "sf2u_30f.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "sf2u_37f.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "sf2u_31f.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "sf2u_38f.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "sf2u_28f.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "sf2u_35f.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "sf2_29b.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "sf2_36b.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sf2-5m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sf2-7m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sf2-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sf2-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sf2-6m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sf2-8m.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sf2-2m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sf2-4m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sf2-13m.4d"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sf2-15m.6d"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sf2-9m.3d"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sf2-11m.5d"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sf2_9.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sf2_9.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sf2_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sf2_19.12c"),
                });
            definitions["sf2ui"] = new Cps1ClassicDefinition(
                "sf2ui",
                null,
                new Cps1VideoConfig(0x12, 0x14, 0x16, 0x18, 0x1a, 0x1c),
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "sf2u_30i.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "sf2u_37i.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "sf2u_31i.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "sf2u_38i.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "sf2u_28i.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "sf2u_35i.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "sf2_29b.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "sf2_36b.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sf2-5m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sf2-7m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sf2-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sf2-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sf2-6m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sf2-8m.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sf2-2m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sf2-4m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sf2-13m.4d"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sf2-15m.6d"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sf2-9m.3d"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sf2-11m.5d"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sf2_9.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sf2_9.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sf2_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sf2_19.12c"),
                });
            definitions["sf2uk"] = new Cps1ClassicDefinition(
                "sf2uk",
                null,
                new Cps1VideoConfig(0x14, 0x12, 0x10, 0x0e, 0x0c, 0x0a),
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "sf2u_30k.11e"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "sf2u_37k.11f"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "sf2u_31k.12e"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "sf2u_38k.12f"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "sf2u_28k.9e"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "sf2u_35k.9f"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "sf2u_29a.10e"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "sf2u_36a.10f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sf2-5m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sf2-7m.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sf2-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sf2-3m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sf2-6m.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sf2-8m.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sf2-2m.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sf2-4m.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sf2-13m.4d"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sf2-15m.6d"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sf2-9m.3d"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sf2-11m.5d"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sf2_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sf2_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sf2_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sf2_19.12c"),
                });
            definitions["sf2v004"] = new Cps1ClassicDefinition(
                "sf2v004",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "sf2v004.23"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "sf2v004.22"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "sf2red.21"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sf2yyc"] = new Cps1ClassicDefinition(
                "sf2yyc",
                null,
                Cps1VideoConfig.Default,
                0x600000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x80000, "b12.rom"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x80000, "b14.rom"),
                    Load(RomLoadKind.Byte, 0x100000, 0x0, 0x20000, "b11.rom"),
                    Load(RomLoadKind.Byte, 0x100001, 0x0, 0x20000, "b13.rom"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "s92_01.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "s92_02.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "s92_03.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "s92_04.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "s92_05.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "s92_06.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "s92_07.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "s92_08.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "s92_10.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "s92_11.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "s92_12.bin"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "s92_13.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "s92_09.bin"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "s92_09.bin"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "s92_18.bin"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "s92_19.bin"),
                });
            definitions["sfach"] = new Cps1ClassicDefinition(
                "sfach",
                null,
                Cps1VideoConfig.Default,
                0x800000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "sfach23"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "sfza22"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "sfzch21"),
                    Load(RomLoadKind.WordSwap, 0x180000, 0x0, 0x80000, "sfza20"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sfz01"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sfz02"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sfz03"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sfz04"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sfz05"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sfz06"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sfz07"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sfz08"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sfz10"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sfz11"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sfz12"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sfz13"),
                    Load(RomLoadKind.Graphics64Word, 0x600000, 0x0, 0x80000, "sfz14"),
                    Load(RomLoadKind.Graphics64Word, 0x600002, 0x0, 0x80000, "sfz15"),
                    Load(RomLoadKind.Graphics64Word, 0x600004, 0x0, 0x80000, "sfz16"),
                    Load(RomLoadKind.Graphics64Word, 0x600006, 0x0, 0x80000, "sfz17"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sfz09"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sfz09"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sfz18"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sfz19"),
                });
            definitions["sfzch"] = new Cps1ClassicDefinition(
                "sfzch",
                null,
                Cps1VideoConfig.Default,
                0x800000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "sfzch23"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "sfza22"),
                    Load(RomLoadKind.WordSwap, 0x100000, 0x0, 0x80000, "sfzch21"),
                    Load(RomLoadKind.WordSwap, 0x180000, 0x0, 0x80000, "sfza20"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sfz_01.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sfz_02.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sfz_03.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sfz_04.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sfz_05.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sfz_06.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sfz_07.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sfz_08.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x400000, 0x0, 0x80000, "sfz_10.3c"),
                    Load(RomLoadKind.Graphics64Word, 0x400002, 0x0, 0x80000, "sfz_11.4c"),
                    Load(RomLoadKind.Graphics64Word, 0x400004, 0x0, 0x80000, "sfz_12.5c"),
                    Load(RomLoadKind.Graphics64Word, 0x400006, 0x0, 0x80000, "sfz_13.6c"),
                    Load(RomLoadKind.Graphics64Word, 0x600000, 0x0, 0x80000, "sfz_14.7c"),
                    Load(RomLoadKind.Graphics64Word, 0x600002, 0x0, 0x80000, "sfz_15.8c"),
                    Load(RomLoadKind.Graphics64Word, 0x600004, 0x0, 0x80000, "sfz_16.9c"),
                    Load(RomLoadKind.Graphics64Word, 0x600006, 0x0, 0x80000, "sfz_17.10c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sfz_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sfz_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sfz_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sfz_19.12c"),
                });
            definitions["strider"] = new Cps1ClassicDefinition(
                "strider",
                null,
                new Cps1VideoConfig(0x26, 0x28, 0x2a, 0x2c, 0x2e, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "30.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "35.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "31.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "36.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "st-14.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "st-2.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "st-11.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "st-5.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "st-9.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "st-1.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "st-10.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "st-4.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "st-8.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "19.12c"),
                });
            definitions["striderj"] = new Cps1ClassicDefinition(
                "striderj",
                null,
                new Cps1VideoConfig(0x26, 0x28, 0x2a, 0x2c, 0x2e, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "sth_36.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "sth_42.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "sth_37.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "sth_43.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "sth_34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "sth_40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "sth_35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "sth_41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "sth_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "sth_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "sth_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "sth_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "sth_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "sth_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "sth_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "sth_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "sth_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "sth_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "sth_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "sth_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "sth_25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "sth_18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "sth_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "sth_33.9f"),
                    Load(RomLoadKind.Graphics64Byte, 0x200000, 0x0, 0x20000, "sth_11.7b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200001, 0x0, 0x20000, "sth_03.7a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200002, 0x0, 0x20000, "sth_15.11b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200003, 0x0, 0x20000, "sth_07.11a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200004, 0x0, 0x20000, "sth_26.8e"),
                    Load(RomLoadKind.Graphics64Byte, 0x200005, 0x0, 0x20000, "sth_19.8c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200006, 0x0, 0x20000, "sth_28.10e"),
                    Load(RomLoadKind.Graphics64Byte, 0x200007, 0x0, 0x20000, "sth_21.10c"),
                    Load(RomLoadKind.Graphics64Byte, 0x300000, 0x0, 0x20000, "sth_12.8b"),
                    Load(RomLoadKind.Graphics64Byte, 0x300001, 0x0, 0x20000, "sth_04.8a"),
                    Load(RomLoadKind.Graphics64Byte, 0x300002, 0x0, 0x20000, "sth_16.12b"),
                    Load(RomLoadKind.Graphics64Byte, 0x300003, 0x0, 0x20000, "sth_08.12a"),
                    Load(RomLoadKind.Graphics64Byte, 0x300004, 0x0, 0x20000, "sth_27.9e"),
                    Load(RomLoadKind.Graphics64Byte, 0x300005, 0x0, 0x20000, "sth_20.9c"),
                    Load(RomLoadKind.Graphics64Byte, 0x300006, 0x0, 0x20000, "sth_29.11e"),
                    Load(RomLoadKind.Graphics64Byte, 0x300007, 0x0, 0x20000, "sth_22.11c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sth_23.13c"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sth_23.13c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sth_30.12e"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sth_31.13e"),
                });
            definitions["striderjr"] = new Cps1ClassicDefinition(
                "striderjr",
                null,
                Cps1VideoConfig.Default,
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "sthj_23.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "sthj_22.7f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "sth_01.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "sth_02.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "sth_03.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "sth_04.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "sth_05.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "sth_06.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "sth_07.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "sth_08.10a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "sth_09.12a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "sth_09.12a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "sth_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "sth_19.12c"),
                });
            definitions["striderua"] = new Cps1ClassicDefinition(
                "striderua",
                null,
                new Cps1VideoConfig(0x26, 0x28, 0x2a, 0x2c, 0x2e, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "30.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "35.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "31.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "36.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "st-14.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "st-2.8a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "st-11.10a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "st-5.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "st-9.6a"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "st-1.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "st-10.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "st-4.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "st-8.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "19.12c"),
                });
            definitions["unsquad"] = new Cps1ClassicDefinition(
                "unsquad",
                null,
                new Cps1VideoConfig(0x26, 0x28, 0x2a, 0x2c, 0x2e, 0x30),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "aru_30.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "aru_35.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "aru_31.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "aru_36.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "ar-32m.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "ar-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "ar-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "ar-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "ar-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "ar_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "ar_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "aru_18.11c"),
                });
            definitions["varth"] = new Cps1ClassicDefinition(
                "varth",
                null,
                new Cps1VideoConfig(0x2e, 0x26, 0x30, 0x28, 0x32, 0x2a),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "vae_30b.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "vae_35b.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "vae_31b.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "vae_36b.12h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "vae_28b.9f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "vae_33b.9h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "vae_29b.10f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "vae_34b.10h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "va-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "va-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "va-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "va-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "va_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "va_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "va_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "va_19.12c"),
                });
            definitions["varthj"] = new Cps1ClassicDefinition(
                "varthj",
                null,
                new Cps1VideoConfig(0x20, 0x2e, 0x2c, 0x2a, 0x28, 0x30),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "vaj_36b.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "vaj_42b.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "vaj_37b.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "vaj_43b.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "vaj_34b.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "vaj_40b.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "vaj_35b.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "vaj_41b.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "va_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "va_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "va_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "va_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "va_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "va_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "va_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "va_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "va_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "va_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "va_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "va_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "va_25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "va_18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "va_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "va_33.9f"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "va_23.13c"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "va_23.13c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "va_30.12e"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "va_31.13e"),
                });
            definitions["varthr1"] = new Cps1ClassicDefinition(
                "varthr1",
                null,
                new Cps1VideoConfig(0x2e, 0x26, 0x30, 0x28, 0x32, 0x2a),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "vae_30a.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "vae_35a.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "vae_31a.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "vae_36a.12h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "vae_28a.9f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "vae_33a.9h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "vae_29a.10f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "vae_34a.10h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "va-5m.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "va-7m.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "va-1m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "va-3m.5a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "va_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "va_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "va_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "va_19.12c"),
                });
            definitions["varthu"] = new Cps1ClassicDefinition(
                "varthu",
                null,
                new Cps1VideoConfig(0x2e, 0x26, 0x30, 0x28, 0x32, 0x2a),
                0x200000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "vau_23a.8f"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "vau_22a.7f"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "va-5m.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "va-7m.5a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "va-1m.4a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "va-3m.6a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "va_09.11a"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "va_09.11a"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "va_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "va_19.12c"),
                });
            definitions["willow"] = new Cps1ClassicDefinition(
                "willow",
                null,
                new Cps1VideoConfig(0x30, 0x2e, 0x2c, 0x2a, 0x28, 0x26),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "wle_30.11f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "wle_35.11h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "wlu_31.12f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "wlu_36.12h"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "wlm-32.8h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "wlm-7.7a"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "wlm-5.9a"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "wlm-3.3a"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "wlm-1.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200000, 0x0, 0x20000, "wl_24.7d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200001, 0x0, 0x20000, "wl_14.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200002, 0x0, 0x20000, "wl_26.9d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200003, 0x0, 0x20000, "wl_16.9c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200004, 0x0, 0x20000, "wl_20.3d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200005, 0x0, 0x20000, "wl_10.3c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200006, 0x0, 0x20000, "wl_22.5d"),
                    Load(RomLoadKind.Graphics64Byte, 0x200007, 0x0, 0x20000, "wl_12.5c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "wl_09.12b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "wl_09.12b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "wl_18.11c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "wl_19.12c"),
                });
            definitions["willowj"] = new Cps1ClassicDefinition(
                "willowj",
                null,
                new Cps1VideoConfig(0x30, 0x2e, 0x2c, 0x2a, 0x28, 0x26),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "wl_36.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "wl_42.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "wl_37.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "wl_43.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "wl_34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "wl_40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "wl_35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "wl_41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "wl_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "wl_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "wl_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "wl_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "wl_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "wl_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "wl_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "wl_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "wl_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "wl_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "wl_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "wl_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "wl_25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "wl_18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "wl_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "wl_33.9f"),
                    Load(RomLoadKind.Graphics64Byte, 0x200000, 0x0, 0x20000, "wl_11.7b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200001, 0x0, 0x20000, "wl_03.7a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200002, 0x0, 0x20000, "wl_15.11b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200003, 0x0, 0x20000, "wl_07.11a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200004, 0x0, 0x20000, "wl_26.8e"),
                    Load(RomLoadKind.Graphics64Byte, 0x200005, 0x0, 0x20000, "wl_19.8c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200006, 0x0, 0x20000, "wl_28.10e"),
                    Load(RomLoadKind.Graphics64Byte, 0x200007, 0x0, 0x20000, "wl_21.10c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "wl_23.13c"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "wl_23.13c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "wl_30.12e"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "wl_31.13e"),
                });
            definitions["wofhfh"] = new Cps1ClassicDefinition(
                "wofhfh",
                null,
                Cps1VideoConfig.Default,
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.WordSwap, 0x0, 0x0, 0x80000, "23"),
                    Load(RomLoadKind.WordSwap, 0x80000, 0x0, 0x80000, "22"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Word, 0x0, 0x0, 0x80000, "1"),
                    Load(RomLoadKind.Graphics64Word, 0x2, 0x0, 0x80000, "2"),
                    Load(RomLoadKind.Graphics64Word, 0x4, 0x0, 0x80000, "3"),
                    Load(RomLoadKind.Graphics64Word, 0x6, 0x0, 0x80000, "4"),
                    Load(RomLoadKind.Graphics64Word, 0x200000, 0x0, 0x80000, "5"),
                    Load(RomLoadKind.Graphics64Word, 0x200002, 0x0, 0x80000, "6"),
                    Load(RomLoadKind.Graphics64Word, 0x200004, 0x0, 0x80000, "7"),
                    Load(RomLoadKind.Graphics64Word, 0x200006, 0x0, 0x80000, "8"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "9"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x18000, "9"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "18"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "19"),
                });
            definitions["wonder3"] = new Cps1ClassicDefinition(
                "wonder3",
                null,
                new Cps1VideoConfig(0x28, 0x26, 0x24, 0x22, 0x20, 0x30),
                0x400000,
                0x40000,
                new[]
                {
                    Load(RomLoadKind.Byte, 0x0, 0x0, 0x20000, "rtj_36.12f"),
                    Load(RomLoadKind.Byte, 0x1, 0x0, 0x20000, "rtj_42.12h"),
                    Load(RomLoadKind.Byte, 0x40000, 0x0, 0x20000, "rtj_37.13f"),
                    Load(RomLoadKind.Byte, 0x40001, 0x0, 0x20000, "rtj_43.13h"),
                    Load(RomLoadKind.Byte, 0x80000, 0x0, 0x20000, "rt_34.10f"),
                    Load(RomLoadKind.Byte, 0x80001, 0x0, 0x20000, "rt_40.10h"),
                    Load(RomLoadKind.Byte, 0xc0000, 0x0, 0x20000, "rtj_35.11f"),
                    Load(RomLoadKind.Byte, 0xc0001, 0x0, 0x20000, "rtj_41.11h"),
                },
                new[]
                {
                    Load(RomLoadKind.Graphics64Byte, 0x0, 0x0, 0x20000, "rt_09.4b"),
                    Load(RomLoadKind.Graphics64Byte, 0x1, 0x0, 0x20000, "rt_01.4a"),
                    Load(RomLoadKind.Graphics64Byte, 0x2, 0x0, 0x20000, "rt_13.9b"),
                    Load(RomLoadKind.Graphics64Byte, 0x3, 0x0, 0x20000, "rt_05.9a"),
                    Load(RomLoadKind.Graphics64Byte, 0x4, 0x0, 0x20000, "rt_24.5e"),
                    Load(RomLoadKind.Graphics64Byte, 0x5, 0x0, 0x20000, "rt_17.5c"),
                    Load(RomLoadKind.Graphics64Byte, 0x6, 0x0, 0x20000, "rt_38.8h"),
                    Load(RomLoadKind.Graphics64Byte, 0x7, 0x0, 0x20000, "rt_32.8f"),
                    Load(RomLoadKind.Graphics64Byte, 0x100000, 0x0, 0x20000, "rt_10.5b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100001, 0x0, 0x20000, "rt_02.5a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100002, 0x0, 0x20000, "rt_14.10b"),
                    Load(RomLoadKind.Graphics64Byte, 0x100003, 0x0, 0x20000, "rt_06.10a"),
                    Load(RomLoadKind.Graphics64Byte, 0x100004, 0x0, 0x20000, "rt_25.7e"),
                    Load(RomLoadKind.Graphics64Byte, 0x100005, 0x0, 0x20000, "rt_18.7c"),
                    Load(RomLoadKind.Graphics64Byte, 0x100006, 0x0, 0x20000, "rt_39.9h"),
                    Load(RomLoadKind.Graphics64Byte, 0x100007, 0x0, 0x20000, "rt_33.9f"),
                    Load(RomLoadKind.Graphics64Byte, 0x200000, 0x0, 0x20000, "rt_11.7b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200001, 0x0, 0x20000, "rt_03.7a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200002, 0x0, 0x20000, "rt_15.11b"),
                    Load(RomLoadKind.Graphics64Byte, 0x200003, 0x0, 0x20000, "rt_07.11a"),
                    Load(RomLoadKind.Graphics64Byte, 0x200004, 0x0, 0x20000, "rt_26.8e"),
                    Load(RomLoadKind.Graphics64Byte, 0x200005, 0x0, 0x20000, "rt_19.8c"),
                    Load(RomLoadKind.Graphics64Byte, 0x200006, 0x0, 0x20000, "rt_28.10e"),
                    Load(RomLoadKind.Graphics64Byte, 0x200007, 0x0, 0x20000, "rt_21.10c"),
                    Load(RomLoadKind.Graphics64Byte, 0x300000, 0x0, 0x20000, "rt_12.8b"),
                    Load(RomLoadKind.Graphics64Byte, 0x300001, 0x0, 0x20000, "rt_04.8a"),
                    Load(RomLoadKind.Graphics64Byte, 0x300002, 0x0, 0x20000, "rt_16.12b"),
                    Load(RomLoadKind.Graphics64Byte, 0x300003, 0x0, 0x20000, "rt_08.12a"),
                    Load(RomLoadKind.Graphics64Byte, 0x300004, 0x0, 0x20000, "rt_27.9e"),
                    Load(RomLoadKind.Graphics64Byte, 0x300005, 0x0, 0x20000, "rt_20.9c"),
                    Load(RomLoadKind.Graphics64Byte, 0x300006, 0x0, 0x20000, "rt_29.11e"),
                    Load(RomLoadKind.Graphics64Byte, 0x300007, 0x0, 0x20000, "rt_22.11c"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x8000, "rt_23.13b"),
                    Load(RomLoadKind.Raw, 0x10000, 0x8000, 0x8000, "rt_23.13b"),
                },
                new[]
                {
                    Load(RomLoadKind.Raw, 0x0, 0x0, 0x20000, "rt_30.12c"),
                    Load(RomLoadKind.Raw, 0x20000, 0x0, 0x20000, "rt_31.13c"),
                });
            return definitions;
        }

        private static Dictionary<string, uint> BuildKnownRomCrcs()
        {
            return new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
            {
                ["00.14a"] = 0x3bc962eau,
                ["01 rk098"] = 0x4296de4du,
                ["01.4a"] = 0x187b2886u,
                ["01.bin"] = 0xbeade53fu,
                ["01.u10"] = 0x7d15b9d7u,
                ["02 rk037"] = 0x68ca7fceu,
                ["02.5a"] = 0xcc83c02fu,
                ["02.bin"] = 0x7f162009u,
                ["02.u11"] = 0xf706b466u,
                ["03 rk097"] = 0x16cf11d0u,
                ["03.7a"] = 0x7bf51337u,
                ["03.bin"] = 0xa4823a1bu,
                ["03.u12"] = 0x7e28974eu,
                ["04 rk033"] = 0x9f46f926u,
                ["04.8a"] = 0x4951bc0fu,
                ["04.bin"] = 0x13ea1c44u,
                ["04.u13"] = 0x44cd5e95u,
                ["05 rk116"] = 0x4c161fa9u,
                ["05.9a"] = 0x339378b8u,
                ["05.bin"] = 0xa505621eu,
                ["05.u14"] = 0x475a5ef1u,
                ["06 rk077"] = 0xec949f8cu,
                ["06.10a"] = 0x6f9edd75u,
                ["06.bin"] = 0x23775344u,
                ["06.u15"] = 0xfb68be4eu,
                ["07.11a"] = 0xe04af054u,
                ["07.bin"] = 0xde6271fbu,
                ["07.u16"] = 0x83b3fa5eu,
                ["08.12a"] = 0xb475d4e9u,
                ["08.bin"] = 0x81c9550fu,
                ["08.u17"] = 0x7e0ca927u,
                ["09.12b"] = 0x08d63519u,
                ["09.4a"] = 0xae24bb19u,
                ["09.4b"] = 0xc3e83c69u,
                ["09.bin"] = 0x59ccd474u,
                ["09.u20"] = 0x6e36e963u,
                ["0e.30.e11"] = 0x005b54ccu,
                ["1"] = 0x0861dff3u,
                ["1-9207.040"] = 0x7f59e24cu,
                ["1-b-yf197.07"] = 0x22c9cc8eu,
                ["1-d-yf207.12"] = 0x57213be8u,
                ["1-e-yg003.02"] = 0x14b84312u,
                ["1-h-yg010.10"] = 0xb5548f17u,
                ["1-i-yf224.03"] = 0xc1befaa8u,
                ["1-j-yf213.09"] = 0x994bfa58u,
                ["1-k-yf036.06"] = 0x0627c831u,
                ["1-prg-27c4001.bin"] = 0x8938a029u,
                ["1.096"] = 0xad468e07u,
                ["1.1"] = 0xd3523f34u,
                ["1.2c"] = 0x22c934c4u,
                ["1.4m"] = 0xd2ae67a8u,
                ["1.6f"] = 0x65c2c719u,
                ["1.7f"] = 0x99f1cca4u,
                ["1.8f"] = 0x19fffa37u,
                ["1.a3"] = 0x877b2b18u,
                ["1.amf"] = 0xbeade53fu,
                ["1.bin"] = 0xe58db26cu,
                ["1.ic171"] = 0xd88abbceu,
                ["1.ic26"] = 0x17d5ba8au,
                ["1.ic28"] = 0xd5bee9ccu,
                ["1.rk"] = 0xa4823a1bu,
                ["1.stt"] = 0xbeade53fu,
                ["1.u18"] = 0x9f4017fbu,
                ["1.u195"] = 0x924c6ce2u,
                ["1.u65"] = 0xfe32af5du,
                ["10"] = 0xeeec6183u,
                ["10-7d24.040"] = 0x90c93dd2u,
                ["10.10"] = 0x17174249u,
                ["10.13f"] = 0xff7e41d9u,
                ["10.4b"] = 0xbcc0f28cu,
                ["10.5b"] = 0xff28f8d0u,
                ["10.5f"] = 0x026d0cd2u,
                ["10.amf"] = 0x8fea8384u,
                ["10.bin"] = 0x8c2fca3cu,
                ["10.c3"] = 0xeb48f7f2u,
                ["10.ic88"] = 0xef3f5be8u,
                ["10.ic90"] = 0x896eaf48u,
                ["10.u125"] = 0xda529062u,
                ["10.u21"] = 0x79673708u,
                ["10.u221"] = 0xd1707134u,
                ["10.u69"] = 0xe051b2e9u,
                ["11"] = 0x9eef3507u,
                ["11-4171.040"] = 0x219fd7e2u,
                ["11.11"] = 0xbf14418au,
                ["11.14f"] = 0xc9a6b319u,
                ["11.4c"] = 0x37c9b6c6u,
                ["11.7b"] = 0x29eaf490u,
                ["11.bin"] = 0x6e8c98d8u,
                ["11.c4"] = 0xff36859eu,
                ["11.ic84"] = 0xcea6d1d6u,
                ["11.ic91"] = 0x054cd5c4u,
                ["11.u138"] = 0x2007b45au,
                ["11.u22"] = 0x29194b90u,
                ["11.u32"] = 0xcb1423a2u,
                ["12"] = 0xafeba416u,
                ["12-f56b.040"] = 0xefc17c9au,
                ["12.12"] = 0x490440b2u,
                ["12.4d"] = 0xda088d61u,
                ["12.8b"] = 0x38652339u,
                ["12.bin"] = 0x971903fau,
                ["12.c5"] = 0x5d21d8b3u,
                ["12.ic83"] = 0x91a9a05du,
                ["12.ic92"] = 0x87e069e8u,
                ["12.u137"] = 0x7dff1790u,
                ["12.u23"] = 0x3c4dfb4fu,
                ["13"] = 0x9fb11869u,
                ["13.13"] = 0xcef1aab8u,
                ["13.4e"] = 0x3f70dd37u,
                ["13.9b"] = 0x0273d87du,
                ["13.bin"] = 0x26f09d38u,
                ["13.c6"] = 0xbc937c96u,
                ["13.ic87"] = 0xe57f6db9u,
                ["13.ic89"] = 0x305dd72au,
                ["13.u130"] = 0x1054d40cu,
                ["13.u24"] = 0x0c73a1c7u,
                ["13.u73"] = 0x531ea745u,
                ["14"] = 0x7ca73780u,
                ["14.10b"] = 0x18fb232cu,
                ["14.13g"] = 0x80c3a813u,
                ["14.14"] = 0x344a8270u,
                ["14.4f"] = 0x20f85c03u,
                ["14.7c"] = 0xb7d04e8bu,
                ["14.bin"] = 0x672d4f85u,
                ["14.c7"] = 0x5db24ca7u,
                ["14.ic85"] = 0x7d9f1a67u,
                ["14.ic94"] = 0x5dfb44d1u,
                ["14.u129"] = 0x2e32610bu,
                ["14.u25"] = 0xd2b764a9u,
                ["14.u72"] = 0x654904c8u,
                ["15.11b"] = 0xd36cdb91u,
                ["15.14g"] = 0x524f920eu,
                ["15.15"] = 0x397725dcu,
                ["15.4g"] = 0x3c2a212au,
                ["15.8c"] = 0x005f000bu,
                ["15.bin"] = 0x00983914u,
                ["15.c8"] = 0x9a96be48u,
                ["15.ic85"] = 0x7d9f1a67u,
                ["15.ic94"] = 0x5dfb44d1u,
                ["15.u26"] = 0xa933434fu,
                ["15.u71"] = 0x42774e37u,
                ["16.12b"] = 0x381608aeu,
                ["16.16"] = 0xb991ad91u,
                ["16.4h"] = 0xf187ba1cu,
                ["16.9c"] = 0x6b4713b4u,
                ["16.bin"] = 0xd49d2eb0u,
                ["16.c9"] = 0x1fd98ad0u,
                ["16.ic87"] = 0xe57f6db9u,
                ["16.ic89"] = 0x305dd72au,
                ["16.u27"] = 0x3e293482u,
                ["16.u70"] = 0x8d3fd82cu,
                ["17.10c"] = 0xb9441519u,
                ["17.17"] = 0x0b538062u,
                ["17.5c"] = 0x2e2f8320u,
                ["17.bin"] = 0x21652214u,
                ["17.c10"] = 0xa917a922u,
                ["17.ic83"] = 0x91a9a05du,
                ["17.ic92"] = 0x87e069e8u,
                ["17_29.10e"] = 0x8830b54du,
                ["17_30.11e"] = 0xd3cd6d18u,
                ["17_36.10f"] = 0x3f13ada3u,
                ["17_37.11f"] = 0xe892716eu,
                ["18"] = 0x9997a34fu,
                ["18.010"] = 0x375c66e7u,
                ["18.11c"] = 0x4386bc80u,
                ["18.18"] = 0x9b14f7edu,
                ["18.7a"] = 0xd34e271au,
                ["18.7c"] = 0x1833f932u,
                ["18.bin"] = 0x73a0c11cu,
                ["19"] = 0xe95270acu,
                ["19.010"] = 0xeb5ca884u,
                ["19.12c"] = 0x444536d7u,
                ["19.19"] = 0x3ad62311u,
                ["19.7b"] = 0x2a40166au,
                ["19.8c"] = 0x7114e5c6u,
                ["19.bin"] = 0xb3aa1f48u,
                ["196e"] = 0x88cc38a3u,
                ["1_atf20v8.u25"] = 0xcd99ca47u,
                ["1_gal20v8.ic169"] = 0xe5cf9f53u,
                ["1_palce16v8.bin"] = 0xbac89609u,
                ["1_palce20v8.bin"] = 0xa5078c38u,
                ["1a_yf087.bin"] = 0xba529b4fu,
                ["1b_yf082.bin"] = 0x22c9cc8eu,
                ["1c_yf088.bin"] = 0x4b1b33a8u,
                ["1d_yf028.bin"] = 0x57213be8u,
                ["1e_yf111.bin"] = 0x14b84312u,
                ["1f_yf085.bin"] = 0x2c7e2229u,
                ["1g_yf002.bin"] = 0x5e9cd89au,
                ["1h_yf115.bin"] = 0xb5548f17u,
                ["1i_yf038.bin"] = 0xc1befaa8u,
                ["1j_yf117.bin"] = 0x994bfa58u,
                ["1k.31.e13"] = 0xea78f9b4u,
                ["1k_ye039.bin"] = 0x0627c831u,
                ["1l_ye040.bin"] = 0x3e66ad9du,
                ["1mre125.u70"] = 0xbaa0f81fu,
                ["2"] = 0x2f010023u,
                ["2-6c41.010"] = 0x739379beu,
                ["2-prg-27c4001.bin"] = 0x7d5b8a97u,
                ["2.096"] = 0xb9fdb6b5u,
                ["2.2"] = 0x736c1835u,
                ["2.3c"] = 0x07c85e9bu,
                ["2.4m"] = 0x61fd0a01u,
                ["2.a4"] = 0x144aa4c9u,
                ["2.amf"] = 0x7f162009u,
                ["2.bin"] = 0x42809e5au,
                ["2.ic171"] = 0x74844192u,
                ["2.ic175"] = 0xbd98ff15u,
                ["2.ic176"] = 0x1073b7b6u,
                ["2.ic19"] = 0xa2db1575u,
                ["2.mdta"] = 0x74844192u,
                ["2.rk"] = 0x13ea1c44u,
                ["2.stt"] = 0x7f162009u,
                ["2.u221"] = 0x1073b7b6u,
                ["2.u24"] = 0xdb01f9feu,
                ["2.u66"] = 0x27668828u,
                ["20.096"] = 0xf9f334ceu,
                ["20.20"] = 0x59bcc1bbu,
                ["20.7c"] = 0x2f1345b4u,
                ["20.9c"] = 0x002796dcu,
                ["20.bin"] = 0x675f4537u,
                ["20.e3"] = 0x8d5d0045u,
                ["20.u28"] = 0x8daf3814u,
                ["21-c.6f"] = 0x2ab2034fu,
                ["21.096"] = 0xd21ccddbu,
                ["21.10c"] = 0x523f462au,
                ["21.21"] = 0x1b872a98u,
                ["21.7d"] = 0x17e11df0u,
                ["21.bin"] = 0x04d175c9u,
                ["21.e4"] = 0x55c2b455u,
                ["21.u29"] = 0x54d0b680u,
                ["21003_u27.bin"] = 0x7d921309u,
                ["210101a_cda2.bin"] = 0x3f167412u,
                ["210102_cdb2.bin"] = 0x8a6920d8u,
                ["210105a_rom1.bin"] = 0xe6294edfu,
                ["210204_rom2.bin"] = 0x3f713043u,
                ["22"] = 0x94e8d01au,
                ["22-c.7f"] = 0x99f1cca4u,
                ["22.096"] = 0x092578a4u,
                ["22.11c"] = 0x52145369u,
                ["22.22"] = 0x23dc647au,
                ["22.7e"] = 0x7e69e2e6u,
                ["22.bin"] = 0xdb8a32acu,
                ["22.e5"] = 0x4109d637u,
                ["22.u30"] = 0xbf3ebe68u,
                ["222e"] = 0x1e20d0a3u,
                ["23"] = 0x6ae4b312u,
                ["23-c.8f"] = 0x35f9517bu,
                ["23.096"] = 0xbfa45d23u,
                ["23.13c"] = 0xb3b79d4fu,
                ["23.23"] = 0xad49eecdu,
                ["23.7f"] = 0x8426144bu,
                ["23.bin"] = 0xe592ba4fu,
                ["23.e6"] = 0xef9c2d4du,
                ["23.u31"] = 0x94d494c2u,
                ["24.24"] = 0xeda9fa6bu,
                ["24.5e"] = 0xc6909b6fu,
                ["24.7g"] = 0x889aac05u,
                ["24.9e"] = 0x005b54ccu,
                ["24.bin"] = 0x13ea1c44u,
                ["24.e7"] = 0xa8b5633au,
                ["24.u33"] = 0xbb34e444u,
                ["25.10e"] = 0x524f5c55u,
                ["25.7e"] = 0x152ea74au,
                ["25.7h"] = 0x29f79c78u,
                ["25.bin"] = 0xb89a740fu,
                ["25.e8"] = 0x72e923dfu,
                ["25.u34"] = 0xd666ec70u,
                ["26"] = 0xf30ffa29u,
                ["26.10a"] = 0x3692f6e5u,
                ["26.11e"] = 0x8312d055u,
                ["26.8e"] = 0x07fc714bu,
                ["26.bin"] = 0xa6974195u,
                ["26.e9"] = 0x82e8e384u,
                ["26.u35"] = 0xd20db83cu,
                ["27.12e"] = 0x035ee5d9u,
                ["27.9e"] = 0xa27e81fau,
                ["27.bin"] = 0x40296ecdu,
                ["27.e10"] = 0x4a3a8d09u,
                ["27.u36"] = 0x38e43390u,
                ["27010.06"] = 0x81c9550fu,
                ["27010.09hi"] = 0xa505621eu,
                ["27010.09lo"] = 0x23775344u,
                ["27010.1"] = 0xbeade53fu,
                ["27010.11"] = 0xde6271fbu,
                ["27010.2"] = 0x7f162009u,
                ["27010.3"] = 0x924c6ce2u,
                ["27010.4"] = 0x8226c11cu,
                ["27020.1"] = 0x3c4348cfu,
                ["27020.2"] = 0x6cfffb11u,
                ["27020.3"] = 0xab21635du,
                ["27020.4"] = 0xc97046a5u,
                ["27020.5"] = 0x2ce56f9fu,
                ["27020.6"] = 0xdbbfd400u,
                ["27020.7"] = 0x0ad7fb2bu,
                ["27020.8"] = 0x37635e97u,
                ["27020.u195"] = 0x2bffa6f9u,
                ["27020.u221"] = 0xaa4d55a6u,
                ["27040.5"] = 0x137d5f2eu,
                ["27040.6"] = 0x16c6372eu,
                ["274001.10"] = 0xa0d27605u,
                ["274001.11"] = 0x8112bbb4u,
                ["274001.12"] = 0x47cf8dfbu,
                ["274001.3"] = 0xe79eacb3u,
                ["274001.4"] = 0x47887cf3u,
                ["274001.5"] = 0x3f765ae8u,
                ["274001.6"] = 0x312d790cu,
                ["274001.7"] = 0x58307167u,
                ["274001.8"] = 0x3bc2ef5eu,
                ["274001.9"] = 0xcb73759du,
                ["27512.1"] = 0x08f6b60eu,
                ["27512.2"] = 0xabfca165u,
                ["27512.3"] = 0xa4823a1bu,
                ["27512.8"] = 0x13ea1c44u,
                ["27512.u133"] = 0x13ea1c44u,
                ["27512.u191"] = 0xa4823a1bu,
                ["27c010.1"] = 0xbeade53fu,
                ["27c010.2"] = 0x7f162009u,
                ["27c010.u195"] = 0x924c6ce2u,
                ["27c010.u221"] = 0x8226c11cu,
                ["27c010.u28"] = 0x76f9f91fu,
                ["27c010.u29"] = 0xe8f14362u,
                ["27c010.u30"] = 0xbf0cd819u,
                ["27c010.u31"] = 0x6de44671u,
                ["27c020.4"] = 0x0c83844du,
                ["27c020.5"] = 0x59ccd474u,
                ["27c020.6"] = 0x82097d63u,
                ["27c020.7"] = 0xa258b4d5u,
                ["27c020.u210"] = 0x6cfffb11u,
                ["27c040.u196"] = 0x80454da7u,
                ["27c040.u222"] = 0x0a3692beu,
                ["27c1024.10"] = 0x84427d1bu,
                ["27c1024.11"] = 0xc2a5373eu,
                ["27c1024.12"] = 0x55bc790cu,
                ["27c1024.9"] = 0xf8725addu,
                ["27c4000-m12374r-1.bin"] = 0x0e4058bau,
                ["27c4000-m12374r-2.bin"] = 0x13dfeb08u,
                ["27c4000-m12374r-3.bin"] = 0x6133f349u,
                ["27c4000-m12481-1.bin"] = 0xcc0805fcu,
                ["27c4000-m12481-2.bin"] = 0x55ef0adcu,
                ["27c4000-m12481-3.bin"] = 0xa0e1f6e0u,
                ["27c4000-m12481-4.bin"] = 0xf3c2c98du,
                ["27c4000-m12481-5.bin"] = 0x88847705u,
                ["27c4000-m12481-6.bin"] = 0xb7ad3394u,
                ["27c4000-m12481-7.bin"] = 0xb284c4a7u,
                ["27c4000-m12481-8.bin"] = 0x1371f714u,
                ["27c4000-m12481.bin"] = 0x96dfcbf1u,
                ["27c4000-m12623.bin"] = 0x7d921309u,
                ["27c512.13b"] = 0xa4823a1bu,
                ["27c512.3"] = 0xa4823a1bu,
                ["27c512.8"] = 0x13ea1c44u,
                ["28.10e"] = 0xaf62bf07u,
                ["28.9f"] = 0xc184d26du,
                ["28.bin"] = 0xff728865u,
                ["29.10f"] = 0xf06a12f2u,
                ["29.11e"] = 0x6b41f82du,
                ["29.bin"] = 0x7d9c479cu,
                ["2_atf16v8.u66"] = 0x48253c66u,
                ["2_gal16v8.ic7"] = 0x0ebc7cd7u,
                ["2_gal16v8.p1"] = 0xa944ff96u,
                ["2_palce16v8.bin"] = 0xbad3316bu,
                ["2_palce20v8.bin"] = 0x60d016b9u,
                ["3"] = 0xf7e4f2f0u,
                ["3-f2ab.040"] = 0x61fd0a01u,
                ["3-snd-27c208.bin"] = 0xa0c3de92u,
                ["3.096"] = 0xbe0b1a78u,
                ["3.1m"] = 0x739379beu,
                ["3.3"] = 0xf585bf2cu,
                ["3.4c"] = 0xac119a46u,
                ["3.a5"] = 0x8053335du,
                ["3.amf"] = 0xa4823a1bu,
                ["3.bin"] = 0x17d5ba8au,
                ["3.ic171"] = 0xa2355d90u,
                ["3.ic172"] = 0x0bdb9da2u,
                ["3.ic173"] = 0xc9c6e720u,
                ["3.ic28"] = 0xd5bee9ccu,
                ["3.mdta"] = 0x9f544ef4u,
                ["3.prg.040"] = 0xef25fe49u,
                ["3.stt"] = 0xa4823a1bu,
                ["3.u11"] = 0x65ae8c7eu,
                ["3.u196"] = 0x95ea597eu,
                ["3.u67"] = 0x91c8d782u,
                ["30"] = 0x5d35f737u,
                ["30.11f"] = 0xd0580ff2u,
                ["30.12e"] = 0x7e5f6cb4u,
                ["30.bin"] = 0x8141fe32u,
                ["31.12f"] = 0x353dbde1u,
                ["31.13e"] = 0x4a30c737u,
                ["31.bin"] = 0x87954a41u,
                ["32.8f"] = 0x21a0a453u,
                ["33.6f"] = 0x9b3cfc08u,
                ["33.9f"] = 0x89de1533u,
                ["34.10f"] = 0x8f663d00u,
                ["34.8f"] = 0xe0fb5657u,
                ["35.11f"] = 0x9db93d7au,
                ["35.11h"] = 0x7a791e77u,
                ["35.bin"] = 0x8c1f3994u,
                ["36.12f"] = 0x1a516657u,
                ["36.12h"] = 0x7a13cfbfu,
                ["36.bin"] = 0xc02a13ebu,
                ["37.13c"] = 0x3692f6e5u,
                ["37.13f"] = 0x932fc943u,
                ["37.bin"] = 0x4c2ccef7u,
                ["38.8h"] = 0xcd7923edu,
                ["38.bin"] = 0x1c0fc4e1u,
                ["39.9h"] = 0xbc09b360u,
                ["3_atf16v8.u100"] = 0x9ae375bau,
                ["3_gal16v8.ic72"] = 0xebf1f643u,
                ["3_palce20v8.bin"] = 0xf1fe9368u,
                ["3js_09.rom"] = 0x21ce044cu,
                ["3js_18.rom"] = 0xac6e307du,
                ["3js_19.rom"] = 0x068741dbu,
                ["3mre121.u68"] = 0x8edff95au,
                ["3snd.ic28"] = 0xd5bee9ccu,
                ["4"] = 0xaa51e43bu,
                ["4-d4d2.010"] = 0xfe5eee87u,
                ["4-snd-z80-27c512.bin"] = 0x4d4255b7u,
                ["4.096"] = 0xbba67a43u,
                ["4.1m"] = 0xfe5eee87u,
                ["4.4"] = 0xa02fb5aau,
                ["4.5c"] = 0x4ad13297u,
                ["4.a6"] = 0xf2c400b4u,
                ["4.amf"] = 0x39f15a1eu,
                ["4.bin"] = 0x69d7b06bu,
                ["4.ic171"] = 0xbd98ff15u,
                ["4.ic175"] = 0x924c6ce2u,
                ["4.ic176"] = 0x74844192u,
                ["4.mdta"] = 0xbd98ff15u,
                ["4.prg.010"] = 0xa0751944u,
                ["4.stt"] = 0x13ea1c44u,
                ["4.u222"] = 0xdb567b66u,
                ["4.u68"] = 0x45fc0a81u,
                ["4.u8"] = 0x67a60fe6u,
                ["40.10h"] = 0x1586dbf3u,
                ["41-1m.3a"] = 0xff77985au,
                ["41-32m.8h"] = 0x4e9648cau,
                ["41-3m.5a"] = 0x983be58fu,
                ["41-5m.7a"] = 0x01d1cb11u,
                ["41-7m.9a"] = 0xaeaa3509u,
                ["41.11h"] = 0x1aae69a4u,
                ["41_01.4a"] = 0xd8946fc1u,
                ["41_02.5a"] = 0x802e8153u,
                ["41_05.9a"] = 0xd8ba28e0u,
                ["41_06.10a"] = 0x4e53650bu,
                ["41_09.4b"] = 0xbe1b6bc2u,
                ["41_10.5b"] = 0xb7eb6a6du,
                ["41_13.9b"] = 0x2e06d0ecu,
                ["41_14.10b"] = 0x5a33f676u,
                ["41_17.5c"] = 0xbbeff902u,
                ["41_18.11c"] = 0xd1f15aebu,
                ["41_18.7c"] = 0xa5e1c1f3u,
                ["41_19.12c"] = 0x15aec3a6u,
                ["41_23.13b"] = 0x0f9d8527u,
                ["41_24.5e"] = 0x5aa43ceeu,
                ["41_25.7e"] = 0x94add360u,
                ["41_30.12c"] = 0xd1f15aebu,
                ["41_31.13c"] = 0x15aec3a6u,
                ["41_32.8f"] = 0xf0168249u,
                ["41_33.9f"] = 0x7a31b0e2u,
                ["41_34.10f"] = 0xb5f341ecu,
                ["41_35.11f"] = 0x95cc979au,
                ["41_36.12f"] = 0x7fbd42abu,
                ["41_37.13f"] = 0xc6464b0bu,
                ["41_38.8h"] = 0x8889c0aau,
                ["41_39.9h"] = 0x5b5c3949u,
                ["41_40.10h"] = 0x3979837du,
                ["41_41.11h"] = 0x57496819u,
                ["41_42.12h"] = 0xc7781f89u,
                ["41_43.13h"] = 0x440fc0b5u,
                ["41_9.12b"] = 0x0f9d8527u,
                ["41e_18.11c"] = 0xd1f15aebu,
                ["41e_30.11f"] = 0x9deb1e75u,
                ["41e_31.12f"] = 0xdf201112u,
                ["41e_35.11h"] = 0xd63942b3u,
                ["41e_36.12h"] = 0x816a818fu,
                ["41em_30.11f"] = 0x4249ec61u,
                ["41em_31.12f"] = 0x584e88e5u,
                ["41em_35.11h"] = 0xddbee5ebu,
                ["41em_36.12h"] = 0x3cfc31d0u,
                ["41u_30.11f"] = 0xbe5439d0u,
                ["41u_31.12f"] = 0x9811d6ebu,
                ["41u_35.11h"] = 0x6ac96595u,
                ["41u_36.12h"] = 0xa87e6137u,
                ["42.12h"] = 0x12a290a0u,
                ["43.13h"] = 0x872ad76du,
                ["4_atf20v8.u118"] = 0x60d016b9u,
                ["4_gal16v8.ic80"] = 0x2c43c330u,
                ["4_palce16v8.bin"] = 0x97a67c6du,
                ["4_palce20v8.bin"] = 0x20946530u,
                ["5"] = 0x1547e595u,
                ["5-caf3.040"] = 0xc8dcaa95u,
                ["5.5"] = 0x76458083u,
                ["5.7a"] = 0x7705aa46u,
                ["5.a"] = 0xffe16cdcu,
                ["5.a7"] = 0xa6ad6ef3u,
                ["5.amf"] = 0x03991fbau,
                ["5.bin"] = 0x47fab9edu,
                ["5.ic171"] = 0xc6f86e84u,
                ["5.ic172"] = 0x7fd91118u,
                ["5.ic26"] = 0x17d5ba8au,
                ["5.ic28"] = 0xd5bee9ccu,
                ["5.mdta"] = 0xd76d6621u,
                ["5.prg.040"] = 0x4d9d2327u,
                ["5.stt"] = 0xa505621eu,
                ["5.u140"] = 0x156c487bu,
                ["5.u34"] = 0x73a10d5du,
                ["5_atf20v8.u146"] = 0x049b7f4fu,
                ["5_gal20v8.ic121"] = 0x76fa8969u,
                ["5_palce16v8.bin"] = 0x48253c66u,
                ["5_palce20v8.bin"] = 0x44df0cc6u,
                ["5mre148.u69"] = 0x468962b1u,
                ["6"] = 0x7a99446eu,
                ["6-034f.040"] = 0x1ab0000cu,
                ["6.6"] = 0x5fda906eu,
                ["6.8a"] = 0x4eee9aeau,
                ["6.a8"] = 0x023baa18u,
                ["6.amf"] = 0x3a85a275u,
                ["6.bin"] = 0xb6215991u,
                ["6.ic84"] = 0xcea6d1d6u,
                ["6.ic91"] = 0x054cd5c4u,
                ["6.prg.010"] = 0x93eeb161u,
                ["6.stt"] = 0x23775344u,
                ["6.u128"] = 0xb671f752u,
                ["6.u33"] = 0xaffa4f82u,
                ["6_atf16v8.u160"] = 0xb0f10adfu,
                ["6_gal20v8.ic120"] = 0x6a55a974u,
                ["6_palce16v8.bin"] = 0x12516583u,
                ["6st-u196.2m1"] = 0x596609d4u,
                ["6st-u210.2m1"] = 0xed4186bdu,
                ["6st.u18"] = 0x2ddfe46eu,
                ["6st.u19"] = 0x39d763d3u,
                ["6st.u29"] = 0xe4eca601u,
                ["6st.u31"] = 0x35486f2du,
                ["6st.u64"] = 0x8165f536u,
                ["6st.u68"] = 0x8edff95au,
                ["6st.u69"] = 0x468962b1u,
                ["6st.u70"] = 0xbaa0f81fu,
                ["7"] = 0x8941ca12u,
                ["7-b0fa.040"] = 0x8425ff6bu,
                ["7.2f"] = 0x51031180u,
                ["7.7"] = 0x8c6c7430u,
                ["7.9a"] = 0x5b18b722u,
                ["7.a9"] = 0xf56085bau,
                ["7.amf"] = 0x13ea1c44u,
                ["7.bin"] = 0xded88f5fu,
                ["7.ic88"] = 0xef3f5be8u,
                ["7.ic90"] = 0x896eaf48u,
                ["7.stt"] = 0xde6271fbu,
                ["7.u126"] = 0x2f835213u,
                ["7.u64"] = 0xc60c5e75u,
                ["7mrd413.u64"] = 0x8165f536u,
                ["8"] = 0xc4ddc5b4u,
                ["8-a6b7.040"] = 0x24ce197bu,
                ["8.10a"] = 0x2d7f21e4u,
                ["8.3f"] = 0x325cc0b7u,
                ["8.8"] = 0xf655708cu,
                ["8.a10"] = 0x26fb340cu,
                ["8.amf"] = 0xecdb083bu,
                ["8.bin"] = 0xb8c39d56u,
                ["8.ic86"] = 0x34bbb3fau,
                ["8.ic93"] = 0x818ca33du,
                ["8.stt"] = 0x81c9550fu,
                ["8.u139"] = 0xe9421cbau,
                ["8.u63"] = 0x4a68b194u,
                ["8_atf16v8.u134g"] = 0x11f38ab7u,
                ["8_atf16v8.u96g"] = 0x11f38ab7u,
                ["8_atf16v8.u97g"] = 0x11f38ab7u,
                ["8_atf16v8.u98g"] = 0x11f38ab7u,
                ["8_atf16v8.u99g"] = 0x11f38ab7u,
                ["8k.28.e9"] = 0xb7ad5214u,
                ["9"] = 0x0e94f718u,
                ["9-8a2c.040"] = 0x9d20ef9bu,
                ["9.12a"] = 0x08d63519u,
                ["9.4f"] = 0x2e1d35f2u,
                ["9.512"] = 0xb8367eb5u,
                ["9.9"] = 0x8cd4df5bu,
                ["9.amf"] = 0x9156472fu,
                ["9.bin"] = 0xb6a71ed7u,
                ["9.ic86"] = 0x34bbb3fau,
                ["9.ic93"] = 0x818ca33du,
                ["9.u127"] = 0x8d08103cu,
                ["9.u195"] = 0xcd1d5666u,
                ["9.u62"] = 0xf0bba5c7u,
                ["93c46.bin"] = 0x36ab4e7du,
                ["9k.18.c11"] = 0x7f162009u,
                ["a-15.5"] = 0x6f07d2cbu,
                ["a-se235.bin"] = 0xa258de13u,
                ["ai.ic93"] = 0xcffbf4beu,
                ["ap.ic88"] = 0xfdf5f163u,
                ["ar-1m.3a"] = 0x5965ca8du,
                ["ar-32m.8h"] = 0xae1d7fb0u,
                ["ar-3m.5a"] = 0xac6db17du,
                ["ar-5m.7a"] = 0xbf4575d8u,
                ["ar-7m.9a"] = 0xa02945f4u,
                ["ar22b.1a"] = 0xf1db9030u,
                ["ar24b.1a"] = 0x09a51271u,
                ["ar_01.4a"] = 0x392151b4u,
                ["ar_02.5a"] = 0xbac5dec5u,
                ["ar_05.9a"] = 0xe246ed9fu,
                ["ar_06.10a"] = 0xc8f04223u,
                ["ar_09.12b"] = 0xf3dd1367u,
                ["ar_09.4b"] = 0xdb9376f8u,
                ["ar_10.5b"] = 0x4219b622u,
                ["ar_13.9b"] = 0x81436481u,
                ["ar_14.10b"] = 0xe6bae179u,
                ["ar_17.5c"] = 0x0b8e0df4u,
                ["ar_18.7c"] = 0x9336db6au,
                ["ar_23.13c"] = 0xf3dd1367u,
                ["ar_24.5e"] = 0x9cd6e2a3u,
                ["ar_25.7e"] = 0x15ccf981u,
                ["ar_30.12e"] = 0x584b43a9u,
                ["ar_32.8f"] = 0xdb6acdcfu,
                ["ar_33.9f"] = 0x3968f4b5u,
                ["ar_34.10f"] = 0xf6e80386u,
                ["ar_35.11f"] = 0x86d98ff3u,
                ["ar_36.12f"] = 0x65030392u,
                ["ar_37.13f"] = 0x33e9694bu,
                ["ar_38.8h"] = 0x8b9e75b9u,
                ["ar_39.9h"] = 0x9b8e1363u,
                ["ar_40.10h"] = 0xbe36c145u,
                ["ar_41.11h"] = 0x758893d3u,
                ["ar_42.12h"] = 0xc48170deu,
                ["ar_43.13h"] = 0x7cc8fb9eu,
                ["ara63b.1a"] = 0x3e049379u,
                ["ara_01.3a"] = 0xbf4575d8u,
                ["ara_02.4a"] = 0xa02945f4u,
                ["ara_03.5a"] = 0x5965ca8du,
                ["ara_04.6a"] = 0xac6db17du,
                ["ara_09.12a"] = 0xaf88359cu,
                ["ara_18.11c"] = 0x584b43a9u,
                ["araj_22.7f"] = 0x9913002eu,
                ["araj_23.8f"] = 0x7045d6cbu,
                ["aru_18.11c"] = 0x584b43a9u,
                ["aru_30.11f"] = 0x24d8f88du,
                ["aru_31.12f"] = 0x33e9694bu,
                ["aru_35.11h"] = 0x8b954b59u,
                ["aru_36.12h"] = 0x7cc8fb9eu,
                ["b-16.6"] = 0x6cfffb11u,
                ["b-se194.bin"] = 0x5726cab8u,
                ["b11.rom"] = 0x94a46525u,
                ["b12.rom"] = 0x8f742fd5u,
                ["b13.rom"] = 0x8fb3dd47u,
                ["b14.rom"] = 0x8831ec7fu,
                ["bi.ic94"] = 0x4a1b43feu,
                ["bk.29.e10"] = 0x524f5c55u,
                ["bnh-01.bin"] = 0xffbc3bddu,
                ["bnh-02.bin"] = 0x40e58d52u,
                ["bnh-03.bin"] = 0x58f92cadu,
                ["bnh-04.bin"] = 0x284eea8au,
                ["bnh-05.bin"] = 0xd02719b7u,
                ["bnh-06.bin"] = 0xd9d43b55u,
                ["bnh-07.bin"] = 0x03b7900du,
                ["bnh-08.bin"] = 0x327b8da8u,
                ["bp.ic87"] = 0x4e1c52b7u,
                ["bprg1.11d"] = 0x31793da7u,
                ["bruteforce.palce16v8h-25.11d"] = 0x430f722du,
                ["buf1"] = 0xeb122de7u,
                ["c-27.7"] = 0x13ea1c44u,
                ["c-se005.bin"] = 0xc781bf87u,
                ["c5.bin"] = 0x6152277du,
                ["c6.bin"] = 0x7f654421u,
                ["c628"] = 0x662e090fu,
                ["c632.ic1"] = 0x0fbd9270u,
                ["c632b.ic1"] = 0x5c3cbb67u,
                ["c91e-01.bin"] = 0xf863071cu,
                ["c91e-02.bin"] = 0x4b03c308u,
                ["c91e-03.bin"] = 0x3383ea96u,
                ["c91e-04.bin"] = 0xb8e1f4cfu,
                ["c91e-05.bin"] = 0x096115fbu,
                ["ca-1m.3a"] = 0x4d0620fdu,
                ["ca-32m.8h"] = 0x0c4837d4u,
                ["ca-3m.5a"] = 0x0b0341c3u,
                ["ca-5m.7a"] = 0x66d4cc37u,
                ["ca-7m.9a"] = 0xb6f896f2u,
                ["ca22b.1a"] = 0x5152e678u,
                ["ca24b.1a"] = 0x76ec0b1cu,
                ["ca_0.bin"] = 0x6819f572u,
                ["ca_1.bin"] = 0xf18256d2u,
                ["ca_18.11c"] = 0x4a613a2cu,
                ["ca_19.12c"] = 0x74584493u,
                ["ca_2.bin"] = 0x1f474165u,
                ["ca_3.bin"] = 0x1f7f36a7u,
                ["ca_4.bin"] = 0xb6c2ae5cu,
                ["ca_5.bin"] = 0x68e7b97eu,
                ["ca_6.bin"] = 0xd7df2079u,
                ["ca_7.bin"] = 0x76c35a1fu,
                ["ca_9.12b"] = 0x96fe7485u,
                ["cae_09.12b"] = 0x96fe7485u,
                ["cae_18.11c"] = 0x4a613a2cu,
                ["cae_19.12c"] = 0x74584493u,
                ["cae_30.11f"] = 0x23305cd5u,
                ["cae_30a.11f"] = 0x91fceacdu,
                ["cae_31.12f"] = 0x9008dfb3u,
                ["cae_31a.12f"] = 0xe5b75cafu,
                ["cae_35.11h"] = 0x69419113u,
                ["cae_35a.11h"] = 0x3ef03083u,
                ["cae_36.12h"] = 0x4dbf6f8eu,
                ["cae_36a.12h"] = 0xc73fd713u,
                ["caj_01.4a"] = 0x1002d0b8u,
                ["caj_02.5a"] = 0x125b018du,
                ["caj_05.9a"] = 0x207373d7u,
                ["caj_06.10a"] = 0xcf80e164u,
                ["caj_09.4b"] = 0x41b0f9a6u,
                ["caj_10.5b"] = 0xbf8a5f52u,
                ["caj_13.9b"] = 0x6f3948b2u,
                ["caj_14.10b"] = 0x8458e7d7u,
                ["caj_17.5c"] = 0x540f2fd8u,
                ["caj_18.7c"] = 0x29c1d4b1u,
                ["caj_23.13b"] = 0x96fe7485u,
                ["caj_24.5e"] = 0xe356aad7u,
                ["caj_25.7e"] = 0xcdd0204du,
                ["caj_30.12c"] = 0x4a613a2cu,
                ["caj_31.13c"] = 0x74584493u,
                ["caj_32.8f"] = 0x9b5836b3u,
                ["caj_33.9f"] = 0xdde3891fu,
                ["caj_34.10f"] = 0x51ea57f4u,
                ["caj_35.11f"] = 0x01d71973u,
                ["caj_36a.12f"] = 0x91fceacdu,
                ["caj_37a.13f"] = 0xe5b75cafu,
                ["caj_38.8h"] = 0x2464d4abu,
                ["caj_39.9h"] = 0xeea23b67u,
                ["caj_40.10h"] = 0x2ab71ae1u,
                ["caj_41.11h"] = 0x3a43b538u,
                ["caj_42a.12h"] = 0x039f8362u,
                ["caj_43a.13h"] = 0xc73fd713u,
                ["cau_01.4a"] = 0x34c3094eu,
                ["cau_02.5a"] = 0x3e1f5b34u,
                ["cau_05.9a"] = 0xf042cc7bu,
                ["cau_06.10a"] = 0x3777ede1u,
                ["cau_09.12b"] = 0x96fe7485u,
                ["cau_09.4b"] = 0xd4b17c3au,
                ["cau_10.5b"] = 0x4af10ef2u,
                ["cau_13.9b"] = 0x9d5c7911u,
                ["cau_14.10b"] = 0x2bef78c4u,
                ["cau_17.5c"] = 0x4fab0d0cu,
                ["cau_18.11c"] = 0x4a613a2cu,
                ["cau_18.7c"] = 0x4c52edf1u,
                ["cau_19.12c"] = 0x74584493u,
                ["cau_23.13b"] = 0x96fe7485u,
                ["cau_24.5e"] = 0x0eac450fu,
                ["cau_25.7e"] = 0x859ee531u,
                ["cau_30.12c"] = 0x4a613a2cu,
                ["cau_30a.11f"] = 0x91fceacdu,
                ["cau_31.13c"] = 0x74584493u,
                ["cau_31a.12f"] = 0xe5b75cafu,
                ["cau_32.8f"] = 0x433a0859u,
                ["cau_33.9f"] = 0x8560c130u,
                ["cau_34.10f"] = 0x5fda906eu,
                ["cau_35.11f"] = 0x74c2ddf0u,
                ["cau_35a.11h"] = 0xf090d9b2u,
                ["cau_36.12f"] = 0xc2574c0cu,
                ["cau_36a.12h"] = 0xc73fd713u,
                ["cau_37.13f"] = 0x8e6d4f8au,
                ["cau_38.8h"] = 0xcb96ed24u,
                ["cau_39.9h"] = 0x147be975u,
                ["cau_40.10h"] = 0x736c1835u,
                ["cau_41.11h"] = 0x2a44bfe5u,
                ["cau_42.12h"] = 0xd89e00beu,
                ["cau_43.13h"] = 0xece07955u,
                ["caw1.bin"] = 0xb19b10ceu,
                ["caw2.bin"] = 0x8125d3f0u,
                ["caw3.bin"] = 0xffe16cdcu,
                ["caw4.bin"] = 0x4937fc41u,
                ["caw5.bin"] = 0x30dd78dbu,
                ["caw6.bin"] = 0x61192f7cu,
                ["caw7.bin"] = 0xa045c689u,
                ["cb_0.bin"] = 0xc9ca6efcu,
                ["cb_1.bin"] = 0xc53de0e5u,
                ["cb_2.bin"] = 0x2934a28cu,
                ["cb_3.bin"] = 0xca41ae19u,
                ["cb_4.bin"] = 0xb9ea6163u,
                ["cb_5.bin"] = 0x74a402f2u,
                ["cb_6.bin"] = 0x73f471e1u,
                ["cb_7.bin"] = 0x7570e1f0u,
                ["cc-1m.4a"] = 0x00637302u,
                ["cc-2m.8a"] = 0x0c69f151u,
                ["cc-3m.6a"] = 0xcc87cf61u,
                ["cc-4m.10a"] = 0x1f9ebb97u,
                ["cc-5m.3a"] = 0x7261d8bau,
                ["cc-6m.7a"] = 0x28718bedu,
                ["cc-7m.5a"] = 0x6a60f949u,
                ["cc-8m.9a"] = 0xd4acc53au,
                ["cc63b.1a"] = 0xcae8f0f9u,
                ["cc_01.3a"] = 0x7261d8bau,
                ["cc_02.4a"] = 0x6a60f949u,
                ["cc_03.5a"] = 0x00637302u,
                ["cc_04.6a"] = 0xcc87cf61u,
                ["cc_05.7a"] = 0x28718bedu,
                ["cc_06.8a"] = 0xd4acc53au,
                ["cc_07.9a"] = 0x0c69f151u,
                ["cc_08.10a"] = 0x1f9ebb97u,
                ["cc_09.11a"] = 0x698e8b58u,
                ["cc_18.11c"] = 0x6de2c2dbu,
                ["cc_19.12c"] = 0xb99091aeu,
                ["cc_22d.7f"] = 0xa91949b7u,
                ["cc_22f.7f"] = 0x0fd34195u,
                ["cc_24d.9e"] = 0x680e543fu,
                ["cc_24f.9e"] = 0x3a794f25u,
                ["cc_28d.9f"] = 0x8820039fu,
                ["cc_28f.9f"] = 0xfc3c2906u,
                ["cce_23d.8f"] = 0x19c58eceu,
                ["cce_23f.8f"] = 0x42c814c5u,
                ["ccj_09.12a"] = 0x698e8b58u,
                ["ccj_18.11c"] = 0x6de2c2dbu,
                ["ccj_19.12c"] = 0xb99091aeu,
                ["ccj_22c.7f"] = 0x9b82a052u,
                ["ccj_22f.7f"] = 0x0fd34195u,
                ["ccj_23b.8f"] = 0xe2a2d80eu,
                ["ccj_23f.8f"] = 0x5b482b62u,
                ["ccj_24b.9e"] = 0x84ff99b2u,
                ["ccj_24f.9e"] = 0x3a794f25u,
                ["ccj_28b.9f"] = 0xfbcec223u,
                ["ccj_28f.9f"] = 0xfc3c2906u,
                ["ccprg.11d"] = 0xe1c225c4u,
                ["ccprg1.11d"] = 0xe1c225c4u,
                ["ccu_09.11a"] = 0x698e8b58u,
                ["ccu_18.11c"] = 0x6de2c2dbu,
                ["ccu_19.12c"] = 0xb99091aeu,
                ["ccu_22c.7f"] = 0x9b82a052u,
                ["ccu_23b.8f"] = 0x03da44fdu,
                ["ccu_24b.9e"] = 0x84ff99b2u,
                ["ccu_28b.9f"] = 0xfbcec223u,
                ["cd-1m.3a"] = 0x8da4f917u,
                ["cd-2m.4a"] = 0x09c8fc2du,
                ["cd-3m.5a"] = 0x6c40f603u,
                ["cd-4m.6a"] = 0x637ff38fu,
                ["cd-5m.7a"] = 0x470befeeu,
                ["cd-6m.8a"] = 0xe7599ac4u,
                ["cd-7m.9a"] = 0x22bfb7a3u,
                ["cd-8m.10a"] = 0x211b4b15u,
                ["cd-q1.1k"] = 0x60927775u,
                ["cd-q2.2k"] = 0x770f4c47u,
                ["cd-q3.3k"] = 0x2f273ffcu,
                ["cd-q4.4k"] = 0x2c67821du,
                ["cd63b.1a"] = 0xef72e902u,
                ["cd_01.3a"] = 0x8da4f917u,
                ["cd_02.4a"] = 0x6c40f603u,
                ["cd_03.5a"] = 0x09c8fc2du,
                ["cd_04.6a"] = 0x637ff38fu,
                ["cd_05.7a"] = 0x470befeeu,
                ["cd_06.8a"] = 0x22bfb7a3u,
                ["cd_07.9a"] = 0xe7599ac4u,
                ["cd_08.10a"] = 0x211b4b15u,
                ["cd_q.5k"] = 0x605fdb0bu,
                ["cde_21a.6f"] = 0x66d23de2u,
                ["cde_22a.7f"] = 0x9278aa12u,
                ["cde_23a.8f"] = 0x8f4e585eu,
                ["cdj_21a.6f"] = 0x66d23de2u,
                ["cdj_22a.7f"] = 0xa0d8de29u,
                ["cdj_23a.8f"] = 0x5f3ece96u,
                ["cdt_21.6f"] = 0x74b04329u,
                ["cdt_22.7f"] = 0x1e534ca5u,
                ["cdt_23.8f"] = 0xf477f7a0u,
                ["cdu_21a.6f"] = 0x66d23de2u,
                ["cdu_22a.7f"] = 0xd19f981eu,
                ["cdu_23a.8f"] = 0x7c2543cdu,
                ["ce91e-a"] = 0x0c83844du,
                ["ce91e-a.bin"] = 0x02e88ec7u,
                ["ce91e-b"] = 0x0862386eu,
                ["ce91e-b.bin"] = 0x963200d2u,
                ["ch196esp"] = 0xed2ff437u,
                ["ch222esp"] = 0x9e6d058au,
                ["ch_01.4a"] = 0x7f3b7b56u,
                ["ch_02.5a"] = 0x7c8c88fbu,
                ["ch_05.9a"] = 0x6c1afb9au,
                ["ch_06.10a"] = 0x81884b2bu,
                ["ch_09.12b"] = 0x4d4255b7u,
                ["ch_09.4b"] = 0x567ab3cau,
                ["ch_10.5b"] = 0xe8251a9bu,
                ["ch_13.9b"] = 0x12a7a8bau,
                ["ch_14.10b"] = 0x4012ec4bu,
                ["ch_17.5c"] = 0xfe490846u,
                ["ch_18.11c"] = 0xf909e8deu,
                ["ch_18.7c"] = 0x516a34d1u,
                ["ch_19.12c"] = 0xfc158cf7u,
                ["ch_23.13b"] = 0x4d4255b7u,
                ["ch_24.5e"] = 0x9cb6e6bcu,
                ["ch_25.7e"] = 0x1dfcbac5u,
                ["ch_30.12c"] = 0xf909e8deu,
                ["ch_31.13c"] = 0xfc158cf7u,
                ["ch_32.8f"] = 0x317d27b0u,
                ["ch_33.9f"] = 0x30dc5dedu,
                ["ch_34.10f"] = 0x609ed2f9u,
                ["ch_35.11f"] = 0xb810867fu,
                ["ch_38.8h"] = 0x6e5c8cb6u,
                ["ch_39.9h"] = 0x872fb2a4u,
                ["ch_40.10h"] = 0xbe0d8301u,
                ["ch_41.11h"] = 0x8ad96155u,
                ["che_30.11f"] = 0x9a2a2db1u,
                ["che_31.12f"] = 0xbbff8a99u,
                ["che_35.11h"] = 0xa7f96b02u,
                ["che_36.12h"] = 0x0fa00c39u,
                ["chj_36a.12f"] = 0xec1328d8u,
                ["chj_37a.13f"] = 0x46d2cf7bu,
                ["chj_42a.12h"] = 0x4ae13503u,
                ["chj_43a.13h"] = 0x8d387fe8u,
                ["ci.ic91"] = 0x22228bc5u,
                ["ci030.u10.400"] = 0xed4186bdu,
                ["ck-1m.3a"] = 0xf33ca9d4u,
                ["ck-32m.8h"] = 0x9b70bd41u,
                ["ck-3m.5a"] = 0x0ba2047fu,
                ["ck-5m.7a"] = 0x4ec75f15u,
                ["ck-7m.9a"] = 0xd85d00d6u,
                ["ck22b.1a"] = 0x24fdfdebu,
                ["ck24b.1a"] = 0xbd99c448u,
                ["conv.u133"] = 0x13ea1c44u,
                ["conv2.u191"] = 0x08f6b60eu,
                ["cp.ic90"] = 0xe3b8589eu,
                ["cp1b1f.1f"] = 0x3979b8e3u,
                ["cp1b1f_boot.1f"] = 0x658849dcu,
                ["cp1b8k.8k"] = 0x8a52ea7au,
                ["cp1b9k.9k"] = 0xa754bdc3u,
                ["cp1b9ka.9k"] = 0x238d3ff4u,
                ["cpu2.bin"] = 0xd7b13f39u,
                ["cpu3.bin"] = 0x8c2593acu,
                ["cpu4.bin"] = 0x665a5485u,
                ["cpu5.bin"] = 0xc3151563u,
                ["cr.00"] = 0xe6bbd39bu,
                ["cr.01"] = 0x6c794ef4u,
                ["cr.02"] = 0x4d1d389du,
                ["cr.03"] = 0x5282be3cu,
                ["csicat27c512.u191"] = 0x08f6b60eu,
                ["d-se064.bin"] = 0x4dd24197u,
                ["d10f1.10f"] = 0x6619c494u,
                ["d21.u70"] = 0xbaa0f81fu,
                ["d22.u69"] = 0x468962b1u,
                ["d23.u19"] = 0x39d763d3u,
                ["d24.u68"] = 0x8edff95au,
                ["d25.u64"] = 0x8165f536u,
                ["d26.u18"] = 0x93ec42aeu,
                ["d7l1.7l"] = 0x27b7410du,
                ["d8l1.8l"] = 0x539fc7dau,
                ["d9k1.9k"] = 0x6c35c805u,
                ["d9k2.9k"] = 0xcd85a156u,
                ["dam63b.1a"] = 0x474b3c8au,
                ["dam_01.3a"] = 0x0ba9c0b0u,
                ["dam_02.4a"] = 0x5d760ab9u,
                ["dam_03.5a"] = 0x4ba90b59u,
                ["dam_04.6a"] = 0x4bdee9deu,
                ["dam_05.7a"] = 0x7dc61b94u,
                ["dam_06.8a"] = 0xfde89758u,
                ["dam_07.9a"] = 0xec351d78u,
                ["dam_08.10a"] = 0xee2acc1eu,
                ["dam_09.12a"] = 0x0656ff53u,
                ["damj_22.7f"] = 0x595ff2f3u,
                ["damj_23.8f"] = 0xc3b248ecu,
                ["de.35.j11"] = 0xc184d26du,
                ["di.ic92"] = 0xab031763u,
                ["dm-05.3a"] = 0x0ba9c0b0u,
                ["dm-06.3c"] = 0x4ba90b59u,
                ["dm-07.3f"] = 0x5d760ab9u,
                ["dm-08.3g"] = 0x4bdee9deu,
                ["dm-17.7j"] = 0x3ea1b0f2u,
                ["dm22a.1a"] = 0xd4776116u,
                ["dm620.2a"] = 0xf6e5f727u,
                ["dm_01.4a"] = 0x80896c33u,
                ["dm_02.4b"] = 0x8b98dc48u,
                ["dm_03.5a"] = 0xb1033e62u,
                ["dm_04.5b"] = 0xa4f4f8f0u,
                ["dm_05.7a"] = 0xd34e271au,
                ["dm_06.7b"] = 0xae24bb19u,
                ["dm_07.8a"] = 0x2a40166au,
                ["dm_08.8b"] = 0xbcc0f28cu,
                ["dm_09.9a"] = 0xc9c4afa5u,
                ["dm_10.9b"] = 0xc2e7d9efu,
                ["dm_11.10a"] = 0x9040cb04u,
                ["dm_12.10b"] = 0x10fdd76au,
                ["dm_13.11a"] = 0x7e69e2e6u,
                ["dm_14.11b"] = 0x3f70dd37u,
                ["dm_15.12a"] = 0x8426144bu,
                ["dm_16.12b"] = 0x20f85c03u,
                ["dm_17.5c"] = 0xdc6ed8adu,
                ["dm_18.5e"] = 0x1aa0db99u,
                ["dm_19.7c"] = 0x2623b52fu,
                ["dm_20.7e"] = 0x281d0b3eu,
                ["dm_21.8c"] = 0x2f1345b4u,
                ["dm_22.8e"] = 0x37c9b6c6u,
                ["dm_23.9c"] = 0x17e11df0u,
                ["dm_24.9e"] = 0xda088d61u,
                ["dm_25.10c"] = 0x889aac05u,
                ["dm_26.10e"] = 0x3c2a212au,
                ["dm_27.11c"] = 0x29f79c78u,
                ["dm_28.11e"] = 0xf187ba1cu,
                ["dm_29.8f"] = 0x49a48796u,
                ["dm_30.8h"] = 0xd9d3f8bdu,
                ["dm_31.9f"] = 0x54acb729u,
                ["dm_32.9h"] = 0x99692344u,
                ["dm_33.10f"] = 0x384d60c4u,
                ["dm_34.10h"] = 0x19abe30fu,
                ["dm_35.11f"] = 0xc04b85c8u,
                ["dm_36.11h"] = 0x89be83deu,
                ["dme_27.9h"] = 0xf734b2beu,
                ["dme_28.9j"] = 0x03d3e714u,
                ["dme_29.10h"] = 0x166a58a2u,
                ["dme_30.10j"] = 0x7ac8407au,
                ["dmj_38.12f"] = 0x82fd1798u,
                ["dmj_39.12h"] = 0x35366cccu,
                ["dmj_40.13f"] = 0xa17c170au,
                ["dmj_41.13h"] = 0x6af0b391u,
                ["dmu_27.9h"] = 0x4a524140u,
                ["dmu_28.9j"] = 0x94aae205u,
                ["dmu_29.10h"] = 0x334d85b2u,
                ["dmu_30.10j"] = 0xcee8ceb5u,
                ["dp.ic89"] = 0x3eec9580u,
                ["e-sf004.bin"] = 0x187667ccu,
                ["epr-b-01.12c"] = 0x7f162009u,
                ["epr-b-01.4a"] = 0xab21635du,
                ["epr-b-02.13c"] = 0xbeade53fu,
                ["epr-b-02.7a"] = 0xee3d878au,
                ["epr-b-03.10f"] = 0x852e10ecu,
                ["epr-b-03.8a"] = 0xe8877e9du,
                ["epr-b-04.11f"] = 0xfdd0b5c1u,
                ["epr-b-04.9a"] = 0x0ad7fb2bu,
                ["epr-b-05.11a"] = 0x37635e97u,
                ["epr-b-05.12f"] = 0xbc02c14cu,
                ["epr-b-06.12a"] = 0x67dcc295u,
                ["epr-b-06.13f"] = 0x8b8221e6u,
                ["epr-b-07.10h"] = 0x3b075de1u,
                ["epr-b-07.4b"] = 0x84afb959u,
                ["epr-b-08.11h"] = 0xdb66b127u,
                ["epr-b-08.7b"] = 0x14756473u,
                ["epr-b-09.12h"] = 0x1c1266b3u,
                ["epr-b-09.8b"] = 0x4894aa8fu,
                ["epr-b-10.13h"] = 0x2d42d82au,
                ["epr-b-10.9b"] = 0x2ce56f9fu,
                ["epr-b-11.11b"] = 0xdbbfd400u,
                ["epr-b-12.12b"] = 0xc9d4ed76u,
                ["epr-b-13.5c"] = 0x63d63e0cu,
                ["epr-b-14.8c"] = 0xc97046a5u,
                ["epr-b-15.9c"] = 0x14e46ab1u,
                ["epr-b-16.10c"] = 0xfa6f32d9u,
                ["epr-b-17.11c"] = 0xf187086bu,
                ["epr-b-18.5e"] = 0x88f3485au,
                ["epr-b-19.8e"] = 0x031525ccu,
                ["epr-b-20.9e"] = 0x27cae573u,
                ["epr-b-21.10e"] = 0xacbbdb09u,
                ["epr-b-22.11e"] = 0xf241f0c7u,
                ["epr-b-23.8f"] = 0xe5819676u,
                ["epr-b-24.8h"] = 0x25ae23bcu,
                ["f-sf001.bin"] = 0x5b585071u,
                ["ff-19.bin"] = 0x7bc03747u,
                ["ff-1m.3a"] = 0xd5469303u,
                ["ff-21.bin"] = 0x0c248e2bu,
                ["ff-22m.7h"] = 0xcbdd8689u,
                ["ff-23.bin"] = 0x53949d0eu,
                ["ff-23m.8h"] = 0x86def74fu,
                ["ff-25.bin"] = 0x8d34a67du,
                ["ff-32m.8h"] = 0xc747696eu,
                ["ff-3m.5a"] = 0x0c6302bfu,
                ["ff-5m.7a"] = 0x91a909bdu,
                ["ff-7m.9a"] = 0x89f8b4cdu,
                ["ff.34.j10"] = 0xf06a12f2u,
                ["ff1.bin"] = 0x5b276c14u,
                ["ff36.bin"] = 0xf9a5ce83u,
                ["ff37.bin"] = 0xe1033784u,
                ["ff42.bin"] = 0x65f11215u,
                ["ff43.bin"] = 0xb6dee1c3u,
                ["ff_01.4a"] = 0x815b1797u,
                ["ff_02.5a"] = 0x5d91f694u,
                ["ff_05.9a"] = 0xd0fcd4b5u,
                ["ff_06.10a"] = 0x1c18f042u,
                ["ff_09.12b"] = 0xb8367eb5u,
                ["ff_09.4b"] = 0x5b116d0du,
                ["ff_1.3a"] = 0x969d18e2u,
                ["ff_10.5b"] = 0x624a924au,
                ["ff_13.9b"] = 0x8721a7dau,
                ["ff_14.10b"] = 0x0a2e9101u,
                ["ff_17.5c"] = 0x2dc18cf4u,
                ["ff_18.11c"] = 0x375c66e7u,
                ["ff_18.7c"] = 0xb19ede59u,
                ["ff_19.12c"] = 0x1ef137f9u,
                ["ff_2.4a"] = 0x02b59f99u,
                ["ff_22.7f"] = 0xb2d5a3aau,
                ["ff_23.12b"] = 0xb8367eb5u,
                ["ff_23.13b"] = 0xb8367eb5u,
                ["ff_23.13c"] = 0xb8367eb5u,
                ["ff_23.8f"] = 0xae3dda7fu,
                ["ff_23.bin"] = 0xb8367eb5u,
                ["ff_24.5e"] = 0xa1ab607au,
                ["ff_25.7e"] = 0x6e8181eau,
                ["ff_3.5a"] = 0x01d507aeu,
                ["ff_30.12c"] = 0x375c66e7u,
                ["ff_30.12e"] = 0x375c66e7u,
                ["ff_31.13c"] = 0x1ef137f9u,
                ["ff_31.13e"] = 0x1ef137f9u,
                ["ff_32.8f"] = 0xc8bc4a57u,
                ["ff_33.9f"] = 0x7369fa07u,
                ["ff_34.10f"] = 0x0c8dc3fcu,
                ["ff_34.9f"] = 0x0c8dc3fcu,
                ["ff_35.10f"] = 0x4a934121u,
                ["ff_35.11f"] = 0x4a934121u,
                ["ff_36.11f"] = 0xf9a5ce83u,
                ["ff_36.12f"] = 0xed988977u,
                ["ff_37.12f"] = 0xe1033784u,
                ["ff_37.13f"] = 0xdba5a476u,
                ["ff_38.8h"] = 0x6535a57fu,
                ["ff_39.9h"] = 0x9416b477u,
                ["ff_4.6a"] = 0xf7c4ceb0u,
                ["ff_40.10h"] = 0x8075bab9u,
                ["ff_40.9h"] = 0x8075bab9u,
                ["ff_41.10h"] = 0x2af68154u,
                ["ff_41.11h"] = 0x2af68154u,
                ["ff_42.11h"] = 0x65f11215u,
                ["ff_9.12a"] = 0xb8367eb5u,
                ["ffe_23.12b"] = 0xb8367eb5u,
                ["ffe_30.11f"] = 0x2347bf51u,
                ["ffe_31.12f"] = 0x6dc6b792u,
                ["ffe_35.11h"] = 0x5f694eccu,
                ["ffe_36.12h"] = 0xb36a0b99u,
                ["ffe_43.12h"] = 0x995e968au,
                ["ffj_01.4a"] = 0x815b1797u,
                ["ffj_02.5a"] = 0x5d91f694u,
                ["ffj_05.9a"] = 0xd0fcd4b5u,
                ["ffj_06.10a"] = 0x1c18f042u,
                ["ffj_09.4b"] = 0x5b116d0du,
                ["ffj_10.5b"] = 0x624a924au,
                ["ffj_13.9b"] = 0x8721a7dau,
                ["ffj_14.10b"] = 0x0a2e9101u,
                ["ffj_17.5c"] = 0x2dc18cf4u,
                ["ffj_18.7c"] = 0xb19ede59u,
                ["ffj_24.5e"] = 0xa1ab607au,
                ["ffj_25.7e"] = 0x6e8181eau,
                ["ffj_30.12c"] = 0x375c66e7u,
                ["ffj_30.12e"] = 0x375c66e7u,
                ["ffj_30.bin"] = 0x375c66e7u,
                ["ffj_31.13c"] = 0x1ef137f9u,
                ["ffj_31.13e"] = 0x1ef137f9u,
                ["ffj_31.bin"] = 0x1ef137f9u,
                ["ffj_32.8f"] = 0xc8bc4a57u,
                ["ffj_33.9f"] = 0x7369fa07u,
                ["ffj_34.10f"] = 0x0c8dc3fcu,
                ["ffj_35.11f"] = 0x4a934121u,
                ["ffj_36.12f"] = 0xe619eb30u,
                ["ffj_36a.12f"] = 0x088ed1c9u,
                ["ffj_37.13f"] = 0xa8127e4eu,
                ["ffj_37a.13f"] = 0x708557ffu,
                ["ffj_38.8h"] = 0x6535a57fu,
                ["ffj_39.9h"] = 0x9416b477u,
                ["ffj_40.10h"] = 0x8075bab9u,
                ["ffj_41.11h"] = 0x2af68154u,
                ["ffj_42.12h"] = 0x07bf1c21u,
                ["ffj_42a.12h"] = 0xc4c491e6u,
                ["ffj_43.13h"] = 0xfbeca028u,
                ["ffj_43a.13h"] = 0xc004004au,
                ["ffu_30.11f"] = 0xed988977u,
                ["ffu_30_3.11f"] = 0xe619eb30u,
                ["ffu_31.12f"] = 0xdba5a476u,
                ["ffu_31_3.12f"] = 0x59abd207u,
                ["ffu_35.11h"] = 0x07bf1c21u,
                ["ffu_35_3.11h"] = 0xbca85263u,
                ["ffu_36.11f"] = 0xe2a48af9u,
                ["ffu_36.12h"] = 0x4d89f542u,
                ["ffu_36_3.12h"] = 0xdf46ece8u,
                ["ffu_37.12f"] = 0xc371c667u,
                ["ffu_42.11h"] = 0xf4bb480eu,
                ["ffu_43.12h"] = 0x2f5771f9u,
                ["fg-a.bin"] = 0x16a89b2cu,
                ["fg-b.bin"] = 0x22f2c097u,
                ["fg-c.bin"] = 0xd1dfcd2du,
                ["fg-d.bin"] = 0x4303f863u,
                ["fg-e.bin"] = 0xf8ccf27eu,
                ["fg-f.bin"] = 0xd96c76b2u,
                ["fun-s3-j1.u210"] = 0x6cfffb11u,
                ["fun-u18.bin"] = 0x84f9354fu,
                ["fun-u19.bin"] = 0x1a518609u,
                ["fun-u210.bin"] = 0x6cfffb11u,
                ["fun-u67.bin"] = 0x055b64f1u,
                ["fun-u68.bin"] = 0x0405f21fu,
                ["fun-u69.bin"] = 0x05dc2043u,
                ["fun-u70.bin"] = 0xa94a8b19u,
                ["g1.bin"] = 0x8069026fu,
                ["g2.bin"] = 0x745f0ebau,
                ["g3.bin"] = 0xfeda0f8bu,
                ["g4.bin"] = 0x11493e55u,
                ["gal20v8.68kadd"] = 0x27cdd376u,
                ["gal20v8a-1.bin"] = 0xcd99ca47u,
                ["gal20v8a-2.bin"] = 0x60d016b9u,
                ["gal20v8a-3.bin"] = 0x049b7f4fu,
                ["gbp63b.1a"] = 0x5077d37eu,
                ["gbpj_01.3a"] = 0xa7bea5bbu,
                ["gbpj_02.4a"] = 0x357b76ecu,
                ["gbpj_03.5a"] = 0xbcbc1881u,
                ["gbpj_04.6a"] = 0xb1126fdeu,
                ["gbpj_05.7a"] = 0xbb5be4b0u,
                ["gbpj_06.8a"] = 0x1be8fd86u,
                ["gbpj_07.9a"] = 0xdeb8ef02u,
                ["gbpj_08.10a"] = 0x9f90359du,
                ["gbpj_09.12a"] = 0xfc431024u,
                ["gbpj_18.11c"] = 0xcc54778du,
                ["gbpj_19.12c"] = 0xea8b56d8u,
                ["gbpj_23a.8f"] = 0x52ef2d85u,
                ["gbpr2.1a"] = 0x486e8ca0u,
                ["gfx10.040"] = 0x6a060c6cu,
                ["gfx10.bin"] = 0x763974c9u,
                ["gfx11.040"] = 0x13324965u,
                ["gfx11.bin"] = 0x22f2ec92u,
                ["gfx12.040"] = 0xc29f7b70u,
                ["gfx12.bin"] = 0xa3c205c1u,
                ["gfx13.040"] = 0x8e8db215u,
                ["gfx13.bin"] = 0x6d75a193u,
                ["gfx14.040"] = 0xf34a7f9du,
                ["gfx15.040"] = 0xa5e4f449u,
                ["gfx16.040"] = 0x49a3dfc7u,
                ["gfx6.bin"] = 0xa5e1c8a4u,
                ["gfx7.bin"] = 0xe9bd74f5u,
                ["gfx8.bin"] = 0x2b94287au,
                ["gfx9.040"] = 0xf8f33a0eu,
                ["gfx9.bin"] = 0x9b9a887au,
                ["grp1.u31"] = 0x6de44671u,
                ["grp2.u30"] = 0xbf0cd819u,
                ["grp3.u29"] = 0xe8f14362u,
                ["grp4.u28"] = 0x76f9f91fu,
                ["i.010.u11"] = 0xca403ac1u,
                ["ioa1"] = 0x59c7ee3bu,
                ["iob1.11d"] = 0x3abc0700u,
                ["iob1.11e"] = 0x3abc0700u,
                ["iob1.12d"] = 0x3abc0700u,
                ["iob1.12e"] = 0x3abc0700u,
                ["iob2.11d"] = 0xd26f0a27u,
                ["ioc1.ic1"] = 0xa399772du,
                ["ioc1.ic7"] = 0xa399772du,
                ["j1.bin"] = 0x1547e595u,
                ["j4.bin"] = 0x7a99446eu,
                ["kd-1m.3a"] = 0x5f74bf78u,
                ["kd-2m.3c"] = 0x9ef36604u,
                ["kd-3m.5a"] = 0x5e5303bfu,
                ["kd-4m.5c"] = 0x402b9b4fu,
                ["kd-5m.4a"] = 0xe45b8701u,
                ["kd-6m.4c"] = 0x113358f3u,
                ["kd-7m.6a"] = 0xa7750322u,
                ["kd-8m.6c"] = 0x38853c44u,
                ["kd22b.1a"] = 0xbd1a6035u,
                ["kd29b.1a"] = 0x6b892f82u,
                ["kd_05.7a"] = 0x5f74bf78u,
                ["kd_06.8a"] = 0xe45b8701u,
                ["kd_07.9a"] = 0x5e5303bfu,
                ["kd_08.10a"] = 0xa7750322u,
                ["kd_09.12a"] = 0xbac6ec26u,
                ["kd_1.4a"] = 0x5894399au,
                ["kd_10.5b"] = 0xe788ae96u,
                ["kd_11.7b"] = 0x147e3310u,
                ["kd_12.8b"] = 0xa6042aa2u,
                ["kd_13.9b"] = 0xb6685131u,
                ["kd_14.10b"] = 0x4840c5efu,
                ["kd_14.7c"] = 0x9ef36604u,
                ["kd_15.11b"] = 0x57359746u,
                ["kd_15.8c"] = 0x113358f3u,
                ["kd_16.12b"] = 0x63dcb7e0u,
                ["kd_16.9c"] = 0x402b9b4fu,
                ["kd_17.10c"] = 0x38853c44u,
                ["kd_17.5c"] = 0xdc9a83d3u,
                ["kd_18.11c"] = 0x4c63181du,
                ["kd_18.7c"] = 0x6ad3b2bbu,
                ["kd_19.12c"] = 0x92941b80u,
                ["kd_19.8c"] = 0xb1f30f7cu,
                ["kd_2.5a"] = 0xb022e3e3u,
                ["kd_20.9c"] = 0x01c1f399u,
                ["kd_21.10c"] = 0xce10d2c3u,
                ["kd_22.11c"] = 0x5ade98ebu,
                ["kd_23.13b"] = 0xbac6ec26u,
                ["kd_24.5e"] = 0x97008fdbu,
                ["kd_25.7e"] = 0x5d0fa853u,
                ["kd_26.8e"] = 0x57e5fab5u,
                ["kd_27.9e"] = 0x40d7bfedu,
                ["kd_28.10e"] = 0x3a424135u,
                ["kd_28.9e"] = 0x9367bcd9u,
                ["kd_29.10e"] = 0x0360fa72u,
                ["kd_29.11e"] = 0xa1eeac03u,
                ["kd_3.7a"] = 0x5d18bc83u,
                ["kd_30.12c"] = 0x4c63181du,
                ["kd_31.13c"] = 0x92941b80u,
                ["kd_32.8f"] = 0x1b2a802au,
                ["kd_33.6f"] = 0x9bd7ad4bu,
                ["kd_33.9f"] = 0x65c2bed6u,
                ["kd_34.10f"] = 0x9367bcd9u,
                ["kd_35.11f"] = 0x0360fa72u,
                ["kd_35.9f"] = 0x4ca6a48au,
                ["kd_36.10f"] = 0x3c66c32bu,
                ["kd_36a.10f"] = 0x95a3cef8u,
                ["kd_38.8h"] = 0x9c3dd2d1u,
                ["kd_39.9h"] = 0xd7920213u,
                ["kd_4.8a"] = 0x0ce0ba30u,
                ["kd_40.10h"] = 0x4ca6a48au,
                ["kd_41a.11h"] = 0x95a3cef8u,
                ["kd_5.9a"] = 0xc29b9ab3u,
                ["kd_6.10a"] = 0x519faee4u,
                ["kd_7.11a"] = 0x7fe03079u,
                ["kd_8.12a"] = 0xc69b77aeu,
                ["kd_9.12a"] = 0xbac6ec26u,
                ["kd_9.4b"] = 0x401a98e3u,
                ["kde_28.9e"] = 0x9367bcd9u,
                ["kde_29.10e"] = 0x6a0ba878u,
                ["kde_30.11e"] = 0xf8dc4ce3u,
                ["kde_30a.11e"] = 0xfcb5efe2u,
                ["kde_31.12e"] = 0x309debd8u,
                ["kde_31a.12e"] = 0xc710d722u,
                ["kde_35.9f"] = 0x4ca6a48au,
                ["kde_36.10f"] = 0xb509b39du,
                ["kde_37.11f"] = 0xd1276c1cu,
                ["kde_37a.11f"] = 0xf22e5266u,
                ["kde_38.12f"] = 0x76cd5738u,
                ["kde_38a.12f"] = 0x57d6ed3au,
                ["kdj_30a.11e"] = 0xebc788adu,
                ["kdj_31a.12e"] = 0xc710d722u,
                ["kdj_36a.12f"] = 0xebc788adu,
                ["kdj_37a.11f"] = 0xe55c3529u,
                ["kdj_37a.13f"] = 0xc710d722u,
                ["kdj_38a.12f"] = 0x57d6ed3au,
                ["kdj_42a.12h"] = 0xe55c3529u,
                ["kdj_43a.13h"] = 0x57d6ed3au,
                ["kdu_28.9e"] = 0x9367bcd9u,
                ["kdu_29.10e"] = 0x0360fa72u,
                ["kdu_30b.11e"] = 0x825817f9u,
                ["kdu_31b.12e"] = 0x9af36039u,
                ["kdu_35.9f"] = 0x4ca6a48au,
                ["kdu_36a.10f"] = 0x95a3cef8u,
                ["kdu_37b.11f"] = 0xd2422dfbu,
                ["kdu_38b.12f"] = 0xbe8405a1u,
                ["km418c256z-80.u210"] = 0x6cfffb11u,
                ["km6264-10.u133"] = 0x13ea1c44u,
                ["km6264b-10.u191"] = 0x6f07d2cbu,
                ["kr-1m.4a"] = 0xf095be2du,
                ["kr-2m.8a"] = 0x0200bc3du,
                ["kr-3m.6a"] = 0x179dfd96u,
                ["kr-4m.10a"] = 0x0bb2b4e7u,
                ["kr-5m.3a"] = 0x9e36c1a4u,
                ["kr-6m.7a"] = 0x1f4298d2u,
                ["kr-7m.5a"] = 0xc5832caeu,
                ["kr-8m.9a"] = 0x37fa8751u,
                ["kr22b.1a"] = 0xf15b2c0fu,
                ["kr63b.1a"] = 0xfd5b6522u,
                ["kr_01.3a"] = 0x9e36c1a4u,
                ["kr_01.4a"] = 0x40cecf5cu,
                ["kr_02.4a"] = 0xc5832caeu,
                ["kr_02.5a"] = 0xb54612e3u,
                ["kr_03.5a"] = 0xf095be2du,
                ["kr_03.7a"] = 0xea10db07u,
                ["kr_04.6a"] = 0x179dfd96u,
                ["kr_04.8a"] = 0xe30c8388u,
                ["kr_05.7a"] = 0x1f4298d2u,
                ["kr_05.9a"] = 0x5b8a615bu,
                ["kr_06.10a"] = 0xecb1a09au,
                ["kr_06.8a"] = 0x37fa8751u,
                ["kr_07.11a"] = 0x6af10648u,
                ["kr_07.9a"] = 0x0200bc3du,
                ["kr_08.10a"] = 0x0bb2b4e7u,
                ["kr_08.12a"] = 0xd310c9e8u,
                ["kr_09.11a"] = 0x5e44d9eeu,
                ["kr_09.12a"] = 0x5e44d9eeu,
                ["kr_09.4b"] = 0x08b76e10u,
                ["kr_10.5b"] = 0x37006d66u,
                ["kr_11.7b"] = 0xa967ceb3u,
                ["kr_12.8b"] = 0x03d945b1u,
                ["kr_13.9b"] = 0x435aaa03u,
                ["kr_14.10b"] = 0x0ae88766u,
                ["kr_15.11b"] = 0x8140b83bu,
                ["kr_16.12b"] = 0x40c39d1bu,
                ["kr_17.5c"] = 0xb171c968u,
                ["kr_18.11c"] = 0xda69d15fu,
                ["kr_18.7c"] = 0x09fa14a5u,
                ["kr_19.12c"] = 0xbfc654e9u,
                ["kr_19.8c"] = 0x029f4abeu,
                ["kr_20.9c"] = 0xbd4bffb8u,
                ["kr_21.10c"] = 0x01b35065u,
                ["kr_22.11c"] = 0xfd351922u,
                ["kr_22.7f"] = 0xd0b671a9u,
                ["kr_23.13b"] = 0x5e44d9eeu,
                ["kr_23e.8f"] = 0x1b3997ebu,
                ["kr_23j.8f"] = 0xeae7417fu,
                ["kr_23u.8f"] = 0x252bc2bau,
                ["kr_24.5e"] = 0xde65153eu,
                ["kr_25.7e"] = 0x9aace189u,
                ["kr_26.8e"] = 0x8865d86bu,
                ["kr_27.9e"] = 0x3e041444u,
                ["kr_28.10e"] = 0x5f84f92fu,
                ["kr_29.11e"] = 0x1387a076u,
                ["kr_30.12c"] = 0xda69d15fu,
                ["kr_31.13c"] = 0xbfc654e9u,
                ["kr_32.8f"] = 0x87380dddu,
                ["kr_33.9f"] = 0x11803e95u,
                ["kr_34.10f"] = 0xfe6eb08du,
                ["kr_35.11f"] = 0xf854b020u,
                ["kr_38.8h"] = 0xf4466bf4u,
                ["kr_39.9h"] = 0xfd8a9aebu,
                ["kr_40.10h"] = 0x1172806du,
                ["kr_41.11h"] = 0xeb52e78du,
                ["kr_gfx1.rom"] = 0x9e36c1a4u,
                ["kr_gfx2.rom"] = 0xf095be2du,
                ["kr_gfx3.rom"] = 0xc5832caeu,
                ["kr_gfx4.rom"] = 0x179dfd96u,
                ["kr_gfx5.rom"] = 0x1f4298d2u,
                ["kr_gfx6.rom"] = 0x0200bc3du,
                ["kr_gfx7.rom"] = 0x37fa8751u,
                ["kr_gfx8.rom"] = 0x0bb2b4e7u,
                ["krj_36.12f"] = 0xad3d1a8eu,
                ["krj_37.13f"] = 0x85596094u,
                ["krj_42.12h"] = 0xe694a491u,
                ["krj_43.13h"] = 0x9198bf8fu,
                ["l.bin"] = 0x698e8b58u,
                ["left.code.040"] = 0x95d00a7eu,
                ["lk.19.c13"] = 0xbeade53fu,
                ["lw-01.9d"] = 0x0318f298u,
                ["lw-02.12d"] = 0x43e6c5c8u,
                ["lw-02.6b"] = 0x43e6c5c8u,
                ["lw-03.14c"] = 0xce2159e7u,
                ["lw-03u.12e"] = 0x807d051fu,
                ["lw-03u.14c"] = 0x807d051fu,
                ["lw-04.13c"] = 0x39305536u,
                ["lw-04u.13c"] = 0xe6cd098eu,
                ["lw-04u.13e"] = 0xe6cd098eu,
                ["lw-05.6d"] = 0xe4552fd7u,
                ["lw-05.9e"] = 0xe4552fd7u,
                ["lw-06.12e"] = 0x5b9edffcu,
                ["lw-06.9d"] = 0x5b9edffcu,
                ["lw-07.10g"] = 0xfd252a26u,
                ["lw-07.13e"] = 0xfd252a26u,
                ["lw-08.9b"] = 0x25a8e43cu,
                ["lw-08.9f"] = 0x25a8e43cu,
                ["lw-09.12f"] = 0x899cb4adu,
                ["lw-12.9g"] = 0x8e6a832bu,
                ["lw-13.10d"] = 0x8e058ef5u,
                ["lw-13.12g"] = 0x8e058ef5u,
                ["lw10.13f"] = 0xbea45994u,
                ["lw10c.13f"] = 0x8f5ea3f5u,
                ["lw10e.13f"] = 0x3ce81dbeu,
                ["lw11.12f"] = 0x73e920b7u,
                ["lw11c.12f"] = 0xe62742b6u,
                ["lw11c.14f"] = 0xe62742b6u,
                ["lw11e.14f"] = 0x82656910u,
                ["lw14.13h"] = 0x539b2339u,
                ["lw14c.13g"] = 0x708e7472u,
                ["lw14c.13h"] = 0x708e7472u,
                ["lw14e.13g"] = 0x472eaad1u,
                ["lw15.12h"] = 0x50d7012du,
                ["lw15c.12h"] = 0x1b70f216u,
                ["lw15c.14g"] = 0x1b70f216u,
                ["lw15e.14g"] = 0xfb1e2bd0u,
                ["lw40.12f"] = 0x73e920b7u,
                ["lw41.12h"] = 0x58210b9eu,
                ["lw42.13f"] = 0xbea45994u,
                ["lw43.13h"] = 0x539b2339u,
                ["lw621.1a"] = 0x5eec6ce9u,
                ["lw_00.13c"] = 0x59df2a63u,
                ["lw_00.14a"] = 0x59df2a63u,
                ["lw_00b.14a"] = 0x59df2a63u,
                ["lw_1.2a"] = 0x65f41485u,
                ["lw_10.13f"] = 0x23bca4d5u,
                ["lw_10c.13f"] = 0xc46479d7u,
                ["lw_11.14f"] = 0x61e2cc56u,
                ["lw_11c.14f"] = 0x67e42546u,
                ["lw_13.10a"] = 0xb81c0e96u,
                ["lw_14.10b"] = 0x82862cceu,
                ["lw_14.13g"] = 0x3a023771u,
                ["lw_14c.13g"] = 0x97670f4au,
                ["lw_15.11a"] = 0x1b7d2e07u,
                ["lw_15.14g"] = 0x8a0c18d3u,
                ["lw_15c.14g"] = 0x402e2a46u,
                ["lw_16.11b"] = 0x40b26554u,
                ["lw_17.5c"] = 0xc5eea115u,
                ["lw_18.5e"] = 0xb4b6241bu,
                ["lw_19.7c"] = 0x15af8440u,
                ["lw_2.2b"] = 0x4bd75feeu,
                ["lw_20.7e"] = 0xdf1a3665u,
                ["lw_25.10c"] = 0xbac91554u,
                ["lw_26.10e"] = 0x57bcd032u,
                ["lw_27.11c"] = 0x103c1bd2u,
                ["lw_28.11e"] = 0xa805ad30u,
                ["lw_29.8f"] = 0x7bda1ac6u,
                ["lw_3.3a"] = 0xc03ef278u,
                ["lw_30.8h"] = 0xb385954eu,
                ["lw_31.9f"] = 0xc49d37fbu,
                ["lw_32.9h"] = 0x30967a15u,
                ["lw_37.13c"] = 0x59df2a63u,
                ["lw_4.3b"] = 0x50cf757fu,
                ["lwchr.3a"] = 0x54ed4c39u,
                ["lwio.11e"] = 0xad52b90cu,
                ["lwio.12b"] = 0xad52b90cu,
                ["lwio.12c"] = 0xad52b90cu,
                ["lwio.12e"] = 0xad52b90cu,
                ["lwio.15e"] = 0xad52b90cu,
                ["lwio.8i"] = 0xad52b90cu,
                ["lwu_00.14a"] = 0x59df2a63u,
                ["lwu_10a.13f"] = 0x8cb38c81u,
                ["lwu_10aa.13f"] = 0xbea45994u,
                ["lwu_11a.14f"] = 0xddf78831u,
                ["lwu_11aa.14f"] = 0x73e920b7u,
                ["lwu_14a.13g"] = 0xd70ef9fdu,
                ["lwu_14aa.13g"] = 0x539b2339u,
                ["lwu_15a.14g"] = 0xf7ce2097u,
                ["lwu_15aa.14g"] = 0xe47524b9u,
                ["m12073-1"] = 0x24ce197bu,
                ["m12073-2"] = 0xc8dcaa95u,
                ["m12073-3"] = 0xefc17c9au,
                ["m12073-4"] = 0x219fd7e2u,
                ["m12073-5"] = 0x90c93dd2u,
                ["m12073-6"] = 0x9d20ef9bu,
                ["m12223-1"] = 0x8425ff6bu,
                ["m12223-2"] = 0x1ab0000cu,
                ["m1_074733.bin"] = 0xfbb43b64u,
                ["m48t35y-70pc1.9n"] = 0x96107b4au,
                ["m5m27c401.u196"] = 0x39f15a1eu,
                ["m5m27c401.u222"] = 0x03991fbau,
                ["ma12073.4mm"] = 0xac421276u,
                ["mb-10m.3c"] = 0x97976ff5u,
                ["mb-11m.4c"] = 0x8fb94743u,
                ["mb-12m.5c"] = 0xb350a840u,
                ["mb-13m.6c"] = 0xda810d5fu,
                ["mb-1m.3a"] = 0x41468e06u,
                ["mb-2m.4a"] = 0x2ffbfea8u,
                ["mb-3m.5a"] = 0xf453aa9eu,
                ["mb-4m.6a"] = 0x1eb9841du,
                ["mb-5m.7a"] = 0x506b9dc9u,
                ["mb-6m.8a"] = 0xb76c70e9u,
                ["mb-7m.9a"] = 0xaff8c2fbu,
                ["mb-8m.10a"] = 0xe60c9556u,
                ["mb-q1.1k"] = 0x0630c3ceu,
                ["mb-q2.2k"] = 0x354f9c21u,
                ["mb-q3.3k"] = 0x7838487cu,
                ["mb-q4.4k"] = 0xab66e087u,
                ["mb-q5.1m"] = 0xc789fef2u,
                ["mb-q6.2m"] = 0xecb81b61u,
                ["mb-q7.3m"] = 0x041e49bau,
                ["mb-q8.4m"] = 0x59fe702au,
                ["mb63b.1a"] = 0xb8392f02u,
                ["mb_01.3a"] = 0xa53b1c81u,
                ["mb_02.4a"] = 0x23fe10f6u,
                ["mb_03.5a"] = 0xcb866c2fu,
                ["mb_04.6a"] = 0xc9143e75u,
                ["mb_05.7a"] = 0x506b9dc9u,
                ["mb_06.8a"] = 0xaff8c2fbu,
                ["mb_07.9a"] = 0xb76c70e9u,
                ["mb_08.10a"] = 0xe60c9556u,
                ["mb_10.3c"] = 0x97976ff5u,
                ["mb_11.4c"] = 0xb350a840u,
                ["mb_12.5c"] = 0x8fb94743u,
                ["mb_13.6c"] = 0xda810d5fu,
                ["mb_q.5k"] = 0xd6fa76d1u,
                ["mb_qa.5k"] = 0xe21a03c4u,
                ["mbde_20.5f"] = 0xb8b2139bu,
                ["mbde_21.6f"] = 0x690c026au,
                ["mbde_24.9e"] = 0xc20895a5u,
                ["mbde_25.10e"] = 0x9bdb6b11u,
                ["mbde_26.11e"] = 0x72b7451cu,
                ["mbde_27.12e"] = 0x4086f534u,
                ["mbde_28.9f"] = 0x2618d5e1u,
                ["mbde_29.10f"] = 0x3f52d5e5u,
                ["mbde_30.11f"] = 0xa036dc16u,
                ["mbde_31.12f"] = 0x085f47f0u,
                ["mbdj_20.5f"] = 0xb8b2139bu,
                ["mbdj_21.6f"] = 0x690c026au,
                ["mbdj_24.9e"] = 0xc20895a5u,
                ["mbdj_25.10e"] = 0x9bdb6b11u,
                ["mbdj_26.11e"] = 0x72b7451cu,
                ["mbdj_27.12e"] = 0x4086f534u,
                ["mbdj_28.9f"] = 0x2618d5e1u,
                ["mbdj_29.10f"] = 0x3f52d5e5u,
                ["mbdj_30.11f"] = 0xbeff31cfu,
                ["mbdj_31.12f"] = 0x085f47f0u,
                ["mbe_20a.5f"] = 0xaeb557b0u,
                ["mbe_21a.6f"] = 0xd5007b05u,
                ["mbe_23e.8f"] = 0x5394057au,
                ["mbe_24b.9e"] = 0x95d5e729u,
                ["mbe_25b.10e"] = 0xa50d3fd4u,
                ["mbe_28b.9f"] = 0xb1c7cbcbu,
                ["mbe_29b.10f"] = 0x08e32e56u,
                ["mbj_20a.5f"] = 0xaeb557b0u,
                ["mbj_21a.6f"] = 0xd5007b05u,
                ["mbj_22b.7f"] = 0xacd38478u,
                ["mbj_23e.8f"] = 0x0d06036au,
                ["mbu_20a.5f"] = 0xfc848af5u,
                ["mbu_21a.6f"] = 0xd5007b05u,
                ["mbu_23e.8f"] = 0x224f0062u,
                ["mbu_24b.9e"] = 0x95d5e729u,
                ["mbu_25b.10e"] = 0xa50d3fd4u,
                ["mbu_28b.9f"] = 0xb1c7cbcbu,
                ["mbu_29b.10f"] = 0x08e32e56u,
                ["moon-1.c173.u30"] = 0x7e36ec84u,
                ["moon-2.c132.u29"] = 0x66403570u,
                ["mpa_01.3a"] = 0x7c8c0c22u,
                ["mpa_02.4a"] = 0x23f95339u,
                ["mpa_03.5a"] = 0x107842a6u,
                ["mpa_04.6a"] = 0xfce457aeu,
                ["mpa_05.7a"] = 0xba8f3585u,
                ["mpa_06.8a"] = 0x037f20ccu,
                ["mpa_07.9a"] = 0xba8f3585u,
                ["mpa_08.10a"] = 0x037f20ccu,
                ["mpa_09.12a"] = 0x0b5b1b72u,
                ["mpa_10.3c"] = 0x870f3a2au,
                ["mpa_11.4c"] = 0x8923fc3au,
                ["mpa_12.5c"] = 0x87b88629u,
                ["mpa_13.6c"] = 0xa09a6acfu,
                ["mpa_18.11c"] = 0xcef6d39eu,
                ["mpa_19.12c"] = 0x24947f8eu,
                ["mpa_23.8f"] = 0x38b9883au,
                ["mrnj_01.3a"] = 0x3f878020u,
                ["mrnj_02.4a"] = 0x3e5624d8u,
                ["mrnj_03.5a"] = 0xd1e61f96u,
                ["mrnj_04.6a"] = 0xd241971bu,
                ["mrnj_05.7a"] = 0xc0a14562u,
                ["mrnj_06.8a"] = 0xe6a71dfcu,
                ["mrnj_07.9a"] = 0x99afb6c7u,
                ["mrnj_08.10a"] = 0x52882c20u,
                ["mrnj_09.12a"] = 0x62470d72u,
                ["mrnj_18.11c"] = 0x08e13940u,
                ["mrnj_19.12c"] = 0x5fa59927u,
                ["mrnj_23d.8f"] = 0xf929be72u,
                ["ms-1m.3a"] = 0x0d2bbe00u,
                ["ms-32m.8h"] = 0x2475ddfcu,
                ["ms-3m.5a"] = 0x3a1a5bf4u,
                ["ms-5m.7a"] = 0xc00fe7e2u,
                ["ms-7m.9a"] = 0x4ccacac5u,
                ["ms22b.1a"] = 0xdde86cb0u,
                ["ms24b.1a"] = 0x636dbe6du,
                ["ms6.u10"] = 0xed4186bdu,
                ["ms6.u133"] = 0x13ea1c44u,
                ["ms6.u18"] = 0x2ddfe46eu,
                ["ms6.u19"] = 0x39d763d3u,
                ["ms6.u191"] = 0x08f6b60eu,
                ["ms6.u196"] = 0x596609d4u,
                ["ms6.u210"] = 0x6cfffb11u,
                ["ms6.u29"] = 0xe4eca601u,
                ["ms6.u31"] = 0x35486f2du,
                ["ms6.u64"] = 0x8165f536u,
                ["ms6.u68"] = 0x8edff95au,
                ["ms6.u69"] = 0x468962b1u,
                ["ms6.u70"] = 0xbaa0f81fu,
                ["ms6_gal16v8.u173"] = 0x32dec205u,
                ["ms6_gal16v8.u176"] = 0xdeb37f27u,
                ["ms6_gal16v8.u198"] = 0xcd1246feu,
                ["ms6_gal20v8.u104"] = 0x67b56d29u,
                ["ms6_gal20v8.u234"] = 0x2c16b7c6u,
                ["ms6_gal22v10.u134"] = 0xb66848bbu,
                ["ms6_gal22v10.u50"] = 0xdc665408u,
                ["ms6b.44"] = 0x5f05a861u,
                ["ms6b.u0"] = 0xb6f3724bu,
                ["ms6b.u10"] = 0xc812b7b2u,
                ["ms6b.u196"] = 0x435153d5u,
                ["ms6c.44"] = 0x8ceec769u,
                ["ms6c.u0"] = 0x04088b61u,
                ["ms_01.4a"] = 0xf7ab1b88u,
                ["ms_02.5a"] = 0xd071a405u,
                ["ms_05.9a"] = 0xf62c2369u,
                ["ms_06.10a"] = 0xd3ce2a91u,
                ["ms_09.12b"] = 0x57b29519u,
                ["ms_09.4b"] = 0x4adee6f6u,
                ["ms_10.5b"] = 0xf02c0718u,
                ["ms_13.9b"] = 0xe01adc4bu,
                ["ms_14.10b"] = 0xdfb2e4dfu,
                ["ms_17.5c"] = 0x0bc1665fu,
                ["ms_18.11c"] = 0xfb64e90du,
                ["ms_18.7c"] = 0x1ba76df2u,
                ["ms_19.12c"] = 0x74f892b9u,
                ["ms_23.13b"] = 0x57b29519u,
                ["ms_24.5e"] = 0xbe64a3a1u,
                ["ms_25.7e"] = 0x0f199d56u,
                ["ms_30.11f"] = 0x21c1f078u,
                ["ms_30.12c"] = 0xfb64e90du,
                ["ms_31.12f"] = 0xd7e762b5u,
                ["ms_31.13c"] = 0x74f892b9u,
                ["ms_32.8f"] = 0x3d89c530u,
                ["ms_33.9f"] = 0xce25defcu,
                ["ms_34.10f"] = 0x0e59a62du,
                ["ms_35.11f"] = 0x03da99d1u,
                ["ms_35.11h"] = 0xa540a73au,
                ["ms_36.12h"] = 0x66f2dcdbu,
                ["ms_38.8h"] = 0x904a2ed5u,
                ["ms_39.9h"] = 0x01efce86u,
                ["ms_40.10h"] = 0xbabade3au,
                ["ms_41.11h"] = 0xfadf99eau,
                ["mse_30.11f"] = 0x03fc8dbcu,
                ["mse_31.12f"] = 0x30332bcfu,
                ["mse_35.11h"] = 0xd5bf66cdu,
                ["mse_36.12h"] = 0x8f7d6ce9u,
                ["msj_36.12f"] = 0x04f0ef50u,
                ["msj_37.13f"] = 0x6c060d70u,
                ["msj_42.12h"] = 0x9fcbb9cdu,
                ["msj_43.13h"] = 0xaec77787u,
                ["msu_30.11f"] = 0xd963c816u,
                ["msu_31.12f"] = 0x20cd7904u,
                ["msu_35.11h"] = 0x72f179b3u,
                ["msu_36.12h"] = 0xbf88c080u,
                ["mx29f1610mcpsop44.u18"] = 0x69cd2a53u,
                ["mx29f1610mcpsop44.u19a"] = 0x4a0cebaau,
                ["n.010.u12"] = 0x275b67acu,
                ["nm-1m.3a"] = 0x9e878024u,
                ["nm-32m.8h"] = 0xd6d1add3u,
                ["nm-3m.5a"] = 0xbb01e6b6u,
                ["nm-5m.7a"] = 0x487b8747u,
                ["nm-7m.9a"] = 0x203dc8c6u,
                ["nm22b.1a"] = 0x378881e1u,
                ["nm24b.1a"] = 0x7b25bac6u,
                ["nm_01.4a"] = 0x8a83f7c4u,
                ["nm_02.5a"] = 0x84c69469u,
                ["nm_05.9a"] = 0x16db1e61u,
                ["nm_06.10a"] = 0x8b9bcf95u,
                ["nm_09.4b"] = 0x9d60d286u,
                ["nm_10.5b"] = 0x33c1388cu,
                ["nm_13.9b"] = 0xa4909fe0u,
                ["nm_14.10b"] = 0x66612270u,
                ["nm_17.5c"] = 0xccfc50e2u,
                ["nm_18.7c"] = 0x4347deedu,
                ["nm_23.13b"] = 0x8d3c5a42u,
                ["nm_24.5e"] = 0x3312c648u,
                ["nm_25.7e"] = 0xacfc84d2u,
                ["nm_30.12c"] = 0xbab333d4u,
                ["nm_31.13c"] = 0x2650a0a8u,
                ["nm_32.8f"] = 0xb3704ddeu,
                ["nm_33.9f"] = 0xc469dc74u,
                ["nm_34.10f"] = 0x5737feedu,
                ["nm_35.11f"] = 0xbd11a7f8u,
                ["nm_38.8h"] = 0xae98a997u,
                ["nm_39.9h"] = 0x6a274ecdu,
                ["nm_40.10h"] = 0x8a4099f3u,
                ["nm_41.11h"] = 0x6309603du,
                ["nme_09.12b"] = 0x0f4b0581u,
                ["nme_18.11c"] = 0xbab333d4u,
                ["nme_19.12c"] = 0x2650a0a8u,
                ["nme_30.11f"] = 0x71b333dbu,
                ["nme_30a.11f"] = 0xd2c03e56u,
                ["nme_31.12f"] = 0x7e83dbd2u,
                ["nme_31a.12f"] = 0xb2bd4f6fu,
                ["nme_35.11h"] = 0xd153bc18u,
                ["nme_35a.11h"] = 0x5fd31661u,
                ["nme_36.12h"] = 0x6aeeec81u,
                ["nme_36a.12h"] = 0xee9450e3u,
                ["nmj_01.4a"] = 0x8a83f7c4u,
                ["nmj_02.5a"] = 0x84c69469u,
                ["nmj_05.9a"] = 0x16db1e61u,
                ["nmj_06.10a"] = 0x8b9bcf95u,
                ["nmj_09.4b"] = 0x9d60d286u,
                ["nmj_10.5b"] = 0x33c1388cu,
                ["nmj_13.9b"] = 0xa4909fe0u,
                ["nmj_14.10b"] = 0x66612270u,
                ["nmj_17.5c"] = 0xccfc50e2u,
                ["nmj_18.7c"] = 0x4347deedu,
                ["nmj_23.13b"] = 0x8d3c5a42u,
                ["nmj_24.5e"] = 0x3312c648u,
                ["nmj_25.7e"] = 0xacfc84d2u,
                ["nmj_30.12c"] = 0xbab333d4u,
                ["nmj_31.13c"] = 0x2650a0a8u,
                ["nmj_32.8f"] = 0xb3704ddeu,
                ["nmj_33.9f"] = 0xc469dc74u,
                ["nmj_34.10f"] = 0x5737feedu,
                ["nmj_35.11f"] = 0xbd11a7f8u,
                ["nmj_36a.12f"] = 0xdaeceabbu,
                ["nmj_37a.13f"] = 0x619068b6u,
                ["nmj_38.8h"] = 0xae98a997u,
                ["nmj_39.9h"] = 0x6a274ecdu,
                ["nmj_40.10h"] = 0x8a4099f3u,
                ["nmj_41.11h"] = 0x6309603du,
                ["nmj_42a.12h"] = 0x55024740u,
                ["nmj_43a.13h"] = 0xa948a53bu,
                ["o.u191"] = 0x08f6b60eu,
                ["o224b.1a"] = 0xc211c8cdu,
                ["pa3-01m.2c"] = 0x068a152cu,
                ["pa3-07m.2f"] = 0x3a4a619du,
                ["pa3_05.10d"] = 0x73a10d5du,
                ["pa3_06.11d"] = 0xaffa4f82u,
                ["pa3_11.11f"] = 0xcb1423a2u,
                ["pa3e_16.10l"] = 0x1be9a483u,
                ["pa3e_16a.10l"] = 0x7169ea67u,
                ["pa3e_17.11l"] = 0xd7041d32u,
                ["pa3e_17a.11l"] = 0xa213fa80u,
                ["pa3j_16.10l"] = 0xca1d7897u,
                ["pa3j_17.11l"] = 0x21f6e51fu,
                ["pa3w_16.10l"] = 0xd1ba585cu,
                ["pa3w_17.11l"] = 0x12138234u,
                ["pal16l8.11e"] = 0x27617943u,
                ["pal16v8.13e"] = 0x5406caf1u,
                ["pal16v8.1a"] = 0x78c3161fu,
                ["pal22v10.j8"] = 0xa9445f88u,
                ["palce16v8h-1.bin"] = 0x48253c66u,
                ["palce16v8h-2.bin"] = 0x9ae375bau,
                ["palce16v8h-3.bin"] = 0xb0f10adfu,
                ["pd1.bin"] = 0x8208c0d7u,
                ["pd2.bin"] = 0xd8325c94u,
                ["pf1-2-sg076.bin"] = 0x1d15bc7au,
                ["pf4 sh058.ic89"] = 0x16289710u,
                ["pf4-sg072.bin"] = 0x16289710u,
                ["pf4-sg072.ic90"] = 0x446575c7u,
                ["pf4-sh058.ic90"] = 0x446575c7u,
                ["pf5 sh036.ic90"] = 0x0a6be48bu,
                ["pf5-sg063.ic91"] = 0x0a6be48bu,
                ["pf5-sg095.bin"] = 0x0a6be48bu,
                ["pf5-sh036.ic91"] = 0x0a6be48bu,
                ["pf6 sh070.ic88"] = 0x9b5b09d7u,
                ["pf6-sg068.bin"] = 0x9b5b09d7u,
                ["pf6-sg070.ic86"] = 0x9b5b09d7u,
                ["pf6-sh071.ic86"] = 0x9b5b09d7u,
                ["pf7 sh072.ic92"] = 0xfb78022eu,
                ["pf7-sg103.bin"] = 0xfb78022eu,
                ["pf7-sg103.ic88"] = 0xfb78022eu,
                ["pf7-sh072.ic88"] = 0xfb78022eu,
                ["pf8 sh074.ic93"] = 0x6258c7cfu,
                ["pf8-sg101.bin"] = 0x6258c7cfu,
                ["pf8-sg101.ic93"] = 0x6258c7cfu,
                ["pf8-sh074.ic93"] = 0x6258c7cfu,
                ["pf9 sh001.ic91"] = 0x9f25090eu,
                ["pf9-sh001.bin"] = 0x9f25090eu,
                ["pf9-sh001.ic84"] = 0x9f25090eu,
                ["pf9-sh065.ic84"] = 0x9f25090eu,
                ["pgm0h.4"] = 0xb800c1beu,
                ["pgm0l.3"] = 0xa39f50d2u,
                ["pic16c55"] = 0xf22e2311u,
                ["pic16c57-rp"] = 0x5a6d393cu,
                ["pic16c57-xt-p.bin"] = 0xaeae5cccu,
                ["pic16c57-xt.hex"] = 0xa6a5eac4u,
                ["pic16c57.bin"] = 0x22e1a720u,
                ["pic_u33.bin"] = 0x6dba4094u,
                ["pnij_01.4a"] = 0x01a0f311u,
                ["pnij_02.5a"] = 0x0e21fc33u,
                ["pnij_05.9a"] = 0x8c515dc0u,
                ["pnij_06.10a"] = 0x79f4bfe3u,
                ["pnij_09.4b"] = 0x48177b0au,
                ["pnij_10.5b"] = 0xc2acc171u,
                ["pnij_13.9b"] = 0x406451b0u,
                ["pnij_14.10b"] = 0x7fe59b19u,
                ["pnij_17.13b"] = 0xe86f787au,
                ["pnij_18.5c"] = 0xf17a0e56u,
                ["pnij_19.7c"] = 0xaf08b230u,
                ["pnij_24.12c"] = 0x5092257du,
                ["pnij_25.13c"] = 0x22109aaau,
                ["pnij_26.5e"] = 0xe2af981eu,
                ["pnij_27.7e"] = 0x83d5cb0eu,
                ["pnij_32.8f"] = 0x84560befu,
                ["pnij_33.9f"] = 0x3ed2c680u,
                ["pnij_36.12f"] = 0x2d4ffb2bu,
                ["pnij_38.8h"] = 0xeb75bd8cu,
                ["pnij_39.9h"] = 0x70fbe579u,
                ["pnij_42.12h"] = 0xc085dfafu,
                ["prg1"] = 0xf1129744u,
                ["prg1.bin"] = 0xd7b13f39u,
                ["prg2"] = 0x4386879au,
                ["prg2.bin"] = 0x665a5485u,
                ["prg3.bin"] = 0x8c2593acu,
                ["prg4.bin"] = 0xc3151563u,
                ["prg_0.bin"] = 0xcbb4062cu,
                ["prg_1.bin"] = 0xe434b882u,
                ["prg_2.bin"] = 0x2ce2fc75u,
                ["prg_3.bin"] = 0x0a93b43eu,
                ["prg_4.bin"] = 0x80b6dfb3u,
                ["prg_5.bin"] = 0xbfa503e7u,
                ["prg_6.bin"] = 0x977d2d34u,
                ["prg_7.bin"] = 0x65b0d8fdu,
                ["prh2.u222"] = 0xfff85f9bu,
                ["prl1.u196"] = 0x65c28bc9u,
                ["ps-1m.3a"] = 0x77b7ccabu,
                ["ps-2m.4a"] = 0x64fa58d4u,
                ["ps-3m.5a"] = 0x0122720bu,
                ["ps-4m.6a"] = 0x60da42c8u,
                ["ps-5m.7a"] = 0xc54ea839u,
                ["ps-6m.8a"] = 0xa544f4ccu,
                ["ps-7m.9a"] = 0x04c5acbdu,
                ["ps-8m.10a"] = 0x8f02f436u,
                ["ps-q1.1k"] = 0x31fd8726u,
                ["ps-q2.2k"] = 0x980a9eefu,
                ["ps-q3.3k"] = 0x0dd44491u,
                ["ps-q4.4k"] = 0xbed42f03u,
                ["ps63b.1a"] = 0x03a758b0u,
                ["ps_01.3a"] = 0x77b7ccabu,
                ["ps_02.4a"] = 0x0122720bu,
                ["ps_03.5a"] = 0x64fa58d4u,
                ["ps_04.6a"] = 0x60da42c8u,
                ["ps_05.7a"] = 0xc54ea839u,
                ["ps_06.8a"] = 0x04c5acbdu,
                ["ps_07.9a"] = 0xa544f4ccu,
                ["ps_08.10a"] = 0x8f02f436u,
                ["ps_21.6f"] = 0x8affa5a9u,
                ["ps_gfx5.rom"] = 0xc54ea839u,
                ["ps_gfx6.rom"] = 0xa544f4ccu,
                ["ps_gfx7.rom"] = 0x04c5acbdu,
                ["ps_gfx8.rom"] = 0x8f02f436u,
                ["ps_q.5k"] = 0x49ff4446u,
                ["psb-a.rom"] = 0x57f0f5e3u,
                ["psb-b.rom"] = 0xd9eb867eu,
                ["psb2a.rom"] = 0xd7b13f39u,
                ["psb3b.rom"] = 0x90113db4u,
                ["psb4a.rom"] = 0x665a5485u,
                ["psb5b.rom"] = 0x58f42c05u,
                ["pse_24.9e"] = 0x0f434414u,
                ["pse_25.10e"] = 0xb77102e2u,
                ["pse_26.11e"] = 0x389a99d2u,
                ["pse_27.12e"] = 0x3eb181c3u,
                ["pse_28.9f"] = 0xb732345du,
                ["pse_29.10f"] = 0xec037bceu,
                ["pse_30.11f"] = 0x68fb06acu,
                ["pse_31.12f"] = 0x37108e7bu,
                ["psh_24.9e"] = 0xfaa14841u,
                ["psh_25.10e"] = 0x724fdfdau,
                ["psh_26.11e"] = 0x6ad2bb83u,
                ["psh_27.12e"] = 0x579f4fd3u,
                ["psh_28.9f"] = 0x5c5b1f20u,
                ["psh_29.10f"] = 0x779cf901u,
                ["psh_30.11f"] = 0x058d3659u,
                ["psh_31.12f"] = 0x2c9f70b5u,
                ["psj_21.6f"] = 0x8affa5a9u,
                ["psj_22.7f"] = 0xe01036bcu,
                ["psj_23.8f"] = 0x6b2fda52u,
                ["psu_24.9e"] = 0x1cfecad7u,
                ["psu_25.10e"] = 0xc51acc94u,
                ["psu_26.11e"] = 0x9236d121u,
                ["psu_27.12e"] = 0x61c960a1u,
                ["psu_28.9f"] = 0xbdf921c1u,
                ["psu_29.10f"] = 0x52dce1cau,
                ["psu_30.11f"] = 0x8320e501u,
                ["psu_31.12f"] = 0x78d4c298u,
                ["pu11256.bin"] = 0x6581faeau,
                ["pu13478.bin"] = 0x61613de4u,
                ["q5 - 01_91634b.3a"] = 0x09d0e7ceu,
                ["q5 - 02_91634b.4a"] = 0x22e4ce9au,
                ["q5 - 03_91634b.5a"] = 0xf7b3aed6u,
                ["q5 - 04_91634b.6a"] = 0x520c6c88u,
                ["q5 - 05_90629b.7a"] = 0xf7b3aed6u,
                ["q5 - 06_90629b.8a"] = 0x09d0e7ceu,
                ["q5 - 07_90629b.9a"] = 0x520c6c88u,
                ["q5 - 08_90629b.10a"] = 0x22e4ce9au,
                ["q5 - 09_90629b.12a"] = 0xe14dc524u,
                ["q5 - 09_91634b.12a"] = 0xe14dc524u,
                ["q5 - 18_90629b.11c"] = 0xd10c1b68u,
                ["q5 - 18_91634b.11c"] = 0xd10c1b68u,
                ["q5 - 19_90629b.12c"] = 0x7d17e496u,
                ["q5 - 19_91634b.12c"] = 0x7d17e496u,
                ["q5 - 22_91634b.7f"] = 0x93248458u,
                ["q5 - 23_91634b.8f"] = 0x709f577fu,
                ["q5 - 33_90629b.6f"] = 0x93248458u,
                ["q5 - 34_90629b.8f"] = 0xde54487fu,
                ["q522b.1a"] = 0x0a1527abu,
                ["q5_01.4a"] = 0xc5453f56u,
                ["q5_02.5a"] = 0xa2cadcbeu,
                ["q5_05.9a"] = 0x143e068fu,
                ["q5_06.10a"] = 0xc92a91fcu,
                ["q5_09.4b"] = 0x48496d80u,
                ["q5_10.5b"] = 0x119e5e93u,
                ["q5_13.9b"] = 0xc741ac52u,
                ["q5_14.10b"] = 0xa8755f82u,
                ["q5_17.5c"] = 0xbd3b4d11u,
                ["q5_18.7c"] = 0xc57da03cu,
                ["q5_23.13b"] = 0xe14dc524u,
                ["q5_24.5e"] = 0xb419d139u,
                ["q5_25.7e"] = 0x979237cbu,
                ["q5_30.12c"] = 0xd10c1b68u,
                ["q5_31.13c"] = 0x7d17e496u,
                ["q5_32.8f"] = 0x3ef9c7c2u,
                ["q5_33.9f"] = 0x04d03930u,
                ["q5_34.10f"] = 0x7fcc1317u,
                ["q5_35.11f"] = 0x59961612u,
                ["q5_36.12f"] = 0x38a08099u,
                ["q5_37.13f"] = 0xeb547ebcu,
                ["q5_38.8h"] = 0x9c24670cu,
                ["q5_39.9h"] = 0xa5839b25u,
                ["q5_40.10h"] = 0x7f14b7b4u,
                ["q5_41.11h"] = 0xd3654067u,
                ["q5_42.12h"] = 0x4d29b3a4u,
                ["q5_43.13h"] = 0x3ef65ea8u,
                ["qad63b.1a"] = 0xb3312b13u,
                ["qad_01.3a"] = 0x9d853b57u,
                ["qad_02.4a"] = 0xb35976c4u,
                ["qad_03.5a"] = 0xcea4ca8cu,
                ["qad_04.6a"] = 0x41b74d1bu,
                ["qad_09.12a"] = 0x733161ccu,
                ["qad_18.11c"] = 0x2bfe6f6au,
                ["qad_19.12c"] = 0x13d3236bu,
                ["qad_22a.7f"] = 0x3191ddd0u,
                ["qad_23a.8f"] = 0x4d3553deu,
                ["qd22b.1a"] = 0x783c53abu,
                ["qd_01.4a"] = 0xf688cf8fu,
                ["qd_05.9a"] = 0xc3db0910u,
                ["qd_09.4b"] = 0x8c3f9f44u,
                ["qd_13.9b"] = 0xafbd551bu,
                ["qd_17.5c"] = 0xa812f9e2u,
                ["qd_23.13b"] = 0xcfb5264bu,
                ["qd_24.5e"] = 0x2f1bd0ecu,
                ["qd_32.8f"] = 0xa8d295d3u,
                ["qd_38.8h"] = 0xccdddd1fu,
                ["qdu_30.12c"] = 0xf190da84u,
                ["qdu_31.13c"] = 0xb7583f73u,
                ["qdu_36a.12f"] = 0xde9c24a0u,
                ["qdu_37a.13f"] = 0x10d22320u,
                ["qdu_42a.12h"] = 0xcfe36f0cu,
                ["qdu_43a.13h"] = 0x15e6beb9u,
                ["qkn.33"] = 0x43aa343du,
                ["qkn.34"] = 0xd03b553fu,
                ["rcm63b.1a"] = 0x84acd494u,
                ["rcm_01.3a"] = 0x6ecdf13fu,
                ["rcm_02.4a"] = 0x944d4f0fu,
                ["rcm_03.5a"] = 0x36f3073cu,
                ["rcm_04.6a"] = 0x54e622ffu,
                ["rcm_05.7a"] = 0x5dd131fdu,
                ["rcm_06.8a"] = 0xf0faf813u,
                ["rcm_07.9a"] = 0x826de013u,
                ["rcm_08.10a"] = 0xfbff64cfu,
                ["rcm_09.11a"] = 0x22ac8f5fu,
                ["rcm_09.12a"] = 0x9632d6efu,
                ["rcm_10.3c"] = 0x4dc8ada9u,
                ["rcm_11.4c"] = 0xf2b9ee06u,
                ["rcm_12.5c"] = 0xfed5f203u,
                ["rcm_13.6c"] = 0x5069d4a9u,
                ["rcm_14.7c"] = 0x303be3bdu,
                ["rcm_15.8c"] = 0x4f2d372fu,
                ["rcm_16.9c"] = 0x93d97fdeu,
                ["rcm_17.10c"] = 0x92371042u,
                ["rcm_18.11c"] = 0x80f1f8aau,
                ["rcm_19.12c"] = 0xf257dbe1u,
                ["rcm_21a.6f"] = 0x517ccde2u,
                ["rcm_22a.7f"] = 0x8729a689u,
                ["rcm_23a.8f"] = 0xefd96cb2u,
                ["rcma_21a.6f"] = 0x4376ea95u,
                ["rcma_22b.7f"] = 0x708268c4u,
                ["rcma_23b.8f"] = 0x61e4a397u,
                ["rcmu_21a.6f"] = 0x4376ea95u,
                ["rcmu_22b.7f"] = 0x708268c4u,
                ["rcmu_23b.8f"] = 0x1cd33c7au,
                ["right.code.040"] = 0x5a9d0b64u,
                ["rj313.u196.800"] = 0x435153d5u,
                ["rom1"] = 0x41dc73b9u,
                ["rom1.bin"] = 0x8f2c41a4u,
                ["rom10.bin"] = 0x807284f1u,
                ["rom11.bin"] = 0x21652214u,
                ["rom12.bin"] = 0xd49d2eb0u,
                ["rom13.bin"] = 0x2919883bu,
                ["rom14.bin"] = 0xf538e620u,
                ["rom15.bin"] = 0x293579c5u,
                ["rom16.bin"] = 0xc3727ce7u,
                ["rom2.bin"] = 0x65f3dc43u,
                ["rom3.bin"] = 0x3cd830e3u,
                ["rom4.bin"] = 0x9683dd30u,
                ["rom5.bin"] = 0x5321f759u,
                ["rom6.bin"] = 0xc8eb5f76u,
                ["rom7.bin"] = 0xb5669ad3u,
                ["rom8.bin"] = 0xf07a6085u,
                ["rom9.bin"] = 0x0d98bfd6u,
                ["rt-1m.3a"] = 0x902489d0u,
                ["rt-2m.4a"] = 0xe9a034f4u,
                ["rt-3m.5a"] = 0xe35ce720u,
                ["rt-4m.6a"] = 0xdf0eea8bu,
                ["rt-5m.7a"] = 0x86aef804u,
                ["rt-6m.8a"] = 0x13cb0e7cu,
                ["rt-7m.9a"] = 0x4f057110u,
                ["rt-8m.10a"] = 0x1f055014u,
                ["rt22b.1a"] = 0x89560d6au,
                ["rt24b.1a"] = 0x54b85159u,
                ["rt_01.4a"] = 0x3e11f8cdu,
                ["rt_02.5a"] = 0xfcffd73cu,
                ["rt_03.7a"] = 0x98087e08u,
                ["rt_04.8a"] = 0x4d7b9a1au,
                ["rt_05.9a"] = 0x283fd470u,
                ["rt_06.10a"] = 0xd9650bc4u,
                ["rt_07.11a"] = 0xc62defa1u,
                ["rt_08.12a"] = 0x75f4975bu,
                ["rt_09.4b"] = 0x2c40e480u,
                ["rt_10.5b"] = 0xe3f3ff94u,
                ["rt_11.7b"] = 0x04f3c298u,
                ["rt_12.8b"] = 0xe54664ccu,
                ["rt_13.9b"] = 0x51009117u,
                ["rt_14.10b"] = 0x5c546d9au,
                ["rt_15.11b"] = 0xb6aba565u,
                ["rt_16.12b"] = 0x37c96cfcu,
                ["rt_17.5c"] = 0xe5dcddebu,
                ["rt_18.11c"] = 0x26b211abu,
                ["rt_18.7c"] = 0xce1afb7cu,
                ["rt_19.12c"] = 0xdbe64ad0u,
                ["rt_19.8c"] = 0x1f0f72bdu,
                ["rt_20.9c"] = 0x4fe52659u,
                ["rt_21.10c"] = 0x20012ddcu,
                ["rt_22.11c"] = 0x228a0d4au,
                ["rt_23.13b"] = 0x7d5a77a7u,
                ["rt_24.5e"] = 0xee4484ceu,
                ["rt_25.7e"] = 0x11b28831u,
                ["rt_26.8e"] = 0x532f542eu,
                ["rt_27.9e"] = 0xec6edc0fu,
                ["rt_28.10e"] = 0x6064e499u,
                ["rt_28a.9f"] = 0x054137c8u,
                ["rt_29.11e"] = 0x8fa77f9fu,
                ["rt_30.12c"] = 0x26b211abu,
                ["rt_31.13c"] = 0xdbe64ad0u,
                ["rt_32.8f"] = 0x08e2b758u,
                ["rt_33.9f"] = 0xd6a99384u,
                ["rt_33a.9h"] = 0x7264cb1bu,
                ["rt_34.10f"] = 0x054137c8u,
                ["rt_38.8h"] = 0xb2940c2du,
                ["rt_39.9h"] = 0xea7ac9eeu,
                ["rt_40.10h"] = 0x7264cb1bu,
                ["rt_9.12b"] = 0xabfca165u,
                ["rte_28.9f"] = 0x054137c8u,
                ["rte_29.10f"] = 0x9a8df1e4u,
                ["rte_29a.10f"] = 0xcddaa919u,
                ["rte_30.11f"] = 0x0d541519u,
                ["rte_30a.11f"] = 0xef5b8b33u,
                ["rte_31.12f"] = 0x33e0337du,
                ["rte_31a.12f"] = 0x32835e5eu,
                ["rte_33.9h"] = 0x7264cb1bu,
                ["rte_34.10h"] = 0x6348a79du,
                ["rte_34a.10h"] = 0xed52e7e5u,
                ["rte_35.11h"] = 0x73dd0e20u,
                ["rte_35a.11h"] = 0x7d705529u,
                ["rte_36.12h"] = 0xa8865243u,
                ["rte_36a.12h"] = 0x7637975fu,
                ["rtj_35.11f"] = 0xe72f9ea3u,
                ["rtj_36.12f"] = 0xe3741247u,
                ["rtj_37.13f"] = 0xa1f677b0u,
                ["rtj_41.11h"] = 0xa11ee998u,
                ["rtj_42.12h"] = 0xb4baa117u,
                ["rtj_43.13h"] = 0x85337a47u,
                ["rtu_29a.10f"] = 0x37ba3e20u,
                ["rtu_30a.11f"] = 0x0b156fd8u,
                ["rtu_31a.12f"] = 0x0e723fccu,
                ["rtu_34a.10h"] = 0xf99f46c0u,
                ["rtu_35a.11h"] = 0x57350bf4u,
                ["rtu_36a.12h"] = 0x523a45dcu,
                ["s1.u196"] = 0x2bc76a02u,
                ["s2.u222"] = 0x0804f973u,
                ["s222b.1a"] = 0x6d86b45eu,
                ["s224b.1a"] = 0xcdc4413eu,
                ["s224bn.1a"] = 0x31367e94u,
                ["s2t_10.3c"] = 0x3c042686u,
                ["s2t_11.4c"] = 0x8b7e7183u,
                ["s2t_12.5c"] = 0x293c888cu,
                ["s2t_13.6c"] = 0x842b35a4u,
                ["s2te_21.6f"] = 0xfd200288u,
                ["s2te_22.7f"] = 0xaea6e035u,
                ["s2te_23.8f"] = 0x2dd72514u,
                ["s2tj_21.6f"] = 0xfd200288u,
                ["s2tj_22.7f"] = 0xaea6e035u,
                ["s2tj_23.8f"] = 0xea73b4dcu,
                ["s2tu_21.6f"] = 0xfd200288u,
                ["s2tu_22.7f"] = 0xaea6e035u,
                ["s2tu_23.8f"] = 0x89a1fc38u,
                ["s92-10m.3c"] = 0x960687d5u,
                ["s92-11m.4c"] = 0xd6ec9a0au,
                ["s92-12m.5c"] = 0x978ecd18u,
                ["s92-13m.6c"] = 0xed2c67f6u,
                ["s92-1m.3a"] = 0x03b0d852u,
                ["s92-2m.4a"] = 0xcdb5f027u,
                ["s92-3m.5a"] = 0x840289ecu,
                ["s92-4m.6a"] = 0xe2799472u,
                ["s92-5m.7a"] = 0xba8a2761u,
                ["s92-6m.8a"] = 0x21e3f87du,
                ["s92-7m.9a"] = 0xe584bfb5u,
                ["s92-8m.10a"] = 0xbefc47dfu,
                ["s9263b.1a"] = 0x0a7ecfe0u,
                ["s92_01.3a"] = 0x03b0d852u,
                ["s92_01.bin"] = 0x03b0d852u,
                ["s92_02.4a"] = 0x840289ecu,
                ["s92_02.bin"] = 0x840289ecu,
                ["s92_03.5a"] = 0xcdb5f027u,
                ["s92_03.bin"] = 0xcdb5f027u,
                ["s92_04.6a"] = 0xe2799472u,
                ["s92_04.bin"] = 0xe2799472u,
                ["s92_05.7a"] = 0xba8a2761u,
                ["s92_05.bin"] = 0xba8a2761u,
                ["s92_06.8a"] = 0xe584bfb5u,
                ["s92_06.bin"] = 0xe584bfb5u,
                ["s92_07.9a"] = 0x21e3f87du,
                ["s92_07.bin"] = 0x21e3f87du,
                ["s92_08.10a"] = 0xbefc47dfu,
                ["s92_08.bin"] = 0xbefc47dfu,
                ["s92_09.11a"] = 0x08f6b60eu,
                ["s92_09.12a"] = 0x08f6b60eu,
                ["s92_09.bin"] = 0x08f6b60eu,
                ["s92_10.3c"] = 0x960687d5u,
                ["s92_10.bin"] = 0x960687d5u,
                ["s92_11.4c"] = 0x978ecd18u,
                ["s92_11.bin"] = 0x978ecd18u,
                ["s92_12.5c"] = 0xd6ec9a0au,
                ["s92_12.bin"] = 0xd6ec9a0au,
                ["s92_13.6c"] = 0xed2c67f6u,
                ["s92_13.bin"] = 0xed2c67f6u,
                ["s92_18.11c"] = 0x7f162009u,
                ["s92_18.bin"] = 0x7f162009u,
                ["s92_19.12c"] = 0xbeade53fu,
                ["s92_19.bin"] = 0xbeade53fu,
                ["s92_21a.5f"] = 0x925a7877u,
                ["s92_21a.6f"] = 0x925a7877u,
                ["s92_21a.bin"] = 0x925a7877u,
                ["s92_22a.7f"] = 0x99f1cca4u,
                ["s92_22b.7f"] = 0x2bbe15edu,
                ["s92_22c.7f"] = 0x5fd8630bu,
                ["s92e_23a.8f"] = 0x3f846b74u,
                ["s92e_23b.8f"] = 0x0aaa1a3au,
                ["s92e_23c.8f"] = 0x994b408du,
                ["s92j_21a.6f"] = 0x925a7877u,
                ["s92j_22a.7f"] = 0xc4f64bcdu,
                ["s92j_22b.7f"] = 0x2fbb3bfeu,
                ["s92j_22c.7f"] = 0x8c0b2ed6u,
                ["s92j_23a.8f"] = 0x4f42bb5au,
                ["s92j_23b.8f"] = 0x140876c5u,
                ["s92j_23c.8f"] = 0xf0120635u,
                ["s92t_23a.8f"] = 0xd7c28adeu,
                ["s92u_23a.8f"] = 0xac44415bu,
                ["s92u_23b.8f"] = 0x996a3015u,
                ["s92u_23c.8f"] = 0x0a8b6aa2u,
                ["se.36.j13"] = 0xd30c263eu,
                ["sf-2_28l.9e"] = 0xeee2b426u,
                ["sf-2_30l.11e"] = 0x34a1ce02u,
                ["sf-2_31l.12e"] = 0x64ebc8d2u,
                ["sf-2_35l.9f"] = 0xeca8b452u,
                ["sf-2_37l.11f"] = 0x5b630ed2u,
                ["sf-2_38l.12f"] = 0x73847443u,
                ["sf-2u_28m.9e"] = 0xeee2b426u,
                ["sf-2u_29m.10e"] = 0xbb4af315u,
                ["sf-2u_30m.11e"] = 0x34a1ce02u,
                ["sf-2u_31m.12e"] = 0x64ebc8d2u,
                ["sf-2u_35m.9f"] = 0xeca8b452u,
                ["sf-2u_36m.10f"] = 0xc02a13ebu,
                ["sf-2u_37m.11f"] = 0x8cbff19cu,
                ["sf-2u_38m.12f"] = 0x73847443u,
                ["sf2-11m.5d"] = 0x0627c831u,
                ["sf2-13m.4d"] = 0x994bfa58u,
                ["sf2-15m.6d"] = 0x3e66ad9du,
                ["sf2-1m.3a"] = 0xba529b4fu,
                ["sf2-2m.3c"] = 0x14b84312u,
                ["sf2-3m.5a"] = 0x4b1b33a8u,
                ["sf2-4m.5c"] = 0x5e9cd89au,
                ["sf2-5m.4a"] = 0x22c9cc8eu,
                ["sf2-6m.4c"] = 0x2c7e2229u,
                ["sf2-7m.6a"] = 0x57213be8u,
                ["sf2-8m.6c"] = 0xb5548f17u,
                ["sf2-9m.3d"] = 0xc1befaa8u,
                ["sf2_05.7a"] = 0xba529b4fu,
                ["sf2_05.bin"] = 0xba529b4fu,
                ["sf2_06.8a"] = 0x22c9cc8eu,
                ["sf2_06.bin"] = 0x22c9cc8eu,
                ["sf2_07.9a"] = 0x4b1b33a8u,
                ["sf2_07.bin"] = 0x4b1b33a8u,
                ["sf2_08.10a"] = 0x57213be8u,
                ["sf2_08.bin"] = 0x57213be8u,
                ["sf2_09.12a"] = 0xa4823a1bu,
                ["sf2_09.bin"] = 0xa4823a1bu,
                ["sf2_14.7c"] = 0x14b84312u,
                ["sf2_14.bin"] = 0x14b84312u,
                ["sf2_15.8c"] = 0x2c7e2229u,
                ["sf2_15.bin"] = 0x2c7e2229u,
                ["sf2_16.9c"] = 0x5e9cd89au,
                ["sf2_16.bin"] = 0x5e9cd89au,
                ["sf2_17.10c"] = 0xb5548f17u,
                ["sf2_17.bin"] = 0xb5548f17u,
                ["sf2_18.11c"] = 0x7f162009u,
                ["sf2_18.bin"] = 0x7f162009u,
                ["sf2_19.12c"] = 0xbeade53fu,
                ["sf2_19.bin"] = 0xbeade53fu,
                ["sf2_24.7d"] = 0xc1befaa8u,
                ["sf2_24.bin"] = 0xc1befaa8u,
                ["sf2_25.8d"] = 0x994bfa58u,
                ["sf2_25.bin"] = 0x994bfa58u,
                ["sf2_26.9d"] = 0x0627c831u,
                ["sf2_26.bin"] = 0x0627c831u,
                ["sf2_27.10d"] = 0x3e66ad9du,
                ["sf2_27.bin"] = 0x3e66ad9du,
                ["sf2_28.9e"] = 0x55d88c35u,
                ["sf2_28a.9e"] = 0x852e10ecu,
                ["sf2_28b.9e"] = 0x4009955eu,
                ["sf2_28l.9e"] = 0xd283187au,
                ["sf2_29.10e"] = 0xfdd0b5c1u,
                ["sf2_29a.bin"] = 0xbb4af315u,
                ["sf2_29b.10e"] = 0xbb4af315u,
                ["sf2_29l.10e"] = 0xbb4af315u,
                ["sf2_30l.11e"] = 0x79022b31u,
                ["sf2_31l.12e"] = 0xfe15cb39u,
                ["sf2_35.9f"] = 0x4b964478u,
                ["sf2_35a.9f"] = 0x3b075de1u,
                ["sf2_35b.9f"] = 0x8c1f3994u,
                ["sf2_35l.9f"] = 0xe3266622u,
                ["sf2_36.10f"] = 0xdb66b127u,
                ["sf2_36a.bin"] = 0xc02a13ebu,
                ["sf2_36b.10f"] = 0xc02a13ebu,
                ["sf2_36l.10f"] = 0xc02a13ebu,
                ["sf2_38l.12f"] = 0x65cb1883u,
                ["sf2_9.12a"] = 0xa4823a1bu,
                ["sf2_ce_rb.22"] = 0x145e5219u,
                ["sf2_ce_rb.23"] = 0x202f9e50u,
                ["sf2ca-21.bin"] = 0x4c1c43bau,
                ["sf2ca-22.bin"] = 0x0550453du,
                ["sf2ca-23.bin"] = 0x36c3ba2fu,
                ["sf2ca_21-c.bin"] = 0xcf7fcc8cu,
                ["sf2ca_22-c.bin"] = 0x99f1cca4u,
                ["sf2ca_23-c.bin"] = 0xe7c8c5a6u,
                ["sf2d__22.rom"] = 0xfe9d9cf5u,
                ["sf2d__23.rom"] = 0x450532b0u,
                ["sf2e_28d.9e"] = 0x175819d1u,
                ["sf2e_28e.9e"] = 0xe3b95625u,
                ["sf2e_28f.9e"] = 0xacd8175bu,
                ["sf2e_28g.9e"] = 0x8bf9f1e5u,
                ["sf2e_30.11e"] = 0x997bdac4u,
                ["sf2e_30a.11e"] = 0xbc02c14cu,
                ["sf2e_30b.11e"] = 0x57bd7051u,
                ["sf2e_30d.11e"] = 0x4bb2657cu,
                ["sf2e_30e.11e"] = 0xf37cd088u,
                ["sf2e_30f.11e"] = 0xfe39ee33u,
                ["sf2e_30g.11e"] = 0xfe39ee33u,
                ["sf2e_31.12e"] = 0x53e54744u,
                ["sf2e_31a.12e"] = 0x8b8221e6u,
                ["sf2e_31b.12e"] = 0xa673143du,
                ["sf2e_31d.12e"] = 0xd57b67d7u,
                ["sf2e_31e.12e"] = 0x7c4771b4u,
                ["sf2e_31f.12e"] = 0x69a0a301u,
                ["sf2e_31g.12e"] = 0x69a0a301u,
                ["sf2e_35d.9f"] = 0x82060da4u,
                ["sf2e_35e.9f"] = 0x3648769au,
                ["sf2e_35f.9f"] = 0xc0a80bd1u,
                ["sf2e_35g.9f"] = 0x626ef934u,
                ["sf2e_37.11f"] = 0xf11b3d64u,
                ["sf2e_37a.11f"] = 0x1c1266b3u,
                ["sf2e_37b.11f"] = 0x62691cddu,
                ["sf2e_37d.11f"] = 0x102f4561u,
                ["sf2e_37e.11f"] = 0xc39468e6u,
                ["sf2e_37f.11f"] = 0xb58a741bu,
                ["sf2e_37g.11f"] = 0xfb92cd74u,
                ["sf2e_38.12f"] = 0x5ff4dc81u,
                ["sf2e_38a.12f"] = 0x2d42d82au,
                ["sf2e_38b.12f"] = 0x4c2ccef7u,
                ["sf2e_38d.12f"] = 0x9c8916efu,
                ["sf2e_38e.12f"] = 0xa4bd0cd9u,
                ["sf2e_38f.12f"] = 0x1510e4e2u,
                ["sf2e_38g.12f"] = 0x5e22db70u,
                ["sf2h14.5"] = 0x66c91972u,
                ["sf2h14.7"] = 0x74803532u,
                ["sf2j28.bin"] = 0xd283187au,
                ["sf2j30.bin"] = 0x79022b31u,
                ["sf2j31.bin"] = 0xfe15cb39u,
                ["sf2j35.bin"] = 0xd28158e4u,
                ["sf2j37.bin"] = 0x516776ecu,
                ["sf2j38.bin"] = 0x38614d70u,
                ["sf2j_09.12a"] = 0xa4823a1bu,
                ["sf2j_18.11c"] = 0x7f162009u,
                ["sf2j_19.12c"] = 0xbeade53fu,
                ["sf2j_28a.9e"] = 0x4009955eu,
                ["sf2j_28c.9e"] = 0x6eddd5e8u,
                ["sf2j_28f.9e"] = 0xacd8175bu,
                ["sf2j_28h.9e"] = 0x8a5c8ee0u,
                ["sf2j_29a.10e"] = 0xbb4af315u,
                ["sf2j_30a.11e"] = 0x57bd7051u,
                ["sf2j_30c.11e"] = 0x8add35ecu,
                ["sf2j_30f.11e"] = 0xfe39ee33u,
                ["sf2j_30h.11e"] = 0xfe39ee33u,
                ["sf2j_31a.12e"] = 0xa673143du,
                ["sf2j_31c.12e"] = 0xc4fff4a9u,
                ["sf2j_31f.12e"] = 0x69a0a301u,
                ["sf2j_31h.12e"] = 0x69a0a301u,
                ["sf2j_35a.9f"] = 0x8c1f3994u,
                ["sf2j_35c.9f"] = 0x6bcb404cu,
                ["sf2j_35f.9f"] = 0xc0a80bd1u,
                ["sf2j_35h.9f"] = 0xc828fc4du,
                ["sf2j_36a.10f"] = 0xc02a13ebu,
                ["sf2j_37a.11f"] = 0x1e1f6844u,
                ["sf2j_37c.11f"] = 0x0d74a256u,
                ["sf2j_37f.11f"] = 0xc1428cc6u,
                ["sf2j_37h.11f"] = 0x330304b0u,
                ["sf2j_37l.11f"] = 0x04ba20c7u,
                ["sf2j_38a.12f"] = 0x4c2ccef7u,
                ["sf2j_38c.12f"] = 0x8210fc0eu,
                ["sf2j_38f.12f"] = 0x1510e4e2u,
                ["sf2j_38h.12f"] = 0xa659f678u,
                ["sf2red.21"] = 0x52c486bbu,
                ["sf2red.22"] = 0x18daf387u,
                ["sf2red.23"] = 0x2d3c4f72u,
                ["sf2u_28a.9e"] = 0x387a175cu,
                ["sf2u_28b.9e"] = 0x4009955eu,
                ["sf2u_28c.9e"] = 0x6eddd5e8u,
                ["sf2u_28d.9e"] = 0x175819d1u,
                ["sf2u_28e.9e"] = 0xe3b95625u,
                ["sf2u_28f.9e"] = 0xacd8175bu,
                ["sf2u_28g.9e"] = 0x8bf9f1e5u,
                ["sf2u_28h.9e"] = 0x8a5c8ee0u,
                ["sf2u_28i.9e"] = 0x1580be4cu,
                ["sf2u_28k.9e"] = 0x8e958f31u,
                ["sf2u_29a.10e"] = 0xbb4af315u,
                ["sf2u_30a.11e"] = 0x08beb861u,
                ["sf2u_30b.11e"] = 0x57bd7051u,
                ["sf2u_30c.11e"] = 0x6cb59385u,
                ["sf2u_30d.11e"] = 0x4bb2657cu,
                ["sf2u_30e.11e"] = 0xf37cd088u,
                ["sf2u_30f.11e"] = 0xfe39ee33u,
                ["sf2u_30g.11e"] = 0xfe39ee33u,
                ["sf2u_30h.11e"] = 0xfe39ee33u,
                ["sf2u_30i.11e"] = 0xfe39ee33u,
                ["sf2u_30k.11e"] = 0x8f66076cu,
                ["sf2u_31a.12e"] = 0x0d5394e0u,
                ["sf2u_31b.12e"] = 0xa673143du,
                ["sf2u_31c.12e"] = 0xc4fff4a9u,
                ["sf2u_31d.12e"] = 0xd57b67d7u,
                ["sf2u_31e.12e"] = 0x7c4771b4u,
                ["sf2u_31f.12e"] = 0x69a0a301u,
                ["sf2u_31g.12e"] = 0x69a0a301u,
                ["sf2u_31h.12e"] = 0x69a0a301u,
                ["sf2u_31i.12e"] = 0x69a0a301u,
                ["sf2u_31k.12e"] = 0xf9f89f60u,
                ["sf2u_35a.9f"] = 0xa1a5adccu,
                ["sf2u_35b.9f"] = 0x8c1f3994u,
                ["sf2u_35c.9f"] = 0x6bcb404cu,
                ["sf2u_35d.9f"] = 0x82060da4u,
                ["sf2u_35e.9f"] = 0x3648769au,
                ["sf2u_35f.9f"] = 0xc0a80bd1u,
                ["sf2u_35g.9f"] = 0x626ef934u,
                ["sf2u_35h.9f"] = 0xc828fc4du,
                ["sf2u_35i.9f"] = 0x1468d185u,
                ["sf2u_35k.9f"] = 0xfce76fadu,
                ["sf2u_36a.10f"] = 0xc02a13ebu,
                ["sf2u_37a.11f"] = 0xb7638d69u,
                ["sf2u_37b.11f"] = 0x4a54d479u,
                ["sf2u_37c.11f"] = 0x32e2c278u,
                ["sf2u_37d.11f"] = 0xb33b42f2u,
                ["sf2u_37e.11f"] = 0x6c61a513u,
                ["sf2u_37f.11f"] = 0x169e7388u,
                ["sf2u_37g.11f"] = 0x5886cae7u,
                ["sf2u_37h.11f"] = 0xe4dffbfeu,
                ["sf2u_37i.11f"] = 0x9df707ddu,
                ["sf2u_37k.11f"] = 0x4e1f6a83u,
                ["sf2u_38a.12f"] = 0x42d6a79eu,
                ["sf2u_38b.12f"] = 0x4c2ccef7u,
                ["sf2u_38c.12f"] = 0x8210fc0eu,
                ["sf2u_38d.12f"] = 0x9c8916efu,
                ["sf2u_38e.12f"] = 0xa4bd0cd9u,
                ["sf2u_38f.12f"] = 0x1510e4e2u,
                ["sf2u_38g.12f"] = 0x5e22db70u,
                ["sf2u_38h.12f"] = 0xa659f678u,
                ["sf2u_38i.12f"] = 0x4cb46dafu,
                ["sf2u_38k.12f"] = 0x6ce0a85au,
                ["sf2v004.22"] = 0x4b26fde7u,
                ["sf2v004.23"] = 0x52d19f2cu,
                ["sfach23"] = 0x02a1a853u,
                ["sfiire073.u18"] = 0x93ec42aeu,
                ["sfiire143.u19"] = 0x39d763d3u,
                ["sfz01"] = 0x0dd53e62u,
                ["sfz02"] = 0x94c31e3fu,
                ["sfz03"] = 0x9584ac85u,
                ["sfz04"] = 0xb983624cu,
                ["sfz05"] = 0x2b47b645u,
                ["sfz06"] = 0x74fd9fb1u,
                ["sfz07"] = 0xbb2c734du,
                ["sfz08"] = 0x454f7868u,
                ["sfz09"] = 0xc772628bu,
                ["sfz10"] = 0x2a7d675eu,
                ["sfz11"] = 0xe35546c8u,
                ["sfz12"] = 0xf122693au,
                ["sfz13"] = 0x7cf942c8u,
                ["sfz14"] = 0x09038c81u,
                ["sfz15"] = 0x1aa17391u,
                ["sfz16"] = 0x19a5abd6u,
                ["sfz17"] = 0x248b3b73u,
                ["sfz18"] = 0x61022b2du,
                ["sfz19"] = 0x3b5886d5u,
                ["sfz63b.1a"] = 0xf5a351dau,
                ["sfz_01.3a"] = 0x0dd53e62u,
                ["sfz_02.4a"] = 0x94c31e3fu,
                ["sfz_03.5a"] = 0x9584ac85u,
                ["sfz_04.6a"] = 0xb983624cu,
                ["sfz_05.7a"] = 0x2b47b645u,
                ["sfz_06.8a"] = 0x74fd9fb1u,
                ["sfz_07.9a"] = 0xbb2c734du,
                ["sfz_08.10a"] = 0x454f7868u,
                ["sfz_09.12a"] = 0xc772628bu,
                ["sfz_10.3c"] = 0x2a7d675eu,
                ["sfz_11.4c"] = 0xe35546c8u,
                ["sfz_12.5c"] = 0xf122693au,
                ["sfz_13.6c"] = 0x7cf942c8u,
                ["sfz_14.7c"] = 0x09038c81u,
                ["sfz_15.8c"] = 0x1aa17391u,
                ["sfz_16.9c"] = 0x19a5abd6u,
                ["sfz_17.10c"] = 0x248b3b73u,
                ["sfz_18.11c"] = 0x61022b2du,
                ["sfz_19.12c"] = 0x3b5886d5u,
                ["sfza20"] = 0x806e8f38u,
                ["sfza22"] = 0x8d9b2480u,
                ["sfzbch23"] = 0x53699f68u,
                ["sfzch21"] = 0x5435225du,
                ["sfzch23"] = 0x1140743fu,
                ["sgyxz_gfx1.bin"] = 0xa60be9f6u,
                ["sgyxz_gfx2.bin"] = 0x6ad9d048u,
                ["sgyxz_prg1.bin"] = 0xd8511929u,
                ["sgyxz_prg2.bin"] = 0x95429c83u,
                ["sgyxz_snd1.bin"] = 0xc15ac0f2u,
                ["sgyxz_snd2.bin"] = 0x210c376fu,
                ["snd.9.b13"] = 0x08f6b60eu,
                ["so2-2m.4a"] = 0x597c2875u,
                ["so2-32m.8h"] = 0x2eb5cf0cu,
                ["so2-4m.6a"] = 0x912a9ca0u,
                ["so2-6m.8a"] = 0xaa6102afu,
                ["so2-8m.10a"] = 0x839e6869u,
                ["so2_01.4a"] = 0x31fd2715u,
                ["so2_02.5a"] = 0xb4b2a0b7u,
                ["so2_03.7a"] = 0xf5a8905eu,
                ["so2_05.9a"] = 0x54bed82cu,
                ["so2_06.10a"] = 0x9d756f51u,
                ["so2_07.11a"] = 0xb43cd1a8u,
                ["so2_09.12b"] = 0xd09d7c7au,
                ["so2_09.4b"] = 0x690c261du,
                ["so2_10.3c"] = 0xe9f569fdu,
                ["so2_10.5b"] = 0x2f871714u,
                ["so2_11.7b"] = 0x3f254efeu,
                ["so2_12.5c"] = 0xb7df8a06u,
                ["so2_13.9b"] = 0xb5e48282u,
                ["so2_14.10b"] = 0x737a744bu,
                ["so2_14.7c"] = 0xf5a8905eu,
                ["so2_15.11b"] = 0xf3aa5a4au,
                ["so2_16.9c"] = 0xb43cd1a8u,
                ["so2_17.5c"] = 0xe78bb308u,
                ["so2_18.11c"] = 0xbbea1643u,
                ["so2_18.7c"] = 0x96f61f4eu,
                ["so2_19.12c"] = 0xac58aa71u,
                ["so2_19.8c"] = 0xe9f569fdu,
                ["so2_20.3d"] = 0x8ca751a3u,
                ["so2_21.10c"] = 0xb7df8a06u,
                ["so2_22.5d"] = 0xfce9a377u,
                ["so2_23.13b"] = 0xd09d7c7au,
                ["so2_24.5e"] = 0x78b6f0cbu,
                ["so2_24.7d"] = 0x3f254efeu,
                ["so2_25.7e"] = 0x6d0e05d6u,
                ["so2_26.8e"] = 0x8ca751a3u,
                ["so2_26.9d"] = 0xf3aa5a4au,
                ["so2_28.10e"] = 0xfce9a377u,
                ["so2_30.11f"] = 0xe17f9bf7u,
                ["so2_30.12c"] = 0xbbea1643u,
                ["so2_30a.11f"] = 0xe4e725d7u,
                ["so2_30e.11f"] = 0xe17f9bf7u,
                ["so2_31.12f"] = 0x51204d36u,
                ["so2_31.13c"] = 0xac58aa71u,
                ["so2_31a.12f"] = 0xc0b91deau,
                ["so2_31e.12f"] = 0x51204d36u,
                ["so2_32.8f"] = 0x75dffc9au,
                ["so2_33.9f"] = 0x39b90d25u,
                ["so2_34.10f"] = 0xb8dae95fu,
                ["so2_35.11f"] = 0x7d24394du,
                ["so2_35.11h"] = 0x4477df61u,
                ["so2_35a.11h"] = 0xe7843445u,
                ["so2_35e.11h"] = 0x78e63575u,
                ["so2_36.12f"] = 0xe17f9bf7u,
                ["so2_36.12h"] = 0x9cfba8b4u,
                ["so2_36a.12h"] = 0x591edf6cu,
                ["so2_36e.12h"] = 0x9cfba8b4u,
                ["so2_37.13f"] = 0x51204d36u,
                ["so2_38.8h"] = 0x0010a9a2u,
                ["so2_39.9h"] = 0xd52ba336u,
                ["so2_40.10h"] = 0xde37771cu,
                ["so2_41.11h"] = 0x914f85e0u,
                ["so2_42.12h"] = 0x2c3884c6u,
                ["so2_43.13h"] = 0x9cfba8b4u,
                ["soonhwa_f-fight.10"] = 0x7cce0ff5u,
                ["soonhwa_f-fight.11"] = 0x52879243u,
                ["soonhwa_f-fight.14"] = 0x319fbc2fu,
                ["soonhwa_f-fight.8"] = 0xf1e18158u,
                ["soonhwa_f-fight.9"] = 0x11a7c515u,
                ["soonhwa_f-fight00.0r00"] = 0x4b4390deu,
                ["soonhwa_f-fight01.0r01"] = 0x09c47caeu,
                ["soonhwa_f-fight02.0r02"] = 0xfe326d39u,
                ["soonhwa_f-fight03.0r03"] = 0x2126bec0u,
                ["soonhwa_f-fightpgm.8h"] = 0xf8ccf27eu,
                ["soonhwa_f-fightpgm.8l"] = 0xd96c76b2u,
                ["sou1"] = 0x84f4b2feu,
                ["sound.020"] = 0x672dcb46u,
                ["sound.512"] = 0x210c376fu,
                ["sound.bin"] = 0xaeec9dc6u,
                ["sound.code.512"] = 0x5e44d9eeu,
                ["sound.u191"] = 0xa4823a1bu,
                ["spe-a.japan9207d.mask1.801"] = 0x14a15fcdu,
                ["spe-b.japan9207d.mask2.801"] = 0x250d2957u,
                ["spe-c.japan9207d.mask4.801"] = 0x0721c26du,
                ["spe-d.japan9207d.mask3.801"] = 0xdb97f56au,
                ["spe-e.japan9208d.snd.mask.020"] = 0x85f837a0u,
                ["sro.1"] = 0x2b1c4c16u,
                ["st-1.7a"] = 0x005f000bu,
                ["st-10.9a"] = 0xb9441519u,
                ["st-11.10a"] = 0x2d7f21e4u,
                ["st-14.8h"] = 0x9b3cfc08u,
                ["st-2.8a"] = 0x4eee9aeau,
                ["st-4.3a"] = 0xb7d04e8bu,
                ["st-5.4a"] = 0x7705aa46u,
                ["st-8.5a"] = 0x6b4713b4u,
                ["st-9.6a"] = 0x5b18b722u,
                ["st22b.1a"] = 0x68fecc55u,
                ["st24m1.1a"] = 0xa80d357eu,
                ["stf champ wave rom 21.6f"] = 0x04fff17bu,
                ["stf champ wave rom 22.7f"] = 0x27e80cb1u,
                ["stf champ wave rom 23.8f"] = 0xeb265dc7u,
                ["stf29.1a"] = 0x043309c5u,
                ["stfii-qkn-cps-17.33"] = 0x3a9458eeu,
                ["stfii-qkn-cps-17.34"] = 0x4ed215d8u,
                ["sth63b.1a"] = 0xc706b773u,
                ["sth_01.3a"] = 0x4eee9aeau,
                ["sth_01.4a"] = 0x1e21b0c1u,
                ["sth_02.4a"] = 0x2d7f21e4u,
                ["sth_02.5a"] = 0xc75f9ea0u,
                ["sth_03.5a"] = 0x7705aa46u,
                ["sth_03.7a"] = 0xaaa07245u,
                ["sth_04.6a"] = 0x5b18b722u,
                ["sth_04.8a"] = 0x853d3e01u,
                ["sth_05.7a"] = 0x005f000bu,
                ["sth_05.9a"] = 0xec9f8714u,
                ["sth_06.10a"] = 0xd84f5478u,
                ["sth_06.8a"] = 0xb9441519u,
                ["sth_07.11a"] = 0x97d072d2u,
                ["sth_07.9a"] = 0xb7d04e8bu,
                ["sth_08.10a"] = 0x6b4713b4u,
                ["sth_08.12a"] = 0x2ce9b4c7u,
                ["sth_09.12a"] = 0x08d63519u,
                ["sth_09.4b"] = 0x1ef6bfbdu,
                ["sth_10.5b"] = 0xdf3dd3bcu,
                ["sth_11.7b"] = 0x2484f241u,
                ["sth_12.8b"] = 0xf670a477u,
                ["sth_13.9b"] = 0x063263aeu,
                ["sth_14.10b"] = 0x6c03e19du,
                ["sth_15.11b"] = 0xe415d943u,
                ["sth_16.12b"] = 0x4092019fu,
                ["sth_17.5c"] = 0xb4f73d86u,
                ["sth_18.11c"] = 0x4386bc80u,
                ["sth_18.7c"] = 0x5b318956u,
                ["sth_19.12c"] = 0x444536d7u,
                ["sth_19.8c"] = 0x257ce683u,
                ["sth_20.9c"] = 0xeb584dd4u,
                ["sth_21.10c"] = 0x538d9423u,
                ["sth_22.11c"] = 0x78dd9c48u,
                ["sth_23.13c"] = 0x2ed403bcu,
                ["sth_24.5e"] = 0x6356f4d2u,
                ["sth_25.7e"] = 0x921e506au,
                ["sth_26.8e"] = 0x0ebfcb02u,
                ["sth_27.9e"] = 0xf82c88d9u,
                ["sth_28.10e"] = 0x98ac8cd1u,
                ["sth_29.11e"] = 0x34ae2997u,
                ["sth_30.12e"] = 0x4386bc80u,
                ["sth_31.13e"] = 0x444536d7u,
                ["sth_32.8f"] = 0x44a206a3u,
                ["sth_33.9f"] = 0xb47ddfc7u,
                ["sth_34.10f"] = 0xbea770b5u,
                ["sth_35.11f"] = 0x5cc429dau,
                ["sth_36.12f"] = 0x53c7b006u,
                ["sth_37.13f"] = 0x80e8877du,
                ["sth_38.8h"] = 0xee5abfc2u,
                ["sth_39.9h"] = 0x9321d6aau,
                ["sth_40.10h"] = 0x43b922dcu,
                ["sth_41.11h"] = 0x50af457fu,
                ["sth_42.12h"] = 0x4037f65fu,
                ["sth_43.13h"] = 0x6b3fa466u,
                ["sthj_22.7f"] = 0x9b3cfc08u,
                ["sthj_23.8f"] = 0x046e7b12u,
                ["stt-a"] = 0x10a7036du,
                ["stt-b"] = 0x7a09224eu,
                ["stt-c"] = 0x11701b8fu,
                ["stt-d"] = 0x3580b124u,
                ["stt-e"] = 0x382a612cu,
                ["stt-f"] = 0x101a0b72u,
                ["svr-01.bin"] = 0xb08dc61fu,
                ["svr-02.bin"] = 0xcca262aau,
                ["svr-03.bin"] = 0x1fe7056cu,
                ["svr-04.bin"] = 0xb29ce7cfu,
                ["svr-05.bin"] = 0x1c774671u,
                ["svr-06.bin"] = 0x05463aa3u,
                ["svr-07.bin"] = 0x87944aaau,
                ["svr-08.bin"] = 0xaa9d82fbu,
                ["tat-01.bin"] = 0xa887f7d4u,
                ["tat-02.bin"] = 0xafb3b589u,
                ["tat-03.bin"] = 0x79fa8bf0u,
                ["tat-04.bin"] = 0x32518120u,
                ["tat-05.bin"] = 0x9390ff23u,
                ["tat-06.bin"] = 0x90f2053eu,
                ["tat-07.bin"] = 0x6a5f153cu,
                ["tat-08.bin"] = 0xc16579aeu,
                ["tat-09.bin"] = 0x169d85a6u,
                ["tat-10.bin"] = 0x0c638630u,
                ["tat-11.bin"] = 0x32a3a841u,
                ["tat-12.bin"] = 0x6ee19b94u,
                ["tb415-01_27c160.bin"] = 0xef508ec5u,
                ["tb416-02_27c160.bin"] = 0xbfd01d21u,
                ["tf.33.j9"] = 0x6340b914u,
                ["ti-i_27c040.bin"] = 0x7d921309u,
                ["tk1-204_27c800.bin"] = 0x0efd1ddbu,
                ["tk1-305_27c800.bin"] = 0xaa468337u,
                ["tk1_01.3a"] = 0xf64bb6a0u,
                ["tk1_02.4a"] = 0x21fe6274u,
                ["tk1_03.5a"] = 0x0bf228cbu,
                ["tk1_04.6a"] = 0x1255dfb1u,
                ["tk1_05.7a"] = 0x44f7661eu,
                ["tk1_06.8a"] = 0xa54c515du,
                ["tk1_07.9a"] = 0xca5c687cu,
                ["tk1_08.10a"] = 0xf9fe6591u,
                ["tk1_09.12a"] = 0xdb77d899u,
                ["tk1_18.11c"] = 0x7e5f6cb4u,
                ["tk1_19.12c"] = 0x4a30c737u,
                ["tk1j_22.7f"] = 0x93654bcfu,
                ["tk1j_23.8f"] = 0x088a3009u,
                ["tk2-1m.3a"] = 0x0d9cb9bfu,
                ["tk2-2m.4a"] = 0xc5ca2460u,
                ["tk2-3m.5a"] = 0x45227027u,
                ["tk2-4m.6a"] = 0xe349551cu,
                ["tk2-5m.7a"] = 0x291f0f0bu,
                ["tk2-6m.8a"] = 0x1abd14d6u,
                ["tk2-7m.9a"] = 0x3edeb949u,
                ["tk2-8m.10a"] = 0xb27948e3u,
                ["tk2-q1.1k"] = 0x611268cfu,
                ["tk2-q2.2k"] = 0x20f55ca9u,
                ["tk2-q3.3k"] = 0xbfcf6f52u,
                ["tk2-q4.4k"] = 0x36642e88u,
                ["tk22b.1a"] = 0x1a1ab6d7u,
                ["tk24b1.1a"] = 0xae4a7645u,
                ["tk263b.1a"] = 0xc4b0349bu,
                ["tk2=ch=_05.7a"] = 0xe4a44d53u,
                ["tk2=ch=_06.8a"] = 0x58066ba8u,
                ["tk2=ch=_07.9a"] = 0xcc9006c9u,
                ["tk2=ch=_08.10a"] = 0xd4a19a02u,
                ["tk2=ch=_22.7f"] = 0xd0937a8du,
                ["tk2=ch=_23.8f"] = 0x4e0b8deeu,
                ["tk2_01.3a"] = 0x0d9cb9bfu,
                ["tk2_02.4a"] = 0x45227027u,
                ["tk2_03.5a"] = 0xc5ca2460u,
                ["tk2_04.6a"] = 0xe349551cu,
                ["tk2_05.7a"] = 0xe4a44d53u,
                ["tk2_06.8a"] = 0x58066ba8u,
                ["tk2_07.9a"] = 0xd706568eu,
                ["tk2_08.10a"] = 0xd4a19a02u,
                ["tk2_qa.5k"] = 0xc9183a0du,
                ["tk2a_22c.7f"] = 0x900ad4cdu,
                ["tk2a_23c.8f"] = 0x2e024628u,
                ["tk2e_22b.7f"] = 0x479b3f24u,
                ["tk2e_22c.7f"] = 0x608c17e3u,
                ["tk2e_23b.8f"] = 0x11fb2ed1u,
                ["tk2e_23c.8f"] = 0x0d708505u,
                ["tk2j_22c.7f"] = 0xb74b09acu,
                ["tk2j_23c.8f"] = 0x9b215a68u,
                ["tk2u_22c.7f"] = 0xf5af4774u,
                ["tk2u_23c.8f"] = 0x29b89c12u,
                ["tke_17.12b"] = 0xb3b79d4fu,
                ["tke_18.11c"] = 0xac6e307du,
                ["tke_19.12c"] = 0x068741dbu,
                ["tke_30.12e"] = 0xac6e307du,
                ["tke_31.13e"] = 0x068741dbu,
                ["tke_36.12f"] = 0x895991d1u,
                ["tke_37.13f"] = 0xb228d58cu,
                ["tke_42.12h"] = 0xc898d2e8u,
                ["tke_43.13h"] = 0x1a14375au,
                ["tkm-1.8a"] = 0x44f7661eu,
                ["tkm-2.4a"] = 0xca5c687cu,
                ["tkm-3.6a"] = 0xf9fe6591u,
                ["tkm-4.10a"] = 0xa54c515du,
                ["tkm-5.7a"] = 0xf64bb6a0u,
                ["tkm-6.3a"] = 0x0bf228cbu,
                ["tkm-7.5a"] = 0x1255dfb1u,
                ["tkm-8.9a"] = 0x21fe6274u,
                ["tkm-9.8h"] = 0x93654bcfu,
                ["tn2-01m.3a"] = 0xcb950cf9u,
                ["tn2-02m.4a"] = 0xf2016a34u,
                ["tn2-03m.5a"] = 0x18a5bf59u,
                ["tn2-04m.6a"] = 0x094e0fb1u,
                ["tn2-10m.3c"] = 0xa34ece70u,
                ["tn2-11m.4c"] = 0xd0edd30bu,
                ["tn2-12m.5c"] = 0xe04ff2f4u,
                ["tn2-13m.6c"] = 0x426621c3u,
                ["tn2292.1a"] = 0x3d899539u,
                ["tn2j_09.12a"] = 0xe464b969u,
                ["tn2j_18.11c"] = 0xa40bf9a7u,
                ["tn2j_19.12c"] = 0x5b3b931eu,
                ["tn2j_28.9e"] = 0x86d27f71u,
                ["tn2j_29.10e"] = 0x9c384e99u,
                ["tn2j_30.11e"] = 0x9226eb5eu,
                ["tn2j_31.12e"] = 0x015e6a8au,
                ["tn2j_35.9f"] = 0x7a1ab87du,
                ["tn2j_36.10f"] = 0x4c4b2a0au,
                ["tn2j_37.11f"] = 0xd1d30da1u,
                ["tn2j_38.12f"] = 0x1f139bccu,
                ["turboii.21"] = 0xed4186bdu,
                ["turboii.22"] = 0x3e57ba19u,
                ["turboii.23"] = 0x9bbfe420u,
                ["u1"] = 0xa01a9fb5u,
                ["u10l1_16.bin"] = 0x1be9a483u,
                ["u11f1.bin"] = 0x278d786cu,
                ["u11l1_17.bin"] = 0xdd3b95c0u,
                ["u133.bin"] = 0x13ea1c44u,
                ["u18"] = 0x8d2899bau,
                ["u19"] = 0xb34a4b42u,
                ["u195.bin"] = 0xdbee7b18u,
                ["u195.rom"] = 0xc95e4443u,
                ["u196"] = 0x39f15a1eu,
                ["u196-2i"] = 0xf758408cu,
                ["u196-2s"] = 0x9932832cu,
                ["u196.bin"] = 0x137d8665u,
                ["u196.rom"] = 0xb23a869du,
                ["u196chp"] = 0x95ea597eu,
                ["u196ne"] = 0xb07a4f90u,
                ["u2"] = 0xbdf02c17u,
                ["u20"] = 0x8987c975u,
                ["u21"] = 0xbc275b76u,
                ["u22"] = 0xf72cd219u,
                ["u221.bin"] = 0x0b3fe5ddu,
                ["u221.rom"] = 0x64e6e091u,
                ["u222"] = 0x03991fbau,
                ["u222-2i"] = 0x1ca7adbdu,
                ["u222-2s"] = 0x720cea3eu,
                ["u222.bin"] = 0x0d305e8bu,
                ["u222.rom"] = 0x9236a79au,
                ["u222chp"] = 0xdb567b66u,
                ["u222ne"] = 0x7133489eu,
                ["u23"] = 0x8d5ddc5du,
                ["u3"] = 0x058beefau,
                ["u4"] = 0x5028a9f1u,
                ["u5"] = 0xd77f89eau,
                ["u6"] = 0xbfbcb034u,
                ["u7"] = 0xa2544d4eu,
                ["u8"] = 0x8869bbb1u,
                ["u9"] = 0x2eb16a83u,
                ["usn_01.3a"] = 0xc08ffb8bu,
                ["usn_02.4a"] = 0x37b1b27au,
                ["usn_03.5a"] = 0x9f89a540u,
                ["usn_04.6a"] = 0x1f31f1d0u,
                ["usn_09.12f"] = 0x0eb8a1d4u,
                ["usn_18.11c"] = 0x4a613a2cu,
                ["usn_19.12c"] = 0x74584493u,
                ["usnj_22.7f"] = 0x54872f9fu,
                ["usnj_23.8f"] = 0xbbe45b1eu,
                ["v1.bin"] = 0x8962b469u,
                ["v2.bin"] = 0x6687df38u,
                ["v3.bin"] = 0x5782baeeu,
                ["va-1m.3a"] = 0x0b1ace37u,
                ["va-1m.4a"] = 0x0b1ace37u,
                ["va-3m.5a"] = 0x44dfe706u,
                ["va-3m.6a"] = 0x44dfe706u,
                ["va-5m.3a"] = 0xb1fb726eu,
                ["va-5m.7a"] = 0xb1fb726eu,
                ["va-7m.5a"] = 0x4c6588cdu,
                ["va-7m.9a"] = 0x4c6588cdu,
                ["va22b.1a"] = 0xbd7cd574u,
                ["va24b.1a"] = 0xcc476650u,
                ["va63b.1a"] = 0x132ab7c5u,
                ["va_01.3a"] = 0xb1fb726eu,
                ["va_01.4a"] = 0xc41312b5u,
                ["va_02.4a"] = 0x4c6588cdu,
                ["va_02.5a"] = 0x11590325u,
                ["va_03.5a"] = 0x0b1ace37u,
                ["va_04.6a"] = 0x44dfe706u,
                ["va_05.9a"] = 0x7065d4e9u,
                ["va_06.10a"] = 0x06e833acu,
                ["va_09.11a"] = 0x7a99446eu,
                ["va_09.12a"] = 0x7a99446eu,
                ["va_09.12b"] = 0x7a99446eu,
                ["va_09.4b"] = 0x183dfaa8u,
                ["va_10.5b"] = 0xd62750cdu,
                ["va_13.9b"] = 0x45537e69u,
                ["va_14.10b"] = 0xdc2f4783u,
                ["va_17.5c"] = 0x054f5a5bu,
                ["va_18.11c"] = 0xde30510eu,
                ["va_18.7c"] = 0xa17817c0u,
                ["va_19.12c"] = 0x0610a4acu,
                ["va_23.13c"] = 0x7a99446eu,
                ["va_24.5e"] = 0x57191ccfu,
                ["va_25.7e"] = 0x51d90690u,
                ["va_30.12e"] = 0xde30510eu,
                ["va_31.13e"] = 0x0610a4acu,
                ["va_32.8f"] = 0x3b4f40b2u,
                ["va_33.9f"] = 0x4b003af7u,
                ["va_38.8h"] = 0xe117a17eu,
                ["va_39.9h"] = 0xb0b12f51u,
                ["vae_28a.9f"] = 0x7a0e0d25u,
                ["vae_28b.9f"] = 0xe524ca50u,
                ["vae_29a.10f"] = 0x5e2cd2c3u,
                ["vae_29b.10f"] = 0x6640996au,
                ["vae_30a.11f"] = 0x7fcd0091u,
                ["vae_30b.11f"] = 0xadb8d391u,
                ["vae_31a.12f"] = 0x15e5ee81u,
                ["vae_31b.12f"] = 0x1749a71cu,
                ["vae_33a.9h"] = 0xf2365922u,
                ["vae_33b.9h"] = 0xc0bbf8c9u,
                ["vae_34a.10h"] = 0x3d9bdf83u,
                ["vae_34b.10h"] = 0xfa59be8au,
                ["vae_35a.11h"] = 0x35cf9509u,
                ["vae_35b.11h"] = 0x44e5548fu,
                ["vae_36a.12h"] = 0x153a201eu,
                ["vae_36b.12h"] = 0x5f2e2450u,
                ["vaj_22b.7f"] = 0x034e3e55u,
                ["vaj_23b.8f"] = 0xad3d3522u,
                ["vaj_34b.10f"] = 0x87c79aedu,
                ["vaj_35b.11f"] = 0x6b0da69fu,
                ["vaj_36b.12f"] = 0x1d798d6au,
                ["vaj_37b.13f"] = 0x24414b17u,
                ["vaj_40b.10h"] = 0x210b4bd0u,
                ["vaj_41b.11h"] = 0x6542c8a4u,
                ["vaj_42b.12h"] = 0x0f720233u,
                ["vaj_43b.13h"] = 0x34b4b06cu,
                ["varth-u196"] = 0x3b187dc3u,
                ["varth-u222"] = 0xfd2f93a8u,
                ["varth1.bin"] = 0x4c6a0d99u,
                ["varth4.bin"] = 0x53317bf6u,
                ["vau_22a.7f"] = 0x0ed71bbdu,
                ["vau_23a.8f"] = 0xfbe68726u,
                ["vco.2"] = 0xde0f0ef5u,
                ["voice.u210"] = 0x6cfffb11u,
                ["w-1"] = 0x6de44671u,
                ["w-2"] = 0xe8f14362u,
                ["w-3"] = 0xbf0cd819u,
                ["w-4"] = 0x76f9f91fu,
                ["w5.u196"] = 0x7e9c8c2fu,
                ["w6.u222"] = 0x49422b6fu,
                ["w7.u210"] = 0x6cfffb11u,
                ["wl22b.1a"] = 0x950cfa39u,
                ["wl24b.1a"] = 0x7101cdf1u,
                ["wl_01.4a"] = 0x08c2df12u,
                ["wl_02.5a"] = 0x86fba7a5u,
                ["wl_03.7a"] = 0x9cf3027du,
                ["wl_05.9a"] = 0xf5254bf2u,
                ["wl_06.10a"] = 0x1f052948u,
                ["wl_07.11a"] = 0xe35407aau,
                ["wl_09.12b"] = 0xf6b3d060u,
                ["wl_09.4b"] = 0x05aa71b4u,
                ["wl_10.3c"] = 0xb87b5a36u,
                ["wl_10.5b"] = 0xdbba0a3fu,
                ["wl_11.7b"] = 0x6f0adee5u,
                ["wl_12.5c"] = 0x7da49d69u,
                ["wl_13.9b"] = 0x1f7c87cdu,
                ["wl_14.10b"] = 0x7d5798b2u,
                ["wl_14.7c"] = 0x9cf3027du,
                ["wl_15.11b"] = 0xf09c8ecfu,
                ["wl_16.9c"] = 0xe35407aau,
                ["wl_17.5c"] = 0xa652f30cu,
                ["wl_18.11c"] = 0xbde23d4du,
                ["wl_18.7c"] = 0x316c7fbcu,
                ["wl_19.12c"] = 0x683898f5u,
                ["wl_19.8c"] = 0xb87b5a36u,
                ["wl_20.3d"] = 0x84992350u,
                ["wl_21.10c"] = 0x7da49d69u,
                ["wl_22.5d"] = 0xfd3f89f0u,
                ["wl_23.13c"] = 0xf6b3d060u,
                ["wl_24.5e"] = 0xd9d73ba1u,
                ["wl_24.7d"] = 0x6f0adee5u,
                ["wl_25.7e"] = 0x857d17d2u,
                ["wl_26.8e"] = 0x84992350u,
                ["wl_26.9d"] = 0xf09c8ecfu,
                ["wl_28.10e"] = 0xfd3f89f0u,
                ["wl_30.12e"] = 0xbde23d4du,
                ["wl_31.13e"] = 0x683898f5u,
                ["wl_32.8f"] = 0x10f64027u,
                ["wl_33.9f"] = 0xa15d5517u,
                ["wl_34.10f"] = 0x23a84f7au,
                ["wl_35.11f"] = 0x5eff7951u,
                ["wl_36.12f"] = 0x2b0d7cbcu,
                ["wl_37.13f"] = 0x30a717fau,
                ["wl_38.8h"] = 0xf6f9111bu,
                ["wl_39.9h"] = 0xe6fce9b0u,
                ["wl_40.10h"] = 0xc7a0ed21u,
                ["wl_41.11h"] = 0x8d6477a3u,
                ["wl_42.12h"] = 0x1ac39615u,
                ["wl_43.13h"] = 0xd0dddc9eu,
                ["wle_30.11f"] = 0x15372aa2u,
                ["wle_35.11h"] = 0x2e64623bu,
                ["wlm-1.5a"] = 0x4aa4c6d3u,
                ["wlm-3.3a"] = 0xc6f2abceu,
                ["wlm-32.8h"] = 0xdfd9f643u,
                ["wlm-5.9a"] = 0x12a0dc0bu,
                ["wlm-7.7a"] = 0xafa74b73u,
                ["wlu_30.11f"] = 0xd604dbb1u,
                ["wlu_31.12f"] = 0x0eb48a83u,
                ["wlu_35.11h"] = 0xdaee72feu,
                ["wlu_36.12h"] = 0x36100209u,
                ["wm91m-11-yd025.11"] = 0x3e66ad9du,
                ["xmqq-10a.bin"] = 0x638d4bc7u,
                ["xmqq-10b.bin"] = 0x9fa773efu,
                ["xmqq-12c.bin"] = 0x71ac69adu,
                ["xmqq-12f.bin"] = 0x196297bfu,
                ["xmqq-12h.bin"] = 0x2d7ee2e9u,
                ["xmqq-13b.bin"] = 0x4e8b81a8u,
                ["xmqq-13c.bin"] = 0x71e29699u,
                ["xmqq-13f.bin"] = 0x8f6abf26u,
                ["xmqq-13h.bin"] = 0x3fefe432u,
                ["xmqq-4a.bin"] = 0xb098e7a9u,
                ["xmqq-4b.bin"] = 0x933ab76du,
                ["xmqq-5a.bin"] = 0xbd40215eu,
                ["xmqq-5b.bin"] = 0x05354905u,
                ["xmqq-5c.bin"] = 0xe2169bb5u,
                ["xmqq-5e.bin"] = 0x63d06d6fu,
                ["xmqq-7c.bin"] = 0xd91cda18u,
                ["xmqq-7e.bin"] = 0x72c45858u,
                ["xmqq-8f.bin"] = 0xbeb00e07u,
                ["xmqq-8h.bin"] = 0x113121f5u,
                ["xmqq-9a.bin"] = 0x9c23e40bu,
                ["xmqq-9b.bin"] = 0xb66d62d4u,
                ["xmqq-9f.bin"] = 0x1ec10bedu,
                ["xmqq-9h.bin"] = 0x3cd8594bu,
                ["y.c.e.c d.w.c-011"] = 0xbc90c12fu,
                ["y.c.e.c d.w.c-012"] = 0x187667ccu,
                ["y.c.e.c d.w.c-013"] = 0x5b585071u,
                ["y.c.e.c m.k.r-001"] = 0xa258de13u,
                ["y.c.e.c m.k.r-002"] = 0x5726cab8u,
                ["y.c.e.c m.k.r-003"] = 0xc781bf87u,
                ["ycecdwc011.u64"] = 0xbc90c12fu,
                ["ycecdwc012.u19"] = 0x187667ccu,
                ["ycecdwc013.u18"] = 0x5b585071u,
                ["ycecmkr001.u70"] = 0xa258de13u,
                ["ycecmkr002.u68"] = 0x5726cab8u,
                ["ycecmkr003.u69"] = 0xc781bf87u,
                ["yi22b.1a"] = 0xb5cad2a0u,
                ["yi24b.1a"] = 0x3004dcdfu,
                ["yyc-2.2"] = 0xdb567b66u,
                ["yyc-3.4"] = 0x95ea597eu,
                ["yyc-4.1"] = 0x1073b7b6u,
                ["yyc-5.3"] = 0x924c6ce2u,
                ["yyc-6.1"] = 0x94778332u,
                ["yyc-7.10"] = 0xd1e452d3u,
                ["yyc-8.9"] = 0xf95bc505u,
                ["yyc-9.8"] = 0x155824a9u,
                ["yyc-a"] = 0x8242621fu,
                ["yyc-b"] = 0xb0159973u,
                ["yyc-c"] = 0x0793a960u,
                ["yyc-d"] = 0x92a8b572u,
                ["yyc-e"] = 0x61138469u,
                ["yyc-f"] = 0xb800dcdbu,
            };
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
                    Word(0x000000, $"{setName}_23c.8f", $"{setName}_23b.8f", $"{setName}.23c", $"{setName}_23b.rom", "tk2e_23c.8f", "tk2e_23b.8f", "tk2e_23b.rom", "tk2u.23c", "tk2a_23b.rom", "tk2j_23c.8f", "tk2j23c.bin"),
                    Word(0x080000, $"{setName}_22c.7f", $"{setName}_22b.7f", $"{setName}.22c", $"{setName}_22b.rom", "tk2e_22c.7f", "tk2e_22b.7f", "tk2e_22b.rom", "tk2u.22c", "tk2a_22b.rom", "tk2j_22c.7f", "tk2j22c.bin")
                },
                new[]
                {
                    Gfx(0x000000, "tk2-1m.3a", "tk2_gfx1.rom", "tk2_01.3a"),
                    Gfx(0x000002, "tk2-3m.5a", "tk2_gfx3.rom", "tk2_02.4a"),
                    Gfx(0x000004, "tk2-2m.4a", "tk2_gfx2.rom", "tk2_03.5a"),
                    Gfx(0x000006, "tk2-4m.6a", "tk2_gfx4.rom", "tk2_04.6a"),
                    Gfx(0x200000, "tk2_05.7a", "tk205.bin", "tk2-5m.7a", "tk2_gfx5.rom"),
                    Gfx(0x200002, "tk2_06.8a", "tk206.bin", "tk2-7m.9a", "tk2_gfx7.rom"),
                    Gfx(0x200004, "tk2_07.9a", "tk207.bin", "tk2-6m.8a", "tk2_gfx6.rom"),
                    Gfx(0x200006, "tk2_08.10a", "tk208.bin", "tk2-8m.10a", "tk2_gfx8.rom")
                },
                Audio("tk2_qa.5k", "tk2_qa.rom"),
                QSoundBanks("tk2-q1.1k|tk2_q1.rom", "tk2-q2.2k|tk2_q2.rom", "tk2-q3.3k|tk2_q3.rom", "tk2-q4.4k|tk2_q4.rom"));

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
                Audio("cd_q.5k", "cd_q.rom"),
                QSoundBanks("cd-q1.1k|cd_q1.rom", "cd-q2.2k|cd_q2.rom", "cd-q3.3k|cd_q3.rom", "cd-q4.4k|cd_q4.rom"));

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
                Audio("ps_q.5k", "ps_q.rom"),
                QSoundBanks("ps-q1.1k|ps_q1.rom", "ps-q2.2k|ps_q2.rom", "ps-q3.3k|ps_q3.rom", "ps-q4.4k|ps_q4.rom"));

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
                    Word(0x000000, "mbj_23e.8f", "mbj23e", "mbe_23e.8f", "mbe_23e.rom", "mbu_23e.8f", "mbu-23e.rom"),
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
                Audio("mb_qa.5k", "mb_qa.rom", "mb_q.5k", "mb_q.bin"),
                QSoundBanks(
                    "mb-q1.1k|mb_q1.bin",
                    "mb-q2.2k|mb_q2.bin",
                    "mb-q3.3k|mb_q3.bin",
                    "mb-q4.4k|mb_q4.bin",
                    "mb-q5.1m|mb_q5.bin",
                    "mb-q6.2m|mb_q6.bin",
                    "mb-q7.3m|mb_q7.bin",
                    "mb-q8.4m|mb_q8.bin"));

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
                Audio("ff_09.12b", "ffe_23.12b", "ff_23.12b"),
                new[]
                {
                    Audio(0x00000, 0, "ff_18.11c"),
                    Audio(0x20000, 0, "ff_19.12c")
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
                Audio("ff_23.bin", "ff_23.13c", "ff_23.13b"),
                new[]
                {
                    Audio(0x00000, 0, "ffj_30.bin", "ffj_30.12e", "ffj_30.12c", "ff_30.12e"),
                    Audio(0x20000, 0, "ffj_31.bin", "ffj_31.13e", "ffj_31.13c", "ff_31.13e")
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

        private static string? ResolveParentSetName(string setName, string? explicitParentSetName)
        {
            if (!string.IsNullOrWhiteSpace(explicitParentSetName))
                return explicitParentSetName;

            return GeneratedParentSets.TryGetValue(setName, out string? parentSetName) ? parentSetName : null;
        }

        private static RomLoad Word(int offset, params string[] names)
            => new(RomLoadKind.WordSwap, offset, 0, -1, names);

        private static RomLoad Byte(int offset, params string[] names)
            => new(RomLoadKind.Byte, offset, 0, -1, names);

        private static RomLoad Gfx(int offset, params string[] names)
            => new(RomLoadKind.Graphics64Word, offset, 0, -1, names);

        private static RomLoad GfxByte(int offset, params string[] names)
            => new(RomLoadKind.Graphics64Byte, offset, 0, -1, names);

        private static RomLoad Audio(int offset, int sourceOffset, params string[] names)
            => new(RomLoadKind.Raw, offset, sourceOffset, -1, names);

        private static RomLoad Load(RomLoadKind kind, int offset, int sourceOffset, int length, params string[] names)
            => new(kind, offset, sourceOffset, length, names);

        private static RomLoad[] Audio(params string[] names)
            => new[]
            {
                Load(RomLoadKind.Raw, 0x00000, 0x0000, 0x8000, names),
                Load(RomLoadKind.Raw, 0x10000, 0x8000, -1, names)
            };

        private static RomLoad[] QSoundBanks(params string[] banks)
        {
            var loads = new RomLoad[banks.Length];
            for (int i = 0; i < banks.Length; i++)
                loads[i] = Load(RomLoadKind.Raw, i * QSoundRomBankSize, 0, -1, banks[i].Split('|'));
            return loads;
        }

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
                    Copy16WordSwap(source, load.SourceOffset, load.Length, destination, load.Offset, load.Names[0]);
                    break;
                case RomLoadKind.Byte:
                    Copy16Byte(source, load.SourceOffset, load.Length, destination, load.Offset, load.Names[0]);
                    break;
                case RomLoadKind.Raw:
                    CopyRaw(source, load.SourceOffset, load.Length, destination, load.Offset, load.Names[0], "program");
                    break;
            }
        }

        private static void LoadGraphics(Dictionary<string, byte[]> entries, byte[] destination, RomLoad load)
        {
            switch (load.Kind)
            {
                case RomLoadKind.Graphics64Word:
                    Load64Word(entries, destination, load.Offset, load.SourceOffset, load.Length, load.Names);
                    break;
                case RomLoadKind.Graphics64Byte:
                    Load64Byte(entries, destination, load.Offset, load.SourceOffset, load.Length, load.Names);
                    break;
            }
        }

        private static void Load64Word(Dictionary<string, byte[]> entries, byte[] destination, int offset, int sourceOffset, int length, params string[] names)
        {
            byte[] source = Find(entries, names);
            int available = CopyLength(source, sourceOffset, length);
            int words = available / 2;
            for (int i = 0; i < words; i++)
            {
                int src = sourceOffset + i * 2;
                int dst = offset + i * 8;
                if (dst + 1 >= destination.Length)
                    throw new InvalidDataException($"ROM '{names[0]}' is too large for the CPS1 graphics region.");
                destination[dst] = source[src];
                destination[dst + 1] = source[src + 1];
            }
        }

        private static void Load64Byte(Dictionary<string, byte[]> entries, byte[] destination, int offset, int sourceOffset, int length, params string[] names)
        {
            byte[] source = Find(entries, names);
            int available = CopyLength(source, sourceOffset, length);
            for (int i = 0; i < available; i++)
            {
                int dst = offset + i * 8;
                if ((uint)dst >= destination.Length)
                    throw new InvalidDataException($"ROM '{names[0]}' is too large for the CPS1 graphics region.");
                destination[dst] = source[sourceOffset + i];
            }
        }

        private static void Copy16WordSwap(byte[] source, int sourceOffset, int length, byte[] destination, int offset, string name)
        {
            int copyLength = CopyLength(source, sourceOffset, length);
            if (offset + copyLength > destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for the CPS1 program region.");

            for (int i = 0; i + 1 < copyLength; i += 2)
            {
                destination[offset + i] = source[sourceOffset + i + 1];
                destination[offset + i + 1] = source[sourceOffset + i];
            }
        }

        private static void Copy16Byte(byte[] source, int sourceOffset, int length, byte[] destination, int offset, string name)
        {
            int copyLength = CopyLength(source, sourceOffset, length);
            int last = offset + (copyLength - 1) * 2;
            if ((uint)last >= destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for the CPS1 program region.");

            for (int i = 0; i < copyLength; i++)
                destination[offset + i * 2] = source[sourceOffset + i];
        }

        private static void CopyRaw(byte[] source, int sourceOffset, int length, byte[] destination, int offset, string name, string region)
        {
            int copyLength = CopyLength(source, sourceOffset, length);
            if (offset + copyLength > destination.Length)
                throw new InvalidDataException($"ROM '{name}' is too large for the CPS1 {region} region.");

            source.AsSpan(sourceOffset, copyLength).CopyTo(destination.AsSpan(offset));
        }

        private static int CopyLength(byte[] source, int sourceOffset, int length)
        {
            int available = Math.Max(0, source.Length - sourceOffset);
            return length < 0 ? available : Math.Min(length, available);
        }

        private static byte[] LoadAudioCpu(Dictionary<string, byte[]> entries, RomLoad[] loads)
        {
            byte[] audioCpu = new byte[AudioCpuRomSize];
            foreach (RomLoad load in loads)
            {
                byte[] source = Find(entries, load.Names);
                CopyRaw(source, load.SourceOffset, load.Length, audioCpu, load.Offset, load.Names[0], "audio CPU");
            }

            return audioCpu;
        }

        private static byte[] LoadQSound(Dictionary<string, byte[]> entries, int size, RomLoad[] loads)
        {
            byte[] qsound = new byte[size];
            foreach (RomLoad load in loads)
            {
                byte[] source = Find(entries, load.Names);
                CopyRaw(source, load.SourceOffset, load.Length, qsound, load.Offset, load.Names[0], "QSound sample");
            }

            return qsound;
        }

        private static byte[] LoadOki(Dictionary<string, byte[]> entries, int size, RomLoad[] loads)
        {
            byte[] oki = new byte[size];
            foreach (RomLoad load in loads)
            {
                byte[] source = Find(entries, load.Names);
                CopyRaw(source, load.SourceOffset, load.Length, oki, load.Offset, load.Names[0], "OKI sample");
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

        private readonly record struct RomLoad(RomLoadKind Kind, int Offset, int SourceOffset, int Length, string[] Names);

        private sealed record Cps1QSoundDefinition(
            string SetName,
            string? ParentSetName,
            Cps1VideoConfig VideoConfig,
            KabukiKeys KabukiKeys,
            int GraphicsSize,
            int QSoundSize,
            RomLoad[] ProgramLoads,
            RomLoad[] GraphicsLoads,
            RomLoad[] AudioCpuLoads,
            RomLoad[] QSoundLoads);

        private sealed record Cps1ClassicDefinition(
            string SetName,
            string? ParentSetName,
            Cps1VideoConfig VideoConfig,
            int GraphicsSize,
            int OkiSize,
            RomLoad[] ProgramLoads,
            RomLoad[] GraphicsLoads,
            RomLoad[] AudioCpuLoads,
            RomLoad[] OkiLoads);

        private static byte[] Find(Dictionary<string, byte[]> entries, params string[] names)
        {
            if (TryFind(entries, out byte[] data, names))
                return data;

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

            foreach (string name in names)
            {
                if (KnownRomCrcs.TryGetValue(name, out uint crc) && TryFindByCrc(entries, crc, out data))
                    return true;
            }

            data = Array.Empty<byte>();
            return false;
        }

        private static bool TryFindByCrc(Dictionary<string, byte[]> entries, uint wantedCrc, out byte[] data)
        {
            foreach (byte[] candidate in entries.Values)
            {
                if (ComputeCrc32(candidate) == wantedCrc)
                {
                    data = candidate;
                    return true;
                }
            }

            data = Array.Empty<byte>();
            return false;
        }

        private static uint ComputeCrc32(byte[] data)
        {
            uint crc = 0xffff_ffffu;
            foreach (byte value in data)
                crc = Crc32Table[(crc ^ value) & 0xff] ^ (crc >> 8);
            return crc ^ 0xffff_ffffu;
        }

        private static uint[] BuildCrc32Table()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint crc = i;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) != 0 ? 0xedb8_8320u ^ (crc >> 1) : crc >> 1;
                table[i] = crc;
            }

            return table;
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
