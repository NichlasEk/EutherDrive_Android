using System;

namespace EutherDrive.Core.Arcade.Cps1;

// YM2151/OPM core for the classic CPS1 audio path.
// This is register-compatible with the MAME/YMFM OPM layout used by CPS1
// (8 channels, fixed OPM operator mapping, timers and 4-op algorithms). It is
// not a bit-exact YMFM port; the incomplete MCS/YMFM tree still needs the full
// operator/envelope engine translated before this can be replaced by it.
internal sealed class Cps1Ym2151
{
    private const int ChannelCount = 8;
    private const int OperatorCount = ChannelCount * 4;
    private const int InputClockHz = 3_579_545;
    private const int SourceSampleRate = InputClockHz / 64;
    private const double TwoPi = Math.PI * 2.0;
    private const double PhaseStepScale = TwoPi / 1_048_576.0;
    private const double OperatorModulationScale = TwoPi * 4.0;

    private static readonly int[][] OperatorMap =
    {
        new[] {  0, 16,  8, 24 },
        new[] {  1, 17,  9, 25 },
        new[] {  2, 18, 10, 26 },
        new[] {  3, 19, 11, 27 },
        new[] {  4, 20, 12, 28 },
        new[] {  5, 21, 13, 29 },
        new[] {  6, 22, 14, 30 },
        new[] {  7, 23, 15, 31 }
    };

    private static readonly int[,] DetuneAdjustment =
    {
        { 0, 0, 1, 2 }, { 0, 0, 1, 2 }, { 0, 0, 1, 2 }, { 0, 0, 1, 2 },
        { 0, 1, 2, 2 }, { 0, 1, 2, 3 }, { 0, 1, 2, 3 }, { 0, 1, 2, 3 },
        { 0, 1, 2, 4 }, { 0, 1, 3, 4 }, { 0, 1, 3, 4 }, { 0, 1, 3, 5 },
        { 0, 2, 4, 5 }, { 0, 2, 4, 6 }, { 0, 2, 4, 6 }, { 0, 2, 5, 7 },
        { 0, 2, 5, 8 }, { 0, 3, 6, 8 }, { 0, 3, 6, 9 }, { 0, 3, 7, 10 },
        { 0, 4, 8, 11 }, { 0, 4, 8, 12 }, { 0, 4, 9, 13 }, { 0, 5, 10, 14 },
        { 0, 5, 11, 16 }, { 0, 6, 12, 17 }, { 0, 6, 13, 19 }, { 0, 7, 14, 20 },
        { 0, 8, 16, 22 }, { 0, 8, 16, 22 }, { 0, 8, 16, 22 }, { 0, 8, 16, 22 }
    };

    private static readonly int[] Detune2Delta = { 0, 384, 500, 608 };
    private static readonly double[] FeedbackScale =
    {
        0.0,
        TwoPi / 64.0,
        TwoPi / 32.0,
        TwoPi / 16.0,
        TwoPi / 8.0,
        TwoPi / 4.0,
        TwoPi / 2.0,
        TwoPi
    };

    private readonly byte[] _registers = new byte[0x100];
    private readonly YmChannel[] _channels = new YmChannel[ChannelCount];
    private readonly YmOperator[] _operators = new YmOperator[OperatorCount];

    private byte _selectedRegister;
    private byte _status;
    private int _timerA;
    private int _timerB;
    private int _timerACounter;
    private int _timerBCounter;
    private double _timerTickAccumulator;
    private double _sourcePhase;
    private short _lastLeft;
    private short _lastRight;
    private short _nextLeft;
    private short _nextRight;

    public Cps1Ym2151()
    {
        for (int i = 0; i < _operators.Length; i++)
            _operators[i] = new YmOperator(this, i);
        for (int channel = 0; channel < _channels.Length; channel++)
            _channels[channel] = new YmChannel(this, channel, OperatorMap[channel]);

        Reset();
    }

    public void Reset()
    {
        Array.Clear(_registers);
        _selectedRegister = 0;
        _status = 0;
        _timerA = 0;
        _timerB = 0;
        _timerACounter = 0;
        _timerBCounter = 0;
        _timerTickAccumulator = 0.0;
        _sourcePhase = 0.0;
        _lastLeft = 0;
        _lastRight = 0;
        _nextLeft = 0;
        _nextRight = 0;

        for (int channel = 0; channel < ChannelCount; channel++)
            _registers[0x20 + channel] = 0xc0;

        foreach (YmOperator op in _operators)
            op.Reset();
        foreach (YmChannel channel in _channels)
            channel.Reset();
    }

