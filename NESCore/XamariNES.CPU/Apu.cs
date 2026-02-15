using System;
using System.Collections.Generic;
using XamariNES.Common.Extensions;
using XamariNES.CPU;

namespace XamariNES.APU
{
    public sealed class Apu : IApu
    {
        private const double CpuHz = 1789772.7272727273; // NTSC
        public int SampleRate => 44100;

        private readonly PulseChannel _pulse1 = PulseChannel.NewChannel1();
        private readonly PulseChannel _pulse2 = PulseChannel.NewChannel2();
        private readonly TriangleChannel _triangle = new TriangleChannel();
        private readonly NoiseChannel _noise = new NoiseChannel();
        private readonly DmcChannel _dmc;
        private readonly FrameCounter _frameCounter = new FrameCounter();
        private bool _frameIrqFlag;
        private bool _frameIrqClearPending;
        private readonly Func<int, byte> _memoryRead;

        private double _sampleAccumulator;
        private readonly double _cyclesPerSample;
        private readonly List<short> _audio = new List<short>(2048);

        public bool IrqPending => (_frameIrqFlag && !_frameCounter.InterruptInhibit) || _dmc.InterruptFlag;

        public Apu(Func<int, byte> memoryRead)
        {
            _memoryRead = memoryRead;
            _dmc = new DmcChannel(memoryRead);
            _cyclesPerSample = CpuHz / SampleRate;
        }

        public void WriteRegister(int offset, byte value)
        {
            switch (offset)
            {
                case 0x4000:
                    _pulse1.ProcessVolUpdate(value);
                    break;
                case 0x4001:
                    _pulse1.ProcessSweepUpdate(value);
                    break;
                case 0x4002:
                    _pulse1.ProcessLoUpdate(value);
                    break;
                case 0x4003:
                    _pulse1.ProcessHiUpdate(value);
                    break;
                case 0x4004:
                    _pulse2.ProcessVolUpdate(value);
                    break;
                case 0x4005:
                    _pulse2.ProcessSweepUpdate(value);
                    break;
                case 0x4006:
                    _pulse2.ProcessLoUpdate(value);
                    break;
                case 0x4007:
                    _pulse2.ProcessHiUpdate(value);
                    break;
                case 0x4008:
                    _triangle.ProcessTriLinearUpdate(value);
                    break;
                case 0x400A:
                    _triangle.ProcessLoUpdate(value);
                    break;
                case 0x400B:
                    _triangle.ProcessHiUpdate(value);
                    break;
                case 0x400C:
                    _noise.ProcessVolUpdate(value);
                    break;
                case 0x400E:
                    _noise.ProcessLoUpdate(value);
                    break;
                case 0x400F:
                    _noise.ProcessHiUpdate(value);
                    break;
                case 0x4010:
                    _dmc.ProcessFreqUpdate(value);
                    break;
                case 0x4011:
                    _dmc.ProcessRawUpdate(value);
                    break;
                case 0x4012:
                    _dmc.ProcessStartUpdate(value);
                    break;
                case 0x4013:
                    _dmc.ProcessLenUpdate(value);
                    break;
                case 0x4015:
                    _pulse1.ProcessSndChnUpdate(value);
                    _pulse2.ProcessSndChnUpdate(value);
                    _triangle.ProcessSndChnUpdate(value);
                    _noise.ProcessSndChnUpdate(value);
                    _dmc.ProcessSndChnUpdate(value, _frameCounter.CpuTicks);
                    break;
                case 0x4017:
                    _frameCounter.ProcessJoy2Update(value);
                    break;
            }
        }

        public byte ReadStatus()
        {
            _frameIrqClearPending = true;
            byte status = 0;
            if (_dmc.InterruptFlag) status |= 0x80;
            if (_frameIrqFlag) status |= 0x40;
            if (_dmc.SampleBytesRemaining > 0) status |= 0x10;
            if (_noise.LengthCounter > 0) status |= 0x08;
            if (_triangle.LengthCounter > 0) status |= 0x04;
            if (_pulse2.LengthCounter > 0) status |= 0x02;
            if (_pulse1.LengthCounter > 0) status |= 0x01;
            return status;
        }

