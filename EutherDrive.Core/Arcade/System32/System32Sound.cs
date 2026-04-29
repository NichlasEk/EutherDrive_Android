using System;
using EutherDrive.Core.Cpu.Z80Emu;
using EutherDrive.Core.MdTracerCore;

namespace EutherDrive.Core.Arcade.System32;

// Sega System 32 sound map and PCM behavior are translated from MAME's
// BSD-3-Clause Sega System 32/RF5C68/MultiPCM devices.
internal sealed class System32Sound : IOpcodeBusInterface
{
    private const int System32MasterClock = 32_215_900;
    private const int Multi32MasterClock = 32_000_000;
    private const int OutputSampleRate = 44_100;
    private const int OutputFramesPerFrame = OutputSampleRate / 60;
    private const double RfSampleRate = (50_000_000.0 / 4.0) / 384.0;

    private readonly byte[] _sharedRam;
    private readonly byte[] _soundRam = new byte[0x1_0000];
    private readonly Z80 _cpu = new();
    private readonly Ym2612 _ym1 = new(new bool[6], quantizeOutput: true, emulateLadderEffect: true, Opn2BusyBehavior.Ym3438);
    private readonly Ym2612 _ym2 = new(new bool[6], quantizeOutput: true, emulateLadderEffect: true, Opn2BusyBehavior.Ym3438);
    private readonly Rf5C68 _rf5c68 = new();
    private readonly SimpleMultiPcm _multiPcm = new(OutputSampleRate);
    private byte[] _soundRom = Array.Empty<byte>();
    private int _soundBank;
    private readonly byte[] _soundIrqControl = { 0xff, 0xff, 0xff, 0xff };
    private byte _soundIrqInput;
    private byte _soundDummy;
    private bool _resetAsserted = true;
    private bool _trace;
    private bool _isMulti32;
    private bool _ym1IrqAsserted;
    private double _ymTickAccumulator;
    private double _rfAccumulator;
    private double _rfPrevLeft;
    private double _rfPrevRight;
    private double _rfNextLeft;
    private double _rfNextRight;
    private double _ym1Left;
    private double _ym1Right;
    private double _ym2Left;
    private double _ym2Right;
    private double _outputFrameAccumulator;

    public System32Sound(byte[] sharedRam)
    {
        _sharedRam = sharedRam;
    }

    public void Load(System32RomSet roms)
    {
        _soundRom = roms.SoundCpu;
        _isMulti32 = roms.IsMulti32;
        _multiPcm.Load(roms.MultiPcm);
        _trace = string.Equals(Environment.GetEnvironmentVariable("EUTHERDRIVE_SYSTEM32_TRACE_SOUND"), "1", StringComparison.Ordinal);
        ResetSound();
    }

    public void ResetSound()
    {
        Array.Clear(_soundRam);
        _cpu.ApplyResetLine();
        _ym1.Reset();
        _ym2.Reset();
        _rf5c68.Reset();
        _multiPcm.Reset();
        _soundBank = 0;
        Array.Fill(_soundIrqControl, (byte)0xff);
        _soundIrqInput = 0;
        _soundDummy = 0;
        _resetAsserted = true;
        _ym1IrqAsserted = false;
        _ymTickAccumulator = 0;
        _rfAccumulator = 0;
        _rfPrevLeft = _rfPrevRight = _rfNextLeft = _rfNextRight = 0;
        _ym1Left = _ym1Right = _ym2Left = _ym2Right = 0;
        _outputFrameAccumulator = 0;
    }

    public void SetResetAsserted(bool asserted)
    {
        if (asserted == _resetAsserted)
            return;

        _resetAsserted = asserted;
        if (asserted)
            _cpu.ApplyResetLine();
    }

    public void SignalV60SoundIrq()
    {
        SignalSoundIrq(1);
    }

    public void RunFrame(short[] audioBuffer)
    {
        if (_soundRom.Length == 0)
        {
            Array.Clear(audioBuffer);
            return;
        }

        int cycles = 0;
        int writeFrame = 0;
        int peak = 0;
        int z80Clock = SoundZ80Clock;
        int z80CyclesPerFrame = z80Clock / 60;
        double outputFramesPerZ80Cycle = OutputSampleRate / (double)z80Clock;
        while (cycles < z80CyclesPerFrame)
        {
            uint elapsed = _cpu.ExecuteInstruction(this);
            cycles += (int)elapsed;

            _outputFrameAccumulator += elapsed * outputFramesPerZ80Cycle;
            int framesToRender = (int)_outputFrameAccumulator;
            if (framesToRender > 0)
            {
                int frameRoom = OutputFramesPerFrame - writeFrame;
                int renderNow = Math.Min(framesToRender, frameRoom);
                if (renderNow > 0)
                {
                    RenderAudioFrames(audioBuffer, ref writeFrame, renderNow, ref peak);
                    _outputFrameAccumulator -= renderNow;
                }

                if (writeFrame >= OutputFramesPerFrame)
                    break;
            }
        }

        if (writeFrame < OutputFramesPerFrame)
            RenderAudioFrames(audioBuffer, ref writeFrame, OutputFramesPerFrame - writeFrame, ref peak);

        if (_trace)
            Console.WriteLine($"[System32 Sound] pc=0x{_cpu.Pc:X4} bank={_soundBank} irqIn=0x{_soundIrqInput:X2} irqCtl={_soundIrqControl[0]:X2}/{_soundIrqControl[1]:X2}/{_soundIrqControl[2]:X2}/{_soundIrqControl[3]:X2} rfOn=0x{_rf5c68.DebugEnabledMask:X2} mpcmOn=0x{_multiPcm.DebugEnabledMask:X8} peak={peak}");
    }