    public byte ReadStatus()
        => _status;

    public bool IrqAsserted
        => (_status & 0x03) != 0;

    public void Write(int offset, byte value)
    {
        if ((offset & 1) == 0)
        {
            _selectedRegister = value;
            return;
        }

        WriteRegister(_selectedRegister, value);
    }

    public void AdvanceTimersByCpuCycles(int cpuCycles, double cpuClockHz)
    {
        if (cpuCycles <= 0 || cpuClockHz <= 0.0)
            return;

        _timerTickAccumulator += cpuCycles * (InputClockHz / cpuClockHz) / 64.0;
        int ticks = (int)_timerTickAccumulator;
        if (ticks <= 0)
            return;

        _timerTickAccumulator -= ticks;
        ClockTimers(ticks);
    }

    public void RenderStereo(
        short[] destination,
        ref int sampleFrameIndex,
        int targetSampleFrames,
        float gain = 0.70f,
        int outputSampleRate = 44_100,
        bool routeToMono = false)
    {
        if (destination.Length == 0)
            return;

        int maxFrames = destination.Length / 2;
        targetSampleFrames = Math.Clamp(targetSampleFrames, sampleFrameIndex, maxFrames);
        if (targetSampleFrames <= sampleFrameIndex)
            return;

        double phaseStep = SourceSampleRate / (double)outputSampleRate;
        while (sampleFrameIndex < targetSampleFrames)
        {
            _sourcePhase += phaseStep;
            while (_sourcePhase >= 1.0)
            {
                _sourcePhase -= 1.0;
                _lastLeft = _nextLeft;
                _lastRight = _nextRight;
                GenerateSourceSample(out _nextLeft, out _nextRight, gain);
            }

            int left = (int)Math.Round(_lastLeft + (_nextLeft - _lastLeft) * _sourcePhase);
            int right = (int)Math.Round(_lastRight + (_nextRight - _lastRight) * _sourcePhase);
            if (routeToMono)
            {
                int mono = (left + right) / 2;
                left = mono;
                right = mono;
            }

            int offset = sampleFrameIndex * 2;
            destination[offset] = Mix(destination[offset], left);
            destination[offset + 1] = Mix(destination[offset + 1], right);
            sampleFrameIndex++;
        }
    }

    private void WriteRegister(byte register, byte value)
    {
        if (register == 0x19)
            _registers[(value & 0x80) != 0 ? 0x1a : 0x19] = value;
        else if (register != 0x1a)
            _registers[register] = value;

        switch (register)
        {
            case 0x08:
                _channels[value & 0x07].KeyOn((value >> 3) & 0x0f);
                break;
            case 0x10:
            case 0x11:
                _timerA = ((_registers[0x10] << 2) | (_registers[0x11] & 0x03)) & 0x03ff;
                break;
            case 0x12:
                _timerB = value;
                break;
            case 0x14:
                ApplyTimerControl(value);
                break;
            default:
                if ((register >= 0x20 && register <= 0x37) || (register >= 0x38 && register <= 0xff))
                    RefreshFromRegisters(register);
                break;
        }
    }

    private void ApplyTimerControl(byte value)
    {
        if ((value & 0x10) != 0)
            _status &= unchecked((byte)~0x01);
        if ((value & 0x20) != 0)
            _status &= unchecked((byte)~0x02);

        if ((value & 0x01) != 0 && _timerACounter <= 0)
            _timerACounter = Math.Max(1, 1024 - _timerA);
        else if ((value & 0x01) == 0)
            _timerACounter = 0;

        if ((value & 0x02) != 0 && _timerBCounter <= 0)
            _timerBCounter = Math.Max(1, 16 * (256 - _timerB));
        else if ((value & 0x02) == 0)
            _timerBCounter = 0;
    }