        public void TickCpu(int cycles)
        {
            for (int i = 0; i < cycles; i++)
            {
                TickCpuOnce();
            }
        }

        private void TickCpuOnce()
        {
            // Clear frame IRQ on odd cycle after read
            if (_frameIrqClearPending && ((_frameCounter.CpuTicks & 1) != 0))
            {
                _frameIrqClearPending = false;
                _frameIrqFlag = false;
            }

            _pulse1.TickCpu();
            _pulse2.TickCpu();
            _triangle.TickCpu(true);
            _noise.TickCpu();
            _dmc.TickCpu();
            _frameCounter.Tick();

            if (_frameCounter.GenerateQuarterFrameClock())
            {
                _pulse1.ClockQuarterFrame();
                _pulse2.ClockQuarterFrame();
                _triangle.ClockQuarterFrame();
                _noise.ClockQuarterFrame();
            }

            if (_frameCounter.GenerateHalfFrameClock())
            {
                _pulse1.ClockHalfFrame();
                _pulse2.ClockHalfFrame();
                _triangle.ClockHalfFrame();
                _noise.ClockHalfFrame();
            }

            if (_frameCounter.ShouldSetInterruptFlag())
            {
                _frameIrqFlag = true;
            }
            else if (_frameCounter.InterruptInhibit)
            {
                _frameIrqFlag = false;
            }

            // DMC DMA (approximate, no CPU stall)
            _dmc.ServiceDma();

            _sampleAccumulator += 1.0;
            if (_sampleAccumulator >= _cyclesPerSample)
            {
                _sampleAccumulator -= _cyclesPerSample;
                double mixed = MixSamples();
                short pcm = ToPcm(mixed);
                _audio.Add(pcm);
                _audio.Add(pcm);
            }
        }

        private double MixSamples()
        {
            byte p1 = _pulse1.Sample();
            byte p2 = _pulse2.Sample();
            byte t = _triangle.Sample();
            byte n = _noise.Sample();
            byte d = _dmc.Sample();
            double pulse = MixTables.PulseTable[p1 + p2];
            double tnd = MixTables.TndTable[d, t, n];
            return pulse + tnd; // 0..1
        }

        private static short ToPcm(double value)
        {
            double scaled = (value * 2.0 - 1.0) * short.MaxValue;
            if (scaled > short.MaxValue) return short.MaxValue;
            if (scaled < short.MinValue) return short.MinValue;
            return (short)Math.Round(scaled);
        }

        public short[] ConsumeAudioBuffer()
        {
            if (_audio.Count == 0)
                return Array.Empty<short>();
            var arr = _audio.ToArray();
            _audio.Clear();
            return arr;
        }
    }

    internal sealed class FrameCounter
    {
        private static readonly ushort[] Steps = { 7456, 14912, 22370, 29828, 37280 };
        private readonly ushort _fourStepReset = (ushort)(Steps[3] + 2);
        private readonly ushort _fiveStepReset = (ushort)(Steps[4] + 2);
        private readonly ushort _interruptStart = Steps[3];
        private readonly ushort _interruptEnd = (ushort)(Steps[3] + 2);

        public ushort CpuTicks { get; private set; }
        public bool InterruptInhibit { get; private set; }
        private bool _fiveStep;
        private FrameResetState _resetState;

        private enum FrameResetState
        {
            Joy2Updated,
            PendingReset,
            None
        }

        public void ProcessJoy2Update(byte value)
        {
            _fiveStep = value.IsBitSet(7);
            InterruptInhibit = value.IsBitSet(6);
            _resetState = FrameResetState.Joy2Updated;
        }

        public void Tick()
        {
            if (_resetState == FrameResetState.Joy2Updated)
            {
                if ((CpuTicks & 1) == 0)
                    _resetState = FrameResetState.PendingReset;
            }
            else if (_resetState == FrameResetState.PendingReset)
            {
                CpuTicks = 0;
                _resetState = FrameResetState.None;
                return;
            }

            if ((!_fiveStep && CpuTicks == _fourStepReset) || CpuTicks == _fiveStepReset)
                CpuTicks = 0;

            CpuTicks++;
        }