    public byte ReadMemory(ushort address)
    {
        if (address <= 0x9fff)
            return ReadSoundRom(address);
        if (address is >= 0xa000 and <= 0xbfff)
            return ReadSoundRom((_soundBank * 0x2000) + (address - 0xa000));
        if (address is >= 0xc000 and <= 0xdfff)
            return _isMulti32 ? _multiPcm.Read(address - 0xc000) : _rf5c68.Read(address - 0xc000);
        if (address >= 0xe000)
            return _sharedRam[address - 0xe000];

        return 0xff;
    }

    public byte ReadOpcode(ushort address) => ReadMemory(address);

    public void WriteMemory(ushort address, byte value)
    {
        if (address is >= 0xc000 and <= 0xdfff)
        {
            if (_isMulti32)
                _multiPcm.Write(address - 0xc000, value);
            else
                _rf5c68.Write(address - 0xc000, value);
            return;
        }

        if (address >= 0xe000)
            _sharedRam[address - 0xe000] = value;
    }

    public byte ReadIo(ushort address)
    {
        byte port = (byte)address;
        if ((port & 0xf0) == 0x80)
            return _ym1.ReadRegister((ushort)(port & 0x03));
        if ((port & 0xf0) == 0x90)
            return _ym2.ReadRegister((ushort)(port & 0x03));
        if (port == 0xf1)
            return _soundDummy;

        return 0xff;
    }

    public void WriteIo(ushort address, byte value)
    {
        byte port = (byte)address;
        if ((port & 0xf0) == 0x80)
        {
            WriteYm(_ym1, port & 0x03, value);
            UpdateYm1Irq();
            return;
        }
        if ((port & 0xf0) == 0x90)
        {
            if (_isMulti32)
                return;

            WriteYm(_ym2, port & 0x03, value);
            return;
        }
        if ((port & 0xf0) == 0xa0)
        {
            _soundBank = (_soundBank & ~0x3f) | (value & 0x3f);
            return;
        }
        if ((port & 0xf0) == 0xb0)
        {
            if (_isMulti32)
            {
                _multiPcm.SetBanks(value & 7, (value >> 3) & 7);
                return;
            }

            _soundBank = (_soundBank & 0x3f) | ((value & 0x04) << 4) | ((value & 0x03) << 7);
            return;
        }
        if ((port & 0xf0) == 0xc0)
        {
            int offset = port & 0x0f;
            if ((offset & 1) != 0)
            {
                _soundIrqInput &= value;
            }
            if ((offset & 4) != 0)
            {
                // TODO: wire sound-to-main IRQ when a game needs it.
            }
            return;
        }
        if ((port & 0xf8) == 0xd0)
        {
            _soundIrqControl[port & 0x03] = value;
            return;
        }
        if (port == 0xf1)
            _soundDummy = value;
    }

    public InterruptLine Nmi() => InterruptLine.High;

    public InterruptLine Int()
    {
        byte effective = (byte)(_soundIrqInput & ~_soundIrqControl[3] & 0x07);
        return effective != 0 ? InterruptLine.Low : InterruptLine.High;
    }

    public byte InterruptVector()
    {
        byte effective = (byte)(_soundIrqInput & ~_soundIrqControl[3] & 0x07);
        for (int i = 0; i < 3; i++)
        {
            if ((effective & (1 << i)) != 0)
                return (byte)(2 * i);
        }

        return 0xff;
    }

    public bool BusReq() => false;

    public bool Reset() => _resetAsserted;

    private void SignalSoundIrq(int which)
    {
        for (int i = 0; i < 3; i++)
        {
            if (_soundIrqControl[i] == which)
                _soundIrqInput |= (byte)(1 << i);
        }
    }

    private void ClearSoundIrq(int which)
    {
        for (int i = 0; i < 3; i++)
        {
            if (_soundIrqControl[i] == which)
                _soundIrqInput &= (byte)~(1 << i);
        }
    }

    private void UpdateYm1Irq()
    {
        bool asserted = _ym1.TimerIrqAsserted;
        if (asserted == _ym1IrqAsserted)
            return;

        _ym1IrqAsserted = asserted;
        if (asserted)
            SignalSoundIrq(0);
        else
            ClearSoundIrq(0);
    }

    private byte ReadSoundRom(int offset)
    {
        return (uint)offset < _soundRom.Length ? _soundRom[offset] : (byte)0xff;
    }

    private static void WriteYm(Ym2612 ym, int port, byte value)
    {
        switch (port)
        {
            case 0:
                ym.WriteAddress1(value);
                break;
            case 1:
                ym.WriteData1(value);
                break;
            case 2:
                ym.WriteAddress2(value);
                break;
            case 3:
                ym.WriteData2(value);
                break;
        }
    }