    private void RefreshFromRegisters(byte register)
    {
        int low = register & 0x07;
        if (register >= 0x20 && register <= 0x3f)
        {
            _channels[low].Refresh();
            return;
        }

        if (register >= 0x40)
        {
            int opOffset = register & 0x1f;
            _operators[opOffset].Refresh();
            _channels[opOffset & 0x07].RefreshFrequency();
        }
    }

    private void GenerateSourceSample(out short left, out short right, float gain)
    {
        double leftMix = 0.0;
        double rightMix = 0.0;
        for (int channel = 0; channel < ChannelCount; channel++)
            _channels[channel].Generate(ref leftMix, ref rightMix);

        left = (short)Math.Clamp((int)Math.Round(leftMix * gain * 32767.0), short.MinValue, short.MaxValue);
        right = (short)Math.Clamp((int)Math.Round(rightMix * gain * 32767.0), short.MinValue, short.MaxValue);
    }

    private void ClockTimers(int ticks)
    {
        byte mode = _registers[0x14];
        if ((mode & 0x01) != 0 && _timerACounter > 0)
        {
            _timerACounter -= ticks;
            while (_timerACounter <= 0)
            {
                if ((mode & 0x04) != 0)
                    _status |= 0x01;
                _timerACounter += Math.Max(1, 1024 - _timerA);
            }
        }

        if ((mode & 0x02) != 0 && _timerBCounter > 0)
        {
            _timerBCounter -= ticks;
            while (_timerBCounter <= 0)
            {
                if ((mode & 0x08) != 0)
                    _status |= 0x02;
                _timerBCounter += Math.Max(1, 16 * (256 - _timerB));
            }
        }
    }

    private byte Reg(int address)
        => _registers[address & 0xff];

    private int ChannelBlockFrequency(int channel)
        => ((Reg(0x28 + channel) & 0x7f) << 6) | (Reg(0x30 + channel) >> 2);

    private int ComputeOperatorPhaseStep(int operatorOffset, int blockFrequency)
    {
        int keyCode = (blockFrequency >> 8) & 0x1f;
        int detune = (Reg(0x40 + operatorOffset) >> 4) & 0x07;
        int detuneAdjustment = DetuneAdjustment[keyCode, detune & 0x03];
        if ((detune & 0x04) != 0)
            detuneAdjustment = -detuneAdjustment;

        int detune2 = (Reg(0xc0 + operatorOffset) >> 6) & 0x03;
        int phaseStep = OpmKeyCodeToPhaseStep(blockFrequency, Detune2Delta[detune2]) + detuneAdjustment;

        int multiple = Reg(0x40 + operatorOffset) & 0x0f;
        int multipleX2 = multiple == 0 ? 1 : multiple * 2;
        return Math.Max(0, (phaseStep * multipleX2) >> 1);
    }

    private static int OpmKeyCodeToPhaseStep(int blockFrequency, int delta)
    {
        int block = (blockFrequency >> 10) & 0x07;
        int keyCode = (blockFrequency >> 6) & 0x0f;
        int adjustedCode = keyCode - (keyCode >> 2);
        int effectiveFrequency = (adjustedCode << 6) | (blockFrequency & 0x3f);
        effectiveFrequency += delta;

        if ((uint)effectiveFrequency >= 768u)
        {
            if (effectiveFrequency < 0)
            {
                effectiveFrequency += 768;
                if (block-- == 0)
                    return BaseOpmPhaseStep(0) >> 7;
            }
            else
            {
                effectiveFrequency -= 768;
                if (effectiveFrequency >= 768)
                {
                    block++;
                    effectiveFrequency -= 768;
                }

                if (block++ >= 7)
                    return BaseOpmPhaseStep(767);
            }
        }

        return BaseOpmPhaseStep(effectiveFrequency) >> (block ^ 7);
    }

    private static int BaseOpmPhaseStep(int effectiveFrequency)
    {
        double octaveFraction = Math.Clamp(effectiveFrequency, 0, 767) / 768.0;
        return (int)Math.Round(41_568.0 * Math.Pow(2.0, octaveFraction));
    }

    private static short Mix(short current, int add)
        => (short)Math.Clamp(current + add, short.MinValue, short.MaxValue);

    private sealed class YmChannel
    {
        private readonly Cps1Ym2151 _chip;
        private readonly int _index;
        private readonly YmOperator[] _ops = new YmOperator[4];
        private double _feedback0;
        private double _feedback1;
        private double _feedbackIn;