        private bool FiveStepResetClock()
        {
            return _fiveStep && _resetState == FrameResetState.PendingReset;
        }

        public bool GenerateQuarterFrameClock()
        {
            return CpuTicks == Steps[0]
                || CpuTicks == Steps[1]
                || CpuTicks == Steps[2]
                || (CpuTicks == Steps[3] && !_fiveStep)
                || CpuTicks == Steps[4]
                || FiveStepResetClock();
        }

        public bool GenerateHalfFrameClock()
        {
            return CpuTicks == Steps[1]
                || (CpuTicks == Steps[3] && !_fiveStep)
                || CpuTicks == Steps[4]
                || FiveStepResetClock();
        }

        public bool ShouldSetInterruptFlag()
        {
            return !_fiveStep && CpuTicks >= _interruptStart && CpuTicks < _interruptEnd;
        }
    }

    internal sealed class LengthCounter
    {
        private static readonly byte[] Table =
        {
            10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
            12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30
        };

        private readonly byte _mask;
        public byte Counter;
        private bool _enabled;
        private bool _halted;

        public LengthCounter(byte mask)
        {
            _mask = mask;
        }

        public void ProcessSndChnUpdate(byte value)
        {
            bool enabled = (value & _mask) != 0;
            _enabled = enabled;
            if (!enabled) Counter = 0;
        }

        public void ProcessVolUpdate(byte value)
        {
            _halted = value.IsBitSet(5);
        }

        public void ProcessTriLinearUpdate(byte value)
        {
            _halted = value.IsBitSet(7);
        }

        public void ProcessHiUpdate(byte value)
        {
            if (_enabled)
                Counter = Table[(value >> 3) & 0x1F];
        }

        public void Clock()
        {
            if (!_halted && Counter > 0)
                Counter--;
        }
    }

    internal sealed class Envelope
    {
        private byte _divider;
        private byte _dividerPeriod;
        private byte _decayLevel;
        private bool _startFlag;
        private bool _loopFlag;
        private bool _constantVolume;

        public byte Volume => _constantVolume ? _dividerPeriod : _decayLevel;

        public void ProcessVolUpdate(byte value)
        {
            _loopFlag = value.IsBitSet(5);
            _constantVolume = value.IsBitSet(4);
            _dividerPeriod = (byte)(value & 0x0F);
        }

        public void ProcessHiUpdate()
        {
            _startFlag = true;
        }

        public void Clock()
        {
            if (_startFlag)
            {
                _startFlag = false;
                _divider = _dividerPeriod;
                _decayLevel = 0x0F;
            }
            else if (_divider == 0)
            {
                _divider = _dividerPeriod;
                if (_decayLevel > 0)
                    _decayLevel--;
                else if (_loopFlag)
                    _decayLevel = 0x0F;
            }
            else
            {
                _divider--;
            }
        }
    }

    internal sealed class PhaseTimer
    {
        private readonly byte _maxPhase;
        private readonly byte _cpuTicksPerClock;
        private readonly byte _dividerBits;
        private readonly bool _canResetPhase;

        private byte _cpuTicks;
        private ushort _cpuDivider;
        public ushort DividerPeriod;
        public byte Phase;

        public PhaseTimer(byte maxPhase, byte cpuTicksPerClock, byte dividerBits, bool canResetPhase)
        {
            _maxPhase = maxPhase;
            _cpuTicksPerClock = cpuTicksPerClock;
            _dividerBits = dividerBits;
            _canResetPhase = canResetPhase;
        }

        public void ProcessLoUpdate(byte value)
        {
            DividerPeriod = (ushort)((DividerPeriod & 0xFF00) | value);
        }

        public void ProcessHiUpdate(byte value)
        {
            ushort mask = _dividerBits == 11 ? (ushort)0x07 : (ushort)0x0F;
            DividerPeriod = (ushort)(((value & mask) << 8) | (DividerPeriod & 0x00FF));
            if (_canResetPhase)
                Phase = 0;
        }