    private void RenderAudioFrames(short[] destination, ref int writeFrame, int frameCount, ref int peak)
    {
        double ymTicksPerOutput = YmTickRate / OutputSampleRate;
        double rfSamplesPerOutput = RfSampleRate / OutputSampleRate;
        int write = writeFrame * 2;
        int maxFrames = Math.Min(frameCount, Math.Max(0, (destination.Length / 2) - writeFrame));
        for (int i = 0; i < maxFrames; i++)
        {
            _ymTickAccumulator += ymTicksPerOutput;
            int ymTicks = (int)_ymTickAccumulator;
            if (ymTicks > 0)
            {
                _ymTickAccumulator -= ymTicks;
                _ym1.Tick(ymTicks, (left, right) =>
                {
                    _ym1Left = left;
                    _ym1Right = right;
                });
                UpdateYm1Irq();
                if (!_isMulti32)
                {
                    _ym2.Tick(ymTicks, (left, right) =>
                    {
                        _ym2Left = left;
                        _ym2Right = right;
                    });
                }
            }

            double pcmLeft;
            double pcmRight;
            if (_isMulti32)
            {
                (pcmLeft, pcmRight) = _multiPcm.RenderSample();
            }
            else
            {
                _rfAccumulator += rfSamplesPerOutput;
                while (_rfAccumulator >= 1.0)
                {
                    _rfPrevLeft = _rfNextLeft;
                    _rfPrevRight = _rfNextRight;
                    (_rfNextLeft, _rfNextRight) = _rf5c68.RenderSample();
                    _rfAccumulator -= 1.0;
                }

                pcmLeft = Lerp(_rfPrevLeft, _rfNextLeft, _rfAccumulator);
                pcmRight = Lerp(_rfPrevRight, _rfNextRight, _rfAccumulator);
            }

            double ymLeft = _isMulti32 ? _ym1Left : _ym1Left + _ym2Left;
            double ymRight = _isMulti32 ? _ym1Right : _ym1Right + _ym2Right;
            double leftMix = (ymLeft * 0.30) + (pcmLeft * 0.45);
            double rightMix = (ymRight * 0.30) + (pcmRight * 0.45);
            short left = ToSample(leftMix);
            short right = ToSample(rightMix);
            destination[write++] = left;
            destination[write++] = right;
            peak = Math.Max(peak, Math.Abs(left));
            peak = Math.Max(peak, Math.Abs(right));
            writeFrame++;
        }
    }

    private int SoundZ80Clock => (_isMulti32 ? Multi32MasterClock : System32MasterClock) / 4;

    private double YmTickRate => ((_isMulti32 ? Multi32MasterClock : System32MasterClock) / 4.0) / 6.0;

    private static double Lerp(double previous, double next, double fraction)
        => previous + ((next - previous) * fraction);

    private static short ToSample(double value)
    {
        value = Math.Clamp(value, -1.0, 1.0);
        return (short)Math.Round(value * short.MaxValue);
    }

    private sealed class SimpleMultiPcm
    {
        private const int TlShift = 12;
        private const int EgShift = 16;
        private const int LfoShift = 8;
        private const double ChipRate = (40_000_000.0 / 4.0) / 224.0;
        private static readonly double[] BaseTimes =
        {
            0, 0, 0, 0,
            6222.95, 4978.37, 4148.66, 3556.01,
            3111.47, 2489.21, 2074.33, 1778.00,
            1555.74, 1244.63, 1037.19, 889.02,
            777.87, 622.31, 518.59, 444.54,
            388.93, 311.16, 259.32, 222.27,
            194.47, 155.60, 129.66, 111.16,
            97.23, 77.82, 64.85, 55.60,
            48.62, 38.91, 32.43, 27.80,
            24.31, 19.46, 16.24, 13.92,
            12.15, 9.75, 8.12, 6.98,
            6.08, 4.90, 4.08, 3.49,
            3.04, 2.49, 2.13, 1.90,
            1.72, 1.41, 1.18, 1.04,
            0.91, 0.73, 0.59, 0.50,
            0.45, 0.45, 0.45, 0.45
        };
        private static readonly double[] LfoFreq =
        {
            0.168, 2.019, 3.196, 4.206, 5.215, 5.888, 6.224, 7.066
        };
        private static readonly double[] PhaseScaleLimit =
        {
            0.0, 3.378, 5.065, 6.750, 10.114, 20.170, 40.180, 79.307
        };
        private static readonly double[] AmplitudeScaleLimit =
        {
            0.0, 0.4, 0.8, 1.5, 3.0, 6.0, 12.0, 24.0
        };
        private static readonly int[] ValueToChannel =
        {
            0, 1, 2, 3, 4, 5, 6, -1,
            7, 8, 9, 10, 11, 12, 13, -1,
            14, 15, 16, 17, 18, 19, 20, -1,
            21, 22, 23, 24, 25, 26, 27, -1
        };