        private bool _left;
        private bool _right;
        private int _algorithm;
        private int _feedback;

        public YmChannel(Cps1Ym2151 chip, int index, int[] operators)
        {
            _chip = chip;
            _index = index;
            for (int i = 0; i < _ops.Length; i++)
                _ops[i] = chip._operators[operators[i]];
        }

        public void Reset()
        {
            _feedback0 = 0.0;
            _feedback1 = 0.0;
            _feedbackIn = 0.0;
            Refresh();
        }

        public void Refresh()
        {
            byte control = _chip.Reg(0x20 + _index);
            _right = (control & 0x40) != 0;
            _left = (control & 0x80) != 0;
            if (!_left && !_right)
            {
                _left = true;
                _right = true;
            }

            _feedback = (control >> 3) & 0x07;
            _algorithm = control & 0x07;
            RefreshFrequency();
        }

        public void RefreshFrequency()
        {
            int blockFrequency = _chip.ChannelBlockFrequency(_index);
            foreach (YmOperator op in _ops)
                op.SetBlockFrequency(blockFrequency);
        }

        public void KeyOn(int mask)
        {
            for (int op = 0; op < 4; op++)
                _ops[op].SetKeyOn(((mask >> op) & 1) != 0);
        }

        public void Generate(ref double leftMix, ref double rightMix)
        {
            _feedback0 = _feedback1;
            _feedback1 = _feedbackIn;

            double feedbackInput = _feedback == 0
                ? 0.0
                : (_feedback0 + _feedback1) * FeedbackScale[_feedback];
            double op1 = _feedbackIn = _ops[0].Clock(feedbackInput);

            double op2;
            double op3;
            double op4;
            double result;
            switch (_algorithm)
            {
                case 0:
                    op2 = _ops[1].Clock(op1 * OperatorModulationScale);
                    op3 = _ops[2].Clock(op2 * OperatorModulationScale);
                    result = _ops[3].Clock(op3 * OperatorModulationScale);
                    break;
                case 1:
                    op2 = _ops[1].Clock(0.0);
                    op3 = _ops[2].Clock((op1 + op2) * OperatorModulationScale);
                    result = _ops[3].Clock(op3 * OperatorModulationScale);
                    break;
                case 2:
                    op2 = _ops[1].Clock(0.0);
                    op3 = _ops[2].Clock(op2 * OperatorModulationScale);
                    result = _ops[3].Clock((op1 + op3) * OperatorModulationScale);
                    break;
                case 3:
                    op2 = _ops[1].Clock(op1 * OperatorModulationScale);
                    op3 = _ops[2].Clock(0.0);
                    result = _ops[3].Clock((op2 + op3) * OperatorModulationScale);
                    break;
                case 4:
                    op2 = _ops[1].Clock(op1 * OperatorModulationScale);
                    op3 = _ops[2].Clock(0.0);
                    op4 = _ops[3].Clock(op3 * OperatorModulationScale);
                    result = op2 + op4;
                    break;
                case 5:
                    double mod = op1 * OperatorModulationScale;
                    op2 = _ops[1].Clock(mod);
                    op3 = _ops[2].Clock(mod);
                    op4 = _ops[3].Clock(mod);
                    result = op2 + op3 + op4;
                    break;
                case 6:
                    op2 = _ops[1].Clock(op1 * OperatorModulationScale);
                    op3 = _ops[2].Clock(0.0);
                    op4 = _ops[3].Clock(0.0);
                    result = op2 + op3 + op4;
                    break;
                default:
                    op2 = _ops[1].Clock(0.0);
                    op3 = _ops[2].Clock(0.0);
                    op4 = _ops[3].Clock(0.0);
                    result = op1 + op2 + op3 + op4;
                    break;
            }

            result *= 0.18;
            if (_left)
                leftMix += result;
            if (_right)
                rightMix += result;
        }
    }

    private sealed class YmOperator
    {
        private readonly Cps1Ym2151 _chip;
        private readonly int _offset;
        private EnvelopeState _state;
        private bool _keyOn;
        private int _blockFrequency;
        private double _phaseStep;
        private double _phase;
        private double _envelope;
        private double _attackStep;
        private double _decayStep;
        private double _sustainStep;
        private double _releaseStep;
        private double _sustainLevel;
        private double _totalLevel;