        public void Tick(bool sequencerEnabled)
        {
            _cpuTicks++;
            if (_cpuTicks < _cpuTicksPerClock)
                return;
            _cpuTicks = 0;

            if (_cpuDivider == 0)
            {
                _cpuDivider = DividerPeriod;
                if (sequencerEnabled)
                    Phase = (byte)((Phase + 1) & (_maxPhase - 1));
            }
            else
            {
                _cpuDivider--;
            }
        }
    }

    internal sealed class PulseChannel
    {
        private readonly PhaseTimer _timer;
        private DutyCycle _duty = DutyCycle.OneEighth;
        private readonly LengthCounter _length;
        private readonly Envelope _envelope = new Envelope();
        private readonly PulseSweep _sweep;
        private readonly bool _sweepEnabled;

        private PulseChannel(bool channel1)
        {
            _timer = new PhaseTimer(8, 2, 11, true);
            _length = new LengthCounter(channel1 ? (byte)0x01 : (byte)0x02);
            _sweep = new PulseSweep(channel1 ? SweepNegateBehavior.OnesComplement : SweepNegateBehavior.TwosComplement);
            _sweepEnabled = true;
        }

        public static PulseChannel NewChannel1() => new PulseChannel(true);
        public static PulseChannel NewChannel2() => new PulseChannel(false);

        public void ProcessVolUpdate(byte value)
        {
            _duty = DutyCycleExtensions.FromVol(value);
            _length.ProcessVolUpdate(value);
            _envelope.ProcessVolUpdate(value);
        }

        public void ProcessSweepUpdate(byte value)
        {
            _sweep.ProcessSweepUpdate(value, _timer.DividerPeriod);
        }

        public void ProcessLoUpdate(byte value)
        {
            _timer.ProcessLoUpdate(value);
            _sweep.ProcessTimerPeriodUpdate(_timer.DividerPeriod);
        }

        public void ProcessHiUpdate(byte value)
        {
            _timer.ProcessHiUpdate(value);
            _sweep.ProcessTimerPeriodUpdate(_timer.DividerPeriod);
            _length.ProcessHiUpdate(value);
            _envelope.ProcessHiUpdate();
        }

        public void ProcessSndChnUpdate(byte value)
        {
            _length.ProcessSndChnUpdate(value);
        }

        public void ClockQuarterFrame() => _envelope.Clock();

        public void ClockHalfFrame()
        {
            _length.Clock();
            if (_sweepEnabled)
                _sweep.Clock(ref _timer.DividerPeriod);
        }

        public void TickCpu() => _timer.Tick(true);

        public byte Sample()
        {
            if (_length.Counter == 0 || (_sweepEnabled && _sweep.IsChannelMuted(_timer.DividerPeriod)))
                return 0;

            byte wave = _duty.Waveform()[_timer.Phase];
            return (byte)(wave * _envelope.Volume);
        }

        public byte LengthCounter => _length.Counter;
    }

    internal sealed class TriangleChannel
    {
        private readonly PhaseTimer _timer = new PhaseTimer(32, 1, 11, false);
        private readonly LinearCounter _linear = new LinearCounter();
        private readonly LengthCounter _length = new LengthCounter(0x04);

        private static readonly byte[] Waveform =
        {
            15,14,13,12,11,10,9,8,7,6,5,4,3,2,1,0,0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15
        };

        public void ProcessTriLinearUpdate(byte value)
        {
            _linear.ProcessTriLinearUpdate(value);
            _length.ProcessTriLinearUpdate(value);
        }

        public void ProcessLoUpdate(byte value) => _timer.ProcessLoUpdate(value);

        public void ProcessHiUpdate(byte value)
        {
            _timer.ProcessHiUpdate(value);
            _linear.ProcessHiUpdate();
            _length.ProcessHiUpdate(value);
        }

        public void ProcessSndChnUpdate(byte value) => _length.ProcessSndChnUpdate(value);

        public void ClockQuarterFrame() => _linear.Clock();

        public void ClockHalfFrame() => _length.Clock();

        public void TickCpu(bool silenceUltrasonic)
        {
            bool silenced = _linear.Counter == 0 || _length.Counter == 0 || (silenceUltrasonic && _timer.DividerPeriod < 2);
            _timer.Tick(!silenced);
        }

        public byte Sample() => Waveform[_timer.Phase];

        public byte LengthCounter => _length.Counter;