        private readonly Slot[] _slots = new Slot[28];
        private readonly int _outputRate;
        private readonly int[] _attackStep = new int[0x40];
        private readonly int[] _decayReleaseStep = new int[0x40];
        private readonly long[] _freqStepTable = new long[0x400];
        private readonly int[] _leftPanTable = new int[0x800];
        private readonly int[] _rightPanTable = new int[0x800];
        private readonly int[] _linearToExpVolume = new int[0x400];
        private readonly int[] _totalLevelSteps = new int[2];
        private readonly int[] _pitchTable = new int[256];
        private readonly int[] _amplitudeTable = new int[256];
        private readonly int[][] _pitchScaleTables = new int[8][];
        private readonly int[][] _amplitudeScaleTables = new int[8][];
        private byte[] _rom = Array.Empty<byte>();
        private int _currentSlot;
        private int _registerAddress;
        private int _bankLow;
        private int _bankHigh;

        public SimpleMultiPcm(int outputRate)
        {
            _outputRate = outputRate;
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = new Slot();
            InitTables();
        }

        public int DebugEnabledMask
        {
            get
            {
                int mask = 0;
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].Playing)
                        mask |= 1 << i;
                }

                return mask;
            }
        }

        public void Load(byte[] rom)
        {
            _rom = rom;
        }

        public void Reset()
        {
            foreach (Slot slot in _slots)
                slot.Reset();
            _currentSlot = 0;
            _registerAddress = 0;
            _bankLow = 0;
            _bankHigh = 0;
        }

        public byte Read(int offset) => 0;

        public void Write(int offset, byte value)
        {
            switch (offset & 3)
            {
                case 0:
                    if ((uint)_currentSlot < _slots.Length)
                        WriteSlot(_slots[_currentSlot], _registerAddress, value);
                    break;
                case 1:
                    _currentSlot = ValueToChannel[value & 0x1f];
                    break;
                case 2:
                    _registerAddress = value > 7 ? 7 : value;
                    break;
            }
        }

        public void SetBanks(int low, int high)
        {
            _bankLow = low & 7;
            _bankHigh = high & 7;
        }

        public (double Left, double Right) RenderSample()
        {
            if (_rom.Length == 0)
                return (0, 0);

            int left = 0;
            int right = 0;
            foreach (Slot slot in _slots)
            {
                if (!slot.Playing)
                    continue;

                int volumeIndex = ((slot.TotalLevel >> TlShift) & 0x7f) | (slot.Pan << 7);
                int samplePosition = slot.Offset >> TlShift;
                int step = slot.Step;
                if (slot.Reverse)
                    samplePosition = slot.Sample.End - samplePosition - 1;

                int currentSample = ReadSample(slot, samplePosition);
                int fraction = slot.Offset & ((1 << TlShift) - 1);
                int sample = ((currentSample * fraction) + (slot.PreviousSample * ((1 << TlShift) - fraction))) >> TlShift;

                if (slot.Vibrato != 0)
                    step = (int)(((long)step * PitchLfoStep(slot.PitchLfo)) >> TlShift);

                slot.Offset += step;
                if ((samplePosition ^ (slot.Offset >> TlShift)) != 0)
                    slot.PreviousSample = currentSample;

                int end = Math.Max(1, slot.Sample.End) << TlShift;
                if (slot.Offset >= end)
                {
                    int wrap = Math.Max(1, slot.Sample.End - slot.Sample.Loop) << TlShift;
                    slot.Offset -= wrap;
                    if (slot.Offset < 0)
                        slot.Offset = 0;
                    slot.Reverse = false;
                }

                UpdateTotalLevel(slot);

                if (slot.Tremolo != 0)
                    sample = (int)(((long)sample * AmplitudeLfoStep(slot.AmplitudeLfo)) >> TlShift);

                sample = (int)(((long)sample * EnvelopeGeneratorUpdate(slot)) >> 10);
                left += (int)(((long)_leftPanTable[volumeIndex] * sample) >> TlShift);
                right += (int)(((long)_rightPanTable[volumeIndex] * sample) >> TlShift);
            }

            return (Math.Clamp(left / 32768.0, -1.0, 1.0), Math.Clamp(right / 32768.0, -1.0, 1.0));
        }

        private void WriteSlot(Slot slot, int register, byte value)
        {
            slot.Regs[register & 7] = value;
            switch (register & 7)
            {
                case 0:
                    slot.Pan = (value >> 4) & 0x0f;
                    break;
                case 1:
                    InitSample(slot, slot.Regs[1] | ((slot.Regs[2] & 1) << 8));
                    WriteSlot(slot, 6, (byte)slot.Sample.LfoVibratoReg);
                    WriteSlot(slot, 7, (byte)slot.Sample.LfoAmplitudeReg);
                    if (slot.Playing)
                        Retrigger(slot);
                    break;
                case 2:
                case 3:
                    slot.Octave = slot.Regs[3] >> 4;
                    slot.Pitch = ((slot.Regs[3] & 0x0f) << 6) | (slot.Regs[2] >> 2);
                    UpdateStep(slot);
                    break;
                case 4:
                    if ((value & 0x80) != 0)
                    {
                        slot.Playing = true;
                        Retrigger(slot);
                    }
                    else
                    {
                        if (slot.Playing)
                        {
                            if (slot.Sample.ReleaseReg != 0x0f)
                                slot.Envelope.State = EnvelopeState.Release;
                            else
                                slot.Playing = false;
                        }
                    }

                    break;
                case 5:
                    slot.DestTotalLevel = (value >> 1) & 0x7f;
                    if ((value & 1) == 0)
                    {
                        slot.TotalLevelStep = (slot.TotalLevel >> TlShift) > slot.DestTotalLevel
                            ? _totalLevelSteps[0]
                            : _totalLevelSteps[1];
                    }
                    else
                    {
                        slot.TotalLevel = slot.DestTotalLevel << TlShift;
                        slot.TotalLevelStep = 0;
                    }
                    break;
                case 6:
                case 7:
                    slot.LfoFrequency = (slot.Regs[6] >> 3) & 7;
                    slot.Vibrato = slot.Regs[6] & 7;
                    slot.Tremolo = slot.Regs[7] & 7;
                    if (value != 0)
                    {
                        LfoComputeStep(slot.PitchLfo, slot.LfoFrequency, slot.Vibrato, amplitudeLfo: false);
                        LfoComputeStep(slot.AmplitudeLfo, slot.LfoFrequency, slot.Tremolo, amplitudeLfo: true);
                    }
                    break;
            }
        }

        private void InitSample(Slot slot, int index)
        {
            int address = index * 12;
            int start = (ReadRom(address) << 16) | (ReadRom(address + 1) << 8) | ReadRom(address + 2);
            slot.Sample.Start = start & 0x3f_ffff;
            slot.Sample.Format = (start >> 20) & 0xfe;
            slot.Sample.Loop = (ReadRom(address + 3) << 8) | ReadRom(address + 4);
            slot.Sample.End = 0x1_0000 - ((ReadRom(address + 5) << 8) | ReadRom(address + 6));
            slot.Sample.AttackReg = (ReadRom(address + 8) >> 4) & 0x0f;
            slot.Sample.Decay1Reg = ReadRom(address + 8) & 0x0f;
            slot.Sample.Decay2Reg = ReadRom(address + 9) & 0x0f;
            slot.Sample.DecayLevel = (ReadRom(address + 9) >> 4) & 0x0f;
            slot.Sample.ReleaseReg = ReadRom(address + 10) & 0x0f;
            slot.Sample.KeyRateScale = (ReadRom(address + 10) >> 4) & 0x0f;
            slot.Sample.LfoVibratoReg = ReadRom(address + 7);
            slot.Sample.LfoAmplitudeReg = ReadRom(address + 11) & 0x0f;
            UpdateStep(slot);
        }

        private void Retrigger(Slot slot)
        {
            slot.Offset = 0;
            slot.PreviousSample = 0;
            slot.TotalLevel = slot.DestTotalLevel << TlShift;
            EnvelopeGeneratorCalc(slot);
            slot.Envelope.State = EnvelopeState.Attack;
            slot.Envelope.Volume = 0;
            if (slot.Sample.End <= 0)
                slot.Sample.End = 0x1_0000;
        }

        private void UpdateStep(Slot slot)
        {
            int octave = (slot.Octave - 1) & 0x0f;
            long pitch = _freqStepTable[slot.Pitch & 0x3ff];
            if ((octave & 0x08) != 0)
                pitch >>= 16 - octave;
            else
                pitch <<= octave;
            slot.Step = Math.Max(1, (int)(pitch / _outputRate));
        }

        private int ReadSample(Slot slot, int sampleOffset)
        {
            if ((slot.Sample.Format & 4) != 0)
            {
                int address = slot.Sample.Start + ((sampleOffset >> 1) * 3);
                if ((sampleOffset & 1) == 0)
                    return SignExtend12((ReadRom(address) << 4) | (ReadRom(address + 1) & 0x0f)) << 4;
                return SignExtend12((ReadRom(address + 2) << 4) | (ReadRom(address + 1) >> 4)) << 4;
            }

            return unchecked((sbyte)ReadRom(slot.Sample.Start + sampleOffset)) << 8;
        }

        private void UpdateTotalLevel(Slot slot)
        {
            int target = slot.DestTotalLevel << TlShift;
            if (slot.TotalLevel == target)
                return;

            int next = slot.TotalLevel + slot.TotalLevelStep;
            if (slot.TotalLevelStep < 0 && next < target)
                next = target;
            else if (slot.TotalLevelStep > 0 && next > target)
                next = target;
            slot.TotalLevel = next;
        }

        private int EnvelopeGeneratorUpdate(Slot slot)
        {
            switch (slot.Envelope.State)
            {
                case EnvelopeState.Attack:
                    slot.Envelope.Volume += slot.Envelope.AttackRate;
                    if (slot.Envelope.Volume >= (0x3ff << EgShift))
                    {
                        slot.Envelope.State = EnvelopeState.Decay1;
                        if (slot.Envelope.Decay1Rate >= (0x400 << EgShift))
                            slot.Envelope.State = EnvelopeState.Decay2;
                        slot.Envelope.Volume = 0x3ff << EgShift;
                    }
                    break;
                case EnvelopeState.Decay1:
                    slot.Envelope.Volume -= slot.Envelope.Decay1Rate;
                    if (slot.Envelope.Volume <= 0)
                        slot.Envelope.Volume = 0;
                    if ((slot.Envelope.Volume >> (EgShift + 6)) <= slot.Envelope.DecayLevel)
                        slot.Envelope.State = EnvelopeState.Decay2;
                    break;
                case EnvelopeState.Decay2:
                    slot.Envelope.Volume -= slot.Envelope.Decay2Rate;
                    if (slot.Envelope.Volume <= 0)
                        slot.Envelope.Volume = 0;
                    break;
                case EnvelopeState.Release:
                    slot.Envelope.Volume -= slot.Envelope.ReleaseRate;
                    if (slot.Envelope.Volume <= 0)
                    {
                        slot.Envelope.Volume = 0;
                        slot.Playing = false;
                    }
                    break;
            }

            return _linearToExpVolume[Math.Clamp(slot.Envelope.Volume >> EgShift, 0, 0x3ff)];
        }

        private void EnvelopeGeneratorCalc(Slot slot)
        {
            int octave = slot.Octave;
            if ((octave & 8) != 0)
                octave -= 16;

            int rate = slot.Sample.KeyRateScale != 0x0f
                ? ((octave + slot.Sample.KeyRateScale) * 2) + ((slot.Pitch >> 9) & 1)
                : 0;

            slot.Envelope.AttackRate = GetRate(_attackStep, rate, slot.Sample.AttackReg);
            slot.Envelope.Decay1Rate = GetRate(_decayReleaseStep, rate, slot.Sample.Decay1Reg);
            slot.Envelope.Decay2Rate = GetRate(_decayReleaseStep, rate, slot.Sample.Decay2Reg);
            slot.Envelope.ReleaseRate = GetRate(_decayReleaseStep, rate, slot.Sample.ReleaseReg);
            slot.Envelope.DecayLevel = 0x0f - slot.Sample.DecayLevel;
        }

        private static int GetRate(int[] steps, int rate, int value)
        {
            if (value == 0)
                return steps[0];
            if (value == 0x0f)
                return steps[0x3f];
            return steps[Math.Clamp((4 * value) + rate, 0, 0x3f)];
        }

        private int PitchLfoStep(Lfo lfo)
        {
            lfo.Phase = (ushort)(lfo.Phase + lfo.PhaseStep);
            int p = lfo.Table[(lfo.Phase >> LfoShift) & 0xff];
            p = lfo.Scale[p];
            return p << (TlShift - LfoShift);
        }

        private int AmplitudeLfoStep(Lfo lfo)
        {
            lfo.Phase = (ushort)(lfo.Phase + lfo.PhaseStep);
            int p = lfo.Table[(lfo.Phase >> LfoShift) & 0xff];
            p = lfo.Scale[p];
            return p << (TlShift - LfoShift);
        }

        private void LfoComputeStep(Lfo lfo, int lfoFrequency, int lfoScale, bool amplitudeLfo)
        {
            double step = LfoFreq[lfoFrequency & 7] * 256.0 / _outputRate;
            lfo.PhaseStep = (int)((1 << LfoShift) * step);
            if (amplitudeLfo)
            {
                lfo.Table = _amplitudeTable;
                lfo.Scale = _amplitudeScaleTables[lfoScale & 7];
            }
            else
            {
                lfo.Table = _pitchTable;
                lfo.Scale = _pitchScaleTables[lfoScale & 7];
            }
        }

        private void InitTables()
        {
            for (int level = 0; level < 0x80; level++)
            {
                double volumeDb = level * -24.0 / 64.0;
                double totalLevel = Math.Pow(10.0, volumeDb / 20.0) / 4.0;
                for (int pan = 0; pan < 0x10; pan++)
                {
                    double panLeft;
                    double panRight;
                    if (pan == 0x08)
                    {
                        panLeft = 0;
                        panRight = 0;
                    }
                    else if (pan == 0)
                    {
                        panLeft = 1;
                        panRight = 1;
                    }
                    else if ((pan & 0x08) != 0)
                    {
                        panLeft = 1;
                        int invertedPan = 0x10 - pan;
                        panRight = Math.Pow(10.0, (invertedPan * -12.0 / 4.0) / 20.0);
                        if ((invertedPan & 7) == 7)
                            panRight = 0;
                    }
                    else
                    {
                        panRight = 1;
                        panLeft = Math.Pow(10.0, (pan * -12.0 / 4.0) / 20.0);
                        if ((pan & 7) == 7)
                            panLeft = 0;
                    }

                    _leftPanTable[(pan << 7) | level] = ValueToFixed(TlShift, panLeft * totalLevel);
                    _rightPanTable[(pan << 7) | level] = ValueToFixed(TlShift, panRight * totalLevel);
                }
            }

            for (int i = 0; i < 0x400; i++)
                _freqStepTable[i] = ValueToFixedLong(TlShift, ChipRate * (1024.0 + i) / 1024.0);

            for (int i = 4; i < 0x40; i++)
            {
                _attackStep[i] = (int)((0x400 << EgShift) / (BaseTimes[i] * 44100.0 / 1000.0));
                _decayReleaseStep[i] = (int)((0x400 << EgShift) / (BaseTimes[i] * 14.32833 * 44100.0 / 1000.0));
            }
            _attackStep[0x3f] = 0x400 << EgShift;

            _totalLevelSteps[0] = -(int)((0x80 << TlShift) / (78.2 * 44100.0 / 1000.0));
            _totalLevelSteps[1] = (int)((0x80 << TlShift) / (78.2 * 2.0 * 44100.0 / 1000.0));

            for (int i = 0; i < 0x400; i++)
            {
                double db = -(96.0 - (96.0 * i / 0x400));
                _linearToExpVolume[i] = ValueToFixed(TlShift, Math.Pow(10.0, db / 20.0));
            }

            InitLfoTables();
        }

        private void InitLfoTables()
        {
            for (int i = 0; i < 256; i++)
            {
                if (i < 64)
                    _pitchTable[i] = (i * 2) + 128;
                else if (i < 128)
                    _pitchTable[i] = 383 - (i * 2);
                else if (i < 192)
                    _pitchTable[i] = 384 - (i * 2);
                else
                    _pitchTable[i] = (i * 2) - 383;

                _amplitudeTable[i] = i < 128 ? 255 - (i * 2) : (i * 2) - 256;
            }

            for (int table = 0; table < 8; table++)
            {
                _pitchScaleTables[table] = new int[256];
                double limit = PhaseScaleLimit[table];
                for (int i = -128; i < 128; i++)
                {
                    double value = limit * i / 128.0;
                    _pitchScaleTables[table][i + 128] = ValueToFixed(LfoShift, Math.Pow(2.0, value / 1200.0));
                }

                _amplitudeScaleTables[table] = new int[256];
                limit = -AmplitudeScaleLimit[table];
                for (int i = 0; i < 256; i++)
                {
                    double value = limit * i / 256.0;
                    _amplitudeScaleTables[table][i] = ValueToFixed(LfoShift, Math.Pow(10.0, value / 20.0));
                }
            }
        }

        private byte ReadRom(int address)
        {
            int offset = MapRomAddress(address & 0x3f_ffff);
            return (uint)offset < _rom.Length ? _rom[offset] : (byte)0;
        }

        private int MapRomAddress(int address)
        {
            if (address < 0x10_0000)
                return address;
            if (address < 0x18_0000)
                return 0x10_0000 + (_bankLow * 0x8_0000) + (address - 0x10_0000);
            if (address < 0x20_0000)
                return 0x10_0000 + (_bankHigh * 0x8_0000) + (address - 0x18_0000);
            return address;
        }

        private static int ValueToFixed(int bits, double value)
            => (int)((1 << bits) * value);

        private static long ValueToFixedLong(int bits, double value)
            => (long)((1 << bits) * value);

        private static int SignExtend12(int value)
        {
            value &= 0x0fff;
            return (value & 0x0800) != 0 ? value - 0x1000 : value;
        }

        private enum EnvelopeState : byte
        {
            Attack,
            Decay1,
            Decay2,
            Release
        }

        private sealed class Sample
        {
            public int Start;
            public int Loop;
            public int End;
            public int AttackReg;
            public int Decay1Reg;
            public int Decay2Reg;
            public int DecayLevel;
            public int ReleaseReg;
            public int KeyRateScale;
            public int LfoVibratoReg;
            public int LfoAmplitudeReg;
            public int Format;
        }

        private sealed class Envelope
        {
            public int Volume;
            public EnvelopeState State = EnvelopeState.Attack;
            public int AttackRate;
            public int Decay1Rate;
            public int Decay2Rate;
            public int ReleaseRate;
            public int DecayLevel;
        }

        private sealed class Lfo
        {
            public int Phase;
            public int PhaseStep;
            public int[] Table = Array.Empty<int>();
            public int[] Scale = Array.Empty<int>();
        }

        private sealed class Slot
        {
            public readonly byte[] Regs = new byte[8];
            public readonly Sample Sample = new();
            public readonly Envelope Envelope = new();
            public readonly Lfo PitchLfo = new();
            public readonly Lfo AmplitudeLfo = new();
            public bool Playing;
            public bool Reverse;
            public int Pan;
            public int TotalLevel;
            public int DestTotalLevel;
            public int TotalLevelStep;
            public int Offset;
            public int Octave;
            public int Pitch;
            public int Step;
            public int PreviousSample;
            public int LfoFrequency;
            public int Vibrato;
            public int Tremolo;

            public void Reset()
            {
                Array.Clear(Regs);
                Playing = false;
                Reverse = false;
                Pan = 0;
                TotalLevel = 0;
                DestTotalLevel = 0;
                TotalLevelStep = 0;
                Offset = 0;
                Octave = 0;
                Pitch = 0;
                Step = 1;
                PreviousSample = 0;
                LfoFrequency = 0;
                Vibrato = 0;
                Tremolo = 0;
                Sample.Start = 0;
                Sample.Loop = 0;
                Sample.End = 0x1_0000;
                Sample.AttackReg = 0;
                Sample.Decay1Reg = 0;
                Sample.Decay2Reg = 0;
                Sample.DecayLevel = 0;
                Sample.ReleaseReg = 0;
                Sample.KeyRateScale = 0;
                Sample.LfoVibratoReg = 0;
                Sample.LfoAmplitudeReg = 0;
                Sample.Format = 0;
                Envelope.Volume = 0;
                Envelope.State = EnvelopeState.Attack;
                Envelope.AttackRate = 0;
                Envelope.Decay1Rate = 0;
                Envelope.Decay2Rate = 0;
                Envelope.ReleaseRate = 0;
                Envelope.DecayLevel = 0;
                PitchLfo.Phase = 0;
                PitchLfo.PhaseStep = 0;
                AmplitudeLfo.Phase = 0;
                AmplitudeLfo.PhaseStep = 0;
            }
        }
    }

    private sealed class Rf5C68
    {
        private const int AddressFractionBits = 11;
        private readonly byte[] _ram = new byte[0x1_0000];
        private readonly Channel[] _channels =
        {
            new(), new(), new(), new(), new(), new(), new(), new()
        };
        private byte _currentBank;
        private ushort _writeBank;
        private bool _enabled;

        public int DebugEnabledMask
        {
            get
            {
                int mask = 0;
                for (int i = 0; i < _channels.Length; i++)
                    if (_channels[i].Enabled)
                        mask |= 1 << i;
                return mask;
            }
        }

        public void Reset()
        {
            Array.Clear(_ram);
            foreach (Channel channel in _channels)
                channel.Reset();
            _currentBank = 0;
            _writeBank = 0;
            _enabled = false;
        }

        public byte Read(int offset)
        {
            offset &= 0x1fff;
            if ((offset & 0x1000) != 0)
                return _ram[_writeBank | (offset & 0x0fff)];
            return 0xff;
        }

        public void Write(int offset, byte value)
        {
            offset &= 0x1fff;
            if ((offset & 0x1000) != 0)
            {
                _ram[_writeBank | (offset & 0x0fff)] = value;
                return;
            }

            int register = offset & 0x0f;
            if (register > 8)
                return;

            Channel channel = _channels[_currentBank & 7];
            switch (register)
            {
                case 0:
                    channel.Envelope = value;
                    break;
                case 1:
                    channel.Pan = value;
                    break;
                case 2:
                    channel.Step = (ushort)((channel.Step & 0xff00) | value);
                    break;
                case 3:
                    channel.Step = (ushort)((channel.Step & 0x00ff) | (value << 8));
                    break;
                case 4:
                    channel.LoopStart = (ushort)((channel.LoopStart & 0xff00) | value);
                    break;
                case 5:
                    channel.LoopStart = (ushort)((channel.LoopStart & 0x00ff) | (value << 8));
                    break;
                case 6:
                    channel.Start = value;
                    if (!channel.Enabled)
                        channel.Address = (uint)(channel.Start << (8 + AddressFractionBits));
                    break;
                case 7:
                    _enabled = (value & 0x80) != 0;
                    if ((value & 0x40) != 0)
                        _currentBank = (byte)(value & 7);
                    else
                        _writeBank = (ushort)((value & 0x0f) << 12);
                    break;
                case 8:
                    for (int i = 0; i < _channels.Length; i++)
                    {
                        bool enabled = ((~value >> i) & 1) != 0;
                        _channels[i].Enabled = enabled;
                        if (!enabled)
                            _channels[i].Address = (uint)(_channels[i].Start << (8 + AddressFractionBits));
                    }
                    break;
            }
        }

        public (double Left, double Right) RenderSample()
        {
            if (!_enabled)
                return (0, 0);

            int left = 0;
            int right = 0;
            foreach (Channel channel in _channels)
            {
                if (!channel.Enabled)
                    continue;

                int sample = _ram[(channel.Address >> AddressFractionBits) & 0xffff];
                if (sample == 0xff)
                {
                    channel.Address = (uint)(channel.LoopStart << AddressFractionBits);
                    sample = _ram[(channel.Address >> AddressFractionBits) & 0xffff];
                    if (sample == 0xff)
                        continue;
                }

                channel.Address += channel.Step;
                int lv = (channel.Pan & 0x0f) * channel.Envelope;
                int rv = ((channel.Pan >> 4) & 0x0f) * channel.Envelope;
                if ((sample & 0x80) != 0)
                {
                    sample &= 0x7f;
                    left += (sample * lv) >> 5;
                    right += (sample * rv) >> 5;
                }
                else
                {
                    left -= (sample * lv) >> 5;
                    right -= (sample * rv) >> 5;
                }
            }

            return (Math.Clamp(left, -32768, 32767) / 32768.0, Math.Clamp(right, -32768, 32767) / 32768.0);
        }

        private sealed class Channel
        {
            public bool Enabled;
            public byte Envelope;
            public byte Pan;
            public byte Start;
            public uint Address;
            public ushort Step;
            public ushort LoopStart;

            public void Reset()
            {
                Enabled = false;
                Envelope = 0;
                Pan = 0;
                Start = 0;
                Address = 0;
                Step = 0;
                LoopStart = 0;
            }
        }
    }
}