        public YmOperator(Cps1Ym2151 chip, int offset)
        {
            _chip = chip;
            _offset = offset;
        }

        public void Reset()
        {
            _state = EnvelopeState.Release;
            _keyOn = false;
            _phase = 0.0;
            _envelope = 0.0;
            Refresh();
        }

        public void Refresh()
        {
            RefreshPhaseStep();

            int tl = _chip.Reg(0x60 + _offset) & 0x7f;
            _totalLevel = Math.Pow(10.0, -(tl * 0.75) / 20.0);

            int ar = _chip.Reg(0x80 + _offset) & 0x1f;
            int ksr = (_chip.Reg(0x80 + _offset) >> 6) & 0x03;
            int d1r = _chip.Reg(0xa0 + _offset) & 0x1f;
            int d2r = _chip.Reg(0xc0 + _offset) & 0x1f;
            int rr = _chip.Reg(0xe0 + _offset) & 0x0f;
            int sl = (_chip.Reg(0xe0 + _offset) >> 4) & 0x0f;
            int keyCode = (_blockFrequency >> 8) & 0x1f;
            int ksrValue = keyCode >> (ksr ^ 3);

            _attackStep = RateToStep(EffectiveRate(ar * 2, ksrValue), 0.015);
            _decayStep = RateToStep(EffectiveRate(d1r * 2, ksrValue), 0.060);
            _sustainStep = RateToStep(EffectiveRate(d2r * 2, ksrValue), 0.100);
            _releaseStep = RateToStep(EffectiveRate(rr * 4 + 2, ksrValue), 0.070);

            int sustain = sl | ((sl + 1) & 0x10);
            _sustainLevel = Math.Pow(10.0, -((sustain << 5) * 0.09375) / 20.0);
        }

        public void SetBlockFrequency(int blockFrequency)
        {
            _blockFrequency = blockFrequency;
            RefreshPhaseStep();
        }

        public void SetKeyOn(bool keyOn)
        {
            if (keyOn == _keyOn)
                return;

            _keyOn = keyOn;
            if (keyOn)
            {
                _state = EnvelopeState.Attack;
                _phase = 0.0;
                if (_attackStep >= 1.0)
                    _envelope = 1.0;
            }
            else
            {
                _state = EnvelopeState.Release;
            }
        }

        public double Clock(double modulation)
        {
            ClockEnvelope();

            _phase += _phaseStep;
            if (_phase >= TwoPi)
                _phase -= TwoPi * Math.Floor(_phase / TwoPi);

            double phase = _phase + modulation;
            return Math.Sin(phase) * _envelope * _totalLevel;
        }

        private void RefreshPhaseStep()
            => _phaseStep = _chip.ComputeOperatorPhaseStep(_offset, _blockFrequency) * PhaseStepScale;

        private void ClockEnvelope()
        {
            switch (_state)
            {
                case EnvelopeState.Attack:
                    _envelope += (1.0 - _envelope) * _attackStep;
                    if (_envelope >= 0.995)
                    {
                        _envelope = 1.0;
                        _state = EnvelopeState.Decay;
                    }
                    break;
                case EnvelopeState.Decay:
                    _envelope += (_sustainLevel - _envelope) * _decayStep;
                    if (_envelope <= _sustainLevel + 0.002)
                        _state = EnvelopeState.Sustain;
                    break;
                case EnvelopeState.Sustain:
                    _envelope += (0.0 - _envelope) * _sustainStep;
                    break;
                case EnvelopeState.Release:
                    _envelope += (0.0 - _envelope) * _releaseStep;
                    if (_envelope < 0.00001)
                        _envelope = 0.0;
                    break;
            }
        }

        private static double RateToStep(int rate, double scale)
        {
            if (rate <= 0)
                return 0.0;

            double normalized = rate / 63.0;
            return Math.Clamp(scale * Math.Pow(2.0, normalized * 5.0), 0.00001, 1.0);
        }

        private static int EffectiveRate(int rawRate, int ksr)
            => rawRate == 0 ? 0 : Math.Min(rawRate + ksr, 63);

        private enum EnvelopeState
        {
            Attack,
            Decay,
            Sustain,
            Release
        }
    }
}