        public void Reset() => _timer.Phase = 0;
    }

    internal sealed class NoiseChannel
    {
        private readonly Lfsr _lfsr = new Lfsr();
        private ushort _timerCounter;
        private ushort _timerPeriod = 1;
        private readonly LengthCounter _length = new LengthCounter(0x08);
        private readonly Envelope _envelope = new Envelope();

        private static readonly ushort[] Periods =
        {
            4,8,16,32,64,96,128,160,202,254,380,508,762,1016,2034,4068
        };

        public void ClockQuarterFrame() => _envelope.Clock();
        public void ClockHalfFrame() => _length.Clock();

        public void TickCpu()
        {
            if (_timerCounter == 0)
            {
                _timerCounter = (ushort)(_timerPeriod - 1);
                _lfsr.Clock();
            }
            else
            {
                _timerCounter--;
            }
        }

        public void ProcessVolUpdate(byte value)
        {
            _envelope.ProcessVolUpdate(value);
            _length.ProcessVolUpdate(value);
        }

        public void ProcessLoUpdate(byte value)
        {
            _lfsr.Mode = value.IsBitSet(7) ? LfsrMode.Bit6Feedback : LfsrMode.Bit1Feedback;
            _timerPeriod = Periods[value & 0x0F];
        }

        public void ProcessHiUpdate(byte value)
        {
            _envelope.ProcessHiUpdate();
            _length.ProcessHiUpdate(value);
        }

        public void ProcessSndChnUpdate(byte value) => _length.ProcessSndChnUpdate(value);

        public byte Sample()
        {
            if (_length.Counter == 0) return 0;
            return (byte)(_lfsr.Sample() * _envelope.Volume);
        }

        public byte LengthCounter => _length.Counter;
    }

    internal sealed class DmcChannel
    {
        private static readonly ushort[] Periods =
        {
            428,380,340,320,286,254,226,214,190,160,142,128,106,84,72,54
        };

        private bool _enabled;
        private ushort _timerCounter;
        private ushort _timerPeriod = Periods[0];
        private byte? _sampleBuffer;
        private readonly DmcOutputUnit _output = new DmcOutputUnit();
        private ushort _sampleAddress = 0xC000;
        private ushort _currentSampleAddress = 0xC000;
        private ushort _sampleLength = 1;
        private ushort _sampleBytesRemaining;
        private bool _loopFlag;
        private bool _irqEnabled;
        private bool _interruptFlag;
        private bool _dmaInitialLoad;
        private byte _dmaStartLatency;
        private readonly Func<int, byte> _memoryRead;

        public DmcChannel(Func<int, byte> memoryRead)
        {
            _memoryRead = memoryRead;
        }

        public void ProcessFreqUpdate(byte value)
        {
            _irqEnabled = value.IsBitSet(7);
            _loopFlag = value.IsBitSet(6);
            _timerPeriod = Periods[value & 0x0F];
            if (!_irqEnabled) _interruptFlag = false;
        }

        public void ProcessRawUpdate(byte value)
        {
            _output.OutputLevel = (byte)(value & 0x7F);
        }

        public void ProcessStartUpdate(byte value)
        {
            _sampleAddress = (ushort)(0xC000 | (value << 6));
        }

        public void ProcessLenUpdate(byte value)
        {
            _sampleLength = (ushort)((value << 4) + 1);
        }

        public void ProcessSndChnUpdate(byte value, ushort frameCounterTicks)
        {
            _interruptFlag = false;
            _enabled = value.IsBitSet(4);
            if (_enabled && _sampleBytesRemaining == 0)
            {
                Restart();
                _dmaInitialLoad = true;
            _dmaStartLatency = (byte)(2 + (frameCounterTicks & 1));
            }
            else if (!_enabled)
            {
                _sampleBytesRemaining = 0;
                _sampleBuffer = null;
            }
        }

        private void Restart()
        {
            _currentSampleAddress = _sampleAddress;
            _sampleBytesRemaining = _sampleLength;
        }

        public void TickCpu()
        {
            if (_timerCounter == 0)
            {
                Clock();
                _timerCounter = (ushort)(_timerPeriod - 1);
            }
            else
            {
                _timerCounter--;
            }

            if (_dmaStartLatency > 0)
                _dmaStartLatency--;
        }

        public void ServiceDma()
        {
            if (!_enabled || _sampleBytesRemaining == 0 || _sampleBuffer.HasValue || _dmaStartLatency != 0)
                return;

            byte value = _memoryRead(_currentSampleAddress);
            _sampleBuffer = value;

            _currentSampleAddress = (ushort)(0x8000 | (_currentSampleAddress + 1));
            _sampleBytesRemaining--;

            if (_sampleBytesRemaining == 0)
            {
                if (_loopFlag)
                {
                    Restart();
                }
                else if (_irqEnabled)
                {
                    _interruptFlag = true;
                }
            }
        }

        private void Clock()
        {
            bool sampleBufferWasFull = _sampleBuffer.HasValue;
            _output.Clock(ref _sampleBuffer);

            if (_enabled && sampleBufferWasFull && !_sampleBuffer.HasValue && _sampleBytesRemaining != 0)
            {
                _dmaInitialLoad = false;
                _dmaStartLatency = 2;
            }
        }

        public byte Sample() => _output.Sample();

        public ushort SampleBytesRemaining => _sampleBytesRemaining;
        public bool InterruptFlag => _interruptFlag;
    }

    internal sealed class DmcOutputUnit
    {
        public byte OutputLevel;
        private byte _shiftRegister;
        private byte _bitsRemaining = 8;
        private bool _silenceFlag = true;

        public void Clock(ref byte? sampleBuffer)
        {
            if (!_silenceFlag)
            {
                byte newLevel = _shiftRegister.IsBitSet(0) ? (byte)(OutputLevel + 2) : (byte)(OutputLevel - 2);
                if (newLevel < 128)
                    OutputLevel = newLevel;
            }

            _shiftRegister >>= 1;
            _bitsRemaining--;

            if (_bitsRemaining == 0)
            {
                _bitsRemaining = 8;
                if (sampleBuffer.HasValue)
                {
                    _shiftRegister = sampleBuffer.Value;
                    sampleBuffer = null;
                    _silenceFlag = false;
                }
                else
                {
                    _silenceFlag = true;
                }
            }
        }

        public byte Sample() => OutputLevel;
    }

    internal enum DutyCycle
    {
        OneEighth,
        OneFourth,
        OneHalf,
        ThreeFourths
    }

    internal static class DutyCycleExtensions
    {
        public static DutyCycle FromVol(byte value)
        {
            switch (value & 0xC0)
            {
                case 0x00: return DutyCycle.OneEighth;
                case 0x40: return DutyCycle.OneFourth;
                case 0x80: return DutyCycle.OneHalf;
                case 0xC0: return DutyCycle.ThreeFourths;
                default: return DutyCycle.OneEighth;
            }
        }

        public static byte[] Waveform(this DutyCycle duty)
        {
            switch (duty)
            {
                case DutyCycle.OneEighth: return WaveOneEighth;
                case DutyCycle.OneFourth: return WaveOneFourth;
                case DutyCycle.OneHalf: return WaveOneHalf;
                case DutyCycle.ThreeFourths: return WaveThreeFourths;
                default: return WaveOneEighth;
            }
        }

        private static readonly byte[] WaveOneEighth = { 0, 1, 0, 0, 0, 0, 0, 0 };
        private static readonly byte[] WaveOneFourth = { 0, 1, 1, 0, 0, 0, 0, 0 };
        private static readonly byte[] WaveOneHalf = { 0, 1, 1, 1, 1, 0, 0, 0 };
        private static readonly byte[] WaveThreeFourths = { 1, 0, 0, 1, 1, 1, 1, 1 };
    }

    internal enum SweepNegateBehavior
    {
        OnesComplement,
        TwosComplement
    }

    internal sealed class PulseSweep
    {
        private bool _enabled;
        private byte _divider;
        private byte _dividerPeriod;
        private bool _negateFlag;
        private readonly SweepNegateBehavior _negateBehavior;
        private byte _shift;
        private bool _reloadFlag;
        private ushort _targetPeriod;

        public PulseSweep(SweepNegateBehavior behavior)
        {
            _negateBehavior = behavior;
        }

        public void ProcessSweepUpdate(byte value, ushort timerPeriod)
        {
            _reloadFlag = true;
            _enabled = value.IsBitSet(7);
            _dividerPeriod = (byte)((value >> 4) & 0x07);
            _negateFlag = value.IsBitSet(3);
            _shift = (byte)(value & 0x07);
            _targetPeriod = ComputeTargetPeriod(timerPeriod);
        }

        public void ProcessTimerPeriodUpdate(ushort timerPeriod)
        {
            _targetPeriod = ComputeTargetPeriod(timerPeriod);
        }

        private ushort ComputeTargetPeriod(ushort timerPeriod)
        {
            if (_shift == 0 && _negateFlag)
                return 0;

            ushort delta = (ushort)(timerPeriod >> _shift);
            ushort signedDelta = _negateFlag ? Negate(delta) : delta;
            return (ushort)(timerPeriod + signedDelta);
        }

        private ushort Negate(ushort value)
        {
            if (_negateBehavior == SweepNegateBehavior.OnesComplement)
                return (ushort)~value;
            return (ushort)(~value + 1);
        }

        public bool IsChannelMuted(ushort timerPeriod)
        {
            return timerPeriod < 8 || _targetPeriod > 0x07FF;
        }

        public void Clock(ref ushort timerPeriod)
        {
            if (_divider == 0 && _enabled && _shift > 0 && !IsChannelMuted(timerPeriod))
            {
                timerPeriod = _targetPeriod;
                _targetPeriod = ComputeTargetPeriod(_targetPeriod);
            }

            if (_divider == 0 || _reloadFlag)
            {
                _divider = _dividerPeriod;
                _reloadFlag = false;
            }
            else
            {
                _divider--;
            }
        }
    }

    internal sealed class LinearCounter
    {
        public byte Counter;
        private byte _reloadValue;
        private bool _controlFlag;
        private bool _reloadFlag;

        public void ProcessTriLinearUpdate(byte value)
        {
            _controlFlag = value.IsBitSet(7);
            _reloadValue = (byte)(value & 0x7F);
        }

        public void ProcessHiUpdate()
        {
            _reloadFlag = true;
        }

        public void Clock()
        {
            if (_reloadFlag)
                Counter = _reloadValue;
            else if (Counter > 0)
                Counter--;

            if (!_controlFlag)
                _reloadFlag = false;
        }
    }

    internal enum LfsrMode
    {
        Bit1Feedback,
        Bit6Feedback
    }

    internal sealed class Lfsr
    {
        private ushort _register = 1;
        public LfsrMode Mode = LfsrMode.Bit1Feedback;

        public void Clock()
        {
            int feedback = Mode == LfsrMode.Bit1Feedback
                ? (_register & 0x01) ^ ((_register & 0x02) >> 1)
                : (_register & 0x01) ^ ((_register & 0x40) >> 6);

            _register = (ushort)((_register >> 1) | (feedback << 14));
        }

        public byte Sample()
        {
            return (byte)((~_register) & 0x01);
        }
    }

    internal static class MixTables
    {
        public static readonly double[] PulseTable = BuildPulseTable();
        public static readonly double[,,] TndTable = BuildTndTable();

        private static double[] BuildPulseTable()
        {
            var table = new double[31];
            table[0] = 0.0;
            for (int i = 1; i < table.Length; i++)
            {
                double sum = i;
                table[i] = 95.88 / (8128.0 / sum + 100.0);
            }
            return table;
        }

        private static double[,,] BuildTndTable()
        {
            var table = new double[128, 16, 16];
            for (int d = 0; d < 128; d++)
            {
                for (int t = 0; t < 16; t++)
                {
                    for (int n = 0; n < 16; n++)
                    {
                        if (t > 0 || n > 0 || d > 0)
                        {
                            table[d, t, n] = 159.79 / (1.0 / (t / 8227.0 + n / 12241.0 + d / 22638.0) + 100.0);
                        }
                        else
                        {
                            table[d, t, n] = 0.0;
                        }
                    }
                }
            }
            return table;
        }
    }
}
